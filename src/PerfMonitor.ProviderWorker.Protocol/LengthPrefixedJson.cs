using System.Buffers.Binary;
using System.Text.Json;

namespace PerfMonitor.ProviderWorker.Protocol;

public readonly record struct FrameReadResult<T>(
    bool HasValue,
    T? Value)
    where T : class;

public static class LengthPrefixedJson
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
        };

    public static async ValueTask WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(value);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            value,
            SerializerOptions);
        if (payload.Length == 0 ||
            payload.Length > ProviderWorkerProtocol.MaxMessageBytes)
        {
            throw new InvalidDataException(
                $"Worker frame length {payload.Length} is invalid.");
        }

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            prefix,
            payload.Length);
        await stream.WriteAsync(
            prefix,
            cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(
            payload,
            cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public static async ValueTask<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
        where T : class
    {
        var result = await TryReadAsync<T>(
            stream,
            cancellationToken).ConfigureAwait(false);
        return result.HasValue
            ? result.Value!
            : throw new EndOfStreamException(
                "Worker stream closed before a frame.");
    }

    public static async ValueTask<FrameReadResult<T>> TryReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = new byte[sizeof(int)];
        var firstRead = await stream.ReadAsync(
            prefix.AsMemory(0, 1),
            cancellationToken).ConfigureAwait(false);
        if (firstRead == 0)
        {
            return new FrameReadResult<T>(false, null);
        }

        await stream.ReadExactlyAsync(
            prefix.AsMemory(1),
            cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 ||
            length > ProviderWorkerProtocol.MaxMessageBytes)
        {
            throw new InvalidDataException(
                $"Worker frame length {length} is invalid.");
        }

        var payload = GC.AllocateUninitializedArray<byte>(length);
        await stream.ReadExactlyAsync(
            payload,
            cancellationToken).ConfigureAwait(false);
        var value = JsonSerializer.Deserialize<T>(
            payload,
            SerializerOptions);
        if (value is null)
        {
            throw new InvalidDataException(
                "Worker frame deserialized to null.");
        }

        return new FrameReadResult<T>(true, value);
    }
}
