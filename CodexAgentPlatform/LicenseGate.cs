using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodexAgentPlatform.Application;
using CodexAgentPlatform.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace CodexAgentPlatform;

internal static class LicenseGate
{
    public static void UseLicenseGate(this WebApplication app)
    {
        var access = app.Services.GetRequiredService<ILicenseAccess>();
        var email = app.Configuration["License:ContactEmail"];
        var linkedin = app.Configuration["License:LinkedIn"];
        var contact = new List<string>();
        if (MailAddress.TryCreate(email ?? "", out var address) && address.Address == email)
            contact.Add(address.Address);
        if (Uri.TryCreate(linkedin, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            uri.Host is "linkedin.com" or "www.linkedin.com" && uri.UserInfo.Length == 0 &&
            uri.Query.Length == 0 && uri.Fragment.Length == 0)
            contact.Add(uri.AbsoluteUri);
        var info = contact.Count == 0 ? "Lisans iletişim bilgisi henüz tanımlanmadı." : string.Join(" · ", contact);
        app.Logger.LogInformation("Lisans iletişim bilgisi: {Contact}", info);

        app.Use(async (ctx, next) =>
        {
            var read = HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method);
            if (read && ctx.Request.Path == "/healthz") { ctx.Response.StatusCode = 200; return; }
            if (access.IsValid || ctx.Request.Path == "/license") { await next(); return; }
            ctx.Response.Headers.CacheControl = "no-store";
            if (read && ctx.Request.Path == "/") ctx.Response.Redirect("/license");
            else ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        });

        app.MapGet("/license", async (HttpContext ctx, IAntiforgery csrf, UserManager<PlatformUser> users) =>
        {
            if (access.IsValid) return Results.Redirect("/");
            if (!ctx.Request.IsHttps) return Results.StatusCode(403);
            var owner = await users.FindByIdAsync(PlatformUser.OwnerId);
            var identity = owner is null
                ? "<label>Kurulum kodu <input type='password' name='bootstrap' required></label>"
                : "<label>Yönetici adı <input name='username' autocomplete='username' required></label><label>Yönetici parolası <input name='password' type='password' autocomplete='current-password' required></label>";
            var tokens = csrf.GetAndStoreTokens(ctx);
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
            var scheme = ctx.Request.Cookies["platform-theme"] switch
            {
                "light" => "light", "dark" => "dark", _ => "light dark"
            };
            var message = ctx.Request.Query.ContainsKey("error") ? "<p>Aktivasyon tamamlanamadı. Bilgilerinizi kontrol edip yeniden deneyin.</p>" : "";
            return Results.Content($"""
                <!doctype html><html lang="tr"><meta charset="utf-8"><meta name="color-scheme" content="{scheme}"><meta name="viewport" content="width=device-width,initial-scale=1">
                <title>Lisans aktivasyonu</title><main><h1>Lisans aktivasyonu</h1><p>{WebUtility.HtmlEncode(info)}</p>{message}
                <form method="post" action="/license"><input type="hidden" name="{WebUtility.HtmlEncode(tokens.FormFieldName)}" value="{WebUtility.HtmlEncode(tokens.RequestToken)}">
                {identity}<label>Lisans kodu <input type="password" name="code" autocomplete="off" maxlength="43" required></label>
                <button>Etkinleştir</button></form></main></html>
                """, "text/html; charset=utf-8");
        }).AllowAnonymous();
        app.MapPost("/license", ActivateAsync).AllowAnonymous().RequireRateLimiting("auth");
    }

    private static async Task<IResult> ActivateAsync(HttpContext ctx, IAntiforgery csrf, UserManager<PlatformUser> users,
        PlatformPaths paths, ILicenseAccess access)
    {
        if (!ctx.Request.IsHttps || !ctx.Request.HasFormContentType || access.IsValid) return Results.StatusCode(403);
        try { await csrf.ValidateRequestAsync(ctx); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(); }
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var code = form["code"].ToString();
        var owner = await users.FindByIdAsync(PlatformUser.OwnerId);
        bool authorized;
        if (owner is null)
        {
            var expected = File.Exists(paths.BootstrapFile) ? (await File.ReadAllTextAsync(paths.BootstrapFile)).Trim() : "";
            var bootstrap = form["bootstrap"].ToString().Trim().ToUpperInvariant();
            authorized = expected.Length == 64 && bootstrap.Length == 64 &&
                CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(bootstrap));
        }
        else
        {
            authorized = string.Equals(owner.UserName, form["username"].ToString(), StringComparison.OrdinalIgnoreCase)
                && await users.CheckPasswordAsync(owner, form["password"].ToString());
        }
        if (!authorized || !Regex.IsMatch(code, @"\A[A-Za-z0-9_-]{43}\z") || !await access.ActivateAsync(code, ctx.RequestAborted))
            return Results.Redirect("/license?error=1");
        return Results.Redirect(owner is null ? "/setup" : "/login");
    }
}
