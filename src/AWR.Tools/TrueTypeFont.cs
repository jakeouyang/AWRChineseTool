using System.Windows.Media;
namespace AWR.Tools;

// Uses the same Windows outline engine as Witcher3FontTool. This resolves quadratic,
// cubic and composite contours before rasterization, including OpenType/CFF fonts.
public sealed class TrueTypeFont : IDisposable
{
    private readonly GlyphTypeface face;
    private readonly Dictionary<int, GlyphOutline> cache = new();
    public ushort UnitsPerEm => 16384;
    public int NumGlyphs => face.GlyphCount;
    public short Ascender => checked((short)Math.Round(face.Baseline * UnitsPerEm));
    public short Descender => checked((short)-Math.Round((face.Height-face.Baseline)*UnitsPerEm));
    private TrueTypeFont(string path) => face = new GlyphTypeface(new Uri(Path.GetFullPath(path)));
    public static TrueTypeFont Load(string path) => new(path);
    public int GetGlyphIndex(int codePoint) => face.CharacterToGlyphMap.TryGetValue(codePoint, out var g) ? g : 0;
    public bool HasGlyph(int codePoint) => GetGlyphIndex(codePoint) != 0;
    public IEnumerable<int> MappedCodePoints() => face.CharacterToGlyphMap.Keys;
    public ushort GetAdvance(int glyphId) => checked((ushort)Math.Round(face.AdvanceWidths[(ushort)glyphId]*UnitsPerEm));
    public sealed class GlyphOutline
    {
        public List<(float X, float Y)> Points = new();
        public List<int> ContourEnds = new();
        public float XMin, YMin, XMax, YMax;
        public ushort Advance;
    }
    public GlyphOutline GetOutline(int glyphId)
    {
        if (cache.TryGetValue(glyphId, out var cached)) return cached;
        var o = new GlyphOutline { Advance = GetAdvance(glyphId) };
        var geometry = face.GetGlyphOutline((ushort)glyphId, UnitsPerEm, UnitsPerEm)
            .GetFlattenedPathGeometry(4, ToleranceType.Absolute);
        foreach (var figure in geometry.Figures)
        {
            o.Points.Add(((float)figure.StartPoint.X, -(float)figure.StartPoint.Y));
            foreach (var segment in figure.Segments)
                if (segment is PolyLineSegment poly)
                    foreach (var p in poly.Points) o.Points.Add(((float)p.X, -(float)p.Y));
                else if (segment is LineSegment line)
                    o.Points.Add(((float)line.Point.X, -(float)line.Point.Y));
                else throw new InvalidDataException("Unexpected unflattened glyph contour.");
            o.ContourEnds.Add(o.Points.Count-1);
        }
        if (o.Points.Count > 0)
        {
            o.XMin = o.Points.Min(p => p.X); o.XMax = o.Points.Max(p => p.X);
            o.YMin = o.Points.Min(p => p.Y); o.YMax = o.Points.Max(p => p.Y);
        }
        return cache[glyphId] = o;
    }
    public void Dispose() { }
}
