using System.Globalization;
using System.Text;
using FormaWire.Geometry;

namespace FormaWire.IO;

public sealed record DxfLayerSpec(string Name, int Color);

/// <summary>
/// Minimal, dependency-free DXF R2000 (AC1015) writer: LINE, ARC, ELLIPSE, LWPOLYLINE.
/// Profiles are written per layer; an optional Z elevation puts a profile on another plane.
/// </summary>
public sealed class DxfWriter
{
    private readonly StringBuilder _sb = new();
    private readonly List<(Profile profile, string layer, double z)> _profiles = new();
    private readonly Dictionary<string, DxfLayerSpec> _layers = new(StringComparer.OrdinalIgnoreCase);
    private int _handle = 0x100;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Written as a 999 comment at the top of the file (CAD programs ignore it).</summary>
    public string? Comment { get; init; }

    public DxfWriter AddLayer(string name, int color)
    {
        _layers[name] = new DxfLayerSpec(name, color);
        return this;
    }

    public DxfWriter Add(Profile profile, string layer, double z = 0, int color = 7)
    {
        if (!_layers.ContainsKey(layer)) AddLayer(layer, color);
        _profiles.Add((profile, layer, z));
        return this;
    }

    public void Save(string path) => File.WriteAllText(path, Build(), new UTF8Encoding(false));

    public string Build()
    {
        _sb.Clear();
        var (min, max) = Extents();

        if (Comment is not null) foreach (var line in Comment.Split('\n')) T(999, line.TrimEnd('\r'));

        // ---------------- HEADER
        T(0, "SECTION"); T(2, "HEADER");
        T(9, "$ACADVER"); T(1, "AC1015");
        T(9, "$HANDSEED"); T(5, "FFFF");
        T(9, "$INSUNITS"); T(70, "4");
        T(9, "$EXTMIN"); P(10, min.X, min.Y, 0);
        T(9, "$EXTMAX"); P(10, max.X, max.Y, 0);
        T(9, "$LTSCALE"); T(40, "1.0");
        T(9, "$DIMSTYLE"); T(2, "Standard");
        T(9, "$TEXTSTYLE"); T(7, "Standard");
        T(9, "$CLAYER"); T(8, "0");
        T(9, "$CELTYPE"); T(6, "ByLayer");
        T(9, "$CECOLOR"); T(62, "256");
        T(0, "ENDSEC");

        // ---------------- CLASSES
        T(0, "SECTION"); T(2, "CLASSES"); T(0, "ENDSEC");

        // ---------------- TABLES
        T(0, "SECTION"); T(2, "TABLES");

        // VPORT
        T(0, "TABLE"); T(2, "VPORT"); T(5, "8"); T(330, "0"); T(100, "AcDbSymbolTable"); T(70, "1");
        T(0, "VPORT"); T(5, "2E"); T(330, "8"); T(100, "AcDbSymbolTableRecord"); T(100, "AcDbViewportTableRecord");
        T(2, "*ACTIVE"); T(70, "0");
        P(10, 0, 0); P(11, 1, 1);
        P(12, (min.X + max.X) / 2, (min.Y + max.Y) / 2);
        P(13, 0, 0); P(14, 10, 10); P(15, 10, 10); P(16, 0, 0, 1); P(17, 0, 0, 0);
        D(40, Math.Max(max.Y - min.Y, 1) * 1.2); D(41, 1.5); D(42, 50); D(43, 0); D(44, 0); D(50, 0); D(51, 0);
        T(71, "0"); T(72, "100"); T(73, "1"); T(74, "3"); T(75, "0"); T(76, "1"); T(77, "0"); T(78, "0");
        T(281, "0"); T(65, "1"); P(110, 0, 0, 0); P(111, 1, 0, 0); P(112, 0, 1, 0); T(79, "0"); D(146, 0);
        T(0, "ENDTAB");

        // LTYPE
        T(0, "TABLE"); T(2, "LTYPE"); T(5, "5"); T(330, "0"); T(100, "AcDbSymbolTable"); T(70, "3");
        foreach (var (h, name, desc) in new[] { ("14", "ByBlock", ""), ("15", "ByLayer", ""), ("16", "Continuous", "Solid line") })
        {
            T(0, "LTYPE"); T(5, h); T(330, "5"); T(100, "AcDbSymbolTableRecord"); T(100, "AcDbLinetypeTableRecord");
            T(2, name); T(70, "0"); T(3, desc); T(72, "65"); T(73, "0"); D(40, 0);
        }
        T(0, "ENDTAB");

        // LAYER
        var layers = new List<DxfLayerSpec> { new("0", 7) };
        layers.AddRange(_layers.Values.Where(l => l.Name != "0"));
        T(0, "TABLE"); T(2, "LAYER"); T(5, "2"); T(330, "0"); T(100, "AcDbSymbolTable"); T(70, layers.Count.ToString(Inv));
        foreach (var l in layers)
        {
            T(0, "LAYER"); T(5, H()); T(330, "2"); T(100, "AcDbSymbolTableRecord"); T(100, "AcDbLayerTableRecord");
            T(2, l.Name); T(70, "0"); T(62, l.Color.ToString(Inv)); T(6, "Continuous"); T(370, "-3"); T(390, "F");
        }
        T(0, "ENDTAB");

        // STYLE
        T(0, "TABLE"); T(2, "STYLE"); T(5, "3"); T(330, "0"); T(100, "AcDbSymbolTable"); T(70, "1");
        T(0, "STYLE"); T(5, "11"); T(330, "3"); T(100, "AcDbSymbolTableRecord"); T(100, "AcDbTextStyleTableRecord");
        T(2, "Standard"); T(70, "0"); D(40, 0); D(41, 1); D(50, 0); T(71, "0"); D(42, 2.5); T(3, "txt"); T(4, "");
        T(0, "ENDTAB");

        // VIEW, UCS, APPID
        T(0, "TABLE"); T(2, "VIEW"); T(5, "6"); T(330, "0"); T(100, "AcDbSymbolTable"); T(70, "0"); T(0, "ENDTAB");
        T(0, "TABLE"); T(2, "UCS"); T(5, "7"); T(330, "0"); T(100, "AcDbSymbolTable"); T(70, "0"); T(0, "ENDTAB");
        T(0, "TABLE"); T(2, "APPID"); T(5, "9"); T(330, "0"); T(100, "AcDbSymbolTable"); T(70, "1");
        T(0, "APPID"); T(5, "12"); T(330, "9"); T(100, "AcDbSymbolTableRecord"); T(100, "AcDbRegAppTableRecord"); T(2, "ACAD"); T(70, "0");
        T(0, "ENDTAB");

        // DIMSTYLE
        T(0, "TABLE"); T(2, "DIMSTYLE"); T(5, "A"); T(330, "0"); T(100, "AcDbSymbolTable"); T(70, "1"); T(100, "AcDbDimStyleTable");
        T(0, "DIMSTYLE"); T(105, "27"); T(330, "A"); T(100, "AcDbSymbolTableRecord"); T(100, "AcDbDimStyleTableRecord");
        T(2, "Standard"); T(70, "0"); T(340, "11");
        T(0, "ENDTAB");

        // BLOCK_RECORD
        T(0, "TABLE"); T(2, "BLOCK_RECORD"); T(5, "1"); T(330, "0"); T(100, "AcDbSymbolTable"); T(70, "2");
        T(0, "BLOCK_RECORD"); T(5, "1F"); T(330, "1"); T(100, "AcDbSymbolTableRecord"); T(100, "AcDbBlockTableRecord"); T(2, "*Model_Space"); T(340, "0");
        T(0, "BLOCK_RECORD"); T(5, "1B"); T(330, "1"); T(100, "AcDbSymbolTableRecord"); T(100, "AcDbBlockTableRecord"); T(2, "*Paper_Space"); T(340, "0");
        T(0, "ENDTAB");
        T(0, "ENDSEC");

        // ---------------- BLOCKS
        T(0, "SECTION"); T(2, "BLOCKS");
        T(0, "BLOCK"); T(5, "20"); T(330, "1F"); T(100, "AcDbEntity"); T(8, "0"); T(100, "AcDbBlockBegin");
        T(2, "*Model_Space"); T(70, "0"); P(10, 0, 0, 0); T(3, "*Model_Space"); T(1, "");
        T(0, "ENDBLK"); T(5, "21"); T(330, "1F"); T(100, "AcDbEntity"); T(8, "0"); T(100, "AcDbBlockEnd");
        T(0, "BLOCK"); T(5, "1C"); T(330, "1B"); T(100, "AcDbEntity"); T(67, "1"); T(8, "0"); T(100, "AcDbBlockBegin");
        T(2, "*Paper_Space"); T(70, "0"); P(10, 0, 0, 0); T(3, "*Paper_Space"); T(1, "");
        T(0, "ENDBLK"); T(5, "1D"); T(330, "1B"); T(100, "AcDbEntity"); T(67, "1"); T(8, "0"); T(100, "AcDbBlockEnd");
        T(0, "ENDSEC");

        // ---------------- ENTITIES
        T(0, "SECTION"); T(2, "ENTITIES");
        foreach (var (profile, layer, z) in _profiles)
            foreach (var e in profile.Elements) WriteElement(e, layer, z);
        T(0, "ENDSEC");

        // ---------------- OBJECTS
        T(0, "SECTION"); T(2, "OBJECTS");
        T(0, "DICTIONARY"); T(5, "C"); T(330, "0"); T(100, "AcDbDictionary"); T(281, "1"); T(3, "ACAD_GROUP"); T(350, "D");
        T(0, "DICTIONARY"); T(5, "D"); T(102, "{ACAD_REACTORS"); T(330, "C"); T(102, "}"); T(330, "C"); T(100, "AcDbDictionary"); T(281, "1");
        T(0, "ENDSEC");
        T(0, "EOF");
        return _sb.ToString();
    }

    private void WriteElement(Element e, string layer, double z)
    {
        switch (e)
        {
            case LineSeg l:
                Entity("LINE", layer); T(100, "AcDbLine");
                P(10, l.P0.X, l.P0.Y, z); P(11, l.P1.X, l.P1.Y, z);
                break;
            case ArcSeg a:
            {
                Entity("ARC", layer); T(100, "AcDbCircle");
                P(10, a.Center.X, a.Center.Y, z); D(40, a.Radius);
                T(100, "AcDbArc");
                double s = a.IsCcw ? a.StartAngle : a.EndAngle, en = a.IsCcw ? a.EndAngle : a.StartAngle;
                D(50, Deg(s)); D(51, Deg(en));
                break;
            }
            case EllipseArcSeg el:
            {
                Entity("ELLIPSE", layer); T(100, "AcDbEllipse");
                P(10, el.Center.X, el.Center.Y, z);
                var maj = el.MajorDir * el.SemiMajor;
                P(11, maj.X, maj.Y, 0);
                P(210, 0, 0, 1);
                D(40, el.Ratio);
                double s = el.IsCcw ? el.StartParam : el.EndParam, en = el.IsCcw ? el.EndParam : el.StartParam;
                s = Wrap(s); en = Wrap(en); if (en <= s) en += 2 * Math.PI;
                D(41, s); D(42, en);
                break;
            }
            case PolylineSeg pl:
                Entity("LWPOLYLINE", layer); T(100, "AcDbPolyline");
                T(90, pl.Points.Count.ToString(Inv)); T(70, "0"); D(38, z);
                foreach (var p in pl.Points) P(10, p.X, p.Y);
                break;
            default:
                throw new NotSupportedException(e.GetType().Name);
        }
    }

    private void Entity(string type, string layer)
    {
        T(0, type); T(5, H()); T(330, "1F"); T(100, "AcDbEntity"); T(8, layer);
    }

    private (Vec2 min, Vec2 max) Extents()
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var (pr, _, _) in _profiles)
            foreach (var p in pr.Sample(16))
            { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
        if (x0 == double.MaxValue) return (new Vec2(0, 0), new Vec2(10, 10));
        return (new Vec2(x0, y0), new Vec2(x1, y1));
    }

    private static double Deg(double rad) { double d = rad * 180 / Math.PI; d %= 360; if (d < 0) d += 360; return d; }
    private static double Wrap(double a) { a %= 2 * Math.PI; if (a < 0) a += 2 * Math.PI; return a; }

    private string H() => (_handle++).ToString("X", Inv);
    private void T(int code, string value) { _sb.Append(code.ToString(Inv).PadLeft(3)).Append('\n').Append(value).Append('\n'); }
    private void D(int code, double v) => T(code, v.ToString("0.############", Inv));
    private void P(int code, double x, double y) { D(code, x); D(code + 10, y); }
    private void P(int code, double x, double y, double z) { D(code, x); D(code + 10, y); D(code + 20, z); }
}
