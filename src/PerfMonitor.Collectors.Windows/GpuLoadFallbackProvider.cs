using System.ComponentModel;
using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Collectors.Windows;

/// <summary>Adds Windows engine load only when the isolated hardware reader has no load.</summary>
public sealed class GpuLoadFallbackProvider : IMetricProvider, IAsyncDisposable
{
    private readonly IMetricProvider _primary;
    private readonly IGpuEngineSource _fallback;
    private long? _primaryFailedAt;
    private long? _secondaryFailedAt;
    private string? _primaryStatus;
    private string? _primaryState;
    private string? _secondaryStatus;
    private string _primaryAvailability = AvailabilityStates.Unavailable;
    private IReadOnlyList<ProviderError> _primaryErrors = [];

    public GpuLoadFallbackProvider(IMetricProvider primary) : this(primary, new PdhGpuEngineSource()) { }

    internal GpuLoadFallbackProvider(IMetricProvider primary, IGpuEngineSource fallback)
    {
        if (primary.Descriptor.GroupId != GroupIds.Gpu)
            throw new ArgumentException("The fallback applies only to the GPU group.", nameof(primary));
        _primary = primary;
        _fallback = fallback;
    }

    public ProviderDescriptor Descriptor => _primary.Descriptor;

    public async ValueTask<ProviderResult> CollectAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProviderResult? primary = null;
        if (Remaining(_primaryFailedAt, context) == 0)
        {
            try { primary = await _primary.CollectAsync(context, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RecordPrimary(ProviderResult.Timeout(Descriptor, context.UtcNow), context);
                throw;
            }
            catch (Exception exception) when (exception is IOException or Win32Exception or
                UnauthorizedAccessException or NotSupportedException)
            {
                var code = FailureCode(exception);
                primary = ProviderResult.Failure(Descriptor, context.UtcNow,
                    code == StableErrorCodes.AccessDenied ? AvailabilityStates.PermissionDenied :
                    ExceptionClassifier.Availability(exception), code) with { CollectionState = "worker_failed" };
            }
            if (primary.CollectionState == "paused") return primary;
            RecordPrimary(primary, context);
            // A cancelled primary never starts another reading path. The next
            // scheduled cycle may use PDH while the primary is in its cooldown.
            cancellationToken.ThrowIfCancellationRequested();
            if (HasMetric(primary, MetricIds.GpuLoadMaxPercent))
            {
                _primaryFailedAt = null;
                _secondaryFailedAt = null;
                _secondaryStatus = null;
                _fallback.Dispose();
                return WithReadout(primary, Readout(context));
            }
        }

        var secondaryRemaining = Remaining(_secondaryFailedAt, context);
        GpuEngineSample? sample = null;
        string? secondaryError = null;
        if (secondaryRemaining == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                sample = _fallback.Collect();
                _secondaryStatus = sample.IsReady ? "available" : "warming_up";
                _secondaryFailedAt = null;
            }
            catch (Exception exception) when (exception is IOException or Win32Exception or
                UnauthorizedAccessException or NotSupportedException)
            {
                _secondaryStatus = secondaryError = FailureCode(exception);
                _secondaryFailedAt = context.Timestamp;
                _fallback.Dispose();
            }
        }
        else secondaryError = _secondaryStatus;
        cancellationToken.ThrowIfCancellationRequested();

        // Preserve current primary temperature/device readings only. A result
        // retained from an earlier cycle is never stamped with a new observation.
        var currentData = primary?.ObservedAtUtc is not null &&
            primary.Availability is AvailabilityStates.Available or AvailabilityStates.Partial;
        var data = currentData && primary!.Data is JsonObject current
            ? (JsonObject)current.DeepClone() : EmptyData();
        if (data["metrics"] is not JsonObject metrics) data["metrics"] = metrics = new JsonObject();
        var ready = sample is { IsReady: true, LoadPercent: { } load } &&
            double.IsFinite(load) && load is >= 0 and <= 100;
        if (ready)
        {
            metrics[MetricIds.GpuLoadMaxPercent] = MetricJson.Value(sample!.LoadPercent,
                Units.Percent, SourceIds.WindowsGpuEngine);
            data["loadAggregation"] = "busiest_engine";
            data["readableEngineCount"] = sample.EngineCount;
        }
        data["readout"] = Readout(context);
        var otherReading = currentData && HasMetric(primary!, MetricIds.GpuTemperatureMaxCelsius);
        if (!ready && !otherReading) data["sampleReady"] = false;
        var errors = _primaryErrors.ToList();
        if (secondaryError is not null)
            errors.Add(new(secondaryError, MetricIds.GpuLoadMaxPercent));
        if (ready) errors.RemoveAll(error => error.MetricId == MetricIds.GpuLoadMaxPercent);
        return new(Descriptor.GroupId, Descriptor.ProviderId,
            ready ? context.UtcNow : otherReading ? primary!.ObservedAtUtc : null,
            ready || otherReading ? AvailabilityStates.Partial :
                _primaryAvailability is AvailabilityStates.Available or AvailabilityStates.Partial
                    ? AvailabilityStates.Unavailable : _primaryAvailability,
            ProviderCoverage.Limited, errors.Distinct().ToArray(), data)
        {
            CollectionState = _primaryState ?? "fallback",
        };
    }

    public async ValueTask DisposeAsync()
    {
        try { _fallback.Dispose(); }
        finally
        {
            if (_primary is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync().ConfigureAwait(false);
            else if (_primary is IDisposable disposable) disposable.Dispose();
        }
    }

    private void RecordPrimary(ProviderResult primary, ProviderContext context)
    {
        _primaryAvailability = primary.Availability;
        _primaryErrors = primary.Errors.ToArray();
        _primaryState = primary.CollectionState;
        _primaryStatus = primary.CollectionState ?? primary.Availability;
        if (primary.Availability is AvailabilityStates.Available or AvailabilityStates.Partial &&
            !HasMetric(primary, MetricIds.GpuLoadMaxPercent)) _primaryStatus = "no_load";
        _primaryFailedAt = HasMetric(primary, MetricIds.GpuLoadMaxPercent) ? null : context.Timestamp;
    }

    private JsonObject Readout(ProviderContext context)
    {
        var primaryRoute = _primaryState is "worker_missing" or "worker_backoff" or "worker_failed" or
            "worker_access_denied" or "worker_timeout" or "worker_invalid_data" or "worker_resource_limit"
                ? "hardware_worker" : SourceIds.LibreHardwareMonitor;
        var value = new JsonObject { ["primary"] = primaryRoute, ["primaryStatus"] = _primaryStatus };
        if (_secondaryStatus is not null)
        {
            value["secondary"] = SourceIds.WindowsGpuEngine;
            value["secondaryStatus"] = _secondaryStatus;
        }
        var remaining = Math.Max(Remaining(_primaryFailedAt, context), Remaining(_secondaryFailedAt, context));
        if (remaining > 0) value["retryAfterSeconds"] = remaining;
        return value;
    }

    private static ProviderResult WithReadout(ProviderResult result, JsonObject readout)
    {
        var data = result.Data is JsonObject original ? (JsonObject)original.DeepClone() : EmptyData();
        data["readout"] = readout;
        return result with { Data = data };
    }

    private static JsonObject EmptyData() => new()
    {
        ["metrics"] = new JsonObject
        {
            [MetricIds.GpuDeviceCount] = MetricJson.Value((double?)null, Units.Count, SourceIds.LibreHardwareMonitor),
            [MetricIds.GpuLoadMaxPercent] = MetricJson.Value((double?)null, Units.Percent, SourceIds.WindowsGpuEngine),
            [MetricIds.GpuTemperatureMaxCelsius] = MetricJson.Value((double?)null, Units.Celsius, SourceIds.LibreHardwareMonitor),
        },
        ["devices"] = new JsonArray(),
    };

    private static bool HasMetric(ProviderResult result, string metric) =>
        result.ObservedAtUtc is not null &&
        result.Availability is AvailabilityStates.Available or AvailabilityStates.Partial &&
        !result.Errors.Any(error => error.MetricId == metric) &&
        result.Data is JsonObject data && data["metrics"] is JsonObject metrics &&
        metrics[metric] is JsonObject entry && entry["value"] is JsonValue value &&
        value.TryGetValue<double>(out var number) && double.IsFinite(number) &&
        (metric != MetricIds.GpuLoadMaxPercent || number is >= 0 and <= 100);

    private static double Remaining(long? failedAt, ProviderContext context) => failedAt is { } failed
        ? Math.Max(0, 30 - context.TimeProvider.GetElapsedTime(failed, context.Timestamp).TotalSeconds) : 0;

    private static string FailureCode(Exception exception) => exception is Win32Exception { NativeErrorCode: 5 }
        ? StableErrorCodes.AccessDenied : ExceptionClassifier.StableCode(exception);
}
