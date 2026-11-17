# EverNote2OneNote

A Windows utility that migrates Evernote notes to Microsoft OneNote, built for reliability:
**no note text may be lost**, and anything that cannot be migrated is reported.

Source: Evernote ENEX export files. Target: OneNote via Microsoft Graph.
See [docs/architecture.md](docs/architecture.md) for the design and decisions.

## Status

| Milestone | Scope | Status |
|---|---|---|
| Pilot 1 | Read and inspect ENEX exports locally (read-only) | **Done** |
| Pilot 2 | Convert notes and upload to a OneNote test notebook | Not started |
| Pilot 3 | Migration journal, resume, verification, report | Not started |
| Pilot 4 | Simple WPF interface | Not started |

## Pilot 1: inspect your Evernote export (Windows)

The inspector reads `.enex` files **without modifying them**, extracts the full text and metadata of
every note, inventories attachments, and reports anything that could not be read reliably.

### 1. Install the .NET 10 SDK

```powershell
winget install Microsoft.DotNet.SDK.10
```

Or download it from <https://dotnet.microsoft.com/download/dotnet/10.0>. Open a new terminal afterwards.

### 2. Export your notebooks from Evernote

In Evernote, export each notebook as ENEX (right-click the notebook → *Export notebook…* → `.enex`).
Save all files in one folder **outside this repository**, e.g. `C:\EvernoteExport`.
Keep the default file names: each file name becomes the notebook (section) name.

### 3. Get the code and run the tests

```powershell
git clone https://github.com/crosenlund79/EverNote2OneNote.git
cd EverNote2OneNote
dotnet test
```

### 4. Run the inspection

```powershell
dotnet run --project src\EverNote2OneNote.Cli -c Release -- inspect "C:\EvernoteExport"
```

Add your account's figures to compare the export against them:

```powershell
dotnet run --project src\EverNote2OneNote.Cli -c Release -- inspect "C:\EvernoteExport" `
  --expect-notes <n> --expect-notebooks <n> --expect-attachments <n> --expect-size <e.g. 1.2GB>
```

Other options: `--out <folder>` (report location) and `--dump-text` (also write every note's extracted text).

### 5. Read the results

- **Console summary:** counts only, with no titles, names or text, so it is safe to share.
  Exit code `0` = no read errors, `1` = errors found, `2` = usage or fatal error.
- **Detailed report** (default `%LOCALAPPDATA%\EverNote2OneNote\reports\inspect-<time>\`):
  - `report.md`: summary, per-notebook table, every finding with note title and location
  - `notes.csv` / `attachments.csv`: one row per note / attachment (opens in Excel)
  - `recovered\`: notes whose text had to be recovered from damaged markup, with the original markup
  - `text\`: full extracted text per notebook (only with `--dump-text`)

  The detailed report contains personal data. Keep it private and never commit it.

Findings are graded **Error** (could not be read reliably), **Warning** (read, but needs attention
before migration, e.g. encrypted text) and **Info**. `report.md` explains every code.

## Development

Requires the .NET 10 SDK. Everything except the future WPF app builds and tests on Windows, macOS and Linux.

```bash
dotnet build
dotnet test
```

| Path | Purpose |
|---|---|
| `src/EverNote2OneNote` | Core library: ENEX reading, text extraction, inspection, reports |
| `src/EverNote2OneNote.Cli` | `e2o` command-line tool |
| `tests/EverNote2OneNote.Tests` | xUnit tests and synthetic ENEX fixtures |

## Data safety

This repository is public. Never commit real notes, exports, attachments, reports, logs or credentials.
`.gitignore` blocks `*.enex` (except the synthetic test fixtures), reports and CSV files. Test data is synthetic only.
