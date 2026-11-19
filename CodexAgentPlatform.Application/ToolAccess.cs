namespace CodexAgentPlatform.Application;

public static class ToolAccess
{
    public const string CreateProjects = "projects.create";
    public const string ProjectResources = "resources";
    public const string GlobalResources = "resources.global";
    public const string CreateResources = "resources.create";

    public static IReadOnlyDictionary<string, string> Permissions { get; } = new Dictionary<string, string>
    {
        ["memory"] = "Memory", [ProjectResources] = "Project resources", [GlobalResources] = "Global resources",
        [CreateResources] = "Create resources", ["workspace"] = "Workspace and terminal",
        ["git"] = "Git", ["ssh"] = "SSH", ["delegate"] = "Delegation", [CreateProjects] = "Create projects"
    }.AsReadOnly();

    public static bool Has(Agent agent, string permission) => !agent.Archived &&
        Split(agent.AllowedTools).Contains(permission, StringComparer.Ordinal);

    public static bool HasResourcePermission(Agent agent) => Has(agent, ProjectResources) || Has(agent, GlobalResources);

    public static bool AllowsProject(Agent agent, string? project) => !agent.Archived &&
        (project is null || Split(agent.AllowedProjects).Contains(project, StringComparer.Ordinal));

    public static string[] Split(string? list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string ApplySelectionChanges(string current, string original, IReadOnlySet<string> selected)
    {
        var originalValues = Split(original);
        var result = Split(current).ToHashSet(StringComparer.Ordinal);
        result.ExceptWith(originalValues.Except(selected));
        result.UnionWith(selected.Except(originalValues));
        return string.Join(',', result);
    }
}
