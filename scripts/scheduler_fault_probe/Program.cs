using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

if (args.Length != 3 || !bool.TryParse(args[1], out var reserveBasicGroups) ||
    !int.TryParse(args[2], out var durationSeconds) || durationSeconds is < 1 or > 60)
{
    throw new ArgumentException("Arguments: fresh-output-directory reserve-basic-groups duration-seconds");
}
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output) || File.Exists(output))
{
    throw new IOException("Never overwrite prior measurement output.");
}
Directory.CreateDirectory(output);
const int maxConcurrency = 5;
const int blockerCount = 5;
const double progressBudgetMilliseconds = 250;
var basicGroups = new[] { GroupIds.SystemCpu, GroupIds.Memory, GroupIds.Network, GroupIds.DiskIo };
var events = new ConcurrentQueue<ProbeEvent>();
var timer = Stopwatch.StartNew();
var state = new ProbeState(timer, events);
using var release = new ManualResetEventSlim();
var blockers = Enumerable.Range(0, blockerCount).Select(index =>
    new ProbeProvider($"probe.blocked.{index}", state, release)).ToArray();
var basic = basicGroups.Select(group => new ProbeProvider(group, state, null)).ToArray();
var providers = blockers.Concat(basic).Cast<IMetricProvider>().ToArray();
var sink = new ProbeSink(state);
var scheduler = CreateScheduler(providers, sink, reserveBasicGroups, basicGroups);
var contractAssembler = CreateContractAssembler(providers);
var resources = new List<ResourceSample>();
using var process = Process.GetCurrentProcess();
double observationStart = 0;
double observationEnd = 0;
double stopMilliseconds = 0;
var activeAtStop = 0;
var disposedStartedBlockersBeforeRelease = 0;
var schedulerDisposed = false;
try
{
    scheduler.Start();
    var expectedStarted = reserveBasicGroups ? maxConcurrency - 1 : maxConcurrency;
    var deadline = timer.Elapsed.TotalSeconds + 5;
    while (blockers.Count(provider => provider.Calls > 0) < expectedStarted)
    {
        if (timer.Elapsed.TotalSeconds >= deadline)
        {
            throw new TimeoutException("Fault fixture did not occupy the expected slots.");
        }
        await Task.Delay(10);
    }
    observationStart = timer.Elapsed.TotalMilliseconds;
    state.Record("observation_start", "", "", 0, 0, 0);
    resources.Add(ReadResources());
    var observationDeadline = timer.Elapsed.TotalSeconds + durationSeconds;
    while (timer.Elapsed.TotalSeconds < observationDeadline)
    {
        await Task.Delay(100);
        resources.Add(ReadResources());
    }
    observationEnd = timer.Elapsed.TotalMilliseconds;
    state.Record("observation_end", "", "", 0, 0, 0);
    var stopStart = timer.Elapsed.TotalMilliseconds;
    await scheduler.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    stopMilliseconds = timer.Elapsed.TotalMilliseconds - stopStart;
    activeAtStop = state.Active;
    await scheduler.DisposeAsync();
    schedulerDisposed = true;
    disposedStartedBlockersBeforeRelease = blockers.Count(provider =>
        provider.Calls > 0 && provider.Disposed);
}
finally
{
    // The fixture, not the scheduler, ends its deliberately non-cooperative calls.
    release.Set();
    if (!schedulerDisposed) await scheduler.DisposeAsync();
    await Task.WhenAll(blockers.Select(provider => provider.DisposedSignal.Task))
        .WaitAsync(TimeSpan.FromSeconds(5));
}
var orderedEvents = events.OrderBy(item => item.ElapsedMilliseconds).ToArray();
var progress = basicGroups.Select(group =>
{
    var publications = orderedEvents.Where(item => item.Kind == "published" &&
        item.GroupId == group && item.Availability == AvailabilityStates.Available &&
        item.ElapsedMilliseconds >= observationStart && item.ElapsedMilliseconds <= observationEnd)
        .ToArray();
    // Include both window edges. Zero samples therefore means a whole-window gap.
    var times = new[] { observationStart }.Concat(publications.Select(item => item.ElapsedMilliseconds))
        .Append(observationEnd).ToArray();
    var maximumGap = times.Zip(times.Skip(1), (left, right) => right - left).Max();
    return new
    {
        groupId = group,
        successfulSamples = publications.Length,
        maximumGapMilliseconds = maximumGap,
        maximumJitterMilliseconds = publications.Length == 0 ? (double?)null : publications.Max(item => item.JitterMilliseconds),
        progressBudgetMilliseconds,
        withinBudget = publications.Length > 0 && maximumGap <= progressBudgetMilliseconds,
    };
}).ToArray();
var cpuSeconds = resources[^1].CpuSeconds - resources[0].CpuSeconds;
var coveredSeconds = (resources[^1].ElapsedMilliseconds - resources[0].ElapsedMilliseconds) / 1000;
var report = new
{
    schemaVersion = 1,
    purpose = "Finite synthetic scheduler fault reproduction; not a whole-product resource or platform verdict",
    completed = true,
    environment = new
    {
        os = RuntimeInformation.OSDescription,
        runtime = Environment.Version.ToString(),
        logicalProcessors = Environment.ProcessorCount,
    },
    parameters = new
    {
        reserveBasicGroups,
        maxConcurrency,
        blockerCount,
        blockerPeriodMilliseconds = 50,
        blockerTimeoutMilliseconds = 40,
        basicPeriodMilliseconds = 100,
        basicTimeoutMilliseconds = progressBudgetMilliseconds,
        requestedDurationSeconds = durationSeconds,
        synchronousBlockers = true,
    },
    window = new { observationStartMilliseconds = observationStart, observationEndMilliseconds = observationEnd, coveredSeconds },
    progress,
    allBasicGroupsWithinBudget = progress.All(item => item.withinBudget),
    concurrency = new
    {
        maximumActive = state.MaximumActive,
        maximumActiveBlockers = state.MaximumActiveBlockers,
        startedBlockers = blockers.Count(provider => provider.Calls > 0),
        perProviderMaximum = providers.Cast<ProbeProvider>().Max(provider => provider.MaximumActive),
        cancellationsIgnored = blockers.Sum(provider => provider.CancellationsIgnored),
    },
    shutdown = new
    {
        stopMilliseconds,
        activeAtStop,
        disposedStartedBlockersBeforeRelease,
        activeAfterFixtureRelease = state.Active,
        allProvidersDisposed = providers.Cast<ProbeProvider>().All(provider => provider.Disposed),
    },
    resources = new
    {
        samples = resources.Count,
        cpuSeconds,
        coveredSeconds,
        cpuCoreEquivalentPercent = 100 * cpuSeconds / coveredSeconds,
        cpuMachineNormalizedPercent = 100 * cpuSeconds / coveredSeconds / Environment.ProcessorCount,
        privateBytesPeak = resources.Max(sample => sample.PrivateBytes),
        workingSetBytesPeak = resources.Max(sample => sample.WorkingSetBytes),
        threadPeak = resources.Max(sample => sample.Threads),
        handlePeak = resources.Max(sample => sample.Handles),
    },
    limitations = new[]
    {
        "Synthetic providers run in a console harness; resource figures include the harness and observer.",
        "Ambient user load and thread-pool scheduling are uncontrolled; the measured progress budget is not a hard real-time guarantee.",
        "The reserved group itself, a stalled result sink, or thread-pool exhaustion may still stop progress.",
        "The fixture releases its calls after Stop; permanently stuck native calls still require the existing process boundary.",
    },
};
await File.WriteAllTextAsync(Path.Combine(output, "probe.json"), JsonSerializer.Serialize(report,
    new JsonSerializerOptions { WriteIndented = true }));
await File.WriteAllLinesAsync(Path.Combine(output, "events.csv"), new[]
{
    "elapsedMilliseconds,kind,groupId,availability,activeCalls,durationMilliseconds,jitterMilliseconds,skippedBusyIntervalsTotal,errorCode",
}.Concat(orderedEvents.Select(item => string.Join(",",
    Number(item.ElapsedMilliseconds), item.Kind, item.GroupId, item.Availability, item.ActiveCalls,
    Number(item.DurationMilliseconds), Number(item.JitterMilliseconds), item.SkippedBusyIntervalsTotal, item.ErrorCode))));
await File.WriteAllLinesAsync(Path.Combine(output, "process-resources.csv"), new[]
{
    "elapsedMilliseconds,cpuSeconds,privateBytes,workingSetBytes,threads,handles",
}.Concat(resources.Select(sample => string.Join(",", Number(sample.ElapsedMilliseconds), Number(sample.CpuSeconds),
    sample.PrivateBytes, sample.WorkingSetBytes, sample.Threads, sample.Handles))));
if (sink.CapacityTimeout is { } capacity)
{
    // Contract proof uses the actual result captured during this run. Extra
    // descriptors fill the snapshot's required groups without starting calls.
    // Serialization happens after the timed window, Stop and fixture cleanup.
    await contractAssembler.PublishAsync(capacity.Result, capacity.Execution, CancellationToken.None);
    await File.WriteAllTextAsync(Path.Combine(output, "capacity-failure.snapshot.json"),
        JsonSerializer.Serialize(contractAssembler.Read(), ContractJson.Options));
    await File.WriteAllTextAsync(Path.Combine(output, "capacity-failure.execution.json"),
        JsonSerializer.Serialize(new
        {
            groupId = capacity.Result.GroupId,
            result = capacity.Result,
            execution = capacity.Execution,
            startedTimestamp = capacity.Execution.StartedTimestamp,
            completedTimestamp = capacity.Execution.CompletedTimestamp,
        }, ContractJson.Options));
}
Console.WriteLine($"basicWithinBudget={report.allBasicGroupsWithinBudget}; maximumActive={state.MaximumActive}; stopMs={Number(stopMilliseconds)}");
return 0;

ResourceSample ReadResources()
{
    process.Refresh();
    return new(timer.Elapsed.TotalMilliseconds, process.TotalProcessorTime.TotalSeconds,
        process.PrivateMemorySize64, process.WorkingSet64, process.Threads.Count, process.HandleCount);
}

static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

static SnapshotAssembler CreateContractAssembler(IMetricProvider[] providers)
{
    var required = new[] { GroupIds.SystemCpu, GroupIds.Memory, GroupIds.Volumes,
        GroupIds.Uptime, GroupIds.Processes };
    var descriptors = providers.Select(provider => provider.Descriptor).ToList();
    descriptors.AddRange(required.Where(group => descriptors.All(item => item.GroupId != group))
        .Select(group => new ProviderDescriptor(group, $"probe.{group}.v1", TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1), "user", "low")));
    return new SnapshotAssembler(descriptors);
}

static ProviderScheduler CreateScheduler(IMetricProvider[] providers, IProviderResultSink sink,
    bool reserveBasicGroups, string[] basicGroups)
{
    if (!reserveBasicGroups)
    {
        return new ProviderScheduler(providers, sink, 5);
    }
    // One frozen fixture can measure the old five-argument API and the new reservation API.
    // No production code uses reflection.
    var constructor = typeof(ProviderScheduler).GetConstructors().SingleOrDefault(ctor =>
        ctor.GetParameters().Length == 6 && ctor.GetParameters()[5].Name == "reservedGroupIds") ??
        throw new InvalidOperationException("This scheduler does not expose the reservation API.");
    return (ProviderScheduler)constructor.Invoke([providers, sink, 5, null, null, basicGroups]);
}

internal sealed record ResourceSample(double ElapsedMilliseconds, double CpuSeconds,
    long PrivateBytes, long WorkingSetBytes, int Threads, int Handles);

internal sealed record ProbeEvent(double ElapsedMilliseconds, string Kind, string GroupId,
    string Availability, int ActiveCalls, double DurationMilliseconds, double JitterMilliseconds,
    long SkippedBusyIntervalsTotal, string ErrorCode);

internal sealed class ProbeState(Stopwatch timer, ConcurrentQueue<ProbeEvent> events)
{
    public int Active;
    public int ActiveBlockers;
    public int MaximumActive;
    public int MaximumActiveBlockers;

    public void Record(string kind, string group, string availability, double duration,
        double jitter, long skipped, string error = "") => events.Enqueue(new(
            timer.Elapsed.TotalMilliseconds, kind, group, availability, Volatile.Read(ref Active),
            duration, jitter, skipped, error));

    public static void UpdateMaximum(ref int maximum, int value)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref maximum);
            if (observed >= value) return;
        }
        while (Interlocked.CompareExchange(ref maximum, value, observed) != observed);
    }
}

internal sealed class ProbeProvider : IMetricProvider, IDisposable
{
    private readonly ProbeState _state;
    private readonly ManualResetEventSlim? _release;
    private int _active;
    public int Calls;
    public int MaximumActive;
    public int CancellationsIgnored;
    public bool Disposed;
    public TaskCompletionSource DisposedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ProbeProvider(string group, ProbeState state, ManualResetEventSlim? release)
    {
        _state = state;
        _release = release;
        Descriptor = new(group, $"probe.{group}.v1", TimeSpan.FromMilliseconds(release is null ? 100 : 50),
            TimeSpan.FromMilliseconds(release is null ? 250 : 40), "user", "low");
    }

    public ProviderDescriptor Descriptor { get; }

    public ValueTask<ProviderResult> CollectAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        ProbeState.UpdateMaximum(ref MaximumActive, Interlocked.Increment(ref _active));
        ProbeState.UpdateMaximum(ref _state.MaximumActive, Interlocked.Increment(ref _state.Active));
        if (_release is not null)
        {
            ProbeState.UpdateMaximum(ref _state.MaximumActiveBlockers, Interlocked.Increment(ref _state.ActiveBlockers));
        }
        _state.Record("entered", Descriptor.GroupId, "", 0, 0, 0);
        using var cancellation = cancellationToken.Register(() => Interlocked.Increment(ref CancellationsIgnored));
        try
        {
            _release?.Wait(); // Deliberately ignore the scheduler's cancellation token.
            return ValueTask.FromResult(new ProviderResult(Descriptor.GroupId, Descriptor.ProviderId,
                context.UtcNow, AvailabilityStates.Available, ProviderCoverage.Complete, [], null));
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            Interlocked.Decrement(ref _state.Active);
            if (_release is not null) Interlocked.Decrement(ref _state.ActiveBlockers);
            _state.Record("exited", Descriptor.GroupId, "", 0, 0, 0);
        }
    }

    public void Dispose()
    {
        Disposed = true;
        _state.Record("disposed", Descriptor.GroupId, "", 0, 0, 0);
        DisposedSignal.TrySetResult();
    }
}

internal sealed class ProbeSink(ProbeState state) : IProviderResultSink
{
    private CapacityPublication? _capacityTimeout;
    public CapacityPublication? CapacityTimeout => Volatile.Read(ref _capacityTimeout);

    public ValueTask PublishAsync(ProviderResult result, ProviderExecution execution, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        state.Record("published", result.GroupId, result.Availability, execution.DurationMilliseconds,
            execution.JitterMilliseconds, execution.SkippedBusyIntervalsTotal,
            string.Join("|", result.Errors.Select(error => error.ErrorCode)));
        if (_capacityTimeout is null && result.Availability == AvailabilityStates.Timeout &&
            result.ObservedAtUtc is null && execution.StartedTimestamp is null &&
            execution.DurationMilliseconds == 0 &&
            result.Errors.Any(error => error.ErrorCode == StableErrorCodes.Timeout))
        {
            Interlocked.CompareExchange(ref _capacityTimeout, new(result, execution), null);
        }
        return ValueTask.CompletedTask;
    }
}

internal sealed record CapacityPublication(ProviderResult Result, ProviderExecution Execution);
