using System.Net.WebSockets;

namespace Ozakboy.WebSockets.Tests;

[TestClass]
public sealed class WebSocketClientReconnectTests
{
    [TestMethod]
    public async Task 連線中斷_會自動重連()
    {
        await using var harness = new ClientHarness();
        harness.StartConsuming();
        await harness.Client.ConnectAsync();

        harness.Factory[0].PushFault();

        await harness.WaitForConnectionsAsync(2);
        await harness.WaitForStateAsync(WebSocketClientState.Connected);

        Assert.AreEqual(WebSocketState.Open, harness.Factory[1].State);
        Assert.AreEqual(1, harness.Client.Statistics.ReconnectCount);
    }

    /// <summary>
    /// 重連後訂閱必須被重放。少了這一步,重連會成功、狀態會顯示已連線、不會有任何錯誤,
    /// 但資料永遠不會再進來 —— 這是本套件最重要的一條測試。
    /// Subscriptions must be replayed after a reconnect. Without it the reconnect succeeds, the status reads
    /// connected, no error is raised, and data never arrives again. This is the single most important test here.
    /// </summary>
    [TestMethod]
    public async Task 重連後_訂閱有被重放()
    {
        await using var harness = new ClientHarness();
        harness.StartConsuming();
        await harness.Client.ConnectAsync();

        await harness.Client.SubscribeAsync(new WebSocketSubscription("btc", """{"op":"subscribe","ch":"btcusdt@trade"}"""));
        await harness.Client.SubscribeAsync(new WebSocketSubscription("eth", """{"op":"subscribe","ch":"ethusdt@trade"}"""));

        // 第一條連線只收到當下送出的兩則訂閱。
        // The first connection only ever saw the two subscribes sent at the time.
        var expected = new[] { """{"op":"subscribe","ch":"btcusdt@trade"}""", """{"op":"subscribe","ch":"ethusdt@trade"}""" };
        CollectionAssert.AreEqual(expected, harness.Factory[0].SentTexts.ToArray());

        harness.Factory[0].PushFault();

        await harness.WaitForConnectionsAsync(2);
        await harness.WaitForStateAsync(WebSocketClientState.Connected);

        // 重點:新連線在沒有任何人重新呼叫 SubscribeAsync 的情況下,自己收到了同樣兩則訂閱,順序也一致。
        // The point: the new connection received the same two subscribes, in the same order, without anyone calling
        // SubscribeAsync again.
        var expected2 = new[] { """{"op":"subscribe","ch":"btcusdt@trade"}""", """{"op":"subscribe","ch":"ethusdt@trade"}""" };
        CollectionAssert.AreEqual(expected2, harness.Factory[1].SentTexts.ToArray());

        var statistics = harness.Client.Statistics;
        Assert.AreEqual(1, statistics.ReconnectCount);
        Assert.AreEqual(1, statistics.SubscriptionReplayCount, "第一次連線時還沒有訂閱可重放,只有重連那次算數");
        Assert.AreEqual(2, statistics.SubscriptionCount);
    }

    [TestMethod]
    public async Task 重連前登記的訂閱_在連上後才送出()
    {
        await using var harness = new ClientHarness();

        // 還沒連線就先訂閱:登記成功,但當下沒有連線可送。
        // Subscribing before connecting registers the entry; there is no connection to send on yet.
        var subscribeResult = await harness.Client.SubscribeAsync(new WebSocketSubscription("btc", "sub-btc"));

        Assert.IsTrue(subscribeResult.IsSuccess, subscribeResult.ToString());
        Assert.AreEqual(0, harness.Factory.CreateCount);

        await harness.Client.ConnectAsync();

        var expected3 = new[] { "sub-btc" };
        CollectionAssert.AreEqual(expected3, harness.Factory[0].SentTexts.ToArray());
    }

    [TestMethod]
    public async Task 重放訂閱失敗_整條連線作廢並重連()
    {
        await using var harness = new ClientHarness();
        harness.StartConsuming();
        harness.Factory.Configure = (connection, ordinal) =>
        {
            // 第二條連線握手成功,但重放訂閱時傳送失敗 —— 「連上了卻只訂閱到一半」比沒連上更難發現,
            // 所以這條連線必須整個作廢重來。
            // The second connection completes its handshake but fails while replaying. "Connected with half the
            // subscriptions" is harder to spot than "not connected", so the connection is thrown away entirely.
            if (ordinal == 2)
            {
                connection.SendException = new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "送不出去");
            }
        };

        await harness.Client.ConnectAsync();
        await harness.Client.SubscribeAsync(new WebSocketSubscription("btc", "sub-btc"));

        harness.Factory[0].PushFault();

        // 第二條連線因重放失敗而作廢,之後的第三次嘗試要先等退避間隔。
        // The second connection is discarded because the replay failed, so the third attempt waits out the backoff.
        await harness.AdvanceUntilAsync(() => harness.Factory.CreateCount >= 3, "第三次連線嘗試");
        await harness.WaitForStateAsync(WebSocketClientState.Connected);

        Assert.IsTrue(harness.Factory[1].IsDisposed, "重放失敗的連線必須被釋放");
        var expected4 = new[] { "sub-btc" };
        CollectionAssert.AreEqual(expected4, harness.Factory[2].SentTexts.ToArray());
    }

    [TestMethod]
    public async Task 重連退避_間隔由RetryPolicy決定()
    {
        await using var harness = new ClientHarness();
        harness.StartConsuming();
        harness.Factory.Configure = (connection, ordinal) =>
        {
            if (ordinal is 2 or 3)
            {
                connection.ConnectException = new WebSocketException(WebSocketError.Faulted, "還沒恢復");
            }
        };

        await harness.Client.ConnectAsync();
        harness.Factory[0].PushFault();

        await harness.AdvanceUntilAsync(
            () => harness.Factory.CreateCount >= 4,
            "四次連線嘗試",
            TimeSpan.FromMilliseconds(100));
        await harness.WaitForStateAsync(WebSocketClientState.Connected);

        // 用假時鐘上的建立時間相減來驗證間隔,不必猜計時器排好了沒。
        // Intervals are verified by subtracting creation times on the fake clock, with no guessing about timers.
        var createdAt = harness.Factory.CreatedAt;

        var firstReconnect = createdAt[1] - createdAt[0];
        var secondGap = createdAt[2] - createdAt[1];
        var thirdGap = createdAt[3] - createdAt[2];

        Assert.IsTrue(firstReconnect < TimeSpan.FromMilliseconds(500), $"斷線後第一次重連應該立即發生,實際 {firstReconnect}");
        Assert.IsTrue(secondGap >= TimeSpan.FromSeconds(1), $"第二次應等 BaseDelay × 2^0 = 1 秒,實際 {secondGap}");
        Assert.IsTrue(secondGap < TimeSpan.FromSeconds(2), $"第二次不應等超過 2 秒,實際 {secondGap}");
        Assert.IsTrue(thirdGap >= TimeSpan.FromSeconds(2), $"第三次應等 BaseDelay × 2^1 = 2 秒,實際 {thirdGap}");
        Assert.IsTrue(thirdGap < TimeSpan.FromSeconds(3), $"第三次不應等超過 3 秒,實際 {thirdGap}");
    }

    [TestMethod]
    public async Task 斷線_會以暫時性失敗出現在訊息串流()
    {
        await using var harness = new ClientHarness();
        harness.StartConsuming();
        await harness.Client.ConnectAsync();

        harness.Factory[0].PushFault();
        await harness.WaitForConnectionsAsync(2);
        await Wait.UntilAsync(() => harness.FailuresReceived.Count > 0, "串流中出現失敗通知");

        var failure = harness.FailuresReceived[0];
        Assert.AreEqual(WebSocketErrorCodes.ConnectionLost, failure.Code);
        Assert.IsTrue(failure.IsTransient, "斷線是暫時性失敗,串流要繼續");
    }

    [TestMethod]
    public async Task 對方主動關閉_也會重連()
    {
        await using var harness = new ClientHarness();
        harness.StartConsuming();
        await harness.Client.ConnectAsync();

        harness.Factory[0].PushClose();

        await harness.WaitForConnectionsAsync(2);
        await harness.WaitForStateAsync(WebSocketClientState.Connected);
    }

    [TestMethod]
    public async Task 重連次數用盡_關閉並在串流中回報非暫時性失敗()
    {
        await using var harness = new ClientHarness(options => options.MaxReconnectAttempts = 1);
        harness.StartConsuming();
        harness.Factory.Configure = (connection, ordinal) =>
        {
            if (ordinal >= 2)
            {
                connection.ConnectException = new WebSocketException(WebSocketError.Faulted, "對方沒回來");
            }
        };

        await harness.Client.ConnectAsync();
        harness.Factory[0].PushFault();

        await harness.WaitForStateAsync(WebSocketClientState.Closed);

        Assert.AreEqual(WebSocketCloseReason.ReconnectAttemptsExhausted, harness.Client.CloseReason);
        Assert.AreEqual(2, harness.Factory.CreateCount, "只允許一次重連嘗試");

        await Wait.UntilAsync(
            () => harness.FailuresReceived.Any(error => error.Code == WebSocketErrorCodes.ReconnectExhausted),
            "串流中出現重連用盡的失敗");

        // 終局與否看的是代碼與串流是否結束,不是 IsTransient —— 現有的錯誤分類沒有一個能表達
        // 「這個客戶端已經結束了」,詳見 IWebSocketClient.Messages 的說明。
        // Terminality is judged by the code and by the stream ending, not by IsTransient: no existing error category
        // expresses "this client is finished". See the remarks on IWebSocketClient.Messages.
        var terminal = harness.FailuresReceived.Single(error => error.Code == WebSocketErrorCodes.ReconnectExhausted);
        Assert.AreEqual(ErrorCategory.Unavailable, terminal.Category);

        // 終局失敗之後串流必須結束,否則呼叫端會永遠停在 await foreach 上。
        // The stream must end after a terminal failure, or the caller waits on await foreach forever.
        await Wait.UntilAsync(() => harness.ConsumerCompleted, "訊息串流已結束");
    }

    [TestMethod]
    public async Task 不重連設定_斷線後立刻關閉()
    {
        await using var harness = new ClientHarness(options => options.MaxReconnectAttempts = 0);
        harness.StartConsuming();

        await harness.Client.ConnectAsync();
        harness.Factory[0].PushFault();

        await harness.WaitForStateAsync(WebSocketClientState.Closed);

        Assert.AreEqual(1, harness.Factory.CreateCount);
    }

    [TestMethod]
    public async Task 對方主動關閉且不重連_關閉理由為遠端關閉()
    {
        await using var harness = new ClientHarness(options => options.MaxReconnectAttempts = 0);
        harness.StartConsuming();

        await harness.Client.ConnectAsync();
        harness.Factory[0].PushClose();

        await harness.WaitForStateAsync(WebSocketClientState.Closed);

        Assert.AreEqual(WebSocketCloseReason.RemoteClosed, harness.Client.CloseReason);
    }

    [TestMethod]
    public async Task 無限重連_失敗多次也不會放棄()
    {
        await using var harness = new ClientHarness(options => options.MaxReconnectAttempts = null);
        harness.StartConsuming();
        harness.Factory.Configure = (connection, ordinal) =>
        {
            if (ordinal is >= 2 and <= 5)
            {
                connection.ConnectException = new WebSocketException(WebSocketError.Faulted, "長時間維護中");
            }
        };

        await harness.Client.ConnectAsync();
        harness.Factory[0].PushFault();

        await harness.AdvanceUntilAsync(() => harness.Factory.CreateCount >= 6, "第六次連線嘗試");
        await harness.WaitForStateAsync(WebSocketClientState.Connected);
        Assert.AreNotEqual(WebSocketClientState.Closed, harness.Client.State);
    }
}
