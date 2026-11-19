using FormaWire.Geometry;

namespace FormaWire.Editing;

/// <summary>What Fix drawing did.</summary>
public sealed record FixReport(int ZeroLength, int Duplicates, int EndsJoined, double LargestGap, int Merged)
{
    public override string ToString() => ZeroLength + Duplicates + EndsJoined + Merged == 0 ? "Ripara disegno: niente da riparare"
        : $"Ripara disegno: {EndsJoined} estremi uniti (distanza massima {LargestGap:0.#####} mm), {Duplicates} doppioni e {ZeroLength} elementi di lunghezza zero tolti, {Merged} linee allineate unite";
}

/// <summary>
/// The CAD commands of the editor: trim, extend, break, fillet, chamfer, stretch, rotate, mirror, scale,
/// copy, offset, arcs and circles, grip edits and drawing repair. Boundaries are the visible elements (hidden
/// layers do not cut). Profile chains are kept in order: a split element is replaced by its pieces, a fillet or
/// chamfer between two consecutive edges is inserted, mirrored edges keep their place.
/// </summary>
public sealed partial class EditorDocument
{
    private DocEntity Must(int id) => Get(id) ?? throw new KeyNotFoundException($"elemento {id}");
    private double LengthTol(Element e) => 1e-7 / Math.Max(e.ApproxLength, 1e-9);

    /// <summary>Replace an entity in the XY/UV chains by its pieces (in chain direction).</summary>
    private void ReplaceInChains(int id, IReadOnlyList<int> pieces)
    {
        foreach (var chain in _profiles.SelectMany(p => new[] { p.Upper, p.Lower }))
        {
            int i = chain.IndexOf(id);
            if (i >= 0) { chain.RemoveAt(i); chain.InsertRange(i, pieces); }
        }
    }

    /// <summary>
    /// Trim (cut): remove the part of the element between the two nearest crossings around the click.
    /// Every other visible element is a cutting edge.
    /// </summary>
    public void Trim(int id, Vec2 click)
    {
        var ent = Must(id); var e = ent.Element;
        double tol = LengthTol(e);
        bool full = Curves.IsFullTurn(e);
        var ts = new List<double>();
        foreach (var o in Visible)
            if (o.Id != id)
                foreach (var p in Curves.Intersect(e, o.Element))
                {
                    double t = Curves.ParamOf(e, p);
                    if (full) ts.Add(t % 1);
                    else if (t > tol && t < 1 - tol) ts.Add(t);
                }
        ts.Sort();
        ts = ts.Where((t, i) => i == 0 || t - ts[i - 1] > tol).ToList();
        double tc = Curves.Nearest(e, click);
        var pieces = new List<Element>();
        if (full)
        {
            if (ts.Count < 2) throw new InvalidOperationException("Per tagliare un cerchio servono due elementi che lo attraversano");
            double lo = ts.Where(t => t <= tc).DefaultIfEmpty(ts[^1] - 1).Max(), hi = ts.Where(t => t > tc).DefaultIfEmpty(ts[0] + 1).Min();
            pieces.Add(Curves.Sub(e, hi, lo + 1));
        }
        else
        {
            double lo = ts.Where(t => t < tc).DefaultIfEmpty(0).Max(), hi = ts.Where(t => t > tc).DefaultIfEmpty(1).Min();
            if (lo <= 0 && hi >= 1) throw new InvalidOperationException("Lì nessun elemento attraversa questo: niente da tagliare (per toglierlo usa Cancella)");
            if (lo > 0) pieces.Add(Curves.Sub(e, 0, lo));
            if (hi < 1) pieces.Add(Curves.Sub(e, hi, 1));
        }
        Commit();
        SetElement(id, pieces[0]);
        var ids = new List<int> { id };
        for (int i = 1; i < pieces.Count; i++) ids.Add(AddEntity(ent.Layer, pieces[i], ent.Z));
        ReplaceInChains(id, ids);
        Log.Add($"Taglio: {KindName(e)} {id}" + (ids.Count > 1 ? $" (ora in {ids.Count} pezzi)" : ""));
        Changed(ids);
    }

    /// <summary>Extend the end of a line or arc nearest to the click up to the next visible element.</summary>
    public void Extend(int id, Vec2 click)
    {
        var ent = Must(id); var e = ent.Element;
        if (e is not (LineSeg or ArcSeg) || Curves.IsFullTurn(e)) throw new InvalidOperationException("Si possono estendere solo linee e archi");
        bool atEnd = Curves.Nearest(e, click) >= 0.5;
        double tol = LengthTol(e), period = Curves.Period(e);
        double? best = null;
        foreach (var o in Visible)
            if (o.Id != id)
                foreach (var p in Curves.Intersect(e, o.Element, unboundedA: true))
                {
                    double t = Curves.ParamOf(e, p);
                    if (e is ArcSeg) { if (t <= 1 + tol || t >= period - tol) continue; if (!atEnd) t -= period; }
                    if (atEnd ? t > 1 + tol && (best is null || t < best) : t < -tol && (best is null || t > best)) best = t;
                }
        if (best is not { } tb) throw new InvalidOperationException("In quella direzione non c'è niente fino a cui estendere (scrivi una lunghezza per allungare di quella misura)");
        Commit();
        var extended = atEnd ? Curves.Sub(e, 0, tb) : Curves.Sub(e, tb, 1);
        SetElement(id, extended);
        Log.Add($"Estensione: {KindName(e)} {id} +{extended.ApproxLength - e.ApproxLength:0.####} mm");
        Changed(new[] { id });
    }

    /// <summary>Lengthen (or, negative, shorten) the end of a line or arc nearest to the click by a length along it.</summary>
    public void ExtendBy(int id, Vec2 click, double length)
    {
        var ent = Must(id); var e = ent.Element;
        if (e is not (LineSeg or ArcSeg) || Curves.IsFullTurn(e)) throw new InvalidOperationException("Si possono estendere solo linee e archi");
        if (length == 0) return;
        if (length <= -e.ApproxLength + 1e-9) throw new InvalidOperationException($"L'elemento è lungo {e.ApproxLength:0.####} mm: non si può accorciare di {-length:0.####}");
        double dt = length / e.ApproxLength;
        if (e is ArcSeg arc && Math.Abs(arc.Sweep) * (1 + dt) >= 2 * Math.PI - 1e-9) throw new InvalidOperationException("L'arco diventerebbe un cerchio intero");
        bool atEnd = Curves.Nearest(e, click) >= 0.5;
        Commit();
        SetElement(id, atEnd ? Curves.Sub(e, 0, 1 + dt) : Curves.Sub(e, -dt, 1));
        Log.Add($"Estensione: {KindName(e)} {id} {(length > 0 ? "+" : "−")}{Math.Abs(length):0.####} mm");
        Changed(new[] { id });
    }

    /// <summary>
    /// Break (split) an element at the point nearest to <paramref name="at"/>. In a drawn XY/UV pair the
    /// partner element is split at the same place along it, so the pairing stays one to one.
    /// </summary>
    public void Break(int id, Vec2 at)
    {
        var ent = Must(id); var e = ent.Element;
        double t = Curves.Nearest(e, at);
        bool full = Curves.IsFullTurn(e);
        if (!full && (t * e.ApproxLength < 1e-6 || (1 - t) * e.ApproxLength < 1e-6)) throw new InvalidOperationException("Lì c'è già un estremo");
        var owner = ProfileContaining(id);
        var upper = owner?.Upper ?? new(); var lower = owner?.Lower ?? new();
        int idx = upper.IndexOf(id) is int iu && iu >= 0 ? iu : lower.IndexOf(id);
        bool paired = upper.Count == lower.Count && idx >= 0 && (owner?.UvMode == UvSource.Drawn || lower.Contains(id));
        int partner = !paired ? -1 : upper.Contains(id) ? lower[idx] : upper[idx];
        Commit();
        if (full) { SetElement(id, Curves.Sub(e, t, t + 1)); Log.Add($"Cerchio {id} aperto in {e.PointAt(t)}"); Changed(new[] { id }); return; }
        SplitEntity(id, t);
        if (partner >= 0) SplitEntity(partner, t);
        Log.Add($"Divisione: {KindName(e)} {id} in {e.PointAt(t)}" + (partner >= 0 ? $" (e il corrispondente {partner})" : ""));
        Changed(new[] { id, partner });
    }

    private void SplitEntity(int id, double t)
    {
        var ent = Must(id);
        var (a, b) = Curves.Split(ent.Element, t);
        SetElement(id, a);
        int second = AddEntity(ent.Layer, b, ent.Z);
        ReplaceInChains(id, new[] { id, second });
        // both halves of an edge keep the relief set for it
        foreach (var profile in _profiles)
            if (profile.ReliefOverrides.TryGetValue(id, out var o)) profile.ReliefOverrides[second] = o;
    }

    /// <summary>
    /// Fillet two lines: R0 makes them meet exactly (extending or trimming both), R&gt;0 inserts a tangent
    /// arc. The clicks say which part of each line is kept. Consecutive profile edges get the arc in between.
    /// </summary>
    public void Fillet(int idA, Vec2 clickA, int idB, Vec2 clickB, double radius)
    {
        var k = Corner(idA, clickA, idB, clickB, "Raccorda");
        Vec2 ta = k.X, tb = k.X; ArcSeg? arc = null;
        if (radius > 0)
        {
            double theta = Math.Acos(Math.Clamp(k.Ua.Dot(k.Ub), -1, 1));
            if (theta < 1e-6 || theta > Math.PI - 1e-6) throw new InvalidOperationException("Le linee sono (quasi) allineate: non c'è spigolo da raccordare");
            double dist = radius / Math.Tan(theta / 2);
            if (dist > k.LenA + 1e-9 || dist > k.LenB + 1e-9) throw new InvalidOperationException($"R{radius} è troppo grande per queste linee (servono {dist:0.###} mm di ognuna)");
            ta = k.X + k.Ua * dist; tb = k.X + k.Ub * dist;
            var c = k.X + (k.Ua + k.Ub).Normalized() * (radius / Math.Sin(theta / 2));
            double a0 = (ta - c).Angle, sweep = Math.PI - theta;
            arc = new ArcSeg(c, radius, a0, (ta - c).Cross(tb - c) >= 0 ? sweep : -sweep);
        }
        CloseCorner(k, ta, tb, arc, radius > 0 ? $"Raccordo R{radius} tra {idA} e {idB}" : $"Spigolo: {idA} e {idB} si incontrano in {k.X}");
    }

    /// <summary>
    /// Chamfer two lines: the corner is cut by a straight line from <paramref name="d1"/> along the first line to
    /// <paramref name="d2"/> along the second (both 0 = the lines meet, like Fillet R0). Inside a profile the
    /// chamfer becomes an edge between the two.
    /// </summary>
    public void Chamfer(int idA, Vec2 clickA, int idB, Vec2 clickB, double d1, double d2)
    {
        if (d1 < 0 || d2 < 0) throw new InvalidOperationException("Le misure dello smusso non possono essere negative");
        var k = Corner(idA, clickA, idB, clickB, "Smusso");
        if (d1 > k.LenA + 1e-9) throw new InvalidOperationException($"La prima linea è lunga {k.LenA:0.###} mm dallo spigolo: lo smusso di {d1} non ci sta");
        if (d2 > k.LenB + 1e-9) throw new InvalidOperationException($"La seconda linea è lunga {k.LenB:0.###} mm dallo spigolo: lo smusso di {d2} non ci sta");
        Vec2 ta = k.X + k.Ua * d1, tb = k.X + k.Ub * d2;
        bool cut = !ta.AlmostEquals(tb, 1e-9);
        CloseCorner(k, ta, tb, cut ? new LineSeg(ta, tb) : null,
            cut ? $"Smusso {d1:0.####} × {d2:0.####} tra {idA} e {idB} (lato {ta.DistanceTo(tb):0.####} mm)" : $"Spigolo: {idA} e {idB} si incontrano in {k.X}");
    }

    /// <summary>Two lines meeting (when extended) at X; the kept part of each runs from X along Ua / Ub for LenA / LenB.</summary>
    private sealed record CornerInfo(DocEntity A, DocEntity B, Vec2 X, bool KeepEndA, Vec2 Ua, double LenA, bool KeepEndB, Vec2 Ub, double LenB);

    private CornerInfo Corner(int idA, Vec2 clickA, int idB, Vec2 clickB, string what)
    {
        if (idA == idB) throw new InvalidOperationException("Scegli due linee diverse");
        var entA = Must(idA); var entB = Must(idB);
        if (entA.Element is not LineSeg la || entB.Element is not LineSeg lb) throw new InvalidOperationException($"{what} funziona su due linee");
        Vec2 da = la.P1 - la.P0, db = lb.P1 - lb.P0;
        double cr = da.Cross(db);
        if (Math.Abs(cr) < 1e-9 * da.Length * db.Length) throw new InvalidOperationException("Le linee sono parallele");
        var x = la.P0 + da * ((lb.P0 - la.P0).Cross(db) / cr);
        var (keepA, ua, lenA) = KeptSide(la, x, clickA);
        var (keepB, ub, lenB) = KeptSide(lb, x, clickB);
        return new CornerInfo(entA, entB, x, keepA, ua, lenA, keepB, ub, lenB);

        // The kept part runs from the corner towards the click, so the end on the click side survives and the
        // corner replaces the other end (direction unchanged). Returns: the line's end (P1) survives, unit
        // direction and length from the corner to the surviving end.
        static (bool endSurvives, Vec2 dir, double len) KeptSide(LineSeg l, Vec2 corner, Vec2 click)
        {
            double tx = Curves.ParamOf(l, corner), tc = Curves.Nearest(l, click);
            if (Math.Abs(tc - tx) * l.ApproxLength < 1e-9) throw new InvalidOperationException("Clicca la parte di ogni linea da tenere, lontano dallo spigolo");
            var far = tc > tx ? l.P1 : l.P0;
            return (tc > tx, (far - corner).Normalized(), far.DistanceTo(corner));
        }
    }

    /// <summary>
    /// Cut / extend the two lines of a corner to ta / tb and put <paramref name="piece"/> (running ta → tb) between
    /// them; when they are consecutive edges of a profile the piece is inserted in the chain, in its direction.
    /// </summary>
    private void CloseCorner(CornerInfo k, Vec2 ta, Vec2 tb, Element? piece, string log)
    {
        var la = (LineSeg)k.A.Element; var lb = (LineSeg)k.B.Element;
        int idA = k.A.Id, idB = k.B.Id;
        if (ProfileContaining(idA) is { } ownerA && ProfileContaining(idB) is { } ownerB && ownerA.Id != ownerB.Id)
            throw new InvalidOperationException("I due elementi appartengono a profili diversi: raccorda elementi dello stesso profilo");
        Commit();
        SetElement(idA, k.KeepEndA ? new LineSeg(ta, la.P1, la.Tag) : new LineSeg(la.P0, ta, la.Tag));
        SetElement(idB, k.KeepEndB ? new LineSeg(tb, lb.P1, lb.Tag) : new LineSeg(lb.P0, tb, lb.Tag));
        var touched = new List<int> { idA, idB };
        if (piece is not null)
        {
            var chain = _profiles.SelectMany(p => new[] { p.Upper, p.Lower }).FirstOrDefault(c => c.Contains(idA) && c.Contains(idB)) ?? new List<int>();
            int ia = chain.IndexOf(idA), ib = chain.IndexOf(idB);
            bool closed = chain.Count > 2 && Get(chain[0])!.Element.Start.AlmostEquals(Get(chain[^1])!.Element.End, ChainTolerance);
            bool aFirst = ia >= 0 && ib >= 0 && (ib == ia + 1 || (closed && ia == chain.Count - 1 && ib == 0));
            bool bFirst = ia >= 0 && ib >= 0 && !aFirst && (ia == ib + 1 || (closed && ib == chain.Count - 1 && ia == 0));
            int pieceId = AddEntity(k.A.Layer, bFirst ? piece.Reversed() : piece, k.A.Z);
            if (aFirst) chain.Insert(ia + 1, pieceId);
            else if (bFirst) chain.Insert(ib + 1, pieceId);
            touched.Add(pieceId);
        }
        Log.Add(log);
        Changed(touched);
    }

    /// <summary>
    /// Stretch: every visible end point inside the window moves by d. Lines with one end inside stretch,
    /// elements with both ends inside move; arcs with only one end inside are left alone.
    /// </summary>
    public void Stretch(Vec2 min, Vec2 max, Vec2 d)
    {
        bool In(Vec2 p) => p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y;
        var changes = new List<(int id, Element el)>(); int skipped = 0;
        foreach (var ent in Visible)
        {
            var e = ent.Element; bool s = In(e.Start), t = In(e.End);
            if (e is PolylineSeg pl) { if (pl.Points.Any(In)) changes.Add((ent.Id, new PolylineSeg(pl.Points.Select(p => In(p) ? p + d : p).ToArray(), pl.Tag))); continue; }
            if (s && t) changes.Add((ent.Id, ProfileOps.Translate(e, d)));
            else if (s || t)
            {
                if (e is LineSeg l) changes.Add((ent.Id, Curves.WithEnd(l, s, (s ? l.P0 : l.P1) + d)));
                else skipped++;
            }
        }
        if (changes.Count == 0) throw new InvalidOperationException("Nessun estremo dentro la finestra di stiramento");
        Commit();
        foreach (var (id, el) in changes) SetElement(id, el);
        Log.Add($"Stirati {changes.Count} elementi di {d}" + (skipped > 0 ? $"; {skipped} archi con un solo estremo dentro lasciati come sono" : ""));
        Changed(changes.Select(c => c.id));
    }

    /// <summary>Rotate the selection about a point (degrees, counter-clockwise).</summary>
    public void RotateSelected(Vec2 center, double deg) => Rotate(Selection.ToList(), center, deg);

    private void Rotate(List<int> ids, Vec2 center, double deg)
    {
        if (ids.Count == 0 || Math.Abs(deg) < 1e-12) return;
        Commit();
        foreach (var id in ids) SetElement(id, Curves.Rotate(Get(id)!.Element, center, Mounting.Deg(deg)));
        Log.Add($"Ruotati {ids.Count} elementi di {deg:0.####}° attorno a {center}");
        Changed(ids, rigid: true);
    }

    /// <summary>What a rigid transform of these elements touches: moving XY and UV together is not a hand edit of UV.</summary>
    

    /// <summary>New element copied from <paramref name="src"/>: copies of profile elements are plain drawing (layer 0).</summary>
    private int AddCopy(DocEntity src, Element el)
    {
        bool profile = ProfileContaining(src.Id) is not null;
        return AddEntity(profile ? "0" : src.Layer, el, profile ? 0 : src.Z);
    }

    /// <summary>Copy the selection moved by d. The selection stays on the originals, so the next copy starts from them again.</summary>
    public List<int> CopySelected(Vec2 d)
    {
        if (Selection.Count == 0) throw new InvalidOperationException("Seleziona prima cosa copiare");
        if (d.AlmostEquals(Vec2.Zero, 1e-12)) throw new InvalidOperationException("La copia finirebbe sopra l'originale: scegli un'altra destinazione");
        var src = Selected.ToList();
        Commit();
        var ids = src.Select(e => AddCopy(e, ProfileOps.Translate(e.Element, d))).ToList();
        Log.Add($"Copiati {ids.Count} elementi di {d}");
        Changed();
        return ids;
    }

    /// <summary>
    /// Mirror the selection in the line through a and b. With <paramref name="keepOriginal"/> the mirrored elements
    /// are new (and become the selection); otherwise the elements themselves flip and keep their place in the profiles.
    /// </summary>
    public void MirrorSelected(Vec2 a, Vec2 b, bool keepOriginal)
    {
        if (Selection.Count == 0) throw new InvalidOperationException("Seleziona prima cosa specchiare");
        Mirror(Selection.ToList(), a, b, keepOriginal);
    }

    /// <summary>
    /// Mirror the whole drawing left ↔ right about the vertical line through the middle of the XY profile (of the
    /// drawing when there is no profile yet): a right-hand tool becomes a left-hand one, in the same place.
    /// </summary>
    public void MirrorProfile()
    {
        if (Entities.Count == 0) return;
        var els = UpperProfile()?.Elements.ToList() ?? Visible.Select(e => e.Element).ToList();
        var (mn, mx) = ProfileOps.Extents(new Profile(els));
        double x = (mn.X + mx.X) / 2;
        Mirror(Entities.Select(e => e.Id).ToList(), new Vec2(x, 0), new Vec2(x, 1), false);
    }

    private void Mirror(List<int> ids, Vec2 a, Vec2 b, bool copy)
    {
        if (a.AlmostEquals(b, 1e-9)) throw new InvalidOperationException("I due punti dell'asse coincidono");
        var mirrored = _profiles.Where(p => !copy && p.Upper.Count > 0 && p.Upper.All(ids.Contains))
            .Select(p => (Profile: p, Side: ReliefPlanner.MaterialSide(ProfileOf(p.Upper), p.FlipMaterial))).ToList();
        Commit();
        if (copy)
        {
            var made = ids.Select(id => Get(id)!).ToList().Select(e => AddCopy(e, Curves.Mirror(e.Element, a, b))).ToList();
            Selection.Clear(); foreach (var id in made) Selection.Add(id);
        }
        else foreach (var id in ids) SetElement(id, Curves.Mirror(Get(id)!.Element, a, b));
        // the material is mirrored with the profile (inward normal n → −n relative to the edge direction): an open
        // profile flipped upside down keeps its material on the mirrored side
        foreach (var (profile, before) in mirrored)
            if (ReliefPlanner.MaterialSide(ProfileOf(profile.Upper), profile.FlipMaterial) != -before) profile.FlipMaterial = !profile.FlipMaterial;
        Log.Add($"{(copy ? "Copia specchiata" : "Specchiati")}: {ids.Count} elementi, asse da {a} a {b}");
        Changed(copy ? null : ids, rigid: true);
    }

    /// <summary>Scale the selection about a point; 25.4 turns a drawing made in inches into millimetres.</summary>
    public void ScaleSelected(Vec2 center, double factor) => Scale(Selection.ToList(), center, factor, $"Scalati {Selection.Count} elementi");
    public void ScaleAll(double factor) => Scale(Entities.Select(e => e.Id).ToList(), Vec2.Zero, factor, "Scalato tutto il disegno");

    private void Scale(List<int> ids, Vec2 center, double factor, string what)
    {
        if (ids.Count == 0) return;
        if (!(factor > 0) || double.IsInfinity(factor)) throw new InvalidOperationException("Il fattore di scala deve essere maggiore di zero");
        if (Math.Abs(factor - 1) < 1e-15) return;
        Commit();
        foreach (var id in ids) SetElement(id, Curves.Scale(Get(id)!.Element, center, factor));
        Log.Add($"{what} ×{factor:0.######} attorno a {center}");
        Changed(ids, rigid: true);
    }

    /// <summary>
    /// Turn the selection (the whole drawing when nothing is selected) about the start of a line by the smallest
    /// angle that makes the line horizontal or vertical. Returns the angle in degrees.
    /// </summary>
    public double AlignToAxis(int lineId)
    {
        if (Must(lineId).Element is not LineSeg l) throw new InvalidOperationException("Allinea: clicca una linea");
        double a = (l.P1 - l.P0).Angle * 180 / Math.PI;
        double deg = Math.Round(a / 90) * 90 - a;
        if (Math.Abs(deg) < 1e-10) throw new InvalidOperationException("La linea è già orizzontale o verticale");
        Rotate(Selection.Count > 0 ? Selection.ToList() : Entities.Select(e => e.Id).ToList(), l.P0, deg);
        return deg;
    }

    /// <summary>Arc through three points: start, a point on the arc, end.</summary>
    public int AddArc(Vec2 p0, Vec2 p1, Vec2 p2, string layer = "0")
    {
        double d = 2 * (p0.X * (p1.Y - p2.Y) + p1.X * (p2.Y - p0.Y) + p2.X * (p0.Y - p1.Y));
        double size = Math.Max(p0.DistanceTo(p1), Math.Max(p1.DistanceTo(p2), p0.DistanceTo(p2)));
        if (Math.Abs(d) < 1e-9 * size * size) throw new InvalidOperationException("I tre punti sono allineati: per un tratto dritto usa Linea");
        double s0 = p0.LengthSquared, s1 = p1.LengthSquared, s2 = p2.LengthSquared;
        var c = new Vec2((s0 * (p1.Y - p2.Y) + s1 * (p2.Y - p0.Y) + s2 * (p0.Y - p1.Y)) / d,
                         (s0 * (p2.X - p1.X) + s1 * (p0.X - p2.X) + s2 * (p1.X - p0.X)) / d);
        double a0 = (p0 - c).Angle, a2 = (p2 - c).Angle;
        bool ccw = (p1 - p0).Cross(p2 - p1) > 0;
        double sweep = ccw ? ArcSeg.NormalizeSweepCcw(a2 - a0) : -ArcSeg.NormalizeSweepCcw(a0 - a2);
        var arc = new ArcSeg(c, p0.DistanceTo(c), a0, sweep);
        Commit();
        int id = AddEntity(layer, arc);
        Log.Add($"Arco da {p0} a {p2}: centro {c}, R {arc.Radius:0.####}, {Math.Abs(sweep) * 180 / Math.PI:0.##}°");
        Changed();
        return id;
    }

    public int AddCircle(Vec2 center, double radius, string layer = "0")
    {
        if (!(radius > 1e-9)) throw new InvalidOperationException("Il raggio deve essere maggiore di zero");
        Commit();
        int id = AddEntity(layer, new ArcSeg(center, radius, 0, 2 * Math.PI));
        Log.Add($"Cerchio centro {center}, R {radius:0.####}");
        Changed();
        return id;
    }

    /// <summary>
    /// Offset (parallel copy) of a line or arc at <paramref name="dist"/>, on the side of the point
    /// <paramref name="side"/>; with <paramref name="wholeChain"/> the whole chain through it. Corners are closed
    /// as a drafter does: the two offsets are extended or cut back to where they meet, or joined by an arc around
    /// the corner when they cannot meet. The new elements become the selection.
    /// </summary>
    public List<int> Offset(int id, Vec2 side, double dist, bool wholeChain)
    {
        if (!(dist > 0)) throw new InvalidOperationException("La distanza di offset deve essere maggiore di zero");
        var seed = Must(id);
        if (seed.Element is not (LineSeg or ArcSeg)) throw new InvalidOperationException("L'offset funziona su linee e archi");
        var chain = wholeChain
            ? ProfileOps.PickChain(Visible.Select(e => e.Element).ToList(), seed.Element, ChainTolerance).Elements.ToList()
            : new List<Element> { seed.Element };
        if (chain.FirstOrDefault(e => e is not (LineSeg or ArcSeg)) is { } bad)
            throw new InvalidOperationException($"La catena contiene {KindName(bad).ToLowerInvariant()} {IdOf(bad)}: l'offset funziona su linee e archi (togli la spunta 'tutta la catena')");
        // side: left or right of the clicked element, in the direction the chain runs
        var own = chain.First(e => IdOf(e) == id);
        double tc = Curves.Nearest(own, side);
        double cross = ProfileOps.Tangent(own, tc).Cross(side - own.PointAt(tc));
        if (Math.Abs(cross) < 1e-9) throw new InvalidOperationException("Clicca da un lato dell'elemento, non sopra");
        double d = Math.Sign(cross) * dist;

        int n = chain.Count;
        var offs = new Element[n];
        for (int i = 0; i < n; i++)
            offs[i] = Curves.Offset(chain[i], d) ?? throw new InvalidOperationException($"L'arco {IdOf(chain[i])} (R {((ArcSeg)chain[i]).Radius:0.####}) sparirebbe con un offset di {dist} da quella parte");
        var t0 = new double[n]; var t1 = Enumerable.Repeat(1.0, n).ToArray();
        var joins = new Element?[n];                       // arc after element i, around a corner the offsets cannot close
        bool closed = n > 1 && chain[0].Start.AlmostEquals(chain[^1].End, ChainTolerance);
        for (int i = 0; i < (closed ? n : n - 1); i++)
        {
            int j = (i + 1) % n;
            Element a = offs[i], b = offs[j];
            if (a.End.AlmostEquals(b.Start, 1e-9)) continue;           // tangent: the offsets already meet
            var corner = chain[i].End;
            var mid = (a.End + b.Start) / 2;
            var meet = Curves.IntersectUnbounded(a, b).Where(p => p.DistanceTo(mid) < 4 * dist + a.End.DistanceTo(b.Start)).OrderBy(p => p.DistanceTo(mid)).ToList();
            if (meet.Count > 0)
            {
                t1[i] = Near(a, meet[0], 1); t0[j] = Near(b, meet[0], 0);
            }
            else
            {
                // around the corner, tangent to both: clockwise for an offset to the left, counter-clockwise to the right
                double a0 = (a.End - corner).Angle, b0 = (b.Start - corner).Angle;
                joins[i] = new ArcSeg(corner, dist, a0, d > 0 ? -ArcSeg.NormalizeSweepCcw(a0 - b0) : ArcSeg.NormalizeSweepCcw(b0 - a0));
            }
        }
        var result = new List<Element>();
        for (int i = 0; i < n; i++)
        {
            if (t1[i] - t0[i] < 1e-9) throw new InvalidOperationException($"Con {dist} mm l'elemento {IdOf(chain[i])} sparirebbe: usa una distanza più piccola");
            result.Add(Curves.Sub(offs[i], t0[i], t1[i]));
            if (joins[i] is { } jn) result.Add(jn);
        }
        Commit();
        var ids = result.Select(e => AddCopy(seed, e)).ToList();
        Selection.Clear(); foreach (var x in ids) Selection.Add(x);
        Log.Add($"Offset di {dist} mm: {ids.Count} elementi");
        Changed();
        return ids;

        // parameter of a point of the (extended) element, the one nearest to t = near (arcs: before the start or past the end)
        static double Near(Element e, Vec2 p, double near)
        {
            double t = Curves.ParamOf(e, p);
            if (e is ArcSeg && Math.Abs(t - Curves.Period(e) - near) < Math.Abs(t - near)) t -= Curves.Period(e);
            return t;
        }
    }

    /// <summary>
    /// Make the closed XY profile start at its point nearest to <paramref name="at"/>: the element there is split
    /// (a drawn UV profile at the same place along the partner) and the chain turned to begin with it.
    /// </summary>
    public void SetStartPoint(Vec2 at)
    {
        var up = UpperProfile() ?? throw new InvalidOperationException("Prima imposta il profilo XY");
        if (!up.IsClosed(ChainTolerance)) throw new InvalidOperationException("Il punto di partenza si sceglie su un profilo chiuso: uno aperto parte dal suo estremo (Inverti verso per l'altro)");
        int n = Upper.Count;
        int k = Enumerable.Range(0, n).MinBy(i => Curves.Distance(up.Elements[i], at));
        var e = up.Elements[k];
        double t = Curves.Nearest(e, at);
        bool paired = UvMode == UvSource.Drawn && Lower.Count == n;
        Commit();
        int first;
        if (t * e.ApproxLength < 1e-6) first = k;
        else if ((1 - t) * e.ApproxLength < 1e-6) first = (k + 1) % n;
        else
        {
            if (paired) SplitEntity(Lower[k], t);
            SplitEntity(Upper[k], t);
            first = k + 1;
        }
        Upper = Upper.Skip(first).Concat(Upper.Take(first)).ToList();
        if (paired) Lower = Lower.Skip(first).Concat(Lower.Take(first)).ToList();
        Log.Add($"Il profilo XY ora parte da {Get(Upper[0])!.Element.Start}");
        Changed();
    }

    /// <summary>Grip edit without 'keep connected': move one end of one line.</summary>
    public void MoveEndpoint(int id, Vec2 from, Vec2 to)
    {
        var ent = Must(id);
        if (ent.Element is not LineSeg l) throw new InvalidOperationException("Si possono trascinare solo gli estremi delle linee (un arco si sposta con Sposta)");
        bool start = l.P0.DistanceTo(from) <= l.P1.DistanceTo(from);
        if ((start ? l.P1 : l.P0).AlmostEquals(to, 1e-9)) throw new InvalidOperationException("La linea avrebbe lunghezza zero");
        Commit();
        SetElement(id, Curves.WithEnd(l, start, to));
        Log.Add($"Linea {id}: estremo {from} → {to}");
        Changed(new[] { id });
    }

    /// <summary>
    /// Repair a drawing (selection, or everything visible): remove zero-length elements and duplicates
    /// (also lines lying on top of a longer line), join end points closer than <paramref name="tol"/>
    /// (lines move to arcs, arcs never move), merge collinear lines that continue each other.
    /// </summary>
    public FixReport FixDrawing(double tol)
    {
        var scope = (Selection.Count > 0 ? Selected : Visible).Select(e => e.Id).ToList();
        if (scope.Count == 0) return new FixReport(0, 0, 0, 0, 0);
        var inChain = _profiles.SelectMany(p => p.Upper.Concat(p.Lower)).ToHashSet();
        Commit();
        var removed = new HashSet<int>();

        // 1. zero length
        int zero = 0;
        foreach (var id in scope) if (Get(id)!.Element.ApproxLength < 1e-9) { removed.Add(id); zero++; }

        // 2. duplicates / lines covered by another line
        int dups = 0;
        var live = scope.Where(id => !removed.Contains(id)).Select(id => Get(id)!).ToList();
        var bounds = live.Select(e => Curves.Bounds(e.Element)).ToList();
        for (int i = 0; i < live.Count; i++)
            for (int j = 0; j < live.Count; j++)
            {
                if (i == j || removed.Contains(live[i].Id) || removed.Contains(live[j].Id)) continue;
                var (ai, aa) = bounds[i]; var (bi, ba) = bounds[j];
                if (bi.X < ai.X - tol || bi.Y < ai.Y - tol || ba.X > aa.X + tol || ba.Y > aa.Y + tol) continue;
                var a = live[i]; var b = live[j];
                if (!Covers(a.Element, b.Element, tol)) continue;
                bool same = Covers(b.Element, a.Element, tol);
                if (same && j < i) continue;                       // identical pair: handled once
                // b lies on a: drop b, unless b is a profile edge and a is not (then drop a if identical)
                if (inChain.Contains(b.Id) && !inChain.Contains(a.Id)) { if (same) { removed.Add(a.Id); dups++; } continue; }
                if (inChain.Contains(b.Id)) continue;
                removed.Add(b.Id); dups++;
            }

        // 3. join end points closer than tol (sweep on X)
        var ends = scope.Where(id => !removed.Contains(id))
            .SelectMany(id => new[] { (id, start: true, p: Get(id)!.Element.Start), (id, start: false, p: Get(id)!.Element.End) })
            .OrderBy(x => x.p.X).ToList();
        var group = Enumerable.Range(0, ends.Count).ToArray();
        int Find(int k) { while (group[k] != k) k = group[k] = group[group[k]]; return k; }
        for (int i = 0; i < ends.Count; i++)
            for (int j = i + 1; j < ends.Count && ends[j].p.X - ends[i].p.X <= tol; j++)
                if (ends[i].id != ends[j].id && ends[i].p.DistanceTo(ends[j].p) <= tol) group[Find(i)] = Find(j);
        int joined = 0; double largest = 0;
        foreach (var cluster in Enumerable.Range(0, ends.Count).GroupBy(Find).Where(g => g.Count() > 1))
        {
            var members = cluster.Select(k => ends[k]).ToList();
            var fixedPt = members.Where(m => Get(m.id)!.Element is not LineSeg).Select(m => (Vec2?)m.p).FirstOrDefault();
            var target = fixedPt ?? members.Aggregate(Vec2.Zero, (s, m) => s + m.p) / members.Count;
            foreach (var m in members)
            {
                if (Get(m.id)!.Element is not LineSeg l || m.p.AlmostEquals(target, 1e-12)) continue;
                largest = Math.Max(largest, m.p.DistanceTo(target));
                SetElement(m.id, Curves.WithEnd(l, m.start, target));
                joined++;
            }
        }
        foreach (var id in scope) if (!removed.Contains(id) && Get(id)!.Element.ApproxLength < 1e-9) { removed.Add(id); zero++; }

        // 4. merge collinear lines meeting end to end where nothing else meets (not profile edges: that would change the pairing)
        int merged = 0;
        var inScope = new HashSet<int>(scope);
        for (bool again = true; again;)
        {
            again = false;
            var at = new Dictionary<(long, long), List<int>>();
            foreach (var ent in Visible.Where(e => !removed.Contains(e.Id)))
                foreach (var p in new[] { ent.Element.Start, ent.Element.End })
                {
                    var key = ((long)Math.Round(p.X * 1e6), (long)Math.Round(p.Y * 1e6));
                    if (!at.TryGetValue(key, out var list)) at[key] = list = new List<int>();
                    if (!list.Contains(ent.Id)) list.Add(ent.Id);
                }
            foreach (var (key, ids) in at)
            {
                if (ids.Count != 2 || !ids.All(id => inScope.Contains(id) && !inChain.Contains(id))) continue;
                if (Get(ids[0])!.Element is not LineSeg la || Get(ids[1])!.Element is not LineSeg lb) continue;
                if (Math.Abs((la.P1 - la.P0).Normalized().Cross((lb.P1 - lb.P0).Normalized())) > 1e-9) continue;
                var j = new Vec2(key.Item1 / 1e6, key.Item2 / 1e6);
                bool aStartsHere = la.P0.DistanceTo(j) < la.P1.DistanceTo(j);
                var farA = aStartsHere ? la.P1 : la.P0; var farB = lb.P0.DistanceTo(j) < lb.P1.DistanceTo(j) ? lb.P1 : lb.P0;
                SetElement(ids[0], aStartsHere ? new LineSeg(farB, farA, la.Tag) : new LineSeg(farA, farB, la.Tag));
                removed.Add(ids[1]); merged++; again = true;
                break;
            }
        }

        Entities.RemoveAll(e => removed.Contains(e.Id));
        Upper.RemoveAll(removed.Contains); Lower.RemoveAll(removed.Contains);
        var report = new FixReport(zero, dups, joined, largest, merged);
        Log.Add(report.ToString());
        Changed(scope);
        return report;

        // b lies entirely on a (same kind of curve, both ends and the middle within tol)
        static bool Covers(Element a, Element b, double tol)
        {
            if (a.GetType() != b.GetType()) return false;
            if (b.ApproxLength > a.ApproxLength + tol) return false;
            return new[] { b.Start, b.End, b.PointAt(0.5) }.All(p => Curves.Distance(a, p) <= tol);
        }
    }
}
