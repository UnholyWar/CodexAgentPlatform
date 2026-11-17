using CodexAgentPlatform.Infrastructure;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CodexAgentPlatform.Auth;

public sealed class OwnerAuthenticationState(ILoggerFactory loggerFactory, IServiceScopeFactory scopes,
    IOptions<IdentityOptions> options) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PlatformUser>>();
        var user = await users.GetUserAsync(state.User);
        if (user is null) return false;
        var claim = state.User.FindFirst(options.Value.ClaimsIdentity.SecurityStampClaimType)?.Value;
        return claim == await users.GetSecurityStampAsync(user);
    }
}
