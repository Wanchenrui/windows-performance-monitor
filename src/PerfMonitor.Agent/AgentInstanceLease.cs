namespace PerfMonitor.Agent;

internal sealed class AgentInstanceLease : IDisposable
{
    private readonly FileStream _stream;

    private AgentInstanceLease(FileStream stream)
    {
        _stream = stream;
    }

    public static AgentInstanceLease Acquire(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var lockPath = Path.Combine(dataDirectory, ".agent.lock");
        try
        {
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            return new AgentInstanceLease(stream);
        }
        catch (IOException exception)
        {
            throw new IOException(
                "agent_already_running",
                exception);
        }
    }

    public void Dispose() => _stream.Dispose();
}
