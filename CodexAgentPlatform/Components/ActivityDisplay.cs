namespace CodexAgentPlatform.Components;

internal static class ActivityDisplay
{
    public static string Label(string kind) => kind switch
    {
        "Diff" => "Diff", "fileChange" or "CodeModified" => "File change",
        "commandExecution" => "Command output", "ToolCalled" => "Tool call", "webSearch" => "Web search",
        "TurnCompleted" => "Response completed", "TurnInterrupted" => "Work interrupted", "TurnFailed" => "Operation failed",
        "AutoApproved" => "Automatically approved", "AutoApprovalEnabled" => "Auto-approval enabled",
        "AutoApprovalDisabled" => "Auto-approval disabled", "ApprovalRequested" => "Approval requested",
        "TestExecuted" => "Test executed", "ProjectSelected" => "Project selected", "ProjectCleared" => "Project selection cleared", "WorkProgress" => "Work updated",
        "WorkspaceUnavailable" => "Workspace unavailable", "AgentHandoff" => "Agent handoff",
        "AgentHandoffWaiting" => "Handoff awaiting access", "AgentHandoffUnavailable" => "Handoff unavailable",
        "PermissionGrantRequested" => "Permission requested", "PermissionGranted" => "Permission granted",
        "PermissionGrantDeclined" => "Permission request declined", "ProjectCreated" => "Project created", _ => kind
    };
}
