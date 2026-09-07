# Registers the `baton-daemon` scheduled task that keeps `baton daemon` running persistently
# (#1557 side item). `RoomRetentionSweep` and the fleet-wide concurrency-cap apply (spec/baton.md
# §7) are both hosted services inside `baton daemon` -- they only do anything while some process
# is actually running that verb, and nothing before this script registered one. This is the
# `baton-daemon` sibling of the `fleet-glass-pusher` task `tools/fleet-glass/deploy.ps1` (step 5)
# registers -- same convention (idempotent `Register-ScheduledTask -Force`, restart-on-failure,
# `IgnoreNew` against overlap), different action.
#
# One-time, run manually by the operator (or by the deploy conductor after a PR that touches this
# script merges) -- not invoked by CI or by any lane. Re-running is safe: `-Force` overwrites the
# existing task definition in place rather than erroring or duplicating it.
$ErrorActionPreference = "Stop"

$taskName = "baton-daemon"
$batonHome = if ($env:BATON_HOME) { $env:BATON_HOME } else { Join-Path $HOME ".baton" }

# The action runs `baton daemon` through the launcher on PATH (a bare `baton` in PowerShell resolves to
# `~/.dotnet/tools/baton.ps1`, installed by `tools/tool-refresh/refresh.py`'s `install_launcher`) rather than a fixed exe path,
# so every restart re-resolves `~/.baton/tools/current` and picks up whatever tool-refresh most
# recently flipped the pointer to. `baton daemon` itself only logs via the default console
# provider (`DaemonHost.cs` builds a plain `Host.CreateApplicationBuilder`, no file sink) -- run
# through `powershell.exe -WindowStyle Hidden` so no console window appears, and `*>>` all output
# streams to `daemon.log` under the working directory so the daemon's own output survives a
# session with nobody watching it.
#
# `; exit $LASTEXITCODE` (#1981) is load-bearing, not tidiness: measured on this machine (PowerShell
# 5.1, 2026-09-06), `powershell.exe -Command "& { <thing that exits 70> *>> 'x.log' }"` itself exits
# 0 -- the script block's redirect swallows the code -- while the same command with the trailing
# `exit $LASTEXITCODE` exits 70. Task Scheduler's restart-on-failure below keys on the task's exit
# code, so without this the daemon's own watchdog (DaemonWatchdog, which exits 70 when no service has
# completed a tick in five intervals) would kill a hung daemon and leave it dead: strictly worse than
# the hang it is curing. An existing registration keeps the old action until this script is re-run.
#
# #2036: the exit is now RECORDED before it is returned. On 2026-09-07 the daemon's last log line was
# at 04:57:06Z and the next thing in `daemon.log` was the operator's hand-start 8.7 hours later --
# `daemon.log` said nothing at all about the process ending, so "when did it exit, and with what"
# had to be read off the scheduled task's Last Run Result, which keeps only the LATEST run. The
# `[timestamp] baton daemon exited <code>` line below is the one artifact that outlives the process
# and stays put across the restarts that follow it; `-Encoding unicode` because `*>>` writes
# `daemon.log` as UTF-16 under `powershell.exe` (Windows PowerShell 5.1) and a UTF-8 append into it
# would render as mojibake. The daemon's own last-breath line (DaemonHost's ProcessExit and
# unhandled-exception handlers) is the in-process half of the same fix; this half also covers the
# cases the process never gets to report -- a launcher that could not resolve, or a kill.
#
# `$null -eq $c` is the same class of hole as the swallowed code above: a `baton` that never launched
# at all (launcher missing, PATH broken) leaves `$LASTEXITCODE` unset, and a bare `exit $null` exits
# ZERO -- reporting success for a daemon that never ran, which is the one reading Task Scheduler
# must never be handed. 1 rather than 70: 70 is the watchdog's own code (DaemonWatchdog.HungExitCode)
# and must keep meaning only that.
$action = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument '-NoProfile -WindowStyle Hidden -Command "& { baton daemon *>> ''daemon.log'' }; $c = $LASTEXITCODE; if ($null -eq $c) { $c = 1 }; (''['' + [DateTime]::UtcNow.ToString(''yyyy-MM-ddTHH:mm:ss.fffZ'') + ''] baton daemon exited '' + $c) | Out-File -FilePath ''daemon.log'' -Append -Encoding unicode; exit $c"' `
    -WorkingDirectory $batonHome

# This script registers unelevated, as the operator (#1770): a boot (`-AtStartup`) trigger runs
# before any logon and is denied to a standard user, and an unscoped `-AtLogOn` trigger is an
# any-user trigger, also denied. The daemon needs the interactive user's PATH and `~/.baton`
# regardless, so a logon trigger scoped to that same user is both the only trigger a standard user
# can register here and the only one that makes sense for what the daemon needs to run.
$triggerLogon = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"

# Same shape as fleet-glass-pusher's settings (deploy.ps1 step 5): IgnoreNew means a due trigger
# is skipped outright while a launched instance is still alive, so a healthy daemon never sees a
# second launch; RestartCount/RestartInterval is the self-heal against a daemon that exited (crash,
# an operator's `taskkill`, or -- since #1981 -- its own watchdog) without a fresh trigger due yet.
# Three restarts, five minutes apart: a daemon that hangs again immediately after each restart is
# down for good after ~15 minutes rather than looping forever, and that is the intended trade -- a
# repeat hang is a bug to look at, not a condition to paper over.
$taskSettings = New-ScheduledTaskSettingsSet `
    -MultipleInstances IgnoreNew `
    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 5) -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -Hidden

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $triggerLogon `
    -Settings $taskSettings -Force | Out-Null

# #2036: turn the Task Scheduler operational log on, so the NEXT time the restart policy above does
# or does not fire there is a record of it. On 2026-09-07 the daemon exited and no restart appeared
# in `daemon.log`; whether Task Scheduler attempted one could not be established after the fact
# because this channel was disabled on the host, so the question "did the restart run and fail, or
# never run at all" is still OPEN -- it needs the deliberate measurement the issue describes (end
# the daemon with a known code and watch), and nothing here has performed it. This line only makes
# that measurement, and the next real outage, readable.
#
# Best-effort by construction, and that is the whole reason it is a try/catch under an
# $ErrorActionPreference of "Stop": `wevtutil sl` needs elevation, this script deliberately runs
# unelevated as the operator (#1770 above), so on the normal path this FAILS and prints the command
# to run once from an elevated shell. A logging channel is not worth failing the registration of the
# daemon itself over.
$operationalLog = "Microsoft-Windows-TaskScheduler/Operational"
try {
    & wevtutil.exe sl $operationalLog /e:true 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "wevtutil exited $LASTEXITCODE" }
    Write-Host "Enabled $operationalLog."
} catch {
    Write-Host "Could not enable $operationalLog ($($_.Exception.Message))."
    Write-Host "Run this once from an ELEVATED PowerShell: wevtutil sl $operationalLog /e:true"
}
