using System.Text.Json;
using Baton;
using Baton.Domain;

namespace Baton.Vendors;

public sealed record ExecutionLimitProfile
{
    public string? Adapter { get; init; }
    public string? Model { get; init; }
    public string? Role { get; init; }
    public string? DeclaredTaskSize { get; init; }
    public TimeSpan Timeout { get; init; }
    public long TokenBudget { get; init; }
    public int MaxToolSteps { get; init; }
    public int? MaxRepeatedToolSteps { get; init; }
}

public sealed record ExecutionLimitResolution(
    string? ChosenKey,
    string? TimeoutSource,
    string? TokenBudgetSource,
    string? MaxToolStepsSource,
    TimeSpan Timeout,
    long? TokenBudget,
    int? MaxToolSteps,
    string? OriginatingSelectionKey = null,
    string? MaxRepeatedToolStepsSource = null,
    int? MaxRepeatedToolSteps = null);

public static class ExecutionLimitSource
{
    public const string Profile = "profile";
    public const string DispatchOverride = "dispatch-override";
    public const string RoleDefault = "role-default";
}

public sealed class ExecutionLimitProfileConfigurationException : BatonFlowException
{
    public ExecutionLimitProfileConfigurationException(string message) : base(message) { }
    public ExecutionLimitProfileConfigurationException(string message, Exception innerException) : base(message, innerException) { }
}

public static class ExecutionLimitProfileResolver
{
    public static ExecutionLimitResolution Resolve(
        IReadOnlyList<ExecutionLimitProfile>? profiles,
        string adapter,
        string? model,
        string role,
        DeclaredTaskSize declaredTaskSize,
        TimeSpan roleTimeout,
        long? roleTokenBudget,
        int? roleMaxToolSteps,
        TimeSpan? timeoutOverride = null,
        long? tokenBudgetOverride = null,
        int? maxToolStepsOverride = null,
        int? maxRepeatedToolStepsOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        Validate(profiles);

        var key = model is { Length: > 0 }
            ? new ExecutionLimitProfileKey(adapter, model, role, declaredTaskSize)
            : (ExecutionLimitProfileKey?)null;
        var profile = key is { } actualKey
            ? profiles?.FirstOrDefault(candidate => actualKey.Equals(candidate.ToKey()))
            : null;
        var selectedKey = profile is null ? null : key!.Value.ToString();
        var originatingSelectionKey = key?.ToString();
        return new ExecutionLimitResolution(
            selectedKey,
            timeoutOverride is not null ? ExecutionLimitSource.DispatchOverride : profile is not null ? ExecutionLimitSource.Profile : ExecutionLimitSource.RoleDefault,
            tokenBudgetOverride is not null ? ExecutionLimitSource.DispatchOverride : profile is not null ? ExecutionLimitSource.Profile : ExecutionLimitSource.RoleDefault,
            maxToolStepsOverride is not null ? ExecutionLimitSource.DispatchOverride : profile is not null ? ExecutionLimitSource.Profile : ExecutionLimitSource.RoleDefault,
            timeoutOverride ?? profile?.Timeout ?? roleTimeout,
            tokenBudgetOverride ?? profile?.TokenBudget ?? roleTokenBudget,
            maxToolStepsOverride ?? profile?.MaxToolSteps ?? roleMaxToolSteps,
            originatingSelectionKey,
            maxRepeatedToolStepsOverride is not null ? ExecutionLimitSource.DispatchOverride
                : profile?.MaxRepeatedToolSteps is not null ? ExecutionLimitSource.Profile : null,
            maxRepeatedToolStepsOverride ?? profile?.MaxRepeatedToolSteps);
    }

    /// <summary>Check a recorded selection against the exact normalized key this resolver produces.</summary>
    public static bool ChosenKeyMatchesSelection(
        string? chosenKey, string adapter, string? model, string role, DeclaredTaskSize size) =>
        chosenKey is not null
        && model is { Length: > 0 }
        && string.Equals(
            chosenKey,
            new ExecutionLimitProfileKey(adapter, model, role, size).ToString(),
            StringComparison.Ordinal);

    public static void Validate(IReadOnlyList<ExecutionLimitProfile>? profiles)
    {
        if (profiles is null) return;
        var keys = new HashSet<ExecutionLimitProfileKey>();
        for (var index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index] ?? throw Invalid(index, "row is null");
            if (string.IsNullOrWhiteSpace(profile.Adapter)
                || string.IsNullOrWhiteSpace(profile.Model)
                || string.IsNullOrWhiteSpace(profile.Role))
                throw Invalid(index, "adapter, model, and role are required");
            var size = ParseSize(profile.DeclaredTaskSize, index);
            if (profile.Timeout <= TimeSpan.Zero || profile.Timeout == Timeout.InfiniteTimeSpan)
                throw Invalid(index, "timeout must be finite and positive");
            if (profile.TokenBudget <= 0) throw Invalid(index, "token budget must be positive");
            if (profile.MaxToolSteps <= 0) throw Invalid(index, "tool steps must be positive");
            if (profile.MaxRepeatedToolSteps is <= 0) throw Invalid(index, "repeated tool steps must be positive");
            var key = new ExecutionLimitProfileKey(profile.Adapter, profile.Model, profile.Role, size);
            if (!keys.Add(key)) throw Invalid(index, $"duplicate normalized key '{key}'");
        }
    }

    internal static void ValidateJson(JsonElement root)
    {
        if (TryGetProfilesProperty(root, out var profiles)
            && profiles.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
            throw new ExecutionLimitProfileConfigurationException(
                "ExecutionLimitProfiles must be an array of complete profile rows.");
    }

    internal static bool TryGetProfilesProperty(JsonElement root, out JsonElement profiles)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            profiles = default;
            return false;
        }

        JsonElement? found = null;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "ExecutionLimitProfiles", StringComparison.OrdinalIgnoreCase))
            {
                if (found is not null)
                    throw new ExecutionLimitProfileConfigurationException(
                        "ExecutionLimitProfiles must not be specified more than once, including case variants.");
                found = property.Value;
            }
        }

        if (found is { } value)
        {
            profiles = value;
            return true;
        }

        profiles = default;
        return false;
    }

    private static DeclaredTaskSize ParseSize(string? value, int index)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Enum.TryParse<DeclaredTaskSize>(value, true, out var parsed)
            || !Enum.IsDefined(parsed))
            throw Invalid(index, "declared task size must be small, medium, large, or unknown");
        return parsed;
    }

    private static ExecutionLimitProfileConfigurationException Invalid(int index, string reason) =>
        new($"ExecutionLimitProfiles row {index + 1} is invalid: {reason}.");

    private readonly record struct ExecutionLimitProfileKey
    {
        public ExecutionLimitProfileKey(string adapter, string model, string role, DeclaredTaskSize size)
        {
            Adapter = Normalize(adapter); Model = Normalize(model); Role = Normalize(role); Size = size;
        }
        public string Adapter { get; }
        public string Model { get; }
        public string Role { get; }
        public DeclaredTaskSize Size { get; }
        public override string ToString() => string.Join('/', Adapter, Model, Role, Size.ToString().ToLowerInvariant());
    }

    private static ExecutionLimitProfileKey ToKey(this ExecutionLimitProfile profile) =>
        new(profile.Adapter!, profile.Model!, profile.Role!, Enum.Parse<DeclaredTaskSize>(profile.DeclaredTaskSize!, true));

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
}
