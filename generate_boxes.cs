// Copyright 2026 Nicholas Oughtibridge
// SPDX-License-Identifier: Apache-2.0
//
// Generates finger-jointed, open-top drawer divider boxes as LightBurn (.lbrn2) files.
// Run with:  dotnet run generate_boxes.cs
//
// All dimensions in mm. Box dimensions are EXTERNAL.
using System.Globalization;
using System.Text;

const double T      = 4.0;    // material thickness
const double H      = 60.0;   // external box height
const double UNIT   = 110.0;  // base grid unit
const double FINGER = 12.0;   // target finger width (actual width is adjusted so the count is odd)
const double GAP    = 4.0;    // spacing between parts on the sheet
const double BED_MARGIN = 10.0;  // keep parts this far inside the Falcon 2 400 x 415 work area
const double BED_W  = 400.0 - 2 * BED_MARGIN;  // usable packing area
const double BED_H  = 415.0 - 2 * BED_MARGIN;
const double SHEET_OFFSET = 400 + 20; // sheet 2 parts are parked to the right of the bed

// Laser layer defaults - CALIBRATE on scrap first. (Falcon 2 22 W, 3 mm ply ballpark.)
const double CUT_SPEED  = 8;   // mm/s
const double CUT_POWER  = 100; // %
const int    CUT_PASSES = 1;
const double KERF       = 0.1; // mm, kerf offset for snug finger joints

string outDir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
Directory.CreateDirectory(Path.Combine(outDir, "preview"));

var designs = new (int a, int b)[] { (1, 1), (1, 2), (1, 3), (2, 3), (3, 3) };
foreach (var (a, b) in designs)
{
    double W = a * UNIT, L = b * UNIT;
    var parts = new List<Panel>
    {
        BasePanel(W, L),
        SidePanel(W, "A", $"Side A (front/back) {W:0}x{H:0}", fingersAtTop: true),
        SidePanel(W, "A", $"Side A (front/back) {W:0}x{H:0}", fingersAtTop: true),
        SidePanel(L, "B", $"Side B (left/right) {L:0}x{H:0}", fingersAtTop: false),
        SidePanel(L, "B", $"Side B (left/right) {L:0}x{H:0}", fingersAtTop: false),
    };
    foreach (var p in parts) p.Outline = Trace(p);

    // Pack shorter sides first so they can stand beside the base.
    var order = parts.OrderBy(p => p == parts[0] ? 0 : 1).ThenBy(p => p.W).ToList();
    var placed = Pack(order);
    int sheets = placed.Max(x => x.sheet) + 1;

    string name = $"box_{a}x{b}";
    string notes = $"Drawer divider box {a}x{b}: external {W:0} x {L:0} x {H:0} mm, {T:0} mm material, open top.\n" +
                   $"Parts: 1 x Base, 2 x Side A ({W:0} mm long), 2 x Side B ({L:0} mm long). Finger joints, no glue needed but recommended.\n" +
                   (sheets > 1 ? $"Needs {sheets} sheets: layer 'Sheet 2' parts are parked right of the bed - move them onto the bed for the second run.\n" : "All parts fit one 400 x 415 sheet with a 10 mm margin.\n") +
                   $"Layer settings ({CUT_SPEED} mm/s, {CUT_POWER}%, kerf {KERF} mm) are a starting point - test on scrap and adjust.";

    File.WriteAllText(Path.Combine(outDir, name + ".lbrn2"), Lbrn2(placed, sheets, notes), new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(outDir, "preview", name + ".svg"), Svg(placed, sheets, $"{name}  ({W:0} x {L:0} x {H:0} mm external)"), new UTF8Encoding(false));
    Console.WriteLine($"{name}: {parts.Count} parts, {sheets} sheet(s)");
}

// ---------------------------------------------------------------- geometry

// Splits [start,end] into an odd number of equal segments close to the target width.
static List<(double s0, double s1)> Segs(double start, double end, double target)
{
    double len = end - start;
    int n = (int)Math.Round(len / target);
    if (n < 1) n = 1;
    if (n % 2 == 0) n = n > 1 ? n - 1 : n + 1;
    double s = len / n;
    var r = new List<(double, double)>();
    for (int i = 0; i < n; i++) r.Add((start + i * s, start + (i + 1) * s));
    return r;
}

// Panel coordinate systems (u = along the edge, v = up/outward):
//   Base   : u = box X (0..W), v = box Y (0..L)
//   Side A : u = box X (0..W), v = box Z (0..H)   (front/back)
//   Side B : u = box Y (0..L), v = box Z (0..H)   (left/right)
// Overlap volumes are assigned to exactly one panel:
//   vertical corner joints (Z split into odd segments): even segments -> Side A, odd -> Side B.
//   base/side joints (edge split into odd segments, excluding the corner cubes): even -> side, odd -> base.
//   the eight corner cubes belong to the side that wins Z segment 0 (Side A), never to the base.
static Panel BasePanel(double W, double L)
{
    var p = new Panel { Name = $"Base {W:0}x{L:0}", Label = "Base", W = W, H = L };
    p.Notches.Add((0, 0, T, T)); p.Notches.Add((W - T, 0, W, T));
    p.Notches.Add((0, L - T, T, L)); p.Notches.Add((W - T, L - T, W, L));
    var su = Segs(T, W - T, FINGER);
    for (int i = 0; i < su.Count; i += 2)
    {
        p.Notches.Add((su[i].s0, 0, su[i].s1, T));
        p.Notches.Add((su[i].s0, L - T, su[i].s1, L));
    }
    var sv = Segs(T, L - T, FINGER);
    for (int i = 0; i < sv.Count; i += 2)
    {
        p.Notches.Add((0, sv[i].s0, T, sv[i].s1));
        p.Notches.Add((W - T, sv[i].s0, W, sv[i].s1));
    }
    return p;
}

static Panel SidePanel(double len, string label, string name, bool fingersAtTop)
{
    var p = new Panel { Name = name, Label = label, W = len, H = H };
    var su = Segs(T, len - T, FINGER);
    for (int i = 1; i < su.Count; i += 2)            // base wins odd segments
        p.Notches.Add((su[i].s0, 0, su[i].s1, T));
    var sv = Segs(0, H, FINGER);
    int first = fingersAtTop ? 1 : 0;                // Side A keeps even Z segments, Side B keeps odd
    for (int i = first; i < sv.Count; i += 2)
    {
        p.Notches.Add((0, sv[i].s0, T, sv[i].s1));
        p.Notches.Add((len - T, sv[i].s0, len, sv[i].s1));
    }
    return p;
}

// Rectangle minus edge notches -> single closed outline (grid cell occupancy + boundary chaining).
static List<(double x, double y)> Trace(Panel p)
{
    var xs = new SortedSet<double> { 0, p.W }; var ys = new SortedSet<double> { 0, p.H };
    foreach (var n in p.Notches) { xs.Add(n.x0); xs.Add(n.x1); ys.Add(n.y0); ys.Add(n.y1); }
    var X = xs.ToList(); var Y = ys.ToList();
    int nx = X.Count - 1, ny = Y.Count - 1;
    var occ = new bool[nx, ny];
    for (int i = 0; i < nx; i++)
        for (int j = 0; j < ny; j++)
        {
            double cx = (X[i] + X[i + 1]) / 2, cy = (Y[j] + Y[j + 1]) / 2;
            occ[i, j] = !p.Notches.Any(n => cx > n.x0 && cx < n.x1 && cy > n.y0 && cy < n.y1);
        }
    bool Occ(int i, int j) => i >= 0 && i < nx && j >= 0 && j < ny && occ[i, j];

    var edges = new Dictionary<(int, int), (int, int)>();
    void Add((int, int) from, (int, int) to)
    {
        if (!edges.TryAdd(from, to)) throw new Exception($"{p.Name}: outline pinches at vertex {from}");
    }
    for (int i = 0; i < nx; i++)
        for (int j = 0; j < ny; j++)
        {
            if (!occ[i, j]) continue;
            if (!Occ(i, j - 1)) Add((i, j), (i + 1, j));          // bottom edge, CCW
            if (!Occ(i + 1, j)) Add((i + 1, j), (i + 1, j + 1));  // right
            if (!Occ(i, j + 1)) Add((i + 1, j + 1), (i, j + 1));  // top
            if (!Occ(i - 1, j)) Add((i, j + 1), (i, j));          // left
        }

    var start = edges.Keys.First();
    var loop = new List<(int, int)>();
    var cur = start;
    do { loop.Add(cur); cur = edges[cur]; } while (cur != start && loop.Count <= edges.Count);
    if (loop.Count != edges.Count) throw new Exception($"{p.Name}: outline is not a single loop");

    // drop collinear points
    var pts = loop.Select(g => (x: X[g.Item1], y: Y[g.Item2])).ToList();
    var outl = new List<(double x, double y)>();
    for (int k = 0; k < pts.Count; k++)
    {
        var a = pts[(k - 1 + pts.Count) % pts.Count]; var b = pts[k]; var c = pts[(k + 1) % pts.Count];
        bool collinear = (a.x == b.x && b.x == c.x) || (a.y == b.y && b.y == c.y);
        if (!collinear) outl.Add(b);
    }
    return outl;
}

// ---------------------------------------------------------------- layout

static List<Placed> Pack(List<Panel> parts)
{
    var result = new List<Placed>();
    int sheet = 0; double x = 0, y = 0, rowH = 0; bool rowOpen = false;
    foreach (var p in parts)
    {
        var opts = new[] { (w: p.W, h: p.H, rot: false), (w: p.H, h: p.W, rot: true) };
        bool done = false;
        if (rowOpen)
            foreach (var o in opts)
                if (o.h <= rowH && x + o.w <= BED_W)
                {
                    result.Add(new Placed(p, sheet, x, y, o.rot)); x += o.w + GAP; done = true; break;
                }
        if (done) continue;

        var best = opts.Where(o => o.w <= BED_W).OrderBy(o => o.h).First();
        if (rowOpen) y += rowH + GAP;
        if (y + best.h > BED_H) { sheet++; y = 0; }
        x = 0; rowH = best.h; rowOpen = true;
        result.Add(new Placed(p, sheet, x, y, best.rot)); x += best.w + GAP;
    }
    return result;
}

static List<(double x, double y)> WorldPts(Placed pl)
{
    double ox = pl.sheet * SHEET_OFFSET + BED_MARGIN + pl.x;
    return pl.p.Outline.Select(v => pl.rot ? (ox + pl.p.H - v.y, BED_MARGIN + pl.y + v.x) : (ox + v.x, BED_MARGIN + pl.y + v.y)).ToList();
}

// ---------------------------------------------------------------- writers

static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

static string Lbrn2(List<Placed> placed, int sheets, string notes)
{
    var sb = new StringBuilder();
    sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
    sb.Append($"<LightBurnProject AppVersion=\"1.7.08\" FormatVersion=\"1\" MaterialHeight=\"{F(T)}\" MirrorX=\"False\" MirrorY=\"False\">\n");
    for (int s = 0; s < sheets; s++)
    {
        sb.Append("    <CutSetting type=\"Cut\">\n");
        sb.Append($"        <index Value=\"{s}\"/>\n");
        sb.Append($"        <name Value=\"Sheet {s + 1}\"/>\n");
        sb.Append($"        <minPower Value=\"{F(CUT_POWER)}\"/>\n");
        sb.Append($"        <maxPower Value=\"{F(CUT_POWER)}\"/>\n");
        sb.Append($"        <maxPower2 Value=\"{F(CUT_POWER)}\"/>\n");
        sb.Append($"        <speed Value=\"{F(CUT_SPEED)}\"/>\n");
        sb.Append($"        <numPasses Value=\"{CUT_PASSES}\"/>\n");
        sb.Append($"        <kerf Value=\"{F(KERF)}\"/>\n");
        sb.Append($"        <priority Value=\"{s}\"/>\n");
        sb.Append("    </CutSetting>\n");
    }
    int id = 0;
    foreach (var pl in placed)
    {
        var pts = WorldPts(pl);
        // VertID/PrimID are identifiers for this shape's vertex and primitive lists. LightBurn 2.x drops shapes
        // without them, and shapes that share an id get their vertex lists merged, so each shape needs its own.
        sb.Append($"    <Shape Type=\"Path\" CutIndex=\"{pl.sheet}\" VertID=\"{id}\" PrimID=\"{id}\">\n");
        id++;
        sb.Append("        <XForm>1 0 0 1 0 0</XForm>\n");
        // LightBurn 2.x compact path form: "Vx yc0x1c1x1" per vertex (c0x1/c1x1 = no bezier handles), LineClosed = closed polyline.
        sb.Append("        <VertList>");
        foreach (var v in pts) sb.Append($"V{F(v.x)} {F(v.y)}c0x1c1x1");
        sb.Append("</VertList>\n        <PrimList>LineClosed</PrimList>\n");
        sb.Append("    </Shape>\n");
    }
    sb.Append($"    <Notes ShowOnLoad=\"1\" Notes=\"{Esc(notes)}\"/>\n");
    sb.Append("</LightBurnProject>\n");
    return sb.ToString();
}

static string Svg(List<Placed> placed, int sheets, string title)
{
    double totalW = sheets * SHEET_OFFSET, totalH = 415;
    var sb = new StringBuilder();
    sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"-2 -14 {F(totalW + 4)} {F(totalH + 18)}\">\n");
    sb.Append($"<text x=\"0\" y=\"-4\" font-size=\"8\" font-family=\"sans-serif\">{title}</text>\n");
    sb.Append($"<g transform=\"translate(0,{F(totalH)}) scale(1,-1)\">\n");
    for (int s = 0; s < sheets; s++)
        sb.Append($"<rect x=\"{F(s * SHEET_OFFSET)}\" y=\"0\" width=\"400\" height=\"415\" fill=\"#f3f3f3\" stroke=\"#bbb\" stroke-dasharray=\"4 2\" stroke-width=\"0.5\"/>\n");
    foreach (var pl in placed)
    {
        var pts = WorldPts(pl);
        sb.Append("<polygon points=\"" + string.Join(" ", pts.Select(v => $"{F(v.x)},{F(v.y)}")) + "\" fill=\"#e2c48a\" stroke=\"#000\" stroke-width=\"0.4\"/>\n");
        double cx = pts.Average(v => v.x), cy = pts.Average(v => v.y);
        sb.Append($"<text x=\"{F(cx)}\" y=\"{F(-cy)}\" transform=\"scale(1,-1)\" font-size=\"7\" text-anchor=\"middle\" font-family=\"sans-serif\">{pl.p.Label}</text>\n");
    }
    sb.Append("</g></svg>\n");
    return sb.ToString();
}

static string Esc(string s) => s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\n", "&#10;");

class Panel
{
    public string Name = "", Label = "";
    public double W, H;
    public List<(double x0, double y0, double x1, double y1)> Notches = new();
    public List<(double x, double y)> Outline = new();
}

record Placed(Panel p, int sheet, double x, double y, bool rot);
