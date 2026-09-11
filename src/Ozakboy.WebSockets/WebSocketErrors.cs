using Ozakboy.Core.Abstractions;

namespace Ozakboy.WebSockets;

/// <summary>
/// 產生本套件的錯誤,把代碼與分類的對應集中在一個地方。
/// Builds this package's errors, keeping the code-to-category mapping in one place.
/// </summary>
/// <remarks>
/// <para>
/// 分類的挑選規則只有一條:<see cref="Error.IsTransient"/> 必須說實話。凡是「重試有機會成功」的失敗才用
/// 暫時性分類,重試不可能成功的一律用非暫時性分類 —— 否則呼叫端就得改看錯誤代碼才知道要不要重試,
/// 而一旦有人這樣做,<see cref="Error.IsTransient"/> 就不再是判斷重試的單一真相來源了。
/// There is only one rule for picking a category: <see cref="Error.IsTransient"/> must tell the truth. A transient
/// category is used only where retrying could succeed, and anything that retrying cannot fix gets a non-transient
/// one. Otherwise callers have to branch on the error code to know whether to retry — and once anyone does that,
/// <see cref="Error.IsTransient"/> has stopped being the single source of truth for that decision.
/// </para>
/// <para>
/// 數值型的細節一律同時放進 <see cref="Error.Data"/>,讓下游用 <see cref="Error.TryGetInt64"/> 讀回來,
/// 不必去剖析訊息字串。訊息隨時可能被改寫,資料鍵不會。
/// Numeric details also go into <see cref="Error.Data"/> so that downstream code can read them back with
/// <see cref="Error.TryGetInt64"/> instead of parsing the message. The message may be rewritten at any time; the
/// data keys will not.
/// </para>
/// </remarks>
internal static class WebSocketErrors
{
    /// <summary>
    /// <see cref="Error.Data"/> 的資料鍵。與錯誤代碼一樣是契約的一部分。
    /// The <see cref="Error.Data"/> keys. Like the error codes, these are part of the contract.
    /// </summary>
    internal static class DataKeys
    {
        /// <summary>放棄前實際嘗試了幾次重連。How many reconnect attempts were made before giving up.</summary>
        internal const string Attempts = "attempts";

        /// <summary>相關逾時的毫秒數。The relevant timeout in milliseconds.</summary>
        internal const string TimeoutMs = "timeoutMs";

        /// <summary>訊息大小上限(位元組)。The message size limit in bytes.</summary>
        internal const string LimitBytes = "limitBytes";

        /// <summary>相關訂閱的識別碼。The identifier of the subscription involved.</summary>
        internal const string SubscriptionId = "subscriptionId";

        /// <summary>當下的生命週期狀態。The lifecycle state at the time.</summary>
        internal const string State = "state";

        /// <summary>被拒絕或被取消的操作名稱。The name of the operation that was refused or cancelled.</summary>
        internal const string Operation = "operation";

        /// <summary>內層失敗的錯誤代碼。The error code of the underlying failure.</summary>
        internal const string InnerCode = "innerCode";
    }

    /// <summary>
    /// 以不變文化格式化訊息。錯誤訊息會進日誌並被跨環境比對,不能跟著執行機器的地區設定變。
    /// Formats a message with the invariant culture: error text lands in logs and gets compared across machines, so
    /// it must not follow the host's regional settings.
    /// </summary>
    private static string Inv(FormattableString message) => FormattableString.Invariant(message);

    internal static Error OptionsInvalid(string message) =>
        Error.Validation(WebSocketErrorCodes.OptionsInvalid, message);

    internal static Error ConnectFailed(Exception exception) =>
        Error.FromException(exception, WebSocketErrorCodes.ConnectFailed, ErrorCategory.Network);

    internal static Error ConnectTimeout(TimeSpan timeout) =>
        Error.Timeout(
            WebSocketErrorCodes.ConnectTimeout,
            Inv($"WebSocket 握手超過 {timeout.TotalSeconds:0.###} 秒未完成。The WebSocket handshake did not complete within {timeout.TotalSeconds:0.###} s."))
            .WithData(DataKeys.TimeoutMs, (long)timeout.TotalMilliseconds);

    internal static Error ConnectionLost(string detail) =>
        Error.Network(
            WebSocketErrorCodes.ConnectionLost,
            Inv($"WebSocket 連線中斷:{detail}。The WebSocket connection was lost: {detail}."));

    internal static Error ConnectionLost(Exception exception) =>
        Error.FromException(exception, WebSocketErrorCodes.ConnectionLost, ErrorCategory.Network);

    internal static Error IdleTimeout(TimeSpan timeout) =>
        Error.Timeout(
            WebSocketErrorCodes.IdleTimeout,
            Inv($"超過 {timeout.TotalSeconds:0.###} 秒沒有收到任何訊息,連線判定為已死。No message arrived for {timeout.TotalSeconds:0.###} s; the connection is treated as dead."))
            .WithData(DataKeys.TimeoutMs, (long)timeout.TotalMilliseconds);

    /// <summary>
    /// 重連次數用盡:這個客戶端物件的生命週期到此為止。
    /// The reconnect budget is spent and this client instance's lifetime is over.
    /// </summary>
    /// <remarks>
    /// 分類是 <see cref="ErrorCategory.Exhausted"/> 而不是 <see cref="ErrorCategory.Unavailable"/>。語意上
    /// 兩者都說得通 —— 對方確實不可用 —— 但 <see cref="ErrorCategory.Unavailable"/> 是暫時性分類,套上去會讓
    /// <see cref="Error.IsTransient"/> 對這個錯誤回答「值得重試」,而事實是再怎麼重試這個物件都不會再連上,
    /// 必須換一個新的客戶端。
    /// The category is <see cref="ErrorCategory.Exhausted"/> rather than <see cref="ErrorCategory.Unavailable"/>.
    /// Both read plausibly — the peer genuinely is unavailable — but <see cref="ErrorCategory.Unavailable"/> is a
    /// transient category, and using it would make <see cref="Error.IsTransient"/> answer "worth retrying" for a
    /// failure that no amount of retrying will fix: this instance will never connect again and the caller has to
    /// construct a new client.
    /// </remarks>
    internal static Error ReconnectExhausted(int attempts) =>
        Error.Exhausted(
            WebSocketErrorCodes.ReconnectExhausted,
            Inv($"重連 {attempts} 次後放棄,客戶端已停止。Gave up after {attempts} reconnect attempts; the client has stopped."))
            .WithData(DataKeys.Attempts, (long)attempts);

    /// <summary>
    /// 目前沒有連線可以送出。
    /// There is no connection to send on right now.
    /// </summary>
    /// <remarks>
    /// 分類跟著狀態走。客戶端已經 <see cref="WebSocketClientState.Closed"/> 時它永遠不會再連上,那是
    /// <see cref="ErrorCategory.Exhausted"/>;其餘狀態(連線中、重連中)只是此刻沒有連線,稍後會有,
    /// 那才是暫時性的 <see cref="ErrorCategory.Unavailable"/>。少了這個區分,對著一個已經死掉的客戶端重試
    /// 傳送的呼叫端會永遠重試下去,而 <see cref="Error.IsTransient"/> 一路都告訴它「再試一次」。
    /// The category follows the state. A client already in <see cref="WebSocketClientState.Closed"/> will never
    /// connect again, which is <see cref="ErrorCategory.Exhausted"/>; every other state — connecting, reconnecting —
    /// simply has no connection at this instant and will have one later, which is the transient
    /// <see cref="ErrorCategory.Unavailable"/>. Without the distinction, a caller retrying a send against a dead
    /// client retries forever while <see cref="Error.IsTransient"/> keeps telling it to try again.
    /// </remarks>
    internal static Error NotConnected(WebSocketClientState state) =>
        new Error(
            WebSocketErrorCodes.NotConnected,
            Inv($"目前狀態為 {state},沒有可用的連線。No usable connection: the client is currently {state}."),
            state == WebSocketClientState.Closed ? ErrorCategory.Exhausted : ErrorCategory.Unavailable)
            .WithData(DataKeys.State, Inv($"{state}"));

    internal static Error SendFailed(Exception exception) =>
        Error.FromException(exception, WebSocketErrorCodes.SendFailed, ErrorCategory.Network);

    internal static Error SubscriptionReplayFailed(string subscriptionId, Error inner) =>
        new Error(
            WebSocketErrorCodes.SubscriptionReplayFailed,
            Inv($"重連後重放訂閱 {subscriptionId} 失敗:{inner.Message}。Replaying subscription {subscriptionId} after reconnect failed: {inner.Message}."),
            ErrorCategory.Network)
        {
            Exception = inner.Exception,
        }
            .WithData(DataKeys.SubscriptionId, subscriptionId)
            .WithData(DataKeys.InnerCode, inner.Code);

    internal static Error SubscriptionNotFound(string subscriptionId) =>
        Error.NotFound(
            WebSocketErrorCodes.SubscriptionNotFound,
            Inv($"找不到訂閱 {subscriptionId}。No subscription with id {subscriptionId}."))
            .WithData(DataKeys.SubscriptionId, subscriptionId);

    internal static Error MessageTooLarge(int limit) =>
        Error.Network(
            WebSocketErrorCodes.MessageTooLarge,
            Inv($"單則訊息超過 {limit} 位元組的上限,連線已放棄。A single message exceeded the {limit}-byte limit; the connection was abandoned."))
            .WithData(DataKeys.LimitBytes, (long)limit);

    internal static Error InvalidState(WebSocketClientState state, string operation) =>
        Error.Conflict(
            WebSocketErrorCodes.InvalidState,
            Inv($"目前狀態為 {state},不允許執行 {operation}。The client is {state}, which does not allow {operation}."))
            .WithData(DataKeys.State, Inv($"{state}"))
            .WithData(DataKeys.Operation, operation);

    internal static Error Cancelled(string operation) =>
        new Error(
            WebSocketErrorCodes.Cancelled,
            Inv($"{operation} 已被呼叫端取消。{operation} was cancelled by the caller."),
            ErrorCategory.Cancelled)
            .WithData(DataKeys.Operation, operation);
}
