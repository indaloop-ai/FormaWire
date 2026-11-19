using FormaWire.Geometry;

namespace FormaWire.Deformation;

/// <summary>General conic a·x² + b·xy + c·y² + d·x + e·y + f = 0.</summary>
public readonly record struct Conic(double A, double B, double C, double D, double E, double F)
{
    public double Discriminant => B * B - 4 * A * C;
    public bool IsEllipse => Discriminant < 0;

    public double Evaluate(Vec2 p) => A * p.X * p.X + B * p.X * p.Y + C * p.Y * p.Y + D * p.X + E * p.Y + F;

    /// <summary>Approximate geometric distance of p to the conic (Sampson distance).</summary>
    public double SampsonDistance(Vec2 p)
    {
        double gx = 2 * A * p.X + B * p.Y + D;
        double gy = B * p.X + 2 * C * p.Y + E;
        double g = Math.Sqrt(gx * gx + gy * gy);
        return g > 0 ? Math.Abs(Evaluate(p)) / g : double.MaxValue;
    }

    /// <summary>Centre, semi-axes and rotation for an ellipse. Throws if not an ellipse.</summary>
    public (Vec2 Center, double SemiMajor, double SemiMinor, double Rotation) ToEllipse()
    {
        if (!IsEllipse) throw new InvalidOperationException("La conica non è un'ellisse");
        double det = 4 * A * C - B * B;
        double x0 = (B * E - 2 * C * D) / det;
        double y0 = (B * D - 2 * A * E) / det;
        double f0 = A * x0 * x0 + B * x0 * y0 + C * y0 * y0 + D * x0 + E * y0 + F;
        double theta = 0.5 * Math.Atan2(B, A - C);
        double ct = Math.Cos(theta), st = Math.Sin(theta);
        double a1 = A * ct * ct + B * st * ct + C * st * st;
        double c1 = A * st * st - B * st * ct + C * ct * ct;
        double ra = -f0 / a1, rb = -f0 / c1;
        if (ra <= 0 || rb <= 0) throw new InvalidOperationException("Ellisse degenere");
        double sa = Math.Sqrt(ra), sb = Math.Sqrt(rb);
        if (sa < sb) { (sa, sb) = (sb, sa); theta += Math.PI / 2; }
        // normalise rotation to (-π/2, π/2]
        while (theta > Math.PI / 2) theta -= Math.PI;
        while (theta <= -Math.PI / 2) theta += Math.PI;
        return (new Vec2(x0, y0), sa, sb, theta);
    }

    /// <summary>
    /// Algebraic least-squares fit (smallest eigenvector of the scatter matrix) on
    /// normalised coordinates. Exact to numerical precision when the points lie on a conic.
    /// </summary>
    public static Conic Fit(IReadOnlyList<Vec2> pts)
    {
        if (pts.Count < 6) throw new ArgumentException("Servono almeno 6 punti");
        double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
        double sc = pts.Max(p => Math.Max(Math.Abs(p.X - mx), Math.Abs(p.Y - my)));
        if (sc <= 0) throw new ArgumentException("Punti degeneri");

        var S = new double[6, 6];
        foreach (var p in pts)
        {
            double x = (p.X - mx) / sc, y = (p.Y - my) / sc;
            var r = new[] { x * x, x * y, y * y, x, y, 1.0 };
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < 6; j++) S[i, j] += r[i] * r[j];
        }
        var v = Jacobi.SmallestEigenvector(S);
        double a = v[0], b = v[1], c = v[2], d = v[3], e = v[4], f = v[5];

        // de-normalise: x' = (x-mx)/sc, y' = (y-my)/sc  (see derivation in docs)
        double A = a, B = b, C = c;
        double D = -2 * a * mx - b * my + d * sc;
        double E = -b * mx - 2 * c * my + e * sc;
        double F = a * mx * mx + b * mx * my + c * my * my - d * sc * mx - e * sc * my + f * sc * sc;
        double norm = Math.Sqrt(A * A + B * B + C * C + D * D + E * E + F * F);
        return new Conic(A / norm, B / norm, C / norm, D / norm, E / norm, F / norm);
    }
}

/// <summary>Cyclic Jacobi eigen-decomposition for small symmetric matrices.</summary>
internal static class Jacobi
{
    public static double[] SmallestEigenvector(double[,] S)
    {
        int n = S.GetLength(0);
        var a = (double[,])S.Clone();
        var v = new double[n, n];
        for (int i = 0; i < n; i++) v[i, i] = 1;

        for (int sweep = 0; sweep < 100; sweep++)
        {
            double off = 0;
            for (int p = 0; p < n; p++) for (int q = p + 1; q < n; q++) off += a[p, q] * a[p, q];
            if (off < 1e-30) break;
            for (int p = 0; p < n; p++)
                for (int q = p + 1; q < n; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-300) continue;
                    double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < n; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq; a[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk; a[q, k] = s * apk + c * aqk;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq; v[k, q] = s * vkp + c * vkq;
                    }
                }
        }
        int best = 0;
        for (int i = 1; i < n; i++) if (a[i, i] < a[best, best]) best = i;
        var r = new double[n];
        for (int k = 0; k < n; k++) r[k] = v[k, best];
        return r;
    }
}
