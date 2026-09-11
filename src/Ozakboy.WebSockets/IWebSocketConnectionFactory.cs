namespace Ozakboy.WebSockets;

/// <summary>
/// 產生 <see cref="IWebSocketConnection"/> 的工廠。
/// Creates <see cref="IWebSocketConnection"/> instances.
/// </summary>
/// <remarks>
/// 每次重連都必須是全新的一條連線 —— <see cref="System.Net.WebSockets.ClientWebSocket"/> 用過即拋,
/// 關閉或中止後無法再次連線,所以重連不能重用同一個物件,必須向工廠再要一個。
/// Every reconnect needs a brand-new connection: <see cref="System.Net.WebSockets.ClientWebSocket"/> is
/// single-use and cannot reconnect after being closed or aborted, so the client asks the factory for another
/// instance rather than reusing the old one.
/// </remarks>
public interface IWebSocketConnectionFactory
{
    /// <summary>
    /// 建立一條尚未連線的新連線。
    /// Creates a new, not-yet-connected connection.
    /// </summary>
    /// <returns>新的連線。The new connection.</returns>
    IWebSocketConnection Create();
}
