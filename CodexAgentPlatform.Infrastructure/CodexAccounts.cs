using System.Text.Json;
using CodexAgentPlatform.Application;

namespace CodexAgentPlatform.Infrastructure;

public sealed class CodexAccounts(ICodexConnection codex)
{
    public Task<JsonElement> ReadAsync(CancellationToken ct) => codex.CallAsync("account/read", new { refreshToken = false }, ct);
    public Task<JsonElement> LoginAsync(CancellationToken ct) => codex.CallAsync("account/login/start", new { type = "chatgptDeviceCode" }, ct);
    public Task<JsonElement> CancelAsync(string loginId, CancellationToken ct) =>
        codex.CallAsync("account/login/cancel", new { loginId }, ct);
    public Task<JsonElement> LogoutAsync(CancellationToken ct) => codex.CallAsync("account/logout", null, ct);
    public Task<JsonElement> WindowsReadinessAsync(CancellationToken ct) => codex.CallAsync("windowsSandbox/readiness", null, ct);
    public Task<JsonElement> WindowsSetupAsync(CancellationToken ct) =>
        codex.CallAsync("windowsSandbox/setupStart", new { mode = "elevated" }, ct);

    public async Task<List<CodexModel>> ModelsAsync(CancellationToken ct)
    {
        List<CodexModel> models = [];
        var seen = new HashSet<string>();
        string? cursor = null;
        do
        {
            var page = await codex.CallAsync("model/list", new { cursor, limit = 100, includeHidden = false }, ct);
            models.AddRange(page.Items("data").Where(x => !x.True("hidden"))
                .Select(x => new CodexModel(x.String("model") ?? x.String("id") ?? "", x.String("displayName") ?? "",
                    x.True("isDefault"), x.String("defaultReasoningEffort"),
                    x.Items("supportedReasoningEfforts").Select(y => y.String("reasoningEffort")!).Where(y => y is not null).ToArray()))
                .Where(x => x.Id.Length > 0));
            cursor = page.String("nextCursor");
        } while (!string.IsNullOrEmpty(cursor) && seen.Add(cursor) && seen.Count < 20);
        return models.DistinctBy(m => m.Id).ToList();
    }
}
