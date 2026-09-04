# Multi-seat WoW streaming: MultiSeat + Apollo + Moonlight

Written after the fact. Host is Windows 11 Pro 25H2 (build 26200), Ryzen 5800X3D, RTX 4070 Ti SUPER, single 5120x1440 ultrawide at 125% scaling. Goal: three kids each playing WoW 3.3.5a on their own tablet, streamed from one PC, each with an isolated Windows session.

Roughly two days of debugging condensed into the parts that actually mattered. Read [Things that cost hours](#things-that-cost-hours) before starting — several of those present as completely different problems than they are.

---

## What changed since this was first written

An earlier draft of this document told you to patch MultiSeat's source before provisioning seats. **Don't.** Both patches are upstream now, and the shipped versions are better than the hand-rolled ones.

| Topic | Then | Now |
|---|---|---|
| **Seat geometry** | patch it yourself | Automatic. `RdpFileBuilder` writes `desktopwidth`/`desktopheight` from the seat's configured size, plus two keys the hand-rolled patch missed: `dynamic resolution:i:0` (without it the hidden mstsc window can still drive the session's size) and `desktopscalefactor`, which sets DPI scaling inside the seat instead of leaving it at 100%. |
| **Per-seat audio** | patch it yourself | `AudioMode` defaults to `PerSession`. Both sink keys are left unset and `stream_mic = disabled` automatically. `SharedHost` remains available if you need the microphone. |
| **Virtual audio devices** | one VB-CABLE / VoiceMeeter device per seat | Not installed at all unless you ask for `-AudioMode SharedHost`. Skip them entirely. |
| **TermWrap detection** | ignore MultiSeat's false negative | Fixed. MultiSeat tests "`ServiceDll` is not stock `termsrv.dll`" rather than grepping for the string `rdpwrap`, and waits for TermService to start before judging. |
| **PowerShell version** | PowerShell 7 required | The installers run on Windows PowerShell 5.1 — `install-prerequisites.ps1` deliberately relaunches itself under 5.1 for a 64-bit PnP fix. PS7 is fine, just no longer necessary. |

---

## What the pieces do

**MultiSeat** is the orchestrator. Per seat it creates a Windows local account, opens a loopback RDP session into it (`mstsc /v:127.0.0.2`), launches an Apollo instance inside that session, and assigns it a port block.

**Apollo** (`ClassicOldSong/Apollo`, a Sunshine fork) is the streaming host — one instance per seat, each capturing its own session. **Moonlight** on each tablet is the client. A **multi-session patch** is required because Windows client editions allow only one interactive session.

Each seat is a separate Windows user with its own desktop, cursor and input queue. That is what makes this genuine multi-seat rather than three people fighting over one mouse.

## Prerequisites

- Windows 11 **Pro** — Home lacks Group Policy and behaves differently for RDP
- .NET 9 SDK, to build MultiSeat from source
- Visual C++ 2015–2022 x64 redistributable
- An NVENC GPU — MultiSeat bundles a patched NVENC library to lift the consumer concurrent-session cap

No longer needed: virtual audio devices, and PowerShell 7. Both were requirements of the older design.

---

## 1. Install MultiSeat

Clone this fork — it carries the TermWrap installer that upstream doesn't have. It sits one commit ahead of upstream, so pulling their work stays a fast-forward.

```powershell
git clone https://github.com/Weetermachine/MultiSeat.git
cd MultiSeat
.\prerequisites\install-prerequisites.ps1   # as Administrator
```

Reboot when it asks — HidHide and the multi-session patch need it. Then:

```powershell
.\scripts\install-service.ps1
```

> **Internalise this early.** The running service lives at `C:\Program Files\MultiSeat\`, *not* the source tree. Editing the copy under `src\` does nothing to the running service.

## 2. Multi-session patch — TermWrap

RDP Wrapper finds its patch offsets in `rdpwrap.ini`, keyed to the exact `termsrv.dll` build. Every Windows cumulative update that ships a new `termsrv.dll` breaks it until someone publishes a matching entry. That happened mid-setup here and took RDP down entirely — the stopgap was uninstalling the update and disabling Windows Update, which is untenable on a box with ports forwarded.

TermWrap disassembles `termsrv.dll` and resolves the offsets itself, so a new build is a non-event:

```powershell
.\prerequisites\install-termwrap.ps1
```

It removes RDP Wrapper, installs the x64 DLLs, merges the registry file, and asserts the settings the upstream instructions omit. It is idempotent — re-run it any time to re-assert and re-verify. It ends with nine checks and exits non-zero if any fail:

| Check | Expected |
|---|---|
| `ServiceDll` | resolves to `TermWrap.dll` |
| `TermWrap.dll`, `Zydis.dll` | both present in `C:\Program Files\RDP Wrapper\` |
| TermService | Running, StartType Automatic |
| TCP 3389 | something listening |
| `fDenyTSConnections` | `0` |
| `fSingleSessionPerUser` | `0` |
| `UserAuthentication` | `0` |

### Why those three registry values exist

None of them are in TermWrap's own README, and each presents as a completely different problem:

- **`fDenyTSConnections = 1`** disables Remote Desktop outright — nothing binds 3389 no matter what TermService does. **RDP Wrapper's own uninstaller sets it**, which is why the uninstall has to run *before* the fix, not after.
- **`fSingleSessionPerUser = 1`** makes a second connection reconnect the existing session instead of creating a new one. Presents as "mstsc opens a window that immediately closes."
- **`UserAuthentication`** (NLA) must be `0` for MultiSeat's loopback connection.

Reboot after it completes. Rollback is `reg import "C:\Program Files\RDP Wrapper\Revert_to_default.reg"`, reboot, delete the DLLs — the script's header documents this too.

## 3. Apollo

**On a clean machine there is nothing to do here — skip to step 4.**

`install-prerequisites.ps1` already installed **ApolloVibe** (the `vibesoftwarecoder` fork) to `C:\Program Files\ApolloVibe\`, which is exactly where the default `ApolloExePath` points. MultiSeat deliberately ships its own Apollo on its own port block so it never touches a standalone Apollo you might run for your own desktop.

The rest of this step applies **only if you already have a separate, standalone Apollo installed** — as the host this was written on did. If you do not, following it will install a second Apollo and override a working default for no reason.

<details>
<summary><b>Only if you have a standalone Apollo as well</b></summary>

Use `ClassicOldSong/Apollo` v0.4.6 or newer — earlier versions ship a SudoVDA driver whose code signature expired in August 2025.

**Disable its service.** A standalone Apollo registers `ApolloService`, which auto-starts a second `sunshine.exe` that fights MultiSeat's instances (see [Things that cost hours](#things-that-cost-hours)). MultiSeat deliberately leaves this service alone, so you must turn it off yourself:

```powershell
Stop-Service ApolloService -Force
Set-Service ApolloService -StartupType Disabled
```

**Only if you want MultiSeat to use that Apollo rather than ApolloVibe**, override the path. Put it in `appsettings.local.json`, **not** `appsettings.json`:

```jsonc
// C:\Program Files\MultiSeat\appsettings.local.json
{
  "MultiSeat": {
    "ApolloExePath": "C:\\Program Files\\Apollo\\sunshine.exe"
  }
}
```

`appsettings.local.json` is gitignored, loaded last, and never touched by `dotnet publish` — so the override survives every redeploy. Edits to `appsettings.json` get overwritten the next time you deploy.

Confirm where `sunshine.exe` actually landed; the path varies by installer version. Validate the JSON before restarting, because malformed JSON stops the service from starting at all:

```powershell
Get-Content "C:\Program Files\MultiSeat\appsettings.local.json" -Raw | ConvertFrom-Json
Restart-Service MultiSeatService
```

</details>

> **ApolloVibe and Vibepollo are different things, and the names are nearly identical.** **ApolloVibe** (`vibesoftwarecoder/Apollo`) is what the prerequisites script installs and what MultiSeat expects — that one is correct. **Vibepollo** (Nonary's fork) is not: MultiSeat parses Apollo's log for a display whose `friendly_name` contains VDD, SudoVDA or SudoMaker, and Vibepollo's driver reports differently, so the parse never matches and `output_name` never resolves. Do not substitute it.

## 4. Seats

Create one Windows local account per kid, then one seat per account in the dashboard at `http://localhost:9550`. The API key is at `C:\ProgramData\MultiSeat\api-key.txt`.

**Ports are assigned automatically — don''t compute them.** MultiSeat allocates each seat a 30-port block and the dashboard shows you everything you need: each seat card displays its **Port** (that is the address you give Moonlight) and links directly to that seat''s **Apollo web UI**. Use those.

Two things worth knowing anyway, because they explain what you are looking at:

- Blocks are **30 apart**, not 10 — so three seats land on 48100, 48130, 48160 rather than 48100/48110/48120.
- Bases are handed out in **provisioning order, not name order**. On this host the second child created got 48100. Never assume which kid owns which block.

Set each seat's resolution to the tablet's native size when you create it. Seats get exactly that geometry rather than inheriting the console's 5120x1440, and Apollo follows the resolution the Moonlight client asks for on connect.

> Seat accounts are **standard users** now, not local administrators (`GrantSeatAdministrator` is off by default). Anything a seat must write to has to grant access explicitly — which is what step 6 does for the game folders.

---

## 5. Client permissions — the step everyone misses

**Apollo gives the first paired client everything, and everyone after it almost nothing.** This is per seat, it is silent, it does not look like a permissions problem, and nothing in MultiSeat manages it for you.

| | |
|---|---|
| **Symptom** | The tablet pairs fine. It connects fine. It sees the app list. Then launching anything returns **403**, and there is no working mouse or keyboard. |
| **Cause** | Apollo grants the **first** client paired to an instance all permissions. Every client paired after that gets only *View Streams* and *List Apps*. |
| **Fix** | Open that seat's Apollo web UI — the dashboard seat card links straight to it — then **Clients** tab, and grant the permissions below to every client that wasn't first. Applies immediately, no restart. |

Grant each non-first client **all** of these:

- [ ] **Launch Apps** — without it: the 403. The app list is visible but nothing starts.
- [ ] **Mouse Input** — without it: the cursor never moves.
- [ ] **Keyboard Input** — without it: no typing, and no modifier keys in game.
- [ ] **Touch Input** — required for tablets. Easy to miss on a desktop-shaped mental model.
- [ ] **Controller Input** — only if a gamepad is used on that seat.

**You will hit this even with one tablet per seat**, because the natural way to test a new seat is to pair your own phone or PC to it first. That test device becomes the privileged client, and the kid's tablet — paired second — is the one that breaks. Either unpair the test device, or grant the tablet the permissions above.

Permissions are per Apollo instance, so a seat that works proves nothing about the other two. **Check all three Clients tabs before handing the tablets over.**

---

## 6. WoW client per seat

Give each seat its own copy of the game. Separate `WTF` folders mean separate resolution, keybinds and UI settings, which you want. Any path on any drive works — `D:` below is just where this host put them:

```
D:\WorldOfWarcraftClients\<Name> Client\
```

Grant the seat accounts access — Modify, not read-only, because the game writes to `WTF`, `Cache` and `Screenshots` inside its own directory:

```powershell
icacls "D:\WorldOfWarcraftClients" /grant "Users:(OI)(CI)M" /T
```

**Patch `Wow.exe` for RDP.** WoW 3.3.5a refuses to launch in a session it detects as remote, and every seat is an RDP session. [`Jnnshschl/WowRdpPatcher`](https://github.com/Jnnshschl/WowRdpPatcher) NOPs out that check. Patch one copy, verify it launches, then copy that exe to the others rather than re-running the patcher each time.

Set each client's `realmlist.wtf` to your server, and register the exe as an app in that seat's Apollo web UI (Applications tab) so it appears as its own tile in Moonlight rather than dropping the kid onto a bare desktop.

## 7. Clients

Standard Moonlight from the Play Store or App Store. (MoonlightVibe is a Windows client, not an Android one.) Add each PC manually as `<host-ip>:<seat-port>`, pointing each tablet at its own seat, and pair via PIN in that seat's Apollo web UI.

> **One client setting is not optional:** turn **"Play audio on host PC" ON**. This is the opposite of the old virtual-cable setup, and it is safe because the "host" of a redirected session *is* the seat's own session. With it off, the seat has no sound.

---

## Things that cost hours

### Apollo's own service fighting MultiSeat's instance

*Presents as: streams silently capturing the host's physical monitor.*

This only happens if you installed a **standalone** Apollo alongside ApolloVibe — a clean prerequisites-only install registers no such service. A standalone Apollo installs `ApolloService`, which auto-starts and spawns a second `sunshine.exe` via `sunshinesvc.exe`. That second instance holds an open handle on `ROOT\DISPLAY\0001`, so when MultiSeat's instance tries to restart the display adapter, Windows vetoes it. The tell is Kernel-PnP Event 225 naming `sunshine.exe` as the blocking process, plus SetupAPI `error=13`.

```powershell
Get-Process sunshine | Select-Object Id, Path, StartTime
```

More processes than seats means something other than MultiSeat is launching Apollo.

### A broken UMDF stack from a Win10 to Win11 in-place upgrade

*Presents as: virtual display creation failing while the driver reports healthy in Device Manager.*

The actual cause was `WUDFRd failed to load` with status `0xC0000365` (`STATUS_FAILED_DRIVER_ENTRY`) on *every* user-mode driver — SudoVDA, a Razer HID device, and audio. Kernel-PnP logs it. If UMDF is broken, no amount of Apollo configuration matters: repair Windows with DISM, then SFC, then an in-place repair install from ISO if those don't restore it.

### Diagnosing over RDP

*Presents as: every encoder, including software, failing with `DuplicateOutput() test failed [0x8000FFFF]`.*

A plain RDP session into the host changes what `EnumDisplaySettings` reports and breaks Desktop Duplication. Hours went into symptoms that existed only because of the diagnostic connection. **Test display problems at the physical machine.**

### A cumulative update breaking RDP Wrapper

*Presents as: "RDP loopback session did not appear within timeout"; RDPConf shows `Listening [not supported]`.*

This is the failure TermWrap exists to avoid. On TermWrap you will not see it.

---

## Known limitations

### Per-seat virtual displays don't work on this host

Apollo creates a SudoVDA monitor, but it attaches to the console session's desktop rather than the seat's RDP session, so MultiSeat finds only the RDP surface and Apollo captures that instead. This is upstream issue #15, reproduced independently on a near-identical host.

Confirmed here: all three seats' generated `sunshine.conf` still read `# output_name = pending`. MultiSeat now expects the display to be absent at startup and retries detection after a client connects (issue #16), so the old alarming log line is no longer treated as a fault — but the placeholder persisting means Apollo is still capturing the RDP surface.

The geometry fix works around the consequence rather than the cause: by setting the RDP session's size at connect time, each seat gets its configured resolution even with no virtual display. Still unavailable without one: **HDR** in seats (the RDP surface has no EDID), and **NVENC capture of a virtual display** — TermService software-encodes the RDP surface instead, which is the main per-seat CPU cost.

### No microphone in seats

A session that keeps its own audio cannot see the host's Steam Streaming Microphone, so `stream_mic` is written `disabled`. Game audio out works; Moonlight-to-game mic does not. If you need it, switch that seat to `AudioMode = SharedHost` and accept the virtual-cable requirements that come back with it.

### Input isolation is not built

`MultiSeatInputHook.dll` has C++ source in the repo but ships uncompiled, and `EnableKeyboardMouseIsolation` defaults to false. It governs whether the host's physical keyboard and mouse bleed into seat sessions. Tablet input arrives through Moonlight and is already session-scoped, so this may never matter.

---

## Verification checklist

```powershell
# One Apollo per seat, all from the MultiSeat-configured path
Get-Process sunshine | Select-Object Id, Path

# Apollo's own service stays off
Get-Service ApolloService | Select-Object Status, StartType

# One listening port per seat - reads each seat's real port, so it cannot go stale
Get-ChildItem "C:\ProgramData\MultiSeat\apollo\*\sunshine.conf" | ForEach-Object {
    $m = Select-String -Path $_.FullName -Pattern '^port = (\d+)'
    $port = [int]$m.Matches[0].Groups[1].Value
    $up = Get-NetTCPConnection -LocalPort $port -State Listen -EA SilentlyContinue
    "{0,-12} base {1}  webui {2}  {3}" -f $_.Directory.Name, $port, ($port + 1),
        $(if ($up) { "LISTENING" } else { "NOT LISTENING" })
}

# RDP up, multi-session allowed, TermWrap loaded
Get-Service TermService | Select-Object Status, StartType
Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server" |
    Select-Object fDenyTSConnections, fSingleSessionPerUser
Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\TermService\Parameters" |
    Select-Object ServiceDll
```

A seat with a running Apollo process but no listening port is broken — delete and recreate it. Then check the generated config reflects the current design:

```powershell
# Should contain desktopwidth/desktopheight and audiomode:i:0
Get-Content C:\ProgramData\MultiSeat\default_rdp_staging.rdp

# Should contain NO virtual_sink or audio_sink line, and stream_mic = disabled
Select-String -Path "C:\ProgramData\MultiSeat\apollo\*\sunshine.conf" `
    -Pattern "virtual_sink|audio_sink|stream_mic"
```

Per-seat logs live at `C:\ProgramData\MultiSeat\apollo\<Account>\apollo.log`. MultiSeat's own logging goes to the Windows Application event log under the `MultiSeat.Service` source — not to a file.

---

*Verified against this fork (upstream `c4dbfab` plus the TermWrap installer): the corrections at the top against the source tree, and the observed state (seat geometry, `audiomode:i:0`, absent sink keys, `output_name = pending`) against a live three-seat install. The full flow has not been re-run end to end since upstream's versions landed, so treat step ordering as sound and exact log strings as approximate.*
