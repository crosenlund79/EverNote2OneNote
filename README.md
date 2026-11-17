# EverNote2OneNote

A Windows utility that migrates Evernote notes to Microsoft OneNote, built for reliability:
**no note text may be lost**, and anything that cannot be migrated is reported.

Source: Evernote ENEX export files. Target: OneNote via Microsoft Graph.
See [docs/architecture.md](docs/architecture.md) for the design and decisions.

## Status

| Milestone | Scope | Status |
|---|---|---|
| Pilot 1 | Read and inspect ENEX exports locally (read-only): Windows app and command line | **Done** |
| Pilot 2 | Migrate one selected note to a OneNote test notebook and verify it | **Ready for testing** |
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

## Pilot 2: migrate one note to OneNote

In the Windows app: inspect your export, open the **Notes** tab, select one note, click **Connect to OneNote**,
then **Migrate Selected Note**. The app creates the page, reads it back from OneNote, and shows a verification
report with links to open the page.

- Pages go to the OneNote notebook **EverNote2OneNote Test**, one section per Evernote notebook. Existing
  notebooks and pages are never changed. The `.enex` files are only read.
- The page gets the note's title, its full text (paragraphs, headings, lists, tables, web links, bold/italic),
  images and file attachments, and a small header line with the Evernote notebook, dates and tags.
  The page's creation date is set to the note's Evernote creation date.
- **Verification:** the original text (from the inspection) is compared with the text read back from OneNote,
  ignoring spaces and line breaks. Every character must be present, in order, exactly once. Missing, extra,
  moved and duplicated text is listed. Attachments are checked separately. Nothing is repaired automatically.
- Migrating the same note twice in one session asks for confirmation first.

### One-time setup: Microsoft app registration

Microsoft only lets an app access OneNote through an *app registration* that you create (free, about 5 minutes).

1. **You need a Microsoft Entra tenant.** Sign in to <https://entra.microsoft.com> with your Microsoft account.
   If you cannot open *App registrations* because your account has no directory, create a free Azure account at
   <https://azure.microsoft.com/free> first. This gives you a tenant (*Default Directory*). Microsoft may ask you to
   verify your identity. App registrations themselves are free.
2. Go to **Entra ID → App registrations → New registration**:
   - **Name:** `EverNote2OneNote`
   - **Supported account types:** *Personal accounts only* (or *Any Entra ID Tenant + Personal Microsoft accounts*)
   - **Redirect URI:** platform *Public client/native (mobile & desktop)*, value `http://localhost`
   - Click **Register**.
3. Copy the **Application (client) ID**. No client secret, certificate or permission changes are needed.
   The app asks for *Notes.ReadWrite* when you sign in, and you approve it on Microsoft's consent page.
4. In the app, click **Connect to OneNote**, paste the ID, and sign in in your web browser.

The ID is saved in `%LOCALAPPDATA%\EverNote2OneNote\settings.json`; it is not a secret. Sign-in tokens are kept
in memory only, never on disk or in this repository. If you forgot the redirect URI, open the registration's
**Authentication** page, add the platform *Mobile and desktop applications* with `http://localhost`.

### Pilot 2 limitations

- Personal Microsoft accounts only (Microsoft 365 Personal/Family, outlook.com); work or school accounts are not
  supported yet.
- Checkboxes are migrated as `[x]` / `[ ]` text rather than OneNote to-do tags, so the text can be verified exactly.
- Formatting is simplified: fonts, colours, sizes and indentation are dropped; merged table cells are split.
- Links to other Evernote notes become plain text. Encrypted text is replaced by a visible marker. Both are reported.
- Attachments over 25 MB are not transferred (OneNote's limit) and are reported. Notes with more than 5 attachments,
  or more than 50 MB of them, are created text-first and their attachments are appended at the end of the page.
- Images are checked by count, because OneNote renames them; files are checked by name.
- The repeat-migration warning only lasts while the app is open. After a restart, migrating the same note again
  creates a second page.

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
