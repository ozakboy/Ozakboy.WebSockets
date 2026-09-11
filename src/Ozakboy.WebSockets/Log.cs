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
    /// <summary>
    /// 記錄連線成功。<paramref name="endpoint"/> <b>只能是 scheme 與主機</b>,不可包含路徑或查詢字串。
    /// Records a successful connection. <paramref name="endpoint"/> <b>must be scheme and host only</b>, never
    /// the path or query.
    /// </summary>
    /// <remarks>
    /// 路徑會夾帶憑證。幣安的使用者資料串流就是 <c>wss://…/ws/&lt;listenKey&gt;</c> —— 那把 listenKey 能連上
    /// 帳戶的私有資料,而這行日誌在 Information 層級、每次重連都寫一次;一天重連上百次,日誌裡就有上百份
    /// 可用的憑證,而日誌通常不加密、會被複製、會被貼給別人看。要知道連到哪個主機,authority 就夠了。
    /// A path can carry a credential. A Binance user data stream is literally <c>wss://…/ws/&lt;listenKey&gt;</c>,
    /// and that key opens the account's private data — while this line is written at Information level on every
    /// reconnect. A few hundred reconnects a day puts a few hundred usable credentials into a log that is
    /// typically unencrypted, copied around, and pasted to other people. The authority alone answers the only
    /// question this line is asked: which host.
    /// </remarks>
    /// <param name="logger">記錄器。The logger.</param>
    /// <param name="endpoint">連線目標的 scheme 與主機。The scheme and host connected to.</param>
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "WebSocket 已連線。WebSocket connected to {Endpoint}.")]
    internal static partial void Connected(ILogger logger, string endpoint);

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
