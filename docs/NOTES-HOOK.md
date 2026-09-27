# Notes hook

Hardware Live's dashboard has an optional "Notes" widget for freeform commentary alongside the
built-in, deterministic "Health analysis" panel. It is entirely optional (docs/SPEC.md
Invariant 5: "AI is optional, never required") -- with no notes file, the widget renders
**nothing at all**: no placeholder, no "waiting for notes" text, no mention of any tool.

Any tool -- a script, a scheduled task, a human editing the file by hand, or an AI coding
assistant -- can write notes by dropping a JSON file at:

```
%LOCALAPPDATA%\HardwareLive\notes.json
```

The app polls `GET /api/notes` every 30 seconds. It reads this file, and only this file: there
is no way to write notes over HTTP, and the app itself never generates or edits notes content.

## File format

```json
{
  "at": "2026-09-27 14:32",
  "ts": 1758982320,
  "source": "my-monitoring-script",
  "lines": [
    "GPU core temp trending up ~0.6 C/min under load.",
    "Nothing critical yet; worth a look if it keeps climbing."
  ]
}
```

| Field    | Type            | Required | Notes                                                        |
|----------|-----------------|----------|----------------------------------------------------------------|
| `at`     | string          | yes      | A human-readable label for when the note was written, 1-200 chars. Shown as-is (control characters stripped). |
| `ts`     | number          | yes      | Unix timestamp **in seconds**, used to compute the note's age. |
| `source` | string          | no       | Who/what wrote it, 1-100 chars, e.g. `"my-monitoring-script"`. Shown only if present -- the app never names a tool on its own. |
| `lines`  | array of string | yes      | Up to 20 lines, each up to 500 chars. Rendered as plain text only (never HTML/Markdown). |

No other properties are accepted; an object with an unrecognized property is treated as invalid.

## Validation and failure behavior

The file is untrusted input. The app:

- Rejects it if it's larger than 16 KB, has more than 20 lines, or any line longer than 500
  characters.
- Strips control characters from every string field before display.
- Treats a missing, malformed, or otherwise invalid file exactly like an absent one: `GET
  /api/notes` returns `204 No Content`, the widget shows nothing, and the condition is logged
  server-side. The app never crashes on a bad notes file.

## Freshness

Once a note is more than 30 minutes old, the widget dims it and adds a label: `from <source>,
N min ago` (or just `N min ago` if `source` was omitted).

## Optional example: writing notes from an agentic coding session

If you're running an AI coding assistant (Claude Code, Codex, or anything else) alongside
Hardware Live and want it to leave a note after investigating something, have it write the file
directly, e.g. from a PowerShell one-liner:

```powershell
$notes = @{
  at     = (Get-Date).ToString('yyyy-MM-dd HH:mm')
  ts     = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
  source = 'agent session'
  lines  = @('Checked the fan curve after the WATCH concern -- looks intentional, not a stall.')
}
$dir = Join-Path $env:LOCALAPPDATA 'HardwareLive'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$notes | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $dir 'notes.json')
```

This is entirely optional tooling outside the app itself -- Hardware Live ships with no
integration for any specific assistant, and works identically with no notes file at all.
