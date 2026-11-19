using System.Text.RegularExpressions;
using System.Threading.Channels;
using CodexAgentPlatform.Application;
using Microsoft.Extensions.Logging;

namespace CodexAgentPlatform.Infrastructure;

public sealed record VoiceSpeaker(string Id, string Name, string? Voice);

public sealed record VoiceReply(VoiceSpeaker Speaker, string Text);

public sealed partial class ChatVoiceCall(
    ICodexConnection codex, ChatStore chats, ChatWorker worker, ILogger<ChatVoiceCall> log,
    string? conversationId, string? projectId, string? selectedAgent, bool autoApprove, bool pinProject = false) : IAsyncDisposable
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
    private VoiceSpeaker activeSpeaker = null!;
    private bool firstUserMessage = true;
    // Realtime may deliver either or both representations of an utterance, in either order.
    private readonly List<(string Key, bool FromHandoff, DateTime ReceivedAt)> unpaired = [];
    private int workGeneration;
    private int disposed;

    public event Action<string>? ConversationOpened;
    public event Action<VoiceSpeaker>? SpeakerChanged;
    public event Action<VoiceReply>? ReplyReady;
    public event Action? UserSpoke;
    public event Action<string>? Failed;
    public VoiceSpeaker Speaker => activeSpeaker;

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

        activeSpeaker = await chats.VoiceEntryAsync(selectedAgent, ct);
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
            threadId, version = "v3", outputModality = "audio", includeStartupContext = false,
            voiceSessionId = Guid.NewGuid().ToString(), clientManagedHandoffs = true, delegationAckFiller = false,
            transport = new { type = "webrtc", sdp = offer },
            prompt = """
                Hand off every complete caller utterance to the application, even greetings or brief acknowledgements.
                Preserve the original language and words. The application handles all replies. Do not answer the caller yourself.
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
        var input = item.Method == "thread/realtime/transcript/done" && item.Parameters.String("role") == "user" ||
            item.Method == "thread/realtime/itemAdded" && item.Parameters.Get("item").String("type") == "handoff_request";
        if (input && !inbox.Writer.TryWrite(item))
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
                var text = Transcript(item.Parameters.String("text"));
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

    private async Task<Message> SaveAsync(string text, CancellationToken ct)
    {
        var message = await chats.SaveVoiceUserAsync(conversationId, currentProjectId, text, autoApprove,
            firstUserMessage ? activeSpeaker.Id : null, ct);
        firstUserMessage = false;
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
        var now = DateTime.UtcNow;
        var key = new string(text.Where(char.IsLetterOrDigit).ToArray());
        if (key.Length == 0) return;
        unpaired.RemoveAll(turn => now - turn.ReceivedAt > TimeSpan.FromSeconds(20));
        var match = unpaired.FindIndex(turn => turn.FromHandoff != fromHandoff &&
            string.Equals(turn.Key, key, StringComparison.OrdinalIgnoreCase));
        if (match >= 0)
        {
            unpaired.RemoveAt(match);
            return;
        }
        if (unpaired.Count >= 16) unpaired.RemoveAt(0);
        unpaired.Add((key, fromHandoff, now));
        UserSpoke?.Invoke();
        var user = await SaveAsync(text, ct);
        (VoiceSpeaker? Changed, VoiceReply? Reply) control;
        await submission.WaitAsync(ct);
        try
        {
            control = await chats.VoiceControlAsync(user, activeSpeaker, ct);
            if (control.Changed is { } speaker) SetSpeaker(speaker);
        }
        finally { submission.Release(); }
        if (control.Reply is { } reply)
        {
            ReplyReady?.Invoke(reply);
            return;
        }
        var named = ProjectRouting.Intent(text).Create || ProjectRouting.GlobalResourceInventory(text)
            ? [] : await chats.FindSpokenProjectsAsync(text, ct);
        if (named.Length > 1)
        {
            await ReplyAsync(user, activeSpeaker, $"Which project should we use, {named[0].Name} or {named[1].Name}?", ct);
            return;
        }
        if (named.Length == 1 && named[0].Id != currentProjectId)
        {
            if (currentProjectId is not null || pinProject)
            {
                await ReplyAsync(user, activeSpeaker, $"Choose {named[0].Name} in Chat settings and reopen the voice call before using that project.", ct);
                return;
            }
            currentProjectId = named[0].Id;
            user = await chats.ScopeVoiceUserAsync(user, currentProjectId, ct);
        }
        if (IsStopRequest(text))
        {
            Interlocked.Increment(ref workGeneration);
            await submission.WaitAsync(ct);
            try { await worker.PauseAsync(user.ConversationId, ct); }
            finally { submission.Release(); }
            await ReplyAsync(user, activeSpeaker, "I requested that the current task stop.", ct);
            return;
        }
        work = RunTurnAsync(work, user, workGeneration, ct);
    }

    private async Task RunTurnAsync(Task? previous, Message user, int generation, CancellationToken ct)
    {
        try
        {
            if (previous is not null) await previous;
            VoiceReply? clarification;
            await submission.WaitAsync(ct);
            try
            {
                if (Volatile.Read(ref workGeneration) != generation) return;
                clarification = await chats.SubmitVoiceAsync(user, activeSpeaker.Id, ct);
                if (clarification is not null) SetSpeaker(clarification.Speaker);
            }
            finally { submission.Release(); }
            if (clarification is not null) ReplyReady?.Invoke(clarification);
            else await ReportResultAsync(user, generation, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            await ReplyAsync(user, activeSpeaker, ex.Message, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not route a voice turn.");
            Failed?.Invoke("The application could not route the voice request.");
        }
    }

    private async Task ReportResultAsync(Message user, int generation, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            bool approvalAnnounced = false;
            while (await timer.WaitForNextTickAsync(ct) && Volatile.Read(ref workGeneration) == generation)
            {
                var result = await chats.VoiceWorkAsync(user.ConversationId, user.Id, ct);
                if (Volatile.Read(ref workGeneration) != generation) return;
                if (result is null)
                {
                    await ReplyAsync(user, activeSpeaker, "Open the chat to select the work item or project.", ct);
                    return;
                }
                if (result.Value.ConversationAgentId is { } coordinator && coordinator != activeSpeaker.Id)
                {
                    var previous = activeSpeaker;
                    var next = await chats.VoiceSpeakerAsync(coordinator, ct);
                    if (activeSpeaker == previous && next is not null) SetSpeaker(next);
                }
                if (result.Value.PendingApproval && !approvalAnnounced)
                {
                    var agent = await chats.VoiceSpeakerAsync(result.Value.AgentId, ct) ?? activeSpeaker;
                    ReplyReady?.Invoke(new(agent, "This task is waiting for your approval in the chat."));
                    approvalAnnounced = true;
                }
                if (ChatStore.Active.Contains(result.Value.Status)) continue;
                if (!pinProject && result.Value.ProjectId is { } projectId) currentProjectId = projectId;
                if (!result.Value.PendingApproval)
                {
                    var agent = await chats.VoiceSpeakerAsync(result.Value.AgentId, ct) ?? activeSpeaker;
                    var text = result.Value.Text;
                    ReplyReady?.Invoke(new(agent, text.Length > 0 ? text : $"Task status: {result.Value.Status}."));
                }
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { log.LogWarning(ex, "Could not relay the agent's result to voice."); }
    }

    private void SetSpeaker(VoiceSpeaker speaker)
    {
        var changed = activeSpeaker.Id != speaker.Id;
        activeSpeaker = speaker;
        if (changed) SpeakerChanged?.Invoke(speaker);
    }

    private async Task ReplyAsync(Message user, VoiceSpeaker speaker, string text, CancellationToken ct)
    {
        var reply = new VoiceReply(speaker, text);
        await chats.RecordVoiceReplyAsync(user, reply, ct);
        ReplyReady?.Invoke(reply);
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
