# Tests for baton launcher scripts (baton.cmd and baton.ps1) and pointer flip (#1668)
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$launcherDir = [System.IO.Path]::Combine($repoRoot, "tools", "tool-refresh", "launcher")
$cmdLauncher = [System.IO.Path]::Combine($launcherDir, "baton.cmd")
$ps1Launcher = [System.IO.Path]::Combine($launcherDir, "baton.ps1")

function Assert-Equal($expected, $actual, $message) {
    if ($expected -ne $actual) {
        throw "Assertion failed: $message. Expected '$expected', got '$actual'."
    }
}

function Assert-Contains($haystack, $needle, $message) {
    if (-not $haystack.Contains($needle)) {
        throw "Assertion failed: $message. Expected output to contain '$needle', got:`n$haystack"
    }
}

# Standard Win32/CRT command-line quoting (the algorithm CommandLineToArgvW expects on the way in),
# so a forwarded argument round-trips through cmd's %* passthrough and the target process's own argv
# parser byte-for-byte instead of being reconstructed loosely by a naive space-join.
function ConvertTo-CommandLineArg([string]$arg) {
    if ($arg -eq "") { return '""' }
    if ($arg -notmatch '[\s"]') { return $arg }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    for ($i = 0; $i -lt $arg.Length; $i++) {
        $backslashes = 0
        while ($i -lt $arg.Length -and $arg[$i] -eq '\') { $backslashes++; $i++ }
        if ($i -eq $arg.Length) {
            [void]$sb.Append('\' * ($backslashes * 2))
            break
        } elseif ($arg[$i] -eq '"') {
            [void]$sb.Append('\' * ($backslashes * 2 + 1))
            [void]$sb.Append('"')
        } else {
            [void]$sb.Append('\' * $backslashes)
            [void]$sb.Append($arg[$i])
        }
    }
    [void]$sb.Append('"')
    return $sb.ToString()
}

# Builds a real mock `baton.exe` fixture in $outDir: echoes $label plus its forwarded argv, then
# exits with $exitCode. Compiled with the legacy csc.exe rather than `dotnet publish`/`dotnet build`
# -- a few hundred milliseconds, and it doesn't compete for the lock `dotnet build` takes (see
# tools/gates/gates.py's OVERLAP/BUILD_PHASE split for why that matters here).
function New-MockBatonExe([string]$outDir, [string]$label, [int]$exitCode, [string]$stderrLine = "") {
    [System.IO.Directory]::CreateDirectory($outDir) | Out-Null
    $cscCandidates = @(
        (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
        (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe")
    )
    $csc = $cscCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $csc) {
        throw "csc.exe not found under $env:WINDIR\Microsoft.NET -- cannot build the launcher test's mock exe fixture"
    }

    $src = Join-Path $outDir "MockBaton.cs"
    $exe = Join-Path $outDir "baton.exe"
    $lines = @(
        'class MockBaton {',
        '    static int Main(string[] args) {',
        ('        System.Console.WriteLine("' + $label + ' " + string.Join(" ", args));')
    )
    if ($stderrLine) {
        $lines += ('        System.Console.Error.WriteLine("' + $stderrLine + '");')
    }
    $lines += @(
        ('        return ' + $exitCode + ';'),
        '    }',
        '}'
    )
    Set-Content -LiteralPath $src -Value $lines
    $cscOutput = & $csc /nologo "/out:$exe" $src 2>&1
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "csc.exe failed to build the mock baton.exe fixture: $cscOutput"
    }
    return $exe
}

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "baton-launcher-tests-$([System.Guid]::NewGuid().ToString('N'))"
[System.IO.Directory]::CreateDirectory($tempDir) | Out-Null

try {
    $env:BATON_HOME = $tempDir
    $toolsDir = Join-Path $tempDir "tools"
    [System.IO.Directory]::CreateDirectory($toolsDir) | Out-Null
    $currentFile = Join-Path $toolsDir "current"

    # 1. Missing current pointer file fails closed
    Write-Host "Test 1: Missing pointer fails closed..."
    
    # Test baton.ps1
    $proc = Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile", "-File", "`"$ps1Launcher`"", "--version" -RedirectStandardError (Join-Path $tempDir "ps1_err1.txt") -Wait -PassThru -NoNewWindow
    Assert-Equal 1 $proc.ExitCode "baton.ps1 missing pointer exit code"
    $err = Get-Content (Join-Path $tempDir "ps1_err1.txt") -Raw
    Assert-Contains $err "pixi run tool-refresh" "baton.ps1 missing pointer error message"

    # Test baton.cmd
    $proc = Start-Process -FilePath "cmd.exe" -ArgumentList "/c", "`"$cmdLauncher`"", "--version" -RedirectStandardError (Join-Path $tempDir "cmd_err1.txt") -Wait -PassThru -NoNewWindow
    Assert-Equal 1 $proc.ExitCode "baton.cmd missing pointer exit code"
    $err = Get-Content (Join-Path $tempDir "cmd_err1.txt") -Raw
    Assert-Contains $err "pixi run tool-refresh" "baton.cmd missing pointer error message"

    # 2. Empty pointer file fails closed
    Write-Host "Test 2: Empty pointer file fails closed..."
    Set-Content -LiteralPath $currentFile -Value "   `r`n"
    
    # Test baton.ps1
    $proc = Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile", "-File", "`"$ps1Launcher`"", "--version" -RedirectStandardError (Join-Path $tempDir "ps1_err2.txt") -Wait -PassThru -NoNewWindow
    Assert-Equal 1 $proc.ExitCode "baton.ps1 empty pointer exit code"
    $err = Get-Content (Join-Path $tempDir "ps1_err2.txt") -Raw
    Assert-Contains $err "pixi run tool-refresh" "baton.ps1 empty pointer error message"

    # Test baton.cmd
    $proc = Start-Process -FilePath "cmd.exe" -ArgumentList "/c", "`"$cmdLauncher`"", "--version" -RedirectStandardError (Join-Path $tempDir "cmd_err2.txt") -Wait -PassThru -NoNewWindow
    Assert-Equal 1 $proc.ExitCode "baton.cmd empty pointer exit code"
    $err = Get-Content (Join-Path $tempDir "cmd_err2.txt") -Raw
    Assert-Contains $err "pixi run tool-refresh" "baton.cmd empty pointer error message"

    # 3. Garbage pointer (missing target binary) fails closed
    Write-Host "Test 3: Garbage pointer fails closed..."
    Set-Content -LiteralPath $currentFile -Value "garbage_sha_12345`r`n"
    
    # Test baton.ps1
    $proc = Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile", "-File", "`"$ps1Launcher`"", "--version" -RedirectStandardError (Join-Path $tempDir "ps1_err3.txt") -Wait -PassThru -NoNewWindow
    Assert-Equal 1 $proc.ExitCode "baton.ps1 garbage pointer exit code"
    $err = Get-Content (Join-Path $tempDir "ps1_err3.txt") -Raw
    Assert-Contains $err "pixi run tool-refresh" "baton.ps1 garbage pointer error message"

    # Test baton.cmd
    $proc = Start-Process -FilePath "cmd.exe" -ArgumentList "/c", "`"$cmdLauncher`"", "--version" -RedirectStandardError (Join-Path $tempDir "cmd_err3.txt") -Wait -PassThru -NoNewWindow
    Assert-Equal 1 $proc.ExitCode "baton.cmd garbage pointer exit code"
    $err = Get-Content (Join-Path $tempDir "cmd_err3.txt") -Raw
    Assert-Contains $err "pixi run tool-refresh" "baton.cmd garbage pointer error message"

    # 4. Valid pointer executes target binary and forwards args and exit code
    Write-Host "Test 4: Valid pointer launches target binary and forwards args verbatim..."
    $validSha = "v1_sha_abcd"
    $shaDir = Join-Path $toolsDir $validSha
    [System.IO.Directory]::CreateDirectory($shaDir) | Out-Null
    Set-Content -LiteralPath $currentFile -Value "$validSha`r`n"
    New-MockBatonExe -outDir $shaDir -label "mock-v1" -exitCode 42 | Out-Null

    # A space, a `!` next to a real variable name (would get eaten by delayed expansion left active
    # in baton.cmd's dispatch line), and an embedded double quote -- the three hazards F3's fix and
    # this test exist for. baton.cmd's manual `%*` forwarding is where all three matter; baton.ps1's
    # `& $exePath @args` forwarding is idiomatic and untested only for the embedded quote, which
    # Windows PowerShell 5.1's own native-command argument passing mangles regardless of what
    # baton.ps1 does with it (a host limitation, not a launcher defect) -- so the space and `!` cases
    # are asserted on both launchers, and the quote only through baton.cmd.
    $cmdTestArgs = @('has space', 'bang!TOOL_SHA!end', 'quo"te')
    $rawArgs = ($cmdTestArgs | ForEach-Object { ConvertTo-CommandLineArg $_ }) -join ' '

    # cmd.exe's own `/c "..."` handling strips only the very first and very last quote of the whole
    # command line when it begins AND ends with one, then treats everything between as a single
    # unparsed token -- an extra outer quote pair is required so cmd re-parses the inner content
    # (the launcher path plus forwarded args) normally instead of swallowing it as one blob.
    $cmdOut = Join-Path $tempDir "cmd_out4.txt"
    $proc = Start-Process -FilePath "cmd.exe" -ArgumentList "/c `"`"$cmdLauncher`" $rawArgs`"" -RedirectStandardOutput $cmdOut -RedirectStandardError (Join-Path $tempDir "cmd_err4.txt") -Wait -PassThru -NoNewWindow
    Assert-Equal 42 $proc.ExitCode "baton.cmd exit code propagation"
    $out = (Get-Content -LiteralPath $cmdOut -Raw)
    Assert-Contains $out "mock-v1" "baton.cmd launched the mock target"
    foreach ($a in $cmdTestArgs) { Assert-Contains $out $a "baton.cmd forwarded arg '$a' verbatim" }

    $ps1TestArgs = @('has space', 'bang!TOOL_SHA!end')
    $ps1RawArgs = ($ps1TestArgs | ForEach-Object { ConvertTo-CommandLineArg $_ }) -join ' '
    $ps1Out = Join-Path $tempDir "ps1_out4.txt"
    $proc = Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile -File `"$ps1Launcher`" $ps1RawArgs" -RedirectStandardOutput $ps1Out -RedirectStandardError (Join-Path $tempDir "ps1_err4.txt") -Wait -PassThru -NoNewWindow
    Assert-Equal 42 $proc.ExitCode "baton.ps1 exit code propagation"
    $out = (Get-Content -LiteralPath $ps1Out -Raw)
    Assert-Contains $out "mock-v1" "baton.ps1 launched the mock target"
    foreach ($a in $ps1TestArgs) { Assert-Contains $out $a "baton.ps1 forwarded arg '$a' verbatim" }

    # 5. Atomic pointer flip actually redirects the launcher to the new target
    Write-Host "Test 5: Pointer flip to new version launches the NEW target..."
    $v2Sha = "v2_sha_ef01"
    $v2Dir = Join-Path $toolsDir $v2Sha
    [System.IO.Directory]::CreateDirectory($v2Dir) | Out-Null
    New-MockBatonExe -outDir $v2Dir -label "mock-v2" -exitCode 0 | Out-Null

    $tmpPointer = Join-Path $toolsDir "current.tmp.test"
    $bakPointer = Join-Path $toolsDir "current.bak.test"
    Set-Content -LiteralPath $tmpPointer -Value "$v2Sha`r`n"
    [System.IO.File]::Replace($tmpPointer, $currentFile, $bakPointer)
    if (Test-Path -LiteralPath $bakPointer) { Remove-Item -LiteralPath $bakPointer -Force }

    $readSha = (Get-Content -LiteralPath $currentFile -Raw).Trim()
    Assert-Equal $v2Sha $readSha "Pointer was updated to v2Sha"

    $cmdOut5 = Join-Path $tempDir "cmd_out5.txt"
    $proc = Start-Process -FilePath "cmd.exe" -ArgumentList "/c `"`"$cmdLauncher`" ping`"" -RedirectStandardOutput $cmdOut5 -RedirectStandardError (Join-Path $tempDir "cmd_err5.txt") -Wait -PassThru -NoNewWindow
    Assert-Equal 0 $proc.ExitCode "baton.cmd exit code after pointer flip"
    $out5 = (Get-Content -LiteralPath $cmdOut5 -Raw)
    Assert-Contains $out5 "mock-v2" "baton.cmd ran the NEW target after the pointer flip"
    if ($out5.Contains("mock-v1")) {
        throw "Assertion failed: baton.cmd still ran the OLD target after the pointer flip. Got:`n$out5"
    }

    # 6. Redirected native stderr preserves the target exit code (#1897).
    Write-Host "Test 6: Redirected native stderr preserves the target exit code..."
    $stderrZeroSha = "stderr_zero_sha_1897"
    $stderrZeroDir = Join-Path $toolsDir $stderrZeroSha
    [System.IO.Directory]::CreateDirectory($stderrZeroDir) | Out-Null
    Set-Content -LiteralPath $currentFile -Value "$stderrZeroSha`r`n"
    $stderrZeroLine = "mock-stderr-zero"
    New-MockBatonExe -outDir $stderrZeroDir -label "mock-stderr-zero" -exitCode 0 -stderrLine $stderrZeroLine | Out-Null

    $ps1LauncherEscaped = $ps1Launcher.Replace("'", "''")
    $allStreamsZero = Join-Path $tempDir "ps1_all_streams_zero.txt"
    $allStreamsZeroEscaped = $allStreamsZero.Replace("'", "''")
    $allStreamsZeroCommand = "& { & '$ps1LauncherEscaped' *> '$allStreamsZeroEscaped'; exit `$LASTEXITCODE }"
    & powershell.exe -NoProfile -Command $allStreamsZeroCommand
    $stderrZeroExitCode = $LASTEXITCODE
    Assert-Equal 0 $stderrZeroExitCode "baton.ps1 exit code with redirected native stderr"
    $allStreamsZeroContents = Get-Content -LiteralPath $allStreamsZero -Raw
    Assert-Contains $allStreamsZeroContents $stderrZeroLine "baton.ps1 redirects native stderr with all streams"

    $stderrThreeSha = "stderr_three_sha_1897"
    $stderrThreeDir = Join-Path $toolsDir $stderrThreeSha
    [System.IO.Directory]::CreateDirectory($stderrThreeDir) | Out-Null
    Set-Content -LiteralPath $currentFile -Value "$stderrThreeSha`r`n"
    $stderrThreeLine = "mock-stderr-three"
    New-MockBatonExe -outDir $stderrThreeDir -label "mock-stderr-three" -exitCode 3 -stderrLine $stderrThreeLine | Out-Null

    $allStreamsThree = Join-Path $tempDir "ps1_all_streams_three.txt"
    $allStreamsThreeEscaped = $allStreamsThree.Replace("'", "''")
    $allStreamsThreeCommand = "& { & '$ps1LauncherEscaped' *> '$allStreamsThreeEscaped'; exit `$LASTEXITCODE }"
    & powershell.exe -NoProfile -Command $allStreamsThreeCommand
    $stderrThreeExitCode = $LASTEXITCODE
    Assert-Equal 3 $stderrThreeExitCode "baton.ps1 nonzero exit code with redirected native stderr"
    $allStreamsThreeContents = Get-Content -LiteralPath $allStreamsThree -Raw
    Assert-Contains $allStreamsThreeContents $stderrThreeLine "baton.ps1 redirects native stderr for a nonzero exit"

    # 7. Neither task-registering script calls New-ScheduledTaskSettingsSet with a parameter name
    # that cmdlet doesn't actually have (#1770: -DisallowStartIfOnBatteries/-StopIfGoingOnBatteries
    # don't exist on it and blew up the register call with NamedParameterNotFound before either
    # script reached Register-ScheduledTask). Both scripts carried the same bug, so both are checked.
    Write-Host "Test 7: task-registering scripts only pass real New-ScheduledTaskSettingsSet parameters..."
    $realParams = (Get-Command New-ScheduledTaskSettingsSet).Parameters.Keys
    $taskScripts = @(
        [System.IO.Path]::Combine($repoRoot, "tools", "tool-refresh", "register-daemon-task.ps1"),
        [System.IO.Path]::Combine($repoRoot, "tools", "fleet-glass", "deploy.ps1")
    )
    foreach ($registerScript in $taskScripts) {
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($registerScript, [ref]$null, [ref]$null)
        $calls = $ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.CommandAst] -and
            $node.GetCommandName() -eq "New-ScheduledTaskSettingsSet"
        }, $true)
        if ($calls.Count -eq 0) {
            throw "Assertion failed: no New-ScheduledTaskSettingsSet call found in $registerScript"
        }
        foreach ($call in $calls) {
            $usedParams = $call.CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.CommandParameterAst] } | ForEach-Object { $_.ParameterName }
            foreach ($p in $usedParams) {
                $match = $realParams | Where-Object { $_ -like "$p*" }
                if (-not $match) {
                    throw "Assertion failed: New-ScheduledTaskSettingsSet call in $registerScript passes unknown parameter '-$p'"
                }
            }
        }
    }

    # 8. #2036: the `baton-daemon` action's wrapper records the exit before returning it. Runs the
    # EXACT `-Argument` string register-daemon-task.ps1 registers -- lifted out of that script's AST
    # rather than restated here, so a wrapper that stops logging its exit fails this test instead of
    # passing a copy of itself -- through the real `baton.ps1` launcher on PATH down to a mock
    # `baton.exe`, in a throwaway working directory, and reads back `daemon.log`. SCOPED: this
    # measures the wrapper's capture-log-exit
    # shape (the half of #2036's gap 1 that lives outside the daemon process). It does NOT measure
    # what Task Scheduler does with the code it returns; that needs the live measurement #2036 asks
    # for and this test cannot stand in for it.
    Write-Host "Test 8: the registered baton-daemon action records its exit code in daemon.log..."
    $daemonTaskScript = [System.IO.Path]::Combine($repoRoot, "tools", "tool-refresh", "register-daemon-task.ps1")
    $daemonAst = [System.Management.Automation.Language.Parser]::ParseFile($daemonTaskScript, [ref]$null, [ref]$null)
    $actionCall = $daemonAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq "New-ScheduledTaskAction"
    }, $true) | Select-Object -First 1
    if (-not $actionCall) {
        throw "Assertion failed: no New-ScheduledTaskAction call found in $daemonTaskScript"
    }
    $actionArgument = $null
    for ($i = 0; $i -lt $actionCall.CommandElements.Count - 1; $i++) {
        $element = $actionCall.CommandElements[$i]
        if ($element -is [System.Management.Automation.Language.CommandParameterAst] -and
            $element.ParameterName -eq "Argument") {
            $actionArgument = $actionCall.CommandElements[$i + 1].Value
        }
    }
    if (-not $actionArgument) {
        throw "Assertion failed: could not read the -Argument string of New-ScheduledTaskAction in $daemonTaskScript"
    }

    # The operational-log enable (#2036 gap 2) cannot be measured here -- `wevtutil sl` needs
    # elevation and this suite runs unelevated. What CAN be checked is the property that makes it
    # safe to ship unrun: it is attempted, and it is inside a try, so under this script's
    # `$ErrorActionPreference = "Stop"` a refused enable cannot fail the daemon's registration.
    $daemonScriptText = Get-Content -LiteralPath $daemonTaskScript -Raw
    Assert-Contains $daemonScriptText "Microsoft-Windows-TaskScheduler/Operational" `
        "register-daemon-task.ps1 enables the Task Scheduler operational log"
    $wevtutilCalls = $daemonAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
        $node.Extent.Text -like "*wevtutil*"
    }, $true)
    if ($wevtutilCalls.Count -eq 0) {
        throw "Assertion failed: no wevtutil call found in $daemonTaskScript"
    }
    foreach ($wevtutilCall in $wevtutilCalls) {
        $ancestor = $wevtutilCall.Parent
        while ($ancestor -and -not ($ancestor -is [System.Management.Automation.Language.TryStatementAst])) {
            $ancestor = $ancestor.Parent
        }
        if (-not $ancestor) {
            throw "Assertion failed: the wevtutil call in $daemonTaskScript is not inside a try -- an unelevated run would fail the registration"
        }
    }

    # Through the REAL launcher, against a stub that WRITES TO STDERR -- both halves matter, and an
    # earlier draft of this arm had neither (2026-09-07 review). The action's `*>> 'daemon.log'`
    # redirects the child's native stderr, and under PowerShell 5.1 a redirected native stderr line
    # becomes a terminating NativeCommandError wherever $ErrorActionPreference is "Stop" -- which
    # would abort the -Command script before its trailing Out-File/`exit $c` ever run. That is #1899,
    # and its fix is the one line `$ErrorActionPreference = "Continue"` inside baton.ps1. A mock
    # `baton.exe` dropped straight on PATH resolves `baton` to the exe and never traverses the
    # launcher at all, so it cannot notice if that line is deleted; only `baton.ps1` goes on PATH
    # here (no `baton.cmd`), so `baton` resolves to the launcher, which then resolves
    # $BATON_HOME/tools/current to the stub. A stub emitting no stderr would likewise pass whether or
    # not the fix is present.
    $wrapperBinDir = Join-Path $tempDir "daemon-wrapper-bin"
    [System.IO.Directory]::CreateDirectory($wrapperBinDir) | Out-Null
    Copy-Item -LiteralPath $ps1Launcher -Destination (Join-Path $wrapperBinDir "baton.ps1") -Force
    $wrapperSha = "daemon_wrapper_sha_2036"
    $wrapperShaDir = Join-Path $toolsDir $wrapperSha
    [System.IO.Directory]::CreateDirectory($wrapperShaDir) | Out-Null
    Set-Content -LiteralPath $currentFile -Value "$wrapperSha`r`n"
    $wrapperStderrLine = "MOCK-DAEMON-STDERR"
    New-MockBatonExe $wrapperShaDir "MOCK-DAEMON" 70 $wrapperStderrLine | Out-Null
    $wrapperRunDir = Join-Path $tempDir "daemon-wrapper-run"
    [System.IO.Directory]::CreateDirectory($wrapperRunDir) | Out-Null
    $pathBeforeWrapper = $env:PATH
    try {
        $env:PATH = "$wrapperBinDir;$pathBeforeWrapper"
        # No -NoNewWindow, deliberately: the action carries `-WindowStyle Hidden`, and a child
        # sharing this console would hide the console the test itself is printing to.
        $proc = Start-Process -FilePath "powershell.exe" -ArgumentList $actionArgument `
            -WorkingDirectory $wrapperRunDir -Wait -PassThru
    } finally {
        $env:PATH = $pathBeforeWrapper
    }
    Assert-Equal 70 $proc.ExitCode "the action returns the daemon's own exit code to Task Scheduler"
    $wrapperLog = Join-Path $wrapperRunDir "daemon.log"
    if (-not (Test-Path -LiteralPath $wrapperLog)) {
        throw "Assertion failed: the action left no daemon.log in $wrapperRunDir"
    }
    # -Raw with no -Encoding: Get-Content follows the BOM `*>>` wrote, so an appended line in the
    # wrong encoding shows up here as mojibake and fails the Assert-Contains below rather than
    # passing quietly.
    $wrapperLogText = Get-Content -LiteralPath $wrapperLog -Raw
    Assert-Contains $wrapperLogText "MOCK-DAEMON daemon" "the action still captures the daemon's own output"
    Assert-Contains $wrapperLogText $wrapperStderrLine "the action captures the daemon's stderr through the launcher"
    Assert-Contains $wrapperLogText "baton daemon exited 70" "the action records the exit code in daemon.log"

    # 9. #2083: the daemon task is relaunched by a real repeating trigger, not RestartCount. The
    # wrapper test above proves a non-zero daemon exit reaches the task action; this AST assertion
    # pins the separate scheduler contract that gives such an exit another launch opportunity.
    Write-Host "Test 9: baton-daemon has a repeating relaunch trigger..."
    $triggerCalls = $daemonAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq "New-ScheduledTaskTrigger"
    }, $true)
    Assert-Equal 1 $triggerCalls.Count "baton-daemon has exactly one scheduled-task trigger"
    $triggerText = $triggerCalls[0].Extent.Text
    Assert-Contains $triggerText "-Once" "baton-daemon trigger starts its repetition schedule now"
    Assert-Contains $triggerText "-RepetitionInterval" "baton-daemon trigger repeats after a daemon exit"
    Assert-Contains $triggerText "-RepetitionDuration" "baton-daemon trigger has a bounded repetition schedule"
    if ($daemonScriptText.Contains("-AtLogOn")) {
        throw "Assertion failed: baton-daemon uses a logon-only trigger instead of a repeating relaunch trigger"
    }
    if ($daemonScriptText.Contains("-RestartCount") -or $daemonScriptText.Contains("-RestartInterval")) {
        throw "Assertion failed: baton-daemon still relies on RestartCount/RestartInterval instead of its repeating trigger"
    }

    Write-Host "All launcher tests PASSED!"
} finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    $env:BATON_HOME = $null
}
