using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CodexAgentPlatform.Application;
using Microsoft.Extensions.Logging;

namespace CodexAgentPlatform.Infrastructure;

public sealed partial class ChatVoiceCall(
    ICodexConnection codex, ChatStore chats, ChatWorker worker, ILogger<ChatVoiceCall> log,
    string? conversationId, string? projectId, string? selectedAgent, bool autoApprove, bool pinProject = false,
    string? voiceAgentId = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<CodexEvent> inbox = Channel.CreateBounded<CodexEvent>(64);
    private readonly TaskCompletionSource<string> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<string> handoffs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim submission = new(1);
    private string? threadId;
    private string? currentProjectId = projectId;
    private Task<string>? startup;
    private Task? events;
    private Task? work;
    // Realtime may deliver either or both representations of an utterance, in either order.
    private readonly List<(Message User, bool FromHandoff, bool Routed)> unpaired = [];
    private int workGeneration;
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
        var voice = await chats.AgentVoiceAsync(voiceAgentId ?? selectedAgent, currentProjectId, ct);
        if (voice is not null)
        {
            try
            {
                if (!(await new CodexAccounts(codex).VoicesAsync(ct)).Contains(voice, StringComparer.Ordinal)) voice = null;
            }
            catch (CodexRpcException ex)
            {
                log.LogDebug(ex, "Could not verify the requested Realtime voice. Using the default voice.");
                voice = null;
            }
        }
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
            threadId, version = "v3", voice, outputModality = "audio", includeStartupContext = false, initialItems = context.History,
            voiceSessionId = Guid.NewGuid().ToString(), clientManagedHandoffs = true,
            transport = new { type = "webrtc", sdp = offer },
            prompt = $$"""
                Speak naturally and briefly in the user's language. In general chat answer conversational questions yourself.
                With a current project or selected specialist, the application automatically sends the user's turns to
                platform chat and returns the answer to this call. This also applies after the user names a project.
                Let that answer supply the facts. Never claim that files, repositories or tools are inaccessible
                based on what you personally see in the voice session. Wait for the application's verified result.
                Selected specialist at call start: {{JsonSerializer.Serialize(selectedAgent == PlatformChat.Id ? null : selectedAgent)}}
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
                    if (payload.String("type") == "handoff_request" &&
                        (payload.String("item_id") ?? payload.String("handoff_id")) is { Length: > 0 } id &&
                        handoffs.Add(id))
                        await ReceiveUserAsync(Transcript(payload.String("input_transcript")), true, ct);
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
                await ReceiveUserAsync(text, false, ct);
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

    private async Task ReceiveUserAsync(string text, bool fromHandoff, CancellationToken ct)
    {
        if (text.Length == 0) return;
        var match = unpaired.FindIndex(turn => turn.FromHandoff != fromHandoff &&
            string.Equals(turn.User.Text.TrimEnd('.', '!', '?'), text.TrimEnd('.', '!', '?'), StringComparison.OrdinalIgnoreCase));
        Message? user = null;
        if (match >= 0)
        {
            var paired = unpaired[match];
            unpaired.RemoveAt(match);
            if (paired.Routed) return;
            user = paired.User;
        }
        var intent = ProjectRouting.Intent(text);
        var named = intent.Create ? [] : await chats.FindSpokenProjectsAsync(text, ct);
        if (user is null)
        {
            if (currentProjectId is null && !pinProject && named.Length == 1) currentProjectId = named[0].Id;
            user = await SaveAsync("user", text, ct);
            var route = fromHandoff || intent.Create || ProjectRouting.RequestsArchitect(text) || currentProjectId is not null || named.Length > 0 ||
                selectedAgent is not null && selectedAgent != PlatformChat.Id || IsStopRequest(text) && work is { IsCompleted: false };
            if (unpaired.Count >= 16) unpaired.RemoveAt(0);
            unpaired.Add((user, fromHandoff, route));
            if (!route) return;
        }
        if (named.Length > 1)
        {
            await SpeakAsync($"Ask the user which project to use: {named[0].Name} or {named[1].Name}.", ct);
            return;
        }
        if (named.Length == 1 && named[0].Id != user.VoiceProjectId)
        {
            await SpeakAsync($"Choose {named[0].Name} in Chat settings and reopen the voice call before using that project.", ct);
            return;
        }
        if (IsStopRequest(text))
        {
            Interlocked.Increment(ref workGeneration);
            await submission.WaitAsync(ct);
            try { await worker.PauseAsync(user.ConversationId, ct); }
            finally { submission.Release(); }
            await SpeakAsync("I requested that the current task stop.", ct);
            return;
        }
        work = RunTurnAsync(work, user, workGeneration, ct);
    }

    private async Task RunTurnAsync(Task? previous, Message user, int generation, CancellationToken ct)
    {
        try
        {
            if (previous is not null) await previous;
            string? clarification;
            await submission.WaitAsync(ct);
            try
            {
                if (Volatile.Read(ref workGeneration) != generation) return;
                clarification = await chats.SubmitVoiceAsync(user, selectedAgent, ct);
            }
            finally { submission.Release(); }
            if (clarification is not null) await SpeakAsync(clarification, ct);
            else await ReportResultAsync(user, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            await SpeakAsync(ex.Message, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not route a voice turn.");
            Failed?.Invoke("The application could not route the voice request.");
        }
    }

    private async Task ReportResultAsync(Message user, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            bool approvalAnnounced = false;
            while (await timer.WaitForNextTickAsync(ct))
            {
                var result = await chats.VoiceWorkAsync(user.ConversationId, user.Id, ct);
                if (result is null)
                {
                    await SpeakAsync("Open the chat to select the work item or project.", ct);
                    return;
                }
                if (result.Value.PendingApproval && !approvalAnnounced)
                {
                    await SpeakAsync("This task is waiting for your approval in the chat.", ct);
                    approvalAnnounced = true;
                }
                if (ChatStore.Active.Contains(result.Value.Status)) continue;
                if (result.Value.ProjectId is { } projectId) currentProjectId = projectId;
                var summary = TextLimits.Clip(result.Value.Text, 900);
                if (!result.Value.PendingApproval)
                    await SpeakAsync(summary.Length > 0 ? $"Agent result: {summary}" : $"Task status: {result.Value.Status}.", ct);
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
        submission.Dispose();
        if (threadId is not null && codex.Connected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await codex.CallAsync("thread/realtime/stop", new { threadId }, timeout.Token); }
            catch (Exception ex) { log.LogDebug("Voice stop ended with {Type}.", ex.GetType().Name); }
        }
        lifetime.Dispose();
    }
}
