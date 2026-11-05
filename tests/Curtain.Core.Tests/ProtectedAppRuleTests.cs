using Curtain.Core.Protection;
using Curtain.Core.Settings;

/// <summary>Call Run() from the existing top-level Curtain.Core.Tests Program.cs.</summary>
internal static class ProtectedAppRuleTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception($"FAIL: {name}");
            Console.WriteLine($"PASS: {name}");
            passed++;
        }

        string root = Path.Combine(Path.GetTempPath(), "CurtainRuleTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string executable = Path.Combine(root, "ExampleChat.exe");
            var identity = new ProtectedAppIdentity
            {
                ExecutablePath = executable,
                DisplayName = "Example Chat"
            }.Normalize();
            var rule = new ProtectedAppRule
            {
                Identity = identity,
                DisplayName = "Example Chat",
                CustomProtection = new WindowProtectionSettings(BlurAmount: 2.25, AllowClickThrough: true)
            };

            Check(identity.IsValid && identity.StableKey.StartsWith("path:", StringComparison.Ordinal),
                "unpackaged application has stable path identity");
            Check(ProtectedAppRuleMatcher.Matches(identity, new ProtectedAppIdentity
            {
                ExecutablePath = executable.ToUpperInvariant(), DisplayName = "Different display name"
            }), "matching ignores display name and uses normalized executable path");
            Check(!ProtectedAppRuleMatcher.Matches(identity, new ProtectedAppIdentity
            {
                ExecutablePath = Path.Combine(root, "Different.exe"), DisplayName = "Example Chat"
            }), "same display name from a different executable does not match");

            var store = new ProtectedAppRuleStore(root);
            Check(store.Load().Rules.Count == 0, "missing rule file loads as empty store");
            ProtectedAppRule saved = store.Upsert(rule);
            Check(saved.RuleId != Guid.Empty && store.Snapshot().Count == 1, "rule upsert persists one rule");
            string serializedRules = File.ReadAllText(Path.Combine(root, "protected-apps.json"));
            Check(!serializedRules.Contains("hwnd", StringComparison.OrdinalIgnoreCase) &&
                  !serializedRules.Contains("processId", StringComparison.OrdinalIgnoreCase),
                "persistent rules contain no runtime HWND or PID identity");

            var reloaded = new ProtectedAppRuleStore(root).Load();
            Check(reloaded.Rules.Count == 1, "rule survives store reconstruction");
            Check(reloaded.Rules[0].CustomProtection?.AllowClickThrough == true &&
                  Math.Abs(reloaded.Rules[0].CustomProtection!.BlurAmount - 2.25) < 0.001,
                "per-application blur and click-through preferences round-trip");

            store.Upsert(rule with { DisplayName = "Updated label" });
            Check(store.Snapshot().Count == 1, "upsert by stable identity does not create duplicate rules");
            Check(store.Snapshot()[0].RuleId == saved.RuleId, "upsert preserves the original rule ID");

            string primary = Path.Combine(root, "protected-apps.json");
            File.WriteAllText(primary, "{ this is deliberately invalid JSON");
            var recoveryStore = new ProtectedAppRuleStore(root);
            var recovered = recoveryStore.Load();
            Check(recovered.RecoveredFromBackup && recovered.Rules.Count == 1,
                "corrupt primary file recovers from last known-good backup");
            recoveryStore.Remove(saved.RuleId);
            var postRecovery = new ProtectedAppRuleStore(root).Load();
            Check(postRecovery.Rules.Count == 0, "recovered store can be safely written after backup recovery");

            Check(store.Remove(saved.RuleId) && store.Snapshot().Count == 0,
                "explicit rule removal deletes only the selected rule");

            var packagedRule = new ProtectedAppIdentity
            {
                ExecutablePath = executable,
                PackageFamilyName = "Vendor.App_12345",
                ApplicationUserModelId = "Vendor.App_12345!Main"
            };
            Check(ProtectedAppRuleMatcher.Matches(packagedRule, packagedRule with
            {
                ExecutablePath = Path.Combine(root, "Moved.exe")
            }), "packaged AUMID identity survives executable path changes");
            Check(!ProtectedAppRuleMatcher.Matches(packagedRule, packagedRule with
            {
                ApplicationUserModelId = "Vendor.Other_12345!Main"
            }), "conflicting packaged AUMID blocks a path-only false positive");
            Check(!ProtectedAppRuleMatcher.Matches(packagedRule, packagedRule with
            {
                ApplicationUserModelId = "Vendor.Other_12345!Main",
                ExecutablePath = executable
            }), "conflicting AUMID cannot match through a shared package family or executable path");
            Check(!ProtectedAppRuleMatcher.Matches(packagedRule, packagedRule with
            {
                PackageFamilyName = null,
                ApplicationUserModelId = null
            }), "missing packaged identity does not silently downgrade automatic matching to path");
            Check(ProtectedAppRuleMatcher.CanUpdateRule(packagedRule, packagedRule with
            {
                PackageFamilyName = null,
                ApplicationUserModelId = null
            }), "explicit manual re-add can update a rule when package identity is temporarily unavailable");

            string mergeDirectory = Path.Combine(root, "identity-merge");
            var mergeStore = new ProtectedAppRuleStore(mergeDirectory);
            ProtectedAppRule richRule = mergeStore.Upsert(new ProtectedAppRule { Identity = packagedRule });
            ProtectedAppRule updatedRule = mergeStore.Upsert(richRule with
            {
                Identity = packagedRule with { PackageFamilyName = null, ApplicationUserModelId = null },
                DisplayName = "Example Chat Updated"
            });
            Check(updatedRule.RuleId == richRule.RuleId &&
                  updatedRule.Identity.ApplicationUserModelId == packagedRule.ApplicationUserModelId,
                "explicit re-add does not downgrade a previously known packaged identity");

            string corruptDirectory = Path.Combine(root, "unreadable-primary-and-backup");
            Directory.CreateDirectory(corruptDirectory);
            string corruptPrimary = Path.Combine(corruptDirectory, "protected-apps.json");
            string corruptBackup = corruptPrimary + ".bak";
            File.WriteAllText(corruptPrimary, "primary-corruption-marker");
            File.WriteAllText(corruptBackup, "backup-corruption-marker");
            var corruptStore = new ProtectedAppRuleStore(corruptDirectory);
            Check(corruptStore.Load().RecoveryCode == "PrimaryAndBackupUnreadable",
                "corrupt primary and backup safely load as empty");
            corruptStore.Upsert(rule);
            Check(File.ReadAllText(corruptBackup) == "backup-corruption-marker",
                "first save after unrecoverable corruption preserves the prior backup evidence");

            string interruptedDirectory = Path.Combine(root, "interrupted-write");
            var interruptedStore = new ProtectedAppRuleStore(interruptedDirectory);
            interruptedStore.Upsert(rule);
            File.WriteAllText(Path.Combine(interruptedDirectory, "protected-apps.json.tmp"), "incomplete-write");
            Check(new ProtectedAppRuleStore(interruptedDirectory).Load().Rules.Count == 1,
                "incomplete temporary write does not replace the last valid rule file");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return passed;
    }
}
