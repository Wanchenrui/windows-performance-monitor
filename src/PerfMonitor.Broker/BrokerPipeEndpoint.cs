using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PerfMonitor.Broker;

public sealed record BrokerPipeEndpoint(string PipeName)
{
    public const string DefaultPipeName = "PerfMonitor.Broker.v1";
    public const int MaxServerInstances = 16;

    public string DisplayPath => $@"\\.\pipe\{PipeName}";

    public static BrokerPipeEndpoint Default { get; } =
        new(DefaultPipeName);

    public PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);
        var localSystem = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid,
            domainSid: null);
        using var identity = WindowsIdentity.GetCurrent(
            TokenAccessLevels.Query);
        var owner = identity.User ??
            throw new InvalidOperationException(
                "The Broker identity has no SID.");
        security.SetOwner(owner);
        var network = new SecurityIdentifier(
            WellKnownSidType.NetworkSid,
            domainSid: null);
        security.AddAccessRule(
            new PipeAccessRule(
                network,
                PipeAccessRights.FullControl,
                AccessControlType.Deny));
        var authenticatedUsers = new SecurityIdentifier(
            WellKnownSidType.AuthenticatedUserSid,
            domainSid: null);
        security.AddAccessRule(
            new PipeAccessRule(
                authenticatedUsers,
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));
        security.AddAccessRule(
            new PipeAccessRule(
                localSystem,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        if (!owner.Equals(localSystem))
        {
            // Console mode is forced dry-run-only. Its current user
            // owns the test/development Pipe; service mode is owned by
            // LocalSystem and never grants this extra rule.
            security.AddAccessRule(
                new PipeAccessRule(
                    owner,
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));
        }
        return security;
    }

    public NamedPipeServerStream CreateServerStream(
        bool firstInstance)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Broker Pipe ACLs require Windows.");
        }

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            MaxServerInstances,
            PipeTransmissionMode.Byte,
            firstInstance
                ? PipeOptions.Asynchronous |
                    PipeOptions.WriteThrough |
                    PipeOptions.FirstPipeInstance
                : PipeOptions.Asynchronous |
                    PipeOptions.WriteThrough,
            inBufferSize: 16 * 1024,
            outBufferSize: 16 * 1024,
            pipeSecurity: CreateSecurity(),
            inheritability: HandleInheritability.None,
            additionalAccessRights: (PipeAccessRights)0);
    }
}
