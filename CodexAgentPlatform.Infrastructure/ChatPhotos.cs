using System.Text.RegularExpressions;
using CodexAgentPlatform.Application;
using Microsoft.EntityFrameworkCore;

namespace CodexAgentPlatform.Infrastructure;

public static partial class ChatPhotos
{
    public const int MaxCount = 4;
    public const int MaxBytes = 8 * 1024 * 1024;

    internal static MessagePhoto[] Prepare(IReadOnlyList<PhotoUpload>? uploads)
    {
        if (uploads is null || uploads.Count == 0) return [];
        if (uploads.Count > MaxCount) throw new ArgumentException($"Attach up to {MaxCount} photos per message.");
        return uploads.Select(photo => new MessagePhoto
        {
            Name = DisplayName(photo.Name),
            ContentType = ContentType(photo.Bytes),
            Bytes = photo.Bytes
        }).ToArray();
    }

    public static string DisplayName(string name)
    {
        var fileName = Path.GetFileName(name.Replace('\\', '/')).Trim();
        return TextLimits.Clip(string.IsNullOrWhiteSpace(fileName) ? "Photo" : fileName, 100);
    }

    private static string ContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaxBytes) throw new ArgumentException("Each photo must be 8 MB or smaller.");
        if (bytes.Length >= 33 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            bytes.Slice(12, 4).SequenceEqual("IHDR"u8)) return "image/png";
        if (bytes.Length >= 16 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 &&
            bytes[^2] == 255 && bytes[^1] == 217) return "image/jpeg";
        if (bytes.Length >= 20 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8) &&
            (bytes.Slice(12, 4).SequenceEqual("VP8 "u8) || bytes.Slice(12, 4).SequenceEqual("VP8L"u8) ||
             bytes.Slice(12, 4).SequenceEqual("VP8X"u8))) return "image/webp";
        throw new ArgumentException("Choose a readable JPEG, PNG or WebP photo.");
    }

    internal static void Attach(PlatformDb db, long messageId, IEnumerable<MessagePhoto> photos)
    {
        foreach (var photo in photos)
        {
            photo.MessageId = messageId;
            db.MessagePhotos.Add(photo);
        }
    }

    internal static async Task<(List<string> Paths, bool ForCurrentRequest)> StageAsync(
        PlatformDb db, ChatRun run, string workspace, bool includeRecent, CancellationToken ct)
    {
        var messageId = run.OriginMessageId ?? run.UserMessageId;
        var visible = db.MessagePhotos.AsNoTracking().Where(p =>
            db.Messages.Any(m => m.Id == p.MessageId && m.ConversationId == run.ConversationId && m.Role == "user"));
        var photos = await visible.Where(p => p.MessageId == messageId).ToListAsync(ct);
        var forCurrentRequest = photos.Count > 0;
        if (!forCurrentRequest && includeRecent)
        {
            var recent = await visible.Where(p => p.MessageId < messageId && db.Runs.Any(r => r.ConversationId == run.ConversationId &&
                    (r.UserMessageId == p.MessageId || r.OriginMessageId == p.MessageId) &&
                    r.ProjectId == run.ProjectId && (run.ProjectId != null || r.Workspace == run.Workspace)))
                .MaxAsync(p => (long?)p.MessageId, ct);
            if (recent is { } earlier)
                photos = await visible.Where(p => p.MessageId == earlier).ToListAsync(ct);
        }
        var paths = new List<string>(photos.Count);
        foreach (var photo in photos)
        {
            var root = Path.Combine(workspace, "chat-images");
            Directory.CreateDirectory(root);
            PlatformPaths.CheckLinks(root);
            var folder = Path.Combine(root, photo.Id);
            Directory.CreateDirectory(folder);
            PlatformPaths.CheckLinks(folder);
            var path = Path.Combine(folder, StagedName(photo.Name, photo.ContentType));
            var temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, useAsync: true))
                    await stream.WriteAsync(photo.Bytes, ct);
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
            paths.Add(path);
        }
        return (paths, forCurrentRequest);
    }

    internal static string StagedName(string name, string contentType)
    {
        var stem = UnsafeFileName().Replace(Path.GetFileNameWithoutExtension(name), "-").Trim('-', '.');
        var extension = contentType switch { "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
        return (stem.Length == 0 ? "photo" : $"photo-{stem}") + extension;
    }

    [GeneratedRegex("[^a-zA-Z0-9_.-]+")]
    private static partial Regex UnsafeFileName();
}
