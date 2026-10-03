using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using PerfMonitor.ProviderWorker.Protocol;

namespace PerfMonitor.Collectors.Worker;

internal sealed class ProcessWorkerSessionFactory :
    IWorkerSessionFactory
{
    private readonly HardwareWorkerOptions _options;

    public ProcessWorkerSessionFactory(
        HardwareWorkerOptions options)
    {
        options.Validate();
        _options = options;
    }

    public ValueTask<IWorkerSession> StartAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workerPath = Path.GetFullPath(_options.WorkerPath);
        if (!File.Exists(workerPath))
        {
            throw new FileNotFoundException(
                "The hardware Provider Worker is not installed.",
                workerPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            WorkingDirectory =
                Path.GetDirectoryName(workerPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false),
        };
        startInfo.ArgumentList.Add("--stdio");
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        try
        {
            if (!process.Start())
            {
                throw new IOException(
                    "The hardware Provider Worker did not start.");
            }

            TrySetBelowNormalPriority(process);
            var job = WindowsJobObject.TryCreateAndAssign(
                process,
                _options.MaxPrivateMemoryBytes);
            return ValueTask.FromResult<IWorkerSession>(
                new ProcessWorkerSession(
                    process,
                    job,
                    _options));
        }
        catch
        {
            TryKill(process);
            process.Dispose();
            throw;
        }
    }

    private static void TrySetBelowNormalPriority(
        Process process)
    {
        try
        {
            process.PriorityClass =
                ProcessPriorityClass.BelowNormal;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
        }
    }
}

internal sealed class ProcessWorkerSession :
    IWorkerSession
{
    private readonly Process _process;
    private readonly WindowsJobObject? _job;
    private readonly HardwareWorkerOptions _options;
    private readonly byte[] _stderrRing;
    private readonly Task _stderrDrain;
    private int _stderrWriteOffset;
    private int _stderrCount;
    private int _aborted;
    private int _disposed;

    public ProcessWorkerSession(
        Process process,
        WindowsJobObject? job,
        HardwareWorkerOptions options)
    {
        _process = process;
        _job = job;
        _options = options;
        _stderrRing = new byte[options.MaxStderrBytes];
        _stderrDrain = DrainStandardErrorAsync(
            _process.StandardError.BaseStream);
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public long PrivateMemoryBytes
    {
        get
        {
            if (HasExited)
            {
                return 0;
            }

            try
            {
                _process.Refresh();
                return _process.PrivateMemorySize64;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                Win32Exception or
                NotSupportedException)
            {
                return 0;
            }
        }
    }

    public async ValueTask<WorkerResponse> ExchangeAsync(
        WorkerRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (HasExited)
        {
            throw new EndOfStreamException(
                "The hardware Worker exited before the request.");
        }

        await LengthPrefixedJson.WriteAsync(
            _process.StandardInput.BaseStream,
            request,
            cancellationToken).ConfigureAwait(false);
        return await LengthPrefixedJson.ReadAsync<WorkerResponse>(
            _process.StandardOutput.BaseStream,
            cancellationToken).ConfigureAwait(false);
    }

    public void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
        {
            return;
        }

        TryKill();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception exception) when (
            exception is IOException or
            InvalidOperationException or
            ObjectDisposedException)
        {
        }

        if (!HasExited)
        {
            using var stopping = new CancellationTokenSource(
                _options.GracefulStopTimeout);
            try
            {
                await _process.WaitForExitAsync(
                    stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill();
            }
        }

        _job?.Dispose();
        try
        {
            await _stderrDrain.WaitAsync(
                _options.GracefulStopTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or
            ObjectDisposedException or
            TimeoutException)
        {
        }
        _process.Dispose();
    }

    private async Task DrainStandardErrorAsync(
        Stream stream)
    {
        var buffer = new byte[1024];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                RetainStandardError(buffer.AsSpan(0, read));
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            ObjectDisposedException)
        {
        }
    }

    private void RetainStandardError(
        ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            _stderrRing[_stderrWriteOffset] = value;
            _stderrWriteOffset =
                (_stderrWriteOffset + 1) % _stderrRing.Length;
            _stderrCount = Math.Min(
                _stderrRing.Length,
                _stderrCount + 1);
        }
    }

    private void TryKill()
    {
        try
        {
            if (!HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
        }
    }
}
