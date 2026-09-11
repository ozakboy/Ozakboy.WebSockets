using System.Net.WebSockets;

namespace Ozakboy.WebSockets.Tests;

[TestClass]
public sealed class WebSocketClientCloseTests
{
    /// <summary>
    /// 優雅關閉之後 socket 必須停在 <see cref="WebSocketState.Closed"/>。停在
    /// <see cref="WebSocketState.Aborted"/> 就代表關閉路徑上有人拿取消權杖去砍接收 —— 那是這個套件
    /// 明確要避開的坑。
    /// After a graceful close the socket must end at <see cref="WebSocketState.Closed"/>. Ending at
    /// <see cref="WebSocketState.Aborted"/> would mean something on the close path cancelled the receive with a
    /// token, which is the exact trap this package is built to avoid.
    /// </summary>
    [TestMethod]
    public async Task CloseAsync_優雅關閉_socket以Closed收場而不是Aborted()
    {
        await using var harness = new ClientHarness();
        harness.StartConsuming();
        await harness.Client.ConnectAsync();

        var result = await harness.Client.CloseAsync();

        Assert.IsTrue(result.IsSuccess, result.ToString());
        Assert.AreEqual(WebSocketClientState.Closed, harness.Client.State);
        Assert.AreEqual(WebSocketCloseReason.CallerRequested, harness.Client.CloseReason);
        Assert.AreEqual(WebSocketState.Closed, harness.Factory[0].State, "優雅關閉的 socket 不該是 Aborted");
        Assert.IsFalse(harness.Factory[0].WasAborted, "正常路徑上不該呼叫 Abort");
        Assert.IsTrue(harness.Factory[0].IsDisposed);
    }

    [TestMethod]
    public async Task CloseAsync_之後訊息串流會結束()
    {
        await using var harness = new ClientHarness();
        harness.StartConsuming();
        await harness.Client.ConnectAsync();

        await harness.Client.CloseAsync();

        await Wait.UntilAsync(() => harness.ConsumerCompleted, "串流結束");
    }

    [TestMethod]
    public async Task CloseAsync_之後不再重連()
    {
        await using var harness = new ClientHarness();
        harness.StartConsuming();
        await harness.Client.ConnectAsync();

        await harness.Client.CloseAsync();
        harness.Time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(20);

        Assert.AreEqual(1, harness.Factory.CreateCount);
    }

    [TestMethod]
    public async Task CloseAsync_對方不回覆關閉frame_逾時後改以中止收尾()
    {
        await using var harness = new ClientHarness(options => options.CloseTimeout = TimeSpan.FromSeconds(5));
        harness.StartConsuming();
        harness.Factory.Configure = (connection, _) => connection.EchoClose = false;

        await harness.Client.ConnectAsync();

        var closeTask = harness.Client.CloseAsync();
        await harness.AdvanceUntilAsync(() => closeTask.IsCompleted, "關閉逾時觸發");

        var result = await closeTask;

        Assert.IsTrue(result.IsFailure, "等不到對方回覆的關閉不算成功");
        Assert.IsTrue(harness.Factory[0].WasAborted, "逾時之後才允許中止");
        Assert.AreEqual(WebSocketClientState.Closed, harness.Client.State);
    }

    [TestMethod]
    public async Task CloseAsync_重複呼叫_第二次直接成功()
    {
        await using var harness = new ClientHarness();
        await harness.Client.ConnectAsync();

        await harness.Client.CloseAsync();
        var second = await harness.Client.CloseAsync();

        Assert.IsTrue(second.IsSuccess, second.ToString());
    }

    [TestMethod]
    public async Task CloseAsync_未曾連線_也能關閉()
    {
        await using var harness = new ClientHarness();

        var result = await harness.Client.CloseAsync();

        Assert.IsTrue(result.IsSuccess, result.ToString());
        Assert.AreEqual(WebSocketClientState.Closed, harness.Client.State);
    }

    [TestMethod]
    public async Task CloseAsync_在重連等待中_立刻停止()
    {
        await using var harness = new ClientHarness();
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
        await harness.WaitForBackoffArmedAsync();

        var result = await harness.Client.CloseAsync();

        Assert.IsTrue(result.IsSuccess, result.ToString());
        Assert.AreEqual(WebSocketClientState.Closed, harness.Client.State);
        Assert.AreEqual(WebSocketCloseReason.CallerRequested, harness.Client.CloseReason);
    }

    [TestMethod]
    public async Task DisposeAsync_未先關閉_理由為已釋放()
    {
        await using var harness = new ClientHarness();
        harness.StartConsuming();
        await harness.Client.ConnectAsync();

        await harness.Client.DisposeAsync();

        Assert.AreEqual(WebSocketClientState.Closed, harness.Client.State);
        Assert.AreEqual(WebSocketCloseReason.Disposed, harness.Client.CloseReason);
    }

    [TestMethod]
    public async Task DisposeAsync_先關閉過_保留原本的關閉理由()
    {
        await using var harness = new ClientHarness();
        await harness.Client.ConnectAsync();
        await harness.Client.CloseAsync();

        await harness.Client.DisposeAsync();

        Assert.AreEqual(WebSocketCloseReason.CallerRequested, harness.Client.CloseReason);
    }

    [TestMethod]
    public async Task DisposeAsync_重複呼叫_不會擲出例外()
    {
        await using var harness = new ClientHarness();
        await harness.Client.ConnectAsync();

        await harness.Client.DisposeAsync();
        await harness.Client.DisposeAsync();

        Assert.AreEqual(WebSocketClientState.Closed, harness.Client.State);
    }

    [TestMethod]
    public async Task 釋放之後_不允許再連線()
    {
        await using var harness = new ClientHarness();
        await harness.Client.DisposeAsync();

        var result = await harness.Client.ConnectAsync();

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(WebSocketErrorCodes.InvalidState, result.Error!.Code);
    }
}
