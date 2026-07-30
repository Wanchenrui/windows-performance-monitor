using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using PerfMonitor.Actions;
using PerfMonitor.Contracts;

namespace PerfMonitor.Broker;

public interface IBrokerClientIdentityResolver
{
    BrokerCallerIdentity Resolve(NamedPipeServerStream pipe);
}

public sealed class BrokerClientIdentityResolver :
    IBrokerClientIdentityResolver
{
    private const long MaxClientImageBytes =
        32L * 1024 * 1024;

    public BrokerCallerIdentity Resolve(
        NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!OperatingSystem.IsWindows() || !pipe.IsConnected)
        {
            throw new ActionExecutorException(
                ActionErrorCodes.CallerIdentityDenied);
        }

        string? pipeSid = null;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(
                    TokenAccessLevels.Query);
                pipeSid = identity.User?.Value;
            });
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException)
        {
            throw new ActionExecutorException(
                ActionErrorCodes.CallerIdentityDenied);
        }

        if (string.IsNullOrWhiteSpace(pipeSid) ||
            !BrokerIdentityNativeMethods.GetNamedPipeClientProcessId(
                pipe.SafePipeHandle,
                out var clientPidRaw) ||
            clientPidRaw == 0 ||
            clientPidRaw > int.MaxValue)
        {
            throw new ActionExecutorException(
                ActionErrorCodes.CallerIdentityDenied);
        }

        var clientPid = checked((int)clientPidRaw);
        using var process = NativeMethods.OpenProcess(
            ProcessAccess.QueryLimitedInformation,
            inheritHandle: false,
            clientPid);
        if (process.IsInvalid)
        {
            throw new ActionExecutorException(
                ActionErrorCodes.CallerIdentityDenied);
        }

        var processSid = ReadProcessSid(process);
        if (!StringComparer.Ordinal.Equals(pipeSid, processSid))
        {
            throw new ActionExecutorException(
                ActionErrorCodes.CallerIdentityDenied);
        }

        var imagePath = ReadImagePath(process);
        var imageHash = HashImage(imagePath);
        return new BrokerCallerIdentity(
            pipeSid,
            clientPid,
            imagePath,
            imageHash);
    }

    private static string ReadProcessSid(
        SafeProcessHandle process)
    {
        if (!NativeMethods.OpenProcessToken(
            process,
            NativeMethods.TokenQuery,
            out var token))
        {
            throw new ActionExecutorException(
                ActionErrorCodes.CallerIdentityDenied);
        }

        using (token)
        using (var identity = new WindowsIdentity(
            token.DangerousGetHandle()))
        {
            return identity.User?.Value ??
                throw new ActionExecutorException(
                    ActionErrorCodes.CallerIdentityDenied);
        }
    }

    private static string ReadImagePath(
        SafeProcessHandle process)
    {
        var length = 32_768;
        var buffer = new char[length];
        if (!NativeMethods.QueryFullProcessImageName(
            process,
            flags: 0,
            buffer,
            ref length) ||
            length <= 0)
        {
            throw new ActionExecutorException(
                ActionErrorCodes.ClientImageDenied);
        }

        return Path.GetFullPath(
            new string(buffer, 0, length));
    }

    private static string HashImage(string imagePath)
    {
        using var stream = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaxClientImageBytes)
        {
            throw new ActionExecutorException(
                ActionErrorCodes.ClientImageDenied);
        }

        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

internal static class BrokerIdentityNativeMethods
{
    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);
}
