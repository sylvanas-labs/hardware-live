# Hardware Live: portable v1 spec

Status: **APPROVED r4 (2026-09-27).** Open questions resolved; r4 adds FPS capture.
r2 folds in the Codex adversarial review (2026-09-27) and Alessa's layout/preset request.
r3 fixes r2's review: privilege split, authenticated layout writes, exact-sensor preset refs.

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
   - Read endpoints are GET only. Layout writes use POST/PUT/DELETE and require an
     `X-HL-Token` header matching a random per-launch token embedded in the served page.
   - A custom header forces a CORS preflight, which we never approve, so cross-origin pages
     can't forge writes.
   - Bodies are capped at 256 KB and schema-validated. No CORS headers, ever.
3. **No hidden installs.** Drivers (PawnIO) are detected and guided, never silently installed.
4. **Least privilege.** Only the sampler runs elevated. HTTP, JSON/layout parsing and the
   browser always run at the user's normal (medium) integrity level.

## Data source decision (changed in r2)

| Option | Verdict |
|---|---|
| A. LHM app's built-in web server (`:8085/data.json`) | **Rejected: violates invariant 2.** Verified in LHM source (`HttpServer.cs`, `MainForm.cs`): the default `listenerIp` is `"?"`, and any IP not found in `Dns.GetHostEntry(hostname).AddressList` is coerced to `"+"` (all interfaces). Loopback isn't in that list, so **it can't be made local-only**. Auth is off by default, and it accepts `Sensor?action=Set` (hardware writes). Enabling it on a friend's PC could expose fan and control writes to their LAN. |
| **B. Our own sampler on `LibreHardwareMonitorLib`** (NuGet 0.9.6, netstandard2.0) | **Chosen.** One process: we control the binding, read-only use, and lifecycle. |
| C. HWiNFO shared memory | Rejected: the free tier auto-disables after 12h. |

**Runtime: .NET 9, self-contained, two small executables** (r3 privilege split):
- `hl-sampler.exe` (elevated): LibreHardwareMonitorLib only. It **pushes** JSON snapshot
  frames one way over a named pipe. The pipe ACL grants the current user only, and the
  sampler accepts no inbound commands (it ignores anything read from the pipe).
- `hardware-live.exe` (unelevated): pipe client, HTTP server, layouts, analysis. It
  launches Edge.

This is (C#, matching the org's desktop
convention; `desktop-app-template` is a candidate base, confirm before starting). This
settles r1's Python-vs-PyInstaller question: the recipient needs no runtime. The HTML/JS UI
is embedded as resources.

**Licensing:** LHM is MPL-2.0. We ship its DLL unmodified, include its license, and link to its
source. PawnIO (GPLv2+) is not bundled.

## Components

1. **Sampler** (`hl-sampler.exe`, elevated, minimal). `Computer` from LibreHardwareMonitorLib, all hardware groups enabled, polled at 1 Hz.
   - Numeric `float?` values only; null means "no reading" and never crashes anything.
   - Temperatures in °C from the library, not display strings.
   - Streams frames over the pipe. Nothing else: no HTTP, no file parsing, no config writes.
2. **Loopback server** (`hardware-live.exe`, unelevated). `HttpListener` on
   `http://127.0.0.1:<port>/`, enforcing invariant 2.
   - Keeps a 5-min ring buffer, session peaks, and a CSV session log (opt-in).
   - GET: `/api/snapshot`, `/api/meta` (hardware tree + roles), `/api/health`, static UI.
   - Token-guarded writes: `POST/PUT/DELETE /api/layouts[/{id}]`, `POST /api/layouts/import`.
   - With no sampler frames arriving, the UI shows a "sampler not running" state.
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
8. **Install and lifecycle** (`install.ps1`, ASCII, PowerShell 5.1-safe). Two per-user logon
   tasks:
   - `HL-Sampler`: RunLevel Highest.
   - `HL-App`: RunLevel Limited. It waits for the pipe and opens an Edge `--app` window
     from the unelevated process.
   - Both restart on failure: 3 tries, 1 min apart.
   - PawnIO missing means the UI shows a guided-install banner and CPU/board tiles read "needs
     PawnIO", not zero.
   - `uninstall.ps1` removes both tasks and the config, and handles the Performance Log
     Users rollback (component 9).
9. **FPS capture (r4, opt-in).** Uses the PresentMon console app, MIT, **pinned v2.6.0**.
   - The exe is bundled with its license, and its SHA-256 is verified at build time. It is
     never downloaded at runtime.
   - It's spawned by the **unelevated** `hardware-live.exe` with `--output_stdout --no_csv
     --no_console_stats --v2_metrics --session_name HardwareLive --stop_existing_session`.
   - Needs no admin, only membership in the Windows **Performance Log Users** group.
     `install.ps1` offers to add the user, with an explicit consent prompt that explains it
     lets the user start ETW trace sessions. FPS stays off if they decline.
   - `install.ps1` records in `config.json` whether **it** added the membership
     (`perfLogUsersAddedByApp`) or found it already there. `uninstall.ps1` offers to remove
     it only when the app added it, explaining the effect, and never touches pre-existing
     membership.
   - **Target eligibility (r4.1).** The target is the foreground-window process
     (`GetForegroundWindow` → PID) **only if** all of the following hold:
     - it has presented at ≥ 20 frames/s for 3 consecutive 1 s buckets;
     - it isn't on the built-in denylist: `explorer`, `dwm`, browsers
       (`msedge`/`chrome`/`firefox`/`brave`/`opera`), launchers and overlays
       (`steam`/`steamwebhelper`/`EpicGamesLauncher`/`Battle.net`/`Discord`/`obs64`/
       `nvcontainer`), and our own Edge app window;
     - it isn't a system or session-0 process.

     Otherwise there's **no target** and FPS tiles show "–". There is **no** "most presents"
     fallback. Users can pin a process ("always track X") or extend the denylist in
     `config.json`.
   - Derived values (exact formulas, r4.1). Frames are bucketed by `CPUStartQPCTime`
     (`--qpc_time_ms`) into aligned, half-open 1 s buckets `[k, k+1000) ms`. `ft` is each
     frame's `MsBetweenPresents`. Every row PresentMon emits for the target counts,
     including dropped frames.
     - `fps.avg[k]` = 1000 · n_k / Σ ft over bucket k (no value when n_k < 2)
     - `fps.low1` = 1000 / P99(ft) over the frames in the last 60 buckets, using the
       nearest-rank percentile (no value when there are < 100 frames)
     - `frametime.ms[k]` = mean ft in bucket k
     - `frametime.jitter[k]` = population stddev of ft in bucket k
     - `fps.app` = the target's process name
   - PresentMon exits or crashes: restart with backoff; after 3 failures, show "FPS
     unavailable" rather than a stale number. No game running: FPS tiles show "–", not 0.
10. **Fixture tool.** `hardware-live.exe --dump fixture.json` captures the full sensor tree,
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
| 3D Gaming | **FPS avg + 1% low + frame-time chart + app name**, CPU control temp + clock + load, GPU core/hotspot/mem temps, GPU power, GPU clock + load, VRAM used, RAM used; chart: CPU vs GPU load (spots which side is the bottleneck) |
| Thermals | Every temperature sensor, sorted by headroom to its limit |
| Cooling | All fans + pump RPM and duty %, alongside the temps they cool |
| Storage | Drive temps, activity, SMART life/spare |

  - **Custom presets:** "Save as preset", rename, duplicate, delete. Built-ins can't be
    overwritten; editing one forks a custom copy.
  - Stored server-side in `layouts.json` (it survives browser resets and reinstalls), with
    **export/import** as JSON for sharing. Last-used preset is remembered.
  - **Widget references (r3).** Each widget has a `ref`:
    - `{role}`: built-ins use role selectors, so "3D Gaming" works on any PC. A multi-match
      role (e.g. `cpu.clock.core`) expands to every matching sensor, in identifier order.
    - `{id, hw, role?}`: custom widgets store the stable LHM `Identifier` (e.g.
      `/amdcpu/0/clock/3`) plus hardware name/identifier, with an optional role fallback.
      This keeps individual, per-core, duplicate-role and unclassified sensors exact.
    - Resolution order on load or import: exact `id` on matching hardware, then the `role`
      fallback, then skip with a visible "N widgets unavailable on this PC" notice.
  - Analysis focus follows the preset: the CPU preset surfaces CPU concerns first. Critical
    items always show, regardless of preset.

## Acceptance criteria (v1)

- **Security:**
  - A test proves the listener rejects a non-loopback `Host` header on every route.
  - A method/path matrix test covers every combination. The only allowed non-GET calls are
    the token-guarded `POST/PUT/DELETE /api/layouts[/{id}]` and `POST /api/layouts/import`.
    Every other method on every other route, and any mutation verb on read routes, returns 405.
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
- **Privilege split:** `hardware-live.exe` and Edge show medium integrity in Process Explorer
  and only `hl-sampler.exe` is elevated. A test proves the sampler ignores inbound pipe
  data, and the pipe ACL denies other users.
- **Write auth:** tests show that a write without a token, a write with a wrong token,
  an oversize body and a malformed schema are all rejected. A cross-origin page (different
  port) can't mutate layouts.
- **Layout:**
  - Drag-reorder persists across a restart.
  - Round-trip test: a custom layout with per-core clocks, two duplicate-role fans and an
    unclassified sensor saves and reloads to exactly the same widgets.
  - Switching presets changes the visible widgets.
  - A preset exported on one machine and imported on another skips missing roles without
    errors.
  - Keyboard reorder works.
- **FPS:**
  - Deterministic oracle: `tests/oracle/fps_oracle.py`, an independent reference
    implementation of the formulas above, runs over recorded `--v2_metrics` fixtures (at
    least one 60 s game capture with dropped frames). The C# output must match the oracle
    for **every** aligned bucket's `fps.avg`, `frametime.ms` and `frametime.jitter`, and for
    `fps.low1`, within 0.01.
  - Target cases:
    - idle desktop gives "–"
    - a browser playing 60 fps video in the foreground gives "–" (denylist)
    - a launcher in the foreground while a game runs behind it gives "–"
    - a game in the foreground gives a value
    - a pinned process gives a value even when it isn't in the foreground
  - Permission round-trip:
    - a user not in the group: install with consent, then uninstall and accept removal
      leaves them not in the group
    - a user already in the group: install then uninstall leaves the membership untouched
  - Not in Performance Log Users: FPS tiles show a "needs permission" hint, and nothing
    crashes.
  - Killing PresentMon leads to restart, then "FPS unavailable" after 3 failures.
  - A test fixture parses recorded `--v2_metrics` stdout.
- **Parity:** the prototype's features are present, i.e. the analysis panel, trends and notes
  hook. Alessa's GPU off-bus panel is a **Later** plugin, since it's machine-specific.

## Resolved decisions (Alessa, 2026-09-27)

1. **Public repo, MIT license** for our code. `THIRD-PARTY-NOTICES.md` covers LHM (MPL-2.0,
   unmodified DLL + source link) and PresentMon (MIT).
2. **Unsigned v1.** SmartScreen will warn, and the README documents "More info, then Run
   anyway". Signing is Later.
3. **FPS is in v1** via PresentMon (component 9).

## Research log

- LHM web server risk: `LibreHardwareMonitor.Windows.Forms/Utilities/HttpServer.cs` L100-124
  (IP validation falls back to `"+"`; auth defaults), L242-250 & L306-358 (`Set` action / POST);
  `UI/MainForm.cs` L316-318 (`listenerIp` default `"?"`, auth default false). Read 2026-09-27.
- LHM 0.9.6 (2026-02-14), MPL-2.0; WinRing0 replaced by PawnIO (PR #1857); PawnIO separate,
  GPLv2+ (github.com/namazso/PawnIO)
- NuGet LibreHardwareMonitorLib 0.9.6: netstandard2.0 / net452 / net5.0
- HWiNFO free shared-memory 12h cap: hwinfo.com/licenses
- PresentMon: github.com/GameTechDev/PresentMon, MIT, v2.6.0 (2026-09-21); console flags per
  README-ConsoleApplication.md; "Performance Log Users" requirement + admin caveat
  (cross-user/short-lived processes show `<unknown>`) per README.md. Read 2026-09-27.
- Codex adversarial review r1: verdict needs-attention (4 findings, all accepted: loopback,
  RawValue/units, classifier proof, lifecycle).
- Codex scoped review of r4 (vs 49406b8): 3 medium findings (permission rollback, FPS target
  eligibility, FPS oracle), all accepted and fixed in r4.1.
- Codex scoped re-verify of r2 (vs 94c26b5): confirmed r1's 4 resolved; 3 new medium findings
  (GET-only vs layout writes, elevated monolith, role-only presets), all accepted and fixed in r3.
