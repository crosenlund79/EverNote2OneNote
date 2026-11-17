EverNote2OneNote - test application (inspection, single-note and full migration)
================================================================================

What it does
  Reads your Evernote export files (.enex), checks every note, and shows what it found:
  notebooks, notes, attachments, whether all note text could be extracted, and any
  warnings or errors. Your .enex files are only read, never changed.
  You can also migrate one selected note, or all notes, into the OneNote desktop app (local only).

Before you start
  In Evernote, export each notebook as an ENEX file (right-click the notebook,
  "Export notebook...", format .enex). Save them all in one folder, e.g. C:\EvernoteExport.

How to run
  1. Double-click EverNote2OneNote.exe. No installation and no .NET are needed.
  2. If Windows shows "Windows protected your PC", click "More info", then "Run anyway".
     (This test build is not code-signed, so Windows does not recognise it yet.)
  3. Click "Browse..." and choose the folder that contains your .enex files.
  4. Click "Inspect Archive" and wait for it to finish.
  5. Review the results. Click "Open Report Folder" for the detailed report.
     On the Attachments tab, double-click an image to preview it.

The results
  - Green: all note text was read and nothing needs attention.
  - Yellow: all note text was read; some warnings need review (for example encrypted text).
  - Red: some data could not be read reliably. See the "Findings" tab and the report.
  - "Summary (safe to share)" tab: counts only, with no titles or text. You can copy it and send it.

The report folder (%LOCALAPPDATA%\EverNote2OneNote\reports\...) contains note titles and
the full extracted text of your notes. Keep it private.

Pilot 2: migrate one note into the OneNote desktop app
  Requires the OneNote desktop app (Microsoft 365 / Office) on this PC. No sign-in,
  no cloud service.
  1. Inspect your export, open the "Notes" tab and select one note.
  2. Check "Destination notebook (selected note)" (default: Evernote Import). If a notebook with that
     name is open in OneNote it is used; otherwise a new local notebook is created
     (%LOCALAPPDATA%\EverNote2OneNote\OneNote\Notebooks\, not synced by OneDrive).
  3. Click "Migrate Selected Note". For an existing notebook, or if a page with the same title
     already exists, the app shows what exists and asks first. The note is always added as a
     NEW page in the section named after its Evernote notebook; existing pages are never changed.
  4. The page is read back and compared with the original text. With "Also export a .onepkg
     backup" checked, the notebook is also exported (optional; never overwrites a file).
  5. The result window shows text differences, attachment results, the backup location, and
     buttons "Show Page in OneNote" and "Open Export Folder".

Pilot 3: migrate all notes
  1. Inspect your export folder, then click "Migrate All Notes".
  2. Check the plan and click "Start Migration" (nothing is written before that):
     - .enex files directly in the folder go to a NEW notebook "EverNote"; files in a subfolder
       "Gaming" go to "EverNote - Gaming"; deeper folders: "EverNote - Gaming - Campaigns".
     - Each .enex file becomes a section, each note a new page.
     - If a notebook name is already taken, the new notebook gets a marked unique name, e.g.
       "EverNote - Gaming (Import 2026-11-17 2015)". Existing notebooks are never used or changed.
     - Notes that could not be read at all are excluded and listed with the reason.
  3. While it runs, the window shows the current notebook, section and note and the counts.
     "Stop After Current Note" finishes the current note, then stops. Failed notes are recorded
     and the run continues; it stops by itself if OneNote closes, or after 5 failures in a row.
  4. With the backup option checked, each new notebook is exported once at the end (.onepkg).
  5. The report says COMPLETE only if every note and attachment was migrated and verified;
     otherwise INCOMPLETE, with the reasons. "Open Report Folder" shows migration-report.md,
     migration-notes.csv (every note and its destination) and journal.jsonl (progress log),
     in %LOCALAPPDATA%\EverNote2OneNote\OneNote\Runs\. They contain note titles: keep them private.
  Running it again creates new notebooks; nothing is ever deleted. Remove test notebooks
  yourself in OneNote when you no longer need them.

If the migration fails with a OneNote/COM error (for example 0x8002801D)
  Click "Check OneNote Connection" (read-only). It analyses OneNote's automation and type library
  registrations and tests read-only calls; its report ends with an evidence-based conclusion.
  "Copy to Clipboard" lets you send it. This app never changes the registry.
