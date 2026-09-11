using System.Net.WebSockets;

namespace Ozakboy.WebSockets;

/// <summary>
/// 單一條 WebSocket 連線的最小介面,正式環境的實作是 <see cref="ClientWebSocketConnection"/>
/// (包在 BCL 的 <see cref="ClientWebSocket"/> 外面)。
/// The minimal surface of one WebSocket connection. The production implementation is
/// <see cref="ClientWebSocketConnection"/>, a thin wrapper over the BCL <see cref="ClientWebSocket"/>.
/// </summary>
/// <remarks>
/// <para>
/// 抽出這層的用途是可測性:<see cref="WebSocketClient"/> 裡值得測的東西(重連、重放訂閱、閒置逾時、背壓)
/// 全都在連線之上,而不在連線之內。有了這個介面,那些行為可以用假連線在確定的時間軸上測完,
/// 不需要開通訊埠、不需要真的網路,測試也不會因為機器忙碌而偶發失敗。
/// This seam exists for testability. Everything in <see cref="WebSocketClient"/> that is worth testing — reconnect,
/// subscription replay, idle timeout, backpressure — sits above the connection rather than inside it. With this
/// interface those behaviours can be exercised against a fake on a deterministic clock, with no port to bind, no
/// real network, and no flakiness when the machine is busy.
/// </para>
/// <para>
/// 介面刻意保持極小,實作端幾乎沒有邏輯可言;換句話說,沒有被測試覆蓋到的部分也就只剩下對
/// <see cref="ClientWebSocket"/> 的直接轉呼叫。
/// The interface is deliberately tiny, so the implementation contains almost no logic: what testing cannot reach is
/// reduced to direct pass-through calls into <see cref="ClientWebSocket"/>.
/// </para>
/// <para>
/// <b>沒有 <c>CloseAsync</c>。</b>優雅關閉只用 <see cref="CloseOutputAsync"/>:送出關閉 frame 之後讓接收迴圈
/// 自己收到對方的關閉 frame 而結束,socket 才會停在 <see cref="WebSocketState.Closed"/>。
/// 理由見 <see cref="ReceiveAsync"/> 的說明。
/// <b>There is no <c>CloseAsync</c>.</b> A graceful close uses <see cref="CloseOutputAsync"/> alone: send the close
/// frame and let the receive loop end by seeing the peer's close frame, which leaves the socket at
/// <see cref="WebSocketState.Closed"/>. See the remarks on <see cref="ReceiveAsync"/> for why.
/// </para>
/// </remarks>
public interface IWebSocketConnection : IDisposable
{
    /// <summary>
    /// 底層 socket 的狀態。
    /// The state of the underlying socket.
    /// </summary>
    /// <remarks>
    /// <b>不要拿這個當健康度指標。</b>它是 <see cref="WebSocketState.Open"/> 只代表 socket 沒有關閉,
    /// 不代表資料還在進來。實測遇過維持 <see cref="WebSocketState.Open"/> 卻連續 200 秒收不到任何 frame 的情況。
    /// <b>Do not treat this as a health signal.</b> <see cref="WebSocketState.Open"/> only means the socket is not
    /// closed, not that data is flowing. We have measured a socket that stayed <see cref="WebSocketState.Open"/>
    /// while receiving nothing at all for 200 seconds.
    /// </remarks>
    WebSocketState State { get; }

    /// <summary>
    /// 連線到指定位址並完成握手。
    /// Connects to the endpoint and completes the handshake.
    /// </summary>
    /// <param name="uri">要連線的位址。The endpoint.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>代表這次握手的工作。A task representing the handshake.</returns>
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    /// <summary>
    /// 送出一則完整訊息。
    /// Sends one complete message.
    /// </summary>
    /// <param name="buffer">要送出的內容。The payload.</param>
    /// <param name="messageType">訊息類型。The message type.</param>
    /// <param name="endOfMessage">是否為訊息的最後一段。Whether this is the final fragment.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>代表這次傳送的工作。A task representing the send.</returns>
    /// <remarks>
    /// 同一條連線上不得有兩個傳送同時進行,呼叫端必須自行序列化。
    /// Two sends must never overlap on one connection; the caller is responsible for serialising them.
    /// </remarks>
    ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken);

    /// <summary>
    /// 接收下一段資料。
    /// Receives the next fragment.
    /// </summary>
    /// <param name="buffer">寫入的目的緩衝區。The destination buffer.</param>
    /// <param name="cancellationToken">
    /// 取消權杖。<b>這個權杖只該當成硬逾時的最後保險。</b>
    /// The cancellation token. <b>Treat it strictly as a hard-timeout last resort.</b>
    /// </param>
    /// <returns>這一段的接收結果。The receive result for this fragment.</returns>
    /// <remarks>
    /// <para>
    /// <b>不要用取消權杖來「停止接收」。</b>取消一個進行中的接收,其語意不是停止接收而是中止整條連線:
    /// socket 會進入 <see cref="WebSocketState.Aborted"/>,之後就再也無法優雅關閉 ——
    /// 送出關閉 frame 會失敗,對方看到的是連線被硬扯斷而不是正常結束。
    /// <b>Never use the token to "stop receiving".</b> Cancelling an in-flight receive does not mean stop receiving;
    /// it means abort the connection. The socket moves to <see cref="WebSocketState.Aborted"/> and can no longer be
    /// closed gracefully: sending a close frame fails, and the peer sees a connection that was yanked away rather
    /// than ended properly.
    /// </para>
    /// <para>
    /// 正確的停止方式是讓接收迴圈自己結束:先送出關閉 frame(<see cref="CloseOutputAsync"/>),
    /// 對方回覆關閉 frame 之後這個方法會回傳 <see cref="WebSocketMessageType.Close"/>,迴圈據此 break,
    /// socket 停在 <see cref="WebSocketState.Closed"/>。
    /// The correct way to stop is to let the receive loop finish on its own: send the close frame with
    /// <see cref="CloseOutputAsync"/>, and when the peer echoes it this method returns
    /// <see cref="WebSocketMessageType.Close"/> so the loop can break, leaving the socket at
    /// <see cref="WebSocketState.Closed"/>.
    /// </para>
    /// </remarks>
    ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>
    /// 送出關閉 frame,但不等待對方回覆。
    /// Sends the close frame without waiting for the peer's reply.
    /// </summary>
    /// <param name="closeStatus">關閉狀態碼。The close status.</param>
    /// <param name="statusDescription">關閉描述。The close description.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>代表這次傳送的工作。A task representing the send.</returns>
    Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken);

    /// <summary>
    /// 立刻中止連線。
    /// Aborts the connection immediately.
    /// </summary>
    /// <remarks>
    /// 只在連線已經確定沒救的時候使用(閒置逾時判定已死、優雅關閉等不到對方回覆)。
    /// 正常關閉請走 <see cref="CloseOutputAsync"/>,否則 socket 會停在 <see cref="WebSocketState.Aborted"/>。
    /// Use only when the connection is already beyond saving — declared dead by the idle timeout, or a graceful
    /// close that the peer never answered. Normal shutdown goes through <see cref="CloseOutputAsync"/>; otherwise
    /// the socket ends at <see cref="WebSocketState.Aborted"/>.
    /// </remarks>
    void Abort();
}
