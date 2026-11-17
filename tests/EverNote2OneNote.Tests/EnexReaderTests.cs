using System.Security.Cryptography;
using System.Text;
using EverNote2OneNote.Enex;

namespace EverNote2OneNote.Tests;

public class EnexReaderTests
{
    [Fact]
    public void Reads_header_notes_and_metadata()
    {
        using var reader = EnexReader.Open(TestFiles.Fixture("Basic/Recipes.enex"));
        var notes = reader.ReadNotes().ToList();

        Assert.Empty(reader.Problems);
        Assert.Equal(new EnexHeader("Evernote", "10.105.4", "20261117T090000Z"), reader.Header);
        Assert.Equal(2, notes.Count);

        var pancakes = notes[0];
        Assert.Null(pancakes.ParseError);
        Assert.Equal("Pancakes & Syrup", pancakes.Title);
        Assert.Equal("20120315T081500Z", pancakes.Created);
        Assert.Equal("20130102T190000Z", pancakes.Updated);
        Assert.Equal(["breakfast", "sweet"], pancakes.Tags);
        Assert.Contains(new KeyValuePair<string, string>("author", "Test Author"), pancakes.Attributes);
        Assert.Contains(new KeyValuePair<string, string>("application-data[synthetic]", "value"), pancakes.Attributes);
        Assert.StartsWith("<?xml", pancakes.Content);

        var image = Assert.Single(pancakes.Resources);
        Assert.Equal("image/png", image.Mime);
        Assert.Equal(70, image.Size);
        Assert.Equal("b357a19c87624c7c4d131aeeb4ae677f", image.Md5);
        Assert.Equal("pan.png", image.FileName);
        Assert.Equal("1", image.Width);
    }

    [Fact]
    public void Reads_tasks_and_all_resources()
    {
        using var reader = EnexReader.Open(TestFiles.Fixture("Basic/Recipes.enex"));
        var shopping = reader.ReadNotes().ToList()[1];

        var task = Assert.Single(shopping.Tasks);
        Assert.Equal("Call the bakery", task.Title);
        Assert.Equal("open", task.Status);
        Assert.Contains(task.Fields, f => f.Key == "reminder" && f.Value.Contains("20200102T090000Z"));

        Assert.Equal(2, shopping.Resources.Count);
        var pdf = shopping.Resources[0];
        Assert.Equal(("application/pdf", 72L, "b33f8ab3fc2cba631d098816431e1027", "list.pdf"), (pdf.Mime, pdf.Size, pdf.Md5, pdf.FileName));
        var photo = shopping.Resources[1];
        Assert.Equal(("image/jpeg", 26L, "photo.jpg"), (photo.Mime, photo.Size, photo.FileName));
        Assert.True(photo.HasRecognition);
        Assert.Contains(new KeyValuePair<string, string>("source-url", "https://example.com/photo"), photo.Attributes);
    }

    [Fact]
    public void A_damaged_note_does_not_hide_the_notes_after_it()
    {
        using var reader = EnexReader.Open(TestFiles.Fixture("Problems/Damaged.enex"));
        var notes = reader.ReadNotes().ToList();

        Assert.Equal(["Before damage", "Fish & Chips", "Broken body", "Odd structures", "After damage"], notes.Select(n => n.Title));
        Assert.All(notes.Where(n => n.Index != 2), n => Assert.Null(n.ParseError));

        var damaged = notes[1];
        Assert.Contains("line 7", damaged.ParseError);
        Assert.Null(damaged.Content);
        Assert.Equal("Batter recipe: flour, beer & salt.\nFry at 180 degrees.", damaged.RecoveredText);
        Assert.Equal("20120315T081500Z", damaged.Created); // metadata after the damage is recovered too
        Assert.Equal("20130102T190000Z", damaged.Updated);

        Assert.Equal(["<custom-field>Keep me</custom-field>"], notes[3].UnknownElements);
        var stray = Assert.Single(reader.Problems);
        Assert.Equal(IssueCodes.UnexpectedContent, stray.Code);
        Assert.Contains("stray text", stray.Message);
    }

    [Fact]
    public void A_truncated_file_keeps_earlier_notes_and_recovers_partial_text()
    {
        using var temp = new TempDirectory();
        var full = File.ReadAllText(TestFiles.Fixture("Basic/Recipes.enex"));
        var cut = full[..(full.IndexOf("Shopping list for Saturday", StringComparison.Ordinal) + 26)];
        var path = temp.Write("Truncated.enex", cut);

        using var reader = EnexReader.Open(path);
        var notes = reader.ReadNotes().ToList();

        Assert.Equal(2, notes.Count);
        Assert.Null(notes[0].ParseError);
        Assert.NotNull(notes[1].ParseError);
        Assert.Equal("Milk\nBread\nShopping list for Saturday", notes[1].RecoveredText);
        Assert.Contains(reader.Problems, p => p.Code == IssueCodes.FileTruncated);
    }

    [Fact]
    public void An_unclosed_note_followed_by_another_note_is_isolated()
    {
        using var temp = new TempDirectory();
        var unclosed = Enex.Note("First", "<en-note><div>Alpha text</div></en-note>").Replace("</note>", "");
        var path = temp.Write("Unclosed.enex", Enex.File(unclosed, Enex.Note("Second", "<en-note><div>Beta text</div></en-note>")));

        using var reader = EnexReader.Open(path);
        var notes = reader.ReadNotes().ToList();

        Assert.Equal(2, notes.Count);
        Assert.NotNull(notes[0].ParseError);
        Assert.Equal("First", notes[0].Title);
        Assert.Contains("Alpha text", notes[0].Content); // the body itself was complete
        Assert.Null(notes[1].ParseError);
        Assert.Empty(reader.Problems);
    }

    [Fact]
    public void Invalid_attachment_data_is_reported_and_counted()
    {
        using var temp = new TempDirectory();
        var resource = "<resource><data encoding=\"base64\">@@@ not base64 @@@</data><mime>image/png</mime></resource>";
        var path = temp.Write("BadData.enex", Enex.File(Enex.Note("Bad data", "<en-note><div>Body text</div></en-note>", resource)));

        using var reader = EnexReader.Open(path);
        var note = Assert.Single(reader.ReadNotes());

        Assert.NotNull(note.ParseError);
        Assert.Contains("Body text", note.Content);
        Assert.Empty(note.Resources);
        Assert.Equal(1, note.UnreadAttachments);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(50)]
    [InlineData(101)]
    public void Small_read_buffers_give_identical_results(int bufferSize)
    {
        foreach (var path in Directory.EnumerateFiles(TestFiles.Fixtures, "*.enex", SearchOption.AllDirectories))
        {
            using var expected = EnexReader.Open(path);
            using var actual = EnexReader.Open(path, bufferSize);
            Assert.Equal(Describe(expected), Describe(actual));
        }

        static List<string> Describe(EnexReader reader) =>
            reader.ReadNotes()
                .Select(n => $"{n.Index}|{n.Line}|{n.ByteOffset}|{n.Title}|{n.Content}|{n.ParseError}|{n.RecoveredText}|" +
                             string.Join(",", n.Resources.Select(r => r.Md5)))
                .Concat(reader.Problems.Select(p => p.Code + p.Message))
                .ToList();
    }

    [Fact]
    public void Note_markup_inside_cdata_or_comments_is_not_a_note_boundary()
    {
        using var temp = new TempDirectory();
        var note = Enex.Note("Tricky", "<en-note><div>before</div><note>inside</note><div>after</div></en-note>");
        var path = temp.Write("Tricky.enex", Enex.File("<!-- <note> a commented-out note </note> -->\n", note));

        using var reader = EnexReader.Open(path);
        var single = Assert.Single(reader.ReadNotes());

        Assert.Null(single.ParseError);
        Assert.Contains("<note>inside</note>", single.Content);
        Assert.Empty(reader.Problems);
    }

    [Fact]
    public void External_entities_and_dtds_are_never_resolved()
    {
        using var temp = new TempDirectory();
        var secret = temp.Write("secret.txt", "TOPSECRET");
        var uri = new Uri(secret).AbsoluteUri;
        var body = $"<!DOCTYPE en-note [<!ENTITY inner SYSTEM \"{uri}\">]><en-note><div>Body &inner; end</div></en-note>";
        var enex = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                   $"<!DOCTYPE en-export [<!ENTITY outer SYSTEM \"{uri}\">]>\n" +
                   "<en-export>\n" +
                   $"<note><title>&outer;</title><content><![CDATA[{body}]]></content></note>\n" +
                   Enex.Footer;
        var path = temp.Write("Xxe.enex", enex);

        var result = Inspection.Inspector.Run(path);

        var note = Assert.Single(result.Notes);
        Assert.Equal("&outer;", note.Title);
        Assert.Equal("Body &inner; end", note.Text);
        Assert.DoesNotContain(result.Issues, i => i.Message.Contains("TOPSECRET"));
    }

    [Fact]
    public void Utf16_files_are_reported_instead_of_appearing_empty()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "Utf16.enex");
        File.WriteAllText(path, Enex.File(Enex.Note("A", "<en-note/>")), Encoding.Unicode);

        using var reader = EnexReader.Open(path);

        Assert.Equal(IssueCodes.UnsupportedEncoding, Assert.Single(reader.Problems).Code);
        Assert.Equal(0, reader.NoteCount);
    }

    [Fact]
    public void Non_enex_files_are_reported()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("Plain.enex", "just some text");

        using var reader = EnexReader.Open(path);

        Assert.Contains(reader.Problems, p => p.Code == IssueCodes.NotEnex);
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?>\n<en-export application=\"Evernote\"></en-export>\n")]
    [InlineData("<?xml version=\"1.0\"?>\n<en-export application=\"Evernote\"/>\n")]
    public void Empty_exports_are_valid(string content)
    {
        using var temp = new TempDirectory();
        using var reader = EnexReader.Open(temp.Write("Empty.enex", content));

        Assert.Empty(reader.Problems);
        Assert.Equal(0, reader.NoteCount);
        Assert.Equal("Evernote", reader.Header?.Application);
    }

    [Fact]
    public void Large_attachments_are_streamed_and_hashed_correctly()
    {
        using var temp = new TempDirectory();
        var data = new byte[30 * 1024 * 1024];
        new Random(42).NextBytes(data);
        var md5 = Convert.ToHexStringLower(MD5.HashData(data));
        var path = Path.Combine(temp.Path, "Large.enex");
        using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            writer.Write(Enex.Header);
            writer.Write($"<note><title>Big</title><content><![CDATA[{Enex.Body($"<en-note><en-media hash=\"{md5}\" type=\"application/zip\"/></en-note>")}]]></content>");
            writer.Write("<resource><data encoding=\"base64\">");
            writer.Write(Convert.ToBase64String(data, Base64FormattingOptions.InsertLineBreaks));
            writer.Write("</data><mime>application/zip</mime></resource></note>\n");
            writer.Write(Enex.Footer);
        }

        var result = Inspection.Inspector.Run(path);

        var attachment = Assert.Single(result.Attachments);
        Assert.Equal(data.Length, attachment.Size);
        Assert.Equal(md5, attachment.Md5);
        Assert.True(attachment.Referenced);
        Assert.Contains(result.Issues, i => i.Code == IssueCodes.LargeAttachment);
    }
}
