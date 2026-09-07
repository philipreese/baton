namespace Baton.Domain;

/// <summary>
/// The one home for every per-command-class wall clock (#1998). Nothing restates these values: the
/// broker reads them to bound a command, the timeout text reads them to say what it exceeded, and the
/// room's delivery check reads <see cref="ShippingBreachReason"/> to say why a branch never reached
/// origin.
/// <para>
/// The ruling that sizes them, and the two failures on 2026-09-06 it was paid for, are
/// <c>spec/baton.md</c> §9's per-command-class paragraph. Not restated here.
/// </para>
/// <para>
/// <b>Provenance of the gate figure.</b> It is not read from <c>tools/gates/</c> — <c>gates.py</c>
/// records a receipt (tree hash, dirty hash, mode, timestamp) and no duration. It is the measurement
/// the register already holds, <c>spec/baton.md</c> C-12's #1958 paragraph, taken from the cost
/// ledger's own <c>prePushGateMs</c> over 23 pushes. C-12 is that figure's one home;
/// <see cref="MedianPrePushGateWallClock"/> is the single place this code spells it, and every ceiling
/// below is an expression over it rather than a second number.
/// </para>
/// <para>
/// <b>What that figure is not.</b> The register records a MEDIAN, not a percentile, so the ruling's
/// own ×1.5 margin is applied to a median and <see cref="Gate"/> is not a tail bound. C-12's other
/// median says why it cannot be one: 160.2 s of the 369.2 s was spent queued on the shared build
/// lock, whose own wait budget (<c>tools/buildlock.py</c>) is 1800 s by default, so a contended run is
/// unbounded in exactly the direction a ceiling would have to cover. What these ceilings buy is that a
/// command known to be progressing is not killed at a quick command's figure; a fleet-contended one
/// can still exceed them.
/// </para>
/// </summary>
public static class ShellCommandCeilings
{
    /// <summary>
    /// Every command that is not a named gate or shipping command. Unchanged by #1998 — this is the
    /// flat ceiling the broker enforced before the classes existed.
    /// </summary>
    public static readonly TimeSpan Other = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The median wall clock of one pre-push gate run, <b>cited from <c>spec/baton.md</c> C-12 rather
    /// than re-measured here</b>. What that hook runs is <c>gates-fast</c>, which is why the gate class
    /// is sized from it; the register's figure measures the whole hook, so this is the gate task plus
    /// the hook around it rather than a claim about <c>gates-fast</c> alone. See this class's own remark
    /// for what it does not bound.
    /// </summary>
    public static readonly TimeSpan MedianPrePushGateWallClock = TimeSpan.FromSeconds(369.2);

    /// <summary>The gate class: <see cref="MedianPrePushGateWallClock"/> plus 50 % margin (the ruling's own sizing).</summary>
    public static readonly TimeSpan Gate = MedianPrePushGateWallClock * 1.5;

    /// <summary>
    /// What a shipping command is allowed on top of the gate its hook runs: the transfer itself plus
    /// <c>gh</c>'s round trip. An ESTIMATE, not a measurement — a push of a few commits over a working
    /// network is seconds, and this is slack for a slow one rather than a figure anything recorded.
    /// </summary>
    public static readonly TimeSpan PushTransferAllowance = TimeSpan.FromMinutes(2);

    /// <summary>The shipping class: the <see cref="Gate"/> ceiling (the pre-push hook runs the gate) plus <see cref="PushTransferAllowance"/>.</summary>
    public static readonly TimeSpan Shipping = Gate + PushTransferAllowance;

    /// <summary>The ceiling <paramref name="commandClass"/> runs under. The one read every enforcing path takes.</summary>
    public static TimeSpan For(ShellCommandClass commandClass) => commandClass switch
    {
        ShellCommandClass.Shipping => Shipping,
        ShellCommandClass.Gate => Gate,
        _ => Other,
    };

    /// <summary>
    /// The marker a shipping-class timeout carries into the tool result the worker reads, and therefore
    /// into the room's captured <c>.stdout.log</c>. Bracketed and namespaced for the same reason
    /// <see cref="GrantRefusal.Marker"/> is, and read back under the same anchoring rule: only inside a
    /// vendor's tool-RESULT node for a Baton run-command call
    /// (<c>Status.IWorkerUsageParser.ReportsShippingCeilingTimeout</c>), never as a search of the whole
    /// stream — which in THIS repository would otherwise match a lane that merely read this file.
    /// </summary>
    public const string ShippingCeilingMarker = "[baton:shipping-ceiling-exceeded]";

    /// <summary>
    /// What a killed command's tool result says. Names the class and prints the ceiling that actually
    /// applied, so the text cannot drift from the table above or from a caller's injected ceiling.
    /// </summary>
    /// <param name="effectiveCeiling">
    /// The ceiling this command was actually run under — <see cref="For"/> in production, and a test's
    /// own short value where the timeout arm has to be reachable in a second. Printed rather than
    /// <see cref="For"/> re-read, so the message never claims a bound the command did not run under.
    /// </param>
    public static string DescribeTimeout(ShellCommandClass commandClass, TimeSpan effectiveCeiling)
    {
        var text = $"Command exceeded Baton's {Name(commandClass)} command ceiling "
            + $"({Seconds(effectiveCeiling)} s).";
        return commandClass == ShellCommandClass.Shipping
            ? $"{ShippingCeilingMarker} {text} The push ran past it while the pre-push gate was still running."
            : text;
    }

    /// <summary>
    /// The room-facing half of the same fact: why a delivery check found no branch on origin (#1998).
    /// Reads the TABLE value rather than any injected one, deliberately — the room is not the process
    /// that ran the command, and a test seam's one-second ceiling is not what a lane ran under.
    /// </summary>
    public static string ShippingBreachReason() =>
        $"the push exceeded the shipping ceiling ({Seconds(Shipping)} s) during the pre-push gate";

    /// <summary>
    /// Whether <paramref name="text"/> is a shipping-class ceiling timeout this build produced —
    /// <b>the marker FIRST, never anywhere in the text</b>. <see cref="DescribeTimeout"/> puts it at
    /// position 0 and <c>Baton.Vendors.CodexDynamicToolResult.Failed</c> carries that text verbatim, so
    /// a leading test costs nothing and discriminates the case a containment test cannot: a command
    /// whose own OUTPUT quotes the marker, which in THIS repository is any lane that diffs or prints
    /// this file. It is one of two conditions — the caller also reads the item's status
    /// (<c>Status.IWorkerUsageParser.ReportsShippingCeilingTimeout</c>), because a non-zero exit prefixes
    /// its own line ahead of the command's output.
    /// </summary>
    public static bool IsShippingCeilingTimeout(string? text) =>
        text is not null && text.StartsWith(ShippingCeilingMarker, StringComparison.Ordinal);

    private static string Name(ShellCommandClass commandClass) => commandClass switch
    {
        ShellCommandClass.Shipping => "shipping",
        ShellCommandClass.Gate => "gate",
        _ => "default",
    };

    private static string Seconds(TimeSpan ceiling) => ceiling.TotalSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
