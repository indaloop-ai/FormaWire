namespace FormaWire.Geometry;

/// <summary>Role of an XY edge when the UV profile is built from relief angles.</summary>
public enum EdgeKind
{
    /// <summary>Front (radial) edge: no relief of its own by default; its ends slide along it to meet the relieved neighbours.</summary>
    Front,
    /// <summary>Side (lateral) edge: gets the side relief.</summary>
    Flank,
    /// <summary>Corner radius between two lines: re-fitted between the moved lines (same radius and sweep where possible).</summary>
    Fillet,
}

/// <summary>Direction an edge is relieved in.</summary>
public enum ReliefDir
{
    /// <summary>Along X (0°), whatever the slope of the edge — the drafting office's side relief.</summary>
    Horizontal,
    /// <summary>Along Y (90°), whatever the slope of the edge.</summary>
    Vertical,
    /// <summary>Square to the edge: a true offset of the line (concentric for an arc).</summary>
    Normal,
}

/// <summary>
/// How one XY edge is relieved: depth·tan(angle) in direction <see cref="Dir"/>, towards the material
/// (<see cref="Inward"/> = unit normal into the material); a negative angle goes the other way.
/// </summary>
public readonly record struct EdgeRelief(EdgeKind Kind, ReliefDir Dir, double AngleDeg, Vec2 Inward)
{
    public double Distance(double depth) => depth * Math.Tan(Mounting.Deg(AngleDeg));

    /// <summary>Translation of a line (or of a curve moved rigidly).</summary>
    public Vec2 Shift(double depth)
    {
        if (Kind == EdgeKind.Fillet) return Vec2.Zero;
        double d = Distance(depth);
        return Dir switch
        {
            ReliefDir.Horizontal => new Vec2(Math.Sign(Inward.X) * d, 0),
            ReliefDir.Vertical => new Vec2(0, Math.Sign(Inward.Y) * d),
            _ => Inward * d,
        };
    }
}

/// <summary>
/// Relief read back from a drawn XY/UV edge pair, in each direction (degrees, positive = into the
/// material; null where it is not defined, e.g. a horizontal shift of a horizontal line).
/// </summary>
public readonly record struct EdgeMeasure(EdgeKind Kind, double? H, double? V, double? N, string? Note)
{
    public double? In(ReliefDir dir) => dir switch { ReliefDir.Horizontal => H, ReliefDir.Vertical => V, _ => N };
}

/// <summary>
/// The second (UV) profile from relief angles instead of index ranges:
///  * every edge gets a role automatically — lines within <see cref="FrontMaxDeg"/> of horizontal are front
///    edges, arcs tangent to two lines are fillets, everything else is a flank (material = below an open
///    profile, inside a closed one);
///  * flanks get the side relief, horizontally by default (the drafting office's convention: depth·tan(angle)
///    in X whatever the slope), or vertically, or square to the edge; front lines keep their line unless
///    given a relief, and their ends slide to the moved neighbours; fillets are re-fitted between their lines.
/// The front correction (8° mounting → 6° clearance = −2°) moves the whole profile in Y; the caller adds it.
/// </summary>
public static class ReliefPlanner
{
    /// <summary>
    /// Lines closer than this to horizontal are front edges: on them a side relief in X gives almost no
    /// clearance (tan(side)·sin(slope)) while the front correction does, and as front edges they absorb the
    /// relief of their neighbours without twisting.
    /// </summary>
    public const double FrontMaxDeg = 20;

    /// <summary>+1 when the material is on the right of the chain direction, −1 on the left. Open profiles: below (the tip is on top); closed: inside.</summary>
    public static int MaterialSide(Profile p, bool flip = false)
    {
        int m = p.IsClosed() ? (SignedArea(p) > 0 ? -1 : 1) : (p.End.X >= p.Start.X ? 1 : -1);
        return flip ? -m : m;
    }

    static double SignedArea(Profile p)
    {
        var pts = p.Sample(16); double a = 0;
        for (int i = 1; i < pts.Count; i++) a += pts[i - 1].Cross(pts[i]);
        return a / 2;
    }

    /// <summary>Unit normal pointing into the material at parameter t.</summary>
    public static Vec2 InwardNormal(Element e, double t, int material)
    {
        var tg = ProfileOps.Tangent(e, t);
        return new Vec2(tg.Y, -tg.X) * material;
    }

    /// <summary>The automatic relief of every edge.</summary>
    /// <param name="offset">Relieve square to the edges (true offset) instead of horizontally / vertically.</param>
    public static List<EdgeRelief> Auto(Profile p, double sideDeg, int material, bool offset = false)
        => Enumerable.Range(0, p.Count).Select(i => AutoEdge(p, i, sideDeg, material, offset)).ToList();

    public static EdgeRelief AutoEdge(Profile p, int i, double sideDeg, int material, bool offset = false)
    {
        var e = p.Elements[i];
        var n = InwardNormal(e, 0.5, material);
        if (e is ArcSeg && IsFillet(p, i)) return new EdgeRelief(EdgeKind.Fillet, offset ? ReliefDir.Normal : ReliefDir.Horizontal, 0, n);
        double frontLimit = e is LineSeg ? Math.Sin(Mounting.Deg(FrontMaxDeg)) : 1e-9;
        if (Math.Abs(n.X) < frontLimit) return new EdgeRelief(EdgeKind.Front, offset ? ReliefDir.Normal : ReliefDir.Vertical, 0, n);
        return new EdgeRelief(EdgeKind.Flank, offset ? ReliefDir.Normal : ReliefDir.Horizontal, sideDeg, n);
    }

    /// <summary>An arc tangent to a line at both ends.</summary>
    public static bool IsFillet(Profile p, int i)
    {
        int n = p.Count;
        if (!p.IsClosed() && (i == 0 || i == n - 1)) return false;
        var prev = p.Elements[(i - 1 + n) % n]; var arc = p.Elements[i]; var next = p.Elements[(i + 1) % n];
        if (arc is not ArcSeg || prev is not LineSeg || next is not LineSeg) return false;
        return Tangent(ProfileOps.Tangent(prev, 1), ProfileOps.Tangent(arc, 0)) && Tangent(ProfileOps.Tangent(arc, 1), ProfileOps.Tangent(next, 0));
        static bool Tangent(Vec2 a, Vec2 b) => a.Dot(b) > 0 && Math.Abs(a.Cross(b)) < 1e-3;
    }

    /// <summary>
    /// Build the UV profile (relief only, no front correction). Validated: reproduces the hand-drawn lower
    /// profiles of BA1408 (automatic roles) and IN0932 (four edges set by hand) to &lt;1 µm.
    /// </summary>
    public static Profile Generate(Profile upper, double depth, IReadOnlyList<EdgeRelief> edges, List<string>? warnings = null)
    {
        int n = upper.Count;
        if (edges.Count != n) throw new ArgumentException($"{edges.Count} impostazioni per {n} elementi");
        if (depth <= 0) throw new InvalidOperationException("La profondità UV deve essere positiva");
        bool closed = upper.IsClosed();
        var els = upper.Elements;
        var s = edges.Select(r => r.Shift(depth)).ToArray();

        // curves are rigid: move, offset or re-fit them first, lines are then joined to them
        var curves = new Element?[n];
        for (int i = 0; i < n; i++)
        {
            if (els[i] is LineSeg) continue;
            int ip = (i - 1 + n) % n, inx = (i + 1) % n;
            if (edges[i].Kind == EdgeKind.Fillet && els[i] is ArcSeg arc && els[ip] is LineSeg lp && els[inx] is LineSeg ln)
            {
                var (d, dr) = Refit(arc, lp, s[ip], ln, s[inx], edges[i].Dir == ReliefDir.Normal);
                curves[i] = Resized(arc, d, dr, i, warnings);
            }
            else if (edges[i].Dir == ReliefDir.Normal && els[i] is ArcSeg oa)
            {
                // offset of an arc = concentric; the radius shrinks when the material is towards the centre
                double dist = edges[i].Distance(depth);
                bool centreInMaterial = edges[i].Inward.Dot(oa.Center - oa.PointAt(0.5)) > 0;
                curves[i] = Resized(oa, Vec2.Zero, centreInMaterial ? -dist : dist, i, warnings);
            }
            else curves[i] = ProfileOps.Translate(els[i], s[i]);
        }

        int nv = closed ? n : n + 1;
        var V = new Vec2[nv];
        for (int k = 0; k < nv; k++)
        {
            if (!closed && k == 0) { V[0] = curves[0]?.Start ?? els[0].Start + s[0]; continue; }
            if (!closed && k == n) { V[n] = curves[n - 1]?.End ?? els[n - 1].End + s[n - 1]; continue; }
            V[k] = Joint((k - 1 + n) % n, k % n);
        }

        var lower = new List<Element>(n);
        for (int i = 0; i < n; i++)
        {
            string tag = (els[i].Tag ?? $"E{i + 1}") + "'";
            if (curves[i] is { } c) { lower.Add(Retag(c, tag)); continue; }
            var l = (LineSeg)els[i];
            Vec2 p0 = V[i], p1 = V[(i + 1) % nv], dir = l.P1 - l.P0, nd = p1 - p0;
            if (nd.Dot(dir) <= 1e-12)
                throw new InvalidOperationException($"L'elemento {i + 1} ({l.ApproxLength:0.###} mm) è troppo corto per la spoglia dei vicini: in UV si sovrappongono. Dai meno spoglia a un elemento vicino (0 = nessuna).");
            if (nd.Length < 0.25 * dir.Length) warnings?.Add($"l'elemento {i + 1} in UV si accorcia da {dir.Length:0.###} a {nd.Length:0.###} mm");
            double twist = Math.Max(DistToLine(p0, l.P0 + s[i], dir), DistToLine(p1, l.P0 + s[i], dir));
            if (twist > 1e-5) warnings?.Add($"elemento {i + 1}: i due estremi si spostano in modo diverso ({twist * 1000:0.##} µm fuori dalla sua retta) — la faccia sarà svergolata, non piana");
            lower.Add(new LineSeg(p0, p1, tag));
        }
        return new Profile(lower, (upper.Name ?? "XY") + " lower");

        // the vertex between element a and the next one b; each line is anchored on its own end (drawings have µm gaps)
        Vec2 Joint(int a, int b)
        {
            if (curves[a] is { } ca && curves[b] is { } cb)
            {
                if (!ca.End.AlmostEquals(cb.Start, 1e-7))
                    throw new InvalidOperationException($"Gli elementi {a + 1} e {b + 1} sono entrambi curvi e si spostano in modo diverso: metti un tratto dritto tra loro o dai loro la stessa spoglia.");
                return ca.End;
            }
            if (curves[a] is { } c1) return c1.End;
            if (curves[b] is { } c2) return c2.Start;
            if (s[a].AlmostEquals(s[b], 1e-12)) return (els[a].End + els[b].Start) / 2 + s[a];
            Vec2 da = els[a].End - els[a].Start, db = els[b].End - els[b].Start;
            Vec2 pa = els[a].End + s[a], pb = els[b].Start + s[b];
            double cr = da.Normalized().Cross(db.Normalized());
            if (Math.Abs(cr) < Math.Sin(Mounting.Deg(1)))
            {
                // in line: fine if both moved onto the same line, otherwise there is nothing to join them
                if (Math.Abs((pb - pa).Cross(da.Normalized())) < 1e-9) return (pa + pb) / 2;
                throw new InvalidOperationException($"Gli elementi {a + 1} e {b + 1} sono quasi allineati ma con spoglie diverse: dai loro la stessa spoglia, oppure 0 a uno dei due.");
            }
            return pa + da * ((pb - pa).Cross(db) / da.Cross(db));
        }
    }

    static ArcSeg Resized(ArcSeg arc, Vec2 d, double dr, int i, List<string>? warnings)
    {
        if (arc.Radius + dr <= 1e-6) throw new InvalidOperationException($"Elemento {i + 1}: R{arc.Radius:0.###} è più piccolo della spoglia");
        if (Math.Abs(dr) > 1e-12) warnings?.Add($"elemento {i + 1}: R{arc.Radius:0.###} diventa R{arc.Radius + dr:0.####} in UV");
        return new ArcSeg(arc.Center + d, arc.Radius + dr, arc.StartAngle, arc.Sweep, arc.Tag);
    }

    /// <summary>
    /// Move of a fillet so it stays tangent to its two moved lines: n·Δ − dr = n·s for each line
    /// (n = unit normal towards the centre). The radius is kept (dr = 0, the drafter's way) unless the lines
    /// are parallel (a half round between two flanks) or <paramref name="concentric"/> asks for a true offset
    /// and both lines moved by the same amount — then the centre stays and the radius changes (an exact cone).
    /// </summary>
    static (Vec2 d, double dr) Refit(ArcSeg arc, LineSeg prev, Vec2 sp, LineSeg next, Vec2 sn, bool concentric)
    {
        var np = Toward(prev, arc.Center); var nn = Toward(next, arc.Center);
        double bp = np.Dot(sp), bn = nn.Dot(sn), det = np.Cross(nn);
        if (concentric && Math.Abs(bp - bn) < 1e-12) return (Vec2.Zero, -bp);
        if (Math.Abs(det) > 1e-6) return (new Vec2((bp * nn.Y - np.Y * bn) / det, (np.X * bn - bp * nn.X) / det), 0);
        var t = np.Perp();
        return (np * ((bp - bn) / 2) + t * ((t.Dot(sp) + t.Dot(sn)) / 2), -(bp + bn) / 2);

        static Vec2 Toward(LineSeg l, Vec2 c) { var n = (l.P1 - l.P0).Perp().Normalized(); return n.Dot(c - l.P0) >= 0 ? n : -n; }
    }

    static double DistToLine(Vec2 p, Vec2 a, Vec2 dir) => Math.Abs((p - a).Cross(dir.Normalized()));

    static Element Retag(Element e, string tag) => e switch
    {
        LineSeg l => new LineSeg(l.P0, l.P1, tag),
        ArcSeg a => new ArcSeg(a.Center, a.Radius, a.StartAngle, a.Sweep, tag),
        EllipseArcSeg el => new EllipseArcSeg(el.Center, el.SemiMajor, el.SemiMinor, el.Rotation, el.StartParam, el.Sweep, tag),
        PolylineSeg p => new PolylineSeg(p.Points, tag),
        _ => e,
    };

    // ------------------------------------------------------------------ reading angles back from two profiles

    /// <summary>
    /// The relief every XY/UV pair actually has (take the front correction out of <paramref name="lower"/>
    /// first): for a line, how far the UV line lies from the XY line horizontally, vertically and square to it,
    /// over the depth; for an arc, its centre move (H/V) or its radius change (N).
    /// </summary>
    public static List<EdgeMeasure> Measure(Profile upper, Profile lower, double depth, int material)
    {
        var res = new List<EdgeMeasure>(upper.Count);
        for (int i = 0; i < upper.Count; i++)
        {
            var kind = AutoEdge(upper, i, 0, material).Kind;
            if (i >= lower.Count) { res.Add(new(kind, null, null, null, "manca")); continue; }
            var n = InwardNormal(upper.Elements[i], 0.5, material);
            switch (upper.Elements[i], lower.Elements[i])
            {
                case (LineSeg a, LineSeg b):
                {
                    var da = (a.P1 - a.P0).Normalized(); var db = (b.P1 - b.P0).Normalized();
                    if (Math.Abs(da.Cross(db)) > 1e-6) { res.Add(new(kind, null, null, null, "svergolato")); break; }
                    var m = (b.P0 + b.P1) / 2;
                    double? h = Math.Abs(da.Y) < 1e-9 ? null : Deg((m.X - (a.P0.X + (m.Y - a.P0.Y) * da.X / da.Y)) * Math.Sign(n.X));
                    double? v = Math.Abs(da.X) < 1e-9 ? null : Deg((m.Y - (a.P0.Y + (m.X - a.P0.X) * da.Y / da.X)) * Math.Sign(n.Y));
                    res.Add(new(kind, h, v, Deg((m - a.P0).Dot(n)), null));
                    break;
                }
                case (ArcSeg a, ArcSeg b):
                {
                    if (Math.Abs(Math.Abs(a.Sweep) - Math.Abs(b.Sweep)) > 1e-6) { res.Add(new(kind, null, null, null, "ampiezza diversa")); break; }
                    var dc = b.Center - a.Center; double dr = b.Radius - a.Radius;
                    bool centreInMaterial = n.Dot(a.Center - a.PointAt(0.5)) > 0;
                    if (dc.Length < 1e-9) res.Add(new(kind, null, null, Deg(centreInMaterial ? -dr : dr), null));
                    else if (Math.Abs(dr) < 1e-9) res.Add(new(kind, Math.Abs(n.X) < 1e-9 ? null : Deg(dc.X * Math.Sign(n.X)), Math.Abs(n.Y) < 1e-9 ? null : Deg(dc.Y * Math.Sign(n.Y)), null, null));
                    else res.Add(new(kind, null, null, null, $"spostato, R {dr:+0.###;-0.###}"));
                    break;
                }
                default: res.Add(new(kind, null, null, null, null)); break;
            }
        }
        return res;
        double Deg(double d) => Math.Atan(d / depth) * 180 / Math.PI;
    }

    /// <summary>
    /// The Y offset the whole lower profile carries — how the drafting office draws the front correction
    /// (depth·tan 2° for 8° mounting → 6°). Median over the ends of the front lines (all vertices if there are
    /// none), so a few vertices moved by the side relief do not disturb it.
    /// </summary>
    public static double CommonYOffset(Profile upper, Profile lower, int material)
    {
        if (upper.Count == 0 || upper.Count != lower.Count) return 0;
        var front = new List<double>(); var all = new List<double>();
        for (int i = 0; i < upper.Count; i++)
        {
            double d0 = lower.Elements[i].Start.Y - upper.Elements[i].Start.Y, d1 = lower.Elements[i].End.Y - upper.Elements[i].End.Y;
            all.Add(d0); all.Add(d1);
            if (AutoEdge(upper, i, 0, material).Kind == EdgeKind.Front) { front.Add(d0); front.Add(d1); }
        }
        var d = front.Count > 0 ? front : all;
        d.Sort();
        return d[d.Count / 2];
    }

    /// <summary>Y shift of the whole second profile for a front correction (−2° = 8° mounting → 6° clearance: +depth·tan 2°).</summary>
    public static Vec2 FrontShift(double depth, double correctionDeg) => new(0, -depth * Math.Tan(Mounting.Deg(correctionDeg)));

    /// <summary>The front correction a common Y offset stands for.</summary>
    public static double FrontCorrectionOf(double yOffset, double depth) => 0 - Math.Atan(yOffset / depth) * 180 / Math.PI;   // 0 − x: no "−0"
}
