using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A Store/MSIX app installs under WindowsApps into a folder whose name carries its version; matching by package
/// family (name + publisher, no version) keeps a per-app rule bound to the app across its auto-updates.
/// </summary>
public sealed class AppPathTokenTests
{
    private const string WhatsAppExe = @"C:\Program Files\WindowsApps\5319275A.WhatsAppDesktop_2.2628.101.0_x64__cv1g1gvanyjgm\WhatsApp.exe";
    private const string WhatsAppDir = @"C:\Program Files\WindowsApps\5319275A.WhatsAppDesktop_2.2628.101.0_x64__cv1g1gvanyjgm";
    private const string WhatsAppFamily = "5319275A.WhatsAppDesktop_cv1g1gvanyjgm";

    [Theory]
    [InlineData(WhatsAppExe)]
    [InlineData(WhatsAppDir)]
    [InlineData(@"%PROGRAMFILES%\WindowsApps\5319275A.WhatsAppDesktop_2.2628.101.0_x64__cv1g1gvanyjgm\WhatsApp.exe")]
    [InlineData(@"C:/Program Files/WindowsApps/5319275A.WhatsAppDesktop_2.2628.101.0_x64__cv1g1gvanyjgm/WhatsApp.exe")]
    public void PackageFamily_FromPackagedPath(string path)
    {
        Assert.Equal(WhatsAppFamily, AppPathToken.PackageFamilyFromPath(path));
    }

    [Fact]
    public void PackageFamily_IsVersionIndependent()
    {
        var before = AppPathToken.PackageFamilyFromPath(
            @"C:\Program Files\WindowsApps\5319275A.WhatsAppDesktop_2.2628.101.0_x64__cv1g1gvanyjgm\WhatsApp.exe");
        var after = AppPathToken.PackageFamilyFromPath(
            @"C:\Program Files\WindowsApps\5319275A.WhatsAppDesktop_2.2629.100.0_x64__cv1g1gvanyjgm\WhatsApp.exe");

        Assert.Equal(before, after);
        Assert.Equal(WhatsAppFamily, after);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Discord\app-1.0.9013\Discord.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\WhatsApp\WhatsApp.exe")]
    [InlineData(@"C:\Foo\WindowsApps\bar\baz.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\NotAPackageFolder\app.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Name_1.0.0.0_x64__short\app.exe")]
    [InlineData("")]
    [InlineData("   ")]
    public void PackageFamily_NullForNonPackage(string path)
    {
        Assert.Null(AppPathToken.PackageFamilyFromPath(path));
    }

    [Theory]
    [InlineData("app:dir=" + WhatsAppDir)]
    [InlineData("app:path=" + WhatsAppExe)]
    public void NormalizeAppRule_PackagedBecomesPkg(string token)
    {
        Assert.Equal("app:pkg=" + WhatsAppFamily, AppPathToken.NormalizeAppRule(token));
    }

    [Theory]
    [InlineData(@"Z:\Tools\Vendor\app-1.2.3\", @"app:dir=Z:\Tools\Vendor\")]
    [InlineData(@"Z:\Tools\Vendor\v1.2.3", @"app:dir=Z:\Tools\Vendor\")]
    [InlineData(@"Z:\Tools\Vendor", @"app:dir=Z:\Tools\Vendor\")]
    [InlineData(@"Z:\WindowsApps\123.Messaging_2.0.0.0_x64__cv1g1gvanyjgm", "app:pkg=123.Messaging_cv1g1gvanyjgm")]
    public void ADiscoveredAppUsesAVersionIndependentRule(string directory, string expected)
    {
        Assert.Equal(expected, AppPathToken.RuleForDiscoveredDirectory(directory));
    }

    [Theory]
    [InlineData("%USERPROFILE%")]
    [InlineData("%LOCALAPPDATA%")]
    [InlineData("%APPDATA%")]
    [InlineData("%PROGRAMDATA%")]
    [InlineData("%PROGRAMFILES%")]
    [InlineData("%PROGRAMFILES(X86)%")]
    [InlineData("%SYSTEMROOT%")]
    public void NormalizeAppRule_UsesOneTrailingSeparatorForKnownDirectoryRoots(string token)
    {
        var root = AppPathToken.Expand(token).First().TrimEnd('\\', '/');
        var expected = "app:dir=" + token + "\\";

        foreach (var path in new[] { root, root + "\\", root + "\\\\", token, token + "\\" })
        {
            Assert.Equal(expected, AppPathToken.NormalizeAppRule("app:dir=" + path));
        }
    }

    [Fact]
    public void NormalizeAppRule_ExpandsAnyDirectoryVariableBeforeAddingTheSeparator()
    {
        var name = $"AMNEZIAGEO_PATH_TEST_{Guid.NewGuid():N}";
        const string folder = @"Z:\Games\Example";
        Environment.SetEnvironmentVariable(name, folder + "\\");

        try
        {
            Assert.Equal("app:dir=" + folder + "\\", AppPathToken.NormalizeAppRule($"app:dir=%{name}%"));
            Assert.Equal("app:path=" + folder + "\\app.exe", AppPathToken.NormalizeAppRule($"app:path=%{name}%\\app.exe"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Theory]
    [InlineData(@"%USERPROFILE%\AppData\Local", "%LOCALAPPDATA%")]
    [InlineData(@"%userprofile%\appdata\local\", "%LOCALAPPDATA%")]
    [InlineData(@"%USERPROFILE%\AppData\Roaming", "%APPDATA%")]
    [InlineData(@"%USERPROFILE%\AppData\Roaming\", "%APPDATA%")]
    [InlineData(@"%USERPROFILE%\AppData\Local\Vendor\app.exe", @"%LOCALAPPDATA%\Vendor\app.exe")]
    [InlineData(@"%USERPROFILE%\AppData\Roaming\Vendor\app.exe", @"%APPDATA%\Vendor\app.exe")]
    [InlineData(@"%USERPROFILE%\AppData\LocalLow", @"%USERPROFILE%\AppData\LocalLow")]
    [InlineData(@"%USERPROFILE%\AppData\RoamingBackup", @"%USERPROFILE%\AppData\RoamingBackup")]
    public void Tokenize_NormalizesLegacyProfileAppDataPaths(string path, string expected)
    {
        Assert.Equal(expected, AppPathToken.Tokenize(path));
    }

    [Theory]
    [InlineData("app:svc=Discord")]
    [InlineData("geosite:youtube")]
    public void NormalizeAppRule_OtherKindsPassThrough(string token)
    {
        Assert.Equal(token, AppPathToken.NormalizeAppRule(token));
    }

    [WindowsFact("the user profiles are read from the Windows Users folder")]
    public void Expand_PerUserRuleReachesEveryUserProfile()
    {
        var expanded = AppPathToken.Expand(@"%LOCALAPPDATA%\Discord");

        Assert.NotEmpty(expanded);
        Assert.All(expanded, one => Assert.EndsWith(@"AppData\Local\Discord", one, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(expanded, one => one.Contains(@"config\systemprofile", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Expand_MachineRuleStandsAsItIs()
    {
        var expanded = AppPathToken.Expand(@"%PROGRAMFILES%\AmneziaGeo");

        Assert.Equal([Environment.ExpandEnvironmentVariables(@"%PROGRAMFILES%\AmneziaGeo")], expanded);
    }
}
