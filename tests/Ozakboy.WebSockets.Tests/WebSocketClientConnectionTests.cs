using System.Net.WebSockets;

namespace Ozakboy.WebSockets.Tests;

[TestClass]
public sealed class WebSocketClientConnectionTests
{
    [TestMethod]
    public async Task ConnectAsync_握手成功_狀態為已連線()
    {
        await using var harness = new ClientHarness();

        var result = await harness.Client.ConnectAsync();

        Assert.IsTrue(result.IsSuccess, result.ToString());
        Assert.AreEqual(WebSocketClientState.Connected, harness.Client.State);
        Assert.AreEqual(1, harness.Factory.CreateCount);
        Assert.AreEqual(WebSocketState.Open, harness.Factory[0].State);
        Assert.IsNotNull(harness.Client.Statistics.ConnectedAt);
    }

    [TestMethod]
    public async Task ConnectAsync_握手失敗_回傳網路失敗且狀態退回未連線()
    {
        await using var harness = new ClientHarness();
        harness.Factory.Configure = (connection, _) =>
            connection.ConnectException = new WebSocketException(WebSocketError.NotAWebSocket, "拒絕連線。refused");

        var result = await harness.Client.ConnectAsync();

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(WebSocketErrorCodes.ConnectFailed, result.Error!.Code);
        Assert.IsTrue(result.Error.IsTransient, "網路類失敗應該是暫時性的");
        Assert.AreEqual(WebSocketClientState.Disconnected, harness.Client.State);
    }

    [TestMethod]
    public async Task ConnectAsync_失敗後_可以再呼叫一次()
    {
        await using var harness = new ClientHarness();
        harness.Factory.Configure = (connection, ordinal) =>
        {
            if (ordinal == 1)
            {
                connection.ConnectException = new WebSocketException(WebSocketError.Faulted, "第一次失敗");
            }
        };

        var first = await harness.Client.ConnectAsync();
        var second = await harness.Client.ConnectAsync();

        Assert.IsTrue(first.IsFailure);
        Assert.IsTrue(second.IsSuccess, second.ToString());
    }

    [TestMethod]
    public async Task ConnectAsync_握手卡住超過逾時_回傳逾時失敗()
    {
        await using var harness = new ClientHarness(options => options.ConnectTimeout = TimeSpan.FromSeconds(10));
        harness.Factory.Configure = (connection, _) => connection.ConnectGate = new TaskCompletionSource();

        var connectTask = harness.Client.ConnectAsync();
        await harness.AdvanceUntilAsync(() => connectTask.IsCompleted, "連線逾時觸發");

        var result = await connectTask;

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(WebSocketErrorCodes.ConnectTimeout, result.Error!.Code);
        Assert.AreEqual(ErrorCategory.Timeout, result.Error.Category);
    }

    [TestMethod]
    public async Task ConnectAsync_已經在執行中_回傳狀態衝突()
    {
        await using var harness = new ClientHarness();
        await harness.Client.ConnectAsync();

        var second = await harness.Client.ConnectAsync();

        Assert.IsTrue(second.IsFailure);
        Assert.AreEqual(WebSocketErrorCodes.InvalidState, second.Error!.Code);
    }

    [TestMethod]
    public async Task Start_背景連線_最終會連上()
    {
        await using var harness = new ClientHarness();

        var result = harness.Client.Start();

        Assert.IsTrue(result.IsSuccess, result.ToString());
        await harness.WaitForStateAsync(WebSocketClientState.Connected);
        Assert.AreEqual(1, harness.Factory.CreateCount);
    }

    [TestMethod]
    public async Task Start_第一次就連不上_仍會依退避繼續重試()
    {
        await using var harness = new ClientHarness();
        harness.Factory.Configure = (connection, ordinal) =>
        {
            if (ordinal == 1)
            {
                connection.ConnectException = new WebSocketException(WebSocketError.Faulted, "對方還沒起來");
            }
        };

        harness.Client.Start();

        await harness.WaitForStateAsync(WebSocketClientState.Reconnecting);
        await harness.AdvanceUntilAsync(() => harness.Client.State == WebSocketClientState.Connected, "重試後連上");

        Assert.AreEqual(2, harness.Factory.CreateCount);
    }

    [TestMethod]
    public async Task Start_重複呼叫_回傳狀態衝突()
    {
        await using var harness = new ClientHarness();
        harness.Client.Start();

        var second = harness.Client.Start();

        Assert.IsTrue(second.IsFailure);
        Assert.AreEqual(WebSocketErrorCodes.InvalidState, second.Error!.Code);
    }

    [TestMethod]
    public void 建構_設定為null_擲出ArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new WebSocketClient(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new WebSocketClient(
            new WebSocketClientOptions { Uri = new Uri("wss://a.invalid/") },
            (IWebSocketConnectionFactory)null!));
    }

    [TestMethod]
    public void 建構_設定不合法_擲出ArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() => _ = new WebSocketClient(new WebSocketClientOptions()));
        Assert.ThrowsExactly<ArgumentException>(() =>
            _ = new WebSocketClient(new WebSocketClientOptions { Uri = new Uri("https://example.invalid/") }));
    }

    /// <summary>
    /// 建構式塞不進 <c>Result</c>,失敗只能走例外;但錯誤的代碼與分類不該因此消失在訊息字串裡。
    /// 型別維持 <see cref="ArgumentException"/>(既有的 catch 不受影響),完整的 <c>Error</c> 由
    /// <c>ResultException</c> 帶在 <c>InnerException</c> 上。
    /// A constructor cannot return a <c>Result</c>, so the failure travels as an exception — but the code and
    /// category must not vanish into the message. The type stays <see cref="ArgumentException"/>, leaving existing
    /// catch blocks alone, while the full <c>Error</c> rides along in <c>InnerException</c> as a
    /// <c>ResultException</c>.
    /// </summary>
    [TestMethod]
    public void 建構_設定不合法_內層例外帶著完整的Error()
    {
        var thrown = Assert.ThrowsExactly<ArgumentException>(() => _ = new WebSocketClient(new WebSocketClientOptions()));

        var inner = thrown.InnerException as ResultException;
        Assert.IsNotNull(inner, "內層例外必須是 ResultException,呼叫端才拿得回 Error");
        Assert.AreEqual(WebSocketErrorCodes.OptionsInvalid, inner.Error.Code);
        Assert.AreEqual(ErrorCategory.Validation, inner.Error.Category);
    }

    [TestMethod]
    public async Task 建構_不指定連線工廠_可建立但不會主動連線()
    {
        var options = new WebSocketClientOptions { Uri = new Uri("wss://example.invalid/stream") };

        await using var client = new WebSocketClient(options);

        // 建構本身不碰網路,狀態停在未連線;這個測試因此不需要任何對外連線。
        // Construction touches no network and leaves the client Disconnected, so this test needs no outbound socket.
        Assert.AreEqual(WebSocketClientState.Disconnected, client.State);
        Assert.AreEqual(WebSocketCloseReason.None, client.CloseReason);
    }

    [TestMethod]
    public async Task 狀態改變_會觸發事件()
    {
        await using var harness = new ClientHarness();

        await harness.Client.ConnectAsync();

        var changes = harness.StateChanges;
        Assert.IsTrue(changes.Any(change => change.CurrentState == WebSocketClientState.Connecting));
        Assert.IsTrue(changes.Any(change => change.CurrentState == WebSocketClientState.Connected));
        Assert.AreEqual(WebSocketClientState.Disconnected, changes[0].PreviousState);
    }

    [TestMethod]
    public async Task 事件處理常式擲出例外_不會讓客戶端停擺()
    {
        await using var harness = new ClientHarness();
        harness.Client.StateChanged += (_, _) => throw new InvalidOperationException("處理常式故意爆炸");

        var result = await harness.Client.ConnectAsync();

        Assert.IsTrue(result.IsSuccess, result.ToString());
        Assert.AreEqual(WebSocketClientState.Connected, harness.Client.State);
    }
}
