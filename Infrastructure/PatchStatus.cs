using System;
using System.Collections.Generic;
using System.Linq;

namespace PeakSafeOptimizer.Infrastructure
{
    // Exactly three values on purpose: Plugin.ApplySettingChange switches on this without a default
    // arm, so a fourth member would be silently unhandled. The finer distinction lives in PatchResult
    public enum PatchOutcome { Applied, Skipped, Failed }

    // Every skip and every failure has its own value so a reader can tell a config decision apart
    // from a compatibility mismatch apart from a bug
    public enum PatchResult
    {
        Applied,
        Uninstalled,
        /// <summary>Turned off in the configuration file.</summary>
        SkippedDisabled,
        /// <summary>Reserved PatchId with no registered PatchDefinition in this build of the mod.</summary>
        SkippedNoDefinition,
        SkippedAlreadyInstalled,
        SkippedNotInstalled,
        /// <summary>The patch's own structural validation rejected this game build.</summary>
        SkippedStructureMismatch,
        /// <summary>The patch's own IL match / IL generation rejected this game build.</summary>
        SkippedIlMismatch,
        /// <summary>
        /// Retired: nothing produces it any more, because the MVID gate it reported on is gone. Kept
        /// so the other values keep their meaning and an old log line still reads correctly.
        /// </summary>
        SkippedUncertifiedBuild,
        /// <summary>
        /// A sibling on the same switch could not be installed, so this member was withdrawn or never
        /// attempted. The reason names the failed member and why.
        /// </summary>
        SkippedGroupIncomplete,
        FailedFactory,
        FailedInitialize,
        FailedResolve,
        FailedHarmonyMethod,
        FailedPatch,
        FailedUninstall
    }

    public sealed class PatchStatusEntry
    {
        public PatchStatusEntry(
            string name,
            bool enabled,
            PatchResult result,
            string reason,
            CompatibilityLevel? compatibility = null)
        {
            Name = name;
            Enabled = enabled;
            Result = result;
            Reason = reason;
            Compatibility = compatibility;
        }

        public string Name { get; }
        public bool Enabled { get; }
        public PatchResult Result { get; }
        public string Reason { get; }

        /// <summary>Declared compatibility level, when a PatchDefinition was available.</summary>
        public CompatibilityLevel? Compatibility { get; }

        public PatchOutcome Outcome => Classify(Result);

        // Every PatchResult is named explicitly rather than folded into the default arm, so a new
        // value is a visible gap in this table instead of being silently absorbed as a failure.
        //
        // Applied and only Applied means the patch is live. Uninstalled is Skipped rather than Applied
        // because a successful uninstall is the opposite of applied, and it is not a failure either.
        // Only hot reload produces it, since Install never uninstalls into the status it returns. The
        // two compatibility mismatches are warning-level verdicts about the game build. The Failed
        // values are plugin-side faults, which must be errors so they are not mistaken for a skip.
        // The default arm treats an unclassified result as a failure on purpose, so it is loud
        private static PatchOutcome Classify(PatchResult result)
        {
            switch (result)
            {
                case PatchResult.Applied:
                    return PatchOutcome.Applied;
                case PatchResult.Uninstalled:
                case PatchResult.SkippedDisabled:
                case PatchResult.SkippedNoDefinition:
                case PatchResult.SkippedAlreadyInstalled:
                case PatchResult.SkippedNotInstalled:
                case PatchResult.SkippedStructureMismatch:
                case PatchResult.SkippedIlMismatch:
                case PatchResult.SkippedUncertifiedBuild:
                case PatchResult.SkippedGroupIncomplete:
                    return PatchOutcome.Skipped;
                case PatchResult.FailedFactory:
                case PatchResult.FailedInitialize:
                case PatchResult.FailedResolve:
                case PatchResult.FailedHarmonyMethod:
                case PatchResult.FailedPatch:
                case PatchResult.FailedUninstall:
                    return PatchOutcome.Failed;
                default:
                    // Unreachable while the switch is exhaustive. An unclassified result must be loud
                    return PatchOutcome.Failed;
            }
        }
    }

    public sealed class PatchStatus
    {
        internal const string LogPrefix = "PeakSafeOptimizer";

        private readonly List<PatchStatusEntry> _entries = new List<PatchStatusEntry>();
        private readonly List<ModuleDiscoveryFailure> _moduleFailures = new List<ModuleDiscoveryFailure>();
        public IReadOnlyList<PatchStatusEntry> Entries => _entries;

        // Diagnostic only. Patches validate independently in both modes, and no mode gates installation
        public string CompatibilityMode { get; internal set; } = "unknown";

        /// <summary>Build identity of the patched game assembly; diagnostic only.</summary>
        public BuildFingerprint Fingerprint { get; internal set; }

        internal void Add(PatchStatusEntry entry) => _entries.Add(entry);

        // First match, or append. Group rollback needs a withdrawn member reported once rather than
        // twice with contradictory results.
        //
        // Premise: one entry per name. Install and Toggle guarantee it, and PatchInstaller.FindReason
        // reads the *last* match, so a duplicated id would make the two act on different entries
        internal void Replace(string name, PatchStatusEntry entry)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (string.Equals(_entries[i].Name, name, StringComparison.Ordinal))
                {
                    _entries[i] = entry;
                    return;
                }
            }
            _entries.Add(entry);
        }

        internal void AddModuleFailure(ModuleDiscoveryFailure failure) => _moduleFailures.Add(failure);

        public int AppliedCount => _entries.Count(x => x.Outcome == PatchOutcome.Applied);
        public int SkippedCount => _entries.Count(x => x.Outcome == PatchOutcome.Skipped);
        public int FailedCount => _entries.Count(x => x.Outcome == PatchOutcome.Failed);

        // Every line carries the prefix so one grep pulls the whole report out of a shared log
        public IReadOnlyList<string> FormatReport()
        {
            int enabled = _entries.Count(x => x.Enabled);
            int applied = AppliedCount;
            int skipped = SkippedCount;
            int failed = FailedCount;

            var lines = new List<string>
            {
                Line("report begin (per-patch compatibility gating)"),
                Line(Fingerprint != null
                    ? $"build: MVID={Fingerprint.ActualMvid} expected={BuildFingerprint.CurrentAssemblyCSharpMvid} " +
                      $"certified={Fingerprint.IsCurrent} SHA256={Fingerprint.Sha256}"
                    : "build: fingerprint unavailable"),
                Line($"compatibility mode: {CompatibilityMode}"),
                Line(string.Equals(CompatibilityMode, "certified", StringComparison.Ordinal)
                    ? "mode meaning: MVID matches the audited build; patch validation still runs and is still authoritative."
                    : "mode meaning: MVID is unknown (game update or different build). Every patch still " +
                      "validates and installs on its own assertions; the Fingerprint and StrictIl lines below " +
                      "are the ones worth re-reading against a fresh decompile.")
            };
            // `skipped` is the coarse bucket, which also holds Uninstalled entries. There are none on
            // the startup path, so the documented startup totals are exactly the install decisions
            lines.Add(Line($"totals: registered={_entries.Count}, enabled={enabled}, applied={applied}, " +
                           $"skipped={skipped}, failed={failed}, module-failures={_moduleFailures.Count}"));

            foreach (PatchStatusEntry entry in _entries.Where(x => x.Outcome == PatchOutcome.Applied))
                lines.Add(Line($"applied  : {entry.Name} [{Describe(entry.Compatibility)}] {entry.Reason}"));
            foreach (PatchStatusEntry entry in _entries.Where(x => x.Outcome == PatchOutcome.Skipped))
            {
                // Uninstalled shares the Skipped bucket but is not a skip: it ran and was then removed.
                // It gets its own word so the line never claims it was never attempted
                string prefix = entry.Result == PatchResult.Uninstalled ? "removed  " : "skipped  ";
                lines.Add(Line($"{prefix}: {entry.Name} [{Describe(entry.Compatibility)}] {entry.Result}: {entry.Reason}"));
            }
            foreach (PatchStatusEntry entry in _entries.Where(x => x.Outcome == PatchOutcome.Failed))
                lines.Add(Line($"failed   : {entry.Name} [{Describe(entry.Compatibility)}] {entry.Result}: {entry.Reason}"));
            foreach (ModuleDiscoveryFailure failure in _moduleFailures)
                lines.Add(Line($"module   : {failure.Module} Failed: {failure.Reason}"));

            lines.Add(Line("guarantee: no patch was installed without passing its own compatibility check " +
                           "(Initialize structural/IL validation + target resolution + Harmony patch). " +
                           $"{skipped} skipped, {failed} failed, {applied} live."));
            lines.Add(Line("report end"));
            return lines;
        }

        private static string Line(string body) => $"{LogPrefix} | {body}";

        private static string Describe(CompatibilityLevel? level) =>
            level.HasValue ? level.Value.ToString() : "n/a";
    }
}
