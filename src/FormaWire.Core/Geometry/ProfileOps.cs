namespace FormaWire.Geometry;

/// <summary>Editing operations on profiles (the core of the editor; the UI only drives them).</summary>
public static class ProfileOps
{
    /// <summary>
    /// Pick the chain that contains <paramref name="seed"/> out of a loose set of drawing elements
    /// (typically everything on one layer of a full tool drawing). Walks both ways from the seed
    /// through end-point connectivity; stops at a free end or at a branch (more than one candidate),
    /// so dimension lines and other geometry touching the profile do not get swallowed.
    /// </summary>
    public static Profile PickChain(IReadOnlyList<Element> drawing, Element seed, double tol, List<string>? warnings = null)
    {
        var used = new HashSet<Element>(ReferenceEqualityComparer.Instance) { seed };
        var forward = Walk(drawing, seed, seed.End, tol, used, warnings);
        var backward = Walk(drawing, seed, seed.Start, tol, used, warnings);
        var chain = new List<Element>();
        for (int i = backward.Count - 1; i >= 0; i--) chain.Add(backward[i].Reversed());
        chain.Add(seed);
        chain.AddRange(forward);
        return new Profile(chain, "picked");
    }

    private static List<Element> Walk(IReadOnlyList<Element> drawing, Element from, Vec2 cursor, double tol, HashSet<Element> used, List<string>? warnings)
    {
        var result = new List<Element>();
        while (true)
        {
            var cands = new List<(Element e, bool rev)>();
            foreach (var e in drawing)
            {
                if (used.Contains(e)) continue;
                if (e.Start.AlmostEquals(cursor, tol)) cands.Add((e, false));
                else if (e.End.AlmostEquals(cursor, tol)) cands.Add((e, true));
            }
            if (cands.Count == 0) return result;
            if (cands.Count > 1)
            {
                warnings?.Add($"catena fermata in {cursor}: lì si incontrano {cands.Count} elementi (diramazione)");
                return result;
            }
            var (next, rev) = cands[0];
            used.Add(next);
            var oriented = rev ? next.Reversed() : next;
            result.Add(oriented);
            cursor = oriented.End;
            if (cursor.AlmostEquals(from.Start, tol)) return result; // closed loop
        }
    }

    public static Profile Translate(Profile p, Vec2 d) => new(p.Elements.Select(e => Translate(e, d)), p.Name);

    public static Element Translate(Element e, Vec2 d) => e switch
    {
        LineSeg l => new LineSeg(l.P0 + d, l.P1 + d, l.Tag),
        ArcSeg a => new ArcSeg(a.Center + d, a.Radius, a.StartAngle, a.Sweep, a.Tag),
        EllipseArcSeg el => new EllipseArcSeg(el.Center + d, el.SemiMajor, el.SemiMinor, el.Rotation, el.StartParam, el.Sweep, el.Tag),
        PolylineSeg pl => new PolylineSeg(pl.Points.Select(q => q + d).ToArray(), pl.Tag),
        _ => throw new NotSupportedException(e.GetType().Name),
    };

    /// <summary>Move the profile so that <paramref name="point"/> lands on the origin.</summary>
    public static Profile MoveToOrigin(Profile p, Vec2 point) => Translate(p, -point);

    /// <summary>
    /// The usual reference for a PAC job: the cutting tip. Returns the vertex with the largest Y
    /// (ties: smallest X), i.e. where the drafting office puts Y = 0.
    /// </summary>
    public static Vec2 TipVertex(Profile p)
    {
        var verts = p.Elements.Select(e => e.Start).Append(p.End).ToList();
        return verts.OrderByDescending(v => v.Y).ThenBy(v => v.X).First();
    }

    /// <summary>
    /// The drafting-office reference point: X of the leftmost point, Y of the highest point of the
    /// profile (two different vertices in general). A bulging arc counts with its exact top / leftmost point.
    /// </summary>
    public static Vec2 ReferenceCorner(Profile p)
    {
        var (min, max) = Extents(p);
        return new Vec2(min.X, max.Y);
    }

    public static (Vec2 min, Vec2 max) Extents(Profile p)
    {
        var boxes = p.Elements.Select(Curves.Extent).ToList();
        return (new Vec2(boxes.Min(b => b.Min.X), boxes.Min(b => b.Min.Y)), new Vec2(boxes.Max(b => b.Max.X), boxes.Max(b => b.Max.Y)));
    }

    /// <summary>
    /// Add the wire entry/exit lines the way the jobs are drawn: from the first/last vertex a straight
    /// line going outward in X and downward (−Y) at <paramref name="angleDeg"/>, until it is
    /// <paramref name="beyond"/> mm past the profile's X extent (left end → minX − beyond, right end → maxX + beyond).
    /// The exit line can go further (<paramref name="beyondOut"/>; BA1360: in 1, out 2.55).
    /// </summary>
    public static Profile AddLeads(Profile p, double beyond, double angleDeg = 45, bool leadIn = true, bool leadOut = true, double? beyondOut = null)
    {
        if (p.IsClosed()) throw new InvalidOperationException("Non si possono aggiungere entrata/uscita a un profilo chiuso");
        var (min, max) = Extents(p);
        double t = Math.Tan(Mounting.Deg(angleDeg));
        var els = p.Elements.ToList();
        bool startIsLeft = p.Start.X <= p.End.X;
        if (leadIn)
        {
            double targetX = startIsLeft ? min.X - beyond : max.X + beyond;
            double dx = targetX - p.Start.X;
            var q = new Vec2(targetX, p.Start.Y - Math.Abs(dx) * t);
            els.Insert(0, new LineSeg(q, p.Start, "lead-in"));
        }
        if (leadOut)
        {
            double b = beyondOut ?? beyond;
            double targetX = startIsLeft ? max.X + b : min.X - b;
            double dx = targetX - p.End.X;
            var q = new Vec2(targetX, p.End.Y - Math.Abs(dx) * t);
            els.Add(new LineSeg(p.End, q, "lead-out"));
        }
        return new Profile(els, p.Name);
    }

    public static Profile Reverse(Profile p) => p.Reversed();

    /// <summary>
    /// Lengthen the open ends by <paramref name="startLength"/> / <paramref name="endLength"/> along
    /// the end tangents, to run the wire through unfinished material. A straight end element is
    /// extended; a curved one gets a tangent line appended.
    /// </summary>
    public static Profile ExtendEnds(Profile p, double startLength, double endLength)
    {
        if (p.IsClosed()) throw new InvalidOperationException("Non si può allungare un profilo chiuso");
        var els = p.Elements.ToList();
        if (startLength > 0)
        {
            var first = els[0];
            var t = Tangent(first, 0);
            if (first is LineSeg l) els[0] = new LineSeg(l.P0 - t * startLength, l.P1, l.Tag);
            else els.Insert(0, new LineSeg(first.Start - t * startLength, first.Start, "lead-in"));
        }
        if (endLength > 0)
        {
            var last = els[^1];
            var t = Tangent(last, 1);
            if (last is LineSeg l) els[^1] = new LineSeg(l.P0, l.P1 + t * endLength, l.Tag);
            else els.Add(new LineSeg(last.End, last.End + t * endLength, "lead-out"));
        }
        return new Profile(els, p.Name);
    }

    /// <summary>Unit tangent in the chain direction at t=0 or t=1.</summary>
    public static Vec2 Tangent(Element e, double t)
    {
        const double h = 1e-6;
        var a = e.PointAt(Math.Clamp(t - h, 0, 1)); var b = e.PointAt(Math.Clamp(t + h, 0, 1));
        return (b - a).Normalized();
    }

    /// <summary>Elements of a profile with their 1-based index, for listing/selection.</summary>
    public static IEnumerable<(int index, Element element)> Indexed(Profile p) => p.Elements.Select((e, i) => (i + 1, e));
}
