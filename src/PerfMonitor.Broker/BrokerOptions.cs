namespace PerfMonitor.Broker;

public enum BrokerRunMode
{
    Console,
    Service,
}
public sealed record BrokerOptions(
    BrokerRunMode Mode,
    string DataDirectory,
    string PolicyPath,
    string DatabasePath,
    string PipeName,
    TimeSpan? Duration)
{
    public static BrokerOptions Parse(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        BrokerRunMode? mode = null;
        string? dataDirectory = null;
        string? policyPath = null;
        string? pipeName = null;
        TimeSpan? duration = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--console":
                    SetMode(ref mode, BrokerRunMode.Console);
                    break;

                case "--service":
                    SetMode(ref mode, BrokerRunMode.Service);
                    break;

                case "--data-directory":
                    dataDirectory = ReadPath(
                        arguments,
                        ref index,
                        "--data-directory");
                    break;

                case "--policy":
                    policyPath = ReadPath(
                        arguments,
                        ref index,
                        "--policy");
                    break;

                case "--pipe-name":
                    pipeName = ReadValue(
                        arguments,
                        ref index,
                        "--pipe-name");
                    break;

                case "--duration-seconds":
                {
                    var value = ReadValue(
                        arguments,
                        ref index,
                        "--duration-seconds");
                    if (!int.TryParse(
                        value,
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var seconds) ||
                        seconds is < 1 or > 3_600)
                    {
                        throw new ArgumentOutOfRangeException(
                            "--duration-seconds");
                    }

                    duration = TimeSpan.FromSeconds(seconds);
                    break;
                }

                default:
                    throw new ArgumentException(
                        $"Unknown Broker argument: {arguments[index]}");
            }
        }

        if (mode is null)
        {
            throw new ArgumentException(
                "Broker requires --console or --service.");
        }

        var defaultDataDirectory = DefaultDataDirectory();
        if (mode == BrokerRunMode.Service &&
            (dataDirectory is not null ||
             policyPath is not null ||
             pipeName is not null ||
             duration is not null))
        {
            throw new ArgumentException(
                "Service mode uses fixed machine paths and Pipe name.");
        }

        dataDirectory ??= defaultDataDirectory;
        policyPath ??= Path.Combine(
            dataDirectory,
            "broker-policy-v1.json");
        pipeName ??= BrokerPipeEndpoint.DefaultPipeName;
        if (string.IsNullOrWhiteSpace(pipeName) ||
            pipeName.Length > 240 ||
            pipeName.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Broker Pipe name is invalid.");
        }

        return new BrokerOptions(
            mode.Value,
            Path.GetFullPath(dataDirectory),
            Path.GetFullPath(policyPath),
            Path.Combine(
                Path.GetFullPath(dataDirectory),
                "broker-v1.db"),
            pipeName,
            duration);
    }

    private static string DefaultDataDirectory()
    {
        var programData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
        {
            throw new InvalidOperationException(
                "ProgramData is unavailable.");
        }

        return Path.Combine(
            programData,
            "PerfMonitor",
            "broker");
    }

    private static void SetMode(
        ref BrokerRunMode? current,
        BrokerRunMode requested)
    {
        if (current is not null)
        {
            throw new ArgumentException(
                "Broker mode may be specified exactly once.");
        }

        current = requested;
    }

    private static string ReadPath(
        IReadOnlyList<string> arguments,
        ref int index,
        string option) =>
        Path.GetFullPath(
            ReadValue(arguments, ref index, option));

    private static string ReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option)
    {
        index++;
        if (index >= arguments.Count ||
            string.IsNullOrWhiteSpace(arguments[index]))
        {
            throw new ArgumentException(
                $"{option} requires a value.");
        }

        return arguments[index];
    }
}
