using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Ozakboy.Core.Abstractions;

namespace Ozakboy.WebSockets;

/// <summary>
/// <see cref="IWebSocketClient"/> 的實作。
/// The <see cref="IWebSocketClient"/> implementation.
/// </summary>
/// <remarks>
/// <para>
/// 設計上的三個關鍵決定,都來自實測而不是文件:
/// Three of the design decisions here came from measurement rather than documentation:
/// </para>
/// <para>
/// <b>一、心跳是「偵測對方」而不是「回應對方」。</b><see cref="ClientWebSocket"/> 會在協定層自動回覆 pong,
/// 應用層看不到那些 frame 也沒有 API 可以介入。所以本類別完全不處理 ping/pong,改用閒置逾時:
/// 超過 <see cref="WebSocketClientOptions.IdleTimeout"/> 沒收到任何訊息就判定連線已死並重連。
/// <b>1. The heartbeat detects the peer rather than answering it.</b> <see cref="ClientWebSocket"/> replies with a
/// pong automatically at the protocol layer, invisible and inaccessible to the application. This class therefore
/// does no ping/pong handling at all and relies on an idle timeout instead: nothing received within
/// <see cref="WebSocketClientOptions.IdleTimeout"/> means the connection is dead and gets replaced.
/// </para>
/// <para>
/// <b>二、接收迴圈永遠不吃取消權杖。</b>用 <see cref="CancellationToken"/> 取消進行中的
/// <c>ReceiveAsync</c>,語意是中止整條連線而不是停止接收 —— socket 會變成
/// <see cref="WebSocketState.Aborted"/>,之後就無法優雅關閉。因此接收一律以
/// <see cref="CancellationToken.None"/> 呼叫,停止的方式是先送出關閉 frame 讓迴圈自己看到對方的關閉
/// frame 而結束;取消權杖只保留給「對方不回應」時的硬逾時。
/// <b>2. The receive loop never takes a cancellation token.</b> Cancelling an in-flight <c>ReceiveAsync</c> means
/// aborting the connection, not stopping the read: the socket becomes <see cref="WebSocketState.Aborted"/> and can
/// never be closed gracefully afterwards. Receives therefore always pass
/// <see cref="CancellationToken.None"/>; stopping works by sending the close frame first and letting the loop end
/// when it sees the peer's reply. Cancellation is reserved for the hard timeout when the peer does not answer.
/// </para>
/// <para>
/// <b>三、「連得上」不等於「收得到」。</b>實測遇過握手成功、socket 維持 <c>Open</c>、200 秒內一個 frame
/// 都沒有進來的情況,沒有例外也沒有斷線。這正是第一點的閒置逾時唯一能抓到、而檢查
/// <see cref="WebSocketState"/> 永遠抓不到的故障。
/// <b>3. Connected is not the same as receiving.</b> We measured a handshake that succeeded, a socket that stayed
/// <c>Open</c>, and 200 seconds without a single frame — no exception, no disconnect. That failure is exactly what
/// the idle timeout in point 1 catches and what inspecting <see cref="WebSocketState"/> never will.
/// </para>
/// </remarks>
public sealed class WebSocketClient : IWebSocketClient
{
    private readonly WebSocketClientOptions _options;
    private readonly IWebSocketConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// 連線目標的 scheme 與主機,連線日誌只寫這個。路徑與查詢字串刻意不留,見 <see cref="Log.Connected"/>。
    /// The scheme and host connected to; the connection log carries only this. The path and query are
    /// deliberately dropped — see <see cref="Log.Connected"/>.
    /// </summary>
    /// <remarks>
    /// 建構時算一次而不是每次連線現算:這個值不會變,而且在日誌呼叫的引數裡算字串,
    /// 即使該層級沒有啟用也會照算(CA1873)。
    /// Computed once rather than per connection: the value never changes, and computing a string inside a
    /// logging call's argument runs even when the level is disabled (CA1873).
    /// </remarks>
    private readonly string _endpointAuthority;

    private readonly Channel<Result<WebSocketMessage>> _queue;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Lock _stateGate = new();
    private readonly Lock _subscriptionGate = new();
    private readonly List<WebSocketSubscription> _subscriptions = [];
    private readonly CancellationTokenSource _shutdownCts = new();

    private IWebSocketConnection? _connection;
    private Task? _runLoop;
    private ITimer? _idleTimer;
    private ITimer? _pingTimer;

    private WebSocketClientState _state = WebSocketClientState.Disconnected;
    private WebSocketCloseReason _closeReason = WebSocketCloseReason.None;
    private volatile bool _closeRequested;
    private int _idleTimedOut;
    private int _started;
    private int _disposed;

    private long _messagesReceived;
    private long _messagesDropped;
    private long _messagesSent;
    private long _bytesReceived;
    private int _reconnectCount;
    private int _subscriptionReplayCount;
    private int _consecutiveFailedAttempts;
    private long _connectedAtTicks;
    private long _lastMessageTicks;
    private long _lastDisconnectedTicks;

    /// <summary>
    /// 以正式環境的 <see cref="ClientWebSocket"/> 連線工廠建立客戶端。
    /// Creates a client backed by the production <see cref="ClientWebSocket"/> factory.
    /// </summary>
    /// <param name="options">設定。The options.</param>
    /// <param name="logger">
    /// 日誌記錄器;省略時不記錄。長時間執行的服務強烈建議提供 —— 重連與丟棄的軌跡只會出現在這裡。
    /// The logger; nothing is logged when omitted. Strongly recommended for a long-running service: the trail of
    /// reconnects and drops exists nowhere else.
    /// </param>
    /// <param name="timeProvider">
    /// 時間來源;省略時使用 <see cref="TimeProvider.System"/>。
    /// The time source; <see cref="TimeProvider.System"/> when omitted.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 設定未通過 <see cref="WebSocketClientOptions.Validate"/> 時擲出。設定錯誤是程式缺陷而不是執行期失敗,
    /// 所以在這裡就擲出,不留到連線時才回傳失敗。
    /// <see cref="Exception.InnerException"/> 是一個
    /// <see cref="Core.Abstractions.ResultException"/>,它的 <see cref="Core.Abstractions.ResultException.Error"/>
    /// 帶著完整的錯誤代碼與分類。
    /// Thrown when the options fail <see cref="WebSocketClientOptions.Validate"/>. A bad configuration is a defect
    /// rather than a runtime failure, so it surfaces here instead of as a failed connection later. The
    /// <see cref="Exception.InnerException"/> is a <see cref="Core.Abstractions.ResultException"/> whose
    /// <see cref="Core.Abstractions.ResultException.Error"/> carries the full code and category.
    /// </exception>
    public WebSocketClient(
        WebSocketClientOptions options,
        ILogger<WebSocketClient>? logger = null,
        TimeProvider? timeProvider = null)
        : this(options, CreateDefaultFactory(options), logger, timeProvider)
    {
    }

    /// <summary>
    /// 以指定的連線工廠建立客戶端。測試時注入假連線工廠即可完全脫離網路。
    /// Creates a client with a supplied connection factory. Injecting a fake factory takes tests entirely off the
    /// network.
    /// </summary>
    /// <param name="options">設定。The options.</param>
    /// <param name="connectionFactory">連線工廠。The connection factory.</param>
    /// <param name="logger">日誌記錄器;省略時不記錄。The logger; nothing is logged when omitted.</param>
    /// <param name="timeProvider">
    /// 時間來源;省略時使用 <see cref="TimeProvider.System"/>。退避等待、閒置逾時、應用層 ping 全部依賴它,
    /// 注入假時鐘就能在不等待真實時間的情況下測試這些行為。
    /// The time source; <see cref="TimeProvider.System"/> when omitted. Backoff waits, the idle timeout, and the
    /// application ping all go through it, so a fake clock exercises those behaviours without real waiting.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> 或 <paramref name="connectionFactory"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="options"/> or <paramref name="connectionFactory"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 設定未通過 <see cref="WebSocketClientOptions.Validate"/> 時擲出;
    /// <see cref="Exception.InnerException"/> 是攜帶完整錯誤的
    /// <see cref="Core.Abstractions.ResultException"/>。
    /// Thrown when the options fail <see cref="WebSocketClientOptions.Validate"/>. The
    /// <see cref="Exception.InnerException"/> is a <see cref="Core.Abstractions.ResultException"/> carrying the full
    /// error.
    /// </exception>
    public WebSocketClient(
        WebSocketClientOptions options,
        IWebSocketConnectionFactory connectionFactory,
        ILogger<WebSocketClient>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connectionFactory);

        var validation = options.Validate();
        if (validation.IsFailure)
        {
            // 建構式的簽章塞不進 Result,這個失敗只能走例外。型別維持 ArgumentException —— 對呼叫端而言
            // 這確實是參數錯誤,改成別的型別只會讓既有的 catch 失效;同時把完整的 Error 包成
            // ResultException 掛在 InnerException 上,想讀代碼與分類的呼叫端就不必去剖析訊息字串。
            // A constructor signature has no room for a Result, so this failure can only travel as an exception. The
            // type stays ArgumentException — from the caller's side this really is a bad argument, and changing it
            // would only break existing catch blocks — while the full Error rides along as a ResultException in
            // InnerException, so callers that want the code and category never have to parse the message.
            throw new ArgumentException(validation.Error.Message, nameof(options), validation.Error.ToException());
        }

        _options = options;
        _connectionFactory = connectionFactory;
        _logger = logger ?? NullLogger<WebSocketClient>.Instance;

        // Validate 已經確認 Uri 有值且為絕對位址,這裡不會是 null。
        // Validate has already established that Uri is set and absolute, so this cannot be null.
        _endpointAuthority = options.Uri!.GetLeftPart(UriPartial.Authority);
        _timeProvider = timeProvider ?? TimeProvider.System;

        _queue = Channel.CreateBounded<Result<WebSocketMessage>>(
            new BoundedChannelOptions(options.QueueCapacity)
            {
                // 用 Wait 是為了讓 TryWrite 在滿的時候誠實回傳 false,好讓本類別自己套用背壓策略。
                // 若交給 DropOldest/DropWrite 等內建模式,訊息會被 Channel 靜默丟掉,呼叫端永遠不會知道。
                // Wait is chosen so that TryWrite honestly returns false when full and this class can apply its own
                // strategy. The built-in drop modes would discard messages silently, which is exactly the failure
                // this package refuses to have.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
    }

    private static ClientWebSocketConnectionFactory CreateDefaultFactory(WebSocketClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ClientWebSocketConnectionFactory(options);
    }

    /// <inheritdoc />
    public WebSocketClientState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public WebSocketCloseReason CloseReason
    {
        get
        {
            lock (_stateGate)
            {
                return _closeReason;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<WebSocketSubscription> Subscriptions
    {
        get
        {
            lock (_subscriptionGate)
            {
                return [.. _subscriptions];
            }
        }
    }

    /// <inheritdoc />
    public WebSocketClientStatistics Statistics
    {
        get
        {
            WebSocketClientState state;
            lock (_stateGate)
            {
                state = _state;
            }

            return new WebSocketClientStatistics
            {
                State = state,
                MessagesReceived = Interlocked.Read(ref _messagesReceived),
                MessagesDropped = Interlocked.Read(ref _messagesDropped),
                MessagesSent = Interlocked.Read(ref _messagesSent),
                BytesReceived = Interlocked.Read(ref _bytesReceived),
                ReconnectCount = Volatile.Read(ref _reconnectCount),
                SubscriptionReplayCount = Volatile.Read(ref _subscriptionReplayCount),
                ConsecutiveFailedAttempts = Volatile.Read(ref _consecutiveFailedAttempts),
                SubscriptionCount = SubscriptionCountSnapshot(),
                QueuedMessageCount = _queue.Reader.Count,
                ConnectedAt = ToTimestamp(Interlocked.Read(ref _connectedAtTicks)),
                LastMessageAt = ToTimestamp(Interlocked.Read(ref _lastMessageTicks)),
                LastDisconnectedAt = ToTimestamp(Interlocked.Read(ref _lastDisconnectedTicks)),
            };
        }
    }

    /// <inheritdoc />
    public event EventHandler<WebSocketClientStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<WebSocketMessageDroppedEventArgs>? MessageDropped;

    /// <inheritdoc />
    public async Task<Result> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var guard = TryBeginStart("ConnectAsync");
        if (guard.IsFailure)
        {
            return guard;
        }

        SetState(WebSocketClientState.Connecting);

        var connectResult = await ConnectOnceAsync(cancellationToken).ConfigureAwait(false);
        if (connectResult.IsFailure)
        {
            // 沒連上就退回未連線,讓呼叫端可以決定要不要再試一次。
            // Failing the first attempt returns to Disconnected so the caller can decide whether to try again.
            Volatile.Write(ref _started, 0);
            SetState(WebSocketClientState.Disconnected, error: connectResult.Error);
            return connectResult;
        }

        SetState(WebSocketClientState.Connected);
        _runLoop = Task.Run(() => RunAsync(startConnected: true), CancellationToken.None);
        return Result.Success();
    }

    /// <inheritdoc />
    public Result Start()
    {
        var guard = TryBeginStart("Start");
        if (guard.IsFailure)
        {
            return guard;
        }

        var validation = _options.Validate();
        if (validation.IsFailure)
        {
            Volatile.Write(ref _started, 0);
            return validation;
        }

        SetState(WebSocketClientState.Connecting);
        _runLoop = Task.Run(() => RunAsync(startConnected: false), CancellationToken.None);
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> SubscribeAsync(WebSocketSubscription subscription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        lock (_subscriptionGate)
        {
            _subscriptions.RemoveAll(existing => string.Equals(existing.Id, subscription.Id, StringComparison.Ordinal));
            _subscriptions.Add(subscription);
        }

        var connection = Volatile.Read(ref _connection);
        if (connection is null || connection.State != WebSocketState.Open)
        {
            // 還沒連上。訂閱已經登記,連上之後會隨重放一起送出,所以這不是失敗。
            // Not connected yet. The subscription is registered and will go out with the replay, so this is not a
            // failure.
            return Result.Success();
        }

        return await SendOnAsync(
            connection,
            Encoding.UTF8.GetBytes(subscription.SubscribePayload),
            WebSocketMessageType.Text,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> UnsubscribeAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        WebSocketSubscription? removed = null;
        lock (_subscriptionGate)
        {
            var index = _subscriptions.FindIndex(existing => string.Equals(existing.Id, subscriptionId, StringComparison.Ordinal));
            if (index >= 0)
            {
                removed = _subscriptions[index];
                _subscriptions.RemoveAt(index);
            }
        }

        if (removed is null)
        {
            return WebSocketErrors.SubscriptionNotFound(subscriptionId);
        }

        if (removed.UnsubscribePayload is null)
        {
            return Result.Success();
        }

        var connection = Volatile.Read(ref _connection);
        if (connection is null || connection.State != WebSocketState.Open)
        {
            // 連線都不在了,取消訊息沒有送出的意義;移出重放清單本身就已經達成目的。
            // With no connection there is nothing to tell; removing it from the replay list already does the job.
            return Result.Success();
        }

        return await SendOnAsync(
            connection,
            Encoding.UTF8.GetBytes(removed.UnsubscribePayload),
            WebSocketMessageType.Text,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> SendAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        return await SendCoreAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
        await SendCoreAsync(payload, WebSocketMessageType.Binary, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async IAsyncEnumerable<Result<WebSocketMessage>> Messages(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var reader = _queue.Reader;

        while (true)
        {
            bool hasMore;
            try
            {
                hasMore = await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消列舉是呼叫端的正常操作,不是故障,所以安靜結束而不是往外丟例外。
                // Ending the enumeration is a normal caller action rather than a fault, so it finishes quietly
                // instead of throwing.
                yield break;
            }

            if (!hasMore)
            {
                yield break;
            }

            while (reader.TryRead(out var item))
            {
                yield return item;
            }
        }
    }

    /// <inheritdoc />
    public async Task<Result> CloseAsync(CancellationToken cancellationToken = default) =>
        await CloseCoreAsync(WebSocketCloseReason.CallerRequested, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 關閉連線並釋放資源。
    /// Closes the connection and releases resources.
    /// </summary>
    /// <returns>代表這次釋放的工作。A task representing the disposal.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        var reason = CloseReason == WebSocketCloseReason.None
            ? WebSocketCloseReason.Disposed
            : CloseReason;

        await CloseCoreAsync(reason, CancellationToken.None).ConfigureAwait(false);

        _shutdownCts.Dispose();
        _sendGate.Dispose();
    }

    // ── 連線與重連迴圈 ────────────────────────────────────────────────
    // ── Connection and reconnect loop ─────────────────────────────────

    /// <summary>
    /// 背景主迴圈:維持連線,斷了就依退避策略重連,直到被關閉、次數用盡,或遇到重試不可能修好的
    /// 非暫時性失敗。
    /// The background loop that keeps the connection up, reconnecting with backoff until it is closed, runs out of
    /// attempts, or hits a non-transient failure that retrying could never fix.
    /// </summary>
    private async Task RunAsync(bool startConnected)
    {
        var token = _shutdownCts.Token;
        var connected = startConnected;
        var isInitialAttempt = !startConnected;
        var failedAttempts = 0;
        var lastEndedWithRemoteClose = false;

        // 迴圈條件刻意不寫成 while(!關閉),而是只在「要不要再連一次」的地方檢查。
        // 若在這裡就擋掉,已經連上的那條連線會連一次接收都沒跑到就被丟棄,關閉 frame 也就沒有人去收 ——
        // socket 會停在 CloseSent 而不是 Closed,優雅關閉等於沒做。
        // The loop condition deliberately checks for shutdown only where a new connection would be made. Checking it
        // here would discard an already-established connection without running the receive loop even once, leaving
        // the peer's close frame unread and the socket stuck at CloseSent instead of Closed.
        while (true)
        {
            if (!connected)
            {
                if (token.IsCancellationRequested || _closeRequested)
                {
                    break;
                }

                if (!isInitialAttempt && IsReconnectCeilingReached(failedAttempts))
                {
                    var reason = lastEndedWithRemoteClose && failedAttempts == 0
                        ? WebSocketCloseReason.RemoteClosed
                        : WebSocketCloseReason.ReconnectAttemptsExhausted;

                    Log.ReconnectExhausted(_logger, Volatile.Read(ref _consecutiveFailedAttempts));
                    FailTerminally(reason, WebSocketErrors.ReconnectExhausted(failedAttempts));
                    return;
                }

                if (failedAttempts > 0)
                {
                    // 退避間隔一律由 RetryPolicy 計算,含抖動;本類別不自己推算,也不自己加隨機數。
                    // The interval always comes from RetryPolicy, jitter included. This class neither derives its own
                    // curve nor adds its own randomness.
                    var delay = _options.ReconnectPolicy.GetDelay(failedAttempts);
                    Log.ReconnectDelay(_logger, delay);

                    try
                    {
                        await Task.Delay(delay, _timeProvider, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                isInitialAttempt = false;
                var connectResult = await ConnectOnceAsync(token).ConfigureAwait(false);
                if (connectResult.IsFailure)
                {
                    failedAttempts++;
                    Volatile.Write(ref _consecutiveFailedAttempts, failedAttempts);
                    Log.ConnectFailed(_logger, failedAttempts, connectResult.Error.Code);

                    // 非暫時性的失敗一次都不再試。重試不可能改變結果 —— 這正是 IsTransient 存在的意義 ——
                    // 而 MaxReconnectAttempts 預設是無限,繼續退避重試就會變成永遠不會結束的空轉:
                    // 不崩潰、不停止、日誌一直在動,看起來像在工作。典型來源是客戶端跑起來之後設定物件
                    // 被改壞,每一次重連都收到同一個 ws.options_invalid。
                    // 這裡刻意依 IsTransient 判斷而不是針對特定代碼特判,新增的非暫時性錯誤才會自動適用。
                    // A non-transient failure is not retried even once: retrying cannot change the outcome — that is
                    // what IsTransient is for — and with MaxReconnectAttempts defaulting to unlimited, backing off
                    // and trying again becomes a spin that never ends: no crash, no stop, a log that keeps moving and
                    // looks like work. The typical source is a configuration object mutated after start-up, which
                    // yields the same ws.options_invalid on every attempt. The decision reads IsTransient rather than
                    // singling out a code, so any non-transient error added later is covered automatically.
                    //
                    // 關閉中是例外:此時的失敗多半是取消(非暫時性分類),那是正常收尾而不是故障,
                    // 要留給下方的關閉路徑處理。
                    // Shutdown is the exception: a failure at that moment is usually a cancellation, which is a
                    // non-transient category but a normal ending rather than a fault, and belongs to the close path
                    // below.
                    if (!connectResult.Error.IsTransient && !token.IsCancellationRequested && !_closeRequested)
                    {
                        Log.Unrecoverable(_logger, connectResult.Error.Code);
                        FailTerminally(
                            WebSocketCloseReason.UnrecoverableError,
                            WebSocketErrors.Unrecoverable(connectResult.Error, failedAttempts));
                        return;
                    }

                    // 只在一連串失敗的第一次送出通知,否則長時間斷線會把佇列灌滿重複的失敗訊息。
                    // Only the first failure of a run is published; otherwise a long outage floods the queue with
                    // identical failures.
                    if (failedAttempts == 1)
                    {
                        PublishFailure(connectResult.Error);
                    }

                    SetState(WebSocketClientState.Reconnecting, error: connectResult.Error);
                    continue;
                }

                connected = true;
                lastEndedWithRemoteClose = false;
                failedAttempts = 0;
                Volatile.Write(ref _consecutiveFailedAttempts, 0);
                SetState(WebSocketClientState.Connected);
            }

            var connection = Volatile.Read(ref _connection);
            var outcome = connection is null
                ? Result.Failure(WebSocketErrors.ConnectionLost("連線在接收開始前就不見了。the connection vanished before the receive loop started"))
                : await ReceiveLoopAsync(connection).ConfigureAwait(false);

            connected = false;
            Interlocked.Exchange(ref _lastDisconnectedTicks, _timeProvider.GetUtcNow().UtcTicks);
            Interlocked.Exchange(ref _connectedAtTicks, 0);
            TeardownConnection();

            if (_closeRequested || token.IsCancellationRequested)
            {
                break;
            }

            Interlocked.Increment(ref _reconnectCount);

            if (outcome.IsFailure)
            {
                lastEndedWithRemoteClose = false;
                Log.ConnectionLost(_logger, outcome.Error.Code);
                PublishFailure(outcome.Error);
            }
            else
            {
                lastEndedWithRemoteClose = true;
                var remoteClosed = WebSocketErrors.ConnectionLost("遠端主動關閉。the remote endpoint closed the connection");
                Log.ConnectionLost(_logger, remoteClosed.Code);
                PublishFailure(remoteClosed);
            }

            SetState(WebSocketClientState.Reconnecting);
        }
    }

    private bool IsReconnectCeilingReached(int failedAttempts) =>
        _options.MaxReconnectAttempts is int max && failedAttempts >= max;

    /// <summary>
    /// 建立一條新連線、完成握手、重放訂閱,並啟動閒置逾時與應用層 ping 的計時器。
    /// Creates a connection, completes the handshake, replays subscriptions, and starts the idle and ping timers.
    /// </summary>
    private async Task<Result> ConnectOnceAsync(CancellationToken cancellationToken)
    {
        var validation = _options.Validate();
        if (validation.IsFailure)
        {
            return validation;
        }

        IWebSocketConnection? connection = null;

        try
        {
            connection = _connectionFactory.Create();

            using var timeoutCts = new CancellationTokenSource(_options.ConnectTimeout, _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await connection.ConnectAsync(_options.Uri!, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                return Discard(ref connection, WebSocketErrors.ConnectTimeout(_options.ConnectTimeout));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Discard(ref connection, WebSocketErrors.Cancelled("ConnectAsync"));
            }

            var now = _timeProvider.GetUtcNow();
            Interlocked.Exchange(ref _connectedAtTicks, now.UtcTicks);
            Interlocked.Exchange(ref _lastMessageTicks, now.UtcTicks);
            Interlocked.Exchange(ref _idleTimedOut, 0);

            // 重放必須在把連線公開出去之前完成 —— 否則呼叫端可能在訂閱還沒送出的空窗期就開始送訊息。
            // The replay finishes before the connection is published, so callers cannot send during the window in
            // which the subscriptions have not gone out yet.
            var replay = await ReplaySubscriptionsAsync(connection, cancellationToken).ConfigureAwait(false);
            if (replay.IsFailure)
            {
                return Discard(ref connection, replay.Error);
            }

            Volatile.Write(ref _connection, connection);
            StartTimers();
            // 只記 scheme 與主機。路徑會夾帶憑證 —— 幣安的使用者資料串流就是 wss://…/ws/<listenKey>,
            // 而這行每次重連都寫一次。見 Log.Connected 的說明。
            // Scheme and host only: the path can carry a credential, and this line is written on every reconnect.
            Log.Connected(_logger, _endpointAuthority);
            connection = null;
            return Result.Success();
        }
#pragma warning disable CA1031 // 連線階段任何未預期的例外都必須轉成失敗結果,否則背景迴圈會整個消失、客戶端安靜停擺。
                              // Any unexpected exception during connect must become a failed Result; letting it escape
                              // would kill the background loop and leave the client silently dead.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return Discard(ref connection, WebSocketErrors.ConnectFailed(exception));
        }
        finally
        {
            connection?.Dispose();
        }
    }

    private static Result Discard(ref IWebSocketConnection? connection, Error error)
    {
        connection?.Dispose();
        connection = null;
        return Result.Failure(error);
    }

    /// <summary>
    /// 把目前登記的訂閱依序重送一次。
    /// Resends every registered subscription in order.
    /// </summary>
    /// <remarks>
    /// 這是整個套件最容易被漏掉、也最難察覺的一步:少了它,重連會成功、狀態會顯示已連線、
    /// 不會有任何錯誤,但資料永遠不會再進來。任何一筆重放失敗都會讓整條連線作廢並重來,
    /// 因為「連上但只訂閱到一半」比「沒連上」更難發現。
    /// This is the step that is easiest to forget and hardest to notice missing: without it the reconnect succeeds,
    /// the status reads connected, no error is raised, and data never arrives again. A single failed replay
    /// invalidates the whole connection and starts over, because "connected with half the subscriptions" is harder
    /// to spot than "not connected".
    /// </remarks>
    private async Task<Result> ReplaySubscriptionsAsync(IWebSocketConnection connection, CancellationToken cancellationToken)
    {
        WebSocketSubscription[] pending;
        lock (_subscriptionGate)
        {
            pending = [.. _subscriptions];
        }

        if (pending.Length == 0)
        {
            return Result.Success();
        }

        foreach (var subscription in pending)
        {
            var sendResult = await SendOnAsync(
                connection,
                Encoding.UTF8.GetBytes(subscription.SubscribePayload),
                WebSocketMessageType.Text,
                cancellationToken).ConfigureAwait(false);

            if (sendResult.IsFailure)
            {
                Log.SubscriptionReplayFailed(_logger, subscription.Id);
                return WebSocketErrors.SubscriptionReplayFailed(subscription.Id, sendResult.Error);
            }
        }

        Interlocked.Increment(ref _subscriptionReplayCount);
        Log.SubscriptionsReplayed(_logger, pending.Length);
        return Result.Success();
    }

    /// <summary>
    /// 接收迴圈。成功表示對方送出了關閉 frame;失敗表示連線異常中斷。
    /// The receive loop. Success means the peer sent a close frame; failure means the connection broke.
    /// </summary>
    /// <remarks>
    /// <b>這裡刻意不傳取消權杖給 <c>ReceiveAsync</c>。</b>取消進行中的接收會把 socket 打成
    /// <see cref="WebSocketState.Aborted"/>,之後就無法優雅關閉。要停止這個迴圈的方式是讓它自己結束:
    /// 外面先送出關閉 frame,對方回覆之後這裡就會收到 <see cref="WebSocketMessageType.Close"/> 而返回;
    /// 若對方不回應,外面會在硬逾時後呼叫 <see cref="IWebSocketConnection.Abort"/>,接收會因此擲出例外而結束。
    /// <b>No cancellation token is passed to <c>ReceiveAsync</c> on purpose.</b> Cancelling an in-flight receive
    /// moves the socket to <see cref="WebSocketState.Aborted"/>, after which a graceful close is impossible. The way
    /// to stop this loop is to let it end by itself: the caller sends the close frame, the peer answers, and this
    /// loop returns on <see cref="WebSocketMessageType.Close"/>. If the peer never answers, the caller aborts after
    /// a hard timeout and the receive throws its way out.
    /// </remarks>
    private async Task<Result> ReceiveLoopAsync(IWebSocketConnection connection)
    {
        var buffer = new byte[_options.ReceiveBufferSize];
        var assembled = new ArrayBufferWriter<byte>(_options.ReceiveBufferSize);
        var kind = WebSocketMessageKind.Text;

        while (true)
        {
            ValueWebSocketReceiveResult received;

            try
            {
                received = await connection.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // 接收端的任何例外都代表這條連線結束了,一律轉成失敗結果交給重連迴圈處理。
                              // Any exception from the receive means this connection is over; it becomes a failed
                              // Result for the reconnect loop rather than escaping.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                return Interlocked.Exchange(ref _idleTimedOut, 0) == 1
                    ? Result.Failure(WebSocketErrors.IdleTimeout(_options.IdleTimeout))
                    : Result.Failure(WebSocketErrors.ConnectionLost(exception));
            }

            // 任何一個 frame 都證明對方還活著,閒置計時因此跟著更新。
            // Any frame proves the peer is alive, so the idle clock is refreshed here.
            var now = _timeProvider.GetUtcNow();
            Interlocked.Exchange(ref _lastMessageTicks, now.UtcTicks);

            if (received.MessageType == WebSocketMessageType.Close)
            {
                return Result.Success();
            }

            if (received.Count > 0)
            {
                if (assembled.WrittenCount + received.Count > _options.MaxMessageSize)
                {
                    return Result.Failure(WebSocketErrors.MessageTooLarge(_options.MaxMessageSize));
                }

                assembled.Write(buffer.AsSpan(0, received.Count));
                Interlocked.Add(ref _bytesReceived, received.Count);
            }

            kind = received.MessageType == WebSocketMessageType.Binary
                ? WebSocketMessageKind.Binary
                : WebSocketMessageKind.Text;

            if (!received.EndOfMessage)
            {
                continue;
            }

            var message = kind == WebSocketMessageKind.Binary
                ? WebSocketMessage.FromBinary(assembled.WrittenSpan.ToArray(), now)
                : WebSocketMessage.FromText(Encoding.UTF8.GetString(assembled.WrittenSpan), now, assembled.WrittenCount);

            assembled.ResetWrittenCount();

            Interlocked.Increment(ref _messagesReceived);
            await PublishMessageAsync(message).ConfigureAwait(false);
        }
    }

    // ── 背壓 ─────────────────────────────────────────────────────────
    // ── Backpressure ──────────────────────────────────────────────────

    private async ValueTask PublishMessageAsync(WebSocketMessage message)
    {
        var item = Result.Success(message);

        if (_queue.Writer.TryWrite(item))
        {
            return;
        }

        switch (_options.BackpressureStrategy)
        {
            case BackpressureStrategy.Wait:
                try
                {
                    await _queue.Writer.WriteAsync(item, _shutdownCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 關閉中,這一則沒有人會再取走。
                    // Shutting down; nobody will ever read this one.
                }
                catch (ChannelClosedException)
                {
                    // 佇列已關閉,同上。
                    // The queue is already closed; same as above.
                }

                return;

            case BackpressureStrategy.DropNewest:
                RecordDrop(message, BackpressureStrategy.DropNewest);
                return;

            case BackpressureStrategy.DropOldest:
            default:
                if (!WriteEvictingOldest(item))
                {
                    RecordDrop(message, BackpressureStrategy.DropOldest);
                }

                return;
        }
    }

    /// <summary>
    /// 把失敗通知寫進串流。無論背壓策略為何都會擠掉最舊的訊息以騰出空間 ——
    /// 缺口通知比缺口本身更不能丟。
    /// Writes a failure notice into the stream, evicting the oldest message regardless of the backpressure strategy:
    /// losing the notice of a gap is worse than losing the data in it.
    /// </summary>
    private void PublishFailure(Error error)
    {
        if (!WriteEvictingOldest(Result.Failure<WebSocketMessage>(error)))
        {
            Log.FailureNoticeDropped(_logger);
        }
    }

    private bool WriteEvictingOldest(Result<WebSocketMessage> item)
    {
        for (var round = 0; round <= _options.QueueCapacity; round++)
        {
            if (_queue.Writer.TryWrite(item))
            {
                return true;
            }

            if (!_queue.Reader.TryRead(out var evicted))
            {
                continue;
            }

            if (evicted.TryGetValue(out var evictedMessage))
            {
                RecordDrop(evictedMessage, BackpressureStrategy.DropOldest);
            }
            else
            {
                Log.FailureNoticeDropped(_logger);
            }
        }

        return false;
    }

    private void RecordDrop(WebSocketMessage message, BackpressureStrategy strategy)
    {
        var total = Interlocked.Increment(ref _messagesDropped);
        Log.MessageDropped(_logger, strategy, total);
        RaiseMessageDropped(new WebSocketMessageDroppedEventArgs(message, strategy, total));
    }

    // ── 計時器 ───────────────────────────────────────────────────────
    // ── Timers ────────────────────────────────────────────────────────

    private void StartTimers()
    {
        if (_options.IdleTimeout > TimeSpan.Zero)
        {
            var interval = _options.ResolveIdleCheckInterval();
            _idleTimer = _timeProvider.CreateTimer(OnIdleCheck, null, interval, interval);
        }

        if (_options.ApplicationPingInterval > TimeSpan.Zero)
        {
            _pingTimer = _timeProvider.CreateTimer(
                OnApplicationPing,
                null,
                _options.ApplicationPingInterval,
                _options.ApplicationPingInterval);
        }
    }

    private void StopTimers()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
        _pingTimer?.Dispose();
        _pingTimer = null;
    }

    /// <summary>
    /// 閒置檢查:超過設定時間沒收到任何訊息就中止連線,交由重連迴圈接手。
    /// The idle check: abort the connection when nothing has arrived within the configured window and let the
    /// reconnect loop take over.
    /// </summary>
    /// <remarks>
    /// 這裡用中止而不是優雅關閉是刻意的 —— 會走到這裡就表示對方已經不回應了,送出關閉 frame 也等不到回覆,
    /// 只會讓重連多等一個逾時。
    /// Aborting rather than closing gracefully is deliberate: reaching this point means the peer has stopped
    /// responding, so a close frame would go unanswered and only delay the reconnect by another timeout.
    /// </remarks>
    private void OnIdleCheck(object? state)
    {
        var connection = Volatile.Read(ref _connection);
        if (connection is null)
        {
            return;
        }

        var lastTicks = Interlocked.Read(ref _lastMessageTicks);
        if (lastTicks == 0)
        {
            return;
        }

        var elapsed = _timeProvider.GetUtcNow() - new DateTimeOffset(lastTicks, TimeSpan.Zero);
        if (elapsed < _options.IdleTimeout)
        {
            return;
        }

        if (Interlocked.Exchange(ref _idleTimedOut, 1) == 1)
        {
            return;
        }

        Log.IdleTimeout(_logger, _options.IdleTimeout);
        connection.Abort();
    }

    private void OnApplicationPing(object? state)
    {
        var factory = _options.ApplicationPingPayloadFactory;
        if (factory is null)
        {
            return;
        }

        _ = SendApplicationPingAsync(factory);
    }

    private async Task SendApplicationPingAsync(Func<string> factory)
    {
        try
        {
            var payload = factory();
            var result = await SendAsync(payload, CancellationToken.None).ConfigureAwait(false);
            if (result.IsFailure)
            {
                Log.ApplicationPingFailed(_logger, result.Error.Code);
            }
        }
#pragma warning disable CA1031 // 這是背景的射後不理工作,例外逃出去會變成未觀察到的 Task 例外,必須就地吞掉並記錄。
                              // This is fire-and-forget background work; an escaping exception becomes an unobserved
                              // task exception, so it is swallowed and logged here.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Log.ApplicationPingFailed(_logger, exception.Message);
        }
    }

    // ── 傳送 ─────────────────────────────────────────────────────────
    // ── Sending ───────────────────────────────────────────────────────

    private async Task<Result> SendCoreAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType messageType,
        CancellationToken cancellationToken)
    {
        var connection = Volatile.Read(ref _connection);
        if (connection is null || connection.State != WebSocketState.Open)
        {
            return WebSocketErrors.NotConnected(State);
        }

        return await SendOnAsync(connection, payload, messageType, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 在指定連線上送出一則完整訊息。同一條連線上的傳送以號誌序列化 ——
    /// WebSocket 不允許兩個傳送交錯,否則 frame 會互相穿插而讓對方收到壞掉的訊息。
    /// Sends one complete message on a given connection. Sends are serialised with a semaphore because WebSocket
    /// forbids overlapping sends: interleaved frames would reach the peer as corrupted messages.
    /// </summary>
    private async Task<Result> SendOnAsync(
        IWebSocketConnection connection,
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType messageType,
        CancellationToken cancellationToken)
    {
        try
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return WebSocketErrors.Cancelled("SendAsync");
        }
        catch (ObjectDisposedException)
        {
            return WebSocketErrors.NotConnected(State);
        }

        try
        {
            await connection.SendAsync(payload, messageType, true, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _messagesSent);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return WebSocketErrors.Cancelled("SendAsync");
        }
#pragma warning disable CA1031 // 傳送失敗在長命連線上是日常,一律轉成失敗結果,不讓例外穿過公開 API。
                              // Send failures are routine on a long-lived connection; they become failed Results
                              // rather than exceptions escaping through the public API.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return WebSocketErrors.SendFailed(exception);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    // ── 關閉 ─────────────────────────────────────────────────────────
    // ── Closing ───────────────────────────────────────────────────────

    /// <summary>
    /// 優雅關閉:送出關閉 frame,等接收迴圈看到對方的回覆而自行結束,socket 因此停在
    /// <see cref="WebSocketState.Closed"/> 而不是 <see cref="WebSocketState.Aborted"/>。
    /// The graceful path: send the close frame, let the receive loop end when it sees the peer's reply, and leave the
    /// socket at <see cref="WebSocketState.Closed"/> rather than <see cref="WebSocketState.Aborted"/>.
    /// </summary>
    private async Task<Result> CloseCoreAsync(WebSocketCloseReason reason, CancellationToken cancellationToken)
    {
        if (State == WebSocketClientState.Closed)
        {
            return Result.Success();
        }

        _closeRequested = true;

        // 先取消重連迴圈的等待,免得在關閉的同時又冒出一條新連線。
        // Cancel the reconnect loop's waits first, so no new connection appears while we are closing.
        await CancelShutdownTokenAsync().ConfigureAwait(false);

        var result = Result.Success();
        var connection = Volatile.Read(ref _connection);

        if (connection is not null && connection.State == WebSocketState.Open)
        {
            try
            {
                await connection
                    .CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client shutdown", cancellationToken)
                    .ConfigureAwait(false);
            }
#pragma warning disable CA1031 // 關閉失敗不該阻止關閉流程繼續 —— 後面還有中止這條保險。
                              // A failed close must not stop the shutdown; the abort fallback below still applies.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                result = Result.Failure(WebSocketErrors.SendFailed(exception));
            }
        }

        var runLoop = _runLoop;
        if (runLoop is not null)
        {
            var waited = await WaitForRunLoopAsync(runLoop, cancellationToken).ConfigureAwait(false);
            if (waited.IsFailure)
            {
                result = waited;
            }
        }
        else
        {
            TeardownConnection();
        }

        if (result.IsSuccess)
        {
            Log.ClosedGracefully(_logger);
        }

        SetState(WebSocketClientState.Closed, reason);
        _queue.Writer.TryComplete();
        return result;
    }

    private async Task<Result> WaitForRunLoopAsync(Task runLoop, CancellationToken cancellationToken)
    {
        // 硬逾時是最後的保險,不是正常路徑:正常路徑是對方回覆關閉 frame,接收迴圈自己結束。
        // The hard timeout is a last resort, not the normal path — normally the peer answers and the receive loop
        // ends by itself.
        using var timeoutCts = new CancellationTokenSource(_options.CloseTimeout, _timeProvider);

        Task delay;
        try
        {
            delay = Task.Delay(Timeout.InfiniteTimeSpan, _timeProvider, timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            delay = Task.CompletedTask;
        }

        var finished = await Task.WhenAny(runLoop, delay).ConfigureAwait(false);

        // 收掉沒用到的那一條等待,免得留下一個永遠不會完成的計時器工作。
        // Cancel whichever wait was not used, so no never-completing timer task is left behind.
        await timeoutCts.CancelAsync().ConfigureAwait(false);

        if (!ReferenceEquals(finished, runLoop))
        {
            Log.CloseTimedOut(_logger, _options.CloseTimeout);
            Volatile.Read(ref _connection)?.Abort();

            try
            {
                await runLoop.WaitAsync(_options.CloseTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // 已經在收尾了,這裡再失敗也只能記錄,不能讓關閉流程卡住。
                              // We are already shutting down; a further failure can only be reported, never allowed
                              // to stall the close.
            catch (Exception)
#pragma warning restore CA1031
            {
                // 迴圈仍未結束,下面的 TeardownConnection 會強制清理。
                // The loop is still running; the teardown below cleans up regardless.
            }

            TeardownConnection();
            return WebSocketErrors.ConnectTimeout(_options.CloseTimeout);
        }

        TeardownConnection();
        return Result.Success();
    }

    private async Task CancelShutdownTokenAsync()
    {
        try
        {
            await _shutdownCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // 已經釋放過,視同已取消。
            // Already disposed, which is equivalent to already cancelled.
        }
    }

    private void TeardownConnection()
    {
        StopTimers();
        var connection = Interlocked.Exchange(ref _connection, null);
        connection?.Dispose();
    }

    /// <summary>
    /// 終局結束:把終局錯誤送進串流、關閉狀態機、結束串流。呼叫端負責先記錄「為什麼」的日誌 ——
    /// 放棄的理由不只一種,日誌訊息必須分得出來。
    /// Ends for good: publishes the terminal error, closes the state machine, and completes the stream. The caller
    /// logs why first, because there is more than one way to give up and the log has to tell them apart.
    /// </summary>
    private void FailTerminally(WebSocketCloseReason reason, Error error)
    {
        PublishFailure(error);
        SetState(WebSocketClientState.Closed, reason, error);
        _queue.Writer.TryComplete();
    }

    // ── 狀態與事件 ───────────────────────────────────────────────────
    // ── State and events ──────────────────────────────────────────────

    private Result TryBeginStart(string operation)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return WebSocketErrors.InvalidState(WebSocketClientState.Closed, operation);
        }

        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return WebSocketErrors.InvalidState(State, operation);
        }

        return Result.Success();
    }

    private void SetState(WebSocketClientState newState, WebSocketCloseReason closeReason = WebSocketCloseReason.None, Error? error = null)
    {
        WebSocketClientState previous;

        lock (_stateGate)
        {
            if (_state == newState)
            {
                if (closeReason != WebSocketCloseReason.None)
                {
                    _closeReason = closeReason;
                }

                return;
            }

            previous = _state;
            _state = newState;

            if (closeReason != WebSocketCloseReason.None)
            {
                _closeReason = closeReason;
            }
        }

        RaiseStateChanged(new WebSocketClientStateChangedEventArgs(
            previous,
            newState,
            _timeProvider.GetUtcNow(),
            closeReason,
            error));
    }

    private void RaiseStateChanged(WebSocketClientStateChangedEventArgs args)
    {
        try
        {
            StateChanged?.Invoke(this, args);
        }
#pragma warning disable CA1031 // 呼叫端的事件處理常式擲出例外不該讓客戶端停擺;記錄後繼續運作。
                              // A throwing handler from the caller must not take the client down; log and carry on.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Log.EventHandlerFailed(_logger, exception);
        }
    }

    private void RaiseMessageDropped(WebSocketMessageDroppedEventArgs args)
    {
        try
        {
            MessageDropped?.Invoke(this, args);
        }
#pragma warning disable CA1031 // 同上:事件處理常式的例外不能讓接收迴圈中斷。
                              // As above: a handler's exception must not break the receive loop.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Log.EventHandlerFailed(_logger, exception);
        }
    }

    private int SubscriptionCountSnapshot()
    {
        lock (_subscriptionGate)
        {
            return _subscriptions.Count;
        }
    }

    private static DateTimeOffset? ToTimestamp(long ticks) =>
        ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
}
