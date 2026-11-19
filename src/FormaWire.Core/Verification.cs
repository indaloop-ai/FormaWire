using FormaWire.Deformation;
using FormaWire.Geometry;
using FormaWire.IO;

namespace FormaWire;

public sealed record VerifyResult(int Points, double MaxDevUpper, double MaxDevLower, int WorstBlockUpper, int WorstBlockLower);

/// <summary>Compares a deformation result with the machine program PAC generated for the same job.</summary>
public static class Verification
{
    public static VerifyResult Compare(DeformResult result, IReadOnlyList<NcPoint> nc, int samplesPerCurve = 400)
    {
        var up = result.Upper.Sample(samplesPerCurve);
        var lo = result.Lower.Sample(samplesPerCurve);
        double wu = 0, wl = 0; int bu = 0, bl = 0;
        foreach (var p in nc)
        {
            double du = Profile.DistanceToPolyline(p.Xy, up);
            double dl = Profile.DistanceToPolyline(p.Lower, lo);
            if (du > wu) { wu = du; bu = p.Block; }
            if (dl > wl) { wl = dl; bl = p.Block; }
        }
        return new VerifyResult(nc.Count, wu, wl, bu, bl);
    }
}

/// <summary>Convenience loaders.</summary>
public static class Jobs
{
    public sealed record PaeLoad(PaeJob Job, ProfilePair Pair, Mounting Mounting, PaeCall Call);

    /// <summary>Load the deformed surface of a PAE job (the SF referenced by the sub call).</summary>
    public static PaeLoad LoadPae(string path, int? sfOverride = null, Mounting? mountingOverride = null)
    {
        var job = PaeReader.Read(path);
        var call = job.DeformCall;
        int sf = sfOverride ?? (call is null ? throw new InvalidOperationException("Il PAE non chiama la sub di deformazione: indica SF e montaggio") : (int)call.Get("SFN", 1));
        var pair = job.ToProfilePair(sf, Path.GetFileNameWithoutExtension(path));
        var mounting = mountingOverride ?? call?.ToMounting() ?? new Mounting();
        return new PaeLoad(job, pair, mounting, call ?? new PaeCall("(none)", new Dictionary<string, double>()));
    }

    /// <summary>Load upper/lower profiles from two DXF layers, chain and align them.</summary>
    public static ProfilePair LoadDxf(string path, string upperLayer, string lowerLayer, double? depth, double tol, List<string> warnings)
    {
        var dwg = DxfReader.Read(path);
        warnings.AddRange(dwg.Warnings);
        var upEls = dwg.ElementsOnLayer(upperLayer).ToList();
        var loEls = dwg.ElementsOnLayer(lowerLayer).ToList();
        if (upEls.Count == 0) throw new InvalidOperationException($"Nessuna geometria sul layer '{upperLayer}'. Layer presenti: {string.Join(", ", dwg.Layers)}");
        if (loEls.Count == 0) throw new InvalidOperationException($"Nessuna geometria sul layer '{lowerLayer}'. Layer presenti: {string.Join(", ", dwg.Layers)}");
        var upper = ProfileChainer.Chain(upEls, tol, upperLayer, warnings);
        var lower = ProfileChainer.Chain(loEls, tol, lowerLayer, warnings);
        lower = ProfileChainer.AlignLowerToUpper(upper, lower);
        double h = depth ?? Math.Abs((dwg.ElevationOfLayer(lowerLayer) ?? 0) - (dwg.ElevationOfLayer(upperLayer) ?? 0));
        if (h <= 0) throw new InvalidOperationException("Profondità zero: usa --depth, oppure disegna il profilo inferiore a Z = -profondità");
        if (upper.Count != lower.Count)
            throw new InvalidOperationException($"Il profilo superiore ha {upper.Count} elementi, quello inferiore {lower.Count}: devono corrispondere uno a uno.\n  superiore: {string.Join(", ", upper.Elements.Select(e => e.GetType().Name.Replace("Seg", "")))}\n  inferiore: {string.Join(", ", lower.Elements.Select(e => e.GetType().Name.Replace("Seg", "")))}");
        return new ProfilePair(upper, lower, h, Path.GetFileNameWithoutExtension(path));
    }
}
