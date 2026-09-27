# Hardware Live

A local, live Windows hardware dashboard. It shows temperatures, power, clocks and fans, with a
rule-based health analysis that reports status, concerns, trends and time-to-limit.

**Status:** planning. See [PLAN.md](PLAN.md) and [docs/SPEC.md](docs/SPEC.md).
A working single-machine prototype exists; this repo is the portable version.

Read-only by design: it never writes to hardware or controls fans. It listens on
`127.0.0.1` only.

## Development

- `dotnet build HardwareLive.sln -c Release` -- builds all projects (0 warnings, treated as
  errors).
- `dotnet test HardwareLive.sln -c Release` -- runs the C# test suite.
- `node --test tests/ui/logic.test.mjs` -- runs the dashboard UI's pure-logic tests (no npm
  packages needed; Node's built-in test runner). On some Node builds, passing the bare
  directory (`node --test tests/ui/`) fails to discover the file at all (`MODULE_NOT_FOUND`) --
  if that happens for you, either point at the file directly as above, or run bare `node --test`
  from the repo root, which auto-discovers it.

## Heads-up: Windows SmartScreen

v1 builds are **not code-signed**, so Windows will show "Windows protected your PC" on first
run. Click **More info**, then **Run anyway**. Signed builds are planned.

## License

MIT (see [LICENSE](LICENSE)). Bundled third-party components are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
