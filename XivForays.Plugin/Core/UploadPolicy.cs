using System;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

internal enum UploadOutcome
{
    /// <summary>2xx: the batch is done.</summary>
    Accepted,

    /// <summary>5xx, 408, 429, timeout or network error: keep the batch and retry after a backoff.</summary>
    Retry,

    /// <summary>Any other 4xx: the server will never accept this batch, so drop it (never retried).</summary>
    Drop,

    /// <summary>404: the route is not deployed (web friends branch missing). Drop, warn once, pause.</summary>
    EndpointMissing,

    /// <summary>401/403: the key is missing, wrong or disabled. Drop and pause.</summary>
    Unauthorized,

    /// <summary>413: the request body is over the server's limit. Split the batch in two and send both halves (no backoff).</summary>
    TooLarge
}

/// <summary>
/// Upload load policy, shared by every upload stream. Dependency-free so it can be unit tested.
/// Batching: the worker runs every <see cref="WorkerIntervalMs"/>, sends at most
/// <see cref="MaxBatchSize"/> records and holds at most <see cref="MaxPending"/> in memory (oldest dropped).
/// Nothing is written to disk. Retries back off exponentially from <see cref="BaseBackoffMs"/> to
/// <see cref="MaxBackoffMs"/>; a batch is given up after <see cref="MaxAttempts"/> failed attempts.
/// </summary>
internal sealed class UploadPolicy
{
    public const int WorkerIntervalMs = 2000;
    public const int MaxBatchSize = 256;

    /// <summary>
    /// Serialized bytes per observations batch: well under the live server's 512 KB request limit (a 256-record pose
    /// batch is about 590 KB). The new ingest allows 2 MB, so this is safe on both.
    /// </summary>
    public const int MaxBatchBytes = 400 * 1024;
    public const int MaxPending = 4096;
    public const long BaseBackoffMs = 2000;
    public const long MaxBackoffMs = 5 * 60 * 1000;
    public const int MaxAttempts = 8;

    /// <summary>Pause after 401/403/404, which only an operator can fix, so the client does not poll the server.</summary>
    public const long PermanentFailurePauseMs = 5 * 60 * 1000;

    private int consecutiveFailures;
    private int attemptsForCurrentBatch;
    private long nextAttemptAtMs;

    public int ConsecutiveFailures => consecutiveFailures;
    public long NextAttemptAtMs => nextAttemptAtMs;

    public static UploadOutcome Classify(int? statusCode, bool timedOut)
    {
        if (timedOut || statusCode == null)
            return UploadOutcome.Retry;

        var status = statusCode.Value;
        if (status is >= 200 and < 300)
            return UploadOutcome.Accepted;
        if (status is 408 or 429 || status >= 500)
            return UploadOutcome.Retry;
        if (status == 404)
            return UploadOutcome.EndpointMissing;
        if (status == 413)
            return UploadOutcome.TooLarge;
        if (status is 401 or 403)
            return UploadOutcome.Unauthorized;
        // 3xx (redirects are disabled) and other 4xx: retrying cannot help.
        return UploadOutcome.Drop;
    }

    /// <summary>Delay before the next attempt after <paramref name="failures"/> consecutive transient failures.</summary>
    public static long BackoffMs(int failures)
    {
        if (failures <= 0)
            return 0;

        var shift = Math.Min(failures - 1, 20);
        return Math.Min(MaxBackoffMs, BaseBackoffMs << shift);
    }

    public bool CanAttempt(long nowMs) => nowMs >= nextAttemptAtMs;

    /// <summary>Records the result of one attempt. Returns true when the current batch should be kept for a retry.</summary>
    public bool Record(UploadOutcome outcome, long nowMs)
    {
        if (outcome != UploadOutcome.Retry)
        {
            consecutiveFailures = 0;
            attemptsForCurrentBatch = 0;
            nextAttemptAtMs = outcome is UploadOutcome.EndpointMissing or UploadOutcome.Unauthorized
                ? nowMs + PermanentFailurePauseMs
                : 0;
            return false;
        }

        consecutiveFailures++;
        attemptsForCurrentBatch++;
        nextAttemptAtMs = nowMs + BackoffMs(consecutiveFailures);
        if (attemptsForCurrentBatch < MaxAttempts)
            return true;

        // Give this batch up but keep backing off, so a server outage costs nothing extra.
        attemptsForCurrentBatch = 0;
        return false;
    }
}
