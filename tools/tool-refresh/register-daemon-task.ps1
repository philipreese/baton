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
$action = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument '-NoProfile -WindowStyle Hidden -Command "& { baton daemon *>> ''daemon.log'' }; exit $LASTEXITCODE"' `
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
