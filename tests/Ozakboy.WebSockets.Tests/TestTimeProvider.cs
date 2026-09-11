namespace Ozakboy.WebSockets.Tests;

/// <summary>
/// 手動推進的假時鐘。所有跟時間有關的行為(重連退避、閒置逾時、應用層 ping、關閉逾時)都靠它測,
/// 測試因此不需要真的等待,也不會因為機器忙碌而偶發失敗。
/// A manually advanced clock. Every time-dependent behaviour — reconnect backoff, idle timeout, application ping,
/// close timeout — is exercised through it, so tests never wait for real time and never flake on a busy machine.
/// </summary>
/// <remarks>
/// 自己實作而不是引用 Microsoft.Extensions.TimeProvider.Testing,是為了讓
/// <c>dotnet list package --include-transitive</c> 的結果維持在最小,不必為了測試多掛一個套件。
/// Hand-rolled rather than pulled from Microsoft.Extensions.TimeProvider.Testing so that
/// <c>dotnet list package --include-transitive</c> stays as small as possible.
/// </remarks>
internal sealed class TestTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<TestTimer> _timers = [];
    private DateTimeOffset _utcNow;

    public TestTimeProvider(DateTimeOffset start)
    {
        _utcNow = start;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new TestTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// 目前掛著、且已排定下一次觸發時間的計時器數量。測試用它來確認背景程式碼真的已經開始等待,
    /// 才推進時鐘 —— 否則會推進在前、註冊在後,計時器永遠不會觸發。
    /// The number of timers currently armed. Tests use it to confirm the background code really is waiting before
    /// advancing the clock; advancing first would leave the later-registered timer never firing.
    /// </summary>
    public int ArmedTimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(timer => timer.IsArmed);
            }
        }
    }

    /// <summary>
    /// 推進時鐘,途中依序觸發所有到期的計時器。
    /// Advances the clock, firing every timer that comes due along the way, in order.
    /// </summary>
    public void Advance(TimeSpan amount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);

        var target = GetUtcNow() + amount;

        while (true)
        {
            TestTimer? next = null;
            var nextDue = DateTimeOffset.MaxValue;

            lock (_gate)
            {
                foreach (var timer in _timers)
                {
                    if (timer.IsArmed && timer.NextDue <= target && timer.NextDue < nextDue)
                    {
                        next = timer;
                        nextDue = timer.NextDue;
                    }
                }

                if (next is null)
                {
                    _utcNow = target;
                    return;
                }

                _utcNow = nextDue;
            }

            next.Fire();
        }
    }

    private void Remove(TestTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class TestTimer : ITimer
    {
        private readonly TestTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        public TestTimer(TestTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public bool IsArmed { get; private set; }

        public DateTimeOffset NextDue { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
            {
                return false;
            }

            _period = period;

            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                IsArmed = false;
                return true;
            }

            NextDue = _owner.GetUtcNow() + dueTime;
            IsArmed = true;
            return true;
        }

        public void Fire()
        {
            if (_disposed)
            {
                return;
            }

            if (_period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero)
            {
                IsArmed = false;
            }
            else
            {
                NextDue += _period;
            }

            _callback(_state);
        }

        public void Dispose()
        {
            _disposed = true;
            IsArmed = false;
            _owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
