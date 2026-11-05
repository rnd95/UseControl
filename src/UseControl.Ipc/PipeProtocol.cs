using System.Buffers.Binary;
using System.Text.Json;

namespace UseControl.Ipc;

/// <summary>
/// One JSON message per frame: a 4-byte little-endian length, then UTF-8 JSON. One request and one
/// response per connection.
/// </summary>
public static class PipeProtocol
{
    /// <summary>The control pipe of the service: \\.\pipe\UseControl.Control.</summary>
    public const string PipeName = "UseControl.Control";

    /// <summary>Large enough for any config or audit, small enough that a bad client cannot exhaust memory.</summary>
    public const int MaxMessageBytes = 8 * 1024 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options);
        if (body.Length > MaxMessageBytes) throw new InvalidDataException("Message too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessageBytes) throw new InvalidDataException($"Bad message length {length}.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body, IpcJson.Options) ?? throw new InvalidDataException("Empty message.");
    }
}
