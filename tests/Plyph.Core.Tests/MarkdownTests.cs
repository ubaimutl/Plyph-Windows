using Plyph.Core;
using Xunit;

namespace Plyph.Core.Tests;

public sealed class MarkdownTests
{
    [Fact]
    public void Discussion_Renders_Headings_Emphasis_And_Lists_Without_Raw_Markers()
    {
        Assert.Equal(
            "<b>Summary</b>\n\n• <b>Personal context</b>: shared background\n• <i>Actions</i>: specific instructions\n\n1. Do the task.",
            MarkdownParser.MarkdownMarkup("# Summary\n\n- **Personal context**: shared background\n- *Actions*: specific instructions\n\n1. Do the task."));

        Assert.Equal(
            "<b><i>Important</i></b> and <s>old</s>",
            MarkdownParser.MarkdownMarkup("***Important*** and ~~old~~"));
    }

    [Fact]
    public void Code_Remains_Literal_Including_Xml_Characters_And_Markdown_Syntax()
    {
        Assert.Equal(
            "Use <tt>some_name</tt> and <tt>a ` b</tt>.",
            MarkdownParser.MarkdownMarkup("Use `some_name` and ``a ` b``."));

        Assert.Equal(
            "<tt>if a &lt; b:\n    print(\"**hello** &amp; world\")</tt>",
            MarkdownParser.MarkdownMarkup("```python\nif a < b:\n    print(\"**hello** & world\")\n```"));

        Assert.Equal(
            "<tt>__raw__</tt>",
            MarkdownParser.MarkdownMarkup("~~~\n__raw__\n~~~"));

        Assert.Equal(
            "<tt>unclosed &lt;code&gt;</tt>",
            MarkdownParser.MarkdownMarkup("```\nunclosed <code>"));
    }

    [Fact]
    public void Untrusted_Html_And_Links_Cannot_Inject_Markup()
    {
        Assert.Equal(
            "&lt;span foreground=\"red\"&gt;Oops&lt;/span&gt; &amp; &lt;img src=x&gt;",
            MarkdownParser.MarkdownMarkup("<span foreground=\"red\">Oops</span> & <img src=x>"));

        Assert.Equal(
            "Docs (https://example.com/?a=1&amp;b=2)",
            MarkdownParser.MarkdownMarkup("[Docs](https://example.com/?a=1&b=2)"));

        Assert.Equal(
            "image",
            MarkdownParser.MarkdownMarkup("![image](https://example.com/photo.png)"));

        Assert.Equal(
            "unsafe (javascript:alert)",
            MarkdownParser.MarkdownMarkup("[unsafe](javascript:alert)"));
    }

    [Fact]
    public void Plain_Text_Escaped_Markers_And_Identifiers_Stay_Readable()
    {
        Assert.Equal(
            "some_variable and /tmp/my_file.py\n\nHello 🌍",
            MarkdownParser.MarkdownMarkup("some_variable and /tmp/my_file.py\n\nHello 🌍"));

        Assert.Equal(
            "*literal* and **unfinished",
            MarkdownParser.MarkdownMarkup("\\*literal\\* and **unfinished"));

        Assert.Equal(
            "│ A quote\n\n────────",
            MarkdownParser.MarkdownMarkup("> A quote\n\n---"));
    }

    [Fact]
    public void ParseToSpans_Converts_Markup_To_Styled_Segments()
    {
        var spans = MarkdownParser.ParseToSpans("Hello **bold** and `code`");
        Assert.Contains(spans, s => s.Text == "bold" && s.Bold);
        Assert.Contains(spans, s => s.Text == "code" && s.Monospace);
    }
}
