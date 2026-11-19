using System.Globalization;
using System.Text.RegularExpressions;
using CodexAgentPlatform.Application;
using Microsoft.EntityFrameworkCore;

namespace CodexAgentPlatform.Infrastructure;

internal static partial class ProjectRouting
{
    internal static (bool Create, bool Implement) Intent(string text)
    {
        var input = text.ToLower(CultureInfo.GetCultureInfo("tr-TR"));
        if (input.Contains('`') || Exclusions().IsMatch(input)) return default;
        var create = CreateProject().IsMatch(input);
        var work = create ? CreateProject().Replace(input, "") : input;
        return (create, ImplementationSubject().IsMatch(work) && ImplementationVerb().IsMatch(work));
    }

    internal static Agent? Developer(IEnumerable<Agent> agents, string project) =>
        agents.Where(a => ToolAccess.Has(a, "workspace") &&
                (a.Id == "developer" || DevelopmentRole().IsMatch(a.Title)))
            .OrderByDescending(a => ToolAccess.AllowsProject(a, project)).ThenByDescending(a => a.Id == "developer")
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    internal static Agent? Creator(IEnumerable<Agent> agents) =>
        agents.Where(a => ToolAccess.Has(a, ToolAccess.CreateProjects) || a.Id == "architect")
            .OrderByDescending(a => ToolAccess.Has(a, ToolAccess.CreateProjects)).ThenByDescending(a => a.Id == "architect")
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    internal static bool RequestsArchitect(string text) => ArchitectRequest().IsMatch(text.Trim());

    internal static Task<string?> CreatedProjectAsync(PlatformDb db, string conversation, DateTime? since, CancellationToken ct) =>
        db.Events.AsNoTracking().Where(e => e.ConversationId == conversation && e.Kind == "ProjectCreated" &&
                (since == null || e.EventOccurredAtUtc >= since) && db.Projects.Any(p => p.Id == e.ProjectId))
            .OrderByDescending(e => e.Id).Select(e => e.ProjectId).FirstOrDefaultAsync(ct);

    internal static async Task<string?> FocusedProjectAsync(PlatformDb db, string conversation, string? runProject,
        DateTime? runStarted, CancellationToken ct)
    {
        // Older chats can contain successful creation followed by unscoped turns.
        var since = runProject is not null ? runStarted : await db.Events.AsNoTracking()
            .Where(e => e.ConversationId == conversation && e.Kind == "ProjectCleared")
            .OrderByDescending(e => e.Id).Select(e => (DateTime?)e.EventOccurredAtUtc).FirstOrDefaultAsync(ct);
        return await CreatedProjectAsync(db, conversation, since, ct) ?? runProject;
    }

    // Familiar, affirmative commands get deterministic routing. Other wording can still use the catalogue and handoff tools.
    [GeneratedRegex(@"\b(?:nasıl|how to|how (?:do|can|should)|don't|do not|(?:oluştur|yap|yaz|kodla|aç|kur)ma(?:yın|yınız)?|should (?:i|we)|eğer)\b")]
    private static partial Regex Exclusions();

    [GeneratedRegex(@"\b(?:proje(?:yi|si|sini)?\s+(?:oluştur|aç|kur)(?:un|unuz|ur musun|abilir misin)?|(?:create|open|set up|start)\s+(?:(?:a|the|new)\s+){0,3}project)\b")]
    private static partial Regex CreateProject();

    [GeneratedRegex(@"\b(?:node(?:\.?js)?|nodjs|react|vue|angular|python|dotnet|javascript|typescript|rust|java|uygulama\p{L}*|app\p{L}*|api|endpoint\p{L}*|kod\p{L}*|özellik\p{L}*|script\p{L}*|feature\p{L}*|bug\p{L}*|frontend|backend|servis\p{L}*|web sitesi)\b")]
    private static partial Regex ImplementationSubject();

    [GeneratedRegex(@"\b(?:yap|geliştir|kodla|yaz|ekle|uygula|düzelt|oluştur)(?:ın|in|un|ınız|iniz|unuz|ar mısın|er misin|abilir misin|ebilir misin)?\b|\b(?:build|implement|code|write|add|fix|scaffold|develop|create)\b")]
    private static partial Regex ImplementationVerb();

    [GeneratedRegex(@"\b(?:developer|software engineer|full[- ]stack|yazılım geliştirici)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DevelopmentRole();

    [GeneratedRegex(@"^(?:ben\s+)?(?:mimarla|mimar ile|architect ile|architect'le)\s+(?:görüşmek|konuşmak)\s+istiyorum\b|^(?:i want to|let me) (?:talk|speak) (?:to|with) (?:the )?architect\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchitectRequest();
}
