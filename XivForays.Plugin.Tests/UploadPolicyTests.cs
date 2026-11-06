using XivMate.DataGathering.Forays.Dalamud.Core;
using Xunit;

namespace XivForays.Plugin.Tests;

public class UploadPolicyTests
{
    [Fact]
    public void BatchingLimitsAreTheAgreedOnes()
    {
        Assert.Equal(2000, UploadPolicy.WorkerIntervalMs);
        Assert.Equal(256, UploadPolicy.MaxBatchSize);
        Assert.Equal(4096, UploadPolicy.MaxPending);
    }

    [Theory]
    [InlineData(200, UploadOutcome.Accepted)]
    [InlineData(204, UploadOutcome.Accepted)]
    [InlineData(500, UploadOutcome.Retry)]
    [InlineData(502, UploadOutcome.Retry)]
    [InlineData(503, UploadOutcome.Retry)]
    [InlineData(408, UploadOutcome.Retry)]
    [InlineData(429, UploadOutcome.Retry)]
    [InlineData(400, UploadOutcome.Drop)]
    [InlineData(413, UploadOutcome.Drop)]
    [InlineData(422, UploadOutcome.Drop)]
    [InlineData(302, UploadOutcome.Drop)]
    [InlineData(404, UploadOutcome.EndpointMissing)]
    [InlineData(401, UploadOutcome.Unauthorized)]
    [InlineData(403, UploadOutcome.Unauthorized)]
    public void StatusCodesAreClassified(int status, object expected) =>
        Assert.Equal((UploadOutcome)expected, UploadPolicy.Classify(status, timedOut: false));

    [Fact]
    public void TimeoutsAndNetworkErrorsAreRetried()
    {
        Assert.Equal(UploadOutcome.Retry, UploadPolicy.Classify(null, timedOut: true));
        Assert.Equal(UploadOutcome.Retry, UploadPolicy.Classify(null, timedOut: false));
    }

    [Fact]
    public void BackoffIsExponentialAndCapped()
    {
        Assert.Equal(0, UploadPolicy.BackoffMs(0));
        Assert.Equal(2_000, UploadPolicy.BackoffMs(1));
        Assert.Equal(4_000, UploadPolicy.BackoffMs(2));
        Assert.Equal(8_000, UploadPolicy.BackoffMs(3));
        Assert.Equal(256_000, UploadPolicy.BackoffMs(8));
        Assert.Equal(300_000, UploadPolicy.BackoffMs(9));
        Assert.Equal(300_000, UploadPolicy.BackoffMs(1000));
    }

    [Fact]
    public void TransientFailuresKeepTheBatchUntilTheAttemptLimitThenBackOffStill()
    {
        var policy = new UploadPolicy();
        long now = 1_000;
        for (var attempt = 1; attempt < UploadPolicy.MaxAttempts; attempt++)
        {
            Assert.True(policy.CanAttempt(now));
            Assert.True(policy.Record(UploadOutcome.Retry, now));
            Assert.False(policy.CanAttempt(now + UploadPolicy.BackoffMs(attempt) - 1));
            now += UploadPolicy.BackoffMs(attempt);
        }

        Assert.False(policy.Record(UploadOutcome.Retry, now));
        Assert.False(policy.CanAttempt(now + 1));

        Assert.False(policy.Record(UploadOutcome.Accepted, now));
        Assert.True(policy.CanAttempt(now));
        Assert.Equal(0, policy.ConsecutiveFailures);
    }

    [Theory]
    [InlineData(UploadOutcome.Drop, 0L)]
    [InlineData(UploadOutcome.EndpointMissing, UploadPolicy.PermanentFailurePauseMs)]
    [InlineData(UploadOutcome.Unauthorized, UploadPolicy.PermanentFailurePauseMs)]
    public void ClientErrorsAreNeverRetried(object outcome, long pause)
    {
        var policy = new UploadPolicy();
        Assert.False(policy.Record((UploadOutcome)outcome, 10_000));
        Assert.Equal(pause == 0 ? 0 : 10_000 + pause, policy.NextAttemptAtMs);
        Assert.Equal(pause == 0, policy.CanAttempt(10_000));
    }
}
