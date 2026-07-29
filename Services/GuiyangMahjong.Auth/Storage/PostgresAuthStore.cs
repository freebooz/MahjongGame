using System.Security.Cryptography;
using System.Text.Json;
using GuiyangMahjong.Auth.Domain;
using Npgsql;
using NpgsqlTypes;

namespace GuiyangMahjong.Auth.Storage;

public sealed class PostgresAuthStore(NpgsqlDataSource postgres) : IAuthStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Storage", "schema.sql");
        await using var command = postgres.CreateCommand(await File.ReadAllTextAsync(path, cancellationToken));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = postgres.CreateCommand("SELECT 1");
            _ = await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    public async Task<AuthIdentity> GetOrCreateGuestAsync(
        string installationHash,
        AuthIdentity proposedIdentity,
        CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            """
            INSERT INTO auth_identities(
                installation_hash, player_id, display_name, provider, created_at_utc, updated_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT (installation_hash) DO UPDATE
                SET updated_at_utc = auth_identities.updated_at_utc
            RETURNING player_id, display_name, provider, created_at_utc, updated_at_utc
            """);
        command.Parameters.AddWithValue(installationHash);
        command.Parameters.AddWithValue(proposedIdentity.PlayerId);
        command.Parameters.AddWithValue(proposedIdentity.DisplayName);
        command.Parameters.AddWithValue(proposedIdentity.Provider);
        command.Parameters.AddWithValue(proposedIdentity.CreatedAtUtc);
        command.Parameters.AddWithValue(proposedIdentity.UpdatedAtUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("Auth identity upsert returned no row.");
        return ReadIdentity(reader);
    }

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
            FROM auth_identities AS identity
            LEFT JOIN auth_player_controls AS control
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
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO auth_refresh_sessions(
                session_id, player_id, token_hash, expires_at_utc, created_at_utc, revoked_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6)
            """,
            connection,
            transaction);
        AddSessionParameters(command, session);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return SessionCreationStatus.Created;
    }

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
                   identity.display_name, identity.provider, identity.created_at_utc, identity.updated_at_utc,
                   control.account_status, control.frozen_until_utc
            FROM auth_refresh_sessions AS session
            JOIN auth_identities AS identity ON identity.player_id = session.player_id
            LEFT JOIN auth_player_controls AS control ON control.player_id=identity.player_id
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
        var identity = new AuthIdentity(
            playerId,
            reader.GetString(4),
            reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7));
        var accountStatus = reader.IsDBNull(8) ? "Active" : reader.GetString(8);
        DateTimeOffset? frozenUntil = reader.IsDBNull(9)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(9);
        await reader.DisposeAsync();

        var status = !FixedTimeEquals(storedHash, currentTokenHash)
            ? RefreshRotationStatus.Invalid
            : revokedAt is not null
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

        await using var revoke = new NpgsqlCommand(
            "UPDATE auth_refresh_sessions SET revoked_at_utc=$1 WHERE session_id=$2",
            connection, transaction);
        revoke.Parameters.AddWithValue(now);
        revoke.Parameters.AddWithValue(currentSessionId);
        await revoke.ExecuteNonQueryAsync(cancellationToken);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO auth_refresh_sessions(
                session_id, player_id, token_hash, expires_at_utc, created_at_utc, revoked_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6)
            """, connection, transaction);
        AddSessionParameters(insert, replacement with { PlayerId = playerId });
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RefreshRotationResult(RefreshRotationStatus.Rotated, identity);
    }

    public async Task<bool> RevokeRefreshSessionAsync(
        string sessionId,
        byte[] tokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var select = new NpgsqlCommand(
            "SELECT token_hash, revoked_at_utc FROM auth_refresh_sessions WHERE session_id=$1 FOR UPDATE",
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
        await reader.DisposeAsync();
        if (!matches)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await using var revoke = new NpgsqlCommand(
            "UPDATE auth_refresh_sessions SET revoked_at_utc=$1 WHERE session_id=$2",
            connection, transaction);
        revoke.Parameters.AddWithValue(now);
        revoke.Parameters.AddWithValue(sessionId);
        await revoke.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

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
            "SELECT EXISTS(SELECT 1 FROM auth_identities WHERE player_id=$1)",
            connection,
            transaction))
        {
            exists.Parameters.AddWithValue(playerId);
            playerFound = (bool)(await exists.ExecuteScalarAsync(cancellationToken) ?? false);
        }

        await using var receipt = new NpgsqlCommand(
            """
            INSERT INTO auth_admin_commands(
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
                FROM auth_admin_commands
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
            UPDATE auth_refresh_sessions
            SET revoked_at_utc=$1
            WHERE player_id=$2
              AND created_at_utc <= $1
              AND expires_at_utc > $1
              AND revoked_at_utc IS NULL
            """,
            connection,
            transaction);
        revoke.Parameters.AddWithValue(effectiveAtUtc);
        revoke.Parameters.AddWithValue(playerId);
        var revoked = await revoke.ExecuteNonQueryAsync(cancellationToken);
        await using var updateReceipt = new NpgsqlCommand(
            "UPDATE auth_admin_commands SET affected_count=$1 WHERE command_id=$2",
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
            FROM auth_identities
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
            FROM auth_player_controls
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
            INSERT INTO auth_player_controls(
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
                UPDATE auth_refresh_sessions
                SET revoked_at_utc=$1
                WHERE player_id=$2
                  AND expires_at_utc > $1
                  AND revoked_at_utc IS NULL
                """,
                connection,
                transaction);
            revoke.Parameters.AddWithValue(effectiveAtUtc);
            revoke.Parameters.AddWithValue(playerId);
            revoked = await revoke.ExecuteNonQueryAsync(cancellationToken);
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
            INSERT INTO auth_player_control_events(
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

    public async Task RecordLoginAsync(
        AuthLoginEvent loginEvent, CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            """
            INSERT INTO auth_login_events(
                event_id, player_id, device_id, masked_ip, client_summary, outcome, occurred_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (event_id) DO NOTHING
            """);
        command.Parameters.AddWithValue(Guid.Parse(loginEvent.EventId));
        command.Parameters.AddWithValue(loginEvent.PlayerId);
        command.Parameters.AddWithValue(loginEvent.DeviceId);
        command.Parameters.AddWithValue(loginEvent.MaskedIp);
        command.Parameters.AddWithValue(loginEvent.ClientSummary);
        command.Parameters.AddWithValue(loginEvent.Outcome);
        command.Parameters.AddWithValue(loginEvent.OccurredAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

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
                   (SELECT COUNT(*) FROM auth_refresh_sessions AS session
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
            FROM auth_identities AS identity
            LEFT JOIN auth_player_controls AS control
                ON control.player_id=identity.player_id
            LEFT JOIN LATERAL (
                SELECT occurred_at_utc, device_id, masked_ip
                FROM auth_login_events
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
                   (SELECT COUNT(*) FROM auth_refresh_sessions AS session
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
            FROM auth_identities AS identity
            LEFT JOIN auth_player_controls AS control
                ON control.player_id=identity.player_id
            LEFT JOIN LATERAL (
                SELECT occurred_at_utc, device_id, masked_ip
                FROM auth_login_events
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
            FROM auth_refresh_sessions
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
            FROM auth_login_events
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
            FROM auth_player_control_events
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
        reader.GetFieldValue<DateTimeOffset>(4));

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
        command.Parameters.AddWithValue(session.ExpiresAtUtc);
        command.Parameters.AddWithValue(session.CreatedAtUtc);
        command.Parameters.AddWithValue((object?)session.RevokedAtUtc ?? DBNull.Value);
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
            FROM auth_player_control_events
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

    private static bool FixedTimeEquals(byte[] left, byte[] right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);

    public ValueTask DisposeAsync() => postgres.DisposeAsync();
}
