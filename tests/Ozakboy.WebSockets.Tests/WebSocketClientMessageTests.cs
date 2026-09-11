using System.Text;

namespace Ozakboy.WebSockets.Tests;

[TestClass]
public sealed class WebSocketClientMessageTests
{
    [TestMethod]
    public async Task 文字訊息_原樣出現在串流()
    {
        await using var harness = new ClientHarness();
        harness.StartConsuming();
        await harness.Client.ConnectAsync();

        harness.Factory[0].PushText("""{"e":"trade","p":"64000.50"}""");

        await Wait.UntilAsync(() => harness.TextsReceived.Count == 1, "收到一則文字訊息");

        Assert.AreEqual("""{"e":"trade","p":"64000.50"}""", harness.TextsReceived[0]);
        Assert.AreEqual(WebSocketMessageKind.Text, harness.Received[0].GetValueOrThrow().Kind);
    }

    [TestMethod]
    public async Task 分段訊息_會被組裝成一則()
    {
        await using var harness = new ClientHarness();
        harness.StartConsuming();
        await harness.Client.ConnectAsync();

        harness.Factory[0].PushTextFragment("""{"e":"tra""", endOfMessage: false);
        harness.Factory[0].PushTextFragment("""de"}""", endOfMessage: true);

        await Wait.UntilAsync(() => harness.TextsReceived.Count == 1, "收到組裝後的訊息");

        Assert.AreEqual("""{"e":"trade"}""", harness.TextsReceived[0]);
        Assert.AreEqual(1, harness.Client.Statistics.MessagesReceived);
    }

    [TestMethod]
    public async Task 二進位訊息_內容原樣交出()
    {
        await using var harness = new ClientHarness();
        harness.StartConsuming();
        await harness.Client.ConnectAsync();

        harness.Factory[0].PushBinary([1, 2, 3, 250]);

        await Wait.UntilAsync(() => harness.Received.Count == 1, "收到二進位訊息");

        var message = harness.Received[0].GetValueOrThrow();
        Assert.AreEqual(WebSocketMessageKind.Binary, message.Kind);
        var expected = new byte[] { 1, 2, 3, 250 };
        CollectionAssert.AreEqual(expected, message.Binary.ToArray());
        Assert.IsNull(message.Text);
        Assert.AreEqual(4, message.ByteCount);
    }

    [TestMethod]
    public async Task 訊息時間戳_取自客戶端的時間來源()
    {
        await using var harness = new ClientHarness();
        harness.StartConsuming();
        await harness.Client.ConnectAsync();

        harness.Time.Advance(TimeSpan.FromMinutes(3));
        harness.Factory[0].PushText("hello");

        await Wait.UntilAsync(() => harness.Received.Count == 1, "收到訊息");

        Assert.AreEqual(harness.Time.GetUtcNow(), harness.Received[0].GetValueOrThrow().ReceivedAt);
    }

    [TestMethod]
    public async Task 訊息超過大小上限_連線作廢並重連()
    {
        await using var harness = new ClientHarness(options =>
        {
            options.ReceiveBufferSize = 1024;
            options.MaxMessageSize = 1024;
        });
        harness.StartConsuming();
        await harness.Client.ConnectAsync();

        harness.Factory[0].PushTextFragment(new string('a', 700), endOfMessage: false);
        harness.Factory[0].PushTextFragment(new string('b', 700), endOfMessage: false);

        await Wait.UntilAsync(
            () => harness.FailuresReceived.Any(error => error.Code == WebSocketErrorCodes.MessageTooLarge),
            "串流中出現訊息過大的失敗");

        await harness.WaitForConnectionsAsync(2);
    }

    [TestMethod]
    public async Task 統計_反映收到的則數與位元組數()
    {
        await using var harness = new ClientHarness();
        harness.StartConsuming();
        await harness.Client.ConnectAsync();

        harness.Factory[0].PushText("abcd");
        harness.Factory[0].PushText("efghij");

        await harness.WaitForMessagesAsync(2);

        var statistics = harness.Client.Statistics;
        Assert.AreEqual(2, statistics.MessagesReceived);
        Assert.AreEqual(10, statistics.BytesReceived);
        Assert.AreEqual(0, statistics.MessagesDropped);
        Assert.IsNotNull(statistics.LastMessageAt);
    }

    [TestMethod]
    public async Task 送出文字_會抵達連線()
    {
        await using var harness = new ClientHarness();
        await harness.Client.ConnectAsync();

        var result = await harness.Client.SendAsync("ping-me");

        Assert.IsTrue(result.IsSuccess, result.ToString());
        var expected = new[] { "ping-me" };
        CollectionAssert.AreEqual(expected, harness.Factory[0].SentTexts.ToArray());
        Assert.AreEqual(1, harness.Client.Statistics.MessagesSent);
    }

    [TestMethod]
    public async Task 送出二進位_會抵達連線()
    {
        await using var harness = new ClientHarness();
        await harness.Client.ConnectAsync();

        var result = await harness.Client.SendAsync(new byte[] { 9, 8, 7 });

        Assert.IsTrue(result.IsSuccess, result.ToString());
        var expected2 = new byte[] { 9, 8, 7 };
        CollectionAssert.AreEqual(expected2, harness.Factory[0].SentBinaries[0]);
    }

    [TestMethod]
    public async Task 未連線時送出_回傳未連線失敗()
    {
        await using var harness = new ClientHarness();

        var result = await harness.Client.SendAsync("anything");

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(WebSocketErrorCodes.NotConnected, result.Error!.Code);
        Assert.IsTrue(result.Error.IsTransient);
    }

    [TestMethod]
    public async Task 傳送擲出例外_轉成失敗結果而不是例外()
    {
        await using var harness = new ClientHarness();
        harness.Factory.Configure = (connection, _) =>
            connection.SendException = new System.Net.WebSockets.WebSocketException("送不出去");

        await harness.Client.ConnectAsync();
        var result = await harness.Client.SendAsync("anything");

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(WebSocketErrorCodes.SendFailed, result.Error!.Code);
        Assert.IsNotNull(result.Error.Exception);
    }

    [TestMethod]
    public async Task 送出時取消_回傳取消失敗()
    {
        await using var harness = new ClientHarness();
        await harness.Client.ConnectAsync();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await harness.Client.SendAsync("anything", cts.Token);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(WebSocketErrorCodes.Cancelled, result.Error!.Code);
        Assert.AreEqual(ErrorCategory.Cancelled, result.Error.Category);
    }

    [TestMethod]
    public async Task 串流被取消_安靜結束而不是擲出例外()
    {
        await using var harness = new ClientHarness();
        await harness.Client.ConnectAsync();

        using var cts = new CancellationTokenSource();
        var enumeration = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var _ in harness.Client.Messages(cts.Token))
            {
                count++;
            }

            return count;
        });

        await cts.CancelAsync();

        var received = await enumeration;
        Assert.AreEqual(0, received);
    }

    [TestMethod]
    public async Task 送出null文字_擲出ArgumentNullException()
    {
        await using var harness = new ClientHarness();

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => harness.Client.SendAsync((string)null!));
    }

    [TestMethod]
    public async Task 訂閱null_擲出ArgumentNullException()
    {
        await using var harness = new ClientHarness();

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => harness.Client.SubscribeAsync(null!));
    }

    [TestMethod]
    public void WebSocketMessage_文字_計算UTF8位元組數()
    {
        var now = DateTimeOffset.UnixEpoch;

        var message = WebSocketMessage.FromText("中文", now);

        Assert.AreEqual(WebSocketMessageKind.Text, message.Kind);
        Assert.AreEqual(6, message.ByteCount);
        Assert.AreEqual(now, message.ReceivedAt);
        Assert.IsTrue(message.Binary.IsEmpty);
        Assert.IsTrue(message.ToString().Contains("6 bytes", StringComparison.Ordinal));
        Assert.IsFalse(
            message.ToString().Contains("中文", StringComparison.Ordinal),
            "訊息內容不該出現在敘述裡,以免行情或憑證被印進日誌");
    }

    [TestMethod]
    public void WebSocketMessage_null文字_擲出ArgumentNullException() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => WebSocketMessage.FromText(null!, DateTimeOffset.UnixEpoch));

    [TestMethod]
    public void WebSocketMessage_二進位_保留內容()
    {
        var payload = Encoding.UTF8.GetBytes("raw");

        var message = WebSocketMessage.FromBinary(payload, DateTimeOffset.UnixEpoch);

        Assert.AreEqual(WebSocketMessageKind.Binary, message.Kind);
        Assert.AreEqual(3, message.ByteCount);
        CollectionAssert.AreEqual(payload, message.Binary.ToArray());
    }
}
