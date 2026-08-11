#requires -Version 5.1
<#
    Integration tests for ParsecHooks.

    Drives the real ParsecHooks.exe against a synthetic Parsec log (via the logPath config
    override) and asserts REAL display state through ProbeDisplay.exe, which talks to the
    Win32 CCD API independently. Nothing is asserted from the app's own reporting.

    Everything machine-specific is detected at run time: display count, the primary's mode,
    the secondary's geometry and the OS build all come from the machine you run on.

    Requirements
      - 2 or more active displays, and HDR currently ON on the primary. Phases that need
        those are skipped with a clear message otherwise.
      - Run as the logged-in user, not elevated (the app needs no elevation either).

    This WILL blank and re-enable your secondary display several times and toggle HDR. It
    always restores state in `finally`, and re-persists the layout so nothing leaks into a
    later run. Expect ~4 minutes.
#>
$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot          # repo root
$exe     = Join-Path $root 'bin\ParsecHooks.exe'
$ini     = Join-Path $root 'bin\parsec-hooks.ini'
$work    = Join-Path ([IO.Path]::GetTempPath()) 'parsechooks-tests'
$probe   = Join-Path $work 'ProbeDisplay.exe'
$testLog = Join-Path $work 'fake-parsec-log.txt'
$appLog  = Join-Path $env:LOCALAPPDATA 'parsec-hooks\parsec-hooks.log'
$stateF  = Join-Path $env:LOCALAPPDATA 'parsec-hooks\applied-state.bin'
$utf8    = New-Object Text.UTF8Encoding($false)
$csc     = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

# Fake identities for the synthetic log. Real Parsec tokens look like "name#1234567".
$USER1 = 'tester#1234567'
$USER2 = 'friend#7654321'

New-Item -ItemType Directory -Force -Path $work | Out-Null

$script:fails = 0
$script:skips = 0
function Check($label, $cond, $detail) {
    if ($cond) { Write-Host ("  PASS  " + $label) -ForegroundColor Green }
    else { Write-Host ("  FAIL  " + $label + "  --> " + $detail) -ForegroundColor Red; $script:fails++ }
}
function Skip($label, $why) {
    Write-Host ("  SKIP  " + $label + "  --> " + $why) -ForegroundColor DarkYellow; $script:skips++
}
function Emit($line) { [IO.File]::AppendAllText($testLog, $line + "`r`n", $utf8) }
function Ts { (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') }
function KillApp {
    Get-Process ParsecHooks -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    Start-Sleep -Milliseconds 600
}

# --- synchronise on the app's own log so we never race its multi-step apply ---
$script:mark = 0
function LogLines { if (Test-Path $appLog) { @(Get-Content $appLog -ErrorAction SilentlyContinue) } else { @() } }
function MarkLog { $script:mark = (LogLines).Count }
function WaitLog($re, $timeoutSec = 25) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        $lines = LogLines
        for ($i = $script:mark; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match $re) { $script:mark = $i + 1; return $true }
        }
        if ($lines.Count -gt $script:mark) { $script:mark = $lines.Count }
        Start-Sleep -Milliseconds 120
    }
    Write-Host ("        (timeout waiting for log /" + $re + "/)") -ForegroundColor DarkYellow
    return $false
}

# --- ground truth, read independently of the app under test ---
function State {
    $out = & $probe list
    $count = ([regex]::Matches(($out -join "`n"), '(?m)^\[\d+\]')).Count
    $hdr = $null
    foreach ($l in $out) { if ($l -match 'HDR: supported=\w+ enabled=(\w+)') { $hdr = ($Matches[1] -eq 'True'); break } }
    [pscustomobject]@{ Displays = $count; PrimaryHdr = $hdr; Raw = ($out -join "`n") }
}
function PrimaryMode {
    foreach ($l in (& $probe modes)) {
        if ($l -match 'current mode on .* = (\d+)x(\d+)@(\d+)') { return "$($Matches[1])x$($Matches[2])@$($Matches[3])" }
    }
    return $null
}
function WaitState($predicate, $timeoutSec = 20) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        if (& $predicate (State)) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}
function WriteConfig($extra) {
    $base = @(
        'keep = primary'
        'disableSecondaryMonitors = true'
        'disableHdr = true'
        'hdrScope = kept'
        'applyDelayMs = 400'
        'revertDelayMs = 600'
        'settleMs = 300'
        'pollMs = 250'
        'baselineRefreshMs = 5000'
        "logPath = $testLog"
        'logLevel = debug'
        'notifications = false'
    ) + $extra
    [IO.File]::WriteAllText($ini, (($base -join "`r`n") + "`r`n"), $utf8)
}

Write-Host "`n### build ###" -ForegroundColor Cyan
if (-not (Test-Path $csc)) { throw "in-box C# compiler not found at $csc" }
& $csc /nologo /optimize /target:exe /out:$probe (Join-Path $PSScriptRoot 'ProbeDisplay.cs')
if ($LASTEXITCODE -ne 0) { throw 'ProbeDisplay build failed' }
"  probe   -> $probe"
if (-not (Test-Path $exe)) {
    cmd /c (Join-Path $root 'build.cmd') | Out-Null
}
if (-not (Test-Path $exe)) { throw "ParsecHooks.exe not found; run build.cmd first" }
"  app     -> $exe"

# ---------------------------------------------------------------- preconditions
Write-Host "`n### detecting this machine (nothing below is hardcoded) ###" -ForegroundColor Cyan
KillApp
$base = State
$baseDisplays = $base.Displays
$nativeMode   = PrimaryMode
# Remember every "WxH @(x,y)" and the Hz of each display so exact restoration can be
# asserted later without knowing the numbers in advance.
$baseGeometry = [regex]::Matches($base.Raw, '\d+x\d+ @\(-?\d+,-?\d+\)') | ForEach-Object { $_.Value }
$baseHz       = [regex]::Matches($base.Raw, '\d+[,.]\d+Hz') | ForEach-Object { $_.Value }
$osBuild      = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuild

"  displays   : $baseDisplays"
"  primary    : $nativeMode"
"  geometry   : $($baseGeometry -join '  ')"
"  refresh    : $($baseHz -join '  ')"
"  OS build   : $osBuild"
"  primaryHdr : $($base.PrimaryHdr)"

$canTopology = $baseDisplays -ge 2
$canHdr      = $base.PrimaryHdr -eq $true
if (-not $canTopology) { Write-Host "  NOTE: fewer than 2 displays - topology phases will be skipped" -ForegroundColor DarkYellow }
if (-not $canHdr)      { Write-Host "  NOTE: HDR is off on the primary - HDR phases will be skipped" -ForegroundColor DarkYellow }

# a mode smaller than native, to stand in for a client-negotiated resolution
$smaller = $null
foreach ($l in (& $probe modes)) {
    if ($l -match '^\s{2}(\d+)x(\d+)\s') {
        $w = [int]$Matches[1]; $h = [int]$Matches[2]
        if ($w -ge 1024 -and $w -lt ([int]($nativeMode -split 'x')[0])) { $smaller = "$w`x$h"; break }
    }
}
"  stand-in client mode: $(if ($smaller) { $smaller } else { '<none found>' })"

try {
    Write-Host "`n### setup ###" -ForegroundColor Cyan
    Remove-Item $appLog, $stateF -Force -ErrorAction SilentlyContinue
    $pre = @(
        "[F $(Ts)] ===== Parsec: Started ====="
        "[D $(Ts)] log: Parsec release (150-104a, Service: 13, Loader: 17)"
        "[D $(Ts)] IPC AS Client Connected."
    ) -join "`r`n"
    [IO.File]::WriteAllText($testLog, $pre + "`r`n", $utf8)
    WriteConfig @()
    Check "starting state is usable" ($baseDisplays -ge 1) "no displays detected"

    Write-Host "`n### phase 1: launch -> idle + baseline captured ###" -ForegroundColor Cyan
    MarkLog
    Start-Process -FilePath $exe -WorkingDirectory (Join-Path $root 'bin')
    Check "reconciled 0 clients" (WaitLog '0 client\(s\) currently connected') "no reconcile line"
    Check "baseline captured"    (WaitLog "baseline captured: $baseDisplays active display") "no baseline line"
    Check "process running"      ($null -ne (Get-Process ParsecHooks -ErrorAction SilentlyContinue)) "not running"
    Check "real OS build logged" ((LogLines) -match "build $osBuild") "OS line wrong"
    Check "no premature change"  ((State).Displays -eq $baseDisplays) "displays changed early"

    Write-Host "`n### phase 2: noise must NOT trigger ###" -ForegroundColor Cyan
    MarkLog
    Emit "[D $(Ts)] IPC AS Client Connected."
    Emit "[D $(Ts)] UPNP: Getting a valid IGD: A valid IGD has been found but it reported as not connected"
    Emit "[I $(Ts)] STUN reply from ::ffff:198.51.100.7:3478"
    Emit "[I $(Ts)] Parsec Virtual USB Adapter failed to initialize, no virtual device support"
    Emit "[D $(Ts)] somebody#123 connected."
    Emit "[I $(Ts)] Client '$USER1' went dormant"
    Emit "[I $(Ts)] marker#1 disconnected."
    Check "sentinel processed"        (WaitLog 'DISCONNECT marker#1') "watcher not reading"
    $s = State
    Check "noise left monitors alone" ($s.Displays -eq $baseDisplays) "displays=$($s.Displays)"
    Check "noise left HDR alone"      ($s.PrimaryHdr -eq $base.PrimaryHdr) "hdr=$($s.PrimaryHdr)"
    Check "wrong-level line ignored"  (-not ((LogLines) -match 'CONNECT   somebody#123')) "matched a [D] line"

    if (-not $canTopology) {
        Skip "phases 3-12 (topology)" "needs 2 or more displays"
    } else {

    Write-Host "`n### phase 3: connect -> disable others + HDR off ###" -ForegroundColor Cyan
    MarkLog
    Emit "[I $(Ts)] $USER1 connected."
    Check "apply completed"    (WaitLog 'saved applied-state') "apply never finished"
    $s = State
    Check "others disabled"    ($s.Displays -eq 1) "displays=$($s.Displays)"
    Check "state file written" (Test-Path $stateF) "missing"
    if ($canHdr) { Check "HDR off on primary" ($s.PrimaryHdr -eq $false) "hdr=$($s.PrimaryHdr)" }
    else { Skip "HDR off on primary" "HDR was already off" }

    Write-Host "`n### phase 3b: does the change hold? ###" -ForegroundColor Cyan
    $samples = @()
    for ($i = 0; $i -lt 8; $i++) { Start-Sleep -Seconds 1; $samples += (State).Displays }
    "  display-count samples over 8s: $($samples -join ',')"
    Check "stayed disabled for 8s"        (($samples | Where-Object { $_ -ne 1 }).Count -eq 0) "drifted: $($samples -join ',')"
    Check "no unrequested revert logged" (-not ((LogLines)[$script:mark..((LogLines).Count-1)] -match 'restored baseline')) "reverted on its own"

    if ($canHdr) {
        Write-Host "`n### phase 3c: guard re-asserts external drift ###" -ForegroundColor Cyan
        # Switching HDR back on externally makes Windows re-apply its remembered layout, which
        # also re-enables the display we turned off. Both must be corrected.
        MarkLog
        & $probe hdr primary on | Out-Null
        Start-Sleep -Seconds 2
        $mid = State
        "  right after external HDR-on: displays=$($mid.Displays) hdr=$($mid.PrimaryHdr)"
        Check "guard noticed the drift" (WaitLog 'guard: HDR had come back on' 20) "guard never fired"
        Check "guard re-asserted both"  (WaitState { param($x) $x.Displays -eq 1 -and $x.PrimaryHdr -eq $false } 25) "not restored"
    } else { Skip "phase 3c (guard drift)" "needs HDR on the primary" }

    Write-Host "`n### phase 4: 2nd client -> no extra action ###" -ForegroundColor Cyan
    MarkLog
    Emit "[I $(Ts)] $USER2 connected."
    Check "2nd connect seen" (WaitLog "CONNECT   $([regex]::Escape($USER2)) -> 2 client") "not tracked"
    Check "still 1 display"  ((State).Displays -eq 1) "displays changed"

    Write-Host "`n### phase 5: 1st client leaves -> must NOT revert ###" -ForegroundColor Cyan
    MarkLog
    Emit "[I $(Ts)] $USER1 disconnected."
    Check "disconnect seen"  (WaitLog "DISCONNECT $([regex]::Escape($USER1)) -> 1 client") "not tracked"
    Start-Sleep -Seconds 2
    Check "no revert while a client remains" (-not ((LogLines) -match 'Parsec session ended')) "reverted too early"
    Check "still flagged as applied"         (Test-Path $stateF) "state file gone"
    Check "guard converges to 1 display"     (WaitState { param($x) $x.Displays -eq 1 } 20) "never converged"

    Write-Host "`n### phase 6: last client leaves -> full revert ###" -ForegroundColor Cyan
    MarkLog
    Emit "[I $(Ts)] $USER2 disconnected."
    Check "revert completed" (WaitLog 'Parsec session ended') "revert never finished"
    Start-Sleep -Milliseconds 800
    $s = State
    Check "all displays back"  ($s.Displays -eq $baseDisplays) "displays=$($s.Displays)"
    Check "state file cleared" (-not (Test-Path $stateF)) "still present"
    if ($canHdr) { Check "HDR restored ON" ($s.PrimaryHdr -eq $true) "hdr=$($s.PrimaryHdr)" }

    Write-Host "`n### phase 7: exact geometry fidelity ###" -ForegroundColor Cyan
    $now = (State).Raw
    $missingGeom = $baseGeometry | Where-Object { $now -notlike "*$_*" }
    $missingHz   = $baseHz       | Where-Object { $now -notlike "*$_*" }
    Check "every position/size restored" ($missingGeom.Count -eq 0) "missing: $($missingGeom -join ' ')"
    Check "every refresh rate restored"  ($missingHz.Count -eq 0)   "missing: $($missingHz -join ' ')"
    Check "primary mode restored"        ((PrimaryMode) -eq $nativeMode) "mode=$(PrimaryMode) expected $nativeMode"

    Write-Host "`n### phase 8: bounce cancels a pending revert ###" -ForegroundColor Cyan
    MarkLog
    Emit "[I $(Ts)] $USER1 connected."
    Check "re-applied" (WaitLog 'saved applied-state') "apply failed"
    MarkLog
    Emit "[I $(Ts)] $USER1 disconnected."
    Emit "[I $(Ts)] $USER1 connected."
    Check "revert was cancelled"          (WaitLog 'cancelled pending revert') "no cancel"
    Start-Sleep -Seconds 2
    Check "stayed applied through bounce" ((State).Displays -eq 1) "displays changed"

    Write-Host "`n### phase 9: kill while applied -> crash recovery ###" -ForegroundColor Cyan
    Check "state file present pre-kill" (Test-Path $stateF) "missing"
    KillApp
    Check "still degraded after kill" ((State).Displays -eq 1) "topology should survive process death"
    MarkLog
    Start-Process -FilePath $exe -WorkingDirectory (Join-Path $root 'bin')
    Check "recovery ran" (WaitLog 'crash recovery complete') "no recovery"
    Start-Sleep -Milliseconds 800
    $s = State
    Check "recovery restored displays" ($s.Displays -eq $baseDisplays) "displays=$($s.Displays)"
    Check "state file cleared"         (-not (Test-Path $stateF)) "still present"
    if ($canHdr) { Check "recovery restored HDR" ($s.PrimaryHdr -eq $true) "hdr=$($s.PrimaryHdr)" }

    Write-Host "`n### phase 10: log rotation + idle baseline recovery ###" -ForegroundColor Cyan
    MarkLog
    [IO.File]::WriteAllText($testLog, "[F $(Ts)] ===== Parsec: Started =====`r`n", $utf8)
    Check "rotation detected"     (WaitLog 'truncated/rotated') "not detected"
    Check "process survived"       ($null -ne (Get-Process ParsecHooks -ErrorAction SilentlyContinue)) "died"
    Check "baseline taken on idle" (WaitLog 'baseline captured') "no immediate baseline"
    MarkLog
    Emit "[I $(Ts)] $USER1 connected."
    Check "applies after rotation" (WaitLog 'saved applied-state') "did not apply"
    Check "1 display after rotation" ((State).Displays -eq 1) "displays wrong"
    MarkLog
    Emit "[I $(Ts)] $USER1 disconnected."
    Check "reverts after rotation" (WaitLog 'Parsec session ended') "did not revert"
    Start-Sleep -Milliseconds 800
    Check "all displays back after rotation" ((State).Displays -eq $baseDisplays) "displays wrong"

    Write-Host "`n### phase 11: missing log tolerated ###" -ForegroundColor Cyan
    MarkLog
    Remove-Item $testLog -Force
    Check "log-vanished handled" (WaitLog 'log vanished') "not handled"
    Check "process survived"      ($null -ne (Get-Process ParsecHooks -ErrorAction SilentlyContinue)) "died"

    Write-Host "`n### phase 12: config edited on disk reloads automatically ###" -ForegroundColor Cyan
    MarkLog
    WriteConfig @('guardMs = 4000')
    Check "noticed the external edit" (WaitLog 'config file changed on disk' 20) "never noticed"
    Check "reloaded the new value"    (WaitLog 'config reloaded:.*guardMs=4000' 10) "not picked up"

    Write-Host "`n### phase 13: a client-negotiated mode must survive the session ###" -ForegroundColor Cyan
    # The regression this guards: our HDR/topology work resets the mode as a side effect, and
    # if the display database is not ratified afterwards Windows re-applies it every ~10s and
    # the client's resolution is lost.
    if (-not $smaller) { Skip "phase 13" "no smaller mode available to stand in for a client" }
    else {
        MarkLog
        [IO.File]::WriteAllText($testLog, "[F $(Ts)] ===== Parsec: Started =====`r`n", $utf8)
        Check "log re-detected" (WaitLog 'Parsec log appeared' 25) "not re-detected"
        Start-Sleep -Milliseconds 500
        WriteConfig @('applyDelayMs = 3000', 'guardMs = 4000')
        Check "config reloaded" (WaitLog 'config reloaded' 20) "not reloaded"
        Start-Sleep -Seconds 1

        MarkLog
        Emit "[I $(Ts)] $USER1 connected."
        Check "connect seen" (WaitLog "CONNECT   $([regex]::Escape($USER1))" 15) "not seen"
        # Set the mode AND ratify it, which is what makes a real Parsec change stick: a purely
        # transient one is undone by Windows re-applying its database before the app even acts.
        # Safe to persist here because it happens after connect, so the app's idle baseline was
        # already captured and cannot be polluted by it.
        $wh = $smaller -split 'x'
        & $probe setmode $wh[0] $wh[1] | Out-Null
        & $probe persist | Out-Null
        Start-Sleep -Milliseconds 800
        $staged = PrimaryMode
        "  staged client mode: $staged"

        if ($staged -notlike "$smaller@*") {
            Skip "phase 13 assertions" "could not stage a client mode (Windows reverted it to $staged)"
            [void](WaitLog 'Apply: state after' 25)
            MarkLog; Emit "[I $(Ts)] $USER1 disconnected."; [void](WaitLog 'Parsec session ended' 25); Start-Sleep -Seconds 2
        } else {
            Check "apply completed" (WaitLog 'Apply: state after' 25) "apply never finished"
            Start-Sleep -Seconds 2
            Check "monitor disabled"    ((State).Displays -eq 1) "not disabled"
            Check "client mode kept"    ((PrimaryMode) -eq $staged) "mode=$(PrimaryMode) expected $staged"
            Check "layout was ratified" ((LogLines) -match 'ratified current layout') "no ratify logged"

            $ms = @(); for ($i = 0; $i -lt 15; $i++) { Start-Sleep -Seconds 2; $ms += (PrimaryMode) }
            "  mode samples over 30s: $($ms -join ' ')"
            Check "client mode survives 30s" (($ms | Where-Object { $_ -ne $staged }).Count -eq 0) "reverted: $($ms -join ' ')"
            Check "monitor stayed off"       ((State).Displays -eq 1) "monitor came back"

            MarkLog
            Emit "[I $(Ts)] $USER1 disconnected."
            Check "revert completed" (WaitLog 'Parsec session ended' 25) "revert never finished"
            Start-Sleep -Seconds 2
            Check "displays restored"    ((State).Displays -eq $baseDisplays) "displays wrong"
            Check "native mode restored" ((PrimaryMode) -eq $nativeMode) "mode=$(PrimaryMode)"
        }
    }

    }  # end topology-capable

    Write-Host "`n### phase 14: auto-start registration ###" -ForegroundColor Cyan
    $run = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'parsec-hooks' -ErrorAction SilentlyContinue).'parsec-hooks'
    if ($run) {
        Check "Run value points at an exe" ($run -match 'ParsecHooks\.exe') "value=$run"
        $lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'parsec-hooks.lnk'
        Check "no duplicate Startup shortcut" (-not (Test-Path $lnk)) "shortcut present, would launch twice"
    } else { Skip "auto-start checks" "not registered (run install.cmd to exercise these)" }
}
finally {
    Write-Host "`n### teardown ###" -ForegroundColor Cyan
    KillApp
    Remove-Item $stateF -Force -ErrorAction SilentlyContinue

    if ($canHdr -and (State).PrimaryHdr -ne $true) { & $probe hdr primary on | Out-Null; Start-Sleep -Seconds 1 }
    if ((PrimaryMode) -ne $nativeMode -and $nativeMode -match '^(\d+)x(\d+)@(\d+)$') {
        & $probe setmode $Matches[1] $Matches[2] $Matches[3] | Out-Null
        Start-Sleep -Seconds 1
    }
    if ((State).Displays -lt $baseDisplays) { & $probe restore | Out-Null; Start-Sleep -Seconds 2 }

    $s = State
    "  final: displays=$($s.Displays) mode=$(PrimaryMode) primaryHdr=$($s.PrimaryHdr)"
    if ($s.Displays -eq $baseDisplays -and (PrimaryMode) -eq $nativeMode) {
        # Leave Windows' remembered layout matching reality, or a later run measures the wrong
        # "native" state and passes vacuously.
        & $probe persist | Out-Null
        "  remembered layout re-persisted"
    } else {
        Write-Host "  !! NOT fully restored - check Display settings before trusting another run" -ForegroundColor Red
    }
}

Write-Host "`n=================================" -ForegroundColor Cyan
if ($script:fails -eq 0) { Write-Host " ALL CHECKS PASSED$(if ($script:skips) { " ($($script:skips) skipped)" })" -ForegroundColor Green }
else { Write-Host " $($script:fails) CHECK(S) FAILED$(if ($script:skips) { ", $($script:skips) skipped" })" -ForegroundColor Red }
Write-Host "=================================" -ForegroundColor Cyan
exit $script:fails
