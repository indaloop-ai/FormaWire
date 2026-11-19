namespace FormaWire.Geometry;

/// <summary>An ordered, connected chain of elements (open or closed).</summary>
public sealed class Profile
{
    public Profile(IEnumerable<Element> elements, string? name = null)
    {
        Elements = elements.ToList();
        Name = name;
    }

    public string? Name { get; init; }
    public List<Element> Elements { get; }
    public int Count => Elements.Count;
    public Vec2 Start => Elements[0].Start;
    public Vec2 End => Elements[^1].End;
    public bool IsClosed(double tol = 1e-6) => Count > 0 && Start.AlmostEquals(End, tol);

    /// <summary>Largest gap between consecutive elements.</summary>
    public double MaxGap()
    {
        double g = 0;
        for (int i = 1; i < Count; i++) g = Math.Max(g, Elements[i - 1].End.DistanceTo(Elements[i].Start));
        return g;
    }

    public Profile Reversed() => new(Elements.AsEnumerable().Reverse().Select(e => e.Reversed()), Name);

    /// <summary>Dense polyline for distance checks / plotting.</summary>
    public List<Vec2> Sample(int perElement = 64)
    {
        var pts = new List<Vec2>();
        foreach (var e in Elements)
        {
            int n = e is LineSeg ? 1 : perElement;
            foreach (var p in e.Sample(n)) pts.Add(p);
        }
        return pts;
    }

    /// <summary>Distance from a point to the sampled profile.</summary>
    public static double DistanceToPolyline(Vec2 p, IReadOnlyList<Vec2> poly)
    {
        double best = double.MaxValue;
        for (int i = 1; i < poly.Count; i++)
        {
            var a = poly[i - 1]; var b = poly[i];
            var v = b - a; double l2 = v.LengthSquared;
            double u = l2 == 0 ? 0 : Math.Clamp((p - a).Dot(v) / l2, 0, 1);
            best = Math.Min(best, p.DistanceTo(a + v * u));
        }
        return best;
    }
}

/// <summary>
/// Upper (XY, Z=0) and lower (UV, Z=-Depth) profiles with element-to-element correspondence,
/// i.e. the two-plane wire-EDM model (PAC "SF").
/// </summary>
public sealed class ProfilePair
{
    public ProfilePair(Profile upper, Profile lower, double depth, string? name = null)
    {
        if (upper.Count != lower.Count)
            throw new ArgumentException($"Il profilo superiore ha {upper.Count} elementi, quello inferiore {lower.Count}: non si possono accoppiare.");
        if (depth <= 0) throw new ArgumentOutOfRangeException(nameof(depth), "La profondità deve essere positiva");
        Upper = upper; Lower = lower; Depth = depth; Name = name;
    }
    public string? Name { get; }
    public Profile Upper { get; }
    public Profile Lower { get; }
    /// <summary>Distance between the XY and UV planes (PAC: |ZUV − ZXY|).</summary>
    public double Depth { get; }
    public int Count => Upper.Count;
}

/// <summary>Mounting of the blank in the wire-EDM machine (angles in degrees).</summary>
public sealed record Mounting(
    double RotP = 0,    // insert rotation in the tool (PAC ROTP)
    double RotB = 8,    // fixed fixture / bar rotation in the machine (PAC ROTB)
    double DRot = 0,    // extra front-clearance correction (PAC DROT)
    double Iy = 0)      // Y of the scaling pivot (PAC IY)
{
    public double TotalTiltDeg => RotP + RotB;
    public double ScaleY => Math.Cos(Deg(RotP + RotB)) / Math.Cos(Deg(RotP));
    public static double Deg(double d) => d * Math.PI / 180;
}
