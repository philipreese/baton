using System.Diagnostics;

namespace Baton.Cli.Tests.TestSupport;

/// <summary>
/// Shared by every <c>Baton.Cli.Tests</c> fixture that needs a genuinely-dead process identity for
/// <see cref="Baton.Outcomes.EngineLivenessProbe"/>: capture a real process's pid and start time
/// while it is provably alive, then kill it, so the probe's OS-level checks (start-time match,
/// <c>HasExited</c>) see an OS-confirmed-dead PID rather than a fabricated one that might
/// coincidentally collide with something else running on the host. A long-sleeping child killed
/// after capture is what makes the capture deterministic: an immediately-exiting child races the
/// start-time read and intermittently kills the test with a <c>Win32Exception</c> before any
/// assertion (#843, measured on PR #841's then-extant Linux CI leg).
/// </summary>
public static class ProcessIdentityFixture
{
    public static (int Pid, DateTimeOffset StartTime) DeadProcessIdentity()
    {
        var psi = new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true };

        using var process = global::Baton.Core.ProcessLaunch.Start(psi)!;
        try
        {
            return (process.Id, new DateTimeOffset(process.StartTime).ToUniversalTime());
        }
        finally
        {
            process.Kill();
            if (!process.WaitForExit(TimeSpan.FromSeconds(10))) // wait-ok: bounding a post-Kill() exit, expected in milliseconds (#1804)
            {
                throw new TimeoutException(
                    $"killed process {process.Id} did not exit within 10s (#1804: no wait may be unbounded)");
            }
        }
    }
}
