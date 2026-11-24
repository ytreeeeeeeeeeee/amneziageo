namespace AmneziaGeo.Ipc;

/// <summary>
/// Resolves Linux application paths in the same form that /proc reports for running executables.
/// </summary>
public static class LinuxAppPath
{
    /// <summary>
    /// Follows links to the file or directory and its parents. Paths that cannot be resolved stay usable.
    /// </summary>
    public static string Resolve(string path)
    {
        if (!OperatingSystem.IsLinux() || !path.StartsWith('/'))
        {
            return path;
        }

        try
        {
            var linksLeft = 40;
            return ResolveFrom("/", path, ref linksLeft);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }
    }

    private static string ResolveFrom(string directory, string path, ref int linksLeft)
    {
        foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".")
            {
                continue;
            }

            if (component == "..")
            {
                directory = Path.GetDirectoryName(directory) ?? "/";
                continue;
            }

            var next = Path.Combine(directory, component);
            if (new FileInfo(next).LinkTarget is not { } target)
            {
                directory = next;
                continue;
            }

            if (linksLeft-- == 0)
            {
                throw new IOException("Too many symbolic links in an application path.");
            }

            directory = ResolveFrom(target.StartsWith('/') ? "/" : directory, target, ref linksLeft);
        }

        return directory;
    }
}
