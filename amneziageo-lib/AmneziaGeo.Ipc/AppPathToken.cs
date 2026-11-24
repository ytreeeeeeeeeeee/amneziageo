using System.Text;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Rewrites app-rule file paths to portable %ENV% tokens so a routing rule survives export to
/// another machine or user account, and matches back regardless of who is running the app.
/// </summary>
public static class AppPathToken
{
    private const string UserProfile = "%USERPROFILE%";
    private static readonly char[] _separators = ['\\', '/'];

    /// <summary>
    /// Replaces a leading known-folder prefix with its canonical %ENV% token.
    /// </summary>
    public static string Tokenize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var p = NormalizeSeparators(path.Trim().Trim('"'));
        if (p.StartsWith('%'))
        {
            if (TryResolveProfilePath(p, out var profilePath))
            {
                return TokenizeProfilePath(profilePath);
            }

            var expanded = ExpandMachinePath(p);
            if (!System.IO.Path.IsPathFullyQualified(expanded))
            {
                return p;
            }

            p = expanded;
        }

        // Resolve profiles by path shape: the service runs with a different user environment.
        var usersRoot = UsersRoot();
        if (usersRoot.Length > 0 && TryGetTail(p, usersRoot, out var afterRoot) && !afterRoot.IsEmpty)
        {
            var separator = afterRoot.IndexOfAny(_separators);
            var relative = separator < 0 ? default : afterRoot[(separator + 1)..].TrimStart(_separators);
            return TokenizeProfilePath(relative);
        }

        foreach (var (directory, variable) in MachineFolders())
        {
            if (directory.Length > 0 && TryGetTail(p, directory, out var tail))
            {
                return JoinWindowsPath(variable, tail);
            }
        }

        return p;
    }

    /// <summary>
    /// Whether a tokenized path sits below a tokenized directory.
    /// </summary>
    public static bool IsUnderDirectory(string directory, string path) =>
        HasDescendant(directory, path)
        || ((directory.StartsWith('%') || path.StartsWith('%'))
            && HasDescendant(ComparisonPath(directory), ComparisonPath(path)));

    private static bool HasDescendant(string directory, string path) =>
        directory.Length > 0
        && TryGetTail(path, directory.AsSpan().TrimEnd(_separators), out var tail)
        && !tail.IsEmpty;

    private static string ComparisonPath(string path)
    {
        if (TryResolveProfilePath(path, out var relative))
        {
            // Portable profile tokens share a location beneath the Users directory without choosing an account.
            var root = UsersRoot();
            var profile = root.Length > 0 ? JoinWindowsPath(root, UserProfile) : UserProfile;
            return JoinWindowsPath(profile, relative);
        }

        return path.StartsWith('%') ? ExpandMachinePath(path) : path;
    }

    /// <summary>
    /// Every path a tokenized rule stands for on this machine. Per-user paths can belong to any profile.
    /// </summary>
    public static IReadOnlyList<string> Expand(string token)
    {
        var path = NormalizeSeparators(token.Trim().Trim('"'));
        return TryResolveProfilePath(path, out var relative)
            ? [.. Profiles().Select(profile => System.IO.Path.Combine(profile, relative))]
            : [ExpandMachinePath(path)];
    }

    private static readonly (string Variable, string Folder)[] _userFolders =
    [
        ("%LOCALAPPDATA%", "AppData\\Local"),
        ("%APPDATA%", "AppData\\Roaming"),
        (UserProfile, ""),
    ];

    private static string TokenizeProfilePath(ReadOnlySpan<char> path)
    {
        foreach (var (variable, folder) in _userFolders)
        {
            if (folder.Length > 0 && TryGetTail(path, folder, out var tail))
            {
                return JoinWindowsPath(variable, tail);
            }
        }

        return JoinWindowsPath(UserProfile, path);
    }

    private static bool TryResolveProfilePath(string path, out string relative)
    {
        foreach (var (variable, folder) in _userFolders)
        {
            if (TryGetTail(path, variable, out var tail))
            {
                relative = JoinWindowsPath(folder, tail);
                return true;
            }
        }

        relative = string.Empty;
        return false;
    }

    private static string ExpandMachinePath(string path)
    {
        foreach (var (directory, variable) in MachineFolders())
        {
            if (directory.Length > 0 && TryGetTail(path, variable, out var tail))
            {
                return JoinWindowsPath(directory, tail);
            }
        }

        return ExpandInputPath(path);
    }

    /// <summary>
    /// Expands variables using the current account when accepting a path entered in the editor.
    /// </summary>
    public static string ExpandInputPath(string path)
    {
        var end = path.StartsWith('%') ? path.IndexOf('%', 1) : -1;
        if (end > 1 && TryGetTail(path, path.AsSpan(0, end + 1), out var tail) && !tail.IsEmpty
            && Environment.GetEnvironmentVariable(path[1..end]) is { Length: > 0 } root)
        {
            var suffix = Environment.ExpandEnvironmentVariables(tail.ToString());
            return NormalizeSeparators(root.TrimEnd(_separators) + DirectorySeparator(root) + suffix);
        }

        return NormalizeSeparators(Environment.ExpandEnvironmentVariables(path));
    }

    private static bool TryGetTail(ReadOnlySpan<char> path, ReadOnlySpan<char> prefix, out ReadOnlySpan<char> tail)
    {
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (path.Length == prefix.Length || path[prefix.Length] is '\\' or '/'))
        {
            tail = path[prefix.Length..].TrimStart(_separators);
            return true;
        }

        tail = default;
        return false;
    }

    private static string JoinWindowsPath(string root, ReadOnlySpan<char> tail) =>
        tail.IsEmpty ? root : root.Length == 0 ? tail.ToString() : string.Concat(root, "\\", tail);

    private static string NormalizeSeparators(string path) =>
        OperatingSystem.IsWindows() ? NormalizeWindowsSeparators(path) : path;

    internal static string NormalizeWindowsSeparators(string path)
    {
        path = path.Replace('/', '\\');

        // UNC and device paths start with two separators.
        var start = path.StartsWith(@"\\", StringComparison.Ordinal) ? 1 : 0;
        var repeated = path.IndexOf(@"\\", start, StringComparison.Ordinal);
        if (repeated < 0)
        {
            return path;
        }

        var normalized = new StringBuilder(path.Length);
        normalized.Append(path, 0, repeated + 1);
        for (var i = repeated + 1; i < path.Length; i++)
        {
            if (path[i] != '\\' || path[i - 1] != '\\')
            {
                normalized.Append(path[i]);
            }
        }

        return normalized.ToString();
    }

    private static char DirectorySeparator(string path) =>
        !OperatingSystem.IsWindows() && path.StartsWith('/') ? '/' : '\\';

    private static string WithTrailingSeparator(string directory) =>
        directory.Length == 0 ? directory : DirectorySeparator(directory) == '/'
            ? directory.TrimEnd('/') + '/'
            : directory.TrimEnd(_separators) + '\\';

    // The user profiles on this machine.
    private static IReadOnlyList<string> Profiles()
    {
        var root = UsersRoot();
        if (root.Length == 0)
        {
            return [];
        }

        try
        {
            return System.IO.Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Builds a rule from a discovered app's install folder, allowing versioned installs to move after an update.
    /// </summary>
    public static string RuleForDiscoveredDirectory(string directory)
    {
        var path = Tokenize(directory);
        return PackageFamilyFromPath(path) is { } family
            ? "app:pkg=" + family
            : "app:dir=" + WithTrailingSeparator(StripVersionedLeaf(path));
    }

    private static string StripVersionedLeaf(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return dir;
        }

        var trimmed = dir.TrimEnd('\\', '/');
        var slash = trimmed.LastIndexOfAny(['\\', '/']);
        if (slash <= 0)
        {
            return trimmed;
        }

        var leaf = trimmed[(slash + 1)..];
        return LooksVersioned(leaf) ? trimmed[..slash] : trimmed;
    }

    // A folder name that carries a version: Squirrel's app-<ver>, a v-prefixed semver, or a bare dotted number.
    private static bool LooksVersioned(string segment)
    {
        if (segment.StartsWith("app-", StringComparison.OrdinalIgnoreCase)
            && segment.Length > 4
            && char.IsDigit(segment[4]))
        {
            return true;
        }

        var i = 0;
        if (segment.Length > 0 && (segment[0] == 'v' || segment[0] == 'V'))
        {
            i = 1;
        }

        if (i >= segment.Length || !char.IsDigit(segment[i]))
        {
            return false;
        }

        var hasDot = false;
        for (; i < segment.Length; i++)
        {
            var c = segment[i];
            if (c == '.')
            {
                hasDot = true;
            }
            else if (!char.IsDigit(c))
            {
                break;
            }
        }

        return hasDot;
    }

    /// <summary>
    /// Tokenizes the value of an app:dir= / app:path= rule token; other kinds pass through unchanged.
    /// </summary>
    public static string TokenizeRule(string token)
    {
        var eq = token.IndexOf('=');
        if (eq <= 0)
        {
            return token;
        }

        var kind = token[..eq];
        if (!kind.Equals("app:dir", StringComparison.OrdinalIgnoreCase)
            && !kind.Equals("app:path", StringComparison.OrdinalIgnoreCase))
        {
            return token;
        }

        var value = Tokenize(token[(eq + 1)..]);
        if (kind.Equals("app:dir", StringComparison.OrdinalIgnoreCase))
        {
            value = WithTrailingSeparator(value);
        }

        return kind + "=" + value;
    }

    /// <summary>
    /// Normalizes an app:dir= / app:path= rule to a version-independent app:pkg= matcher for a Store/MSIX app,
    /// or to a portable %ENV% path otherwise. Other kinds pass through unchanged.
    /// </summary>
    public static string NormalizeAppRule(string token)
    {
        var eq = token.IndexOf('=');
        if (eq > 0)
        {
            var kind = token[..eq];
            if (kind.Equals("app:dir", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("app:path", StringComparison.OrdinalIgnoreCase))
            {
                var path = token[(eq + 1)..];
                if (!(OperatingSystem.IsLinux() && path.StartsWith('/')) && PackageFamilyFromPath(path) is { } family)
                {
                    return "app:pkg=" + family;
                }
            }
        }

        return TokenizeRule(token);
    }

    /// <summary>
    /// Returns the package family name (Name + PublisherId) for an image path under WindowsApps, dropping the
    /// version, architecture and resource id, so a rule keeps matching a Store/MSIX app across its auto-updates.
    /// Null when the path is not a packaged app.
    /// </summary>
    public static string? PackageFamilyFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        const string marker = "\\WindowsApps\\";
        var norm = NormalizeWindowsSeparators(path);
        var at = norm.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var after = norm[(at + marker.Length)..];
        var slash = after.IndexOf('\\');
        var fullName = slash >= 0 ? after[..slash] : after;
        return PackageFamilyFromFullName(fullName);
    }

    /// <summary>
    /// Returns the package family "Name_PublisherId" for a package full name
    /// "Name_Version_Architecture_ResourceId_PublisherId", or null when the name is not one.
    /// </summary>
    public static string? PackageFamilyFromFullName(string fullName)
    {
        var parts = fullName.Split('_');
        if (parts.Length < 5)
        {
            return null;
        }

        var name = parts[0];
        var publisher = parts[^1];
        return name.Length > 0 && IsPublisherId(publisher) ? name + "_" + publisher : null;
    }

    // A package publisher id: 13 base32 characters.
    private static bool IsPublisherId(string segment)
    {
        if (segment.Length != 13)
        {
            return false;
        }

        foreach (var c in segment)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
            {
                return false;
            }
        }

        return true;
    }

    private static string UsersRoot()
    {
        var pub = Environment.GetEnvironmentVariable("PUBLIC");
        if (!string.IsNullOrEmpty(pub))
        {
            var root = System.IO.Path.GetDirectoryName(NormalizeSeparators(pub));
            if (!string.IsNullOrEmpty(root))
            {
                return root.TrimEnd('\\', '/');
            }
        }

        var drive = Environment.GetEnvironmentVariable("SystemDrive");
        return string.IsNullOrEmpty(drive) ? string.Empty : drive + "\\Users";
    }

    // (x86) first: its path has the plain Program Files path as a prefix.
    private static IEnumerable<(string Dir, string Token)> MachineFolders()
    {
        yield return (Env("ProgramFiles(x86)"), "%PROGRAMFILES(X86)%");
        yield return (Env("ProgramW6432"), "%PROGRAMFILES%");
        yield return (Env("ProgramFiles"), "%PROGRAMFILES%");
        yield return (Env("ProgramData"), "%PROGRAMDATA%");
        yield return (Env("SystemRoot"), "%SYSTEMROOT%");
    }

    private static string Env(string name)
    {
        return NormalizeSeparators(Environment.GetEnvironmentVariable(name) ?? string.Empty).TrimEnd(_separators);
    }
}
