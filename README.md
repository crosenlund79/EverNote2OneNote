# EverNote2OneNote

A Windows utility that migrates Evernote notes to Microsoft OneNote, built for reliability:
**no note text may be lost**, and anything that cannot be migrated is reported.

Source: Evernote ENEX export files. Target: the local OneNote desktop app (via its automation API), exported as `.onepkg`.
See [docs/architecture.md](docs/architecture.md) for the design and decisions.

## Status

| Milestone | Scope | Status |
|---|---|---|
| Pilot 1 | Read and inspect ENEX exports locally (read-only): Windows app and command line | **Done** |
| Pilot 2 | Migrate one selected note into the local OneNote desktop app, verify it, export `.onepkg` | **Done** |
| Pilot 3 | Migrate all notes into new notebooks, with progress, stop, journal, verification and a final report | **Ready for testing** |

## Try Pilot 1 on Windows (no installation)

The test application reads your `.enex` files **without modifying them**, checks every note and shows
notebooks, notes, attachments, text extraction status, warnings and errors. Nothing is uploaded
(until you use the migration below).

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

In the Windows app: inspect your export, open the **Notes** tab, select one note, check the **Destination notebook
(selected note)** (default `Evernote Import`), and click **Migrate Selected Note**.

- **Destination:** if a notebook with that name is open in OneNote, it is used; otherwise a new local notebook with
  that name is created in `%LOCALAPPDATA%\EverNote2OneNote\OneNote\Notebooks\` (not synced by OneDrive). Each Evernote
  notebook becomes a section; an existing section with the same name is reused. The note is always added as a
  **new page**: existing pages are never changed, overwritten or deleted. Before writing into an existing notebook
  (once per notebook per session), and whenever a page with the same title already exists in the section, the app
  shows what exists and asks for confirmation. The `.enex` files are only read.
- The page gets the note's title, its full text (paragraphs, headings, bullet and numbered lists, simple tables,
  web links, bold/italic), images, file attachments, a small header line with the Evernote notebook, dates and tags,
  and the note's creation date.
- **Verification:** the page is read back through the OneNote API and its text is compared with the original
  (from the inspection), ignoring spaces and line breaks. Every character must be present, in order, exactly once.
  Missing, extra, moved and duplicated text is listed; attachments are checked separately; nothing is repaired.
- **Optional backup:** with *Also export a .onepkg backup* checked, the destination notebook is exported with
  OneNote's own *Publish* function to `%LOCALAPPDATA%\EverNote2OneNote\OneNote\Exports\`. The app waits until OneNote
  has finished writing the package and checks that it is valid. Existing files are never overwritten. A failed
  backup is reported but does not affect the migrated page.
- If OneNote rejects the full page, the app writes it again on a fresh page as plain text only and reports that
  attachments were not transferred. If the OneNote automation API is not available, it stops with an explanation.

## Pilot 3: migrate all notes

Inspect your export folder, then click **Migrate All Notes**.

1. **Plan (nothing is written yet).** The app shows the complete plan and starts only when you click **Start Migration**:
   - Each folder becomes a **new** notebook: `.enex` files directly in the export folder go to `EverNote`, files in
     `Gaming\` go to `EverNote - Gaming`, files in `Gaming\Campaigns\` go to `EverNote - Gaming - Campaigns`.
   - Each `.enex` file becomes a section named after the file, and each note becomes a new page.
   - Existing notebooks are never used. If a name is already taken (a notebook open in OneNote, or a notebook folder
     from an earlier import), the new notebook gets a clearly marked unique name, e.g.
     `EverNote - Gaming (Import 2026-11-17 2015)`. Two folders or files that would get the same name get ` (2)`.
   - Notes whose text could not be read at all are **excluded** and listed with the reason. Read errors in an `.enex`
     file are listed too. Characters that can cause problems in OneNote names (`? * \ / : < > | & # " ' % ~`) become `_`.
2. **Migration.** Notes are migrated one at a time with the same engine as a single note. The window shows the
   current notebook, section and note, how many are done, migrated and failed. **Stop After Current Note** finishes
   the current note and stops cleanly. A failed note is recorded and the run continues. The run stops by itself if
   OneNote closes or stops responding, or after 5 failures in a row. A page creation that failed is never retried
   (a blank page may exist; the report says so). Nothing is ever deleted.
3. **Optional backup.** With *Also export a .onepkg backup of each notebook* checked, each new notebook is exported
   once, after all notes. A failed export is reported but does not affect the migrated pages. Existing files are never
   overwritten.
4. **Report.** It says **COMPLETE** only if every note was migrated and verified, every attachment was found on its
   page, nothing was excluded, stopped or damaged in the export, and no note contains encrypted text that could not be
   migrated. Otherwise it says **INCOMPLETE** and lists why. It counts notes and attachments discovered, migrated, with
   verified text, with missing or changed text, failed, excluded and not attempted; and attachments transferred and missing.
   **Open Report Folder** opens `%LOCALAPPDATA%\EverNote2OneNote\OneNote\Runs\Migration <date time>\`:
   - `migration-report.md`: the report
   - `migration-notes.csv`: every note with its ENEX file, note number, destination notebook and section, status and details
   - `journal.jsonl`: progress log written while migrating (one line per step, with the source file path and note
     number), so it shows what was done even if the PC stops unexpectedly

   These files contain note titles: keep them private.

To migrate again, run Migrate All Notes again: it creates new, uniquely named notebooks. It never continues or changes
an earlier import, and it never deletes anything; remove test notebooks yourself in OneNote when you no longer need them.

### If the migration fails with a OneNote/COM error

Click **Check OneNote Connection**. It is read-only: it reads OneNote's automation and type library registrations
(including where each entry is stored), loads the type libraries without registering anything, and asks OneNote
for the list of open notebooks both by name (late-bound) and through the typed interface. Its report ends with a
conclusion based on that evidence; **Copy to Clipboard** lets you send it. The app never changes the registry.

### Limitations (Pilot 2 and 3)

- Needs the OneNote desktop app from Microsoft 365 / Office. *OneNote for Windows 10* has no automation API.
- Simple tables keep their rows and cells, with images in their cells next to their text (merged cells are split;
  tables nested in tables become text in their cell). If OneNote rejects a table, the page is written without
  tables, with images kept in reading order next to their text.
  Checkboxes become `[x]` / `[ ]` text so the text can be verified exactly. Fonts, colours and sizes are dropped.
- Links to other Evernote notes become plain text. Encrypted text is replaced by a visible marker. Both are reported.
- Images are checked by count, because OneNote stores them without file names. Files are checked by name.
  Attachments over 100 MB are not transferred and are reported.
- Only notebooks that are open in OneNote (or folders in this app's notebook folder) are recognised as existing; a
  closed notebook with the same name elsewhere is not found.
- Migrate All Notes cannot resume an interrupted run; running it again creates new notebooks. The journal and the
  CSV show exactly which notes were migrated.

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
