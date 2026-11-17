using System.Text;
using EverNote2OneNote.Enex;
using EverNote2OneNote.Text;

namespace EverNote2OneNote.Inspection;

/// <summary>Reads ENEX files (never modifying them) and records everything found, including problems.</summary>
public static class Inspector
{
    private const long OneNoteMaxPartBytes = 25L * 1024 * 1024;

    private static readonly HashSet<string> InlineImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/jpg", "image/pjpeg", "image/gif", "image/tiff", "image/bmp", "image/x-ms-bmp",
    };

    /// <summary>Lists the .enex files to inspect: a single file, or every .enex file under a folder.</summary>
    public static List<string> FindFiles(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (File.Exists(full))
        {
            return [full];
        }
        if (!Directory.Exists(full))
        {
            throw new FileNotFoundException($"Not found: {full}");
        }
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            IgnoreInaccessible = false,
        };
        return Directory.EnumerateFiles(full, "*.enex", options)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static InspectionResult Run(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var baseDirectory = File.Exists(full) ? System.IO.Path.GetDirectoryName(full)! : full;
        var result = new InspectionResult { SourcePath = full };

        foreach (var file in FindFiles(full))
        {
            InspectFile(file, baseDirectory, result);
        }

        foreach (var group in result.Files.GroupBy(f => f.Notebook, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var paths = string.Join(", ", group.Select(f => f.RelativePath));
            result.Issues.Add(new Issue(IssueCodes.DuplicateNotebook, group.First().RelativePath, null, null,
                $"{group.Count()} files map to the notebook name \"{group.Key}\": {paths}"));
        }
        return result;
    }

    private static void InspectFile(string path, string baseDirectory, InspectionResult result)
    {
        var relative = System.IO.Path.GetRelativePath(baseDirectory, path);
        var summary = new FileSummary
        {
            FullPath = path,
            RelativePath = relative,
            Notebook = System.IO.Path.GetFileNameWithoutExtension(path),
            Bytes = new FileInfo(path).Length,
        };
        result.Files.Add(summary);

        try
        {
            using var reader = EnexReader.Open(path);
            summary.Header = reader.Header;
            foreach (var problem in reader.Problems)
            {
                result.Issues.Add(new Issue(problem.Code, relative, null, null, problem.Message));
            }
            if (reader.NoteCount == 0 && reader.Header is not null)
            {
                result.Issues.Add(new Issue(IssueCodes.EmptyExport, relative, null, null, "The file contains no notes."));
            }
            foreach (var note in reader.ReadNotes())
            {
                InspectNote(note, summary, result);
                summary.NoteCount++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            summary.Unreadable = true;
            result.Issues.Add(new Issue(IssueCodes.FileUnreadable, relative, null, null,
                $"Reading stopped after {summary.NoteCount} note(s): {ex.Message}"));
        }
    }

    private static void InspectNote(EnexNote note, FileSummary file, InspectionResult result)
    {
        var issues = new List<(string Code, string Message)>();
        EnmlExtraction? body = note.Content is null ? null : EnmlText.Extract(note.Content);

        string text;
        TextStatus status;
        string? problem = note.ParseError;
        if (body is not null)
        {
            text = body.Text;
            status = body.FallbackReason is null ? TextStatus.Extracted : TextStatus.Recovered;
        }
        else if (note.ParseError is not null)
        {
            text = note.RecoveredText ?? "";
            status = note.RecoveredText is null ? TextStatus.NotRecovered : TextStatus.Recovered;
        }
        else
        {
            text = "";
            status = TextStatus.Extracted;
            issues.Add((IssueCodes.MissingContent, "The note has no <content> element."));
        }

        if (note.ParseError is not null)
        {
            var outcome = body is not null
                ? "The note body itself was read completely; other parts of the note may be incomplete."
                : status == TextStatus.Recovered
                    ? "Text was recovered from the raw markup; formatting and some metadata may be missing."
                    : "Text could not be recovered automatically.";
            var unread = note.UnreadAttachments > 0
                ? $" {note.UnreadAttachments} attachment(s) in this note could not be read or counted."
                : "";
            issues.Add((IssueCodes.NoteMalformed, $"{note.ParseError} {outcome}{unread} Source line {note.Line:N0}."));
        }
        if (body?.FallbackReason is { } reason)
        {
            problem = reason;
            issues.Add((IssueCodes.ContentMalformed, $"{reason} Text was recovered from the raw markup."));
        }

        // Attachments and their references from the body.
        var referenced = new HashSet<string>(body?.MediaHashes ?? [], StringComparer.OrdinalIgnoreCase);
        var present = new HashSet<string>(note.Resources.Select(r => r.Md5 ?? ""), StringComparer.OrdinalIgnoreCase);
        foreach (var missing in referenced.Where(h => !present.Contains(h)))
        {
            issues.Add((IssueCodes.MissingAttachment, $"The body refers to attachment {missing}, which is not in the export."));
        }

        var summary = new NoteSummary
        {
            File = file,
            Index = note.Index,
            ByteOffset = note.ByteOffset,
            Line = note.Line,
            Title = note.Title,
            Created = note.Created,
            Updated = note.Updated,
            Tags = note.Tags,
            Tasks = note.Tasks,
            Text = text,
            TextStatus = status,
            ContentBytes = note.Content is null ? 0 : Encoding.UTF8.GetByteCount(note.Content),
            AttachmentCount = note.Resources.Count,
            AttachmentBytes = note.Resources.Sum(r => r.Size),
            UnreadAttachments = note.UnreadAttachments,
            TodoCount = body?.TodoCount ?? 0,
            EncryptedBlocks = body?.Encrypted.Count ?? 0,
            Problem = problem,
            OriginalContent = status == TextStatus.Extracted ? null : note.Content,
            UnknownElements = note.UnknownElements,
        };
        result.Notes.Add(summary);

        foreach (var resource in note.Resources)
        {
            bool isReferenced = resource.Md5 is not null && referenced.Contains(resource.Md5);
            result.Attachments.Add(new AttachmentSummary
            {
                Note = summary,
                FileName = resource.FileName,
                Mime = resource.Mime,
                Size = resource.Size,
                Md5 = resource.Md5,
                Referenced = isReferenced,
                Width = resource.Width,
                Height = resource.Height,
            });

            var label = resource.FileName ?? resource.Mime ?? "attachment";
            if (!isReferenced)
            {
                issues.Add((IssueCodes.UnreferencedAttachment, $"\"{label}\" is not shown in the note body."));
            }
            if (resource.Size > OneNoteMaxPartBytes)
            {
                issues.Add((IssueCodes.LargeAttachment, $"\"{label}\" is {Format.Bytes(resource.Size)}."));
            }
            if (resource.Mime is { } mime && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !InlineImageTypes.Contains(mime))
            {
                issues.Add((IssueCodes.UnsupportedImage, $"\"{label}\" is {mime}."));
            }
            if (string.Equals(resource.Mime, "application/vnd.evernote.ink", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add((IssueCodes.InkAttachment, $"\"{label}\" is Evernote ink."));
            }
        }

        if (body is not null)
        {
            if (body.Encrypted.Count > 0)
            {
                var hints = body.Encrypted.Where(e => !string.IsNullOrEmpty(e.Hint)).Select(e => $"\"{e.Hint}\"").ToList();
                issues.Add((IssueCodes.EncryptedText, $"{body.Encrypted.Count} encrypted block(s)" +
                    (hints.Count > 0 ? $"; hint(s): {string.Join(", ", hints)}." : ".")));
            }
            if (body.UnknownEntities.Count > 0)
            {
                issues.Add((IssueCodes.UnknownEntity, string.Join(" ", body.UnknownEntities)));
            }
            if (body.UnexpectedElements.Count > 0)
            {
                issues.Add((IssueCodes.UnexpectedMarkup, "Elements: " + string.Join(", ", body.UnexpectedElements.Select(e => $"<{e}>"))));
            }
            if (body.MergedCells > 0)
            {
                issues.Add((IssueCodes.MergedTableCells, $"{body.MergedCells} merged cell(s)."));
            }
            if (body.ExternalImages > 0)
            {
                issues.Add((IssueCodes.ExternalImage, $"{body.ExternalImages} image(s) linked from the web."));
            }
        }

        if (note.UnknownElements.Count > 0)
        {
            issues.Add((IssueCodes.UnknownElement, $"{note.UnknownElements.Count} unrecognised item(s); preserved verbatim in recovered/."));
        }
        if (note.Tasks.Count > 0)
        {
            issues.Add((IssueCodes.Tasks, $"{note.Tasks.Count} task(s)."));
        }
        foreach (var (label, value) in new[] { ("created", note.Created), ("updated", note.Updated) })
        {
            if (value is not null && !EnexTimestamp.TryParse(value, out _))
            {
                issues.Add((IssueCodes.InvalidDate, $"Unrecognised {label} date \"{value}\"."));
            }
        }
        if (string.IsNullOrWhiteSpace(note.Title))
        {
            issues.Add((IssueCodes.UntitledNote, "The note has no title."));
        }
        if (text.Length == 0 && note.Resources.Count == 0 && note.Tasks.Count == 0 && status == TextStatus.Extracted && body?.Encrypted.Count is null or 0)
        {
            issues.Add((IssueCodes.EmptyNote, "The note has no text, attachments or tasks."));
        }

        foreach (var (code, message) in issues)
        {
            result.Issues.Add(new Issue(code, file.RelativePath, note.Index, note.Title, message));
        }
    }
}
