namespace Ozakboy.WebSockets;

/// <summary>
/// 客戶端為什麼進入 <see cref="WebSocketClientState.Closed"/>。
/// Why the client ended up in <see cref="WebSocketClientState.Closed"/>.
/// </summary>
/// <remarks>
/// 「呼叫端主動關閉」與「連線異常中斷到放棄重連」在監控上完全是兩件事:前者是正常關機,
/// 後者必須告警。只看狀態機分不出來,所以獨立成一個欄位。
/// A caller-initiated shutdown and an exhausted reconnect loop are entirely different events for monitoring: the
/// first is a normal stop, the second must raise an alert. The state machine alone cannot tell them apart, so the
/// reason is carried separately.
/// </remarks>
public enum WebSocketCloseReason
{
    /// <summary>
    /// 尚未關閉。
    /// Not closed yet.
    /// </summary>
    None = 0,

    /// <summary>
    /// 呼叫端呼叫了 <see cref="IWebSocketClient.CloseAsync"/>,屬於正常關機。
    /// The caller invoked <see cref="IWebSocketClient.CloseAsync"/>; a normal shutdown.
    /// </summary>
    CallerRequested = 1,

    /// <summary>
    /// 對方送出關閉 frame 主動結束連線,且本客戶端未設定自動重連(或重連已用盡)。
    /// The remote endpoint sent a close frame, and this client is not reconnecting (or has run out of attempts).
    /// </summary>
    RemoteClosed = 2,

    /// <summary>
    /// 重連次數用盡後放棄。這是需要告警的情況 —— 資料流已經停止,而且不會自己回來。
    /// Gave up after exhausting the reconnect attempts. This is the case that needs an alert: the data stream has
    /// stopped and will not come back on its own.
    /// </summary>
    ReconnectAttemptsExhausted = 3,

    /// <summary>
    /// 物件被 <see cref="System.IAsyncDisposable.DisposeAsync"/> 釋放。
    /// The instance was released through <see cref="System.IAsyncDisposable.DisposeAsync"/>.
    /// </summary>
    Disposed = 4,
}
