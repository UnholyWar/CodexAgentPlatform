namespace CodexAgentPlatform.Application;

public interface ILicenseAccess
{
    bool IsValid { get; }
    Task<bool> ActivateAsync(string code, CancellationToken ct);
}
