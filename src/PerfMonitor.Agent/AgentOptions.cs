namespace PerfMonitor.Agent;

public sealed record AgentOptions(
    bool Once,
    TimeSpan? Duration,
    TimeSpan Warmup,
    TimeSpan OutputPeriod,
    string? OutputPath,
    int MaxConcurrency)
{
    public static AgentOptions Parse(IReadOnlyList<string> arguments)
    {
        var once = false;
        TimeSpan? duration = null;
        var warmup = TimeSpan.FromSeconds(3);
        var outputPeriod = TimeSpan.FromSeconds(1);
        string? outputPath = null;
        var maxConcurrency = 3;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--once":
                    once = true;
                    break;
                case "--duration-seconds":
                    duration = TimeSpan.FromSeconds(
                        ParsePositiveDouble(
                            NextValue(arguments, ref index, argument),
                            argument));
                    break;
                case "--warmup-seconds":
                    warmup = TimeSpan.FromSeconds(
                        ParseNonNegativeDouble(
                            NextValue(arguments, ref index, argument),
                            argument));
                    break;
                case "--output-period-ms":
                    outputPeriod = TimeSpan.FromMilliseconds(
                        ParsePositiveDouble(
                            NextValue(arguments, ref index, argument),
                            argument));
                    break;
                case "--output":
                    outputPath = Path.GetFullPath(
                        NextValue(arguments, ref index, argument));
                    break;
                case "--max-concurrency":
                    maxConcurrency = ParsePositiveInt32(
                        NextValue(arguments, ref index, argument),
                        argument);
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown argument: {argument}");
            }
        }

        if (once && duration is not null)
        {
            throw new ArgumentException(
                "--once and --duration-seconds cannot be combined.");
        }

        return new AgentOptions(
            once,
            duration,
            warmup,
            outputPeriod,
            outputPath,
            maxConcurrency);
    }

    private static string NextValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option)
    {
        index++;
        if (index >= arguments.Count)
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return arguments[index];
    }

    private static double ParsePositiveDouble(
        string value,
        string option)
    {
        var parsed = ParseNonNegativeDouble(value, option);
        if (parsed <= 0)
        {
            throw new ArgumentOutOfRangeException(
                option,
                $"{option} must be greater than zero.");
        }

        return parsed;
    }

    private static double ParseNonNegativeDouble(
        string value,
        string option)
    {
        if (!double.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) ||
            !double.IsFinite(parsed) ||
            parsed < 0)
        {
            throw new ArgumentOutOfRangeException(
                option,
                $"{option} must be a finite non-negative number.");
        }

        return parsed;
    }

    private static int ParsePositiveInt32(
        string value,
        string option)
    {
        if (!int.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) ||
            parsed <= 0)
        {
            throw new ArgumentOutOfRangeException(
                option,
                $"{option} must be a positive integer.");
        }

        return parsed;
    }
}
