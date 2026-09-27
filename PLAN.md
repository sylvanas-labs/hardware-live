# PLAN

Full spec: [docs/SPEC.md](docs/SPEC.md) (APPROVED r4).

## Now
- [ ] Next: Soon step 1 (FPS via PresentMon v2.6.0 + 3D Gaming preset wiring)
- [ ] When step 7 (FPS/PresentMon) lands, wire `tools/fetch-presentmon.ps1` + its LICENSE
      output path into `tools/package.ps1` (contract stubbed: `-OutputDirectory`, must place a
      `LICENSE` file there) and re-run `package.ps1` to confirm FPS gets bundled.

## Soon (in order)
1. FPS via PresentMon v2.6.0 (unelevated, Performance Log Users opt-in) + 3D Gaming preset wiring

## Later
- GPU off-bus edge panel as an optional plugin (Alessa's rig)
- Code signing (v1 ships unsigned; SmartScreen documented)
- Release pipeline on GitHub Releases
- Light theme polish, CSV export of a session, alert sounds

## Done
- 2026-09-27 Packaging + install/uninstall/lifecycle complete (step 8): `tools/package.ps1`
  (self-contained win-x64 app+sampler, licenses incl. full MPL-2.0 text, SHA256SUMS.txt,
  zip); `install.ps1` (self-elevate capturing the pre-elevation SID, PASS/WARN/FAIL preflight,
  robocopy `/COPY:DAT` + verified-and-rolled-back protected ACL on Program Files and
  ProgramData, `\HardwareLive\Sampler`(SYSTEM)/`App`(Limited) logon tasks, monotonic FPS/
  Performance Log Users consent + `install-state.json`, health poll, `-WhatIf`); matching
  `uninstall.ps1` (idempotent, permission rollback, `-WhatIf`); `tools/HardwareLive.InstallLib.psm1`
  + `tools/test-installlib.ps1` (32 admin-free unit tests: SID validation matching the
  sampler's own rules, ACL allow-list predicate incl. owner check, monotonic flag merge, JSON
  merge preserving unknown keys); `tools/check-scripts.ps1` (PS 5.1 parse-sweep + ASCII scan,
  wired into `dotnet test` via `PowerShellFactAttribute`); app-side reopen path
  (`--open`/`--no-window`/`openWindowOnStart`, named-mutex single instance, Edge `--app`
  window with default-browser fallback, `hardware-live.exe` AssemblyName) with 19 new xunit
  tests. 22 new C# tests total; all 491 xunit + 28 node tests green.
- 2026-09-27 Presets complete (step 6): compiled Overview/CPU/GPU/3D Gaming/Thermals/
  Cooling/Storage presets, atomic `%LOCALAPPDATA%\HardwareLive\layouts.json` custom-layout
  persistence and recovery, active preset + °C/°F settings, custom CRUD/fork/export/import,
  portable role/exact-sensor refs, headroom/focus ordering, unavailable-widget reporting, and
  server/client temperature presentation conversion
- 2026-09-27 UI polish pass: `/api/meta` `labels` map (role title + disambiguating subtitle,
  replacing raw LHM names on tiles/legends/trends/picker), one grid tile per multi-instance
  sensor (`grid-auto-flow: dense`), °C everywhere, chart titles by content+unit with an
  8-color color-blind-friendlier palette + disconnected-series exclusion + fixed-axis fix,
  uniform tile heights, and test-isolated sampler pipe names (`PipeNames.ForTest()`); 12 new
  C# tests + 3 new `tests/ui/logic.test.mjs` tests
- 2026-09-27 Widget grid UI complete (step 5): dashboard served as embedded static assets
  (strict CSP, X-Content-Type-Options, no-store, explicit allow-list, no filesystem reads at
  request time), tile/chart/gauge/analysis/notes widgets, pointer + keyboard drag reorder in an
  explicit edit mode, sensor picker with search/filters/grouping, quick filter bar
  (localStorage), default layout, polling with backoff + visibility pause, startup-window
  health state (`starting` vs `sampler not running`, `uptimeSeconds`), `GET /api/notes` +
  `docs/NOTES-HOOK.md`, chart series/max layout-schema extension; 117 new C# tests +
  `tests/ui/logic.test.mjs` (17 tests, plain `node --test`)
- 2026-09-27 Threshold profiles + analysis engine complete: user-override -> device-limit ->
  vendor-profile -> generic-community threshold resolution (`Core/Profiles/`), the
  rule-based health analyzer (status/concerns/trends/phase/headline, `Core/Analysis/`), and
  `%LOCALAPPDATA%\HardwareLive\config.json` (falls back to the app-base file) wired into
  `/api/meta` (`thresholds`, `profile`) and `/api/health`; 51 new tests
- 2026-09-27 Classifier complete: sensor-to-role mapping (Type>SensorType>Identifier>Name), 5 golden-role fixtures, limit-sensor metadata, `/api/meta` roles + `/api/health` UNKNOWN(unmapped) reasons
- 2026-09-27 Sampler complete: read-only LHM collection, one-way ACL pipe, verified client, telemetry APIs/store, and `--dump` fixture tool
- 2026-09-27 Step 1 complete: .NET 10 WinExe scaffold, loopback-only Kestrel server, authenticated in-memory layout routes, and security tests
- 2026-09-27 Spec approved (r4): public/MIT, unsigned v1, FPS in v1
- 2026-09-27 Prototype built and running on Alessa's PC (`~/.claude/scripts/hwdash/`)
- 2026-09-27 Codex adversarial review r1 (4 findings accepted; LHM web server rejected, verified in source)
- 2026-09-27 Data-source research (LHM web server, PawnIO, licensing, HWiNFO limits)
