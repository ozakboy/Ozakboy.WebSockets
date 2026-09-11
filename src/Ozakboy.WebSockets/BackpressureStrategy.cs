namespace Ozakboy.WebSockets;

/// <summary>
/// 接收佇列滿了的時候怎麼辦。
/// What to do when the receive queue is full.
/// </summary>
/// <remarks>
/// <para>
/// 長時間執行的程式一旦下游處理速度跟不上來源,訊息就會無限堆積到記憶體耗盡,所以佇列一律是有界的,
/// 而「滿了怎麼辦」必須由呼叫端自己決定 —— 這三種選擇在交易系統裡各有正確的場合,沒有通用的最佳解。
/// In a long-running process, a consumer slower than the source piles messages up until memory runs out, so the
/// queue is always bounded and the caller must choose what happens when it fills. All three options are correct in
/// some trading context; there is no universally best answer.
/// </para>
/// <para>
/// 無論選哪一種,被丟棄的訊息都會經由 <see cref="IWebSocketClient.MessageDropped"/> 事件與
/// <see cref="WebSocketClientStatistics.MessagesDropped"/> 計數呈現。靜默丟資料是最糟的失敗模式:
/// 策略少收一根 K 線卻沒人知道,就會拿錯誤的資料下單。
/// Whichever is chosen, dropped messages surface through the <see cref="IWebSocketClient.MessageDropped"/> event and
/// the <see cref="WebSocketClientStatistics.MessagesDropped"/> counter. Silently losing data is the worst failure
/// mode there is: a strategy that quietly misses a candle will trade on wrong data.
/// </para>
/// </remarks>
public enum BackpressureStrategy
{
    /// <summary>
    /// 丟掉佇列中最舊的一則,騰出空間給新訊息。預設值。
    /// Drops the oldest queued message to make room for the new one. The default.
    /// </summary>
    /// <remarks>
    /// 適合行情這類「最新的最有價值」的串流:落後太多的舊報價就算處理完也已經沒有意義。
    /// The right choice for market data, where the newest value is the useful one: a quote that is already far
    /// behind has no value even once it is processed.
    /// </remarks>
    DropOldest = 0,

    /// <summary>
    /// 丟掉剛收到的那一則,保留佇列中已有的訊息。
    /// Drops the newly arrived message and keeps what is already queued.
    /// </summary>
    /// <remarks>
    /// 適合必須依序處理、且舊訊息不能跳過的串流(例如逐筆成交回報)。
    /// The right choice for streams that must be processed in order and cannot skip older entries, such as a
    /// per-fill execution feed.
    /// </remarks>
    DropNewest = 1,

    /// <summary>
    /// 不丟棄,阻塞接收迴圈直到佇列有空位。
    /// Does not drop anything; blocks the receive loop until space frees up.
    /// </summary>
    /// <remarks>
    /// 一則都不能少的時候用它,但要清楚代價:接收迴圈停住之後,對方的傳送緩衝區與 TCP 視窗會跟著填滿,
    /// 最終多半是被對方斷線;而閒置逾時偵測也會因為長時間沒有新訊息而誤判連線已死。
    /// 換句話說,這個選項把「丟訊息」換成了「丟連線」,只有在下游只是偶爾慢一下時才適用。
    /// Use it when nothing may be lost, but understand the cost: once the receive loop stalls, the peer's send
    /// buffer and the TCP window fill up and the peer usually drops the connection; the idle-timeout detector will
    /// also eventually conclude the connection is dead because nothing new is arriving. This option trades losing
    /// messages for losing the connection, and only suits a consumer that is occasionally, briefly slow.
    /// </remarks>
    Wait = 2,
}
