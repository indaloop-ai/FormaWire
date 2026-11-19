using FormaWire.Deformation;

namespace FormaWire.Editing;

/// <summary>One independently configured XY/UV pair in a shared drawing.</summary>
public sealed class MachiningProfile
{
    public int Id { get; internal set; }
    public string Name { get; internal set; } = "";
    public List<int> Upper { get; internal set; } = new();
    public List<int> Lower { get; internal set; } = new();
    public double Depth { get; internal set; } = 5;
    public double SideClearanceDeg { get; internal set; } = 1;
    public double FrontCorrectionDeg { get; internal set; }
    public bool FlipMaterial { get; internal set; }
    public bool ReliefNormal { get; internal set; }
    public double MountDeg { get; internal set; } = 8;
    public double InsertRotDeg { get; internal set; } = 6;
    public double PivotY { get; internal set; }
    public UvSource UvMode { get; internal set; } = UvSource.Angles;
    public Dictionary<int, EdgeOverride> ReliefOverrides { get; internal set; } = new();
    public DeformResult? Deformed { get; internal set; }
    public string? UvError { get; internal set; }
    public List<string> UvWarnings { get; internal set; } = new();
    public List<ReliefRow> Relief { get; internal set; } = new();
    public double? MeasuredFrontDeg { get; internal set; }
    public string XyLayer => Id == 1 ? "XY" : $"XY_{Id}";
    public string UvLayer => Id == 1 ? "UV" : $"UV_{Id}";
    internal HashSet<int> LowerAtCommit = new();
    internal HashSet<int> KnownIds = new();

    internal MachiningProfile Clone()
    {
        var copy = (MachiningProfile)MemberwiseClone();
        copy.Upper = new(Upper); copy.Lower = new(Lower);
        copy.ReliefOverrides = new(ReliefOverrides); copy.Relief = new(Relief);
        copy.UvWarnings = new(UvWarnings);
        copy.LowerAtCommit = new(LowerAtCommit); copy.KnownIds = new(KnownIds);
        return copy;
    }
}
