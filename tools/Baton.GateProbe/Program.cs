using System.Text.Json;
using Baton.Vendors;
using Baton.Domain;

namespace Baton.GateProbe;

/// <summary>
/// Prints the invocation a real <see cref="IWorkerAdapter"/> would dispatch, as JSON, so a check can
/// run <b>that exact argv</b> against the vendor CLI instead of a hand-written approximation (#550).
/// </summary>
/// <remarks>
/// <para>
/// This exists so a check does not have to write the flag list down — see
/// <c>gate.adapters-own-flag-set-still-gates</c> in <c>tools/vendor-verify/verify.py</c> for why a
/// hand-picked one cannot catch the next suppression.
/// </para>
/// <para>
/// <b>In <c>tools/</c> and not <c>tests/</c>, for a measured reason.</b> It was written under
/// <c>tests/</c> first, alongside <c>Aer.Flow.CrashTestHost</c>, and did not work:
/// <c>tests/Directory.Build.props</c> links <c>BatonHomeRedirect</c> into every project there, giving
/// each one a throwaway per-process <c>BATON_HOME</c>. This probe's whole job is to write AER's real
/// launch config — the settings file carrying the <c>PreToolUse</c> hook — somewhere a SEPARATE
/// process can then read it, and a home that dies with the probe leaves the caller running
/// <c>claude</c> against a <c>--settings</c> path that no longer exists.
/// </para>
/// <para>
/// An <c>aer</c> verb was rejected too: a CLI verb is a product surface AER would owe a contract
/// for, and this is an instrument.
/// </para>
/// <para>
/// Placeholders are left UNEXPANDED — <c>%BATON_OUTPUT_DIR%</c> and friends are what the adapter really
/// produces, and <c>CoreDispatcher</c> expands them at dispatch. The caller substitutes its own
/// directories, which keeps this honest about what the adapter emits.
/// </para>
/// <para>
/// <c>--stream-json</c>, <c>--no-outputs</c>, <c>--model</c> and <c>--timeout</c> (#2537) exist for
/// one caller: a check that needs the SAME resolved invocation a real read-only StreamJson binding
/// would get (model, hook, env, timeout, seed files) so it can transform delivery without
/// hand-writing a second approximation of <c>Resolve</c>'s output. Each is additive and defaults to
/// the prior behaviour untouched. Deliberately no <c>--working-directory</c>: setting
/// <see cref="WorkerInvocation.WorkingDirectory"/> routes through <see cref="ProjectCeilingGate"/>,
/// which refuses any directory with no recorded trust-store entry (decision 0004) — a caller that
/// wants a vendor cwd passes it to the spawned process directly, as every existing check here
/// already does, rather than through the resolved invocation.
/// </para>
/// </remarks>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine(
                "usage: Baton.GateProbe <claude|gemini> [--grant-writes] [--prompt <text>]\n" +
                "    [--stream-json] [--no-outputs] [--model <name>] [--timeout <seconds>]\n" +
                "Prints {program, args, environment, promptText, seedFiles,\n" +
                "    hookVerdictLedgerFileName} for the adapter's resolved invocation.");
            return 2;
        }

        var vendor = args[0];
        var grantWrites = args.Contains("--grant-writes");
        var promptIndex = Array.IndexOf(args, "--prompt");
        var prompt = promptIndex >= 0 && promptIndex + 1 < args.Length ? args[promptIndex + 1] : "Say OK.";
        var streamJson = args.Contains("--stream-json");
        var noOutputs = args.Contains("--no-outputs");
        var modelIndex = Array.IndexOf(args, "--model");
        var model = modelIndex >= 0 && modelIndex + 1 < args.Length ? args[modelIndex + 1] : null;
        var timeoutIndex = Array.IndexOf(args, "--timeout");
        TimeSpan? timeout = timeoutIndex >= 0 && timeoutIndex + 1 < args.Length
            ? TimeSpan.FromSeconds(double.Parse(args[timeoutIndex + 1]))
            : null;

        // Reads withheld too, so the denied-tools channel carries something on every arm and the
        // difference between arms is exactly the one category under test.
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: grantWrites, RunShellCommands: false, NetworkAccess: false);

        IWorkerAdapter adapter = vendor switch
        {
            "claude" => new ClaudeWorkerAdapter(),
            "agy" => new AgyWorkerAdapter(),
            _ => throw new ArgumentException($"unknown vendor '{vendor}'"),
        };

        var target = adapter.Resolve(
            new WorkerInvocation(prompt, Model: model, PermissionGrant: grant, StreamJson: streamJson, Timeout: timeout),
            new WorkerContract(
                "probe", [], noOutputs ? [] : [new ProducedOutput("out.txt")], []));

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            program = target.Program,
            args = target.Args,
            environment = (target.Environment ?? []).ToDictionary(e => e.Name, e => e.Value),
            promptText = target.PromptText,
            seedFiles = (target.SeedFiles ?? []).Select(f => new { path = f.PathTemplate, content = f.Content }),
            hookVerdictLedgerFileName = target.HookVerdictLedgerFileName,
        }));

        return 0;
    }
}
