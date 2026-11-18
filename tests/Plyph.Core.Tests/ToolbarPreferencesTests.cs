using Plyph.Core;
using Xunit;

namespace Plyph.Core.Tests;

public sealed class ToolbarPreferencesTests
{
    [Fact]
    public void Action_Icons_RoundTrip_And_Existing_Actions_Keep_Their_Default()
    {
        var settings = NewSettings();
        settings.SetString("custom-actions", """[{"id":"legacy","name":"Existing","prompt":"Rewrite"}]""");

        var actions = ActionsStore.ReadActions(settings);
        Assert.Equal("custom", Assert.Single(actions).Icon);
        actions.Add(new PlyphAction { Id = "summary", Name = "Summarize", Prompt = "Summarize", Icon = "summary" });
        ActionsStore.WriteActions(settings, actions);

        Assert.Equal("summary", ActionsStore.GetAvailableActions(settings).Single(action => action.Id == "summary").Icon);
    }

    [Fact]
    public void Users_Can_Unpin_All_Default_Actions()
    {
        var settings = NewSettings();
        Assert.Equal(new[] { "ask", "correct", "rewrite" }, ActionsStore.GetQuickActionIds(settings));
        settings.SetString("quick-actions", "[]");
        Assert.Empty(ActionsStore.GetQuickActionIds(settings));
    }

    [Fact]
    public void New_Exclusions_Are_OptIn_And_Match_The_Executable_Exactly()
    {
        var settings = NewSettings();
        Assert.Empty(AppExclusions.Read(settings));

        var rule = AppExclusions.ForProcess("My, Editor.exe");
        settings.SetString("excluded-apps", rule + ", NotepadWindow");
        var rules = AppExclusions.Read(settings);
        Assert.Equal(2, rules.Count);
        Assert.Equal("My, Editor.exe", AppExclusions.DisplayName(rules[0]));
        Assert.True(AppExclusions.Matches(rules[0], "my, editor", "AnotherWindow"));
        Assert.False(AppExclusions.Matches(rules[0], "My, Editor Helper", "my, editor"));
        Assert.True(AppExclusions.Matches(rules[1], "editor", "NewNotepadWindow"));
    }

    private static PlyphSettings NewSettings() => new(StorageMode.Portable, Path.GetTempPath(), persistToDisk: false);
}
