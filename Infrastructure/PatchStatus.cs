using System;
using System.Collections.Generic;
using System.Linq;

namespace PeakSafeOptimizer.Infrastructure
{
    /// <summary>
    /// Coarse category used by callers that only need applied/skipped/failed.
    ///
    /// Deliberately still exactly three values. <c>Plugin.ApplySettingChange</c> switches on this
    /// without a <c>default</c> arm, so a fourth member would be silently unhandled there and a
    /// successful hot-reload uninstall would stop logging its one line. The finer distinction lives
    /// in <see cref="PatchResult"/>, which every report line prints, so nothing is lost by keeping
    /// this coarse: see PatchStatusEntry.Classify for the exact mapping.
    /// </summary>
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
        /// Retired. The installer used to return this for any patch registered with
        /// requireCurrentMvid on a build whose MVID was not the audited one; that gate is gone, so
        /// nothing produces this result any more. Kept so the enum's other values keep their meaning
        /// and so an old log line still reads correctly.
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

        /// <summary>
        /// Complete mapping of every <see cref="PatchResult"/> onto the coarse category. All sixteen
        /// values are named explicitly rather than folded into the <c>default</c> arm (which C#
        /// requires anyway, since it does not treat an enum switch as exhaustive), so that a new
        /// PatchResult is a visible gap in this table instead of being silently absorbed as a failure.
        ///
        /// Applied - and only Applied - means "this patch is live right now". Everything that leaves
        /// the patch not running is Skipped unless the reason is our own fault, which is Failed:
        ///
        /// * <see cref="PatchResult.Applied"/>: Harmony.Patch succeeded and the record is live.
        /// * <see cref="PatchResult.Uninstalled"/>: Skipped, not Applied. A successful uninstall is
        ///   the opposite of applied, so counting it as applied printed "applied  : X uninstalled"
        ///   and inflated AppliedCount. It is not a failure either, so Skipped is the honest bucket:
        ///   "this patch is not running now". Only the hot-reload path can produce it (Install never
        ///   uninstalls into the status it returns - group rollback uninstalls into a throwaway
        ///   PatchStatus and reports SkippedGroupIncomplete or FailedUninstall instead), so the
        ///   startup totals are unaffected by this classification. FormatReport prints it with its
        ///   own "removed" prefix so the word matches the result.
        /// * SkippedDisabled / SkippedNoDefinition / SkippedAlreadyInstalled / SkippedNotInstalled:
        ///   nothing was attempted, and nothing is wrong. (SkippedAlreadyInstalled means the live
        ///   record from a previous install still stands.)
        /// * SkippedStructureMismatch / SkippedIlMismatch: the patch's own compatibility verdict about
        ///   this game build. Warning-level, not error-level. (SkippedUncertifiedBuild is retired and
        ///   no longer produced.)
        /// * SkippedGroupIncomplete: a sibling on the same switch could not install, so this member
        ///   was withdrawn or never attempted. Not this member's fault, hence Skipped.
        /// * FailedFactory / FailedInitialize / FailedResolve / FailedHarmonyMethod / FailedPatch /
        ///   FailedUninstall: a plugin-side fault. The patch is still not live, but the log must be
        ///   an error so it is not mistaken for a compatibility skip.
        /// </summary>
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
                    // Unreachable while the switch above is exhaustive. A new PatchResult that lands
                    // here is treated as a failure on purpose: an unclassified result must be loud.
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
        ///
        /// Premise: within one PatchStatus a given name appears at most once, so "the first" is "the
        /// only one". Install guarantees it with its `handled` set and Toggle walks a single group. A
        /// group resolver that returned a duplicated id would break the premise, and then this method
        /// and PatchInstaller.FindReason (which reads the *last* match) would act on different
        /// entries, leaving one rewritten entry plus one stale contradictory one.
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
                    : "mode meaning: MVID is unknown (game update or different build). Every patch still " +
                      "validates and installs on its own assertions; the Fingerprint and StrictIl lines below " +
                      "are the ones worth re-reading against a fresh decompile.")
            };
            // `skipped` is the coarse Skipped bucket, which also holds Uninstalled entries. On the
            // startup path there are none (Install never uninstalls into the status it returns), so
            // the documented startup totals are exactly registered/enabled/applied/skipped/failed of
            // install decisions. FormatReport is only called from startup; hot reload logs per entry.
            lines.Add(Line($"totals: registered={_entries.Count}, enabled={enabled}, applied={applied}, " +
                           $"skipped={skipped}, failed={failed}, module-failures={_moduleFailures.Count}"));

            foreach (PatchStatusEntry entry in _entries.Where(x => x.Outcome == PatchOutcome.Applied))
                lines.Add(Line($"applied  : {entry.Name} [{Describe(entry.Compatibility)}] {entry.Reason}"));
            foreach (PatchStatusEntry entry in _entries.Where(x => x.Outcome == PatchOutcome.Skipped))
            {
                // Uninstalled shares the Skipped bucket (see PatchStatusEntry.Classify) but is not a
                // skip: the patch ran and was then removed. It gets its own word so the line never
                // claims the patch was never attempted. Only hot reload produces it.
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
