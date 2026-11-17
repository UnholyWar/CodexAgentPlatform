using CodexAgentPlatform;

var app = PlatformHost.Build(new WebApplicationOptions { Args = args });
await PlatformHost.InitializeAsync(app);
await app.RunAsync();

public partial class Program { }
