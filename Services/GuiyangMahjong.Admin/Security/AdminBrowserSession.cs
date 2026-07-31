using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.Admin.Options;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GuiyangMahjong.Admin.Security;

/// <summary>
/// 管理员浏览器会话的持久化投影。存储层只接收会话、CSRF、设备和网络的 SHA-256 摘要，
/// 不保存企业 Access Token、原始设备标识或完整 IP；记录到期后必须拒绝继续使用。
/// </summary>
public sealed record AdminBrowserSessionRecord(
    string SessionHash,
    string CsrfHash,
    AdminPrincipal Principal,
    string DeviceHash,
    string IpNetworkHash,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc);

/// <summary>
/// 管理员会话与登录安全事件存储边界。生产实现必须支持多副本共享和原子撤销，
/// 测试实现可以使用进程内存，但不得用于生产部署。
/// </summary>
public interface IAdminBrowserSessionStore
{
    /// <summary>初始化或验证 Admin 自有表结构；不允许借此创建其他服务的 Schema。</summary>
    Task InitializeAsync(CancellationToken cancellationToken);
    /// <summary>验证会话存储可读，不创建会话或延长有效期。</summary>
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);
    /// <summary>写入新会话；SessionHash 冲突必须失败，避免覆盖既有管理员身份。</summary>
    Task CreateAsync(AdminBrowserSessionRecord session, CancellationToken cancellationToken);
    /// <summary>按不透明令牌摘要读取会话；不存在时返回 null。</summary>
    Task<AdminBrowserSessionRecord?> GetAsync(string sessionHash, CancellationToken cancellationToken);
    /// <summary>幂等撤销会话；重复注销不得恢复或延长会话。</summary>
    Task RevokeAsync(string sessionHash, DateTimeOffset revokedAtUtc, CancellationToken cancellationToken);
    /// <summary>记录脱敏登录结果和拒绝原因，用于异常设备/IP 调查。</summary>
    Task RecordLoginEventAsync(AdminLoginSecurityEvent loginEvent, CancellationToken cancellationToken);
}

/// <summary>脱敏管理员登录安全事件；原始凭据、设备 ID 和 IP 永远不进入该记录。</summary>
public sealed record AdminLoginSecurityEvent(
    string EventId,
    string OperatorId,
    string Outcome,
    string ReasonCode,
    string DeviceHash,
    string IpNetworkHash,
    DateTimeOffset OccurredAtUtc,
    string TraceId);

/// <summary>单进程开发和测试用会话存储；锁保证创建、读取与撤销具有一致的可见顺序。</summary>
public sealed class InMemoryAdminBrowserSessionStore : IAdminBrowserSessionStore
{
    private readonly Dictionary<string, AdminBrowserSessionRecord> sessions = new(StringComparer.Ordinal);
    private readonly List<AdminLoginSecurityEvent> loginEvents = [];
    private readonly object gate = new();

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc/>
    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    /// <inheritdoc/>
    public Task CreateAsync(AdminBrowserSessionRecord session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!sessions.TryAdd(session.SessionHash, session))
                throw new InvalidOperationException("Administrator session hash already exists.");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<AdminBrowserSessionRecord?> GetAsync(string sessionHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            sessions.TryGetValue(sessionHash, out var session);
            return Task.FromResult(session);
        }
    }

    /// <inheritdoc/>
    public Task RevokeAsync(string sessionHash, DateTimeOffset revokedAtUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (sessions.TryGetValue(sessionHash, out var session) && session.RevokedAtUtc is null)
                sessions[sessionHash] = session with { RevokedAtUtc = revokedAtUtc };
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RecordLoginEventAsync(AdminLoginSecurityEvent loginEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) loginEvents.Add(loginEvent);
        return Task.CompletedTask;
    }
}

/// <summary>
/// PostgreSQL 管理员会话存储。连接使用 Admin 运行身份且所有 SQL 固定限定在 admin_monitor，
/// 从数据库层阻断对房间、结算和资产表的直接写入。
/// </summary>
public sealed class PostgresAdminBrowserSessionStore(NpgsqlDataSource postgres) : IAdminBrowserSessionStore, IAsyncDisposable
{
    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand("SELECT 1 FROM admin_monitor.admin_sessions LIMIT 1");
        _ = await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await InitializeAsync(cancellationToken);
            return true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task CreateAsync(AdminBrowserSessionRecord session, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO admin_monitor.admin_sessions(
                session_hash, csrf_hash, operator_id, roles_json, regions_json, case_ids_json,
                shift_id, mfa_satisfied, break_glass_until_utc, device_hash, ip_network_hash,
                created_at_utc, expires_at_utc, revoked_at_utc)
            VALUES ($1,$2,$3,$4::jsonb,$5::jsonb,$6::jsonb,$7,$8,$9,$10,$11,$12,$13,$14)
            """;
        await using var command = postgres.CreateCommand(sql);
        command.Parameters.AddWithValue(session.SessionHash);
        command.Parameters.AddWithValue(session.CsrfHash);
        command.Parameters.AddWithValue(session.Principal.OperatorId);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(session.Principal.Roles));
        command.Parameters.AddWithValue(JsonSerializer.Serialize(session.Principal.Regions));
        command.Parameters.AddWithValue(JsonSerializer.Serialize(session.Principal.CaseIds));
        command.Parameters.AddWithValue((object?)session.Principal.ShiftId ?? DBNull.Value);
        command.Parameters.AddWithValue(session.Principal.MfaSatisfied);
        command.Parameters.AddWithValue((object?)session.Principal.BreakGlassUntilUtc ?? DBNull.Value);
        command.Parameters.AddWithValue(session.DeviceHash);
        command.Parameters.AddWithValue(session.IpNetworkHash);
        command.Parameters.AddWithValue(session.CreatedAtUtc);
        command.Parameters.AddWithValue(session.ExpiresAtUtc);
        command.Parameters.AddWithValue((object?)session.RevokedAtUtc ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<AdminBrowserSessionRecord?> GetAsync(string sessionHash, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT session_hash, csrf_hash, operator_id, roles_json, regions_json, case_ids_json,
                   shift_id, mfa_satisfied, break_glass_until_utc, device_hash, ip_network_hash,
                   created_at_utc, expires_at_utc, revoked_at_utc
            FROM admin_monitor.admin_sessions WHERE session_hash=$1
            """;
        await using var command = postgres.CreateCommand(sql);
        command.Parameters.AddWithValue(sessionHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var principal = new AdminPrincipal(
            reader.GetString(2),
            DeserializeSet(reader.GetString(3)),
            DeserializeSet(reader.GetString(4)),
            DeserializeSet(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetBoolean(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8));
        return new AdminBrowserSessionRecord(
            reader.GetString(0), reader.GetString(1), principal,
            reader.GetString(9), reader.GetString(10),
            reader.GetFieldValue<DateTimeOffset>(11), reader.GetFieldValue<DateTimeOffset>(12),
            reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13));
    }

    /// <inheritdoc/>
    public async Task RevokeAsync(string sessionHash, DateTimeOffset revokedAtUtc, CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            "UPDATE admin_monitor.admin_sessions SET revoked_at_utc=COALESCE(revoked_at_utc,$2) WHERE session_hash=$1");
        command.Parameters.AddWithValue(sessionHash);
        command.Parameters.AddWithValue(revokedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task RecordLoginEventAsync(AdminLoginSecurityEvent loginEvent, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO admin_monitor.admin_login_security_events(
                event_id, operator_id, outcome, reason_code, device_hash, ip_network_hash,
                occurred_at_utc, trace_id)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8)
            """;
        await using var command = postgres.CreateCommand(sql);
        command.Parameters.AddWithValue(Guid.Parse(loginEvent.EventId));
        command.Parameters.AddWithValue(loginEvent.OperatorId);
        command.Parameters.AddWithValue(loginEvent.Outcome);
        command.Parameters.AddWithValue(loginEvent.ReasonCode);
        command.Parameters.AddWithValue(loginEvent.DeviceHash);
        command.Parameters.AddWithValue(loginEvent.IpNetworkHash);
        command.Parameters.AddWithValue(loginEvent.OccurredAtUtc);
        command.Parameters.AddWithValue(loginEvent.TraceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>将 JSON 字符串数组还原为严格区分大小写的授权集合；无效 JSON 直接导致读取失败。</summary>
    private static IReadOnlySet<string> DeserializeSet(string json) =>
        (JsonSerializer.Deserialize<string[]>(json) ?? [])
            .ToHashSet(StringComparer.Ordinal);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => postgres.DisposeAsync();
}

/// <summary>
/// BFF 会话服务负责生成高熵令牌、执行设备/IP 绑定和 CSRF 固定时间校验。
/// 原始令牌只返回浏览器一次，服务端及日志中始终只使用摘要。
/// </summary>
public sealed class AdminBrowserSessionService(
    IAdminBrowserSessionStore store,
    IOptions<AdminOptions> options,
    TimeProvider timeProvider,
    ILogger<AdminBrowserSessionService> logger)
{
    private readonly AdminWebSecurityOptions security = options.Value.WebSecurity;

    /// <summary>创建会话并返回 Cookie 与 CSRF 原值；调用前必须已完成企业身份、MFA、RBAC/ABAC 校验。</summary>
    public async Task<(string SessionToken, string CsrfToken, AdminBrowserSessionRecord Record)> CreateAsync(
        AdminPrincipal principal, HttpContext context, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var sessionToken = RandomToken();
        var csrfToken = RandomToken();
        var configuredExpiry = now.AddMinutes(security.SessionLifetimeMinutes);
        var credentialExpiry = context.Items.TryGetValue(
                "GuiyangMahjong.AdminCredentialExpiresAtUtc",
                out var expiryValue)
            && expiryValue is DateTimeOffset parsedExpiry
                ? parsedExpiry
                : configuredExpiry;
        // 使用两者较早时间，保证 Cookie 绝不会把上游企业凭证的授权窗口延长。
        var expiresAtUtc = credentialExpiry < configuredExpiry
            ? credentialExpiry
            : configuredExpiry;
        if (expiresAtUtc <= now)
            throw new InvalidOperationException("Administrator credential expires before a browser session can be created.");
        var record = new AdminBrowserSessionRecord(
            Hash(sessionToken), Hash(csrfToken), principal,
            DeviceHash(context), IpNetworkHash(context), now,
            expiresAtUtc, null);
        await store.CreateAsync(record, cancellationToken);
        await store.RecordLoginEventAsync(new AdminLoginSecurityEvent(
            Guid.NewGuid().ToString(), principal.OperatorId, "Succeeded", "SESSION_CREATED",
            record.DeviceHash, record.IpNetworkHash, now, context.TraceIdentifier), cancellationToken);
        return (sessionToken, csrfToken, record);
    }

    /// <summary>验证 Cookie、有效期、设备和网络绑定；失败时写入脱敏安全事件并返回 null。</summary>
    public async Task<AdminBrowserSessionRecord?> ValidateAsync(
        string sessionToken, HttpContext context, CancellationToken cancellationToken)
    {
        var record = await store.GetAsync(Hash(sessionToken), cancellationToken);
        var now = timeProvider.GetUtcNow();
        var reason = record switch
        {
            null => "SESSION_UNKNOWN",
            { RevokedAtUtc: not null } => "SESSION_REVOKED",
            _ when record.ExpiresAtUtc <= now => "SESSION_EXPIRED",
            _ when security.BindDevice && !FixedEquals(record.DeviceHash, DeviceHash(context)) => "DEVICE_CHANGED",
            _ when security.BindIpNetwork && !FixedEquals(record.IpNetworkHash, IpNetworkHash(context)) => "IP_NETWORK_CHANGED",
            _ => string.Empty
        };
        if (reason.Length == 0) return record;
        await store.RecordLoginEventAsync(new AdminLoginSecurityEvent(
            Guid.NewGuid().ToString(), record?.Principal.OperatorId ?? "unknown", "Rejected", reason,
            DeviceHash(context), IpNetworkHash(context), now, context.TraceIdentifier), cancellationToken);
        return null;
    }

    /// <summary>校验 Cookie 会话对应的双提交 CSRF 请求头；缺失或长度不同均直接拒绝。</summary>
    public bool ValidateCsrf(AdminBrowserSessionRecord record, HttpContext context)
    {
        var supplied = context.Request.Headers[security.CsrfHeaderName].ToString();
        return supplied.Length > 0 && FixedEquals(record.CsrfHash, Hash(supplied));
    }

    /// <summary>撤销当前不透明会话；重复调用保持幂等。</summary>
    public Task RevokeAsync(string sessionToken, CancellationToken cancellationToken) =>
        store.RevokeAsync(Hash(sessionToken), timeProvider.GetUtcNow(), cancellationToken);

    /// <summary>
    /// 尽力写入登录或会话拒绝事件；审计存储异常不能把 401/403 变成 500，
    /// 同时结构化日志只记录原因码、TraceId 和操作者，不记录原始设备/IP。
    /// </summary>
    public async Task RecordRejectionAsync(
        HttpContext context,
        string operatorId,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.RecordLoginEventAsync(new AdminLoginSecurityEvent(
                Guid.NewGuid().ToString(),
                string.IsNullOrWhiteSpace(operatorId) ? "unknown" : operatorId,
                "Rejected",
                reasonCode,
                DeviceHash(context),
                IpNetworkHash(context),
                timeProvider.GetUtcNow(),
                context.TraceIdentifier), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "管理员登录安全事件写入失败 ReasonCode={ReasonCode} TraceId={TraceId}",
                reasonCode,
                context.TraceIdentifier);
        }
    }

    /// <summary>读取不含原始设备信息的稳定设备摘要；缺失设备头时使用明确哨兵值。</summary>
    private static string DeviceHash(HttpContext context) =>
        Hash(context.Request.Headers["X-Admin-Device-Id"].ToString().Trim() is { Length: > 0 } value
            ? value : "missing-device");

    /// <summary>仅绑定 IPv4 /24 或 IPv6 /56 网络前缀，兼顾漫游容忍与异常来源检测。</summary>
    private static string IpNetworkHash(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null) return Hash("unknown-network");
        var bytes = address.MapToIPv6().GetAddressBytes();
        var prefixLength = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 15 : 7;
        return Hash(Convert.ToHexString(bytes.AsSpan(0, prefixLength)));
    }

    private static string RandomToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool FixedEquals(string left, string right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
}

/// <summary>应用启动时验证会话表可访问；生产迁移由独立 migration 身份预先执行。</summary>
public sealed class AdminBrowserSessionStoreInitializer(
    IAdminBrowserSessionStore store,
    IOptions<AdminOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        options.Value.WebSecurity.BrowserSessionEnabled
            ? store.InitializeAsync(cancellationToken)
            : Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
