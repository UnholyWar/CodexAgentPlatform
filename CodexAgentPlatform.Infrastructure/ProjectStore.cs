using CodexAgentPlatform.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CodexAgentPlatform.Infrastructure;

public static class ProjectStore
{
    public static void Normalize(Project project)
    {
        project.Name = project.Name.Trim();
        if (project.Name.Length is 0 or > 80)
            throw new ArgumentException("Enter a project name between 1 and 80 characters.");
    }

    public static async Task<Project> CreateAsync(PlatformDb db, string name,
        ChatRun? creator, CancellationToken ct)
    {
        var project = new Project { Name = name };
        Normalize(project);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (creator is not null)
        {
            var agent = await db.Agents.SingleOrDefaultAsync(a => a.Id == creator.AgentId, ct);
            if (agent is null || !ToolAccess.Has(agent, ToolAccess.CreateProjects) || !ToolAccess.AllowsProject(agent, creator.ProjectId))
                throw new UnauthorizedAccessException("This agent cannot create projects in this conversation.");
            agent.AllowedProjects = string.Join(',', ToolAccess.Split(agent.AllowedProjects).Append(project.Id));
            db.Events.Add(new Activity { Kind = "ProjectCreated", AgentId = agent.Id, ProjectId = project.Id,
                ConversationId = creator.ConversationId, WorkId = creator.WorkId, Detail = project.Name });
        }

        db.Projects.Add(project);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            throw new ArgumentException("This project name is already in use.", ex);
        }
        await transaction.CommitAsync(ct);
        return project;
    }

    public static async Task<string?> DeleteAsync(PlatformDb db, string id, string? expectedName, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var project = await db.Projects.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (project is null) return "This project no longer exists.";
        if (project.Name != expectedName) return "The project was renamed. Reopen it before deleting.";

        var links = new (int Count, string Label)[]
        {
            (await db.Resources.CountAsync(r => r.ProjectId == id, ct), "linked resources"),
            (await db.Work.CountAsync(w => w.ProjectId == id, ct), "work items"),
            (await db.Runs.CountAsync(r => r.ProjectId == id, ct), "chat runs"),
            (await db.Notes.CountAsync(n => n.ProjectId == id, ct), "memory notes"),
            (await db.Messages.CountAsync(m => m.VoiceProjectId == id, ct), "voice messages")
        };
        var blockers = links.Where(link => link.Count > 0).Select(link => $"{link.Label} ({link.Count})").ToArray();
        if (blockers.Length > 0)
            return $"Deletion is blocked by {string.Join(", ", blockers)}.";

        foreach (var agent in await db.Agents.ToListAsync(ct))
        {
            var projects = ToolAccess.Split(agent.AllowedProjects);
            if (projects.Contains(id, StringComparer.Ordinal))
                agent.AllowedProjects = string.Join(',', projects.Where(allowed => allowed != id));
        }

        await AgentPermissionGrants.CloseDeletedProjectRequestsAsync(db, id, ct);
        // Keep historical events. Project routing only selects existing projects.
        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return null;
    }
}
