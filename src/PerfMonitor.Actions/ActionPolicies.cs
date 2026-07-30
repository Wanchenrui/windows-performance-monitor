using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PerfMonitor.Contracts;

namespace PerfMonitor.Actions;

public sealed record AgentActionPolicy
{
    public int SchemaVersion { get; init; } = 1;
    public string PolicyVersion { get; init; } = "default-disabled-v1";
    public bool DryRunOnly { get; init; } = true;
    public IReadOnlyList<string> EnabledActionTypes { get; init; } = [];
    public IReadOnlyList<string> AllowedPriorities { get; init; } = [];
    public IReadOnlyList<string> AllowedDiagnosticIds { get; init; } = [];
    public IReadOnlyList<string> AllowedPowerProfileIds { get; init; } = [];

    public static AgentActionPolicy Default { get; } =
        new AgentActionPolicy().Validate();

    public AgentActionPolicy Validate() =>
        (this with
        {
            PolicyVersion = ActionPolicyValidation.ValidateVersion(
                PolicyVersion),
            EnabledActionTypes =
                ActionPolicyValidation.ValidateKnownValues(
                    EnabledActionTypes,
                    ActionTypes.All,
                    ActionContractLimits.MaxApprovedIdLength,
                    "enabled_action"),
            AllowedPriorities =
                ActionPolicyValidation.ValidateKnownValues(
                    AllowedPriorities,
                    ActionPriorities.All,
                    ActionContractLimits.MaxApprovedIdLength,
                    "priority"),
            AllowedDiagnosticIds =
                ActionPolicyValidation.ValidateKnownValues(
                    AllowedDiagnosticIds,
                    ApprovedDiagnosticIds.All,
                    ActionContractLimits.MaxApprovedIdLength,
                    "diagnostic"),
            AllowedPowerProfileIds =
                ActionPolicyValidation.ValidateKnownValues(
                    AllowedPowerProfileIds,
                    ApprovedPowerProfileIds.All,
                    ActionContractLimits.MaxApprovedIdLength,
                    "power_profile"),
        }).ValidateSchema();

    private AgentActionPolicy ValidateSchema()
    {
        if (SchemaVersion != 1)
        {
            throw new InvalidDataException(
                "agent_action_policy_schema_unsupported");
        }

        return this;
    }
}

public sealed record ApprovedClientImagePolicy
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public string SignerSubject { get; init; } = string.Empty;
    public string SignerCertificateSha256 { get; init; } =
        string.Empty;

    public ApprovedClientImagePolicy Validate(
        bool requireTrustedSignature)
    {
        if (string.IsNullOrWhiteSpace(Path) ||
            !System.IO.Path.IsPathFullyQualified(Path))
        {
            throw new InvalidDataException(
                "broker_policy_client_path_invalid");
        }

        var fullPath = System.IO.Path.GetFullPath(Path);
        if (!TryDecodeSha256(Sha256, out var digest))
        {
            throw new InvalidDataException(
                "broker_policy_client_hash_invalid");
        }

        var signerSubject = (SignerSubject ?? string.Empty).Trim();
        if (requireTrustedSignature &&
            (
                signerSubject.Length is < 1 or > 1024 ||
                signerSubject.IndexOfAny(['\r', '\n']) >= 0 ||
                !TryDecodeSha256(
                    SignerCertificateSha256,
                    out _)
            ))
        {
            throw new InvalidDataException(
                "broker_policy_client_signer_invalid");
        }

        return this with
        {
            Path = fullPath,
            Sha256 = Convert.ToHexString(digest),
            SignerSubject = signerSubject,
            SignerCertificateSha256 =
                requireTrustedSignature
                    ? SignerCertificateSha256.ToUpperInvariant()
                    : string.Empty,
        };
    }

    public bool Matches(BrokerCallerIdentity caller)
    {
        string fullCandidatePath;
        try
        {
            fullCandidatePath = System.IO.Path.GetFullPath(
                caller.ClientImagePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return false;
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(
            Path,
            fullCandidatePath) ||
            !TryDecodeSha256(Sha256, out var expected) ||
            !TryDecodeSha256(
                caller.ClientImageSha256,
                out var actual))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            expected,
            actual);
    }

    public bool MatchesSigner(BrokerCallerIdentity caller)
    {
        if (!caller.SignatureTrusted ||
            !StringComparer.Ordinal.Equals(
                SignerSubject,
                caller.SignerSubject) ||
            !TryDecodeSha256(
                SignerCertificateSha256,
                out var expected) ||
            !TryDecodeSha256(
                caller.SignerCertificateSha256 ?? string.Empty,
                out var actual))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            expected,
            actual);
    }

    private static bool TryDecodeSha256(
        string? value,
        out byte[] digest)
    {
        digest = [];
        if (value?.Length != 64)
        {
            return false;
        }

        try
        {
            digest = Convert.FromHexString(value);
            return digest.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed record BrokerMachinePolicy
{
    public int SchemaVersion { get; init; } = 1;
    public string PolicyVersion { get; init; } = "default-disabled-v1";
    public bool DryRunOnly { get; init; } = true;
    public bool RequireApprovedClientImage { get; init; } = true;
    public bool RequireTrustedClientSignature { get; init; } = true;
    public bool RequireProtectedClientPath { get; init; } = true;
    public IReadOnlyList<string> EnabledActionTypes { get; init; } = [];
    public IReadOnlyList<string> AllowedCallerSids { get; init; } = [];
    public IReadOnlyList<ApprovedClientImagePolicy> ApprovedClientImages
    {
        get;
        init;
    } = [];
    public IReadOnlyList<string> AllowedPriorities { get; init; } = [];
    public IReadOnlyList<string> AllowedDiagnosticIds { get; init; } = [];
    public IReadOnlyList<string> AllowedPowerProfileIds { get; init; } = [];
    public IReadOnlyList<string> DeniedProcessNames { get; init; } =
    [
        "perf-monitor-agent",
        "perf-monitor-broker",
        "perf-monitor-desktop",
    ];

    public static BrokerMachinePolicy Default { get; } =
        new BrokerMachinePolicy().Validate();

    public BrokerMachinePolicy Validate()
    {
        if (SchemaVersion != 1)
        {
            throw new InvalidDataException(
                "broker_policy_schema_unsupported");
        }

        var images = ApprovedClientImages
            .Select(item => item.Validate(
                RequireTrustedClientSignature))
            .OrderBy(static item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (images.Select(static item => item.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != images.Length)
        {
            throw new InvalidDataException(
                "broker_policy_client_duplicate");
        }

        return this with
        {
            PolicyVersion = ActionPolicyValidation.ValidateVersion(
                PolicyVersion),
            EnabledActionTypes =
                ActionPolicyValidation.ValidateKnownValues(
                    EnabledActionTypes,
                    ActionTypes.All,
                    ActionContractLimits.MaxApprovedIdLength,
                    "enabled_action"),
            AllowedCallerSids =
                ActionPolicyValidation.ValidateSids(
                    AllowedCallerSids),
            ApprovedClientImages = images,
            AllowedPriorities =
                ActionPolicyValidation.ValidateKnownValues(
                    AllowedPriorities,
                    ActionPriorities.All,
                    ActionContractLimits.MaxApprovedIdLength,
                    "priority"),
            AllowedDiagnosticIds =
                ActionPolicyValidation.ValidateKnownValues(
                    AllowedDiagnosticIds,
                    ApprovedDiagnosticIds.All,
                    ActionContractLimits.MaxApprovedIdLength,
                    "diagnostic"),
            AllowedPowerProfileIds =
                ActionPolicyValidation.ValidateKnownValues(
                    AllowedPowerProfileIds,
                    ApprovedPowerProfileIds.All,
                    ActionContractLimits.MaxApprovedIdLength,
                    "power_profile"),
            DeniedProcessNames =
                ActionPolicyValidation.ValidateProcessNames(
                    DeniedProcessNames),
        };
    }
}

public sealed record BrokerCallerIdentity(
    string Sid,
    int ClientPid,
    string ClientImagePath,
    string ClientImageSha256)
{
    public bool SignatureTrusted { get; init; }
    public string? SignerSubject { get; init; }
    public string? SignerCertificateSha256 { get; init; }
    public bool IsProtectedInstallPath { get; init; }
}

public sealed record ActionPolicyDecision(
    bool Allowed,
    string? ErrorCode,
    bool DryRunOnly)
{
    public static ActionPolicyDecision Deny(string errorCode) =>
        new(false, errorCode, true);
}

public static class ActionPolicyEvaluator
{
    public static ActionPolicyDecision EvaluateCaller(
        BrokerMachinePolicy policy,
        BrokerCallerIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(caller);
        if (!policy.AllowedCallerSids.Contains(
            caller.Sid,
            StringComparer.Ordinal))
        {
            return ActionPolicyDecision.Deny(
                ActionErrorCodes.CallerIdentityDenied);
        }

        var approvedImage = policy.ApprovedClientImages.FirstOrDefault(
            image => image.Matches(caller));
        if (policy.RequireApprovedClientImage &&
            approvedImage is null)
        {
            return ActionPolicyDecision.Deny(
                ActionErrorCodes.ClientImageDenied);
        }

        if (policy.RequireProtectedClientPath &&
            !caller.IsProtectedInstallPath)
        {
            return ActionPolicyDecision.Deny(
                ActionErrorCodes.ClientImageDenied);
        }

        if (policy.RequireTrustedClientSignature &&
            (
                approvedImage is null ||
                !approvedImage.MatchesSigner(caller)
            ))
        {
            return ActionPolicyDecision.Deny(
                ActionErrorCodes.ClientImageDenied);
        }

        return new ActionPolicyDecision(
            true,
            ErrorCode: null,
            DryRunOnly: policy.DryRunOnly);
    }

    public static ActionPolicyDecision Evaluate(
        AgentActionPolicy policy,
        ActionRequestContract action)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return EvaluateActionLists(
            policy.EnabledActionTypes,
            policy.AllowedPriorities,
            policy.AllowedDiagnosticIds,
            policy.AllowedPowerProfileIds,
            policy.DryRunOnly,
            action);
    }

    public static ActionPolicyDecision Evaluate(
        BrokerMachinePolicy policy,
        BrokerCallerIdentity caller,
        ActionRequestContract action,
        bool forceDryRunOnly)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(caller);
        var callerDecision = EvaluateCaller(policy, caller);
        if (!callerDecision.Allowed)
        {
            return callerDecision;
        }

        return EvaluateActionLists(
            policy.EnabledActionTypes,
            policy.AllowedPriorities,
            policy.AllowedDiagnosticIds,
            policy.AllowedPowerProfileIds,
            policy.DryRunOnly || forceDryRunOnly,
            action);
    }

    private static ActionPolicyDecision EvaluateActionLists(
        IReadOnlyList<string> enabledActions,
        IReadOnlyList<string> priorities,
        IReadOnlyList<string> diagnosticIds,
        IReadOnlyList<string> powerProfileIds,
        bool dryRunOnly,
        ActionRequestContract action)
    {
        ActionContractValidation.ValidateAction(action);
        if (!enabledActions.Contains(
            action.ActionType,
            StringComparer.Ordinal))
        {
            return ActionPolicyDecision.Deny(
                ActionErrorCodes.ActionPolicyDenied);
        }

        var allowed = action.ActionType switch
        {
            ActionTypes.SetProcessPriority =>
                priorities.Contains(
                    action.Priority!,
                    StringComparer.Ordinal),
            ActionTypes.StartApprovedDiagnostic =>
                diagnosticIds.Contains(
                    action.DiagnosticId!,
                    StringComparer.Ordinal),
            ActionTypes.ApplyApprovedPowerProfile =>
                powerProfileIds.Contains(
                    action.PowerProfileId!,
                    StringComparer.Ordinal),
            ActionTypes.TerminateProcess => true,
            _ => false,
        };
        return allowed
            ? new ActionPolicyDecision(true, null, dryRunOnly)
            : ActionPolicyDecision.Deny(
                ActionErrorCodes.ActionPolicyDenied);
    }
}

public static class ActionPolicyFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition =
            JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling =
            JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 16,
    };

    public static AgentActionPolicy LoadOrCreateAgent(
        string path,
        out string? warning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        warning = null;
        try
        {
            if (!File.Exists(path))
            {
                WriteNew(path, AgentActionPolicy.Default);
                return AgentActionPolicy.Default;
            }

            var policy = JsonSerializer.Deserialize<AgentActionPolicy>(
                File.ReadAllText(path),
                JsonOptions) ?? throw new JsonException(
                "Agent action policy is null.");
            return policy.Validate();
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidDataException or
            ArgumentException)
        {
            warning = "agent_action_policy_fallback";
            return AgentActionPolicy.Default;
        }
    }

    public static BrokerMachinePolicy LoadOrCreateBroker(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            WriteNew(path, BrokerMachinePolicy.Default);
            return BrokerMachinePolicy.Default;
        }

        var policy = JsonSerializer.Deserialize<BrokerMachinePolicy>(
            File.ReadAllText(path),
            JsonOptions) ?? throw new JsonException(
            "Broker machine policy is null.");
        return policy.Validate();
    }

    public static string Serialize<T>(T policy) =>
        JsonSerializer.Serialize(policy, JsonOptions);

    private static void WriteNew<T>(string path, T policy)
    {
        var directory = Path.GetDirectoryName(
            Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4 * 1024,
            FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, policy, JsonOptions);
        stream.Flush(flushToDisk: true);
    }
}

internal static class ActionPolicyValidation
{
    public static string ValidateVersion(string value)
    {
        if (!ActionContractValidation.IsToken(
            value,
            ActionContractLimits.MaxPolicyVersionLength))
        {
            throw new InvalidDataException(
                "action_policy_version_invalid");
        }

        return value;
    }

    public static IReadOnlyList<string> ValidateKnownValues(
        IReadOnlyList<string> values,
        IReadOnlyList<string> allowed,
        int maxLength,
        string label)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > allowed.Count ||
            values.Any(value =>
                !ActionContractValidation.IsToken(
                    value,
                    maxLength) ||
                !allowed.Contains(
                    value,
                    StringComparer.Ordinal)))
        {
            throw new InvalidDataException(
                $"action_policy_{label}_invalid");
        }

        return SortUnique(values, label);
    }

    public static IReadOnlyList<string> ValidateApprovedIds(
        IReadOnlyList<string> values,
        string label)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > 64 ||
            values.Any(value =>
                !ActionContractValidation.IsApprovedId(value)))
        {
            throw new InvalidDataException(
                $"action_policy_{label}_invalid");
        }

        return SortUnique(values, label);
    }

    public static IReadOnlyList<string> ValidateSids(
        IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > 64 ||
            values.Any(value =>
                value.Length is < 4 or > 184 ||
                !value.StartsWith("S-", StringComparison.Ordinal) ||
                value[2..].Any(character =>
                    character != '-' &&
                    (character < '0' || character > '9'))))
        {
            throw new InvalidDataException(
                "broker_policy_sid_invalid");
        }

        return SortUnique(values, "caller_sid");
    }

    public static IReadOnlyList<string> ValidateProcessNames(
        IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > 64 ||
            values.Any(value =>
                string.IsNullOrWhiteSpace(value) ||
                value.Length > 128 ||
                value.IndexOfAny(
                    [Path.DirectorySeparatorChar,
                     Path.AltDirectorySeparatorChar]) >= 0))
        {
            throw new InvalidDataException(
                "broker_policy_process_name_invalid");
        }

        var normalized = values
            .Select(static value => Path.GetFileNameWithoutExtension(
                value.Trim()))
            .ToArray();
        return SortUnique(
            normalized,
            "process_name",
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> SortUnique(
        IReadOnlyList<string> values,
        string label,
        StringComparer? comparer = null)
    {
        comparer ??= StringComparer.Ordinal;
        var sorted = values
            .OrderBy(static value => value, comparer)
            .ToArray();
        if (sorted.Distinct(comparer).Count() != sorted.Length)
        {
            throw new InvalidDataException(
                $"action_policy_{label}_duplicate");
        }

        return sorted;
    }
}
