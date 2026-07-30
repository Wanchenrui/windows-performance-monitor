using System.Globalization;
using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Collectors.Windows;

internal sealed record DiskIoCounterSample(
    bool IsReady,
    double? ReadBytesPerSecond,
    double? WriteBytesPerSecond,
    double? ReadOperationsPerSecond,
    double? WriteOperationsPerSecond);

internal interface IDiskIoCounterSource : IDisposable
{
    DiskIoCounterSample Collect();
}

internal sealed class PdhDiskIoCounterSource : IDiskIoCounterSource
{
    private const uint ErrorSuccess = 0;
    private const uint PdhFormatDouble = 0x0000_0200;
    private const uint PdhCounterStatusValidData = 0x0000_0000;
    private const uint PdhCounterStatusNewData = 0x0000_0001;

    private SafePdhQueryHandle? _query;
    private nint _readBytes;
    private nint _writeBytes;
    private nint _reads;
    private nint _writes;
    private bool _hasBaseline;

    public DiskIoCounterSample Collect()
    {
        EnsureInitialized();
        var query = _query ??
            throw new ObjectDisposedException(
                nameof(PdhDiskIoCounterSource));
        EnsureSuccess(
            NativeMethods.PdhCollectQueryData(query),
            "PdhCollectQueryData");
        if (!_hasBaseline)
        {
            _hasBaseline = true;
            return new DiskIoCounterSample(
                false,
                null,
                null,
                null,
                null);
        }

        return new DiskIoCounterSample(
            true,
            ReadValue(_readBytes),
            ReadValue(_writeBytes),
            ReadValue(_reads),
            ReadValue(_writes));
    }

    public void Dispose()
    {
        _query?.Dispose();
        _query = null;
    }

    private void EnsureInitialized()
    {
        if (_query is not null)
        {
            return;
        }

        var status = NativeMethods.PdhOpenQuery(
            null,
            0,
            out var query);
        if (status != ErrorSuccess)
        {
            query?.Dispose();
            throw new NotSupportedException(
                FormatFailure("PdhOpenQuery", status));
        }

        try
        {
            _readBytes = AddCounter(
                query,
                @"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
            _writeBytes = AddCounter(
                query,
                @"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
            _reads = AddCounter(
                query,
                @"\PhysicalDisk(_Total)\Disk Reads/sec");
            _writes = AddCounter(
                query,
                @"\PhysicalDisk(_Total)\Disk Writes/sec");
            _query = query;
        }
        catch
        {
            query.Dispose();
            throw;
        }
    }

    private static nint AddCounter(
        SafePdhQueryHandle query,
        string counterPath)
    {
        var status = NativeMethods.PdhAddEnglishCounter(
            query,
            counterPath,
            0,
            out var counter);
        if (status != ErrorSuccess)
        {
            throw new NotSupportedException(
                FormatFailure(
                    "PdhAddEnglishCounter",
                    status));
        }

        return counter;
    }

    private static double ReadValue(nint counter)
    {
        var status = NativeMethods.PdhGetFormattedCounterValue(
            counter,
            PdhFormatDouble,
            out _,
            out var value);
        EnsureSuccess(
            status,
            "PdhGetFormattedCounterValue");
        if (value.Status is not
                PdhCounterStatusValidData and not
                PdhCounterStatusNewData ||
            !double.IsFinite(value.DoubleValue) ||
            value.DoubleValue < 0)
        {
            throw new InvalidDataException(
                "PDH returned an invalid disk I/O sample.");
        }

        return value.DoubleValue;
    }

    private static void EnsureSuccess(uint status, string operation)
    {
        if (status != ErrorSuccess)
        {
            throw new InvalidDataException(
                FormatFailure(operation, status));
        }
    }

    private static string FormatFailure(
        string operation,
        uint status) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{operation} failed with 0x{status:X8}.");
}

public sealed class DiskIoProvider : IMetricProvider, IDisposable
{
    private readonly IDiskIoCounterSource _source;

    public DiskIoProvider()
        : this(new PdhDiskIoCounterSource())
    {
    }

    internal DiskIoProvider(IDiskIoCounterSource source)
    {
        _source = source;
    }

    public ProviderDescriptor Descriptor { get; } = new(
        GroupIds.DiskIo,
        ProviderIds.DiskIo,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(750),
        "user",
        "low");

    public ValueTask<ProviderResult> CollectAsync(
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sample = _source.Collect();
        var data = new JsonObject
        {
            ["sampleReady"] = sample.IsReady,
            ["scope"] = "physical-disk-total",
            ["metrics"] = new JsonObject
            {
                [MetricIds.DiskReadBytesPerSecond] =
                    MetricJson.Value(
                        sample.ReadBytesPerSecond,
                        Units.BytePerSecond,
                        SourceIds.DiskIo),
                [MetricIds.DiskWriteBytesPerSecond] =
                    MetricJson.Value(
                        sample.WriteBytesPerSecond,
                        Units.BytePerSecond,
                        SourceIds.DiskIo),
                [MetricIds.DiskReadOperationsPerSecond] =
                    MetricJson.Value(
                        sample.ReadOperationsPerSecond,
                        Units.CountPerSecond,
                        SourceIds.DiskIo),
                [MetricIds.DiskWriteOperationsPerSecond] =
                    MetricJson.Value(
                        sample.WriteOperationsPerSecond,
                        Units.CountPerSecond,
                        SourceIds.DiskIo),
            },
        };
        return ValueTask.FromResult(
            new ProviderResult(
                Descriptor.GroupId,
                Descriptor.ProviderId,
                context.UtcNow,
                AvailabilityStates.Available,
                ProviderCoverage.Complete,
                [],
                data));
    }

    public void Dispose()
    {
        // v0.7.0: PDH owns native query state and must be released
        // after Provider runners have stopped.
        _source.Dispose();
    }
}
