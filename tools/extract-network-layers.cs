#:package PDFtoImage@5.*
#:package Tesseract@5.*
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#pragma warning disable CA1416, CS0618
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using PDFtoImage;
using SkiaSharp;
using Tesseract;

// Reads access points and network closets off flat Wi-Fi map PDFs, into files the dashboard can import
// (Manage -> Import maps). It runs separately from the app, so the app doesn't need OCR installed.
//
//   dotnet run tools/extract-network-layers.cs -- <mapFolder> <tessdataFolder> <outFolder> 6600 [options] [code...]
//
// Options, all optional:
//   --models=<indoor>,<outdoor>,<alternate>   The model number that ends the names of each symbol type, such as 100,200,300.
//                                             A name one character away is corrected. Without this, models aren't checked.
//   --also-valid=<model>,<model>              Other real models, which are flagged for checking rather than corrected.
//   --outdoor-names=<regex>                   Names that belong to outdoor symbols. The default is EXT.
//
// tessdataFolder holds eng.traineddata, from https://github.com/tesseract-ocr/tessdata_best
// Files must be named <CODE>-anything.pdf or <CODE> anything.pdf. Put the output in a folder named
// Network_Layers beside the import list. Output describes real network layouts: keep it out of git.
//
// How maps are expected to look: names in red, like PREFIX-ROOM-100, ending in a three-digit model, near a symbol: blue star
// (indoor), green star (outdoor), gold star (alternate outdoor), orange box (MDF), yellow box (IDF), plus a legend column.
// For maps drawn differently, adjust the colors under "symbols, found by color" below.
string? Option(string name) => args.FirstOrDefault(a => a.StartsWith($"--{name}="))?[(name.Length + 3)..];
var positional = args.Where(a => !a.StartsWith("--")).ToList();
var dir = positional[0]; var tess = positional[1]; var outDir = positional[2]; int longSide = int.Parse(positional[3]); Directory.CreateDirectory(outDir);
var only = positional.Skip(4).ToHashSet();
var models = (Option("models") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
var alsoValid = (Option("also-valid") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
var outdoorNames = new Regex(Option("outdoor-names") ?? "EXT");
var files = Directory.GetFiles(dir, "*.pdf").OrderBy(f => f).Where(f => only.Count == 0 || only.Contains(CodeOf(f))).ToList();
var results = new ConcurrentBag<string>();
Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 6 }, f =>
{
    try { results.Add(Run(f)); }
    catch (Exception ex) { results.Add($"{CodeOf(f),-5} ERROR {ex.GetType().Name}: {ex.Message}"); }
});
foreach (var r in results.OrderBy(x => x)) Console.WriteLine(r);

static string CodeOf(string f) => Regex.Split(Path.GetFileNameWithoutExtension(f), "[- ]")[0];

string Run(string file)
{
    var code = CodeOf(file);
    var prefix = code; // Name prefix on the map. Checked against what's actually found below.
    var bytes = File.ReadAllBytes(file);
    var size = Conversion.GetPageSize(bytes, page: 0); var scale = longSide / Math.Max(size.Width, size.Height);
    using var bmp = Conversion.ToImage(bytes, page: 0, options: new RenderOptions(Width: (int)(size.Width * scale), Height: (int)(size.Height * scale), BackgroundColor: SKColors.White));
    int w = bmp.Width, h = bmp.Height; var px = bmp.Pixels;
    double areaScale = Math.Pow(longSide / 2200.0, 2);

    // ---------- symbols, found by color ----------
    (string Name, int R, int G, int B, int Min, int Tol)[] classes =
    [
        ("ap-indoor", 80, 150, 240, (int)(12 * areaScale), 45), ("ap-outdoor", 20, 190, 30, (int)(12 * areaScale), 45),
        ("ap-outdoor-577", 184, 134, 30, (int)(12 * areaScale), 30), ("mdf", 250, 150, 0, (int)(60 * areaScale), 35), ("idf", 250, 225, 0, (int)(60 * areaScale), 35),
    ];
    var symbols = new List<Sym>();
    foreach (var c in classes)
    {
        var seen = new bool[w * h]; var stack = new Stack<int>();
        for (int i = 0; i < px.Length; i++)
        {
            if (seen[i] || !Near(px[i], c.R, c.G, c.B, c.Tol)) continue;
            stack.Push(i); seen[i] = true; long sx = 0, sy = 0; int a = 0;
            while (stack.Count > 0)
            {
                var p = stack.Pop(); int x = p % w, y = p / w; sx += x; sy += y; a++;
                if (x > 0 && !seen[p - 1] && Near(px[p - 1], c.R, c.G, c.B, c.Tol)) { seen[p - 1] = true; stack.Push(p - 1); }
                if (x < w - 1 && !seen[p + 1] && Near(px[p + 1], c.R, c.G, c.B, c.Tol)) { seen[p + 1] = true; stack.Push(p + 1); }
                if (y > 0 && !seen[p - w] && Near(px[p - w], c.R, c.G, c.B, c.Tol)) { seen[p - w] = true; stack.Push(p - w); }
                if (y < h - 1 && !seen[p + w] && Near(px[p + w], c.R, c.G, c.B, c.Tol)) { seen[p + w] = true; stack.Push(p + w); }
            }
            if (a >= c.Min) symbols.Add(new Sym(c.Name, sx / (double)a, sy / (double)a, a));
        }
    }

    // ---------- the legend: a tight vertical stack of one symbol of each kind ----------
    double side = Math.Max(w, h); var legend = new List<Sym>();
    foreach (var mdf in symbols.Where(s => s.Kind == "mdf"))
    {
        var col = symbols.Where(s => Math.Abs(s.X - mdf.X) < side * .02 && Math.Abs(s.Y - mdf.Y) < side * .09).ToList();
        if (col.Any(s => s.Kind == "ap-indoor") && col.Any(s => s.Kind == "idf") && col.Any(s => s.Kind.StartsWith("ap-outdoor")))
        { legend = col.Where(s => s.Kind != "ap-indoor" || true).ToList(); break; }
    }
    var real = symbols.Except(legend).ToList();

    // Text can split a star into pieces. A small piece beside a bigger symbol of the same kind is part of it.
    int normalArea = (int)(40 * areaScale);
    real = real.Where(s => s.Area >= normalArea || !real.Any(b => b != s && b.Kind == s.Kind && b.Area > s.Area && Math.Sqrt(Math.Pow(b.X - s.X, 2) + Math.Pow(b.Y - s.Y, 2)) < side * .012)).ToList();

    // ---------- names: isolate red text, then read it ----------
    var red = new SKColor[px.Length];
    for (int i = 0; i < px.Length; i++) { var c = px[i]; int r = c.Red - Math.Max(c.Green, c.Blue); byte v = (byte)Math.Clamp(255 - r * 2.2, 0, 255); red[i] = new SKColor(v, v, v); }
    using var mask = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque) { Pixels = red };
    var pngPath = Path.Combine(outDir, code + "-red.png");
    using (var fs = File.Create(pngPath)) mask.Encode(fs, SKEncodedImageFormat.Png, 100);
    var words = new List<(string T, float Conf, double X, double Y, double X1, double X2, double Y1, double Y2)>();
    using (var engine = new TesseractEngine(tess, "eng", EngineMode.LstmOnly))
    {
        engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-");
        engine.SetVariable("user_defined_dpi", "300");
        using var img = Pix.LoadFromFile(pngPath);
        using var page = engine.Process(img, PageSegMode.SparseText);
        using var it = page.GetIterator(); it.Begin();
        do
        {
            var t = it.GetText(PageIteratorLevel.Word)?.Trim();
            if (string.IsNullOrEmpty(t) || !it.TryGetBoundingBox(PageIteratorLevel.Word, out var box)) continue;
            // Two names read as one word are split, sharing out the box by length.
            var parts = Regex.Split(t, @"(?<=-\d{3})(?=[A-Za-z])").Where(p => p.Length > 0).ToList();
            double start = box.X1, unit = (box.X2 - box.X1) / (double)t.Length;
            foreach (var part in parts) { words.Add((part, it.GetConfidence(PageIteratorLevel.Word), start + unit * part.Length / 2, (box.Y1 + box.Y2) / 2.0, start, start + unit * part.Length, box.Y1, box.Y2)); start += unit * part.Length; }
        } while (it.Next(PageIteratorLevel.Word));
    }
    // The real prefix is whatever most names start with. A near miss of the file's code is a misread.
    var prefixes = words.Select(x => Regex.Match(x.T.ToUpperInvariant(), @"^([A-Z]{1,5})-").Groups[1].Value).Where(x => x.Length > 0).GroupBy(x => x).OrderByDescending(g => g.Count()).ToList();
    if (prefixes.Count > 0 && Edit(prefixes[0].Key, code) > 1) prefix = prefixes[0].Key;
    var names = words.Where(x => x.T.Length >= 6 && x.T.Contains('-') && Regex.IsMatch(x.T.ToUpperInvariant(), @"\d")).ToList();
    var ignored = words.Except(names).Count();

    // ---------- pair each name with its nearest free symbol ----------
    var aps = real.Where(s => s.Kind.StartsWith("ap-")).ToList();
    var limit = side * .06; var pairs = new List<(int N, int S, double D)>();
    for (int n = 0; n < names.Count; n++) for (int s = 0; s < aps.Count; s++)
    { var d = Math.Sqrt(Math.Pow(names[n].X - aps[s].X, 2) + Math.Pow(names[n].Y - aps[s].Y, 2)); if (d <= limit) pairs.Add((n, s, d)); }
    var nameUsed = new HashSet<int>(); var symUsed = new HashSet<int>(); var matches = new List<(int N, int S)>(); var mismatched = new HashSet<int>();
    // Outdoor names go with outdoor symbols first, so close neighbors can't swap.
    bool SameKind(int n, int s) { var o = outdoorNames.IsMatch(names[n].T.ToUpperInvariant()); return o == aps[s].Kind.StartsWith("ap-outdoor"); }
    foreach (var p in pairs.Where(x => SameKind(x.N, x.S)).OrderBy(x => x.D)) if (!nameUsed.Contains(p.N) && !symUsed.Contains(p.S)) { nameUsed.Add(p.N); symUsed.Add(p.S); matches.Add((p.N, p.S)); }
    foreach (var p in pairs.OrderBy(x => x.D)) if (!nameUsed.Contains(p.N) && !symUsed.Contains(p.S)) { nameUsed.Add(p.N); symUsed.Add(p.S); matches.Add((p.N, p.S)); mismatched.Add(p.N); }

    // ---------- read each paired name a second time, cropped and enlarged on its own ----------
    var second = new Dictionary<int, string>();
    using (var engine2 = new TesseractEngine(tess, "eng", EngineMode.LstmOnly))
    {
        engine2.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-");
        engine2.SetVariable("user_defined_dpi", "300");
        foreach (var (nIdx, _) in matches)
        {
            var nm = names[nIdx]; int pad = (int)(nm.Y2 - nm.Y1) / 2 + 4;
            var rect = new SKRectI(Math.Max(0, (int)nm.X1 - pad), Math.Max(0, (int)nm.Y1 - pad), Math.Min(w, (int)nm.X2 + pad), Math.Min(h, (int)nm.Y2 + pad));
            if (rect.Width < 8 || rect.Height < 8) continue;
            using var crop = new SKBitmap(); if (!mask.ExtractSubset(crop, rect)) continue;
            using var big = crop.Resize(new SKImageInfo(rect.Width * 2, rect.Height * 2), new SKSamplingOptions(SKCubicResampler.Mitchell));
            using var data = SKImage.FromBitmap(big).Encode(SKEncodedImageFormat.Png, 100);
            using var pix = Pix.LoadFromMemory(data.ToArray());
            using var pg = engine2.Process(pix, PageSegMode.SingleLine);
            second[nIdx] = (pg.GetText() ?? "").Trim();
        }
    }
    File.Delete(pngPath);

    // ---------- correct names and flag doubts ----------
    var features = new List<object>(); int clean = 0, fixedUp = 0, flagged = 0; var flagSamples = new List<string>();
    var read = new List<(int NIdx, int SIdx, string Name, List<string> Flags, bool Changed)>();
    foreach (var (nIdx, sIdx) in matches)
    {
        var (nm, fl, ch) = Normalize(names[nIdx].T, prefix, aps[sIdx].Kind);
        var secondText = Regex.Replace(second.GetValueOrDefault(nIdx, ""), @"\s+", "");
        var (name2, fl2, _) = Normalize(secondText, prefix, aps[sIdx].Kind);
        // If only the second read makes sense, use it. If they simply differ, a person should look.
        if (fl.Count > 0 && fl2.Count == 0) { nm = name2; fl.Clear(); ch = true; }
        else if (name2 != nm) fl.Add($"second read: {name2}");
        if (mismatched.Contains(nIdx)) fl.Add("name and symbol type disagree");
        read.Add((nIdx, sIdx, nm, fl, ch));
    }

    // A name missing the dash its siblings have (XY4 beside XY-1, XY-2) gets it back.
    var all = read.Select(r => r.Name).ToHashSet();
    for (int i = 0; i < read.Count; i++)
    {
        var m = Regex.Match(read[i].Name, $@"^{Regex.Escape(prefix)}-([A-Z]+)(\d)-(\d+)$");
        if (m.Success && all.Any(n => Regex.IsMatch(n, $@"^{Regex.Escape(prefix)}-{m.Groups[1].Value}-\d-")))
            read[i] = read[i] with { Name = $"{prefix}-{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}", Changed = true };
    }

    // Some patterns are valid-looking but suspicious once compared with the rest of the map.
    var allNames = read.Select(r => r.Name).ToList();
    var letterRooms = allNames.Count(n => Regex.IsMatch(n, $@"^{Regex.Escape(prefix)}-[A-Z]+\d*-")) > allNames.Count / 2;
    foreach (var r in read)
    {
        var loc = Regex.Match(r.Name, $@"^{Regex.Escape(prefix)}-([A-Z0-9]+)-\d{{3}}$").Groups[1].Value;
        if (letterRooms && Regex.IsMatch(loc, @"^\d{1,2}$")) r.Flags.Add("room may start with a letter");
        if (loc.Length > 0 && allNames.Any(n => n.StartsWith($"{prefix}-{loc}-") && Regex.IsMatch(n, $@"-{loc}-\d-"))) r.Flags.Add("number may be missing");
    }

    // Two access points can't share a name, so at least one of them was misread.
    foreach (var dup in read.GroupBy(r => r.Name).Where(g => g.Count() > 1))
        foreach (var r in dup) r.Flags.Add("duplicate name");

    foreach (var (nIdx, sIdx, name, flags, changed) in read)
    {
        if (flags.Count > 0) { flagged++; if (flagSamples.Count < 5) flagSamples.Add($"{names[nIdx].T}->{name}[{string.Join(",", flags)}]"); } else if (changed) fixedUp++; else clean++;
        features.Add(new { x = aps[sIdx].X / w, y = aps[sIdx].Y / h, symbol = aps[sIdx].Kind, label = name, raw = names[nIdx].T, flags });
    }
    // A small mark with no name is noise, not an access point. Real small stars have names beside them.
    var unnamedSymbols = aps.Where((s, i) => !symUsed.Contains(i) && s.Area >= normalArea).ToList();
    foreach (var s in unnamedSymbols) features.Add(new { x = s.X / w, y = s.Y / h, symbol = s.Kind, label = (string?)null, raw = (string?)null, flags = new List<string> { "no name found" } });
    var strayNames = names.Where((_, i) => !nameUsed.Contains(i)).Select(x => new { text = x.T, x = x.X / w, y = x.Y / h }).Where(x => Regex.IsMatch(x.text.ToUpperInvariant(), @"-\d{3}$") && x.text.Count(c => c == '-') >= 2).ToList();
    var closets = real.Where(s => !s.Kind.StartsWith("ap-")).ToList();
    foreach (var s in closets) features.Add(new { x = s.X / w, y = s.Y / h, symbol = s.Kind, label = s.Kind == "mdf" ? "MDF" : "IDF", raw = (string?)null, flags = new List<string>() });
    var names2 = features.Count;
    File.WriteAllText(Path.Combine(outDir, code + ".json"), JsonSerializer.Serialize(new { code, prefix, file = Path.GetFileName(file), legendFound = legend.Count > 0, strayNames, features }, new JsonSerializerOptions { WriteIndented = true }));

    string K(string kind) => real.Count(s => s.Kind == kind).ToString();
    return $"{code,-5} prefix={prefix,-5} legend={(legend.Count > 0 ? "y" : "N")} indoor={K("ap-indoor"),3} outdoor={K("ap-outdoor"),2}+{K("ap-outdoor-577"),-2} mdf={K("mdf")} idf={K("idf"),2} | names={names.Count,3} paired={matches.Count,3} clean={clean,3} fixed={fixedUp,3} flagged={flagged,2} noName={unnamedSymbols.Count,2} stray={strayNames.Count,2} other={ignored,3}" + (flagSamples.Count > 0 ? "  e.g. " + string.Join(" ", flagSamples.Take(2)) : "");
}

static bool Near(SKColor p, int r, int g, int b, int tol) => Math.Abs(p.Red - r) <= tol && Math.Abs(p.Green - g) <= tol && Math.Abs(p.Blue - b) <= tol;

(string Name, List<string> Flags, bool Changed) Normalize(string raw, string prefix, string kind)
{
    var flags = new List<string>(); var s = raw.ToUpperInvariant().Trim('-');
    var original = s;
    if (!s.StartsWith(prefix + "-"))
    {
        var dash = s.IndexOf('-');
        var first = dash > 0 ? s[..dash] : s;
        if (first.StartsWith(prefix) && first.Length > prefix.Length) s = prefix + "-" + s[prefix.Length..]; // The dash after the prefix was lost.
        else if (dash > 0 && Edit(first, prefix) <= 1) s = prefix + s[dash..];                                // The prefix, slightly misread.
        else if (s.Count(c => c == '-') >= 2 && first.Length >= prefix.Length && Edit(first, prefix) <= 2) s = prefix + s[dash..]; // The prefix, badly misread, in a name with the right shape.
        else { s = prefix + "-" + s; flags.Add("prefix not read"); }                                          // Don't overwrite what might be the room.
    }
    var parts = s.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
    if (parts.Count < 3) flags.Add("unexpected format");

    // Middle sections: letter O and digit 0 get confused, and a doubled pair is usually one character.
    for (int i = 1; i < parts.Count - 1; i++)
    {
        var p = parts[i];
        p = Regex.Replace(p, "(0O|O0)(?=\\d)", "0"); // Only when a letter O is involved: 1009 has a real double zero.
        p = Regex.Replace(p, "(?<=[A-Z]{2})O(?=\\d)", "0");
        p = Regex.Replace(p, "^(RM|P|CC)[IL](?=\\d)", "$1");
        p = Regex.Replace(p, "^11(?=[A-Z])", "I");
        p = Regex.Replace(p, "(?<=\\d)O", "0");
        p = Regex.Replace(p, "^PO(?=\\d)", "P0");
        if (Regex.IsMatch(p, "^P[0O]?[A-Z]$")) flags.Add("unclear character");
        p = Regex.Replace(p, "^EX[1IL]$", "EXT");
        parts[i] = p;
    }

    // Last section: the model. The symbol's color says what it should be.
    var modelIndex = kind switch { "ap-indoor" => 0, "ap-outdoor" => 1, "ap-outdoor-577" => 2, _ => -1 };
    var expected = modelIndex >= 0 && modelIndex < models.Length ? models[modelIndex] : "";
    if (parts.Count > 0 && expected.Length > 0 && parts[^1] != expected)
    {
        var last = parts[^1];
        // One wrong, missing or extra character is a misread, unless it's another real model, which a person should check.
        if (Edit(last, expected) == 1 && !alsoValid.Contains(last)) parts[^1] = expected;
        else flags.Add($"model reads {last}, symbol suggests {expected}");
    }
    var name = string.Join("-", parts);
    return (name, flags, name != original);
}

static int Edit(string a, string b)
{
    var d = new int[a.Length + 1, b.Length + 1];
    for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
    for (int j = 0; j <= b.Length; j++) d[0, j] = j;
    for (int i = 1; i <= a.Length; i++) for (int j = 1; j <= b.Length; j++)
        d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
    return d[a.Length, b.Length];
}

record Sym(string Kind, double X, double Y, int Area);
