using Ozakboy.Core.Abstractions;

namespace Ozakboy.WebSockets;

/// <summary>
/// 產生本套件的錯誤,把代碼與分類的對應集中在一個地方。
/// Builds this package's errors, keeping the code-to-category mapping in one place.
/// </summary>
internal static class WebSocketErrors
{
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
            Inv($"WebSocket 握手超過 {timeout.TotalSeconds:0.###} 秒未完成。The WebSocket handshake did not complete within {timeout.TotalSeconds:0.###} s."));

    internal static Error ConnectionLost(string detail) =>
        Error.Network(
            WebSocketErrorCodes.ConnectionLost,
            Inv($"WebSocket 連線中斷:{detail}。The WebSocket connection was lost: {detail}."));

    internal static Error ConnectionLost(Exception exception) =>
        Error.FromException(exception, WebSocketErrorCodes.ConnectionLost, ErrorCategory.Network);

    internal static Error IdleTimeout(TimeSpan timeout) =>
        Error.Timeout(
            WebSocketErrorCodes.IdleTimeout,
            Inv($"超過 {timeout.TotalSeconds:0.###} 秒沒有收到任何訊息,連線判定為已死。No message arrived for {timeout.TotalSeconds:0.###} s; the connection is treated as dead."));

    internal static Error ReconnectExhausted(int attempts) =>
        new(
            WebSocketErrorCodes.ReconnectExhausted,
            Inv($"重連 {attempts} 次後放棄,客戶端已停止。Gave up after {attempts} reconnect attempts; the client has stopped."),
            ErrorCategory.Unavailable);

    internal static Error NotConnected(WebSocketClientState state) =>
        new(
            WebSocketErrorCodes.NotConnected,
            Inv($"目前狀態為 {state},沒有可用的連線。No usable connection: the client is currently {state}."),
            ErrorCategory.Unavailable);

    internal static Error SendFailed(Exception exception) =>
        Error.FromException(exception, WebSocketErrorCodes.SendFailed, ErrorCategory.Network);

    internal static Error SubscriptionReplayFailed(string subscriptionId, Error inner) =>
        new(
            WebSocketErrorCodes.SubscriptionReplayFailed,
            Inv($"重連後重放訂閱 {subscriptionId} 失敗:{inner.Message}。Replaying subscription {subscriptionId} after reconnect failed: {inner.Message}."),
            ErrorCategory.Network)
        {
            Exception = inner.Exception,
        };

    internal static Error SubscriptionNotFound(string subscriptionId) =>
        Error.NotFound(
            WebSocketErrorCodes.SubscriptionNotFound,
            Inv($"找不到訂閱 {subscriptionId}。No subscription with id {subscriptionId}."));

    internal static Error MessageTooLarge(int limit) =>
        Error.Network(
            WebSocketErrorCodes.MessageTooLarge,
            Inv($"單則訊息超過 {limit} 位元組的上限,連線已放棄。A single message exceeded the {limit}-byte limit; the connection was abandoned."));

    internal static Error InvalidState(WebSocketClientState state, string operation) =>
        Error.Conflict(
            WebSocketErrorCodes.InvalidState,
            Inv($"目前狀態為 {state},不允許執行 {operation}。The client is {state}, which does not allow {operation}."));

    internal static Error Cancelled(string operation) =>
        new(
            WebSocketErrorCodes.Cancelled,
            Inv($"{operation} 已被呼叫端取消。{operation} was cancelled by the caller."),
            ErrorCategory.Cancelled);
}
