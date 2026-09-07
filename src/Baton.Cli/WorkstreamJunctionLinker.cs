using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// Creates the by-workstream junction under <see cref="BatonPaths.ByWorkstream"/> for a dispatched
/// room. What this buys and why it exists is written up under spec/baton.md's dispatch section
/// (§2). <c>DispatchCommand</c>'s and <c>RedispatchCommand</c>'s <c>bindings.json</c> stay the
/// single source of the room ↔ workstream mapping; this is a read-time convenience over it.
/// </summary>
/// <remarks>
/// Junctions (<c>mklink /J</c>), not symlinks: junction creation needs no elevation or Developer Mode
/// on Windows — the only supported platform (`ci.yml`, #1405) — while a directory symlink does. There
/// is no managed junction API in .NET, and adding a raw reparse-point P/Invoke would be a new Win32
/// surface Architecture Rule 3 reserves for the Job Object containment it already owns — so this
/// shells out to <c>cmd.exe /c mklink /J</c>, the same pattern <c>WorktreeProvisioner</c> already uses
/// for git.
/// </remarks>
public static class WorkstreamJunctionLinker
{
    /// <summary>
    /// Creates the junction for <paramref name="roomDirectoryPath"/> under its workstream's link
    /// directory. A no-op when <paramref name="workstream"/> is null — the default, unlabeled case
    /// <c>--label</c> itself has, and every room minted before #1619. <b>Never throws</b>: a failed
    /// junction (a machine policy that refuses <c>mklink</c>, an occupied link name) degrades to a
    /// stderr warning rather than failing a dispatch whose room already exists on disk and is already
    /// fully functional without the shortcut — this is a convenience link, not the room's identity.
    /// </summary>
    public static void CreateIfRequested(string? workstream, string roomDirectoryPath)
    {
        if (workstream is null)
        {
            return;
        }

        var fullRoomPath = Path.GetFullPath(roomDirectoryPath);
        var linkPath = ResolveLinkPath(workstream, fullRoomPath);
        var linkDirectory = Path.GetDirectoryName(linkPath)!;

        try
        {
            // mklink /J requires the link's parent directory to already exist.
            Directory.CreateDirectory(linkDirectory);

            if (Directory.Exists(linkPath))
            {
                var existingTarget = new DirectoryInfo(linkPath).LinkTarget;
                if (existingTarget is not null
                    && BatonPaths.RecordKeyComparer.Equals(BatonPaths.RecordKey(existingTarget), BatonPaths.RecordKey(fullRoomPath)))
                {
                    // A retry against a room this exact link already points at -- nothing to do.
                    return;
                }

                // Never silent: an occupied name that does NOT already point at this room is exactly
                // the failure mode a caller needs to know about, not one to swallow.
                Console.Error.WriteLine(
                    $"Warning: the by-workstream link at '{linkPath}' already exists and points at "
                    + $"'{existingTarget ?? "a target that could not be read"}', not at '{fullRoomPath}'. Leaving "
                    + $"it as is — the room itself is unaffected — it stays reachable at '{roomDirectoryPath}'.");
                return;
            }

            var startInfo = ChildProcessStartInfo.Create("cmd.exe", startInfo =>
            {
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                // #466: the null-encoding default decodes the pipe with the console code page (OEM
                // cp437 under a default console), mangling non-ASCII output -- pin UTF-8 explicitly.
                startInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
                startInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
            });
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(linkPath);
            startInfo.ArgumentList.Add(fullRoomPath);

            using var process = Process.Start(startInfo)
                ?? throw new Win32Exception("could not start 'cmd.exe'");
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine(
                    $"Warning: could not create the by-workstream link at '{linkPath}' (mklink /J exit "
                    + $"{process.ExitCode}): {stderrTask.Result.Trim()}. The room itself is unaffected — "
                    + $"it stays reachable at '{roomDirectoryPath}'.");
            }
        }
        // Narrow on purpose: these are what a blocked mklink or a denied CreateDirectory can actually
        // throw. MED-1's discriminating test proves the mklink-exit-code branch above reachable without
        // needing this catch at all (an occupied linkPath fails mklink itself, not this process); nothing
        // has yet needed to widen this list, and anything outside it propagates and fails the dispatch
        // deliberately, rather than being swallowed on the strength of "probably another junction failure".
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Console.Error.WriteLine(
                $"Warning: could not create the by-workstream link at '{linkPath}': {ex.Message}. The room "
                + $"itself is unaffected — it stays reachable at '{roomDirectoryPath}'.");
        }
    }

    /// <summary>
    /// The junction's full path for <paramref name="roomDirectoryPath"/> under <paramref name="workstream"/>'s
    /// link directory. Why the leaf name alone is not used as the link name is spec/baton.md §2's
    /// by-workstream junction paragraph — not restated here. Internal (not private) so a test can
    /// predict where a junction lands without re-implementing the hash — see
    /// <c>Baton.Cli.csproj</c>'s <c>InternalsVisibleTo</c>.
    /// </summary>
    internal static string ResolveLinkPath(string workstream, string roomDirectoryPath)
    {
        var fullRoomPath = Path.GetFullPath(roomDirectoryPath);
        var roomName = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullRoomPath));
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(BatonPaths.RecordKey(fullRoomPath).ToLowerInvariant())))[..8].ToLowerInvariant();
        return Path.Combine(BatonPaths.ByWorkstream, workstream, $"{roomName}-{hash}");
    }
}
