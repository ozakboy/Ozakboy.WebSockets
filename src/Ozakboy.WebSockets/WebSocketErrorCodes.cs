namespace Ozakboy.WebSockets;

/// <summary>
/// 本套件產生的錯誤代碼。上層要依錯誤分支時請比對這些常數,不要比對訊息字串。
/// The error codes this package produces. Branch on these constants rather than on message text.
/// </summary>
/// <remarks>
/// 訊息是給人看的、隨時可能被改寫;代碼是契約的一部分,不會在同一個主版本內變動。
/// Messages are for people and get rewritten; codes are part of the contract and do not change within a major
/// version.
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
    public const string IdleTimeout = "ws.idle_timeout";

    /// <summary>
    /// 重連次數用盡,客戶端已停止。分類為 <see cref="Core.Abstractions.ErrorCategory.Unavailable"/>。
    /// The reconnect attempts ran out and the client has stopped. Categorised as
    /// <see cref="Core.Abstractions.ErrorCategory.Unavailable"/>.
    /// </summary>
    public const string ReconnectExhausted = "ws.reconnect_exhausted";

    /// <summary>
    /// 目前沒有連線,無法送出。分類為 <see cref="Core.Abstractions.ErrorCategory.Unavailable"/>(暫時性)。
    /// Not currently connected, so nothing can be sent. Categorised as
    /// <see cref="Core.Abstractions.ErrorCategory.Unavailable"/>, which is transient.
    /// </summary>
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
    public const string SubscriptionReplayFailed = "ws.subscription_replay_failed";

    /// <summary>
    /// 找不到要取消的訂閱。分類為 <see cref="Core.Abstractions.ErrorCategory.NotFound"/>。
    /// The subscription to remove does not exist. Categorised as
    /// <see cref="Core.Abstractions.ErrorCategory.NotFound"/>.
    /// </summary>
    public const string SubscriptionNotFound = "ws.subscription_not_found";

    /// <summary>
    /// 單則訊息超過 <see cref="WebSocketClientOptions.MaxMessageSize"/>。
    /// 分類為 <see cref="Core.Abstractions.ErrorCategory.Network"/>(暫時性)。
    /// One message exceeded <see cref="WebSocketClientOptions.MaxMessageSize"/>. Categorised as
    /// <see cref="Core.Abstractions.ErrorCategory.Network"/>, which is transient.
    /// </summary>
    public const string MessageTooLarge = "ws.message_too_large";

    /// <summary>
    /// 目前的生命週期狀態不允許這個操作(例如已關閉後再呼叫連線)。
    /// 分類為 <see cref="Core.Abstractions.ErrorCategory.Conflict"/>。
    /// The current lifecycle state does not allow this operation, such as connecting after the client was closed.
    /// Categorised as <see cref="Core.Abstractions.ErrorCategory.Conflict"/>.
    /// </summary>
    public const string InvalidState = "ws.invalid_state";

    /// <summary>
    /// 操作被呼叫端取消。分類為 <see cref="Core.Abstractions.ErrorCategory.Cancelled"/>。
    /// The operation was cancelled by the caller. Categorised as
    /// <see cref="Core.Abstractions.ErrorCategory.Cancelled"/>.
    /// </summary>
    public const string Cancelled = "ws.cancelled";
}
