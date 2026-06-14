using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MIQ.Parsing;
using MIQ.Rendering;
using MIQ.Perf;

// Build with -p:DefineConstants=NEWPATH against a tree that has
// MiqVolume.CenterInteractiveState (i.e. P1-A applied). Without it, the harness
// uses only the legacy primitive sequence (BuildSegmentationLut + SharedWindow +
// ExtractSlice×3) and so compiles against pre-P1-A `main` — which is how the
// main-baseline golden is captured. See docs/performance-plan.md.

// ── Constants ────────────────────────────────────────────────────────────────
const int N_ITERS = 3;   // measured iterations per quick stage (after 1 warm-up)
const int N_SCRUB = 20;  // ExtractSlice calls in the scrub bench

// Correctness golden + differential are checked in these modes. Off exercises the
// intensity/window path; Auto exercises segmentation detection (monochrome on the
// binary mask, random on a plain seg, FreeSurfer on a parcellation, ScanVolume0).
var goldenModes = new (string name, MiqRenderingOptions opts)[]
{
    ("off",  new MiqRenderingOptions(Segmentation: MiqSegmentationColoring.Off)),
    ("auto", new MiqRenderingOptions(Segmentation: MiqSegmentationColoring.Auto)),
};

// Perf timing uses the plugin's default (Off).
var perfOpts = new MiqRenderingOptions();

var jsonOpts = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

// ── CLI ──────────────────────────────────────────────────────────────────────
bool updateBaseline = false, updateGolden = false, verifyMode = false;
double threshold = 1.20;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--update-baseline": updateBaseline = true; break;
        case "--update-golden":   updateGolden   = true; break;
        case "--verify":          verifyMode     = true; break;
        case "--threshold" when i + 1 < args.Length:
            threshold = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            Console.Error.WriteLine("Usage: MIQ.Perf [--update-baseline] [--update-golden] [--verify] [--threshold <ratio>]");
            Environment.Exit(2);
            return;
    }
}

#if NEWPATH
Console.WriteLine("build: NEWPATH (CenterInteractiveState timed + differential-checked)");
#else
Console.WriteLine("build: legacy primitives only (use -p:DefineConstants=NEWPATH for the new path)");
#endif

// ── Paths ────────────────────────────────────────────────────────────────────
var repoRoot = FindRepoRoot(AppContext.BaseDirectory)
    ?? throw new Exception("Cannot locate MIQ-Win.sln. Run from within the repo tree.");
var perfDir = Path.Combine(repoRoot, "scripts", "perf");

var corpusPath   = Path.Combine(perfDir, "testcases.txt");
var baselinePath = Path.Combine(perfDir, "baseline.json");
var resultsPath  = Path.Combine(perfDir, "results.json");
var goldenPath   = Path.Combine(perfDir, "golden.json");

if (!File.Exists(corpusPath))
{
    Console.Error.WriteLine($"error: {corpusPath} not found.");
    Console.Error.WriteLine("Copy scripts/perf/testcases.txt.example to testcases.txt and add your file paths.");
    Environment.Exit(1);
    return;
}

// ── Libdeflate ───────────────────────────────────────────────────────────────
if (Libdeflate.TryLoad())
{
    MiqParser.GzipDecompressorOverride = Libdeflate.Decompress;
    MiqBinaryReader.GzipBufferDecompressorOverride = Libdeflate.DecompressBuffer;
    Console.WriteLine("libdeflate: loaded  (fast gzip — matches plugin path)");
}
else
{
    Console.WriteLine("libdeflate: not found — managed gzip in use (timings will be slower than plugin)");
}

// ── Corpus ───────────────────────────────────────────────────────────────────
var testFiles = File.ReadAllLines(corpusPath)
    .Select(l => l.Trim())
    .Where(l => l.Length > 0 && !l.StartsWith('#'))
    .ToList();

if (testFiles.Count == 0)
{
    Console.Error.WriteLine("No test files in corpus (all lines are blank or comments).");
    Environment.Exit(1);
    return;
}

// ── Load existing baseline + golden ─────────────────────────────────────────
var baseline     = TryLoadJson<PerfReport>(baselinePath, jsonOpts);
var goldenOnDisk = verifyMode && !updateGolden ? TryLoadJson<GoldenReport>(goldenPath, jsonOpts) : null;

// ── Run ──────────────────────────────────────────────────────────────────────
var resultFiles  = new List<FileResult>();
var goldenNew    = new Dictionary<string, FileGolden>();
bool anyVerifyFail = false;
bool anyDiffFail   = false;

Console.WriteLine();
Console.WriteLine($"Threshold: {threshold:P0}   Iterations: {N_ITERS}   Scrub: {N_SCRUB} slices");
Console.WriteLine(new string('─', 74));

foreach (var filePath in testFiles)
{
    if (!File.Exists(filePath))
    {
        Console.WriteLine($"\n! File not found, skipped: {filePath}");
        continue;
    }

    var name   = Path.GetFileName(filePath);
    var sizeMb = new FileInfo(filePath).Length / (1024.0 * 1024.0);
    Console.WriteLine($"\n{name}  ({sizeMb:0.0} MB)");

    var baselineFile = baseline?.Files.FirstOrDefault(f => f.Name == name);
    var stages = new Dictionary<string, StageMetric>();

    try
    {
        // ── Stage 1: ParsePartial ─────────────────────────────────────────
        MiqImage? image = null;
        var s1 = MeasureQuick(() => { image = MiqParser.ParsePartial(filePath); });
        stages["parsePartial"] = s1;

        var hdr = image!.Header;
        var volStr = hdr.Volumes > 1 ? $", {hdr.Volumes} vols" : "";
        var partialStr = image.IsPartial ? ", vol-0 only" : "";
        Console.WriteLine($"  [{hdr.Width}×{hdr.Height}×{hdr.Depth}{volStr}, {hdr.Datatype.Label()}{partialStr}]");
        PrintHeader();
        PrintStage("1  ParsePartial", s1, baselineFile?.Stages.GetValueOrDefault("parsePartial"), threshold);

        var vol = new MiqVolume(image, perfOpts.Orientation);

        // ── Stage 2: first-preview work (Off mode) ─────────────────────────
        // NEWPATH times CenterInteractiveState (the post-P1-A plugin path); else
        // the legacy BuildSegmentationLut + SharedWindow + ExtractSlice×3 sequence.
        SegmentationLut? lut = null;
        IntensityWindow.Bounds? window = null;
        var s2 = MeasureQuick(() =>
        {
#if NEWPATH
            var st = vol.CenterInteractiveState(perfOpts);
            lut = st.Lut; window = st.Window;
#else
            var st = LegacyState(vol, perfOpts);
            lut = st.Lut; window = st.Window;
#endif
        });
        stages["centerState"] = s2;
        PrintStage("2  CenterState", s2, baselineFile?.Stages.GetValueOrDefault("centerState"), threshold);

        // Time-to-first-preview = ParsePartial + first-preview work. Stored under a
        // stable key so it stays comparable across the P1-A harness change.
        var ttp = new StageMetric(s1.MinMs + s2.MinMs, s1.MinMs + s2.MinMs, s1.AllocKb + s2.AllocKb);
        stages["timeToFirstPreview"] = ttp;
        Console.WriteLine("   " + new string('─', 71));
        PrintStage("   Time-to-preview", ttp, baselineFile?.Stages.GetValueOrDefault("timeToFirstPreview"), threshold);
        Console.WriteLine("   " + new string('─', 71));

        // ── Stage 3: FullParse (only if Phase-1 was partial) ──────────────
        MiqVolume scrubVol = vol;
        if (image.IsPartial)
        {
            var s5 = MeasureSingle(() =>
            {
                var fullImg = MiqParser.Parse(filePath);
                scrubVol = new MiqVolume(fullImg, perfOpts.Orientation);
            });
            stages["fullParse"] = s5;
            PrintStage("3  FullParse", s5, baselineFile?.Stages.GetValueOrDefault("fullParse"), threshold);
        }

        // ── Stage 4: Scrub bench ──────────────────────────────────────────
        var s6 = MeasureScrub(scrubVol, window, lut);
        stages["scrubBench"] = s6;
        PrintStage("4  ScrubBench (med)", s6, baselineFile?.Stages.GetValueOrDefault("scrubBench"), threshold);

        resultFiles.Add(new FileResult(filePath, name, stages));

        // ── Correctness: golden + differential, per mode ──────────────────
        if (updateGolden || verifyMode)
        {
            foreach (var (modeName, modeOpts) in goldenModes)
            {
                var key = $"{name}|{modeName}";

                // Golden is ALWAYS the legacy primitive-sequence output — it exists on
                // both main and P1-A, so golden.json is comparable across the change.
                var legacy = LegacyState(vol, modeOpts);
                var legacyGolden = ComputeGolden(legacy.Window, legacy.Lut, legacy.Slices);
                goldenNew[key] = legacyGolden;

#if NEWPATH
                // Differential: the new single-decode path must equal the legacy
                // sequence it replaces, in every mode, this run.
                var st = vol.CenterInteractiveState(modeOpts);
                var newGolden = ComputeGolden(st.Window, st.Lut, st.Slices);
                var diff = CompareGolden(legacyGolden, newGolden);
                if (diff.Count > 0)
                {
                    anyDiffFail = true;
                    Console.WriteLine($"  !! DIFFERENTIAL [{modeName}] CenterInteractiveState ≠ legacy sequence:");
                    foreach (var d in diff) Console.WriteLine($"     {d}");
                }
#endif

                if (verifyMode && goldenOnDisk is not null)
                {
                    if (goldenOnDisk.Files.TryGetValue(key, out var expected))
                    {
                        var diffs = CompareGolden(expected, legacyGolden);
                        if (diffs.Count > 0)
                        {
                            anyVerifyFail = true;
                            Console.WriteLine($"  !! GOLDEN MISMATCH [{modeName}] vs main:");
                            foreach (var d in diffs) Console.WriteLine($"     {d}");
                        }
                        else
                        {
                            Console.WriteLine($"  ✓ golden [{modeName}] OK");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"  ({key} not in golden.json — run --update-golden)");
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ERROR: {ex.GetType().Name}: {ex.Message}");
    }
}

// ── Write outputs ────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine(new string('─', 74));

Directory.CreateDirectory(perfDir);
var report = new PerfReport(DateTime.UtcNow.ToString("o"), threshold, resultFiles);
File.WriteAllText(resultsPath, JsonSerializer.Serialize(report, jsonOpts));
Console.WriteLine($"Results  → {resultsPath}");

if (updateBaseline)
{
    File.WriteAllText(baselinePath, JsonSerializer.Serialize(report, jsonOpts));
    Console.WriteLine($"Baseline → {baselinePath}  (commit it)");
}
else if (baseline is not null)
{
    Console.WriteLine("Re-run with --update-baseline to promote.");
}

if (updateGolden)
{
    File.WriteAllText(goldenPath, JsonSerializer.Serialize(new GoldenReport(goldenNew), jsonOpts));
    Console.WriteLine($"Golden   → {goldenPath}  ({goldenNew.Count} entries, {goldenModes.Length} modes/file)");
}

if (anyDiffFail)
{
    Console.Error.WriteLine("\nDIFFERENTIAL FAILED — CenterInteractiveState differs from the legacy sequence.");
    Environment.Exit(1);
}
if (verifyMode && anyVerifyFail)
{
    Console.Error.WriteLine("\nGOLDEN VERIFY FAILED — output differs from golden.json (vs main).");
    Environment.Exit(1);
}

// ── Local functions ──────────────────────────────────────────────────────────

// Legacy primitive sequence — exactly what Plugin.View did before P1-A. Exists on
// both main and P1-A, so it is the stable reference for golden capture.
static LegacyResult LegacyState(MiqVolume vol, MiqRenderingOptions options)
{
    var lut = vol.BuildSegmentationLut(options);
    var window = lut is null ? vol.SharedWindow(options) : null;
    var slices = new Dictionary<SlicePlane, CenterSlice>
    {
        [SlicePlane.Coronal]  = vol.ExtractSlice(SlicePlane.Coronal,  vol.CenterIndex(SlicePlane.Coronal),  window, lut),
        [SlicePlane.Sagittal] = vol.ExtractSlice(SlicePlane.Sagittal, vol.CenterIndex(SlicePlane.Sagittal), window, lut),
        [SlicePlane.Axial]    = vol.ExtractSlice(SlicePlane.Axial,    vol.CenterIndex(SlicePlane.Axial),    window, lut),
    };
    return new LegacyResult(lut, window, slices);
}

static string? FindRepoRoot(string start)
{
    var d = new DirectoryInfo(start);
    while (d != null)
    {
        if (File.Exists(Path.Combine(d.FullName, "MIQ-Win.sln"))) return d.FullName;
        d = d.Parent;
    }
    return null;
}

static T? TryLoadJson<T>(string path, JsonSerializerOptions opts) where T : class
{
    if (!File.Exists(path)) return null;
    try   { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), opts); }
    catch { Console.WriteLine($"Warning: could not parse {path}"); return null; }
}

static void PrintHeader()
{
    Console.WriteLine($"   {"Stage",-22} {"min_ms",8}  {"alloc_KB",10}   vs_baseline");
}

static void PrintStage(string name, StageMetric m, StageMetric? b, double thresh)
{
    Console.WriteLine($"   {name,-22} {m.MinMs,8:0.0}  {m.AllocKb,10:N0}   {DeltaStr(m, b, thresh)}");
}

static string DeltaStr(StageMetric m, StageMetric? b, double thresh)
{
    if (b is null) return "–";
    var ratio = m.MinMs / Math.Max(0.001, b.MinMs);
    var pct   = (ratio - 1.0) * 100.0;
    var sign  = pct >= 0 ? "+" : "";
    var flag  = ratio > thresh ? "  !! REGRESSION" : "";
    return $"{sign}{pct:0.0}%{flag}";
}

// Warm-up (JIT + OS file cache) then N_ITERS measured runs. Reports min ms and
// median alloc. GC.Collect between iterations so retained garbage from prior
// stages doesn't inflate allocation counts.
static StageMetric MeasureQuick(Action op)
{
    op();
    GC.Collect(2, GCCollectionMode.Forced, true);

    var msTimes  = new double[N_ITERS];
    var allocKbs = new long[N_ITERS];
    for (int i = 0; i < N_ITERS; i++)
    {
        GC.Collect(2, GCCollectionMode.Forced, true);
        var a0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        op();
        sw.Stop();
        msTimes[i]  = sw.Elapsed.TotalMilliseconds;
        allocKbs[i] = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - a0) / 1024;
    }
    Array.Sort(msTimes);
    var sortedAlloc = (long[])allocKbs.Clone();
    Array.Sort(sortedAlloc);
    return new StageMetric(msTimes[0], msTimes[N_ITERS / 2], sortedAlloc[N_ITERS / 2]);
}

// Single measured run (no warm-up) for expensive stages like FullParse.
static StageMetric MeasureSingle(Action op)
{
    GC.Collect(2, GCCollectionMode.Forced, true);
    var a0 = GC.GetAllocatedBytesForCurrentThread();
    var sw = Stopwatch.StartNew();
    op();
    sw.Stop();
    var allocKb = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - a0) / 1024;
    return new StageMetric(sw.Elapsed.TotalMilliseconds, sw.Elapsed.TotalMilliseconds, allocKb);
}

// N_SCRUB ExtractSlice calls spread across all three planes. Reports per-slice
// median time (min across N_ITERS sweeps) and per-slice alloc.
static StageMetric MeasureScrub(MiqVolume vol, IntensityWindow.Bounds? window, SegmentationLut? lut)
{
    var planes = new[] { SlicePlane.Coronal, SlicePlane.Sagittal, SlicePlane.Axial };
    var reqs   = new (SlicePlane plane, int idx)[N_SCRUB];
    for (int i = 0; i < N_SCRUB; i++)
    {
        var plane = planes[i % 3];
        var count = vol.SliceCount(plane);
        reqs[i] = (plane, count > 1 ? (int)((long)i * count / N_SCRUB) : 0);
    }

    foreach (var (p, idx) in reqs) vol.ExtractSlice(p, idx, window, lut); // warm up
    GC.Collect(2, GCCollectionMode.Forced, true);

    var perSliceMs = new double[N_ITERS];
    for (int iter = 0; iter < N_ITERS; iter++)
    {
        GC.Collect(2, GCCollectionMode.Forced, true);
        var sw = Stopwatch.StartNew();
        foreach (var (p, idx) in reqs) vol.ExtractSlice(p, idx, window, lut);
        sw.Stop();
        perSliceMs[iter] = sw.Elapsed.TotalMilliseconds / N_SCRUB;
    }
    Array.Sort(perSliceMs);

    GC.Collect(2, GCCollectionMode.Forced, true);
    var a0 = GC.GetAllocatedBytesForCurrentThread();
    foreach (var (p, idx) in reqs) vol.ExtractSlice(p, idx, window, lut);
    var allocKb = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - a0) / 1024 / N_SCRUB;

    return new StageMetric(perSliceMs[0], perSliceMs[N_ITERS / 2], allocKb);
}

// Golden signature for one file+mode. Hash = SHA-256 over (width LE + height LE +
// label-string UTF-8 + pixel bytes) — catches any pixel-level regression.
static FileGolden ComputeGolden(
    IntensityWindow.Bounds? window, SegmentationLut? lut,
    IReadOnlyDictionary<SlicePlane, CenterSlice> slices)
{
    int? lowBits  = window.HasValue ? BitConverter.SingleToInt32Bits(window.Value.Low)  : null;
    int? highBits = window.HasValue ? BitConverter.SingleToInt32Bits(window.Value.High) : null;

    var seg = lut is null         ? "none"
        : lut.IsMonochromeWhite   ? "monochrome"
        : lut.IsFreeSurfer        ? "freesurfer"
        : "random";

    var planes = new Dictionary<string, PlaneGolden>();
    foreach (var (plane, cs) in slices)
    {
        var img      = cs.Image;
        var labels   = cs.Labels;
        var labelStr = $"{labels.Leading}|{labels.Trailing}|{labels.Top}|{labels.Bottom}";
        var pixels   = img.Grayscale?.Pixels ?? img.Rgb!.Pixels;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(BitConverter.GetBytes(img.Width));
        sha.AppendData(BitConverter.GetBytes(img.Height));
        sha.AppendData(Encoding.UTF8.GetBytes(labelStr));
        sha.AppendData(pixels);
        var hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();

        planes[plane.ToString()] = new PlaneGolden(img.Width, img.Height, hash, labelStr);
    }

    return new FileGolden(lowBits, highBits, seg, planes);
}

static List<string> CompareGolden(FileGolden expected, FileGolden actual)
{
    var diffs = new List<string>();
    if (expected.WindowLowBits  != actual.WindowLowBits)
        diffs.Add($"window.low:  expected {expected.WindowLowBits}, got {actual.WindowLowBits}");
    if (expected.WindowHighBits != actual.WindowHighBits)
        diffs.Add($"window.high: expected {expected.WindowHighBits}, got {actual.WindowHighBits}");
    if (expected.Segmentation != actual.Segmentation)
        diffs.Add($"segmentation: expected '{expected.Segmentation}', got '{actual.Segmentation}'");
    foreach (var (planeName, exp) in expected.Planes)
    {
        if (!actual.Planes.TryGetValue(planeName, out var act))
            { diffs.Add($"{planeName}: missing from output"); continue; }
        if (exp.Width != act.Width || exp.Height != act.Height)
            diffs.Add($"{planeName}: size expected {exp.Width}×{exp.Height}, got {act.Width}×{act.Height}");
        if (exp.PixelHash != act.PixelHash)
            diffs.Add($"{planeName}: pixel hash mismatch (labels: expected '{exp.Labels}', got '{act.Labels}')");
    }
    return diffs;
}

// ── Data types ───────────────────────────────────────────────────────────────

record LegacyResult(SegmentationLut? Lut, IntensityWindow.Bounds? Window,
                    IReadOnlyDictionary<SlicePlane, CenterSlice> Slices);
record StageMetric(double MinMs, double MedianMs, long AllocKb);
record FileResult(string Path, string Name, Dictionary<string, StageMetric> Stages);
record PerfReport(string Timestamp, double Threshold, List<FileResult> Files);
record PlaneGolden(int Width, int Height, string PixelHash, string Labels);
record FileGolden(int? WindowLowBits, int? WindowHighBits, string Segmentation, Dictionary<string, PlaneGolden> Planes);
record GoldenReport(Dictionary<string, FileGolden> Files);
