namespace Ozakboy.WebSockets;

/// <summary>
/// 收到或送出的訊息是文字還是二進位。
/// Whether a message is text or binary.
/// </summary>
/// <remarks>
/// 刻意不直接沿用 <see cref="System.Net.WebSockets.WebSocketMessageType"/>:那個列舉還包含
/// <c>Close</c>,但關閉 frame 是連線事件而不是資料訊息,不會經由訊息串流交給上層,
/// 讓它出現在公開型別上只會逼呼叫端處理一個永遠不會出現的分支。
/// Deliberately not <see cref="System.Net.WebSockets.WebSocketMessageType"/>: that enum also has <c>Close</c>, but a
/// close frame is a connection event rather than a data message and never reaches the message stream. Exposing it
/// would only force callers to handle a case that cannot occur.
/// </remarks>
public enum WebSocketMessageKind
{
    /// <summary>
    /// UTF-8 文字訊息。
    /// A UTF-8 text message.
    /// </summary>
    Text = 0,

    /// <summary>
    /// 二進位訊息。
    /// A binary message.
    /// </summary>
    Binary = 1,
}
