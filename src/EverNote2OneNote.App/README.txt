EverNote2OneNote - Pilot 1 test application (inspection only)
=============================================================

What it does
  Reads your Evernote export files (.enex), checks every note, and shows what it found:
  notebooks, notes, attachments, whether all note text could be extracted, and any
  warnings or errors. Your .enex files are only read, never changed. Nothing is uploaded,
  and OneNote is not involved yet.

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
