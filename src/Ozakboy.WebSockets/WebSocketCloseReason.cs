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

    /// <summary>
    /// 遇到非暫時性的失敗而放棄,一次都沒有重試。同樣需要告警。
    /// Gave up on a non-transient failure without retrying even once. This also needs an alert.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 與 <see cref="ReconnectAttemptsExhausted"/> 的差別是「試過很多次都失敗」對「一次都不該試」。
    /// 重試不可能改變非暫時性失敗的結果 —— 設定在執行期被改壞就是最典型的例子 —— 所以繼續退避重試只會
    /// 無限空轉:不會崩潰、不會停止、日誌一直在動,看起來像在工作。這兩種結局必須分得出來,否則值班的人
    /// 會照著「重連用盡」去查對方的可用性,而真正的原因在自己這邊。
    /// The difference from <see cref="ReconnectAttemptsExhausted"/> is "tried many times and failed" versus "must not
    /// try even once". Retrying cannot change the outcome of a non-transient failure — a configuration mutated after
    /// start-up being the clearest case — so backing off and trying again would only spin forever: no crash, no stop,
    /// a log that keeps moving and looks like work. The two endings have to be distinguishable, or whoever is on call
    /// goes looking at the peer's availability when the cause is on this side.
    /// </para>
    /// <para>
    /// 害客戶端放棄的那個錯誤,其代碼與分類放在終局錯誤的
    /// <see cref="Core.Abstractions.Error.Data"/> 裡(<see cref="WebSocketErrorDataKeys.InnerCode"/> 與
    /// <see cref="WebSocketErrorDataKeys.InnerCategory"/>)。
    /// The code and category of the failure that made the client give up are in the terminal error's
    /// <see cref="Core.Abstractions.Error.Data"/>, as <see cref="WebSocketErrorDataKeys.InnerCode"/> and
    /// <see cref="WebSocketErrorDataKeys.InnerCategory"/>.
    /// </para>
    /// </remarks>
    UnrecoverableError = 5,
}
