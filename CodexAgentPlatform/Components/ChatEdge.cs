using CodexAgentPlatform.Infrastructure;

namespace CodexAgentPlatform.Components;

public static class ChatEdge
{
    public static string? State(string? chat, ChatSnapshot snapshot, DateTime utcNow)
    {
        if (chat is null) return null;
        if (snapshot.Approvals.Any(a => a.ConversationId == chat && a.Status == "Pending" && a.ExpiresAtUtc > utcNow))
            return "approval";
        if (snapshot.RunningConversationId == chat ||
            snapshot.Approvals.Any(a => a.ConversationId == chat && a.Status == "Executing"))
            return "working";
        return null;
    }
}
