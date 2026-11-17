using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using CodexAgentPlatform.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodexAgentPlatform.Tests;

public sealed partial class AuthenticationTests
{
    [Fact]
    public async Task HttpsSetupUsesCsrfAndOneTimeCodeThenProtectsChatAndSupportsLogout()
    {
        await using var fixture = await TestStorage.CreateAsync();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null);
        await using var app = PlatformHost.Build(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name, EnvironmentName = "Development",
            ContentRootPath = AppContext.BaseDirectory
        }, builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platform:Data"] = fixture.Paths.Data,
                ["Platform:Workspaces"] = fixture.Paths.Workspaces,
                ["Platform:LocalBootstrap"] = "false",
                ["Platform:AllowLocalHttp"] = "false"
            });
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.UseHttps(cert)));
        });
        await PlatformHost.InitializeAsync(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var ct = timeout.Token;
        await app.StartAsync(ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, serverCert, _, _) => cert.Thumbprint == serverCert?.Thumbprint
        }) { BaseAddress = new Uri(address) };

        using (var denied = await client.GetAsync("/", ct))
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        const string password = "Test-passphrase!2026";
        var code = (await File.ReadAllTextAsync(fixture.Paths.BootstrapFile, ct)).Trim();
        var form = new Dictionary<string, string>
        {
            ["username"] = "operator", ["password"] = password, ["confirm"] = password, ["bootstrap"] = code
        };
        using (var invalid = await client.PostAsync("/session/setup", new FormUrlEncodedContent(form), ct))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        form["__RequestVerificationToken"] = await CsrfAsync(client, "/setup", ct);
        form["bootstrap"] = "admin";
        using (var invalid = await client.PostAsync("/session/setup", new FormUrlEncodedContent(form), ct))
            Assert.Equal("/setup?error=code", invalid.Headers.Location?.OriginalString);
        form["bootstrap"] = code;
        using (var valid = await client.PostAsync("/session/setup", new FormUrlEncodedContent(form), ct))
        {
            Assert.Equal(HttpStatusCode.Redirect, valid.StatusCode);
            Assert.Equal("/", valid.Headers.Location?.OriginalString);
            var cookie = Assert.Single(valid.Headers.GetValues("Set-Cookie"), x => x.StartsWith("__Host-CodexPlatform="));
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        }
        Assert.False(File.Exists(fixture.Paths.BootstrapFile));
        var output = await client.GetStringAsync("/", ct);
        Assert.Contains("chat-scroll", output);
        Assert.Contains("composer", output);
        var logoutToken = Token(output);
        using (var logout = await client.PostAsync("/session/logout",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = logoutToken }), ct))
            Assert.Equal("/login", logout.Headers.Location?.OriginalString);
        using (var denied = await client.GetAsync("/", ct))
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);

        form["__RequestVerificationToken"] = await CsrfAsync(client, "/login", ct);
        form["username"] = "admin";
        form["password"] = "admin";
        using (var oldLogin = await client.PostAsync("/session/login", new FormUrlEncodedContent(form), ct))
            Assert.Equal("/login?error=invalid", oldLogin.Headers.Location?.OriginalString);
        form["username"] = "operator";
        form["password"] = password;
        using (var login = await client.PostAsync("/session/login", new FormUrlEncodedContent(form), ct))
            Assert.Equal("/", login.Headers.Location?.OriginalString);
        foreach (var page in new[] { "/", "/conversations", "/agents", "/work", "/memory", "/timeline", "/projects", "/resources", "/approvals", "/codex", "/system" })
        {
            using var response = await client.GetAsync(page, ct);
            Assert.True(response.IsSuccessStatusCode, $"{page} returned {response.StatusCode}");
        }
        await app.StopAsync(ct);
    }

    private static async Task<string> CsrfAsync(HttpClient client, string path, CancellationToken ct) =>
        Token(await client.GetStringAsync(path, ct));

    private static string Token(string html)
    {
        var match = TokenInput().Match(html);
        Assert.True(match.Success, "Antiforgery token missing in HTML.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"\\s+value=\"([^\"]+)\"")]
    private static partial Regex TokenInput();
}
