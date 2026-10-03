using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PerfMonitor.Ipc.NamedPipes;

public sealed record PipeEndpoint(
    string PipeName,
    SecurityIdentifier UserSid)
{
    public const int MaxServerInstances = 8;

    public string DisplayPath => $@"\\.\pipe\{PipeName}";

    public static PipeEndpoint ForCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent(
            TokenAccessLevels.Query);
        var sid = identity.User ?? throw new InvalidOperationException(
            "The current Windows identity has no user SID.");
        return ForSid(sid);
    }

    public static PipeEndpoint ForSid(SecurityIdentifier userSid)
    {
        ArgumentNullException.ThrowIfNull(userSid);
        // Win32 forbids backslashes inside PipeName. The dots preserve the
        // same logical namespace as PerfMonitor\<SID>\agent.
        var name = $"PerfMonitor.{userSid.Value}.agent";
        if (name.Length > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(userSid));
        }

        return new PipeEndpoint(name, userSid);
    }

    public PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);
        security.SetOwner(UserSid);

        var networkSid = new SecurityIdentifier(
            WellKnownSidType.NetworkSid,
            domainSid: null);
        security.AddAccessRule(
            new PipeAccessRule(
                networkSid,
                PipeAccessRights.FullControl,
                AccessControlType.Deny));
        security.AddAccessRule(
            new PipeAccessRule(
                UserSid,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

        var localSystemSid = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid,
            domainSid: null);
        if (!UserSid.Equals(localSystemSid))
        {
            security.AddAccessRule(
                new PipeAccessRule(
                    localSystemSid,
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));
        }

        return security;
    }

    public NamedPipeServerStream CreateServerStream(bool firstInstance)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Named Pipe ACLs require Windows.");
        }

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            MaxServerInstances,
            PipeTransmissionMode.Byte,
            firstInstance
                ? PipeOptions.Asynchronous |
                    PipeOptions.FirstPipeInstance
                : PipeOptions.Asynchronous,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            pipeSecurity: CreateSecurity(),
            inheritability: HandleInheritability.None,
            additionalAccessRights: (PipeAccessRights)0);
    }
}
