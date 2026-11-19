namespace FormaWire.Geometry;

/// <summary>
/// Curve queries behind the CAD tools (trim, extend, break, fillet, snaps): parameters, nearest
/// points, sub-curves and intersections. Parameters are the element's own t (0 = Start, 1 = End);
/// outside [0,1] they describe the extension of a line or the rest of an arc's circle.
/// </summary>
public static class Curves
{
    const double Eps = 1e-9;

    // ------------------------------------------------------------------ parameters

    /// <summary>
    /// Parameter of a point assumed to lie on the element (or its extension). Lines: projection, unbounded.
    /// Arcs: in [0, 2π/|sweep|), i.e. going on in the arc direction past the end.
    /// </summary>
    public static double ParamOf(Element e, Vec2 p) => e switch
    {
        LineSeg l => (p - l.P0).Dot(l.P1 - l.P0) / Math.Max((l.P1 - l.P0).LengthSquared, 1e-300),
        ArcSeg a => ArcParam(a.StartAngle, a.Sweep, (p - a.Center).Angle),
        EllipseArcSeg el => ArcParam(el.StartParam, el.Sweep, el.ParamOf(p)),
        _ => NearestBySampling(e, p),
    };

    static double ArcParam(double start, double sweep, double angle)
    {
        double d = sweep >= 0 ? ArcSeg.NormalizeSweepCcw(angle - start) : ArcSeg.NormalizeSweepCcw(start - angle);
        return d / Math.Abs(sweep);
    }

    /// <summary>Length of one full turn in parameter units (2π/|sweep|), or +∞ for elements that do not wrap.</summary>
    public static double Period(Element e) => e switch
    {
        ArcSeg a => 2 * Math.PI / Math.Abs(a.Sweep),
        EllipseArcSeg el => 2 * Math.PI / Math.Abs(el.Sweep),
        _ => double.PositiveInfinity,
    };

    public static bool IsFullTurn(Element e) => Math.Abs(Period(e) - 1) < 1e-9;

    /// <summary>Parameter in [0,1] of the point of the element nearest to p.</summary>
    public static double Nearest(Element e, Vec2 p)
    {
        switch (e)
        {
            case LineSeg: return Math.Clamp(ParamOf(e, p), 0, 1);
            case ArcSeg or EllipseArcSeg:
            {
                double t = e is ArcSeg ? ParamOf(e, p) : NearestBySampling(e, p);
                if (t <= 1) return t;
                return p.DistanceTo(e.Start) <= p.DistanceTo(e.End) ? 0 : 1;
            }
            default: return NearestBySampling(e, p);
        }
    }

    public static Vec2 NearestPoint(Element e, Vec2 p) => e.PointAt(Nearest(e, p));
    public static double Distance(Element e, Vec2 p) => NearestPoint(e, p).DistanceTo(p);

    static double NearestBySampling(Element e, Vec2 p)
    {
        const int N = 256;
        int best = 0; double bd = double.MaxValue;
        for (int i = 0; i <= N; i++) { double d = e.PointAt((double)i / N).DistanceTo(p); if (d < bd) { bd = d; best = i; } }
        double lo = Math.Max(0, (best - 1.0) / N), hi = Math.Min(1, (best + 1.0) / N);
        for (int k = 0; k < 60; k++)
        {
            double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
            if (e.PointAt(m1).DistanceTo(p) < e.PointAt(m2).DistanceTo(p)) hi = m2; else lo = m1;
        }
        return (lo + hi) / 2;
    }

    // ------------------------------------------------------------------ sub-curves

    /// <summary>
    /// The part of the element between t0 and t1, same direction and tag. Lines and arcs may be
    /// extended (t outside [0,1]); polylines are clamped.
    /// </summary>
    public static Element Sub(Element e, double t0, double t1) => e switch
    {
        LineSeg l => new LineSeg(l.PointAt(t0), l.PointAt(t1), l.Tag),
        ArcSeg a => new ArcSeg(a.Center, a.Radius, a.StartAngle + a.Sweep * t0, a.Sweep * (t1 - t0), a.Tag),
        EllipseArcSeg el => new EllipseArcSeg(el.Center, el.SemiMajor, el.SemiMinor, el.Rotation, el.StartParam + el.Sweep * t0, el.Sweep * (t1 - t0), el.Tag),
        PolylineSeg pl => SubPolyline(pl, Math.Clamp(t0, 0, 1), Math.Clamp(t1, 0, 1)),
        _ => throw new NotSupportedException(e.GetType().Name),
    };

    static PolylineSeg SubPolyline(PolylineSeg pl, double t0, double t1)
    {
        double total = pl.ApproxLength, s0 = t0 * total, s1 = t1 * total, cum = 0;
        var pts = new List<Vec2> { pl.PointAt(t0) };
        for (int i = 1; i < pl.Points.Count; i++)
        {
            cum += pl.Points[i].DistanceTo(pl.Points[i - 1]);
            if (cum > s0 + Eps && cum < s1 - Eps) pts.Add(pl.Points[i]);
        }
        pts.Add(pl.PointAt(t1));
        return new PolylineSeg(pts, pl.Tag);
    }

    public static (Element, Element) Split(Element e, double t) => (Sub(e, 0, t), Sub(e, t, 1));

    // ------------------------------------------------------------------ transforms

    public static Vec2 Rotate(Vec2 p, Vec2 c, double rad)
    {
        double cs = Math.Cos(rad), sn = Math.Sin(rad); var d = p - c;
        return c + new Vec2(d.X * cs - d.Y * sn, d.X * sn + d.Y * cs);
    }

    public static Element Rotate(Element e, Vec2 c, double rad) => e switch
    {
        LineSeg l => new LineSeg(Rotate(l.P0, c, rad), Rotate(l.P1, c, rad), l.Tag),
        ArcSeg a => new ArcSeg(Rotate(a.Center, c, rad), a.Radius, a.StartAngle + rad, a.Sweep, a.Tag),
        EllipseArcSeg el => new EllipseArcSeg(Rotate(el.Center, c, rad), el.SemiMajor, el.SemiMinor, el.Rotation + rad, el.StartParam, el.Sweep, el.Tag),
        PolylineSeg pl => new PolylineSeg(pl.Points.Select(q => Rotate(q, c, rad)).ToArray(), pl.Tag),
        _ => throw new NotSupportedException(e.GetType().Name),
    };

    /// <summary>Reflection of a point in the line through a and b.</summary>
    public static Vec2 Mirror(Vec2 p, Vec2 a, Vec2 b)
    {
        var u = (b - a).Normalized(); var d = p - a;
        return a + u * (2 * d.Dot(u)) - d;
    }

    /// <summary>
    /// Reflection in the line through a and b. The element still runs from (mirrored) start to (mirrored) end,
    /// so arcs and ellipses turn the other way.
    /// </summary>
    public static Element Mirror(Element e, Vec2 a, Vec2 b)
    {
        double phi = (b - a).Angle;
        return e switch
        {
            LineSeg l => new LineSeg(Mirror(l.P0, a, b), Mirror(l.P1, a, b), l.Tag),
            ArcSeg c => new ArcSeg(Mirror(c.Center, a, b), c.Radius, 2 * phi - c.StartAngle, -c.Sweep, c.Tag),
            // u → u' and v → −v': the point at φ lands at −φ of the mirrored ellipse
            EllipseArcSeg el => new EllipseArcSeg(Mirror(el.Center, a, b), el.SemiMajor, el.SemiMinor, 2 * phi - el.Rotation, -el.StartParam, -el.Sweep, el.Tag),
            PolylineSeg pl => new PolylineSeg(pl.Points.Select(q => Mirror(q, a, b)).ToArray(), pl.Tag),
            _ => throw new NotSupportedException(e.GetType().Name),
        };
    }

    /// <summary>Uniform scaling about c (k &gt; 0).</summary>
    public static Element Scale(Element e, Vec2 c, double k) => e switch
    {
        LineSeg l => new LineSeg(c + (l.P0 - c) * k, c + (l.P1 - c) * k, l.Tag),
        ArcSeg a => new ArcSeg(c + (a.Center - c) * k, a.Radius * k, a.StartAngle, a.Sweep, a.Tag),
        EllipseArcSeg el => new EllipseArcSeg(c + (el.Center - c) * k, el.SemiMajor * k, el.SemiMinor * k, el.Rotation, el.StartParam, el.Sweep, el.Tag),
        PolylineSeg pl => new PolylineSeg(pl.Points.Select(q => c + (q - c) * k).ToArray(), pl.Tag),
        _ => throw new NotSupportedException(e.GetType().Name),
    };

    /// <summary>
    /// Parallel copy of a line or arc at distance d to the left of its direction (negative: to the right).
    /// Arcs keep their centre; null when an arc would shrink to nothing.
    /// </summary>
    public static Element? Offset(Element e, double d)
    {
        switch (e)
        {
            case LineSeg l:
                var n = (l.P1 - l.P0).Normalized().Perp() * d;
                return new LineSeg(l.P0 + n, l.P1 + n, l.Tag);
            case ArcSeg a:
                // left of a counter-clockwise arc is its centre
                double r = a.Radius - Math.Sign(a.Sweep) * d;
                return r > 1e-9 ? new ArcSeg(a.Center, r, a.StartAngle, a.Sweep, a.Tag) : null;
            default:
                throw new NotSupportedException("L'offset funziona su linee e archi");
        }
    }

    /// <summary>Line with one end point replaced, keeping its tag.</summary>
    public static LineSeg WithEnd(LineSeg l, bool start, Vec2 p) => start ? new LineSeg(p, l.P1, l.Tag) : new LineSeg(l.P0, p, l.Tag);

    // ------------------------------------------------------------------ intersections

    /// <summary>
    /// Intersection points of two elements. With <paramref name="unboundedA"/> the first element is
    /// taken as its infinite line / full circle (for Extend).
    /// </summary>
    public static List<Vec2> Intersect(Element a, Element b, bool unboundedA = false)
    {
        var res = new List<Vec2>();
        if (a is PolylineSeg || a is EllipseArcSeg)
        {
            // approximate curves: intersect their chords with b (bounded only)
            var pa = Chords(a);
            for (int i = 1; i < pa.Count; i++) res.AddRange(Intersect(new LineSeg(pa[i - 1], pa[i]), b));
            return Dedup(res);
        }
        if (b is PolylineSeg || b is EllipseArcSeg)
        {
            var pb = Chords(b);
            for (int i = 1; i < pb.Count; i++) res.AddRange(Intersect(a, new LineSeg(pb[i - 1], pb[i]), unboundedA));
            return Dedup(res);
        }
        foreach (var p in Raw(a, b))
            if (OnElement(a, p, unboundedA) && OnElement(b, p, false)) res.Add(p);
        return Dedup(res);
    }

    /// <summary>Where the infinite lines / full circles of two lines or arcs cross.</summary>
    public static List<Vec2> IntersectUnbounded(Element a, Element b) => Dedup(Raw(a, b).ToList());

    static IEnumerable<Vec2> Raw(Element a, Element b)
    {
        switch (a, b)
        {
            case (LineSeg l1, LineSeg l2):
            {
                var d1 = l1.P1 - l1.P0; var d2 = l2.P1 - l2.P0;
                double cr = d1.Cross(d2);
                if (Math.Abs(cr) < 1e-12 * d1.Length * d2.Length) yield break;
                double t = (l2.P0 - l1.P0).Cross(d2) / cr;
                yield return l1.P0 + d1 * t;
                break;
            }
            case (LineSeg l, ArcSeg c): foreach (var p in LineCircle(l.P0, l.P1, c.Center, c.Radius)) yield return p; break;
            case (ArcSeg c, LineSeg l): foreach (var p in LineCircle(l.P0, l.P1, c.Center, c.Radius)) yield return p; break;
            case (ArcSeg c1, ArcSeg c2): foreach (var p in CircleCircle(c1.Center, c1.Radius, c2.Center, c2.Radius)) yield return p; break;
        }
    }

    static IEnumerable<Vec2> LineCircle(Vec2 p0, Vec2 p1, Vec2 c, double r)
    {
        var d = p1 - p0; double a = d.LengthSquared; if (a < 1e-300) yield break;
        var f = p0 - c;
        double b = 2 * f.Dot(d), cc = f.LengthSquared - r * r;
        double disc = b * b - 4 * a * cc;
        double tol = 8 * a * r * 1e-9; // disc ≈ 8·a·r·δ for a line δ inside the tangent: treat δ < 1 nm as tangent
        if (disc < -tol) yield break;
        if (disc <= tol) { yield return p0 + d * (-b / (2 * a)); yield break; }
        double sq = Math.Sqrt(disc);
        yield return p0 + d * ((-b - sq) / (2 * a));
        yield return p0 + d * ((-b + sq) / (2 * a));
    }

    static IEnumerable<Vec2> CircleCircle(Vec2 c1, double r1, Vec2 c2, double r2)
    {
        var d = c2 - c1; double dist = d.Length;
        if (dist < 1e-12) yield break;                       // concentric
        if (dist > r1 + r2 + 1e-9 || dist < Math.Abs(r1 - r2) - 1e-9) yield break;
        double a = (r1 * r1 - r2 * r2 + dist * dist) / (2 * dist);
        double h2 = r1 * r1 - a * a;
        var m = c1 + d * (a / dist);
        if (h2 <= 1e-18) { yield return m; yield break; }
        var n = d.Perp() * (Math.Sqrt(h2) / dist);
        yield return m + n; yield return m - n;
    }

    static bool OnElement(Element e, Vec2 p, bool unbounded)
    {
        if (unbounded) return true;
        double t = ParamOf(e, p);
        if (e is LineSeg l) { double tol = 1e-9 / Math.Max(l.ApproxLength, 1e-12); return t >= -tol && t <= 1 + tol; }
        double tolA = 1e-9 / Math.Max(e.ApproxLength, 1e-12);
        return t <= 1 + tolA || t >= Period(e) - tolA;
    }

    static List<Vec2> Chords(Element e)
    {
        if (e is PolylineSeg pl) return pl.Points.ToList();
        int n = Math.Clamp((int)(e.ApproxLength * 20), 64, 720);
        return e.Sample(n).ToList();
    }

    static List<Vec2> Dedup(List<Vec2> pts)
    {
        var res = new List<Vec2>();
        foreach (var p in pts) if (!res.Any(q => q.AlmostEquals(p, 1e-9))) res.Add(p);
        return res;
    }

    /// <summary>Exact bounding box of a line or arc (an arc counts the points where it crosses the axes directions); ellipses densely sampled.</summary>
    public static (Vec2 Min, Vec2 Max) Extent(Element e)
    {
        IEnumerable<Vec2> pts = e switch
        {
            LineSeg => new[] { e.Start, e.End },
            ArcSeg a => new[] { a.Start, a.End }.Concat(Enumerable.Range(0, 4).Select(k => k * Math.PI / 2)
                .Where(t => IsFullTurn(a) || ArcParam(a.StartAngle, a.Sweep, t) <= 1).Select(t => a.Center + Vec2.FromPolar(a.Radius, t))),
            PolylineSeg pl => pl.Points,
            _ => e.Sample(720),
        };
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var p in pts) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
        return (new Vec2(x0, y0), new Vec2(x1, y1));
    }

    /// <summary>Axis-aligned bounds (arcs sampled), for fast rejection.</summary>
    public static (Vec2 Min, Vec2 Max) Bounds(Element e)
    {
        IEnumerable<Vec2> pts = e is LineSeg ? new[] { e.Start, e.End } : e is PolylineSeg pl ? pl.Points : e.Sample(48);
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var p in pts) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
        double pad = e is LineSeg ? 1e-9 : e.ApproxLength * 0.01 + 1e-9; // sampled curves can bulge between samples
        return (new Vec2(x0 - pad, y0 - pad), new Vec2(x1 + pad, y1 + pad));
    }

    /// <summary>
    /// All pairwise intersection points of a set of elements (sweep on X bounds), excluding points
    /// where two elements merely share an end point.
    /// </summary>
    public static List<Vec2> AllIntersections(IReadOnlyList<Element> els, int maxElements = 5000)
    {
        var res = new List<Vec2>();
        if (els.Count > maxElements) return res;
        var items = els.Select(e => (e, b: Bounds(e))).OrderBy(x => x.b.Min.X).ToList();
        for (int i = 0; i < items.Count; i++)
            for (int j = i + 1; j < items.Count && items[j].b.Min.X <= items[i].b.Max.X; j++)
            {
                var (a, ba) = items[i]; var (b, bb) = items[j];
                if (bb.Min.Y > ba.Max.Y || bb.Max.Y < ba.Min.Y) continue;
                foreach (var p in Intersect(a, b))
                {
                    bool sharedEnd = (p.AlmostEquals(a.Start, 1e-7) || p.AlmostEquals(a.End, 1e-7)) && (p.AlmostEquals(b.Start, 1e-7) || p.AlmostEquals(b.End, 1e-7));
                    if (!sharedEnd) res.Add(p);
                }
            }
        return res;
    }
}
