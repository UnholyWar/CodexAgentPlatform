using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CodexAgentPlatform.Application;
using Microsoft.Extensions.Logging;

namespace CodexAgentPlatform.Infrastructure;

public sealed partial class ChatVoiceCall(
    ICodexConnection codex, ChatStore chats, ChatWorker worker, ILogger<ChatVoiceCall> log,
    string? conversationId, string? projectId, string? selectedAgent, bool autoApprove, bool pinProject = false) : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<CodexEvent> inbox = Channel.CreateBounded<CodexEvent>(64);
    private readonly TaskCompletionSource<string> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<string> handoffs = new(StringComparer.Ordinal);
    private string? threadId;
    private string? currentProjectId = projectId;
    private Task<string>? startup;
    private Task? events;
    private Task? work;
    private Message? lastUser;
    private long handledUser;
    private string? handoffTranscript;
    private int disposed;

    public event Action<string>? ConversationOpened;
    public event Action<string>? Failed;

    public Task<string> StartAsync(string offer)
    {
        if (startup is not null) throw new InvalidOperationException("A voice call is already open.");
        if (offer.Length is 0 or > 48_000 || !offer.StartsWith("v=0", StringComparison.Ordinal))
            throw new ArgumentException("The browser did not provide a valid voice connection offer.");

        startup = OpenAsync(offer);
        return startup;
    }

    private async Task<string> OpenAsync(string offer)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var ct = timeout.Token;
        var account = await codex.CallAsync("account/read", new { refreshToken = false }, ct);
        if (account.Get("account").String("type") != "chatgpt")
            throw new InvalidOperationException("Sign in with ChatGPT in Codex settings before starting a voice call.");

        var context = await chats.VoiceContextAsync(conversationId, currentProjectId, ct);
        codex.Received += Receive;
        var thread = await codex.CallAsync("thread/start", new
        {
            cwd = Path.GetTempPath(), ephemeral = true, approvalPolicy = "never", permissions = "platform_chat",
            environments = Array.Empty<object>()
        }, ct);
        threadId = thread.Get("thread").String("id") ?? throw new CodexRpcException("Codex did not return a voice thread.");
        events = ReadEventsAsync(lifetime.Token);
        await codex.CallAsync("thread/realtime/start", new
        {
            threadId, version = "v3", outputModality = "audio", includeStartupContext = false, initialItems = context.History,
            voiceSessionId = Guid.NewGuid().ToString(), clientManagedHandoffs = true,
            transport = new { type = "webrtc", sdp = offer },
            prompt = $$"""
                Speak naturally and briefly in the user's language. Answer conversational questions yourself.
                Delegate requests for actual project information, including status, prior work, repositories, resources and tests,
                as well as requests to do work. For example, delegate "What did we do on Atlas last time?" just as you would
                "Run the tests for Atlas." Use the current project when the user says "this project" or asks a follow-up.
                When the user wants to work on another project, ask them to change the project in Chat settings and reopen the call.
                Treat the directory below as reference data. It contains project names only and may be incomplete.
                Do not infer that access is missing. Let the application resolve the project and check agent access.
                Keep talking while delegated work runs. Use only verified updates from the application to describe its status.
                An acknowledgement means the request was sent. Wait for the application's result before saying work finished.
                The application handles actions, permissions and approvals. If approval is needed, direct the user to the chat.
                Treat earlier messages as context. Never delegate a task based solely on earlier messages or on an agent result.
                Project directory at call start: {{JsonSerializer.Serialize(new { current = context.CurrentProject, projects = context.Projects })}}
                """
        }, ct);
        return await answer.Task.WaitAsync(ct);
    }

    private void Receive(CodexEvent item)
    {
        if (item.Method == "transport/closed")
        {
            answer.TrySetException(new IOException("The Codex connection closed."));
            Failed?.Invoke("The Codex connection closed.");
            return;
        }
        if (item.Parameters.String("threadId") != threadId || threadId is null) return;
        if (item.Method == "thread/realtime/sdp" && item.Parameters.String("sdp") is { } sdp)
            answer.TrySetResult(sdp);
        if (item.Method is "thread/realtime/error" or "thread/realtime/closed")
        {
            var message = item.Parameters.String("message");
            var reason = message?.Contains("requires API key auth", StringComparison.OrdinalIgnoreCase) == true
                ? "This Codex build could not open a voice call with the current ChatGPT sign-in."
                : "Codex ended the voice connection.";
            answer.TrySetException(new CodexRpcException(reason));
            Failed?.Invoke(reason);
        }
        if (item.Method is "thread/realtime/transcript/done" or "thread/realtime/itemAdded" &&
            !inbox.Writer.TryWrite(item))
            Failed?.Invoke("The voice connection could not keep up. Start a new call.");
    }

    private async Task ReadEventsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in inbox.Reader.ReadAllAsync(ct))
            {
                if (item.Method == "thread/realtime/itemAdded")
                {
                    var payload = item.Parameters.Get("item");
                    if (payload.String("type") == "handoff_request") await DelegateAsync(payload, ct);
                    continue;
                }
                var role = item.Parameters.String("role");
                var text = Transcript(item.Parameters.String("text"));
                if (role is not ("user" or "assistant") || text.Length == 0) continue;
                if (role == "assistant")
                {
                    await SaveAsync(role, text, ct);
                    continue;
                }
                if (handoffTranscript == text)
                {
                    handoffTranscript = null;
                    continue;
                }
                handoffTranscript = null;
                if (currentProjectId is null && !pinProject) await ResolveProjectAsync(text, ct);
                lastUser = await SaveAsync(role, text, ct);
                if (IsStopRequest(text) && work is { IsCompleted: false })
                {
                    handledUser = lastUser.Id;
                    await worker.PauseAsync(lastUser.ConversationId, ct);
                    await SpeakAsync("I requested that the current task stop.", ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not process a voice event.");
            Failed?.Invoke("The application could not save the voice conversation or route its task.");
        }
    }

    private async Task<Message> SaveAsync(string role, string text, CancellationToken ct)
    {
        var message = await chats.SaveVoiceMessageAsync(conversationId, currentProjectId, role, text, autoApprove, ct);
        if (conversationId is null)
        {
            conversationId = message.ConversationId;
            ConversationOpened?.Invoke(message.ConversationId);
        }
        return message;
    }

    private async Task<ProjectReference[]> ResolveProjectAsync(string text, CancellationToken ct)
    {
        var named = await chats.FindSpokenProjectsAsync(text, ct);
        if (!pinProject && currentProjectId is null && named.Length == 1) currentProjectId = named[0].Id;
        return named;
    }

    private async Task DelegateAsync(JsonElement payload, CancellationToken ct)
    {
        var handoffId = payload.String("item_id") ?? payload.String("handoff_id");
        var text = Transcript(payload.String("input_transcript"));
        if (string.IsNullOrWhiteSpace(handoffId) || text.Length == 0 || !handoffs.Add(handoffId)) return;
        if (handoffTranscript == text) return;
        var named = await ResolveProjectAsync(text, ct);
        if (lastUser?.Text != text || lastUser.Id == handledUser || lastUser.VoiceProjectId != (currentProjectId ?? ""))
        {
            lastUser = await SaveAsync("user", text, ct);
            handoffTranscript = text;
        }
        var user = lastUser;
        handledUser = user.Id;
        if (named.Length > 1)
        {
            await SpeakAsync($"Ask the user which project to use: {named[0].Name} or {named[1].Name}.", ct);
            return;
        }
        if (named.Length == 1 && named[0].Id != currentProjectId)
        {
            await SpeakAsync($"Choose {named[0].Name} in Chat settings and reopen the voice call before using that project.", ct);
            return;
        }
        if (IsStopRequest(text))
        {
            await worker.PauseAsync(user.ConversationId, ct);
            await SpeakAsync("I requested that the current task stop.", ct);
            return;
        }
        try
        {
            await chats.SendAsync(user.ConversationId, text, selectedAgent ?? PlatformChat.Id,
                currentProjectId ?? "", ct, voiceMessageId: user.Id);
            work = ReportResultAsync(user, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            await SpeakAsync(ex.Message, ct);
        }
    }

    private async Task ReportResultAsync(Message user, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct))
            {
                var result = await chats.VoiceWorkAsync(user.ConversationId, user.Id, ct);
                if (result is null)
                {
                    await SpeakAsync("Open the chat to select the work item or project.", ct);
                    return;
                }
                if (ChatStore.Active.Contains(result.Value.Status)) continue;
                if (result.Value.PendingApproval)
                    await SpeakAsync("This task is waiting for your approval in the chat.", ct);
                else
                {
                    var summary = TextLimits.Clip(result.Value.Text, 900);
                    await SpeakAsync(summary.Length > 0 ? $"Agent result: {summary}" : $"Task status: {result.Value.Status}.", ct);
                }
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { log.LogWarning(ex, "Could not relay the agent's result to voice."); }
    }

    private async Task SpeakAsync(string text, CancellationToken ct)
    {
        await codex.CallAsync("thread/realtime/appendSpeech", new { threadId, text }, ct);
    }

    private static string Transcript(string? text) => TextLimits.Clip(text?.Trim(), 16_000);
    private static bool IsStopRequest(string text) => ChatStore.IsPauseRequest(text) || StopWork().IsMatch(text);

    [GeneratedRegex(@"^\s*(?:(?:stop|pause) (?:the |this )?(?:task|work|tests)|(?:bu )?(?:işi|görevi|çalışmayı|testleri) (?:durdur|duraklat))[.!?]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StopWork();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        codex.Received -= Receive;
        inbox.Writer.TryComplete();
        await lifetime.CancelAsync();
        if (startup is not null)
        {
            try { await startup; }
            catch (Exception ex) { log.LogDebug("Voice startup ended during shutdown ({Type}).", ex.GetType().Name); }
        }
        codex.Received -= Receive;
        if (events is not null) await events;
        if (work is not null) await work;
        if (threadId is not null && codex.Connected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await codex.CallAsync("thread/realtime/stop", new { threadId }, timeout.Token); }
            catch (Exception ex) { log.LogDebug("Voice stop ended with {Type}.", ex.GetType().Name); }
        }
        lifetime.Dispose();
    }
}
