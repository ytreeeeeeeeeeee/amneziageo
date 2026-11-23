using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The app picker shows every program the device lets it see at once, and saving it touches only the rules of the
/// programs it offered.
/// </summary>
public sealed class AppPickerTests
{
    [Fact]
    public void EveryProgram_IsShownAtOnce()
    {
        var picker = Picker();

        Assert.Equal(["org.auto", "org.browser", "org.downloads", "org.mail"], Shown(picker));
    }

    [Fact]
    public void ASearch_FindsAProgramByItsName()
    {
        var picker = Picker();

        picker.Query = " Auto ";

        Assert.Equal(["org.auto"], Shown(picker));
    }

    [Fact]
    public void ASearch_FindsAProgramByItsPackage()
    {
        var picker = Picker();

        picker.Query = "org.down";

        Assert.Equal(["org.downloads"], Shown(picker));
    }

    [Fact]
    public void ThePicker_AnswersWithEveryRowAndTheMarkedOnes()
    {
        var picker = Picker("org.mail");
        picker.Query = "auto";
        picker.Shown[0].Picked = true;

        Assert.Equal(["org.auto", "org.browser", "org.downloads", "org.mail"], picker.Offered);
        Assert.Equal(["org.auto", "org.mail"], picker.Picked);
    }

    [Theory]
    [InlineData(@"Z:\Games\Missing Game\game.exe", @"app:path=Z:\Games\Missing Game\game.exe")]
    [InlineData(@"app:path=Z:\Games\Missing Game\game.exe", @"app:path=Z:\Games\Missing Game\game.exe")]
    [InlineData(@"Z:\Games\Missing Game", @"app:dir=Z:\Games\Missing Game\")]
    [InlineData(@"Z:/Games/Missing Game/game.exe", @"app:path=Z:\Games\Missing Game\game.exe")]
    [InlineData(@"app:dir=%LOCALAPPDATA%/Missing Game", @"app:dir=%LOCALAPPDATA%\Missing Game\")]
    [InlineData(@"app:dir=Z:\Games\Missing Game", @"app:dir=Z:\Games\Missing Game\")]
    [InlineData(@"app:dir=Z:\Games\Folder.exe", @"app:dir=Z:\Games\Folder.exe\")]
    [InlineData(@"app:dir=Z:\Tools\v1.2", @"app:dir=Z:\Tools\v1.2\")]
    [InlineData(@"app:dir=%LOCALAPPDATA%\Missing Game", @"app:dir=%LOCALAPPDATA%\Missing Game\")]
    public void AnExplicitWindowsPath_IsOfferedAndAddedWithoutAnAppMatch(string input, string token)
    {
        var runningApps = new TaskCompletionSource<IpcAck>();
        var editor = new RoutingListEditorViewModel(new Silent(runningApps.Task));
        try
        {
            editor.AddMethodIndex = 1;
            editor.RuleInput = input;

            Assert.Equal(token, editor.MatchedSuggestions[0].Token);
            Assert.Equal(token, editor.TopSuggestions[0].Token);

            editor.PickSuggestionCommand.Execute(token);
            Assert.Equal(token, Assert.Single(editor.Rules));

            editor.RuleInput = input.Replace('/', '\\');
            Assert.DoesNotContain(editor.MatchedSuggestions, suggestion => suggestion.Token == token);
        }
        finally
        {
            runningApps.SetCanceled();
        }
    }

    [Theory]
    [InlineData(@"Z:\Games\Missing Game\game.exe", @"app:path=Z:\Games\Missing Game\game.exe")]
    [InlineData(@"Z:\Games\Missing Game", @"app:dir=Z:\Games\Missing Game\")]
    public void ACustomWindowsPathVariable_UsesTheExpandedPathForTheKindAndRule(string path, string token)
    {
        var variable = $"AMNEZIAGEO_PATH_TEST_{Guid.NewGuid():N}";
        var runningApps = new TaskCompletionSource<IpcAck>();
        var editor = new RoutingListEditorViewModel(new Silent(runningApps.Task));
        Environment.SetEnvironmentVariable(variable, path);
        try
        {
            editor.AddMethodIndex = 1;
            editor.RuleInput = $"%{variable}%";

            var suggestion = Assert.Single(editor.MatchedSuggestions);
            Assert.Equal(token, suggestion.Token);
            editor.PickSuggestionCommand.Execute(suggestion.Token);
            Assert.Equal(token, Assert.Single(editor.Rules));

            editor.RuleInput = path;
            Assert.Empty(editor.MatchedSuggestions);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            runningApps.SetCanceled();
        }
    }

    [Fact]
    public void AnOldFolderRuleWithoutATrailingSeparatorIsNotOfferedAgain()
    {
        var editor = Editor(@"app:dir=Z:\Games\Missing Game");
        editor.AddMethodIndex = 1;
        editor.RuleInput = @"Z:\Games\Missing Game\";

        Assert.Empty(editor.MatchedSuggestions);
    }

    [Theory]
    [InlineData(@"game.exe")]
    [InlineData(@"app:path=Games\game.exe")]
    [InlineData(@"app:path=Z:game.exe")]
    [InlineData(@"app:dir=\Games")]
    [InlineData(@"app:path=Z:\Games\.\game.exe")]
    [InlineData(@"app:dir=Z:/Games/../Tools")]
    [InlineData(@"app:dir=%LOCALAPPDATA%\..\Tools")]
    public void ARelativeOrNonCanonicalWindowsPath_IsNotOfferedAsAnExplicitRule(string input)
    {
        var runningApps = new TaskCompletionSource<IpcAck>();
        var editor = new RoutingListEditorViewModel(new Silent(runningApps.Task));
        try
        {
            editor.AddMethodIndex = 1;
            editor.RuleInput = input;

            Assert.Empty(editor.MatchedSuggestions);
            Assert.Empty(editor.TopSuggestions);
        }
        finally
        {
            runningApps.SetCanceled();
        }
    }

    [Fact]
    public void ARuleOnAProgramThePickerDidNotOffer_StaysAfterSave()
    {
        var editor = Editor("app:pkg=org.browser", "app:pkg=org.gone", "geoip:ru");

        editor.ApplyPickedApps("proxy", ["org.browser", "org.mail"], ["org.browser", "org.mail"]);

        Assert.Equal(["app:pkg=org.browser", "app:pkg=org.gone", "geoip:ru", "app:pkg=org.mail"], editor.ProxyRules);
    }

    [Fact]
    public void AnUnmarkedProgram_LosesItsRule()
    {
        var editor = Editor("app:pkg=org.browser", "app:pkg=org.gone", "app:pkg=org.mail");

        editor.ApplyPickedApps("proxy", ["org.browser", "org.mail"], ["org.mail"]);

        Assert.Equal(["app:pkg=org.gone", "app:pkg=org.mail"], editor.ProxyRules);
    }

    [Fact]
    public void KeptRules_StayInTheirPlaces()
    {
        var editor = Editor("app:pkg=org.mail", "geoip:ru", "app:pkg=org.browser");

        editor.ApplyPickedApps("proxy", ["org.auto", "org.browser", "org.mail"], ["org.auto", "org.browser", "org.mail"]);

        Assert.Equal(["app:pkg=org.mail", "geoip:ru", "app:pkg=org.browser", "app:pkg=org.auto"], editor.ProxyRules);
    }

    [Fact]
    public void ThePick_GoesToTheGroupItWasOpenedFor()
    {
        var editor = Editor("app:pkg=org.browser");

        editor.ApplyPickedApps("direct", ["org.browser", "org.mail"], ["org.mail"]);

        Assert.Equal(["app:pkg=org.browser"], editor.ProxyRules);
        Assert.Equal(["app:pkg=org.mail"], editor.DirectRules);
    }

    // A picker over four programs.
    private static AppPickerViewModel Picker(params string[] picked)
    {
        var picker = new AppPickerViewModel();
        picker.Load(
        [
            new AppPickerRow("Auto", "org.auto", picked.Contains("org.auto")),
            new AppPickerRow("Browser", "org.browser", picked.Contains("org.browser")),
            new AppPickerRow("Download manager", "org.downloads", picked.Contains("org.downloads")),
            new AppPickerRow("Mail", "org.mail", picked.Contains("org.mail")),
        ]);
        return picker;
    }

    private static string[] Shown(AppPickerViewModel picker) => [.. picker.Shown.Select(row => row.Package)];

    // An editor whose tunnel group carries the rules.
    private static RoutingListEditorViewModel Editor(params string[] rules)
    {
        var editor = new RoutingListEditorViewModel(new Silent());
        foreach (var rule in rules)
        {
            editor.ProxyRules.Add(rule);
        }

        return editor;
    }

    // Answers every command with nothing.
    private sealed class Silent(Task<IpcAck>? runningApps = null) : IAgentConnection
    {
        public event Action? Connected
        {
            add { }
            remove { }
        }

        public event Action? Disconnected
        {
            add { }
            remove { }
        }

        public event Action<StatusSnapshot>? SnapshotReceived
        {
            add { }
            remove { }
        }

        public void Start()
        {
        }

        public Task<IpcAck> SendCommandAsync(IpcCommand command) =>
            command.Op == IpcContract.OpListProcesses && runningApps is not null
                ? runningApps
                : Task.FromResult(new IpcAck(true, string.Empty));

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
