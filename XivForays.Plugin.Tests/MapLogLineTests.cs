using System.Numerics;
using XivMate.DataGathering.Forays.Dalamud.Core;
using Xunit;
using Xunit.Abstractions;

namespace XivForays.Plugin.Tests;

/// <summary>All 14 live map view lines of 16 Nov: the old models reproduced, and where the map really is.</summary>
public class MapLogLineTests(ITestOutputHelper output)
{
    public static TheoryData<int> Lines()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < MapLogLines.All.Count; i++)
            data.Add(i);
        return data;
    }

    [Fact]
    public void AllFourteenLinesParse() => Assert.Equal(14, MapLogLines.All.Count);

    [Theory]
    [MemberData(nameof(Lines))]
    public void TheNineOldModelsReproduceTheLoggedGaps(int index)
    {
        // With each line's own logged offset (the agent's on 0.0.0.16, the sheet's on 13:02:43).
        var line = MapLogLines.All[index];
        var player = line.PlayerWorld;
        for (var model = 0; model < 9; model++)
        {
            var view = MapView.From(line.Frame, model, line.LoggedOffset.X, line.LoggedOffset.Y, line.SizeFactor);
            var gap = Vector2.Distance(view.ToScreen(player.X, player.Y), line.Arrow);
            var logged = line.LoggedGaps[model];
            Assert.False(float.IsNaN(logged), $"{line} model {model} not logged");
            Assert.True(MathF.Abs(gap - logged) <= MathF.Max(3f, logged * 0.01f),
                $"{line} {MapModels.Name(model)}: computed {gap:0}, logged {logged:0}");
        }
    }

    [Theory]
    [MemberData(nameof(Lines))]
    public void TheMapCentreModelLandsOnTheLoggedArrow(int index)
    {
        // The new code on every line: the sheet offset (Hydatos 0/475, Pagos 0/0), the model the picker chooses, the
        // player before any shift within 5 px of the logged arrow.
        var line = MapLogLines.All[index];
        var player = line.PlayerWorld;
        var sticky = -1;
        var gaps = new float[MapModels.Count];
        var view = MapCalibration.Resolve(line.Frame, line.SheetOffset.X, line.SheetOffset.Y, line.SizeFactor, player, line.Arrow,
            ref sticky, gaps, out var model, out var confirmed);
        Assert.Equal(MapModels.MapCentre, model);
        Assert.True(confirmed);
        Assert.Equal(MapModels.MapCentre, sticky);
        var raw = (view with { Shift = Vector2.Zero }).ToScreen(player.X, player.Y);
        var gap = Vector2.Distance(raw, line.Arrow);
        output.WriteLine($"{line.Time}\tmap {line.MapId}\tzoom {line.Frame.Zoom}\tarrow {line.Arrow.X:0}/{line.Arrow.Y:0}\tnew {raw.X:0.0}/{raw.Y:0.0}\tgap {gap:0.0} px" +
                         $"\told frame centre -pan {gaps[6]:0}");
        Assert.InRange(gap, 0f, 5f);
        // The old frame centre -pan error is the frame node's centre against the map centre, not a stray constant.
        var oldCentre = (line.Frame.FrameMin + line.Frame.FrameMax) / 2f;
        Assert.Equal(Vector2.Distance(oldCentre, line.Frame.MapCentre), gaps[6], 3f);
    }

    [Fact]
    public void TheOldHydatosLinesWithTheSheetOffset()
    {
        // The 0.0.0.16 Hydatos lines (agent offset 0/-475) recomputed with the sheet offset 0/475: printed for the result.
        foreach (var line in MapLogLines.All)
        {
            if (line.MapId != 515 || line.LoggedOffset.Y == MapLogLines.HydatosSheetOffsetY)
                continue;
            var player = line.PlayerWorld;
            var text = new System.Text.StringBuilder(line + " sheet offset:");
            for (var model = 0; model < 9; model++)
            {
                var view = MapView.From(line.Frame, model, 0, MapLogLines.HydatosSheetOffsetY, line.SizeFactor);
                text.Append(' ').Append(MapModels.Name(model)).Append(' ')
                    .Append(Vector2.Distance(view.ToScreen(player.X, player.Y), line.Arrow).ToString("0"));
            }

            output.WriteLine(text.ToString());
        }
    }
}
