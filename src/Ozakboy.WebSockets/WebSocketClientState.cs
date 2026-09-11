namespace Ozakboy.WebSockets;

/// <summary>
/// 客戶端的連線生命週期狀態。供上層顯示健康度、決定是否要告警。
/// The connection lifecycle state of the client, for health displays and alerting decisions.
/// </summary>
/// <remarks>
/// <para>
/// 這個狀態刻意與 <see cref="System.Net.WebSockets.WebSocketState"/> 分開。後者只描述單一條 socket,
/// 重連時舊 socket 會被丟棄、換上新的一條;而上層關心的是「這個客戶端現在有沒有在供料」,
/// 那是跨越多條 socket 的概念。
/// This is deliberately separate from <see cref="System.Net.WebSockets.WebSocketState"/>, which describes a single
/// socket. Reconnecting throws the old socket away and creates a new one, whereas callers care about whether the
/// client as a whole is delivering data — a notion that spans sockets.
/// </para>
/// <para>
/// <b>重要:</b><see cref="Connected"/> 不保證收得到資料。實測遇過握手成功、socket 維持
/// <c>Open</c>、但 200 秒之內一個 frame 都沒有進來的情況(中介設備放行握手卻吃掉資料流),
/// 全程沒有例外也沒有斷線。要偵測那種故障只能靠閒置逾時,見
/// <see cref="WebSocketClientOptions.IdleTimeout"/>。
/// <b>Important:</b> <see cref="Connected"/> does not guarantee that data is arriving. We have measured a case where
/// the handshake succeeded, the socket stayed <c>Open</c>, and not a single frame arrived for 200 seconds — no
/// exception, no disconnect. Only an idle timeout can detect that; see
/// <see cref="WebSocketClientOptions.IdleTimeout"/>.
/// </para>
/// </remarks>
public enum WebSocketClientState
{
    /// <summary>
    /// 尚未連線,也還沒開始連線。這是建構後的初始狀態。
    /// Not connected and not yet connecting. The state a freshly constructed client is in.
    /// </summary>
    Disconnected = 0,

    /// <summary>
    /// 正在進行第一次連線(握手尚未完成)。
    /// The first connection attempt is in flight; the handshake has not completed.
    /// </summary>
    Connecting = 1,

    /// <summary>
    /// 已連線。訂閱已重放完畢,接收迴圈正在執行。
    /// Connected: subscriptions have been replayed and the receive loop is running.
    /// </summary>
    Connected = 2,

    /// <summary>
    /// 連線中斷後正在退避等待或重新連線。此狀態下送出的訊息會失敗,但訂閱仍會保留並在連上後重放。
    /// The connection dropped and the client is backing off or reconnecting. Sends fail in this state, but
    /// subscriptions are retained and replayed once the connection is back.
    /// </summary>
    Reconnecting = 3,

    /// <summary>
    /// 已終止,不會再嘗試連線。可能是呼叫端主動關閉,也可能是重連次數用盡;
    /// 兩者的區別見 <see cref="IWebSocketClient.CloseReason"/>。
    /// Terminated with no further attempts. Either the caller closed it or the reconnect attempts ran out;
    /// <see cref="IWebSocketClient.CloseReason"/> tells the two apart.
    /// </summary>
    Closed = 4,
}
