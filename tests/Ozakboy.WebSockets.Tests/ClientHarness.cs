namespace Ozakboy.WebSockets.Tests;

/// <summary>
/// 組裝一個受測客戶端:假連線工廠 + 假時鐘 + 可選的背景消費者。
/// Assembles a client under test: fake connection factory, fake clock, and an optional background consumer.
/// </summary>
internal sealed class ClientHarness : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly List<Result<WebSocketMessage>> _received = [];
    private readonly List<WebSocketMessageDroppedEventArgs> _drops = [];
    private readonly List<WebSocketClientStateChangedEventArgs> _stateChanges = [];
    private readonly CancellationTokenSource _consumerCts = new();

    private Task? _consumer;

    public ClientHarness(Action<WebSocketClientOptions>? configure = null)
    {
        Time = new TestTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Factory = new FakeWebSocketConnectionFactory(Time);
        Options = DefaultOptions();
        configure?.Invoke(Options);
        Client = new WebSocketClient(Options, Factory, logger: null, timeProvider: Time);

        Client.MessageDropped += (_, args) =>
        {
            lock (_gate)
            {
                _drops.Add(args);
            }
        };

        Client.StateChanged += (_, args) =>
        {
            lock (_gate)
            {
                _stateChanges.Add(args);
            }
        };
    }

    public TestTimeProvider Time { get; }

    public FakeWebSocketConnectionFactory Factory { get; }

    public WebSocketClientOptions Options { get; }

    public WebSocketClient Client { get; }

    public bool ConsumerCompleted => _consumer is { IsCompleted: true };

    /// <summary>
    /// 測試預設值:關閉閒置逾時、關閉抖動、拉長連線逾時,讓每個測試只需要開啟自己關心的那一項。
    /// Test defaults: idle timeout off, jitter off, a long connect timeout — so each test only turns on the one
    /// thing it cares about.
    /// </summary>
    private static WebSocketClientOptions DefaultOptions() => new()
    {
        Uri = new Uri("wss://example.invalid/stream"),
        IdleTimeout = TimeSpan.Zero,
        // 拉到非常大,讓測試在推進假時鐘時不會意外撞到連線逾時;要測逾時的測試自己調小。
        // Deliberately huge so that advancing the fake clock never trips the connect timeout by accident; the test
        // that exercises the timeout sets its own value.
        ConnectTimeout = TimeSpan.FromHours(1),
        CloseTimeout = TimeSpan.FromSeconds(5),
        QueueCapacity = 64,
        ReconnectPolicy = new RetryPolicy
        {
            MaxAttempts = int.MaxValue,
            BaseDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(8),
            Strategy = BackoffStrategy.Exponential,
            JitterRatio = 0d,
        },
    };

    public void StartConsuming()
    {
        _consumer = Task.Run(async () =>
        {
            await foreach (var item in Client.Messages(_consumerCts.Token).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    _received.Add(item);
                }
            }
        });
    }

    public IReadOnlyList<Result<WebSocketMessage>> Received
    {
        get
        {
            lock (_gate)
            {
                return [.. _received];
            }
        }
    }

    public IReadOnlyList<WebSocketMessageDroppedEventArgs> Drops
    {
        get
        {
            lock (_gate)
            {
                return [.. _drops];
            }
        }
    }

    public IReadOnlyList<WebSocketClientStateChangedEventArgs> StateChanges
    {
        get
        {
            lock (_gate)
            {
                return [.. _stateChanges];
            }
        }
    }

    public IReadOnlyList<string> TextsReceived
    {
        get
        {
            lock (_gate)
            {
                return [.. _received.Where(item => item.IsSuccess).Select(item => item.GetValueOrThrow().Text ?? string.Empty)];
            }
        }
    }

    public IReadOnlyList<Error> FailuresReceived
    {
        get
        {
            lock (_gate)
            {
                return [.. _received.Where(item => item.IsFailure).Select(item => item.Error!)];
            }
        }
    }

    public Task WaitForConnectionsAsync(int count) =>
        Wait.UntilAsync(() => Factory.CreateCount >= count, $"已建立 {count} 條連線");

    public Task WaitForStateAsync(WebSocketClientState state) =>
        Wait.UntilAsync(() => Client.State == state, $"狀態變成 {state}");

    public Task WaitForMessagesAsync(int count) =>
        Wait.UntilAsync(() => Client.Statistics.MessagesReceived >= count, $"已收到 {count} 則訊息");

    /// <summary>
    /// 一邊推進假時鐘一邊等待條件成立。推進在前、計時器註冊在後的話,那個計時器永遠不會觸發,
    /// 測試會變成間歇性失敗;所以這裡反覆小步推進,直到背景程式碼真的走到位。
    /// Advances the fake clock in small steps until a condition holds. Advancing before the background code arms its
    /// timer would leave that timer unfired and make the test intermittent, so this keeps nudging the clock until
    /// the code gets there.
    /// </summary>
    public async Task AdvanceUntilAsync(Func<bool> condition, string description, TimeSpan? step = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var stepSize = step ?? TimeSpan.FromSeconds(1);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            if (Time.ArmedTimerCount > 0)
            {
                Time.Advance(stepSize);
            }

            await Task.Delay(2).ConfigureAwait(false);
        }

        Assert.Fail($"推進假時鐘等待條件逾時:{description}。Timed out advancing the clock for: {description}.");
    }

    /// <summary>
    /// 等到重連退避的計時器確實排定為止。先確認狀態是重連中,那時連線逾時的計時器已經釋放,
    /// 剩下的唯一計時器就是退避等待,推進時鐘才有確定的意義。
    /// Waits until the reconnect backoff timer is armed. Checking for the Reconnecting state first guarantees the
    /// connect-timeout timer has already been disposed, so the only remaining timer is the backoff wait and
    /// advancing the clock has a definite meaning.
    /// </summary>
    public async Task WaitForBackoffArmedAsync()
    {
        await WaitForStateAsync(WebSocketClientState.Reconnecting).ConfigureAwait(false);
        await Wait.UntilAsync(() => Time.ArmedTimerCount > 0, "退避計時器已排定").ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _consumerCts.CancelAsync().ConfigureAwait(false);
        await Client.DisposeAsync().ConfigureAwait(false);

        if (_consumer is not null)
        {
            try
            {
                await _consumer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 消費者被取消是預期內的收尾。
                // A cancelled consumer is the expected way to finish.
            }
        }

        _consumerCts.Dispose();
    }
}
