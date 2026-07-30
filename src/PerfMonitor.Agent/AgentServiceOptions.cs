namespace PerfMonitor.Agent;

public sealed record AgentServiceOptions(
    AgentOptions Sampling,
    string DataDirectory,
    bool EnableIpc)
{
    public static AgentServiceOptions Parse(
        IReadOnlyList<string> arguments)
    {
        var samplingArguments = new List<string>(arguments.Count);
        string? dataDirectory = null;
        var enableIpc = true;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--data-directory":
                    index++;
                    if (index >= arguments.Count)
                    {
                        throw new ArgumentException(
                            "--data-directory requires a value.");
                    }

                    dataDirectory = Path.GetFullPath(arguments[index]);
                    break;

                case "--disable-ipc":
                    enableIpc = false;
                    break;

                default:
                    samplingArguments.Add(argument);
                    break;
            }
        }

        dataDirectory ??= DefaultDataDirectory();
        return new AgentServiceOptions(
            AgentOptions.Parse(samplingArguments),
            dataDirectory,
            enableIpc);
    }

    private static string DefaultDataDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException(
                "Local application data is unavailable.");
        }

        return Path.Combine(
            localApplicationData,
            "PerfMonitor",
            "data");
    }
}
