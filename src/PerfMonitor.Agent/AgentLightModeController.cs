using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Agent;

internal sealed class AgentLightModeController
{
    private readonly object _gate = new();
    private readonly ProviderSamplingMode _samplingMode;
    private readonly SnapshotAssembler _assembler;
    private readonly ProviderDescriptor[] _hardware;
    private bool? _originalPaused;

    public AgentLightModeController(ProviderSamplingMode samplingMode,
        SnapshotAssembler assembler, IEnumerable<ProviderDescriptor> descriptors)
    {
        _samplingMode = samplingMode;
        _assembler = assembler;
        _hardware = descriptors.Where(descriptor =>
            descriptor.GroupId is GroupIds.Gpu or GroupIds.Sensors).ToArray();
    }

    public LightModeContract Read()
    {
        lock (_gate) return BuildStatus();
    }

    public LightModeContract Set(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (enabled == (_originalPaused is not null)) return BuildStatus();
            if (enabled)
            {
                _originalPaused = _samplingMode.OptionalHardwarePaused;
                _samplingMode.OptionalHardwarePaused = true;
            }
            else
            {
                _samplingMode.OptionalHardwarePaused = _originalPaused!.Value;
                _originalPaused = null;
            }

            // Apply the quality change before returning the setting. Restoring
            // clears the pause marker but waits for a real new observation.
            var utc = TimeProvider.System.GetUtcNow();
            var timestamp = TimeProvider.System.GetTimestamp();
            foreach (var descriptor in _hardware)
            {
                var result = ProviderResult.Paused(descriptor) with
                {
                    CollectionState = _samplingMode.OptionalHardwarePaused ? "paused" : null,
                };
                _assembler.PublishAsync(result, new ProviderExecution(descriptor,
                    utc, utc, utc, 0, 0, 0, 0) { CompletedTimestamp = timestamp },
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            return BuildStatus();
        }
    }

    private LightModeContract BuildStatus() => new()
    {
        Supported = true,
        Enabled = _originalPaused is not null,
        CanRestore = _originalPaused is not null,
        InstanceId = _assembler.InstanceId,
        PausedGroups = _samplingMode.OptionalHardwarePaused
            ? Array.AsReadOnly(_hardware.Select(descriptor => descriptor.GroupId).ToArray()) : [],
        ImpactDescription = _hardware.Length == 0
            ? "本 Agent 未注册 GPU/温度采集，开关不会改变当前采集；仅本次 Agent 会话有效。"
            : "仅暂停 GPU/温度采集请求，基础采集保持原设置；恢复原采集状态，仅本次 Agent 会话有效。已开始的调用可以继续；硬件采集若本来不可用，不承诺额外资源收益。",
    };
}
