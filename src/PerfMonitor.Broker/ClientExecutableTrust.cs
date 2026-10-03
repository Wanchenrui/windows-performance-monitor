using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PerfMonitor.Actions;
using PerfMonitor.Contracts;

namespace PerfMonitor.Broker;

public sealed record BrokerClientExecutableTrust(
    string FinalPath,
    string Sha256,
    bool SignatureTrusted,
    string? SignerSubject,
    string? SignerCertificateSha256,
    bool IsProtectedInstallPath)
{
    public int AuthenticodeStatus { get; init; }
}

public interface IBrokerClientExecutableTrustVerifier
{
    BrokerClientExecutableTrust Verify(string imagePath);
}

public sealed class WindowsBrokerClientExecutableTrustVerifier :
    IBrokerClientExecutableTrustVerifier
{
    private const long MaxClientImageBytes =
        32L * 1024 * 1024;
    private const int MaxCertificateBytes = 64 * 1024;
    private const string CodeSigningEku =
        "1.3.6.1.5.5.7.3.3";
    private readonly string _programFilesRoot;

    public WindowsBrokerClientExecutableTrustVerifier(
        string? programFilesRoot = null)
    {
        _programFilesRoot = Path.GetFullPath(
            programFilesRoot ?? ReadProgramFilesRoot());
    }

    public BrokerClientExecutableTrust Verify(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        try
        {
            using var stream = new FileStream(
                imagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaxClientImageBytes)
            {
                throw new ActionExecutorException(
                    ActionErrorCodes.ClientImageDenied);
            }

            var finalPath = ReadFinalPath(stream.SafeFileHandle);
            var sha256 = Convert.ToHexString(
                SHA256.HashData(stream));
            var signature = VerifyAuthenticode(
                finalPath,
                stream.SafeFileHandle);
            return new BrokerClientExecutableTrust(
                finalPath,
                sha256,
                signature.Trusted,
                signature.Subject,
                signature.CertificateSha256,
                IsExpectedAgentPath(
                    finalPath,
                    _programFilesRoot))
            {
                AuthenticodeStatus = signature.NativeStatus,
            };
        }
        catch (ActionExecutorException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            CryptographicException or
            ArgumentException or
            NotSupportedException)
        {
            throw new ActionExecutorException(
                ActionErrorCodes.ClientImageDenied);
        }
    }

    public static bool IsExpectedAgentPath(
        string candidatePath,
        string programFilesRoot)
    {
        try
        {
            var candidate = NormalizeExtendedPath(
                candidatePath);
            var expected = Path.GetFullPath(
                Path.Combine(
                    programFilesRoot,
                    "PerfMonitor",
                    "agent",
                    "perf-monitor-agent.exe"));
            return StringComparer.OrdinalIgnoreCase.Equals(
                candidate,
                expected);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return false;
        }
    }

    private static string ReadProgramFilesRoot()
    {
        var value = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "Program Files is unavailable.");
        }

        return value;
    }

    private static string ReadFinalPath(
        SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= 32_768)
        {
            var buffer = new StringBuilder(capacity);
            var length =
                ClientExecutableTrustNativeMethods
                    .GetFinalPathNameByHandle(
                        handle,
                        buffer,
                        checked((uint)buffer.Capacity),
                        flags: 0);
            if (length == 0)
            {
                throw new IOException(
                    "client_image_final_path_unavailable");
            }

            if (length < buffer.Capacity)
            {
                return NormalizeExtendedPath(
                    buffer.ToString());
            }

            capacity = checked((int)length + 1);
        }

        throw new PathTooLongException(
            "Client image path exceeds the supported limit.");
    }

    private static string NormalizeExtendedPath(string value)
    {
        const string ExtendedUncPrefix = @"\\?\UNC\";
        const string ExtendedPrefix = @"\\?\";
        if (value.StartsWith(
            ExtendedUncPrefix,
            StringComparison.OrdinalIgnoreCase))
        {
            value = @"\\" + value[ExtendedUncPrefix.Length..];
        }
        else if (value.StartsWith(
            ExtendedPrefix,
            StringComparison.OrdinalIgnoreCase))
        {
            value = value[ExtendedPrefix.Length..];
        }

        return Path.GetFullPath(value);
    }

    private static AuthenticodeFacts VerifyAuthenticode(
        string finalPath,
        SafeFileHandle fileHandle)
    {
        var pathPointer = nint.Zero;
        var fileInfoPointer = nint.Zero;
        var trustData = new WinTrustData();
        var action = new Guid(
            "00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try
        {
            pathPointer = Marshal.StringToCoTaskMemUni(
                finalPath);
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = checked((uint)Marshal.SizeOf<
                    WinTrustFileInfo>()),
                FilePath = pathPointer,
                FileHandle = fileHandle.DangerousGetHandle(),
                KnownSubject = nint.Zero,
            };
            fileInfoPointer = Marshal.AllocCoTaskMem(
                Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(
                fileInfo,
                fileInfoPointer,
                false);
            trustData = new WinTrustData
            {
                StructSize = checked((uint)Marshal.SizeOf<
                    WinTrustData>()),
                UiChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfo = fileInfoPointer,
                StateAction = 1,
                ProviderFlags =
                    ClientExecutableTrustNativeMethods
                        .WtdRevocationCheckNone |
                    ClientExecutableTrustNativeMethods
                        .WtdCacheOnlyUrlRetrieval |
                    ClientExecutableTrustNativeMethods
                        .WtdDisableMd2Md4,
                UiContext = 0,
            };

            var status =
                ClientExecutableTrustNativeMethods.WinVerifyTrust(
                    new nint(-1),
                    ref action,
                    ref trustData);
            if (status != 0 ||
                trustData.StateData == nint.Zero)
            {
                return AuthenticodeFacts.FromFailure(status);
            }

            return ReadVerifiedSigner(trustData.StateData);
        }
        catch (Exception exception) when (
            exception is CryptographicException or
            ArgumentException or
            OverflowException)
        {
            return AuthenticodeFacts.Untrusted;
        }
        finally
        {
            if (trustData.StateData != nint.Zero)
            {
                trustData.StateAction = 2;
                _ = ClientExecutableTrustNativeMethods
                    .WinVerifyTrust(
                        new nint(-1),
                        ref action,
                        ref trustData);
            }

            if (fileInfoPointer != nint.Zero)
            {
                Marshal.FreeCoTaskMem(fileInfoPointer);
            }

            if (pathPointer != nint.Zero)
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
    }

    private static AuthenticodeFacts ReadVerifiedSigner(
        nint stateData)
    {
        var providerData =
            ClientExecutableTrustNativeMethods
                .WTHelperProvDataFromStateData(stateData);
        if (providerData == nint.Zero)
        {
            return AuthenticodeFacts.Untrusted;
        }

        var signer =
            ClientExecutableTrustNativeMethods
                .WTHelperGetProvSignerFromChain(
                    providerData,
                    signerIndex: 0,
                    counterSigner: false,
                    counterSignerIndex: 0);
        if (signer == nint.Zero)
        {
            return AuthenticodeFacts.Untrusted;
        }

        var providerCertificate =
            ClientExecutableTrustNativeMethods
                .WTHelperGetProvCertFromChain(
                    signer,
                    certificateIndex: 0);
        if (providerCertificate == nint.Zero)
        {
            return AuthenticodeFacts.Untrusted;
        }

        var provider = Marshal.PtrToStructure<
            CryptProviderCertificate>(providerCertificate);
        if (provider.CertificateContext == nint.Zero)
        {
            return AuthenticodeFacts.Untrusted;
        }

        var context = Marshal.PtrToStructure<CertificateContext>(
            provider.CertificateContext);
        if (context.EncodedCertificate == nint.Zero ||
            context.EncodedCertificateBytes is 0 or >
                MaxCertificateBytes)
        {
            return AuthenticodeFacts.Untrusted;
        }

        var encoded = new byte[
            checked((int)context.EncodedCertificateBytes)];
        Marshal.Copy(
            context.EncodedCertificate,
            encoded,
            startIndex: 0,
            encoded.Length);
        using var certificate =
            X509CertificateLoader.LoadCertificate(encoded);
        if (!HasCodeSigningEku(certificate))
        {
            return AuthenticodeFacts.Untrusted;
        }

        return new AuthenticodeFacts(
            Trusted: true,
            certificate.Subject,
            Convert.ToHexString(
                SHA256.HashData(certificate.RawData)),
            NativeStatus: 0);
    }

    private static bool HasCodeSigningEku(
        X509Certificate2 certificate)
    {
        var extensions = certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .ToArray();
        return extensions.Length == 1 &&
            extensions[0].EnhancedKeyUsages
                .Cast<Oid>()
                .Any(oid => StringComparer.Ordinal.Equals(
                    oid.Value,
                    CodeSigningEku));
    }

    private sealed record AuthenticodeFacts(
        bool Trusted,
        string? Subject,
        string? CertificateSha256,
        int NativeStatus)
    {
        public static AuthenticodeFacts Untrusted { get; } =
            new(false, null, null, 0);

        public static AuthenticodeFacts FromFailure(int status) =>
            new(false, null, null, status);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public nint FilePath;
        public nint FileHandle;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinTrustData
    {
        public uint StructSize;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint FileInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertificate
    {
        public uint StructSize;
        public nint CertificateContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext
    {
        public uint EncodingType;
        public nint EncodedCertificate;
        public uint EncodedCertificateBytes;
        public nint CertificateInfo;
        public nint CertificateStore;
    }
}

internal static class ClientExecutableTrustNativeMethods
{
    internal const uint WtdRevocationCheckNone = 0x00000010;
    internal const uint WtdCacheOnlyUrlRetrieval = 0x00001000;
    internal const uint WtdDisableMd2Md4 = 0x00002000;

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    internal static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] StringBuilder path,
        uint pathCharacters,
        uint flags);

    [DllImport(
        "wintrust.dll",
        ExactSpelling = true,
        SetLastError = true)]
    internal static extern int WinVerifyTrust(
        nint window,
        ref Guid action,
        ref WindowsBrokerClientExecutableTrustVerifier.WinTrustData data);

    [DllImport(
        "wintrust.dll",
        ExactSpelling = true)]
    internal static extern nint WTHelperProvDataFromStateData(
        nint stateData);

    [DllImport(
        "wintrust.dll",
        ExactSpelling = true)]
    internal static extern nint WTHelperGetProvSignerFromChain(
        nint providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);

    [DllImport(
        "wintrust.dll",
        ExactSpelling = true)]
    internal static extern nint WTHelperGetProvCertFromChain(
        nint signer,
        uint certificateIndex);
}
