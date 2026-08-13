<#
.SYNOPSIS
    Installs TermWrap — the multi-session patch for Terminal Services.
.DESCRIPTION
    Windows client editions allow one interactive session; MultiSeat needs several, so a shim
    must load in place of the stock termsrv.dll.

    MultiSeat previously shipped RDP Wrapper for this. RDP Wrapper looks its patch offsets up
    in rdpwrap.ini, keyed by the exact termsrv.dll build, so every Windows cumulative update
    that ships a new termsrv.dll breaks it until somebody publishes a matching ini entry. On
    this host that meant RDPConf reporting "Listening [not supported]" and seat provisioning
    failing with "RDP loopback session did not appear within timeout" — worked around only by
    uninstalling the update and disabling Windows Update entirely, which is not an acceptable
    trade on a machine with ports forwarded to the internet.

    TermWrap (https://github.com/llccd/TermWrap) disassembles termsrv.dll with Zydis and finds
    the offsets itself, so it needs no per-build ini and survives Windows updates.

    WHAT UPSTREAM'S README LEAVES OUT
    Following TermWrap's four documented steps exactly left RDP completely broken here. Three
    things had to be fixed afterwards, none of them mentioned upstream — this script asserts
    all three (step 6):

      fDenyTSConnections = 1     Remote Desktop switched off entirely; nothing bound to 3389
                                 no matter what TermService did. Set by RDP Wrapper's
                                 uninstaller, which is why it must run BEFORE this fix.
      fSingleSessionPerUser = 1  RDP listened, but `mstsc /v:127.0.0.2` opened a window that
                                 closed immediately and MultiSeat logged "the connection may
                                 have reconnected an existing session instead of creating a
                                 new one". This is RDPConf's "Single session per user"
                                 checkbox; TermWrap's .reg does not clear it.
      TermService Stopped/Manual Had to be started and set to Automatic by hand.

    Also asserts UserAuthentication = 0 (NLA off) under WinStations\RDP-Tcp, which MultiSeat
    requires and RDP Wrapper's removal may reset.

    Idempotent: re-running detects an existing TermWrap install and skips straight to
    re-asserting the settings above and verifying, without reinstalling.

.PARAMETER SkipDownload
    Skip downloading; only install if the TermWrap zip is already in this folder.
.PARAMETER SkipReboot
    Suppress the reboot recommendation at the end.

.NOTES
    ROLLBACK
      1. reg import "C:\Program Files\RDP Wrapper\Revert_to_default.reg"
         (or Revert_to_rdpwrap.reg to hand Terminal Services back to RDP Wrapper)
      2. Reboot.
      3. Delete TermWrap.dll / Zydis.dll (and UmWrap.dll / EndpWrap.dll if installed)
         from C:\Program Files\RDP Wrapper\.
    Both .reg files ship inside the TermWrap zip and are copied to the install directory.
#>
param(
    [switch]$SkipDownload,
    [switch]$SkipReboot
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Write-Step($msg) { Write-Host "`n[TermWrap] $msg" -ForegroundColor Cyan }
function Write-OK($msg)   { Write-Host "  OK: $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "  WARNING: $msg" -ForegroundColor Yellow }
function Write-Fail($msg) { Write-Host "  FAIL: $msg" -ForegroundColor Red }

# TermWrap's .reg files hardcode %ProgramFiles%\RDP Wrapper as the DLL location, so the
# install directory is not ours to choose — it must match or ServiceDll points at nothing.
$InstallDir   = Join-Path $env:ProgramFiles "RDP Wrapper"
$TermServKey  = "HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server"
$RdpTcpKey    = "HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp"
$ParametersKey= "HKLM:\SYSTEM\CurrentControlSet\Services\TermService\Parameters"

# ── 1. Elevation ─────────────────────────────────────────────────────────────
# Checked explicitly rather than via #Requires so the failure is a sentence rather than a
# PowerShell diagnostic — and so it fails here, not part-way through a registry write.
Write-Step "Checking for administrator rights..."
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Fail "This script must run as Administrator."
    Write-Host "  It writes to HKLM and to $InstallDir, and restarts TermService." -ForegroundColor Yellow
    Write-Host "  Re-run from an elevated PowerShell prompt (right-click > Run as administrator)." -ForegroundColor Yellow
    exit 1
}
Write-OK "Running elevated as $($identity.Name)"

# ── Architecture ─────────────────────────────────────────────────────────────
# TermWrap ships x64 and x86 payloads only. TermService is a native process, so an emulated
# x64 DLL will not load on ARM64 — fail loudly rather than installing something that cannot
# work. (The x64 payload is selected by an explicit path segment below, never by "first
# match": install-virtual-display.ps1's asset discovery picks the ARM64 driver on this x64
# host, and that bug must not be copied here.)
if ($env:PROCESSOR_ARCHITECTURE -notin @("AMD64", "x86") -or
    -not [Environment]::Is64BitOperatingSystem) {
    Write-Fail "TermWrap ships x64/x86 payloads only; this OS is $env:PROCESSOR_ARCHITECTURE."
    Write-Host "  MultiSeat requires 64-bit x86 Windows." -ForegroundColor Yellow
    exit 1
}

# Home and Server editions additionally need UmWrap/EndpWrap per upstream; Pro does not.
$edition   = (Get-CimInstance Win32_OperatingSystem).Caption
$needUmWrap = $edition -match "Home|Server"
Write-Host "  Edition: $edition ($(if ($needUmWrap) { 'UmWrap required' } else { 'TermWrap only' }))" -ForegroundColor DarkGray

# ── Already installed? ───────────────────────────────────────────────────────
# Skip the install work, but still re-assert the settings below: fDenyTSConnections and
# friends are exactly the values that get reset out from under a working install, so a
# re-run that only looked and reported would be useless precisely when it is needed.
Write-Step "Checking for an existing TermWrap install..."
$currentDll = (Get-ItemProperty $ParametersKey -Name ServiceDll -ErrorAction SilentlyContinue).ServiceDll
$currentDllResolved = if ($currentDll) { [Environment]::ExpandEnvironmentVariables($currentDll) } else { $null }
$alreadyInstalled = $currentDllResolved -and
                    (Split-Path $currentDllResolved -Leaf) -ieq "TermWrap.dll" -and
                    (Test-Path $currentDllResolved)

if ($alreadyInstalled) {
    Write-OK "TermWrap already active (ServiceDll: $currentDllResolved)"
    Write-Host "  Skipping download and install — re-asserting settings and verifying." -ForegroundColor DarkGray
} else {
    if ($currentDllResolved) {
        Write-Host "  ServiceDll currently: $currentDllResolved" -ForegroundColor DarkGray
    } else {
        Write-Host "  ServiceDll not set (stock Terminal Services)" -ForegroundColor DarkGray
    }

    # ── 2. VC++ 2015–2022 x64 redistributable ────────────────────────────────
    # TermWrap 0.6's release notes say it now links against msvcrt.dll instead of the STL, so
    # this may no longer be a hard dependency — but the check is cheap and a missing runtime
    # presents as TermService failing to load the DLL with no useful message. Non-fatal: the
    # verification block at the end is the real gate.
    Write-Step "Checking the VC++ 2015-2022 x64 redistributable..."
    if (Test-Path (Join-Path $env:SystemRoot "System32\vcruntime140.dll")) {
        Write-OK "vcruntime140.dll present"
    } else {
        Write-Host "  vcruntime140.dll not found — installing via winget..." -ForegroundColor White
        if (Get-Command winget -ErrorAction SilentlyContinue) {
            & winget install --id Microsoft.VCRedist.2015+.x64 --accept-source-agreements --accept-package-agreements --silent 2>&1 |
                ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
            if (Test-Path (Join-Path $env:SystemRoot "System32\vcruntime140.dll")) {
                Write-OK "VC++ redistributable installed"
            } else {
                Write-Warn "winget finished but vcruntime140.dll is still absent — continuing; verification will catch a real failure."
            }
        } else {
            Write-Warn "winget not available. Install the VC++ 2015-2022 x64 redistributable manually if verification fails:"
            Write-Host "    https://aka.ms/vs/17/release/vc_redist.x64.exe" -ForegroundColor Yellow
        }
    }

    # ── 3. Remove RDP Wrapper ────────────────────────────────────────────────
    # MUST run before step 6. RDPWInst -u sets fDenyTSConnections = 1 (Remote Desktop off
    # entirely), which step 6 then clears. Reordering these silently leaves RDP disabled.
    Write-Step "Removing RDP Wrapper if present..."
    $rdpwInst = Join-Path $InstallDir "RDPWInst.exe"
    if (Test-Path $rdpwInst) {
        Write-Host "  Found $rdpwInst — running RDPWInst.exe -u" -ForegroundColor White
        Write-Host "  (this is what sets fDenyTSConnections = 1; corrected below)" -ForegroundColor DarkGray
        try {
            Start-Process $rdpwInst -ArgumentList "-u" -Wait -NoNewWindow
            Write-OK "RDP Wrapper uninstalled"
        } catch {
            Write-Warn "RDPWInst -u failed: $_ — continuing, TermWrap replaces it via ServiceDll anyway"
        }
    } else {
        Write-OK "RDP Wrapper not installed — nothing to remove"
    }

    # ── 4. Download + extract ────────────────────────────────────────────────
    Write-Step "Downloading TermWrap..."
    $releaseApi = "https://api.github.com/repos/llccd/TermWrap/releases/latest"
    $zipDest    = Join-Path $ScriptDir "TermWrap.zip"

    $localZip = Get-ChildItem $ScriptDir -Filter "TermWrap*.zip" -ErrorAction SilentlyContinue |
                Select-Object -First 1
    if ($localZip) {
        $zipDest = $localZip.FullName
        Write-Host "  Found locally: $($localZip.Name)" -ForegroundColor DarkGray
    } elseif ($SkipDownload) {
        Write-Fail "-SkipDownload specified and no TermWrap zip found in $ScriptDir."
        exit 1
    } else {
        try {
            Write-Host "  Fetching latest release info from GitHub..." -ForegroundColor White
            $ProgressPreference = 'SilentlyContinue'
            $release = Invoke-RestMethod -Uri $releaseApi -UseBasicParsing -Headers @{ "User-Agent" = "MultiSeat-Prereq" }
            $asset   = $release.assets | Where-Object { $_.name -match "\.zip$" } | Select-Object -First 1
            if (-not $asset) {
                Write-Fail "No zip asset in the latest TermWrap release. Check https://github.com/llccd/TermWrap/releases"
                exit 1
            }
            $zipDest = Join-Path $ScriptDir $asset.name
            Write-Host "  Downloading $($asset.name) ($($release.tag_name))..." -ForegroundColor White
            Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipDest -UseBasicParsing
            $ProgressPreference = 'Continue'
            Write-OK "Downloaded $($asset.name)"
        } catch {
            Write-Fail "Download failed: $_"
            Write-Host "  Manual steps: download the latest zip from" -ForegroundColor Yellow
            Write-Host "  https://github.com/llccd/TermWrap/releases into $ScriptDir," -ForegroundColor Yellow
            Write-Host "  then re-run this script with -SkipDownload." -ForegroundColor Yellow
            exit 1
        }
    }

    Write-Step "Extracting..."
    $extractDir = Join-Path $env:TEMP "MultiSeat_TermWrap_Install"
    if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
    Expand-Archive $zipDest -DestinationPath $extractDir -Force
    Write-OK "Extracted to $extractDir"

    # ── 5. Copy the x64 DLLs ─────────────────────────────────────────────────
    # Select by an explicit x64 path segment. Never "first match" — the zip carries x86 and
    # x64 trees with identical filenames, and picking the wrong one yields a TermService that
    # starts but never binds 3389.
    Write-Step "Installing DLLs to $InstallDir..."

    $dllNames = @("TermWrap.dll", "Zydis.dll")
    if ($needUmWrap) { $dllNames += @("UmWrap.dll", "EndpWrap.dll") }

    if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }

    foreach ($name in $dllNames) {
        $src = Get-ChildItem $extractDir -Recurse -Filter $name -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -match '[\\/]x64[\\/]' } |
               Select-Object -First 1
        if (-not $src) {
            Write-Fail "No x64 $name in the release zip. Contents:"
            Get-ChildItem $extractDir -Recurse -File |
                ForEach-Object { Write-Host "    $($_.FullName.Substring($extractDir.Length + 1))" -ForegroundColor DarkGray }
            Write-Host "  Refusing to fall back to a non-x64 payload — it cannot load into TermService." -ForegroundColor Yellow
            exit 1
        }
        Copy-Item $src.FullName (Join-Path $InstallDir $name) -Force
        Write-OK "$name ($([math]::Round($src.Length / 1KB)) KB) -> $InstallDir"
    }

    # Keep the .reg files beside the DLLs so rollback needs no re-download.
    foreach ($reg in Get-ChildItem $extractDir -Recurse -Filter "*.reg") {
        Copy-Item $reg.FullName (Join-Path $InstallDir $reg.Name) -Force
    }
    Write-Host "  Copied the .reg files (rollback: Revert_to_default.reg)" -ForegroundColor DarkGray

    # ── 6a. Merge the registry file ──────────────────────────────────────────
    $regName = if ($needUmWrap) { "Install_termwrap_umwrap.reg" } else { "Install_termwrap_only.reg" }
    Write-Step "Merging $regName..."
    $regFile = Join-Path $InstallDir $regName
    if (-not (Test-Path $regFile)) {
        Write-Fail "$regName not found in the release zip."
        exit 1
    }
    & reg.exe import "$regFile" 2>&1 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "reg import returned exit code $LASTEXITCODE — ServiceDll was not repointed."
        exit 1
    }
    Write-OK "Registry merged (ServiceDll -> %ProgramFiles%\RDP Wrapper\TermWrap.dll)"
}

# ── 6b. Assert what upstream's install does not ──────────────────────────────
# Runs on both paths — fresh install and re-run. These are the values that get reset out
# from under a working install, so re-asserting them is the point of a re-run.
Write-Step "Asserting Terminal Services settings..."

function Set-TsValue($Path, $Name, $Value, $Why) {
    $before = (Get-ItemProperty $Path -Name $Name -ErrorAction SilentlyContinue).$Name
    $shown  = if ($null -eq $before) { "(unset)" } else { $before }
    if ($before -eq $Value) {
        Write-Host "  $Name = $shown (already correct)" -ForegroundColor DarkGray
        return
    }
    if (-not (Test-Path $Path)) { New-Item $Path -Force | Out-Null }
    Set-ItemProperty $Path -Name $Name -Value $Value -Type DWord
    Write-Host "  $Name : $shown -> $Value   [$Why]" -ForegroundColor White
}

Set-TsValue $TermServKey "fDenyTSConnections"    0 "Remote Desktop must be enabled at all"
Set-TsValue $TermServKey "fSingleSessionPerUser" 0 "otherwise mstsc reconnects instead of creating a session"
Set-TsValue $RdpTcpKey   "UserAuthentication"    0 "MultiSeat requires NLA off"

$svc = Get-Service TermService
if ($svc.StartType -ne "Automatic") {
    Write-Host "  TermService StartType : $($svc.StartType) -> Automatic" -ForegroundColor White
    Set-Service TermService -StartupType Automatic
} else {
    Write-Host "  TermService StartType = Automatic (already correct)" -ForegroundColor DarkGray
}

# ── 7. Restart TermService ───────────────────────────────────────────────────
Write-Step "Restarting TermService..."
$restartOk = $false
try {
    # -Force restarts dependent services (UmRdpService) rather than refusing.
    Restart-Service TermService -Force -ErrorAction Stop
    Start-Sleep -Seconds 2
    $restartOk = $true
    Write-OK "TermService restarted"
} catch {
    Write-Warn "Could not restart TermService: $_"
    Write-Host "  The configuration below is still correct; a reboot will load the new DLL." -ForegroundColor Yellow
}

# ── 8. Verify ────────────────────────────────────────────────────────────────
# Check the end state. Having run the steps is not evidence that they worked — the whole
# reason this script exists is that the documented steps left a broken install.
Write-Step "Verifying..."
$failures = @()

function Test-Check($Label, [bool]$Passed, $Detail) {
    if ($Passed) {
        Write-Host ("  [PASS] {0,-32} {1}" -f $Label, $Detail) -ForegroundColor Green
    } else {
        Write-Host ("  [FAIL] {0,-32} {1}" -f $Label, $Detail) -ForegroundColor Red
        $script:failures += $Label
    }
}

# ServiceDll resolves to TermWrap.dll
$dll = (Get-ItemProperty $ParametersKey -Name ServiceDll -ErrorAction SilentlyContinue).ServiceDll
$dllResolved = if ($dll) { [Environment]::ExpandEnvironmentVariables($dll) } else { "(unset)" }
Test-Check "ServiceDll -> TermWrap.dll" `
    ($dll -and (Split-Path $dllResolved -Leaf) -ieq "TermWrap.dll") $dllResolved

# Both DLLs on disk — Zydis is not optional: without it TermWrap cannot disassemble
# termsrv.dll, and TermService starts but never binds 3389.
foreach ($name in @("TermWrap.dll", "Zydis.dll")) {
    $path = Join-Path $InstallDir $name
    Test-Check "$name present" (Test-Path $path) $path
}

# TermService Running + Automatic
$svc = Get-Service TermService
Test-Check "TermService Running"    ($svc.Status -eq "Running")      $svc.Status
Test-Check "TermService Automatic"  ($svc.StartType -eq "Automatic") $svc.StartType

# Something is listening on 3389
$listener = Get-NetTCPConnection -LocalPort 3389 -State Listen -ErrorAction SilentlyContinue
Test-Check "Listening on TCP 3389" ([bool]$listener) `
    $(if ($listener) { "$(($listener | Select-Object -First 1).LocalAddress):3389" } else { "nothing bound" })

# The three settings upstream does not set
foreach ($chk in @(
    @{ Key = $TermServKey; Name = "fDenyTSConnections" },
    @{ Key = $TermServKey; Name = "fSingleSessionPerUser" },
    @{ Key = $RdpTcpKey;   Name = "UserAuthentication" }
)) {
    $val = (Get-ItemProperty $chk.Key -Name $chk.Name -ErrorAction SilentlyContinue).$($chk.Name)
    Test-Check "$($chk.Name) = 0" ($val -eq 0) $(if ($null -eq $val) { "(unset)" } else { $val })
}

# ── Result ───────────────────────────────────────────────────────────────────
Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "[TermWrap] FAILED — $($failures.Count) check(s) did not pass:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host ""
    Write-Host "  If TermWrap will not work on this host, roll back with:" -ForegroundColor Yellow
    Write-Host "    reg import `"$InstallDir\Revert_to_default.reg`"   (stock Terminal Services)" -ForegroundColor Yellow
    Write-Host "    reg import `"$InstallDir\Revert_to_rdpwrap.reg`"   (back to RDP Wrapper)" -ForegroundColor Yellow
    Write-Host "  then reboot and delete TermWrap.dll / Zydis.dll from $InstallDir." -ForegroundColor Yellow
    exit 1
}

Write-Host "[TermWrap] All checks passed." -ForegroundColor Green
Write-Host ""
Write-Host "  Multi-session is patched by TermWrap, which resolves its offsets by" -ForegroundColor Green
Write-Host "  disassembling termsrv.dll — no rdpwrap.ini, and no breakage when a Windows" -ForegroundColor Green
Write-Host "  cumulative update ships a new termsrv.dll." -ForegroundColor Green
Write-Host ""
if (-not $SkipReboot) {
    if ($restartOk) {
        Write-Host "  A reboot is still recommended — upstream requires one, and this host needed" -ForegroundColor Yellow
        Write-Host "  one before the patch behaved. Verify by provisioning two seats afterwards." -ForegroundColor Yellow
    } else {
        Write-Host "  REBOOT REQUIRED — TermService could not be restarted, so the new DLL is not" -ForegroundColor Yellow
        Write-Host "  loaded yet. Reboot, then provision two seats to verify." -ForegroundColor Yellow
    }
    Write-Host ""
}
exit 0
