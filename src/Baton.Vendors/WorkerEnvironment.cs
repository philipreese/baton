namespace Baton.Vendors;

/// <summary>
/// Environment variable names AER sets on a worker for its own gate to read back, shared by every
/// adapter that ships one.
/// </summary>
public static class WorkerEnvironment
{
    /// <summary>
    /// The workspace a granted write is bounded to (#679) — the worker's <c>WorkingDirectory</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Told to the hook rather than inferred there. A hook subprocess is spawned by the vendor CLI and
    /// inherits <i>its</i> working directory, so it cannot read the workspace off its own cwd; and the
    /// payload's own <c>cwd</c> is the vendor's account of itself where this is AER's.
    /// </para>
    /// <para>
    /// Mirrored as a literal in <c>Baton.Cli</c>, which cannot be referenced from here. Getting the two
    /// out of step is fail-closed — the gate reads no workspace and narrows a granted write to the
    /// outbox — so it costs a broken run rather than opening a hole.
    /// </para>
    /// <para>
    /// Left unset when the invocation declares no working directory. The gate reads absence as "bound
    /// a granted write to the outbox alone", never as "unbounded".
    /// </para>
    /// </remarks>
    public const string WorkspaceVariable = "BATON_WORKSPACE_DIR";

    /// <summary>The dispatch-captured base commit for the exact-file restore MCP tool.</summary>
    public const string ExactFileRestoreBaseVariable = "BATON_EXACT_FILE_RESTORE_BASE";
}
