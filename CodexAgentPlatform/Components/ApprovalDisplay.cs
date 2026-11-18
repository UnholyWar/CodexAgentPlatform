namespace CodexAgentPlatform.Components;

public static class ApprovalDisplay
{
    // Existing approvals may still carry the original Turkish production prefix.
    private const string LegacyProductionPrefix = "ÜRETİM";
    private const string ProductionPrefix = "PRODUCTION";

    public static bool IsProduction(string scope) =>
        scope.StartsWith(ProductionPrefix, StringComparison.Ordinal) ||
        scope.StartsWith(LegacyProductionPrefix, StringComparison.Ordinal);

    public static string Scope(string scope) =>
        scope.StartsWith(LegacyProductionPrefix, StringComparison.Ordinal)
            ? ProductionPrefix + scope[LegacyProductionPrefix.Length..]
            : scope;
}
