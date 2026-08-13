# MultiSeat Requirements

## Operating System

| Requirement | Details |
|-------------|---------|
| **Windows 11** | Build 26100+ (24H2) strongly recommended |
| **Windows 10** | Build 19041+ (2004) minimum — some features may be limited |
| **Windows Server** | Not officially tested |
| **Architecture** | x64 only |

> Windows 11 24H2 is recommended because it ships with the RdpIdd virtual display driver improvements that MultiSeat relies on for stable per-session display allocation.

---

## Required Software

### PowerShell 7+

The prerequisites and install scripts require **PowerShell 7** (pwsh). Windows PowerShell 5 is not supported.

Install via winget:
```powershell
winget install Microsoft.PowerShell
```
Or download from: https://github.com/PowerShell/PowerShell/releases

After installing, open **PowerShell 7** (search "pwsh" in Start) and run all MultiSeat scripts from there.

---

### .NET Runtime

| Component | Version | Notes |
|-----------|---------|-------|
| **.NET 9 Runtime** | 9.0+ | Required to run MultiSeat.Service |
| **.NET 9 ASP.NET Core Runtime** | 9.0+ | Included with the full runtime |

Download: https://dotnet.microsoft.com/download/dotnet/9.0

---

### Apollo (Sunshine Fork)

Apollo is a fork of Sunshine with support for running multiple instances simultaneously — one per seat.

| Requirement | Details |
|-------------|---------|
| **Apollo** | v0.4.6+ |
| **Install path** | `C:\Program Files\ApolloVibe\sunshine.exe` (configurable; separate from any standalone `C:\Program Files\Apollo`) |
| **Notes** | Do NOT use upstream Sunshine — it does not support multi-instance. MultiSeat installs and manages its own Apollo, so it coexists with a standalone Apollo on the same host. |

Download: https://github.com/ClassicOldSong/Apollo/releases

---

### Virtual Display Driver — SudoVDA

Each seat needs its own virtual display for the streaming encoder to capture. SudoVDA is bundled with Apollo and managed automatically — Apollo creates and destroys one virtual display per instance.

| Requirement | Details |
|-------------|---------|
| **SudoVDA** (bundled with Apollo) | Installed automatically with Apollo |
| **Minimum displays** | One virtual display per seat (Apollo manages these) |
| **Notes** | The display must be connected to an Active RDP session — do not disconnect mstsc |

---

### Headless Operation — Persistent Virtual Display

If the host machine has **no physical monitor** connected, Windows may not expose a GPU output for the GPU driver or remote access tools (AnyDesk, RDP) to use before any seat is provisioned.

Install a persistent virtual display that appears at boot, independent of any streaming client:

```powershell
.\prerequisites\install-virtual-display.ps1
```

This script downloads and installs the [Virtual Display Driver by itsmikethetech](https://github.com/itsmikethetech/Virtual-Display-Driver), a persistent IddCx virtual monitor that works on Windows 10/11 x64. A reboot may be required after installation.

Alternatively, download it manually: https://github.com/itsmikethetech/Virtual-Display-Driver/releases

> **Note:** This is separate from SudoVDA. SudoVDA (Apollo's driver) creates per-seat virtual displays only while Apollo is running. The persistent virtual display fills the gap when no seat is active, so the machine always has at least one display for remote access.

---

### Virtual Audio Devices — VB-CABLE + VoiceMeeter Potato

Each seat needs its own virtual audio device so audio is routed to the correct Moonlight client. MultiSeat uses VB-CABLE basic (seat 0) and VoiceMeeter Potato (seats 1–3). Both are free and auto-downloaded by the prerequisites script.

| Requirement | Details |
|-------------|---------|
| **VB-CABLE** (basic) | v4.5+ — provides 1 virtual audio device (seat 0) |
| **VoiceMeeter Potato** | v3.1+ — provides 3 virtual audio devices (seats 1–3) |
| **VoiceMeeter running** | Must be running for audio routing to work — auto-starts at boot after install |

Downloads:
- VB-CABLE: https://vb-audio.com/Cable/
- VoiceMeeter Potato: https://vb-audio.com/Voicemeeter/potato.htm

---

### HidHide

HidHide hides physical controllers from the host so that controller input is only visible to the correct seat's session.

| Requirement | Details |
|-------------|---------|
| **HidHide** | v1.5.230+ |
| **Install path** | `C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe` |

Download: https://github.com/nefarius/HidHide/releases

---

### ViGEmBus

ViGEmBus provides the virtual Xbox controller driver that MultiSeat uses to forward controller input to each seat's session.

| Requirement | Details |
|-------------|---------|
| **ViGEmBus** | v1.22.0+ |

Download: https://github.com/nefarius/ViGEmBus/releases

---

### Multi-Session Patch — TermWrap (recommended) or RDPWrap (legacy)

Windows Home and Pro editions normally allow only one concurrent interactive session. A shim must load in place of the stock `termsrv.dll` to lift that limit. Two products do this and MultiSeat works with either — it detects the patch as "TermService's `ServiceDll` is not the stock `termsrv.dll`", never by vendor filename.

| Requirement | Details |
|-------------|---------|
| **TermWrap** (recommended) | v0.6+ — finds its patch offsets by disassembling `termsrv.dll` with Zydis, so it needs no per-build config and **survives Windows updates** |
| **RDPWrap** (legacy) | v1.6.2+ — looks its offsets up in `rdpwrap.ini`, keyed by the exact `termsrv.dll` build, so **every cumulative update that ships a new one breaks multi-session** until a matching ini entry is published |
| **Required for** | Windows 10/11 Home and Pro |
| **Not needed for** | Windows Server (multi-session is built in) |

Install: `prerequisites\install-termwrap.ps1` (or `install-prerequisites.ps1`, which calls it by default; pass `-UseRdpWrapper` for the legacy path).

Downloads: https://github.com/llccd/TermWrap · https://github.com/stascorp/rdpwrap

**Two gotchas that are not in TermWrap's own documentation.** Following its README's four steps exactly left RDP completely broken on the development host, because two registry values under `HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server` were wrong afterwards and TermWrap's `.reg` sets neither:

| Value | Broken state | Symptom |
|---|---|---|
| `fDenyTSConnections` | `1` | Remote Desktop switched off entirely — nothing binds 3389 no matter what TermService does. Set by RDP Wrapper's uninstaller, so it bites precisely when migrating. |
| `fSingleSessionPerUser` | `1` | RDP listens, but `mstsc /v:127.0.0.2` opens a window that closes immediately and MultiSeat logs *"the connection may have reconnected an existing session instead of creating a new one"*. This is RDPConf's "Single session per user" checkbox. |

`TermService` was also left `Stopped`/`Manual` and had to be started and set to `Automatic`. `install-termwrap.ps1` asserts all three (plus `UserAuthentication = 0`, which MultiSeat requires and RDP Wrapper's removal may reset) and verifies the end state before reporting success.

Rollback: `reg import "C:\Program Files\RDP Wrapper\Revert_to_default.reg"`, reboot, then delete the TermWrap DLLs.

---

### Moonlight Client (on player devices)

| Requirement | Details |
|-------------|---------|
| **Moonlight** | Latest stable release |
| **Platforms** | Windows, macOS, Linux, Android, iOS, tvOS, Raspberry Pi |

Download: https://moonlight-stream.org/

---

## Hardware Requirements

| Component | Minimum | Recommended |
|-----------|---------|-------------|
| **CPU** | 8-core | 12+ cores (2+ cores per seat) |
| **RAM** | 16 GB | 32+ GB (4+ GB per seat) |
| **GPU** | NVIDIA GTX 1060 or AMD RX 580 | NVIDIA RTX 3060+ or AMD RX 6700+ |
| **GPU VRAM** | 6 GB | 8+ GB |
| **Storage** | SSD with 50 GB free | NVMe SSD |
| **Network** | 100 Mbps LAN | 1 Gbps LAN (for multiple seats) |

> **GPU Note:** Hardware video encoding (NVENC/AMF) is required for acceptable performance. Each active seat uses one encoder instance. Check your GPU's concurrent encoder limits:
> - NVIDIA consumer GPUs (GTX/RTX): typically 3–5 concurrent NVENC sessions (varies by driver)
> - NVIDIA professional GPUs (Quadro/RTX A-series): unlimited concurrent sessions
> - AMD GPUs: typically 1–2 concurrent AMF sessions on consumer cards

---

## Windows Configuration

### Remote Desktop

Remote Desktop must be enabled:

```
Settings → System → Remote Desktop → Enable Remote Desktop: On
```

Or via PowerShell:
```powershell
Set-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
Enable-NetFirewallRule -DisplayGroup "Remote Desktop"
```

### User Account Control (UAC)

The MultiSeat service runs as SYSTEM and launches processes as user accounts. For admin accounts used as streaming seats, UAC must not prevent the service from obtaining elevated tokens. Standard (non-admin) accounts work without UAC concerns.

### Windows Defender / Antivirus

MultiSeat.InputHook.dll uses low-level Windows hooks for keyboard/mouse isolation. Some antivirus products may flag this as suspicious. Add the MultiSeat install directory to your AV exclusions if needed.

---

## Per-Seat Requirements

For each concurrent streaming seat:

| Resource | Requirement |
|----------|-------------|
| Windows local account | One per seat (created/managed by MultiSeat, or link an existing account) |
| Virtual display | One SudoVDA display per seat |
| Virtual audio device | One per seat: VB-CABLE (seat 0) or VoiceMeeter virtual input (seats 1–3) |
| TCP/UDP ports | 30-port block per seat (default: 48100–48129, 48130–48159, ...) |
| Apollo config directory | Created automatically under `C:\ProgramData\MultiSeat\apollo\` |
| Emulator netplay port | One per seat: `PortBase + 13` (48113, 48143, ...); seats netplay over `127.0.0.1` |

### Shared game library

MultiSeat creates a shared library at `C:\MultiSeatGames` (configurable) at first start, with
`SteamLibrary` + `ROMs` subfolders granted to `BUILTIN\Users`. Add the `SteamLibrary` folder in
each seat's Steam (Settings → Storage) so a game an owning account already installed isn't
re-downloaded; put ROMs in `ROMs`. Disable via `EnableSharedGameLibrary` / point at a data drive
via `SharedGameLibraryDir`.

---

## Network / Firewall

MultiSeat automatically creates Windows Firewall rules for each seat's ports. If you use a third-party firewall, allow the following per seat:

| Port Range | Protocol | Use |
|------------|----------|-----|
| PortBase + 0 | TCP | Apollo HTTPS (Moonlight pairing) |
| PortBase + 1 | TCP | Apollo HTTP |
| PortBase + 2 | TCP/UDP | RTP video |
| PortBase + 3 | TCP/UDP | RTP audio |
| PortBase + 4 | TCP/UDP | Control channel |
| 9550 | TCP | MultiSeat dashboard (local only recommended) |

Default `PortBase` = 48100. Each additional seat adds 30 to the base (Seat 1 = 48130, Seat 2 = 48160, etc.). The base sits above a stock Apollo's block so MultiSeat coexists with a standalone Apollo.

---

## Known Limitations

- **NVIDIA consumer GPU concurrent sessions:** GTX/RTX consumer cards have a driver-enforced limit of 3–5 simultaneous NVENC sessions. Use an NVENC session limiter patcher or a professional GPU to exceed this.
- **Virtual display disconnect:** If the mstsc session managing a seat's virtual display is disconnected, Apollo loses access to the display and streaming fails. MultiSeat keeps this session active automatically — do not manually disconnect it.
- **Windows updates:** a Windows update that replaces `termsrv.dll` breaks **RDPWrap** until a matching `rdpwrap.ini` entry is published — check the RDPWrap GitHub for updated patches. **TermWrap is not affected**: it resolves its offsets by disassembling whatever `termsrv.dll` is installed. This is the reason TermWrap is the recommended path.
- **Single GPU:** Multi-GPU configurations are not tested. All seats should use the same GPU for encoding.
