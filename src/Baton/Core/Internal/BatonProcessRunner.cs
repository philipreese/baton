using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Baton.Core.Internal;

/// <summary>
/// Drives a single spawn-to-exit lifecycle for <see cref="BatonTask"/>: spawn under a Job Object,
/// emit <c>Started</c>, pump captured output live (when enabled), enforce the configured timeout and
/// an observed <see cref="CancellationToken"/> by killing the whole process tree, wait for exit, and
/// emit <c>Exited</c> with the right <see cref="BatonExitReason"/>. A direct port of aer-core's
/// <c>task.rs</c> (<c>run_impl</c>) and <c>os/windows.rs</c> (#1474) — the Windows-only half of the
/// Rust core, which is the only half this repo ever built (spec/baton.md C-10).
/// </summary>
internal static class BatonProcessRunner
{
    private const int DrainBufferSize = 8192;

    /// <summary>
    /// How often the timeout monitor re-reads an extensible budget (<see cref="BatonTask.WithTimeoutBudget"/>).
    /// Only reached when a probe was supplied; with none, the monitor waits the configured timeout out
    /// in one delay exactly as it did before #2019.
    /// </summary>
    private static readonly TimeSpan BudgetPollInterval = TimeSpan.FromSeconds(15);

    public static void Run(
        string program,
        string[] args,
        TimeSpan? timeout,
        Func<TimeSpan>? timeoutBudget,
        bool captureOutput,
        IReadOnlyList<(string Key, string Value)> envVars,
        bool clearEnv,
        string? cwd,
        Action<BatonEventArgs> raiseEvent,
        CancellationToken cancellationToken)
    {
        SafeJobObjectHandle job;
        try
        {
            job = SafeJobObjectHandle.Create();
        }
        catch (Win32Exception ex)
        {
            throw new BatonException(BatonErrorCode.SpawnFailed, $"Failed to create the containing job object: {ex.Message}", ex);
        }

        Process? process = null;
        CancellationTokenRegistration registration = default;
        CancellationTokenSource? timeoutMonitorCts = null;
        Task? timeoutMonitorTask = null;

        // Armed as soon as the child is confirmed alive and inside the job; disarmed only once the
        // wait below has returned a real exit code. Mirrors aer-core's KillOnDropGuard: anything that
        // unwinds through this method while armed (an EventRaised subscriber throwing out of the
        // Started event, in particular) still kills the tree in `finally` rather than orphaning it.
        bool armed = false;

        try
        {
            ProcessStartInfo startInfo = BuildStartInfo(program, args, envVars, clearEnv, cwd);
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Win32Exception ex)
            {
                throw new BatonException(BatonErrorCode.SpawnFailed, $"Failed to start '{program}': {ex.Message}", ex);
            }

            if (process is null)
            {
                throw new BatonException(BatonErrorCode.SpawnFailed, $"Failed to start '{program}': the OS returned no process.");
            }

            if (!job.TryAssign(process.SafeHandle))
            {
                int error = Marshal.GetLastWin32Error();

                // ERROR_ACCESS_DENIED (5) is what AssignProcessToJobObject returns for a process
                // that has already terminated -- a fast child (a sub-10ms `cmd /c echo`) can exit
                // inside the Start->assign window, and that is a completed run whose exit code is
                // sitting there to collect, not a spawn failure (#1484; bit CI intermittently as
                // "Spawn refused ... Win32 error 5" on perfectly healthy steps). Fall through to the
                // normal wait path -- but kill the PID tree first (below), never bare.
                if (error != 5 || !process.HasExited)
                {
                    // The no-orphans guarantee applies to spawn failures too, not
                    // just to teardown after a successful spawn: the child is alive but never made it
                    // into the job, so the job's own kill-on-close would never reach it. Kill it directly
                    // before reporting the failure.
                    KillAndWait(process);
                    throw new BatonException(BatonErrorCode.SpawnFailed, $"Failed to assign '{program}' to its job object (Win32 error {error}).");
                }

                // The exited child never made it into the job, so neither did anything it spawned:
                // an escaped grandchild holding the inherited pipe write handles would block the
                // drain threads FOREVER on this path -- Terminate at wait-return no-ops on the empty
                // job, and the timeout monitor's IsTreeAlive reads that same empty job as dead, so
                // no bound applies (#1485 review). .NET's PID-walk tree kill is the one mechanism
                // that still reaches such a descendant; best-effort, swallowed like KillAndWait's --
                // a fast no-op in the common no-grandchild case, and it cannot disturb the direct
                // child's exit code, which is read from the still-held process handle.
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort: the tree may already be fully gone.
                }
            }

            // Armed as soon as the child is inside the job -- before StandardInput.Close() below,
            // which practically never throws but would otherwise open a window (between a
            // successful spawn+assign and the first event) where an exception left the tree
            // untracked by this method's own kill-on-unwind guard.
            armed = true;

            // Stdin redirected then immediately closed: a child that reads from stdin sees EOF exactly
            // as it would reading a native NUL device, and is never left connected to this process's
            // own stdin. Not a full equivalence -- a child that instead writes to stdin gets
            // ERROR_BROKEN_PIPE here, where a real NUL device would have silently accepted the write.
            process.StandardInput.Close();

            raiseEvent(new BatonEventArgs { Kind = BatonTaskEventKind.Started, Pid = (uint)process.Id });

            // TOCTOU note (mirrors aer-core's CancelHandle.cancel() / timeout monitor doc comments):
            // probe-then-kill narrows but cannot fully close the race between "tree observed alive"
            // and the kill actually landing. A process that exits naturally in that gap is harmlessly
            // reported as killed anyway, because terminating an already-dead job is a no-op.
            int[] cancelKillFired = [0];
            int[] timedOutKillFired = [0];

            if (cancellationToken.CanBeCanceled)
            {
                registration = cancellationToken.Register(() => KillIfAlive(job, cancelKillFired));
            }

            if (timeout is { } configuredTimeout)
            {
                timeoutMonitorCts = new CancellationTokenSource();
                CancellationToken monitorToken = timeoutMonitorCts.Token;
                timeoutMonitorTask = Task.Run(async () =>
                {
                    // Re-read, not delay-once (#2019): a budget the probe grows at minute 39 of a
                    // 40-minute box has to move the kill, or crediting the wait would change nothing
                    // for the lane it exists for. With no probe the loop is the single delay it
                    // replaced -- one full-length wait, then the kill.
                    Stopwatch elapsed = Stopwatch.StartNew();

                    // High-water mark, hoisted above the loop on purpose (#2058 review): a credit,
                    // once granted, is never retracted. Re-deriving the budget per poll instead
                    // would open a window that only exists once `elapsed` passes the configured box
                    // -- exactly while a lane is living on credit -- where one poll that fails to
                    // re-earn the extension makes `remaining` negative and kills the tree on the
                    // spot, mid-credited-tail. That is #2019's own harm, reproduced by the fix for
                    // it, and it presents as "the box is still too small" rather than as a failed
                    // measurement.
                    TimeSpan granted = configuredTimeout;
                    bool probeFailureWarned = false;
                    try
                    {
                        while (true)
                        {
                            if (timeoutBudget is not null)
                            {
                                try
                                {
                                    TimeSpan probed = timeoutBudget();
                                    if (probed > granted)
                                    {
                                        granted = probed;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    // Fails closed, and here that means KEEPING WHAT WAS ALREADY
                                    // GRANTED -- the same thing as falling back to the configured
                                    // box until the first credit lands, and the opposite of it
                                    // afterwards. The probe is a MEASUREMENT, so one that cannot be
                                    // taken grants nothing NEW; it cannot take back an extension the
                                    // lane is already running on. Swallowed rather than rethrown
                                    // because throwing here would leave the tree with no deadline at
                                    // all -- the opposite of what a broken probe should cost. Warned
                                    // once rather than per poll: the cause repeats every
                                    // BudgetPollInterval, so the first write is the findable one and
                                    // the rest are noise.
                                    if (!probeFailureWarned)
                                    {
                                        probeFailureWarned = true;
                                        Debug.WriteLine($"BatonTask timeout budget probe failed; keeping the budget already granted: {ex}");
                                    }
                                }
                            }

                            TimeSpan remaining = granted - elapsed.Elapsed;
                            if (remaining <= TimeSpan.Zero)
                            {
                                KillIfAlive(job, timedOutKillFired);
                                return;
                            }

                            TimeSpan wait = timeoutBudget is not null && remaining > BudgetPollInterval
                                ? BudgetPollInterval
                                : remaining;
                            await Task.Delay(wait, monitorToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // The run finished before the deadline elapsed; nothing to do.
                    }
                }, CancellationToken.None);
            }

            int exitCode = captureOutput
                ? RunWithLiveCapture(process, job, raiseEvent)
                : RunDiscardingOutput(process, job);

            // The process is confirmed dead now that an exit code was obtained. Disarm so an
            // exception from the Exited callback below doesn't fire a redundant kill against an
            // already-reaped tree.
            armed = false;

            // Stopped here (and nulled out) rather than left solely to `finally`: the flags read
            // just below must observe the monitor task fully settled, not mid-flight between its own
            // tree_alive probe and setting timedOutKillFired. Nulling prevents `finally`'s own
            // cleanup call from calling Cancel() on an already-disposed CancellationTokenSource.
            StopTimeoutMonitor(timeoutMonitorCts, timeoutMonitorTask);
            timeoutMonitorCts = null;
            timeoutMonitorTask = null;
            registration.Dispose();

            bool timedOut = Volatile.Read(ref timedOutKillFired[0]) != 0;
            bool cancelled = !timedOut && Volatile.Read(ref cancelKillFired[0]) != 0;

            BatonExitReason reason = timedOut ? BatonExitReason.TimedOut
                : cancelled ? BatonExitReason.CancelRequested
                : BatonExitReason.Natural;
            int code = timedOut || cancelled ? -1 : exitCode;

            raiseEvent(new BatonEventArgs { Kind = BatonTaskEventKind.Exited, ExitCode = code, ExitReason = reason });

            if (timedOut)
            {
                throw new BatonTimeoutException();
            }

            if (cancelled)
            {
                throw new BatonCancelException();
            }
        }
        finally
        {
            StopTimeoutMonitor(timeoutMonitorCts, timeoutMonitorTask);
            registration.Dispose();

            if (armed)
            {
                job.Terminate();
            }

            job.Dispose();
            process?.Dispose();
        }
    }

    private static void StopTimeoutMonitor(CancellationTokenSource? cts, Task? monitorTask)
    {
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            monitorTask?.GetAwaiter().GetResult();
        }
        catch
        {
            // The monitor task only ever completes normally or via its own internal
            // OperationCanceledException catch; nothing here should be able to throw, but this is
            // teardown code and must not itself become the reason a run fails to report.
        }

        cts.Dispose();
    }

    private static void KillIfAlive(SafeJobObjectHandle job, int[] flag)
    {
        if (job.IsTreeAlive() && Interlocked.CompareExchange(ref flag[0], 1, 0) == 0)
        {
            job.Terminate();
        }
    }

    private static void KillAndWait(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort: the process may already be exiting on its own.
        }

        try
        {
            process.WaitForExit();
        }
        catch
        {
            // Nothing left to report this to.
        }
    }

    private static ProcessStartInfo BuildStartInfo(
        string program, string[] args, IReadOnlyList<(string Key, string Value)> envVars, bool clearEnv, string? cwd)
    {
        ProcessStartInfo startInfo = ChildProcessStartInfo.Create(program, startInfo =>
        {
            startInfo.RedirectStandardInput = true;
            // Pipes are required even when output is not surfaced to callers: without draining, a
            // child writing beyond the OS pipe buffer deadlocks WaitForExit(). Never inherit here.
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            // Both drain threads below read raw bytes off BaseStream, never through the
            // StreamReader these encodings would configure -- but #466/#1016's source-scan gate
            // (RedirectedProcessEncodingTests) requires every redirected stream to pin one anyway,
            // so a future caller who does reach for .StandardOutput/.StandardError directly is not
            // silently handed the OEM code page.
            startInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
            startInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
        });

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // ClearEnvironmentVariables must run before applying WithEnv entries, otherwise it would wipe
        // out the very variables just set.
        if (clearEnv)
        {
            startInfo.EnvironmentVariables.Clear();
        }

        foreach ((string key, string value) in envVars)
        {
            startInfo.EnvironmentVariables[key] = value;
        }

        if (cwd is not null)
        {
            startInfo.WorkingDirectory = cwd;
        }

        return startInfo;
    }

    /// <summary>
    /// Discard-path wait: drain threads run purely to prevent pipe-buffer deadlock, nothing is
    /// delivered to the caller. Equivalent to aer-core's <c>os/windows.rs::wait</c> with <c>None</c>
    /// sinks.
    /// </summary>
    private static int RunDiscardingOutput(Process process, SafeJobObjectHandle job)
    {
        Thread stdoutDrain = StartDrainThread(process.StandardOutput.BaseStream, sink: null, isStdout: true, onDone: null);
        Thread stderrDrain = StartDrainThread(process.StandardError.BaseStream, sink: null, isStdout: false, onDone: null);

        // Waits for the root process only -- not for grandchildren to close the pipe.
        process.WaitForExit();

        // Explicit and unconditional, independent of the timeout monitor / cancellation registration
        // possibly having already called Terminate(): a straggling grandchild that inherited the
        // pipe handles keeps them open after the root exits, which would otherwise hang the drain
        // threads forever. Terminating an already-empty job is a harmless no-op. See aer-core's
        // os/windows.rs "Why TerminateJobObject is required" note.
        job.Terminate();

        stdoutDrain.Join();
        stderrDrain.Join();

        return process.ExitCode;
    }

    /// <summary>
    /// Capture-path wait: the calling thread pumps chunks live as they arrive instead of blocking
    /// until exit, so a slow process's output is delivered while it is still running (aer-core
    /// regression #72). The actual OS wait runs on its own thread, mirroring aer-core's
    /// <c>run_impl</c> capture branch.
    /// </summary>
    private static int RunWithLiveCapture(Process process, SafeJobObjectHandle job, Action<BatonEventArgs> raiseEvent)
    {
        using BlockingCollection<ChunkMessage> chunks = new();
        int pendingDrains = 2;

        void OnDrainDone()
        {
            if (Interlocked.Decrement(ref pendingDrains) == 0)
            {
                chunks.CompleteAdding();
            }
        }

        Thread stdoutDrain = StartDrainThread(process.StandardOutput.BaseStream, chunks, isStdout: true, OnDrainDone);
        Thread stderrDrain = StartDrainThread(process.StandardError.BaseStream, chunks, isStdout: false, OnDrainDone);

        Exception? waitException = null;
        Thread waitThread = new(() =>
        {
            try
            {
                process.WaitForExit();
            }
            catch (Exception ex)
            {
                waitException = ex;
            }
            finally
            {
                // See RunDiscardingOutput's comment: unconditional, and what actually closes a
                // straggling grandchild's inherited pipe handles so the drain threads below unblock.
                // In `finally` rather than after WaitForExit(): if WaitForExit() itself throws, the
                // drain threads and the foreach above must still be released rather than left blocked
                // forever waiting on a tree nothing else is going to kill (#1474 second-reader S1).
                job.Terminate();
            }
        })
        { IsBackground = true };
        waitThread.Start();

        // Live delivery: emit each chunk as it arrives. Ends once both drain threads have finished
        // (their pipes hit EOF, ultimately because the tree died), which is what completes the
        // collection below.
        //
        // The foreach body runs caller code (raiseEvent -> the subscriber's EventRaised handler): if
        // it throws, the exception must not reach the `using chunks` disposal above while a drain
        // thread could still be mid-`sink.Add` on it -- that raced an ObjectDisposedException onto a
        // background thread, unhandled, which is fatal to the whole process (#1474 second-reader B1).
        // The `finally` below both guarantees the joins still happen and, by terminating the job
        // first, guarantees they happen promptly: without killing the tree here, a throw while the
        // child is still producing output would otherwise block this rethrow on drain threads that
        // only reach EOF once the process eventually exits on its own.
        try
        {
            foreach (ChunkMessage chunk in chunks.GetConsumingEnumerable())
            {
                raiseEvent(new BatonEventArgs
                {
                    Kind = chunk.IsStdout ? BatonTaskEventKind.StdoutChunk : BatonTaskEventKind.StderrChunk,
                    Seq = chunk.Seq,
                    Data = chunk.Bytes,
                });
            }
        }
        finally
        {
            job.Terminate();
            stdoutDrain.Join();
            stderrDrain.Join();
            waitThread.Join();
        }

        if (waitException is not null)
        {
            throw new BatonException(BatonErrorCode.WaitFailed, "Waiting on the child process failed.", waitException);
        }

        return process.ExitCode;
    }

    /// <summary>
    /// Reads <paramref name="stream"/> in <see cref="DrainBufferSize"/>-byte chunks until EOF or
    /// error. When <paramref name="sink"/> is non-null, each read is wrapped with a per-thread,
    /// monotonically increasing <c>seq</c> starting at 0 and added; when null, bytes are read and
    /// discarded -- still required so the child cannot deadlock on a full pipe buffer. Shared shape
    /// for both streams so draining behavior (buffer size, EOF/error handling, seq numbering) stays
    /// identical in one place, mirroring aer-core's <c>spawn_drain_thread</c>.
    /// </summary>
    private static Thread StartDrainThread(Stream stream, BlockingCollection<ChunkMessage>? sink, bool isStdout, Action? onDone)
    {
        Thread thread = new(() =>
        {
            try
            {
                ulong seq = 0;
                byte[] buffer = new byte[DrainBufferSize];
                while (true)
                {
                    int read;
                    try
                    {
                        read = stream.Read(buffer, 0, buffer.Length);
                    }
                    catch
                    {
                        break;
                    }

                    if (read <= 0)
                    {
                        break;
                    }

                    if (sink is not null)
                    {
                        byte[] copy = new byte[read];
                        Buffer.BlockCopy(buffer, 0, copy, 0, read);
                        sink.Add(new ChunkMessage(isStdout, seq, copy));
                        seq++;
                    }
                }
            }
            finally
            {
                onDone?.Invoke();
            }
        })
        { IsBackground = true };
        thread.Start();
        return thread;
    }

    private readonly record struct ChunkMessage(bool IsStdout, ulong Seq, byte[] Bytes);
}
