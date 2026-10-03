namespace PerfMonitor.Contracts;

/// <summary>Process-local control of PerfMonitor's optional hardware collection.</summary>
public sealed record LightModeContract
{
    public bool Supported { get; init; }
    public bool Enabled { get; init; }
    public bool CanRestore { get; init; }
    public bool SessionOnly { get; init; } = true;
    public required string InstanceId { get; init; }
    public IReadOnlyList<string> PausedGroups { get; init; } = [];
    public required string ImpactDescription { get; init; }

    public static LightModeContract Unsupported(string instanceId) => new()
    {
        InstanceId = instanceId,
        ImpactDescription = "当前 Agent 不支持会话轻量模式。",
    };
}
