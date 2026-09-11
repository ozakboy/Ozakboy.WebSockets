namespace Ozakboy.WebSockets;

/// <summary>
/// 有訊息因為佇列已滿而被丟棄的事件資料。
/// The payload raised when a message is dropped because the queue was full.
/// </summary>
/// <remarks>
/// 這個事件存在的唯一理由是不讓資料靜默消失。交易系統若因為佇列滿而漏掉一根 K 線,
/// 而且沒有任何地方留下痕跡,策略會拿錯誤的資料下單,事後也查不出原因。
/// This event exists solely so that data never disappears silently. If a trading system misses a candle because the
/// queue overflowed and nothing anywhere records it, the strategy trades on wrong data and the cause is
/// unrecoverable afterwards.
/// </remarks>
public sealed class WebSocketMessageDroppedEventArgs : EventArgs
{
    /// <summary>
    /// 建立事件資料。
    /// Creates the event payload.
    /// </summary>
    /// <param name="message">被丟棄的訊息。The dropped message.</param>
    /// <param name="strategy">當時採用的背壓策略。The backpressure strategy in force.</param>
    /// <param name="totalDropped">
    /// 含這一則在內,累計被丟棄的則數。
    /// The running total of dropped messages, including this one.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="message"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    public WebSocketMessageDroppedEventArgs(WebSocketMessage message, BackpressureStrategy strategy, long totalDropped)
    {
        ArgumentNullException.ThrowIfNull(message);

        Message = message;
        Strategy = strategy;
        TotalDropped = totalDropped;
    }

    /// <summary>
    /// 被丟棄的訊息。內容仍然完整,呼叫端可以選擇另外持久化或計數。
    /// The dropped message. The payload is intact, so the caller can persist or count it separately.
    /// </summary>
    public WebSocketMessage Message { get; }

    /// <summary>
    /// 當時採用的背壓策略。
    /// The backpressure strategy in force when the drop happened.
    /// </summary>
    public BackpressureStrategy Strategy { get; }

    /// <summary>
    /// 含這一則在內,累計被丟棄的則數。
    /// The running total of dropped messages, including this one.
    /// </summary>
    public long TotalDropped { get; }
}
