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
    private sealed class LicensedFixture : CodexAgentPlatform.Application.ILicenseAccess
    {
        public bool IsValid { get; set; }
        public string Code { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        public Task<bool> ActivateAsync(string code, CancellationToken ct) => Task.FromResult(IsValid = code == Code);
    }

    [Fact]
    public async Task HttpsSetupUsesCsrfAndOneTimeCodeThenProtectsChatAndSupportsLogout()
    {
        await using var fixture = await TestStorage.CreateAsync();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null);
        var license = new LicensedFixture();
        await using var app = PlatformHost.Build(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name, EnvironmentName = "Development",
            ContentRootPath = AppContext.BaseDirectory
        }, builder =>
        {
            builder.Services.AddSingleton<CodexAgentPlatform.Application.ILicenseAccess>(license);
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
        var cookies = new CookieContainer();
        using var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CookieContainer = cookies,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, serverCert, _, _) => cert.Thumbprint == serverCert?.Thumbprint
        }) { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("tr-TR");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz", ct)).StatusCode);
        Assert.Equal("/license", (await client.GetAsync("/", ct)).Headers.Location?.OriginalString);
        foreach (var route in new[] { "/login", "/setup", "/memory", "/artifacts/test.txt" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(route, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/_blazor/negotiate", null, ct)).StatusCode);
        Assert.Contains("Kurulum kodu", await client.GetStringAsync("/license", ct));
        var bootstrap = await File.ReadAllTextAsync(fixture.Paths.BootstrapFile, ct);
        using var activated = await client.PostAsync("/license", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["bootstrap"] = bootstrap.Trim(), ["code"] = license.Code,
            ["__RequestVerificationToken"] = await CsrfAsync(client, "/license", ct)
        }), ct);
        Assert.Equal("/setup", activated.Headers.Location?.OriginalString);
        var setupPage = await client.GetStringAsync("/setup", ct);
        Assert.Contains("<html lang=\"en\">", setupPage);
        Assert.Contains("Create administrator account", setupPage);

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
        Assert.Contains("Invalid setup code.", await client.GetStringAsync("/setup?error=code", ct));
        form["bootstrap"] = code;
        form["confirm"] = "Wrong-confirmation!2026";
        using (var invalid = await client.PostAsync("/session/setup", new FormUrlEncodedContent(form), ct))
            Assert.Equal("/setup?error=confirmation", invalid.Headers.Location?.OriginalString);
        Assert.Contains("The two password fields do not match.", await client.GetStringAsync("/setup?error=confirmation", ct));
        form["confirm"] = password;
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

        license.IsValid = false;
        var activation = await client.GetStringAsync("/license", ct);
        Assert.Contains("Yönetici parolası", activation);
        Assert.DoesNotContain("Kurulum kodu", activation);
        var activationForm = new Dictionary<string, string>
        {
            ["username"] = "operator", ["password"] = "incorrect", ["code"] = license.Code,
            ["__RequestVerificationToken"] = Token(activation)
        };
        using var refused = await client.PostAsync("/license", new FormUrlEncodedContent(activationForm), ct);
        Assert.Equal("/license?error=1", refused.Headers.Location?.OriginalString);
        Assert.False(license.IsValid);
        activationForm["password"] = password;
        using var reactivated = await client.PostAsync("/license", new FormUrlEncodedContent(activationForm), ct);
        Assert.Equal("/login", reactivated.Headers.Location?.OriginalString);
        form["__RequestVerificationToken"] = await CsrfAsync(client, "/login", ct);
        form["username"] = "admin";
        form["password"] = "admin";
        using (var oldLogin = await client.PostAsync("/session/login", new FormUrlEncodedContent(form), ct))
            Assert.Equal("/login?error=invalid", oldLogin.Headers.Location?.OriginalString);
        Assert.Contains("Invalid username or password.", await client.GetStringAsync("/login?error=invalid", ct));
        form["username"] = "operator";
        form["password"] = password;
        using (var login = await client.PostAsync("/session/login", new FormUrlEncodedContent(form), ct))
            Assert.Equal("/", login.Headers.Location?.OriginalString);
        foreach (var (page, heading) in new[]
        {
            ("/", "Chat"), ("/conversations", "Conversations"), ("/agents", "Agents"),
            ("/work", "Work history"), ("/memory", "Memory"), ("/timeline", "Activity timeline"),
            ("/projects", "Projects"), ("/resources", "Resources"), ("/approvals", "Approvals"),
            ("/codex", "Codex settings"), ("/system", "System settings")
        })
        {
            using var response = await client.GetAsync(page, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            var target = new Uri(client.BaseAddress!, page);
            var ownerCookie = cookies.GetCookies(target)["__Host-CodexPlatform"] is not null;
            var ownerLayout = raw.Contains("action=\"/session/logout\"", StringComparison.Ordinal);
            var exactHeading = Regex.IsMatch(raw, """<h1(?: b-[a-z0-9]{10}(?:="")?)?>""" + Regex.Escape(heading) + "</h1>");
            var trace = $"PAGE_CHECK page={page} status={(int)response.StatusCode} " +
                $"sameUrl={target == response.RequestMessage?.RequestUri} redirect={response.Headers.Location is not null} " +
                $"ownerCookie={ownerCookie} ownerLayout={ownerLayout} exactHeading={exactHeading} " +
                $"exceptionPage={raw.Contains("Exception:", StringComparison.Ordinal)}";
            Assert.True(response.StatusCode == HttpStatusCode.OK && target == response.RequestMessage?.RequestUri &&
                response.Headers.Location is null && ownerCookie && ownerLayout && exactHeading, trace);
            var html = WebUtility.HtmlDecode(raw);
            Assert.Contains("<html lang=\"en\">", html);
            Assert.DoesNotMatch("[çÇğĞıİöÖşŞüÜ]", html);
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
