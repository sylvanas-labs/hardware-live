# PLAN

Full spec: [docs/SPEC.md](docs/SPEC.md) (APPROVED r4).

## Now
- [ ] Step 1 of Soon (scaffold + loopback server + security tests)

## Soon (after approval, in order)
1. .NET 10 scaffold (check desktop-app-template fit) + loopback `HttpListener` with Host/GET guards + security tests
2. Sampler on LibreHardwareMonitorLib 0.9.6 (read-only) + `--dump` fixture tool
3. Classifier (HardwareType > SensorType > Identifier > name) + golden-role fixtures (4 machines) + UNKNOWN health
4. Threshold profiles + analysis engine ported from prototype + rule tests (incl. null/NaN)
5. Widget grid UI: drag reorder (edit mode, keyboard), sensor picker + filters
6. Presets (Overview/CPU/GPU/3D Gaming/Thermals/Cooling/Storage) + custom save/export/import in `layouts.json`
7. FPS via PresentMon v2.6.0 (unelevated, Performance Log Users opt-in) + 3D Gaming preset wiring
8. `install.ps1`/`uninstall.ps1`: elevated logon task, restart-on-failure, PawnIO guidance; clean-PC reboot test

## Later
- GPU off-bus edge panel as an optional plugin (Alessa's rig)
- Code signing (v1 ships unsigned; SmartScreen documented)
- Release pipeline on GitHub Releases
- Light theme polish, CSV export of a session, alert sounds

## Done
- 2026-09-27 Spec approved (r4): public/MIT, unsigned v1, FPS in v1
- 2026-09-27 Prototype built and running on Alessa's PC (`~/.claude/scripts/hwdash/`)
- 2026-09-27 Codex adversarial review r1 (4 findings accepted; LHM web server rejected, verified in source)
- 2026-09-27 Data-source research (LHM web server, PawnIO, licensing, HWiNFO limits)
