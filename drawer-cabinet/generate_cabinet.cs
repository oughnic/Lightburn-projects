// Copyright 2026 Nicholas Oughtibridge
// SPDX-License-Identifier: Apache-2.0
//
// Generates a cabinet of finger-jointed drawers as LightBurn (.lbrn2) files, one file per sheet of wood.
// Run with:  dotnet run generate_cabinet.cs -- <config.json> [output-folder]
//
// All dimensions in mm. Cabinet dimensions are EXTERNAL. The config format is described in README.md.
#:property PublishAot=false
using System.Globalization;
using System.Text;
using System.Text.Json;

try { Gen.Run(args); }
catch (UserError e) { Console.Error.WriteLine("Error: " + e.Message); Environment.Exit(1); }

class UserError : Exception { public UserError(string m) : base(m) { } }

// ------------------------------------------------------------------------------------------ configuration

class Config
{
    public string Name { get; set; } = "cabinet";
    public MaterialCfg Material { get; set; } = new();
    public StockCfg Stock { get; set; } = new();
    public MachineCfg Machine { get; set; } = new();
    public LaserCfg Laser { get; set; } = new();
    public CabinetCfg? Cabinet { get; set; }
    public List<RowCfg>? Rows { get; set; }
    public DrawerCfg Drawers { get; set; } = new();
    public SupportCfg Supports { get; set; } = new();
    public JointCfg Joints { get; set; } = new();
    public OutputCfg Output { get; set; } = new();
}
class MaterialCfg { public double Thickness { get; set; } = 3; }
class StockCfg { public double Width { get; set; } = 300; public double Length { get; set; } = 400; public double EdgeMargin { get; set; } = 5; }
class MachineCfg { public double BedWidth { get; set; } = 400; public double BedHeight { get; set; } = 415; public double BedMargin { get; set; } = 10; public double PartGap { get; set; } = 4; }
class LaserCfg { public double Speed { get; set; } = 8; public double Power { get; set; } = 100; public int Passes { get; set; } = 1; public double Kerf { get; set; } = 0.1; }
class CabinetCfg { public double Width { get; set; } public double Height { get; set; } public double Depth { get; set; } }
class RowCfg
{
    public JsonElement Height { get; set; }          // number (clear opening, mm) or "auto"
    public double? MinWidth { get; set; }            // overrides drawers.minWidth for this row
    public int? Count { get; set; }                  // fixed number of drawers in this row
}
class DrawerCfg
{
    public double MinWidth { get; set; }
    public double FingerHoleRadius { get; set; }
    public double ClearanceX { get; set; } = 2;      // total side-to-side play per drawer
    public double ClearanceY { get; set; } = 3;      // gap above the drawer
    public double ClearanceZ { get; set; } = 3;      // gap behind the drawer
}
class SupportCfg { public double? MaxSpan { get; set; } = 350; }
class OutputCfg { public bool MixParts { get; set; } = false; }
class JointCfg
{
    public double FingerWidth { get; set; } = 12;
    public double TabWidth { get; set; } = 20;
    public double Margin { get; set; } = 6;
    public SplitCfg Split { get; set; } = new();
}
class SplitCfg
{
    public double TabPitch { get; set; } = 70;
    public double NeckWidth { get; set; } = 6;
    public double HeadWidth { get; set; } = 12;
    public double NeckDepth { get; set; } = 3;
    public double HeadDepth { get; set; } = 4;
}

// ------------------------------------------------------------------------------------------ model

static class G
{
    public static double T = 3, Finger = 12, TabW = 20, M = 6;
    public static double NeckW = 6, HeadW = 12, NeckD = 3, HeadD = 4, Pitch = 70;
    public static double Gap = 4;
    public static double JobW, JobH, OriginX, OriginY;     // cutting area on the bed
    public static double R(double v) => Math.Round(v, 4);
    public static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

record Rect(double X0, double Y0, double X1, double Y1)
{
    public static Rect Of(double x0, double y0, double x1, double y1) => new(G.R(x0), G.R(y0), G.R(x1), G.R(y1));
    public bool Overlaps(Rect o) => X0 < o.X1 - 1e-6 && o.X0 < X1 - 1e-6 && Y0 < o.Y1 - 1e-6 && o.Y0 < Y1 - 1e-6;
}

// Maps a panel's local (u,v) rectangle plus its thickness range into cabinet/box coordinates (axes 0=X, 1=Y, 2=Z).
record Frame(int UAxis, double UOff, int VAxis, double VOff, int NAxis, double N0, double N1)
{
    public double[] ToWorld(Rect r)
    {
        var b = new double[6];
        b[UAxis] = UOff + r.X0; b[UAxis + 3] = UOff + r.X1;
        b[VAxis] = VOff + r.Y0; b[VAxis + 3] = VOff + r.Y1;
        b[NAxis] = N0; b[NAxis + 3] = N1;
        return b;
    }
}

class Panel
{
    public string Name = "", Label = "", Group = "";
    public double W, H;                                   // local bounding size
    public List<Rect> Adds = new(), Subs = new(), Keep = new();
    public List<(double cx, double cy, double r)> Holes = new();
    public Frame? Fr;
    public void Add(double x0, double y0, double x1, double y1) => Adds.Add(Rect.Of(x0, y0, x1, y1));
    public void Sub(double x0, double y0, double x1, double y1) => Subs.Add(Rect.Of(x0, y0, x1, y1));
    public bool Solid(double x, double y)
    {
        bool inside = false;
        foreach (var a in Adds) if (x > a.X0 && x < a.X1 && y > a.Y0 && y < a.Y1) { inside = true; break; }
        if (!inside) return false;
        foreach (var s in Subs) if (x > s.X0 && x < s.X1 && y > s.Y0 && y < s.Y1) return false;
        return true;
    }
}

class Piece
{
    public string Name = "", Label = "", Group = "";
    public List<List<(double x, double y)>> Loops = new();
    public double W, H;
    public Piece Clone(string name, string label) => new() { Name = name, Label = label, Group = Group, Loops = Loops, W = W, H = H };
    public double CutLength()
    {
        double t = 0;
        foreach (var l in Loops)
            for (int i = 0; i < l.Count; i++)
            {
                var a = l[i]; var b = l[(i + 1) % l.Count];
                t += Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));
            }
        return t;
    }
}

record Placed(Piece P, double X, double Y, bool Rot);

class Raster { public List<double> X = new(), Y = new(); public int Nx, Ny; public bool[,] Occ = new bool[0, 0]; }

class Plan
{
    public List<double> Sx = new(), Sy = new();                       // seam positions (vertical seams at x, horizontal at y)
    public List<List<double>> TabsV = new(), TabsH = new();           // tab centres along each seam
    public int Nc => Sx.Count + 1;
    public int Nr => Sy.Count + 1;
}

class RowLayout
{
    public int RowNo;                                                 // 1 = top row
    public double Y0, Y1, Height, Pitch;                              // clear opening between shelf surfaces
    public int N, K;                                                  // drawers, dividers
    public List<double> DividerX = new();
    public List<double> SlotX = new();                                // left edge of each drawer's slot
}

// ------------------------------------------------------------------------------------------ generator

static class Gen
{
    public static void Run(string[] args)
    {
        if (args.Length < 1 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("Usage: dotnet run generate_cabinet.cs -- <config.json> [output-folder]");
            return;
        }
        string cfgPath = Path.GetFullPath(args[0]);
        if (!File.Exists(cfgPath)) throw new UserError($"Config file not found: {cfgPath}");
        var cfg = LoadConfig(cfgPath);
        string outDir = args.Length > 1 ? Path.GetFullPath(args[1])
            : Path.Combine(Path.GetDirectoryName(cfgPath)!, Path.GetFileNameWithoutExtension(cfgPath));

        // ---- apply and validate settings
        var T = G.T = cfg.Material.Thickness;
        Need(T >= 1 && T <= 12, "material.thickness must be between 1 and 12 mm.");
        G.Finger = cfg.Joints.FingerWidth; G.TabW = cfg.Joints.TabWidth; G.M = cfg.Joints.Margin;
        var sp = cfg.Joints.Split;
        G.NeckW = sp.NeckWidth; G.HeadW = sp.HeadWidth; G.NeckD = sp.NeckDepth; G.HeadD = sp.HeadDepth; G.Pitch = sp.TabPitch;
        Need(G.Finger >= 4 && G.TabW >= 4 && G.M >= 2, "joints.fingerWidth and tabWidth must be at least 4 mm and margin at least 2 mm.");
        Need(G.HeadW > G.NeckW && G.NeckW > 0 && G.NeckD > 0 && G.HeadD > 0 && G.Pitch > 20,
             "joints.split needs headWidth > neckWidth > 0, positive depths and tabPitch > 20.");
        var st = cfg.Stock; var mc = cfg.Machine;
        G.Gap = mc.PartGap;
        G.JobW = Math.Min(st.Width - 2 * st.EdgeMargin, mc.BedWidth - 2 * mc.BedMargin - st.EdgeMargin);
        G.JobH = Math.Min(st.Length - 2 * st.EdgeMargin, mc.BedHeight - 2 * mc.BedMargin - st.EdgeMargin);
        G.OriginX = mc.BedMargin + st.EdgeMargin; G.OriginY = mc.BedMargin + st.EdgeMargin;
        Need(G.JobW > 50 && G.JobH > 50, "stock and machine settings leave no usable cutting area.");

        var cab = cfg.Cabinet ?? throw new UserError("\"cabinet\" is required: give width, height and depth.");
        double W = cab.Width, H = cab.Height, D = cab.Depth;
        Need(W >= 80 && H >= 60 && D >= 80, "cabinet width, height and depth must be given (minimums 80, 60 and 80 mm).");
        var rowCfgs = cfg.Rows;
        if (rowCfgs == null || rowCfgs.Count == 0) throw new UserError("\"rows\" is required: list at least one row, top row first.");
        var dr = cfg.Drawers;
        Need(dr.MinWidth > 0, "drawers.minWidth is required.");
        Need(dr.FingerHoleRadius > 0, "drawers.fingerHoleRadius is required.");
        Need(cfg.Laser.Speed > 0 && cfg.Laser.Power > 0 && cfg.Laser.Passes >= 1, "laser speed, power and passes must be positive.");

        // ---- row heights (listed top first)
        int n = rowCfgs.Count;
        double clearTotal = H - 2 * T - (n - 1) * T;
        var heights = new double?[n];
        double fixedSum = 0; int autos = 0;
        for (int i = 0; i < n; i++)
        {
            var e = rowCfgs[i].Height;
            if (e.ValueKind == JsonValueKind.Number) { heights[i] = e.GetDouble(); fixedSum += heights[i]!.Value; }
            else if (e.ValueKind == JsonValueKind.String && e.GetString()!.Equals("auto", StringComparison.OrdinalIgnoreCase)) autos++;
            else throw new UserError($"rows[{i}].height must be a number or \"auto\".");
        }
        Need(autos <= 1, "Only one row may have height \"auto\".");
        if (autos == 1)
        {
            double rest = clearTotal - fixedSum;
            Need(rest >= 30, $"The fixed rows leave only {rest:0.#} mm for the \"auto\" row. Increase the cabinet height or shrink a row.");
            for (int i = 0; i < n; i++) heights[i] ??= rest;
        }
        else if (Math.Abs(fixedSum - clearTotal) > 0.01)
            throw new UserError($"Row heights add up to {fixedSum:0.##} mm, but a {H:0.##} mm cabinet with {n} row(s) has {clearTotal:0.##} mm of clear height " +
                                $"({H:0.##} less top and bottom panels and {n - 1} shelf/shelves of {T:0.##} mm). Set cabinet.height to {fixedSum + (n + 1) * T:0.##}, change a row, or make one row \"auto\".");
        foreach (var h in heights) Need(h!.Value >= 30, "Every row must be at least 30 mm high.");

        // ---- rows bottom-up: shelf k occupies y in [ys[k], ys[k]+T]; k=0 is the bottom panel, k=n the top panel
        var ys = new double[n + 1];
        var layouts = new RowLayout[n];                                          // index r = 0 is the BOTTOM row
        double wi = W - 2 * T;
        for (int r = 0; r < n; r++)
        {
            int cfgIdx = n - 1 - r;
            double h = heights[cfgIdx]!.Value;
            ys[r + 1] = ys[r] + T + h;
            layouts[r] = LayoutRow(cfgIdx + 1, ys[r] + T, ys[r + 1], wi,
                                   rowCfgs[cfgIdx].MinWidth ?? dr.MinWidth, cfg.Supports.MaxSpan, rowCfgs[cfgIdx].Count);
        }
        Need(Math.Abs(ys[n] - (H - T)) < 0.01, "Internal error: rows do not fill the cabinet height.");

        // ---- carcass
        var carcass = MakeBox(W, H, D, "carcass", new[] { "Back", "Bottom", "Top", "Left side", "Right side" }, new[] { "Back", "Bottom", "Top", "Left", "Right" });
        var back = carcass[0];
        var left = carcass[3]; var right = carcass[4];
        var shelfPanel = new Panel[n + 1];
        shelfPanel[0] = carcass[1]; shelfPanel[n] = carcass[2];

        var sideRuns = Segs(T + G.M, D - G.M, G.TabW);
        if (sideRuns.Count < 3) throw new UserError($"Cabinet depth {D:0.#} mm is too small for the shelf and divider tabs.");
        var rearRuns = Segs(T + G.M, W - T - G.M, G.TabW);
        for (int k = 1; k < n; k++)
        {
            var s = new Panel { Name = $"Shelf under row {n - k + 1}", Label = $"Shelf r{n - k + 1}", Group = "carcass", W = W, H = D };
            s.Add(T, T, W - T, D);                                              // body
            foreach (var (a, b) in Even(sideRuns)) { s.Add(0, a, T, b); s.Add(W - T, a, W, b); left.Sub(ys[k], a, ys[k] + T, b); right.Sub(ys[k], a, ys[k] + T, b); }
            foreach (var (a, b) in Even(rearRuns)) { s.Add(a, 0, b, T); back.Sub(a, ys[k], b, ys[k] + T); }
            s.Fr = new Frame(0, 0, 2, 0, 1, G.R(ys[k]), G.R(ys[k] + T));
            shelfPanel[k] = s;
            carcass.Add(s);
        }
        double dividerVolume = 0;
        for (int r = 0; r < n; r++)
        {
            var lay = layouts[r];
            int di = 0;
            foreach (double xd in lay.DividerX)
            {
                di++;
                double h = lay.Height;
                var d = new Panel { Name = $"Divider row {lay.RowNo} #{di}", Label = $"Div r{lay.RowNo}.{di}", Group = "carcass", W = D, H = h + 2 * T };
                d.Add(T, T, D, T + h);                                          // body: u = depth from the back, v = height
                var bottomTabs = Even(sideRuns); var topTabs = Odd(sideRuns);
                foreach (var (a, b) in bottomTabs) { d.Add(a, 0, b, T); shelfPanel[r].Sub(xd, a, xd + T, b); }
                foreach (var (a, b) in topTabs) { d.Add(a, T + h, b, 2 * T + h); shelfPanel[r + 1].Sub(xd, a, xd + T, b); }
                var rr = Segs(T + G.M, T + h - G.M, G.TabW);
                foreach (var (a, b) in Even(rr)) { d.Add(0, a, T, b); back.Sub(xd, lay.Y0 - T + a, xd + T, lay.Y0 - T + b); }
                d.Fr = new Frame(2, 0, 1, G.R(lay.Y0 - T), 0, G.R(xd), G.R(xd + T));
                carcass.Add(d);
                dividerVolume += T * (D - T) * h;
            }
        }
        double carcassVolume = W * H * D - (W - 2 * T) * (H - 2 * T) * (D - T) + (n - 1) * (W - 2 * T) * (D - T) * T + dividerVolume;
        Verify("carcass", carcass, carcassVolume);

        // ---- drawers
        var drawerPieces = new List<Piece>();
        var designCache = new Dictionary<string, List<Piece>>();
        var drawerInfo = new List<(int row, int col, double w, double l, double h)>();
        double ld = D - T - dr.ClearanceZ;
        for (int r = n - 1; r >= 0; r--)                                         // top row first
        {
            var lay = layouts[r];
            double wd = lay.Pitch - dr.ClearanceX, hd = lay.Height - dr.ClearanceY;
            Need(wd >= 40 && hd >= 25 && ld >= 40, $"Row {lay.RowNo}: drawers would be {wd:0.#} x {ld:0.#} x {hd:0.#} mm, which is too small. Increase the cabinet size or lower minWidth.");
            double maxR = Math.Min(hd / 2, wd / 2) - T - 4;
            if (dr.FingerHoleRadius > maxR)
                throw new UserError($"Row {lay.RowNo}: a finger hole of radius {dr.FingerHoleRadius:0.#} mm does not fit a {wd:0.#} x {hd:0.#} mm drawer front. The largest radius is {maxR:0.#} mm.");
            string key = $"{wd:0.###}|{ld:0.###}|{hd:0.###}";
            if (!designCache.TryGetValue(key, out var design))
            {
                var box = MakeBox(wd, ld, hd, "drawers", new[] { "Base", "Front", "Back", "Left", "Right" }, new[] { "Base", "Front", "Back", "Left", "Right" });
                box[1].Holes.Add((wd / 2, hd / 2, dr.FingerHoleRadius));
                box[1].Keep.Add(Rect.Of(wd / 2 - dr.FingerHoleRadius, hd / 2 - dr.FingerHoleRadius, wd / 2 + dr.FingerHoleRadius, hd / 2 + dr.FingerHoleRadius));
                Verify($"drawer {wd:0.#}x{ld:0.#}x{hd:0.#}", box, wd * ld * hd - (wd - 2 * T) * (ld - 2 * T) * (hd - T));
                design = box.SelectMany(SplitPanel).ToList();
                designCache[key] = design;
            }
            for (int c = 0; c < lay.N; c++)
            {
                string tag = $"Drawer {lay.RowNo}.{c + 1}";
                foreach (var pc in design)
                    drawerPieces.Add(pc.Clone($"{tag} {pc.Name}", $"D{lay.RowNo}.{c + 1} {pc.Label}"));
                drawerInfo.Add((lay.RowNo, c + 1, wd, ld, hd));
            }
        }

        // ---- carcass pieces
        var carcassPieces = carcass.SelectMany(SplitPanel).ToList();
        // ---- pack and write
        Directory.CreateDirectory(Path.Combine(outDir, "preview"));
        // Remove sheets left over from an earlier run, which may have used different file names.
        var stale = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(cfg.Name) + @"-(carcass|drawers|sheet)-\d+\.(lbrn2|svg)$");
        foreach (var dir in new[] { outDir, Path.Combine(outDir, "preview") })
            foreach (var old in Directory.GetFiles(dir))
                if (stale.IsMatch(Path.GetFileName(old))) File.Delete(old);
        // Either carcass and drawers get their own sheets, or all parts are packed together to waste less wood.
        var groups = cfg.Output.MixParts
            ? new[] { (prefix: "sheet", label: "mixed parts", pieces: carcassPieces.Concat(drawerPieces).ToList()) }
            : new[] { (prefix: "carcass", label: "carcass", pieces: carcassPieces), (prefix: "drawers", label: "drawers", pieces: drawerPieces) };
        var files = new List<(string name, string group, int idx, int of, List<Placed> sheet)>();
        foreach (var g in groups)
        {
            var sheets = Pack(g.pieces);
            for (int i = 0; i < sheets.Count; i++) files.Add(($"{cfg.Name}-{g.prefix}-{i + 1}", g.label, i + 1, sheets.Count, sheets[i]));
        }
        foreach (var f in files)
        {
            string notes = SheetNotes(cfg, f.group, f.idx, f.of, f.sheet);
            File.WriteAllText(Path.Combine(outDir, f.name + ".lbrn2"), Lbrn2(cfg, f.sheet, notes), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(outDir, "preview", f.name + ".svg"), Svg(cfg, f.sheet, $"{f.name}  ({f.group} sheet {f.idx} of {f.of})"), new UTF8Encoding(false));
        }
        File.WriteAllText(Path.Combine(outDir, cfg.Name + "-front.svg"), Elevation(cfg, W, H, D, layouts, dr), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, cfg.Name + "-summary.md"), Summary(cfg, W, H, D, layouts, drawerInfo, carcassPieces, drawerPieces, files.Select(x => (x.name, x.sheet)).ToList()), new UTF8Encoding(false));

        Console.WriteLine($"{cfg.Name}: {W:0.#} x {H:0.#} x {D:0.#} mm, {n} row(s), {drawerInfo.Count} drawer(s), {layouts.Sum(l => l.K)} divider(s)");
        if (cfg.Output.MixParts)
            Console.WriteLine($"  {carcassPieces.Count} carcass and {drawerPieces.Count} drawer pieces mixed on {files.Count} sheet(s)");
        else
            Console.WriteLine($"  {carcassPieces.Count} carcass pieces on {files.Count(x => x.group == "carcass")} sheet(s), {drawerPieces.Count} drawer pieces on {files.Count(x => x.group == "drawers")} sheet(s)");
        Console.WriteLine($"  cutting area per sheet {G.JobW:0.#} x {G.JobH:0.#} mm; output in {outDir}");
    }

    static void Need(bool ok, string msg) { if (!ok) throw new UserError(msg); }

    static Config LoadConfig(string path)
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        };
        try { return JsonSerializer.Deserialize<Config>(File.ReadAllText(path), opts) ?? throw new UserError("The config file is empty."); }
        catch (JsonException e) { throw new UserError($"Cannot read {Path.GetFileName(path)}: {e.Message}"); }
    }

    // ------------------------------------------------------------------ row layout

    // Chooses how many drawers fit a row (as many as possible at the minimum width), widens them equally to use the whole
    // internal width, and adds vertical dividers so that no stretch of shelf is wider than maxSpan.
    static RowLayout LayoutRow(int rowNo, double y0, double y1, double wi, double minW, double? maxSpan, int? forced)
    {
        double T = G.T;
        int nmax = (int)Math.Floor(wi / minW + 1e-9);
        if (nmax < 1) throw new UserError($"Row {rowNo}: the internal width {wi:0.#} mm is narrower than the minimum drawer width {minW:0.#} mm.");
        IEnumerable<int> counts = forced.HasValue ? new[] { forced.Value } : Enumerable.Range(1, nmax).Reverse();
        foreach (int n in counts)
        {
            if (n < 1) throw new UserError($"Row {rowNo}: count must be at least 1.");
            for (int k = 0; k < n; k++)
            {
                double pitch = (wi - k * T) / n;
                if (pitch < minW - 1e-9) break;
                int g = maxSpan.HasValue ? Math.Max(1, (int)Math.Floor(maxSpan.Value / pitch + 1e-9)) : n;
                int need = (int)Math.Ceiling(n / (double)g) - 1;
                if (need > k && !forced.HasValue) continue;
                var lay = new RowLayout { RowNo = rowNo, Y0 = y0, Y1 = y1, Height = y1 - y0, N = n, K = k, Pitch = pitch };
                int groups = k + 1;
                double cursor = T;
                for (int gi = 0; gi < groups; gi++)
                {
                    int size = n / groups + (gi < n % groups ? 1 : 0);
                    for (int j = 0; j < size; j++) { lay.SlotX.Add(cursor); cursor += pitch; }
                    if (gi < groups - 1) { lay.DividerX.Add(cursor); cursor += T; }
                }
                return lay;
            }
        }
        throw new UserError($"Row {rowNo}: cannot fit {forced} drawers of at least {minW:0.#} mm in {wi:0.#} mm.");
    }

    // ------------------------------------------------------------------ finger-jointed box (shared by carcass and drawers)

    // Splits [start,end] into an odd number of equal segments close to the target width.
    static List<(double a, double b)> Segs(double start, double end, double target)
    {
        double len = end - start;
        if (len <= 0) throw new UserError("Internal error: empty joint range (a panel is too small for its joints).");
        int n = (int)Math.Round(len / target);
        if (n < 1) n = 1;
        if (n % 2 == 0) n = n > 1 ? n - 1 : n + 1;
        double s = len / n;
        var r = new List<(double, double)>();
        for (int i = 0; i < n; i++) r.Add((start + i * s, start + (i + 1) * s));
        return r;
    }
    static List<(double a, double b)> Even(List<(double a, double b)> s) => s.Where((_, i) => i % 2 == 0).ToList();
    static List<(double a, double b)> Odd(List<(double a, double b)> s) => s.Where((_, i) => i % 2 == 1).ToList();

    // A five-sided box, open at the top (z = H), in box coordinates X=0..W, Y=0..L, Z=0..H. Names order: base, A1 (y=0), A2 (y=L-T), B1 (x=0), B2 (x=W-T).
    // For the cabinet, box X/Y/Z are the cabinet's width/height/depth-from-back, so the base is the back panel.
    //
    // Overlap volumes belong to exactly one panel:
    //   vertical corner joints (Z split into odd segments): even segments -> Side A, odd -> Side B.
    //   base/side joints (edge split into odd segments, excluding corner cubes): even -> side, odd -> base.
    //   the corner cubes belong to the side that wins Z segment 0 (Side A), never to the base.
    static List<Panel> MakeBox(double W, double L, double H, string group, string[] names, string[] labels)
    {
        double T = G.T;
        Panel P(int i, Panel p) { p.Name = names[i]; p.Label = labels[i]; p.Group = group; return p; }
        var list = new List<Panel>
        {
            P(0, BasePanel(W, L)), P(1, SidePanel(W, H, true)), P(2, SidePanel(W, H, true)), P(3, SidePanel(L, H, false)), P(4, SidePanel(L, H, false)),
        };
        list[0].Fr = new Frame(0, 0, 1, 0, 2, 0, T);
        list[1].Fr = new Frame(0, 0, 2, 0, 1, 0, T);
        list[2].Fr = new Frame(0, 0, 2, 0, 1, L - T, L);
        list[3].Fr = new Frame(1, 0, 2, 0, 0, 0, T);
        list[4].Fr = new Frame(1, 0, 2, 0, 0, W - T, W);
        return list;
    }

    static Panel BasePanel(double W, double L)
    {
        double T = G.T;
        var p = new Panel { W = W, H = L };
        p.Add(0, 0, W, L);
        p.Sub(0, 0, T, T); p.Sub(W - T, 0, W, T); p.Sub(0, L - T, T, L); p.Sub(W - T, L - T, W, L);
        var su = Segs(T, W - T, G.Finger);
        for (int i = 0; i < su.Count; i += 2) { p.Sub(su[i].a, 0, su[i].b, T); p.Sub(su[i].a, L - T, su[i].b, L); }
        var sv = Segs(T, L - T, G.Finger);
        for (int i = 0; i < sv.Count; i += 2) { p.Sub(0, sv[i].a, T, sv[i].b); p.Sub(W - T, sv[i].a, W, sv[i].b); }
        return p;
    }

    static Panel SidePanel(double len, double H, bool fingersAtTop)
    {
        double T = G.T;
        var p = new Panel { W = len, H = H };
        p.Add(0, 0, len, H);
        var su = Segs(T, len - T, G.Finger);
        for (int i = 1; i < su.Count; i += 2) p.Sub(su[i].a, 0, su[i].b, T);          // base wins odd segments
        var sv = Segs(0, H, G.Finger);
        for (int i = fingersAtTop ? 1 : 0; i < sv.Count; i += 2) { p.Sub(0, sv[i].a, T, sv[i].b); p.Sub(len - T, sv[i].a, len, sv[i].b); }
        return p;
    }

    // ------------------------------------------------------------------ rasterising and outline tracing

    // Non-uniform grid whose lines pass through every rectangle edge; each cell is solid or not.
    static Raster Build(Panel p, IEnumerable<double>? extraX = null, IEnumerable<double>? extraY = null)
    {
        var xs = new SortedSet<double> { 0, G.R(p.W) }; var ys = new SortedSet<double> { 0, G.R(p.H) };
        foreach (var r in p.Adds.Concat(p.Subs)) { xs.Add(r.X0); xs.Add(r.X1); ys.Add(r.Y0); ys.Add(r.Y1); }
        if (extraX != null) foreach (var v in extraX) xs.Add(G.R(v));
        if (extraY != null) foreach (var v in extraY) ys.Add(G.R(v));
        var ras = new Raster { X = xs.ToList(), Y = ys.ToList() };
        ras.Nx = ras.X.Count - 1; ras.Ny = ras.Y.Count - 1;
        ras.Occ = new bool[ras.Nx, ras.Ny];
        for (int i = 0; i < ras.Nx; i++)
            for (int j = 0; j < ras.Ny; j++)
                ras.Occ[i, j] = p.Solid((ras.X[i] + ras.X[i + 1]) / 2, (ras.Y[j] + ras.Y[j + 1]) / 2);
        return ras;
    }

    // Boundary of the solid cells as closed loops (outer outlines and any interior holes), collinear points removed.
    static List<List<(double x, double y)>> TraceLoops(List<double> X, List<double> Y, bool[,] occ, string what)
    {
        int nx = occ.GetLength(0), ny = occ.GetLength(1);
        bool Occ(int i, int j) => i >= 0 && i < nx && j >= 0 && j < ny && occ[i, j];
        var edges = new Dictionary<(int, int), (int, int)>();
        void Add((int, int) from, (int, int) to)
        {
            if (!edges.TryAdd(from, to)) throw new UserError($"Internal error: {what}: outline pinches at ({X[from.Item1]:0.##}, {Y[from.Item2]:0.##}).");
        }
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                if (!occ[i, j]) continue;
                if (!Occ(i, j - 1)) Add((i, j), (i + 1, j));
                if (!Occ(i + 1, j)) Add((i + 1, j), (i + 1, j + 1));
                if (!Occ(i, j + 1)) Add((i + 1, j + 1), (i, j + 1));
                if (!Occ(i - 1, j)) Add((i, j + 1), (i, j));
            }
        var loops = new List<List<(double x, double y)>>();
        var seen = new HashSet<(int, int)>();
        foreach (var start in edges.Keys.ToList())
        {
            if (seen.Contains(start)) continue;
            var pts = new List<(double x, double y)>();
            var cur = start;
            do { seen.Add(cur); pts.Add((X[cur.Item1], Y[cur.Item2])); cur = edges[cur]; } while (cur != start);
            var clean = new List<(double x, double y)>();
            for (int k = 0; k < pts.Count; k++)
            {
                var a = pts[(k - 1 + pts.Count) % pts.Count]; var b = pts[k]; var c = pts[(k + 1) % pts.Count];
                bool collinear = (a.x == b.x && b.x == c.x) || (a.y == b.y && b.y == c.y);
                if (!collinear) clean.Add(b);
            }
            loops.Add(clean);
        }
        return loops;
    }

    // ------------------------------------------------------------------ 3D self-check

    static bool Ov(double[] a, double[] b)
    {
        for (int k = 0; k < 3; k++) if (!(a[k] < b[k + 3] - 1e-3 && b[k] < a[k + 3] - 1e-3)) return false;
        return true;
    }

    // Places every panel in cabinet coordinates and checks that no two panels share any volume and that the total volume
    // equals what the finished assembly should contain. Together these mean the joints tile together with no gaps or clashes.
    static void Verify(string what, List<Panel> panels, double expected)
    {
        var data = new List<(Panel p, List<double[]> boxes, double[] lo, double[] hi)>();
        double volume = 0;
        foreach (var p in panels)
        {
            var ras = Build(p);
            var boxes = new List<double[]>();
            double area = 0;
            for (int j = 0; j < ras.Ny; j++)
            {
                int i = 0;
                while (i < ras.Nx)
                {
                    if (!ras.Occ[i, j]) { i++; continue; }
                    int i0 = i;
                    while (i < ras.Nx && ras.Occ[i, j]) i++;
                    var r = new Rect(ras.X[i0], ras.Y[j], ras.X[i], ras.Y[j + 1]);
                    area += (r.X1 - r.X0) * (r.Y1 - r.Y0);
                    boxes.Add(p.Fr!.ToWorld(r));
                }
            }
            volume += area * (p.Fr!.N1 - p.Fr.N0);
            var lo = new double[3]; var hi = new double[3];
            for (int a = 0; a < 3; a++) { lo[a] = boxes.Min(b => b[a]); hi[a] = boxes.Max(b => b[a + 3]); }
            data.Add((p, boxes, lo, hi));
        }
        for (int a = 0; a < data.Count; a++)
            for (int b = a + 1; b < data.Count; b++)
            {
                var region = new double[6];
                for (int k = 0; k < 3; k++) { region[k] = Math.Max(data[a].lo[k], data[b].lo[k]); region[k + 3] = Math.Min(data[a].hi[k], data[b].hi[k]); }
                if (region[0] >= region[3] || region[1] >= region[4] || region[2] >= region[5]) continue;
                var ba = data[a].boxes.Where(x => Ov(x, region)).ToList();
                var bb = data[b].boxes.Where(x => Ov(x, region)).ToList();
                foreach (var x in ba)
                    foreach (var y in bb)
                        if (Ov(x, y)) throw new UserError($"Internal geometry check failed for {what}: '{data[a].p.Name}' and '{data[b].p.Name}' overlap near x={Math.Max(x[0], y[0]):0.#} y={Math.Max(x[1], y[1]):0.#} z={Math.Max(x[2], y[2]):0.#}.");
            }
        if (Math.Abs(volume - expected) > Math.Max(0.5, expected * 1e-6))
            throw new UserError($"Internal geometry check failed for {what}: the panels contain {volume:0.##} mm3 of material but the assembly needs {expected:0.##} mm3.");
    }

    // ------------------------------------------------------------------ splitting oversized panels

    static bool FitsJob(double w, double h) => (w <= G.JobW + 1e-6 && h <= G.JobH + 1e-6) || (h <= G.JobW + 1e-6 && w <= G.JobH + 1e-6);

    static int CountFor(double len, double m)
    {
        if (len <= m + 1e-6) return 1;
        for (int n = 2; n < 200; n++) if (len / n + G.NeckD + G.HeadD + 16 <= m) return n;
        throw new UserError("A panel is too large to split into pieces for this cutting area.");
    }

    static Plan MakePlan(Panel p)
    {
        var plan = new Plan();
        if (FitsJob(p.W, p.H)) return plan;
        double best = double.MaxValue; int bc = 1, br = 1;
        foreach (var (mw, mh) in new[] { (G.JobW, G.JobH), (G.JobH, G.JobW) })
        {
            int nc = CountFor(p.W, mw), nr = CountFor(p.H, mh);
            double cost = nc * nr * 1e6 + (nc - 1) * p.H + (nr - 1) * p.W;
            if (cost < best) { best = cost; bc = nc; br = nr; }
        }
        for (int i = 1; i < bc; i++) plan.Sx.Add(PickSeam(p, true, i * p.W / bc));
        for (int j = 1; j < br; j++) plan.Sy.Add(PickSeam(p, false, j * p.H / br));
        foreach (double s in plan.Sx) plan.TabsV.Add(PlaceTabs(p, true, s, plan.Sy));
        foreach (double t in plan.Sy) plan.TabsH.Add(PlaceTabs(p, false, t, plan.Sx));
        return plan;
    }

    // Seams are nudged away from joint edges so no sliver is left, and preferably from notches and slots altogether.
    static double PickSeam(Panel p, bool vertical, double ideal)
    {
        for (int pass = 0; pass < 2; pass++)
            for (int k = 0; k <= 16; k++)
                foreach (double sh in k == 0 ? new[] { 0.0 } : new[] { k * 0.5, -k * 0.5 })
                {
                    double s = G.R(ideal + sh);
                    if (SeamOk(p, vertical, s, pass == 0)) return s;
                }
        throw new UserError($"Cannot find a clean place to join the pieces of '{p.Name}' near {ideal:0.#} mm.");
    }

    static bool SeamOk(Panel p, bool vertical, double s, bool strict)
    {
        foreach (var r in p.Adds.Concat(p.Subs))
        {
            double a = vertical ? r.X0 : r.Y0, b = vertical ? r.X1 : r.Y1;
            if (Math.Abs(s - a) < 2 || Math.Abs(s - b) < 2) return false;
        }
        if (strict)
            foreach (var r in p.Subs)
            {
                double a = vertical ? r.X0 : r.Y0, b = vertical ? r.X1 : r.Y1;
                if (s > a && s < b) return false;
            }
        foreach (var r in p.Keep)
        {
            double a = vertical ? r.X0 : r.Y0, b = vertical ? r.X1 : r.Y1;
            if (s > a - 3 && s < b + 3) return false;
        }
        return true;
    }

    static bool RectFree(Panel p, Rect r)
    {
        foreach (var s in p.Subs) if (s.Overlaps(r)) return false;
        foreach (var k in p.Keep) if (k.Overlaps(r)) return false;
        var xs = new List<double>(); var ys = new List<double>();
        for (double x = r.X0 + 1e-4; x < r.X1; x += 1.0) xs.Add(x);
        xs.Add(r.X1 - 1e-4);
        for (double y = r.Y0 + 1e-4; y < r.Y1; y += 1.0) ys.Add(y);
        ys.Add(r.Y1 - 1e-4);
        foreach (var x in xs) foreach (var y in ys) if (!p.Solid(x, y)) return false;
        return true;
    }

    // Joint tabs (T-shaped "hammer" tabs that lock the neighbouring piece) along one seam.
    static List<double> PlaceTabs(Panel p, bool vertical, double s, List<double> crossSeams)
    {
        double depth = G.NeckD + G.HeadD, pad = 1.5;
        double along = vertical ? p.H : p.W;
        double em = 2 * G.T + 4 + G.HeadW / 2;
        double span = along - 2 * em;
        var res = new List<double>();
        if (span <= 0) throw new UserError($"'{p.Name}' is too small to carry joint tabs.");
        int k = span >= G.HeadW * 3 ? Math.Max(2, (int)Math.Round(span / G.Pitch)) : 1;
        double step = span / k;
        for (int i = 0; i < k; i++)
        {
            double c0 = em + (i + 0.5) * step;
            bool found = false;
            for (int q = 0; q <= (int)(step * 0.4 / 0.5) && q < 200 && !found; q++)
                foreach (double sh in q == 0 ? new[] { 0.0 } : new[] { q * 0.5, -q * 0.5 })
                {
                    double c = G.R(c0 + sh);
                    double a0 = c - G.HeadW / 2 - pad, a1 = c + G.HeadW / 2 + pad;
                    if (crossSeams.Any(t => a0 < t + depth + pad + 1 && a1 > t - pad - 1)) continue;
                    var region = vertical ? Rect.Of(s - pad, a0, s + depth + pad, a1) : Rect.Of(a0, s - pad, a1, s + depth + pad);
                    if (!RectFree(p, region)) continue;
                    res.Add(c);
                    found = true;
                    break;
                }
        }
        if (res.Count == 0) throw new UserError($"No room for joint tabs on '{p.Name}' near {s:0.#} mm. Try a different cabinet size.");
        return res;
    }

    static (int c, int r) Owner(Plan pl, double cx, double cy)
    {
        int c = 0, r = 0;
        for (int i = 0; i < pl.Sx.Count; i++)
        {
            double s = pl.Sx[i];
            if (cx <= s) continue;
            bool tab = false;
            if (cx < s + G.NeckD + G.HeadD)
            {
                double hw = cx < s + G.NeckD ? G.NeckW / 2 : G.HeadW / 2;
                foreach (double yc in pl.TabsV[i]) if (Math.Abs(cy - yc) < hw) { tab = true; break; }
            }
            if (!tab) c++;
        }
        for (int j = 0; j < pl.Sy.Count; j++)
        {
            double t = pl.Sy[j];
            if (cy <= t) continue;
            bool tab = false;
            if (cy < t + G.NeckD + G.HeadD)
            {
                double hw = cy < t + G.NeckD ? G.NeckW / 2 : G.HeadW / 2;
                foreach (double xc in pl.TabsH[j]) if (Math.Abs(cx - xc) < hw) { tab = true; break; }
            }
            if (!tab) r++;
        }
        return (c, r);
    }

    static List<(double x, double y)> Circle(double cx, double cy, double r)
    {
        const int N = 96;
        var pts = new List<(double x, double y)>();
        for (int i = 0; i < N; i++)
        {
            double a = 2 * Math.PI * i / N;
            pts.Add((Math.Round(cx + r * Math.Cos(a), 3), Math.Round(cy + r * Math.Sin(a), 3)));
        }
        return pts;
    }

    // Turns a panel into one or more cuttable pieces, splitting with interlocking tabs when it is bigger than the cutting area.
    static List<Piece> SplitPanel(Panel p)
    {
        var plan = MakePlan(p);
        var ex = new List<double>(); var ey = new List<double>();
        for (int i = 0; i < plan.Sx.Count; i++)
        {
            double s = plan.Sx[i];
            ex.Add(s); ex.Add(s + G.NeckD); ex.Add(s + G.NeckD + G.HeadD);
            foreach (double yc in plan.TabsV[i]) { ey.Add(yc - G.NeckW / 2); ey.Add(yc + G.NeckW / 2); ey.Add(yc - G.HeadW / 2); ey.Add(yc + G.HeadW / 2); }
        }
        for (int j = 0; j < plan.Sy.Count; j++)
        {
            double t = plan.Sy[j];
            ey.Add(t); ey.Add(t + G.NeckD); ey.Add(t + G.NeckD + G.HeadD);
            foreach (double xc in plan.TabsH[j]) { ex.Add(xc - G.NeckW / 2); ex.Add(xc + G.NeckW / 2); ex.Add(xc - G.HeadW / 2); ex.Add(xc + G.HeadW / 2); }
        }
        var ras = Build(p, ex, ey);
        int nc = plan.Nc, nr = plan.Nr, total = nc * nr;
        var owner = new int[ras.Nx, ras.Ny];
        for (int i = 0; i < ras.Nx; i++)
            for (int j = 0; j < ras.Ny; j++)
            {
                if (!ras.Occ[i, j]) { owner[i, j] = -1; continue; }
                var (c, r) = Owner(plan, (ras.X[i] + ras.X[i + 1]) / 2, (ras.Y[j] + ras.Y[j + 1]) / 2);
                owner[i, j] = c + nc * r;
            }
        var result = new List<Piece>();
        for (int idx = 0; idx < total; idx++)
        {
            var occ = new bool[ras.Nx, ras.Ny];
            bool any = false;
            for (int i = 0; i < ras.Nx; i++) for (int j = 0; j < ras.Ny; j++) if (owner[i, j] == idx) { occ[i, j] = true; any = true; }
            if (!any) continue;
            var loops = TraceLoops(ras.X, ras.Y, occ, p.Name);
            foreach (var h in p.Holes)
            {
                var (c, r) = Owner(plan, h.cx, h.cy);
                if (c + nc * r == idx) loops.Add(Circle(h.cx, h.cy, h.r));
            }
            double minX = loops.Min(l => l.Min(v => v.x)), minY = loops.Min(l => l.Min(v => v.y));
            double maxX = loops.Max(l => l.Max(v => v.x)), maxY = loops.Max(l => l.Max(v => v.y));
            var pc = new Piece { Name = p.Name, Label = p.Label, Group = p.Group, W = maxX - minX, H = maxY - minY };
            foreach (var l in loops) pc.Loops.Add(l.Select(v => (G.R(v.x - minX), G.R(v.y - minY))).ToList());
            result.Add(pc);
        }
        if (result.Count > 1)
            for (int i = 0; i < result.Count; i++)
            {
                result[i].Name = $"{p.Name} [{i + 1}/{result.Count}]";
                result[i].Label = $"{p.Label} {i + 1}/{result.Count}";
            }
        foreach (var pc in result)
            if (!FitsJob(pc.W, pc.H))
                throw new UserError($"'{pc.Name}' is {pc.W:0.#} x {pc.H:0.#} mm, which does not fit the {G.JobW:0.#} x {G.JobH:0.#} mm cutting area.");
        return result;
    }

    // ------------------------------------------------------------------ packing

    class Bin
    {
        public List<(double x, double y, double w, double h)> Free = new() { (0, 0, G.JobW + G.Gap, G.JobH + G.Gap) };
        public List<Placed> Items = new();
    }

    // MaxRects packing (best short side fit): biggest pieces first, each into the first sheet that has room, rotation allowed.
    // Every piece is padded by the part gap, so the free area starts one gap larger than the cutting area.
    static List<List<Placed>> Pack(List<Piece> pieces)
    {
        var bins = new List<Bin>();
        foreach (var p in pieces.OrderByDescending(x => x.W * x.H))
        {
            var orient = new List<(double w, double h, bool rot)>();
            if (p.W <= G.JobW + 1e-6 && p.H <= G.JobH + 1e-6) orient.Add((p.W, p.H, false));
            if (p.H <= G.JobW + 1e-6 && p.W <= G.JobH + 1e-6) orient.Add((p.H, p.W, true));
            if (orient.Count == 0) throw new UserError($"'{p.Name}' ({p.W:0.#} x {p.H:0.#} mm) does not fit the cutting area.");
            if (bins.Any(b => TryPlace(b, p, orient))) continue;
            var nb = new Bin();
            bins.Add(nb);
            if (!TryPlace(nb, p, orient)) throw new UserError($"'{p.Name}' could not be placed on an empty sheet.");
        }
        return bins.Select(b => b.Items).ToList();
    }

    static bool TryPlace(Bin bin, Piece p, List<(double w, double h, bool rot)> orient)
    {
        double bestShort = double.MaxValue, bestLong = double.MaxValue;
        int bi = -1; (double w, double h, bool rot) bo = default;
        for (int i = 0; i < bin.Free.Count; i++)
        {
            var f = bin.Free[i];
            foreach (var o in orient)
            {
                double w = o.w + G.Gap, h = o.h + G.Gap;
                if (w > f.w + 1e-6 || h > f.h + 1e-6) continue;
                double s = Math.Min(f.w - w, f.h - h), l = Math.Max(f.w - w, f.h - h);
                if (s < bestShort - 1e-9 || (Math.Abs(s - bestShort) < 1e-9 && l < bestLong)) { bestShort = s; bestLong = l; bi = i; bo = o; }
            }
        }
        if (bi < 0) return false;
        var fr = bin.Free[bi];
        double pw = bo.w + G.Gap, ph = bo.h + G.Gap;
        bin.Items.Add(new Placed(p, fr.x, fr.y, bo.rot));
        var next = new List<(double x, double y, double w, double h)>();
        foreach (var f in bin.Free)
        {
            if (fr.x + pw <= f.x + 1e-9 || f.x + f.w <= fr.x + 1e-9 || fr.y + ph <= f.y + 1e-9 || f.y + f.h <= fr.y + 1e-9) { next.Add(f); continue; }
            if (fr.x > f.x + 1e-9) next.Add((f.x, f.y, fr.x - f.x, f.h));
            if (fr.x + pw < f.x + f.w - 1e-9) next.Add((fr.x + pw, f.y, f.x + f.w - (fr.x + pw), f.h));
            if (fr.y > f.y + 1e-9) next.Add((f.x, f.y, f.w, fr.y - f.y));
            if (fr.y + ph < f.y + f.h - 1e-9) next.Add((f.x, fr.y + ph, f.w, f.y + f.h - (fr.y + ph)));
        }
        var pruned = new List<(double x, double y, double w, double h)>();
        for (int i = 0; i < next.Count; i++)
        {
            var a = next[i];
            bool inside = false;
            for (int j = 0; j < next.Count && !inside; j++)
            {
                if (i == j) continue;
                var b = next[j];
                bool contained = a.x >= b.x - 1e-9 && a.y >= b.y - 1e-9 && a.x + a.w <= b.x + b.w + 1e-9 && a.y + a.h <= b.y + b.h + 1e-9;
                bool same = Math.Abs(a.x - b.x) < 1e-9 && Math.Abs(a.y - b.y) < 1e-9 && Math.Abs(a.w - b.w) < 1e-9 && Math.Abs(a.h - b.h) < 1e-9;
                if (contained && (!same || j < i)) inside = true;
            }
            if (!inside) pruned.Add(a);
        }
        bin.Free = pruned;
        return true;
    }

    static List<(double x, double y)> World(Placed pl, List<(double x, double y)> loop)
    {
        double ox = G.OriginX + pl.X, oy = G.OriginY + pl.Y;
        return loop.Select(v => pl.Rot ? (ox + pl.P.H - v.y, oy + v.x) : (ox + v.x, oy + v.y)).ToList();
    }

    // ------------------------------------------------------------------ writers

    static string Esc(string s) => s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\n", "&#10;");

    static string SheetNotes(Config cfg, string group, int idx, int of, List<Placed> sheet)
    {
        double cut = sheet.Sum(s => s.P.CutLength());
        var sb = new StringBuilder();
        sb.Append($"{cfg.Name} - {group} sheet {idx} of {of}: {sheet.Count} parts, {cut:0} mm of cutting, {G.T:0.##} mm material.\n");
        sb.Append($"Place the {cfg.Stock.Width:0.#} x {cfg.Stock.Length:0.#} mm stock with its corner at X {cfg.Machine.BedMargin:0.#}, Y {cfg.Machine.BedMargin:0.#}; parts stay {cfg.Stock.EdgeMargin:0.#} mm inside its edges.\n");
        sb.Append($"Parts: {string.Join(", ", sheet.OrderBy(s => s.P.Label).Select(s => s.P.Label))}.\n");
        sb.Append($"Layer settings ({G.F(cfg.Laser.Speed)} mm/s, {G.F(cfg.Laser.Power)}%, kerf {G.F(cfg.Laser.Kerf)} mm) are a starting point - test on scrap and adjust.");
        return sb.ToString();
    }

    static string Lbrn2(Config cfg, List<Placed> sheet, string notes)
    {
        var L = cfg.Laser;
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append($"<LightBurnProject AppVersion=\"1.7.08\" FormatVersion=\"1\" MaterialHeight=\"{G.F(G.T)}\" MirrorX=\"False\" MirrorY=\"False\">\n");
        sb.Append("    <CutSetting type=\"Cut\">\n");
        sb.Append("        <index Value=\"0\"/>\n");
        sb.Append("        <name Value=\"Cut\"/>\n");
        sb.Append($"        <minPower Value=\"{G.F(L.Power)}\"/>\n");
        sb.Append($"        <maxPower Value=\"{G.F(L.Power)}\"/>\n");
        sb.Append($"        <maxPower2 Value=\"{G.F(L.Power)}\"/>\n");
        sb.Append($"        <speed Value=\"{G.F(L.Speed)}\"/>\n");
        sb.Append($"        <numPasses Value=\"{L.Passes}\"/>\n");
        sb.Append($"        <kerf Value=\"{G.F(L.Kerf)}\"/>\n");
        sb.Append("        <priority Value=\"0\"/>\n");
        sb.Append("    </CutSetting>\n");
        int id = 0;
        foreach (var pl in sheet)
            foreach (var loop in pl.P.Loops)
            {
                // VertID/PrimID must be unique per shape: LightBurn 2.x drops shapes without them and merges shapes that share them.
                sb.Append($"    <Shape Type=\"Path\" CutIndex=\"0\" VertID=\"{id}\" PrimID=\"{id}\">\n");
                id++;
                sb.Append("        <XForm>1 0 0 1 0 0</XForm>\n        <VertList>");
                foreach (var v in World(pl, loop)) sb.Append($"V{G.F(v.x)} {G.F(v.y)}c0x1c1x1");
                sb.Append("</VertList>\n        <PrimList>LineClosed</PrimList>\n    </Shape>\n");
            }
        sb.Append($"    <Notes ShowOnLoad=\"1\" Notes=\"{Esc(notes)}\"/>\n");
        sb.Append("</LightBurnProject>\n");
        return sb.ToString();
    }

    static string Svg(Config cfg, List<Placed> sheet, string title)
    {
        var mc = cfg.Machine; var st = cfg.Stock;
        double bw = mc.BedWidth, bh = mc.BedHeight;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"-2 -14 {G.F(bw + 4)} {G.F(bh + 18)}\">\n");
        sb.Append($"<text x=\"0\" y=\"-4\" font-size=\"8\" font-family=\"sans-serif\">{Esc(title)}</text>\n");
        sb.Append($"<g transform=\"translate(0,{G.F(bh)}) scale(1,-1)\">\n");
        sb.Append($"<rect x=\"0\" y=\"0\" width=\"{G.F(bw)}\" height=\"{G.F(bh)}\" fill=\"#f3f3f3\" stroke=\"#999\" stroke-width=\"0.5\"/>\n");
        sb.Append($"<rect x=\"{G.F(mc.BedMargin)}\" y=\"{G.F(mc.BedMargin)}\" width=\"{G.F(st.Width)}\" height=\"{G.F(st.Length)}\" fill=\"#fffdf6\" stroke=\"#bbb\" stroke-dasharray=\"4 2\" stroke-width=\"0.5\"/>\n");
        foreach (var pl in sheet)
        {
            var d = new StringBuilder();
            foreach (var loop in pl.P.Loops)
            {
                var pts = World(pl, loop);
                d.Append("M" + string.Join("L", pts.Select(v => $"{G.F(v.x)},{G.F(v.y)}")) + "Z");
            }
            sb.Append($"<path d=\"{d}\" fill=\"#e2c48a\" fill-rule=\"evenodd\" stroke=\"#000\" stroke-width=\"0.4\"/>\n");
            var all = pl.P.Loops.SelectMany(l => World(pl, l)).ToList();
            double cx = (all.Min(v => v.x) + all.Max(v => v.x)) / 2, cy = (all.Min(v => v.y) + all.Max(v => v.y)) / 2;
            sb.Append($"<text x=\"{G.F(cx)}\" y=\"{G.F(-cy)}\" transform=\"scale(1,-1)\" font-size=\"5\" text-anchor=\"middle\" font-family=\"sans-serif\">{Esc(pl.P.Label)}</text>\n");
        }
        sb.Append("</g></svg>\n");
        return sb.ToString();
    }

    // Front view of the finished cabinet: rows, dividers and drawers with their finger holes.
    static string Elevation(Config cfg, double W, double H, double D, RowLayout[] layouts, DrawerCfg dr)
    {
        double T = G.T;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"-10 -22 {G.F(W + 20)} {G.F(H + 36)}\">\n");
        sb.Append($"<text x=\"0\" y=\"-8\" font-size=\"9\" font-family=\"sans-serif\">{Esc(cfg.Name)}: front view, {W:0.#} x {H:0.#} x {D:0.#} mm external</text>\n");
        sb.Append($"<g transform=\"translate(0,{G.F(H)}) scale(1,-1)\">\n");
        sb.Append($"<rect x=\"0\" y=\"0\" width=\"{G.F(W)}\" height=\"{G.F(H)}\" fill=\"#cfae74\" stroke=\"#000\" stroke-width=\"0.6\"/>\n");
        foreach (var lay in layouts)
        {
            sb.Append($"<rect x=\"{G.F(T)}\" y=\"{G.F(lay.Y0)}\" width=\"{G.F(W - 2 * T)}\" height=\"{G.F(lay.Height)}\" fill=\"#5b4a32\"/>\n");
            foreach (double xd in lay.DividerX)
                sb.Append($"<rect x=\"{G.F(xd)}\" y=\"{G.F(lay.Y0)}\" width=\"{G.F(T)}\" height=\"{G.F(lay.Height)}\" fill=\"#cfae74\" stroke=\"#000\" stroke-width=\"0.4\"/>\n");
            double wd = lay.Pitch - dr.ClearanceX, hd = lay.Height - dr.ClearanceY;
            for (int i = 0; i < lay.SlotX.Count; i++)
            {
                double x = lay.SlotX[i] + dr.ClearanceX / 2;
                sb.Append($"<rect x=\"{G.F(x)}\" y=\"{G.F(lay.Y0)}\" width=\"{G.F(wd)}\" height=\"{G.F(hd)}\" fill=\"#e2c48a\" stroke=\"#000\" stroke-width=\"0.5\"/>\n");
                sb.Append($"<circle cx=\"{G.F(x + wd / 2)}\" cy=\"{G.F(lay.Y0 + hd / 2)}\" r=\"{G.F(dr.FingerHoleRadius)}\" fill=\"#5b4a32\" stroke=\"#000\" stroke-width=\"0.4\"/>\n");
                sb.Append($"<text x=\"{G.F(x + 3)}\" y=\"{G.F(-(lay.Y0 + hd - 3))}\" transform=\"scale(1,-1)\" font-size=\"6\" font-family=\"sans-serif\">{lay.RowNo}.{i + 1}  {wd:0.#} x {hd:0.#}</text>\n");
            }
        }
        sb.Append("</g></svg>\n");
        return sb.ToString();
    }

    static string Summary(Config cfg, double W, double H, double D, RowLayout[] layouts, List<(int row, int col, double w, double l, double h)> drawers,
                          List<Piece> carcass, List<Piece> drawerPieces, List<(string name, List<Placed> sheet)> files)
    {
        var dr = cfg.Drawers;
        var sb = new StringBuilder();
        sb.Append($"# {cfg.Name}\n\n");
        sb.Append($"External size {W:0.#} x {H:0.#} x {D:0.#} mm (width x height x depth), {G.T:0.##} mm material. Rows are numbered from the top.\n\n");
        sb.Append("| Row | Clear height | Drawers | Drawer size (W x D x H) | Dividers |\n|-----|--------------|---------|-------------------------|----------|\n");
        foreach (var lay in layouts.Reverse())
        {
            var d = drawers.First(x => x.row == lay.RowNo);
            sb.Append($"| {lay.RowNo} | {lay.Height:0.#} mm | {lay.N} | {d.w:0.#} x {d.l:0.#} x {d.h:0.#} mm | {lay.K} |\n");
        }
        sb.Append($"\nFinger holes: radius {dr.FingerHoleRadius:0.#} mm in the centre of each front.\n\n");
        sb.Append($"Parts: {carcass.Count} carcass pieces, {drawerPieces.Count} drawer pieces.\n\n");
        sb.Append("| File | Parts | Cutting length |\n|------|-------|----------------|\n");
        foreach (var f in files) sb.Append($"| {f.name}.lbrn2 | {f.sheet.Count} | {f.sheet.Sum(s => s.P.CutLength()):0} mm |\n");
        sb.Append("\n## Parts\n\n| Part | Size (mm) | Sheet file |\n|------|-----------|------------|\n");
        foreach (var f in files)
            foreach (var s in f.sheet.OrderBy(s => s.P.Name))
                sb.Append($"| {s.P.Name} | {s.P.W:0.#} x {s.P.H:0.#} | {f.name}.lbrn2 |\n");
        return sb.ToString();
    }
}
