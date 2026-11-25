using System.Globalization;
using System.Text.Json;

namespace AmneziaGeo.Android.Ui.Services;

internal sealed record LegacyRoutingSelection(long? ListId)
{
    // The old JSON used null for both states. Zero preserves a known off choice on a save before migration.
    public static LegacyRoutingSelection? Read(JsonElement root) => root.TryGetProperty("SelectedRouting", out var selected)
        && selected.ValueKind == JsonValueKind.Number && selected.TryGetInt64(out var id) && id >= 0
            ? new LegacyRoutingSelection(id == 0 ? null : id)
            : null;

    public static LegacyRoutingSelection? ReadProfile(JsonElement root, string profile)
    {
        if (!root.TryGetProperty("Routing", out var routing) || routing.ValueKind != JsonValueKind.Object
            || !routing.TryGetProperty(profile, out var raw) || raw.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = raw.GetString().AsSpan();
        var bar = text.IndexOf('|');
        if (bar < 0 || !long.TryParse(text[..bar], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return null;
        }

        return text[(bar + 1)..] switch
        {
            "1" => new LegacyRoutingSelection(id),
            "0" => new LegacyRoutingSelection(ListId: null),
            _ => null
        };
    }
}
