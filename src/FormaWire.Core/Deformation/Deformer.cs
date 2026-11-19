using FormaWire.Geometry;

namespace FormaWire.Deformation;

public sealed class DeformOptions
{
    /// <summary>Max deviation allowed before a line pair is treated as a curved section (mm).</summary>
    public double LineTolerance { get; init; } = 1e-3;
    /// <summary>Max residual of an exact-ellipse representation (mm).</summary>
    public double EllipseTolerance { get; init; } = 1e-7;
    /// <summary>Chord tolerance for polyline fallbacks (mm).</summary>
    public double ChordTolerance { get; init; } = 5e-4;
}

public sealed class DeformResult
{
    public required ProfilePair Source { get; init; }
    public required Mounting Mounting { get; init; }
    public required Profile Upper { get; init; }
    public required Profile Lower { get; init; }
    public List<string> Warnings { get; } = new();

    /// <summary>Derived angles, for display.</summary>
    public double ScaleY => Mounting.ScaleY;
    public double EffectiveFrontClearanceDeg => Mounting.RotB + Mounting.DRot;
}

/// <summary>
/// Reproduces (exactly, validated to &lt;1 µm against machine programs) the PAC
/// NUOVA-BARRETTAxPLACCHETTA-RUOTATA-deltaANG pipeline, but keeps curves exact:
/// line pairs map to lines, arc pairs map to true elliptical arcs.
/// </summary>
public sealed class Deformer
{
    private readonly Mounting _m;
    private readonly double _h, _c, _s, _scy, _shift, _iy;
    private readonly DeformOptions _opt;

    public Deformer(Mounting mounting, double depth, DeformOptions? options = null)
    {
        _m = mounting; _h = depth; _opt = options ?? new DeformOptions();
        double a = Mounting.Deg(mounting.RotP + mounting.RotB);
        _c = Math.Cos(a); _s = Math.Sin(a);
        _scy = mounting.ScaleY;
        _shift = -depth * Math.Tan(a) + depth * Math.Tan(Mounting.Deg(mounting.DRot));
        _iy = mounting.Iy;
    }

    // ---- the validated point pipeline -------------------------------------------------

    /// <summary>
    /// Section of the (pre-compressed, tilted) ruling through pt (Z=0) and pb (Z=-h)
    /// with the horizontal plane Z = zw, in machine XY.
    /// </summary>
    private Vec2 Section(Vec2 pt, Vec2 pb, double zw)
    {
        double x0 = pt.X, y0 = (pt.Y - _iy) * _c;
        double x1 = pb.X, y1 = (pb.Y - _iy) * _c;
        double t = (zw - y0 * _s) / ((y1 - y0) * _s - _h * _c);
        double X = x0 + (x1 - x0) * t;
        double Y = y0 + (y1 - y0) * t;
        double Z = -_h * t;
        return new Vec2(X, Y * _c - Z * _s);
    }

    public Vec2 TopPoint(Vec2 pt, Vec2 pb) { var p = Section(pt, pb, 0); return new Vec2(p.X, p.Y * _scy + _iy); }
    public Vec2 BottomPoint(Vec2 pt, Vec2 pb) { var p = Section(pt, pb, -_h); return new Vec2(p.X, (p.Y + _shift) * _scy + _iy); }

    // ---- element mapping -------------------------------------------------------------

    public DeformResult Deform(ProfilePair pair)
    {
        var upper = new List<Element>(); var lower = new List<Element>();
        var warnings = new List<string>();
        for (int i = 0; i < pair.Count; i++)
        {
            var eu = pair.Upper.Elements[i]; var el = pair.Lower.Elements[i];
            string tag = $"#{i + 1} {eu.Tag ?? eu.GetType().Name}/{el.Tag ?? el.GetType().Name}";
            upper.Add(MapElement(eu, el, true, tag, warnings));
            lower.Add(MapElement(eu, el, false, tag, warnings));
        }
        var res = new DeformResult
        {
            Source = pair, Mounting = _m,
            Upper = new Profile(upper, "XY"), Lower = new Profile(lower, "UV"),
        };
        res.Warnings.AddRange(warnings);
        return res;
    }

    private Vec2 Map(Element eu, Element el, double t, bool top)
        => top ? TopPoint(eu.PointAt(t), el.PointAt(t)) : BottomPoint(eu.PointAt(t), el.PointAt(t));

    private Element MapElement(Element eu, Element el, bool top, string tag, List<string> warnings)
    {
        string srcTag = top ? eu.Tag ?? tag : el.Tag ?? tag;

        if (eu is LineSeg && el is LineSeg)
        {
            var p0 = Map(eu, el, 0, top); var p1 = Map(eu, el, 1, top); var pm = Map(eu, el, 0.5, top);
            double dev = Profile.DistanceToPolyline(pm, new[] { p0, p1 });
            if (dev <= _opt.LineTolerance) return new LineSeg(p0, p1, srcTag);
            warnings.Add($"{tag}: coppia di linee non complanari, la sezione è curva (scarto {dev:0.000000} mm) — approssimata con una polilinea");
            return Chordize(eu, el, top, srcTag);
        }

        if (eu is ArcSeg au && el is ArcSeg al)
        {
            if (Math.Abs(Math.Abs(au.Sweep) - Math.Abs(al.Sweep)) < 1e-9 && Math.Sign(au.Sweep) == Math.Sign(al.Sweep))
            {
                var ell = MapArcPair(au, al, top, srcTag, out string? why);
                if (ell is not null) return ell;
                warnings.Add($"{tag}: {why} — approssimata con una polilinea");
            }
            else warnings.Add($"{tag}: archi di ampiezza diversa non danno una superficie quadrica — approssimata con una polilinea");
            return Chordize(eu, el, top, srcTag);
        }

        warnings.Add($"{tag}: coppia {eu.GetType().Name}/{el.GetType().Name} — approssimata con una polilinea");
        return Chordize(eu, el, top, srcTag);
    }

    /// <summary>
    /// The ruled patch between two circular arcs with equal sweep is a quadric cone, so its
    /// horizontal section is an exact conic. Sample the whole cone (full turn) for a perfectly
    /// conditioned fit, then cut the arc out with the mapped end points.
    /// </summary>
    private Element? MapArcPair(ArcSeg au, ArcSeg al, bool top, string tag, out string? why)
    {
        why = null;
        double fullTurn = 2 * Math.PI / Math.Abs(au.Sweep); // t range covering the whole circle
        const int N = 72;
        var samples = new List<Vec2>(N);
        for (int i = 0; i < N; i++) samples.Add(Map(au, al, fullTurn * i / N, top));

        Conic conic;
        try { conic = Conic.Fit(samples); }
        catch (Exception ex) { why = "calcolo della conica fallito: " + ex.Message; return null; }
        if (!conic.IsEllipse) { why = "la sezione non è un'ellisse"; return null; }

        double worst = samples.Max(conic.SampsonDistance);
        if (worst > _opt.EllipseTolerance) { why = $"scarto dell'ellisse {worst:E2} mm"; return null; }

        (Vec2 c, double a, double b, double rot) = conic.ToEllipse();
        var p0 = Map(au, al, 0, top); var p1 = Map(au, al, 1, top); var pm = Map(au, al, 0.5, top);

        if (b / a > 1 - 1e-10)
        {
            // genuinely circular (no tilt): keep it an arc
            double a0 = (p0 - c).Angle, a1 = (p1 - c).Angle, am = (pm - c).Angle;
            double sw = ArcSeg.NormalizeSweepCcw(a1 - a0);
            if (!CcwContains(a0, sw, am)) sw -= 2 * Math.PI;
            return new ArcSeg(c, (a + b) / 2, a0, sw, tag);
        }

        var proto = new EllipseArcSeg(c, a, b, rot, 0, 1);
        double f0 = proto.ParamOf(p0), f1 = proto.ParamOf(p1), fm = proto.ParamOf(pm);
        double sweep = ArcSeg.NormalizeSweepCcw(f1 - f0);
        if (!CcwContains(f0, sweep, fm)) sweep -= 2 * Math.PI;
        var ell = new EllipseArcSeg(c, a, b, rot, f0, sweep, tag);

        // sanity: representation must reproduce the mapped points
        double chk = Math.Max(ell.Start.DistanceTo(p0), Math.Max(ell.End.DistanceTo(p1), ell.PointAt(0.5).DistanceTo(pm)));
        if (chk > 1e-6) { why = $"parametrizzazione dell'ellisse non corrisponde ({chk:E2} mm)"; return null; }
        return ell;
    }

    private static bool CcwContains(double start, double sweepCcw, double angle)
    {
        double d = ArcSeg.NormalizeSweepCcw(angle - start);
        return d <= sweepCcw + 1e-9;
    }

    private Element Chordize(Element eu, Element el, bool top, string tag)
    {
        var pts = new List<Vec2> { Map(eu, el, 0, top) };
        Subdivide(eu, el, top, 0, 1, pts, 0);
        return new PolylineSeg(pts, tag);
    }

    private void Subdivide(Element eu, Element el, bool top, double t0, double t1, List<Vec2> pts, int depth)
    {
        var a = Map(eu, el, t0, top); var b = Map(eu, el, t1, top);
        double tm = (t0 + t1) / 2; var m = Map(eu, el, tm, top);
        double dev = Profile.DistanceToPolyline(m, new[] { a, b });
        if (dev > _opt.ChordTolerance && depth < 16)
        {
            Subdivide(eu, el, top, t0, tm, pts, depth + 1);
            Subdivide(eu, el, top, tm, t1, pts, depth + 1);
        }
        else pts.Add(b);
    }
}
