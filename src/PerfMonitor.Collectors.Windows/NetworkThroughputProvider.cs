using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Collectors.Windows;

internal sealed record NetworkCounterRead(
    string InterfaceId,
    long? BytesReceived,
    long? BytesSent,
    string? ErrorCode);

internal readonly record struct NetworkCounterSample(
    long BytesReceived,
    long BytesSent);

internal interface INetworkCounterSource
{
    IReadOnlyList<NetworkCounterRead> Read();
}

internal sealed class SystemNetworkCounterSource : INetworkCounterSource
{
    public IReadOnlyList<NetworkCounterRead> Read()
    {
        var reads = new List<NetworkCounterRead>();
        foreach (var networkInterface in
            NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.NetworkInterfaceType ==
                    NetworkInterfaceType.Loopback ||
                networkInterface.OperationalStatus !=
                    OperationalStatus.Up)
            {
                continue;
            }

            try
            {
                var statistics =
                    networkInterface.GetIPStatistics();
                reads.Add(new NetworkCounterRead(
                    networkInterface.Id,
                    statistics.BytesReceived,
                    statistics.BytesSent,
                    null));
            }
            catch (Exception exception) when (
                exception is NetworkInformationException or
                InvalidOperationException or
                NotSupportedException)
            {
                reads.Add(new NetworkCounterRead(
                    networkInterface.Id,
                    null,
                    null,
                    ExceptionClassifier.StableCode(exception)));
            }
        }

        return reads;
    }
}

public sealed class NetworkThroughputProvider : IMetricProvider
{
    private readonly INetworkCounterSource _source;
    private IReadOnlyDictionary<string, NetworkCounterSample> _previous =
        new Dictionary<string, NetworkCounterSample>(
            StringComparer.Ordinal);
    private long? _previousTimestamp;

    public NetworkThroughputProvider()
        : this(new SystemNetworkCounterSource())
    {
    }

    internal NetworkThroughputProvider(INetworkCounterSource source)
    {
        _source = source;
    }

    public ProviderDescriptor Descriptor { get; } = new(
        GroupIds.Network,
        ProviderIds.Network,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(750),
        "user",
        "low");

    public ValueTask<ProviderResult> CollectAsync(
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reads = _source.Read();
        var reasons = new Dictionary<string, int>(
            StringComparer.Ordinal);
        var current = new Dictionary<string, NetworkCounterSample>(
            StringComparer.Ordinal);
        foreach (var read in reads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(read.InterfaceId) ||
                read.BytesReceived is not { } bytesReceived ||
                read.BytesSent is not { } bytesSent ||
                bytesReceived < 0 ||
                bytesSent < 0 ||
                !current.TryAdd(
                    read.InterfaceId,
                    new NetworkCounterSample(
                        bytesReceived,
                        bytesSent)))
            {
                Increment(
                    reasons,
                    read.ErrorCode ?? StableErrorCodes.InvalidData);
            }
        }

        double? receiveRate = null;
        double? sendRate = null;
        var rateParticipants = 0;
        var resetCount = 0;
        if (_previousTimestamp is { } previousTimestamp)
        {
            var elapsed = context.TimeProvider.GetElapsedTime(
                previousTimestamp,
                context.Timestamp);
            if (elapsed > TimeSpan.Zero)
            {
                double receiveDelta = 0;
                double sendDelta = 0;
                foreach (var entry in current)
                {
                    if (!_previous.TryGetValue(
                            entry.Key,
                            out var previous))
                    {
                        continue;
                    }

                    if (entry.Value.BytesReceived <
                            previous.BytesReceived ||
                        entry.Value.BytesSent <
                            previous.BytesSent)
                    {
                        Increment(
                            reasons,
                            StableErrorCodes.InvalidData);
                        resetCount++;
                        continue;
                    }

                    receiveDelta +=
                        entry.Value.BytesReceived -
                        previous.BytesReceived;
                    sendDelta +=
                        entry.Value.BytesSent -
                        previous.BytesSent;
                    rateParticipants++;
                }

                if (rateParticipants > 0)
                {
                    receiveRate =
                        receiveDelta / elapsed.TotalSeconds;
                    sendRate =
                        sendDelta / elapsed.TotalSeconds;
                }
            }
            else
            {
                Increment(reasons, StableErrorCodes.InvalidData);
            }
        }

        // v0.7.0: baselines are RAM-only and keyed per interface so
        // adapter churn and counter resets cannot create false spikes.
        if (_previousTimestamp is null ||
            context.Timestamp > _previousTimestamp.Value)
        {
            _previous = current;
            _previousTimestamp = context.Timestamp;
        }

        var skipped =
            reads.Count - current.Count + resetCount;
        var readable = Math.Max(0, current.Count - resetCount);
        var limited = skipped > 0;
        var availability =
            current.Count == 0 && reads.Count > 0
                ? AvailabilityStates.Error
                : limited
                    ? AvailabilityStates.Partial
                    : AvailabilityStates.Available;
        var data = new JsonObject
        {
            ["sampleReady"] =
                receiveRate is not null && sendRate is not null,
            ["rateParticipantCount"] = rateParticipants,
            ["metrics"] = new JsonObject
            {
                [MetricIds.NetworkReceiveBytesPerSecond] =
                    MetricJson.Value(
                        receiveRate,
                        Units.BytePerSecond,
                        SourceIds.Network),
                [MetricIds.NetworkSendBytesPerSecond] =
                    MetricJson.Value(
                        sendRate,
                        Units.BytePerSecond,
                        SourceIds.Network),
                [MetricIds.NetworkActiveInterfaceCount] =
                    MetricJson.Value(
                        current.Count,
                        Units.Count,
                        SourceIds.Network),
            },
        };
        return ValueTask.FromResult(
            new ProviderResult(
                Descriptor.GroupId,
                Descriptor.ProviderId,
                context.UtcNow,
                availability,
                new ProviderCoverage(
                    limited ? "limited" : "complete",
                    reads.Count,
                    readable,
                    skipped,
                    MetricJson.ReadOnlyReasons(reasons)),
                reasons.Keys
                    .Order(StringComparer.Ordinal)
                    .Select(code => new ProviderError(code, null))
                    .ToArray(),
                data));
    }

    private static void Increment(
        IDictionary<string, int> reasons,
        string reason)
    {
        reasons.TryGetValue(reason, out var count);
        reasons[reason] = count + 1;
    }
}
