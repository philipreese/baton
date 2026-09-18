using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>A structured reason for one bounded, engine-owned recovery transition.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RecoveryCauseKind
{
    OutstandingToolAtTerminalSuccess,
}

/// <summary>
/// The recorded facts needed to explain and bound an automatic recovery. The command is optional
/// because a vendor may report only the tool name in its stream envelope.
/// </summary>
public sealed record RecoveryCause(
    RecoveryCauseKind Kind,
    string ToolName,
    string? CommandLine = null);
