using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using PerfMonitor.Contracts;

namespace PerfMonitor.Ipc.NamedPipes;

public static class LengthPrefixedJson
{
    public static async ValueTask<JsonDocument?> ReadAsync(
        Stream stream,
        int maxMessageSize,
        CancellationToken cancellationToken)
    {
        ValidateMaximum(maxMessageSize);
        var header = new byte[sizeof(uint)];
        var headerBytes = await ReadExactlyOrEofAsync(
            stream,
            header,
            cancellationToken).ConfigureAwait(false);
        if (headerBytes == 0)
        {
            return null;
        }

        if (headerBytes != header.Length)
        {
            throw new IpcProtocolException(
                IpcErrorCodes.InvalidRequest);
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > maxMessageSize)
        {
            throw new IpcProtocolException(
                IpcErrorCodes.MessageTooLarge);
        }

        var payload = ArrayPool<byte>.Shared.Rent(checked((int)length));
        try
        {
            var payloadMemory = payload.AsMemory(0, checked((int)length));
            var payloadBytes = await ReadExactlyOrEofAsync(
                stream,
                payloadMemory,
                cancellationToken).ConfigureAwait(false);
            if (payloadBytes != payloadMemory.Length)
            {
                throw new IpcProtocolException(
                    IpcErrorCodes.InvalidRequest);
            }

            try
            {
                // JsonDocument may retain ReadOnlyMemory for its lifetime.
                // Detach from the pooled buffer before returning it below.
                var ownedPayload = payloadMemory.ToArray();
                return JsonDocument.Parse(
                    ownedPayload,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = IpcProtocol.MaxJsonDepth,
                    });
            }
            catch (JsonException)
            {
                throw new IpcProtocolException(
                    IpcErrorCodes.InvalidRequest);
            }
        }
        finally
        {
            payload.AsSpan(0, checked((int)length)).Clear();
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    public static async ValueTask WriteAsync<T>(
        Stream stream,
        T message,
        int maxMessageSize,
        CancellationToken cancellationToken)
    {
        ValidateMaximum(maxMessageSize);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            message,
            IpcJson.Options);
        if (payload.Length == 0 || payload.Length > maxMessageSize)
        {
            throw new IpcProtocolException(
                IpcErrorCodes.MessageTooLarge);
        }

        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            header,
            checked((uint)payload.Length));
        await stream.WriteAsync(header, cancellationToken)
            .ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ValidateMaximum(int maxMessageSize)
    {
        if (maxMessageSize is <= 0 or >
            IpcProtocol.AbsoluteMaxMessageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxMessageSize));
        }
    }

    private static async ValueTask<int> ReadExactlyOrEofAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer[total..],
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }

            total = checked(total + read);
        }

        return total;
    }
}
