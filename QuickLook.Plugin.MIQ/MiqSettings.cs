using System.Globalization;
using System.IO;
using System.Windows.Media;
using MIQ.Rendering;
using QuickLook.Common.Helpers;

namespace QuickLook.Plugin.MIQ;

/// <summary>
/// User preferences from <c>MIQ.settings.ini</c>. The file lives in QuickLook's
/// data folder (next to <c>QuickLook.config</c>), located via
/// <see cref="SettingHelper.LocalDataPath"/> — deliberately NOT inside the
/// plugin's own folder, which QuickLook deletes and re-extracts on every
/// reinstall/upgrade (settings there would be lost). Using the host's data root
/// means the file persists exactly when QuickLook's own settings do, for every
/// deployment (installed, portable, Microsoft Store). See <see cref="Load"/>.
///
/// Re-read on every preview (cheap) so edits apply on the next Space — no
/// restart. Missing/invalid keys fall back to the default below; an
/// unreadable file yields all-defaults; a missing file is created with the
/// defaults so it is self-documenting.
///
/// To add a setting: (1) add a property with its default, (2) read it in
/// <see cref="Apply"/>, (3) add a line to <see cref="DefaultText"/>. That's it.
/// </summary>
internal sealed class MiqSettings
{
    public const string FileName = "MIQ.settings.ini";

    // ---- defaults: the property initialiser IS the default ----

    // Initial preview window size.
    public double PreviewWidth { get; private set; } = 850;
    public double PreviewHeight { get; private set; } = 690;

    public Color AxisLabelColor { get; private set; } = Color.FromRgb(0xFF, 0x40, 0x40);
    public Color AxisLabelUnknownColor { get; private set; } = Color.FromRgb(0x78, 0x32, 0x32);
    public double AxisLabelFontSize { get; private set; } = 9;

    // Same red as the axis labels / scrubber, but semi-transparent so it reads
    // as an overlay rather than competing with the labels.
    public Color CrosshairColor { get; private set; } = Color.FromArgb(0xAA, 0xFF, 0x40, 0x40);
    public bool CrosshairDashed { get; private set; } = true;
    public double CrosshairThickness { get; private set; } = 1.0;

    public Color ScrubberColor { get; private set; } = Color.FromRgb(0xFF, 0x40, 0x40);

    public Color BackgroundColor { get; private set; } = Colors.Black;

    public double MetadataFontSize { get; private set; } = 14;
    public Color MetadataLabelColor { get; private set; } = Color.FromRgb(0x96, 0x96, 0x99);
    public Color MetadataValueColor { get; private set; } = Color.FromRgb(0xEB, 0xEB, 0xF0);

    // Footer disclaimer in the metadata panel (mirrors macOS MIQ). Hide via
    // ShowDisclaimer = false.
    public bool ShowDisclaimer { get; private set; } = true;

    // View orientation: Stored renders axes as stored; Neurological/Radiological
    // reorient + relabel each plane (they differ only by the coronal/axial R/L
    // flip). Files without an OrientationFrame always fall back to Stored.
    public MiqOrientation Orientation { get; private set; } = MiqOrientation.Stored;

    public double IntensityPercentileLow { get; private set; } = 2.0;
    public double IntensityPercentileHigh { get; private set; } = 98.0;
    // When true, the intensity window is recomputed for each volume as the
    // user scrubs. Results are cached per volume so back-navigation is instant.
    public bool PerVolumeWindow { get; private set; } = true;

    // Which metadata rows to show, in order. Names absent from a given file
    // (e.g. Scaling) are skipped.
    public IReadOnlyList<string> MetadataFields { get; private set; } =
        ["Format", "Dimensions", "Spacing", "Orientation", "Datatype", "Volumes", "Scaling"];

    // ---- resolved WPF resources (frozen; built by Resolve) ----
    public Brush AxisBrush { get; private set; } = null!;
    public Brush AxisUnknownBrush { get; private set; } = null!;
    public Brush BackgroundBrush { get; private set; } = null!;
    public Brush MetadataLabelBrush { get; private set; } = null!;
    public Brush MetadataValueBrush { get; private set; } = null!;
    public Pen CrosshairPen { get; private set; } = null!;
    public Brush ScrubberFillBrush { get; private set; } = null!;
    public Brush ScrubberKnobBrush { get; private set; } = null!;

    public MiqRenderingOptions Options =>
        new(IntensityPercentileLow, IntensityPercentileHigh, Orientation);

    private MiqSettings() { }

    public static MiqSettings Load()
    {
        var s = new MiqSettings();
        try
        {
            var path = ResolveSettingsPath(out var pluginDir);
            if (path == null) { s.Resolve(); return s; }

            if (File.Exists(path))
                s.Apply(Parse(File.ReadAllLines(path)));
            else
                TryWrite(path, s.DefaultText());

            // Leave a breadcrumb in the plugin folder — the obvious place to
            // look — pointing at the durable file. It is wiped on upgrade but
            // recreated on the next preview.
            WriteBreadcrumb(pluginDir, path);
        }
        catch { /* any failure → all defaults */ }
        s.Resolve();
        return s;
    }

    /// Durable path for the settings file: QuickLook's data root (next to
    /// QuickLook.config), obtained from the host so it is correct for every
    /// deployment. <paramref name="pluginDir"/> receives this plugin's own
    /// folder (for the breadcrumb). Layered fallbacks keep the plugin working
    /// even if the host API is unavailable.
    private static string? ResolveSettingsPath(out string? pluginDir)
    {
        pluginDir = Path.GetDirectoryName(typeof(MiqSettings).Assembly.Location);

        string? root = null;
        try { root = SettingHelper.LocalDataPath; } catch { /* fall back below */ }

        // If the host field is missing/empty, the plugin folder's PARENT
        // (…\QuickLook.Plugin\) still survives a reinstall — only the leaf
        // <plugin> folder is wiped. Last resort: the plugin folder itself
        // (lost on upgrade, but never worse than not having the file).
        if (string.IsNullOrEmpty(root) && !string.IsNullOrEmpty(pluginDir))
            root = Path.GetDirectoryName(pluginDir);
        if (string.IsNullOrEmpty(root))
            root = pluginDir;

        return string.IsNullOrEmpty(root) ? null : Path.Combine(root!, FileName);
    }

    private static void TryWrite(string path, string text)
    {
        try { File.WriteAllText(path, text); }
        catch { /* read-only dir: still run on defaults */ }
    }

    private static void WriteBreadcrumb(string? pluginDir, string settingsPath)
    {
        // Nothing to point at if we have no plugin folder, or if the settings
        // file already lives there (degenerate fallback).
        if (string.IsNullOrEmpty(pluginDir) ||
            string.Equals(Path.GetDirectoryName(settingsPath), pluginDir,
                StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            File.WriteAllText(Path.Combine(pluginDir!, "MIQ settings location.txt"),
                "MIQ preview settings are stored outside this folder so they\r\n" +
                "survive plugin updates. Edit this file:\r\n\r\n    " +
                settingsPath + "\r\n");
        }
        catch { /* best-effort breadcrumb */ }
    }

    /// Select + order metadata per <see cref="MetadataFields"/>. Names not in
    /// the data are skipped; an empty/whiteout list keeps everything.
    public IReadOnlyList<MetadataEntry> SelectMetadata(IReadOnlyList<MetadataEntry> all)
    {
        if (MetadataFields.Count == 0) return all;
        var result = new List<MetadataEntry>(MetadataFields.Count);
        foreach (var name in MetadataFields)
            foreach (var e in all)
                if (string.Equals(e.Label, name, StringComparison.OrdinalIgnoreCase))
                    result.Add(e);
        return result.Count > 0 ? result : all;
    }

    private static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#' or '[') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
        return map;
    }

    private void Apply(IReadOnlyDictionary<string, string> m)
    {
        PreviewWidth = Num(m, "PreviewWidth", PreviewWidth);
        PreviewHeight = Num(m, "PreviewHeight", PreviewHeight);
        AxisLabelColor = Col(m, "AxisLabelColor", AxisLabelColor);
        AxisLabelUnknownColor = Col(m, "AxisLabelUnknownColor", AxisLabelUnknownColor);
        AxisLabelFontSize = Num(m, "AxisLabelFontSize", AxisLabelFontSize);
        CrosshairColor = Col(m, "CrosshairColor", CrosshairColor);
        CrosshairDashed = Flag(m, "CrosshairDashed", CrosshairDashed);
        CrosshairThickness = Num(m, "CrosshairThickness", CrosshairThickness);
        ScrubberColor = Col(m, "ScrubberColor", ScrubberColor);
        BackgroundColor = Col(m, "BackgroundColor", BackgroundColor);
        MetadataFontSize = Num(m, "MetadataFontSize", MetadataFontSize);
        MetadataLabelColor = Col(m, "MetadataLabelColor", MetadataLabelColor);
        MetadataValueColor = Col(m, "MetadataValueColor", MetadataValueColor);
        ShowDisclaimer = Flag(m, "ShowDisclaimer", ShowDisclaimer);
        Orientation = OrientationOf(m, "Orientation", Orientation);
        IntensityPercentileLow = Num(m, "IntensityPercentileLow", IntensityPercentileLow);
        IntensityPercentileHigh = Num(m, "IntensityPercentileHigh", IntensityPercentileHigh);
        PerVolumeWindow = Flag(m, "PerVolumeWindow", PerVolumeWindow);
        MetadataFields = List(m, "MetadataFields", MetadataFields);
    }

    private void Resolve()
    {
        AxisBrush = FrozenBrush(AxisLabelColor);
        AxisUnknownBrush = FrozenBrush(AxisLabelUnknownColor);
        BackgroundBrush = FrozenBrush(BackgroundColor);
        MetadataLabelBrush = FrozenBrush(MetadataLabelColor);
        MetadataValueBrush = FrozenBrush(MetadataValueColor);

        var pen = new Pen(FrozenBrush(CrosshairColor), Math.Max(0.1, CrosshairThickness));
        if (CrosshairDashed) pen.DashStyle = new DashStyle([4, 3], 0);
        pen.Freeze();
        CrosshairPen = pen;

        var sc = ScrubberColor;
        ScrubberFillBrush = FrozenBrush(Color.FromArgb(110, sc.R, sc.G, sc.B));
        ScrubberKnobBrush = FrozenBrush(Color.FromArgb(210, sc.R, sc.G, sc.B));
    }

    // ---- parse helpers: any failure → the supplied fallback ----
    private static Color Col(IReadOnlyDictionary<string, string> m, string k, Color def)
    {
        if (!m.TryGetValue(k, out var v)) return def;
        v = v.Trim();
        // Settings use #RRGGBBAA (alpha last, like CSS/VSCode).
        // WPF ColorConverter expects #AARRGGBB, so reorder when we see 8 digits.
        if (v.Length == 9 && v[0] == '#')
            v = "#" + v.Substring(7, 2) + v.Substring(1, 6);
        try { return (Color)ColorConverter.ConvertFromString(v); }
        catch { return def; }
    }

    // Accepts the macOS-persisted strings ("stored"/"ras"/"las") plus the
    // friendly aliases shown in the default settings file.
    private static MiqOrientation OrientationOf(
        IReadOnlyDictionary<string, string> m, string k, MiqOrientation def)
    {
        if (!m.TryGetValue(k, out var v)) return def;
        switch (v.Trim().ToLowerInvariant())
        {
            case "stored": return MiqOrientation.Stored;
            case "neurological": case "neuro": case "ras": return MiqOrientation.Neurological;
            case "radiological": case "radio": case "las": return MiqOrientation.Radiological;
            default: return def;
        }
    }

    private static string OrientationName(MiqOrientation o) => o switch
    {
        MiqOrientation.Neurological => "neurological",
        MiqOrientation.Radiological => "radiological",
        _ => "stored",
    };

    private static double Num(IReadOnlyDictionary<string, string> m, string k, double def) =>
        m.TryGetValue(k, out var v) &&
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : def;

    private static bool Flag(IReadOnlyDictionary<string, string> m, string k, bool def)
    {
        if (!m.TryGetValue(k, out var v)) return def;
        v = v.Trim();
        if (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" ||
            v.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
        if (v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0" ||
            v.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
        return def;
    }

    private static IReadOnlyList<string> List(
        IReadOnlyDictionary<string, string> m, string k, IReadOnlyList<string> def)
    {
        if (!m.TryGetValue(k, out var v)) return def;
        var list = new List<string>();
        foreach (var p in v.Split(','))
        {
            var t = p.Trim();
            if (t.Length > 0) list.Add(t);
        }
        return list.Count > 0 ? list : def;
    }

    private static Brush FrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private string DefaultText()
    {
        string Hex(Color c) => c.A == 0xFF
            ? $"#{c.R:X2}{c.G:X2}{c.B:X2}"
            : $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}";

        return string.Join("\r\n",
            "; MIQ preview settings — edit a value, save this file, then press",
            "; Space on a file to apply it (no restart). A missing or invalid value",
            "; falls back to its built-in default; delete the whole file to",
            "; regenerate it with defaults.",
            ";",
            "; This file lives in QuickLook's data folder (next to QuickLook.config),",
            "; not in the plugin folder, so it is kept when the MIQ plugin is updated.",
            ";",
            "; Keys are case-insensitive. Colours are #RRGGBB or #RRGGBBAA (AA =",
            "; opacity, like CSS/VSCode). Sizes are in device-independent pixels.",
            "",
            "",
            "; ===================== Behaviour =====================",
            "",
            "; View orientation: stored | neurological | radiological",
            ";   stored        render axes exactly as stored in the file.",
            ";   neurological  canonical anatomical view, patient-LEFT on the",
            ";                 viewer's left (coronal/axial).",
            ";   radiological  same, but patient-LEFT on the viewer's right.",
            "; Sagittal is identical in both (Anterior on the left). Files without",
            "; orientation metadata always fall back to stored.",
            $"Orientation             = {OrientationName(Orientation)}",
            "",
            "; Intensity window — contrast mapping as percentiles (0-100) of voxel",
            "; values pooled across all slices. Narrow [low, high] for more contrast,",
            "; widen it for more headroom.",
            $"IntensityPercentileLow  = {Inv(IntensityPercentileLow)}",
            $"IntensityPercentileHigh = {Inv(IntensityPercentileHigh)}",
            "; Recompute the window per volume while scrubbing a 4-D series (cached",
            "; per volume, so back-navigation stays instant).",
            $"PerVolumeWindow         = {(PerVolumeWindow ? "true" : "false")}",
            "",
            "; Metadata rows to show, in this order; unavailable rows are skipped.",
            "; Available: Format, Dimensions, Spacing, Orientation, Datatype,",
            ";            Volumes, Scaling.",
            $"MetadataFields          = {string.Join(", ", MetadataFields)}",
            "",
            "; Footer disclaimer — MIQ-Win is a research/preview tool, not a medical",
            "; device and not for diagnostic use. Set false to hide the line at the",
            "; bottom of the metadata panel.",
            $"ShowDisclaimer          = {(ShowDisclaimer ? "true" : "false")}",
            "",
            "",
            "; ===================== Appearance =====================",
            "",
            "; Preview window, initial size.",
            $"PreviewWidth            = {Inv(PreviewWidth)}",
            $"PreviewHeight           = {Inv(PreviewHeight)}",
            "",
            "; Canvas / letterbox behind the slices.",
            $"BackgroundColor         = {Hex(BackgroundColor)}",
            "",
            "; Axis orientation labels (R/L/A/P/S/I). The \"unknown\" colour marks an",
            "; axis whose direction can't be determined.",
            $"AxisLabelColor          = {Hex(AxisLabelColor)}",
            $"AxisLabelUnknownColor   = {Hex(AxisLabelUnknownColor)}",
            $"AxisLabelFontSize       = {Inv(AxisLabelFontSize)}",
            "",
            "; Crosshair overlay (appears once you start interacting).",
            $"CrosshairColor          = {Hex(CrosshairColor)}",
            $"CrosshairDashed         = {(CrosshairDashed ? "true" : "false")}",
            $"CrosshairThickness      = {Inv(CrosshairThickness)}",
            "",
            "; Volume scrubber (4-D series). Fill + knob are derived from this at",
            "; lower / higher opacity.",
            $"ScrubberColor           = {Hex(ScrubberColor)}",
            "",
            "; Metadata panel text.",
            $"MetadataFontSize        = {Inv(MetadataFontSize)}",
            $"MetadataLabelColor      = {Hex(MetadataLabelColor)}",
            $"MetadataValueColor      = {Hex(MetadataValueColor)}",
            "");
    }

    private static string Inv(double d) => d.ToString(CultureInfo.InvariantCulture);
}
