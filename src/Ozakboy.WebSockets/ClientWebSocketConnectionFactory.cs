using System.Net.WebSockets;

namespace Ozakboy.WebSockets;

/// <summary>
/// 依 <see cref="WebSocketClientOptions"/> 產生設定好的 <see cref="ClientWebSocketConnection"/>。
/// Produces <see cref="ClientWebSocketConnection"/> instances configured from <see cref="WebSocketClientOptions"/>.
/// </summary>
/// <remarks>
/// 標頭、子協定與協定層 keep-alive 必須在 <c>ConnectAsync</c> 之前設定 —— 連上之後再改
/// <see cref="ClientWebSocket.Options"/> 會擲出例外,而且每次重連都是一個全新的 socket,
/// 所以這些設定必須在每次建立時重新套用一次。
/// Headers, sub-protocols, and the protocol-level keep-alive must be set before <c>ConnectAsync</c>: mutating
/// <see cref="ClientWebSocket.Options"/> after connecting throws. Since every reconnect uses a brand-new socket,
/// they have to be applied again on each creation.
/// </remarks>
public sealed class ClientWebSocketConnectionFactory : IWebSocketConnectionFactory
{
    private readonly WebSocketClientOptions _options;

    /// <summary>
    /// 建立工廠。
    /// Creates the factory.
    /// </summary>
    /// <param name="options">要套用到每條新連線的設定。The options applied to every new connection.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    public ClientWebSocketConnectionFactory(WebSocketClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public IWebSocketConnection Create()
    {
        var socket = new ClientWebSocket();

        try
        {
            socket.Options.KeepAliveInterval = _options.TransportKeepAliveInterval;

            foreach (var header in _options.RequestHeaders)
            {
                socket.Options.SetRequestHeader(header.Key, header.Value);
            }

            foreach (var subProtocol in _options.SubProtocols)
            {
                socket.Options.AddSubProtocol(subProtocol);
            }

            return new ClientWebSocketConnection(socket);
        }
        catch
        {
            // 設定套用到一半失敗時,這個 socket 已經不會有人接手釋放,必須在這裡收乾淨。
            // If applying the options fails halfway, nobody else will ever dispose this socket, so clean it up here.
            socket.Dispose();
            throw;
        }
    }
}
