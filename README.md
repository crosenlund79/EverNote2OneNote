# EverNote2OneNote

A Windows utility that migrates Evernote notes to Microsoft OneNote, built for reliability:
**no note text may be lost**, and anything that cannot be migrated is reported.

Source: Evernote ENEX export files. Target: OneNote via Microsoft Graph.
See [docs/architecture.md](docs/architecture.md) for the design and decisions.

## Status

| Milestone | Scope | Status |
|---|---|---|
| Pilot 1 | Read and inspect ENEX exports locally (read-only): Windows app and command line | **Done** |
| Pilot 2 | Convert notes and upload to a OneNote test notebook | Not started |
| Pilot 3 | Migration journal, resume, verification, report | Not started |

## Try Pilot 1 on Windows (no installation)

The test application reads your `.enex` files **without modifying them**, checks every note and shows
notebooks, notes, attachments, text extraction status, warnings and errors. Nothing is uploaded.

1. **Export from Evernote:** export each notebook as ENEX (right-click the notebook → *Export notebook…* → `.enex`)
   into one folder, e.g. `C:\EvernoteExport`. Keep the default file names, because they become the notebook names.
2. **Download the app:** open
   [Actions → Windows build](https://github.com/crosenlund79/EverNote2OneNote/actions/workflows/windows.yml)
   (you must be signed in to GitHub), click the latest run with a green check mark, scroll down to
   **Artifacts** and click **EverNote2OneNote-win-x64**. A zip file downloads.
3. **Extract** the zip (right-click → *Extract All…*) and double-click **EverNote2OneNote.exe**.
   It is self-contained, so .NET is not required.
4. If Windows shows *"Windows protected your PC"*, click **More info → Run anyway**. The test build is not code-signed.
5. Click **Browse…**, choose your export folder, then click **Inspect Archive**.
6. Review the result. **Open Report Folder** shows the detailed report:
   - `report.md`: every finding with note title and location
   - `notes.csv`, `attachments.csv`: one row per note or attachment (open in Excel)
   - `recovered\`: notes whose text had to be recovered from damaged markup, with the original markup
   - `text\`: the full extracted text of every note, for spot checks

   The report is saved in `%LOCALAPPDATA%\EverNote2OneNote\reports\` and contains personal data, so keep it private.
   The **Summary (safe to share)** tab contains counts only.

Findings are graded **Error** (could not be read reliably), **Warning** (read, but needs attention before
migration, e.g. encrypted text) and **Info**.

## Command-line tool (for developers)

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet run --project src\EverNote2OneNote.Cli -c Release -- inspect "C:\EvernoteExport" `
  --expect-notes <n> --expect-notebooks <n> --expect-attachments <n> --expect-size <e.g. 1.2GB>
```

The console summary contains counts only. Other options: `--out <folder>`, `--dump-text`.
Exit code `0` = no read errors, `1` = errors found, `2` = usage or fatal error.

## Development

```bash
dotnet build
dotnet test
dotnet publish src/EverNote2OneNote.App -c Release -r win-x64 -o publish   # single self-contained .exe
```

The library, CLI and tests build and run on Windows, macOS and Linux. The WPF app compiles everywhere but runs
only on Windows. The [Windows build](.github/workflows/windows.yml) workflow builds and tests on Windows,
publishes the app, runs it against the synthetic fixtures, and uploads the downloadable artifact.

| Path | Purpose |
|---|---|
| `src/EverNote2OneNote` | Core library (`EverNote2OneNote.Core.dll`): ENEX reading, text extraction, inspection, reports |
| `src/EverNote2OneNote.App` | Windows test application (`EverNote2OneNote.exe`, WPF) |
| `src/EverNote2OneNote.Cli` | `e2o` command-line tool |
| `tests/EverNote2OneNote.Tests` | xUnit tests and synthetic ENEX fixtures |

## Data safety

This repository is public. Never commit real notes, exports, attachments, reports, logs or credentials.
`.gitignore` blocks `*.enex` (except the synthetic test fixtures), reports and CSV files. Test data is synthetic only.
