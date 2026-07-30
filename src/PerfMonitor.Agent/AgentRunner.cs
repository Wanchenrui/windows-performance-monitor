using PerfMonitor.Collectors.Windows;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Agent;

public static class AgentRunner
{
    public static async Task<int> RunAsync(
        AgentOptions options,
        CancellationToken cancellationToken)
    {
        var providers = WindowsProviderFactory.CreateDefault();
        var assembler = new SnapshotAssembler(
            providers.Select(provider => provider.Descriptor));
        await using var scheduler = new ProviderScheduler(
            providers,
            assembler,
            options.MaxConcurrency);
        scheduler.Start();

        if (options.Warmup > TimeSpan.Zero)
        {
            await Task.Delay(options.Warmup, cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.Once)
        {
            await WriteSnapshotAsync(
                assembler.Read(),
                options.OutputPath,
                append: false,
                quiet: options.Quiet,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return 0;
        }

        using var durationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        if (options.Duration is not null)
        {
            durationCancellation.CancelAfter(options.Duration.Value);
        }

        var append = false;
        try
        {
            while (true)
            {
                durationCancellation.Token.ThrowIfCancellationRequested();
                await WriteSnapshotAsync(
                    assembler.Read(),
                    options.OutputPath,
                    append,
                    options.Quiet,
                    durationCancellation.Token).ConfigureAwait(false);
                append = true;
                await Task.Delay(
                    options.OutputPeriod,
                    durationCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            durationCancellation.IsCancellationRequested)
        {
            if (!cancellationToken.IsCancellationRequested &&
                options.Duration is not null)
            {
                await WriteSnapshotAsync(
                    assembler.Read(),
                    options.OutputPath,
                    append,
                    options.Quiet,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        return 0;
    }

    private static async Task WriteSnapshotAsync(
        AgentSnapshot snapshot,
        string? outputPath,
        bool append,
        bool quiet,
        CancellationToken cancellationToken)
    {
        ValidateCoreGroups(snapshot);
        var json = AgentJson.Serialize(snapshot);
        if (!quiet)
        {
            // v0.4.0: 长稳模式禁止把快照同时复制到控制台。
            await Console.Out.WriteLineAsync(
                    json.AsMemory(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        if (outputPath is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (append)
        {
            await File.AppendAllTextAsync(
                outputPath,
                json + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllTextAsync(
                outputPath,
                json + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateCoreGroups(AgentSnapshot snapshot)
    {
        string[] required =
        [
            GroupIds.SystemCpu,
            GroupIds.Memory,
            GroupIds.Volumes,
            GroupIds.Uptime,
            GroupIds.Processes,
            GroupIds.Sampler,
            GroupIds.Self,
        ];
        foreach (var groupId in required)
        {
            if (!snapshot.Groups.ContainsKey(groupId))
            {
                throw new InvalidDataException(
                    $"Snapshot is missing group {groupId}.");
            }
        }
    }
}
