namespace EverNote2OneNote;

public enum Severity
{
    /// <summary>Data could not be read reliably. Must be resolved or explained before migrating.</summary>
    Error,

    /// <summary>Data was read, but some of it cannot be migrated as-is and needs review.</summary>
    Warning,

    /// <summary>Worth knowing; no data is at risk.</summary>
    Info,
}

public sealed record IssueDefinition(string Code, Severity Severity, string Description);

/// <summary>The fixed catalogue of findings the inspector reports.</summary>
public static class IssueCodes
{
    // Errors: something could not be read reliably.
    public const string FileUnreadable = nameof(FileUnreadable);
    public const string NotEnex = nameof(NotEnex);
    public const string UnsupportedEncoding = nameof(UnsupportedEncoding);
    public const string FileTruncated = nameof(FileTruncated);
    public const string UnexpectedContent = nameof(UnexpectedContent);
    public const string NoteMalformed = nameof(NoteMalformed);
    public const string ContentMalformed = nameof(ContentMalformed);

    // Warnings: read correctly, but needs attention before or during migration.
    public const string EncryptedText = nameof(EncryptedText);
    public const string MissingAttachment = nameof(MissingAttachment);
    public const string MissingContent = nameof(MissingContent);
    public const string UnknownElement = nameof(UnknownElement);
    public const string UnknownEntity = nameof(UnknownEntity);
    public const string UnexpectedMarkup = nameof(UnexpectedMarkup);
    public const string UnexpectedEncoding = nameof(UnexpectedEncoding);
    public const string InvalidDate = nameof(InvalidDate);
    public const string LargeAttachment = nameof(LargeAttachment);
    public const string UnsupportedImage = nameof(UnsupportedImage);
    public const string InkAttachment = nameof(InkAttachment);
    public const string DuplicateNotebook = nameof(DuplicateNotebook);

    // Info.
    public const string EmptyExport = nameof(EmptyExport);
    public const string EmptyNote = nameof(EmptyNote);
    public const string UntitledNote = nameof(UntitledNote);
    public const string UnreferencedAttachment = nameof(UnreferencedAttachment);
    public const string Tasks = nameof(Tasks);
    public const string MergedTableCells = nameof(MergedTableCells);
    public const string ExternalImage = nameof(ExternalImage);

    public static IReadOnlyList<IssueDefinition> All { get; } =
    [
        new(FileUnreadable, Severity.Error, "The file could not be opened or read to the end."),
        new(NotEnex, Severity.Error, "The file does not look like an Evernote ENEX export."),
        new(UnsupportedEncoding, Severity.Error, "The file uses a text encoding that cannot be read."),
        new(FileTruncated, Severity.Error, "The file ends unexpectedly; notes may be missing from the export."),
        new(UnexpectedContent, Severity.Error, "Data was found outside any note and is not attributed to a note."),
        new(NoteMalformed, Severity.Error, "The note's XML is damaged; text was recovered from raw markup where possible."),
        new(ContentMalformed, Severity.Error, "The note body is not valid ENML; text was recovered from raw markup."),

        new(EncryptedText, Severity.Warning, "Encrypted text cannot be read without its passphrase."),
        new(MissingAttachment, Severity.Warning, "The note body refers to an attachment that is not in the export."),
        new(MissingContent, Severity.Warning, "The note has no body."),
        new(UnknownElement, Severity.Warning, "Unrecognised ENEX data; preserved verbatim in the report."),
        new(UnknownEntity, Severity.Warning, "Unrecognised character reference; kept literally in the text."),
        new(UnexpectedMarkup, Severity.Warning, "The note body uses non-ENML elements; their text was kept."),
        new(UnexpectedEncoding, Severity.Warning, "The file declares an encoding other than UTF-8."),
        new(InvalidDate, Severity.Warning, "A date could not be parsed; the raw value is kept."),
        new(LargeAttachment, Severity.Warning, "Attachment over 25 MB, above OneNote's upload limit per file."),
        new(UnsupportedImage, Severity.Warning, "Image type OneNote cannot show inline; it can only be attached as a file."),
        new(InkAttachment, Severity.Warning, "Evernote ink (handwriting) cannot be shown in OneNote."),
        new(DuplicateNotebook, Severity.Warning, "Several files map to the same notebook (section) name."),

        new(EmptyExport, Severity.Info, "The file contains no notes."),
        new(EmptyNote, Severity.Info, "The note has no text, attachments or tasks."),
        new(UntitledNote, Severity.Info, "The note has no title."),
        new(UnreferencedAttachment, Severity.Info, "Attachment not shown in the note body; it is still included."),
        new(Tasks, Severity.Info, "Evernote tasks are stored outside the note body."),
        new(MergedTableCells, Severity.Info, "A table uses merged cells, which OneNote does not support."),
        new(ExternalImage, Severity.Info, "Image linked from the web; it is not part of the export."),
    ];

    private static readonly Dictionary<string, IssueDefinition> ByCode = All.ToDictionary(d => d.Code);

    public static IssueDefinition Get(string code) => ByCode[code];
}

/// <summary>One finding. <see cref="NoteIndex"/> is null for file-level findings.</summary>
public sealed record Issue(string Code, string File, int? NoteIndex, string? NoteTitle, string Message)
{
    public Severity Severity => IssueCodes.Get(Code).Severity;
}
