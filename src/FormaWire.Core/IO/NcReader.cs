using System.Globalization;
using System.Text.RegularExpressions;
using FormaWire.Geometry;

namespace FormaWire.IO;

/// <summary>A programmed point: XY plane position plus accumulated UV offset of the other plane.</summary>
public sealed record NcPoint(int Block, Vec2 Xy, Vec2 Uv)
{
    public Vec2 Lower => Xy + Uv;
}

/// <summary>
/// Reads the first taper pass (the first G41/G42 … G40 pass carrying U/V words) of a Mitsubishi
/// wire-EDM program as produced by the PAC post for the MV1200R. U/V are incremental.
/// </summary>
public static class NcReader
{
    static readonly Regex Block = new(@"^N(\d+)(G0?[01])?(G4[012])?X(-?[\d.]+)(?:Y(-?[\d.]+))?(?:U(-?[\d.]+))?(?:V(-?[\d.]+))?", RegexOptions.Compiled);
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static List<NcPoint> ReadFirstTaperPass(string path)
    {
        foreach (var pass in Passes(path))
            if (pass.Any(p => p.hasUv)) return pass.Select(p => p.pt).ToList();
        return new List<NcPoint>();
    }

    private static IEnumerable<List<(NcPoint pt, bool hasUv)>> Passes(string path)
    {
        List<(NcPoint, bool)>? cur = null;
        double y = 0, u = 0, v = 0;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            var m = Block.Match(line);
            if (!m.Success) continue;
            string comp = m.Groups[3].Success ? m.Groups[3].Value : "";
            if (comp is "G41" or "G42") { cur = new List<(NcPoint, bool)>(); u = v = 0; }
            if (cur is null) continue;
            if (comp == "G40") { var done = cur; cur = null; yield return done; continue; }
            double x = double.Parse(m.Groups[4].Value, Inv);
            if (m.Groups[5].Success) y = double.Parse(m.Groups[5].Value, Inv);
            if (m.Groups[6].Success) u += double.Parse(m.Groups[6].Value, Inv);
            if (m.Groups[7].Success) v += double.Parse(m.Groups[7].Value, Inv);
            cur.Add((new NcPoint(int.Parse(m.Groups[1].Value, Inv), new Vec2(x, y), new Vec2(u, v)), m.Groups[6].Success));
        }
        if (cur is { Count: > 0 }) yield return cur;
    }

    public static double? MaxTaperDeg(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var m = Regex.Match(line, @"MAX\.CONICITA gradi ([\d.]+)");
            if (m.Success) return double.Parse(m.Groups[1].Value, Inv);
        }
        return null;
    }
}
