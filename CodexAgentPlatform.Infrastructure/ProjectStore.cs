using CodexAgentPlatform.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CodexAgentPlatform.Infrastructure;

public static class ProjectStore
{
    public static void Normalize(Project project)
    {
        project.Name = project.Name.Trim();
        project.DefaultBranch = project.DefaultBranch.Trim();
        if (project.Name.Length is 0 or > 80 || project.DefaultBranch.Length is 0 or > 150)
            throw new ArgumentException("A project name (up to 80 characters) and default branch (up to 150 characters) are required.");
    }

    public static async Task<Project> CreateAsync(PlatformDb db, string name, string? defaultBranch,
        ChatRun? creator, CancellationToken ct)
    {
        var project = new Project { Name = name };
        if (defaultBranch is not null) project.DefaultBranch = defaultBranch;
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
}
