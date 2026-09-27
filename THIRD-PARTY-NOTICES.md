# Third-party notices

Hardware Live's own code is MIT-licensed (see `LICENSE`). Release builds bundle the following
components unmodified, under their own licenses:

| Component | Version | License | Source |
|---|---|---|---|
| LibreHardwareMonitorLib | 0.9.6 | Mozilla Public License 2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| PresentMon (console application) | 2.6.0 | MIT | https://github.com/GameTechDev/PresentMon |

Full license texts ship alongside the binaries in `licenses/` in every release.

**Not bundled:** PawnIO (GPL-2.0-or-later, https://github.com/namazso/PawnIO) is a separately
installed driver required for CPU and motherboard sensors. The installer links to it; it never
installs it silently.
