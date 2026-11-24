using System.Text.Json;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Android.Engine;

/// <summary>
/// Session the tunnel raises when no head asks for it: the system starts an always-on tunnel with a bare intent,
/// and a tunnel the system killed comes back the same way.
/// </summary>
public sealed record VpnRequest(
    string Config,
    string Name,
    string? AppMode,
    string[]? AppList,
    int Mtu,
    bool Ipv6,
    string? WsHost,
    int WsPort,
    int EngineLog = 1,
    int MtuMode = 0,
    bool DirectTcp = true,
    bool ExcludeRoutes = false,
    string[]? BypassApps = null,
    bool LocalInTunnel = false,
    bool WsOffered = false,
    NoticeWords? Notice = null,
    int SignalPort = 0,
    string[]? SignalFrom = null,
    string[]? Bases = null,
    bool HoldTraffic = true,
    string[]? VpnDns = null,
    bool VpnDnsNetwork = false,
    string? SessionId = null,
    GeoRoutingPlan? Plan = null,
    string? PlanStamp = null);

// A staged session is private to its launch. The service promotes it with a same-directory rename.
internal sealed class VpnSessionFiles(string directory)
{
    private const string StagedPrefix = "session-next-";
    private string Current => Path.Combine(directory, "session.json");

    public string Stage(VpnRequest request)
    {
        var id = Guid.NewGuid().ToString("N");
        WriteAtomic(StagedPath(id), request with { SessionId = id });
        return id;
    }

    public VpnRequest? Read() => ReadFile(Current);

    public VpnRequest? ReadStaged(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
        {
            return null;
        }

        var request = ReadFile(StagedPath(id));
        return request is { Plan: not null } && request.SessionId == id ? request : null;
    }

    // The rename and revocation both remove the staged path. Only one can win.
    public bool TryPromote(string id)
    {
        try
        {
            File.Move(StagedPath(id), Current, overwrite: true);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    public void Write(VpnRequest request) => WriteAtomic(Current, request);

    public bool Exists => File.Exists(Current);

    public void Clear() => File.Delete(Current);

    public void Drop(string id) => File.Delete(StagedPath(id));

    public void Prune()
    {
        var cutoff = DateTime.UtcNow.AddDays(-1);
        foreach (var file in Directory.EnumerateFiles(directory, StagedPrefix + "*.json*"))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff)
            {
                File.Delete(file);
            }
        }
    }

    private string StagedPath(string id) => Path.Combine(directory, StagedPrefix + id + ".json");

    private static VpnRequest? ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        var request = JsonSerializer.Deserialize<VpnRequest>(stream);
        return request is { Config.Length: > 0 } ? request : null;
    }

    private static void WriteAtomic(string path, VpnRequest request)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            using (var stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, request);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
