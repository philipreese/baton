namespace Baton.Dispatch;

/// <summary>
/// The environment variables a spawned worker inherits from the operator's shell — an allowlist,
/// because until #549 it inherited everything (<c>BatonTask.WithClearEnv</c> was never called, and the
/// binding's default is inherit-all).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an allowlist and not a denylist.</b> The gate a worker runs under can be changed by
/// variables AER does not choose: <c>CLAUDE_CODE_SIMPLE=1</c> disables hooks exactly as <c>--bare</c>
/// does, and it needs only to be exported in the shell the daemon was started from. A denylist has to
/// enumerate every such variable, for every vendor, forever, and fails **open** on the one nobody
/// thought of. This fails **closed**: an unknown variable does not reach the worker. The direction is
/// the same argument <c>ClaudeWorkerAdapter</c> records for never passing <c>--bare</c> — an auth
/// failure is loud, and a missing gate is silent.
/// </para>
/// <para>
/// <b>The per-vendor minimum is measured, not assumed</b> — see <c>docs/vendor-doc-audit.md</c>
/// §"Environment starvation" for what each CLI survives and which of them can serve as a control.
/// An advisory pass proposed a much wider list on credential-discovery grounds that the measurement
/// contradicts.
/// </para>
/// <para>
/// Entries beyond that measured minimum are ordinary OS, toolchain and network-reachability
/// plumbing, included to keep the blast radius of clearing the environment small. None of them is
/// known to influence a permission gate; anything that does belongs in an adapter's own explicit
/// <c>Environment</c>, which is applied after this and therefore wins.
/// </para>
/// <para>
/// <b>The measurement is Windows-only.</b> Said here because the first version of this file read as
/// if the whole allowlist were evidence-backed: only <c>USERPROFILE</c> carries a measurement.
/// </para>
/// <para>
/// <b><c>CLAUDE_CONFIG_DIR</c> is deliberately absent.</b> Architecture Rule 4's 2026-07-25
/// correction permits it, and per-worker config roots are a live design option — so its absence
/// here is a decision and not an oversight. A config root AER did not choose is a gate surface AER
/// did not choose, which is the same argument the rest of this list rests on. A per-worker root is
/// set by an adapter's own <c>Environment</c>, which is applied after this and therefore wins. The
/// cost is real and worth naming: on a host where only an alternate root holds a subscription
/// login, every worker now dies at <c>Not logged in</c> — loud, which is the direction this file
/// chooses everywhere.
/// </para>
/// </remarks>
public static class InheritedEnvironment
{
    /// <summary>Meaningful on every platform AER runs on.</summary>
    private static readonly string[] Common =
    [
        // Load-bearing: AER spawns "claude"/"agy"/"dotnet" by NAME, so without PATH the spawn itself
        // fails before any vendor question arises.
        "PATH",
        "LANG", "LC_ALL", "LC_CTYPE", "TZ",
        // DOTNET_ROOT locates the host itself, so a `dotnet exec <dll>`-spawned worker cannot start
        // without it on a machine where dotnet is not on PATH by absolute path.
        "DOTNET_ROOT", "DOTNET_ROOT(x86)",
        // NOT the .NET first-run suppressors, and their absence is measured rather than assumed.
        // When clearing the environment timed the dialogue e2e tests out at their 30s limit on
        // Windows CI (passing locally in 2s), DOTNET_CLI_HOME/DOTNET_NOLOGO/
        // DOTNET_SKIP_FIRST_TIME_EXPERIENCE/DOTNET_MULTILEVEL_LOOKUP/DOTNET_CLI_TELEMETRY_OPTOUT/
        // NUGET_PACKAGES went in alongside the Windows profile block below, and this comment credited
        // the DOTNET_* half with the fix. A reviewer pointed out that `dotnet exec` is the HOST, not
        // the SDK CLI: no first-run experience, no logo, no CLI telemetry, no NuGet resolution -- so
        // none of them could be worth 28 seconds, while the profile block genuinely is load-bearing
        // for the `powershell -File` participants. Removing them and re-running Windows CI settled
        // it: green, with Baton.Cli.Tests at 15s for the whole assembly against the 30s per-step
        // ceiling (CI run 30473155421). They were inert. The profile block is the real fix.
        // NUGET_HTTP_CACHE_PATH went with them, for the same reason and one more: it had come to be
        // held here by CoreDispatcherTests planting its sentinel in it, which is a production entry
        // justified by a test. The test now plants LC_CTYPE, which is on this list on its own merits.
        //
        // Keep the reasoning, not just the outcome: a variable belongs here because something on the
        // spawn path reads it, and "it was in the commit that fixed something" is not that.

        // REACHABILITY. Both vendor CLIs are network clients, and on a corporate network these are
        // the whole of how they reach anything. Omitting them was a regression this file introduced
        // and did not notice: the allowlist was measured on a host that needs none of them, so every
        // arm passed while an operator behind a proxy would have had every vendor call fail with no
        // network and no TLS trust. Measured-on-one-machine is not measured (`claim-scope`).
        // Lowercase forms are separate variables on POSIX and several clients read only those.
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "ALL_PROXY",
        "http_proxy", "https_proxy", "no_proxy", "all_proxy",
        "NODE_EXTRA_CA_CERTS", "SSL_CERT_FILE", "SSL_CERT_DIR", "REQUESTS_CA_BUNDLE",
    ];

    private static readonly string[] Windows =
    [
        // USERPROFILE is the measured one (agy). The rest are what a Windows process and cmd.exe
        // assume exist; SYSTEMROOT in particular is required for socket and crypto initialisation.
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "SYSTEMROOT", "WINDIR", "SYSTEMDRIVE",
        "APPDATA", "LOCALAPPDATA", "PROGRAMDATA", "ALLUSERSPROFILE",
        "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMW6432",
        "COMSPEC", "PATHEXT", "TEMP", "TMP",
        // powershell.exe resolves its own modules through this; a declared worker command can be
        // powershell on Windows.
        "PSMODULEPATH",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE",
    ];

    /// <summary>The allowlisted names, in a stable order.</summary>
    internal static IReadOnlyList<string> Names => [.. Common, .. Windows];

    /// <summary>
    /// Each allowlisted variable that is actually set, with the value this process sees. Variables
    /// that are unset are skipped rather than passed as empty — an empty <c>USERPROFILE</c> is a
    /// different failure from an absent one, and neither is worth inventing.
    /// </summary>
    /// <remarks>
    /// <b>One exception, and it is the only one on this list:</b> under POSIX <c>TZ=""</c> means UTC
    /// while an absent <c>TZ</c> means system local time, so collapsing empty into absent silently
    /// moves an operator who exported <c>TZ=</c> back onto local time. Every other candidate checked
    /// — <c>LC_ALL</c>, <c>NO_PROXY</c>, <c>HTTP_PROXY</c>, <c>TMPDIR</c>,
    /// <c>DOTNET_CLI_TELEMETRY_OPTOUT</c> — means the same thing empty as absent. Recorded rather
    /// than fixed: the blast radius of an empty-string pass-through across the whole list is worse
    /// than one wrong timezone, and a false absolute in a file whose subject is honesty about what
    /// was measured is the part actually worth removing.
    /// </remarks>
    public static IEnumerable<(string Name, string Value)> Resolve()
    {
        foreach (var name in Names)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                yield return (name, value);
            }
        }
    }
}
