using System.Text.Json;
using FormaWire.Deformation;
using FormaWire.Geometry;
using FormaWire.IO;

namespace FormaWire.Editing;

public sealed partial class EditorDocument
{
    private List<MachiningProfile> _profiles = new() { NewProfile(1) };
    private int _activeProfileId = 1, _nextProfileId = 2;
    private Dictionary<int, DocEntity> _knownEntities = new();
    public IReadOnlyList<MachiningProfile> Profiles => _profiles.AsReadOnly();
    public MachiningProfile ActiveProfile => _profiles.First(p => p.Id == _activeProfileId);
    private static MachiningProfile NewProfile(int id) => new() { Id = id, Name = $"Profilo {id}" };
    private Profile ProfileOf(IEnumerable<int> ids) => new(ids.Select(id => Get(id)!.Element));
    public MachiningProfile? ProfileContaining(int id) => _profiles.FirstOrDefault(p => p.Upper.Contains(id) || p.Lower.Contains(id));

    public void ActivateProfile(int id)
    {
        if (!_profiles.Any(p => p.Id == id)) throw new InvalidOperationException("Profilo non trovato");
        _activeProfileId = id;
        Selection.Clear();
    }

    public int AddProfile()
    {
        Commit();
        var profile = NewProfile(_nextProfileId++);
        _profiles.Add(profile);
        _activeProfileId = profile.Id;
        Selection.Clear();
        Log.Add($"{profile.Name}: scegli il suo profilo XY nel disegno");
        return profile.Id;
    }

    public void RenameProfile(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 60 || name.Any(char.IsControl))
            throw new InvalidOperationException("Scrivi un nome da 1 a 60 caratteri su una sola riga");
        if (name == ActiveProfile.Name) return;
        Commit(); ActiveProfile.Name = name;
    }

    /// <summary>Remove the setup, preserving its source drawing; generated UV is discarded.</summary>
    public void RemoveActiveProfile()
    {
        if (_profiles.Count == 1) throw new InvalidOperationException("Deve restare almeno un profilo");
        Commit();
        var profile = ActiveProfile;
        if (profile.UvMode == UvSource.Angles) Entities.RemoveAll(e => profile.Lower.Contains(e.Id));
        foreach (var id in profile.Upper.Concat(profile.Lower)) Replace(id, e => e with { Layer = "0" });
        _profiles.Remove(profile);
        _activeProfileId = _profiles[0].Id;
        Selection.Clear();
        _knownEntities = Entities.ToDictionary(e => e.Id);
        Log.Add($"Rimosso {profile.Name}; il disegno originale resta disponibile. Annulla ripristina il profilo.");
    }

    private void EnsureAvailable(IEnumerable<int> ids)
    {
        var selected = ids.ToHashSet();
        var owner = _profiles.FirstOrDefault(p => p.Id != _activeProfileId && p.Upper.Concat(p.Lower).Any(selected.Contains));
        if (owner is not null) throw new InvalidOperationException($"Gli elementi appartengono a {owner.Name}. Scegli quel profilo per modificarli, oppure usa un'altra catena.");
    }

    private T InProfile<T>(MachiningProfile profile, Func<T> action)
    {
        int active = _activeProfileId;
        try { _activeProfileId = profile.Id; return action(); }
        finally { _activeProfileId = active; }
    }

    public void DeformAll()
    {
        // Publish results only after every pair has calculated successfully.
        var results = new List<(MachiningProfile Profile, DeformResult Result)>();
        foreach (var profile in _profiles)
        {
            try { results.Add((profile, InProfile(profile, CalculateDeformation))); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { throw new InvalidOperationException($"{profile.Name}: {ex.Message}", ex); }
        }
        foreach (var (profile, result) in results)
        {
            profile.Deformed = result;
            Log.AddRange(result.Warnings.Select(w => $"{profile.Name}: {w}"));
        }
        Log.Add($"Deformati {_profiles.Count} profili con i rispettivi angoli di montaggio e spoglia");
    }

    public string ExportAllDxf(DxfContent what, bool flat)
    {
        if (_profiles.Count == 1) return ExportDxf(what, flat);
        // Validate every profile, including when only nominal geometry is requested.
        foreach (var profile in _profiles)
            try { InProfile(profile, ValidatePair); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { throw new InvalidOperationException($"{profile.Name}: {ex.Message}", ex); }
        if (what != DxfContent.Undeformed) DeformAll();
        var w = new DxfWriter { Comment = ProfileComments(_profiles, savedDrawing: false, nominal: what == DxfContent.Both, deformed: what == DxfContent.Deformed) };
        foreach (var profile in _profiles)
        {
            double z = flat ? 0 : -profile.Depth;
            if (what == DxfContent.Undeformed)
                w.Add(ProfileOf(profile.Upper), profile.XyLayer, 0, 1).Add(ProfileOf(profile.Lower), profile.UvLayer, z, 5);
            else
            {
                var result = profile.Deformed!;
                w.Add(result.Upper, profile.XyLayer, 0, 1).Add(result.Lower, profile.UvLayer, z, 5);
                if (what == DxfContent.Both)
                    w.Add(result.Source.Upper, profile.XyLayer + "_NOM", 0, 8).Add(result.Source.Lower, profile.UvLayer + "_NOM", z, 8);
            }
        }
        return w.Build();
    }

    private const string ProfileMarker = "FORMAWIRE_PROFILE ";
    private sealed class SavedProfile
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string XyLayer { get; set; } = "";
        public string UvLayer { get; set; } = "";
        public double Depth { get; set; } = 5;
        public double Side { get; set; } = 1;
        public double Front { get; set; }
        public double Mount { get; set; } = 8;
        public double Insert { get; set; } = 6;
        public double Pivot { get; set; }
        public bool Flip { get; set; }
        public bool Normal { get; set; }
        public bool Angles { get; set; }
        public int[] ReverseUpper { get; set; } = Array.Empty<int>();
        public int[] ReverseLower { get; set; } = Array.Empty<int>();
        public Dictionary<int, EdgeOverride> Overrides { get; set; } = new();
    }

    private static int ExportedCount(Element element) => element is PolylineSeg poly ? poly.Points.Count - 1 : 1;
    private static int[] ReversedIndices(IEnumerable<Element> elements)
    {
        int index = 0; var reversed = new List<int>();
        foreach (var el in elements)
        {
            if (el is ArcSeg { Sweep: < 0 } or EllipseArcSeg { Sweep: < 0 }) reversed.Add(index);
            index += ExportedCount(el);
        }
        return reversed.ToArray();
    }

    private string ProfileComments(IEnumerable<MachiningProfile> profiles, bool savedDrawing, bool nominal = false, bool deformed = false)
    {
        return string.Join("\n", profiles.Select(p =>
        {
            var overrides = new Dictionary<int, EdgeOverride>();
            int index = 0;
            foreach (var id in p.Upper)
                for (int i = 0; i < ExportedCount(Get(id)!.Element); i++, index++)
                    if (p.ReliefOverrides.TryGetValue(id, out var value)) overrides[index] = value;
            return ProfileMarker + JsonSerializer.Serialize(new SavedProfile
            {
                Id = p.Id, Name = p.Name, XyLayer = p.XyLayer + (nominal ? "_NOM" : ""), UvLayer = p.UvLayer + (nominal ? "_NOM" : ""),
                Depth = p.Depth, Side = p.SideClearanceDeg, Front = p.FrontCorrectionDeg, Mount = p.MountDeg,
                Insert = p.InsertRotDeg, Pivot = p.PivotY, Flip = p.FlipMaterial, Normal = p.ReliefNormal,
                Angles = savedDrawing && p.UvMode == UvSource.Angles, Overrides = overrides,
                ReverseUpper = ReversedIndices(deformed ? p.Deformed!.Upper.Elements : p.Upper.Select(id => Get(id)!.Element)),
                ReverseLower = ReversedIndices(deformed ? p.Deformed!.Lower.Elements : p.Lower.Select(id => Get(id)!.Element))
            });
        }));
    }

    private bool LoadProfileSettings(IEnumerable<string> comments)
    {
        var metadata = comments.Where(c => c.StartsWith(ProfileMarker, StringComparison.Ordinal)).ToList();
        if (metadata.Count == 0) return false;
        List<SavedProfile> saved;
        try
        {
            saved = metadata.Select(c => JsonSerializer.Deserialize<SavedProfile>(c[ProfileMarker.Length..])
                ?? throw new JsonException("Profilo vuoto")).ToList();
            if (saved.Any(p => p.Id < 1 || p.Id == int.MaxValue || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 60 || p.Depth <= 0 ||
                !new[] { p.Depth, p.Side, p.Front, p.Mount, p.Insert, p.Pivot }.All(double.IsFinite) ||
                string.IsNullOrEmpty(p.XyLayer) || string.IsNullOrEmpty(p.UvLayer) || p.Overrides is null || p.ReverseUpper is null || p.ReverseLower is null) ||
                saved.Select(p => p.Id).Distinct().Count() != saved.Count ||
                saved.SelectMany(p => new[] { p.XyLayer, p.UvLayer }).Distinct(StringComparer.OrdinalIgnoreCase).Count() != saved.Count * 2)
                throw new JsonException("Parametri o layer dei profili non validi");
        }
        catch (JsonException ex) { Log.Add("Impostazioni dei profili ignorate: " + ex.Message); return false; }

        _profiles = saved.Select(p => new MachiningProfile
        {
            Id = p.Id, Name = p.Name, Depth = p.Depth, SideClearanceDeg = p.Side, FrontCorrectionDeg = p.Front,
            MountDeg = p.Mount, InsertRotDeg = p.Insert, PivotY = p.Pivot, FlipMaterial = p.Flip, ReliefNormal = p.Normal,
            UvMode = p.Angles ? UvSource.Angles : UvSource.Drawn
        }).ToList();
        _activeProfileId = _profiles[0].Id;
        _nextProfileId = _profiles.Max(p => p.Id) + 1;
        // Capture membership before relabelling (Both exports also contain cut layers).
        for (int n = 0; n < saved.Count; n++)
        {
            _profiles[n].Upper = IdsOnLayer(saved[n].XyLayer);
            _profiles[n].Lower = IdsOnLayer(saved[n].UvLayer);
        }
        var assigned = _profiles.SelectMany(p => p.Upper.Concat(p.Lower)).ToHashSet();
        var reserved = _profiles.SelectMany(p => new[] { p.XyLayer, p.UvLayer }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in Entities.Where(e => !assigned.Contains(e.Id) && reserved.Contains(e.Layer)).ToList())
            Replace(entity.Id, e => e with { Layer = e.Layer + "_DRAWING" });
        for (int n = 0; n < saved.Count; n++)
        {
            var data = saved[n]; var profile = _profiles[n];
            foreach (var (ids, reversed, layer, z) in new[] {
                (profile.Upper, data.ReverseUpper, profile.XyLayer, 0.0),
                (profile.Lower, data.ReverseLower, profile.UvLayer, -profile.Depth) })
                for (int i = 0; i < ids.Count; i++)
                {
                    bool reverse = reversed.Contains(i);
                    Replace(ids[i], e => e with { Element = reverse ? e.Element.Reversed() : e.Element, Layer = layer, Z = z });
                }
            foreach (var (index, value) in data.Overrides)
                if (index >= 0 && index < profile.Upper.Count && (value.AngleDeg is null || double.IsFinite(value.AngleDeg.Value)) &&
                    (value.Dir is null || Enum.IsDefined(value.Dir.Value))) profile.ReliefOverrides[profile.Upper[index]] = value;
        }
        Log.Add($"Ripristinati {_profiles.Count} profili indipendenti");
        return true;
    }
}
