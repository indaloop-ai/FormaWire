using System.Globalization;
using FormaWire.Deformation;
using FormaWire.Geometry;
using FormaWire.IO;

namespace FormaWire.Editing;

/// <summary>One drawing entity in the editor. Elements are immutable, so an entity is replaced to change it.</summary>
public sealed record DocEntity(int Id, string Layer, Element Element, double Z = 0);

/// <summary>Where the second (UV) profile comes from.</summary>
public enum UvSource
{
    /// <summary>Built from the XY profile and the clearance angles, rebuilt after every change.</summary>
    Angles,
    /// <summary>Picked in the drawing, loaded from the file, or edited by hand; its angles are measured.</summary>
    Drawn,
}

/// <summary>What an exported DXF contains.</summary>
public enum DxfContent { Deformed, Undeformed, Both }

/// <summary>A per-edge choice in the relief table: its own direction and/or angle (null = automatic).</summary>
public readonly record struct EdgeOverride(ReliefDir? Dir, double? AngleDeg)
{
    /// <summary>Blank = automatic (null), a number = relief angle in degrees (0 = none, negative = the other way).</summary>
    public static double? ParseAngle(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        if (double.TryParse(t.Replace(',', '.').TrimEnd('°'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        throw new FormatException($"'{t}': scrivi un angolo in gradi (0 = nessuna spoglia, negativo = dall'altra parte) oppure lascia vuoto per automatico");
    }
}

/// <summary>One XY edge in the relief table.</summary>
public sealed class ReliefRow
{
    public int Index { get; init; }
    public int Id { get; init; }
    public string Kind { get; init; } = "";
    public double Length { get; init; }
    public EdgeRelief Auto { get; init; }
    public EdgeRelief Effective { get; init; }
    public EdgeOverride? Override { get; init; }
    /// <summary>What the current UV profile actually gives this edge.</summary>
    public EdgeMeasure? Measured { get; init; }
}

/// <summary>
/// The editor's model: entities, selection, the XY/UV profile chains, mounting parameters, undo.
/// Every command is a method here (CAD commands in EditorDocument.Cad.cs) so the UI stays thin and the
/// behaviour is testable.
///
/// The deformation takes two angles, the mounting (ROTB) and the insert inclination (ROTP). Every clearance
/// is in the second (UV) profile: the side reliefs per edge, and the front correction (8° mounting → 6° =
/// −2°) as a Y shift of the whole profile, as the drafting office draws it. A drawn UV profile is used as
/// it is; its angles are measured.
/// </summary>
public sealed partial class EditorDocument
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private int _nextId = 1;
    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    private HashSet<int> _lowerAtCommit => ActiveProfile.LowerAtCommit;
    private sealed record Snapshot(List<DocEntity> Entities, List<MachiningProfile> Profiles, int ActiveId, int NextId, int NextProfileId);

    public List<DocEntity> Entities { get; private set; } = new();
    public HashSet<int> Selection { get; } = new();
    public HashSet<string> HiddenLayers { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Ordered ids of the XY (upper) profile; the elements are already oriented in chain direction.</summary>
    public List<int> Upper { get => ActiveProfile.Upper; private set => ActiveProfile.Upper = value; }
    public List<int> Lower { get => ActiveProfile.Lower; private set => ActiveProfile.Lower = value; }
    public List<ReliefRow> Relief { get => ActiveProfile.Relief; private set => ActiveProfile.Relief = value; }

    public string? FileName { get; set; }
    public double ChainTolerance { get; set; } = 0.001;
    public List<string> Log { get; } = new();
    public DeformResult? Deformed { get => ActiveProfile.Deformed; private set => ActiveProfile.Deformed = value; }

    // ------------------------------------------------------------------ parameters
    private double _depth { get => ActiveProfile.Depth; set => ActiveProfile.Depth = value; }
    private double _side { get => ActiveProfile.SideClearanceDeg; set => ActiveProfile.SideClearanceDeg = value; }
    private double _front { get => ActiveProfile.FrontCorrectionDeg; set => ActiveProfile.FrontCorrectionDeg = value; }
    private bool _flip { get => ActiveProfile.FlipMaterial; set => ActiveProfile.FlipMaterial = value; }
    private bool _normal { get => ActiveProfile.ReliefNormal; set => ActiveProfile.ReliefNormal = value; }

    /// <summary>Insert thickness: XY at Z0, UV at Z = −Depth.</summary>
    public double Depth { get => _depth; set { if (value > 0 && value != _depth) { _depth = value; foreach (var id in Lower) Replace(id, e => e with { Z = -value }); Changed(); } } }
    /// <summary>Side relief of the flanks when the UV profile is built from angles.</summary>
    public double SideClearanceDeg { get => _side; set { if (value != _side) { _side = value; Changed(); } } }
    /// <summary>
    /// Front correction of the UV profile built from angles: the whole profile moves by −depth·tan(angle) in Y
    /// (−2° = 2° less front clearance than the mounting gives: 8° → 6°; validated on BA1408).
    /// </summary>
    public double FrontCorrectionDeg { get => _front; set { if (value != _front) { _front = value; Changed(); } } }
    /// <summary>Relieve square to the edges (a true offset) instead of horizontally (flanks) / vertically (front edges).</summary>
    public bool ReliefNormal { get => _normal; set { if (value != _normal) { _normal = value; Changed(); } } }
    /// <summary>The material is on the other side of the profile than assumed (open profile: below; closed: inside).</summary>
    public bool FlipMaterial { get => _flip; set { if (value != _flip) { _flip = value; Changed(); } } }

    // defaults as the PAC sub NUOVA-BARRETTAxPLACCHETTA-RUOTATA-deltaANG (@DEFV ROTB 8, ROTP 6)
    private double _mount { get => ActiveProfile.MountDeg; set => ActiveProfile.MountDeg = value; }
    private double _rotp { get => ActiveProfile.InsertRotDeg; set => ActiveProfile.InsertRotDeg = value; }
    private double _iy { get => ActiveProfile.PivotY; set => ActiveProfile.PivotY = value; }
    /// <summary>Angle the bar is mounted at in the machine (PAC ROTB; 8° on the fixture in use).</summary>
    public double MountDeg { get => _mount; set { _mount = value; Deformed = null; } }
    /// <summary>Inclination of the insert in the bar (PAC ROTP), on top of the mounting.</summary>
    public double InsertRotDeg { get => _rotp; set { _rotp = value; Deformed = null; } }
    /// <summary>Y of the pivot of the deformation (PAC IY).</summary>
    public double PivotY { get => _iy; set { _iy = value; Deformed = null; } }

    /// <summary>Y shift of the UV profile built from angles for the front correction.</summary>
    public Vec2 FrontShift => ReliefPlanner.FrontShift(Depth, FrontCorrectionDeg);
    /// <summary>The front correction the current UV profile carries (its common Y offset), null without UV.</summary>
    public double? MeasuredFrontDeg { get => ActiveProfile.MeasuredFrontDeg; private set => ActiveProfile.MeasuredFrontDeg = value; }
    public Mounting Mounting => new(RotP: InsertRotDeg, RotB: MountDeg, DRot: 0, Iy: PivotY);

    public UvSource UvMode { get => ActiveProfile.UvMode; private set => ActiveProfile.UvMode = value; }
    /// <summary>Per-edge relief choices, keyed by entity id (so they survive inserted leads and reordering).</summary>
    public Dictionary<int, EdgeOverride> ReliefOverrides { get => ActiveProfile.ReliefOverrides; private set => ActiveProfile.ReliefOverrides = value; }
    /// <summary>Why the UV profile could not be built from the angles (null when fine).</summary>
    public string? UvError { get => ActiveProfile.UvError; private set => ActiveProfile.UvError = value; }
    public List<string> UvWarnings { get => ActiveProfile.UvWarnings; private set => ActiveProfile.UvWarnings = value; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public DocEntity? Get(int id) => Entities.FirstOrDefault(e => e.Id == id);
    public IEnumerable<string> Layers => Entities.Select(e => e.Layer).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(l => l, StringComparer.OrdinalIgnoreCase);
    public IEnumerable<DocEntity> Selected => Entities.Where(e => Selection.Contains(e.Id));
    public IEnumerable<DocEntity> Visible => Entities.Where(e => !HiddenLayers.Contains(e.Layer));

    // ------------------------------------------------------------------ undo
    private void Commit()
    {
        _undo.Push(Current());
        _redo.Clear();
        foreach (var profile in _profiles) profile.LowerAtCommit = new(profile.Lower);
        if (_undo.Count > 100) { var keep = _undo.Take(100).Reverse().ToList(); _undo.Clear(); foreach (var s in keep) _undo.Push(s); }
    }
    public void Undo() { if (_undo.Count == 0) return; _redo.Push(Current()); Restore(_undo.Pop()); }
    public void Redo() { if (_redo.Count == 0) return; _undo.Push(Current()); Restore(_redo.Pop()); }
    private Snapshot Current() => new(Entities.ToList(), _profiles.Select(p => p.Clone()).ToList(), _activeProfileId, _nextId, _nextProfileId);
    private void Restore(Snapshot s)
    {
        Entities = s.Entities.ToList(); _profiles = s.Profiles.Select(p => p.Clone()).ToList();
        _activeProfileId = s.ActiveId; _nextId = s.NextId; _nextProfileId = s.NextProfileId;
        Selection.RemoveWhere(id => Get(id) is null);
        _knownEntities = Entities.ToDictionary(e => e.Id);
    }

    // Refresh every pair whose geometry changed, without rebuilding other profiles
    // just because a parameter of the active one changed.
    private void Changed(IEnumerable<int>? touched = null, bool rigid = false)
    {
        var current = Entities.ToDictionary(e => e.Id);
        var changedIds = current.Where(e => !_knownEntities.TryGetValue(e.Key, out var old) || old != e.Value)
            .Select(e => e.Key).Concat(_knownEntities.Keys.Except(current.Keys)).ToHashSet();
        var edited = touched?.ToHashSet();
        int active = _activeProfileId;
        try
        {
            foreach (var profile in _profiles)
            {
                if (profile.Id != active && !profile.KnownIds.Overlaps(changedIds) &&
                    !profile.Upper.Concat(profile.Lower).Any(changedIds.Contains)) continue;
                _activeProfileId = profile.Id;
                Upper.RemoveAll(id => !current.ContainsKey(id));
                Lower.RemoveAll(id => !current.ContainsKey(id));
                Deformed = null;
                if (UvMode == UvSource.Angles && edited is not null && edited.Overlaps(_lowerAtCommit) &&
                    !(rigid && Upper.Any(edited.Contains)))
                {
                    UvMode = UvSource.Drawn;
                    Log.Add($"{profile.Name}: profilo UV modificato a mano; ora è disegnato");
                }
                if (UvMode == UvSource.Angles) RegenerateLower();
                foreach (var id in ReliefOverrides.Keys.Where(id => !Upper.Contains(id)).ToList()) ReliefOverrides.Remove(id);
                RebuildRelief();
                profile.KnownIds = Upper.Concat(Lower).ToHashSet();
            }
        }
        finally { _activeProfileId = active; }
        Selection.RemoveWhere(id => Get(id) is null);
        _knownEntities = Entities.ToDictionary(e => e.Id);
    }

    // ------------------------------------------------------------------ loading
    private void Reset()
    {
        Entities.Clear(); Selection.Clear(); HiddenLayers.Clear();
        _profiles = new() { NewProfile(1) }; _activeProfileId = 1; _nextProfileId = 2; _knownEntities.Clear();
        UvMode = UvSource.Angles; UvError = null; UvWarnings.Clear(); Deformed = null;
    }

    public void Clear() { Commit(); Reset(); Changed(); }

    public void LoadDxf(string fileName, string[] lines)
    {
        var dwg = DxfReader.Parse(lines);
        Commit();
        Reset();
        foreach (var ent in dwg.Entities)
            foreach (var el in ent.Elements) AddEntity(ent.Layer, el, ent.Z);
        FileName = fileName;
        ReadSettings(dwg.Comments);
        Log.AddRange(dwg.Warnings.Select(w => "DXF: " + w));
        Log.Add($"Aperto {fileName}: {Entities.Count} elementi su {Layers.Count()} layer");
        // a file saved by this editor (or drawn the same way): XY / UV layers are the profiles
        if (!LoadProfileSettings(dwg.Comments) && Layers.Contains("XY"))
        {
            try
            {
                SetUpperFromIds(IdsOnLayer("XY"), commit: false);
                if (Layers.Contains("UV"))
                {
                    var z = Entities.Where(e => e.Layer.Equals("UV", StringComparison.OrdinalIgnoreCase)).Select(e => e.Z).Average();
                    if (Math.Abs(z) > 1e-9) _depth = Math.Abs(z);
                    SetLowerFromIds(IdsOnLayer("UV"), commit: false);
                }
            }
            catch (Exception ex) { Log.Add("Layer XY/UV presenti ma non utilizzabili come profili: " + ex.Message); }
        }
        Changed();
    }

    public void LoadPae(string fileName, string text)
    {
        var job = PaeReader.Parse(text);
        Commit();
        Reset();
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var call = job.DeformCall;
        int sf = call is null ? job.Surfaces.Keys.DefaultIfEmpty(1).First() : (int)call.Get("SFN", 1);
        var surf = job.Surfaces.TryGetValue(sf, out var s0) ? s0 : null;
        var upperNames = new HashSet<string>(surf?.UpperNames.Select(n => n.Replace("-", "")) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var lowerNames = new HashSet<string>(surf?.LowerNames.Select(n => n.Replace("-", "")) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, el) in job.Entities)
        {
            string layer = upperNames.Contains(name) ? "XY" : lowerNames.Contains(name) ? "UV" : "PAE";
            double z = lowerNames.Contains(name) && surf is not null ? surf.ZUV : 0;
            map[name] = AddEntity(layer, el, z);
        }
        FileName = fileName;
        Log.AddRange(job.Warnings.Select(w => "PAE: " + w));
        if (call is not null) { MountDeg = call.Get("ROTB", 8); InsertRotDeg = call.Get("ROTP", 0); PivotY = call.Get("IY", 0); }
        if (surf is not null)
        {
            Upper = surf.UpperNames.Select(n => map[n.Replace("-", "")]).ToList();
            Lower = surf.LowerNames.Select(n => map[n.Replace("-", "")]).ToList();
            // orient elements as the SF lists them
            foreach (var (names, ids) in new[] { (surf.UpperNames, Upper), (surf.LowerNames, Lower) })
                for (int i = 0; i < names.Count; i++)
                    if (names[i].Contains('-')) Replace(ids[i], e => e with { Element = e.Element.Reversed() });
            _depth = Math.Abs(surf.ZUV - surf.ZXY);
            UvMode = UvSource.Drawn;
            ReadFrontCorrection(always: true);
        }
        Log.Add($"Aperto {fileName}: {Entities.Count} elementi, SF{sf} ({Upper.Count} coppie), montaggio {MountDeg:0.##}°, tagliente {InsertRotDeg:0.##}°");
        Changed();
    }

    private List<int> IdsOnLayer(string layer) => Entities.Where(e => e.Layer.Equals(layer, StringComparison.OrdinalIgnoreCase)).Select(e => e.Id).ToList();

    private int AddEntity(string layer, Element el, double z = 0)
    {
        int id = _nextId++;
        Entities.Add(new DocEntity(id, layer, Retag(el, id), z));
        return id;
    }

    private static Element Retag(Element e, int id) => e switch
    {
        LineSeg l => new LineSeg(l.P0, l.P1, $"#{id}"),
        ArcSeg a => new ArcSeg(a.Center, a.Radius, a.StartAngle, a.Sweep, $"#{id}"),
        EllipseArcSeg el => new EllipseArcSeg(el.Center, el.SemiMajor, el.SemiMinor, el.Rotation, el.StartParam, el.Sweep, $"#{id}"),
        PolylineSeg p => new PolylineSeg(p.Points, $"#{id}"),
        _ => e,
    };
    private static int IdOf(Element e) => int.Parse(e.Tag!.TrimStart('#'), Inv);

    /// <summary>Parameters this editor writes into the DXF it saves (a 999 comment, ignored by CAD programs).</summary>
    private string SettingsComment() => string.Create(Inv, $"FORMAWIRE MOUNT={MountDeg} ROTP={InsertRotDeg} IY={PivotY} DEPTH={Depth} SIDE={SideClearanceDeg} FRONT={FrontCorrectionDeg} NORMAL={(ReliefNormal ? 1 : 0)}");

    private void ReadSettings(IEnumerable<string> comments)
    {
        // Keep settings from drawings saved before the app was renamed.
        foreach (var c in comments.Where(c => c.StartsWith("FORMAWIRE ", StringComparison.OrdinalIgnoreCase)
            || c.StartsWith("EDMDEFORM ", StringComparison.OrdinalIgnoreCase)))
            foreach (var kv in c.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(t => t.Split('=')))
            {
                if (kv.Length != 2 || !double.TryParse(kv[1], NumberStyles.Float, Inv, out var v)) continue;
                switch (kv[0].ToUpperInvariant())
                {
                    case "MOUNT": MountDeg = v; break;
                    case "ROTP": InsertRotDeg = v; break;
                    case "IY": PivotY = v; break;
                    case "DEPTH": if (v > 0) _depth = v; break;
                    case "SIDE": _side = v; break;
                    case "FRONT": _front = v; break;
                    case "NORMAL": _normal = v != 0; break;
                }
            }
    }

    /// <summary>
    /// A drawn UV profile carries the front correction as a common Y offset (BA1408: +5·tan 2° = −2°). Show it
    /// in the front-correction field (a picked profile without offset keeps the value already entered).
    /// </summary>
    private void ReadFrontCorrection(bool always)
    {
        var up = UpperProfile(); var lo = LowerProfile();
        if (up is null || lo is null || up.Count != lo.Count) return;
        double g = ReliefPlanner.CommonYOffset(up, lo, Material(up));
        if (Math.Abs(g) < 1e-9 && !always) return;
        _front = Math.Round(ReliefPlanner.FrontCorrectionOf(g, Depth), 9);
        if (Math.Abs(g) > 1e-9) Log.Add($"Correzione frontale letta dai profili: UV spostato di {g:+0.####;-0.####} mm in Y = {_front:0.###}°");
    }

    // ------------------------------------------------------------------ selection
    public void Select(IEnumerable<int> ids, bool additive = false, bool toggle = false)
    {
        if (!additive && !toggle) Selection.Clear();
        foreach (var id in ids)
        {
            if (toggle && Selection.Contains(id)) Selection.Remove(id); else Selection.Add(id);
        }
    }
    public void SelectAll() { Selection.Clear(); foreach (var e in Visible) Selection.Add(e.Id); }
    public void InvertSelection() { var inv = Visible.Select(e => e.Id).Except(Selection).ToList(); Selection.Clear(); foreach (var id in inv) Selection.Add(id); }
    public void SelectLayer(string layer) { Selection.Clear(); foreach (var id in IdsOnLayer(layer)) Selection.Add(id); }

    /// <summary>Select the whole chain connected to the given entity (stops at branches).</summary>
    public void SelectChain(int seedId, bool additive = false)
    {
        var seed = Get(seedId) ?? throw new KeyNotFoundException();
        var owner = ProfileContaining(seedId);
        var ownChain = owner is null ? null : owner.Upper.Contains(seedId) ? owner.Upper : owner.Lower;
        var pool = Visible.Where(e => ownChain is null ? ProfileContaining(e.Id) is null : ownChain.Contains(e.Id)).Select(e => e.Element).ToList();
        var warnings = new List<string>();
        var chain = ProfileOps.PickChain(pool, seed.Element, ChainTolerance, warnings);
        if (!additive) Selection.Clear();
        foreach (var el in chain.Elements) Selection.Add(IdOf(el));
        Log.AddRange(warnings);
    }

    // ------------------------------------------------------------------ editing
    private void Replace(int id, Func<DocEntity, DocEntity> f)
    {
        int i = Entities.FindIndex(e => e.Id == id);
        if (i >= 0) Entities[i] = f(Entities[i]);
    }
    private void SetElement(int id, Element el) => Replace(id, e => e with { Element = el });

    public void DeleteSelected()
    {
        if (Selection.Count == 0) return;
        var ids = Selection.ToList();
        Commit();
        Entities.RemoveAll(e => Selection.Contains(e.Id));
        Upper.RemoveAll(Selection.Contains); Lower.RemoveAll(Selection.Contains);
        Log.Add($"Cancellati {ids.Count} elementi");
        Selection.Clear(); Changed(ids);
    }

    public void IsolateSelected()
    {
        if (Selection.Count == 0) return;
        Commit();
        int before = Entities.Count;
        var removed = Entities.Where(e => !Selection.Contains(e.Id)).Select(e => e.Id).ToList();
        // the UV profile follows XY when it is built from angles: keep it
        foreach (var profile in _profiles.Where(p => p.UvMode == UvSource.Angles)) removed.RemoveAll(profile.Lower.Contains);
        var gone = new HashSet<int>(removed);
        Entities.RemoveAll(e => gone.Contains(e.Id));
        Upper.RemoveAll(gone.Contains); Lower.RemoveAll(gone.Contains);
        Log.Add($"Isolati {Entities.Count} elementi (tolti {before - Entities.Count})");
        Changed(removed);
    }

    public void MoveSelected(Vec2 d) => Move(Selection.ToList(), d, $"Spostati {Selection.Count} elementi di {d}");

    /// <summary>
    /// Move the selected elements rigidly and stretch the unselected straight neighbours so the
    /// chain stays connected (how a drafter shifts a run of a profile and gets the tapered connectors).
    /// </summary>
    public void MoveSelectedConnected(Vec2 d)
    {
        var ids = Selection.ToList();
        if (ids.Count == 0 || d.AlmostEquals(Vec2.Zero, 1e-12)) return;
        var sel = new HashSet<int>(ids);
        // neighbours: visible, not selected, and in the same profile chain when the selection is part of one
        IEnumerable<DocEntity> pool = Visible.Where(o => !sel.Contains(o.Id));
        var ownChain = _profiles.SelectMany(p => new[] { p.Upper, p.Lower }).FirstOrDefault(chain => ids.All(chain.Contains));
        if (ownChain is not null) pool = pool.Where(o => ownChain.Contains(o.Id));
        var neighbours = pool.ToList();
        var moves = new List<(int id, bool start, Vec2 to)>();
        foreach (var e in Entities.Where(e => sel.Contains(e.Id)))
            foreach (var pt in new[] { e.Element.Start, e.Element.End })
                foreach (var o in neighbours)
                {
                    bool atStart = o.Element.Start.AlmostEquals(pt, ChainTolerance), atEnd = o.Element.End.AlmostEquals(pt, ChainTolerance);
                    if (!atStart && !atEnd) continue;
                    if (o.Element is not LineSeg)
                        throw new InvalidOperationException($"L'elemento {o.Id} ({KindName(o.Element)}) tocca la selezione in {pt} e non è una linea: includilo nella selezione oppure usa una linea come vicino.");
                    moves.Add((o.Id, atStart, pt + d));
                }
        Commit();
        foreach (var id in ids) SetElement(id, ProfileOps.Translate(Get(id)!.Element, d));
        foreach (var (id, start, to) in moves) SetElement(id, Curves.WithEnd((LineSeg)Get(id)!.Element, start, to));
        Log.Add($"Spostati {ids.Count} elementi di {d}, {moves.Count} estremi vicini seguono");
        Changed(ids.Concat(moves.Select(m => m.id)));
    }

    /// <summary>Move one vertex; every visible line ending there follows. Arcs are not stretched.</summary>
    public void MoveVertex(Vec2 from, Vec2 to)
    {
        var touching = Visible.Where(e => e.Element.Start.AlmostEquals(from, ChainTolerance) || e.Element.End.AlmostEquals(from, ChainTolerance)).ToList();
        if (touching.Count == 0) throw new InvalidOperationException($"Nessun estremo di elemento in {from}");
        var arcs = touching.Where(e => e.Element is not LineSeg).ToList();
        if (arcs.Count > 0)
            throw new InvalidOperationException($"Il vertice {from} appartiene a {KindName(arcs[0].Element)} {arcs[0].Id}: un arco non si stira — spostalo con i suoi vicini (selezionali, Sposta con 'mantieni collegati').");
        Commit();
        foreach (var e in touching)
        {
            var l = (LineSeg)e.Element;
            bool s = l.P0.AlmostEquals(from, ChainTolerance), t = l.P1.AlmostEquals(from, ChainTolerance);
            SetElement(e.Id, new LineSeg(s ? to : l.P0, t ? to : l.P1, l.Tag));
        }
        Log.Add($"Vertice {from} → {to} ({touching.Count} linee)");
        Changed(touching.Select(e => e.Id));
    }

    /// <summary>Create the UV profile as a copy of the XY profile, to be edited by hand.</summary>
    public void CopyUpperToLower()
    {
        var upper = UpperProfile() ?? throw new InvalidOperationException("Prima imposta il profilo XY");
        Commit();
        Entities.RemoveAll(e => Lower.Contains(e.Id));
        Lower = upper.Elements.Select(el => AddEntity(ActiveProfile.UvLayer, el, -Depth)).ToList();
        UvMode = UvSource.Drawn;
        Selection.Clear(); foreach (var id in Lower) Selection.Add(id);
        Log.Add($"Profilo UV creato come copia di XY ({Lower.Count} elementi): nascondi XY, poi sposta i tratti (mantieni collegati) o trascina i vertici");
        Changed();
    }

    /// <summary>Index (1-based) of an entity in the XY or UV chain, for display.</summary>
    public string ChainPosition(int id)
    {
        int i = Upper.IndexOf(id); if (i >= 0) return $"XY {i + 1}";
        i = Lower.IndexOf(id); if (i >= 0) return $"UV {i + 1}";
        return "";
    }
    public void MoveAll(Vec2 d) => Move(Entities.Select(e => e.Id).ToList(), d, $"Spostato tutto di {d}");

    private void Move(List<int> ids, Vec2 d, string msg)
    {
        if (ids.Count == 0 || d.AlmostEquals(Vec2.Zero, 1e-12)) return;
        Commit();
        foreach (var id in ids) SetElement(id, ProfileOps.Translate(Get(id)!.Element, d));
        Log.Add(msg);
        // moving XY and UV together is not a hand edit of the UV profile
        Changed(ids, rigid: true);
    }

    /// <summary>Translate the whole drawing so that <paramref name="p"/> becomes the origin.</summary>
    public void PointToOrigin(Vec2 p) => MoveAll(-p);

    /// <summary>
    /// Translate everything so the XY profile's leftmost X becomes X0 and its highest Y becomes Y0 (without a
    /// profile: the selection's, or the whole visible drawing's). Returns what was used, for the message, and
    /// whether anything moved.
    /// </summary>
    public (string What, bool Moved) ProfileToOrigin()
    {
        var (els, what) = UpperProfile() is { } up ? (up.Elements.ToList(), "il profilo XY")
            : Selection.Count > 0 ? (Selected.Select(e => e.Element).ToList(), "la selezione")
            : (Visible.Select(e => e.Element).ToList(), "il disegno");
        if (els.Count == 0) throw new InvalidOperationException("Il disegno è vuoto");
        var corner = ProfileOps.ReferenceCorner(new Profile(els));
        if (corner.AlmostEquals(Vec2.Zero, 1e-12)) return (what, false);
        MoveAll(-corner);
        return (what, true);
    }

    /// <summary>Translate everything so the selection's (or the XY profile's) leftmost X and highest Y land on the axes.</summary>
    public void CornerToOrigin()
    {
        var els = Selection.Count > 0 ? Selected.Select(e => e.Element).ToList() : UpperProfile()?.Elements;
        if (els is null || els.Count == 0) return;
        MoveAll(-ProfileOps.ReferenceCorner(new Profile(els)));
    }

    public void SetLayerOfSelected(string layer, double? z = null)
    {
        if (Selection.Count == 0) return;
        Commit();
        foreach (var id in Selection) Replace(id, e => e with { Layer = layer, Z = z ?? e.Z });
        Log.Add($"{Selection.Count} elementi spostati sul layer {layer}");
    }

    public int AddLine(Vec2 a, Vec2 b, string layer = "0")
    {
        if (a.AlmostEquals(b, 1e-9)) throw new InvalidOperationException("I due punti coincidono");
        Commit();
        int id = AddEntity(layer, new LineSeg(a, b));
        Log.Add($"Linea {a} → {b} ({a.DistanceTo(b):0.####} mm)");
        Changed();
        return id;
    }

    /// <summary>Lengthen the free end of a selected line by <paramref name="length"/> (both ends if both are free).</summary>
    public void ExtendSelectedLines(double length)
    {
        var lines = Selected.Where(e => e.Element is LineSeg).ToList();
        if (lines.Count == 0 || length == 0) return;
        Commit();
        foreach (var ent in lines)
        {
            var l = (LineSeg)ent.Element;
            bool startFree = !Entities.Any(o => o.Id != ent.Id && (o.Element.Start.AlmostEquals(l.P0, ChainTolerance) || o.Element.End.AlmostEquals(l.P0, ChainTolerance)));
            bool endFree = !Entities.Any(o => o.Id != ent.Id && (o.Element.Start.AlmostEquals(l.P1, ChainTolerance) || o.Element.End.AlmostEquals(l.P1, ChainTolerance)));
            if (!startFree && !endFree) { Log.Add($"elemento {ent.Id}: entrambi gli estremi collegati, non allungato"); continue; }
            var dir = (l.P1 - l.P0).Normalized();
            SetElement(ent.Id, new LineSeg(startFree ? l.P0 - dir * length : l.P0, endFree ? l.P1 + dir * length : l.P1, l.Tag));
        }
        Changed(lines.Select(e => e.Id));
    }

    /// <summary>Add the entry/exit lines to the XY profile (the exit line may go further: <paramref name="beyondOut"/>).</summary>
    public void AddLeads(double beyond, double angleDeg, bool leadIn, bool leadOut, double? beyondOut = null)
    {
        var prof = UpperProfile() ?? throw new InvalidOperationException("Prima imposta il profilo XY");
        var withLeads = ProfileOps.AddLeads(prof, beyond, angleDeg, leadIn, leadOut, beyondOut);
        Commit();
        var ids = new List<int>();
        foreach (var el in withLeads.Elements)
            ids.Add(el.Tag is { } t && t.StartsWith('#') ? IdOf(el) : AddEntity(ActiveProfile.XyLayer, el));
        Upper = ids;
        if (UvMode == UvSource.Drawn) Lower.Clear();
        Log.Add($"Linee di entrata/uscita aggiunte ({angleDeg:0.#}°, {beyond} / {beyondOut ?? beyond} mm oltre il profilo)");
        Changed();
    }

    public void ReverseUpper()
    {
        if (Upper.Count == 0) return;
        Commit();
        Upper.Reverse(); foreach (var id in Upper) SetElement(id, Get(id)!.Element.Reversed());
        if (Lower.Count > 0) { Lower.Reverse(); foreach (var id in Lower) SetElement(id, Get(id)!.Element.Reversed()); }
        Changed();
    }

    // ------------------------------------------------------------------ profiles
    public Profile? UpperProfile() => Upper.Count == 0 ? null : new Profile(Upper.Select(id => Get(id)!.Element), "XY");
    public Profile? LowerProfile() => Lower.Count == 0 ? null : new Profile(Lower.Select(id => Get(id)!.Element), "UV");

    /// <summary>First break in a profile chain (index of the element after the gap, and where), or null.</summary>
    public (int Index, Vec2 At, double Gap)? ChainGap(IReadOnlyList<int> chain)
    {
        for (int i = 1; i < chain.Count; i++)
        {
            var a = Get(chain[i - 1])!.Element.End; var b = Get(chain[i])!.Element.Start;
            if (!a.AlmostEquals(b, ChainTolerance)) return (i + 1, a, a.DistanceTo(b));
        }
        return null;
    }

    public void SetUpperFromSelection() => SetUpperFromIds(Selection.ToList(), commit: true);
    public void SetLowerFromSelection() => SetLowerFromIds(Selection.ToList(), commit: true);

    /// <summary>Click on the drawing: the chain through that element becomes the XY profile.</summary>
    public void PickUpper(int seedId) { SelectChain(seedId); SetUpperFromSelection(); }
    /// <summary>Click on the drawing: the chain through that element becomes the (drawn) UV profile.</summary>
    public void PickLower(int seedId) { SelectChain(seedId); SetLowerFromSelection(); }

    private void SetUpperFromIds(IEnumerable<int> ids, bool commit)
    {
        var idList = ids.ToList();
        EnsureAvailable(idList);
        if (UvMode == UvSource.Angles && idList.Count > 0 && idList.All(Lower.Contains))
            throw new InvalidOperationException("Questo è il profilo UV costruito dagli angoli: scegli il profilo XY nel disegno");
        var chain = ChainOf(idList, "XY");
        if (commit) Commit();
        foreach (var id in Upper.Except(idList)) Replace(id, e => e with { Layer = "0" });
        if (UvMode == UvSource.Angles) Entities.RemoveAll(e => Lower.Contains(e.Id));
        else foreach (var id in Lower) Replace(id, e => e with { Layer = "0" });
        Lower.Clear();
        // a new XY profile gets its UV profile from the angles straight away
        UvMode = UvSource.Angles;
        Upper = chain.Elements.Select(IdOf).ToList();
        foreach (var el in chain.Elements) Replace(IdOf(el), e => e with { Element = el, Layer = ActiveProfile.XyLayer, Z = 0 });
        Log.Add($"Profilo XY: {Upper.Count} elementi da {chain.Start} a {chain.End}");
        if (commit) Changed();
    }

    private void SetLowerFromIds(IEnumerable<int> ids, bool commit)
    {
        var upper = UpperProfile() ?? throw new InvalidOperationException("Prima imposta il profilo XY");
        var all = ids.ToList();
        EnsureAvailable(all);
        var idList = all.Where(id => !Upper.Contains(id)).ToList();
        if (all.Count > 0 && idList.Count == 0) throw new InvalidOperationException("Questo è il profilo XY: scegli il secondo profilo");
        var chain = ChainOf(idList, "UV");
        chain = ProfileChainer.AlignLowerToUpper(upper, chain);
        if (chain.Count != upper.Count)
            throw new InvalidOperationException($"Il secondo profilo ha {chain.Count} elementi, XY ne ha {upper.Count}: devono corrispondere uno a uno (usa Spezza o Raccorda per farli coincidere).");
        if (commit) Commit();
        if (UvMode == UvSource.Angles) Entities.RemoveAll(e => Lower.Contains(e.Id) && !idList.Contains(e.Id));
        else foreach (var id in Lower.Except(idList)) Replace(id, e => e with { Layer = "0" });
        Lower = chain.Elements.Select(IdOf).ToList();
        foreach (var el in chain.Elements) Replace(IdOf(el), e => e with { Element = el, Layer = ActiveProfile.UvLayer, Z = -Depth });
        UvMode = UvSource.Drawn;
        Log.Add($"Profilo UV: {Lower.Count} elementi (disegnato)");
        ReadFrontCorrection(always: !commit);
        if (commit) Changed();
    }

    private Profile ChainOf(IEnumerable<int> ids, string name)
    {
        var els = ids.Select(id => Get(id)?.Element ?? throw new KeyNotFoundException($"elemento {id}")).ToList();
        if (els.Count == 0) throw new InvalidOperationException("Nessuna selezione");
        var warnings = new List<string>();
        var chain = ProfileChainer.Chain(els, ChainTolerance, name, warnings);
        Log.AddRange(warnings);
        return chain;
    }

    // ------------------------------------------------------------------ the second profile from angles

    private int Material(Profile up) => ReliefPlanner.MaterialSide(up, FlipMaterial);

    /// <summary>The relief of every XY edge: automatic, unless set in the table.</summary>
    public List<EdgeRelief> Edges(Profile up)
    {
        int m = Material(up);
        return Enumerable.Range(0, up.Count).Select(i =>
        {
            var auto = ReliefPlanner.AutoEdge(up, i, SideClearanceDeg, m, ReliefNormal);
            if (!ReliefOverrides.TryGetValue(Upper[i], out var o)) return auto;
            // a radius set by hand no longer follows its lines: it is moved (or offset) with its own relief
            return auto.Kind == EdgeKind.Fillet
                ? new EdgeRelief(EdgeKind.Flank, o.Dir ?? auto.Dir, o.AngleDeg ?? SideClearanceDeg, auto.Inward)
                : auto with { Dir = o.Dir ?? auto.Dir, AngleDeg = o.AngleDeg ?? auto.AngleDeg };
        }).ToList();
    }

    public void RebuildRelief()
    {
        var up = UpperProfile();
        var lo = LowerProfile();
        MeasuredFrontDeg = null;
        if (up is null || ChainGap(Upper) is not null) { Relief = new(); return; }
        int m = Material(up);
        var edges = Edges(up);
        List<EdgeMeasure>? measured = null;
        if (lo is not null && lo.Count == up.Count)
        {
            double g = ReliefPlanner.CommonYOffset(up, lo, m);
            MeasuredFrontDeg = ReliefPlanner.FrontCorrectionOf(g, Depth);
            measured = ReliefPlanner.Measure(up, ProfileOps.Translate(lo, new Vec2(0, -g)), Depth, m);
        }
        Relief = Upper.Select((id, i) => new ReliefRow
        {
            Index = i + 1, Id = id, Kind = KindName(up.Elements[i]), Length = up.Elements[i].ApproxLength,
            Auto = ReliefPlanner.AutoEdge(up, i, SideClearanceDeg, m, ReliefNormal), Effective = edges[i],
            Override = ReliefOverrides.TryGetValue(id, out var o) ? o : null, Measured = measured?[i],
        }).ToList();
    }

    private void RegenerateLower()
    {
        UvError = null; UvWarnings = new();
        Entities.RemoveAll(e => Lower.Contains(e.Id));
        Lower = new List<int>();
        var up = UpperProfile();
        if (up is null) return;
        if (ChainGap(Upper) is { } gap) { UvError = $"Il profilo XY è interrotto prima dell'elemento {gap.Index} in {gap.At} (distanza {gap.Gap:0.####} mm): chiudilo (Ripara disegno, Estendi, Raccorda R0) o sceglilo di nuovo."; return; }
        try
        {
            var warnings = new List<string>();
            var lower = ProfileOps.Translate(ReliefPlanner.Generate(up, Depth, Edges(up), warnings), FrontShift);
            Lower = lower.Elements.Select(el => AddEntity(ActiveProfile.UvLayer, el, -Depth)).ToList();
            UvWarnings = warnings;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { UvError = ex.Message; }
    }

    /// <summary>Set the relief angle of one XY edge from the table: blank = automatic, a number = degrees (0 = none, negative = the other way).</summary>
    public void SetReliefOverride(int id, string? text) => SetOverride(id, o => o with { AngleDeg = EdgeOverride.ParseAngle(text) });
    /// <summary>Set the relief direction of one XY edge: null = automatic.</summary>
    public void SetReliefDirection(int id, ReliefDir? dir) => SetOverride(id, o => o with { Dir = dir });

    private void SetOverride(int id, Func<EdgeOverride, EdgeOverride> change)
    {
        var o = change(ReliefOverrides.TryGetValue(id, out var cur) ? cur : new EdgeOverride(null, null));
        Commit();
        if (o.Dir is null && o.AngleDeg is null) ReliefOverrides.Remove(id); else ReliefOverrides[id] = o;
        Changed();
    }

    public void ClearReliefOverrides() { Commit(); ReliefOverrides.Clear(); Changed(); }

    /// <summary>
    /// Give a region of the XY profile (the teeth, one side…) its relief in one step: <paramref name="flankDeg"/>
    /// goes to its flanks, <paramref name="frontDeg"/> to its front edges, <paramref name="dir"/> to both; radii
    /// keep following their lines. Null leaves that part as it is; <paramref name="reset"/> puts the region back
    /// to automatic.
    /// </summary>
    public void SetRelief(IEnumerable<int> ids, ReliefDir? dir = null, double? flankDeg = null, double? frontDeg = null, bool reset = false)
    {
        var up = UpperProfile() ?? throw new InvalidOperationException("Prima imposta il profilo XY");
        var edges = ids.Where(Upper.Contains).Distinct().ToList();
        if (edges.Count == 0) throw new InvalidOperationException("Seleziona prima degli elementi del profilo XY (cliccali o trascina una finestra sopra)");
        int m = Material(up);
        Commit();
        int n = 0;
        foreach (var id in edges)
        {
            if (reset) { n += ReliefOverrides.Remove(id) ? 1 : 0; continue; }
            var kind = ReliefPlanner.AutoEdge(up, Upper.IndexOf(id), 0, m).Kind;
            if (kind == EdgeKind.Fillet) continue;
            double? angle = kind == EdgeKind.Flank ? flankDeg : frontDeg;
            if (dir is null && angle is null) continue;
            var cur = ReliefOverrides.TryGetValue(id, out var o) ? o : new EdgeOverride(null, null);
            ReliefOverrides[id] = new EdgeOverride(dir ?? cur.Dir, angle ?? cur.AngleDeg);
            n++;
        }
        Log.Add(reset ? $"{n} elementi tornati automatici"
            : $"{n} elementi impostati:{(flankDeg is { } a ? $" fianchi {a:0.###}°" : "")}{(frontDeg is { } f ? $" frontali {f:0.###}°" : "")}{(dir is { } d ? $" {DirName(d)}" : "")}");
        Changed();
    }

    /// <summary>Build the UV profile from the angles (replaces a drawn one).</summary>
    public void UseAngles()
    {
        if (UvMode == UvSource.Angles) return;
        Commit();
        UvMode = UvSource.Angles;
        Log.Add("Profilo UV costruito dagli angoli di spoglia");
        Changed();
    }

    /// <summary>
    /// Read the angles of a drawn UV profile into the fields: its Y offset becomes the front correction, the
    /// most common flank relief the side relief, the edges that differ get their own value. Then the UV is
    /// built from them; the log says how close that comes to the drawn one.
    /// </summary>
    public void AdoptMeasuredAngles()
    {
        var up = UpperProfile() ?? throw new InvalidOperationException("Prima imposta il profilo XY");
        var drawn = LowerProfile();
        if (drawn is null || drawn.Count != up.Count) throw new InvalidOperationException("Nessun profilo UV disegnato corrispondente a XY da cui leggere gli angoli");
        int m = Material(up);
        double g = ReliefPlanner.CommonYOffset(up, drawn, m);
        var meas = ReliefPlanner.Measure(up, ProfileOps.Translate(drawn, new Vec2(0, -g)), Depth, m);
        var auto0 = ReliefPlanner.Auto(up, 0, m, ReliefNormal);
        var flanks = Enumerable.Range(0, up.Count).Where(i => auto0[i].Kind == EdgeKind.Flank && meas[i].In(auto0[i].Dir) is not null)
                               .Select(i => Math.Round(meas[i].In(auto0[i].Dir)!.Value, 3)).ToList();
        double side = flanks.Count == 0 ? SideClearanceDeg : flanks.GroupBy(a => a).OrderByDescending(x => x.Count()).First().Key;
        Commit();
        _side = side;
        _front = ReliefPlanner.FrontCorrectionOf(g, Depth);
        ReliefOverrides.Clear();
        for (int i = 0; i < up.Count; i++)
        {
            var auto = ReliefPlanner.AutoEdge(up, i, side, m, ReliefNormal);
            if (auto.Kind == EdgeKind.Fillet || meas[i].In(auto.Dir) is not { } a) continue;
            // 1e-4° ≈ 9 nm over 5 mm: drawings rounded to 6 decimals still read as the plain angle
            if (Math.Abs(a - auto.AngleDeg) > 1e-4) ReliefOverrides[Upper[i]] = new EdgeOverride(null, a);
        }
        UvMode = UvSource.Angles;
        Changed();
        var built = LowerProfile();
        double dev = built is null || built.Count != drawn.Count ? double.NaN
            : Enumerable.Range(0, drawn.Count).Max(i => Math.Max(Math.Max(built.Elements[i].Start.DistanceTo(drawn.Elements[i].Start), built.Elements[i].End.DistanceTo(drawn.Elements[i].End)), built.Elements[i].PointAt(0.5).DistanceTo(drawn.Elements[i].PointAt(0.5))));
        Log.Add(double.IsNaN(dev) ? "Angoli letti dal profilo UV disegnato, ma il profilo UV non si ricostruisce da questi: " + UvError
            : $"Angoli letti dal profilo UV disegnato: frontale {_front:0.###}°, laterale {side:0.###}°, {ReliefOverrides.Count} elementi impostati singolarmente; il profilo UV ricostruito differisce da quello disegnato di {dev * 1000:0.###} µm");
    }

    // ------------------------------------------------------------------ deformation
    private ProfilePair ValidatePair()
    {
        var upper = UpperProfile() ?? throw new InvalidOperationException("Prima imposta il profilo XY");
        if (UvError is not null) throw new InvalidOperationException(UvError);
        var lower = LowerProfile() ?? throw new InvalidOperationException("Manca il secondo profilo (UV)");
        if (lower.Count != upper.Count) throw new InvalidOperationException($"UV ha {lower.Count} elementi, XY ne ha {upper.Count}: devono corrispondere uno a uno");
        if (ChainGap(Upper) is { } g1) throw new InvalidOperationException($"Il profilo XY è interrotto prima dell'elemento {g1.Index} in {g1.At} (distanza {g1.Gap:0.####} mm)");
        if (ChainGap(Lower) is { } g2) throw new InvalidOperationException($"Il profilo UV è interrotto prima dell'elemento {g2.Index} in {g2.At} (distanza {g2.Gap:0.####} mm)");
        return new ProfilePair(upper, lower, Depth, FileName);
    }

    private DeformResult CalculateDeformation() => new Deformer(Mounting, Depth).Deform(ValidatePair());

    public DeformResult Deform()
    {
        Deformed = CalculateDeformation();
        Log.AddRange(Deformed.Warnings);
        Log.Add($"{ActiveProfile.Name}: deformato con montaggio {MountDeg:0.##}°, tagliente {InsertRotDeg:0.##}°, profondità {Depth}");
        return Deformed;
    }

    /// <summary>
    /// The profiles as DXF: the deformed ones (to cut), the undeformed ones (the XY profile and the second
    /// profile as drawn, layers XY / UV — it re-opens here and in PAC), or both (undeformed on XY_NOM / UV_NOM).
    /// </summary>
    public string ExportDxf(DxfContent what, bool flat)
    {
        double z = flat ? 0 : -Depth;
        var w = new DxfWriter { Comment = SettingsComment() };
        if (what == DxfContent.Undeformed)
        {
            var up = UpperProfile() ?? throw new InvalidOperationException("Prima imposta il profilo XY");
            if (UvError is not null) throw new InvalidOperationException(UvError);
            var lo = LowerProfile() ?? throw new InvalidOperationException("Manca il secondo profilo (UV)");
            return w.Add(up, "XY", 0, 1).Add(lo, "UV", z, 5).Build();
        }
        var d = Deformed ?? Deform();
        w.Add(d.Upper, "XY", 0, 1).Add(d.Lower, "UV", z, 5);
        if (what == DxfContent.Both) w.Add(d.Source.Upper, "XY_NOM", 0, 8).Add(d.Source.Lower, "UV_NOM", z, 8);
        return w.Build();
    }

    public string ExportDeformedDxf(bool includeNominal, bool flat) => ExportDxf(includeNominal ? DxfContent.Both : DxfContent.Deformed, flat);

    /// <summary>The current drawing (all entities, by layer) as DXF — for saving the cleaned-up work, with the parameters in a comment.</summary>
    public string ExportDrawingDxf()
    {
        var w = new DxfWriter { Comment = SettingsComment() + "\n" + ProfileComments(_profiles, savedDrawing: true) };
        var assigned = _profiles.SelectMany(p => p.Upper.Concat(p.Lower)).ToHashSet();
        var reserved = _profiles.SelectMany(p => new[] { p.XyLayer, p.UvLayer }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Entities.Where(e => !assigned.Contains(e.Id)).GroupBy(e => (e.Layer, e.Z)))
            w.Add(new Profile(group.Select(e => e.Element)), reserved.Contains(group.Key.Layer) ? group.Key.Layer + "_DRAWING" : group.Key.Layer, group.Key.Z);
        foreach (var profile in _profiles)
        {
            if (profile.Upper.Count > 0) w.Add(ProfileOf(profile.Upper), profile.XyLayer, 0, 1);
            if (profile.Lower.Count > 0) w.Add(ProfileOf(profile.Lower), profile.UvLayer, -profile.Depth, 5);
        }
        return w.Build();
    }

    public static string KindName(Element e) => e switch { LineSeg => "Linea", ArcSeg a when Curves.IsFullTurn(a) => "Cerchio", ArcSeg => "Arco", EllipseArcSeg => "Ellisse", _ => "Polilinea" };
    public static string DirName(ReliefDir d) => d switch { ReliefDir.Horizontal => "orizzontale", ReliefDir.Vertical => "verticale", _ => "perpendicolare" };
}
