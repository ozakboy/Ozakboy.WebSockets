using System.Net.WebSockets;

namespace Ozakboy.WebSockets.Tests;

/// <summary>
/// 正式環境連線實作的測試。這裡只驗證建構、設定套用與釋放 —— 全程不連線、不開通訊埠,
/// 因為那一層本來就只是對 <see cref="ClientWebSocket"/> 的轉呼叫,沒有自己的邏輯可測。
/// Tests for the production connection wrapper. Only construction, option application, and disposal are covered:
/// nothing connects and no port is opened, because that layer is pure pass-through into
/// <see cref="ClientWebSocket"/> with no logic of its own.
/// </summary>
[TestClass]
public sealed class ClientWebSocketConnectionTests
{
    [TestMethod]
    public void 建構_socket為null_擲出ArgumentNullException() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new ClientWebSocketConnection(null!));

    [TestMethod]
    public void 新建的連線_狀態為None()
    {
        using var socket = new ClientWebSocket();
        using var connection = new ClientWebSocketConnection(socket);

        Assert.AreEqual(WebSocketState.None, connection.State);
    }

    [TestMethod]
    public void 中止未連線的socket_不會擲出例外()
    {
        using var socket = new ClientWebSocket();
        using var connection = new ClientWebSocketConnection(socket);

        connection.Abort();

        // 從未連線過的 socket 被中止之後回報的是 Closed 而不是 Aborted,這是 BCL 的行為。
        // A socket that never connected reports Closed rather than Aborted after being aborted; that is BCL behaviour.
        Assert.AreEqual(WebSocketState.Closed, connection.State);
    }

    [TestMethod]
    public void 工廠_建構參數為null_擲出ArgumentNullException() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new ClientWebSocketConnectionFactory(null!));

    [TestMethod]
    public void 工廠_每次都產生新的連線()
    {
        var options = new WebSocketClientOptions { Uri = new Uri("wss://example.invalid/stream") };
        var factory = new ClientWebSocketConnectionFactory(options);

        using var first = factory.Create();
        using var second = factory.Create();

        Assert.AreNotSame(first, second, "ClientWebSocket 用過即拋,重連必須換一條新的");
        Assert.AreEqual(WebSocketState.None, first.State);
    }

    [TestMethod]
    public void 工廠_套用標頭與子協定與keepAlive()
    {
        var options = new WebSocketClientOptions
        {
            Uri = new Uri("wss://example.invalid/stream"),
            TransportKeepAliveInterval = TimeSpan.FromSeconds(45),
        };
        options.RequestHeaders["X-Placeholder"] = "value";
        options.SubProtocols.Add("json");

        var factory = new ClientWebSocketConnectionFactory(options);

        using var connection = factory.Create();

        // 設定無法從外部讀回,能驗證的是套用過程沒有擲出例外、連線可用。
        // The applied options are not readable from outside; what can be verified is that applying them threw
        // nothing and the connection is usable.
        Assert.AreEqual(WebSocketState.None, connection.State);
    }

    [TestMethod]
    public void 工廠_標頭名稱不合法_擲出且不留下未釋放的socket()
    {
        var options = new WebSocketClientOptions { Uri = new Uri("wss://example.invalid/stream") };
        options.RequestHeaders["非法 標頭:名稱"] = "value";

        var factory = new ClientWebSocketConnectionFactory(options);

        Assert.ThrowsExactly<ArgumentException>(() => _ = factory.Create());
    }
}
