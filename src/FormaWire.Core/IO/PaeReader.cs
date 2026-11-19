using System.Globalization;
using System.Text.RegularExpressions;
using FormaWire.Geometry;

namespace FormaWire.IO;

/// <summary>A ruled surface definition (PAC "SF") referencing entity names.</summary>
public sealed record PaeSurface(int Index, double ZXY, IReadOnlyList<string> UpperNames, double ZUV, IReadOnlyList<string> LowerNames, string? StartPoint, string? FinalPoint);

/// <summary>A CALL to a subroutine with its KEY&lt;value&gt; parameters.</summary>
public sealed record PaeCall(string SubName, IReadOnlyDictionary<string, double> Params)
{
    public bool IsRotatedInsertSub => SubName.Contains("RUOTATA", StringComparison.OrdinalIgnoreCase)
                                   || SubName.Contains("DEFTANGENZIALE", StringComparison.OrdinalIgnoreCase);
    public double Get(string key, double dflt = 0) => Params.TryGetValue(key, out var v) ? v : dflt;

    public Mounting ToMounting() => new(RotP: Get("ROTP"), RotB: Get("ROTB"), DRot: Get("DROT"), Iy: Get("IY"));
}

/// <summary>Parsed content of a PAC/TXPAC .PAE program (geometry + surfaces + sub calls).</summary>
public sealed class PaeJob
{
    public string? ProgramNumber { get; set; }
    public string? NcPath { get; set; }
    public Dictionary<string, Element> Entities { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Vec2> Points { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, PaeSurface> Surfaces { get; } = new();
    public List<PaeCall> Calls { get; } = new();
    public double? PartThickness { get; set; }
    public List<string> Warnings { get; } = new();

    /// <summary>The CALL of the deformation sub, if any.</summary>
    public PaeCall? DeformCall => Calls.FirstOrDefault(c => c.IsRotatedInsertSub);

    public Element Resolve(string name)
    {
        bool rev = name.Length > 1 && name[1] == '-';
        string key = rev ? name[0] + name[2..] : name;
        if (!Entities.TryGetValue(key, out var e)) throw new KeyNotFoundException($"Entità PAE '{key}' non definita");
        return rev ? e.Reversed() : e;
    }

    public ProfilePair ToProfilePair(int sfIndex, string? name = null)
    {
        if (!Surfaces.TryGetValue(sfIndex, out var sf)) throw new KeyNotFoundException($"SF{sfIndex} non definita");
        var upper = new Profile(sf.UpperNames.Select(Resolve), $"SF{sfIndex} XY");
        var lower = new Profile(sf.LowerNames.Select(Resolve), $"SF{sfIndex} UV");
        return new ProfilePair(upper, lower, Math.Abs(sf.ZUV - sf.ZXY), name);
    }
}

public static class PaeReader
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static PaeJob Read(string path) => Parse(File.ReadAllText(path, System.Text.Encoding.Latin1));

    public static PaeJob Parse(string text)
    {
        // join continuation lines ("...|" + newline + ",...")
        text = Regex.Replace(text, @"\|\s*\r?\n", "");
        var job = new PaeJob();
        string? prog = null, nc = null;
        var vars = new Dictionary<string, double>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("'!NPRO,")) { prog = line[7..]; continue; }
            if (line.StartsWith("'!FISO,")) { nc = line[7..]; continue; }
            if (line.StartsWith('\'')) continue;
            // strip trailing comment
            int q = line.IndexOf('\'');
            if (q >= 0) line = line[..q].Trim();

            var toks = line.Split(',');
            var head = toks[0].ToUpperInvariant();

            if (Regex.IsMatch(head, @"^#\d+$") && toks.Length >= 2 && TryNum(toks[1], out var vv)) { vars[head] = vv; continue; }

            if (Regex.IsMatch(head, @"^[LC]\d+$"))
            {
                ParseEntity(job, head, toks[1..]);
                continue;
            }
            if (Regex.IsMatch(head, @"^P\d+$"))
            {
                if (toks.Length >= 3 && toks[1].StartsWith('X') && toks[2].StartsWith('Y') && TryNum(toks[1][1..], out var px) && TryNum(toks[2][1..], out var py))
                    job.Points[head] = new Vec2(px, py);
                continue;
            }
            if (Regex.IsMatch(head, @"^SF\d+$"))
            {
                ParseSurface(job, int.Parse(head[2..], Inv), toks[1..]);
                continue;
            }
            if (head == "PART")
            {
                foreach (var t in toks[1..]) if (t.StartsWith("DP", StringComparison.OrdinalIgnoreCase) && TryNum(t[2..], out var dp)) job.PartThickness = dp;
                continue;
            }
            if (head == "CALL" && toks.Length >= 2)
            {
                var pars = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in toks[2..])
                {
                    var m = Regex.Match(t.Trim(), @"^([A-Za-z]+)(-?[\d.]+)$");
                    if (m.Success && TryNum(m.Groups[2].Value, out var pv)) pars[m.Groups[1].Value.ToUpperInvariant()] = pv;
                }
                job.Calls.Add(new PaeCall(toks[1].Trim(), pars));
                continue;
            }
        }
        job.ProgramNumber = prog; job.NcPath = nc;
        return job;
    }

    static bool TryNum(string s, out double v) => double.TryParse(s.Trim(), NumberStyles.Float, Inv, out v);

    static void ParseEntity(PaeJob job, string name, string[] toks)
    {
        // Sequence of X/Y/Z coordinates; a repeated X starts the second point. R = radius (arcs).
        var pts = new List<double[]>();
        double[] cur = { double.NaN, double.NaN, 0 };
        bool any = false;
        double? radius = null;
        foreach (var t0 in toks)
        {
            var t = t0.Trim();
            if (t.Length < 2) continue;
            char k = char.ToUpperInvariant(t[0]);
            if (!TryNum(t[1..], out var v))
            {
                job.Warnings.Add($"{name}: valore '{t}' non leggibile (riferimento parametrico?) — entità saltata");
                return;
            }
            switch (k)
            {
                case 'X':
                    if (any && !double.IsNaN(cur[0])) { pts.Add(cur); cur = new[] { double.NaN, double.NaN, 0 }; }
                    cur[0] = v; any = true; break;
                case 'Y': cur[1] = v; break;
                case 'Z': cur[2] = v; break;
                case 'R': radius = v; break;
            }
        }
        if (any) pts.Add(cur);
        if (pts.Count != 2 || pts.Any(p => double.IsNaN(p[0]) || double.IsNaN(p[1])))
        {
            job.Warnings.Add($"{name}: forma di definizione non supportata — entità saltata");
            return;
        }
        var p0 = new Vec2(pts[0][0], pts[0][1]);
        var p1 = new Vec2(pts[1][0], pts[1][1]);
        if (name[0] == 'L')
            job.Entities[name] = new LineSeg(p0, p1, name);
        else
        {
            if (radius is null) { job.Warnings.Add($"{name}: arco senza raggio — saltato"); return; }
            job.Entities[name] = ArcSeg.FromStartEndRadiusCcwMinor(p0, p1, radius.Value, name);
        }
    }

    static void ParseSurface(PaeJob job, int index, string[] toks)
    {
        double zxy = 0, zuv = 0;
        var upper = new List<string>(); var lower = new List<string>();
        string? sp = null, fp = null;
        int phase = 0; // 0 before ZXY, 1 upper list, 2 lower list
        foreach (var t0 in toks)
        {
            var t = t0.Trim(); if (t.Length == 0) continue;
            var u = t.ToUpperInvariant();
            if (u.StartsWith("ZXY")) { zxy = Num(u[3..]); phase = 1; continue; }
            if (u.StartsWith("ZUV")) { zuv = Num(u[3..]); phase = 2; continue; }
            if (u.StartsWith("SP")) { sp = u; continue; }
            if (u.StartsWith("FP")) { fp = u; continue; }
            if (u.StartsWith("EA"))
            {
                throw new NotSupportedException($"SF{index}: la forma con angoli di sformo (EA...) non è ancora supportata; disegna il profilo inferiore.");
            }
            if (Regex.IsMatch(u, @"^[LC]-?\d+$"))
            {
                if (phase == 1) upper.Add(u); else if (phase == 2) lower.Add(u);
                continue;
            }
            job.Warnings.Add($"SF{index}: valore '{t}' ignorato");
        }
        job.Surfaces[index] = new PaeSurface(index, zxy, upper, zuv, lower, sp, fp);
    }

    static double Num(string s) => double.Parse(s, NumberStyles.Float, Inv);
}
