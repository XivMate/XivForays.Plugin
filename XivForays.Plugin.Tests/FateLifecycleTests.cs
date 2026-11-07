using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using Xunit;

namespace XivForays.Plugin.Tests;

public class FateLifecycleTests
{
    private const uint Amarok = 1367;
    private const int StartedAt = 1793940000;

    private static Observation At(int secondsAfterStart, uint territory = 732, uint map = 414, Guid? visit = null) => new()
    {
        ObservedAtMs = (StartedAt + secondsAfterStart) * 1000L,
        VisitId = visit ?? Guid.Parse("4463f91b-5bd4-4d59-8830-261375699a11"),
        TerritoryId = territory,
        MapId = map
    };

    private static FateSnapshot Fate(byte state, byte progress) =>
        new(Amarok, state, progress, StartedAt, 100f, 20f, -300f, 30f);

    private static List<Observation> Run(FateLifecycle lifecycle, Observation context, params FateSnapshot[] table)
    {
        var emitted = new List<Observation>();
        lifecycle.Sample(context, table, emitted.Add);
        return emitted;
    }

    [Fact]
    public void RunningThenEndingThenGoneEmitsOneObservedEnd()
    {
        var lifecycle = new FateLifecycle();
        Assert.Equal(ObservationKind.FateStarted, Assert.Single(Run(lifecycle, At(5), Fate(FateLifecycle.Running, 0))).Kind);
        Assert.Equal(ObservationKind.FateUpdated, Assert.Single(Run(lifecycle, At(60), Fate(FateLifecycle.Running, 74))).Kind);

        var end = Assert.Single(Run(lifecycle, At(300), Fate(FateLifecycle.Ending, 100)));
        Assert.Equal(ObservationKind.FateEnded, end.Kind);
        Assert.Equal(FateLifecycle.Ending, end.State);
        Assert.Equal((byte)100, end.Progress);
        Assert.Equal(FateOutcome.Success, end.Outcome);
        Assert.True(end.OutcomeInferred);
        Assert.Equal(300u, end.DurationSeconds);
        Assert.Equal(Amarok, end.GameId);

        // Still in the table as Ending/Ended for a while, then gone: nothing more.
        Assert.Empty(Run(lifecycle, At(301), Fate(FateLifecycle.Ending, 100)));
        Assert.Empty(Run(lifecycle, At(302), Fate(FateLifecycle.Ended, 100)));
        Assert.Empty(Run(lifecycle, At(310)));
    }

    [Theory]
    [InlineData(FateLifecycle.Ended, (byte)100, (int)FateOutcome.Success)]
    [InlineData(FateLifecycle.Failed, (byte)40, (int)FateOutcome.Failed)]
    public void ObservedEndStateIsNotInferred(byte state, byte progress, int outcome)
    {
        var lifecycle = new FateLifecycle();
        Run(lifecycle, At(5), Fate(FateLifecycle.Running, 10));
        var end = Assert.Single(Run(lifecycle, At(200), Fate(state, progress)));
        Assert.Equal(ObservationKind.FateEnded, end.Kind);
        Assert.Equal((FateOutcome)outcome, end.Outcome);
        Assert.False(end.OutcomeInferred);
    }

    [Fact]
    public void RunningThenGoneAt100IsInferredSuccess()
    {
        var lifecycle = new FateLifecycle();
        Run(lifecycle, At(5), Fate(FateLifecycle.Running, 0));
        Run(lifecycle, At(100), Fate(FateLifecycle.Running, 100));

        var end = Assert.Single(Run(lifecycle, At(120)));
        Assert.Equal(ObservationKind.FateEnded, end.Kind);
        Assert.Equal(FateOutcome.Success, end.Outcome);
        Assert.True(end.OutcomeInferred);
        Assert.Equal((byte)100, end.Progress);
        Assert.Equal(FateLifecycle.Running, end.State);
        Assert.Equal(120u, end.DurationSeconds);
    }

    [Fact]
    public void RunningThenGoneAt40IsInferredUnknown()
    {
        var lifecycle = new FateLifecycle();
        Run(lifecycle, At(5), Fate(FateLifecycle.Running, 40));

        var end = Assert.Single(Run(lifecycle, At(900)));
        Assert.Equal(ObservationKind.FateEnded, end.Kind);
        Assert.Equal(FateOutcome.Unknown, end.Outcome);
        Assert.True(end.OutcomeInferred);
        Assert.Equal((byte)40, end.Progress);
        Assert.Equal(900u, end.DurationSeconds);
    }

    [Fact]
    public void ZoneChangeMidFateIsDisappearedInTheOldZone()
    {
        var lifecycle = new FateLifecycle();
        Run(lifecycle, At(5), Fate(FateLifecycle.Running, 40));

        var emitted = new List<Observation>();
        lifecycle.LeaveVisit((StartedAt + 60) * 1000L, emitted.Add);
        var gone = Assert.Single(emitted);
        Assert.Equal(ObservationKind.FateDisappeared, gone.Kind);
        Assert.Equal(732u, gone.TerritoryId); // old zone, so the server's TerritoryId/MapId > 0 check passes
        Assert.Equal(414u, gone.MapId);
        Assert.Null(gone.Outcome);
        Assert.Null(gone.DurationSeconds);

        // New zone, no FATEs: nothing left to end.
        Assert.Empty(Run(lifecycle, At(70, 763, 415, Guid.NewGuid())));
    }

    [Fact]
    public void VanishAfterBetweenAreasGapIsDisappearedNotEnded()
    {
        var lifecycle = new FateLifecycle();
        Run(lifecycle, At(5), Fate(FateLifecycle.Running, 40));
        lifecycle.Interrupt();

        var gone = Assert.Single(Run(lifecycle, At(30)));
        Assert.Equal(ObservationKind.FateDisappeared, gone.Kind);
    }

    [Fact]
    public void FateStillVisibleAfterGapKeepsTracking()
    {
        var lifecycle = new FateLifecycle();
        Run(lifecycle, At(5), Fate(FateLifecycle.Running, 40));
        lifecycle.Interrupt();
        Assert.Empty(Run(lifecycle, At(30), Fate(FateLifecycle.Running, 40)));

        // The gap is over: a later vanish is a real end again.
        Assert.Equal(ObservationKind.FateEnded, Assert.Single(Run(lifecycle, At(60))).Kind);
    }
}
