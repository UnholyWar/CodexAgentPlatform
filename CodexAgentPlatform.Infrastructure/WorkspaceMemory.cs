using System.Text.Json;
using CodexAgentPlatform.Application;

namespace CodexAgentPlatform.Infrastructure;

public static class WorkspaceMemory
{
    public static async Task WriteAgentAsync(PlatformPaths paths, Agent agent, CancellationToken ct)
    {
        if (agent.Id is not ("architect" or "developer" or "qa" or "devops") &&
            !Guid.TryParseExact(agent.Id, "N", out _))
            throw new ArgumentException("Invalid agent identity.");
        var directory = Path.Combine(paths.Data, "memory", "agents", agent.Id);
        Directory.CreateDirectory(directory);
        PlatformPaths.CheckLinks(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "identity.md"),
            $"# {agent.Name}\n\n{agent.Title}\n\n{agent.Instructions}", ct);
    }

    public static async Task WriteWorkAsync(PlatformPaths paths, WorkItem work, CancellationToken ct)
    {
        var directory = Path.Combine(paths.Data, "tasks", work.Id);
        Directory.CreateDirectory(directory);
        var data = JsonSerializer.Serialize(work, new JsonSerializerOptions { WriteIndented = true });
        var markdown = $"""
            # {work.Title}

            {work.Description}

            - Status: {work.Status}
            - Workspace: {work.Workspace}
            - Branch: {work.Branch}
            - Commit: {work.Commit}
            - Thread: {work.CodexThreadId}
            - Updated (UTC): {work.UpdatedAtUtc:O}

            ## Last step
            {work.LastStep}

            ## Next step
            {work.NextStep}
            """;
        foreach (var (name, body) in new[] { ("work.json", data), ("working.md", markdown) })
        {
            var temp = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(temp, body, ct);
                File.Move(temp, Path.Combine(directory, name), overwrite: true);
            }
            finally { File.Delete(temp); }
        }
    }
}
