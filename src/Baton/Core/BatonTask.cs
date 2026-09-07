using Baton.Core.Internal;

namespace Baton.Core;

/// <summary>
/// A single-shot process execution: one command, one run, a deterministic lifecycle. Owns the spawned
/// process's entire descendant tree via a Windows Job Object for the duration of
/// <see cref="Run"/>/<see cref="RunAsync"/>, exposes configuration via fluent <c>With*</c> methods, and
/// reports progress via the <see cref="EventRaised"/> event rather than a raw callback.
/// </summary>
/// <remarks>
/// <para>
/// Windows-only (post-#1424, spec/baton.md C-10): this is a straight port of aer-core's Rust M1–M5
/// milestones into managed code (#1474) — no P/Invoke into a native library, no FFI boundary. The
/// consumer-facing shape (constructor, fluent config, <see cref="Run"/>/<see cref="RunAsync"/>,
/// <see cref="EventRaised"/>, the exception types) is unchanged from the deleted <c>Baton.Core</c>
/// binding project so <c>CoreDispatcher</c>'s call sites did not need to change; only FFI-only
/// artifacts (opaque native handles, a C-ABI error-code enum, callback marshalling) were dropped.
/// </para>
/// <para>
/// A given instance may be run at most once, enforced by this wrapper with
/// <see cref="InvalidOperationException"/> (previously also enforced natively; now there is only one
/// enforcement point).
/// </para>
/// </remarks>
public sealed class BatonTask : IDisposable
{
    private readonly string program;
    private readonly string[] args;
    private readonly List<(string Key, string Value)> envVars = [];
    private TimeSpan? timeout;
    private Func<TimeSpan>? timeoutBudget;
    private bool captureOutput;
    private bool clearEnv;
    private string? cwd;
    private int hasRunFlag;
    private int disposedFlag;

    /// <summary>
    /// Raised for every event a run produces, in delivery order: one <c>Started</c>, then interleaved
    /// <c>StdoutChunk</c>/<c>StderrChunk</c> events (only when <see cref="WithCaptureOutput"/> was
    /// enabled; each stream's <see cref="BatonEventArgs.Seq"/> is monotonically increasing within that
    /// stream), then one <c>Exited</c>. Invoked synchronously on the thread executing the run — for
    /// <see cref="RunAsync"/> that is the thread-pool thread the run was scheduled on, not the caller's
    /// original thread.
    /// </summary>
    public event EventHandler<BatonEventArgs>? EventRaised;

    /// <summary>
    /// Creates a task for the given program and arguments. The process is not spawned until
    /// <see cref="Run"/> or <see cref="RunAsync"/> is called.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> or <paramref name="args"/> is null.</exception>
    public BatonTask(string program, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(args);

        this.program = program;
        this.args = args;
    }

    /// <summary>
    /// Sets a wall-clock timeout. If the process has not exited by the deadline it is killed and
    /// <see cref="Run"/>/<see cref="RunAsync"/> throws <see cref="BatonTimeoutException"/>. Must be
    /// called before the task is run.
    /// </summary>
    /// <returns>This instance, for chaining.</returns>
    public BatonTask WithTimeout(TimeSpan timeout)
    {
        ThrowIfDisposed();
        this.timeout = timeout;
        return this;
    }

    /// <summary>
    /// Makes the timeout re-readable while the run is in flight: <paramref name="budget"/> is polled
    /// periodically and its return value is the run's total wall-clock budget, measured from the same
    /// start as <see cref="WithTimeout"/>'s. A budget shorter than the configured timeout is ignored —
    /// this can only ever extend a run, never cut one short — and a probe that throws credits nothing.
    /// Must be called before the task is run, and only alongside <see cref="WithTimeout"/>: with no
    /// timeout configured there is no deadline to extend and this is inert.
    /// </summary>
    /// <remarks>
    /// Exists for <see cref="Dispatch.BuildLockWaitCredit"/> (#2019): a lane's box has to be able to
    /// grow while the lane runs, because the queueing it is being credited for is measured as it
    /// happens. Without a probe the monitor is the single-shot delay it has always been.
    /// </remarks>
    /// <returns>This instance, for chaining.</returns>
    public BatonTask WithTimeoutBudget(Func<TimeSpan> budget)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(budget);
        timeoutBudget = budget;
        return this;
    }

    /// <summary>
    /// Enables (or disables) stdout/stderr capture. When enabled, <see cref="EventRaised"/> carries
    /// <c>StdoutChunk</c>/<c>StderrChunk</c> events with the child's output, delivered live as bytes
    /// arrive rather than buffered until exit. Must be called before the task is run.
    /// </summary>
    /// <returns>This instance, for chaining.</returns>
    public BatonTask WithCaptureOutput(bool capture = true)
    {
        ThrowIfDisposed();
        captureOutput = capture;
        return this;
    }

    /// <summary>
    /// Sets an environment variable for the child process. Repeatable: calling this again with the
    /// same <paramref name="key"/> overrides the previously set value. Variables set this way are
    /// always visible to the child, regardless of <see cref="WithClearEnv"/>. Must be called before
    /// the task is run.
    /// </summary>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="BatonException"><paramref name="key"/> is empty or contains '=' (<see cref="BatonErrorCode.InvalidArgument"/>).</exception>
    public BatonTask WithEnv(string key, string value)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (key.Length == 0 || key.Contains('='))
        {
            throw new BatonException(BatonErrorCode.InvalidArgument, "Environment variable key must be non-empty and must not contain '='.");
        }

        int existing = envVars.FindIndex(e => e.Key == key);
        if (existing >= 0)
        {
            envVars[existing] = (key, value);
        }
        else
        {
            envVars.Add((key, value));
        }

        return this;
    }

    /// <summary>
    /// Sets whether the child inherits the parent's environment. When <paramref name="clear"/> is
    /// <see langword="true"/>, the child inherits nothing except variables set via
    /// <see cref="WithEnv"/>. Default is <see langword="false"/> (inherit everything). Must be
    /// called before the task is run.
    /// </summary>
    /// <returns>This instance, for chaining.</returns>
    public BatonTask WithClearEnv(bool clear = true)
    {
        ThrowIfDisposed();
        clearEnv = clear;
        return this;
    }

    /// <summary>
    /// Sets the child process's working directory. Must be called before the task is run. If the
    /// path does not exist or is not a directory, this surfaces at run time as a
    /// <see cref="BatonException"/> with <see cref="BatonErrorCode.SpawnFailed"/>.
    /// </summary>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="BatonException"><paramref name="path"/> is empty (<see cref="BatonErrorCode.InvalidArgument"/>).</exception>
    public BatonTask WithCwd(string path)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            throw new BatonException(BatonErrorCode.InvalidArgument, "Working directory path must not be empty.");
        }

        cwd = path;
        return this;
    }

    /// <summary>
    /// Spawns the process and blocks the calling thread until it exits.
    /// </summary>
    /// <exception cref="InvalidOperationException">This instance has already been run.</exception>
    /// <exception cref="BatonTimeoutException">The configured timeout elapsed.</exception>
    /// <exception cref="BatonCancelException">Never thrown from <see cref="Run"/> — only <see cref="RunAsync"/> accepts a token.</exception>
    /// <exception cref="BatonException">The run failed for any other reason.</exception>
    public void Run() => RunCore(CancellationToken.None);

    /// <summary>
    /// Runs the task on a thread-pool thread via <see cref="Task.Run(Action)"/>, wrapping the
    /// inherently-blocking spawn/wait sequence.
    /// </summary>
    /// <param name="cancellationToken">
    /// When cancelled, kills the process tree the same way a timeout would, and the run then
    /// completes by throwing <see cref="BatonCancelException"/> — not <see cref="OperationCanceledException"/>.
    /// A cancellation observed after the process has already exited is a no-op.
    /// </param>
    /// <exception cref="InvalidOperationException">This instance has already been run.</exception>
    /// <exception cref="BatonCancelException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="BatonTimeoutException">The configured timeout elapsed.</exception>
    /// <exception cref="BatonException">The run failed for any other reason.</exception>
    public Task RunAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => RunCore(cancellationToken), CancellationToken.None);

    /// <summary>Marks this instance disposed. No unmanaged resource outlives a single <see cref="Run"/> call, so there is nothing to release beyond that.</summary>
    /// <remarks>Idempotent; safe to call at any time, including while a run is in progress on another thread.</remarks>
    public void Dispose() => Interlocked.Exchange(ref disposedFlag, 1);

    private void RunCore(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref hasRunFlag, 1) != 0)
        {
            throw new InvalidOperationException("BatonTask.Run/RunAsync may only be called once per instance.");
        }

        BatonProcessRunner.Run(program, args, timeout, timeoutBudget, captureOutput, envVars, clearEnv, cwd, RaiseEvent, cancellationToken);
    }

    private void RaiseEvent(BatonEventArgs args) => EventRaised?.Invoke(this, args);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposedFlag) != 0, this);
}
