namespace CodexAgentPlatform.Application;

public static class ToolAccess
{
    public static bool Has(Agent agent, string permission) => !agent.Archived &&
        Split(agent.AllowedTools).Contains(permission, StringComparer.Ordinal);

    public static bool AllowsProject(Agent agent, string? project) => !agent.Archived &&
        (project is null || Split(agent.AllowedProjects).Contains(project, StringComparer.Ordinal));

    public static string[] Split(string? list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
