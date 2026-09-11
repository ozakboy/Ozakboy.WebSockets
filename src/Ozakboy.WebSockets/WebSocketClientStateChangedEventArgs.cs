using Ozakboy.Core.Abstractions;

namespace Ozakboy.WebSockets;

/// <summary>
/// 客戶端狀態變化的事件資料。
/// The payload of a client state change.
/// </summary>
public sealed class WebSocketClientStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// 建立事件資料。
    /// Creates the event payload.
    /// </summary>
    /// <param name="previousState">變化前的狀態。The state before the change.</param>
    /// <param name="currentState">變化後的狀態。The state after the change.</param>
    /// <param name="timestamp">變化發生的時間。When the change happened.</param>
    /// <param name="closeReason">
    /// 進入 <see cref="WebSocketClientState.Closed"/> 的理由;其他狀態為 <see cref="WebSocketCloseReason.None"/>。
    /// Why the client closed; <see cref="WebSocketCloseReason.None"/> for any other state.
    /// </param>
    /// <param name="error">
    /// 造成這次變化的失敗(若有)。正常的連線與關閉為 <see langword="null"/>。
    /// The failure behind the change, if any. <see langword="null"/> for a normal connect or close.
    /// </param>
    public WebSocketClientStateChangedEventArgs(
        WebSocketClientState previousState,
        WebSocketClientState currentState,
        DateTimeOffset timestamp,
        WebSocketCloseReason closeReason = WebSocketCloseReason.None,
        Error? error = null)
    {
        PreviousState = previousState;
        CurrentState = currentState;
        Timestamp = timestamp;
        CloseReason = closeReason;
        Error = error;
    }

    /// <summary>
    /// 變化前的狀態。
    /// The state before the change.
    /// </summary>
    public WebSocketClientState PreviousState { get; }

    /// <summary>
    /// 變化後的狀態。
    /// The state after the change.
    /// </summary>
    public WebSocketClientState CurrentState { get; }

    /// <summary>
    /// 變化發生的時間(取自客戶端的 <see cref="TimeProvider"/>)。
    /// When the change happened, according to the client's <see cref="TimeProvider"/>.
    /// </summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// 進入 <see cref="WebSocketClientState.Closed"/> 的理由。
    /// Why the client closed.
    /// </summary>
    public WebSocketCloseReason CloseReason { get; }

    /// <summary>
    /// 造成這次變化的失敗(若有)。
    /// The failure behind the change, if any.
    /// </summary>
    public Error? Error { get; }
}
