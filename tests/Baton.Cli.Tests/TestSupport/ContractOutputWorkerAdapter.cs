using Baton.Vendors;
using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Cli.Tests.TestSupport;

/// <summary>
/// A CI-safe stand-in for a well-behaved (or silently no-op) worker, driven by what the
/// <see cref="WorkerContract"/> declares rather than by the prompt — so an <c>baton dispatch</c> test
/// can run a real catalog role through the whole pump without a live LLM and without the prompt having
/// to be a literal shell command (which <see cref="ShellCommandWorkerAdapter"/> requires and
/// <c>RoleDispatch</c>'s prose prompt is not). When <paramref name="satisfyOutputs"/> is true it writes
/// each declared output into <c>$BATON_OUTPUT_DIR</c>; when false it exits 0 having written nothing — the
/// exact "exit 0 but produced nothing" the role's contract floor exists to catch.
/// </summary>
/// <param name="satisfyOutputs">Whether to write the declared outputs at all.</param>
/// <param name="outputFixtures">
/// Optional map of output name → a source file to copy in place of the placeholder <c>x</c>. An output
/// whose contract is a schema (e.g. <c>verdict.json</c> must parse as a <c>ReviewVerdict</c>) needs a
/// conforming document, not <c>x</c>; the test pre-writes that document with a real file API and this
/// copies it — no JSON is assembled through a shell echo. Outputs not in the map still get <c>x</c>.
/// </param>
/// <param name="bindsDispatchedWorkspaceReadable">
/// #1987: what this fake answers for <see cref="IWorkerAdapter.BindsDispatchedWorkspaceReadable"/> —
/// the question <c>DispatchCommand</c>'s pre-run workspace disclosure asks the bound adapter. Both
/// answers are dispatchable here (the worktree itself is still granted by the real registry's agy
/// entry), which is what makes the disclosure's two arms testable at all: <c>agy</c> is the only
/// vendor adapter ever handed an auto-provisioned worktree, so the false arm has no vendor tag of
/// its own to be dispatched under.
/// </param>
/// <param name="stdoutFixture">
/// Optional file emitted verbatim on stdout before producing outputs. This lets an end-to-end test
/// run a local fake process under a shipped adapter name while exercising that adapter's real stream
/// parser metadata, instead of making an unsupported fake parser turn a measured count into null.
/// </param>
internal sealed class ContractOutputWorkerAdapter(
    bool satisfyOutputs,
    IReadOnlyDictionary<string, string>? outputFixtures = null,
    IReadOnlyList<WorkerCapabilityItem>? capabilities = null,
    int failureExitCode = 0,
    bool bindsDispatchedWorkspaceReadable = false,
    string? stdoutFixture = null) : IWorkerAdapter
{
    public bool BindsDispatchedWorkspaceReadable => bindsDispatchedWorkspaceReadable;

    /// <summary>The directory <see cref="DiscoverCapabilitiesAsync"/> was last called with — lets a test pin which directory <c>DispatchCommand</c> actually scanned (#1512 H1).</summary>
    public string? LastDiscoverCapabilitiesWorkingDirectory { get; private set; }

    public Task<WorkerCapabilities> DiscoverCapabilitiesAsync(string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        LastDiscoverCapabilitiesWorkingDirectory = workingDirectory;
        return Task.FromResult(new WorkerCapabilities("fake", capabilities ?? Array.Empty<WorkerCapabilityItem>(), Array.Empty<string>()));
    }

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        var commands = new List<string>();
        if (stdoutFixture is not null)
        {
            commands.Add($"type {stdoutFixture}");
        }

        if (satisfyOutputs && contract.ProducedOutputs.Count > 0)
        {
            commands.AddRange(contract.ProducedOutputs.Select(o => WriteCommand(o.Name)));
        }
        else
        {
            commands.Add($"exit {failureExitCode}");
        }

        return new CoreDispatchTarget("cmd", ["/c", string.Join(" & ", commands)], invocation.WorkingDirectory);
    }

    private string WriteCommand(string outputName)
    {
        if (outputFixtures is not null && outputFixtures.TryGetValue(outputName, out var source))
        {
            // Unquoted paths on Windows on purpose: the managed spawn path (ProcessStartInfo.ArgumentList)
            // wraps this whole space-containing script in quotes for CreateProcess, so inner quotes
            // collide and cmd reports a bogus path. The
            // rest of this fake already assumes space-free temp paths (its echo redirects are unquoted
            // too), so this keeps the same assumption rather than adding a new one.
            return $"copy /y {source} %BATON_OUTPUT_DIR%\\{outputName}";
        }

        return $"echo x>%BATON_OUTPUT_DIR%\\{outputName}";
    }
}
