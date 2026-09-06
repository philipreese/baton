namespace Baton.Status;

/// <summary>
/// The named-<see cref="Mutex"/> critical-section primitive <c>RoomRegistryStore</c> originated
/// (spec/baton.md §8) — extracted here so <c>QuotaLedgerStore</c> (spec/baton.md §7, issue #1570) can
/// share it rather than copying it; a second caller is what turns this from "the registry's private
/// helper" into a real seam. Every process touching the same file — reader or writer — serializes
/// against every other one, keyed on <paramref name="filePath"/> and <paramref name="lockNamePrefix"/>
/// (distinct prefixes for distinct files mean two stores can never contend on each other's locks even
/// if two file paths happened to collide, which <see cref="BatonPaths.RecordKey"/> already makes
/// vanishingly unlikely on its own).
/// </summary>
/// <remarks>
/// <b>Acquire, the caller's action, and release all happen synchronously on one thread.</b>
/// <see cref="Mutex"/> ownership is thread-affine on Windows: the OS thread that calls
/// <see cref="Mutex.WaitOne()"/> is the only one allowed to call <see cref="Mutex.ReleaseMutex"/>. An
/// <c>await</c> between acquiring and releasing can resume on a different thread-pool thread with no
/// synchronization context to pin it, which makes <see cref="Mutex.ReleaseMutex"/> throw. Callers must
/// wrap this in one <c>Task.Run</c> from the outside (moving the whole synchronous call to a worker
/// thread) rather than making <paramref name="action"/> itself <c>async</c>.
/// <para>
/// The lock name is built from a SHA-256 digest of <paramref name="filePath"/> (normalized through
/// <see cref="BatonPaths.RecordKey"/> and upper-invariant, so two spellings of the same file — forward
/// vs. backward slashes, a different <c>BATON_HOME</c> casing — hash to the one mutex name) rather than
/// the raw path, because a raw path is neither a valid nor a safely short Windows kernel-object name.
/// <b>Changing this digest formula, the <c>Global\</c> prefix, or an existing caller's
/// <paramref name="lockNamePrefix"/> renames the lock</b> — an older <c>baton</c> build and a newer one
/// (side-by-side per-commit installs, #1668) would then take out two different mutexes against the same
/// file, which is exactly the loss this primitive exists to prevent.
/// </para>
/// </remarks>
/// <summary>
/// How <see cref="MutexGuardedFileLock"/> waits for a lock another process already holds (#1942): one
/// kernel wait of <paramref name="AttemptTimeout"/>, and — if that elapses — up to
/// <paramref name="MaxAttempts"/> of them in total, separated by a jittered backoff. A caller that
/// wants the historical single-shot behaviour uses <see cref="Single"/>, which is exactly one attempt
/// and therefore never sleeps.
/// </summary>
/// <remarks>
/// <b>Why retry a wait that already timed out, rather than just passing a bigger
/// <paramref name="AttemptTimeout"/>.</b> The two are not the same under the failure this exists for.
/// A single long wait sits in one kernel queue for its whole budget; the jittered gap between attempts
/// is what desynchronizes a group of processes that arrived together (six live rooms all polling the
/// same registry file) so they stop re-colliding on every release. The per-attempt wait still stays
/// long relative to any critical section this guards, because each timeout costs the caller its place
/// in the wait queue — short slices would trade one failure mode for a worse one.
/// </remarks>
/// <param name="AttemptTimeout">How long one <see cref="Mutex.WaitOne(TimeSpan)"/> waits.</param>
/// <param name="MaxAttempts">How many such waits are made in total, including the first.</param>
/// <param name="BackoffBase">
/// The unit the gap between attempts is built from: attempt <c>n</c> sleeps <c>n</c> times this plus a
/// random extra of up to one more, so two processes that timed out together do not retry together.
/// </param>
public sealed record LockWaitPolicy(TimeSpan AttemptTimeout, int MaxAttempts, TimeSpan BackoffBase)
{
    /// <summary>
    /// One attempt, no retry and no backoff — byte-for-byte the behaviour every caller had before
    /// #1942, and what the <c>TimeSpan</c>-taking <see cref="MutexGuardedFileLock.RunUnderLock{T}(string,string,TimeSpan,Func{T})"/>
    /// overloads still resolve to.
    /// </summary>
    public static LockWaitPolicy Single(TimeSpan lockTimeout) =>
        new(lockTimeout, MaxAttempts: 1, BackoffBase: TimeSpan.Zero);

    /// <summary>
    /// The worst-case wait before <see cref="MutexGuardedFileLock.RunUnderLock{T}(string,string,LockWaitPolicy,Func{T})"/>
    /// gives up — the kernel waits only, excluding the backoff sleeps, since those are randomized. What
    /// the timeout <see cref="IOException"/> reports, so an operator reading it is told the budget that
    /// actually elapsed rather than a single attempt's slice.
    /// </summary>
    public TimeSpan MaxTotalWait => AttemptTimeout * MaxAttempts;

    /// <summary>
    /// The gap after a failed attempt <paramref name="attemptNumber"/> (1-based). Grows linearly and
    /// carries up to one <see cref="BackoffBase"/> of jitter; zero for a policy with no backoff.
    /// </summary>
    internal TimeSpan BackoffAfterAttempt(int attemptNumber)
    {
        var baseMilliseconds = (int)BackoffBase.TotalMilliseconds;
        return baseMilliseconds <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds((baseMilliseconds * attemptNumber) + Random.Shared.Next(baseMilliseconds));
    }
}

public static class MutexGuardedFileLock
{
    /// <summary>
    /// The exact kernel-object name <see cref="RunUnderLock{T}"/> takes out for
    /// <paramref name="filePath"/>/<paramref name="lockNamePrefix"/> — exposed so a test can pin the
    /// literal format against an independently-computed expectation, not for any production caller to
    /// build a <see cref="Mutex"/> of its own.
    /// </summary>
    internal static string BuildMutexName(string filePath, string lockNamePrefix)
    {
        var digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(BatonPaths.RecordKey(filePath).ToUpperInvariant())));
        return $"Global\\{lockNamePrefix}-{digest}";
    }

    /// <summary>
    /// Single-attempt overload: runs <paramref name="action"/> holding the lock, throwing
    /// <see cref="IOException"/> if <paramref name="lockTimeout"/> elapses once. Equivalent to passing
    /// <see cref="LockWaitPolicy.Single"/> to the policy-taking overload below, which carries the rest
    /// of the contract.
    /// </summary>
    public static T RunUnderLock<T>(string filePath, string lockNamePrefix, TimeSpan lockTimeout, Func<T> action) =>
        RunUnderLock(filePath, lockNamePrefix, LockWaitPolicy.Single(lockTimeout), action);

    /// <summary>
    /// Runs <paramref name="action"/> holding a named <see cref="Mutex"/> keyed on
    /// <paramref name="filePath"/> and <paramref name="lockNamePrefix"/>. Waits for the lock as
    /// <paramref name="waitPolicy"/> describes — one kernel wait, or several separated by a jittered
    /// backoff — and throws <see cref="IOException"/> only once the whole policy is exhausted.
    /// <see cref="WaitHandleCannotBeOpenedException"/> (a non-mutex kernel object already holding the
    /// name) propagates; both are the caller's fail-open contract to honour, not this primitive's to
    /// swallow.
    /// </summary>
    public static T RunUnderLock<T>(string filePath, string lockNamePrefix, LockWaitPolicy waitPolicy, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(waitPolicy);
        ArgumentOutOfRangeException.ThrowIfLessThan(waitPolicy.MaxAttempts, 1);

        using var mutex = new Mutex(initiallyOwned: false, name: BuildMutexName(filePath, lockNamePrefix));

        var owned = false;
        for (var attempt = 1; attempt <= waitPolicy.MaxAttempts && !owned; attempt++)
        {
            try
            {
                owned = mutex.WaitOne(waitPolicy.AttemptTimeout);
            }
            catch (AbandonedMutexException)
            {
                // A prior holder crashed mid-access. Per Mutex's own contract, ownership still transfers
                // to us when this is thrown -- whatever partial state it left behind is each caller's own
                // tolerant, skip-malformed-lines read path to handle, not something to react to here.
                owned = true;
            }

            if (!owned && attempt < waitPolicy.MaxAttempts)
            {
                Thread.Sleep(waitPolicy.BackoffAfterAttempt(attempt));
            }
        }

        if (!owned)
        {
            throw new IOException(
                $"Timed out after {waitPolicy.MaxTotalWait} waiting for the '{lockNamePrefix}' lock on '{filePath}'.");
        }

        try
        {
            return action();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>Action-returning overload of <see cref="RunUnderLock{T}(string,string,TimeSpan,Func{T})"/>.</summary>
    public static void RunUnderLock(string filePath, string lockNamePrefix, TimeSpan lockTimeout, Action action) =>
        RunUnderLock(filePath, lockNamePrefix, LockWaitPolicy.Single(lockTimeout), action);

    /// <summary>Action-returning overload of <see cref="RunUnderLock{T}(string,string,LockWaitPolicy,Func{T})"/>.</summary>
    public static void RunUnderLock(string filePath, string lockNamePrefix, LockWaitPolicy waitPolicy, Action action) =>
        RunUnderLock<object?>(filePath, lockNamePrefix, waitPolicy, () =>
        {
            action();
            return null;
        });
}
