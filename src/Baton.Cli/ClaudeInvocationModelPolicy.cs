namespace Baton.Cli;

/// <summary>
/// The admission policy for the one adapter whose CLI default is not a Baton-approved invocation
/// model. This reads the final invocation tuple; it neither resolves nor writes a model.
/// </summary>
internal static class ClaudeInvocationModelPolicy
{
    internal const string ExplicitModelRemedy = "pass --model sonnet, --model opus, or --model haiku";

    internal static string? RefusalMessage(string? adapter, string? model) =>
        string.Equals(adapter?.Trim(), "claude", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(model)
            ? "Claude has no approved implicit invocation model: the standing model policy forbids deferring to the vendor CLI default."
            : null;
}
