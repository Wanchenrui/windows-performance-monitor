namespace PerfMonitor.Contracts;

public static class ContractVersions
{
    public const string V1 = "1.0";
}

public static class ProductVersions
{
    public const string Agent = "0.5.0";
}

public static class ServiceIds
{
    public const string PerfMonitor = "perf-monitor";
}

public static class GroupIds
{
    public const string SystemCpu = "systemCpu";
    public const string Memory = "memory";
    public const string Volumes = "volumes";
    public const string Uptime = "uptime";
    public const string Processes = "processes";
    public const string Sampler = "sampler";
    public const string Self = "self";
}

public static class ProviderIds
{
    public const string SystemCpu = "windows.system-cpu.get-system-times.v1";
    public const string Memory = "windows.memory.global-status.v1";
    public const string Volumes = "windows.volume.drive-info.v1";
    public const string Uptime = "windows.uptime.tick-count.v1";
    public const string Processes = "windows.process.system-diagnostics.v1";
    public const string Sampler = "perfmonitor.scheduler.independent-deadline.v1";
    public const string Self = "perfmonitor.self.process.v1";
}

public static class SourceIds
{
    public const string SystemCpu = "windows.system-cpu-times.v1";
    public const string Memory = "windows.global-memory-status-ex.v1";
    public const string Volumes = "windows.volume-api.v1";
    public const string Uptime = "windows.boot-time.v1";
    public const string ProcessCpu = "windows.get-process-times.v1";
    public const string ProcessMemory = "windows.k32-process-memory-info.v1";
    public const string Scheduler = "perfmonitor.monotonic-scheduler.v1";
    public const string SelfProcess = "perfmonitor.self.process-counters.v1";
    public const string DotnetGc = "dotnet.gc-total-memory.v1";
}

public static class MetricIds
{
    public const string SystemCpuUtilization = "system.cpu.utilization.percent";
    public const string LogicalProcessorCount = "system.cpu.logical_processor.count";
    public const string MemoryUtilization = "system.memory.utilization.percent";
    public const string MemoryUsedBytes = "system.memory.used.bytes";
    public const string MemoryAvailableBytes = "system.memory.available.bytes";
    public const string MemoryTotalBytes = "system.memory.total.bytes";
    public const string VolumeUtilization = "system.volume.utilization.percent";
    public const string VolumeUsedBytes = "system.volume.used.bytes";
    public const string VolumeFreeBytes = "system.volume.free.bytes";
    public const string VolumeTotalBytes = "system.volume.total.bytes";
    public const string UptimeSeconds = "system.uptime.seconds";
    public const string ProcessCpuNormalized = "process.cpu.normalized.percent";
    public const string ProcessCpuCoreEquivalent = "process.cpu.core_equivalent.percent";
    public const string ProcessWorkingSetBytes = "process.memory.working_set.bytes";
    public const string ProcessPrivateBytes = "process.memory.private.bytes";
    public const string SamplerIntervalSeconds = "sampler.interval.seconds";
    public const string SamplerDurationMilliseconds = "sampler.duration.milliseconds";
    public const string SamplerJitterMilliseconds = "sampler.jitter.milliseconds";
    public const string SamplerMissedIntervals = "sampler.missed_intervals.count";
    public const string SamplerSkippedIntervals = "sampler.skipped_intervals.count";
    public const string AgentCpuCoreEquivalent = "agent.cpu.core_equivalent.percent";
    public const string AgentWorkingSetBytes = "agent.memory.working_set.bytes";
    public const string AgentPrivateBytes = "agent.memory.private.bytes";
    public const string AgentGcHeapBytes = "agent.gc.heap.bytes";
}

public static class Units
{
    public const string Percent = "percent";
    public const string Byte = "byte";
    public const string Second = "second";
    public const string Millisecond = "millisecond";
    public const string Count = "count";
}

public static class StableErrorCodes
{
    public const string AccessDenied = "access_denied";
    public const string ProcessExited = "process_exited";
    public const string NotSupported = "not_supported";
    public const string Timeout = "timeout";
    public const string InvalidData = "invalid_data";
    public const string ResourceExhausted = "resource_exhausted";
    public const string ProviderFailure = "provider_failure";
}
