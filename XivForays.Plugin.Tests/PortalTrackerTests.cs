using System.Text.Json;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using Xunit;

namespace XivForays.Plugin.Tests;

public class PortalTrackerTests
{
    private static readonly Guid Visit = Guid.Parse("4463f91b-5bd4-4d59-8830-261375699a11");
    private const long Start = 1793940000000;

    private static Observation At(int seconds, Guid? visit = null, uint territory = 827, uint map = 515) => new()
    {
        ObservedAtMs = Start + seconds * 1000L,
        VisitId = visit ?? Visit,
        TerritoryId = territory,
        MapId = map,
        WeatherId = 7
    };

    private static PortalSnapshot Blue(ulong id = 1, float x = 100, float y = 5, float z = -200) =>
        new(id, PortalTracker.BlueBaseId, x, y, z);

    private static PortalSnapshot Red(ulong id = 2, float x = 300, float y = 5, float z = 50) =>
        new(id, PortalTracker.RedBaseId, x, y, z);

    private static List<Observation> Run(PortalTracker tracker, Observation context, params PortalSnapshot[] table)
    {
        var emitted = new List<Observation>();
        tracker.Sample(context, table, emitted.Add);
        return emitted;
    }

    [Fact]
    public void AppearanceRecordsKindColourPositionTimeAndContext()
    {
        var tracker = new PortalTracker();
        var records = Run(tracker, At(10), Blue(), Red());

        Assert.Equal(2, records.Count);
        var blue = records[0];
        Assert.Equal(ObservationKind.Portal, blue.Kind);
        Assert.Equal(PortalTracker.BlueBaseId, blue.GameId);
        Assert.Equal((byte)1, blue.PortalColour);
        Assert.Equal((byte)1, blue.State);
        Assert.Equal((100f, 5f, -200f), (blue.X!.Value, blue.Y!.Value, blue.Z!.Value));
        Assert.Equal(Start + 10_000, blue.ObservedAtMs);
        Assert.Equal(Visit, blue.VisitId);
        Assert.Equal((byte)7, blue.WeatherId);
        Assert.Equal((byte)2, records[1].PortalColour);
        Assert.NotEqual(blue.ObjectTrackId, records[1].ObjectTrackId);
    }

    [Fact]
    public void SameObjectInTheSameVisitIsRecordedOnce()
    {
        var tracker = new PortalTracker();
        Assert.Single(Run(tracker, At(0), Blue()));
        Assert.Empty(Run(tracker, At(1), Blue()));
        Assert.Empty(Run(tracker, At(2), Blue()));
    }

    [Fact]
    public void DuplicateRowsInOneScanAreRecordedOnce()
    {
        Assert.Single(Run(new PortalTracker(), At(0), Blue(), Blue()));
    }

    [Fact]
    public void NewVisitRecordsTheSameObjectIdAgain()
    {
        var tracker = new PortalTracker();
        var first = Assert.Single(Run(tracker, At(0), Blue()));
        var second = Assert.Single(Run(tracker, At(60, Guid.NewGuid()), Blue()));
        Assert.NotEqual(first.VisitId, second.VisitId);
        Assert.NotEqual(first.ObjectTrackId, second.ObjectTrackId);
    }

    [Fact]
    public void ReusedObjectIdAtAnotherPointIsANewAppearance()
    {
        var tracker = new PortalTracker();
        Run(tracker, At(0), Blue());
        var next = Assert.Single(Run(tracker, At(30), Blue(x: 400, z: 90)));
        Assert.Equal((byte)1, next.State);
        Assert.Equal(400f, next.X);
    }

    [Fact]
    public void GoneRecordAfterGraceCarriesLastSeenTimeAndDuration()
    {
        var tracker = new PortalTracker();
        var appeared = Assert.Single(Run(tracker, At(0), Blue()));
        Run(tracker, At(40), Blue());

        // Out of the table for less than the grace period: nothing yet.
        Assert.Empty(Run(tracker, At(42)));
        var gone = Assert.Single(Run(tracker, At(46)));
        Assert.Equal(ObservationKind.Portal, gone.Kind);
        Assert.Equal((byte)0, gone.State);
        Assert.Equal(appeared.ObjectTrackId, gone.ObjectTrackId);
        Assert.Equal(Start + 40_000, gone.ObservedAtMs);
        Assert.Equal(40_000u, gone.VisibleMs);
        Assert.Equal(100f, gone.X);

        // Only once.
        Assert.Empty(Run(tracker, At(60)));
    }

    [Fact]
    public void ReturningAtTheSameSpotWithinGraceIsNotRecordedAgain()
    {
        var tracker = new PortalTracker();
        Run(tracker, At(0), Blue());
        Assert.Empty(Run(tracker, At(2)));
        Assert.Empty(Run(tracker, At(3), Blue()));
        Assert.Empty(Run(tracker, At(5)));
    }

    [Fact]
    public void LeavingTheFieldMapEndsActivePortalsAtOnce()
    {
        var tracker = new PortalTracker();
        Run(tracker, At(0), Blue());
        var gone = Assert.Single(Run(tracker, At(3, map: 520), Blue()));
        Assert.Equal((byte)0, gone.State);
    }

    [Theory]
    [InlineData(732u, 515u)]
    [InlineData(827u, 520u)]
    [InlineData(795u, 515u)]
    public void OtherZonesAndMapsRecordNothing(uint territory, uint map)
    {
        Assert.Empty(Run(new PortalTracker(), At(0, territory: territory, map: map), Blue(), Red()));
    }

    [Fact]
    public void OtherObjectsAreIgnored()
    {
        Assert.Empty(Run(new PortalTracker(), At(0), new PortalSnapshot(9, 2012936, 1, 2, 3)));
    }

    [Fact]
    public void WireRecordHasNoPlayerOrGameObjectIdentity()
    {
        var record = Assert.Single(Run(new PortalTracker(), At(0), Blue(id: 123456789)));
        var json = JsonSerializer.Serialize(record, ObservationJson.Options);
        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(21, doc.RootElement.GetProperty("kind").GetInt32());
        Assert.Contains("portalColour", names);
        Assert.Contains("objectTrackId", names);
        Assert.DoesNotContain("eorzeaMinutes", names);
        Assert.DoesNotContain("123456789", json);
        Assert.DoesNotContain("name", names, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void DrawRangeFlickerAtTheSamplingRateGivesOneAppearAndOneGone()
    {
        var tracker = new PortalTracker();
        var emitted = new List<Observation>();
        // 250 ms ticks; the node drops out of the table for 1 s twice while it is up, then leaves for good.
        for (var tick = 0; tick < 200; tick++)
        {
            var ms = tick * 250L;
            var inTable = ms < 20_000 && !(ms is >= 5_000 and < 6_000) && !(ms is >= 11_000 and < 12_000);
            var context = At(0) with { ObservedAtMs = Start + ms };
            tracker.Sample(context, inTable ? new[] { Blue() } : Array.Empty<PortalSnapshot>(), emitted.Add);
        }

        Assert.Equal(1, emitted.Count(o => o.State == 1));
        Assert.Equal(1, emitted.Count(o => o.State == 0));
        Assert.Equal(emitted[0].ObjectTrackId, emitted[1].ObjectTrackId);
    }

    [Fact]
    public void SignalTickCallsSamplePortals()
    {
        // Guard against the recorder going dead again: SamplePortals must be invoked from the sampling loop.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "XivForays.Plugin", "Gathering", "ForaySignalModule.cs")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "XivForays.Plugin", "Gathering", "ForaySignalModule.cs"));
        var start = source.IndexOf("private void SignalTick()", StringComparison.Ordinal);
        var end = source.IndexOf("private void SamplePortals(", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        Assert.Contains("SamplePortals(context);", source.Substring(start, end - start));
    }
}
