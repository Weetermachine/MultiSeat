# Runbook: per-session audio spike (R1/R2)

Companion to [per-session-audio.md](per-session-audio.md). This is the go/no-go gate for the #10 / #12 fix.

**Time:** ~15 minutes · **Code changes:** none (uses the `--audio-peaks` and `--capture-loopback` helpers already in the service)

---

## ✅ RESULT — run 2026-08-07, Windows 11 build 26200: ALL FOUR TESTS PASS

| Test | Result | Evidence |
|---|:---:|---|
| 1 Remote Audio endpoint exists | **PASS** | session saw **exactly one** endpoint, `Remote Audio [DEFAULT]`, id `{3.0.0.00000004}…`; none of the host's 14 were visible |
| 2 Console keeps its audio | **PASS** | console app peak **0.356361** while the session was Active |
| 3 Loopback capture ⭐ | **PASS** | `packets=600 (silent=0)`, peak **0.356324**, 44100 Hz 2ch float |
| 4 Mute works, capture survives | **PASS** | console mstsc `0.351863 → 0.000000`; in-session capture still `packets=500 (silent=0)` |

**→ Green light: build it behind the `AudioMode` flag.** Apollo *can* loopback-capture the RDP session's private endpoint, and muting the console-side mstsc stops host leakage without killing that capture.

Re-run this on another host by following the steps below — it is now unattended apart from one mstsc trust dialog.

---

## What this answers

Today every seat is told *"play your audio on the host PC."* That's why an active seat silences the console (#12) and fights over the default device (#10).

The proposed fix tells each seat *"play your audio on the client"* instead — which makes Windows create a **private playback device inside that seat's session**, called "Remote Audio". Nothing touches the host's real speakers.

That plan only works if **Apollo can record from that private device.** On some Windows builds recording from it silently produces nothing. This runbook answers that.

---

## How this is measured — read this first

**The host is headless and nobody is ever physically at it.** Every readout here is therefore a number, not a sound. Do not substitute listening: the machine is reached over RustDesk, which *forwards host audio to the operator*, so "can I hear it?" measures RustDesk's re-routed stream rather than the endpoint under test. When the thing being diagnosed is audio routing, that confound is fatal.

There are two instruments, and they answer **different** questions:

```powershell
MultiSeat.Service.exe --audio-peaks [seconds]                        # is audio FLOWING to an endpoint
MultiSeat.Service.exe --capture-loopback <device> [secs] [out.wav]   # can audio be CAPTURED FROM one
```

The first polls every active render endpoint — and every application session on it — and reports the peak each reached. The second opens a WASAPI loopback stream, writes a 16-bit PCM WAV, and prints the peak amplitude it actually recorded. Test 3 needs the second one; a healthy reading from the first does **not** imply it.

Three properties of the peak meter that change how you read its output:

1. **Run it inside the session you are measuring.** `IAudioSessionManager2::GetSessionEnumerator` is session-scoped, so it only sees the Windows session it runs in. Console session for host audio; the RDP session for that session's audio.
2. **⚠️ Read the per-`APP` lines, not the endpoint line.** On virtual devices (VB-CABLE, VoiceMeeter) the endpoint meter does not reflect the session mix. This is measured, not theoretical:
   ```
   silent peak=0.000031  CABLE In 16ch (VB-Audio Virtual Cable) [DEFAULT]
            APP | Playnite.DesktopApp (pid 4188) peak=0.327480 AUDIO
   ```
   The device reads its noise floor while an app on it is plainly loud. **Never conclude "this device has no audio" from the endpoint number.**
3. **Peaks prove audio is *flowing to* an endpoint. They do not prove it can be *captured from* it.** Those are different claims, and the gate (Test 3) is specifically about capture. Don't let a healthy peak talk you into passing Test 3.

And two that matter for the capture tool:

4. **An idle endpoint yields NO packets at all**, not packets of silence. So `packets=0` means "nothing was playing" just as much as "capture is broken" — the tool says so in its verdict line. Always confirm the source with `--audio-peaks` first; that is why 4.2 exists. This fired for real during the 2026-08-07 run: a Test 4 attempt read `packets=0` purely because the source loop had ended before capture started.
5. **⚠️ VB-CABLE returns SILENCE to loopback.** Measured: an app rendering to `CABLE In 16ch` at peak 0.356 produced `packets=598, silent=598`, and a raw read that ignores the SILENT flag was also 0.000000 — so it is the platform, not a bug in the tool. Plausible for a virtual cable whose driver already loops render→capture internally. **Never use a VB-CABLE endpoint as your positive control**; you will conclude the design is dead. `Remote Audio` behaves normally (`silent=0`).

The sound source must be a **real application in a real session**. `System.Media.SoundPlayer` works fine when launched as its **own process** (`Start-Process powershell … PlayLooping()`) — measured at peak 0.356361 — but renders nothing when called *inline* from a non-interactive automation context, which has produced two wrong conclusions on this project already. Launch it, don't inline it.

---

## ⚠️ Read before starting

**This test opens a second Windows session**, which only works if RDPWrap is installed and current. RDPWrap breaks whenever a Windows update replaces `termsrv.dll` — and when it's broken, connecting **disconnects your console session instead of adding one**. On a headless box reached over RustDesk, that costs you access until you can recover it.

Step 0.3 checks this before anything risky happens. Don't skip it.

**Don't run this while a MultiSeat seat is live.** This test is completely independent of the MultiSeat service — it doesn't touch the service, its config, or any seat — but a live seat muddies the audio picture.

---

## Part 0 — Preparation

### 0.1 Confirm no seat is running

```powershell
Get-Process mstsc -ErrorAction SilentlyContinue
qwinsta
```

**Expected:** no `mstsc`, and no seat sessions in `qwinsta`. If a seat is up, tear it down from the dashboard first.

### 0.2 Nothing to install

Test 3 used to need Audacity driven by hand. It doesn't any more — `--capture-loopback` does it. Skip straight to 0.3.

**Running a command *inside* the RDP session, from the console.** Several steps below need this. A scheduled task with an Interactive principal lands in the user's session:

```powershell
$act  = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c <command> > C:\Users\Public\spike\out\x.txt 2>&1'
$prin = New-ScheduledTaskPrincipal -UserId 'audiotest' -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName 'spike' -Action $act -Principal $prin -Force
Start-ScheduledTask -TaskName 'spike'
```

Two traps, both hit on the first run:
- Wrap in **`cmd.exe /c`** if you want output redirected. `powershell.exe -File x.ps1 > out.txt` passes `>` as an argument to PowerShell and silently writes nothing.
- `schtasks /it` fails with **"Element not found"**; the PowerShell API above works.

Stage the binaries somewhere the test account can read — `C:\Program Files\MultiSeat\` is fine, or copy the build output to `C:\Users\Public\spike\bin` and grant `BUILTIN\Users` modify.

### 0.3 ⚠️ Verify RDPWrap is installed — DO THIS BEFORE CONNECTING

**This is the step that protects your access.** Without RDPWrap, connecting in Part 1 will *disconnect* you instead of opening a second session. Checking afterwards is too late.

```powershell
$dll = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\TermService\Parameters' -Name ServiceDll).ServiceDll
"ServiceDll : $dll"
"dll exists : $(Test-Path $dll)"
"ini exists : $(Test-Path ([IO.Path]::ChangeExtension($dll,'ini')))"
```

**Required to continue:**
- `ServiceDll` must end in **`rdpwrap.dll`** — not `termsrv.dll`
- both `Test-Path` lines must be **`True`**

> **Do not hardcode `C:\Windows\System32\rdpwrap.dll`.** RDPWrap commonly installs to
> `C:\Program Files\RDP Wrapper\`, and an earlier version of this runbook tested the
> System32 path — which returns `False` on a perfectly healthy install and told the
> operator to STOP. Always resolve the path from `ServiceDll`, as above.

> ❌ **If `ServiceDll` says `termsrv.dll`, no multi-session patch is active. STOP.**
> Run `prerequisites\install-termwrap.ps1`, reboot, and re-check. (On an RDP Wrapper install,
> `prerequisites\install-prerequisites.ps1 -UseRdpWrapper` refreshes `rdpwrap.ini` instead —
> that advice applies only to RDP Wrapper; TermWrap has no ini.)

The strongest evidence is empirical: if `qwinsta` already shows **two `Active` sessions** (your console plus a seat), multi-session is provably working right now.

### 0.4 Record your session state

```powershell
qwinsta
```

Your console session should show as `Active`. You'll re-run this in Part 1 to confirm you **gained** a session rather than **replaced** one.

### 0.5 Create a throwaway test account

A scratch account keeps seat state untouched. Cleanup is Part 6.

```powershell
$pw = Read-Host -AsSecureString "Password for test account"
New-LocalUser -Name "audiotest" -Password $pw -AccountNeverExpires
Add-LocalGroupMember -Group "Remote Desktop Users" -Member "audiotest"
```

### 0.6 Create the test connection file

This is MultiSeat's real `Default.rdp` with **one line changed** — `audiomode:i:1` → `audiomode:i:0` — and the CPU-saving lines dropped so you can see the desktop.

```powershell
@"
full address:s:127.0.0.2
authentication level:i:0
prompt for credentials:i:0
audiomode:i:0
"@ | Set-Content -Path "C:\ProgramData\MultiSeat\spike-test.rdp" -Encoding ASCII
```

> Editing `C:\ProgramData\MultiSeat\Default.rdp` by hand does nothing — `EnsureDefaultRdp` rewrites it on every seat launch. That's why this test uses its own file.

### 0.7 Baseline the console's audio

```powershell
& "C:\Program Files\MultiSeat\MultiSeat.Service.exe" --audio-peaks 5
```

Note which endpoint is marked `[DEFAULT]`. Save this output — it's the "before" half of Test 2.

---

## Part 1 — Open the test session

### 1.1 Connect

```powershell
mstsc "C:\ProgramData\MultiSeat\spike-test.rdp"
```

Log in as `audiotest`. Accept any certificate warning.

### 1.2 Verify you gained a session

**Back on the console**, run:

```powershell
qwinsta
```

**Expected:** your original console session still `Active`, **plus** a new `audiotest` session.

> ❌ **If your console session got disconnected**, the multi-session patch is broken. Stop — run `prerequisites\install-termwrap.ps1` (or, on an RDP Wrapper install, `install-prerequisites.ps1 -UseRdpWrapper` to refresh `rdpwrap.ini`), then start over.

**Leave this RDP session open for every test below.**

---

## Part 2 — TEST 1: does the private audio device exist?

**Inside the RDP session:**

```powershell
& "C:\Program Files\MultiSeat\MultiSeat.Service.exe" --audio-peaks 3
```

- ✅ **PASS** — an endpoint named **"Remote Audio"** appears in the list, marked `[DEFAULT]`.
- ❌ **FAIL** — no such endpoint. Stop; the design's foundation is missing. Save the full output.

The header line also confirms you're measuring the right place — it prints the Windows session id, which should be the RDP session's, not 1.

**Record:** Test 1 = PASS / FAIL

---

## Part 3 — TEST 2: does the host keep its audio?

This is the whole point of the change, and the cheapest test here.

### 3.1 Play something on the console

On the **console session**, start real audio — a browser video, a music player. Leave it playing.

### 3.2 Measure, on the console

```powershell
& "C:\Program Files\MultiSeat\MultiSeat.Service.exe" --audio-peaks 8
```

Find the **`APP |`** line for the player you started.

- ✅ **PASS** — that app shows a clearly non-zero peak (order 0.01–1.0) while the RDP session is open. **This is #12 fixed.** Under today's `audiomode:i:1` the console would be silent.
- ❌ **FAIL** — the app's peak stays at the noise floor (≈0.00003 or 0.000000) → `audiomode` isn't the cause, and our root-cause analysis on #10/#12 is wrong. That's significant; save everything.

> Judge this on the app line. The endpoint line can read its noise floor even while the app on it is loud (see "How this is measured").

**Record:** Test 2 = PASS / FAIL, and the peak value.

---

## Part 4 — TEST 3: can it be recorded? ⭐ THE GATE

Everything depends on this one. Peak metering cannot answer it — flow and capture are different claims.

Source and capture must run in the **same** task. Splitting them across two tool calls is how the first run recorded `packets=0` from a source that had already stopped.

Save this as `C:\Users\Public\spike\run-test3.ps1`:

```powershell
$p = New-Object System.Media.SoundPlayer "C:\Windows\Media\Alarm01.wav"
$p.PlayLooping()
Start-Sleep -Seconds 2

"===== 4.2 confirm the source is actually flowing ====="
& "C:\Program Files\MultiSeat\MultiSeat.Service.exe" --audio-peaks 4

"===== 4.3 THE GATE: loopback capture from Remote Audio ====="
& "C:\Program Files\MultiSeat\MultiSeat.Service.exe" `
    --capture-loopback "Remote Audio" 6 "C:\Users\Public\spike\out\remote-audio.wav"

$p.Stop()
```

Run it **inside the RDP session** with the scheduled-task recipe from 0.2, then read the output file.

**4.2 must show a non-zero `APP |` peak for `powershell` on "Remote Audio" first.** If it doesn't, the source isn't playing — fix that before blaming capture, or you will record silence and wrongly fail the gate.

Then read 4.3's verdict, which the tool prints for you:

- ✅ **PASS** — `peak amplitude` well above 0.01 with `silent=0` → **loopback works. The design is viable. Build it.**
- ❌ **FAIL** — packets arrive but `silent=` equals the packet count and peak is 0 → loopback yields silence on this endpoint. **R1 fails, the gate closes.**
- ⚠️ **`packets=0`** — inconclusive, not a failure. Nothing was playing, or loopback is unsupported. Re-run; do not record a FAIL from this.

The WAV it writes is ordinary 16-bit PCM, so you can open or measure it independently if you want a second opinion.

**Observed 2026-08-07:** `packets=600 (silent=0)`, peak **0.356324**, 44100 Hz 2ch float, file 1,058,444 bytes — exactly `6s × 44100 × 2ch × 2 bytes + 44` header. **PASS.**

**Record:** Test 3 = PASS / FAIL, and the peak amplitude.

### 4.5 Confirm with Apollo (only if 4.3 passed)

`--capture-loopback` passing is a strong signal, but Apollo is the real consumer and it uses its own capture path. Run a scratch ApolloVibe instance **inside the RDP session** with a throwaway config containing:

```
audio_sink = Remote Audio
```

Connect Moonlight from a phone or another PC and confirm you get the session's audio.

If our capture passed but Apollo's doesn't, the problem is specific to Apollo's implementation — important to know before designing around it.

**Not yet run as of 2026-08-07** (Tests 1–4 passed; this one needs a Moonlight client). It is the one remaining unknown between "the platform supports this" and "MultiSeat can ship it".

**Record:** Test 3b = PASS / FAIL / skipped

---

## Part 5 — TEST 4: does seat audio leak to the host?

Under `audiomode:i:0` the session's audio is routed to the `mstsc` window, which lives on the console. **We expect it to come out on the host** — that's the risk the real implementation has to engineer away.

### 5.1 Confirm the leak is real

With the alarm still looping inside the session, measure **on the console**:

```powershell
& "C:\Program Files\MultiSeat\MultiSeat.Service.exe" --audio-peaks 6
```

**Expected:** an `APP | mstsc` line with a non-zero peak. That is the leak, measured. (Not a surprise — it's why `MuteMstscAudio` exists.)

### 5.2 Mute it

On the **console**, mute mstsc's audio session by PID:

```powershell
$pid = (Get-Process mstsc).Id
& "C:\Program Files\MultiSeat\MultiSeat.Service.exe" --mute-audio $pid
```

Re-measure on the console — the `mstsc` peak should fall to the noise floor.

### 5.3 The step that actually matters

**Re-run `run-test3.ps1` inside the session while `mstsc` is still muted**, writing to a different file (`muted.wav`). Same task, same order — source and capture together.

- ✅ **PASS** — host `mstsc` peak is at the noise floor **and** the new capture still has real amplitude → muting is a safe mechanism; `MuteMstscAudio` just needs to be made reliable.
- ❌ **FAIL** — muting also killed the capture → mute isn't viable and we need another way to stop host leakage. Design change required.

**Observed 2026-08-07:** console `mstsc` went `0.351863 → 0.000000` while the in-session capture still returned `packets=500 (silent=0)`, peak **0.356324**. **PASS** — mute stops the leak without touching capture.

**Record:** Test 4 = PASS / FAIL

---

## Part 6 — Cleanup

1. Unregister any leftover scheduled tasks: `Get-ScheduledTask -TaskName 'spike*' | Unregister-ScheduledTask -Confirm:$false`
2. Log the session off properly — `logoff <id>` — then close mstsc. Don't just close the window.
3. Remove the test account, files, and stored credential:

```powershell
Remove-LocalUser -Name "audiotest"
Remove-Item "C:\ProgramData\MultiSeat\spike-test.rdp"
Remove-Item "C:\Users\Public\spike" -Recurse -Force
cmdkey /delete:TERMSRV/127.0.0.2
```

4. Confirm the console's default endpoint is unchanged:

```powershell
& "C:\Program Files\MultiSeat\MultiSeat.Service.exe" --audio-peaks 3
```

Compare the `[DEFAULT]` marker against your 0.7 baseline. Nothing else was modified — the MultiSeat service, its config, and all seats were untouched throughout.

---

## Results sheet

```
Date:                     2026-08-07
Windows build:            26200

Test 1  Remote Audio endpoint exists    PASS
Test 2  Console keeps its audio         PASS      peak: 0.356361
Test 3  Loopback recording works  ⭐    PASS      peak: 0.356324  (packets=600, silent=0)
Test 3b Apollo captures it              skipped   (needs a Moonlight client)
Test 4  Mute works, capture survives    PASS      mstsc 0.351863 -> 0.000000, capture 0.356324

Notes / anything unexpected:
  - VB-CABLE returns SILENCE to loopback (598/598 silent packets while an app rendered
    at 0.356). Platform behaviour, not a tool bug — confirmed by reading the buffer
    ignoring the SILENT flag. Do not use it as a control.
  - Test 2's comparison against audiomode:i:1 was NOT measured head-to-head; it rests
    on issue #12's reporter evidence. Worth closing that gap on a future run.
  - mstsc's "Unknown remote connection" dialog still needs one manual click.
```

Blank sheet for re-runs:

```
Date:
Windows build:            (winver)

Test 1  Remote Audio endpoint exists    PASS / FAIL
Test 2  Console keeps its audio         PASS / FAIL   peak:
Test 3  Loopback recording works  ⭐    PASS / FAIL   peak:
Test 3b Apollo captures it              PASS / FAIL / skipped
Test 4  Mute works, capture survives    PASS / FAIL

Notes / anything unexpected:
```

## What each outcome means

| Test 1 | Test 2 | Test 3 | Test 4 | Verdict |
|:---:|:---:|:---:|:---:|---|
| ✅ | ✅ | ✅ | ✅ | **Green light.** Build behind the `AudioMode` flag. |
| ✅ | ✅ | ✅ | ❌ | Build it, but solve host leakage another way first. |
| ✅ | ✅ | ❌ | — | **Gate closes.** Fall back to a per-session virtual audio driver or per-app routing — both worse. Reassess before spending effort. |
| ✅ | ❌ | — | — | Root-cause analysis on #10/#12 is wrong. Stop and re-diagnose. |
| ❌ | — | — | — | Foundation missing. Re-diagnose. |

## Troubleshooting

**Console session disconnected when connecting** — the multi-session patch is broken. Run `prerequisites\install-termwrap.ps1` (RDP Wrapper installs: `install-prerequisites.ps1 -UseRdpWrapper`, which refreshes `rdpwrap.ini`).

**Certificate / "can't verify identity" warning** — expected for loopback RDP; accept it.

**`--capture-loopback` reports `packets=0`** — nothing was playing to that endpoint. Confirm with `--audio-peaks` in the same session; do not read it as a Test 3 FAIL.

**Capture returns all-silent packets** — if the endpoint is a VB-CABLE one, that is expected and tells you nothing (see "How this is measured"). On `Remote Audio` it is a genuine FAIL.

**Scheduled task runs but writes no output** — you used `powershell.exe -File … > out.txt`. The `>` needs a shell; wrap the whole thing in `cmd.exe /c`.

**`--audio-peaks` shows everything at 0.000031** — that's the VB-CABLE/VoiceMeeter noise floor, i.e. silence. Confirm your sound source is actually playing.

**`--audio-peaks` shows no app sessions at all** — you're running it in the wrong Windows session. Check the session id in its header line.

**No sound from `PlayLooping()`** — confirm "Remote Audio" is the session default inside the session, and that 4.2 shows a non-zero peak for `powershell`.

**Session logs straight back out** — the account needs to be in **Remote Desktop Users** (Step 0.5).

## Worth posting either way

Test 2's result is worth posting to #12 whichever way it goes — it either confirms the fix direction to two waiting reporters, or saves everyone a wrong turn. Both have been patient and are actively testing.

## Follow-up: Test 3 is scriptable now — BUILT

`--capture-loopback <device> [seconds] [out.wav]` (`IAudioClient` with `AUDCLNT_STREAMFLAGS_LOOPBACK` + `IAudioCaptureClient`) closed the last manual gap. The whole spike now runs unattended apart from one mstsc trust dialog, which makes it reasonable to ask an external reporter to run it on their own host — the original motivation for building it.

Remaining manual step: mstsc's "Unknown remote connection" security warning still needs one click. `AllowUnsignedFiles=1` does **not** suppress it on Windows 11; signing `Default.rdp` with `rdpsign.exe` and trusting the thumbprint is the real fix, and would also retire the `SendKeys` dismisser MultiSeat currently relies on for seats.
