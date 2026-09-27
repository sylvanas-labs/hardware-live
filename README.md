# Hardware Live

A local, live Windows hardware dashboard. It shows temperatures, power, clocks and fans, with a
rule-based health analysis that reports status, concerns, trends and time-to-limit.

**Status:** planning. See [PLAN.md](PLAN.md) and [docs/SPEC.md](docs/SPEC.md).
A working single-machine prototype exists; this repo is the portable version.

Read-only by design: it never writes to hardware or controls fans. It listens on
`127.0.0.1` only.

## Heads-up: Windows SmartScreen

v1 builds are **not code-signed**, so Windows will show "Windows protected your PC" on first
run. Click **More info**, then **Run anyway**. Signed builds are planned.

## License

MIT (see [LICENSE](LICENSE)). Bundled third-party components are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
