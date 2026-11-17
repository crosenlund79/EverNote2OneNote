using EverNote2OneNote.Enex;

namespace EverNote2OneNote.Inspection;

public enum TextStatus
{
    /// <summary>The note body was parsed normally; the text is complete.</summary>
    Extracted,

    /// <summary>The body or note was damaged; text was recovered from the raw markup.</summary>
    Recovered,

    /// <summary>The note was damaged and its text could not be recovered automatically.</summary>
    NotRecovered,
}

/// <summary>Progress of <see cref="Inspector.Run"/>; <see cref="CurrentFile"/> is null once all files are read.</summary>
public sealed record InspectionProgress(int FilesCompleted, int FileCount, string? CurrentFile);

public sealed class InspectionResult
{
    public required string SourcePath { get; init; }
    public List<FileSummary> Files { get; } = [];
    public List<NoteSummary> Notes { get; } = [];
    public List<AttachmentSummary> Attachments { get; } = [];
    public List<Issue> Issues { get; } = [];

    public int CountIssues(Severity severity) => Issues.Count(i => i.Severity == severity);
}

public sealed class FileSummary
{
    public required string FullPath { get; init; }

    /// <summary>Path relative to the inspected folder.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Evernote exports one file per notebook, named after the notebook.</summary>
    public required string Notebook { get; init; }

    public required long Bytes { get; init; }
    public EnexHeader? Header { get; set; }
    public bool Unreadable { get; set; }
    public int NoteCount { get; set; }
}

public sealed class NoteSummary
{
    public required FileSummary File { get; init; }
    public required int Index { get; init; }
    public required long ByteOffset { get; init; }
    public required long Line { get; init; }
    public string? Title { get; init; }
    public string? Created { get; init; }
    public string? Updated { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<EnexTask> Tasks { get; init; } = [];

    /// <summary>The complete extracted text of the note body.</summary>
    public required string Text { get; init; }

    public required TextStatus TextStatus { get; init; }

    /// <summary>UTF-8 size of the original note body (ENML).</summary>
    public long ContentBytes { get; init; }

    public int AttachmentCount { get; init; }
    public long AttachmentBytes { get; init; }

    /// <summary>Attachments in a damaged note that could not be read; not included in <see cref="AttachmentCount"/>.</summary>
    public int UnreadAttachments { get; init; }
    public int TodoCount { get; init; }
    public int EncryptedBlocks { get; init; }

    /// <summary>Why the note or its body could not be parsed normally, if applicable.</summary>
    public string? Problem { get; init; }

    /// <summary>The original body, kept for the report when its text had to be recovered.</summary>
    public string? OriginalContent { get; init; }

    public IReadOnlyList<string> UnknownElements { get; init; } = [];

    public int Words => Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

public sealed class AttachmentSummary
{
    public required NoteSummary Note { get; init; }
    public string? FileName { get; init; }
    public string? Mime { get; init; }
    public long Size { get; init; }
    public string? Md5 { get; init; }
    public bool Referenced { get; init; }
    public string? Width { get; init; }
    public string? Height { get; init; }
}
