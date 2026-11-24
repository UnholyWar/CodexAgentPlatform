using System.Net;
using System.Security.Cryptography;
using System.Text;
using CodexAgentPlatform.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace CodexAgentPlatform.Auth;

public static class OwnerEndpoints
{
    public static void MapOwnerEndpoints(this WebApplication app)
    {
        app.MapPost("/session/setup", SetupAsync).AllowAnonymous().RequireRateLimiting("auth");
        app.MapPost("/session/login", LoginAsync).AllowAnonymous().RequireRateLimiting("auth");
        app.MapPost("/session/logout", LogoutAsync).RequireAuthorization();
    }

    public static bool LocalSetupAllowed(HttpContext context, IWebHostEnvironment environment, IConfiguration config)
    {
        var remote = context.Connection.RemoteIpAddress;
        return environment.IsDevelopment() && config.GetValue<bool>("Platform:LocalBootstrap") &&
            remote is not null && IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote) &&
            (context.Request.Host.Host is "localhost" or "127.0.0.1" or "[::1]") &&
            !context.Request.Headers.Keys.Any(x => x.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) ||
                x.Equals("Forwarded", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<IResult> SetupAsync(HttpContext context, IAntiforgery csrf, UserManager<PlatformUser> users,
        SignInManager<PlatformUser> signIn, PlatformPaths paths, IWebHostEnvironment environment, IConfiguration config)
    {
        if (!await ValidFormAsync(context, csrf)) return Results.BadRequest("Invalid form verification.");
        if (await users.FindByIdAsync(PlatformUser.OwnerId) is not null) return Results.Redirect("/login");
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var supplied = form["bootstrap"].ToString().Trim();
        var local = LocalSetupAllowed(context, environment, config) && supplied == "admin";
        var expected = File.Exists(paths.BootstrapFile)
            ? (await File.ReadAllTextAsync(paths.BootstrapFile, context.RequestAborted)).Trim() : null;
        if (!local && (expected is null || expected.Length != 64 || supplied.Length != 64 ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied.ToUpperInvariant()))))
            return Results.Redirect("/setup?error=code");

        var name = form["username"].ToString().Trim();
        var password = form["password"].ToString();
        if (name.Length is < 3 or > 64 || name.Equals("admin", StringComparison.OrdinalIgnoreCase))
            return Results.Redirect("/setup?error=username");
        if (password.Length is < 14 or > 128 || password.Contains(name, StringComparison.OrdinalIgnoreCase))
            return Results.Redirect("/setup?error=password");
        if (password != form["confirm"].ToString())
            return Results.Redirect("/setup?error=confirmation");

        var user = new PlatformUser { Id = PlatformUser.OwnerId, UserName = name };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            var error = created.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidUserName)) ? "username"
                : created.Errors.Any(e => e.Code.StartsWith("Password", StringComparison.Ordinal)) ? "password" : "credentials";
            return Results.Redirect("/setup?error=" + error);
        }
        try { File.Delete(paths.BootstrapFile); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        await signIn.SignInAsync(user, isPersistent: false);
        context.RequestServices.GetRequiredService<ReleaseCatalog>().CheckForUpdates();
        return Results.Redirect("/");
    }

    private static async Task<IResult> LoginAsync(HttpContext context, IAntiforgery csrf, UserManager<PlatformUser> users,
        SignInManager<PlatformUser> signIn)
    {
        if (!await ValidFormAsync(context, csrf)) return Results.BadRequest("Invalid form verification.");
        if (await users.FindByIdAsync(PlatformUser.OwnerId) is null) return Results.Redirect("/setup");
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var userName = form["username"].ToString().Trim();
        var password = form["password"].ToString();
        if (userName.Length <= 64 && password.Length <= 128 &&
            (await signIn.PasswordSignInAsync(userName, password, isPersistent: false, lockoutOnFailure: false)).Succeeded)
        {
            context.RequestServices.GetRequiredService<ReleaseCatalog>().CheckForUpdates();
            return Results.Redirect("/");
        }
        return Results.Redirect("/login?error=invalid");
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, IAntiforgery csrf,
        SignInManager<PlatformUser> signIn, UserManager<PlatformUser> users)
    {
        if (!await ValidFormAsync(context, csrf)) return Results.BadRequest("Invalid form verification.");
        if (await users.GetUserAsync(context.User) is { } user)
            await users.UpdateSecurityStampAsync(user);
        await signIn.SignOutAsync();
        return Results.Redirect("/login");
    }

    private static async Task<bool> ValidFormAsync(HttpContext context, IAntiforgery csrf)
    {
        if (!context.Request.HasFormContentType) return false;
        try
        {
            await csrf.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException) { return false; }
    }
}
