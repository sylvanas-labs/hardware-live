# PLAN

Full spec: [docs/SPEC.md](docs/SPEC.md) (DRAFT: awaiting approval before any code).

## Now
- [ ] Alessa: approve the spec and answer its 3 open questions (runtime, visibility/license, off-bus panel)

## Soon (after approval, in order)
1. `SensorSource` interface + `LhmHttpSource` + recorded `data.json` fixtures
2. Classifier (sensor to role) + unit tests against the fixtures
3. Threshold profiles + config overrides
4. Analysis engine ported from the prototype + unit tests (incl. null-value cases)
5. Server endpoints (`/api/snapshot`, `/api/meta`, `/api/health`) + dynamic UI
6. `CsvSource` for the prototype's SensorSampler feed (Alessa's machine parity)
7. `install.ps1` / `uninstall.ps1` + logon task; test on a second PC

## Later
- Option B: our own `LibreHardwareMonitorLib` sampler (single app, no separate LHM)
- PyInstaller build + GitHub release pipeline
- Light theme polish, CSV export of a session, alert sounds

## Done
- 2026-09-27 Prototype built and running on Alessa's PC (`~/.claude/scripts/hwdash/`)
- 2026-09-27 Data-source research (LHM web server, PawnIO, licensing, HWiNFO limits)
