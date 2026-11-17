namespace EverNote2OneNote.Text;

internal static class HtmlElements
{
    /// <summary>Elements permitted in ENML, plus the Evernote-specific ones.</summary>
    public static readonly HashSet<string> Enml = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "acronym", "address", "area", "b", "bdo", "big", "blockquote", "br", "caption",
        "center", "cite", "code", "col", "colgroup", "dd", "del", "dfn", "div", "dl", "dt", "em", "font",
        "h1", "h2", "h3", "h4", "h5", "h6", "hr", "i", "img", "ins", "kbd", "li", "map", "ol", "p", "pre",
        "q", "s", "samp", "small", "span", "strike", "strong", "sub", "sup", "table", "tbody", "td",
        "tfoot", "th", "thead", "tr", "tt", "u", "ul", "var", "xmp",
        "en-note", "en-media", "en-crypt", "en-todo",
    };

    /// <summary>Elements that start a new line of text.</summary>
    public static readonly HashSet<string> Block = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "caption", "center", "dd", "details", "div", "dl",
        "dt", "en-note", "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4",
        "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p", "pre", "section", "summary", "table",
        "tbody", "tfoot", "thead", "tr", "ul", "xmp",
    };

    /// <summary>
    /// Every element name that may legitimately appear in an ENEX file or a note body.
    /// The raw-markup fallback only removes tags with these names, so text that merely
    /// looks like a tag (e.g. "a &lt;b and c&gt; d" in damaged markup) is never dropped.
    /// </summary>
    public static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // ENEX structure
        "en-export", "note", "title", "content", "created", "updated", "tag", "note-attributes",
        "resource", "data", "mime", "width", "height", "duration", "recognition", "resource-attributes",
        "alternate-data", "task", "reminder",
        // note and resource attributes
        "subject-date", "latitude", "longitude", "altitude", "author", "source", "source-url",
        "source-application", "reminder-order", "reminder-time", "reminder-done-time", "place-name",
        "content-class", "application-data", "timestamp", "camera-make", "camera-model", "reco-type",
        "file-name", "attachment",
        // task fields
        "taskStatus", "inNote", "taskFlag", "sortWeight", "noteLevelID", "taskGroupNoteLevelID",
        "dueDate", "dueDateUIOption", "timeZone", "recurrence", "repeatAfterCompletion",
        "statusUpdated", "creator", "lastEditor", "reminderDate", "reminderDateUIOption",
        "dueDateOffset", "reminderStatus",
        // prohibited in ENML but found in real-world HTML
        "applet", "base", "basefont", "bgsound", "blink", "body", "button", "dir", "embed", "fieldset",
        "form", "frame", "frameset", "head", "html", "iframe", "ilayer", "input", "isindex", "label",
        "layer", "legend", "link", "marquee", "menu", "meta", "noframes", "noscript", "object",
        "optgroup", "option", "param", "plaintext", "script", "select", "style", "textarea", "xml",
        // common HTML5
        "article", "aside", "details", "figcaption", "figure", "footer", "header", "main", "mark",
        "nav", "section", "summary", "time", "svg",
    };

    static HtmlElements()
    {
        Known.UnionWith(Enml);
    }
}
