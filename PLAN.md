# PLAN

Full spec: [docs/SPEC.md](docs/SPEC.md) (APPROVED r4).

## Now
- [ ] Next: Soon step 1 (sampler + fixture tool)

## Soon (in order)
1. Sampler on LibreHardwareMonitorLib 0.9.6 (read-only) + `--dump` fixture tool
2. Classifier (HardwareType > SensorType > Identifier > name) + golden-role fixtures (4 machines) + UNKNOWN health
3. Threshold profiles + analysis engine ported from prototype + rule tests (incl. null/NaN)
4. Widget grid UI: drag reorder (edit mode, keyboard), sensor picker + filters
5. Presets (Overview/CPU/GPU/3D Gaming/Thermals/Cooling/Storage) + custom save/export/import in `layouts.json`
6. FPS via PresentMon v2.6.0 (unelevated, Performance Log Users opt-in) + 3D Gaming preset wiring
7. `install.ps1`/`uninstall.ps1` (launcher must open `http://127.0.0.1:<port>`, not `localhost` - server binds IPv4 loopback only): elevated logon task, restart-on-failure, PawnIO guidance; clean-PC reboot test

## Later
- GPU off-bus edge panel as an optional plugin (Alessa's rig)
- Code signing (v1 ships unsigned; SmartScreen documented)
- Release pipeline on GitHub Releases
- Light theme polish, CSV export of a session, alert sounds

## Done
- 2026-09-27 Step 1 complete: .NET 10 WinExe scaffold, loopback-only Kestrel server, authenticated in-memory layout routes, and security tests
- 2026-09-27 Spec approved (r4): public/MIT, unsigned v1, FPS in v1
- 2026-09-27 Prototype built and running on Alessa's PC (`~/.claude/scripts/hwdash/`)
- 2026-09-27 Codex adversarial review r1 (4 findings accepted; LHM web server rejected, verified in source)
- 2026-09-27 Data-source research (LHM web server, PawnIO, licensing, HWiNFO limits)
