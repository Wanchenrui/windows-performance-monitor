using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Diagnostics;

namespace PerfMonitor.SnapshotBench;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
    private static long _checksum;

    public static int Main(string[] args)
    {
        try
        {
            if (args is ["--self-check"])
            {
                SelfCheck();
                Console.WriteLine("SnapshotBench self-check passed.");
                return 0;
            }

            var options = Options.Parse(args);
            var sourceBefore = SourceHashes(options.Repository);
            var head = Git(options.Repository, "rev-parse", "HEAD");
            var dirtyBefore = Git(options.Repository, "status", "--porcelain", "--untracked-files=all");
            var binaryHashes = Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
                .Append(Environment.ProcessPath!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(path => Path.GetFileName(path)!, HashFile, StringComparer.Ordinal);
            Directory.CreateDirectory(options.Output);
            var startedUtc = DateTimeOffset.UtcNow;
            var startedTimestamp = Stopwatch.GetTimestamp();
            using var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var cases = new List<CaseResult>();
            var inputs = new List<InputInfo>();
            using (var csv = new StreamWriter(Path.Combine(options.Output, "samples.csv"), false, new UTF8Encoding(false)))
            {
                csv.WriteLine("trial,rows,operation,sample,elapsedTicks,elapsedMicroseconds,allocatedBytes");
                foreach (var rows in new[] { 100, 500, 1000 })
                {
                    var fixture = Fixture.Create(rows);
                    inputs.Add(new(rows, HashBytes(JsonSerializer.SerializeToUtf8Bytes(fixture.Processes, JsonOptions)),
                        Encoding.UTF8.GetByteCount(fixture.Processes.ToJsonString(JsonOptions)),
                        HashBytes(JsonSerializer.SerializeToUtf8Bytes(fixture.CreateAssembler().Read(), JsonOptions))));
                    for (var trial = 1; trial <= options.Trials; trial++)
                    {
                        // Rotate the scenario order across trials. Each scenario has
                        // its own assembler and performs warmup before timed samples.
                        var operations = Operations(fixture);
                        for (var offset = 0; offset < operations.Length; offset++)
                        {
                            var operation = operations[(offset + trial - 1) % operations.Length];
                            var result = Measure(operation, rows, trial, options, csv);
                            cases.Add(result);
                            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                                $"{options.Label} trial={trial} rows={rows} {operation.Name}: alloc={result.MeanAllocatedBytes:F1} B/op p95={result.P95Microseconds:F3} us p99={result.P99Microseconds:F3} us"));
                        }
                    }
                }
            }
            process.Refresh();
            var endedTimestamp = Stopwatch.GetTimestamp();
            var sourceAfter = SourceHashes(options.Repository);
            var dirtyAfter = Git(options.Repository, "status", "--porcelain", "--untracked-files=all");
            if (!sourceBefore.OrderBy(item => item.Key).SequenceEqual(sourceAfter.OrderBy(item => item.Key)) ||
                Git(options.Repository, "rev-parse", "HEAD") != head)
                throw new InvalidOperationException("Relevant source or HEAD changed during measurement.");

            var summary = new
            {
                formatVersion = 1,
                label = options.Label,
                scenario = "synthetic in-process SnapshotAssembler microbenchmark; no real process enumeration, IPC, UI, storage I/O, Worker, Broker or product workload",
                options.Iterations,
                options.Warmup,
                options.Trials,
                rowCounts = new[] { 100, 500, 1000 },
                percentileMethod = "nearest rank: sort ascending, index ceil(p * count) - 1; no outlier removal",
                allocationMethod = "GC.GetAllocatedBytesForCurrentThread around each synchronous operation; excludes fixtures, warmup, CSV and summary writes",
                latencyMethod = "Stopwatch.GetTimestamp around each synchronous operation; includes timer/delegate overhead; timer-control is reported without subtracting it",
                clock = "fixed synthetic TimeProvider: freshness remains stable; production monotonic transitions are tested separately",
                gc = "natural collections only; no forced collection; per-scenario process-wide collection deltas can include runtime activity",
                payloadThree = "three independent Data property reads and full traversals in one read operation; consumers should retain one local view when reusing a payload",
                sourceHead = head,
                dirtyBefore,
                dirtyAfter,
                sourceHashes = sourceBefore,
                binaryHashes,
                framework = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                logicalProcessors = Environment.ProcessorCount,
                gcServer = System.Runtime.GCSettings.IsServerGC,
                gcLatencyMode = System.Runtime.GCSettings.LatencyMode.ToString(),
                stopwatchFrequency = Stopwatch.Frequency,
                startedUtc,
                completedUtc = DateTimeOffset.UtcNow,
                elapsedSeconds = Stopwatch.GetElapsedTime(startedTimestamp, endedTimestamp).TotalSeconds,
                processCpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds,
                inputs,
                cases,
                checksum = _checksum,
                samplesSha256 = HashFile(Path.Combine(options.Output, "samples.csv")),
            };
            File.WriteAllText(Path.Combine(options.Output, "summary.json"),
                JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }),
                new UTF8Encoding(false));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static Operation[] Operations(Fixture fixture)
    {
        var read = fixture.CreateAssembler();
        var published = read.Read();
        var small = fixture.CreateAssembler();
        var large = fixture.CreateAssembler();
        return
        [
            new("timer-control", static () => 1),
            new("read-envelope", () => read.Read().Sequence),
            new("read-payload-once", () => Traverse(read.Read().Groups[GroupIds.Processes].Data)),
            new("read-payload-three", () =>
            {
                var snapshot = read.Read();
                return Traverse(snapshot.Groups[GroupIds.Processes].Data) +
                    Traverse(snapshot.Groups[GroupIds.Processes].Data) +
                    Traverse(snapshot.Groups[GroupIds.Processes].Data);
            }),
            new("serialize-existing", () => JsonSerializer.Serialize(published, JsonOptions).Length),
            new("read-serialize", () => JsonSerializer.Serialize(read.Read(), JsonOptions).Length),
            new("watched-projection", () => Traverse(DiagnosticProcessProjection.ProjectGroup(
                read.Read().Groups[GroupIds.Processes], ["synthetic-0000", "synthetic-0049"]).Data)),
            new("publish-small-held", () =>
            {
                small.PublishAsync(fixture.CpuResult, fixture.Execution(fixture.Cpu), CancellationToken.None).GetAwaiter().GetResult();
                return 1;
            }),
            new("publish-process", () =>
            {
                large.PublishAsync(fixture.ProcessResult, fixture.Execution(fixture.Process), CancellationToken.None).GetAwaiter().GetResult();
                return 1;
            }),
        ];
    }

    private static CaseResult Measure(Operation operation, int rows, int trial, Options options, StreamWriter csv)
    {
        for (var index = 0; index < options.Warmup; index++)
            _checksum = unchecked(_checksum + operation.Action());
        var ticks = new long[options.Iterations];
        var allocated = new long[options.Iterations];
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var start = Stopwatch.GetTimestamp();
        for (var index = 0; index < options.Iterations; index++)
        {
            var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            var before = Stopwatch.GetTimestamp();
            var value = operation.Action();
            ticks[index] = Stopwatch.GetTimestamp() - before;
            allocated[index] = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
            _checksum = unchecked(_checksum + value);
        }
        var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
        var collections = new[] { GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2 };
        for (var index = 0; index < options.Iterations; index++)
            csv.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{trial},{rows},{operation.Name},{index + 1},{ticks[index]},{Microseconds(ticks[index]):F6},{allocated[index]}"));
        var sorted = ticks.Order().ToArray();
        return new(trial, rows, operation.Name, options.Iterations,
            allocated.Sum(), allocated.Average(), Microseconds(sorted[0]),
            ticks.Average() * 1_000_000 / Stopwatch.Frequency,
            Microseconds(NearestRank(sorted, 0.5)), Microseconds(NearestRank(sorted, 0.95)),
            Microseconds(NearestRank(sorted, 0.99)), Microseconds(sorted[^1]), elapsed, collections);
    }

    private static long Traverse(JsonNode? data)
    {
        if (data is not JsonArray rows) return 0;
        long sum = rows.Count;
        foreach (var row in rows.OfType<JsonObject>())
        {
            sum += (row["name"]?.GetValue<string>()?.Length ?? 0);
            // The bounded diagnostic projection deliberately has no PID.
            sum += row["identity"]?["pid"]?.GetValue<int>() ?? 0;
            var value = row["metrics"]?[MetricIds.ProcessCpuNormalized]?["value"];
            if (value is JsonValue number && number.TryGetValue<double>(out var cpu))
                sum += (long)cpu;
        }
        return sum;
    }

    private static double Microseconds(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;

    private static long NearestRank(long[] sorted, double percentile) =>
        sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];

    private static void SelfCheck()
    {
        var sorted = Enumerable.Range(1, 1000).Select(static value => (long)value).ToArray();
        if (NearestRank(sorted, 0.95) != 950 || NearestRank(sorted, 0.99) != 990 ||
            NearestRank([17], 0.99) != 17 || Microseconds(Stopwatch.Frequency) != 1_000_000)
            throw new InvalidOperationException("Percentile or unit regression.");
        var fixture = Fixture.Create(100);
        if (((JsonArray)fixture.ProcessResult.Data!).Count != 100 || Traverse(fixture.ProcessResult.Data) <= 100)
            throw new InvalidOperationException("Synthetic payload regression.");
    }

    private static Dictionary<string, string> SourceHashes(string repository)
    {
        var directories = new[] { "src/PerfMonitor.Core", "src/PerfMonitor.Contracts", "src/PerfMonitor.Diagnostics", "tools/PerfMonitor.SnapshotBench" };
        return directories.SelectMany(directory => Directory.EnumerateFiles(Path.Combine(repository, directory), "*", SearchOption.AllDirectories))
            .Where(path => Path.GetExtension(path) is ".cs" or ".csproj" &&
                !Path.GetRelativePath(repository, path).Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .Append(Path.Combine(repository, "Directory.Build.props"))
            .Append(Path.Combine(repository, "global.json"))
            .Order(StringComparer.Ordinal)
            .ToDictionary(path => Path.GetRelativePath(repository, path).Replace('\\', '/'), HashFile, StringComparer.Ordinal);
    }

    private static string Git(string repository, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git failed: {error}");
        return output.TrimEnd();
    }

    private static string HashFile(string path) => HashBytes(File.ReadAllBytes(path));
    private static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record Operation(string Name, Func<long> Action);
    private sealed record InputInfo(int Rows, string ProviderResultSha256, int ProviderJsonBytes, string InitialSnapshotSha256);
    private sealed record CaseResult(int Trial, int Rows, string Operation, int Samples, long TotalAllocatedBytes,
        double MeanAllocatedBytes, double MinMicroseconds, double MeanMicroseconds, double P50Microseconds,
        double P95Microseconds, double P99Microseconds, double MaxMicroseconds, double MeasuredLoopSeconds, int[] GcCollections);

    private sealed record Options(string Repository, string Output, string Label, int Iterations, int Warmup, int Trials)
    {
        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length || !values.TryAdd(args[index], args[index + 1]))
                    throw new ArgumentException("Use --repository PATH --output PATH --label NAME [--iterations N --warmup N --trials N].");
            }
            if (values.Keys.Any(key => key is not ("--repository" or "--output" or "--label" or "--iterations" or "--warmup" or "--trials")))
                throw new ArgumentException("Unknown option.");
            var repository = Path.GetFullPath(values["--repository"]);
            var output = Path.GetFullPath(values["--output"]);
            var relative = Path.GetRelativePath(Path.Combine(repository, "artifacts"), output);
            if (!relative.StartsWith("wave03-", StringComparison.Ordinal) || relative.Contains("..", StringComparison.Ordinal))
                throw new ArgumentException("Output must be a new artifacts/wave03-* directory.");
            if (Directory.Exists(output)) throw new IOException("The measurement output already exists; preserve prior evidence.");
            var iterations = ReadNumber("--iterations", 1000, 100, 100_000);
            var warmup = ReadNumber("--warmup", 100, 1, 10_000);
            var trials = ReadNumber("--trials", 3, 1, 20);
            return new(repository, output, values["--label"], iterations, warmup, trials);

            int ReadNumber(string name, int fallback, int min, int max) => values.TryGetValue(name, out var text) &&
                int.TryParse(text, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
                    ? value : !values.ContainsKey(name) ? fallback : throw new ArgumentOutOfRangeException(name);
        }
    }

    private sealed class SyntheticTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        public override long GetTimestamp() => 0;
        public override long TimestampFrequency => Stopwatch.Frequency;
    }

    private sealed class Fixture
    {
        private readonly SyntheticTimeProvider _time = new();
        public ProviderDescriptor Cpu { get; } = new(GroupIds.SystemCpu, ProviderIds.SystemCpu, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500), "user", "low");
        public ProviderDescriptor Memory { get; } = new(GroupIds.Memory, ProviderIds.Memory, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500), "user", "low");
        public ProviderDescriptor Process { get; } = new(GroupIds.Processes, ProviderIds.Processes, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), "user", "medium");
        public required JsonArray Processes { get; init; }
        public ProviderResult ProcessResult => new(Process.GroupId, Process.ProviderId, _time.GetUtcNow(), AvailabilityStates.Available,
            new ProviderCoverage("complete", Processes.Count, Processes.Count, 0), [], Processes);
        public ProviderResult CpuResult => new(Cpu.GroupId, Cpu.ProviderId, _time.GetUtcNow(), AvailabilityStates.Available,
            ProviderCoverage.Complete, [], new JsonObject { ["metrics"] = new JsonObject
                { [MetricIds.SystemCpuUtilization] = MetricJson.Value(20.0, Units.Percent, SourceIds.SystemCpu) } });
        public ProviderExecution Execution(ProviderDescriptor descriptor) => new(descriptor, _time.GetUtcNow(), _time.GetUtcNow(), _time.GetUtcNow(), 0, 0, 0, 0)
            { StartedTimestamp = 0, CompletedTimestamp = 0 };

        public SnapshotAssembler CreateAssembler()
        {
            var assembler = new SnapshotAssembler([Cpu, Memory, Process], _time, "snapshot-bench-synthetic");
            assembler.PublishAsync(CpuResult, Execution(Cpu), CancellationToken.None).GetAwaiter().GetResult();
            assembler.PublishAsync(new(Memory.GroupId, Memory.ProviderId, _time.GetUtcNow(), AvailabilityStates.Available, ProviderCoverage.Complete, [],
                new JsonObject { ["metrics"] = new JsonObject { [MetricIds.MemoryUtilization] = MetricJson.Value(30.0, Units.Percent, SourceIds.Memory) } }),
                Execution(Memory), CancellationToken.None).GetAwaiter().GetResult();
            assembler.PublishAsync(ProcessResult, Execution(Process), CancellationToken.None).GetAwaiter().GetResult();
            return assembler;
        }

        public static Fixture Create(int count)
        {
            var data = new JsonArray();
            for (var index = 0; index < count; index++)
                data.Add(new JsonObject
                {
                    ["identity"] = new JsonObject { ["pid"] = 1000 + index, ["creationTimeTicks"] = 639_000_000_000_000_000L + index },
                    ["name"] = $"synthetic-{index:D4}", ["cpuReady"] = true,
                    ["metrics"] = new JsonObject
                    {
                        [MetricIds.ProcessCpuNormalized] = MetricJson.Value(index % 100 * 0.5, Units.Percent, SourceIds.ProcessCpu),
                        [MetricIds.ProcessWorkingSetBytes] = MetricJson.Value(10_000_000L + index * 1000L, Units.Byte, SourceIds.ProcessMemory),
                        [MetricIds.ProcessPrivateBytes] = MetricJson.Value(20_000_000L + index * 1000L, Units.Byte, SourceIds.ProcessMemory),
                    },
                });
            return new Fixture { Processes = data };
        }
    }
}
