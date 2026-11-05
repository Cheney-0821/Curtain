namespace Curtain.Core.Protection;

/// <summary>Pure matching rules; never falls back to display name or process name.</summary>
public static class ProtectedAppRuleMatcher
{
    public static ProtectedAppRule? FindMatch(
        IEnumerable<ProtectedAppRule> rules,
        ProtectedAppIdentity candidate)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(candidate);

        ProtectedAppIdentity normalizedCandidate = candidate.Normalize();
        return rules
            .Where(rule => rule.Enabled && rule.IsValid)
            .OrderByDescending(rule => MatchStrength(rule.Identity, normalizedCandidate))
            .FirstOrDefault(rule => Matches(rule.Identity, normalizedCandidate));
    }

    public static bool Matches(ProtectedAppIdentity ruleIdentity, ProtectedAppIdentity candidate)
    {
        ArgumentNullException.ThrowIfNull(ruleIdentity);
        ArgumentNullException.ThrowIfNull(candidate);

        var rule = ruleIdentity.Normalize();
        var target = candidate.Normalize();

        // If either side has a stronger package identity, never silently fall back to a
        // weaker path match when the other side cannot provide that identity.
        if (!string.IsNullOrWhiteSpace(rule.ApplicationUserModelId) ||
            !string.IsNullOrWhiteSpace(target.ApplicationUserModelId))
        {
            if (!string.IsNullOrWhiteSpace(rule.ApplicationUserModelId) &&
                !string.IsNullOrWhiteSpace(target.ApplicationUserModelId))
                return string.Equals(rule.ApplicationUserModelId, target.ApplicationUserModelId,
                    StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(rule.PackageFamilyName) &&
                !string.IsNullOrWhiteSpace(target.PackageFamilyName))
                return string.Equals(rule.PackageFamilyName, target.PackageFamilyName,
                    StringComparison.OrdinalIgnoreCase);

            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.PackageFamilyName) ||
            !string.IsNullOrWhiteSpace(target.PackageFamilyName))
            return !string.IsNullOrWhiteSpace(rule.PackageFamilyName) &&
                   !string.IsNullOrWhiteSpace(target.PackageFamilyName) &&
                   string.Equals(rule.PackageFamilyName, target.PackageFamilyName,
                       StringComparison.OrdinalIgnoreCase);

        return !string.IsNullOrWhiteSpace(rule.ExecutablePath) &&
               !string.IsNullOrWhiteSpace(target.ExecutablePath) &&
               string.Equals(rule.ExecutablePath, target.ExecutablePath,
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Used only when the user explicitly re-adds an app. It allows a missing package
    /// identifier to update the same exact executable-path rule, but never merges two
    /// identities that both provide conflicting AUMID or package-family values.
    /// </summary>
    public static bool CanUpdateRule(ProtectedAppIdentity existingIdentity, ProtectedAppIdentity candidate)
    {
        var existing = existingIdentity.Normalize();
        var target = candidate.Normalize();

        if (!string.IsNullOrWhiteSpace(existing.ApplicationUserModelId) &&
            !string.IsNullOrWhiteSpace(target.ApplicationUserModelId))
            return string.Equals(existing.ApplicationUserModelId, target.ApplicationUserModelId,
                StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(existing.PackageFamilyName) &&
            !string.IsNullOrWhiteSpace(target.PackageFamilyName))
            return string.Equals(existing.PackageFamilyName, target.PackageFamilyName,
                StringComparison.OrdinalIgnoreCase);

        return !string.IsNullOrWhiteSpace(existing.ExecutablePath) &&
               !string.IsNullOrWhiteSpace(target.ExecutablePath) &&
               string.Equals(existing.ExecutablePath, target.ExecutablePath,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static int MatchStrength(ProtectedAppIdentity rule, ProtectedAppIdentity candidate)
    {
        if (!string.IsNullOrWhiteSpace(rule.ApplicationUserModelId) &&
            !string.IsNullOrWhiteSpace(candidate.ApplicationUserModelId) &&
            string.Equals(rule.ApplicationUserModelId, candidate.ApplicationUserModelId,
                StringComparison.OrdinalIgnoreCase)) return 3;

        if (!string.IsNullOrWhiteSpace(rule.PackageFamilyName) &&
            !string.IsNullOrWhiteSpace(candidate.PackageFamilyName) &&
            string.Equals(rule.PackageFamilyName, candidate.PackageFamilyName,
                StringComparison.OrdinalIgnoreCase)) return 2;

        if (!string.IsNullOrWhiteSpace(rule.ExecutablePath) &&
            !string.IsNullOrWhiteSpace(candidate.ExecutablePath) &&
            string.Equals(ProtectedAppIdentity.NormalizeExecutablePath(rule.ExecutablePath),
                ProtectedAppIdentity.NormalizeExecutablePath(candidate.ExecutablePath),
                StringComparison.OrdinalIgnoreCase)) return 1;

        return 0;
    }
}
