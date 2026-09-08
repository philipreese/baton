# Registers the `baton-daemon` scheduled task that keeps `baton daemon` running persistently
# (#1557 side item). `RoomRetentionSweep` and the fleet-wide concurrency-cap apply (spec/baton.md
# §7) are both hosted services inside `baton daemon` -- they only do anything while some process
# is actually running that verb, and nothing before this script registered one. This is the
# `baton-daemon` sibling of the `fleet-glass-pusher` task `tools/fleet-glass/deploy.ps1` (step 5)
# registers -- same convention (idempotent `Register-ScheduledTask -Force`, repeating relaunch,
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
# `exit $LASTEXITCODE` exits 70. The repeating trigger below is the relaunch mechanism (#2083), but
# the scheduler's Last Run Result and the exit record below still need the daemon's real code. An
# existing registration keeps the old action until this script is re-run.
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
#
# `[cultureinfo]::InvariantCulture` on the ToString below is load-bearing too: `:` in a custom format
# string is the CULTURE's time separator, so on a host whose locale separates with '.' the appended
# line would read `04.57.06` and stop matching the `[...Z]` shape every other line in `daemon.log`
# uses -- breaking any reader that parses it, on the one line that exists to be read after an outage.
# (The literal `Z` needs no escaping: it is not a recognised custom specifier, so it is emitted
# verbatim, same as on the C# side.)
$action = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument '-NoProfile -WindowStyle Hidden -Command "& { baton daemon *>> ''daemon.log'' }; $c = $LASTEXITCODE; if ($null -eq $c) { $c = 1 }; (''['' + [DateTime]::UtcNow.ToString(''yyyy-MM-ddTHH:mm:ss.fffZ'', [cultureinfo]::InvariantCulture) + ''] baton daemon exited '' + $c) | Out-File -FilePath ''daemon.log'' -Append -Encoding unicode; exit $c"' `
    -WorkingDirectory $batonHome

# A logon trigger can only launch once, and Task Scheduler's RestartCount does not relaunch this
# task after its non-zero daemon exit (#2083). A one-time trigger with repetition is the durable
# relaunch mechanism: every five minutes it starts a dead daemon, while IgnoreNew skips a due
# trigger while the healthy daemon is still alive. The 10-year duration is the same bounded form
# as fleet-glass-pusher's trigger (deploy.ps1 step 5), rather than an unbounded scheduler setting.
$triggerRepeat = New-ScheduledTaskTrigger -Once -At (Get-Date) `
    -RepetitionInterval (New-TimeSpan -Minutes 5) -RepetitionDuration (New-TimeSpan -Days 3650)

# IgnoreNew keeps the repeating trigger from overlapping a healthy daemon. A repeat hang is still
# observable in daemon.log rather than being papered over by a rapid restart loop.
$taskSettings = New-ScheduledTaskSettingsSet `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -Hidden

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $triggerRepeat `
    -Settings $taskSettings -Force | Out-Null

# #2036: turn the Task Scheduler operational log on, so the next scheduled relaunch has a record.
# On 2026-09-07 the daemon exited and no subsequent launch appeared in `daemon.log`; the disabled
# channel could not establish whether the scheduler attempted one. #2083 replaces the ineffective
# restart count with the repeating trigger above; the channel makes the next trigger and any failed
# action readable.
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
