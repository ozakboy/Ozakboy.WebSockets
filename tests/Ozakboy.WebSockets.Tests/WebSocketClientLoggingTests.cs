using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace Ozakboy.WebSockets.Tests;

/// <summary>
/// 事故之後只剩日誌可查,所以重連、丟棄、放棄這三類事件必須留下痕跡。
/// After an incident the log is the only record, so reconnects, drops, and giving up must all leave a trace.
/// </summary>
[TestClass]
public sealed class WebSocketClientLoggingTests
{
    private const int ConnectedEventId = 1000;
    private const int ConnectFailedEventId = 1001;
    private const int ConnectionLostEventId = 1002;
    private const int ReconnectDelayEventId = 1003;
    private const int ReconnectExhaustedEventId = 1004;
    private const int IdleTimeoutEventId = 1005;
    private const int SubscriptionsReplayedEventId = 1006;
    private const int SubscriptionReplayFailedEventId = 1007;
    private const int MessageDroppedEventId = 1008;
    private const int ApplicationPingFailedEventId = 1010;
    private const int EventHandlerFailedEventId = 1011;
    private const int ClosedGracefullyEventId = 1012;
    private const int CloseTimedOutEventId = 1013;
    private const int UnrecoverableEventId = 1014;

    [TestMethod]
    public async Task 連線與優雅關閉_都有留下日誌()
    {
        var logger = new RecordingLogger();
        var time = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FakeWebSocketConnectionFactory(time);
        var options = Options();

        await using (var client = new WebSocketClient(options, factory, logger, time))
        {
            await client.ConnectAsync();
            await client.CloseAsync();
        }

        Assert.IsTrue(logger.HasEvent(ConnectedEventId), "連線成功要有日誌");
        Assert.IsTrue(logger.HasEvent(ClosedGracefullyEventId), "優雅關閉要有日誌");
    }

    [TestMethod]
    public async Task 斷線重連重放與放棄_都有留下日誌()
    {
        var logger = new RecordingLogger();
        var time = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FakeWebSocketConnectionFactory(time);
        var options = Options();
        options.MaxReconnectAttempts = 2;

        factory.Configure = (connection, ordinal) =>
        {
            if (ordinal == 2)
            {
                connection.SendException = new WebSocketException(WebSocketError.Faulted, "重放送不出去");
            }
            else if (ordinal >= 3)
            {
                connection.ConnectException = new WebSocketException(WebSocketError.Faulted, "連不上");
            }
        };

        await using (var client = new WebSocketClient(options, factory, logger, time))
        {
            // 先訂閱再連線,第一條連線就會走到重放這條路徑。
            // Subscribing before connecting makes the very first connection go through the replay path.
            await client.SubscribeAsync(new WebSocketSubscription("btc", "sub-btc"));
            await client.ConnectAsync();
            factory[0].PushFault();

            await Wait.UntilAsync(
                () =>
                {
                    if (time.ArmedTimerCount > 0)
                    {
                        time.Advance(TimeSpan.FromSeconds(1));
                    }

                    return client.State == WebSocketClientState.Closed;
                },
                "重連用盡後關閉");
        }

        Assert.IsTrue(logger.HasEvent(SubscriptionsReplayedEventId), "重放訂閱要有日誌");
        Assert.IsTrue(logger.HasEvent(SubscriptionReplayFailedEventId), "重放失敗要有日誌");
        Assert.IsTrue(logger.HasEvent(ConnectionLostEventId), "斷線要有日誌");
        Assert.IsTrue(logger.HasEvent(ConnectFailedEventId), "連線失敗要有日誌");
        Assert.IsTrue(logger.HasEvent(ReconnectDelayEventId), "退避等待要有日誌");
        Assert.IsTrue(logger.HasEvent(ReconnectExhaustedEventId), "放棄重連一定要有日誌,這是需要告警的事件");

        var exhausted = logger.Entries.Single(entry => entry.EventId.Id == ReconnectExhaustedEventId);
        Assert.AreEqual(LogLevel.Error, exhausted.Level, "資料流停擺必須是 Error 等級");
    }

    /// <summary>
    /// 「放棄」有兩種,日誌必須分得出來:值班的人看到「重連 N 次後放棄」會去查對方的可用性,
    /// 但非暫時性失敗的原因在自己這邊,查對方只會浪費時間。
    /// There are two ways to give up and the log has to tell them apart: "gave up after N reconnects" sends whoever
    /// is on call to check the peer's availability, while a non-transient failure has its cause on this side and
    /// looking at the peer only wastes their time.
    /// </summary>
    [TestMethod]
    public async Task 非暫時性失敗放棄_日誌與重連用盡分得出來()
    {
        var logger = new RecordingLogger();
        var time = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FakeWebSocketConnectionFactory(time);
        var options = Options();

        await using (var client = new WebSocketClient(options, factory, logger, time))
        {
            await client.ConnectAsync();

            // 客戶端已經在跑了,設定物件才被改壞 —— 設定是可變的、由參照持有。
            // The client is already running when the options object is broken; it is mutable and held by reference.
            options.Uri = null;
            factory[0].PushFault();

            await Wait.UntilAsync(() => client.State == WebSocketClientState.Closed, "非暫時性失敗後關閉");
        }

        Assert.IsTrue(logger.HasEvent(UnrecoverableEventId), "放棄一定要有日誌,這是需要告警的事件");
        Assert.IsFalse(
            logger.HasEvent(ReconnectExhaustedEventId),
            "這次不是次數用盡,記成那一種會把查修方向帶偏");

        var entry = logger.Entries.Single(record => record.EventId.Id == UnrecoverableEventId);
        Assert.AreEqual(LogLevel.Error, entry.Level, "資料流停擺必須是 Error 等級");
        StringAssert.Contains(
            entry.Message,
            WebSocketErrorCodes.OptionsInvalid,
            "日誌要寫出是哪個錯誤讓它放棄的");
    }

    [TestMethod]
    public async Task 丟棄訊息與閒置逾時_都有留下日誌()
    {
        var logger = new RecordingLogger();
        var time = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FakeWebSocketConnectionFactory(time);
        var options = Options();
        options.QueueCapacity = 1;
        options.IdleTimeout = TimeSpan.FromSeconds(30);
        options.IdleCheckInterval = TimeSpan.FromSeconds(5);

        await using (var client = new WebSocketClient(options, factory, logger, time))
        {
            await client.ConnectAsync();

            factory[0].PushText("a");
            factory[0].PushText("b");
            await Wait.UntilAsync(() => client.Statistics.MessagesDropped >= 1, "有訊息被丟棄");

            time.Advance(TimeSpan.FromSeconds(31));
            await Wait.UntilAsync(() => factory.CreateCount >= 2, "閒置逾時後重連");
        }

        Assert.IsTrue(logger.HasEvent(MessageDroppedEventId), "丟棄訊息一定要有日誌,不能靜默消失");
        Assert.IsTrue(logger.HasEvent(IdleTimeoutEventId), "閒置逾時要有日誌");
    }

    [TestMethod]
    public async Task 事件處理常式擲出例外與ping失敗_都有留下日誌()
    {
        var logger = new RecordingLogger();
        var time = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FakeWebSocketConnectionFactory(time);
        var options = Options();
        options.ApplicationPingInterval = TimeSpan.FromSeconds(10);
        options.ApplicationPingPayloadFactory = () => "ping";
        factory.Configure = (connection, _) =>
            connection.SendException = new WebSocketException(WebSocketError.Faulted, "送不出去");

        await using (var client = new WebSocketClient(options, factory, logger, time))
        {
            client.StateChanged += (_, _) => throw new InvalidOperationException("處理常式故意爆炸");

            await client.ConnectAsync();
            time.Advance(TimeSpan.FromSeconds(15));
            await Wait.UntilAsync(() => logger.HasEvent(ApplicationPingFailedEventId), "ping 失敗留下日誌");
        }

        Assert.IsTrue(logger.HasEvent(EventHandlerFailedEventId), "事件處理常式的例外要被記錄,不能靜默吞掉");
    }

    [TestMethod]
    public async Task 對方不回覆關閉frame_逾時會留下日誌()
    {
        var logger = new RecordingLogger();
        var time = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FakeWebSocketConnectionFactory(time);
        var options = Options();
        options.CloseTimeout = TimeSpan.FromSeconds(5);
        factory.Configure = (connection, _) => connection.EchoClose = false;

        await using (var client = new WebSocketClient(options, factory, logger, time))
        {
            await client.ConnectAsync();

            var closeTask = client.CloseAsync();
            await Wait.UntilAsync(
                () =>
                {
                    if (time.ArmedTimerCount > 0)
                    {
                        time.Advance(TimeSpan.FromSeconds(1));
                    }

                    return closeTask.IsCompleted;
                },
                "關閉逾時觸發");

            await closeTask;
        }

        Assert.IsTrue(logger.HasEvent(CloseTimedOutEventId));
    }

    [TestMethod]
    public async Task 連線日誌只留主機_不留可能夾帶憑證的路徑()
    {
        // 幣安的使用者資料串流就是 wss://host/ws/<listenKey>,而那把 listenKey 能連上帳戶的私有資料。
        // 這行日誌在 Information 層級、每次重連都寫一次,若連路徑一起寫,日誌就成了憑證的副本。
        const string Credential = "s3cr3tListenKeyValue";

        var logger = new RecordingLogger();
        var time = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FakeWebSocketConnectionFactory(time);
        var options = Options();
        options.Uri = new Uri($"wss://example.invalid/ws/{Credential}?token={Credential}");

        await using (var client = new WebSocketClient(options, factory, logger, time))
        {
            await client.ConnectAsync();
            await client.CloseAsync();
        }

        Assert.IsTrue(logger.HasEvent(ConnectedEventId), "連線成功要有日誌");

        foreach (var entry in logger.Entries)
        {
            Assert.DoesNotContain(
                Credential,
                entry.Message,
                StringComparison.Ordinal,
                $"第 {entry.EventId} 號日誌寫出了 URI 路徑裡的憑證:{entry.Message}");
        }

        var connected = logger.Entries.First(entry => entry.EventId == ConnectedEventId);

        Assert.Contains("example.invalid", connected.Message, StringComparison.Ordinal, "仍要看得出連到哪個主機");
    }

    private static WebSocketClientOptions Options() => new()
    {
        Uri = new Uri("wss://example.invalid/stream"),
        IdleTimeout = TimeSpan.Zero,
        ConnectTimeout = TimeSpan.FromHours(1),
        CloseTimeout = TimeSpan.FromSeconds(5),
        QueueCapacity = 16,
        ReconnectPolicy = new RetryPolicy
        {
            MaxAttempts = int.MaxValue,
            BaseDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(4),
            Strategy = BackoffStrategy.Exponential,
            JitterRatio = 0d,
        },
    };
}
