using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using PeakSafeOptimizer.Config;

namespace PeakSafeOptimizer.Infrastructure
{
    public sealed class PatchInstaller
    {
        private sealed class PatchMethodRecord
        {
            internal PatchMethodRecord(string kind, MethodInfo method) { Kind = kind; Method = method; }
            internal string Kind { get; }
            internal MethodInfo Method { get; }
            /// <summary>Set once this method is confirmed detached, so a retry never unpatches twice.</summary>
            internal bool Removed { get; set; }
        }

        private sealed class InstalledPatch
        {
            internal MethodBase Target;
            internal Action Reset;
            internal CompatibilityLevel Compatibility;
            internal readonly List<PatchMethodRecord> Methods = new List<PatchMethodRecord>();

            /// <summary>
            /// True when every Harmony method of this patch is confirmed detached but its Reset threw.
            /// The record is kept so a later shutdown can retry Reset, but the patch is not live: it
            /// must not be reported as installed, and a re-enable must reinstall it rather than see
            /// "already installed" and do nothing.
            /// </summary>
            internal bool ResetPending;

            /// <summary>
            /// True when Harmony.Patch threw but left at least one of our methods attached. The exact
            /// opposite situation to <see cref="ResetPending"/>, and deliberately a separate flag:
            /// a ResetPending record has all of its Harmony methods proven detached and may be
            /// reinstalled once its Reset finally succeeds, whereas this record still has live
            /// Harmony methods and must never be installed again - patching on top of it would
            /// attach a second copy of the same prefix/postfix. It is not live either, because the
            /// patch as a whole failed and only part of it is attached, so IsInstalled reports false
            /// and the only legal next step is an uninstall.
            /// </summary>
            internal bool AttachedAfterFailure;
        }

        private const string CertifiedMode = "certified";
        private const string StructuralMode = "structural-validation";

        private readonly Harmony _harmony;
        private readonly BuildFingerprint _fingerprint;
        private readonly ManualLogSource _log;
        private Dictionary<PatchId, Func<PatchDefinition>> _factories;
        private readonly Dictionary<PatchId, InstalledPatch> _installed =
            new Dictionary<PatchId, InstalledPatch>();

        /// <summary>
        /// Resolves every PatchId that shares one configuration switch with the given one. Set by the
        /// plugin from <see cref="OptimizerConfig.GetGroupMembers"/>. When it is null every patch is
        /// treated as its own group, which is what the pre-group behaviour was.
        /// </summary>
        private Func<PatchId, IReadOnlyList<PatchId>> _groupResolver;

        public PatchInstaller(Harmony harmony, BuildFingerprint fingerprint, ManualLogSource log)
        {
            _harmony = harmony; _fingerprint = fingerprint; _log = log;
        }

        /// <summary>
        /// Supplies the group membership used for all-or-nothing installation. Optional: without it
        /// each patch is installed independently.
        /// </summary>
        public void SetGroupResolver(Func<PatchId, IReadOnlyList<PatchId>> resolver) => _groupResolver = resolver;

        /// <summary>
        /// "certified" when the game assembly MVID equals the audited one, "structural-validation"
        /// otherwise. The mode is diagnostic: it never decides whether a patch is attempted.
        /// </summary>
        public string CompatibilityMode =>
            (_fingerprint?.IsCurrent ?? false) ? CertifiedMode : StructuralMode;

        public PatchStatus Install(PatchCatalogSnapshot catalog)
        {
            var factories = new Dictionary<PatchId, Func<PatchDefinition>>();
            foreach (PatchRegistration registration in catalog.Registrations)
            {
                if (registration.Factory != null)
                    factories[registration.Id] = registration.Factory;
            }
            _factories = factories;

            PatchStatus status = NewStatus();
            foreach (ModuleDiscoveryFailure failure in catalog.Failures)
                status.AddModuleFailure(failure);

            // Group members share one switch, so they are all enabled or all disabled together and
            // only the first one encountered drives the install; the rest are handled with it.
            var handled = new HashSet<PatchId>();
            foreach (PatchRegistration registration in catalog.Registrations)
            {
                if (handled.Contains(registration.Id))
                    continue;
                if (!registration.Enabled)
                {
                    handled.Add(registration.Id);
                    status.Add(new PatchStatusEntry(
                        registration.Id.ToString(), false, PatchResult.SkippedDisabled,
                        "disabled by configuration"));
                    continue;
                }
                IReadOnlyList<PatchId> group = ResolveGroup(registration.Id);
                for (int i = 0; i < group.Count; i++)
                    handled.Add(group[i]);
                InstallGroup(group, status);
            }
            return status;
        }

        /// <summary>
        /// True only when this patch is live, i.e. a record exists and it is neither of the two
        /// not-live shapes. A record whose Harmony methods are all detached but whose Reset threw
        /// (ResetPending) is retained for a later retry, and a record left behind by a Patch call
        /// that threw after attaching part of the patch (AttachedAfterFailure) is retained for
        /// verified cleanup; both report false here, because callers such as the collision-reuse lane
        /// gate engine-wide state on a patch actually being active and correct.
        /// </summary>
        public bool IsInstalled(PatchId id) =>
            _installed.TryGetValue(id, out InstalledPatch installed) &&
            !installed.ResetPending && !installed.AttachedAfterFailure;

        /// <summary>
        /// True while any record remains, including one that is only kept to retry a failed Reset or
        /// to clean up a partially attached failed Patch. Shutdown uses this to decide whether static
        /// state was proven clean.
        /// </summary>
        public bool HasInstalledPatches => _installed.Count != 0;

        /// <summary>
        /// True when a record exists at all, whatever shape it is in. Group rollback uses this rather
        /// than <see cref="IsInstalled"/> to decide whether a member was really withdrawn: a record
        /// that survives an uninstall attempt means either live Harmony methods or unproven static
        /// state, and neither is a clean withdrawal.
        /// </summary>
        private bool IsRecorded(PatchId id) => _installed.ContainsKey(id);

        /// <summary>
        /// Hot-reload entry point. Enabled=true installs (idempotent; reuses the same isolated
        /// factory→Initialize→ResolveTarget→HarmonyMethod→Patch path, so a patch that cannot validate
        /// itself against the running build still fails closed here).
        /// Enabled=false uninstalls by the recorded patch methods, then runs the optional Reset.
        /// Every branch is fail-closed and isolated to this one PatchId - or, when the patch shares a
        /// switch with others, to that whole group, which is installed and removed as a unit.
        /// </summary>
        public PatchStatus Toggle(PatchId id, bool enabled)
        {
            PatchStatus status = NewStatus();
            IReadOnlyList<PatchId> group = ResolveGroup(id);
            if (enabled)
            {
                InstallGroup(group, status);
            }
            else
            {
                for (int i = 0; i < group.Count; i++)
                    UninstallOne(group[i], status);
            }
            return status;
        }

        /// <summary>
        /// Group membership for one id, degrading to the single id whenever the resolver is absent,
        /// empty, inconsistent or throwing.
        ///
        /// The degradation is not equally harmless on both paths. In <see cref="Install"/> it is: the
        /// outer loop keeps iterating over the remaining registrations, so every sibling still gets
        /// installed on its own turn and the group ends up complete, just without the all-or-nothing
        /// coupling. In <see cref="Toggle"/> it is not: the toggle only ever touches the ids returned
        /// here, so a degraded resolution installs or removes the representative alone and leaves the
        /// siblings in whatever state they were in - exactly the half-installed group the grouping
        /// exists to prevent, and with no report entry for the untouched members.
        ///
        /// This is left as-is rather than failing the whole switch closed, because reaching it
        /// requires <see cref="OptimizerConfig.GetGroupMembers"/> to throw, and that method is two
        /// dictionary lookups over dictionaries built once in the constructor. Adding a
        /// degradation flag plus a Toggle-side abort would add a new failure path to a path that
        /// cannot currently be reached, which is a worse trade than documenting it.
        /// </summary>
        private IReadOnlyList<PatchId> ResolveGroup(PatchId id)
        {
            Func<PatchId, IReadOnlyList<PatchId>> resolver = _groupResolver;
            if (resolver == null)
                return new[] { id };
            try
            {
                IReadOnlyList<PatchId> group = resolver(id);
                if (group == null || group.Count == 0)
                    return new[] { id };
                // A resolver that does not report the queried patch as a member would let this
                // install something the caller did not ask for; fall back to the single patch.
                for (int i = 0; i < group.Count; i++)
                {
                    if (group[i] == id)
                        return group;
                }
                return new[] { id };
            }
            catch (Exception ex)
            {
                _log?.LogError(
                    $"Patch group resolution failed safely for {id}; treating it as a single patch: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return new[] { id };
            }
        }

        /// <summary>
        /// Installs every member of a group, and withdraws the ones that succeeded if any member did
        /// not. A group exists precisely because its members are only correct together (see
        /// <see cref="OptimizerConfig.GetGroupMembers"/>), so a half-installed group is a state
        /// nothing was reasoned about: for RemoteRagdollLod it would mean a remote character has its
        /// animation graph skipped while still being driven towards animation targets, or the reverse.
        /// A single-member group behaves exactly as the previous per-patch path did.
        /// </summary>
        private void InstallGroup(IReadOnlyList<PatchId> group, PatchStatus status)
        {
            if (group.Count == 1)
            {
                InstallOne(group[0], status);
                return;
            }

            var installedNow = new List<PatchId>(group.Count);
            for (int i = 0; i < group.Count; i++)
            {
                PatchId id = group[i];
                bool wasInstalled = IsInstalled(id);
                InstallOne(id, status);
                if (!wasInstalled && IsInstalled(id))
                    installedNow.Add(id);
                if (IsInstalled(id))
                    continue;

                // This member is not live. Withdraw the ones this call brought up, so the group is
                // either fully present or fully absent.
                string reason = FindReason(status, id.ToString()) ?? "member did not install";
                RollBackGroup(group, installedNow, id, reason, status);
                return;
            }
        }

        private void RollBackGroup(
            IReadOnlyList<PatchId> group,
            List<PatchId> installedNow,
            PatchId failedMember,
            string failedReason,
            PatchStatus status)
        {
            _log?.LogWarning(
                $"{failedMember} could not be installed, so its whole switch is being rolled back " +
                $"({installedNow.Count} sibling(s) to withdraw): {failedReason}");

            for (int i = 0; i < installedNow.Count; i++)
            {
                PatchId member = installedNow[i];
                PatchStatus removal = NewStatus();
                UninstallOne(member, removal);
                // IsRecorded, not IsInstalled: UninstallOne keeps the record in both of its failure
                // shapes, and one of them (all methods detached, Reset threw) sets ResetPending, which
                // IsInstalled excludes. Judging withdrawal by IsInstalled therefore reported a clean
                // rollback while a record with unproven static state was still sitting in _installed
                // and the FailedUninstall entry written into `removal` was dropped on the floor.
                if (IsRecorded(member))
                {
                    // UninstallOne already logged and recorded why, in `removal`. Carry that reason
                    // over into the caller's status so it is not lost, and say which of the two shapes
                    // this is, because they need different things from the user: live Harmony methods
                    // mean the patch is still running, whereas a failed Reset means it is detached but
                    // its static state was never proven clean.
                    string removalReason = FindReason(removal, member.ToString()) ?? "uninstall incomplete";
                    bool detachedButUnclean =
                        _installed.TryGetValue(member, out InstalledPatch leftover) && leftover.ResetPending;
                    string shape = detachedButUnclean
                        ? "Harmony methods are detached but its Reset threw, so its static state is not proven clean"
                        : "at least one Harmony method is still attached, so this member is still running";
                    status.Replace(member.ToString(), new PatchStatusEntry(
                        member.ToString(), true, PatchResult.FailedUninstall,
                        $"group rollback for {failedMember} could not withdraw this member: {shape} " +
                        $"({removalReason})"));
                    continue;
                }
                status.Replace(member.ToString(), new PatchStatusEntry(
                    member.ToString(), true, PatchResult.SkippedGroupIncomplete,
                    $"withdrawn because {failedMember} could not be installed: {failedReason}"));
            }

            // Every other member is accounted for too, so the log does not go quiet after the
            // failure. Two cases: a member that was never reached, and a member that was already live
            // before this call - the latter is not in installedNow and is deliberately left alone,
            // because uninstalling something a previous install brought up would introduce a failure
            // mode this rollback cannot recover from. It is reported instead, so a half-installed
            // switch is visible rather than silent.
            for (int i = 0; i < group.Count; i++)
            {
                PatchId member = group[i];
                if (member == failedMember)
                    continue;
                if (installedNow.Contains(member))
                    continue; // Already reported by the withdrawal loop above, in either outcome.
                if (IsInstalled(member))
                {
                    status.Replace(member.ToString(), new PatchStatusEntry(
                        member.ToString(), true, PatchResult.FailedUninstall,
                        $"still installed from an earlier install while {failedMember} could not be " +
                        $"installed ({failedReason}); this switch is now half installed and was left " +
                        "that way on purpose - toggle it off to remove this member"));
                    continue;
                }
                if (FindReason(status, member.ToString()) != null)
                    continue;
                status.Add(new PatchStatusEntry(
                    member.ToString(), true, PatchResult.SkippedGroupIncomplete,
                    $"not attempted because {failedMember} could not be installed: {failedReason}"));
            }
        }

        /// <summary>
        /// Reason of the *last* entry with this name. Mirror image of
        /// <see cref="PatchStatus.Replace"/>, which rewrites the *first*. Both rely on the same
        /// premise: within one PatchStatus a given patch name appears at most once. That holds because
        /// Install handles every id exactly once (the `handled` set) and Toggle walks one group. If a
        /// group resolver ever returned a duplicated id the two would disagree - Replace would rewrite
        /// the first occurrence while FindReason read the second - and the report would carry two
        /// contradictory entries for one patch.
        /// </summary>
        private static string FindReason(PatchStatus status, string name)
        {
            for (int i = status.Entries.Count - 1; i >= 0; i--)
            {
                if (string.Equals(status.Entries[i].Name, name, StringComparison.Ordinal))
                    return status.Entries[i].Reason;
            }
            return null;
        }

        /// <summary>
        /// Removes every patch known to this installer through the same per-method verification used
        /// by hot reload. Failed removals intentionally remain recorded so their accessors cannot be
        /// reset underneath a still-live Harmony method.
        /// </summary>
        public PatchStatus UninstallAll()
        {
            PatchStatus status = NewStatus();
            var ids = new List<PatchId>(_installed.Keys);
            for (int i = 0; i < ids.Count; i++)
                UninstallOne(ids[i], status);
            return status;
        }

        private PatchStatus NewStatus() =>
            new PatchStatus { CompatibilityMode = CompatibilityMode, Fingerprint = _fingerprint };

        private void InstallOne(PatchId id, PatchStatus status)
        {
            string name = id.ToString();
            if (_installed.TryGetValue(id, out InstalledPatch existing))
            {
                if (existing.AttachedAfterFailure)
                {
                    // A previous Patch call threw after Harmony had already attached part of this
                    // patch. Those methods are still live, so reinstalling here would attach a second
                    // copy of the same prefix/postfix, and the only safe move is to do no Harmony work
                    // at all and report the record. Fail, not Reject: this is not a compatibility
                    // verdict about the game build, it is our own leftover state, and it needs to be
                    // an error in the log so it is not mistaken for a clean skip. FailedPatch rather
                    // than FailedUninstall because the fault originates in the failed Patch call, and
                    // reusing that value keeps the two lines of a single incident under one result.
                    Fail(status, name, existing.Compatibility, PatchResult.FailedPatch, "Patch retry",
                        new InvalidOperationException(
                            "a previous Patch attempt left Harmony methods attached; turn this switch " +
                            "off and on again to detach them before installing"));
                    return;
                }
                if (!existing.ResetPending)
                {
                    status.Add(new PatchStatusEntry(
                        name, true, PatchResult.SkippedAlreadyInstalled, "already installed", existing.Compatibility));
                    return;
                }

                // Detached but not proven clean. Retry the Reset before reinstalling; if it throws
                // again the record stays and the patch stays out, rather than being installed on top
                // of state the previous instance may still own.
                try
                {
                    existing.Reset?.Invoke();
                }
                catch (Exception ex)
                {
                    Fail(status, name, existing.Compatibility, PatchResult.FailedInitialize, "Reset retry", ex);
                    return;
                }
                _installed.Remove(id);
                _log?.LogInfo($"{name} pending reset succeeded on retry; reinstalling.");
            }
            if (_factories == null || !_factories.TryGetValue(id, out Func<PatchDefinition> factory) || factory == null)
            {
                status.Add(new PatchStatusEntry(
                    name, true, PatchResult.SkippedNoDefinition, "no definition registered"));
                return;
            }

            PatchDefinition definition;
            try { definition = factory(); }
            catch (Exception ex) { Fail(status, name, null, PatchResult.FailedFactory, "factory", ex); return; }
            if (definition == null)
            {
                Fail(status, name, null, PatchResult.FailedFactory, "factory",
                    new InvalidOperationException("returned null"));
                return;
            }
            if (definition.Id != id)
            {
                Fail(status, name, definition.Compatibility, PatchResult.FailedFactory, "factory",
                    new InvalidOperationException($"returned definition for {definition.Id}"));
                return;
            }

            CompatibilityLevel level = definition.Compatibility;

            if (definition.RequiresCertifiedBuild && !(_fingerprint?.IsCurrent ?? false))
            {
                status.Add(new PatchStatusEntry(
                    name,
                    true,
                    PatchResult.SkippedUncertifiedBuild,
                    "requires the audited Assembly-CSharp MVID until a method-level behavioral fingerprint exists",
                    level));
                return;
            }

            try { definition.Initialize(); }
            catch (Exception ex)
            {
                ResetUninstalledDefinition(name, definition);
                PatchResult initializeResult = ClassifyValidationFailure(ex, PatchResult.FailedInitialize);
                if (initializeResult == PatchResult.FailedInitialize)
                    Fail(status, name, level, initializeResult, "Initialize", ex);
                else
                    Reject(status, name, level, initializeResult, "Initialize", ex);
                return;
            }

            MethodBase target;
            try { target = definition.ResolveTarget() ?? throw new MissingMethodException("resolver returned null"); }
            catch (Exception ex)
            {
                ResetUninstalledDefinition(name, definition);
                PatchResult resolveResult = ClassifyValidationFailure(ex, PatchResult.FailedResolve);
                if (resolveResult == PatchResult.FailedResolve)
                    Fail(status, name, level, resolveResult, "Resolve", ex);
                else
                    Reject(status, name, level, resolveResult, "Resolve", ex);
                return;
            }

            HarmonyMethod prefix, postfix, transpiler, finalizer;
            try
            {
                prefix = definition.Prefix?.Invoke(); postfix = definition.Postfix?.Invoke();
                transpiler = definition.Transpiler?.Invoke(); finalizer = definition.Finalizer?.Invoke();
                if (prefix == null && postfix == null && transpiler == null && finalizer == null)
                    throw new InvalidOperationException("definition supplied no Harmony method");
            }
            catch (Exception ex)
            {
                ResetUninstalledDefinition(name, definition);
                Fail(status, name, level, PatchResult.FailedHarmonyMethod, "HarmonyMethod", ex);
                return;
            }

            try { _harmony.Patch(target, prefix, postfix, transpiler, finalizer, null); }
            catch (Exception ex)
            {
                // A Transpiler that cannot produce valid IL for this build is a compatibility
                // mismatch, not a plugin bug; both outcomes leave the patch uninstalled either way.
                PatchResult result = transpiler != null && LooksLikeIlFailure(ex)
                    ? PatchResult.SkippedIlMismatch
                    : PatchResult.FailedPatch;
                if (result == PatchResult.SkippedIlMismatch)
                    Reject(status, name, level, result, "Patch", ex);
                else
                    Fail(status, name, level, result, "Patch", ex);

                InstalledPatch partial = CreateInstalled(target, definition, prefix, postfix, transpiler, finalizer);
                if (HasAttachedMethod(partial))
                {
                    // HarmonyX registers the patch methods in the target's PatchInfo before it
                    // generates the wrapper, so a throw out of UpdateWrapper leaves our methods
                    // listed and this branch is the normal one, not a rarity. Mark the record as a
                    // failed-but-attached leftover: it is retained only so a later uninstall can
                    // detach those methods with per-method verification, and it must not count as
                    // installed anywhere.
                    partial.AttachedAfterFailure = true;
                    _installed[id] = partial;
                    _log?.LogWarning(
                        $"{name} Patch threw after attaching at least one Harmony method; the patch is " +
                        "NOT active and is recorded only so an uninstall can detach what is attached. " +
                        "Turn this switch off and on again to clean it up.");
                }
                else
                {
                    ResetUninstalledDefinition(name, definition);
                }
                return;
            }

            // Record only after a successful Patch, so a failed hot reload never leaves a stale
            // record that a later uninstall would try to undo.
            InstalledPatch installed = CreateInstalled(target, definition, prefix, postfix, transpiler, finalizer);
            _installed[id] = installed;
            status.Add(new PatchStatusEntry(name, true, PatchResult.Applied, FormatMethod(target), level));
        }

        private static void AddMethod(InstalledPatch installed, string kind, HarmonyMethod harmonyMethod)
        {
            MethodInfo method = harmonyMethod?.method;
            if (method != null)
                installed.Methods.Add(new PatchMethodRecord(kind, method));
        }

        private static InstalledPatch CreateInstalled(
            MethodBase target,
            PatchDefinition definition,
            HarmonyMethod prefix,
            HarmonyMethod postfix,
            HarmonyMethod transpiler,
            HarmonyMethod finalizer)
        {
            var installed = new InstalledPatch
            {
                Target = target,
                Reset = definition.Reset,
                Compatibility = definition.Compatibility
            };
            AddMethod(installed, "prefix", prefix);
            AddMethod(installed, "postfix", postfix);
            AddMethod(installed, "transpiler", transpiler);
            AddMethod(installed, "finalizer", finalizer);
            return installed;
        }

        private bool HasAttachedMethod(InstalledPatch installed)
        {
            for (int i = 0; i < installed.Methods.Count; i++)
            {
                if (IsStillAttached(installed.Target, installed.Methods[i].Method))
                    return true;
            }
            return false;
        }

        private void ResetUninstalledDefinition(string name, PatchDefinition definition)
        {
            if (definition.Reset == null)
                return;
            try
            {
                definition.Reset();
            }
            catch (Exception ex)
            {
                _log?.LogError(
                    $"{name} reset failed safely after an uninstalled initialization path: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private void UninstallOne(PatchId id, PatchStatus status)
        {
            string name = id.ToString();
            // Keyed on the record, deliberately not on IsInstalled: the two not-live record shapes
            // (ResetPending and AttachedAfterFailure) are exactly the ones that most need to be
            // cleaned up here, and gating this on IsInstalled would strand them in _installed forever.
            if (!_installed.TryGetValue(id, out InstalledPatch installed))
            {
                status.Add(new PatchStatusEntry(name, false, PatchResult.SkippedNotInstalled, "not installed"));
                return;
            }

            // Unpatch first, keep the record until every method is confirmed detached. Removing the
            // record up front (the previous behaviour) could leave a live Harmony patch with no
            // record of it, which then also lost its Reset and its idempotency on a later toggle.
            var errors = new List<string>();
            foreach (PatchMethodRecord record in installed.Methods)
            {
                if (record.Removed)
                    continue;
                try
                {
                    // Per-method Unpatch keeps other patches (and other mods) intact; never UnpatchSelf.
                    _harmony.Unpatch(installed.Target, record.Method);
                    record.Removed = true;
                }
                catch (Exception ex)
                {
                    // Re-query Harmony: if the method is in fact already detached, the record is what
                    // is wrong, not the runtime state, and a retry must not unpatch it again.
                    if (!IsStillAttached(installed.Target, record.Method))
                    {
                        record.Removed = true;
                        _log?.LogWarning(
                            $"{name} {record.Kind} Unpatch threw but the method is already detached: " +
                            $"{ex.GetType().Name}: {ex.Message}");
                        continue;
                    }
                    errors.Add($"{record.Kind}: {ex.GetType().Name}: {ex.Message}");
                    _log?.LogError($"{name} {record.Kind} uninstall failed safely: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (errors.Count > 0)
            {
                // Record intentionally retained, and Reset intentionally not called: the remaining
                // Harmony methods are still live and must keep their accessors.
                status.Add(new PatchStatusEntry(name, false, PatchResult.FailedUninstall,
                    $"uninstall incomplete, patch left installed ({string.Join("; ", errors)})",
                    installed.Compatibility));
                return;
            }

            // Every method is confirmed detached from here on, so a failed-but-attached record has
            // nothing attached any more and the flag that describes it no longer holds. Clearing it
            // matters: if Reset then throws, the record survives as a plain ResetPending one, which
            // InstallOne is allowed to retry. Leaving both flags set would make the record refuse
            // reinstallation forever even though no Harmony method is attached to double-patch.
            installed.AttachedAfterFailure = false;

            // Order matters: all Unpatch calls succeeded → Reset → drop the record.
            if (installed.Reset != null)
            {
                try { installed.Reset(); }
                catch (Exception ex)
                {
                    _log?.LogError($"{name} reset failed safely after uninstall: {ex.GetType().Name}: {ex.Message}");
                    // All Harmony methods are detached, but static state was not proven clean. Keep
                    // the record so a later shutdown/retry can run Reset again instead of reporting
                    // a successful uninstall with stale accessors. ResetPending marks it as not live,
                    // so IsInstalled reports false and a re-enable retries Reset and reinstalls
                    // instead of seeing "already installed" and silently doing nothing.
                    installed.ResetPending = true;
                    status.Add(new PatchStatusEntry(name, false, PatchResult.FailedUninstall,
                        $"Harmony methods detached but reset failed ({ex.GetType().Name}: {ex.Message})",
                        installed.Compatibility));
                    return;
                }
            }
            _installed.Remove(id);
            status.Add(new PatchStatusEntry(name, false, PatchResult.Uninstalled,
                "uninstalled", installed.Compatibility));
        }

        private bool IsStillAttached(MethodBase target, MethodInfo patchMethod)
        {
            try
            {
                HarmonyLib.Patches info = Harmony.GetPatchInfo(target);
                if (info == null)
                    return false;
                return Contains(info.Prefixes, patchMethod) || Contains(info.Postfixes, patchMethod) ||
                       Contains(info.Transpilers, patchMethod) || Contains(info.Finalizers, patchMethod);
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"Harmony patch-info query failed; assuming still attached: {ex.GetType().Name}: {ex.Message}");
                return true;
            }
        }

        private static bool Contains(IEnumerable<Patch> patches, MethodInfo method)
        {
            if (patches == null)
                return false;
            foreach (Patch patch in patches)
            {
                if (patch != null && patch.PatchMethod == method)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Maps a validation exception thrown out of <c>Initialize</c> or <c>ResolveTarget</c> onto a
        /// compatibility verdict. Missing or reshaped members are a structural mismatch; IL-pattern
        /// and IL-generation complaints are an IL mismatch; anything else stays a genuine failure.
        /// Every branch leaves the patch uninstalled - the classification only decides the log level
        /// (Reject → warning, Fail → error) and which bucket the report totals count it in.
        ///
        /// The structural arm is type-based for MissingMemberException/TypeLoadException, and for
        /// InvalidOperationException it is a default-yes with one carved-out exception, because
        /// InvalidOperationException carries two different meanings in this codebase:
        ///
        /// * "this game member no longer has the audited shape" - the three validation helpers
        ///   (Patches\Scale\ScaleValidation, Patches\Stability\StabilityPatchValidation,
        ///   Patches\Equivalent\MathAndScan\MathAndScanValidation) all word this as "Unexpected …",
        ///   but about a hundred further throw sites across the patch files hand-roll their own
        ///   reflection self-check and word it freely ("ItemState.Ground is no longer defined",
        ///   "PlayerGroundSample is no longer a struct", "Campfire.s_CampfireBuff failed structural
        ///   self-check"). Keying the structural verdict on the helpers' "Unexpected " prefix would
        ///   therefore demote all of those to plugin failures, which is exactly backwards: they are
        ///   the normal outcome of a game update and must stay a warning-level compatibility skip.
        /// * "the member shape was fine but our own accessor emission returned null" - a real plugin
        ///   fault, reported until now as SkippedStructureMismatch, i.e. telling the user their game
        ///   build is unsupported when the bug is ours.
        ///
        /// The second family is identifiable without touching any patch file, because it has one
        /// uniform vocabulary: every one of its 38 sites throws a message ending in "creation failed"
        /// or "binding failed" ("&lt;X&gt; accessor creation failed", "&lt;X&gt; FieldRef creation
        /// failed", "&lt;X&gt; getter creation failed", "&lt;X&gt; delegate creation failed",
        /// "&lt;X&gt; delegate binding failed"), and every one of them sits behind a
        /// <c>if (accessor == null …)</c> guard placed *after* that patch's structural checks have
        /// already passed - verified site by site, examples
        /// Patches\ScaleExperimental\RemoteRagdollLod.cs:195,
        /// Patches\Stability\RunBasedValuesPatch.cs:45,
        /// Patches\Scale\CampfireProtectionMergePatch.cs:60. So those fall through to the caller's
        /// fallback (FailedInitialize / FailedResolve) and are logged as errors.
        ///
        /// This is still message sniffing and still fragile, with a known failure direction: a future
        /// emission guard worded differently is misreported as a compatibility skip, exactly as all of
        /// them were before. It fails that way rather than the reverse on purpose - the cost of a
        /// wrongly-skipped plugin bug is a misleading log line, whereas the cost of wrongly failing a
        /// structural mismatch is an error storm on every game update. The robust fix is a dedicated
        /// exception type thrown by the patches themselves, which is a change to every patch file.
        /// </summary>
        private static PatchResult ClassifyValidationFailure(Exception ex, PatchResult fallback)
        {
            if (LooksLikeIlFailure(ex))
                return PatchResult.SkippedIlMismatch;
            if (ex is MissingMemberException || ex is TypeLoadException)
                return PatchResult.SkippedStructureMismatch;
            if (ex is InvalidOperationException && !LooksLikeAccessorEmissionFailure(ex))
                return PatchResult.SkippedStructureMismatch;
            return fallback;
        }

        /// <summary>
        /// True when this exception, or any exception it wraps, is one of the patches' own
        /// accessor/delegate emission guards rather than a verdict about the game build. Matched on
        /// the message tail because that suffix is the convention every one of those guards follows;
        /// see <see cref="ClassifyValidationFailure"/> for the enumeration and the known fragility.
        /// </summary>
        private static bool LooksLikeAccessorEmissionFailure(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                string message = current.Message ?? string.Empty;
                if (message.EndsWith("creation failed", StringComparison.Ordinal) ||
                    message.EndsWith("binding failed", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Heuristic "this is an IL problem, not a plugin bug" test, used for the Transpiler and
        /// Finalizer pipeline.
        ///
        /// Currently unreachable in both of its roles, and kept anyway. This plugin ships no
        /// Transpiler: all 44 PatchDefinition constructions pass only <c>prefix:</c>/<c>postfix:</c>,
        /// and there is no <c>transpiler:</c> or <c>finalizer:</c> argument anywhere in the
        /// repository - so <c>transpiler != null</c> at the Patch call site is always false, and the
        /// HarmonyException arm below can only be reached through <see cref="ClassifyValidationFailure"/>,
        /// where no current patch throws a HarmonyException out of Initialize or ResolveTarget. It
        /// stays because the Transpiler pipeline is a fail-closed safety net: the day a Transpiler is
        /// added, an IL mismatch must degrade to a compatibility skip rather than be reported as a
        /// plugin bug, and re-deriving this at that point is how the net gets forgotten.
        ///
        /// The message sniffing is deliberately kept as-is rather than tightened. It is only consulted
        /// for exceptions coming out of Harmony's own IL generation (or out of an IL matcher a future
        /// Transpiler supplies), whose wording this plugin does not control, so a substring test is
        /// the only thing available. Verified today: no exception message thrown anywhere in this
        /// repository contains "IL ", "instruction", "pattern", "audited" or "found 0", so it cannot
        /// currently misclassify one of our own messages - but note that it is consulted *first* by
        /// ClassifyValidationFailure, so any patch message that later picks up one of those words
        /// would be reported as an IL mismatch on a plugin that has no Transpiler at all.
        /// </summary>
        private static bool LooksLikeIlFailure(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                if (string.Equals(current.GetType().Name, "HarmonyException", StringComparison.Ordinal))
                    return true;
                string message = current.Message ?? string.Empty;
                if (message.IndexOf("IL ", StringComparison.Ordinal) >= 0 ||
                    message.IndexOf("instruction", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("pattern", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("audited", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("found 0", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>Compatibility verdict: logged as a warning, because nothing is broken.</summary>
        private void Reject(
            PatchStatus status, string name, CompatibilityLevel? level, PatchResult result, string stage, Exception ex)
        {
            string reason = $"{stage}: {ex.GetType().Name}: {ex.Message}";
            status.Add(new PatchStatusEntry(name, true, result, reason, level));
            _log?.LogWarning($"{name} skipped safely ({result}) at {reason}");
        }

        /// <summary>Genuine failure: logged as an error. The patch is still not installed.</summary>
        private void Fail(
            PatchStatus status, string name, CompatibilityLevel? level, PatchResult result, string stage, Exception ex)
        {
            string reason = $"{stage}: {ex.GetType().Name}: {ex.Message}";
            status.Add(new PatchStatusEntry(name, true, result, reason, level));
            _log?.LogError($"{name} patch failed safely at {reason}");
        }

        private static string FormatMethod(MethodBase method) => $"{method.DeclaringType?.FullName}.{method.Name}";
    }
}
