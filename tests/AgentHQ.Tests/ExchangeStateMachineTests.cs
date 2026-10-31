using AgentHQ.Core.Exchange;
using Xunit;

namespace AgentHQ.Tests;

/// <summary>
/// The Exchange trigger state machine (phase 2.2, D4): every legal edge and every refusal named
/// in the plan's table, in both directions. Pure rules — no database, no host.
/// </summary>
public class ExchangeStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static ExchangeItem Item(string kind, string state, string recipient = "zcode:*",
        string? claimedBy = null, DateTimeOffset? expiresAt = null, int hop = 0) => new(
        Id: "id-" + kind, Kind: kind, State: state, LatencyClass: ExchangeLatency.Turn, ThreadId: "thread-1",
        HopCount: hop, Sender: ExchangeActor.OpenClaw, Recipient: ExchangeActor.Parse(recipient),
        RepositoryId: "-repo", Subject: "subject", Body: "body", ContextRefs: null, Purpose: "purpose",
        ClaimedBySessionId: claimedBy, CreatedAt: Now, ExpiresAt: expiresAt);

    private static ExchangeStateMachine.Decision Run(ExchangeItem item, string action, bool killed = false) =>
        ExchangeStateMachine.Evaluate(item, action, Now, killed);

    // ---- claim ----

    [Fact]
    public void Claim_MovesQueuedRoleQuestionToClaimed()
    {
        var d = Run(Item(ExchangeKinds.Question, ExchangeStates.Queued), ExchangeStateMachine.Actions.Claim);
        Assert.True(d.Allowed);
        Assert.Equal(ExchangeStates.Claimed, d.To);
    }

    [Fact]
    public void Claim_OnMessageOrAnswerIsIllegal()
    {
        foreach (var kind in new[] { ExchangeKinds.Message, ExchangeKinds.File, ExchangeKinds.Answer, ExchangeKinds.Status })
        {
            var d = Run(Item(kind, ExchangeStates.Delivered), ExchangeStateMachine.Actions.Claim);
            Assert.False(d.Allowed);
            Assert.Equal(ExchangeStateMachine.Codes.IllegalTransition, d.Code);
        }
    }

    [Fact]
    public void Claim_OnDirectlyAddressedQuestionIsIllegal()
    {
        var d = Run(Item(ExchangeKinds.Question, ExchangeStates.Queued, recipient: "zcode:ses1"),
            ExchangeStateMachine.Actions.Claim);
        Assert.False(d.Allowed);
        Assert.Equal(ExchangeStateMachine.Codes.IllegalTransition, d.Code);
    }

    [Fact]
    public void Claim_OnAlreadyClaimedOrAnsweredIsIllegal()
    {
        Assert.False(Run(Item(ExchangeKinds.Question, ExchangeStates.Claimed, claimedBy: "ses1"),
            ExchangeStateMachine.Actions.Claim).Allowed);
        Assert.False(Run(Item(ExchangeKinds.Question, ExchangeStates.Answered),
            ExchangeStateMachine.Actions.Claim).Allowed);
        Assert.False(Run(Item(ExchangeKinds.Question, ExchangeStates.Acknowledged),
            ExchangeStateMachine.Actions.Claim).Allowed);
    }

    // ---- reply ----

    [Fact]
    public void Reply_FromQueuedOrClaimedAnswers()
    {
        Assert.Equal(ExchangeStates.Answered,
            Run(Item(ExchangeKinds.Question, ExchangeStates.Queued), ExchangeStateMachine.Actions.Reply).To);
        Assert.Equal(ExchangeStates.Answered,
            Run(Item(ExchangeKinds.Question, ExchangeStates.Claimed, claimedBy: "ses1"), ExchangeStateMachine.Actions.Reply).To);
    }

    [Fact]
    public void Reply_AfterKilledThreadIsThreadKilled()
    {
        var d = Run(Item(ExchangeKinds.Question, ExchangeStates.Claimed, claimedBy: "ses1"),
            ExchangeStateMachine.Actions.Reply, killed: true);
        Assert.False(d.Allowed);
        Assert.Equal(ExchangeStateMachine.Codes.ThreadKilled, d.Code);
    }

    [Fact]
    public void Reply_AfterAnsweredIsIllegal()
    {
        var d = Run(Item(ExchangeKinds.Question, ExchangeStates.Answered), ExchangeStateMachine.Actions.Reply);
        Assert.False(d.Allowed);
        Assert.Equal(ExchangeStateMachine.Codes.IllegalTransition, d.Code);
    }

    // ---- ack ----

    [Fact]
    public void Ack_FromDeliveredAnsweredExpiredKilledAcknowledges()
    {
        foreach (var state in new[] { ExchangeStates.Delivered, ExchangeStates.Answered, ExchangeStates.Expired, ExchangeStates.Killed })
        {
            var d = Run(Item(ExchangeKinds.Answer, state), ExchangeStateMachine.Actions.Ack);
            Assert.True(d.Allowed, state);
            Assert.Equal(ExchangeStates.Acknowledged, d.To);
        }
    }

    [Fact]
    public void Ack_OfQueuedOrClaimedOrAcknowledgedIsIllegal()
    {
        foreach (var state in new[] { ExchangeStates.Queued, ExchangeStates.Claimed, ExchangeStates.Acknowledged })
        {
            var d = Run(Item(ExchangeKinds.Message, state), ExchangeStateMachine.Actions.Ack);
            Assert.False(d.Allowed, state);
            Assert.Equal(ExchangeStateMachine.Codes.IllegalTransition, d.Code);
        }
    }

    // ---- kill ----

    [Fact]
    public void Kill_FromEveryNonTerminalStateKills()
    {
        foreach (var state in new[] { ExchangeStates.Queued, ExchangeStates.Delivered, ExchangeStates.Claimed,
                     ExchangeStates.Answered, ExchangeStates.Expired, ExchangeStates.Killed })
        {
            var d = Run(Item(ExchangeKinds.Message, state), ExchangeStateMachine.Actions.Kill);
            Assert.True(d.Allowed, state);
            Assert.Equal(ExchangeStates.Killed, d.To);
        }
    }

    [Fact]
    public void Kill_OfAcknowledgedIsIllegal()
    {
        var d = Run(Item(ExchangeKinds.Message, ExchangeStates.Acknowledged), ExchangeStateMachine.Actions.Kill);
        Assert.False(d.Allowed);
        Assert.Equal(ExchangeStateMachine.Codes.IllegalTransition, d.Code);
    }

    // ---- expiry ----

    [Fact]
    public void Expire_PastExpiresAtFromAnyOpenState()
    {
        foreach (var state in new[] { ExchangeStates.Queued, ExchangeStates.Claimed, ExchangeStates.Delivered, ExchangeStates.Answered })
        {
            var item = Item(ExchangeKinds.Question, state, expiresAt: Now.AddSeconds(-1));
            var d = Run(item, ExchangeStateMachine.Actions.Expire);
            Assert.True(d.Allowed, state);
            Assert.Equal(ExchangeStates.Expired, d.To);
        }
    }

    [Fact]
    public void Expire_BeforeExpiresAtOrWithoutOneIsRefused()
    {
        Assert.False(Run(Item(ExchangeKinds.Question, ExchangeStates.Queued, expiresAt: Now.AddSeconds(5)),
            ExchangeStateMachine.Actions.Expire).Allowed);
        Assert.False(Run(Item(ExchangeKinds.Question, ExchangeStates.Queued), ExchangeStateMachine.Actions.Expire).Allowed);
        Assert.False(Run(Item(ExchangeKinds.Question, ExchangeStates.Acknowledged, expiresAt: Now.AddSeconds(-1)),
            ExchangeStateMachine.Actions.Expire).Allowed);
    }

    // ---- hops ----

    [Fact]
    public void HopLimit_RefusesTheFourthReplyAndAllowsTheThird()
    {
        Assert.False(ExchangeStateMachine.IsHopOverLimit(0, 3, out var h1));
        Assert.Equal(1, h1);
        Assert.False(ExchangeStateMachine.IsHopOverLimit(1, 3, out var h2));
        Assert.Equal(2, h2);
        Assert.False(ExchangeStateMachine.IsHopOverLimit(2, 3, out var h3));
        Assert.Equal(3, h3);
        Assert.True(ExchangeStateMachine.IsHopOverLimit(3, 3, out var h4));
        Assert.Equal(4, h4);
    }

    [Fact]
    public void InitialState_QuestionIsQueuedAndOthersDeliveredAnswersCannotBePosted()
    {
        Assert.Equal(ExchangeStates.Queued, ExchangeStateMachine.InitialState(ExchangeKinds.Question));
        Assert.Equal(ExchangeStates.Delivered, ExchangeStateMachine.InitialState(ExchangeKinds.Message));
        Assert.Equal(ExchangeStates.Delivered, ExchangeStateMachine.InitialState(ExchangeKinds.File));
        Assert.Equal(ExchangeStates.Delivered, ExchangeStateMachine.InitialState(ExchangeKinds.Status));
        Assert.Equal(ExchangeStates.Delivered, ExchangeStateMachine.InitialState(ExchangeKinds.Answer));
        Assert.False(ExchangeKinds.IsPostable(ExchangeKinds.Answer));
    }

    [Fact]
    public void EveryEdgeInTheMapIsRepresentedByACaseInThisFile()
    {
        // Guards the plan's "each row above is an entry in the map and a unit test" rule: adding
        // an edge without a decision path (or vice versa) shows up here.
        foreach (var ((from, action), to) in ExchangeStateMachine.Edges)
        {
            var item = Item(ExchangeKinds.Question, from, claimedBy: from == ExchangeStates.Claimed ? "ses1" : null,
                expiresAt: action == ExchangeStateMachine.Actions.Expire ? Now.AddSeconds(-1) : null);
            var d = ExchangeStateMachine.Evaluate(item, action, Now, false);
            Assert.True(d.Allowed, $"{from} --{action}--> {to}");
            Assert.Equal(to, d.To);
        }
    }
}
