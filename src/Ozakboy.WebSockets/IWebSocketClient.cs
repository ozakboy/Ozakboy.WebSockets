using Ozakboy.Core.Abstractions;

namespace Ozakboy.WebSockets;

/// <summary>
/// 可以連續執行數天到數週的 WebSocket 客戶端:自動重連、重連後重放訂閱、閒置逾時存活偵測、
/// 有界佇列背壓。
/// A WebSocket client meant to stay up for days or weeks: automatic reconnect, subscription replay after
/// reconnect, idle-timeout liveness detection, and bounded-queue backpressure.
/// </summary>
/// <remarks>
/// <para>
/// 所有會失敗的操作都回傳 <see cref="Result"/>,不以例外表達預期失敗。斷線、逾時、被拒絕連線在
/// 長時間執行的程式裡不是例外狀況而是日常,用例外表達會逼呼叫端把控制流寫進 <c>try/catch</c>。
/// Every fallible operation returns a <see cref="Result"/> instead of throwing: disconnects, timeouts, and refused
/// connections are routine in a long-running process, not exceptional, and modelling them as exceptions forces
/// control flow into <c>try/catch</c>.
/// </para>
/// <para>
/// 實作必須是執行緒安全的:訂閱、傳送、查詢狀態可以在任何時間從任何執行緒發生。
/// 唯一的例外是 <see cref="Messages"/>,它是單一消費者的串流。
/// Implementations must be thread-safe: subscribing, sending, and reading state can happen from any thread at any
/// time. The single exception is <see cref="Messages"/>, which is a single-consumer stream.
/// </para>
/// </remarks>
public interface IWebSocketClient : IAsyncDisposable
{
    /// <summary>
    /// 目前的生命週期狀態。
    /// The current lifecycle state.
    /// </summary>
    WebSocketClientState State { get; }

    /// <summary>
    /// 客戶端為什麼關閉;尚未關閉時為 <see cref="WebSocketCloseReason.None"/>。
    /// Why the client closed, or <see cref="WebSocketCloseReason.None"/> while it is still running.
    /// </summary>
    /// <remarks>
    /// 這個屬性用來區分「呼叫端主動關閉」與「重連用盡而放棄」——兩者的狀態同樣是
    /// <see cref="WebSocketClientState.Closed"/>,但一個是正常關機、一個必須告警。
    /// This distinguishes a caller-initiated shutdown from an exhausted reconnect loop. Both end in
    /// <see cref="WebSocketClientState.Closed"/>, but one is a normal stop and the other must raise an alert.
    /// </remarks>
    WebSocketCloseReason CloseReason { get; }

    /// <summary>
    /// 執行狀況的即時快照。
    /// A point-in-time snapshot of how the client is doing.
    /// </summary>
    WebSocketClientStatistics Statistics { get; }

    /// <summary>
    /// 目前登記中的訂閱,依登記順序排列。這份清單就是重連後會被重放的內容。
    /// The registered subscriptions in registration order. This list is exactly what gets replayed after a reconnect.
    /// </summary>
    IReadOnlyList<WebSocketSubscription> Subscriptions { get; }

    /// <summary>
    /// 生命週期狀態改變時觸發。
    /// Raised when the lifecycle state changes.
    /// </summary>
    /// <remarks>
    /// 事件在客戶端的內部執行緒上同步觸發,處理常式應該盡快返回,也不應該擲出例外
    /// (擲出的例外會被記錄並吞掉,不會讓客戶端停止運作)。
    /// Handlers run synchronously on the client's internal thread, so they should return quickly and must not throw.
    /// An exception from a handler is logged and swallowed rather than allowed to stop the client.
    /// </remarks>
    event EventHandler<WebSocketClientStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 有訊息因為佇列已滿被丟棄時觸發。
    /// Raised when a message is dropped because the queue was full.
    /// </summary>
    /// <remarks>
    /// 訂閱這個事件(或至少定期看 <see cref="WebSocketClientStatistics.MessagesDropped"/>)是必要的:
    /// 沒有人在看的話,資料就是靜默消失。
    /// Subscribing to this — or at least watching <see cref="WebSocketClientStatistics.MessagesDropped"/> — is not
    /// optional: with nobody looking, data simply disappears.
    /// </remarks>
    event EventHandler<WebSocketMessageDroppedEventArgs>? MessageDropped;

    /// <summary>
    /// 連線一次並等待握手完成;成功後由背景迴圈接手,後續斷線會自動重連。
    /// Connects once and waits for the handshake; on success a background loop takes over and reconnects
    /// automatically after any later drop.
    /// </summary>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>
    /// 握手成功時為成功,否則為失敗(代碼見 <see cref="WebSocketErrorCodes"/>)。
    /// Success when the handshake completes; otherwise a failure whose code is one of
    /// <see cref="WebSocketErrorCodes"/>.
    /// </returns>
    /// <remarks>
    /// <b>這個方法只嘗試一次。</b>自動重連處理的是「已經連上之後」的斷線;第一次就連不上屬於啟動期問題,
    /// 呼叫端通常要用不同的方式處理(例如直接讓程序啟動失敗)。若希望啟動時也一路重試到連上為止,
    /// 請改用 <see cref="Start"/>。
    /// <b>This makes a single attempt.</b> Automatic reconnect covers drops after a successful connection; failing on
    /// the very first attempt is a start-up problem that callers usually want to handle differently, such as by
    /// failing the process outright. Use <see cref="Start"/> instead to retry from the start until connected.
    /// </remarks>
    Task<Result> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 啟動背景連線迴圈並立刻返回;第一次連線也套用重連退避,一路重試到連上或次數用盡。
    /// Starts the background connection loop and returns immediately; the first connection also uses the reconnect
    /// backoff and keeps retrying until it succeeds or the attempts run out.
    /// </summary>
    /// <returns>
    /// 成功啟動時為成功;設定不合法或狀態不允許時為失敗。
    /// Success when the loop starts; a failure when the options are invalid or the state does not allow it.
    /// </returns>
    /// <remarks>
    /// 適合 24 小時執行的程序:啟動時對方還沒準備好也無所謂,連上之前先進
    /// <see cref="WebSocketClientState.Reconnecting"/>,連上之後訂閱照樣會被送出。
    /// Suited to a process that runs around the clock: it does not matter that the peer is not ready at start-up —
    /// the client sits in <see cref="WebSocketClientState.Reconnecting"/> until it connects, and subscriptions go out
    /// as soon as it does.
    /// </remarks>
    Result Start();

    /// <summary>
    /// 登記一筆訂閱,並在目前已連線時立刻送出。
    /// Registers a subscription and, when already connected, sends it immediately.
    /// </summary>
    /// <param name="subscription">訂閱描述。The subscription.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>
    /// 已登記(且連線中時已送出)為成功,送出失敗為失敗。
    /// Success once registered — and sent, if connected; a failure when the send fails.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>登記與送出是兩件事。</b>即使送出失敗,或是呼叫時根本還沒連線,訂閱仍然會被登記,
    /// 並在下一次連線建立後隨著重放一起送出。這正是這個套件存在的理由:重連成功卻沒有重放訂閱,
    /// 會得到一條「看起來完全正常但永遠收不到資料」的連線,而那種故障幾乎沒有任何跡象可循。
    /// <b>Registering and sending are separate.</b> The subscription is registered even if the send fails, or if the
    /// client is not connected yet, and goes out with the replay after the next connection is established. This is
    /// precisely why the package exists: a reconnect without a replay leaves a connection that looks perfectly
    /// healthy and never delivers another message, with almost nothing to give the problem away.
    /// </para>
    /// <para>
    /// 以相同的 <see cref="WebSocketSubscription.Id"/> 再次呼叫會取代先前的登記。
    /// Calling again with the same <see cref="WebSocketSubscription.Id"/> replaces the earlier registration.
    /// </para>
    /// </remarks>
    Task<Result> SubscribeAsync(WebSocketSubscription subscription, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取消一筆訂閱:從重放清單中移除,並在已連線且該訂閱有取消訊息時送出。
    /// Removes a subscription from the replay list and, when connected and an unsubscribe payload exists, sends it.
    /// </summary>
    /// <param name="subscriptionId">訂閱識別碼。The subscription identifier.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>
    /// 成功移除為成功;找不到該識別碼時為 <see cref="WebSocketErrorCodes.SubscriptionNotFound"/> 失敗。
    /// Success when removed; a <see cref="WebSocketErrorCodes.SubscriptionNotFound"/> failure when no such id exists.
    /// </returns>
    Task<Result> UnsubscribeAsync(string subscriptionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 送出一則文字訊息。
    /// Sends a text message.
    /// </summary>
    /// <param name="text">要送出的文字。The text to send.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>
    /// 送出成功為成功;未連線時為 <see cref="WebSocketErrorCodes.NotConnected"/> 失敗。
    /// Success when sent; a <see cref="WebSocketErrorCodes.NotConnected"/> failure when there is no connection.
    /// </returns>
    Task<Result> SendAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// 送出一則二進位訊息。
    /// Sends a binary message.
    /// </summary>
    /// <param name="payload">要送出的位元組。The bytes to send.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>
    /// 送出成功為成功;未連線時為 <see cref="WebSocketErrorCodes.NotConnected"/> 失敗。
    /// Success when sent; a <see cref="WebSocketErrorCodes.NotConnected"/> failure when there is no connection.
    /// </returns>
    Task<Result> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取得訊息串流。每則元素要不是一則收到的訊息,就是一次失敗。
    /// The message stream. Each element is either a received message or a failure.
    /// </summary>
    /// <param name="cancellationToken">
    /// 取消權杖。取消只會結束這次列舉,不會關閉連線 —— 要關閉請呼叫 <see cref="CloseAsync"/>。
    /// The cancellation token. Cancelling ends the enumeration only; it does not close the connection, which is what
    /// <see cref="CloseAsync"/> is for.
    /// </param>
    /// <returns>訊息串流。The message stream.</returns>
    /// <remarks>
    /// <para>
    /// 串流中會出現的失敗有兩種。一種是「這裡斷過線,資料可能有缺口」,串流會繼續;另一種是終局失敗
    /// (目前只有 <see cref="WebSocketErrorCodes.ReconnectExhausted"/>),它是串流的最後一個元素,
    /// 之後列舉就結束了。
    /// Two kinds of failure appear in the stream. One says "the connection dropped here, there may be a gap in the
    /// data" and the stream carries on. The other is terminal — currently only
    /// <see cref="WebSocketErrorCodes.ReconnectExhausted"/> — and is the final element before the enumeration ends.
    /// </para>
    /// <para>
    /// <b>要區分這兩種直接看 <see cref="Error.IsTransient"/> 即可。</b>可繼續的缺口通知分類是
    /// <see cref="ErrorCategory.Network"/> 或 <see cref="ErrorCategory.Timeout"/>,<see cref="Error.IsTransient"/>
    /// 為 <see langword="true"/>;終局失敗的分類是 <see cref="ErrorCategory.Exhausted"/>,
    /// <see cref="Error.IsTransient"/> 為 <see langword="false"/>。告警邏輯不需要、也不應該去比對錯誤代碼
    /// —— 一旦有人這樣做,<see cref="Error.IsTransient"/> 就不再是「值不值得重試」的單一真相來源了。
    /// <b>Tell them apart with <see cref="Error.IsTransient"/>.</b> A gap notice is categorised
    /// <see cref="ErrorCategory.Network"/> or <see cref="ErrorCategory.Timeout"/> and reports
    /// <see cref="Error.IsTransient"/> as <see langword="true"/>; the terminal failure is categorised
    /// <see cref="ErrorCategory.Exhausted"/> and reports <see langword="false"/>. Alerting logic neither needs nor
    /// should branch on the error code — once anyone does that, <see cref="Error.IsTransient"/> has stopped being
    /// the single source of truth for "is this worth retrying".
    /// </para>
    /// <para>
    /// 想知道放棄前試了幾次,從 <see cref="Error.Data"/> 的 <c>attempts</c> 讀回來
    /// (<see cref="Error.TryGetInt64(string, out long)"/>),不必剖析訊息字串。
    /// The number of attempts made before giving up comes back from the <c>attempts</c> entry in
    /// <see cref="Error.Data"/> via <see cref="Error.TryGetInt64(string, out long)"/>, with no message parsing.
    /// </para>
    /// <para>
    /// 斷線一律在串流中現身,而不是只寫進日誌,是刻意的設計:只做 <c>await foreach</c> 的呼叫端也必須
    /// 有機會知道資料有缺口。對行情策略而言「少了一段」和「完全沒收到」的處理方式完全不同。
    /// Surfacing every disconnect in-band rather than only in the log is deliberate: a caller that does nothing but
    /// <c>await foreach</c> still has to be able to learn that data is missing. For a strategy, "a gap" and "nothing
    /// at all" call for entirely different responses.
    /// </para>
    /// <para>
    /// <b>只能有一個消費者。</b>底層是單一讀取者的有界佇列,同時列舉兩次會讓訊息被兩邊瓜分。
    /// <b>Single consumer only.</b> The underlying queue has one reader; enumerating twice concurrently splits the
    /// messages between them.
    /// </para>
    /// </remarks>
    IAsyncEnumerable<Result<WebSocketMessage>> Messages(CancellationToken cancellationToken = default);

    /// <summary>
    /// 優雅關閉連線並停止自動重連。
    /// Closes the connection gracefully and stops reconnecting.
    /// </summary>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>
    /// 完成關閉為成功;逾時而改以中止收尾時為失敗,但客戶端無論如何都會進入
    /// <see cref="WebSocketClientState.Closed"/>。
    /// Success when the close completes; a failure when it timed out and had to be aborted instead — either way the
    /// client ends up in <see cref="WebSocketClientState.Closed"/>.
    /// </returns>
    /// <remarks>
    /// 關閉走的是「送出關閉 frame,等對方回覆,讓接收迴圈自己結束」這條路,socket 會停在
    /// <c>Closed</c> 而不是 <c>Aborted</c>。只有等不到對方回覆(超過
    /// <see cref="WebSocketClientOptions.CloseTimeout"/>)才會改用中止。
    /// The close path is: send the close frame, wait for the peer's reply, let the receive loop end by itself. The
    /// socket finishes at <c>Closed</c>, not <c>Aborted</c>. Only when the peer never answers within
    /// <see cref="WebSocketClientOptions.CloseTimeout"/> does the client fall back to aborting.
    /// </remarks>
    Task<Result> CloseAsync(CancellationToken cancellationToken = default);
}
