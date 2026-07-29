using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Security;
using GuiyangMahjong.Admin.Services;
using GuiyangMahjong.Admin.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Tests;

/// <summary>验证多集群注册冲突、地域隔离、案件归属及 Break-glass 的治理不变量。</summary>
public sealed class GovernancePolicyTests
{
    [Fact]
    public void TopologyRegistry_ConflictAndExpiry_AreRegionIsolated()
    {
        var clock = new MutableTimeProvider(
            DateTimeOffset.Parse("2026-07-29T00:00:00Z"));
        var registry = new TopologyRegistry(
            Microsoft.Extensions.Options.Options.Create(new AdminOptions
            {
                TopologyDiscovery = new TopologyDiscoveryOptions
                {
                    Enabled = true,
                    LeaseSeconds = 30
                }
            }),
            clock);
        registry.Register(Registration(
            "b-source",
            "cn-southwest",
            "cluster-a",
            "registration-b"));
        registry.Register(Registration(
            "a-source",
            "cn-southwest",
            "cluster-a",
            "registration-a"));
        registry.Register(Registration(
            "c-source",
            "cn-east",
            "cluster-b",
            "registration-c"));

        var active = registry.ListActive(MonitoringSourceKind.Lobby);
        Assert.Equal(
            ["c-source", "a-source"],
            active.Select(item => item.Registration.SourceId).ToArray());
        Assert.Equal(
            "a-source",
            registry.ListAll().Single(item =>
                item.Registration.SourceId == "b-source").ConflictWith);

        clock.Advance(TimeSpan.FromSeconds(20));
        registry.Register(Registration(
            "c-source",
            "cn-east",
            "cluster-b",
            "registration-c"));
        clock.Advance(TimeSpan.FromSeconds(11));

        active = registry.ListActive(MonitoringSourceKind.Lobby);
        Assert.Single(active);
        Assert.Equal("c-source", active[0].Registration.SourceId);
    }

    [Fact]
    public void Abac_SameRoleWithoutCaseAssignment_IsDenied()
    {
        var (service, context, principal) = CreatePolicyContext(
            assignedCases: new HashSet<string>(StringComparer.Ordinal));
        var investigation = Case("case-1", principal.OperatorId);

        Assert.Throws<AdminOperationException>(
            () => service.RequireCase(context, investigation));
    }

    [Fact]
    public void Abac_BreakGlass_RequiresMfaShortWindowAndReason()
    {
        var (service, context, principal) = CreatePolicyContext(
            assignedCases: new HashSet<string>(StringComparer.Ordinal),
            breakGlassUntilUtc: DateTimeOffset.Parse(
                "2026-07-29T00:10:00Z"));
        context.Request.Headers["X-Break-Glass-Reason"] =
            "Production incident INC-20260729 investigation";

        service.RequireCase(context, Case("case-2", "another-operator"));

        Assert.True(principal.MfaSatisfied);
    }

    [Fact]
    public void Abac_HighValueCompensation_RequiresSeniorApprover()
    {
        var (service, context, principal) = CreatePolicyContext(
            assignedCases: new HashSet<string>(
                ["case-3"],
                StringComparer.Ordinal));
        var action = new AdminActionRecord(
            "action-1",
            AdminManagementActionType.GrantPlayerCompensation,
            "Player",
            "player-1",
            "requester",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            DateTimeOffset.UtcNow.AddHours(1),
            DateTimeOffset.UtcNow,
            "validated compensation reason",
            "ticket-1",
            "trace-1",
            null,
            "hash",
            JsonSerializer.SerializeToElement(new { state = "active" }),
            AdminActionStatus.PendingApproval,
            null,
            1,
            JsonSerializer.SerializeToElement(new
            {
                caseId = "case-3",
                assetCode = "COIN",
                amount = 100_000L
            }));

        Assert.Throws<AdminOperationException>(
            () => service.RequireCompensationApproval(context, action));
        Assert.False(principal.HasRole(
            AdminRoles.SeniorGovernanceApprover));
    }

    [Fact]
    public async Task AuditAnchor_TamperedRecord_IsDetectedBeforeDelivery()
    {
        var store = new InMemoryAdminActionStore();
        await store.AppendAuditAsync(
            AuditDraft("first"),
            CancellationToken.None);
        await store.AppendAuditAsync(
            AuditDraft("second"),
            CancellationToken.None);
        var auditField = typeof(InMemoryAdminActionStore).GetField(
            "audit",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "In-memory audit field was not found.");
        var records = (List<AdminAuditRecord>)auditField.GetValue(store)!;
        records[1] = records[1] with
        {
            Reason = "tampered database value"
        };
        var service = new AuditChainAnchorService(
            store,
            new UnusedHttpClientFactory(),
            Microsoft.Extensions.Options.Options.Create(new AdminOptions
            {
                AuditArchive = new AuditArchiveOptions
                {
                    AnchorEnabled = true,
                    AnchorUrl = "https://worm.example.test/anchor",
                    AppendToken = new string('a', 32)
                }
            }),
            TimeProvider.System,
            NullLogger<AuditChainAnchorService>.Instance);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.VerifyAndAnchorAsync(CancellationToken.None));
    }

    private static MonitoringSourceRegistration Registration(
        string sourceId,
        string regionId,
        string clusterId,
        string registrationId) =>
        new(
            registrationId,
            sourceId,
            MonitoringSourceKind.Lobby,
            regionId,
            clusterId,
            "lobby-1",
            "node-1",
            "https://monitoring.internal",
            1,
            DateTimeOffset.MinValue);

    private static AdminCaseRecord Case(
        string caseId,
        string requestedBy) =>
        new(
            caseId,
            "command-1",
            "action-1",
            AdminCaseType.PlayerSupport,
            "Player",
            "player-1",
            requestedBy,
            "approver",
            DateTimeOffset.UtcNow,
            "validated investigation reason",
            "ticket-1",
            "trace-1",
            JsonSerializer.SerializeToElement(new { state = "active" }),
            "Open");

    private static AdminAuditDraft AuditDraft(string suffix) =>
        new(
            DateTimeOffset.Parse("2026-07-29T00:00:00Z"),
            "operator-1",
            $"Operation-{suffix}",
            "Room",
            "room-1",
            $"validated reason {suffix}",
            null,
            JsonSerializer.SerializeToElement(new { suffix }),
            null,
            $"trace-{suffix}",
            "ticket-1");

    private static (
        AdminAbacPolicyService Service,
        DefaultHttpContext Context,
        AdminPrincipal Principal) CreatePolicyContext(
            IReadOnlySet<string> assignedCases,
            DateTimeOffset? breakGlassUntilUtc = null)
    {
        var clock = new MutableTimeProvider(
            DateTimeOffset.Parse("2026-07-29T00:00:00Z"));
        var service = new AdminAbacPolicyService(
            Microsoft.Extensions.Options.Options.Create(new AdminOptions
            {
                Abac = new AdminAbacOptions
                {
                    Enabled = true,
                    BreakGlassMaximumMinutes = 15,
                    HighValueCompensationThreshold = 100_000
                }
            }),
            clock,
            NullLogger<AdminAbacPolicyService>.Instance);
        var principal = new AdminPrincipal(
            "operator-1",
            new HashSet<string>(
                [AdminRoles.PlayerApprover],
                StringComparer.Ordinal),
            new HashSet<string>(["cn-southwest"], StringComparer.Ordinal),
            assignedCases,
            "shift-a",
            MfaSatisfied: true,
            breakGlassUntilUtc);
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "trace-test"
        };
        AdminPrincipalContext.Set(context, principal);
        return (service, context, principal);
    }

    /// <summary>测试专用可推进时钟，用于确定性验证租约和紧急授权边界。</summary>
    private sealed class MutableTimeProvider(DateTimeOffset utcNow)
        : TimeProvider
    {
        private DateTimeOffset current = utcNow;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }

    /// <summary>断链测试中网络发送不应发生；若发生则立即使测试失败。</summary>
    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException(
                "Tampered audit chains must not be delivered.");
    }
}
