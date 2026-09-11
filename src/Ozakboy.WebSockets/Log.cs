using Microsoft.Extensions.Logging;

namespace Ozakboy.WebSockets;

/// <summary>
/// 以來源產生器產生的日誌方法。
/// Logging methods produced by the source generator.
/// </summary>
/// <remarks>
/// 用 <see cref="LoggerMessageAttribute"/> 而不是 <c>ILogger.LogInformation</c> 這類擴充方法,是因為後者
/// 每次呼叫都會裝箱參數並配置字串,即使該層級根本沒有啟用。行情客戶端一天可能重連上百次、送出上萬則
/// 訂閱訊息,這個差別會實際累積。
/// Uses <see cref="LoggerMessageAttribute"/> rather than the <c>ILogger.LogInformation</c> extensions, which box
/// their arguments and allocate a string on every call even when the level is disabled. A market-data client can
/// reconnect hundreds of times a day and send tens of thousands of messages, so the difference adds up.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "WebSocket 已連線。WebSocket connected to {Endpoint}.")]
    internal static partial void Connected(ILogger logger, Uri endpoint);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "WebSocket 連線失敗(第 {Attempt} 次):{Reason}。WebSocket connection attempt failed.")]
    internal static partial void ConnectFailed(ILogger logger, int attempt, string reason);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "WebSocket 連線中斷:{Reason}。WebSocket connection lost.")]
    internal static partial void ConnectionLost(ILogger logger, string reason);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "等待 {Delay} 後重連。Waiting before the next reconnect attempt.")]
    internal static partial void ReconnectDelay(ILogger logger, TimeSpan delay);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error, Message = "重連 {Attempts} 次後放棄,資料流已停止。Gave up reconnecting; the data stream has stopped.")]
    internal static partial void ReconnectExhausted(ILogger logger, int attempts);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning, Message = "超過 {Timeout} 未收到任何訊息,連線判定已死並中止。No message within the idle timeout; aborting the connection.")]
    internal static partial void IdleTimeout(ILogger logger, TimeSpan timeout);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Information, Message = "重連後已重放 {Count} 筆訂閱。Replayed subscriptions after reconnect.")]
    internal static partial void SubscriptionsReplayed(ILogger logger, int count);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Error, Message = "重放訂閱 {SubscriptionId} 失敗,放棄這條連線並重連。Replaying a subscription failed; abandoning the connection.")]
    internal static partial void SubscriptionReplayFailed(ILogger logger, string subscriptionId);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Warning, Message = "接收佇列已滿,依 {Strategy} 策略丟棄訊息,累計 {TotalDropped} 則。The receive queue is full and a message was dropped.")]
    internal static partial void MessageDropped(ILogger logger, BackpressureStrategy strategy, long totalDropped);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Error, Message = "接收佇列已滿,連中斷通知都被擠掉了,上層將看不到這次缺口。The queue was so full that a failure notice was evicted; the caller will not see this gap.")]
    internal static partial void FailureNoticeDropped(ILogger logger);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Warning, Message = "應用層 ping 送出失敗:{Reason}。Sending the application-level ping failed.")]
    internal static partial void ApplicationPingFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Error, Message = "事件處理常式擲出例外,已忽略以免中斷客戶端。An event handler threw; swallowed so the client keeps running.")]
    internal static partial void EventHandlerFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Information, Message = "WebSocket 已優雅關閉。The WebSocket was closed gracefully.")]
    internal static partial void ClosedGracefully(ILogger logger);

    [LoggerMessage(EventId = 1013, Level = LogLevel.Warning, Message = "等待對方回覆關閉 frame 超過 {Timeout},改以中止收尾。The peer did not answer the close frame in time; aborting instead.")]
    internal static partial void CloseTimedOut(ILogger logger, TimeSpan timeout);

    [LoggerMessage(EventId = 1014, Level = LogLevel.Error, Message = "連線失敗且重試不可能改變結果({Reason}),不再重連,資料流已停止。A non-transient connect failure; not retrying, and the data stream has stopped.")]
    internal static partial void Unrecoverable(ILogger logger, string reason);
}
