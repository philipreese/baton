# Deploys the fleet mailbox Worker (worker.js) using wrangler's stored OAuth credentials, and
# registers the fleet-glass-pusher scheduled task that runs pusher.py locally (#1548). The sibling
# `baton-daemon` scheduled task (same convention, different action) is registered separately by
# `tools/tool-refresh/register-daemon-task.ps1` (#1557) -- run that once too, it is not part of
# this script.
# Secrets (PUSH_TOKEN, READ_SEGMENT) are generated once into secrets.local.json and never printed.
# secrets.local.json is gitignored (#1413) -- run this locally; it is not part of any CI job.
#
# One-time prerequisite: `npx wrangler login` (opens a browser) so wrangler holds its own OAuth
# session -- this script never handles a Cloudflare credential itself.
#
# #1946: this whole script -- and the Claude.ai artifact paste that follows it -- is now OPTIONAL,
# and no longer how an operator normally reaches the board; tools/fleet-glass/README.md states what
# replaced it and where that is set up. Everything below is
# the MAILBOX plane, whose remaining reach this directory's README states.
# No step here changed: step 3 pushes the Worker's secrets, which
# the Worker still needs for as long as the mailbox plane exists (the brief for #1946 described step
# 3 as an artifact publish; there has never been one in this script).
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# 1. Generate tokens once (idempotent).
$secretsPath = Join-Path $PSScriptRoot "secrets.local.json"
if (-not (Test-Path $secretsPath)) {
    $alphabet = [char[]]((48..57) + (97..122))
    $rand = {
        $bytes = [byte[]]::new(40)
        [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
    }
    @{ push_token = (& $rand); read_segment = (& $rand) } | ConvertTo-Json | Set-Content $secretsPath
}
$secrets = Get-Content $secretsPath | ConvertFrom-Json

# 2. Create KV namespace if placeholder still present.
$toml = Get-Content wrangler.toml -Raw
if ($toml -match "KV_ID_PLACEHOLDER") {
    $out = npx --yes wrangler kv namespace create FLEET 2>&1 | Out-String
    if ($out -match 'id\s*[=:]\s*"([0-9a-f]{32})"') {
        ($toml -replace "KV_ID_PLACEHOLDER", $Matches[1]) | Set-Content wrangler.toml -NoNewline
    } else {
        Write-Host $out
        throw "could not parse KV namespace id"
    }
}

# 3. Push secrets (values go via stdin, never argv).
$secrets.push_token   | npx --yes wrangler secret put PUSH_TOKEN
$secrets.read_segment | npx --yes wrangler secret put READ_SEGMENT

# 4. Deploy.
npx --yes wrangler deploy

# 5. Register/update the fleet-glass-pusher scheduled task (idempotent: Register-ScheduledTask
# -Force overwrites an existing task of the same name in place, so re-running this on a machine
# that already has the task converges rather than failing or duplicating it).
#
# The 15-minute repetition trigger is a deliberate watchdog, not redundant polling (#1548). It is
# safe against a *live* pusher because of two independent guards, and it is what performs the
# self-heal against a *dead* one:
#   - Task Scheduler's own MultipleInstancesPolicy=IgnoreNew skips a due trigger outright while
#     the previously launched instance is still alive, so a healthy long-running pusher normally
#     never sees a second launch at all.
#   - If a launch does get through while pusher.lock is held, pusher.py's acquire_lock (#1538)
#     checks the holder: a dead PID is reclaimed and logged ("reclaimed stale lock (pid dead)");
#     a live PID whose command line contains "pusher" and does NOT also contain "claude" is
#     terminated and replaced ("deploys always win"); any other live PID (including one running
#     under a Claude session) is left running and the lock is reclaimed out from under it, logged
#     as "reclaimed stale lock (pid not a pusher)" even though that PID is alive. None of these
#     branches produces a second running pusher, so firing the trigger against a healthy process
#     is at worst a harmless restart (or, in the Claude-cmdline case, a no-op lock reclaim).
$taskName = "fleet-glass-pusher"
$pythonPath = (Get-Command python -ErrorAction Stop).Source
$pusherScript = Join-Path $PSScriptRoot "pusher.py"
$action = New-ScheduledTaskAction -Execute $pythonPath -Argument "`"$pusherScript`"" -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) `
    -RepetitionInterval (New-TimeSpan -Minutes 15) -RepetitionDuration (New-TimeSpan -Days 3650)
$taskSettings = New-ScheduledTaskSettingsSet `
    -MultipleInstances IgnoreNew `
    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 5) -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
    -Settings $taskSettings -Force | Out-Null
