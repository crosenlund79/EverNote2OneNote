# Architecture and decisions

## Purpose and priorities

A single-purpose, occasionally used migration utility. In priority order:

1. Preserve **all note text**. Missing text is unacceptable.
2. Preserve note titles and basic notebook organisation.
3. Preserve images and attachments wherever possible.
4. Preserve dates, tags and other metadata where practical.
5. Formatting fidelity is a low priority.

The intended flow is simply **Select source → Migrate → Verify → Report**.

## Decisions

| Decision | Choice | Reason |
|---|---|---|
| Source format | Evernote **ENEX** exports | Complete and authoritative (original ENML plus every attachment), offline and re-readable. The official Evernote MCP server (beta, paid plans only) has no documented enumeration, pagination or completeness check, and Evernote's own server would decide what text we receive. The classic Evernote API is deprecated. |
| Target structure | One dedicated OneNote notebook per migration; each ENEX file becomes a section | Simple and needs no configuration. Existing notebooks are never modified. |
| Verification | Detect **missing, reordered or duplicated** text by comparing the ordered source text with the text read back from OneNote | A simple "every word exists" check would miss reordering and duplication. |
| On a verification mismatch | **Report and allow controlled retries**; never auto-append text | Keeps the user in control. |
| Malformed input | Never silently discard text. Isolate damage per note, recover text from raw markup, keep the original markup in the report, and grade the finding as an error | Priority 1 |
| Technology | C# / .NET 10 (LTS), xUnit, WPF later | No paid services or AI dependencies. Minimal NuGet dependencies (none in the core library so far). |

## Components

```
ENEX files (read-only)
  → EnexReader     scans note boundaries, then parses each note independently
  → EnmlText       note body → ordered plain text (+ media references, checkboxes, encrypted blocks)
  → Inspector      findings (Error / Warning / Info) per file and note
  → Summary / ReportWriter   console summary (counts only) + private local report
  → Windows app / CLI        thin front ends over the same engine
  ... later pilots:
  → Converter      ENML → OneNote-safe HTML subset (text kept even where formatting is dropped)
  → OneNote client Microsoft Graph, delegated sign-in, throttling
  → Journal        append-only local progress file: resume without duplicate pages
  → Verifier       read pages back and compare ordered text with the source
```

| Path | Contents |
|---|---|
| `src/EverNote2OneNote/Enex` | `EnexScanner` (byte-level note boundaries, skips CDATA/comments), `EnexReader` (strict read-only parsing, per-note error isolation, raw recovery), note model |
| `src/EverNote2OneNote/Text` | `EnmlText` (XML walk with built-in HTML entity table, no DTD or network access), `RawText` (fallback: removes only recognised tags), `TextBuilder` |
| `src/EverNote2OneNote/Inspection` | `Inspector`, finding catalogue (`IssueCodes`), `Summary`, `ReportWriter` |
| `src/EverNote2OneNote.Cli` | `e2o inspect` |
| `src/EverNote2OneNote.App` | WPF test application: choose folder → Inspect Archive → results and report folder. The inspection runs on a background thread with per-file progress. A hidden `--self-test` switch lets the Windows build pipeline run the published exe end to end. |

### How ENEX reading avoids data loss

- Files are opened with read access only and shared read; nothing is ever written next to them.
  The CLI refuses to write its report inside the source folder.
- A byte-level scan finds each `<note>…</note>` range first, so a damaged note cannot hide the notes after it.
  Bytes outside notes are checked too: stray data and truncated files are reported.
- Each note is parsed on its own. If its XML is damaged, the title, dates, tags and body text are recovered
  from the raw markup, attachments that could not be read are counted, and the note's source line is reported.
- Note bodies are parsed without the ENML DTD (offline, and immune to external-entity attacks). Named HTML entities
  are resolved from .NET's built-in table, and unknown ones are kept literally and reported.
  Bodies that are not well-formed fall back to raw text recovery, which only removes tags with recognised element
  names, so text that merely looks like markup is kept.
- Unknown ENEX elements are preserved verbatim in the report.
- Attachment data is streamed and hashed (MD5, matching `<en-media hash>`) and never held in memory.
  A synthetic 1.47 GB export (70 files, 758 notes, 1,044 attachments) was inspected in about 9 seconds with
  about 110 MB peak memory.

### What ENEX does not contain

Notebook names (taken from the file name), notebook stacks, note IDs, tag hierarchy and note history.
Encrypted text cannot be read without its passphrase. It is reported, never silently skipped.

## Known Microsoft Graph constraints (for later pilots)

- Delegated sign-in only. App-only access to OneNote was retired on 31 March 2025.
- Throttling: 120 requests per minute and 400 per hour per app per user; 429 responses carry no `Retry-After`.
- Uploads: about 70 MB per request, 25 MB per part, and the documentation is inconsistent on the number of parts.
  This must be tested against a real account before the uploader is designed.
- OneNote accepts only a small HTML subset and silently drops the rest, which is why verification reads pages back.
  It does not support merged table cells.
- Section names are limited to 50 characters with forbidden characters; notebook names to 128.
- The creation date can be set (`<meta name="created">`); the last-modified date cannot.

## Data safety

The repository is public. Only synthetic fixtures are committed. Reports, exports, logs and tokens stay on the
local machine, under `%LOCALAPPDATA%\EverNote2OneNote\`, and `.gitignore` blocks them.
