namespace Ozakboy.WebSockets;

/// <summary>
/// 客戶端執行狀況的即時快照。
/// A point-in-time snapshot of how the client is doing.
/// </summary>
/// <remarks>
/// 每次讀取 <see cref="IWebSocketClient.Statistics"/> 都會取得一份新的快照,因此各欄位彼此一致,
/// 可以直接拿去計算比率而不必擔心中途被改動。
/// Every read of <see cref="IWebSocketClient.Statistics"/> produces a fresh snapshot, so the fields are consistent
/// with each other and safe to use in ratios without something changing underneath.
/// </remarks>
public sealed record WebSocketClientStatistics
{
    /// <summary>
    /// 目前的生命週期狀態。
    /// The current lifecycle state.
    /// </summary>
    public required WebSocketClientState State { get; init; }

    /// <summary>
    /// 成功交給上層的訊息則數(不含被丟棄的)。
    /// The number of messages handed to the caller, excluding dropped ones.
    /// </summary>
    public required long MessagesReceived { get; init; }

    /// <summary>
    /// 因為佇列已滿而被丟棄的訊息則數。
    /// The number of messages dropped because the queue was full.
    /// </summary>
    /// <remarks>
    /// 這個數字只要不是零就值得看一眼:代表下游處理速度跟不上,而且已經有資料沒有被處理到。
    /// Any non-zero value deserves attention: the consumer is slower than the source, and data has already gone
    /// unprocessed.
    /// </remarks>
    public required long MessagesDropped { get; init; }

    /// <summary>
    /// 送出的訊息則數(含訂閱與重放)。
    /// The number of messages sent, including subscribes and replays.
    /// </summary>
    public required long MessagesSent { get; init; }

    /// <summary>
    /// 收到的位元組總量。
    /// The total number of bytes received.
    /// </summary>
    public required long BytesReceived { get; init; }

    /// <summary>
    /// 從建構到現在重連過幾次。
    /// How many reconnects have happened since construction.
    /// </summary>
    public required int ReconnectCount { get; init; }

    /// <summary>
    /// 重放訂閱的次數:每次連線成功且確實有訂閱可重放時加一,不是每筆訂閱加一。
    /// How many times subscriptions were replayed: one per successful connection that actually had something to
    /// replay, not one per subscription.
    /// </summary>
    /// <remarks>
    /// 這個數字應該跟著 <see cref="ReconnectCount"/> 一起成長。重連次數在增加而它停著不動,
    /// 就是「連上了但沒重新訂閱」那種靜默故障的樣子。
    /// This should grow alongside <see cref="ReconnectCount"/>. Reconnects climbing while this stays put is what the
    /// silent "connected but never resubscribed" failure looks like.
    /// </remarks>
    public required int SubscriptionReplayCount { get; init; }

    /// <summary>
    /// 目前連續失敗的連線嘗試次數;連上之後歸零。
    /// The number of consecutive failed connection attempts; reset to zero once connected.
    /// </summary>
    public required int ConsecutiveFailedAttempts { get; init; }

    /// <summary>
    /// 目前登記中的訂閱筆數。
    /// The number of subscriptions currently registered.
    /// </summary>
    public required int SubscriptionCount { get; init; }

    /// <summary>
    /// 接收佇列中尚未被取走的訊息則數。
    /// The number of messages sitting in the receive queue.
    /// </summary>
    /// <remarks>
    /// 持續接近 <see cref="WebSocketClientOptions.QueueCapacity"/> 表示快要開始丟訊息了,是丟棄發生之前
    /// 唯一的預警訊號。
    /// Sitting persistently close to <see cref="WebSocketClientOptions.QueueCapacity"/> means drops are imminent;
    /// it is the only early warning available before data starts going missing.
    /// </remarks>
    public required int QueuedMessageCount { get; init; }

    /// <summary>
    /// 目前這條連線建立的時間;未連線時為 <see langword="null"/>。
    /// When the current connection was established, or <see langword="null"/> when not connected.
    /// </summary>
    public required DateTimeOffset? ConnectedAt { get; init; }

    /// <summary>
    /// 最後一次收到訊息的時間;還沒收到過任何訊息時為 <see langword="null"/>。
    /// When the last message arrived, or <see langword="null"/> when nothing has arrived yet.
    /// </summary>
    /// <remarks>
    /// 這個時間戳就是閒置逾時偵測所依據的值。連線狀態正常但這個時間一直不動,正是那種
    /// 「握手成功但資料流被吃掉」的故障的樣子。
    /// This timestamp is what the idle-timeout detector works from. A healthy-looking connection whose last-message
    /// time never advances is exactly what "handshake succeeded, data stream swallowed" looks like.
    /// </remarks>
    public required DateTimeOffset? LastMessageAt { get; init; }

    /// <summary>
    /// 最後一次斷線的時間;從未斷線時為 <see langword="null"/>。
    /// When the connection was last lost, or <see langword="null"/> when it never has been.
    /// </summary>
    public required DateTimeOffset? LastDisconnectedAt { get; init; }
}
