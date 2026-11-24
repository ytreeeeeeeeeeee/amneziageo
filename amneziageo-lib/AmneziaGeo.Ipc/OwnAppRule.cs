namespace AmneziaGeo.Ipc;

/// <summary>
/// Tells whether an app rule names this application itself. Such a rule sends the agent's own downloads, the
/// DNS proxy upstream and the websocket carrier into the tunnel they run.
/// </summary>
public static class OwnAppRule
{
    // Images that belong to this application wherever they stand.
    private static readonly string[] Images =
    [
        "amneziageo.exe",
        "amneziageo.windows.app.exe",
        "amneziageo.windows.tray.exe",
        "amneziageo.windows.ui.exe",
        "amneziageo",
        "amneziageo.linux.app",
        "amneziageo.linux.ui",
        "amneziawg-go",
    ];

    /// <summary>
    /// Whether the token names this application: one of its images, its own folder, or one of its services.
    /// </summary>
    public static bool Names(string token)
    {
        var value = Value(token);
        if (value.Length == 0)
        {
            return false;
        }

        if (token.StartsWith("app:svc=", StringComparison.OrdinalIgnoreCase))
        {
            return value.StartsWith("AmneziaGeo", StringComparison.OrdinalIgnoreCase);
        }

        if (token.StartsWith("app:pkg=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var unquoted = value.Trim('"');
        if (OperatingSystem.IsLinux() && unquoted.StartsWith('/'))
        {
            return NamesLinuxPath(unquoted, LinuxAppPath.Resolve(unquoted));
        }

        var path = AppPathToken.Tokenize(unquoted.Replace('/', '\\').TrimEnd('\\'));
        var leaf = path[(path.LastIndexOf('\\') + 1)..];
        if (Images.Contains(leaf, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // The install folder holds the helpers too (gateway, wstunnel), and a folder above it holds all of them.
        var own = OwnFolder();
        return own.Length > 0
            && (own.Equals(path, StringComparison.OrdinalIgnoreCase)
                || AppPathToken.IsUnderDirectory(path, own)
                || AppPathToken.IsUnderDirectory(own, path));
    }

    /// <summary>
    /// Checks a Linux rule against both its original path and the resolved path used by the matcher.
    /// </summary>
    public static bool NamesLinuxPath(string path, string resolvedPath)
    {
        var own = LinuxAppPath.Resolve(AppContext.BaseDirectory).TrimEnd('/');
        return MatchesLinuxPath(path, own) || MatchesLinuxPath(resolvedPath, own);
    }

    private static bool MatchesLinuxPath(string path, string own)
    {
        var value = path.TrimEnd('/');
        return Images.Contains(Path.GetFileName(value), StringComparer.OrdinalIgnoreCase)
            || (own.Length > 0 && (value.Equals(own, StringComparison.Ordinal)
                || value.StartsWith(own + "/", StringComparison.Ordinal)
                || own.StartsWith(value + "/", StringComparison.Ordinal)));
    }

    // Where this process runs from, in the same words a rule is written in.
    private static string OwnFolder() =>
        AppPathToken.Tokenize(AppContext.BaseDirectory.Replace('/', '\\').TrimEnd('\\'));

    // What the token names, without its kind.
    private static string Value(string token)
    {
        var eq = token.IndexOf('=');
        return eq > 0 ? token[(eq + 1)..].Trim() : string.Empty;
    }
}
