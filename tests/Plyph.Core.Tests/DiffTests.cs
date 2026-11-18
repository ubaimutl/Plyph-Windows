using Plyph.Core;
using Xunit;

namespace Plyph.Core.Tests;

public sealed class DiffTests
{
    public static IEnumerable<object[]> DiffPreserveCases => new List<object[]>
    {
        new object[] { "unchanged", "Hello world.", "Hello world." },
        new object[] { "replacement", "The cat sleeps.", "The dog sleeps." },
        new object[] { "insertion", "", "New text" },
        new object[] { "deletion", "Old text", "" },
        new object[] { "whitespace", "one\r\ntwo  three\t", "one\ntwo three\t\n" },
        new object[] { "unicode", "Grüße 👩🏽‍💻 日本語 e\u0301", "Grüße 🌍 日本語 é" },
        new object[] { "repeated words", "one two one two", "two one two one" },
        new object[] { "large rewrite", string.Concat(Enumerable.Repeat("old ", 2000)), string.Concat(Enumerable.Repeat("new ", 2000)) }
    };

    [Theory]
    [MemberData(nameof(DiffPreserveCases))]
    public void Diff_Preserves_Exact_Source_And_Result(string name, string before, string after)
    {
        _ = name;
        var parts = DiffEngine.DiffText(before, after);
        var reconstructedBefore = string.Concat(parts.Where(p => p.Type != "added").Select(p => p.Text));
        var reconstructedAfter = string.Concat(parts.Where(p => p.Type != "removed").Select(p => p.Text));

        Assert.Equal(before, reconstructedBefore);
        Assert.Equal(after, reconstructedAfter);
    }

    [Fact]
    public void Word_Diff_Isolates_Changed_Word_And_Preserves_Surrounding_Text()
    {
        var parts = DiffEngine.DiffText("The cat sleeps.", "The dog sleeps.");
        Assert.Equal(4, parts.Count);
        Assert.Equal(("equal", "The "), (parts[0].Type, parts[0].Text));
        Assert.Equal(("removed", "cat"), (parts[1].Type, parts[1].Text));
        Assert.Equal(("added", "dog"), (parts[2].Type, parts[2].Text));
        Assert.Equal(("equal", " sleeps."), (parts[3].Type, parts[3].Text));
    }

    [Fact]
    public void Diff_Escapes_User_Markup_And_Distinguishes_Changes()
    {
        var markup = DiffEngine.DiffMarkup("<b>&old</b>", "<b>&new</b>");
        Assert.DoesNotContain("<b>", markup);
        Assert.Contains("&lt;b&gt;&amp;", markup);
        Assert.Contains("strikethrough=\"true\">old", markup);
        Assert.Contains("underline=\"single\">new", markup);
    }
}
