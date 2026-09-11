namespace Ozakboy.WebSockets;

/// <summary>
/// 本套件產生的錯誤代碼。上層要依錯誤分支時請比對這些常數,不要比對訊息字串。
/// The error codes this package produces. Branch on these constants rather than on message text.
/// </summary>
/// <remarks>
/// <para>
/// 訊息是給人看的、隨時可能被改寫;代碼是契約的一部分,不會在同一個主版本內變動。
/// Messages are for people and get rewritten; codes are part of the contract and do not change within a major
/// version.
/// </para>
/// <para>
/// 訊息裡出現的數值(重連次數、逾時長度、大小上限、訂閱識別碼)同時放在
/// <see cref="Core.Abstractions.Error.Data"/> 裡,用
/// <see cref="Core.Abstractions.Error.TryGetInt64(string, out long)"/> 或
/// <see cref="Core.Abstractions.Error.TryGetData(string, out string)"/> 讀回來即可,不必剖析訊息字串。
/// 各代碼帶哪些鍵見下方各自的說明。
/// The numbers that appear in the message — reconnect attempts, timeout lengths, size limits, subscription ids —
/// are also in <see cref="Core.Abstractions.Error.Data"/>. Read them back with
/// <see cref="Core.Abstractions.Error.TryGetInt64(string, out long)"/> or
/// <see cref="Core.Abstractions.Error.TryGetData(string, out string)"/> rather than parsing the text; the keys each
/// code carries are listed with it below.
/// </para>
/// </remarks>
public static class WebSocketErrorCodes
{
    /// <summary>
    /// 設定不合法。分類為 <see cref="Core.Abstractions.ErrorCategory.Validation"/>。
    /// The configuration is invalid. Categorised as <see cref="Core.Abstractions.ErrorCategory.Validation"/>.
    /// </summary>
    public const string OptionsInvalid = "ws.options_invalid";

    /// <summary>
    /// 握手失敗或連線被拒。分類為 <see cref="Core.Abstractions.ErrorCategory.Network"/>(暫時性)。
    /// The handshake failed or the connection was refused. Categorised as
    /// <see cref="Core.Abstractions.ErrorCategory.Network"/>, which is transient.
    /// </summary>
    public const string ConnectFailed = "ws.connect_failed";

    /// <summary>
    /// 握手在 <see cref="WebSocketClientOptions.ConnectTimeout"/> 內未完成。
    /// 分類為 <see cref="Core.Abstractions.ErrorCategory.Timeout"/>(暫時性)。
    /// The handshake did not complete within <see cref="WebSocketClientOptions.ConnectTimeout"/>. Categorised as
    /// <see cref="Core.Abstractions.ErrorCategory.Timeout"/>, which is transient.
    /// </summary>
    /// <remarks>
    /// 資料鍵:<c>timeoutMs</c>。Data key: <c>timeoutMs</c>.
    /// </remarks>
    public const string ConnectTimeout = "ws.connect_timeout";

    /// <summary>
    /// 連線中斷。分類為 <see cref="Core.Abstractions.ErrorCategory.Network"/>(暫時性)。
    /// The connection dropped. Categorised as <see cref="Core.Abstractions.ErrorCategory.Network"/>, which is
    /// transient.
    /// </summary>
    public const string ConnectionLost = "ws.connection_lost";

    /// <summary>
    /// 超過 <see cref="WebSocketClientOptions.IdleTimeout"/> 沒有收到任何訊息,連線被判定已死。
    /// 分類為 <see cref="Core.Abstractions.ErrorCategory.Timeout"/>(暫時性)。
    /// Nothing arrived within <see cref="WebSocketClientOptions.IdleTimeout"/> and the connection was declared dead.
    /// Categorised as <see cref="Core.Abstractions.ErrorCategory.Timeout"/>, which is transient.
    /// </summary>
    /// <remarks>
    /// 資料鍵:<c>timeoutMs</c>。Data key: <c>timeoutMs</c>.
    /// </remarks>
    public const string IdleTimeout = "ws.idle_timeout";

    /// <summary>
    /// 重連次數用盡,客戶端已停止。分類為 <see cref="Core.Abstractions.ErrorCategory.Exhausted"/>(非暫時性),
    /// 因此 <see cref="Core.Abstractions.Error.IsTransient"/> 為 <see langword="false"/>。
    /// The reconnect attempts ran out and the client has stopped. Categorised as
    /// <see cref="Core.Abstractions.ErrorCategory.Exhausted"/>, which is not transient, so
    /// <see cref="Core.Abstractions.Error.IsTransient"/> is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// 這個客戶端物件已經結束了,重試要換一個新的。附帶的 <see cref="Core.Abstractions.Error.Data"/> 含
    /// <c>attempts</c>(放棄前實際嘗試了幾次)。
    /// This client instance is finished; retrying means constructing a new one. The accompanying
    /// <see cref="Core.Abstractions.Error.Data"/> carries <c>attempts</c>, the number of attempts made before
    /// giving up.
    /// </remarks>
    public const string ReconnectExhausted = "ws.reconnect_exhausted";

    /// <summary>
    /// 目前沒有連線,無法送出。分類跟著狀態走:客戶端已關閉時為
    /// <see cref="Core.Abstractions.ErrorCategory.Exhausted"/>(非暫時性),其餘狀態為
    /// <see cref="Core.Abstractions.ErrorCategory.Unavailable"/>(暫時性)。
    /// Not currently connected, so nothing can be sent. The category follows the state: a closed client yields
    /// <see cref="Core.Abstractions.ErrorCategory.Exhausted"/>, which is not transient, and any other state yields
    /// <see cref="Core.Abstractions.ErrorCategory.Unavailable"/>, which is.
    /// </summary>
    /// <remarks>
    /// 重連中只是此刻沒有連線,稍後就會有,重試是對的;已關閉的客戶端則永遠不會再連上,重試沒有意義。
    /// 附帶的 <see cref="Core.Abstractions.Error.Data"/> 含 <c>state</c>。
    /// While reconnecting there is simply no connection at this instant and there will be one shortly, so retrying is
    /// right; a closed client will never connect again and retrying is pointless. The accompanying
    /// <see cref="Core.Abstractions.Error.Data"/> carries <c>state</c>.
    /// </remarks>
    public const string NotConnected = "ws.not_connected";

    /// <summary>
    /// 送出失敗。分類為 <see cref="Core.Abstractions.ErrorCategory.Network"/>(暫時性)。
    /// The send failed. Categorised as <see cref="Core.Abstractions.ErrorCategory.Network"/>, which is transient.
    /// </summary>
    public const string SendFailed = "ws.send_failed";

    /// <summary>
    /// 訂閱重放失敗,這條連線會被放棄並重連。分類為 <see cref="Core.Abstractions.ErrorCategory.Network"/>。
    /// Replaying subscriptions failed; the connection is abandoned and retried. Categorised as
    /// <see cref="Core.Abstractions.ErrorCategory.Network"/>.
    /// </summary>
    /// <remarks>
    /// 資料鍵:<c>subscriptionId</c>、<c>innerCode</c>(內層傳送失敗的代碼)。
    /// Data keys: <c>subscriptionId</c> and <c>innerCode</c>, the code of the underlying send failure.
    /// </remarks>
    public const string SubscriptionReplayFailed = "ws.subscription_replay_failed";

    /// <summary>
    /// 找不到要取消的訂閱。分類為 <see cref="Core.Abstractions.ErrorCategory.NotFound"/>。
    /// The subscription to remove does not exist. Categorised as
    /// <see cref="Core.Abstractions.ErrorCategory.NotFound"/>.
    /// </summary>
    /// <remarks>
    /// 資料鍵:<c>subscriptionId</c>。Data key: <c>subscriptionId</c>.
    /// </remarks>
    public const string SubscriptionNotFound = "ws.subscription_not_found";

    /// <summary>
    /// 單則訊息超過 <see cref="WebSocketClientOptions.MaxMessageSize"/>。
    /// 分類為 <see cref="Core.Abstractions.ErrorCategory.Network"/>(暫時性)。
    /// One message exceeded <see cref="WebSocketClientOptions.MaxMessageSize"/>. Categorised as
    /// <see cref="Core.Abstractions.ErrorCategory.Network"/>, which is transient.
    /// </summary>
    /// <remarks>
    /// 資料鍵:<c>limitBytes</c>。Data key: <c>limitBytes</c>.
    /// </remarks>
    public const string MessageTooLarge = "ws.message_too_large";

    /// <summary>
    /// 目前的生命週期狀態不允許這個操作(例如已關閉後再呼叫連線)。
    /// 分類為 <see cref="Core.Abstractions.ErrorCategory.Conflict"/>。
    /// The current lifecycle state does not allow this operation, such as connecting after the client was closed.
    /// Categorised as <see cref="Core.Abstractions.ErrorCategory.Conflict"/>.
    /// </summary>
    /// <remarks>
    /// 資料鍵:<c>state</c>、<c>operation</c>。Data keys: <c>state</c> and <c>operation</c>.
    /// </remarks>
    public const string InvalidState = "ws.invalid_state";

    /// <summary>
    /// 操作被呼叫端取消。分類為 <see cref="Core.Abstractions.ErrorCategory.Cancelled"/>。
    /// The operation was cancelled by the caller. Categorised as
    /// <see cref="Core.Abstractions.ErrorCategory.Cancelled"/>.
    /// </summary>
    /// <remarks>
    /// 資料鍵:<c>operation</c>。Data key: <c>operation</c>.
    /// </remarks>
    public const string Cancelled = "ws.cancelled";
}
