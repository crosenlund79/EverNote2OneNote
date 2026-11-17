using EverNote2OneNote.Text;

namespace EverNote2OneNote.Tests;

public class EnmlTextTests
{
    private static EnmlExtraction Extract(string enml) => EnmlText.Extract(Enex.Body(enml));

    [Fact]
    public void Extracts_all_text_in_order_with_structure()
    {
        var result = Extract(
            "<en-note><div>Mix flour&nbsp;and milk. Caf&eacute; cr&egrave;me br&ucirc;l&eacute;e&mdash;optional&hellip;</div>" +
            "<div><br/></div>" +
            "<ul><li><en-todo checked=\"true\"/>Buy eggs</li><li><en-todo checked=\"false\"/>Heat pan</li></ul>" +
            "<table><tr><td colspan=\"2\">Ingredients</td></tr><tr><td>Flour</td><td>200 g</td></tr></table>" +
            "<div>See <a href=\"https://example.com/?a=1&amp;b=2\">the recipe <b>site</b></a>.</div>" +
            "<div><en-media hash=\"ABCDEF\" type=\"image/png\"/></div>" +
            "<pre>  line one\n    indented line</pre></en-note>");

        Assert.Null(result.FallbackReason);
        Assert.Equal(
            "Mix flour and milk. Café crème brûlée—optional…\n" +
            "\n" +
            "[x] Buy eggs\n" +
            "[ ] Heat pan\n" +
            "Ingredients\n" +
            "Flour\t200 g\n" +
            "See the recipe site.\n" +
            "  line one\n" +
            "    indented line",
            result.Text);
        Assert.Equal(2, result.TodoCount);
        Assert.Equal(1, result.TodoCheckedCount);
        Assert.Equal(["abcdef"], result.MediaHashes);
        Assert.Equal(1, result.MergedCells);
        Assert.Equal(1, result.Links);
    }

    [Theory]
    [InlineData("<div>  a   b  </div>", "a b")]
    [InlineData("<div>ab<b>c</b>d</div>", "abcd")]
    [InlineData("<div>a<br/>b</div>", "a\nb")]
    [InlineData("<p>one</p><p>two</p>", "one\ntwo")]
    [InlineData("<div>Fish & Chips</div>", "Fish & Chips")]
    [InlineData("<div>&lt;tag&gt; &amp; &#169; &#x263A;</div>", "<tag> & © ☺")]
    [InlineData("<div>&euro;&nbsp;5</div>", "€ 5")]
    [InlineData("", "")]
    public void Handles_whitespace_entities_and_line_breaks(string body, string expected)
    {
        var result = Extract(body.Length == 0 ? "<en-note/>" : $"<en-note>{body}</en-note>");

        Assert.Null(result.FallbackReason);
        Assert.Equal(expected, result.Text);
    }

    [Fact]
    public void Unknown_entities_are_kept_literally_and_reported()
    {
        var result = Extract("<en-note><div>Price &bogus; 10</div></en-note>");

        Assert.Null(result.FallbackReason);
        Assert.Equal("Price &bogus; 10", result.Text);
        Assert.Equal(["&bogus;"], result.UnknownEntities);
    }

    [Fact]
    public void Non_enml_elements_keep_their_text_and_are_reported()
    {
        var result = Extract("<en-note><section>Sectioned</section><font color=\"red\">Red</font><blink>Blink</blink></en-note>");

        Assert.Equal("Sectioned\nRedBlink", result.Text);
        Assert.Equal(["blink", "section"], result.UnexpectedElements);
    }

    [Fact]
    public void Encrypted_blocks_are_recorded_but_not_treated_as_text()
    {
        var result = Extract("<en-note><div>Code: <en-crypt hint=\"pet\" cipher=\"AES\">QUJD</en-crypt> end</div></en-note>");

        Assert.Equal("Code: end", result.Text);
        var block = Assert.Single(result.Encrypted);
        Assert.Equal(("pet", "AES", 4), (block.Hint, block.Cipher, block.CipherTextLength));
    }

    [Fact]
    public void Malformed_bodies_fall_back_without_losing_text()
    {
        var result = Extract(
            "<en-note><div>Unclosed <b>bold</div><div>Still here</div><p>a < b and c > d</p><p>Keep x <y z> w</p>" +
            "<en-media hash=\"ABC123\" type=\"image/png\"/><en-todo checked=\"true\"/><en-crypt hint=\"h\">QQ==</en-crypt></en-note>");

        Assert.NotNull(result.FallbackReason);
        Assert.Equal("Unclosed bold\nStill here\na < b and c > d\nKeep x <y z> w\nQQ==", result.Text);
        Assert.Equal(["abc123"], result.MediaHashes);
        Assert.Equal((1, 1), (result.TodoCount, result.TodoCheckedCount));
        Assert.Equal("h", Assert.Single(result.Encrypted).Hint);
    }

    [Fact]
    public void Raw_recovery_only_removes_recognised_tags()
    {
        Assert.Equal("one\ntwo <custom attr> three", RawText.FromMarkup("<div>one</div><div>two <custom attr> three</div>"));
        Assert.Equal("Title\nBody", RawText.FromMarkup("<title>Title</title><content><![CDATA[<en-note><div>Body</div></en-note>]]></content>"));
    }
}
