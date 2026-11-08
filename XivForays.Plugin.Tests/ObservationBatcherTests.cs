using System.Text.Json;
using XivMate.DataGathering.Forays.Dalamud.Core;
using XivMate.DataGathering.Forays.Dalamud.Models;
using Xunit;

namespace XivForays.Plugin.Tests;

/// <summary>Upload batching: a byte cap under the live server's 512 KB limit, 413 split-and-retry, 429 kept and retried.</summary>
public class ObservationBatcherTests
{
    private static Observation Record(int i) => new()
    {
        Kind = ObservationKind.EnemySpawnPose,
        ObservedAtMs = 1793940205000 + i,
        VisitId = Guid.Parse("4463f91b-5bd4-4d59-8830-261375699a11"),
        TerritoryId = 732,
        MapId = 414,
        GameId = 7777,
        EnemyTrackId = Guid.NewGuid(),
        Pose = new EnemyPose
        {
            BnpcId = 7777, Position = [1, 2, 3], DrawPosition = [1, 2, 3], DrawRotation = [0, 0.5f, 0, 0.8f],
            SkeletonRotation = [0.07f, 0.57f, -0.01f, 0.81f], Timelines = [[0, 3, 1]], Statuses = [[1, 2, 3, 6]],
            Equipment = Enumerable.Range(0, 10).ToArray(), Attached = [[0.1f, 1.2f, 0.3f, 1]]
        }
    };

    [Fact]
    public void BatchesStayUnderTheByteCapAndTheRecordCap()
    {
        var batcher = new ObservationBatcher();
        for (var i = 0; i < 1000; i++)
            batcher.Enqueue(Record(i));

        var sent = 0;
        while (batcher.Next() is { } batch)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(batch, ObservationJson.Options).Length;
            Assert.True(bytes <= UploadPolicy.MaxBatchBytes, $"batch of {batch.Observations.Length} is {bytes} bytes");
            Assert.True(batch.Observations.Length <= UploadPolicy.MaxBatchSize);
            Assert.Equal(1, batch.SchemaVersion);
            sent += batch.Observations.Length;
            batcher.Complete(batch, UploadOutcome.Accepted, keep: false);
        }

        Assert.Equal(1000, sent);
        Assert.True(UploadPolicy.MaxBatchBytes <= 400 * 1024);
    }

    [Fact]
    public void LargeRecordsAreCutByBytesNotOnlyByCount()
    {
        // 2 KB records: a 256-record batch would be ~512 KB, over the live server's limit.
        var batcher = new ObservationBatcher(_ => 2048);
        for (var i = 0; i < 300; i++)
            batcher.Enqueue(Record(i));
        var batch = batcher.Next()!;
        Assert.InRange(batch.Observations.Length, 150, 200);
        Assert.True(ObservationBatcher.EnvelopeBytes + batch.Observations.Length * 2049 <= UploadPolicy.MaxBatchBytes);
    }

    [Fact]
    public void TooLargeSplitsTheBatchAndSendsBothHalvesBeforeNewRecords()
    {
        var batcher = new ObservationBatcher(_ => 100);
        for (var i = 0; i < 8; i++)
            batcher.Enqueue(Record(i));
        var first = batcher.Next()!;
        Assert.Equal(8, first.Observations.Length);
        batcher.Enqueue(Record(100)); // arrives while the big batch is out

        var policy = new UploadPolicy();
        var outcome = UploadPolicy.Classify(413, timedOut: false);
        Assert.Equal(UploadOutcome.TooLarge, outcome);
        Assert.False(policy.Record(outcome, 10_000));
        Assert.True(policy.CanAttempt(10_000)); // no backoff for a split
        batcher.Complete(first, outcome, keep: false);

        var a = batcher.Next()!;
        Assert.Equal(first.Observations[..4], a.Observations);
        Assert.NotEqual(first.BatchId, a.BatchId);
        batcher.Complete(a, UploadOutcome.Accepted, false);
        var b = batcher.Next()!;
        Assert.Equal(first.Observations[4..], b.Observations);
        batcher.Complete(b, UploadOutcome.Accepted, false);
        var c = batcher.Next()!;
        Assert.Single(c.Observations);
        Assert.Equal(1793940205100, c.Observations[0].ObservedAtMs);
    }

    [Fact]
    public void ASingleRecordStillTooLargeIsDroppedNotRetriedForever()
    {
        var batcher = new ObservationBatcher();
        batcher.Enqueue(Record(0));
        var batch = batcher.Next()!;
        batcher.Complete(batch, UploadOutcome.TooLarge, keep: false);
        Assert.Null(batcher.Next());
        Assert.Equal(1, batcher.DroppedTooLarge);
    }

    [Fact]
    public void RateLimitedBatchesAreKeptAndRetried()
    {
        Assert.Equal(UploadOutcome.Retry, UploadPolicy.Classify(429, timedOut: false));
        var policy = new UploadPolicy();
        var batcher = new ObservationBatcher();
        batcher.Enqueue(Record(0));
        var batch = batcher.Next()!;
        var keep = policy.Record(UploadOutcome.Retry, 10_000);
        Assert.True(keep);
        Assert.False(policy.CanAttempt(10_000)); // backs off
        batcher.Complete(batch, UploadOutcome.Retry, keep);
        Assert.Same(batch, batcher.Next()); // the same batch (same id) is resent
    }
}
