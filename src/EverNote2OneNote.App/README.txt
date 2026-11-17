EverNote2OneNote - test application (Pilot 1 inspection, Pilot 2 single-note migration)
======================================================================================

What it does
  Reads your Evernote export files (.enex), checks every note, and shows what it found:
  notebooks, notes, attachments, whether all note text could be extracted, and any
  warnings or errors. Your .enex files are only read, never changed.
  Pilot 2: you can also migrate ONE selected note to OneNote and verify the result.

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

Pilot 2: migrate one note to OneNote
  1. Inspect your export, open the "Notes" tab and select one note.
  2. Click "Connect to OneNote" (first time: paste your Application ID, see below)
     and sign in with your Microsoft account in the web browser.
  3. Click "Migrate Selected Note". The page is created in the OneNote notebook
     "EverNote2OneNote Test" (one section per Evernote notebook), read back and verified.
     Existing notebooks and pages are never changed.
  4. The result window lists any text differences and the attachment results, and has
     "Open in OneNote" / "Open in browser" buttons.

One-time setup: Microsoft app registration (free, about 5 minutes)
  1. Sign in to https://entra.microsoft.com with your Microsoft account. If you cannot open
     "App registrations" (no directory), first create a free Azure account at
     https://azure.microsoft.com/free - this gives you a tenant. Microsoft may ask you to
     verify your identity. App registrations are free.
  2. Entra ID > App registrations > New registration:
       Name: EverNote2OneNote
       Supported account types: Personal accounts only
         (or: Any Entra ID Tenant + Personal Microsoft accounts)
       Redirect URI: platform "Public client/native (mobile & desktop)", value http://localhost
     Click Register.
  3. Copy the "Application (client) ID". No secret or certificate is needed.
  4. In this app: Connect to OneNote > paste the ID > sign in > approve "Notes.ReadWrite".
  The ID is stored in %LOCALAPPDATA%\EverNote2OneNote\settings.json (not a secret).
  Sign-in tokens are kept in memory only.
