# RUNBOOK: install, uninstall, upgrade

## Build a release

```
dotnet build HardwareLive.sln -c Release          # 0 warnings, TreatWarningsAsErrors
dotnet test HardwareLive.sln -c Release            # includes tools/check-scripts.ps1 + tools/test-installlib.ps1
node --test tests/ui/logic.test.mjs
powershell -ExecutionPolicy Bypass -File .\tools\package.ps1 -Version 0.1.0-alpha.1
```

`package.ps1` publishes `app\` and `sampler\` self-contained win-x64, bundles `licenses\`
(LICENSE, THIRD-PARTY-NOTICES.md, the full MPL-2.0 text for LibreHardwareMonitorLib), copies
`install.ps1`/`uninstall.ps1`/`HardwareLive.InstallLib.psm1`/`README-INSTALL.txt`, writes
`SHA256SUMS.txt`, and zips to `out\HardwareLive-<ver>-win-x64.zip`, printing its SHA-256. FPS
(PresentMon) is only bundled if `tools\fetch-presentmon.ps1` exists (step 7, separate work);
otherwise the zip ships without it and prints that.

## Install (on the target PC)

1. Extract the zip. Right-click `install.ps1` -> Properties -> Unblock (or just run with
   `-ExecutionPolicy Bypass` -- the Mark of the Web blocks scripts by default on a downloaded
   zip's contents).
2. `powershell -ExecutionPolicy Bypass -File .\install.ps1`
3. It self-elevates (UAC prompt), runs preflight (OS/PawnIO/Edge/port -- PASS/WARN/FAIL, aborts
   on FAIL), copies to `%ProgramFiles%\HardwareLive\{app,sampler,licenses}`, sets a protected
   ACL (SYSTEM+Administrators FullControl, Users ReadAndExecute -- verified and rolled back on
   failure), creates `%ProgramData%\HardwareLive\logs` with the same protected ACL, registers
   `\HardwareLive\Sampler` (SYSTEM) and `\HardwareLive\App` (the invoking user, Limited) logon
   tasks, offers FPS/Performance Log Users consent, starts both tasks, and polls
   `http://127.0.0.1:<port>/api/health` for up to 30s.
4. Unattended: `-EnableFps` / `-NoFps` skip the consent prompt; `-Port <n>` overrides the port.
5. Dry run: `install.ps1 -WhatIf` prints every action and changes nothing (verified: no
   Program Files/ProgramData/scheduled-task changes after a `-WhatIf` run).

**Upgrade**: re-run `install.ps1` from a newer package. It stops the old tasks/processes,
re-copies (robocopy `/MIR`), and never regresses `perfLogUsersAddedByApp` from `true` to
`false` (monotonic flag, `tools/HardwareLive.InstallLib.psm1`).

## Uninstall

```
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

Stops + unregisters both tasks (and the `\HardwareLive\` folder if empty), removes
`%ProgramFiles%\HardwareLive` and `%ProgramData%\HardwareLive` (`-KeepLogs` to keep the logs
subfolder), asks about `%LOCALAPPDATA%\HardwareLive` (`-KeepUserData`/`-RemoveUserData`,
default keep), and offers to remove the Performance Log Users membership only if
`install-state.json` says this app added it (`-RemovePerfLogUsers`/`-KeepPerfLogUsers`).
Idempotent; `-WhatIf` supported.

## Verification checklist (reviewer's supervised install/uninstall)

- [ ] `Get-Process hardware-live, hl-sampler` both running after install + a reboot.
- [ ] Process Explorer: `hardware-live.exe` and the Edge app window are Medium integrity;
      `hl-sampler.exe` is System.
- [ ] `icacls "%ProgramFiles%\HardwareLive"` shows only SYSTEM/Administrators write; Users is
      RX only.
- [ ] Task Scheduler `\HardwareLive\Sampler` / `\App`: confirm "Stop the task if it runs longer
      than" is unchecked (`ExecutionTimeLimit` = PT0S) -- `New-ScheduledTaskSettingsSet` maps
      `[TimeSpan]::Zero` to that on Win10+, worth an eyeball check once per Windows build.
- [ ] Kill `hardware-live.exe`: task restarts it within 1 minute.
- [ ] Second PC without PawnIO: guided banner, not zeros.
- [ ] Upgrade over an existing install keeps `%LOCALAPPDATA%\HardwareLive\layouts.json`.

## Rollback

Worst case: run `uninstall.ps1`, then reinstall from a known-good package. `install.ps1`
itself rolls back its own ACL step (deletes the just-copied `Program Files\HardwareLive` tree)
if ACL verification fails, so a partial/corrupt ACL state should never be left behind.

## Known gotchas

- **PawnIO**: never auto-installed; preflight WARNs with the exact `winget install
  namazso.PawnIO` command.
- **SmartScreen**: v1 is unsigned (documented, resolved decision). "More info -> Run anyway".
- **Layout separation**: `app\` (framework-dependent-looking but actually self-contained) and
  `sampler\` (self-contained) must stay separate folders -- the sampler's private runtime DLLs
  in the app folder hang the app at startup.
- **127.0.0.1, never `localhost`**: the server binds IPv4 loopback only; `localhost` can
  resolve to `::1` first on some machines, which nothing listens on.
- **Group membership at next logon**: adding Performance Log Users takes effect at the next
  logon, not immediately -- `install.ps1` tells the user this.
- **`Get-FileHash` can silently 404** on a machine where `$env:PSModulePath` resolves
  `Microsoft.PowerShell.Utility` to a pwsh-7-only build ahead of the native Windows PowerShell
  5.1 one (observed on this dev machine). `package.ps1` uses `System.Security.Cryptography.SHA256`
  directly instead, with no module dependency.
