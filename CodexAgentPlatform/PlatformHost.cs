using System.Threading.RateLimiting;
using CodexAgentPlatform.Application;
using CodexAgentPlatform.Auth;
using CodexAgentPlatform.Components;
using CodexAgentPlatform.Infrastructure;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CodexAgentPlatform;

public static class PlatformHost
{
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(options);
        configure?.Invoke(builder);
        var config = builder.Configuration;
        var paths = new PlatformPaths(
            config["Platform:Data"] ?? Path.Combine(builder.Environment.ContentRootPath, "data"),
            config["Platform:Workspaces"] ?? Path.Combine(builder.Environment.ContentRootPath, "workspaces"));
        var secureCookies = !builder.Environment.IsDevelopment() || !config.GetValue<bool>("Platform:AllowLocalHttp");
        var cookiePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;

        builder.Services.AddSingleton(paths);
        builder.Services.AddDbContextFactory<PlatformDb>(options => options.UseSqlite(paths.ConnectionString));
        builder.Services.AddSingleton<StorageStartup>();
        builder.Services.AddSingleton<MemoryStore>();
        builder.Services.AddSingleton<ChatStore>();
        builder.Services.AddSingleton<ChatUpdates>();
        builder.Services.AddSingleton<AgentTools>();
        builder.Services.AddSingleton<ContextBudget>();
        builder.Services.AddSingleton(new Vault(paths, config["Platform:VaultKeyFile"]));
        builder.Services.AddHttpClient(Operations.GitHubHttpClient, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.MaxResponseContentBufferSize = 1_048_576;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RedactLoggedHeaders(_ => true);
        builder.Services.AddSingleton<Operations>();
        builder.Services.AddSingleton<ICodexConnection>(sp => new CodexProcess(paths, config["Platform:CodexExecutable"],
            sp.GetRequiredService<ILogger<CodexProcess>>(), allowWindowsDesktopFallback: builder.Environment.IsDevelopment()));
        builder.Services.AddSingleton<CodexAccounts>();
        builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<ChatWorker>(sp,
            Math.Clamp(config.GetValue("Platform:Concurrency", 2), 1, 4)));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ChatWorker>());
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(paths.KeyDirectory))
            .SetApplicationName("CodexAgentPlatform");
        builder.Services.AddIdentityCore<PlatformUser>(options =>
            {
                options.Password.RequiredLength = 14;
                options.Password.RequiredUniqueChars = 6;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
            })
            .AddUserStore<OwnerStore>().AddSignInManager();
        builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/login";
            options.Cookie.Name = secureCookies ? "__Host-CodexPlatform" : "CodexPlatform.Local";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = cookiePolicy;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
        });
        builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromSeconds(30));
        builder.Services.AddAuthorization(options => options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser().RequireClaim(System.Security.Claims.ClaimTypes.NameIdentifier, PlatformUser.OwnerId).Build());
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.Configure<RequestLocalizationOptions>(options => options
            .SetDefaultCulture("en")
            .AddSupportedCultures("en")
            .AddSupportedUICultures("en"));
        builder.Services.AddScoped<AuthenticationStateProvider, OwnerAuthenticationState>();
        builder.Services.AddAntiforgery(options => options.Cookie.SecurePolicy = cookiePolicy);
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(2), QueueLimit = 0 }));
        });
        builder.Services.AddScoped<ConversationListState>();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents()
            .AddHubOptions(options => options.MaximumReceiveMessageSize = 128 * 1024);

        var app = builder.Build();
        app.UseRequestLocalization();
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
        }
        if (secureCookies) app.UseHttpsRedirection();
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            ctx.Response.Headers.XFrameOptions = "DENY";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseAntiforgery();

        app.MapStaticAssets().AllowAnonymous();
        app.MapOwnerEndpoints();
        app.MapGet("/chat-images/{id:guid}", async (Guid id, IDbContextFactory<PlatformDb> dbs, HttpContext ctx) =>
        {
            await using var db = await dbs.CreateDbContextAsync(ctx.RequestAborted);
            var key = id.ToString("N");
            var photo = await db.MessagePhotos.AsNoTracking().SingleOrDefaultAsync(p => p.Id == key, ctx.RequestAborted);
            ctx.Response.Headers.CacheControl = "private, no-store";
            return photo is null ? Results.NotFound() : Results.File(photo.Bytes, photo.ContentType);
        }).RequireAuthorization();
        app.MapGet("/artifacts/{id:guid}.txt", (Guid id, PlatformPaths paths) =>
        {
            var file = Path.Combine(paths.Data, "logs", $"{id:N}.txt");
            return File.Exists(file) ? Results.File(file, "text/plain; charset=utf-8", $"{id:N}.txt") : Results.NotFound();
        }).RequireAuthorization();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        return app;
    }

    public static Task InitializeAsync(WebApplication app) => app.Services.GetRequiredService<StorageStartup>()
        .InitializeAsync(app.Lifetime.ApplicationStopping);
}
