using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// Maps one SMAPI console line to a CSS class, mirroring SMAPI's own
/// console colors:
///   ERROR → red · "can update" lines → purple · WARN → yellow
///   INFO → blue · TRACE/DEBUG → gray · [JuniGrid] → green
/// </summary>
public static class LogLineClassifier
{
    // SMAPI prints update alerts as:
    //   [SMAPI]    DynamicShader 1.0.41: https://… (you have 1.0.39)
    // The redirected stdout has no ALERT level tag, so "(you have …)" is
    // the only stable marker of these lines.
    private static readonly Regex YouHaveVersion =
        new(@"\(\s*you have\s+[\d.]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "Loaded" filter: SMAPI's "Loaded N mods:" / "Loaded N content packs:" block and its entry lines
    // entries look like:  [.. INFO SMAPI]    Cloudy Skies 1.9.1 by Khloe Leclair | ...
    private static readonly Regex LoadedHeader =
        new(@"Loaded \d+ (mods|content packs):", RegexOptions.Compiled);
    private static readonly Regex LoadedEntry =
        new(@"^\[[^\]]*\]\s{2,}\S.*\s\d+(\.\d+)+[\w.-]*\s+by\s+.+\|", RegexOptions.Compiled);

    /// <summary>Whether the line belongs to the given filter category. err/upd use the same rules as coloring;
    /// loaded is the block of successfully loaded mods / content packs.</summary>
    public static bool MatchesFilter(string line, string filter) => filter switch
    {
        "err" => Classify(line) == "err",
        "upd" => Classify(line) == "upd",
        "loaded" => LoadedHeader.IsMatch(line) || LoadedEntry.IsMatch(line),
        _ => true,
    };

    public static string Classify(string line)
    {
        if (line.StartsWith("[JuniGrid]")) return "sys";

        // Errors first — a failing update check must stay red, not purple.
        if (line.StartsWith("[ERR]")
            || line.Contains(" ERROR ")
            || line.Contains("[ERROR]"))
            return "err";

        // Lines from log files carry their own level tag ([HH:MM:SS LEVEL SMAPI]); the tag takes precedence over content heuristics:
        // ordinary INFO lines whose text happens to contain "update" (e.g. an author credit "1.6 update by …") are no longer misclassified as updates.
        if (line.Contains(" ALERT ")) return "upd";
        if (line.Contains(" WARN ")) return "warn";
        if (line.Contains(" INFO ")) return "info";
        if (line.Contains(" TRACE ") || line.Contains(" DEBUG ")) return "trace";

        // Only lines without a level tag (e.g. forwarded stderr) go through the heuristics: SMAPI update notices are reliably identified
        // by "update" / "(you have x)" (SMAPI's console renders update notices in magenta).
        if (line.Contains("update", StringComparison.OrdinalIgnoreCase)
            || YouHaveVersion.IsMatch(line))
            return "upd";

        return "";
    }
}
