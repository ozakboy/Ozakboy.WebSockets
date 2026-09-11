namespace Ozakboy.WebSockets;

/// <summary>
/// 一則從連線收到的完整訊息(所有分段都已組裝完畢)。
/// One complete message received from the connection, with all fragments already reassembled.
/// </summary>
/// <remarks>
/// <para>
/// 內容一律以原始形式交出:文字就是原始字串,二進位就是原始位元組。反序列化不在本套件範圍 ——
/// 這是通用的 WebSocket 客戶端,不知道也不應該知道上層的訊息格式。
/// The payload is always handed over raw: text as the original string, binary as the original bytes.
/// Deserialisation is out of scope — this is a general-purpose WebSocket client and has no business knowing the
/// caller's message format.
/// </para>
/// <para>
/// <see cref="ReceivedAt"/> 取自客戶端所使用的 <see cref="TimeProvider"/>,不是 <c>DateTimeOffset.UtcNow</c>,
/// 所以在測試裡它會跟著假時鐘走。
/// <see cref="ReceivedAt"/> comes from the client's <see cref="TimeProvider"/> rather than
/// <c>DateTimeOffset.UtcNow</c>, so it follows the fake clock under test.
/// </para>
/// </remarks>
public sealed class WebSocketMessage
{
    private readonly string? _text;
    private readonly ReadOnlyMemory<byte> _binary;

    private WebSocketMessage(WebSocketMessageKind kind, string? text, ReadOnlyMemory<byte> binary, DateTimeOffset receivedAt)
    {
        Kind = kind;
        _text = text;
        _binary = binary;
        ReceivedAt = receivedAt;
    }

    /// <summary>
    /// 訊息是文字還是二進位。
    /// Whether the message is text or binary.
    /// </summary>
    public WebSocketMessageKind Kind { get; }

    /// <summary>
    /// 收到這則訊息的時間(客戶端 <see cref="TimeProvider"/> 的當下時間)。
    /// When the message arrived, according to the client's <see cref="TimeProvider"/>.
    /// </summary>
    public DateTimeOffset ReceivedAt { get; }

    /// <summary>
    /// 訊息的位元組長度。文字訊息回傳其 UTF-8 位元組數。
    /// The message length in bytes; for text messages, the UTF-8 byte count.
    /// </summary>
    public int ByteCount { get; private init; }

    /// <summary>
    /// 文字內容。<see cref="Kind"/> 不是 <see cref="WebSocketMessageKind.Text"/> 時為 <see langword="null"/>。
    /// The text payload, or <see langword="null"/> when <see cref="Kind"/> is not <see cref="WebSocketMessageKind.Text"/>.
    /// </summary>
    public string? Text => _text;

    /// <summary>
    /// 二進位內容。<see cref="Kind"/> 不是 <see cref="WebSocketMessageKind.Binary"/> 時為空。
    /// The binary payload, or empty when <see cref="Kind"/> is not <see cref="WebSocketMessageKind.Binary"/>.
    /// </summary>
    public ReadOnlyMemory<byte> Binary => _binary;

    /// <summary>
    /// 建立文字訊息。
    /// Creates a text message.
    /// </summary>
    /// <param name="text">文字內容。The text payload.</param>
    /// <param name="receivedAt">收到的時間。When it was received.</param>
    /// <param name="byteCount">
    /// 原始 UTF-8 位元組數;省略時由 <paramref name="text"/> 計算。
    /// The original UTF-8 byte count; computed from <paramref name="text"/> when omitted.
    /// </param>
    /// <returns>訊息。The message.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="text"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    public static WebSocketMessage FromText(string text, DateTimeOffset receivedAt, int? byteCount = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        return new WebSocketMessage(WebSocketMessageKind.Text, text, ReadOnlyMemory<byte>.Empty, receivedAt)
        {
            ByteCount = byteCount ?? System.Text.Encoding.UTF8.GetByteCount(text),
        };
    }

    /// <summary>
    /// 建立二進位訊息。
    /// Creates a binary message.
    /// </summary>
    /// <param name="payload">
    /// 位元組內容。呼叫端必須交出一份不會再被改寫的緩衝區 —— 這個型別只保存參考、不做複製。
    /// The byte payload. The caller must hand over a buffer that will not be mutated afterwards: this type keeps a
    /// reference and does not copy.
    /// </param>
    /// <param name="receivedAt">收到的時間。When it was received.</param>
    /// <returns>訊息。The message.</returns>
    public static WebSocketMessage FromBinary(ReadOnlyMemory<byte> payload, DateTimeOffset receivedAt) =>
        new(WebSocketMessageKind.Binary, null, payload, receivedAt)
        {
            ByteCount = payload.Length,
        };

    /// <summary>
    /// 回傳可讀敘述,包含類型與長度。刻意不輸出內容本身,避免把行情或憑證印進日誌。
    /// Returns a readable description with the kind and size. The payload is deliberately left out so that market
    /// data or credentials do not end up in logs.
    /// </summary>
    /// <returns>可讀敘述。A readable description.</returns>
    public override string ToString() =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{Kind} message, {ByteCount} bytes, received {ReceivedAt:O}");
}
