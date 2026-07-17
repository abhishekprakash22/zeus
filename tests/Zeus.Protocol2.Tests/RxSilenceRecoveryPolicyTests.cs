// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Protocol2.Tests;

public class RxSilenceRecoveryPolicyTests
{
    [Fact]
    public void Attempts_AreBoundedAndSpaced()
    {
        var policy = new RxSilenceRecoveryPolicy(
            startMs: 0,
            silenceBeforeRecoveryMs: 3000,
            attemptSpacingMs: 2000,
            maxAttempts: 2);

        Assert.Equal(RxSilenceRecoveryAction.None,
            policy.Tick(2999, streamingExpected: true, recoveryBlocked: false).Action);
        var first = policy.Tick(3000, streamingExpected: true, recoveryBlocked: false);
        Assert.Equal(RxSilenceRecoveryAction.AttemptRestart, first.Action);
        Assert.Equal(1, first.Attempt);

        Assert.Equal(RxSilenceRecoveryAction.None,
            policy.Tick(4999, streamingExpected: true, recoveryBlocked: false).Action);
        var second = policy.Tick(5000, streamingExpected: true, recoveryBlocked: false);
        Assert.Equal(RxSilenceRecoveryAction.AttemptRestart, second.Action);
        Assert.Equal(2, second.Attempt);

        Assert.Equal(RxSilenceRecoveryAction.None,
            policy.Tick(6999, streamingExpected: true, recoveryBlocked: false).Action);
        Assert.Equal(RxSilenceRecoveryAction.Exhausted,
            policy.Tick(7000, streamingExpected: true, recoveryBlocked: false).Action);
        Assert.Equal(RxSilenceRecoveryAction.None,
            policy.Tick(9000, streamingExpected: true, recoveryBlocked: false).Action);
    }

    [Fact]
    public void PacketFlow_ResetsAttempts()
    {
        var policy = new RxSilenceRecoveryPolicy(0, 3000, 2000, 2);

        Assert.Equal(RxSilenceRecoveryAction.AttemptRestart,
            policy.Tick(3000, streamingExpected: true, recoveryBlocked: false).Action);
        var success = policy.RecordPacket(3500);

        Assert.NotNull(success);
        Assert.Equal(1, success!.Value.Attempts);
        Assert.Equal(RxSilenceRecoveryAction.None,
            policy.Tick(6499, streamingExpected: true, recoveryBlocked: false).Action);
        Assert.Equal(RxSilenceRecoveryAction.AttemptRestart,
            policy.Tick(6500, streamingExpected: true, recoveryBlocked: false).Action);
    }

    [Fact]
    public void BlockedRecovery_DoesNotConsumeAttempts()
    {
        var policy = new RxSilenceRecoveryPolicy(0, 3000, 2000, 2);

        Assert.Equal(RxSilenceRecoveryAction.Blocked,
            policy.Tick(3000, streamingExpected: true, recoveryBlocked: true).Action);
        Assert.Equal(0, policy.Attempts);

        var attempt = policy.Tick(5000, streamingExpected: true, recoveryBlocked: false);
        Assert.Equal(RxSilenceRecoveryAction.AttemptRestart, attempt.Action);
        Assert.Equal(1, attempt.Attempt);
    }
}
