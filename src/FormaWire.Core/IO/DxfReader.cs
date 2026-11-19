using System.Globalization;
using FormaWire.Geometry;

namespace FormaWire.IO;

/// <summary>One drawing entity converted to profile elements (a polyline yields several).</summary>
public sealed record DxfEntity(string Type, string Layer, string Handle, double Z, IReadOnlyList<Element> Elements);

public sealed class DxfDrawing
{
    public List<DxfEntity> Entities { get; } = new();
    public List<string> Warnings { get; } = new();
    /// <summary>999 comment lines (ignored by CAD programs; FormaWire keeps its parameters there).</summary>
    public List<string> Comments { get; } = new();
    public IEnumerable<string> Layers => Entities.Select(e => e.Layer).Distinct(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<Element> ElementsOnLayer(string layer) =>
        Entities.Where(e => string.Equals(e.Layer, layer, StringComparison.OrdinalIgnoreCase)).SelectMany(e => e.Elements);

    public double? ElevationOfLayer(string layer)
    {
        var zs = Entities.Where(e => string.Equals(e.Layer, layer, StringComparison.OrdinalIgnoreCase)).Select(e => e.Z).ToList();
        return zs.Count == 0 ? null : zs.Average();
    }
}

/// <summary>Dependency-free DXF reader for 2D profile geometry (LINE, ARC, CIRCLE, LWPOLYLINE, POLYLINE, ELLIPSE).</summary>
public static class DxfReader
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static DxfDrawing Read(string path) => Parse(File.ReadAllLines(path, System.Text.Encoding.Latin1));

    public static DxfDrawing Parse(string[] lines)
    {
        var dwg = new DxfDrawing();
        var tags = new List<(int code, string value)>();
        for (int i = 0; i + 1 < lines.Length; i += 2)
        {
            if (int.TryParse(lines[i].Trim(), NumberStyles.Integer, Inv, out int code))
            {
                if (code == 999) dwg.Comments.Add(lines[i + 1].Trim());
                else tags.Add((code, lines[i + 1].Trim()));
            }
        }

        // locate ENTITIES section
        int start = -1, end = tags.Count;
        for (int i = 0; i < tags.Count - 1; i++)
            if (tags[i].code == 0 && tags[i].value == "SECTION" && tags[i + 1].code == 2 && tags[i + 1].value == "ENTITIES") { start = i + 2; break; }
        if (start < 0) { dwg.Warnings.Add("Sezione ENTITIES non trovata"); return dwg; }
        for (int i = start; i < tags.Count; i++)
            if (tags[i].code == 0 && tags[i].value == "ENDSEC") { end = i; break; }

        // split into entity groups
        var groups = new List<List<(int code, string value)>>();
        for (int i = start; i < end; i++)
        {
            if (tags[i].code == 0) groups.Add(new List<(int, string)>());
            if (groups.Count > 0) groups[^1].Add(tags[i]);
        }

        for (int gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            string type = g[0].value;
            try
            {
                switch (type)
                {
                    case "LINE": dwg.Entities.Add(ReadLine(g)); break;
                    case "ARC": dwg.Entities.Add(ReadArc(g)); break;
                    case "CIRCLE": dwg.Entities.Add(ReadCircle(g)); break;
                    case "LWPOLYLINE": dwg.Entities.Add(ReadLwPolyline(g)); break;
                    case "ELLIPSE": dwg.Entities.Add(ReadEllipse(g)); break;
                    case "POLYLINE":
                    {
                        var verts = new List<List<(int, string)>>();
                        while (gi + 1 < groups.Count && groups[gi + 1][0].value == "VERTEX") verts.Add(groups[++gi]);
                        if (gi + 1 < groups.Count && groups[gi + 1][0].value == "SEQEND") gi++;
                        dwg.Entities.Add(ReadPolyline(g, verts));
                        break;
                    }
                    case "SPLINE":
                        dwg.Warnings.Add($"SPLINE sul layer '{Str(g, 8, "0")}' ignorata (non supportata: convertila in archi/linee)");
                        break;
                    case "POINT": case "TEXT": case "MTEXT": case "DIMENSION": case "INSERT": case "HATCH": case "SOLID": case "LEADER": case "ATTDEF":
                        break;
                    default:
                        dwg.Warnings.Add($"{type} ignorato");
                        break;
                }
            }
            catch (Exception ex) { dwg.Warnings.Add($"{type} saltato: {ex.Message}"); }
        }
        return dwg;
    }

    static string Str(List<(int code, string value)> g, int code, string dflt) { foreach (var t in g) if (t.code == code) return t.value; return dflt; }
    static double Num(List<(int code, string value)> g, int code, double dflt = 0)
    { foreach (var t in g) if (t.code == code) return double.Parse(t.value, NumberStyles.Float, Inv); return dflt; }

    static DxfEntity ReadLine(List<(int code, string value)> g)
    {
        var p0 = new Vec2(Num(g, 10), Num(g, 20)); var p1 = new Vec2(Num(g, 11), Num(g, 21));
        string h = Str(g, 5, "");
        return new DxfEntity("LINE", Str(g, 8, "0"), h, (Num(g, 30) + Num(g, 31)) / 2, new[] { new LineSeg(p0, p1, "L" + h) });
    }

    static DxfEntity ReadArc(List<(int code, string value)> g)
    {
        var c = new Vec2(Num(g, 10), Num(g, 20)); double r = Num(g, 40);
        double a0 = Num(g, 50) * Math.PI / 180, a1 = Num(g, 51) * Math.PI / 180;
        double sweep = ArcSeg.NormalizeSweepCcw(a1 - a0);
        if (sweep == 0) sweep = 2 * Math.PI;
        if (Num(g, 230, 1) < 0) { c = new Vec2(-c.X, c.Y); a0 = Math.PI - a0; sweep = -sweep; } // mirrored OCS
        string h = Str(g, 5, "");
        return new DxfEntity("ARC", Str(g, 8, "0"), h, Num(g, 30), new[] { new ArcSeg(c, r, a0, sweep, "A" + h) });
    }

    static DxfEntity ReadCircle(List<(int code, string value)> g)
    {
        var c = new Vec2(Num(g, 10), Num(g, 20)); double r = Num(g, 40);
        string h = Str(g, 5, "");
        return new DxfEntity("CIRCLE", Str(g, 8, "0"), h, Num(g, 30), new[] { new ArcSeg(c, r, 0, 2 * Math.PI, "C" + h) });
    }

    static DxfEntity ReadEllipse(List<(int code, string value)> g)
    {
        var c = new Vec2(Num(g, 10), Num(g, 20));
        var maj = new Vec2(Num(g, 11), Num(g, 21));
        double a = maj.Length, ratio = Num(g, 40, 1), b = a * ratio;
        double s = Num(g, 41, 0), e = Num(g, 42, 2 * Math.PI);
        double sweep = ArcSeg.NormalizeSweepCcw(e - s); if (sweep == 0) sweep = 2 * Math.PI;
        // centre and major axis are in WCS; with the normal on −Z the minor axis is −perp(major), i.e. the parameter runs backwards
        if (Num(g, 230, 1) < 0) { s = -s; sweep = -sweep; }
        string h = Str(g, 5, "");
        return new DxfEntity("ELLIPSE", Str(g, 8, "0"), h, Num(g, 30), new[] { new EllipseArcSeg(c, a, b, maj.Angle, s, sweep, "E" + h) });
    }

    static DxfEntity ReadLwPolyline(List<(int code, string value)> g)
    {
        var pts = new List<Vec2>(); var bulges = new List<double>();
        double? x = null;
        foreach (var (code, value) in g)
        {
            if (code == 10) { x = double.Parse(value, NumberStyles.Float, Inv); }
            else if (code == 20 && x is not null) { pts.Add(new Vec2(x.Value, double.Parse(value, NumberStyles.Float, Inv))); bulges.Add(0); x = null; }
            else if (code == 42 && bulges.Count > 0) bulges[^1] = double.Parse(value, NumberStyles.Float, Inv);
        }
        bool closed = ((int)Num(g, 70, 0) & 1) == 1;
        string h = Str(g, 5, "");
        return new DxfEntity("LWPOLYLINE", Str(g, 8, "0"), h, Num(g, 38), PolyToElements(pts, bulges, closed, "P" + h));
    }

    static DxfEntity ReadPolyline(List<(int code, string value)> g, List<List<(int code, string value)>> verts)
    {
        var pts = new List<Vec2>(); var bulges = new List<double>();
        foreach (var v in verts) { pts.Add(new Vec2(Num(v, 10), Num(v, 20))); bulges.Add(Num(v, 42)); }
        bool closed = ((int)Num(g, 70, 0) & 1) == 1;
        string h = Str(g, 5, "");
        return new DxfEntity("POLYLINE", Str(g, 8, "0"), h, Num(g, 30), PolyToElements(pts, bulges, closed, "P" + h));
    }

    static List<Element> PolyToElements(List<Vec2> pts, List<double> bulges, bool closed, string tag)
    {
        var els = new List<Element>();
        int n = pts.Count; if (n < 2) return els;
        int segs = closed ? n : n - 1;
        for (int i = 0; i < segs; i++)
        {
            var p0 = pts[i]; var p1 = pts[(i + 1) % n]; double b = bulges[i];
            if (p0.AlmostEquals(p1, 1e-9)) continue;
            string t = $"{tag}.{i + 1}";
            if (Math.Abs(b) < 1e-12) { els.Add(new LineSeg(p0, p1, t)); continue; }
            double theta = 4 * Math.Atan(b);               // signed sweep
            double d = p0.DistanceTo(p1);
            double r = d / (2 * Math.Abs(Math.Sin(theta / 2)));
            var mid = (p0 + p1) * 0.5;
            var nLeft = (p1 - p0).Perp() / d;
            var c = mid + nLeft * (Math.Sign(b) * r * Math.Cos(Math.Abs(theta) / 2));
            els.Add(new ArcSeg(c, r, (p0 - c).Angle, theta, t));
        }
        return els;
    }
}

/// <summary>Orders loose elements into a connected chain.</summary>
public static class ProfileChainer
{
    public static Profile Chain(IEnumerable<Element> elements, double tol, string? name, List<string>? warnings = null)
    {
        var els = elements.ToList();
        if (els.Count == 0) throw new InvalidOperationException($"Nessun elemento per il profilo '{name}'");
        if (els.Count == 1) return new Profile(els, name);

        // free endpoints = endpoints with no partner
        var ends = new List<(int idx, bool isStart, Vec2 p)>();
        for (int i = 0; i < els.Count; i++) { ends.Add((i, true, els[i].Start)); ends.Add((i, false, els[i].End)); }
        var free = ends.Where(e => !ends.Any(o => o.idx != e.idx && o.p.AlmostEquals(e.p, tol))).ToList();

        int startIdx; bool reverseFirst;
        if (free.Count == 0) { startIdx = 0; reverseFirst = false; }
        else
        {
            if (free.Count > 2)
                throw new InvalidOperationException($"Il profilo '{name}' non è una catena unica: {free.Count} estremi liberi (tolleranza {tol}). Estremi liberi: {string.Join(", ", free.Select(f => f.p))}");
            // start at the free end with the smaller X (deterministic), oriented away from it
            var f = free.OrderBy(e => e.p.X).ThenBy(e => e.p.Y).First();
            startIdx = f.idx; reverseFirst = !f.isStart;
        }

        var used = new bool[els.Count];
        var chain = new List<Element> { reverseFirst ? els[startIdx].Reversed() : els[startIdx] };
        used[startIdx] = true;
        while (chain.Count < els.Count)
        {
            var cur = chain[^1].End;
            int best = -1; bool rev = false; double bestD = double.MaxValue; int candidates = 0;
            for (int i = 0; i < els.Count; i++)
            {
                if (used[i]) continue;
                double ds = els[i].Start.DistanceTo(cur), de = els[i].End.DistanceTo(cur);
                if (ds <= tol || de <= tol) candidates++;
                if (ds < bestD) { bestD = ds; best = i; rev = false; }
                if (de < bestD) { bestD = de; best = i; rev = true; }
            }
            if (best < 0 || bestD > tol)
                throw new InvalidOperationException($"Profilo '{name}': la catena si interrompe in {cur} (l'elemento libero più vicino è a {bestD:0.######} mm, tolleranza {tol})");
            if (candidates > 1) warnings?.Add($"Profilo '{name}': diramazione in {cur}, {candidates} elementi possibili — preso il più vicino");
            chain.Add(rev ? els[best].Reversed() : els[best]);
            used[best] = true;
        }
        return new Profile(chain, name);
    }

    /// <summary>Orient and align the lower chain to the upper one (same direction, matching first element).</summary>
    public static Profile AlignLowerToUpper(Profile upper, Profile lower)
    {
        bool closed = upper.IsClosed(1e-6) && lower.IsClosed(1e-6);
        if (!closed)
        {
            if (lower.End.DistanceTo(upper.Start) < lower.Start.DistanceTo(upper.Start)) lower = lower.Reversed();
            return lower;
        }
        // closed: pick the direction and rotation whose element starts best match
        Profile best = lower; double bestScore = double.MaxValue;
        foreach (var cand in new[] { lower, lower.Reversed() })
        {
            for (int rot = 0; rot < cand.Count; rot++)
            {
                var rotated = cand.Elements.Skip(rot).Concat(cand.Elements.Take(rot)).ToList();
                double score = 0;
                for (int i = 0; i < Math.Min(rotated.Count, upper.Count); i++) score += rotated[i].Start.DistanceTo(upper.Elements[i].Start);
                if (score < bestScore) { bestScore = score; best = new Profile(rotated, lower.Name); }
            }
        }
        return best;
    }
}
