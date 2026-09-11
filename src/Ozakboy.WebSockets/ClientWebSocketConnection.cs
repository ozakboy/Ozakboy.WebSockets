using System.Net.WebSockets;

namespace Ozakboy.WebSockets;

/// <summary>
/// 以 BCL 的 <see cref="ClientWebSocket"/> 實作的連線。
/// An <see cref="IWebSocketConnection"/> backed by the BCL <see cref="ClientWebSocket"/>.
/// </summary>
/// <remarks>
/// <para>
/// 這個類別刻意只做轉呼叫,不含任何判斷邏輯。所有值得測試的行為都在
/// <see cref="WebSocketClient"/> 那一層,這裡愈薄,無法在單元測試中覆蓋的面積就愈小。
/// This type deliberately does nothing but forward calls. All the behaviour worth testing lives in
/// <see cref="WebSocketClient"/>; the thinner this layer is, the smaller the surface that unit tests cannot reach.
/// </para>
/// <para>
/// <b>關於 ping/pong:</b><see cref="ClientWebSocket"/> 會在協定層自動回覆對方的 ping,應用層看不到那些 frame,
/// 也沒有任何 API 可以攔截或代為回覆。網路上「收到 ping 要手動回 pong」的教學針對的是別的函式庫,
/// 在這裡照做只會寫出送不出去的程式碼。存活偵測改用閒置逾時,見
/// <see cref="WebSocketClientOptions.IdleTimeout"/>。
/// <b>On ping/pong:</b> <see cref="ClientWebSocket"/> answers the peer's pings automatically at the protocol layer.
/// The application never sees those frames and has no API to intercept them or reply itself. Tutorials that tell you
/// to send a pong by hand are about other libraries; following them here produces code that sends nothing. Liveness
/// is detected with an idle timeout instead — see <see cref="WebSocketClientOptions.IdleTimeout"/>.
/// </para>
/// </remarks>
public sealed class ClientWebSocketConnection : IWebSocketConnection
{
    private readonly ClientWebSocket _socket;

    /// <summary>
    /// 包住一個既有的 <see cref="ClientWebSocket"/>。
    /// Wraps an existing <see cref="ClientWebSocket"/>.
    /// </summary>
    /// <param name="socket">
    /// 尚未連線的 <see cref="ClientWebSocket"/>;其 <see cref="ClientWebSocket.Options"/> 應已設定完畢。
    /// A not-yet-connected <see cref="ClientWebSocket"/> whose <see cref="ClientWebSocket.Options"/> are already set.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="socket"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="socket"/> is <see langword="null"/>.
    /// </exception>
    public ClientWebSocketConnection(ClientWebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
    }

    /// <inheritdoc />
    public WebSocketState State => _socket.State;

    /// <inheritdoc />
    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        _socket.ConnectAsync(uri, cancellationToken);

    /// <inheritdoc />
    public ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken) =>
        _socket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

    /// <inheritdoc />
    public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        _socket.ReceiveAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        _socket.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    /// <inheritdoc />
    public void Abort() => _socket.Abort();

    /// <inheritdoc />
    public void Dispose() => _socket.Dispose();
}
