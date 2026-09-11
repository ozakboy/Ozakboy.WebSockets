using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Ozakboy.WebSockets.Tests;

/// <summary>
/// 假的 WebSocket 連線。測試完全不碰網路、不開通訊埠,連線的每一種行為(正常收訊、突然斷線、
/// 對方主動關閉、傳送失敗、握手卡住、握手成功卻永遠不送資料)都由測試直接指定。
/// A fake WebSocket connection. Tests touch no network and bind no port; every behaviour — normal delivery, an
/// abrupt drop, a peer-initiated close, a failing send, a stalled handshake, a handshake that succeeds and then
/// delivers nothing — is dictated by the test.
/// </summary>
internal sealed class FakeWebSocketConnection : IWebSocketConnection
{
    private readonly Channel<Frame> _incoming = Channel.CreateUnbounded<Frame>();
    private readonly CancellationTokenSource _abortCts = new();
    private readonly Lock _gate = new();
    private readonly List<string> _sentTexts = [];
    private readonly List<byte[]> _sentBinaries = [];

    private WebSocketState _state = WebSocketState.None;

    /// <summary>
    /// 握手時要擲出的例外;為 null 表示握手成功。
    /// The exception to throw from the handshake; null means it succeeds.
    /// </summary>
    public Exception? ConnectException { get; set; }

    /// <summary>
    /// 握手時要等待的閘門。用來模擬「連不上但也不報錯」,好測連線逾時。
    /// A gate the handshake waits on, used to simulate a connect that neither succeeds nor fails so the connect
    /// timeout can be exercised.
    /// </summary>
    public TaskCompletionSource? ConnectGate { get; set; }

    /// <summary>
    /// 傳送時要擲出的例外;為 null 表示傳送成功。
    /// The exception to throw from a send; null means it succeeds.
    /// </summary>
    public Exception? SendException { get; set; }

    /// <summary>
    /// 收到關閉 frame 之後是否自動回覆一個關閉 frame(表現得像個守規矩的對方)。
    /// Whether to echo a close frame back, behaving like a well-mannered peer.
    /// </summary>
    public bool EchoClose { get; set; } = true;

    public bool IsDisposed { get; private set; }

    public bool WasAborted { get; private set; }

    public WebSocketState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public IReadOnlyList<string> SentTexts
    {
        get
        {
            lock (_gate)
            {
                return [.. _sentTexts];
            }
        }
    }

    public IReadOnlyList<byte[]> SentBinaries
    {
        get
        {
            lock (_gate)
            {
                return [.. _sentBinaries];
            }
        }
    }

    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        SetState(WebSocketState.Connecting);

        if (ConnectGate is not null)
        {
            await ConnectGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (ConnectException is not null)
        {
            SetState(WebSocketState.Closed);
            throw ConnectException;
        }

        SetState(WebSocketState.Open);
    }

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (SendException is not null)
        {
            throw SendException;
        }

        lock (_gate)
        {
            if (messageType == WebSocketMessageType.Text)
            {
                _sentTexts.Add(Encoding.UTF8.GetString(buffer.Span));
            }
            else
            {
                _sentBinaries.Add(buffer.ToArray());
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        Frame frame;

        try
        {
            frame = await _incoming.Reader.ReadAsync(_abortCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SetState(WebSocketState.Aborted);
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "連線已被中止。The connection was aborted.");
        }

        switch (frame.Kind)
        {
            case FrameKind.Fault:
                SetState(WebSocketState.Aborted);
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "模擬的連線中斷。Simulated connection loss.");

            case FrameKind.Close:
                lock (_gate)
                {
                    _state = _state == WebSocketState.CloseSent ? WebSocketState.Closed : WebSocketState.CloseReceived;
                }

                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);

            default:
                var count = Math.Min(buffer.Length, frame.Payload.Length);
                frame.Payload.AsSpan(0, count).CopyTo(buffer.Span);
                return new ValueWebSocketReceiveResult(count, frame.MessageType, frame.EndOfMessage);
        }
    }

    public Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        SetState(WebSocketState.CloseSent);

        if (EchoClose)
        {
            _incoming.Writer.TryWrite(Frame.Close());
        }

        return Task.CompletedTask;
    }

    public void Abort()
    {
        WasAborted = true;
        SetState(WebSocketState.Aborted);
        _abortCts.Cancel();
    }

    public void Dispose()
    {
        // 刻意不改動 State:測試要在釋放之後仍然能斷言 socket 是以 Closed 還是 Aborted 收場。
        // State is deliberately left alone so tests can still assert whether the socket finished Closed or Aborted.
        IsDisposed = true;
        _abortCts.Dispose();
    }

    /// <summary>
    /// 模擬對方送來一則完整的文字訊息。
    /// Simulates the peer sending one complete text message.
    /// </summary>
    public void PushText(string text) =>
        _incoming.Writer.TryWrite(Frame.Data(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true));

    /// <summary>
    /// 模擬對方送來一段文字訊息的片段。
    /// Simulates the peer sending one fragment of a text message.
    /// </summary>
    public void PushTextFragment(string text, bool endOfMessage) =>
        _incoming.Writer.TryWrite(Frame.Data(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage));

    /// <summary>
    /// 模擬對方送來一則二進位訊息。
    /// Simulates the peer sending one binary message.
    /// </summary>
    public void PushBinary(byte[] payload) =>
        _incoming.Writer.TryWrite(Frame.Data(payload, WebSocketMessageType.Binary, endOfMessage: true));

    /// <summary>
    /// 模擬對方主動送出關閉 frame。
    /// Simulates the peer initiating a close.
    /// </summary>
    public void PushClose() => _incoming.Writer.TryWrite(Frame.Close());

    /// <summary>
    /// 模擬連線突然中斷(對方沒有送關閉 frame 就消失)。
    /// Simulates an abrupt drop: the peer vanishes without a close frame.
    /// </summary>
    public void PushFault() => _incoming.Writer.TryWrite(Frame.Fault());

    private void SetState(WebSocketState state)
    {
        lock (_gate)
        {
            _state = state;
        }
    }

    private enum FrameKind
    {
        Data,
        Close,
        Fault,
    }

    private sealed class Frame
    {
        private Frame(FrameKind kind, byte[] payload, WebSocketMessageType messageType, bool endOfMessage)
        {
            Kind = kind;
            Payload = payload;
            MessageType = messageType;
            EndOfMessage = endOfMessage;
        }

        public FrameKind Kind { get; }

        public byte[] Payload { get; }

        public WebSocketMessageType MessageType { get; }

        public bool EndOfMessage { get; }

        public static Frame Data(byte[] payload, WebSocketMessageType messageType, bool endOfMessage) =>
            new(FrameKind.Data, payload, messageType, endOfMessage);

        public static Frame Close() => new(FrameKind.Close, [], WebSocketMessageType.Close, true);

        public static Frame Fault() => new(FrameKind.Fault, [], WebSocketMessageType.Text, true);
    }
}
