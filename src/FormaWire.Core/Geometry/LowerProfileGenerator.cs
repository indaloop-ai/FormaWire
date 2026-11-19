namespace FormaWire.Geometry;

/// <summary>A constant shift applied to a run of consecutive elements (1-based, inclusive indices in chain order).</summary>
public sealed record RunShift(int From, int To, Vec2 Shift, string? Label = null)
{
    public static RunShift SideRelief(int from, int to, double depth, double angleDeg)
        => new(from, to, new Vec2(depth * Math.Tan(Mounting.Deg(angleDeg)), 0), $"laterale {angleDeg:0.##}° su {from}-{to}");
}

/// <summary>
/// Builds the lower (UV) profile from the upper one the way the drafting office does it:
/// every vertex of the upper profile is moved by a shift vector, lines follow their vertices,
/// arcs are translated (both ends must carry the same shift).
///
/// Shifts come from:
///  * the target front clearance: a global +Y shift of depth·tan(ROTB − target), which reduces the
///    clearance produced by the fixture tilt (validated on job BA1408: 8° fixture, 6° required → +depth·tan 2°);
///  * side relief / taper runs: ±X shift of depth·tan(angle) on a run of elements.
/// An element whose two vertices carry different shifts becomes a tapered connector (as L16 in
/// IN0932 or L11 in BA1408). A vertex shared by two runs with different shifts is an error.
/// </summary>
public static class LowerProfileGenerator
{
    public static Vec2 FrontClearanceShift(double depth, double fixtureDeg, double targetDeg)
        => new(0, depth * Math.Tan(Mounting.Deg(fixtureDeg - targetDeg)));

    public static Profile Generate(Profile upper, Vec2 globalShift, IReadOnlyList<RunShift> runs, List<string>? warnings = null)
    {
        int n = upper.Count;
        bool closed = upper.IsClosed();
        int nv = closed ? n : n + 1;
        var vertexShift = new Vec2?[nv];
        string?[] owner = new string?[nv];

        foreach (var run in runs)
        {
            if (run.From < 1 || run.To > n || run.From > run.To)
                throw new ArgumentException($"Tratto {run.From}-{run.To} fuori da 1..{n}");
            for (int i = run.From - 1; i <= run.To - 1; i++)
            {
                foreach (int v in new[] { i, (i + 1) % nv })
                {
                    if (vertexShift[v] is { } existing && !existing.AlmostEquals(run.Shift, 1e-9))
                        throw new InvalidOperationException(
                            $"Il vertice {v + 1} (in {upper.Elements[Math.Min(v, n - 1)].Start}) è condiviso da tratti con spostamenti diversi " +
                            $"({owner[v]} e {run.Label ?? run.Shift.ToString()}). Lascia un elemento di raccordo tra i tratti.");
                    vertexShift[v] = run.Shift; owner[v] = run.Label ?? run.Shift.ToString();
                }
            }
        }

        var lower = new List<Element>(n);
        for (int i = 0; i < n; i++)
        {
            var s0 = (vertexShift[i] ?? Vec2.Zero) + globalShift;
            var s1 = (vertexShift[(i + 1) % nv] ?? Vec2.Zero) + globalShift;
            var e = upper.Elements[i];
            string tag = (e.Tag ?? $"E{i + 1}") + "'";
            switch (e)
            {
                case LineSeg l:
                    if (!s0.AlmostEquals(s1, 1e-12))
                    {
                        double bridged = (s1 - s0).Length;
                        warnings?.Add($"elemento {i + 1} ({e.Tag}) fa da raccordo: spostamento {s0} → {s1}" +
                            (bridged > 0.5 * l.ApproxLength ? $"  ← ATTENZIONE: copre {bridged:0.###} mm su un elemento di {l.ApproxLength:0.###} mm, scegli un raccordo più lungo" : ""));
                    }
                    lower.Add(new LineSeg(l.P0 + s0, l.P1 + s1, tag));
                    break;
                case ArcSeg a:
                    if (!s0.AlmostEquals(s1, 1e-12))
                        throw new InvalidOperationException($"L'arco {i + 1} ({e.Tag}) non può fare da raccordo (spostamenti {s0} e {s1}); dividi i tratti su un elemento dritto.");
                    lower.Add(new ArcSeg(a.Center + s0, a.Radius, a.StartAngle, a.Sweep, tag));
                    break;
                case EllipseArcSeg el:
                    if (!s0.AlmostEquals(s1, 1e-12))
                        throw new InvalidOperationException($"L'ellisse {i + 1} non può fare da raccordo");
                    lower.Add(new EllipseArcSeg(el.Center + s0, el.SemiMajor, el.SemiMinor, el.Rotation, el.StartParam, el.Sweep, tag));
                    break;
                default:
                    throw new NotSupportedException($"elemento {i + 1}: {e.GetType().Name}");
            }
        }
        return new Profile(lower, (upper.Name ?? "XY") + " lower");
    }
}
