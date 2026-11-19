namespace CodexAgentPlatform.Application;

public enum WorkStatus
{
    Pending, Running, WaitingForUser, WaitingForApproval, Paused,
    Completed, Failed, Cancelled, Interrupted
}

public enum ResourceKind
{
    GitRepository, SshServer, ApiConnection, LocalWorkspace,
    Environment, DeploymentTarget, ExternalService
}

public sealed class Agent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Instructions { get; set; } = "";
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string? Voice { get; set; }
    public string AllowedTools { get; set; } = "memory";
    public string AllowedProjects { get; set; } = "";
    public DateTime? LastActivityUtc { get; set; }
    public bool Archived { get; set; }
}

public sealed class Conversation
{
    public bool AutoApprove { get; set; }
    public string? CoordinatorAgentId { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class Message
{
    public long Id { get; set; }
    public string ConversationId { get; set; } = "";
    public string? AgentId { get; set; }
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
    // Voice transcripts carry their project scope. An empty value means an unscoped call.
    public string? VoiceProjectId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ConversationThread
{
    public string ConversationId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string Workspace { get; set; } = "";
    public string CodexId { get; set; } = "";
}

public sealed class WorkItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ConversationId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string? ProjectId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Workspace { get; set; } = "";
    public string? CodexThreadId { get; set; }
    public string? Branch { get; set; }
    public string? Commit { get; set; }
    public string? LastStep { get; set; }
    public string? NextStep { get; set; }
    public WorkStatus Status { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
}

public sealed class ChatRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ConversationId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public long UserMessageId { get; set; }
    public long AssistantMessageId { get; set; }
    public string? WorkId { get; set; }
    public string? ProjectId { get; set; }
    public string? Workspace { get; set; }
    public int DelegationDepth { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string? ThreadId { get; set; }
    public string? TurnId { get; set; }
    public WorkStatus Status { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? Error { get; set; }
}

public sealed class Activity
{
    public long Id { get; set; }
    public string? ConversationId { get; set; }
    public string? WorkId { get; set; }
    public string? AgentId { get; set; }
    public string? ProjectId { get; set; }
    public string Kind { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? Artifact { get; set; }
    public DateTime EventOccurredAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class MemoryNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? AgentId { get; set; }
    public string? ProjectId { get; set; }
    public string? SourceTaskId { get; set; }
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Tags { get; set; } = "";
    public string Verification { get; set; } = "hypothesis";
    public DateTime? VerifiedAtUtc { get; set; }
    public string? RelatedCommit { get; set; }
    public string? SourceReferences { get; set; }
    public int Version { get; set; } = 1;
    public bool Archived { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastAccessedAtUtc { get; set; }
}

public sealed class MemoryRevision
{
    public string NoteId { get; set; } = "";
    public int Version { get; set; }
    public string Path { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class Resource
{
    public bool IsGlobal { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? ProjectId { get; set; }
    public string Name { get; set; } = "";
    public ResourceKind Kind { get; set; }
    public string Address { get; set; } = "";
    public string? Username { get; set; }
    public string? HostFingerprint { get; set; }
    public string? SftpFile { get; set; }
    public string? SshServerId { get; set; }
    public int Port { get; set; } = 22;
    public string? VaultId { get; set; }
    public bool Production { get; set; }
}

public sealed class Project
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string DefaultBranch { get; set; } = "main";
}

public sealed class Approval
{
    public bool AutoEligible { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AgentId { get; set; } = "";
    public string? ConversationId { get; set; }
    public string? ContinuationRunId { get; set; }
    public string Kind { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Payload { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string? DecidedBy { get; set; }
    public string? Result { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddMinutes(10);
}

public sealed class TokenUsage
{
    public string ThreadId { get; set; } = "";
    public string TurnId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string? WorkId { get; set; }
    public string? Model { get; set; }
    public long Input { get; set; }
    public long Cached { get; set; }
    public long Output { get; set; }
    public long Total { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class Preferences
{
    public int Id { get; set; } = 1;
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string TimeZone { get; set; } = "Europe/Istanbul";
    public int ContextCharacters { get; set; } = 12_000;
}

public sealed record CodexModel(string Id, string Name, bool IsDefault, string? DefaultEffort, string[] Efforts);

public static class TextLimits
{
    public static string Clip(string? text, int max) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max];
}
