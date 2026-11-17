# EverNote2OneNote

A Windows utility that migrates Evernote notes to Microsoft OneNote, built for reliability:
**no note text may be lost**, and anything that cannot be migrated is reported.

Source: Evernote ENEX export files. Target: the local OneNote desktop app (via its automation API), exported as `.onepkg`.
See [docs/architecture.md](docs/architecture.md) for the design and decisions.

## Status

| Milestone | Scope | Status |
|---|---|---|
| Pilot 1 | Read and inspect ENEX exports locally (read-only): Windows app and command line | **Done** |
| Pilot 2 | Migrate one selected note into the local OneNote desktop app, verify it, export `.onepkg` | **Ready for testing** |
| Pilot 3 | Migration journal, resume, verification, report | Not started |

## Try Pilot 1 on Windows (no installation)

The test application reads your `.enex` files **without modifying them**, checks every note and shows
notebooks, notes, attachments, text extraction status, warnings and errors. Nothing is uploaded
(until you use the Pilot 2 migration below).

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
6. Review the result. On the **Attachments** tab, double-click an image to preview it (decoded on demand from the
   original `.enex` file; nothing is extracted). **Open Report Folder** shows the detailed report:
   - `report.md`: every finding with note title and location
   - `notes.csv`, `attachments.csv`: one row per note or attachment (open in Excel)
   - `recovered\`: notes whose text had to be recovered from damaged markup, with the original markup
   - `text\`: the full extracted text of every note, for spot checks

   The report is saved in `%LOCALAPPDATA%\EverNote2OneNote\reports\` and contains personal data, so keep it private.
   The **Summary (safe to share)** tab contains counts only.

Findings are graded **Error** (could not be read reliably), **Warning** (read, but needs attention before
migration, e.g. encrypted text) and **Info**.

## Pilot 2: migrate one note into the OneNote desktop app

Requires the **OneNote desktop app** (Microsoft 365 / Office) on the same PC. No cloud service, Microsoft Graph,
Azure, app registration or sign-in is used: the app talks to OneNote through its documented local automation
API (COM).

In the Windows app: inspect your export, open the **Notes** tab, select one note, and click **Migrate Selected Note**.

- A new, uniquely named **local** notebook is created for every migration (`EverNote2OneNote Test <date time>`),
  with one section named after the Evernote notebook and one page. Existing notebooks and pages are never changed.
  The `.enex` files are only read.
- The page gets the note's title, its full text (paragraphs, headings, bullet and numbered lists, web links,
  bold/italic), images, file attachments, a small header line with the Evernote notebook, dates and tags, and the
  note's creation date.
- **Verification:** the page is read back through the OneNote API and its text is compared with the original
  (from the inspection), ignoring spaces and line breaks. Every character must be present, in order, exactly once.
  Missing, extra, moved and duplicated text is listed; attachments are checked separately; nothing is repaired.
- **Export:** the test notebook is exported with OneNote's own *Publish* function as a OneNote package (`.onepkg`).
  Existing files are never overwritten.
- Notebooks and exports are kept in `%LOCALAPPDATA%\EverNote2OneNote\OneNote\` (`Notebooks\`, `Exports\`),
  a folder OneDrive does not sync, so nothing is uploaded. The app refuses to work inside a OneDrive folder.
- If OneNote rejects the full page, the app writes it again on a fresh page as plain text only and reports that
  attachments were not transferred. If the OneNote automation API is not available, it stops with an explanation.

### Pilot 2 limitations

- Needs the OneNote desktop app from Microsoft 365 / Office. *OneNote for Windows 10* has no automation API.
- Tables become lines of text with cells separated by tabs. Checkboxes become `[x]` / `[ ]` text so the text can be
  verified exactly. Fonts, colours and sizes are dropped.
- Links to other Evernote notes become plain text. Encrypted text is replaced by a visible marker. Both are reported.
- Images are checked by count, because OneNote stores them without file names. Files are checked by name.
  Attachments over 100 MB are not transferred and are reported.
- The test notebook stays open in OneNote after the migration so you can look at it. Close it in OneNote
  (right-click the notebook, *Close This Notebook*) when you are done; its files stay in the folder above.
- The repeat-migration warning only lasts while the app is open. Each migration creates its own new notebook.

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
