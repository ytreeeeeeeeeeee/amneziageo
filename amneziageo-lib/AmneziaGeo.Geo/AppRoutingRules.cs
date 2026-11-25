using AmneziaGeo.Ipc;

namespace AmneziaGeo.Geo;

/// <summary>Application identities used by the target client's matcher.</summary>
public enum AppRulePlatform
{
    Other,
    Windows,
    Linux,
    Android,
}

internal static class AppRoutingRules
{
    private const char WindowsSeparator = '\\';
    public static AppRulePlatform CurrentPlatform { get; } = OperatingSystem.IsWindows() ? AppRulePlatform.Windows
        : OperatingSystem.IsAndroid() ? AppRulePlatform.Android
        : OperatingSystem.IsLinux() ? AppRulePlatform.Linux : AppRulePlatform.Other;

    public static Identity? Normalize(string token, AppRulePlatform platform)
    {
        var (kind, value) = AppRuleCover.Parse(token);
        if (value.Length == 0)
        {
            return null;
        }

        switch (platform)
        {
            case AppRulePlatform.Android:
                return kind == "pkg" ? new Identity(kind, value) : null;
            case AppRulePlatform.Windows:
                if (kind is "path" or "dir")
                {
                    if (value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal))
                    {
                        return null;
                    }

                    value = AppPathToken.Tokenize(value.Replace('/', WindowsSeparator));
                    if (AppPathToken.PackageFamilyFromPath(value) is { } family)
                    {
                        return new Identity("pkg", family.ToUpperInvariant());
                    }

                    if (kind == "dir")
                    {
                        value = value.TrimEnd(WindowsSeparator) + WindowsSeparator;
                    }
                }
                else if (kind == "pkg")
                {
                    value = AppPathToken.PackageFamilyFromFullName(value) ?? value;
                }

                return new Identity(kind, value.ToUpperInvariant());
            case AppRulePlatform.Linux:
                if (kind == "svc")
                {
                    return null;
                }

                if (kind is "path" or "dir")
                {
                    if (!value.StartsWith('/'))
                    {
                        return null;
                    }

                    value = LinuxAppPath.Resolve(value);
                    if (kind == "dir")
                    {
                        value = value.TrimEnd('/') + "/";
                    }
                }
                else if (kind == "pkg")
                {
                    kind = "name";
                }

                return new Identity(kind, value);
            default:
                return new Identity(kind, value);
        }
    }

    internal readonly record struct Identity(string Kind, string Value)
    {
        public bool Covers(Identity target)
        {
            if (this == target)
            {
                return true;
            }

            if (Kind == "dir" && target.Kind is "path" or "dir")
            {
                return target.Value.StartsWith(Value, StringComparison.Ordinal);
            }

            return Kind == "name" && target.Kind == "path"
                && Value == target.Value[(target.Value.LastIndexOfAny(['/', WindowsSeparator]) + 1)..];
        }
    }
}
