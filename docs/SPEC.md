# Hardware Live: portable v1 spec

Status: **DRAFT r2, awaiting Alessa's approval.** No code until approved.
r2 folds in the Codex adversarial review (2026-09-27) and Alessa's layout/preset request.

## Goal

A local, shareable Windows hardware dashboard. It shows live CPU, GPU, board, RAM, storage
and fan telemetry with a rule-based health assessment and a **customizable, preset-driven
layout**. It must work on an arbitrary PC.

The prototype lives at `~/.claude/scripts/hwdash/` and is hardwired to one machine. This repo
generalizes it.

## Invariants (must never break)

1. **Read-only.**
   - We never call any LHM control or `Set` API.
   - We never write fan, clock or voltage values.
   - A unit test asserts that no control API is referenced.
2. **Loopback only.** The HTTP listener binds `127.0.0.1` explicitly.
   - Reject any request whose `Host` header isn't `127.0.0.1:<port>` or `localhost:<port>`
     (blocks DNS rebinding).
   - GET only, no CORS headers.
3. **No hidden installs.** Drivers (PawnIO) are detected and guided, never silently installed.

## Data source decision (changed in r2)

| Option | Verdict |
|---|---|
| A. LHM app's built-in web server (`:8085/data.json`) | **Rejected: violates invariant 2.** Verified in LHM source (`HttpServer.cs`, `MainForm.cs`): the default `listenerIp` is `"?"`, and any IP not found in `Dns.GetHostEntry(hostname).AddressList` is coerced to `"+"` (all interfaces). Loopback isn't in that list, so **it can't be made local-only**. Auth is off by default, and it accepts `Sensor?action=Set` (hardware writes). Enabling it on a friend's PC could expose fan and control writes to their LAN. |
| **B. Our own sampler on `LibreHardwareMonitorLib`** (NuGet 0.9.6, netstandard2.0) | **Chosen.** One process: we control the binding, read-only use, and lifecycle. |
| C. HWiNFO shared memory | Rejected: the free tier auto-disables after 12h. |

**Runtime: one self-contained .NET 9 single-file `.exe`** (C#, matching the org's desktop
convention; `desktop-app-template` is a candidate base, confirm before starting). This
settles r1's Python-vs-PyInstaller question: the recipient needs no runtime. The HTML/JS UI
is embedded as resources.

**Licensing:** LHM is MPL-2.0. We ship its DLL unmodified, include its license, and link to its
source. PawnIO (GPLv2+) is not bundled.

## Components

1. **Sampler.** `Computer` from LibreHardwareMonitorLib, all hardware groups enabled, polled at 1 Hz.
   - Numeric `float?` values only; null means "no reading" and never crashes anything.
   - Temperatures in °C from the library, not display strings.
   - Keeps a 5-min ring buffer, session peaks, and a CSV session log (opt-in).
2. **Loopback server.** `HttpListener` on `http://127.0.0.1:<port>/`, enforcing invariant 2.
   - `/api/snapshot`, `/api/meta` (hardware tree + roles), `/api/health`, static UI.
3. **Classifier** (sensor to role).
   - Uses, in priority order: the LHM `HardwareType`, then `SensorType`, then the stable
     `Identifier` path, and names only as the last fallback.
   - Roles: `cpu.temp.control` (the temperature that throttles), `cpu.power`, `cpu.clock.eff`,
     `gpu.temp.core`, `gpu.temp.hotspot`, `gpu.temp.mem`, `gpu.power`, `gpu.load`,
     `fan.cpu`, `pump`, `storage.temp`, `dimm.temp`, `vrm.temp`, and so on.
   - Each mapping carries a confidence. **If a mandatory role (CPU control temp, GPU core
     temp) can't be mapped confidently, health reports `UNKNOWN`, never `HEALTHY`.**
4. **Threshold profiles.**
   - A small table keyed by CPU/GPU model (e.g. 9800X3D Tjmax 95; RTX 50xx core 90 / mem 105).
   - A conservative generic fallback, plus user overrides in `config.json`.
5. **Analysis engine** (ported from the prototype, running on roles):
   - status: HEALTHY, WATCH, CRITICAL or UNKNOWN
   - load-phase detection
   - temperature slope and time-to-limit
   - throttle detection
   - fan or pump stall
   - stale sampler
6. **Customizable UI** (new in r2, see next section).
7. **Notes hook (optional).** Shows `notes.json` (`{at, ts, lines[]}`), dimmed after 30 min.
8. **Install and lifecycle** (`install.ps1`, ASCII, PowerShell 5.1-safe). One elevated
   per-user logon task runs the `.exe`.
   - Restart-on-failure: 3 tries, 1 min apart.
   - The `.exe` opens an Edge `--app` window once it's listening.
   - PawnIO missing means the UI shows a guided-install banner and CPU/board tiles read "needs
     PawnIO", not zero.
   - `uninstall.ps1` removes the task and config.
9. **Fixture tool.** `hardware-live.exe --dump fixture.json` captures the full sensor tree,
   so friends can send us fixtures from their hardware.

## Customizable layout: draggable tiles, filters, presets

- **Widgets:** every sensor, and every derived value, can be a widget.
  - Widget kinds: `tile` (value + sparkline + peak), `chart` (multi-series line), `gauge`,
    `analysis` (the status panel), `notes`.
  - Tiles resize in 3 sizes (S/M/L).
- **Drag to reorder** on a responsive grid, with native pointer events (no library).
  - Keyboard accessible: select a tile, then use the arrow keys to move it.
  - There's an explicit **Edit layout** toggle so tiles don't move by accident during normal use.
- **Filtering and granular picking:** a searchable sensor picker lets you add any individual
  sensor.
  - Filters: text, hardware (CPU/GPU/board/storage/...), sensor type (temp/power/clock/
    load/fan/voltage), and "only sensors with a role".
  - A quick filter bar hides or shows whole groups without editing the preset.
- **Presets** are named layouts: an ordered widget list with sizes, plus chart
  definitions and analysis focus. Built-in presets:

| Preset | Contents |
|---|---|
| Overview | The current prototype layout (default) |
| CPU | Control temp, per-CCD temps, package power, effective clock (avg + per core), load, CPU fan/pump, VRM; charts: temp+power, clock+load |
| GPU | Core, hotspot, memory temps; power + % of limit; core/mem clock; load; VRAM; fans; voltage |
| 3D Gaming | CPU control temp + clock + load, GPU core/hotspot/mem temps, GPU power, GPU clock + load, VRAM used, RAM used; chart: CPU vs GPU load (spots which side is the bottleneck) |
| Thermals | Every temperature sensor, sorted by headroom to its limit |
| Cooling | All fans + pump RPM and duty %, alongside the temps they cool |
| Storage | Drive temps, activity, SMART life/spare |

  - **Custom presets:** "Save as preset", rename, duplicate, delete. Built-ins can't be
    overwritten; editing one forks a custom copy.
  - Stored server-side in `layouts.json` (it survives browser resets and reinstalls), with
    **export/import** as JSON for sharing. Last-used preset is remembered.
  - Presets reference **roles**, not raw sensor IDs. The same "3D Gaming" preset then works
    on any PC, and missing roles are simply skipped.
  - Analysis focus follows the preset: the CPU preset surfaces CPU concerns first. Critical
    items always show, regardless of preset.

## Acceptance criteria (v1)

- **Security:**
  - A test proves the listener rejects a non-loopback `Host` header and non-GET methods.
  - From a second machine on the LAN, the port is unreachable (manual check, recorded in
    RUNBOOK).
  - A unit test proves no LHM control API is referenced.
- **Classifier:** golden expected-role files for each fixture, with at least 4 fixtures:
  AMD CPU + NVIDIA, Intel CPU + AMD GPU, Intel laptop + iGPU, and Alessa's 9800X3D/5090.
  - Includes ambiguous, duplicate and missing-sensor cases.
  - A missing mandatory role yields `UNKNOWN`.
- **Analysis:** each rule has trigger and non-trigger tests, plus null/NaN/absent values.
- **Lifecycle:** clean second PC, run `install.ps1`, reboot, and the dashboard opens populated
  with no manual steps.
  - Also covered: killing the `.exe` (it restarts within 1 min), PawnIO absent (guided
    banner, not zeros), and upgrading over an existing install (keeps `layouts.json`).
- **Layout:**
  - Drag-reorder persists across a restart.
  - Switching presets changes the visible widgets.
  - A preset exported on one machine and imported on another skips missing roles without
    errors.
  - Keyboard reorder works.
- **Parity:** the prototype's features are present, i.e. the analysis panel, trends and notes
  hook. Alessa's GPU off-bus panel is a **Later** plugin, since it's machine-specific.

## Open questions for Alessa

1. **Visibility and license:** keep it private and share builds, or go public? If public: MIT
   (our code) plus the MPL notice for the bundled LHM DLL.
2. **Code signing:** unsigned `.exe` files trigger SmartScreen on friends' PCs. Sign with the
   existing desktop-app-template key, or accept the warning for v1?
3. **FPS in the 3D Gaming preset:** LHM has no frame rate. Adding it means PresentMon (MIT, a
   separate binary). Put it in v1 or Later?

## Research log

- LHM web server risk: `LibreHardwareMonitor.Windows.Forms/Utilities/HttpServer.cs` L100-124
  (IP validation falls back to `"+"`; auth defaults), L242-250 & L306-358 (`Set` action / POST);
  `UI/MainForm.cs` L316-318 (`listenerIp` default `"?"`, auth default false). Read 2026-09-27.
- LHM 0.9.6 (2026-02-14), MPL-2.0; WinRing0 replaced by PawnIO (PR #1857); PawnIO separate,
  GPLv2+ (github.com/namazso/PawnIO)
- NuGet LibreHardwareMonitorLib 0.9.6: netstandard2.0 / net452 / net5.0
- HWiNFO free shared-memory 12h cap: hwinfo.com/licenses
- Codex adversarial review r1: verdict needs-attention (4 findings, all accepted: loopback,
  RawValue/units, classifier proof, lifecycle).
