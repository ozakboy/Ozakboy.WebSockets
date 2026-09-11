namespace Ozakboy.WebSockets.Tests;

/// <summary>
/// 背壓測試。這裡刻意<b>不</b>啟動消費者,讓佇列自然填滿 —— 長時間執行的程式真正會遇到的情況,
/// 就是下游一時處理不過來。
/// Backpressure tests deliberately start <b>no</b> consumer so the queue fills up, which is exactly what a
/// long-running process runs into when the downstream falls behind.
/// </summary>
[TestClass]
public sealed class WebSocketClientBackpressureTests
{
    [TestMethod]
    public async Task 佇列滿時_DropOldest丟掉最舊的且可被觀察()
    {
        await using var harness = new ClientHarness(options =>
        {
            options.QueueCapacity = 3;
            options.BackpressureStrategy = BackpressureStrategy.DropOldest;
        });
        await harness.Client.ConnectAsync();

        foreach (var index in Enumerable.Range(1, 4))
        {
            harness.Factory[0].PushText($"m{index}");
        }

        await harness.WaitForMessagesAsync(4);
        await Wait.UntilAsync(() => harness.Drops.Count == 1, "有一則被丟棄");

        var drop = harness.Drops[0];
        Assert.AreEqual("m1", drop.Message.Text, "應該丟掉最舊的那一則");
        Assert.AreEqual(BackpressureStrategy.DropOldest, drop.Strategy);
        Assert.AreEqual(1, drop.TotalDropped);
        Assert.AreEqual(1, harness.Client.Statistics.MessagesDropped);

        // 留在佇列裡的是最新的三則。
        // What remains queued is the three newest.
        harness.StartConsuming();
        await Wait.UntilAsync(() => harness.TextsReceived.Count == 3, "取出剩下的三則");
        var expected = new[] { "m2", "m3", "m4" };
        CollectionAssert.AreEqual(expected, harness.TextsReceived.ToArray());
    }

    [TestMethod]
    public async Task 佇列滿時_DropNewest丟掉剛到的且可被觀察()
    {
        await using var harness = new ClientHarness(options =>
        {
            options.QueueCapacity = 3;
            options.BackpressureStrategy = BackpressureStrategy.DropNewest;
        });
        await harness.Client.ConnectAsync();

        foreach (var index in Enumerable.Range(1, 4))
        {
            harness.Factory[0].PushText($"m{index}");
        }

        await harness.WaitForMessagesAsync(4);
        await Wait.UntilAsync(() => harness.Drops.Count == 1, "有一則被丟棄");

        Assert.AreEqual("m4", harness.Drops[0].Message.Text, "應該丟掉剛到的那一則");
        Assert.AreEqual(BackpressureStrategy.DropNewest, harness.Drops[0].Strategy);

        harness.StartConsuming();
        await Wait.UntilAsync(() => harness.TextsReceived.Count == 3, "取出留下的三則");
        var expected2 = new[] { "m1", "m2", "m3" };
        CollectionAssert.AreEqual(expected2, harness.TextsReceived.ToArray());
    }

    [TestMethod]
    public async Task 佇列滿時_Wait策略一則都不丟()
    {
        await using var harness = new ClientHarness(options =>
        {
            options.QueueCapacity = 2;
            options.BackpressureStrategy = BackpressureStrategy.Wait;
        });
        await harness.Client.ConnectAsync();

        foreach (var index in Enumerable.Range(1, 5))
        {
            harness.Factory[0].PushText($"m{index}");
        }

        // 佇列滿了之後接收迴圈會停住,所以先確認確實卡在那裡、一則都沒被丟掉。
        // The receive loop stalls once the queue is full, so first confirm it is stuck and nothing was dropped.
        await Wait.UntilAsync(() => harness.Client.Statistics.QueuedMessageCount == 2, "佇列已滿");
        Assert.AreEqual(0, harness.Client.Statistics.MessagesDropped);

        harness.StartConsuming();

        await Wait.UntilAsync(() => harness.TextsReceived.Count == 5, "五則全部送達");
        var expected3 = new[] { "m1", "m2", "m3", "m4", "m5" };
        CollectionAssert.AreEqual(expected3, harness.TextsReceived.ToArray());
        Assert.AreEqual(0, harness.Client.Statistics.MessagesDropped);
    }

    [TestMethod]
    public async Task 佇列滿時_中斷通知仍然擠得進去()
    {
        await using var harness = new ClientHarness(options =>
        {
            options.QueueCapacity = 2;
            options.BackpressureStrategy = BackpressureStrategy.DropNewest;
        });
        await harness.Client.ConnectAsync();

        harness.Factory[0].PushText("m1");
        harness.Factory[0].PushText("m2");
        await harness.WaitForMessagesAsync(2);

        harness.Factory[0].PushFault();
        await harness.WaitForConnectionsAsync(2);

        // 就算採用「丟最新」的策略、佇列也已經滿了,缺口通知還是會擠掉一則舊訊息進到串流裡:
        // 沒人知道資料有缺口,比少收幾則更嚴重。
        // Even under DropNewest with a full queue, the gap notice still evicts an older message to get in: nobody
        // knowing there is a gap is worse than losing a few messages.
        harness.StartConsuming();
        await Wait.UntilAsync(
            () => harness.FailuresReceived.Any(error => error.Code == WebSocketErrorCodes.ConnectionLost),
            "缺口通知有進到串流");
    }

    [TestMethod]
    public async Task 統計_佇列長度反映尚未取走的則數()
    {
        await using var harness = new ClientHarness(options => options.QueueCapacity = 8);
        await harness.Client.ConnectAsync();

        harness.Factory[0].PushText("a");
        harness.Factory[0].PushText("b");
        await harness.WaitForMessagesAsync(2);

        Assert.AreEqual(2, harness.Client.Statistics.QueuedMessageCount);
    }
}
