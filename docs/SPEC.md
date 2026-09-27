# Hardware Live: portable v1 spec

Status: **DRAFT, awaiting Alessa's approval.** No code until approved.

## Goal

A local, shareable Windows hardware dashboard. It shows live CPU, GPU, board, RAM, storage
and fan telemetry with a rule-based health assessment. It must work on an arbitrary PC, not
just the one it was prototyped on.

The prototype lives at `~/.claude/scripts/hwdash/` and is hardwired to one machine
(exact sensor names, paths, thresholds). This repo generalizes it.

## Non-goals (v1)

- Fan control or any hardware **writes**. The app is read-only, always.
- Remote or multi-machine monitoring. Binds to 127.0.0.1 only.
- Linux or macOS.
- Built-in LLM commentary. The notes panel stays a passive file hook (see below).

## Architecture decision: data source

| Option | Pros | Cons |
|---|---|---|
| **A. Read LibreHardwareMonitor's built-in web server** (`http://localhost:8085/data.json`) | No sensor code of our own, no admin rights for our app, no DLL redistribution, and LHM already handles every board/CPU/GPU | The user must install LHM and PawnIO, enable "Run Web Server", and run LHM elevated. Two apps. |
| B. Our own sampler using `LibreHardwareMonitorLib` (NuGet 0.9.6, netstandard2.0) | Single app, full control | Our app needs admin, plus an MPL-2.0 notice/source obligation for the DLL. More code, and it overlaps LHM's own job. |
| C. HWiNFO shared memory | Very broad sensor coverage | The free tier's shared memory auto-disables after 12h, so it's not viable unattended. |

**Recommendation: A for v1**, behind a `SensorSource` adapter interface, so B (or the
prototype's CSV feed) can be added later without touching the UI.

Verified 2026-09-27 (sources in the research log below):
- LHM 0.9.6 (2026-02-14) serves `/data.json`: a tree of `Children[]`, where leaf sensors carry
  `SensorId`, `Text`, `Value`, `Min`, `Max`, `Type`.
- LHM uses **PawnIO** (not WinRing0) since PR #1857. PawnIO is a separate install (GPLv2+).
  Without it, CPU and board sensors are missing.
- LHM is MPL-2.0. Under option A we redistribute nothing from it.

## Components

1. **`SensorSource` adapters**
   - `LhmHttpSource` (v1 default): polls `data.json` at 1 Hz and flattens the tree into
     `{id, hardware, hardwareType, sensorType, name, value, unit}`.
   - `CsvSource` (optional): the prototype's `SensorSampler` CSV, so Alessa's machine keeps
     working unchanged.
2. **Server:** Python stdlib `http.server` (no pip dependencies).
   - Keeps an in-memory ring buffer (5 min) and session peaks.
   - Endpoints: `/api/snapshot`, `/api/meta` (detected hardware), `/api/health`.
3. **Classifier:** maps raw sensors to roles (`cpu.temp.package`, `gpu.temp.core`,
   `gpu.temp.memjunction`, `gpu.power`, `fan.*`, `storage.temp`, `dimm.temp`, `vrm.temp`, ...)
   using the LHM `SensorType` plus name heuristics.
   - Unknown sensors still show up in an "Other" group. Nothing is silently dropped.
4. **Threshold profiles:** `profiles/*.json` keyed by CPU/GPU model regex, e.g.
   - `9800X3D` → Tjmax 95
   - `RTX 50xx` core 90, memory junction 105
   - A conservative generic fallback for anything unmatched
   - User overrides in `config.json`
5. **Analysis engine:** the prototype's rules, generalized to roles:
   - status: HEALTHY, WATCH or CRITICAL
   - load phase detection
   - temperature slope and time-to-limit
   - throttle detection (full load with a low clock)
   - fan or pump stall
   - stale data source
6. **UI:** the prototype page, with tiles generated from `/api/meta` instead of hardcoded.
   - Sections are built from whatever hardware exists.
   - Dark/light theme.
7. **Notes hook (optional):** if `notes.json` (`{at, ts, lines[]}`) exists, it's shown under
   the analysis and dimmed once it's more than 30 min old. Any tool, including a Claude
   session, can write it.
8. **Install and autostart:** `install.ps1`, ASCII-only and PowerShell 5.1-safe.
   - Checks for Python 3.11+ and offers the `winget` install.
   - Checks LHM + PawnIO: prints the steps, never installs drivers silently.
   - Registers a per-user logon task that opens an Edge `--app` window.
   - `uninstall.ps1` reverses all of it.

## Acceptance criteria (v1)

- On a PC that is **not** Alessa's, with LHM running and its web server on, `install.ps1` then
  logon shows populated CPU/GPU/storage tiles with no code edits.
- With LHM missing or its web server off, the page shows a clear setup message, not a blank
  grid or a crash.
- Nothing ever binds to anything other than 127.0.0.1.
- Classifier unit tests pass against at least 3 recorded `data.json` fixtures (AMD+NVIDIA,
  Intel+AMD GPU, laptop).
- Analysis unit tests cover each rule's trigger and non-trigger, plus null/empty sensor values
  (no crash on `Value: null` or missing `Children`).
- The prototype's `CsvSource` still renders Alessa's machine identically.

## Open questions for Alessa

1. **Runtime:** plain Python (the recipient needs Python) vs a PyInstaller single `.exe` (no
   Python needed, but AV false positives are common with PyInstaller). Recommend: `.exe` for
   handoff builds, script for dev.
2. **Visibility and license:** keep the repo private and share builds, or make it public? If
   public, which license? (MIT fits option A, since we ship no LHM code.)
3. **Off-bus GPU edge panel:** that's specific to Alessa's rig. Keep it as an optional plugin
   fed by `CsvSource`, or drop it from the portable build?

## Research log

- LHM web server and JSON: home-assistant.io/integrations/libre_hardware_monitor, LHM issue #1737,
  docs.yasb.dev libre-hw-monitor widget, GitHub releases API (0.9.6, 2026-02-14)
- License: MPL-2.0 per the LHM README. PawnIO: github.com/namazso/PawnIO (GPLv2+), LHM PR #1857,
  discussion #2149
- NuGet LibreHardwareMonitorLib 0.9.6: netstandard2.0 / net452 / net5.0
- HWiNFO free shared-memory 12h cap: hwinfo.com/licenses
