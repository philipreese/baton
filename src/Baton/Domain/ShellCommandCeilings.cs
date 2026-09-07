namespace Baton.Domain;

/// <summary>
/// The one home for every per-command-class wall clock (#1998). Nothing restates these values: the
/// broker reads them to bound a command, the timeout text reads them to say what it exceeded, and the
/// room's delivery check reads <see cref="ShippingBreachReason"/> to say why a branch never reached
/// origin.
/// <para>
/// <b>The failure these are sized against, measured 2026-09-06</b> (#1998): two lanes finished their
/// work, committed it, and then lost the whole run at the push. A <c>git push</c> in this repository
/// runs <c>.githooks/pre-push</c>, which runs <c>gates-fast</c>; under the flat five-minute ceiling the
/// push was killed while that gate was still making progress, so the room settled
/// <c>Verify failed (branch-not-pushed, pr-not-open)</c> and a conductor pushed by hand. A ceiling that
/// fires on a command known to be progressing is not a bound on runaway work, it is a bound on finished
/// work.
/// </para>
/// <para>
/// <b>Provenance of the gate figure, stated because it is not what #1998's body assumed.</b> That body
/// says <c>tools/gates/</c> publishes the <c>gates-fast</c> wall clock; checked on 2026-09-06, it does
/// not — <c>gates.py</c> records a receipt (tree hash, dirty hash, mode, timestamp) and no duration, so
/// there is nothing to read. <see cref="MeasuredGatesFastWallClock"/> is therefore a named constant
/// here, carrying the figure the dispatch brief and #1998 both state (<c>gates-fast</c> takes about
/// five minutes on this machine) and its date. It is a measurement of one machine on one day, not a
/// bound: a contended build lock makes a real run arbitrarily longer, and no ceiling here claims to
/// cover that.
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
    /// <c>pixi run gates-fast</c>'s wall clock, measured 2026-09-06 on the dispatch host. See this
    /// class's own remark for why this is a constant rather than a value read from <c>tools/gates/</c>,
    /// and for what it does not claim.
    /// </summary>
    public static readonly TimeSpan MeasuredGatesFastWallClock = TimeSpan.FromMinutes(5);

    /// <summary>The gate class: <see cref="MeasuredGatesFastWallClock"/> plus 50 % margin (the ruling's own sizing).</summary>
    public static readonly TimeSpan Gate = MeasuredGatesFastWallClock * 1.5;

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

    /// <summary>Whether <paramref name="text"/> is a shipping-class ceiling timeout this build produced.</summary>
    public static bool IsShippingCeilingTimeout(string? text) =>
        text is not null && text.Contains(ShippingCeilingMarker, StringComparison.Ordinal);

    private static string Name(ShellCommandClass commandClass) => commandClass switch
    {
        ShellCommandClass.Shipping => "shipping",
        ShellCommandClass.Gate => "gate",
        _ => "default",
    };

    private static string Seconds(TimeSpan ceiling) => ceiling.TotalSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
