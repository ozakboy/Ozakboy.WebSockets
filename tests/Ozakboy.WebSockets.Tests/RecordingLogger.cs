using Microsoft.Extensions.Logging;

namespace Ozakboy.WebSockets.Tests;

/// <summary>
/// 把日誌記下來的假 <see cref="ILogger{TCategoryName}"/>。長時間執行的服務出事之後只剩日誌可看,
/// 所以「該記的有沒有記」本身就值得測。
/// A recording <see cref="ILogger{TCategoryName}"/>. When a long-running service goes wrong, the log is all that is
/// left, so whether the right things get logged is itself worth testing.
/// </summary>
internal sealed class RecordingLogger : ILogger<WebSocketClient>
{
    private readonly Lock _gate = new();
    private readonly List<(LogLevel Level, EventId EventId, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, EventId EventId, string Message)> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        lock (_gate)
        {
            _entries.Add((logLevel, eventId, formatter(state, exception)));
        }
    }

    public bool HasEvent(int eventId)
    {
        lock (_gate)
        {
            return _entries.Exists(entry => entry.EventId.Id == eventId);
        }
    }
}
