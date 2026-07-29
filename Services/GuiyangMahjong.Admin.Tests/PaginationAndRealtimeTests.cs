using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Services;

namespace GuiyangMahjong.Admin.Tests;

/// <summary>验证工作流 F 的游标稳定性、容量上限和 SSE 断点窗口。</summary>
public sealed class PaginationAndRealtimeTests
{
    /// <summary>验证页大小硬上限，以及插入较新记录后继续翻页不会产生重复。</summary>
    [Fact]
    public void CursorPaginationCapsPageAndDoesNotRepeatWhenNewerRowsArrive()
    {
        var baseline = Enumerable.Range(0, 100_000)
            .Select(index => new CapacityItem(
                $"player-{index:D6}",
                DateTimeOffset.Parse("2026-07-29T00:00:00Z")
                    .AddSeconds(-index)))
            .ToArray();

        var first = MonitoringCursorPagination.CreatePage(
            baseline,
            10_000,
            200,
            "players|",
            item => item.CreatedAtUtc,
            item => item.Id,
            null);
        Assert.Equal(200, first.Items.Length);
        Assert.True(first.HasMore);

        // 第一页完成后插入更新的记录；键集边界保证第二页不会重复返回第一页记录。
        var changed = baseline.Append(new CapacityItem(
            "player-new",
            DateTimeOffset.Parse("2026-07-29T01:00:00Z")));
        var second = MonitoringCursorPagination.CreatePage(
            changed,
            200,
            200,
            "players|",
            item => item.CreatedAtUtc,
            item => item.Id,
            first.NextCursor);
        Assert.Empty(first.Items.Select(item => item.Id)
            .Intersect(second.Items.Select(item => item.Id)));
        Assert.DoesNotContain(second.Items, item => item.Id == "player-new");
    }

    /// <summary>验证游标绑定筛选条件，防止跨查询复用后造成越权或错页。</summary>
    [Fact]
    public void CursorCannotBeReusedAcrossFilters()
    {
        var items = new[]
        {
            new CapacityItem(
                "room-1",
                DateTimeOffset.Parse("2026-07-29T00:00:00Z")),
            new CapacityItem(
                "room-2",
                DateTimeOffset.Parse("2026-07-28T00:00:00Z"))
        };
        var page = MonitoringCursorPagination.CreatePage(
            items,
            1,
            200,
            "rooms|playing",
            item => item.CreatedAtUtc,
            item => item.Id,
            null);

        Assert.Throws<InvalidMonitoringCursorException>(() =>
            MonitoringCursorPagination.CreatePage(
                items,
                1,
                200,
                "rooms|waiting",
                item => item.CreatedAtUtc,
                item => item.Id,
                page.NextCursor));
    }

    /// <summary>验证 SSE 可回放窗口以及超出积压窗口后的强制重同步。</summary>
    [Fact]
    public void RealtimeHubReplaysRecentEventsAndRequiresResyncOutsideWindow()
    {
        using var hub = new AdminRealtimeEventHub(
            Microsoft.Extensions.Options.Options.Create(new AdminOptions
            {
                RealtimeCapacity = new RealtimeCapacityOptions
                {
                    EventBacklogLimit = 1000,
                    SubscriberQueueLimit = 16
                }
            }));
        for (var index = 1; index <= 1005; index++)
        {
            hub.Publish(
                "room.upsert",
                $"room-{index}",
                new { index },
                DateTimeOffset.UtcNow);
        }

        var expired = hub.Subscribe(0);
        Assert.True(expired.RequiresResync);
        Assert.Empty(expired.Backlog);
        expired.Dispose();

        var resumable = hub.Subscribe(1000);
        Assert.False(resumable.RequiresResync);
        Assert.Equal(
            new long[] { 1001, 1002, 1003, 1004, 1005 },
            resumable.Backlog.Select(item => item.Sequence));
        resumable.Dispose();
    }

    /// <summary>容量测试使用的不可变排序投影；不包含玩家敏感信息。</summary>
    private sealed record CapacityItem(string Id, DateTimeOffset CreatedAtUtc);
}
