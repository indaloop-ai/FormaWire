namespace FormaWire.Geometry;

/// <summary>
/// One element of a profile chain. Every element is parametrised by t in [0,1]
/// running from <see cref="Start"/> to <see cref="End"/> in the chain direction.
/// </summary>
public abstract class Element
{
    /// <summary>Optional label (e.g. the PAE entity name "L3" / "C-1", or a DXF handle).</summary>
    public string? Tag { get; init; }

    public abstract Vec2 Start { get; }
    public abstract Vec2 End { get; }
    public abstract Vec2 PointAt(double t);
    public abstract Element Reversed();
    public abstract double ApproxLength { get; }

    /// <summary>n+1 points from t=0 to t=1.</summary>
    public IEnumerable<Vec2> Sample(int n)
    {
        for (int i = 0; i <= n; i++) yield return PointAt((double)i / n);
    }
}

public sealed class LineSeg : Element
{
    public LineSeg(Vec2 start, Vec2 end, string? tag = null) { P0 = start; P1 = end; Tag = tag; }
    public Vec2 P0 { get; }
    public Vec2 P1 { get; }
    public override Vec2 Start => P0;
    public override Vec2 End => P1;
    public override Vec2 PointAt(double t) => Vec2.Lerp(P0, P1, t);
    public override Element Reversed() => new LineSeg(P1, P0, Tag);
    public override double ApproxLength => P0.DistanceTo(P1);
    public override string ToString() => $"Line {P0} -> {P1}";
}

/// <summary>Circular arc. Sweep is signed: positive = counter-clockwise.</summary>
public sealed class ArcSeg : Element
{
    public ArcSeg(Vec2 center, double radius, double startAngle, double sweep, string? tag = null)
    {
        if (radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        Center = center; Radius = radius; StartAngle = startAngle; Sweep = sweep; Tag = tag;
    }
    public Vec2 Center { get; }
    public double Radius { get; }
    public double StartAngle { get; }
    public double Sweep { get; }
    public double EndAngle => StartAngle + Sweep;
    public bool IsCcw => Sweep > 0;

    public override Vec2 Start => Center + Vec2.FromPolar(Radius, StartAngle);
    public override Vec2 End => Center + Vec2.FromPolar(Radius, EndAngle);
    public override Vec2 PointAt(double t) => Center + Vec2.FromPolar(Radius, StartAngle + Sweep * t);
    public override Element Reversed() => new ArcSeg(Center, Radius, EndAngle, -Sweep, Tag);
    public override double ApproxLength => Math.Abs(Sweep) * Radius;

    /// <summary>
    /// Build an arc from start, end and radius using the PAC convention observed in the
    /// job files: counter-clockwise from start to end, sweep &lt; 180°.
    /// </summary>
    public static ArcSeg FromStartEndRadiusCcwMinor(Vec2 start, Vec2 end, double radius, string? tag = null)
    {
        var mid = (start + end) * 0.5;
        var d = end - start;
        double len = d.Length;
        if (len > 2 * radius + 1e-9) throw new ArgumentException($"Corda dell'arco {len} più lunga del diametro {2 * radius}");
        double h = Math.Sqrt(Math.Max(radius * radius - len * len / 4, 0));
        var n = d.Perp() / len; // left normal of start->end
        foreach (var c in new[] { mid + n * h, mid - n * h })
        {
            double a0 = (start - c).Angle, a1 = (end - c).Angle;
            double sweep = NormalizeSweepCcw(a1 - a0);
            if (sweep <= Math.PI + 1e-12) return new ArcSeg(c, radius, a0, sweep, tag);
        }
        throw new InvalidOperationException("Nessun arco antiorario minore possibile");
    }

    public static double NormalizeSweepCcw(double sweep)
    {
        sweep %= 2 * Math.PI;
        if (sweep < 0) sweep += 2 * Math.PI;
        return sweep;
    }

    public override string ToString() => $"Arc c={Center} r={Radius:0.####} {StartAngle * 180 / Math.PI:0.##}° sweep {Sweep * 180 / Math.PI:0.##}°";
}

/// <summary>
/// Elliptical arc. Parametric form: C + a·cos(φ)·u + b·sin(φ)·v, u = (cos θ, sin θ), v = (−sin θ, cos θ).
/// Sweep is signed in the parametric angle φ (positive = counter-clockwise).
/// </summary>
public sealed class EllipseArcSeg : Element
{
    public EllipseArcSeg(Vec2 center, double semiMajor, double semiMinor, double rotation, double startParam, double sweep, string? tag = null)
    {
        if (semiMajor <= 0 || semiMinor <= 0 || semiMinor > semiMajor * (1 + 1e-12))
            throw new ArgumentException($"Assi dell'ellisse non validi a={semiMajor} b={semiMinor}");
        Center = center; SemiMajor = semiMajor; SemiMinor = Math.Min(semiMinor, semiMajor); Rotation = rotation;
        StartParam = startParam; Sweep = sweep; Tag = tag;
    }
    public Vec2 Center { get; }
    public double SemiMajor { get; }
    public double SemiMinor { get; }
    /// <summary>Angle of the major axis, radians.</summary>
    public double Rotation { get; }
    public double StartParam { get; }
    public double Sweep { get; }
    public double EndParam => StartParam + Sweep;
    public double Ratio => SemiMinor / SemiMajor;
    public bool IsCcw => Sweep > 0;
    public Vec2 MajorDir => Vec2.FromPolar(1, Rotation);
    public Vec2 MinorDir => MajorDir.Perp();

    public Vec2 PointAtParam(double phi) => Center + MajorDir * (SemiMajor * Math.Cos(phi)) + MinorDir * (SemiMinor * Math.Sin(phi));
    public override Vec2 Start => PointAtParam(StartParam);
    public override Vec2 End => PointAtParam(EndParam);
    public override Vec2 PointAt(double t) => PointAtParam(StartParam + Sweep * t);
    public override Element Reversed() => new EllipseArcSeg(Center, SemiMajor, SemiMinor, Rotation, EndParam, -Sweep, Tag);
    public override double ApproxLength => Math.Abs(Sweep) * (SemiMajor + SemiMinor) / 2;

    /// <summary>Parametric angle φ of a point assumed to lie on the ellipse.</summary>
    public double ParamOf(Vec2 p)
    {
        var d = p - Center;
        double u = d.Dot(MajorDir) / SemiMajor, v = d.Dot(MinorDir) / SemiMinor;
        return Math.Atan2(v, u);
    }

    public override string ToString() => $"EllipseArc c={Center} a={SemiMajor:0.######} b={SemiMinor:0.######} rot={Rotation * 180 / Math.PI:0.###}° φ0={StartParam * 180 / Math.PI:0.##}° sweep {Sweep * 180 / Math.PI:0.##}°";
}

/// <summary>Chordal polyline, used only as a fallback when a section curve is not a representable conic.</summary>
public sealed class PolylineSeg : Element
{
    public PolylineSeg(IReadOnlyList<Vec2> points, string? tag = null)
    {
        if (points.Count < 2) throw new ArgumentException("Una polilinea richiede almeno 2 punti");
        Points = points; Tag = tag;
        _cum = new double[points.Count];
        for (int i = 1; i < points.Count; i++) _cum[i] = _cum[i - 1] + points[i].DistanceTo(points[i - 1]);
    }
    private readonly double[] _cum;
    public IReadOnlyList<Vec2> Points { get; }
    public override Vec2 Start => Points[0];
    public override Vec2 End => Points[^1];
    public override double ApproxLength => _cum[^1];
    public override Vec2 PointAt(double t)
    {
        double s = Math.Clamp(t, 0, 1) * _cum[^1];
        int i = Array.BinarySearch(_cum, s);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, Points.Count - 2);
        double seg = _cum[i + 1] - _cum[i];
        double u = seg > 0 ? (s - _cum[i]) / seg : 0;
        return Vec2.Lerp(Points[i], Points[i + 1], u);
    }
    public override Element Reversed() => new PolylineSeg(Points.Reverse().ToArray(), Tag);
    public override string ToString() => $"Polyline {Points.Count} pts {Start} -> {End}";
}
