using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Services;
using GuiyangMahjong.Admin.Storage;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Tests;

/// <summary>
/// 调查闭环安全契约：案件只能单向关闭，回放 URL 必须绑定案件、玩家、操作者和过期时间。
/// </summary>
public sealed class InvestigationClosureTests
{
    [Fact]
    public async Task CaseClosureIsIrreversibleAndRetainsEvidenceHash()
    {
        var store = new InMemoryAdminCaseStore();
        var now = DateTimeOffset.Parse("2026-07-29T00:00:00Z");
        var action = ApprovedAction(now);
        var created = await store.CreateAsync(
            Guid.NewGuid().ToString(),
            AdminCaseType.DisputeInvestigation,
            action,
            now,
            CancellationToken.None);
        var hash = new string('a', 64);

        var closed = await store.CloseAsync(
            created.Case.CaseId,
            "approver-01",
            "证据核验完成，对局结果与权威事件一致。",
            hash,
            now.AddMinutes(5),
            CancellationToken.None);
        var repeated = await store.CloseAsync(
            created.Case.CaseId,
            "other-approver",
            "不得覆盖的第二份结论文本。",
            new string('b', 64),
            now.AddMinutes(6),
            CancellationToken.None);

        Assert.NotNull(closed);
        Assert.Equal("Closed", closed.Status);
        Assert.Equal(hash, closed.EvidencePackageHash);
        Assert.Equal(closed, repeated);
    }

    [Fact]
    public void ReplaySignatureCannotCrossPlayerOperatorOrExpiry()
    {
        var now = DateTimeOffset.Parse("2026-07-29T00:00:00Z");
        var client = new HttpReplayArchiveClient(
            new UnusedHttpClientFactory(),
            Microsoft.Extensions.Options.Options.Create(new AdminOptions
            {
                ReplayArchive = new ReplayArchiveOptions
                {
                    Enabled = true,
                    SigningKey = new string('s', 32),
                    ReadToken = new string('r', 32),
                    AccessTtlSeconds = 60
                }
            }));
        var grant = client.CreateAccess(
            "case-001",
            "player-001",
            Guid.NewGuid().ToString(),
            "operator-001",
            now);
        var uri = new Uri($"http://localhost{grant.AccessUrl}");
        var query = Microsoft.AspNetCore.WebUtilities
            .QueryHelpers.ParseQuery(uri.Query);
        var expiry = long.Parse(query["expires"].ToString());
        var signature = query["signature"].ToString();

        Assert.True(client.ValidateAccess(
            grant.CaseId,
            grant.PlayerId,
            grant.EventId,
            "operator-001",
            expiry,
            signature,
            now));
        Assert.False(client.ValidateAccess(
            grant.CaseId,
            "player-002",
            grant.EventId,
            "operator-001",
            expiry,
            signature,
            now));
        Assert.False(client.ValidateAccess(
            grant.CaseId,
            grant.PlayerId,
            grant.EventId,
            "operator-002",
            expiry,
            signature,
            now));
        Assert.False(client.ValidateAccess(
            grant.CaseId,
            grant.PlayerId,
            grant.EventId,
            "operator-001",
            expiry,
            signature,
            now.AddMinutes(2)));
    }

    private static AdminActionRecord ApprovedAction(DateTimeOffset now) =>
        new(
            Guid.NewGuid().ToString(),
            AdminManagementActionType.StartDisputeInvestigation,
            "Room",
            "room-001",
            "requester-01",
            now,
            now.AddMinutes(5),
            now.AddHours(1),
            now,
            "玩家对结算提出争议，需要核验权威事件与回放。",
            "TICKET-001",
            "trace-investigation-001",
            1,
            "state-hash",
            JsonSerializer.SerializeToElement(new { roomId = "room-001" }),
            AdminActionStatus.ApprovedAwaitingExecution,
            new AdminActionApproval(
                Guid.NewGuid().ToString(),
                "approver-01",
                now,
                ApprovalDecision.Approve,
                "批准发起调查。"),
            3);

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException(
                "The signature test must not access the network.");
    }
}
