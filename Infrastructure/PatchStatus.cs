using System;
using System.Collections.Generic;
using System.Linq;

namespace PeakSafeOptimizer.Infrastructure
{
    /// <summary>Coarse category used by callers that only need applied/skipped/failed.</summary>
    public enum PatchOutcome { Applied, Skipped, Failed }

    /// <summary>
    /// Exact per-patch result. Every skip and every failure has its own value so a user can read the
    /// startup log and tell a config decision apart from a compatibility mismatch apart from a bug.
    /// </summary>
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
        /// The patch replaces audited behavior and therefore only runs when the game build MVID is
        /// certified. Structural-only guards may still validate and run on an unknown build.
        /// </summary>
        SkippedUncertifiedBuild,
        /// <summary>
        /// This patch belongs to a group that is only correct as a whole, and at least one other
        /// member of that group could not be installed, so the whole group was rolled back. The
        /// reason names the member that failed and why.
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

        private static PatchOutcome Classify(PatchResult result)
        {
            switch (result)
            {
                case PatchResult.Applied:
                case PatchResult.Uninstalled:
                    return PatchOutcome.Applied;
                case PatchResult.SkippedDisabled:
                case PatchResult.SkippedNoDefinition:
                case PatchResult.SkippedAlreadyInstalled:
                case PatchResult.SkippedNotInstalled:
                case PatchResult.SkippedStructureMismatch:
                case PatchResult.SkippedIlMismatch:
                case PatchResult.SkippedUncertifiedBuild:
                case PatchResult.SkippedGroupIncomplete:
                    return PatchOutcome.Skipped;
                default:
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
        public IReadOnlyList<ModuleDiscoveryFailure> ModuleFailures => _moduleFailures;

        /// <summary>
        /// "certified" when the game assembly MVID equals the audited one, "structural-validation"
        /// otherwise. Structural patches validate independently in both modes; patches that replace
        /// audited behavior may require the certified build before their own validation runs.
        /// </summary>
        public string CompatibilityMode { get; internal set; } = "unknown";

        /// <summary>Build identity of the patched game assembly; diagnostic only.</summary>
        public BuildFingerprint Fingerprint { get; internal set; }

        internal void Add(PatchStatusEntry entry) => _entries.Add(entry);

        /// <summary>
        /// Replaces the first entry with this name, or appends when there is none. Used by group
        /// rollback: a patch that installed and was then withdrawn because a sibling failed must be
        /// reported once, as withdrawn, rather than twice with contradictory results.
        /// </summary>
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

        /// <summary>
        /// Multi-line, item-by-item startup report. Every line carries the "PeakSafeOptimizer"
        /// prefix so users can still grep one token to pull the whole report out of a shared log.
        /// </summary>
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
                    : "mode meaning: MVID is unknown (game update or different build). Structural guards still " +
                      "validate independently; behavior-replacement patches requiring the audited build skip safely.")
            };
            lines.Add(Line($"totals: registered={_entries.Count}, enabled={enabled}, applied={applied}, " +
                           $"skipped={skipped}, failed={failed}, module-failures={_moduleFailures.Count}"));

            foreach (PatchStatusEntry entry in _entries.Where(x => x.Outcome == PatchOutcome.Applied))
                lines.Add(Line($"applied  : {entry.Name} [{Describe(entry.Compatibility)}] {entry.Reason}"));
            foreach (PatchStatusEntry entry in _entries.Where(x => x.Outcome == PatchOutcome.Skipped))
                lines.Add(Line($"skipped  : {entry.Name} [{Describe(entry.Compatibility)}] {entry.Result}: {entry.Reason}"));
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

        /// <summary>
        /// Single-line variant, retained for compact contexts such as hot-reload traces.
        /// </summary>
        public string FormatSummary()
        {
            int enabled = _entries.Count(x => x.Enabled);
            string details = string.Join("; ", _entries.Select(x => $"{x.Name}={x.Result} ({x.Reason})"));
            string moduleDetails = string.Join("; ", _moduleFailures.Select(x => $"{x.Module}=Failed ({x.Reason})"));
            string modules = _moduleFailures.Count == 0 ? "" : $" Module failures={_moduleFailures.Count}: {moduleDetails}.";
            return $"Patch status ({CompatibilityMode}): enabled={enabled}, applied={AppliedCount}, " +
                   $"skipped={SkippedCount}, failed={FailedCount}. {details}.{modules}";
        }

        private static string Line(string body) => $"{LogPrefix} | {body}";

        private static string Describe(CompatibilityLevel? level) =>
            level.HasValue ? level.Value.ToString() : "n/a";
    }
}
