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

        var terminal = harness.FailuresReceived.Single(error => error.Code == WebSocketErrorCodes.ReconnectExhausted);

        // 這條是本套件對「終局失敗」的核心承諾:呼叫端只要看 IsTransient 就知道不必再重試,
        // 不需要、也不應該去比對錯誤代碼。分類一旦被改回 Unavailable(暫時性),這裡就會紅燈。
        // This is the package's core promise about terminal failures: IsTransient alone tells the caller not to
        // retry, with no need — and no excuse — to branch on the error code. Putting the category back to the
        // transient Unavailable turns this test red.
        Assert.AreEqual(ErrorCategory.Exhausted, terminal.Category);
        Assert.IsFalse(
            terminal.IsTransient,
            "重連用盡是終局失敗:重試不可能成功,必須換一個新的客戶端,IsTransient 一定要是 false");

        // 放棄前試了幾次要能用程式讀回來,不必從訊息字串 parse。
        // How many attempts were made must be readable programmatically rather than parsed out of the message.
        Assert.IsTrue(terminal.TryGetInt64("attempts", out var attempts), "錯誤要帶上 attempts 資料");
        Assert.AreEqual(1L, attempts, "MaxReconnectAttempts 設為 1,放棄前就是試了 1 次");

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

    /// <summary>
    /// 對一個要連續跑好幾週的元件來說,最糟的失敗形態不是崩潰,是無限空轉:不會停止、日誌一直在動,
    /// 看起來像在工作,實際上永遠不會恢復。設定物件是可變的、由參照持有,重連迴圈每次都重跑
    /// <c>Validate()</c>,所以客戶端跑起來之後被改壞的設定會讓每一次重連都收到同一個非暫時性錯誤。
    /// 而 <see cref="WebSocketClientOptions.MaxReconnectAttempts"/> 預設無限,正是 24 小時執行的建議設定,
    /// 也就是說預設設定就會踩到。
    /// The worst failure for something meant to run for weeks is not a crash but an endless spin: it never stops, the
    /// log keeps moving, it looks like work, and it never recovers. The options object is mutable and held by
    /// reference, and the reconnect loop re-runs <c>Validate()</c> on every attempt, so a configuration broken after
    /// start-up yields the same non-transient error forever — and with
    /// <see cref="WebSocketClientOptions.MaxReconnectAttempts"/> defaulting to unlimited, which is the recommended
    /// setting for a round-the-clock process, the default configuration is the one that walks into it.
    /// </summary>
    [TestMethod]
    public async Task 執行期設定被改壞_終局結束而不是無限空轉()
    {
        // 刻意不動 MaxReconnectAttempts:用的就是會踩到這個 bug 的預設值。
        // MaxReconnectAttempts is deliberately left alone: this is the default that walks into the bug.
        await using var harness = new ClientHarness();
        harness.StartConsuming();
        await harness.Client.ConnectAsync();

        Assert.IsNull(harness.Options.MaxReconnectAttempts, "這條測試的前提是預設的無限重連");

        // 客戶端已經在跑了,才把設定改壞。
        // The client is already running when the configuration is broken.
        harness.Options.Uri = null;

        harness.Factory[0].PushFault();

        // 不推進假時鐘:如果客戶端真的去退避重試了,這裡就會因為永遠等不到 Closed 而逾時失敗,
        // 那正是「無限空轉」在測試裡的樣子。
        // The fake clock is never advanced: if the client did back off and retry, this would time out waiting for
        // Closed, which is exactly what the endless spin looks like from a test.
        await harness.WaitForStateAsync(WebSocketClientState.Closed);

        Assert.AreEqual(WebSocketCloseReason.UnrecoverableError, harness.Client.CloseReason);
        Assert.AreEqual(
            1,
            harness.Factory.CreateCount,
            "非暫時性失敗一次都不該重試,所以不會有第二條連線");

        await Wait.UntilAsync(
            () => harness.FailuresReceived.Any(error => error.Code == WebSocketErrorCodes.Unrecoverable),
            "串流中出現不可恢復的終局失敗");

        var terminal = harness.FailuresReceived.Single(error => error.Code == WebSocketErrorCodes.Unrecoverable);

        Assert.AreEqual(ErrorCategory.Exhausted, terminal.Category);
        Assert.IsFalse(
            terminal.IsTransient,
            "終局失敗一定要是非暫時性,否則呼叫端會照著 IsTransient 對一個已經死掉的客戶端永遠重試");

        // 事後只知道「客戶端放棄了」沒有用,必須看得出是被什麼打敗的。
        // Knowing only that the client gave up is useless; the error has to say what defeated it.
        Assert.IsTrue(
            terminal.TryGetData(WebSocketErrorDataKeys.InnerCode, out var innerCode),
            "終局錯誤要帶上害它放棄的錯誤代碼");
        Assert.AreEqual(WebSocketErrorCodes.OptionsInvalid, innerCode);
        Assert.IsTrue(terminal.TryGetData(WebSocketErrorDataKeys.InnerCategory, out var innerCategory));
        Assert.AreEqual(nameof(ErrorCategory.Validation), innerCategory);
        Assert.IsTrue(terminal.TryGetInt64(WebSocketErrorDataKeys.Attempts, out var attempts));
        Assert.AreEqual(1L, attempts, "失敗當下就放棄,只有那一次嘗試");

        // 狀態變更事件也要帶著同一個理由,否則只訂事件的監控看不出這次是哪一種結局。
        // The state-change event carries the same reason, or monitoring that only subscribes to the event cannot
        // tell which ending this was.
        var closedChange = harness.StateChanges.Single(change => change.CurrentState == WebSocketClientState.Closed);
        Assert.AreEqual(WebSocketCloseReason.UnrecoverableError, closedChange.CloseReason);
        Assert.AreEqual(WebSocketErrorCodes.Unrecoverable, closedChange.Error?.Code);

        // 終局之後串流必須結束,否則呼叫端會永遠停在 await foreach 上 —— 那又是另一種安靜的空轉。
        // The stream must end afterwards, or the caller waits on await foreach forever, which is another silent
        // spin.
        await Wait.UntilAsync(() => harness.ConsumerCompleted, "訊息串流已結束");
    }

    /// <summary>
    /// 上面那條修正不可以讓正常的重連變保守。網路斷、逾時、對方不可用全都是暫時性失敗,
    /// 連續失敗再多次也要一路重試下去 —— 那是本套件存在的理由。
    /// The fix above must not make ordinary reconnects conservative. Dropped networks, timeouts and an unavailable
    /// peer are all transient, and however many times they repeat the client keeps retrying; that is the whole point
    /// of this package.
    /// </summary>
    [TestMethod]
    public async Task 暫時性失敗_連續失敗多次仍然一路重連不會被判終局()
    {
        await using var harness = new ClientHarness(options => options.MaxReconnectAttempts = null);
        harness.StartConsuming();
        harness.Factory.Configure = (connection, ordinal) =>
        {
            if (ordinal is >= 2 and <= 9)
            {
                connection.ConnectException = new WebSocketException(WebSocketError.Faulted, "對方維護中");
            }
        };

        await harness.Client.ConnectAsync();
        harness.Factory[0].PushFault();

        await harness.AdvanceUntilAsync(() => harness.Factory.CreateCount >= 10, "第十次連線嘗試");
        await harness.WaitForStateAsync(WebSocketClientState.Connected);

        Assert.AreNotEqual(WebSocketClientState.Closed, harness.Client.State);
        Assert.AreEqual(WebSocketCloseReason.None, harness.Client.CloseReason, "沒有任何結局發生過");
        Assert.IsFalse(
            harness.FailuresReceived.Any(error => error.Code == WebSocketErrorCodes.Unrecoverable),
            "暫時性失敗不可以被當成不可恢復");
        Assert.IsTrue(
            harness.FailuresReceived.All(error => error.IsTransient),
            "串流中出現的每一個失敗都應該是暫時性的缺口通知");
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
