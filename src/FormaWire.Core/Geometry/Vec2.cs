namespace FormaWire.Geometry;

/// <summary>Immutable 2D vector / point in millimetres.</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static readonly Vec2 Zero = new(0, 0);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double k) => new(a.X * k, a.Y * k);
    public static Vec2 operator *(double k, Vec2 a) => new(a.X * k, a.Y * k);
    public static Vec2 operator /(Vec2 a, double k) => new(a.X / k, a.Y / k);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public double LengthSquared => X * X + Y * Y;
    public double Dot(Vec2 o) => X * o.X + Y * o.Y;
    /// <summary>2D cross product (z component).</summary>
    public double Cross(Vec2 o) => X * o.Y - Y * o.X;
    public Vec2 Normalized() { var l = Length; return l > 0 ? this / l : this; }
    public Vec2 Perp() => new(-Y, X);
    public double DistanceTo(Vec2 o) => (this - o).Length;
    public double Angle => Math.Atan2(Y, X);

    public static Vec2 FromPolar(double r, double angle) => new(r * Math.Cos(angle), r * Math.Sin(angle));

    public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => a + (b - a) * t;

    public bool AlmostEquals(Vec2 o, double tol = 1e-6) => Math.Abs(X - o.X) <= tol && Math.Abs(Y - o.Y) <= tol;

    public override string ToString() => $"({X:0.######}, {Y:0.######})";
}
