using System.Globalization;
using System.Text;
using FormaWire.Editing;
using FormaWire.Geometry;

namespace FormaWire.Web;

/// <summary>Serialises the document into the JSON the canvas script draws and snaps on.</summary>
public static class Scene
{
    static readonly string[] Aci = { "#ffffff", "#ff4d4d", "#ffd84d", "#5df25d", "#4de0e0", "#6d8cff", "#ff6dff", "#d0d0d0", "#8a8a8a", "#c0c0c0" };
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <param name="reliefLabels">Label every XY edge with its number and relief (while the second-profile step is open).</param>
    public static string Build(EditorDocument doc, bool reliefLabels)
    {
        var sb = new StringBuilder();
        sb.Append("{\"entities\":[");
        bool first = true;
        var upper = doc.Profiles.SelectMany(p => p.Upper).ToHashSet(); var lower = doc.Profiles.SelectMany(p => p.Lower).ToHashSet();
        var active = doc.Upper.Concat(doc.Lower).ToHashSet();
        var visible = doc.Visible.ToList();
        foreach (var e in visible)
        {
            if (!first) sb.Append(','); first = false;
            string role = upper.Contains(e.Id) ? "xy" : lower.Contains(e.Id) ? "uv" : "";
            sb.Append("{\"id\":").Append(e.Id).Append(",\"z\":").Append(N(e.Z)).Append(",\"layer\":\"").Append(Esc(e.Layer)).Append("\",\"color\":\"").Append(ColorOf(e.Layer))
              .Append("\",\"sel\":").Append(doc.Selection.Contains(e.Id) ? "true" : "false").Append(",\"active\":").Append(active.Contains(e.Id) ? "true" : "false").Append(",\"role\":\"").Append(role).Append('"');
            // what the snaps need: kind, mid point, arc centre and radius
            sb.Append(",\"k\":\"").Append(e.Element switch { LineSeg => "L", ArcSeg => "A", _ => "P" }).Append('"');
            if (!Curves.IsFullTurn(e.Element)) { sb.Append(",\"mid\":"); P(sb, e.Element.PointAt(0.5)); }
            if (e.Element is ArcSeg a) { sb.Append(",\"c\":"); P(sb, a.Center); sb.Append(",\"r\":").Append(N(a.Radius)); }
            sb.Append(",\"pts\":"); Pts(sb, Sample(e.Element));
            sb.Append('}');
        }
        sb.Append("],\"preview\":[");
        first = true;
        foreach (var profile in doc.Profiles)
        if (profile.Deformed is { } d)
            foreach (var (prof, color, z) in new[] { (d.Upper, "#ff3b3b", 0.0), (d.Lower, "#3b7bff", -profile.Depth) })
                foreach (var el in prof.Elements)
                {
                    if (!first) sb.Append(','); first = false;
                    sb.Append("{\"color\":\"").Append(color).Append("\",\"z\":").Append(N(z)).Append(",\"pts\":"); Pts(sb, Sample(el)); sb.Append('}');
                }
        sb.Append("],\"pairs\":[");
        first = true;
        foreach (var profile in doc.Profiles)
        {
            if (profile.Upper.Count > 0 && profile.Lower.Count == profile.Upper.Count)
                for (int i = 0; i < profile.Upper.Count; i++)
                {
                    var a = doc.Get(profile.Upper[i])!; var b = doc.Get(profile.Lower[i])!;
                    if (!first) sb.Append(','); first = false;
                    sb.Append("{\"color\":\"#6c7a89\",\"za\":0,\"zb\":").Append(N(-profile.Depth)).Append(",\"a\":"); Pts(sb, a.Element.Sample(12).ToList());
                    sb.Append(",\"b\":"); Pts(sb, b.Element.Sample(12).ToList()); sb.Append('}');
                }
            if (profile.Deformed is { } dd)
                for (int i = 0; i < dd.Upper.Count; i++)
                {
                    if (!first) sb.Append(','); first = false;
                    sb.Append("{\"color\":\"#a04040\",\"za\":0,\"zb\":").Append(N(-profile.Depth)).Append(",\"a\":"); Pts(sb, dd.Upper.Elements[i].Sample(12).ToList());
                    sb.Append(",\"b\":"); Pts(sb, dd.Lower.Elements[i].Sample(12).ToList()); sb.Append('}');
                }
        }
        sb.Append("],\"snaps\":[");
        first = true;
        foreach (var p in Curves.AllIntersections(visible.Select(e => e.Element).ToList()))
        {
            if (!first) sb.Append(','); first = false;
            P(sb, p);
        }
        sb.Append("],\"labels\":[");
        if (reliefLabels && !doc.HiddenLayers.Contains(doc.ActiveProfile.XyLayer))
        {
            first = true;
            foreach (var r in doc.Relief)
            {
                if (doc.Get(r.Id) is not { } ent) continue;
                if (!first) sb.Append(','); first = false;
                var (text, color) = Label(r, doc.UvMode);
                sb.Append("{\"p\":"); P(sb, ent.Element.PointAt(0.5));
                sb.Append(",\"id\":").Append(r.Id).Append(",\"t\":\"").Append(Esc(text)).Append("\",\"c\":\"").Append(color).Append("\"}");
            }
        }
        sb.Append("],\"depth\":").Append(N(doc.Depth)).Append('}');
        return sb.ToString();
    }

    /// <summary>Short relief text of an XY edge: "5 ← 1°", "4 frontale", "6 ↓ 2°", "8 ⊥ 1°", "7 R".</summary>
    public static (string Text, string Color) Label(ReliefRow r, UvSource mode)
    {
        if (r.Auto.Kind == EdgeKind.Fillet && r.Effective.Kind == EdgeKind.Fillet) return ($"{r.Index} R", "#9aa");
        string front = r.Auto.Kind == EdgeKind.Front ? "#ffd166" : "#7bed9f";
        if (mode == UvSource.Drawn)
        {
            var m = r.Measured;
            if (m?.Note is { } note) return ($"{r.Index} {note}", "#ffb347");
            if (m?.In(r.Effective.Dir) is not { } a) return ($"{r.Index} ?", "#9aa");
            if (Math.Abs(a) < 5e-4) return ($"{r.Index} {(r.Auto.Kind == EdgeKind.Front ? "frontale" : "0°")}", r.Auto.Kind == EdgeKind.Front ? front : "#9aa");
            return ($"{r.Index} {Arrow(r.Effective with { AngleDeg = a })} {Math.Abs(a).ToString("0.##", Inv)}°", front);
        }
        var e = r.Effective;
        string mark = r.Override is null ? "" : "*";
        if (e.AngleDeg == 0) return ($"{r.Index} {(e.Kind == EdgeKind.Front ? "frontale" : "0°")}{mark}", e.Kind == EdgeKind.Front ? front : "#9aa");
        return ($"{r.Index} {Arrow(e)} {Math.Abs(e.AngleDeg).ToString("0.##", Inv)}°{mark}", front);
    }

    /// <summary>Which way the UV edge moves: → ← horizontally, ↑ ↓ vertically, ⊥ square to the edge.</summary>
    public static string Arrow(EdgeRelief e)
    {
        if (e.Dir == ReliefDir.Normal) return e.AngleDeg >= 0 ? "⊥" : "⊥−";
        var s = e.Shift(1);
        return e.Dir == ReliefDir.Horizontal ? (s.X > 0 ? "→" : s.X < 0 ? "←" : "·") : (s.Y > 0 ? "↑" : s.Y < 0 ? "↓" : "·");
    }

    static List<Vec2> Sample(Element e)
    {
        if (e is LineSeg) return new List<Vec2> { e.Start, e.End };
        if (e is PolylineSeg p) return p.Points.ToList();
        int n = e switch { ArcSeg a => Math.Clamp((int)(Math.Abs(a.Sweep) * 180 / Math.PI / 3), 8, 180), EllipseArcSeg el => Math.Clamp((int)(Math.Abs(el.Sweep) * 180 / Math.PI / 3), 8, 180), _ => 32 };
        return e.Sample(n).ToList();
    }

    static string N(double v) => v.ToString("0.######", Inv);
    static void P(StringBuilder sb, Vec2 p) => sb.Append('[').Append(N(p.X)).Append(',').Append(N(p.Y)).Append(']');
    static void Pts(StringBuilder sb, List<Vec2> pts)
    {
        sb.Append('[');
        for (int i = 0; i < pts.Count; i++) { if (i > 0) sb.Append(','); P(sb, pts[i]); }
        sb.Append(']');
    }

    public static string ColorOf(string layer)
    {
        if (layer.Equals("XY", StringComparison.OrdinalIgnoreCase) || layer.StartsWith("XY_", StringComparison.OrdinalIgnoreCase)) return "#ff9f43";
        if (layer.Equals("UV", StringComparison.OrdinalIgnoreCase) || layer.StartsWith("UV_", StringComparison.OrdinalIgnoreCase)) return "#48dbfb";
        if (int.TryParse(layer, out int n)) return Aci[Math.Abs(n) % Aci.Length];
        return Aci[(Math.Abs(layer.GetHashCode()) % (Aci.Length - 1)) + 1];
    }

    static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
