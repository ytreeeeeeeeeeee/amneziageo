using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.App;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The programs a list keeps direct are refused the tunnel adapter by their files, so the rules have to name the
/// files the disk holds.
/// </summary>
public sealed class AppMatcherImagesTests
{
    [Fact]
    public void Images_NameTheFileOfAPathRuleAndTheProgramsUnderAFolderRule()
    {
        var root = Directory.CreateTempSubdirectory("ageo-images-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "tool", "bin"));
            var named = Path.Combine(root, "bank.exe");
            var nested = Path.Combine(root, "tool", "bin", "helper.exe");
            File.WriteAllText(named, string.Empty);
            File.WriteAllText(nested, string.Empty);
            File.WriteAllText(Path.Combine(root, "tool", "readme.txt"), string.Empty);
            var matcher = new AppMatcher(["path=" + named, "path=" + Path.Combine(root, "gone.exe"), "dir=" + Path.Combine(root, "tool")],
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, past: true);

            var images = matcher.Images(8);

            Assert.Equal(new[] { named, nested }, images);
            Assert.Single(matcher.Images(1));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

/// <summary>
/// The rules by application are replaced on a running matcher, so a list edited while the tunnel stands is in
/// force without reconnecting it.
/// </summary>
public class AppMatcherReloadTests
{
    private static AppMatcher Matcher(params string[] rules) => new(rules, NullLogger<AppMatcher>.Instance);

    [Theory]
    [InlineData("path=Z:/Games/Missing Game/game.exe")]
    [InlineData("dir=Z:/Games/Missing Game")]
    public void AStoredRuleWithForwardSlashesMatchesTheWindowsProcessPath(string rule)
    {
        var matcher = Matcher(rule);

        Assert.True(matcher.MatchesDevicePath(@"Z:\Games\Missing Game\game.exe"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProgramDataRootMatchesNestedAppsAndKeepsTheFolderBoundary(bool usePortableRule)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).TrimEnd('\\');
        Assert.NotEmpty(root);
        var matcher = Matcher("dir=" + (usePortableRule ? "%PROGRAMDATA%" : root));

        Assert.True(matcher.MatchesDevicePath(Path.Combine(root, "Example", "bin", "app.exe")));
        Assert.False(matcher.MatchesDevicePath(Path.Combine(root + "-other", "app.exe")));
    }

    [Theory]
    [InlineData(@"AppData\Local", "%LOCALAPPDATA%")]
    [InlineData(@"AppData\Roaming", "%APPDATA%")]
    public void AppDataRootMatchesNestedAppsInNewAndPreviouslySavedRules(string relativePath, string token)
    {
        var profile = AppPathToken.Expand("%USERPROFILE%").First();
        var root = Path.Combine(profile, relativePath);
        Assert.Equal("app:dir=" + token + "\\", AppPathToken.NormalizeAppRule("app:dir=" + root));
        Assert.Equal(token, AppPathToken.Tokenize(root + "\\"));

        var image = Path.Combine(root, "Vendor", "bin", "app.exe");
        var sibling = Path.Combine(root + "Backup", "app.exe");
        foreach (var rule in new[] { token, root, @"%USERPROFILE%\" + relativePath })
        {
            var matcher = Matcher("dir=" + rule);

            Assert.True(matcher.MatchesDevicePath(image));
            Assert.False(matcher.MatchesDevicePath(sibling));
        }
    }

    [Theory]
    [InlineData("Local")]
    [InlineData("Roaming")]
    public void AParentAppDataRuleMatchesNestedApps(string folder)
    {
        var profile = AppPathToken.Expand("%USERPROFILE%").First();
        var root = Path.Combine(profile, "AppData");
        var image = Path.Combine(root, folder, "Vendor", "app.exe");
        var sibling = Path.Combine(profile, "AppDataBackup", folder, "Vendor", "app.exe");

        foreach (var rule in new[] { root, @"%USERPROFILE%\AppData" })
        {
            var matcher = Matcher("dir=" + rule);

            Assert.True(matcher.MatchesDevicePath(image));
            Assert.False(matcher.MatchesDevicePath(sibling));
        }
    }

    [Theory]
    [InlineData(@"Tools\app.exe")]
    [InlineData(@"AppData\Local\Vendor\app.exe")]
    [InlineData(@"AppData\Roaming\Vendor\app.exe")]
    public void AProfileRootMatchesNestedAppsWithOrWithoutATrailingSeparator(string relativePath)
    {
        var profile = AppPathToken.Expand("%USERPROFILE%").First();
        var image = Path.Combine(profile, relativePath);

        foreach (var rule in new[] { profile, profile + "\\", "%USERPROFILE%", "%USERPROFILE%\\" })
        {
            Assert.True(Matcher("dir=" + rule).MatchesDevicePath(image));
        }
    }

    [Theory]
    [InlineData(@"dir=Z:\Tools\v1.2")]
    [InlineData(@"dir=Z:\Tools\v1.2\")]
    public void AStoredVersionedFolderRuleMatchesOnlyAppsWithinThatFolder(string rule)
    {
        var matcher = Matcher(rule);

        Assert.True(matcher.MatchesDevicePath(@"Z:\Tools\v1.2\Vendor\app.exe"));
        Assert.False(matcher.MatchesDevicePath(@"Z:\Tools\OtherApp\app.exe"));
        Assert.False(matcher.MatchesDevicePath(@"Z:\Tools\v1.3\Vendor\app.exe"));
    }

    [Fact]
    public void AnEditedRuleSetIsPutInForce()
    {
        var matcher = Matcher("name=first.exe");

        Assert.True(matcher.Reload(["name=first.exe", "pkg=Some.App_publisher"]));
        Assert.True(matcher.HasMatchers);
    }

    [Fact]
    public void TheSameRulesAgainChangeNothing()
    {
        var matcher = Matcher("name=first.exe");

        Assert.False(matcher.Reload(["NAME=first.exe".ToLowerInvariant()]));
    }

    [Fact]
    public void RemovingEveryRuleLeavesNothingMatched()
    {
        var matcher = Matcher("name=first.exe");

        Assert.True(matcher.Reload([]));
        Assert.False(matcher.HasMatchers);
    }
}
