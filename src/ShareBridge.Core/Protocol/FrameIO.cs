using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace ShareBridge.Core.Protocol;

/// <summary>
/// Length-prefixed framing over a TCP stream: 4-byte big-endian length, then UTF-8 JSON.
/// Raw file bytes are streamed outside of frames, with sizes known from the offer.
/// </summary>
public static class FrameIO
{
    public static async Task WriteFrameAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json);
        if (payload.Length > Wire.MaxFrameBytes)
            throw new InvalidOperationException($"Frame of {payload.Length} bytes exceeds the {Wire.MaxFrameBytes} byte limit.");

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadFrameBytesAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > Wire.MaxFrameBytes)
            throw new InvalidDataException($"Declared frame length {length} is out of range.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return payload;
    }

    public static async Task<(string Kind, byte[] Payload)> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var payload = await ReadFrameBytesAsync(stream, ct).ConfigureAwait(false);
        var header = JsonSerializer.Deserialize<FrameHeader>(payload, Wire.Json)
                     ?? throw new InvalidDataException("Frame is not a JSON object.");
        return (header.Kind, payload);
    }

    public static T Decode<T>(byte[] payload) =>
        JsonSerializer.Deserialize<T>(payload, Wire.Json)
        ?? throw new InvalidDataException($"Frame could not be read as {typeof(T).Name}: {Preview(payload)}");

    private static string Preview(byte[] payload) =>
        Encoding.UTF8.GetString(payload, 0, Math.Min(payload.Length, 200));
}
