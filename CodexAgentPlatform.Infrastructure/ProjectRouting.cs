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

    internal static (string? Target, bool Identity, bool TaskStatus, bool Turkish) VoiceControl(string text)
    {
        var input = text.Trim().TrimEnd('.', '!', '?', '…');
        var english = EnglishVoiceControl().Match(input);
        if (english.Success) return (Target(english), english.Groups["identity"].Success, english.Groups["status"].Success, false);
        var spoken = input.ToLower(CultureInfo.GetCultureInfo("tr-TR"));
        var turkish = TurkishVoiceControl().Match(spoken);
        if (!turkish.Success)
        {
            var sentences = spoken.Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (sentences.Length > 1 && sentences.All(sentence => TurkishVoiceControl().Match(sentence).Groups["identity"].Success))
                return (null, true, false, true);
        }
        return (Target(turkish), turkish.Groups["identity"].Success, turkish.Groups["status"].Success, true);

        static string? Target(Match match) => match.Groups["agent"].Success ? match.Groups["agent"].Value.Trim() : null;
    }

    internal static Agent? VoiceTarget(IReadOnlyList<Agent> agents, string target)
    {
        var culture = CultureInfo.GetCultureInfo("tr-TR");
        target = target.TrimStart('@').ToLower(culture);
        return Find(target) ?? Find(SpokenAgentRole().Replace(target, "")) ??
            Find(TurkishAgentSuffix().Replace(target, ""));

        Agent? Find(string name)
        {
            var alias = name switch
            {
                "mimar" or "yazılım mimarı" => "architect",
                "geliştirici" or "yazılımcı" or "yazılım geliştiricisi" => "developer",
                "normal sohbet" or "genel sohbet" => PlatformChat.Id,
                _ => name
            };
            var named = agents.Where(a => string.Equals(a.Id, alias, StringComparison.OrdinalIgnoreCase) ||
                a.Name.ToLower(culture) == alias).Take(2).ToArray();
            if (named.Length != 0) return named.Length == 1 ? named[0] : null;
            var titled = agents.Where(a => a.Title.ToLower(culture) == name).Take(2).ToArray();
            return titled.Length == 1 ? titled[0] : null;
        }
    }

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

    [GeneratedRegex(@"^(?:please\s+)?(?:(?<status>who is (?:working on|handling) the task|what(?:'s| is) the task status|how is the task going)|(?<identity>(?:who|which agent)\s+(?:am i (?:talking|speaking) (?:to|with)|is speaking|is on the call)|who are you|what(?:'s| is) your name)|(?:(?:switch|transfer)(?: me)? to|(?:i want to |i would like to |let me )?(?:talk|speak) (?:to|with)|connect me (?:to|with))\s+(?:the )?(?<agent>.+?)(?: (?:now|please))?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishVoiceControl();

    [GeneratedRegex(@"^(?:yok[, ]+)?(?:(?<status>(?:görev|iş) ne durumda|(?:görevi|işi) (?:kim yapıyor|kim devraldı)|görev(?:in)? durumu ne)|(?<identity>(?:şu an )?(?:hangi ajanla (?:görüşüyorum|konuşuyorum)(?: ben)?|kimle (?:görüşüyorum|konuşuyorum)|karşımda kim var|sen kimsin|sen hangi ajansın|hangi ajan konuşuyor|devraldı mı(?: şu an)?))|(?:(?:ben|beni|bunu|şimdi)\s+)?(?<agent>[^.!?]+?)\s+(?:ile\s+)?(?:(?:görüşmek|konuşmak) istiyorum|bağla(?:r mısın)?|(?:geç|dön)(?:elim|ebilir mi(?:sin|yiz))?|geçiş yap(?:alım|abilir mi(?:sin|yiz))?)(?: şimdi)?)$")]
    private static partial Regex TurkishVoiceControl();

    [GeneratedRegex(@"\s+(?:ajan(?:ı|ına|ıyla|la|a)?|agent)$")]
    private static partial Regex SpokenAgentRole();

    [GeneratedRegex(@"(?:['’](?:[ae]|y[ae]|l[ae])|[yl]?[ae])$")]
    private static partial Regex TurkishAgentSuffix();
}
