using System.Text.RegularExpressions;
using CodexAgentPlatform.Application;
using Microsoft.EntityFrameworkCore;

namespace CodexAgentPlatform.Infrastructure;

public sealed record MemoryDraft(string Title, string Summary, string Tags, string Markdown,
    string Verification = "hypothesis", string? SourceReferences = null);

public sealed partial class MemoryStore(IDbContextFactory<PlatformDb> dbs, PlatformPaths paths)
{
    public async Task<List<MemoryNote>> SearchAsync(string? query, string? projectId, TimeRange? range,
        bool includeArchived, CancellationToken ct, MemoryNote? cursor = null, int limit = 40)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var notes = db.Notes.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var fts = SearchExpression(query);
            if (fts is null) return [];
            notes = db.Notes.FromSqlInterpolated($"""
                SELECT n.* FROM Notes n JOIN NoteSearch ON NoteSearch.rowid = n.rowid
                WHERE NoteSearch MATCH {fts}
                """).AsNoTracking();
        }
        if (!includeArchived) notes = notes.Where(x => !x.Archived);
        if (projectId is not null) notes = notes.Where(x => x.ProjectId == projectId);
        if (range is not null) notes = notes.Where(x => x.UpdatedAtUtc >= range.FromUtc && x.UpdatedAtUtc < range.UntilUtc);
        if (cursor is not null)
            notes = notes.Where(x => x.UpdatedAtUtc < cursor.UpdatedAtUtc ||
                (x.UpdatedAtUtc == cursor.UpdatedAtUtc && x.Id.CompareTo(cursor.Id) < 0));
        return await notes.OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id).Take(limit).ToListAsync(ct);
    }

    public async Task<List<Activity>> TimelineAsync(TimeRange? range, string? projectId, string? agentId, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var events = db.Events.AsNoTracking();
        if (range is not null)
            events = events.Where(x => x.EventOccurredAtUtc >= range.FromUtc && x.EventOccurredAtUtc < range.UntilUtc);
        if (projectId is not null) events = events.Where(x => x.ProjectId == projectId);
        if (agentId is not null) events = events.Where(x => x.AgentId == agentId);
        return await events.OrderByDescending(x => x.EventOccurredAtUtc).Take(80).ToListAsync(ct);
    }

    public async Task<string> ReadAsync(string id, int? version, string? section, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var note = await db.Notes.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException("Memory note not found.");
        var requestedVersion = version ?? note.Version;
        var revision = await db.Revisions.SingleAsync(x => x.NoteId == id && x.Version == requestedVersion, ct);
        var fullPath = RevisionPath(revision.Path);
        var markdown = await File.ReadAllTextAsync(fullPath, ct);
        await db.Notes.Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastAccessedAtUtc, DateTime.UtcNow), ct);
        if (string.IsNullOrWhiteSpace(section))
            return markdown;

        var lines = markdown.Split('\n');
        var heading = Array.FindIndex(lines, line => line.StartsWith('#') &&
            line.TrimStart('#').Trim().Equals(section.Trim(), StringComparison.OrdinalIgnoreCase));
        if (heading < 0)
            throw new InvalidOperationException("The requested Markdown section was not found.");
        var level = lines[heading].TakeWhile(c => c == '#').Count();
        return string.Join('\n', lines.Skip(heading).TakeWhile((line, i) =>
            i == 0 || !line.StartsWith('#') || line.TakeWhile(c => c == '#').Count() > level));
    }

    public async Task<MemoryNote> SaveAsync(string? id, MemoryDraft draft, string? agentId, string? projectId,
        string? workId, string? conversationId, CancellationToken ct, string? mergeId = null, bool admin = false,
        int? expectedVersion = null)
    {
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 160)
            throw new InvalidOperationException("Enter a note title between 1 and 160 characters.");
        if (string.IsNullOrWhiteSpace(draft.Markdown) || draft.Markdown.Length > 32_000)
            throw new InvalidOperationException("Enter Markdown content between 1 and 32,000 characters.");
        if (draft.Summary.Length > 600)
            throw new InvalidOperationException("Keep the summary to 600 characters or fewer.");
        if (draft.Tags.Length > 300)
            throw new InvalidOperationException("Keep the tags to 300 characters or fewer.");
        if (draft.SourceReferences?.Length > 2000)
            throw new InvalidOperationException("Keep the source references to 2,000 characters or fewer.");
        if (SecretShape().IsMatch(string.Join('\n', draft.Title, draft.Summary, draft.Tags, draft.Markdown, draft.SourceReferences)))
            throw new InvalidOperationException("Credentials cannot be stored in memory.");
        if (draft.Verification is not ("verified" or "inference" or "hypothesis"))
            throw new InvalidOperationException("Invalid verification status.");

        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var note = id is null ? null : await db.Notes.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (note is null && id is null && projectId is not null && !await db.Projects.AnyAsync(p => p.Id == projectId, ct))
            throw new InvalidOperationException("The selected project no longer exists. Reopen the Memory page to refresh the project list.");
        if (id is not null && (note is null || (!admin && (note.AgentId != agentId || note.ProjectId != projectId))))
            throw new InvalidOperationException("You do not have permission to edit this memory note.");
        var normalizedTitle = draft.Title.Trim();
        var similar = await db.Notes.AsNoTracking().AnyAsync(x => x.Id != id && x.Id != mergeId &&
            !x.Archived && x.ProjectId == projectId && x.AgentId == agentId && EF.Functions.Collate(x.Title, "NOCASE") == normalizedTitle, ct);
        if (similar)
            throw new InvalidOperationException("A note with this title already exists. Find and update the existing note.");

        var kind = note is null ? "MemoryCreated" : "MemoryUpdated";
        if (note is null)
        {
            note = new MemoryNote { AgentId = agentId, ProjectId = projectId, SourceTaskId = workId };
            db.Notes.Add(note);
        }
        else
        {
            if (expectedVersion.HasValue && note.Version != expectedVersion)
                throw new InvalidOperationException("The note was updated by another operation.");
            note.Version++;
        }
        note.Title = normalizedTitle;
        note.Summary = draft.Summary.Trim();
        note.Tags = draft.Tags.Trim();
        note.Verification = draft.Verification;
        note.SourceReferences = draft.SourceReferences;
        if (workId is not null)
            note.RelatedCommit = await db.Work.Where(x => x.Id == workId).Select(x => x.Commit).SingleOrDefaultAsync(ct);
        note.VerifiedAtUtc = draft.Verification == "verified" ? DateTime.UtcNow : null;
        note.UpdatedAtUtc = DateTime.UtcNow;
        note.Archived = false;

        if (mergeId is not null)
        {
            var old = await db.Notes.SingleOrDefaultAsync(x => x.Id == mergeId, ct);
            if (old is null || old.Id == note.Id || old.Archived ||
                (!admin && (old.AgentId != agentId || old.ProjectId != projectId)))
                throw new InvalidOperationException("The note to merge is not accessible.");
            old.Archived = true;
            old.UpdatedAtUtc = DateTime.UtcNow;
            db.Events.Add(Event(old, "MemoryArchived", agentId, workId, conversationId));
            kind = "MemoryConsolidated";
        }

        var name = $"{note.Id}-{note.Version}-{Guid.NewGuid():N}.md";
        await File.WriteAllTextAsync(RevisionPath(name), draft.Markdown, ct);
        db.Revisions.Add(new MemoryRevision { NoteId = note.Id, Version = note.Version, Path = name });
        db.Events.Add(Event(note, kind, agentId, workId, conversationId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return note;
    }

    public async Task ArchiveAsync(string id, string? agentId, string? workId, string? conversationId,
        bool admin, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var note = await db.Notes.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (note is null || (!admin && note.AgentId != agentId))
            throw new InvalidOperationException("You do not have permission to archive this memory note.");
        note.Archived = true;
        note.UpdatedAtUtc = DateTime.UtcNow;
        db.Events.Add(Event(note, "MemoryArchived", agentId, workId, conversationId));
        await db.SaveChangesAsync(ct);
    }

    public static string? SearchExpression(string query)
    {
        var terms = SearchTerm().Matches(query).Take(10).Select(x => $"\"{x.Value}\"").ToArray();
        return terms.Length == 0 ? null : string.Join(" AND ", terms);
    }

    private string RevisionPath(string name)
    {
        if (!RevisionName().IsMatch(name))
            throw new IOException("Invalid Markdown storage path.");
        var root = Path.Combine(paths.Data, "memory", "knowledge");
        Directory.CreateDirectory(root);
        PlatformPaths.CheckLinks(root);
        var path = Path.Combine(root, name);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Invalid Markdown file.");
        return path;
    }

    private static Activity Event(MemoryNote note, string kind, string? agentId, string? workId, string? conversationId) =>
        new() { Kind = kind, Detail = note.Title, Artifact = note.Id, ProjectId = note.ProjectId,
            AgentId = agentId, WorkId = workId, ConversationId = conversationId };

    [GeneratedRegex(@"[\p{L}\p{N}]{1,64}")]
    private static partial Regex SearchTerm();

    [GeneratedRegex(@"^[a-f0-9]{32}-[0-9]+-[a-f0-9]{32}\.md$")]
    private static partial Regex RevisionName();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----|\b(sk-[\w-]{20,}|gh[pousr]_[\w]{20,}|github_pat_[\w]{20,}|Bearer\s+\S{16,})|(?:password|api[_ -]?key|secret)\s*[:=]\s*\S{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex SecretShape();
}
