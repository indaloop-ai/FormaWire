using System.Globalization;
using FormaWire;
using FormaWire.Deformation;
using FormaWire.Geometry;
using FormaWire.IO;

const string Usage = """
formawire — deformazione dei profili per elettroerosione a filo con la placchetta montata inclinata

  formawire deform <lavoro.pae> [-o uscita.dxf] [--nominal] [--flat] [montaggio]
  formawire deform <disegno.dxf> --upper <layer> --lower <layer> [--depth h] [-o uscita.dxf] [--nominal] [--flat] [montaggio]
  formawire deform <disegno.dxf> --upper <layer> --depth h --generate-lower [--front gradi] [--relief gradi | --side a-b:gradi... --shift a-b:dx,dy...]
  formawire pick   <disegno.dxf> --near x,y [--layer L] [--reverse] [--range a-b] [--origin auto|x,y] [--lead mm] [--lead-angle gradi] [-o profilo.dxf]
                   prende la catena del profilo che tocca l'elemento più vicino a x,y, mette (X più a sinistra, Y più alta)
                   nell'origine, aggiunge le linee di entrata/uscita a 45° che finiscono --lead mm (predefinito 1) oltre il profilo in X
  formawire verify <lavoro.pae> <lavoro.nc>      confronta con il programma NC prodotto da PAC
  formawire info   <lavoro.pae|disegno.dxf> [--upper <layer>]   elenca superfici / layer / parametri / numeri degli elementi

Montaggio (predefiniti dalla chiamata della sub nel PAE; per un DXF vanno indicati):
  --rotb <gradi> angolo di montaggio (la barretta in macchina, PAC ROTB)   --rotp <gradi> angolo del tagliente (la placchetta nella barretta, PAC ROTP)
  --drot <gradi> correzione della spoglia frontale (PAC DROT)        --iy <mm>      Y del perno della deformazione (PAC IY)
  --sf <n>       numero della superficie nel PAE (predefinito: quello della chiamata della sub)
Costruzione del profilo inferiore (--generate-lower, solo profilo superiore):
  --front <gradi>  spoglia frontale voluta; profilo inferiore spostato di +Y profondità·tan(ROTB − front)  (BA1408: 6)
  --relief <gradi> spoglia laterale su ogni fianco, ruoli trovati in automatico come nell'editor (BA1408: --relief 1)
  --side a-b:gradi spoglia laterale sugli elementi a..b (da 1, in ordine di catena): spostamento in X di profondità·tan(gradi), segno = verso
  --shift a-b:dx,dy  spostamento in mm sugli elementi a..b
  Gli elementi tra tratti spostati in modo diverso diventano raccordi.
Uscita:
  --nominal      scrive anche i profili non deformati (layer XY_NOM / UV_NOM)
  --flat         scrive il profilo inferiore a Z=0 invece che a Z=-profondità
  --tol <mm>     tolleranza di catena per i DXF (predefinita 0.001)
""";

try
{
    return Run(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine("errore: " + ex.Message);
    return 2;
}

static int Run(string[] args)
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    if (args.Length == 0 || args[0] is "-h" or "--help") { Console.WriteLine(Usage); return 0; }
    var opts = ParseOptions(args.Skip(1).ToArray(), out var positional);
    switch (args[0].ToLowerInvariant())
    {
        case "deform": return Deform(positional, opts);
        case "verify": return Verify(positional, opts);
        case "pick": return Pick(positional, opts);
        case "info": return Info(positional, opts);
        default: Console.WriteLine(Usage); return 1;
    }
}

static Dictionary<string, string> ParseOptions(string[] args, out List<string> positional)
{
    var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    positional = new List<string>();
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--") || args[i] == "-o")
        {
            string key = args[i].TrimStart('-');
            if (key is "nominal" or "flat" or "generate-lower" or "reverse") { o[key] = "1"; continue; }
            if (i + 1 >= args.Length) throw new ArgumentException($"manca il valore di {args[i]}");
            string val = args[++i];
            o[key] = o.TryGetValue(key, out var prev) ? prev + ";" + val : val;
        }
        else positional.Add(args[i]);
    }
    return o;
}

static double Opt(Dictionary<string, string> o, string key, double dflt)
    => o.TryGetValue(key, out var s) ? double.Parse(s, CultureInfo.InvariantCulture) : dflt;

static (ProfilePair pair, Mounting mounting, string source) Load(List<string> pos, Dictionary<string, string> o, List<string> warnings)
{
    if (pos.Count < 1) throw new ArgumentException("manca il file di ingresso");
    string input = pos[0];
    bool isPae = input.EndsWith(".pae", StringComparison.OrdinalIgnoreCase);
    ProfilePair pair; Mounting m;
    if (isPae)
    {
        int? sf = o.TryGetValue("sf", out var s) ? int.Parse(s, CultureInfo.InvariantCulture) : null;
        var load = Jobs.LoadPae(input, sf);
        warnings.AddRange(load.Job.Warnings);
        pair = load.Pair; m = load.Mounting;
        Console.WriteLine($"PAE {Path.GetFileName(input)}: SF{(sf ?? (int)load.Call.Get("SFN", 1))}, {pair.Count} coppie di elementi, profondità {pair.Depth:0.###} mm, sub {load.Call.SubName}");
    }
    else
    {
        m = new Mounting();
        if (!o.ContainsKey("rotb")) warnings.Add("--rotb non indicato per un DXF: uso 8°");
        if (o.ContainsKey("generate-lower"))
        {
            if (!o.ContainsKey("upper") || !o.ContainsKey("depth")) throw new ArgumentException("--generate-lower richiede --upper <layer> e --depth <mm>");
            double h = Opt(o, "depth", 0);
            var dwg = DxfReader.Read(input); warnings.AddRange(dwg.Warnings);
            var upper = ProfileChainer.Chain(dwg.ElementsOnLayer(o["upper"]).ToList(), Opt(o, "tol", 0.001), o["upper"], warnings);
            var lower = GenerateLower(upper, h, Opt(o, "rotb", 8), o, warnings);
            pair = new ProfilePair(upper, lower, h, Path.GetFileNameWithoutExtension(input));
            Console.WriteLine($"DXF {Path.GetFileName(input)}: layer {o["upper"]}, {pair.Count} elementi, profilo inferiore costruito, profondità {h:0.###} mm");
        }
        else
        {
            if (!o.ContainsKey("upper") || !o.ContainsKey("lower")) throw new ArgumentException("un DXF richiede --upper <layer> e --lower <layer> (oppure --generate-lower)");
            double? depth = o.ContainsKey("depth") ? Opt(o, "depth", 0) : null;
            pair = Jobs.LoadDxf(input, o["upper"], o["lower"], depth, Opt(o, "tol", 0.001), warnings);
            Console.WriteLine($"DXF {Path.GetFileName(input)}: layer {o["upper"]}/{o["lower"]}, {pair.Count} coppie di elementi, profondità {pair.Depth:0.###} mm");
        }
    }
    m = m with
    {
        RotB = Opt(o, "rotb", m.RotB), RotP = Opt(o, "rotp", m.RotP),
        DRot = Opt(o, "drot", m.DRot), Iy = Opt(o, "iy", m.Iy),
    };
    return (pair, m, input);
}

static Profile GenerateLower(Profile upper, double depth, double rotB, Dictionary<string, string> o, List<string> warnings)
{
    var global = o.ContainsKey("front") ? LowerProfileGenerator.FrontClearanceShift(depth, rotB, Opt(o, "front", rotB)) : Vec2.Zero;
    if (o.ContainsKey("relief"))
    {
        var edges = ReliefPlanner.Auto(upper, Opt(o, "relief", 0), ReliefPlanner.MaterialSide(upper));
        Console.WriteLine($"Profilo inferiore: spostamento comune {global}, spoglia laterale {Opt(o, "relief", 0):0.##}° — ruoli: {string.Join(" ", edges.Select((e, i) => $"{i + 1}:{(e.Kind == EdgeKind.Flank ? (e.Shift(depth).X > 0 ? "+" : "-") : e.Kind.ToString()[..1])}"))}");
        return ProfileOps.Translate(ReliefPlanner.Generate(upper, depth, edges, warnings), global);
    }
    var runs = new List<RunShift>();
    if (o.TryGetValue("side", out var sides))
        foreach (var spec in sides.Split(';'))
        {
            var (a, b, rest) = ParseRange(spec);
            runs.Add(RunShift.SideRelief(a, b, depth, double.Parse(rest, CultureInfo.InvariantCulture)));
        }
    if (o.TryGetValue("shift", out var shifts))
        foreach (var spec in shifts.Split(';'))
        {
            var (a, b, rest) = ParseRange(spec);
            var xy = rest.Split(',');
            runs.Add(new RunShift(a, b, new Vec2(double.Parse(xy[0], CultureInfo.InvariantCulture), double.Parse(xy[1], CultureInfo.InvariantCulture)), $"shift {rest} on {a}-{b}"));
        }
    Console.WriteLine($"Profilo inferiore: spostamento comune {global} (spoglia frontale {(o.ContainsKey("front") ? Opt(o, "front", rotB) : rotB):0.##}°), {runs.Count} tratti: {string.Join("; ", runs.Select(r => $"{r.Label} → {r.Shift}"))}");
    return LowerProfileGenerator.Generate(upper, global, runs, warnings);
}

static (int a, int b, string rest) ParseRange(string spec)
{
    int colon = spec.IndexOf(':');
    if (colon < 0) throw new ArgumentException($"tratto '{spec}' non valido, atteso a-b:valore");
    var ab = spec[..colon].Split('-');
    return (int.Parse(ab[0], CultureInfo.InvariantCulture), int.Parse(ab[^1], CultureInfo.InvariantCulture), spec[(colon + 1)..]);
}

static void PrintMounting(Mounting m, double depth, double? front = null)
{
    double a = m.RotP + m.RotB;
    Console.WriteLine($"Montaggio: ROTP {m.RotP:0.##}°  ROTB {m.RotB:0.##}°  DROT {m.DRot:0.##}°  IY {m.Iy:0.###}  → inclinazione totale {a:0.##}°");
    Console.WriteLine($"  fattore di scala in Y cos({a:0.##})/cos({m.RotP:0.##}) = {m.ScaleY:0.######}");
    Console.WriteLine(front is null
        ? $"  spoglia frontale dall'inclinazione dell'attrezzo = ROTB + DROT = {m.RotB + m.DRot:0.##}°"
        : $"  spoglia frontale: attrezzo {m.RotB:0.##}° corretto a {front:0.##}° spostando il profilo inferiore");
    Console.WriteLine($"  inclinazione dei fianchi per una spoglia e: tan(e)·sin({a:0.##}°) = tan(e)·{Math.Sin(Mounting.Deg(a)):0.00000}  (3° → {Math.Tan(Mounting.Deg(3)) * Math.Sin(Mounting.Deg(a)) * 1000:0.0} µm/mm)");
    Console.WriteLine($"  fattore di conicità apparente 1/cos({a:0.##}°) = {1 / Math.Cos(Mounting.Deg(a)):0.######}");
}

static int Deform(List<string> pos, Dictionary<string, string> o)
{
    var warnings = new List<string>();
    var (pair, m, input) = Load(pos, o, warnings);
    PrintMounting(m, pair.Depth, o.ContainsKey("front") ? Opt(o, "front", m.RotB) : null);

    var result = new Deformer(m, pair.Depth).Deform(pair);
    warnings.AddRange(result.Warnings);

    string outPath = o.TryGetValue("o", out var op) ? op : Path.ChangeExtension(input, null) + "_DEF.dxf";
    double zLower = o.ContainsKey("flat") ? 0 : -pair.Depth;
    var w = new DxfWriter()
        .Add(result.Upper, "XY", 0, color: 1)
        .Add(result.Lower, "UV", zLower, color: 5);
    if (o.ContainsKey("nominal"))
        w.Add(pair.Upper, "XY_NOM", 0, color: 8).Add(pair.Lower, "UV_NOM", zLower, color: 8);
    w.Save(outPath);

    int lines = result.Upper.Elements.Count(e => e is LineSeg), ell = result.Upper.Elements.Count(e => e is EllipseArcSeg),
        arcs = result.Upper.Elements.Count(e => e is ArcSeg), poly = result.Upper.Elements.Count(e => e is PolylineSeg);
    Console.WriteLine($"Uscita: {outPath}  (per piano: {lines} linee, {ell} archi di ellisse, {arcs} archi, {poly} polilinee)");
    foreach (var e in result.Upper.Elements.OfType<EllipseArcSeg>())
        Console.WriteLine($"  {e.Tag}: {e}");
    foreach (var wmsg in warnings) Console.WriteLine("attenzione: " + wmsg);
    return 0;
}

static int Verify(List<string> pos, Dictionary<string, string> o)
{
    if (pos.Count < 2) throw new ArgumentException("verify richiede <lavoro.pae> <lavoro.nc>");
    var warnings = new List<string>();
    var (pair, m, _) = Load(pos.Take(1).ToList(), o, warnings);
    var result = new Deformer(m, pair.Depth).Deform(pair);
    var nc = NcReader.ReadFirstTaperPass(pos[1]);
    if (nc.Count == 0) throw new InvalidOperationException("nessuna passata conica (parole U/V) nel file NC");
    var v = Verification.Compare(result, nc);
    Console.WriteLine($"NC {Path.GetFileName(pos[1])}: {v.Points} punti nella prima passata conica");
    Console.WriteLine($"  scarto massimo piano XY: {v.MaxDevUpper * 1000:0.0} µm (blocco N{v.WorstBlockUpper})");
    Console.WriteLine($"  scarto massimo piano UV: {v.MaxDevLower * 1000:0.0} µm (blocco N{v.WorstBlockLower})");
    var taper = NcReader.MaxTaperDeg(pos[1]);
    if (taper is not null) Console.WriteLine($"  l'NC indica conicità massima {taper:0.###}°");
    foreach (var wmsg in warnings.Concat(result.Warnings)) Console.WriteLine("attenzione: " + wmsg);
    bool ok = v.MaxDevUpper < 0.002 && v.MaxDevLower < 0.002;
    Console.WriteLine(ok ? "CORRISPONDE (entro l'arrotondamento dell'NC)" : "NON CORRISPONDE");
    return ok ? 0 : 1;
}

static int Pick(List<string> pos, Dictionary<string, string> o)
{
    if (pos.Count < 1 || !o.ContainsKey("near")) throw new ArgumentException("pick richiede <disegno.dxf> --near x,y");
    var dwg = DxfReader.Read(pos[0]);
    var warnings = new List<string>(dwg.Warnings);
    var pool = (o.TryGetValue("layer", out var layer) ? dwg.ElementsOnLayer(layer) : dwg.Entities.SelectMany(e => e.Elements)).ToList();
    if (pool.Count == 0) throw new InvalidOperationException("nessuna geometria da cui scegliere");
    var xy = o["near"].Split(','); var near = new Vec2(double.Parse(xy[0], CultureInfo.InvariantCulture), double.Parse(xy[1], CultureInfo.InvariantCulture));
    var seed = pool.MinBy(e => Profile.DistanceToPolyline(near, e.Sample(32).ToList()))!;
    double tol = Opt(o, "tol", 0.001);
    var chain = ProfileOps.PickChain(pool, seed, tol, warnings);
    if (o.ContainsKey("reverse")) chain = chain.Reversed();
    Console.WriteLine($"partenza: {seed} → catena di {chain.Count} elementi ({(chain.IsClosed() ? "chiusa" : "aperta")}), da {chain.Start} a {chain.End}");
    if (o.TryGetValue("range", out var rng))
    {
        var ab = rng.Split('-'); int a = int.Parse(ab[0], CultureInfo.InvariantCulture), b = int.Parse(ab[^1], CultureInfo.InvariantCulture);
        chain = new Profile(chain.Elements.Skip(a - 1).Take(b - a + 1), chain.Name);
        Console.WriteLine($"tenuti gli elementi {a}-{b}: {chain.Count} elementi");
    }
    string org = o.TryGetValue("origin", out var orgOpt) ? orgOpt : "auto";
    if (org != "none")
    {
        var pt = org == "auto" ? ProfileOps.ReferenceCorner(chain) : new Vec2(double.Parse(org.Split(',')[0], CultureInfo.InvariantCulture), double.Parse(org.Split(',')[1], CultureInfo.InvariantCulture));
        chain = ProfileOps.MoveToOrigin(chain, pt);
        Console.WriteLine($"origine: {pt} (X più a sinistra, Y più alta) portato in (0,0)");
    }
    double lead = Opt(o, "lead", 1.0);
    if (lead > 0)
    {
        chain = ProfileOps.AddLeads(chain, lead, Opt(o, "lead-angle", 45));
        Console.WriteLine($"linee di entrata/uscita a {Opt(o, "lead-angle", 45):0.#}° fino a {lead:0.###} mm oltre il profilo: da {chain.Start} a {chain.End}");
    }
    foreach (var (i, e) in ProfileOps.Indexed(chain)) Console.WriteLine($"  {i,3}  {e}");
    string outPath = o.TryGetValue("o", out var op) ? op : Path.ChangeExtension(pos[0], null) + "_PROFILE.dxf";
    new DxfWriter().Add(chain, "XY", 0, color: 1).Save(outPath);
    Console.WriteLine($"Uscita: {outPath} (layer XY)");
    foreach (var wmsg in warnings) Console.WriteLine("attenzione: " + wmsg);
    return 0;
}

static int Info(List<string> pos, Dictionary<string, string> o)
{
    if (pos.Count < 1) throw new ArgumentException("manca il file di ingresso");
    string input = pos[0];
    if (input.EndsWith(".pae", StringComparison.OrdinalIgnoreCase))
    {
        var job = PaeReader.Read(input);
        Console.WriteLine($"Programma {job.ProgramNumber}, NC {job.NcPath}, spessore pezzo {job.PartThickness}");
        Console.WriteLine($"{job.Entities.Count} entità, {job.Points.Count} punti");
        foreach (var sf in job.Surfaces.Values.OrderBy(s => s.Index))
            Console.WriteLine($"SF{sf.Index}: ZXY {sf.ZXY} [{string.Join(",", sf.UpperNames)}]  ZUV {sf.ZUV} [{string.Join(",", sf.LowerNames)}]");
        foreach (var c in job.Calls)
            Console.WriteLine($"CALL {c.SubName}: {string.Join(" ", c.Params.Select(p => p.Key + "=" + p.Value))}{(c.IsRotatedInsertSub ? "  (sub di deformazione)" : "")}");
        foreach (var wmsg in job.Warnings) Console.WriteLine("attenzione: " + wmsg);
    }
    else
    {
        var dwg = DxfReader.Read(input);
        foreach (var layer in dwg.Layers)
        {
            var els = dwg.ElementsOnLayer(layer).ToList();
            Console.WriteLine($"layer {layer}: {els.Count} elementi ({els.Count(e => e is LineSeg)} linee, {els.Count(e => e is ArcSeg)} archi, {els.Count(e => e is EllipseArcSeg)} ellissi), Z {dwg.ElevationOfLayer(layer):0.###}");
        }
        if (o.TryGetValue("upper", out var ul))
        {
            var warnings = new List<string>();
            var chain = ProfileChainer.Chain(dwg.ElementsOnLayer(ul).ToList(), Opt(o, "tol", 0.001), ul, warnings);
            Console.WriteLine($"catena su {ul} ({(chain.IsClosed() ? "chiusa" : "aperta")}):");
            for (int i = 0; i < chain.Count; i++) Console.WriteLine($"  {i + 1,3}  {chain.Elements[i]}");
            foreach (var wmsg in warnings) Console.WriteLine("attenzione: " + wmsg);
        }
        foreach (var wmsg in dwg.Warnings) Console.WriteLine("attenzione: " + wmsg);
    }
    return 0;
}
