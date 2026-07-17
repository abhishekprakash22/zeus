// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net.Sockets;

namespace Zeus.Protocol1.Tests;

public class RxFailurePolicyTests
{
    [Fact]
    public void Timeout_ReachesBudget_StartsSoftRestart_ThenDisconnects()
    {
        var policy = new RxFailurePolicy(transientFailureBudget: 3);

        Assert.Equal(RxFailureDecision.Continue,
            policy.RecordSocketFailure(SocketError.TimedOut, cancellationRequested: false));
        Assert.Equal(RxFailureDecision.Continue,
            policy.RecordSocketFailure(SocketError.TimedOut, cancellationRequested: false));
        Assert.Equal(RxFailureDecision.StartSoftRestart,
            policy.RecordSocketFailure(SocketError.TimedOut, cancellationRequested: false));
        Assert.Equal(RxFailureDecision.Disconnect,
            policy.RecordSocketFailure(SocketError.TimedOut, cancellationRequested: false));
    }

    [Fact]
    public void ConnectionReset_IsTransientAndCountsAgainstBudget()
    {
        var policy = new RxFailurePolicy(transientFailureBudget: 2);

        Assert.Equal(RxFailureDecision.Continue,
            policy.RecordSocketFailure(SocketError.ConnectionReset, cancellationRequested: false));
        Assert.Equal(RxFailureDecision.StartSoftRestart,
            policy.RecordSocketFailure(SocketError.ConnectionReset, cancellationRequested: false));
    }

    [Fact]
    public void Success_ResetsBudgetAndSoftRestartPhase()
    {
        var policy = new RxFailurePolicy(transientFailureBudget: 2);

        Assert.Equal(RxFailureDecision.Continue,
            policy.RecordSocketFailure(SocketError.TimedOut, cancellationRequested: false));
        policy.RecordSuccess();

        Assert.Equal(RxFailureDecision.Continue,
            policy.RecordSocketFailure(SocketError.TimedOut, cancellationRequested: false));
        Assert.Equal(RxFailureDecision.StartSoftRestart,
            policy.RecordSocketFailure(SocketError.TimedOut, cancellationRequested: false));
    }

    [Fact]
    public void FatalSocketError_DisconnectsImmediately()
    {
        var policy = new RxFailurePolicy(transientFailureBudget: 10);

        Assert.Equal(RxFailureDecision.Disconnect,
            policy.RecordSocketFailure(SocketError.NetworkDown, cancellationRequested: false));
    }

    [Fact]
    public void Cancellation_ExitsImmediately()
    {
        var policy = new RxFailurePolicy(transientFailureBudget: 10);

        Assert.Equal(RxFailureDecision.Exit,
            policy.RecordSocketFailure(SocketError.TimedOut, cancellationRequested: true));
        Assert.Equal(RxFailureDecision.Exit,
            policy.RecordSocketFailure(SocketError.OperationAborted, cancellationRequested: false));
    }
}
