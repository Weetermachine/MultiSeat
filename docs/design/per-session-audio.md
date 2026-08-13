# Design: Per-session audio isolation

Status: **Shipped** · Fixes: #10, #12 · Related: #11 (display-side twin)

## What actually shipped (differs from the plan below)

This fork is single-user and does not need seat microphones, so the per-session path **replaced** the shared-host path outright instead of shipping behind a flag:

- **No `AudioMode` option.** `audiomode:i:0` unconditionally; the `SharedHost` branch does not exist here. (Upstream's PR keeps the flag for installs that need the mic.)
- **The endpoint is never named.** The plan's "point Apollo at the Remote Audio endpoint (named `audio_sink`/`virtual_sink`, or rely on session default)" resolved hard to *rely on the session default*: `audio_sink` makes Apollo re-role the endpoint, and `virtual_sink` makes Apollo rewrite its wave format, breaking loopback for every client including Apollo. Both keys are absent from `sunshine.conf`, and `keep_sink_default` / `auto_capture_sink` went with them.
- **No in-session endpoint-resolver helper.** Not needed once nothing names the endpoint — which also sidesteps the localized friendly name ("Audio remoto" on Spanish Windows).
- **`stream_mic = disabled`.** Non-goal in the plan; in practice a session that keeps its own audio cannot reach the host's Steam Streaming Microphone at all, so the mic is gone, not merely unchanged.
- **`AudioRouter` / `AudioDeviceEnumerator` / `VoiceMeeterConfigurator` are unwired, not deleted** — kept for rollback. `VacCableCount` and the cable-count seat cap are gone.
- **Client-side:** "Play audio on host PC" must be **ON** — the opposite of the shared-VAC setup.

R1/R2 were validated on another fork in production since 2026-08-10 with the virtual cables uninstalled.

---

## Original design notes

## Problem

Every seat's RDP session is created with `audiomode:i:1` ("play audio on the **host** computer"; `SessionLauncher.EnsureDefaultRdp`). Seats therefore render onto the host's **shared** audio subsystem and use host-side virtual audio devices (VB-CABLE for seat 0, VoiceMeeter channels for seats 1–3). Consequences, confirmed by two reporters' logs:

- **#10** — MultiSeat forcing a seat's VAC as the machine-wide default output hijacked the console's default. (Mitigated: MultiSeat no longer sets the render default, and Apollo uses `virtual_sink` + `keep_sink_default = disabled`.)
- **#12** — but that only *shifted* the symptom. With `audiomode:i:1`, an active seat's RDP session renders onto the host's physical device **and Windows suspends the console session's own playback** while that seat is active. Reporter's logs proved: host apps silent on a second unrelated device too (whole console session suspended), and the seat's audio leaks onto the console's physical output.

Root cause: **seats share the host's single audio subsystem.** No amount of default-device juggling fixes it, because there is one global default and one shared physical endpoint. This is the audio twin of #11 (SudoVDA is a global IddCx display, not RDP-session-scoped).

## Goal / non-goals

**Goal:** each seat's game audio is captured for Moonlight from an endpoint that lives **inside that seat's RDP session**, so the host's physical audio and the console session are never touched, with no shared virtual cables.

**Non-goals (this pass):** microphone path (stays on Steam Streaming Microphone; Moonlight→game, unaffected); surround sound (see R3); the #11 display re-architecture (tracked separately).

## Target architecture — RDP per-session audio

RDP audio redirection gives every session its **own** "Remote Audio" (Microsoft Remote Audio) render endpoint. That is exactly the per-session isolation we need. DuoStream uses this same MS remote audio driver.

Flow per seat:

1. Seat RDP session uses **`audiomode:i:0`** ("play on this computer" = the client). Windows creates a per-session **"Remote Audio"** render endpoint inside the seat session and makes it the session default; seat games play to it automatically.
2. **Apollo, running inside the seat session, WASAPI-loopback-captures the Remote Audio endpoint** and streams it to Moonlight. Nothing renders on the host's physical devices.
3. The redirected audio is also sent to the `mstsc` client, which lives in the **console** session (hidden, holds the seat Active). We **mute that `mstsc` process's audio session** so seat audio never plays on the host. The mute path already exists (`SessionLauncher.MuteMstscAudio` → `--mute-audio <pid>` → `AudioMuteHelper.MuteByPid`); today it's a no-op safety net under `audiomode:i:1`, and it becomes load-bearing here.

Because the Remote Audio endpoint is unique to each session, setting/keeping it as that session's default is inherently session-scoped — it cannot collide with the console or other seats the way a shared VAC did.

## Why this fixes #10 and #12

- Host physical device is never a render target for any seat → console is never suspended (#12).
- No machine-wide default is ever changed to a shared device → no hijack (#10).
- Each seat has its own endpoint → seats can't fight each other.

## Key risks / spikes (validate BEFORE building)

| ID | Risk | Spike |
|----|------|-------|
| **R1** (gating) | Can Apollo **WASAPI-loopback-capture** the MS Remote Audio endpoint? Historically loopback on the RDP audio device has been unreliable/silent on some Windows builds. If this fails, the whole approach needs a fallback. | In a live seat session with `audiomode:i:0`, run a WASAPI loopback capture on the Remote Audio endpoint and confirm non-silent PCM. Can use Apollo itself pointed at that sink, or a tiny loopback test. |
| **R2** | Does RDP audio redirection work under **RDPWrap** multi-session on Win11 26100+? Does the Remote Audio endpoint appear in the loopback seat session? | Connect a seat with `audiomode:i:0`, enumerate render endpoints in-session, confirm "Remote Audio" present + default. |
| **R3** | MS Remote Audio driver is **stereo-only** (per DuoStream). Surround game audio downmixes to 2.0. | Accept + document; matches current Opus 2.0 streaming anyway. |
| **R4** | `mstsc` audio-session **mute timing** — the session may be created after we mute. Seat audio could briefly leak to the host. | Mute on connect + re-mute on the connect health tick; verify no console leakage. |
| **R5** | Added latency from the RDP audio hop + loopback. | Measure end-to-end; expected acceptable for game streaming. |

R1 is the gate. If loopback on Remote Audio fails, fallback options: (a) a per-session virtual audio driver, (b) keep the shared-VAC path for game audio but solve host coexistence differently. Both are worse; R1 passing is what makes this design win.

## Code changes (all behind a feature flag)

Add `MultiSeatOptions.AudioMode = { SharedHost (current default) | PerSession }` so the two paths coexist and we can flip the default once proven.

- **`SessionLauncher.EnsureDefaultRdp`** — `audiomode:i:1` → `audiomode:i:0` when `PerSession`.
- **`SessionLauncher`** — make `MuteMstscAudio` load-bearing (retry / re-mute on connect) under `PerSession`.
- **`ApolloConfigBuilder`** — `PerSession`: point Apollo at the Remote Audio endpoint (named `audio_sink`/`virtual_sink`, or rely on session default) and drop the host-VAC `virtual_sink`.
- **New in-session helper** (or extend `--enum-displays`-style pattern) — resolve the seat session's Remote Audio endpoint ID and (if needed) set it as session default; report it for the Apollo config.
- **`AudioRouter` / `AudioDeviceEnumerator`** — `PerSession`: skip VAC assignment, VoiceMeeter startup, and cable dedup entirely.
- **`SeatManager` step 5** — branch on `AudioMode`.
- **Prereqs (`install-prerequisites.ps1`) + `CLAUDE.md`** — VB-CABLE / VoiceMeeter become **not required** under `PerSession`; document.
- **Tests** — config generation under both modes.

## What this removes / simplifies (the upside beyond the bug fixes)

- **No VB-CABLE, no VoiceMeeter Potato** for game audio → removes the most painful prereqs (VoiceMeeter needs a reboot, exclusive-grab quirks, the P/Invoke B1 routing config).
- **No 4-seat audio ceiling** — that limit was "1 host VAC device per seat." Each session gets its own Remote Audio endpoint, so audio no longer caps seat count.
- **No global-default juggling** — deletes a class of fragile `--set-default-render`/`keep_sink_default` logic.

## Rollout

1. **Spike R1 + R2** on the box (needs a live seat — user-driven). Go/no-go gate.
2. Implement behind `AudioMode = PerSession` (default stays `SharedHost`).
3. Dogfood on the box; validate #10 + #12 scenarios (console keeps audio while a seat streams; seat audio reaches Moonlight; multiple seats independent).
4. Flip default to `PerSession`; mark VAC/VoiceMeeter optional in prereqs.
5. Later: remove the shared-host path once `PerSession` is proven across setups.

## Open decisions for the user

1. **Ship behind a flag with `SharedHost` default (recommended)**, or replace the shared path outright?
2. Who drives the **R1/R2 spike** (needs a live seat + a couple of manual audio tests on the box)? That's the gate before any implementation effort.
3. Keep VB-CABLE/VoiceMeeter as an optional fallback path long-term, or fully deprecate once `PerSession` proves out?
