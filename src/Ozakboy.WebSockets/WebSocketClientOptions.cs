using Ozakboy.Core.Abstractions;

namespace Ozakboy.WebSockets;

/// <summary>
/// <see cref="WebSocketClient"/> 的設定。
/// Configuration for <see cref="WebSocketClient"/>.
/// </summary>
/// <remarks>
/// 每個屬性都在設定時就驗證,錯的值當場擲出 <see cref="ArgumentOutOfRangeException"/> 而不是等到連線才失敗;
/// 跨屬性的一致性檢查(例如應用層 ping 開了卻沒給內容)則由 <see cref="Validate"/> 負責,
/// 客戶端建構時會自動呼叫一次。
/// Each property validates on assignment and throws <see cref="ArgumentOutOfRangeException"/> immediately rather
/// than failing later at connect time. Cross-property consistency — an application ping enabled with no payload, for
/// instance — is checked by <see cref="Validate"/>, which the client calls once during construction.
/// </remarks>
public sealed class WebSocketClientOptions
{
    /// <summary>
    /// 要連線的位址。必須是 <c>ws</c> 或 <c>wss</c> 的絕對位址。
    /// The endpoint to connect to. Must be an absolute <c>ws</c> or <c>wss</c> URI.
    /// </summary>
    public Uri? Uri { get; set; }

    /// <summary>
    /// 重連的退避策略。間隔一律交給 <see cref="RetryPolicy.GetDelay(int)"/> 計算,本套件不自行推算。
    /// The backoff policy for reconnects. Intervals always come from <see cref="RetryPolicy.GetDelay(int)"/>; this
    /// package never computes them itself.
    /// </summary>
    /// <remarks>
    /// 預設為 1 秒起跳的指數退避、上限 30 秒、正負 20% 抖動。抖動很重要:對方重啟恢復的瞬間,
    /// 所有斷線的客戶端若用完全相同的間隔重連,會一起湧上去再把它打掛一次。
    /// The default is exponential backoff from one second, capped at 30 seconds, with ±20% jitter. The jitter
    /// matters: when the peer comes back up, clients using identical intervals all reconnect at the same instant
    /// and knock it over again.
    /// </remarks>
    public RetryPolicy ReconnectPolicy
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    } = new RetryPolicy
    {
        // MaxAttempts 在這裡沒有作用 —— 重連次數上限由 MaxReconnectAttempts 控制(它支援無限),
        // RetryPolicy 在此只被當成間隔計算器使用。設成 int.MaxValue 以免 ShouldRetry 被誤用時提前放棄。
        // MaxAttempts has no effect here: the reconnect ceiling is MaxReconnectAttempts (which supports "unlimited"),
        // and RetryPolicy is used purely as an interval calculator. It is set high so that a stray ShouldRetry call
        // does not give up early.
        MaxAttempts = int.MaxValue,
        BaseDelay = TimeSpan.FromSeconds(1),
        MaxDelay = TimeSpan.FromSeconds(30),
        Strategy = BackoffStrategy.Exponential,
        JitterRatio = 0.2d,
    };

    /// <summary>
    /// 連線中斷後最多重連幾次;<see langword="null"/> 表示無限重連。預設為 <see langword="null"/>。
    /// The maximum number of reconnect attempts after a drop; <see langword="null"/> means unlimited. Defaults to
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// 24 小時執行的程式通常要無限重連 —— 對方維護三小時之後回來,客戶端應該自己接上,而不是在半夜
    /// 用盡次數後安靜地停在那裡。設定成有限次數的場合多半是測試或短期工具。
    /// A process that runs around the clock normally wants unlimited attempts: when the peer returns from three
    /// hours of maintenance the client should reconnect by itself rather than having quietly given up in the middle
    /// of the night. Finite values are mostly for tests and short-lived tools.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 設定為小於 0 的值時擲出。
    /// Thrown when set below zero.
    /// </exception>
    public int? MaxReconnectAttempts
    {
        get;
        set
        {
            if (value.HasValue)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(value.Value, 0);
            }

            field = value;
        }
    }

    /// <summary>
    /// 多久沒有收到任何訊息就判定連線已死並重連。<see cref="TimeSpan.Zero"/> 表示停用。預設 60 秒。
    /// How long without receiving anything before the connection is declared dead and reconnected.
    /// <see cref="TimeSpan.Zero"/> disables the check. Defaults to 60 seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>這不是「回應對方的 ping」。</b><see cref="System.Net.WebSockets.ClientWebSocket"/> 會在協定層自動
    /// 回覆 pong,應用層既看不到那些 frame 也沒有 API 可以介入,網路上「收到 ping 要手動回 pong」的教學
    /// 對它並不適用,照做也送不出去。所以存活偵測只能反過來做:偵測對方是否還活著。
    /// <b>This is not about answering the peer's ping.</b> <see cref="System.Net.WebSockets.ClientWebSocket"/> replies
    /// with a pong automatically at the protocol layer; the application never sees those frames and has no API to
    /// intervene. Tutorials telling you to send a pong by hand do not apply here — the code would compile and send
    /// nothing. Liveness detection therefore has to work the other way round: detect whether the peer is still alive.
    /// </para>
    /// <para>
    /// 「連得上」不等於「收得到」。實測遇過握手成功、<c>WebSocketState.Open</c> 維持不變、
    /// 整整 200 秒沒有任何 frame 進來,沒有例外也沒有斷線(中介設備放行了握手卻吃掉資料流)。
    /// 只看連線狀態完全發現不了這種故障,只有「多久沒收到訊息」能。
    /// Being connected is not the same as receiving. We have measured a handshake that succeeded, a socket that
    /// stayed <c>WebSocketState.Open</c>, and 200 seconds with not one frame — no exception, no disconnect, because
    /// something in the path let the handshake through and swallowed the data stream. Inspecting the socket state
    /// cannot detect that; only "how long since the last message" can.
    /// </para>
    /// <para>
    /// 逾時長度要大於資料流本身最長的正常沉默期,否則會在冷門時段誤判重連。
    /// Set this comfortably above the longest normal silence in the stream, or quiet periods will trigger
    /// unnecessary reconnects.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 設定為負值時擲出。
    /// Thrown when set to a negative value.
    /// </exception>
    public TimeSpan IdleTimeout
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            field = value;
        }
    } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 閒置檢查的頻率;<see langword="null"/> 表示自動採用 <see cref="IdleTimeout"/> 的四分之一。
    /// How often the idle check runs; <see langword="null"/> uses a quarter of <see cref="IdleTimeout"/>.
    /// </summary>
    /// <remarks>
    /// 檢查頻率決定偵測的解析度:每 T/4 檢查一次,最壞情況會比實際逾時晚 T/4 才發現。
    /// The interval sets the detection resolution: checking every T/4 means the worst case is noticed T/4 late.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 設定為不大於零的值時擲出。
    /// Thrown when set to a value that is not greater than zero.
    /// </exception>
    public TimeSpan? IdleCheckInterval
    {
        get;
        set
        {
            if (value.HasValue)
            {
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value.Value, TimeSpan.Zero);
            }

            field = value;
        }
    }

    /// <summary>
    /// 應用層 ping 的送出頻率。<see cref="TimeSpan.Zero"/> 表示不送,為預設值。
    /// How often to send an application-level ping. <see cref="TimeSpan.Zero"/>, the default, sends none.
    /// </summary>
    /// <remarks>
    /// 部分交易所在 WebSocket 協定之上另外定義了應用層的 ping/pong 訊息(一則普通的文字訊息)。
    /// 本套件可以選用地代為送出,但<b>絕不假設它存在</b> —— 預設是關閉的,因為對不認得這種訊息的對方送出
    /// 未定義的內容,輕則被忽略,重則被視為協定違規而斷線。
    /// Some exchanges define their own ping/pong messages on top of the WebSocket protocol — an ordinary text
    /// message. This package can send those for you, but <b>never assumes they exist</b>: the feature is off by
    /// default, because sending an undefined payload to a peer that does not recognise it is at best ignored and at
    /// worst treated as a protocol violation and disconnected.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 設定為負值時擲出。
    /// Thrown when set to a negative value.
    /// </exception>
    public TimeSpan ApplicationPingInterval
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            field = value;
        }
    }

    /// <summary>
    /// 產生應用層 ping 訊息內容的函式。<see cref="ApplicationPingInterval"/> 大於零時必須提供。
    /// Produces the application ping payload. Required when <see cref="ApplicationPingInterval"/> is above zero.
    /// </summary>
    /// <remarks>
    /// 用函式而不是固定字串,是因為多數協定的 ping 訊息需要帶遞增識別碼或時間戳。
    /// 這個函式會在背景計時器執行緒上被呼叫,實作必須是執行緒安全且不擲出例外的。
    /// It is a function rather than a fixed string because most protocols want an incrementing id or a timestamp in
    /// the ping. It is invoked on a background timer thread, so the implementation must be thread-safe and must not
    /// throw.
    /// </remarks>
    public Func<string>? ApplicationPingPayloadFactory { get; set; }

    /// <summary>
    /// 單次連線握手的逾時。預設 10 秒。
    /// The timeout for a single connection handshake. Defaults to 10 seconds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 設定為不大於零的值時擲出。
    /// Thrown when set to a value that is not greater than zero.
    /// </exception>
    public TimeSpan ConnectTimeout
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            field = value;
        }
    } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 優雅關閉的逾時:送出關閉 frame 之後最多等對方回應多久。預設 5 秒。
    /// The graceful-close timeout: how long to wait for the peer's close frame after sending ours. Defaults to
    /// 5 seconds.
    /// </summary>
    /// <remarks>
    /// 逾時之後才會呼叫 <see cref="System.Net.WebSockets.WebSocket.Abort"/> 收尾。這個逾時是最後的保險,
    /// 不是正常路徑 —— 正常路徑是對方回覆關閉 frame,接收迴圈自己結束,socket 以 <c>Closed</c> 收場。
    /// Only after this timeout does the client fall back to <see cref="System.Net.WebSockets.WebSocket.Abort"/>. The
    /// timeout is a last resort, not the normal path: normally the peer echoes the close frame, the receive loop
    /// ends by itself, and the socket finishes as <c>Closed</c>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 設定為不大於零的值時擲出。
    /// Thrown when set to a value that is not greater than zero.
    /// </exception>
    public TimeSpan CloseTimeout
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            field = value;
        }
    } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 協定層 keep-alive(WebSocket ping frame)的間隔,直接對應
    /// <see cref="System.Net.WebSockets.ClientWebSocket.Options"/> 的同名設定。預設 30 秒。
    /// The protocol-level keep-alive (WebSocket ping frame) interval, passed straight through to
    /// <see cref="System.Net.WebSockets.ClientWebSocket.Options"/>. Defaults to 30 seconds.
    /// </summary>
    /// <remarks>
    /// 這是協定層的機制,送出與回覆都由 <see cref="System.Net.WebSockets.ClientWebSocket"/> 自己處理,
    /// 應用層看不到也管不到。它能維持中間設備的連線表,但<b>不能</b>用來偵測對方是否還在供料 ——
    /// 那要靠 <see cref="IdleTimeout"/>。
    /// This is a protocol-layer mechanism handled entirely inside
    /// <see cref="System.Net.WebSockets.ClientWebSocket"/>; the application can neither observe nor influence it. It
    /// keeps intermediaries' connection tables warm, but it <b>cannot</b> tell you whether the peer is still sending
    /// data — that is what <see cref="IdleTimeout"/> is for.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 設定為負值時擲出。
    /// Thrown when set to a negative value.
    /// </exception>
    public TimeSpan TransportKeepAliveInterval
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            field = value;
        }
    } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 接收佇列的容量(訊息則數)。預設 1024。
    /// The receive queue capacity in messages. Defaults to 1024.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 設定為不大於零的值時擲出。
    /// Thrown when set to a value that is not greater than zero.
    /// </exception>
    public int QueueCapacity
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, 0);
            field = value;
        }
    } = 1024;

    /// <summary>
    /// 佇列滿了的處理方式。預設 <see cref="BackpressureStrategy.DropOldest"/>。
    /// What to do when the queue is full. Defaults to <see cref="BackpressureStrategy.DropOldest"/>.
    /// </summary>
    public BackpressureStrategy BackpressureStrategy { get; set; } = BackpressureStrategy.DropOldest;

    /// <summary>
    /// 單次 <c>ReceiveAsync</c> 使用的緩衝區大小(位元組)。預設 16384。
    /// The buffer size in bytes for a single <c>ReceiveAsync</c>. Defaults to 16384.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 設定為小於 1024 的值時擲出。
    /// Thrown when set below 1024.
    /// </exception>
    public int ReceiveBufferSize
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1024);
            field = value;
        }
    } = 16 * 1024;

    /// <summary>
    /// 單則訊息組裝後的大小上限(位元組)。預設 8 MB。
    /// The maximum size of one reassembled message in bytes. Defaults to 8 MB.
    /// </summary>
    /// <remarks>
    /// 分段訊息要全部收齊才能交給上層,沒有上限的話一個異常的對方就能靠一則永不結束的訊息把記憶體吃光。
    /// 超過上限時這條連線會被視為異常並重連。
    /// A fragmented message must be fully assembled before it is handed over; without a ceiling, a misbehaving peer
    /// could exhaust memory with a single never-ending message. Exceeding the limit fails the connection and
    /// triggers a reconnect.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 設定為不大於零的值時擲出。
    /// Thrown when set to a value that is not greater than zero.
    /// </exception>
    public int MaxMessageSize
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, 0);
            field = value;
        }
    } = 8 * 1024 * 1024;

    /// <summary>
    /// 握手時要附加的 HTTP 標頭。
    /// Extra HTTP headers to send with the handshake.
    /// </summary>
    public IDictionary<string, string> RequestHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 握手時要協商的子協定。
    /// Sub-protocols to negotiate during the handshake.
    /// </summary>
    public IList<string> SubProtocols { get; } = [];

    /// <summary>
    /// 檢查跨屬性的一致性。
    /// Checks consistency across properties.
    /// </summary>
    /// <returns>
    /// 設定可用時為成功,否則為分類 <see cref="ErrorCategory.Validation"/> 的失敗。
    /// Success when the configuration is usable; otherwise a failure categorised as
    /// <see cref="ErrorCategory.Validation"/>.
    /// </returns>
    public Result Validate()
    {
        if (Uri is null)
        {
            return WebSocketErrors.OptionsInvalid("必須指定 Uri。Uri must be set.");
        }

        if (!Uri.IsAbsoluteUri)
        {
            return WebSocketErrors.OptionsInvalid("Uri 必須是絕對位址。Uri must be absolute.");
        }

        if (!string.Equals(Uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
        {
            return WebSocketErrors.OptionsInvalid("Uri 的配置必須是 ws 或 wss。Uri scheme must be ws or wss.");
        }

        if (ApplicationPingInterval > TimeSpan.Zero && ApplicationPingPayloadFactory is null)
        {
            return WebSocketErrors.OptionsInvalid(
                "設定了 ApplicationPingInterval 就必須提供 ApplicationPingPayloadFactory。"
                + "ApplicationPingPayloadFactory is required when ApplicationPingInterval is set.");
        }

        if (MaxMessageSize < ReceiveBufferSize)
        {
            return WebSocketErrors.OptionsInvalid(
                "MaxMessageSize 不可小於 ReceiveBufferSize。MaxMessageSize must not be below ReceiveBufferSize.");
        }

        return Result.Success();
    }

    /// <summary>
    /// 取得實際使用的閒置檢查間隔。
    /// Gets the idle check interval actually in use.
    /// </summary>
    /// <returns>
    /// <see cref="IdleCheckInterval"/> 的設定值,未設定時為 <see cref="IdleTimeout"/> 的四分之一
    /// (至少 100 毫秒)。
    /// The configured <see cref="IdleCheckInterval"/>, or a quarter of <see cref="IdleTimeout"/> (at least 100 ms)
    /// when it is not set.
    /// </returns>
    internal TimeSpan ResolveIdleCheckInterval()
    {
        if (IdleCheckInterval.HasValue)
        {
            return IdleCheckInterval.Value;
        }

        var quarter = IdleTimeout / 4;
        return quarter < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : quarter;
    }
}
