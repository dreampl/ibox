using System.Buffers.Binary;
using System.Text.Json;
using JackiVpn.Domain.Common;

namespace JackiVpn.Ipc;

/// <summary>Length-prefixed (uint32 LE) UTF-8 JSON frames with a hard size cap.</summary>
public static class IpcFraming
{
    public static async Task WriteAsync(Stream stream, IpcEnvelope envelope, SemaphoreSlim writeLock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(writeLock);
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Storage);
        if (body.Length > IpcProtocol.MaxFrameBytes)
        {
            throw new InvalidOperationException($"IPC frame too large ({body.Length} bytes).");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <returns>The next envelope, or null when the peer closed the pipe.</returns>
    public static async Task<IpcEnvelope?> ReadAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > IpcProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException($"Invalid IPC frame length {length}.");
        }

        var body = new byte[length];
        if (!await ReadExactAsync(stream, body, ct).ConfigureAwait(false))
        {
            return null;
        }

        return JsonSerializer.Deserialize<IpcEnvelope>(body, JsonDefaults.Storage)
            ?? throw new InvalidDataException("Empty IPC envelope.");
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return offset == 0 ? false : throw new EndOfStreamException("Pipe closed mid-frame.");
            }

            offset += n;
        }

        return true;
    }

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, JsonDefaults.Storage);

    public static T FromElement<T>(JsonElement? element) =>
        element is { } e
            ? e.Deserialize<T>(JsonDefaults.Storage) ?? throw new InvalidDataException($"Missing {typeof(T).Name} payload.")
            : throw new InvalidDataException($"Missing {typeof(T).Name} payload.");
}
