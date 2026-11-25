using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Routing;

/// <summary>Settles overlapping address rules by prefix length, then Block, Direct and Proxy.</summary>
public static class RangeClaims
{
    /// <summary>Removes addresses claimed by a stronger rule from each bucket.</summary>
    public static (IReadOnlyList<string> Proxy, IReadOnlyList<string> Direct, IReadOnlyList<string> Block) Settle(
        IReadOnlyList<string> proxy, IReadOnlyList<string> direct, IReadOnlyList<string> block)
    {
        var proxySpans = Parse(proxy);
        var directSpans = Parse(direct);
        var blockSpans = Parse(block);
        return (
            Carve(proxy, [.. directSpans, .. blockSpans], Keys(directSpans, blockSpans)),
            Carve(direct, [.. proxySpans, .. blockSpans], Keys(blockSpans)),
            Carve(block, [.. proxySpans, .. directSpans], []));
    }

    /// <summary>Canonical address and prefix, used to recognize duplicate rules.</summary>
    public static string Key(string entry) => TryParse(entry, out var range)
        ? Format(range.Start, range.Bits, range.Width) : entry.Trim();

    /// <summary>Builds an address membership test over both address families.</summary>
    public static Func<IPAddress, bool> Matcher(IReadOnlyList<string> entries)
    {
        var ipv4 = GeoIpRanges.Build(entries);
        var ipv6 = new List<Range>();
        foreach (var range in Parse(entries).Where(range => range.Width == 128).OrderBy(range => range.Start))
        {
            if (ipv6.Count > 0 && range.Start <= ipv6[^1].End)
            {
                ipv6[^1] = ipv6[^1] with { End = UInt128.Max(ipv6[^1].End, range.End) };
            }
            else
            {
                ipv6.Add(range);
            }
        }

        return address =>
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (GeoIpRanges.TryToNumeric(address, out var v4))
            {
                return ipv4.Contains(v4);
            }

            if (ipv6.Count == 0 || address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }

            var value = NumericIpv6(address);
            var at = LowerBound(ipv6, 128, value);
            return (at < ipv6.Count && ipv6[at].Start == value) || (at > 0 && value <= ipv6[at - 1].End);
        };
    }

    private readonly record struct Range(UInt128 Start, UInt128 End, int Bits, int Width);

    private static IReadOnlyList<string> Carve(IReadOnlyList<string> entries, List<Range> others,
        HashSet<(int Width, UInt128 Start, int Bits)> stronger)
    {
        if (entries.Count == 0 || others.Count == 0)
        {
            return entries;
        }

        others.Sort(static (a, b) => a.Width != b.Width ? a.Width.CompareTo(b.Width) : a.Start.CompareTo(b.Start));
        var carved = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            if (!TryParse(entry, out var range))
            {
                carved.Add(entry);
            }
            else if (!stronger.Contains((range.Width, range.Start, range.Bits)))
            {
                CarveRange(carved, entry, range, others);
            }
        }

        return carved;
    }

    private static void CarveRange(List<string> carved, string original, Range range, List<Range> others)
    {
        var head = range.Start;
        for (var at = LowerBound(others, range.Width, head); at < others.Count; at++)
        {
            var hole = others[at];
            if (hole.Width != range.Width || hole.Start > range.End)
            {
                break;
            }

            if (hole.Bits <= range.Bits || hole.End < head)
            {
                continue;
            }

            if (hole.Start > head)
            {
                AddPrefixes(carved, head, hole.Start - 1, range.Width);
            }

            if (hole.End == range.End)
            {
                return;
            }

            head = hole.End + 1;
        }

        if (head == range.Start)
        {
            carved.Add(original);
        }
        else
        {
            AddPrefixes(carved, head, range.End, range.Width);
        }
    }

    private static List<Range> Parse(IReadOnlyList<string> entries)
    {
        var spans = new List<Range>(entries.Count);
        foreach (var entry in entries)
        {
            if (TryParse(entry, out var range))
            {
                spans.Add(range);
            }
        }

        return spans;
    }

    private static HashSet<(int, UInt128, int)> Keys(params List<Range>[] sets) =>
        sets.SelectMany(set => set).Select(range => (range.Width, range.Start, range.Bits)).ToHashSet();

    private static bool TryParse(string entry, out Range range)
    {
        var text = entry.Trim();
        if (GeoIpRanges.TryParse(text, out var start, out var end, out var bits))
        {
            range = new Range(start, end, bits, 32);
            return true;
        }

        range = default;
        var slash = text.IndexOf('/');
        var host = slash < 0 ? text.AsSpan() : text.AsSpan(0, slash);
        bits = 128;
        if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetworkV6
            || (slash >= 0 && (!int.TryParse(text.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out bits)
                || bits is < 0 or > 128)))
        {
            return false;
        }

        var value = NumericIpv6(address);
        var mask = bits == 0 ? UInt128.Zero : UInt128.MaxValue << (128 - bits);
        range = new Range(value & mask, value | ~mask, bits, 128);
        return true;
    }

    private static UInt128 NumericIpv6(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt128BigEndian(bytes);
    }

    private static int LowerBound(List<Range> spans, int width, UInt128 start)
    {
        var low = 0;
        var high = spans.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            var range = spans[mid];
            if (range.Width < width || (range.Width == width && range.Start < start))
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static void AddPrefixes(List<string> carved, UInt128 head, UInt128 end, int width)
    {
        while (true)
        {
            var size = Math.Min(width, (int)UInt128.TrailingZeroCount(head));
            var span = size == 128 ? UInt128.MaxValue : (UInt128.One << size) - 1;
            while (span > end - head)
            {
                size--;
                span >>= 1;
            }

            carved.Add(Format(head, width - size, width));
            if (span == end - head)
            {
                return;
            }

            head += span + 1;
        }
    }

    private static string Format(UInt128 start, int prefix, int width)
    {
        if (width == 32)
        {
            return GeoIpRanges.Format((uint)start) + "/" + prefix.ToString(CultureInfo.InvariantCulture);
        }

        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, start);
        return new IPAddress(bytes) + "/" + prefix.ToString(CultureInfo.InvariantCulture);
    }
}
