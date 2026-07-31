// PostgreSQL Auth 存储：事务化处理身份绑定、刷新令牌轮换、会话撤销和账号管理状态。
// 刷新令牌只保存不可逆摘要，轮换必须单次消费；并发冲突不得产生两个有效后继令牌。
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using GuiyangMahjong.Auth.Domain;
using GuiyangMahjong.Auth.Players;
using GuiyangMahjong.Contracts.Common;
using GuiyangMahjong.Contracts.Events;
using Npgsql;
using NpgsqlTypes;

namespace GuiyangMahjong.Auth.Storage;

/// <summary>
/// PostgreSQL Auth 生产存储。
/// 身份、刷新令牌轮换、会话撤销和玩家控制使用事务及行锁保证多副本一致；
/// 只持久化令牌哈希和脱敏登录观察值，该实例拥有数据源生命周期。
/// </summary>
public sealed class PostgresAuthStore(NpgsqlDataSource postgres)
    : IAuthStore, IPlayerProfileReader, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var path = AuthStoragePaths.SchemaPath;
        await using var command = postgres.CreateCommand(await File.ReadAllTextAsync(path, cancellationToken));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = postgres.CreateCommand(
                """
                SELECT to_regclass('auth.auth_identities') IS NOT NULL
                   AND to_regclass('session.auth_refresh_sessions') IS NOT NULL
                   AND to_regclass('player.player_profiles') IS NOT NULL
                   AND to_regclass('integration.auth_devices') IS NOT NULL
                   AND to_regclass('integration.identity_outbox') IS NOT NULL
                   AND to_regclass('identity_integration.platform_outbox') IS NOT NULL
                """);
            return await command.ExecuteScalarAsync(cancellationToken) is true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<AuthIdentity> GetOrCreateGuestAsync(
        string installationHash,
        AuthIdentity proposedIdentity,
        CancellationToken cancellationToken)
    {
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO auth.auth_identities(
                installation_hash, player_id, display_name, provider,
                session_epoch, security_epoch, created_at_utc, updated_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (installation_hash) DO UPDATE
                SET updated_at_utc = auth_identities.updated_at_utc
            RETURNING player_id, display_name, provider, created_at_utc, updated_at_utc,
                      session_epoch, security_epoch
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(installationHash);
        command.Parameters.AddWithValue(proposedIdentity.PlayerId);
        command.Parameters.AddWithValue(proposedIdentity.DisplayName);
        command.Parameters.AddWithValue(proposedIdentity.Provider);
        command.Parameters.AddWithValue(proposedIdentity.SessionEpoch);
        command.Parameters.AddWithValue(proposedIdentity.SecurityEpoch);
        command.Parameters.AddWithValue(proposedIdentity.CreatedAtUtc);
        command.Parameters.AddWithValue(proposedIdentity.UpdatedAtUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("Auth identity upsert returned no row.");
        var identity = ReadIdentity(reader);
        await reader.DisposeAsync();

        // 玩家档案和身份首次创建处于同一事务，避免认证成功后出现没有长期档案的半完成状态。
        await using var profile = new NpgsqlCommand(
            """
            INSERT INTO player.player_profiles(
                player_id, display_name, level, settings_json,
                privacy_settings_json, updated_at_utc)
            VALUES ($1, $2, 1, '{}'::jsonb, '{}'::jsonb, $3)
            ON CONFLICT (player_id) DO NOTHING
            """,
            connection,
            transaction);
        profile.Parameters.AddWithValue(identity.PlayerId);
        profile.Parameters.AddWithValue(identity.DisplayName);
        profile.Parameters.AddWithValue(identity.UpdatedAtUtc);
        await profile.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return identity;
    }

    /// <inheritdoc/>
    public async Task<SessionCreationStatus> CreateRefreshSessionAsync(
        RefreshSession session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var control = new NpgsqlCommand(
            """
            SELECT control.account_status, control.frozen_until_utc
            FROM auth.auth_identities AS identity
            LEFT JOIN auth.auth_player_controls AS control
                ON control.player_id=identity.player_id
            WHERE identity.player_id=$1
            FOR UPDATE OF identity
            """,
            connection,
            transaction);
        control.Parameters.AddWithValue(session.PlayerId);
        await using var controlReader = await control.ExecuteReaderAsync(cancellationToken);
        if (!await controlReader.ReadAsync(cancellationToken))
            throw new InvalidDataException("Auth identity was not found for session creation.");
        var accountStatus = controlReader.IsDBNull(0)
            ? "Active"
            : controlReader.GetString(0);
        DateTimeOffset? frozenUntil = controlReader.IsDBNull(1)
            ? null
            : controlReader.GetFieldValue<DateTimeOffset>(1);
        await controlReader.DisposeAsync();
        var creationStatus = accountStatus == "Banned"
            ? SessionCreationStatus.Banned
            : accountStatus == "Frozen" && frozenUntil > now
                ? SessionCreationStatus.Frozen
                : SessionCreationStatus.Created;
        if (creationStatus != SessionCreationStatus.Created)
        {
            await transaction.RollbackAsync(cancellationToken);
            return creationStatus;
        }

        // 单端策略撤销全部旧会话；多端策略按最早创建时间淘汰超出上限的会话。
        // 身份行锁使多个 Auth 副本同时登录同一玩家时仍能得到确定结果。
        var policySql = session.SessionMode == "SingleDevice"
            ? """
              UPDATE session.auth_refresh_sessions
              SET revoked_at_utc=$1, revocation_reason='SessionPolicy'
              WHERE player_id=$2 AND revoked_at_utc IS NULL AND expires_at_utc>$1
              RETURNING session_id
              """
            : """
              WITH active AS (
                  SELECT session_id
                  FROM session.auth_refresh_sessions
                  WHERE player_id=$2 AND revoked_at_utc IS NULL AND expires_at_utc>$1
                  ORDER BY created_at_utc, session_id
                  OFFSET $3
              )
              UPDATE session.auth_refresh_sessions AS target
              SET revoked_at_utc=$1, revocation_reason='SessionPolicy'
              FROM active
              WHERE target.session_id=active.session_id
              RETURNING target.session_id
              """;
        var policyRevokedSessionIds = new List<string>();
        await using (var enforcePolicy = new NpgsqlCommand(policySql, connection, transaction))
        {
            enforcePolicy.Parameters.AddWithValue(now);
            enforcePolicy.Parameters.AddWithValue(session.PlayerId);
            if (session.SessionMode != "SingleDevice")
                enforcePolicy.Parameters.AddWithValue(
                    Math.Max(0, session.MaximumActiveSessions - 1));
            await using var policyReader =
                await enforcePolicy.ExecuteReaderAsync(cancellationToken);
            while (await policyReader.ReadAsync(cancellationToken))
                policyRevokedSessionIds.Add(policyReader.GetString(0));
        }
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO session.auth_refresh_sessions(
                session_id, player_id, token_hash, family_id, parent_session_id,
                device_id, session_epoch, security_epoch, expires_at_utc,
                created_at_utc, revoked_at_utc, replaced_by_session_id,
                revocation_reason, reuse_detected_at_utc)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14)
            """,
            connection,
            transaction);
        AddSessionParameters(command, session);
        await command.ExecuteNonQueryAsync(cancellationToken);
        foreach (var revokedSessionId in policyRevokedSessionIds)
        {
            await AppendSessionRevocationOutboxAsync(
                connection,
                transaction,
                revokedSessionId,
                session.PlayerId,
                "SessionPolicy",
                now,
                traceId: null,
                correlationId: session.FamilyId,
                cancellationToken);
        }
        await AppendSessionCreatedOutboxAsync(
            connection,
            transaction,
            session,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return SessionCreationStatus.Created;
    }

    /// <inheritdoc/>
    public async Task<RefreshRotationResult> RotateRefreshSessionAsync(
        string currentSessionId,
        byte[] currentTokenHash,
        RefreshSession replacement,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var select = new NpgsqlCommand(
            """
            SELECT session.player_id, session.token_hash, session.expires_at_utc, session.revoked_at_utc,
                   session.family_id, session.device_id, session.revocation_reason,
                   identity.display_name, identity.provider, identity.created_at_utc,
                   identity.updated_at_utc, identity.session_epoch, identity.security_epoch,
                   control.account_status, control.frozen_until_utc
            FROM session.auth_refresh_sessions AS session
            JOIN auth.auth_identities AS identity ON identity.player_id = session.player_id
            LEFT JOIN auth.auth_player_controls AS control ON control.player_id=identity.player_id
            WHERE session.session_id = $1
            FOR UPDATE OF session, identity
            """, connection, transaction);
        select.Parameters.AddWithValue(currentSessionId);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.DisposeAsync();
            await transaction.RollbackAsync(cancellationToken);
            return new RefreshRotationResult(RefreshRotationStatus.NotFound, null);
        }
        var playerId = reader.GetString(0);
        var storedHash = reader.GetFieldValue<byte[]>(1);
        var expiresAt = reader.GetFieldValue<DateTimeOffset>(2);
        DateTimeOffset? revokedAt = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3);
        var familyId = reader.GetString(4);
        var deviceId = reader.GetString(5);
        var revocationReason = reader.IsDBNull(6) ? null : reader.GetString(6);
        var identity = new AuthIdentity(
            playerId,
            reader.GetString(7),
            reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetFieldValue<DateTimeOffset>(10),
            reader.GetInt64(11),
            reader.GetInt64(12));
        var accountStatus = reader.IsDBNull(13) ? "Active" : reader.GetString(13);
        DateTimeOffset? frozenUntil = reader.IsDBNull(14)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(14);
        await reader.DisposeAsync();

        if (!FixedTimeEquals(storedHash, currentTokenHash))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new RefreshRotationResult(RefreshRotationStatus.Invalid, null);
        }
        if (revokedAt is not null && revocationReason == "Rotated")
        {
            // 正确的旧 Refresh Token 在轮换后再次出现属于凭证重用。
            // 在同一事务内撤销整个 Family 并推进 Epoch，避免任一并发副本继续签发。
            await using var revokeFamily = new NpgsqlCommand(
                """
                UPDATE session.auth_refresh_sessions
                SET revoked_at_utc=COALESCE(revoked_at_utc,$1),
                    revocation_reason='RefreshTokenReuse',
                    reuse_detected_at_utc=$1
                WHERE player_id=$2 AND family_id=$3
                RETURNING session_id
                """,
                connection,
                transaction);
            revokeFamily.Parameters.AddWithValue(now);
            revokeFamily.Parameters.AddWithValue(playerId);
            revokeFamily.Parameters.AddWithValue(familyId);
            var compromisedSessionIds = new List<string>();
            await using (var familyReader =
                         await revokeFamily.ExecuteReaderAsync(cancellationToken))
            {
                while (await familyReader.ReadAsync(cancellationToken))
                    compromisedSessionIds.Add(familyReader.GetString(0));
            }
            foreach (var compromisedSessionId in compromisedSessionIds)
            {
                await AppendSessionRevocationOutboxAsync(
                    connection,
                    transaction,
                    compromisedSessionId,
                    playerId,
                    "RefreshTokenReuse",
                    now,
                    traceId: null,
                    correlationId: familyId,
                    cancellationToken);
            }
            await using var advanceEpoch = new NpgsqlCommand(
                """
                UPDATE auth.auth_identities
                SET session_epoch=session_epoch+1,
                    security_epoch=security_epoch+1,
                    updated_at_utc=$1
                WHERE player_id=$2
                """,
                connection,
                transaction);
            advanceEpoch.Parameters.AddWithValue(now);
            advanceEpoch.Parameters.AddWithValue(playerId);
            await advanceEpoch.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new RefreshRotationResult(RefreshRotationStatus.ReuseDetected, null);
        }

        var status = revokedAt is not null
            ? RefreshRotationStatus.Revoked
            : expiresAt <= now
                ? RefreshRotationStatus.Expired
                : accountStatus == "Banned"
                    ? RefreshRotationStatus.Banned
                    : accountStatus == "Frozen" && frozenUntil > now
                        ? RefreshRotationStatus.Frozen
                        : RefreshRotationStatus.Rotated;
        if (status != RefreshRotationStatus.Rotated)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new RefreshRotationResult(status, null);
        }

        var effectiveReplacement = replacement with
        {
            PlayerId = playerId,
            FamilyId = familyId,
            ParentSessionId = currentSessionId,
            DeviceId = deviceId,
            SessionEpoch = identity.SessionEpoch,
            SecurityEpoch = identity.SecurityEpoch
        };
        await using var revoke = new NpgsqlCommand(
            """
            UPDATE session.auth_refresh_sessions
            SET revoked_at_utc=$1, replaced_by_session_id=$2, revocation_reason='Rotated'
            WHERE session_id=$3
            """,
            connection, transaction);
        revoke.Parameters.AddWithValue(now);
        revoke.Parameters.AddWithValue(effectiveReplacement.SessionId);
        revoke.Parameters.AddWithValue(currentSessionId);
        await revoke.ExecuteNonQueryAsync(cancellationToken);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO session.auth_refresh_sessions(
                session_id, player_id, token_hash, family_id, parent_session_id,
                device_id, session_epoch, security_epoch, expires_at_utc,
                created_at_utc, revoked_at_utc, replaced_by_session_id,
                revocation_reason, reuse_detected_at_utc)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14)
            """, connection, transaction);
        AddSessionParameters(insert, effectiveReplacement);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await AppendSessionRevocationOutboxAsync(
            connection,
            transaction,
            currentSessionId,
            playerId,
            "Rotated",
            now,
            traceId: null,
            correlationId: familyId,
            cancellationToken);
        await AppendSessionCreatedOutboxAsync(
            connection,
            transaction,
            effectiveReplacement,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RefreshRotationResult(
            RefreshRotationStatus.Rotated,
            identity,
            effectiveReplacement);
    }

    /// <inheritdoc/>
    public async Task<bool> RevokeRefreshSessionAsync(
        string sessionId,
        byte[] tokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var select = new NpgsqlCommand(
            """
            SELECT token_hash, revoked_at_utc, player_id
            FROM session.auth_refresh_sessions
            WHERE session_id=$1
            FOR UPDATE
            """,
            connection, transaction);
        select.Parameters.AddWithValue(sessionId);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.DisposeAsync();
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        var matches = reader.IsDBNull(1)
                      && FixedTimeEquals(reader.GetFieldValue<byte[]>(0), tokenHash);
        var playerId = reader.GetString(2);
        await reader.DisposeAsync();
        if (!matches)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await using var revoke = new NpgsqlCommand(
            """
            UPDATE session.auth_refresh_sessions
            SET revoked_at_utc=$1, revocation_reason='Logout'
            WHERE session_id=$2
            """,
            connection, transaction);
        revoke.Parameters.AddWithValue(now);
        revoke.Parameters.AddWithValue(sessionId);
        await revoke.ExecuteNonQueryAsync(cancellationToken);
        await AppendSessionRevocationOutboxAsync(
            connection,
            transaction,
            sessionId,
            playerId,
            "Logout",
            now,
            traceId: null,
            correlationId: sessionId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc/>
    public async Task<AdminRevokePlayerSessionsResult> RevokePlayerSessionsAsync(
        string commandId,
        string playerId,
        DateTimeOffset effectiveAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var playerFound = false;
        await using (var exists = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM auth.auth_identities WHERE player_id=$1)",
            connection,
            transaction))
        {
            exists.Parameters.AddWithValue(playerId);
            playerFound = (bool)(await exists.ExecuteScalarAsync(cancellationToken) ?? false);
        }

        await using var receipt = new NpgsqlCommand(
            """
            INSERT INTO auth.auth_admin_commands(
                command_id, command_type, target_id, effective_at_utc,
                processed_at_utc, player_found, affected_count)
            VALUES ($1,'RevokePlayerSessions',$2,$3,$3,$4,0)
            ON CONFLICT (command_id) DO NOTHING
            RETURNING command_id
            """,
            connection,
            transaction);
        receipt.Parameters.AddWithValue(commandId);
        receipt.Parameters.AddWithValue(playerId);
        receipt.Parameters.AddWithValue(effectiveAtUtc);
        receipt.Parameters.AddWithValue(playerFound);
        var inserted = await receipt.ExecuteScalarAsync(cancellationToken) is not null;
        if (!inserted)
        {
            await using var existing = new NpgsqlCommand(
                """
                SELECT target_id, player_found, affected_count, effective_at_utc
                FROM auth.auth_admin_commands
                WHERE command_id=$1 AND command_type='RevokePlayerSessions'
                """,
                connection,
                transaction);
            existing.Parameters.AddWithValue(commandId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException(
                    "Admin command id was reused for a different command type.");
            var storedPlayerId = reader.GetString(0);
            var storedEffectiveAtUtc = reader.GetFieldValue<DateTimeOffset>(3);
            if (!string.Equals(storedPlayerId, playerId, StringComparison.Ordinal)
                || (storedEffectiveAtUtc - effectiveAtUtc).Duration()
                    > TimeSpan.FromMilliseconds(1))
            {
                throw new InvalidOperationException(
                    "Admin command id was reused with different command parameters.");
            }
            var result = new AdminRevokePlayerSessionsResult(
                commandId,
                storedPlayerId,
                reader.GetBoolean(1),
                reader.GetInt32(2),
                storedEffectiveAtUtc,
                true);
            await reader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            return result;
        }

        await using var revoke = new NpgsqlCommand(
            """
            UPDATE session.auth_refresh_sessions
            SET revoked_at_utc=$1, revocation_reason='AdministrativeRevocation'
            WHERE player_id=$2
              AND created_at_utc <= $1
              AND expires_at_utc > $1
              AND revoked_at_utc IS NULL
            RETURNING session_id
            """,
            connection,
            transaction);
        revoke.Parameters.AddWithValue(effectiveAtUtc);
        revoke.Parameters.AddWithValue(playerId);
        var revokedSessionIds = new List<string>();
        await using (var revokedReader = await revoke.ExecuteReaderAsync(cancellationToken))
        {
            while (await revokedReader.ReadAsync(cancellationToken))
                revokedSessionIds.Add(revokedReader.GetString(0));
        }
        var revoked = revokedSessionIds.Count;
        foreach (var revokedSessionId in revokedSessionIds)
        {
            await AppendSessionRevocationOutboxAsync(
                connection,
                transaction,
                revokedSessionId,
                playerId,
                "AdministrativeRevocation",
                effectiveAtUtc,
                traceId: null,
                correlationId: commandId,
                cancellationToken);
        }
        if (playerFound)
        {
            await using var advanceEpoch = new NpgsqlCommand(
                """
                UPDATE auth.auth_identities
                SET session_epoch=session_epoch+1, updated_at_utc=$1
                WHERE player_id=$2
                """,
                connection,
                transaction);
            advanceEpoch.Parameters.AddWithValue(effectiveAtUtc);
            advanceEpoch.Parameters.AddWithValue(playerId);
            await advanceEpoch.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var updateReceipt = new NpgsqlCommand(
            "UPDATE auth.auth_admin_commands SET affected_count=$1 WHERE command_id=$2",
            connection,
            transaction);
        updateReceipt.Parameters.AddWithValue(revoked);
        updateReceipt.Parameters.AddWithValue(commandId);
        await updateReceipt.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdminRevokePlayerSessionsResult(
            commandId,
            playerId,
            playerFound,
            revoked,
            effectiveAtUtc,
            false);
    }

    /// <inheritdoc/>
    public async Task<AdminPlayerControlStoreResult> ApplyPlayerControlAsync(
        string commandId,
        string playerId,
        AdminPlayerControlAction action,
        long expectedVersion,
        string reason,
        string traceId,
        string ticketId,
        string requestedBy,
        string approvedBy,
        DateTimeOffset effectiveAtUtc,
        DateTimeOffset? expiresAtUtc,
        string? riskLabel,
        CancellationToken cancellationToken)
    {
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var commandLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))",
            connection,
            transaction))
        {
            commandLock.Parameters.AddWithValue(commandId);
            await commandLock.ExecuteNonQueryAsync(cancellationToken);
        }
        var duplicate = await ReadControlEventAsync(
            connection,
            transaction,
            commandId,
            cancellationToken);
        if (duplicate is not null)
        {
            EnsureSameControlCommand(
                duplicate,
                playerId,
                action,
                expectedVersion,
                reason,
                traceId,
                ticketId,
                requestedBy,
                approvedBy,
                effectiveAtUtc,
                expiresAtUtc,
                riskLabel);
            await transaction.CommitAsync(cancellationToken);
            return new AdminPlayerControlStoreResult(
                AdminPlayerControlStatus.Duplicate,
                ToControlResult(duplicate, true),
                duplicate.AfterState,
                null);
        }

        await using (var identityLock = new NpgsqlCommand(
            """
            SELECT player_id
            FROM auth.auth_identities
            WHERE player_id=$1
            FOR UPDATE
            """,
            connection,
            transaction))
        {
            identityLock.Parameters.AddWithValue(playerId);
            if (await identityLock.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AdminPlayerControlStoreResult(
                    AdminPlayerControlStatus.PlayerNotFound,
                    null,
                    null,
                    "Player was not found.");
            }
        }

        PlayerControlState before;
        await using (var select = new NpgsqlCommand(
            """
            SELECT version, account_status, frozen_until_utc, muted_until_utc,
                   risk_labels, risk_labels_expire_at_utc, updated_at_utc
            FROM auth.auth_player_controls
            WHERE player_id=$1
            """,
            connection,
            transaction))
        {
            select.Parameters.AddWithValue(playerId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            before = await reader.ReadAsync(cancellationToken)
                ? ReadControlState(reader)
                : PlayerControlPolicy.Empty;
        }
        before = PlayerControlPolicy.Normalize(before, effectiveAtUtc);
        if (before.Version != expectedVersion)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AdminPlayerControlStoreResult(
                AdminPlayerControlStatus.VersionConflict,
                null,
                before,
                "Player control state changed.");
        }
        var transition = PlayerControlPolicy.Apply(
            before,
            action,
            effectiveAtUtc,
            expiresAtUtc,
            riskLabel);
        if (transition.State is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AdminPlayerControlStoreResult(
                AdminPlayerControlStatus.InvalidTransition,
                null,
                before,
                transition.Error);
        }
        var after = transition.State;
        await using (var upsert = new NpgsqlCommand(
            """
            INSERT INTO auth.auth_player_controls(
                player_id, version, account_status, frozen_until_utc,
                muted_until_utc, risk_labels, risk_labels_expire_at_utc,
                updated_at_utc)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8)
            ON CONFLICT (player_id) DO UPDATE SET
                version=EXCLUDED.version,
                account_status=EXCLUDED.account_status,
                frozen_until_utc=EXCLUDED.frozen_until_utc,
                muted_until_utc=EXCLUDED.muted_until_utc,
                risk_labels=EXCLUDED.risk_labels,
                risk_labels_expire_at_utc=EXCLUDED.risk_labels_expire_at_utc,
                updated_at_utc=EXCLUDED.updated_at_utc
            """,
            connection,
            transaction))
        {
            AddControlStateParameters(upsert, playerId, after);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        var revoked = 0;
        if (action is AdminPlayerControlAction.TemporaryFreezePlayer
            or AdminPlayerControlAction.PermanentBanPlayer)
        {
            await using var revoke = new NpgsqlCommand(
                """
                UPDATE session.auth_refresh_sessions
                SET revoked_at_utc=$1, revocation_reason=$3
                WHERE player_id=$2
                  AND expires_at_utc > $1
                  AND revoked_at_utc IS NULL
                RETURNING session_id
                """,
                connection,
                transaction);
            revoke.Parameters.AddWithValue(effectiveAtUtc);
            revoke.Parameters.AddWithValue(playerId);
            revoke.Parameters.AddWithValue(action.ToString());
            var revokedSessionIds = new List<string>();
            await using (var revokedReader = await revoke.ExecuteReaderAsync(cancellationToken))
            {
                while (await revokedReader.ReadAsync(cancellationToken))
                    revokedSessionIds.Add(revokedReader.GetString(0));
            }
            revoked = revokedSessionIds.Count;
            foreach (var revokedSessionId in revokedSessionIds)
            {
                await AppendSessionRevocationOutboxAsync(
                    connection,
                    transaction,
                    revokedSessionId,
                    playerId,
                    action.ToString(),
                    effectiveAtUtc,
                    traceId,
                    commandId,
                    cancellationToken);
            }
            await using var advanceEpoch = new NpgsqlCommand(
                """
                UPDATE auth.auth_identities
                SET session_epoch=session_epoch+1,
                    security_epoch=security_epoch+1,
                    updated_at_utc=$1
                WHERE player_id=$2
                """,
                connection,
                transaction);
            advanceEpoch.Parameters.AddWithValue(effectiveAtUtc);
            advanceEpoch.Parameters.AddWithValue(playerId);
            await advanceEpoch.ExecuteNonQueryAsync(cancellationToken);
        }

        var controlEvent = new PlayerControlEvent(
            commandId,
            playerId,
            action.ToString(),
            reason,
            traceId,
            ticketId,
            requestedBy,
            approvedBy,
            effectiveAtUtc,
            expiresAtUtc,
            riskLabel,
            revoked,
            before,
            after);
        await using (var insertEvent = new NpgsqlCommand(
            """
            INSERT INTO auth.auth_player_control_events(
                command_id, player_id, action_type, reason, trace_id, ticket_id,
                requested_by, approved_by, effective_at_utc, expires_at_utc,
                risk_label, expected_version, revoked_session_count,
                before_state, after_state)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15)
            """,
            connection,
            transaction))
        {
            AddControlEventParameters(insertEvent, controlEvent);
            await insertEvent.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new AdminPlayerControlStoreResult(
            AdminPlayerControlStatus.Applied,
            ToControlResult(controlEvent, false),
            after,
            null);
    }

    /// <inheritdoc/>
    public async Task RecordLoginAsync(
        AuthLoginEvent loginEvent, CancellationToken cancellationToken)
    {
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? previousDeviceId = null;
        if (loginEvent.Outcome == "Success")
        {
            await using var previous = new NpgsqlCommand(
                """
                SELECT device_id
                FROM integration.auth_login_events
                WHERE player_id=$1 AND outcome='Success'
                ORDER BY occurred_at_utc DESC
                LIMIT 1
                """,
                connection,
                transaction);
            previous.Parameters.AddWithValue(loginEvent.PlayerId);
            previousDeviceId = await previous.ExecuteScalarAsync(cancellationToken) as string;
        }

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO integration.auth_login_events(
                event_id, player_id, device_id, masked_ip, client_summary, outcome, occurred_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (event_id) DO NOTHING
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(Guid.Parse(loginEvent.EventId));
        command.Parameters.AddWithValue(loginEvent.PlayerId);
        command.Parameters.AddWithValue(loginEvent.DeviceId);
        command.Parameters.AddWithValue(loginEvent.MaskedIp);
        command.Parameters.AddWithValue(loginEvent.ClientSummary);
        command.Parameters.AddWithValue(loginEvent.Outcome);
        command.Parameters.AddWithValue(loginEvent.OccurredAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (loginEvent.Outcome == "Success")
        {
            await using var device = new NpgsqlCommand(
                """
                INSERT INTO integration.auth_devices(
                    player_id, device_id, trust_state, risk_label_references,
                    first_seen_at_utc, last_used_at_utc)
                VALUES ($1,$2,'Unknown','{}',$3,$3)
                ON CONFLICT (player_id, device_id) DO UPDATE
                    SET last_used_at_utc=EXCLUDED.last_used_at_utc
                """,
                connection,
                transaction);
            device.Parameters.AddWithValue(loginEvent.PlayerId);
            device.Parameters.AddWithValue(loginEvent.DeviceId);
            device.Parameters.AddWithValue(loginEvent.OccurredAtUtc);
            await device.ExecuteNonQueryAsync(cancellationToken);
            if (previousDeviceId is not null && previousDeviceId != loginEvent.DeviceId)
            {
                await using var switched = new NpgsqlCommand(
                    """
                    INSERT INTO integration.auth_device_switch_events(
                        event_id, player_id, previous_device_id,
                        current_device_id, occurred_at_utc)
                    VALUES ($1,$2,$3,$4,$5)
                    ON CONFLICT (event_id) DO NOTHING
                    """,
                    connection,
                    transaction);
                switched.Parameters.AddWithValue(Guid.Parse(loginEvent.EventId));
                switched.Parameters.AddWithValue(loginEvent.PlayerId);
                switched.Parameters.AddWithValue(previousDeviceId);
                switched.Parameters.AddWithValue(loginEvent.DeviceId);
                switched.Parameters.AddWithValue(loginEvent.OccurredAtUtc);
                await switched.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<PlayerProfile?> GetProfileAsync(
        string playerId,
        CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            """
            SELECT player_id, display_name, avatar_url, region, level,
                   settings_json::text, privacy_settings_json::text, updated_at_utc
            FROM player.player_profiles
            WHERE player_id=$1
            """);
        command.Parameters.AddWithValue(playerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new PlayerProfile(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PlayerDirectoryItem>> ListPlayersAsync(
        string? search,
        int limit,
        DateTimeOffset? afterCreatedAtUtc,
        string? afterPlayerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            """
            SELECT identity.player_id, identity.display_name, identity.provider,
                   identity.created_at_utc, identity.updated_at_utc,
                   latest.occurred_at_utc, latest.device_id, latest.masked_ip,
                   (SELECT COUNT(*) FROM session.auth_refresh_sessions AS session
                    WHERE session.player_id=identity.player_id
                      AND session.revoked_at_utc IS NULL
                      AND session.expires_at_utc > $1),
                   COALESCE(control.version, 0),
                   CASE
                       WHEN control.account_status='Banned' THEN 'Banned'
                       WHEN control.account_status='Frozen'
                            AND control.frozen_until_utc > $1 THEN 'Frozen'
                       ELSE 'Active'
                   END,
                   CASE WHEN control.frozen_until_utc > $1
                        THEN control.frozen_until_utc ELSE NULL END,
                   CASE WHEN control.muted_until_utc > $1
                        THEN control.muted_until_utc ELSE NULL END,
                   CASE WHEN control.risk_labels_expire_at_utc > $1
                        THEN control.risk_labels ELSE ARRAY[]::TEXT[] END
            FROM auth.auth_identities AS identity
            LEFT JOIN auth.auth_player_controls AS control
                ON control.player_id=identity.player_id
            LEFT JOIN LATERAL (
                SELECT occurred_at_utc, device_id, masked_ip
                FROM integration.auth_login_events
                WHERE player_id=identity.player_id AND outcome='Success'
                ORDER BY occurred_at_utc DESC LIMIT 1
            ) AS latest ON TRUE
            WHERE ($2='' OR identity.player_id ILIKE '%' || $2 || '%'
                         OR identity.display_name ILIKE '%' || $2 || '%')
              AND (
                  $3::timestamptz IS NULL
                  OR (identity.created_at_utc, identity.player_id)
                     < ($3, $4))
            ORDER BY identity.created_at_utc DESC, identity.player_id DESC
            LIMIT $5
            """);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(search?.Trim() ?? string.Empty);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
            Value = afterCreatedAtUtc.HasValue
                ? afterCreatedAtUtc.Value
                : DBNull.Value
        });
        command.Parameters.AddWithValue(afterPlayerId ?? string.Empty);
        command.Parameters.AddWithValue(limit);
        var result = new List<PlayerDirectoryItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadDirectoryItem(reader));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<PlayerDirectoryDetail?> GetPlayerDetailAsync(
        string playerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var identityCommand = new NpgsqlCommand(
            """
            SELECT identity.player_id, identity.display_name, identity.provider,
                   identity.created_at_utc, identity.updated_at_utc,
                   latest.occurred_at_utc, latest.device_id, latest.masked_ip,
                   (SELECT COUNT(*) FROM session.auth_refresh_sessions AS session
                    WHERE session.player_id=identity.player_id
                      AND session.revoked_at_utc IS NULL
                      AND session.expires_at_utc > $1),
                   COALESCE(control.version, 0),
                   CASE
                       WHEN control.account_status='Banned' THEN 'Banned'
                       WHEN control.account_status='Frozen'
                            AND control.frozen_until_utc > $1 THEN 'Frozen'
                       ELSE 'Active'
                   END,
                   CASE WHEN control.frozen_until_utc > $1
                        THEN control.frozen_until_utc ELSE NULL END,
                   CASE WHEN control.muted_until_utc > $1
                        THEN control.muted_until_utc ELSE NULL END,
                   CASE WHEN control.risk_labels_expire_at_utc > $1
                        THEN control.risk_labels ELSE ARRAY[]::TEXT[] END
            FROM auth.auth_identities AS identity
            LEFT JOIN auth.auth_player_controls AS control
                ON control.player_id=identity.player_id
            LEFT JOIN LATERAL (
                SELECT occurred_at_utc, device_id, masked_ip
                FROM integration.auth_login_events
                WHERE player_id=identity.player_id AND outcome='Success'
                ORDER BY occurred_at_utc DESC LIMIT 1
            ) AS latest ON TRUE
            WHERE identity.player_id=$2
            """, connection);
        identityCommand.Parameters.AddWithValue(now);
        identityCommand.Parameters.AddWithValue(playerId);
        await using var identityReader = await identityCommand.ExecuteReaderAsync(cancellationToken);
        if (!await identityReader.ReadAsync(cancellationToken)) return null;
        var player = ReadDirectoryItem(identityReader);
        await identityReader.DisposeAsync();

        var sessions = new List<AuthSessionMonitor>();
        await using var sessionCommand = new NpgsqlCommand(
            """
            SELECT session_id, created_at_utc, expires_at_utc, revoked_at_utc
            FROM session.auth_refresh_sessions
            WHERE player_id=$1
            ORDER BY created_at_utc DESC LIMIT 100
            """, connection);
        sessionCommand.Parameters.AddWithValue(playerId);
        await using var sessionReader = await sessionCommand.ExecuteReaderAsync(cancellationToken);
        while (await sessionReader.ReadAsync(cancellationToken))
        {
            var sessionId = sessionReader.GetString(0);
            var expiresAt = sessionReader.GetFieldValue<DateTimeOffset>(2);
            DateTimeOffset? revokedAt = sessionReader.IsDBNull(3)
                ? null
                : sessionReader.GetFieldValue<DateTimeOffset>(3);
            sessions.Add(new AuthSessionMonitor(
                $"{sessionId[..8]}…",
                sessionReader.GetFieldValue<DateTimeOffset>(1),
                expiresAt,
                revokedAt,
                revokedAt is null && expiresAt > now));
        }
        await sessionReader.DisposeAsync();

        var logins = new List<AuthLoginEvent>();
        await using var loginCommand = new NpgsqlCommand(
            """
            SELECT event_id, device_id, masked_ip, client_summary, outcome, occurred_at_utc
            FROM integration.auth_login_events
            WHERE player_id=$1
            ORDER BY occurred_at_utc DESC LIMIT 200
            """, connection);
        loginCommand.Parameters.AddWithValue(playerId);
        await using var loginReader = await loginCommand.ExecuteReaderAsync(cancellationToken);
        while (await loginReader.ReadAsync(cancellationToken))
        {
            logins.Add(new AuthLoginEvent(
                loginReader.GetGuid(0).ToString(),
                playerId,
                loginReader.GetString(1),
                loginReader.GetString(2),
                loginReader.GetString(3),
                loginReader.GetString(4),
                loginReader.GetFieldValue<DateTimeOffset>(5)));
        }
        await loginReader.DisposeAsync();

        var controlHistory = new List<PlayerControlEvent>();
        await using var controlCommand = new NpgsqlCommand(
            """
            SELECT command_id, player_id, action_type, reason, trace_id, ticket_id,
                   requested_by, approved_by, effective_at_utc, expires_at_utc,
                   risk_label, revoked_session_count,
                   before_state::text, after_state::text
            FROM auth.auth_player_control_events
            WHERE player_id=$1
            ORDER BY effective_at_utc DESC
            LIMIT 200
            """,
            connection);
        controlCommand.Parameters.AddWithValue(playerId);
        await using var controlReader =
            await controlCommand.ExecuteReaderAsync(cancellationToken);
        while (await controlReader.ReadAsync(cancellationToken))
            controlHistory.Add(ReadControlEvent(
                controlReader,
                controlReader.GetString(0),
                1));
        return new PlayerDirectoryDetail(
            player,
            sessions.ToArray(),
            logins.ToArray(),
            logins.Select(item => item.DeviceId).Distinct(StringComparer.Ordinal).ToArray(),
            controlHistory.ToArray());
    }

    private static AuthIdentity ReadIdentity(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetFieldValue<DateTimeOffset>(3),
        reader.GetFieldValue<DateTimeOffset>(4),
        reader.GetInt64(5),
        reader.GetInt64(6));

    private static PlayerDirectoryItem ReadDirectoryItem(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(10),
        reader.GetFieldValue<DateTimeOffset>(3),
        reader.GetFieldValue<DateTimeOffset>(4),
        reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        checked((int)reader.GetInt64(8)),
        reader.GetInt64(9),
        reader.IsDBNull(11)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(11),
        reader.IsDBNull(12)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(12),
        reader.GetFieldValue<string[]>(13));

    private static void AddSessionParameters(NpgsqlCommand command, RefreshSession session)
    {
        command.Parameters.AddWithValue(session.SessionId);
        command.Parameters.AddWithValue(session.PlayerId);
        command.Parameters.AddWithValue(session.TokenHash);
        command.Parameters.AddWithValue(session.FamilyId);
        command.Parameters.AddWithValue((object?)session.ParentSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue(session.DeviceId);
        command.Parameters.AddWithValue(session.SessionEpoch);
        command.Parameters.AddWithValue(session.SecurityEpoch);
        command.Parameters.AddWithValue(session.ExpiresAtUtc);
        command.Parameters.AddWithValue(session.CreatedAtUtc);
        command.Parameters.AddWithValue((object?)session.RevokedAtUtc ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)session.ReplacedBySessionId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)session.RevocationReason ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)session.ReuseDetectedAtUtc ?? DBNull.Value);
    }

    private static PlayerControlState ReadControlState(NpgsqlDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(2),
            reader.IsDBNull(3)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<string[]>(4),
            reader.IsDBNull(5)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6));

    private static void AddControlStateParameters(
        NpgsqlCommand command,
        string playerId,
        PlayerControlState state)
    {
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(state.Version);
        command.Parameters.AddWithValue(state.AccountStatus);
        command.Parameters.AddWithValue((object?)state.FrozenUntilUtc ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)state.MutedUntilUtc ?? DBNull.Value);
        command.Parameters.AddWithValue(state.RiskLabels);
        command.Parameters.AddWithValue(
            (object?)state.RiskLabelsExpireAtUtc ?? DBNull.Value);
        command.Parameters.AddWithValue(state.UpdatedAtUtc);
    }

    private static void AddControlEventParameters(
        NpgsqlCommand command,
        PlayerControlEvent controlEvent)
    {
        command.Parameters.AddWithValue(controlEvent.CommandId);
        command.Parameters.AddWithValue(controlEvent.PlayerId);
        command.Parameters.AddWithValue(controlEvent.ActionType);
        command.Parameters.AddWithValue(controlEvent.Reason);
        command.Parameters.AddWithValue(controlEvent.TraceId);
        command.Parameters.AddWithValue(controlEvent.TicketId);
        command.Parameters.AddWithValue(controlEvent.RequestedBy);
        command.Parameters.AddWithValue(controlEvent.ApprovedBy);
        command.Parameters.AddWithValue(controlEvent.EffectiveAtUtc);
        command.Parameters.AddWithValue(
            (object?)controlEvent.ExpiresAtUtc ?? DBNull.Value);
        command.Parameters.AddWithValue(
            (object?)controlEvent.RiskLabel ?? DBNull.Value);
        command.Parameters.AddWithValue(controlEvent.BeforeState.Version);
        command.Parameters.AddWithValue(controlEvent.RevokedSessionCount);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(controlEvent.BeforeState, JsonOptions));
        command.Parameters.AddWithValue(
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(controlEvent.AfterState, JsonOptions));
    }

    private static async Task<PlayerControlEvent?> ReadControlEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string commandId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT player_id, action_type, reason, trace_id, ticket_id,
                   requested_by, approved_by, effective_at_utc, expires_at_utc,
                   risk_label, revoked_session_count,
                   before_state::text, after_state::text
            FROM auth.auth_player_control_events
            WHERE command_id=$1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(commandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return ReadControlEvent(reader, commandId, 0);
    }

    private static PlayerControlEvent ReadControlEvent(
        NpgsqlDataReader reader,
        string commandId,
        int offset) =>
        new(
            commandId,
            reader.GetString(offset),
            reader.GetString(offset + 1),
            reader.GetString(offset + 2),
            reader.GetString(offset + 3),
            reader.GetString(offset + 4),
            reader.GetString(offset + 5),
            reader.GetString(offset + 6),
            reader.GetFieldValue<DateTimeOffset>(offset + 7),
            reader.IsDBNull(offset + 8)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(offset + 8),
            reader.IsDBNull(offset + 9)
                ? null
                : reader.GetString(offset + 9),
            reader.GetInt32(offset + 10),
            JsonSerializer.Deserialize<PlayerControlState>(
                reader.GetString(offset + 11),
                JsonOptions)
                ?? throw new InvalidDataException(
                    "Stored player control before-state is invalid."),
            JsonSerializer.Deserialize<PlayerControlState>(
                reader.GetString(offset + 12),
                JsonOptions)
                ?? throw new InvalidDataException(
                    "Stored player control after-state is invalid."));

    private static void EnsureSameControlCommand(
        PlayerControlEvent existing,
        string playerId,
        AdminPlayerControlAction action,
        long expectedVersion,
        string reason,
        string traceId,
        string ticketId,
        string requestedBy,
        string approvedBy,
        DateTimeOffset effectiveAtUtc,
        DateTimeOffset? expiresAtUtc,
        string? riskLabel)
    {
        if (existing.PlayerId != playerId
            || existing.ActionType != action.ToString()
            || existing.BeforeState.Version != expectedVersion
            || existing.Reason != reason
            || existing.TraceId != traceId
            || existing.TicketId != ticketId
            || existing.RequestedBy != requestedBy
            || existing.ApprovedBy != approvedBy
            || (existing.EffectiveAtUtc - effectiveAtUtc).Duration()
                > TimeSpan.FromMilliseconds(1)
            || !SameInstant(existing.ExpiresAtUtc, expiresAtUtc)
            || existing.RiskLabel != riskLabel)
        {
            throw new InvalidOperationException(
                "Admin command id was reused with different command parameters.");
        }
    }

    private static bool SameInstant(
        DateTimeOffset? left,
        DateTimeOffset? right) =>
        left.HasValue == right.HasValue
        && (!left.HasValue
            || (left.Value - right!.Value).Duration()
                <= TimeSpan.FromMilliseconds(1));

    private static AdminUpdatePlayerControlResult ToControlResult(
        PlayerControlEvent controlEvent,
        bool duplicate) =>
        new(
            controlEvent.CommandId,
            controlEvent.PlayerId,
            controlEvent.ActionType,
            controlEvent.BeforeState,
            controlEvent.AfterState,
            controlEvent.RevokedSessionCount,
            duplicate);

    /// <summary>
    /// 在撤销会话的同一数据库事务中追加版本化 SessionRevoked Outbox 事实。
    /// Payload 只包含契约允许的标识和原因，不包含 Token、哈希、IP 或设备指纹。
    /// </summary>
    private static async Task AppendSessionRevocationOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sessionId,
        string playerId,
        string reasonCode,
        DateTimeOffset revokedAt,
        string? traceId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var payload = new SessionRevoked(
            SessionId.Parse(sessionId),
            PlayerId.Parse(playerId),
            reasonCode,
            revokedAt);
        var envelope = EventEnvelope.Create(
            payload,
            "session",
            sessionId,
            aggregateVersion: 0,
            "identity-app",
            ResolveTraceId(traceId),
            ResolveCorrelationId(correlationId),
            revokedAt);
        await AppendPlatformOutboxAsync(
            connection,
            transaction,
            envelope,
            cancellationToken);
    }

    /// <summary>
    /// 在新 Refresh Session 与身份策略同一事务中追加 SessionCreated；
    /// 信封不包含 Token Hash、IP 或原始设备指纹。
    /// </summary>
    private static Task AppendSessionCreatedOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RefreshSession session,
        CancellationToken cancellationToken)
    {
        var payload = new SessionCreated(
            SessionId.Parse(session.SessionId),
            PlayerId.Parse(session.PlayerId),
            DeviceId.Parse(session.DeviceId),
            session.ExpiresAtUtc);
        var envelope = EventEnvelope.Create(
            payload,
            "session",
            session.SessionId,
            Math.Max(0, session.SessionEpoch),
            "identity-app",
            ResolveTraceId(null),
            ResolveCorrelationId(session.FamilyId),
            session.CreatedAtUtc);
        return AppendPlatformOutboxAsync(
            connection,
            transaction,
            envelope,
            cancellationToken);
    }

    /// <summary>
    /// 写入统一 Outbox；完整信封与业务会话共享事务，NATS 是否可用不会影响登录事务提交。
    /// </summary>
    private static async Task AppendPlatformOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO identity_integration.platform_outbox(
                event_id,event_type,schema_version,aggregate_type,aggregate_id,
                aggregate_version,payload_json,occurred_at,created_at,status,
                attempt_count,next_attempt_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$8,'Pending',0,$8)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(envelope.EventId.Value);
        command.Parameters.AddWithValue(envelope.EventType);
        command.Parameters.AddWithValue(envelope.SchemaVersion);
        command.Parameters.AddWithValue(envelope.AggregateType);
        command.Parameters.AddWithValue(envelope.AggregateId);
        command.Parameters.AddWithValue(envelope.AggregateVersion);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(envelope, JsonOptions));
        command.Parameters.AddWithValue(envelope.OccurredAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ResolveTraceId(string? traceId) =>
        !string.IsNullOrWhiteSpace(traceId)
            ? traceId
            : Activity.Current?.TraceId.ToString()
              ?? ActivityTraceId.CreateRandom().ToString();

    private static CorrelationId ResolveCorrelationId(string? correlationId) =>
        CorrelationId.TryParse(correlationId, out var parsed)
            ? parsed
            : CorrelationId.New();

    private static bool FixedTimeEquals(byte[] left, byte[] right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);

    /// <summary>异步释放该存储独占的 PostgreSQL 数据源和连接池。</summary>
    public ValueTask DisposeAsync() => postgres.DisposeAsync();
}
