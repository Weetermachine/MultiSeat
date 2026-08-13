#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the MultiSeat Service as a Windows Service.
.DESCRIPTION
    Publishes the MultiSeat.Service project, copies the InputHook DLL,
    creates the required data directories, and registers the Windows service.
.PARAMETER Uninstall
    Remove the service and clean up.
#>
param(
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$ServiceName = "MultiSeatService"
$DisplayName = "MultiSeat Multi-Seat Streaming Service"
$Description = "Manages multi-seat headless game streaming sessions with isolated input, audio, and display."
$InstallDir = "C:\Program Files\MultiSeat"
$DataDir = "C:\ProgramData\MultiSeat"
$ProjectDir = Join-Path $PSScriptRoot "..\src\MultiSeat.Service"
$InputHookBuild = Join-Path $PSScriptRoot "..\src\MultiSeat.InputHook\build\Release\MultiSeatInputHook.dll"

function Write-Step($msg) { Write-Host "[MultiSeat] $msg" -ForegroundColor Cyan }

# -- Uninstall -------------------------------------------------------
if ($Uninstall) {
    Write-Step "Stopping service..."
    $svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.Status -eq 'Running') {
            Stop-Service $ServiceName -Force
            Write-Step "Service stopped"
        }
        sc.exe delete $ServiceName | Out-Null
        Write-Step "Service removed"
    } else {
        Write-Step "Service not found -- nothing to remove"
    }
    Write-Host "`nTo fully clean up, manually delete:" -ForegroundColor Yellow
    Write-Host "  $InstallDir"
    Write-Host "  $DataDir"
    return
}

# -- Prerequisites check ---------------------------------------------
Write-Step "Checking prerequisites..."
$missing = @()
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { $missing += ".NET SDK" }
# Detect HidHide the same way the service does — via its driver service or the CLI on disk.
# The old check keyed off an "HKLM:\SOFTWARE\Nefarius Software Solutions\HidHide" registry key
# that HidHide 1.5.x doesn't reliably create, so it warned "not detected" even when HidHide was
# fully installed (issue #9).
$hidHideCli = "C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe"
if (!(Get-Service -Name "HidHide" -ErrorAction SilentlyContinue) -and !(Test-Path $hidHideCli)) {
    Write-Warning "HidHide not detected -- controller hiding will be unavailable"
}
if ($missing.Count -gt 0) {
    throw "Missing prerequisites: $($missing -join ', ')"
}

# -- RDP configuration -----------------------------------------------
# These settings are also applied by prerequisites\install-prerequisites.ps1.
# Re-applying here ensures a fresh service deploy always has the correct RDP
# configuration, even if the prereq script was run before these settings existed
# or was skipped entirely.

Write-Step "Verifying RDP configuration..."

# Enable Remote Desktop (fDenyTSConnections = 0)
$tsKey = "HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server"
if ((Get-ItemProperty $tsKey -Name "fDenyTSConnections" -ErrorAction SilentlyContinue).fDenyTSConnections -ne 0) {
    Set-ItemProperty $tsKey -Name "fDenyTSConnections" -Value 0
    Enable-NetFirewallRule -DisplayGroup "Remote Desktop" -ErrorAction SilentlyContinue
    Write-Host "  Applied: Remote Desktop enabled" -ForegroundColor Green
} else {
    Write-Host "  OK: Remote Desktop enabled" -ForegroundColor DarkGray
}

# Disable NLA (UserAuthentication=0) and enable TLS (SecurityLayer=2) on the RDP listener.
# SecurityLayer=2 makes TermService generate a self-signed TLS cert (SSLCertificateSHA1Hash),
# which TrustRdpLoopbackServer reads and writes to the console user's HKCU trust store so
# mstsc never shows "Do you trust this remote connection?" for 127.0.0.2.
$rdpTcpKey = "HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp"
$rdpTcpProps = Get-ItemProperty $rdpTcpKey -ErrorAction SilentlyContinue
$rdpChanged = $false
if ($rdpTcpProps.UserAuthentication -ne 0) {
    Set-ItemProperty $rdpTcpKey -Name "UserAuthentication" -Value 0
    $rdpChanged = $true
}
if ($rdpTcpProps.SecurityLayer -ne 2) {
    Set-ItemProperty $rdpTcpKey -Name "SecurityLayer" -Value 2
    $rdpChanged = $true
}
if ($rdpChanged) {
    Restart-Service -Name "TermService" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Write-Host "  Applied: NLA disabled, SecurityLayer=2 (TLS cert trust enabled)" -ForegroundColor Green
} else {
    Write-Host "  OK: NLA disabled, SecurityLayer=2" -ForegroundColor DarkGray
}

# Pre-trust 127.0.0.2 in the current user's HKCU so mstsc never shows "Do you trust
# this remote connection?" for loopback provisioning connections.
# TermService stores its self-signed TLS cert in Cert:\LocalMachine\Remote Desktop.
$rdpCert = Get-ChildItem 'Cert:\LocalMachine\Remote Desktop' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($rdpCert) {
    $trustKey = "HKCU:\Software\Microsoft\Terminal Server Client\Servers\127.0.0.2"
    if (-not (Test-Path $trustKey)) { New-Item $trustKey -Force | Out-Null }
    Set-ItemProperty $trustKey -Name "CertHash" -Value $rdpCert.GetCertHash() -Type Binary
    Set-ItemProperty $trustKey -Name "UsernameHint" -Value "" -Type String
    Write-Host "  Applied: 127.0.0.2 trusted in HKCU (thumbprint: $($rdpCert.Thumbprint))" -ForegroundColor Green
} else {
    Write-Host "  WARNING: No RDP TLS cert found in Cert:\LocalMachine\Remote Desktop -- mstsc trust dialog may appear" -ForegroundColor Yellow
}

# Suppress the RDP client certificate trust dialog (AuthenticationLevel = 0 machine policy).
# MultiSeat launches mstsc via CreateProcessAsUser with no interactive user to click dialogs.
# This is safe because MultiSeat only ever connects to 127.0.0.2 (local loopback).
$rdpClientKey = "HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services"
if (-not (Test-Path $rdpClientKey)) { New-Item $rdpClientKey -Force | Out-Null }
if ((Get-ItemProperty $rdpClientKey -Name "AuthenticationLevel" -ErrorAction SilentlyContinue).AuthenticationLevel -ne 0) {
    Set-ItemProperty $rdpClientKey -Name "AuthenticationLevel" -Value 0 -Type DWord
    Write-Host "  Applied: RDP client cert dialog suppressed" -ForegroundColor Green
} else {
    Write-Host "  OK: RDP client cert dialog suppressed" -ForegroundColor DarkGray
}

# Allow unsigned .rdp files without showing "publisher cannot be identified" warning.
# MultiSeat uses a connection.rdp in C:\ProgramData\MultiSeat\ which is not digitally signed.
if ((Get-ItemProperty $rdpClientKey -Name "AllowUnsignedFiles" -ErrorAction SilentlyContinue).AllowUnsignedFiles -ne 1) {
    Set-ItemProperty $rdpClientKey -Name "AllowUnsignedFiles" -Value 1 -Type DWord
    Write-Host "  Applied: unsigned .rdp file warning suppressed" -ForegroundColor Green
} else {
    Write-Host "  OK: unsigned .rdp file warning suppressed" -ForegroundColor DarkGray
}

# Allow audio capture redirection (audiocapturemode:i:1) without showing a consent dialog.
# Without this, mstsc shows a device redirection trust prompt that blocks headless provisioning.
if ((Get-ItemProperty $rdpClientKey -Name "fDisableAudioCapture" -ErrorAction SilentlyContinue).fDisableAudioCapture -ne 0) {
    Set-ItemProperty $rdpClientKey -Name "fDisableAudioCapture" -Value 0 -Type DWord
    Write-Host "  Applied: audio capture redirection allowed (no consent dialog)" -ForegroundColor Green
} else {
    Write-Host "  OK: audio capture redirection allowed" -ForegroundColor DarkGray
}

# Pre-authorize mstsc's device-redirection consent for 127.0.0.2.
#
# LocalDevices is a SUBKEY holding one REG_DWORD per server name — not a value on the
# parent key. This used to write "LocalDevices" as a DWORD directly under Terminal Server
# Client, which Windows never reads, so the pre-authorization this step reports as applied
# had no effect at all; the consent list appeared on every connection regardless.
#
# The bitmask 0x7FFFFFFF covers every redirection class (audio, drives, printers, smart
# cards, clipboard, ...).
$mstscLocalDevices = "HKCU:\Software\Microsoft\Terminal Server Client\LocalDevices"
if (-not (Test-Path $mstscLocalDevices)) { New-Item $mstscLocalDevices -Force | Out-Null }
if ((Get-ItemProperty $mstscLocalDevices -Name "127.0.0.2" -ErrorAction SilentlyContinue)."127.0.0.2" -ne 0x7FFFFFFF) {
    Set-ItemProperty $mstscLocalDevices -Name "127.0.0.2" -Value 0x7FFFFFFF -Type DWord
    Write-Host "  Applied: mstsc device redirection pre-authorized for 127.0.0.2" -ForegroundColor Green
} else {
    Write-Host "  OK: mstsc device redirection pre-authorized for 127.0.0.2" -ForegroundColor DarkGray
}

# Remove the ineffective value the old code wrote, so it stops looking like protection
# that is in place. Nothing reads it.
$mstscClientKey = "HKCU:\Software\Microsoft\Terminal Server Client"
if ($null -ne (Get-ItemProperty $mstscClientKey -Name "LocalDevices" -ErrorAction SilentlyContinue)) {
    Remove-ItemProperty $mstscClientKey -Name "LocalDevices" -ErrorAction SilentlyContinue
    Write-Host "  Cleaned: removed the old no-op LocalDevices value" -ForegroundColor DarkGray
}

# NOTE: none of the settings above suppress mstsc's "Unknown remote connection / we could
# not verify the publisher" security warning, which is a separate dialog. AllowUnsignedFiles
# above does not stop it on Windows 11 either — verified 2026-08-07, the warning still
# appeared with that policy set to 1. That is why SessionLauncher falls back to dismissing
# the dialog with SendKeys, and why it still reaches the user when the dismisser mistimes it.
# The real fix is signing the generated .rdp with rdpsign.exe and trusting the thumbprint
# via the TrustedCertThumbprints policy. Not done yet.

# -- SudoVDA check ---------------------------------------------------
# SudoVDA is required for per-seat virtual display isolation.
# Without it, Apollo captures the primary physical display and all seats
# share the same view. Warn loudly here; the prereq script installs it.
Write-Step "Checking SudoVDA (virtual display driver)..."

# Detect SudoVDA the same way the running service does
# (VirtualDisplayManager.IsSudoVdaAdapterPresent): a ROOT\DISPLAY device whose
# DeviceDesc or HardwareID actually names SudoMaker/SudoVDA.
#
# The previous check matched FriendlyName against "VDD|Virtual Display|SudoVDA|MTT",
# which matches ANY virtual display driver -- "USB Mobile Monitor Virtual Display",
# the MTT "Virtual Display Driver", etc. It therefore reported "SudoVDA detected"
# on machines with no SudoVDA at all, naming whichever unrelated adapter it hit,
# and contradicted the check immediately below it (issue #14).
function Test-SudoVdaPresent {
    $root = 'HKLM:\SYSTEM\CurrentControlSet\Enum\ROOT\DISPLAY'
    if (-not (Test-Path $root)) { return $null }
    foreach ($k in (Get-ChildItem $root -ErrorAction SilentlyContinue)) {
        $props = Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue
        $desc  = [string]$props.DeviceDesc
        $hwIds = (@($props.HardwareID) -join ';')
        if ($desc -match 'SudoMaker|SudoVDA' -or $hwIds -match 'SudoMaker|SudoVDA') {
            return [PSCustomObject]@{ Key = $k.PSChildName; Desc = $desc }
        }
    }
    return $null
}

$sudovdaDevice = Test-SudoVdaPresent
if ($sudovdaDevice) {
    Write-Host "  OK: SudoVDA detected (ROOT\DISPLAY\$($sudovdaDevice.Key))" -ForegroundColor DarkGray
} else {
    Write-Host ""
    Write-Host "  *** WARNING: SudoVDA virtual display driver is NOT installed. ***" -ForegroundColor Yellow
    Write-Host "  Seats will launch in degraded mode — Apollo will capture the" -ForegroundColor Yellow
    Write-Host "  physical display instead of an isolated virtual display." -ForegroundColor Yellow
    Write-Host "  Run prerequisites\install-prerequisites.ps1 to install SudoVDA." -ForegroundColor Yellow
    Write-Host ""
}

# -- Persistent VDD service start (OPTIONAL, not SudoVDA) -----------------------
# "VirtualDisplayDriver" is the service belonging to the OPTIONAL MttVDD persistent
# virtual display driver (VirtualDrivers/Virtual-Display-Driver), used only so a
# headless machine has a console-session display at boot. SudoVDA does NOT register
# this service, so its absence says nothing about SudoVDA.
#
# The old message here told the user to install SudoVDA when this service was
# missing, which directly contradicted the SudoVDA result printed above and sent
# people chasing a driver that was already fine (issue #14). Start it if present;
# otherwise say what it actually is.
$vddSvc = Get-Service -Name "VirtualDisplayDriver" -ErrorAction SilentlyContinue
if ($vddSvc) {
    if ($vddSvc.Status -eq 'Running') {
        Write-Host "  OK: VirtualDisplayDriver running" -ForegroundColor DarkGray
    } else {
        Write-Step "Starting VirtualDisplayDriver service (virtual displays)..."
        try {
            Start-Service "VirtualDisplayDriver" -ErrorAction Stop
            Write-Host "  OK: VirtualDisplayDriver started" -ForegroundColor Green
        } catch {
            Write-Host "  WARNING: Could not start VirtualDisplayDriver: $_" -ForegroundColor Yellow
            Write-Host "  Virtual displays may be unavailable. Reboot may be required." -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "  Note: optional persistent VDD (MttVDD) not installed — only needed so a" -ForegroundColor DarkGray
    Write-Host "        headless machine has a display at boot. Unrelated to SudoVDA." -ForegroundColor DarkGray
}

# -- VoiceMeeter start ---------------------------------------------------------
# VoiceMeeter Potato must be running for its virtual audio devices (VoiceMeeter
# Input, Aux Input, VAIO3) to route audio. It is registered in HKLM\Run so it
# auto-starts at user login, but may not be running if the session just booted or
# if the service is being re-deployed without a user login in between.
# Start it here (from the admin session, NOT Session 0) before the MultiSeat
# service begins — the service's AudioRouter will also try to start it, but
# launching a GUI app from SYSTEM/Session 0 is unreliable.
$vmExe = $null
foreach ($candidate in @(
    "C:\Program Files\VB\Voicemeeter\voicemeeterpro.exe",
    "C:\Program Files (x86)\VB\Voicemeeter\voicemeeterpro.exe"
)) {
    if (Test-Path $candidate) { $vmExe = $candidate; break }
}
if ($vmExe) {
    $vmRunning = Get-Process -Name "voicemeeterpro" -ErrorAction SilentlyContinue
    if ($vmRunning) {
        Write-Host "  OK: VoiceMeeter Potato already running" -ForegroundColor DarkGray
    } else {
        Write-Step "Starting VoiceMeeter Potato (audio routing)..."
        Start-Process $vmExe -WindowStyle Minimized
        Start-Sleep -Seconds 3
        $vmRunning = Get-Process -Name "voicemeeterpro" -ErrorAction SilentlyContinue
        if ($vmRunning) {
            Write-Host "  OK: VoiceMeeter Potato started" -ForegroundColor Green
        } else {
            Write-Host "  WARNING: VoiceMeeter may not have started. Check manually." -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "  NOTE: VoiceMeeter not found - audio isolation unavailable. Run prerequisites\install-prerequisites.ps1." -ForegroundColor Yellow
}

# -- Standalone Apollo coexistence --------------------------------------------
# MultiSeat installs and manages its OWN Apollo (ApolloVibe at C:\Program Files\ApolloVibe)
# on a non-overlapping port block (PortBase 48100+), so it coexists with a standalone Apollo
# the user may run for their main console. We intentionally leave any default ApolloService
# alone — it is NOT stopped or disabled.
Write-Host "  OK: leaving any standalone ApolloService untouched (MultiSeat uses its own Apollo + ports)" -ForegroundColor DarkGray

# -- Stop service before publish so its DLLs are not locked ----------
$svcBeforePublish = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($svcBeforePublish -and $svcBeforePublish.Status -eq 'Running') {
    Write-Step "Stopping service before publish..."
    Stop-Service $ServiceName -Force
    try { $svcBeforePublish.WaitForStatus('Stopped', (New-TimeSpan -Seconds 15)) }
    catch { Write-Warning "SCM stop timed out -- will force-kill the process" }
}

# Kill any surviving process running the service exe by path
# (handles cases where the process outlives the SCM status change)
$serviceExe = Join-Path $InstallDir "MultiSeat.Service.exe"
$lingering = Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $serviceExe }
foreach ($proc in $lingering) {
    Write-Step "Force-killing lingering process PID $($proc.Id)..."
    $proc | Stop-Process -Force
    $proc.WaitForExit(10000)
}

# Brief pause to let the OS release all file handles before publish
Start-Sleep -Milliseconds 500
Write-Step "Service stopped and file handles released"

# -- Publish ----------------------------------------------------------
# Preserve the installed appsettings.json across the publish. `dotnet publish` copies the
# repo's copy into the output directory, silently reverting local edits — on this host that
# meant ApolloExePath pointing back at the repo default instead of the Apollo actually
# installed, so seats came up with the wrong binary. Restore it afterwards; a genuinely new
# install has no file to preserve and gets the repo's copy as before.
$installedSettings = Join-Path $InstallDir "appsettings.json"
$settingsBackup = $null
if (Test-Path $installedSettings) {
    $settingsBackup = Join-Path $env:TEMP "multiseat-appsettings-$(Get-Date -Format yyyyMMdd-HHmmss).json"
    Copy-Item $installedSettings $settingsBackup -Force
    Write-Step "Preserved existing appsettings.json (backup: $settingsBackup)"
}

Write-Step "Publishing MultiSeat.Service..."
dotnet publish $ProjectDir -c Release -o "$InstallDir" --no-self-contained 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed"
}

if ($settingsBackup) {
    Copy-Item $settingsBackup $installedSettings -Force
    Write-Step "Restored the existing appsettings.json over the published copy"
}

# -- Build InputHook DLL ----------------------------------------------
# OPTIONAL component. MSYS2 is a developer dependency, not a MultiSeat prerequisite, and
# EnableKeyboardMouseIsolation is OFF by default (the hook is a no-op as architected — see
# MultiSeatOptions.EnableKeyboardMouseIsolation). A missing DLL changes nothing about how
# MultiSeat runs, so these are informational notes, NOT warnings: emitting WARNING here made
# a normal install look broken and got reported as a bug (issue #14).
$InputHookSrc = Join-Path $PSScriptRoot "..\src\MultiSeat.InputHook"
$Bash = "C:\msys64\usr\bin\bash.exe"
if (Test-Path $Bash) {
    Write-Step "Building MultiSeatInputHook.dll..."
    $srcUnix = ($InputHookSrc -replace '\\', '/') -replace '^([A-Z]):', { "/$(([string]$_[0]).ToLower())" }
    $buildScript = "export PATH='/ucrt64/bin:`$PATH'; cmake -B '$srcUnix/build/Release' -S '$srcUnix' -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_CXX_COMPILER=/ucrt64/bin/g++.exe -DCMAKE_MAKE_PROGRAM=/ucrt64/bin/ninja.exe && cmake --build '$srcUnix/build/Release'"
    & $Bash -lc $buildScript 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  Note: InputHook build failed -- skipping (optional, off by default)" -ForegroundColor DarkGray
    }
} else {
    Write-Host "  Note: MSYS2 not present at C:\msys64 -- skipping optional InputHook build" -ForegroundColor DarkGray
}

# -- Copy InputHook DLL -----------------------------------------------
if (Test-Path $InputHookBuild) {
    Copy-Item $InputHookBuild "$InstallDir\MultiSeatInputHook.dll" -Force
    Write-Step "Copied MultiSeatInputHook.dll"
} else {
    Write-Host "  Note: MultiSeatInputHook.dll not built -- keyboard/mouse isolation stays unavailable." -ForegroundColor DarkGray
    Write-Host "        This is expected and safe: the feature is off by default and currently inert." -ForegroundColor DarkGray
}

# -- Build and deploy Dashboard ---------------------------------------
$DashboardDir = Join-Path $PSScriptRoot "..\src\MultiSeat.Dashboard"
if (Test-Path (Join-Path $DashboardDir "package.json")) {
    Write-Step "Building dashboard..."
    Push-Location $DashboardDir
    try {
        # Install npm dependencies if node_modules is missing or incomplete
        $nodeModules = Join-Path $DashboardDir "node_modules"
        $viteMarker  = Join-Path $nodeModules "vite\bin\vite.js"
        if (-not (Test-Path $viteMarker)) {
            Write-Step "Installing dashboard npm dependencies..."
            $nodeExe = (Get-Command node -ErrorAction SilentlyContinue)?.Source
            if (-not $nodeExe) { $nodeExe = "C:\Program Files\nodejs\node.exe" }
            if (-not (Test-Path $nodeExe)) { throw "node.exe not found. Install Node.js first." }
            $result = Start-Process $nodeExe -ArgumentList "install.cjs" `
                -Wait -NoNewWindow -PassThru -WorkingDirectory $DashboardDir
            if ($result.ExitCode -ne 0) { throw "npm install failed (exit $($result.ExitCode))" }
        }

        & cmd /c "$DashboardDir\build.bat"
        if ($LASTEXITCODE -ne 0) { throw "Dashboard build failed" }
        $distDir = Join-Path $DashboardDir "dist"
        if (Test-Path $distDir) {
            $wwwroot = Join-Path $InstallDir "wwwroot"
            if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }
            Copy-Item $distDir $wwwroot -Recurse
            Write-Step "Dashboard deployed to $wwwroot"
        } else {
            Write-Warning "Dashboard dist/ not found after build"
        }
    } finally {
        Pop-Location
    }
} else {
    Write-Warning "Dashboard not found -- skipping"
}

# -- Create data directories ------------------------------------------
@("$DataDir", "$DataDir\apollo", "$DataDir\logs") | ForEach-Object {
    if (!(Test-Path $_)) {
        New-Item -ItemType Directory -Path $_ -Force | Out-Null
        Write-Step "Created $_"
    }
}

# -- Register Windows service ----------------------------------------
$exePath = Join-Path $InstallDir "MultiSeat.Service.exe"

$existing = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Step "Service already exists -- stopping and updating..."
    if ($existing.Status -eq 'Running') {
        Stop-Service $ServiceName -Force
    }
    sc.exe config $ServiceName binPath= "`"$exePath`"" start= auto | Out-Null
} else {
    Write-Step "Creating Windows service..."
    sc.exe create $ServiceName binPath= "`"$exePath`"" start= auto DisplayName= "`"$DisplayName`"" | Out-Null
    sc.exe description $ServiceName "`"$Description`"" | Out-Null
}

# Configure service recovery (restart on failure)
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Write-Step "Configured automatic restart on failure"

# -- Start ------------------------------------------------------------
Write-Step "Starting service..."
Start-Service $ServiceName
$svc = Get-Service $ServiceName
Write-Host "`n[MultiSeat] Service installed and $($svc.Status)!" -ForegroundColor Green
Write-Host "  Dashboard: http://localhost:9550"
Write-Host "  Logs:      Windows Event Log (Application / MultiSeat.Service) -- run scripts\show-logs.ps1"
Write-Host "  Config:    $InstallDir\appsettings.json"
