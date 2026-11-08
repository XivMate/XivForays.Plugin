using System;
using System.Collections.Generic;
using System.Text.Json;
using XivMate.DataGathering.Forays.Dalamud.Models;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>
/// The observations upload queue: pending records (at most <see cref="UploadPolicy.MaxPending"/>, oldest dropped), the
/// batch being sent or retried, and the halves of a batch the server refused as too large (413). A new batch holds at
/// most <see cref="UploadPolicy.MaxBatchSize"/> records and <see cref="UploadPolicy.MaxBatchBytes"/> of serialized
/// JSON, well under the live server's 512 KB request limit. Not thread safe: the caller locks. Dependency-free for tests.
/// </summary>
internal sealed class ObservationBatcher
{
    /// <summary>The batch envelope around the records: {"schemaVersion":1,"batchId":"...","observations":[...]}.</summary>
    public const int EnvelopeBytes = 96;

    private readonly Queue<Observation> pending = new();

    // Halves of a 413'd batch, sent before any new records (oldest first).
    private readonly LinkedList<Observation[]> split = new();
    private readonly Func<Observation, int> sizeOf;
    private readonly int maxRecords;
    private readonly int maxBytes;
    private ObservationBatch? current;

    public ObservationBatcher(Func<Observation, int>? sizeOf = null, int maxRecords = UploadPolicy.MaxBatchSize,
        int maxBytes = UploadPolicy.MaxBatchBytes)
    {
        this.sizeOf = sizeOf ?? SerializedSize;
        this.maxRecords = maxRecords;
        this.maxBytes = maxBytes;
    }

    public int PendingCount => pending.Count;
    public bool HasWork => current != null || split.Count > 0 || pending.Count > 0;

    /// <summary>Records dropped because a single record alone was refused as too large.</summary>
    public int DroppedTooLarge { get; private set; }

    public static int SerializedSize(Observation observation) =>
        JsonSerializer.SerializeToUtf8Bytes(observation, ObservationJson.Options).Length;

    public void Clear()
    {
        pending.Clear();
        split.Clear();
        current = null;
    }

    public void Enqueue(Observation observation)
    {
        if (pending.Count >= UploadPolicy.MaxPending)
            pending.Dequeue();
        pending.Enqueue(observation);
    }

    /// <summary>The batch to send now: the one being retried, else a split half, else a new capped batch.</summary>
    public ObservationBatch? Next()
    {
        if (current != null)
            return current;

        if (split.First is { } half)
        {
            split.RemoveFirst();
            return current = new ObservationBatch(1, Guid.NewGuid(), half.Value);
        }

        if (pending.Count == 0)
            return null;

        var records = new List<Observation>();
        var bytes = EnvelopeBytes;
        while (pending.Count > 0 && records.Count < maxRecords)
        {
            var size = sizeOf(pending.Peek()) + 1; // + the comma
            if (records.Count > 0 && bytes + size > maxBytes)
                break;
            records.Add(pending.Dequeue());
            bytes += size;
        }

        return current = new ObservationBatch(1, Guid.NewGuid(), records.ToArray());
    }

    /// <summary>
    /// The result for <paramref name="batch"/>. <paramref name="keep"/> is the policy's retry decision. A 413 splits
    /// the batch in two (each half a new batch id) and sends both before new records; a single record that is still
    /// too large is dropped.
    /// </summary>
    public void Complete(ObservationBatch batch, UploadOutcome outcome, bool keep)
    {
        if (!ReferenceEquals(batch, current))
            return;
        current = null;
        if (outcome == UploadOutcome.TooLarge)
        {
            var records = batch.Observations;
            if (records.Length <= 1)
            {
                DroppedTooLarge += records.Length;
                return;
            }

            var middle = records.Length / 2;
            split.AddFirst(records[middle..]);
            split.AddFirst(records[..middle]);
            return;
        }

        if (keep)
            current = batch;
    }
}
