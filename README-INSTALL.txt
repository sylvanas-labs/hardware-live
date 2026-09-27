Hardware Live -- install instructions
======================================

0. Extract this zip only into a folder you control (e.g. your own Downloads or a folder under
   your user profile) -- never into a shared/world-writable folder like C:\Temp or a network
   share, and never over top of an existing extraction owned by someone else. Then verify the
   download before running anything:
     Get-FileHash .\HardwareLive-<ver>-win-x64.zip -Algorithm SHA256
   or, if Get-FileHash is unavailable (see "Known gotchas" in RUNBOOK.md):
     certutil -hashfile .\HardwareLive-<ver>-win-x64.zip SHA256
   Compare the result against the SHA-256 published alongside the release (also included as
   SHA256SUMS.txt inside the zip, covering every extracted file). Do not run install.ps1 if it
   doesn't match.

1. Extract this zip anywhere (e.g. your Downloads folder or Desktop).

2. Windows blocks scripts downloaded from the internet by default (every file in this zip is
   stamped "downloaded from the internet" -- the Mark of the Web). Two ways to run install.ps1
   anyway:

   a) Right-click install.ps1 -> Properties -> check "Unblock" -> OK. Then double-click
      install.ps1, or run it from PowerShell:
        powershell -ExecutionPolicy Bypass -File .\install.ps1

   b) Or just run it with -ExecutionPolicy Bypass directly (this does not unblock the other
      files, only lets this one script run this one time):
        powershell -ExecutionPolicy Bypass -File .\install.ps1

3. Windows SmartScreen may warn "Windows protected your PC" the first time hardware-live.exe or
   hl-sampler.exe runs -- this release is unsigned (v1, documented; code signing is planned
   later). Click "More info", then "Run anyway". This is a one-time warning per executable.

4. install.ps1 will ask Windows for administrator rights (a UAC prompt) -- this is required to
   install into Program Files, create the SYSTEM-run sampler task, and set the protected file
   permissions the elevated task depends on. Only the sampler ever runs elevated; the dashboard
   itself always runs as your normal user.

5. During install you may see:
   - A warning that PawnIO isn't installed. PawnIO reads CPU/motherboard sensors. install.ps1
     never installs it for you -- it prints the exact command
     (winget install namazso.PawnIO) so you can decide.
   - A prompt about "Performance Log Users" -- only if you want FPS/frame-time capture. This
     is optional, reversible (uninstall.ps1 offers to remove it), and does not grant admin
     rights.

6. When install.ps1 finishes, the dashboard opens automatically at
   http://127.0.0.1:8790/ (not "localhost" -- the server only listens on the IPv4 loopback
   address). It will also open automatically at every logon from now on.

7. To uninstall, run uninstall.ps1 the same way (right-click -> Unblock, or
   -ExecutionPolicy Bypass). It asks whether to keep your saved layouts (default: keep).

Command-line options
---------------------
install.ps1   -WhatIf              preview every change without making it
              -EnableFps / -NoFps  skip the interactive FPS consent prompt
              -Port <n>            use a port other than the previous/default 8790

uninstall.ps1 -WhatIf
              -KeepUserData / -RemoveUserData
              -KeepLogs
              -RemovePerfLogUsers / -KeepPerfLogUsers
