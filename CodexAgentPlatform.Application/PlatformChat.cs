namespace CodexAgentPlatform.Application;

public static class PlatformChat
{
    public const string Id = "normal-chat";
    public const string Name = "Normal chat";

    public static Agent CreateAgent() => new() { Id = Id, Name = Name, AllowedTools = "" };
}
