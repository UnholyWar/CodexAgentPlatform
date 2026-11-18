using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CodexAgentPlatform.Infrastructure;

public sealed class OwnerStore(IDbContextFactory<PlatformDb> dbs) :
    IUserPasswordStore<PlatformUser>, IUserSecurityStampStore<PlatformUser>
{
    public async Task<IdentityResult> CreateAsync(PlatformUser user, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
            return IdentityResult.Success;
        }
        catch (DbUpdateException)
        {
            return Failed("OwnerAlreadyExists", "Initial setup is already complete or the user could not be saved.");
        }
    }

    public async Task<IdentityResult> UpdateAsync(PlatformUser user, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        db.Attach(user);
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        db.Entry(user).State = EntityState.Modified;
        try
        {
            await db.SaveChangesAsync(ct);
            return IdentityResult.Success;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Failed("ConcurrencyFailure", "The user details have changed. Try again.");
        }
    }

    public Task<IdentityResult> DeleteAsync(PlatformUser user, CancellationToken ct) =>
        Task.FromResult(Failed("OwnerRequired", "The administrator account cannot be deleted."));

    public async Task<PlatformUser?> FindByIdAsync(string id, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<PlatformUser?> FindByNameAsync(string name, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.NormalizedUserName == name, ct);
    }

    public Task<string> GetUserIdAsync(PlatformUser user, CancellationToken ct) => Task.FromResult(user.Id);
    public Task<string?> GetUserNameAsync(PlatformUser user, CancellationToken ct) => Task.FromResult(user.UserName);
    public Task<string?> GetNormalizedUserNameAsync(PlatformUser user, CancellationToken ct) => Task.FromResult(user.NormalizedUserName);
    public Task<string?> GetPasswordHashAsync(PlatformUser user, CancellationToken ct) => Task.FromResult(user.PasswordHash);
    public Task<bool> HasPasswordAsync(PlatformUser user, CancellationToken ct) => Task.FromResult(user.PasswordHash is not null);
    public Task<string?> GetSecurityStampAsync(PlatformUser user, CancellationToken ct) => Task.FromResult(user.SecurityStamp);

    public Task SetUserNameAsync(PlatformUser user, string? value, CancellationToken ct)
    {
        user.UserName = value;
        return Task.CompletedTask;
    }

    public Task SetNormalizedUserNameAsync(PlatformUser user, string? value, CancellationToken ct)
    {
        user.NormalizedUserName = value;
        return Task.CompletedTask;
    }

    public Task SetPasswordHashAsync(PlatformUser user, string? value, CancellationToken ct)
    {
        user.PasswordHash = value;
        return Task.CompletedTask;
    }

    public Task SetSecurityStampAsync(PlatformUser user, string value, CancellationToken ct)
    {
        user.SecurityStamp = value;
        return Task.CompletedTask;
    }

    public void Dispose() { }

    private static IdentityResult Failed(string code, string description) =>
        IdentityResult.Failed(new IdentityError { Code = code, Description = description });
}
