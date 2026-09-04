[简体中文](README.md) | [English](README.en.md)

# Codex Usage Monitor

A native Windows system-tray app that summarizes usage from local Codex records and the official `codex app-server`.

This project is released under the [MIT License](LICENSE). You may use, modify, and redistribute it for free as long as you retain the copyright and license notices.

## Features

- General weekly quota, reset time, and consumption pace forecast based on an even seven-day allocation. When the server returns a valid five-hour window, the overview automatically shows the general five-hour quota at the top; otherwise that row is hidden.
- Separate five-hour and weekly Spark limits. They are shown only for Pro or higher plans and only when the server returns a Spark quota.
- Official monthly, 7-day, and 30-day totals sum only the daily Tokens returned by `account/usage/read`. For Today only, when the official bucket has not appeared yet, the app may show an “≈” temporary local sample; an official value replaces it immediately and is never added to it. Local model, cache, and cost analysis remains separate.
- Per-model Token usage, cache hit rate, and API-equivalent cost from local log samples. Estimated costs use the “≈” prefix and show public price coverage of local samples; the app does not invent prices for internal models without published pricing.
- Model page with real time filters for Today, 7 Days, and 30 Days, ranking bars, and a usage donut chart.
- Separate non-cached input, cached input, visible output, and reasoning Tokens without double counting.
- On each app launch, concurrently reads the Markdown pricing tables from OpenAI's official model documentation once. A complete cache is replaced atomically only when prices for every supported model are retrieved successfully; partial results are merged for the current view only and never overwrite the complete cache.
- Local SQLite history. The Models page keeps total local tokens, current API-equivalent cost (actual cached pricing), cache hit/savings, and the API-equivalent cost without caching for the same tokens on one Today / 7 days / 30 days selection, and identifies whether prices came from this launch, a cache, or the built-in snapshot. The History page defaults to the current month and also supports previous/next month, explicit year/month, and inclusive custom date ranges; its local-sample card compares the current cached-price cost with the no-cache cost for the same tokens. Official range totals and local samples remain separate; offline or partial daily-usage results show cached/mixed freshness, last-success time, and coverage-through date.
- Reset-card details including available count, grant time, validity period, nearest expiration time, and locally observed grant/use/expiration events.
- Windows tray notifications 7, 3, and 1 day before expiration. Each expiration threshold is notified at most once per app run.
- A title-bar Settings popover switches light/dark appearance and Chinese/English, and provides an explicit “Start with Windows (in background)” switch. Preferences are stored locally and restored on the next launch; startup remains off by default and requires no administrator privileges.
- By default, the app anonymously checks this repository's latest stable GitHub Release. After a successful check it does not automatically access the network again for 24 hours; a failed check may retry on a later launch, and a manual check bypasses this interval. It reads only the version and official release-page URL; it never downloads update files or replaces, moves, or deletes the installation directory. When a newer version exists, the title bar can open the official Release page or snooze the reminder, and automatic checks can be disabled in Settings.
- Automatic refresh every 15 minutes. Global backoff of 15 / 30 / 60 / 120 minutes applies only when the app-server process fails or all three account methods fail; a single-method failure falls back to historical data only for the affected partition. Manual refresh has a two-minute cooldown.
- If the server reports available reset cards but omits individual card details, one refresh may retry the detail query up to two times. These remain read-only queries and do not invoke a model or consume Tokens.

> “API-equivalent cost” converts local Tokens using published API prices. It is a reference estimate, not the actual ChatGPT/Codex subscription bill. Reset-card use or expiration events may be inferred from consecutive snapshots; the UI labels inferred events accordingly.

## Data sources and privacy

- Quota, plan, official daily totals, and reset cards: starts a trusted local OpenAI Codex executable and communicates with `codex app-server --stdio` through standard input/output. It does not directly read or copy `auth.json`.
- Model, input/output, cache, and reasoning breakdown: read-only scan of rollout JSONL files under `%USERPROFILE%\.codex\sessions` and `archived_sessions`.
- Local database and pricing snapshot: `%LOCALAPPDATA%\CodexUsageMonitor`.
- SQLite stores hashed rollout source identifiers and parsing checkpoints (which may include a local session ID), plus quota, reset-card, and daily-usage metadata. It does not store prompts or conversation bodies. This version retains those statistics indefinitely by default and has no automatic retention policy or in-app delete button. To clear them, exit the app first, then delete `usage.db`, `usage.db-wal`, and `usage.db-shm` from that directory.
- Apart from retrieving OpenAI's official pricing pages at startup, the app does not upload local conversation content.
- Update checks access only `https://api.github.com/repos/patrickzw1/CodexUsageMonitor/releases/latest`. Requests contain no GitHub Token and send no Codex usage, account data, prompts, conversation bodies, local paths, or device identifiers. The app caches only the latest stable version and a constrained official Release-page URL for this repository; it never downloads a ZIP, creates update staging directories, or modifies installation files.
- Refresh performs only local file scanning and account/quota reads. It does not create model inference requests or consume model Tokens, although it does generate a small number of authenticated metadata reads through Codex.
- Reset-card reminders use only the `expiresAt` value returned by the server. If the server returns only a count or grant time, the app does not infer an expiration date and temporarily disables the reminder switch.

> `account/read`, `account/rateLimits/read`, and `account/usage/read` are mutable protocol dependencies of the local Codex app-server, not stable public APIs guaranteed by this project. General five-hour/weekly quota, Spark, reset cards, summary, and daily usage are evaluated from the fields returned by the server. If a method is unavailable, an array is empty, fields change, or the process cannot start, only the affected partition is marked unavailable or falls back to the last successful value with its timestamp. Local rollout Token and cost refresh continues.

Security boundary: the app never executes an arbitrary same-named program from `PATH`. `CODEX_USAGE_MONITOR_CODEX_PATH` accepts only an absolute local path; default candidates are restricted to Codex installation/local-copy directories and must pass OpenAI publisher-signature validation. A single app-server stdout JSON frame is limited to 1 MiB of characters, stderr retains at most 64 KiB, each of the six official pricing responses is limited to 2 MiB, and each rollout line is limited to 1 MiB and persisted in batches.

## Downloads and verification

Regular users can download the versioned Windows portable ZIP and `SHA256SUMS.txt` from the [latest GitHub Release](https://github.com/patrickzw1/CodexUsageMonitor/releases/latest).

The app only shows an update reminder and opens the official Release page. To update manually: close the old app; download the portable ZIP from the official Release page; extract it into a new empty directory; then run `CodexUsageMonitor.exe` from that new directory. Do not extract over the old installation directory.

User data and settings live under `%LOCALAPPDATA%\CodexUsageMonitor`, so nothing needs to be copied from the installation directory. The old program directory and any sibling `.update-backup-*` / `.update-failed-*` directories left by earlier versions are entirely for the user to review and handle. The app never moves or deletes them automatically.

After downloading, compare the SHA-256 values in the download directory (using `0.2.1` as an example):

```powershell
Get-FileHash .\CodexUsageMonitor-0.2.1-win-x64.zip -Algorithm SHA256
Get-Content .\SHA256SUMS.txt
```

The two hashes must match. GitHub's automatically generated **Source code (zip/tar.gz)** archives are source snapshots, not directly runnable software. Regular users should download the portable ZIP generated by this project under Release assets.

## Versioning policy

The project uses semantic versioning. Git tags use `vX.Y.Z`; the version passed to the build script omits the `v` prefix:

- `0.Y.0`: feature releases before 1.0. Increment `Y` for user-visible features, pages, or substantial data capabilities.
- `0.Y.Z`: patch releases within a feature line. Increment `Z` for bug, security, performance, documentation, or packaging fixes only.
- `1.0.0`: the product, data format, and release process are stable for long-term use.
- `X.0.0` (`X >= 2`): incompatible changes to settings, data formats, or core behavior.
- Prereleases use tags such as `v0.3.0-beta.1` and do not replace the stable Release for that version.

## Development and running from source

Requires Windows 10/11 and the .NET SDK 8.0.424 pinned by the repository's `global.json`. Self-contained releases pin the .NET 8.0.30 runtime:

```powershell
dotnet restore CodexUsageMonitor.sln -r win-x64 --locked-mode
dotnet build CodexUsageMonitor.sln
dotnet run --project src\CodexUsageMonitor\CodexUsageMonitor.csproj -- --show
dotnet test CodexUsageMonitor.sln -c Release -m:1 --no-restore
```

The anonymous performance regression benchmark creates only deterministic temporary JSONL and does not read a real `.codex` directory or existing `usage.db`:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\measure-performance.ps1
```

Double-clicking the app shows the window immediately. Starting it again activates the existing window instead of creating another tray instance. Clicking outside hides the window to the tray, empty title-bar space can drag it, and the minus button hides it manually. The gear opens a Settings popover over the content without moving the Overview / Models / History tabs. Left-click the tray icon to reopen it; right-click for the glass-style menu with Open, Refresh, Data Directory, and Quit actions. Use `--hidden` for silent startup, or `--page=models`, `--page=reset`, and `--page=history` to open a specific page.

Startup with Windows is off by default. When enabled from the title-bar Settings popover, the app writes only its own value named `CodexUsageMonitor` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. The value contains the quoted absolute path of the current EXE followed by `--hidden`. It does not use `cmd.exe`, PowerShell, Task Scheduler, or administrator privileges, and it does not read, overwrite, or delete other applications' startup values. Disabling the switch removes only this value. If the app is moved while the switch remains enabled, the next run updates its own value to the current location. If the UI cannot disable it, exit the app and run:

```powershell
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v CodexUsageMonitor /f
```

Windows 11 uses system Desktop Acrylic and native rounded corners. Unsupported systems automatically use a matching translucent light or dark fallback.

## Portable release

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 -Version 0.2.1
```

Formal packaging rejects a dirty working tree. For local validation during development, pass `-Preview` explicitly and use a temporary output directory so an existing `dist` is not overwritten:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 `
  -Version 0.2.1 -Preview -OutputRoot "$env:TEMP\CodexUsageMonitor-preview"
```

The target computer does not need Python or a separately installed .NET Runtime. Extract the versioned ZIP and run `CodexUsageMonitor.exe`. Codex must already be installed and signed in on that computer; the app reads that computer's own local history.

The release script strictly performs locked restore → tests → self-contained multi-file publish → optional Authenticode signing → signature verification → documentation/licenses/runtime SBOM → ZIP → SHA-256. The ZIP contains the complete EXE, DLLs, `.deps.json`, `.runtimeconfig.json`, and native runtime files. The script compares every file across publish output, staging, and ZIP and verifies that README, the project's MIT LICENSE, third-party notices, `licenses/`, release metadata, and CycloneDX SBOM all describe the same version. The SBOM covers only the shipped application, runtime dependencies, and embedded font; it excludes test-only dependencies that are not distributed.

If you have a signing certificate, pass absolute `-SignToolPath` and `-CertificateThumbprint` values. Signing happens before compression and final hashing. Without a certificate, the script explicitly reports `NotSigned` and never fabricates a signature.

The current portable build is not commercially code-signed. Windows SmartScreen may show “Unknown publisher” on first launch after copying it to another computer. After verifying the source, select “More info → Run anyway” if appropriate.

## Open source and trusted distribution

You can provide both source code and ready-to-run Releases. A recommended public release sequence is:

1. The repository uses the MIT License, so the source can be used, modified, and redistributed.
2. Build from a version tag with GitHub Actions, then publish the source, portable ZIP, SHA-256 checksum, and release notes together under GitHub Releases.
3. For regular users, prefer a Microsoft Store MSIX. The Store re-signs it and generally reduces SmartScreen friction.
4. For direct GitHub downloads, sign the EXE and installer with a trusted Authenticode/Artifact Signing identity and timestamp them. Keep using the same publisher identity.
5. Avoid UPX compression, obfuscation, or silent self-update, and keep the build scripts public and reproducible.

Code signing proves publisher identity and file integrity, but it cannot guarantee that a new release will never trigger SmartScreen on its first downloads. Unsigned builds are more likely to be blocked.

## Accounting rules

- Official monthly, 7-day, and 30-day Tokens are sums of official daily values in that date range, using the same source as the history list. SQLite stores the returned values and replaces an existing date on refresh; local events and lifetime summaries are never added.
- Overview no longer repeats calendar-month local-log metrics; local analysis is concentrated on Models and follows one selected period. “API-equivalent cost without caching (same tokens)” reprices cached input at the standard input rate while total tokens, output, and model mix stay unchanged. With normal public prices, it equals the current estimate plus cache savings. Both costs are public API price scenarios, not a subscription bill.
- Both the start and end dates on the History page are inclusive, and the end cannot be later than the latest snapshot's local date. Its official range card calculates total, returned-day count, returned-day average, and peak only from days actually returned. An explicit official zero counts as one returned day; a missing day is not zero-filled.
- The official daily card offers List / Trend chart icons, defaults to List, and remembers the choice only for this run. The straight line and faint area use real date spacing and break across missing days. Hover, or focus the chart and use Left/Right and Home/End, for exact dates and Tokens. Changed dates require Apply; refreshing or changing language does not apply pending inputs. Switching views does not query data again.
- The separate “Local log sample” section reads existing SQLite events for the exact same local calendar range. Its 2×2 summary shows sample Tokens, current API-equivalent cost, cache hit rate, and the no-cache cost for the same Tokens; savings is an auxiliary relationship, with the same priced-model coverage and price source. The no-cache scenario only reprices cached input at the standard input rate, leaving total Tokens, output, and model mix unchanged. Changing the range does not call app-server or rewrite official daily usage, and this section is not an official bill.
- Missing official days are not treated as zero or filled from local logs in monthly, 7-day, 30-day, or history totals. The only exception is the Models page's Today headline: while today's official bucket is absent, it can show an explicitly labelled local rollout sample; an official zero or cached value still wins, and a later official value replaces rather than adds to it.
- Local model rankings, Token composition, cache hit rates, and API-equivalent costs are separate log-sample analysis, not a breakdown of the official total. Model shares and price coverage also use local samples as their denominator.
- Cached input is a subset of input Tokens; non-cached input is `input - cached_input`.
- Reasoning Tokens are a subset of output Tokens; visible output is `output - reasoning_output`.
- Local sample Tokens use the rollout's original value. If absent, only `input + output` is used; cache and reasoning are not added again. The temporary Today headline is not persisted or counted as an official total.
- Duplicate cumulative snapshots are removed by session and cumulative counters. A SHA-256 fingerprint is stored for every processed prefix; incremental parsing from a checkpoint is allowed only when the old prefix fingerprint still matches.
- A first run may scan a large history. Later scans use length, last-write time, and file identity to skip unchanged sources quickly rather than rereading the entire history every 15 minutes. Truncation, file replacement, ordinary in-place rewrites, or appends with a mismatched prefix safely rebuild that source's index. A deliberate in-place modification that preserves both length and last-write time is not reread during steady-state polling; it is revalidated after a later metadata change.
- Full rebuilds write to a staging table in batches. Formal events and checkpoints are replaced atomically in a single SQLite transaction only after parsing succeeds completely. Cancellation, I/O failure, or a process crash preserves the previous complete index.
- Local Token/cache statistics come from `token_count.last_token_usage` in rollout files and represent log aggregates. API-equivalent cost is not a subscription bill and currently does not account for long-context pricing, regional pricing, or models without published prices.
