using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Vendors;

/// <summary>
/// The engine-run capture worker (decision 0047 §4): produces the <c>diff-of-work-so-far</c> artifact a
/// downstream phase reads, by running <c>git diff --output=&lt;artifact&gt; &lt;base&gt;</c> in the
/// workspace. Registered under the capability name <see cref="WorkflowTemplateComposer.CaptureAdapter"/>
/// (<c>"capture"</c>); to <c>Baton</c> it is an ordinary worker dispatched through an adapter, so the
/// engine never learns git exists — the git-ness is quarantined here (Adapter Isolation, docs/agents/developing-baton.md
/// rule 2).
/// </summary>
/// <remarks>
/// <para>
/// <b>The base ref rides in <see cref="WorkerInvocation.PromptTemplate"/></b> — the field
/// <see cref="CommandWorkerAdapter"/> also repurposes for non-prose per-step data — injected by the
/// run entrypoint at workflow start — <c>Baton.Cli.WorkspaceHead</c>'s own doc has the history of
/// what that captures and mirrors. It is diffed against the working tree, not <c>base..HEAD</c> — decision 0047 §4
/// has the why (committed and uncommitted work both captured, so no worker is forced to commit).
/// </para>
/// <para>
/// <b>No shell wrap, no arbitrary interpolation (0047 §4).</b> git is spawned directly; the only
/// interpolated values are a captured SHA and the fixed artifact name. The output path uses the
/// <c>%BATON_OUTPUT_DIR%</c> placeholder, which <c>CoreDispatcher</c> expands in the arg <em>before</em>
/// process creation, so git (not a shell) receives the resolved path and <c>--output</c> writes the
/// diff straight to it. A non-git workspace never reaches here (the entrypoint refuses to inject a base
/// and fails loudly); were it to, git's own non-zero exit surfaces as a failed execution.
/// </para>
/// <para>
/// <b>No grant machinery (#1166).</b> Like <see cref="CommandWorkerAdapter"/>, this is engine-deterministic
/// rather than a vendor worker — there is no AI on the other end to grant anything to, so it does not
/// implement <see cref="IPermissionGrantTranslator"/> and <see cref="ProjectCeilingGate"/> never runs
/// against it.
/// </para>
/// </remarks>
public sealed class CaptureWorkerAdapter : IWorkerAdapter
{
    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var baseRef = invocation.PromptTemplate?.Trim();
        if (string.IsNullOrEmpty(baseRef))
        {
            throw new InvalidOperationException(
                "The capture worker has no base ref to diff against. The run entrypoint injects the "
                + "workspace's HEAD-at-start into the capture binding's PromptTemplate; an empty value "
                + "means that injection did not run, or the workspace is not a git repository.");
        }

        // %BATON_OUTPUT_DIR% is expanded in-arg by CoreDispatcher before spawning, so git receives the
        // resolved path. --output writes the diff to that file directly -- no stdout redirection, no shell.
        var outputReference = WorkerEnvironmentReference.For("BATON_OUTPUT_DIR");
        var args = new List<string>
        {
            "diff",
            $"--output={outputReference}/{WorkflowTemplateComposer.CaptureOutputName}",
            baseRef,
        };

        return new CoreDispatchTarget("git", args, invocation.WorkingDirectory);
    }
}
