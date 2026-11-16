using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using XivMate.DataGathering.Forays.Dalamud.Core;

namespace XivForays.Plugin.Tests;

/// <summary>
/// The 14 live "Collector overlay map view" lines of 16 Nov 2026 (UI scale 1.5), parsed back into map frames. Lines up
/// to 13:01:46 are the released 0.0.0.16 (offset from the map agent); 13:02:43 is a test build that used the Map sheet
/// offset and forced the image part model.
/// </summary>
internal static class MapLogLines
{
    public const string Text = """
        10:59:47 map 467, size factor 100, offset 0/0, image 721x535 at -1343/292 to -261/1094, transform 1.5/1.5, walked scale 1.5/1.5, addon scale 1.5, part 215/636 1836x1364, frame -1343/292 to -284/1037, zoom 0.3929, pan 113.1/292.9, component zoom 0.3929 pan 113.1/292.9 size 724x535, arrow -800/692, player -800/692, model image part, map check 0 px; model gaps: image part 0, image centre +pan 369, image centre -pan 3, image centre +pan/zoom 655, image centre -pan/zoom 287, frame centre +pan 339, frame centre -pan 31, frame centre +pan/zoom 625, frame centre -pan/zoom 317
        11:02:28 map 467, size factor 100, offset 0/0, image 551x506 at -1343/292 to -516/1051, transform 1.5/1.5, walked scale 1.5/1.5, addon scale 1.5, part 647/761 1404x1288, frame -1343/292 to -284/1037, zoom 0.3929, pan 544.5/418.8, component zoom 0.3929 pan 544.5/418.8 size 724x535, arrow -800/692, player -799/695, model image part, map check 2 px; model gaps: image part 3, image centre +pan 698, image centre -pan 130, image centre +pan/zoom 1323, image centre -pan/zoom 742, frame centre +pan 784, frame centre -pan 29, frame centre +pan/zoom 1409, frame centre -pan/zoom 652
        11:02:45 map 467, size factor 100, offset 0/0, image 724x540 at -1343/292 to -257/1102, transform 1.5/1.5, walked scale 1.5/1.5, addon scale 1.5, part 1292/1230 472x352, frame -1343/292 to -284/1037, zoom 1.5357, pan 504.3/381, component zoom 1.5357 pan 504.3/381 size 724x535, arrow -800/692, player -799/694, model image part, map check 2 px; model gaps: image part 2, image centre +pan 2914, image centre -pan 4, image centre +pan/zoom 2407, image centre -pan/zoom 511, frame centre +pan 2884, frame centre -pan 31, frame centre +pan/zoom 2376, frame centre -pan/zoom 481
        11:03:03 map 467, size factor 100, offset 0/0, image 482x425 at -1343/292 to -620/929, transform 1.5/1.5, walked scale 1.5/1.5, addon scale 1.5, part 121/348 1928x1700, frame -1343/292 to -284/1037, zoom 0.25, pan 545.8/394.3, component zoom 0.25 pan 545.8/394.3 size 724x535, arrow -799/692, player -799/693, model image part, map check 0 px; model gaps: image part 1, image centre +pan 313, image centre -pan 199, image centre +pan/zoom 1068, image centre -pan/zoom 953, frame centre +pan 478, frame centre -pan 31, frame centre +pan/zoom 1235, frame centre -pan/zoom 785
        11:06:11 map 467, size factor 100, offset 0/0, image 728x536 at -1343/292 to -251/1096, transform 1.5/1.5, walked scale 1.5/1.5, addon scale 1.5, part 1385/1280 364x268, frame -1343/292 to -284/1037, zoom 2, pan 542.2/390.6, component zoom 2 pan 542.2/390.6 size 724x535, arrow -800/692, player -799/695, model image part, map check 3 px (aligned); model gaps: image part 3, image centre +pan 4013, image centre -pan 3, image centre +pan/zoom 3010, image centre -pan/zoom 1006, frame centre +pan 3982, frame centre -pan 31, frame centre +pan/zoom 2980, frame centre -pan/zoom 975
        11:06:33 map 515, size factor 100, offset 0/-475, image 724x537 at -1343/292 to -257/1097, transform 1.5/1.5, walked scale 1.5/1.5, addon scale 1.5, part 330/148 1268x940, frame -1343/292 to -284/1037, zoom 0.5714, pan -60.2/-407.6, component zoom 0.5714 pan -60.2/-407.6 size 724x535, arrow -800/692, player -903/-819, model image centre +pan, map check 1514 px; model gaps: image part 813, image centre +pan 1514, image centre -pan 812, image centre +pan/zoom 1779, image centre -pan/zoom 551, frame centre +pan 1545, frame centre -pan 842, frame centre +pan/zoom 1810, frame centre -pan/zoom 581
        11:47:35 map 467, size factor 100, offset 0/0, image 582x536 at -1718/281 to -845/1085, transform 1.5/1.5, walked scale 1.5/1.5, addon scale 1.5, part 1770/1628 2328x2144, frame -1718/281 to -659/1026, zoom 0.25, pan 145.6/-374.6, component zoom 0.25 pan 145.6/-374.6 size 724x535, arrow -1175/681, player -1188/653, model frame centre -pan, map check 30 px (aligned); model gaps: image part 1086, image centre +pan 279, image centre -pan 107, image centre +pan/zoom 720, image centre -pan/zoom 502, frame centre +pan 323, frame centre -pan 31, frame centre +pan/zoom 775, frame centre -pan/zoom 432
        11:48:36 map 467, size factor 100, offset 0/0, image 583x536 at -1718/281 to -843/1085, transform 1.5/1.5, walked scale 1.5/1.5, addon scale 1.5, part 1766/1643 2332x2144, frame -1718/281 to -659/1026, zoom 0.25, pan 141.4/-359.7, component zoom 0.25 pan 141.4/-359.7 size 724x535, arrow -1175/681, player -1188/653, model frame centre -pan, map check 30 px (aligned); model gaps: image part 1086, image centre +pan 268, image centre -pan 106, image centre +pan/zoom 691, image centre -pan/zoom 485, frame centre +pan 312, frame centre -pan 31, frame centre +pan/zoom 746, frame centre -pan/zoom 414
        11:50:37 map 467, size factor 100, offset 0/0, image 584x536 at -1718/281 to -842/1085, transform 1.5/1.5, walked scale 1.5/1.5, addon scale 1.5, part 1762/1645 2336x2144, frame -1718/281 to -659/1026, zoom 0.25, pan 137.7/-357.9, component zoom 0.25 pan 137.7/-357.9 size 724x535, arrow -1175/681, player -1188/653, model frame centre -pan, map check 30 px (aligned); model gaps: image part 1086, image centre +pan 267, image centre -pan 105, image centre +pan/zoom 687, image centre -pan/zoom 481, frame centre +pan 309, frame centre -pan 31, frame centre +pan/zoom 740, frame centre -pan/zoom 411
        11:50:51 map 467, size factor 100, offset 0/0, image 584x536 at -1718/281 to -842/1085, transform 1.5/1.5, walked scale 1.5/1.5, addon scale 1.5, part 1761/1645 2336x2144, frame -1718/281 to -659/1026, zoom 0.25, pan 136.7/-357.3, component zoom 0.25 pan 136.7/-357.3 size 724x535, arrow -1175/681, player -1188/653, model frame centre -pan, map check 30 px (aligned); model gaps: image part 1085, image centre +pan 266, image centre -pan 105, image centre +pan/zoom 685, image centre -pan/zoom 479, frame centre +pan 309, frame centre -pan 31, frame centre +pan/zoom 739, frame centre -pan/zoom 410
        12:05:01 map 467, size factor 100, offset 0/0, image 725x489 at -1718/281 to -630/1014, transform 1.5/1.5, walked scale 1.5/1.5, addon scale 1.5, part 735/94 2900x1956, frame -1718/281 to -659/1026, zoom 0.25, pan -889.6/140.5, component zoom 0.25 pan -889.6/140.5 size 724x535, arrow -1175/681, player -1188/653, model frame centre -pan, map check 30 px (aligned); model gaps: image part 768, image centre +pan 670, image centre -pan 34, image centre +pan/zoom 1683, image centre -pan/zoom 1020, frame centre +pan 685, frame centre -pan 31, frame centre +pan/zoom 1698, frame centre -pan/zoom 1005
        12:06:37 map 515, size factor 100, offset 0/-475, image 635x536 at -1718/281 to -765/1085, transform 1.5/1.5, walked scale 1.5/1.5, addon scale 1.5, part 1558/1600 2540x2144, frame -1718/281 to -659/1026, zoom 0.25, pan -66.5/-402.3, component zoom 0.25 pan -66.5/-402.3 size 724x535, arrow -1175/681, player -1291/25, model image centre +pan, map check 666 px; model gaps: image part 1361, image centre +pan 667, image centre -pan 361, image centre +pan/zoom 1125, image centre -pan/zoom 98, frame centre +pan 689, frame centre -pan 384, frame centre +pan/zoom 1147, frame centre -pan/zoom 92
        13:01:46 map 515, size factor 100, offset 0/-475, image 636x536 at -1718/281 to -764/1085, transform 1.5/1.5, walked scale 1.5/1.5, addon scale 1.5, part 1556/1598 2544x2144, frame -1718/281 to -659/1026, zoom 0.25, pan -68.7/-404.6, component zoom 0.25 pan -68.7/-404.6 size 724x535, arrow -1175/681, player -1111/752, model frame centre -pan/zoom, map check 95 px; model gaps: image part 1361, image centre +pan 669, image centre -pan 361, image centre +pan/zoom 1130, image centre -pan/zoom 101, frame centre +pan 691, frame centre -pan 384, frame centre +pan/zoom 1151, frame centre -pan/zoom 96
        13:02:43 map 515, size factor 100, offset 0/475 (sheet; agent size factor 100, offset 0/-475), image 636x536 at -1718/281 to -764/1085, transform 1.5/1.5, walked scale 1.5/1.5, addon scale 1.5, part 1556/1598 2544x2144, frame -1718/281 to -659/1026, zoom 0.25, pan -68.7/-404.6, component zoom 0.25 pan -68.7/-404.6 size 724x535, arrow -1175/681, player -1943/-86, model image part, map check 1085 px; model gaps: image part 1086, image centre +pan 324, image centre -pan 66, image centre +pan/zoom 782, image centre -pan/zoom 457, frame centre +pan 338, frame centre -pan 31, frame centre +pan/zoom 799, frame centre -pan/zoom 432
        """;

    /// <summary>The Map sheet offset of Hydatos (map 515); Pagos (467) is 0/0.</summary>
    public const float HydatosSheetOffsetY = 475f;

    public sealed record Line(
        string Time,
        uint MapId,
        float SizeFactor,
        Vector2 LoggedOffset,
        MapFrame Frame,
        Vector2 ComponentSize,
        Vector2 Arrow,
        Vector2 PlayerScreen,
        int LoggedModel,
        float[] LoggedGaps)
    {
        /// <summary>The offset the maths should use: the Map sheet's (Hydatos 0/475, Pagos 0/0).</summary>
        public Vector2 SheetOffset => MapId == 515 ? new Vector2(0, HydatosSheetOffsetY) : Vector2.Zero;

        /// <summary>The player's world x/z, worked back from the logged player point and the model that drew it.</summary>
        public Vector2 PlayerWorld
        {
            get
            {
                var view = MapView.From(Frame, LoggedModel, LoggedOffset.X, LoggedOffset.Y, SizeFactor);
                var texel = (PlayerScreen - view.Origin) / view.PixelsPerTexel;
                return new Vector2(MapMath.FromTexture(texel.X, LoggedOffset.X, SizeFactor),
                    MapMath.FromTexture(texel.Y, LoggedOffset.Y, SizeFactor));
            }
        }

        public override string ToString() => Time + " map " + MapId;
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    private static Vector2 V(Match m, string x, string y) => new(F(m.Groups[x].Value), F(m.Groups[y].Value));

    private const string N = @"-?[0-9.]+";

    private static readonly Regex Pattern = new(
        $@"^(?<t>\S+) map (?<map>\d+), size factor (?<sf>{N}), offset (?<ox>{N})/(?<oy>{N}).*?, image (?<iw>{N})x(?<ih>{N}) at (?<ix0>{N})/(?<iy0>{N}) to (?<ix1>{N})/(?<iy1>{N}), transform (?<tx>{N})/(?<ty>{N}).*?, part (?<pu>{N})/(?<pv>{N}) (?<pw>{N})x(?<ph>{N}), frame (?<fx0>{N})/(?<fy0>{N}) to (?<fx1>{N})/(?<fy1>{N}), zoom (?<z>{N}), pan (?<px>{N})/(?<py>{N}), component .*? size (?<cw>{N})x(?<ch>{N}), arrow (?<ax>{N})/(?<ay>{N}), player (?<sx>{N})/(?<sy>{N}), model (?<model>[^,]+), map check .*?; model gaps: (?<gaps>.*)$");

    private static readonly Regex Gap = new($@"(?<name>[a-z /+\-]+?) (?<v>{N}|-)(,|$)");

    public static IReadOnlyList<Line> All { get; } = Parse();

    private static List<Line> Parse()
    {
        var lines = new List<Line>();
        foreach (var raw in Text.Split('\n'))
        {
            var text = raw.Trim();
            if (text.Length == 0)
                continue;
            var m = Pattern.Match(text);
            if (!m.Success)
                throw new FormatException("log line not parsed: " + text[..40]);
            var frame = new MapFrame(V(m, "ix0", "iy0"), V(m, "ix1", "iy1"), V(m, "fx0", "fy0"), V(m, "fx1", "fy1"),
                F(m.Groups["tx"].Value), F(m.Groups["z"].Value), V(m, "px", "py"),
                new Vector4(F(m.Groups["pu"].Value), F(m.Groups["pv"].Value), F(m.Groups["pw"].Value), F(m.Groups["ph"].Value)),
                V(m, "cw", "ch"));
            var gaps = new float[9];
            Array.Fill(gaps, float.NaN);
            foreach (Match g in Gap.Matches(m.Groups["gaps"].Value))
            {
                var model = ModelOf(g.Groups["name"].Value.Trim());
                gaps[model] = g.Groups["v"].Value == "-" ? float.NaN : F(g.Groups["v"].Value);
            }

            lines.Add(new Line(m.Groups["t"].Value, uint.Parse(m.Groups["map"].Value, CultureInfo.InvariantCulture),
                F(m.Groups["sf"].Value), V(m, "ox", "oy"), frame, V(m, "cw", "ch"), V(m, "ax", "ay"), V(m, "sx", "sy"),
                ModelOf(m.Groups["model"].Value.Trim()), gaps));
        }

        return lines;
    }

    public static int ModelOf(string name)
    {
        for (var i = 0; i < 9; i++)
            if (MapModels.Name(i) == name)
                return i;
        throw new FormatException("unknown model " + name);
    }
}
