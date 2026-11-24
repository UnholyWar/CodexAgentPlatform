using CodexAgentPlatform.Application;
using CodexAgentPlatform.Components;
using CodexAgentPlatform.Infrastructure;
using Xunit;

namespace CodexAgentPlatform.Tests;

public class ChatEdgeTests
{
    [Fact]
    public void LightTracksOnlyTheOpenConversationAndPendingApprovalTakesPriority()
    {
        var now = DateTime.UtcNow;
        var snapshot = new ChatSnapshot([], [], null, []) { RunningConversationId = "one" };
        Assert.Equal("working", ChatEdge.State("one", snapshot, now));

        var approval = new Approval { ConversationId = "one", ExpiresAtUtc = now.AddMinutes(1) };
        snapshot.Approvals.Add(approval);
        Assert.Equal("approval", ChatEdge.State("one", snapshot, now));
        Assert.Null(ChatEdge.State("two", snapshot, now));
        Assert.Null(ChatEdge.State(null, snapshot, now));
        Assert.Equal("working", ChatEdge.State("one", snapshot, approval.ExpiresAtUtc));

        approval.Status = "Completed";
        Assert.Equal("working", ChatEdge.State("one", snapshot, now));
        snapshot = snapshot with { RunningConversationId = null };
        Assert.Null(ChatEdge.State("one", snapshot, now));
        approval.Status = "Executing";
        Assert.Equal("working", ChatEdge.State("one", snapshot, now));
        approval.Status = "Failed";
        Assert.Null(ChatEdge.State("one", snapshot, now));
    }
}
