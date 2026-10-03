using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PerfMonitor.Collectors.Windows;

internal sealed record GpuEngineReading(string InstanceName, uint Status, double Value);
internal sealed record GpuEngineSample(bool IsReady, double? LoadPercent, int EngineCount);

internal interface IGpuEngineSource : IDisposable
{
    GpuEngineSample Collect();
}

/// <summary>Windows GPU engine counters; no vendor library, driver installation or device access.</summary>
internal sealed class PdhGpuEngineSource : IGpuEngineSource
{
    private const uint MoreData = 0x800007D2;
    private const uint NoInstance = 0x800007D1;
    private const int MaxBufferBytes = 1_048_576;
    private const int MaxItems = 4096;
    private static readonly Regex InstancePattern = new(
        @"^pid_(\d+)_(luid_0x[0-9a-f]+_0x[0-9a-f]+_phys_\d+_eng_\d+)_engtype_.+$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private SafePdhQueryHandle? _query;
    private nint _counter;
    private bool _hasBaseline;

    public GpuEngineSample Collect()
    {
        try
        {
            EnsureInitialized();
            EnsureSuccess(NativeMethods.PdhCollectQueryData(_query!), "PdhCollectQueryData");
            if (!_hasBaseline)
            {
                _hasBaseline = true;
                return new(false, null, 0);
            }
            return Aggregate(ReadArray());
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _query?.Dispose();
        _query = null;
        _counter = 0;
        _hasBaseline = false;
    }

    internal static GpuEngineSample Aggregate(IReadOnlyList<GpuEngineReading> readings)
    {
        if (readings.Count > MaxItems) throw new InvalidDataException("Too many GPU counter instances.");
        // Each counter belongs to one process on one physical engine. Never sum
        // different engines, adapters or duplicate counter names into a GPU percentage.
        var processes = new Dictionary<(string Engine, int Pid), double>();
        foreach (var reading in readings)
        {
            if (reading.Status == NoInstance) continue;
            if (reading.Status is not 0 and not 1 || !double.IsFinite(reading.Value) ||
                reading.Value is < 0 or > 100)
                throw new InvalidDataException("GPU counters returned an invalid utilization sample.");
            if (reading.InstanceName.Length > 256)
                throw new InvalidDataException("GPU counter instance name is too long.");
            var match = InstancePattern.Match(reading.InstanceName);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var pid))
                throw new NotSupportedException("The GPU counter instance format is not supported.");
            var key = (match.Groups[2].Value.ToLowerInvariant(), pid);
            processes.TryGetValue(key, out var previous);
            processes[key] = Math.Max(previous, reading.Value);
        }
        if (processes.Count == 0)
            throw new NotSupportedException("Windows did not expose readable GPU engine instances.");
        var engines = processes.GroupBy(item => item.Key.Engine)
            .Select(group => group.Sum(item => item.Value)).ToArray();
        if (engines.Any(value => !double.IsFinite(value) || value > 100))
            throw new InvalidDataException("GPU engine utilization exceeded its physical range.");
        return new(true, engines.Max(), engines.Length);
    }

    private void EnsureInitialized()
    {
        if (_query is not null) return;
        var status = NativeMethods.PdhOpenQuery(null, 0, out var query);
        if (status != 0) { query?.Dispose(); EnsureSuccess(status, "PdhOpenQuery"); }
        if (query is null || query.IsInvalid)
            throw new InvalidDataException("PDH returned an invalid query handle.");
        try
        {
            EnsureSuccess(NativeMethods.PdhAddEnglishCounter(query,
                @"\GPU Engine(*)\Utilization Percentage", 0, out _counter), "PdhAddEnglishCounter");
            _query = query;
        }
        catch { query.Dispose(); throw; }
    }

    private IReadOnlyList<GpuEngineReading> ReadArray()
    {
        uint size = 0;
        var status = NativeMethods.PdhGetFormattedCounterArray(_counter, 0x200, ref size, out _, 0);
        if (status != MoreData) EnsureSuccess(status, "PdhGetFormattedCounterArray(size)");
        if (size == 0) throw new NotSupportedException("Windows exposed no GPU engine counter values.");
        if (size > MaxBufferBytes) throw new InvalidDataException("GPU counter buffer exceeds its limit.");
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            var allocated = size;
            EnsureSuccess(NativeMethods.PdhGetFormattedCounterArray(_counter, 0x200, ref size,
                out var count, buffer), "PdhGetFormattedCounterArray");
            var itemSize = Marshal.SizeOf<NativeMethods.PdhFormattedCounterItem>();
            if (count > MaxItems || size > allocated || (long)count * itemSize > size)
                throw new InvalidDataException("GPU counter array exceeds its buffer or item limit.");
            var readings = new List<GpuEngineReading>(checked((int)count));
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<NativeMethods.PdhFormattedCounterItem>(
                    buffer + checked(index * itemSize));
                var offset = item.Name.ToInt64() - buffer.ToInt64();
                if (offset < (long)count * itemSize || offset < 0 || offset >= size || (offset & 1) != 0)
                    throw new InvalidDataException("GPU counter name lies outside the returned buffer.");
                var maxLength = Math.Min(257, checked((int)((size - offset) / 2)));
                var length = 0;
                while (length < maxLength && Marshal.ReadInt16(item.Name, length * 2) != 0) length++;
                if (length == maxLength || length > 256)
                    throw new InvalidDataException("GPU counter name is not a bounded UTF-16 string.");
                readings.Add(new(Marshal.PtrToStringUni(item.Name, length)!, item.Value.Status,
                    item.Value.DoubleValue));
            }
            return readings;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void EnsureSuccess(uint status, string operation)
    {
        if (status == 0) return;
        if (status is 5 or 0xC0000BDB)
            throw new UnauthorizedAccessException($"{operation} denied GPU counter access.");
        if (status is 0xC0000BB8 or 0xC0000BB9 or 0x800007D5)
            throw new NotSupportedException($"{operation}: GPU counters unavailable (0x{status:X8}).");
        throw new InvalidDataException($"{operation} returned 0x{status:X8}.");
    }
}
