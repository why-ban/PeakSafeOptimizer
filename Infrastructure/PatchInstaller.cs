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
            // Set once this method is confirmed detached, so a retry never unpatches twice
            internal bool Removed { get; set; }
        }

        private sealed class InstalledPatch
        {
            internal MethodBase Target;
            internal Action Reset;
            internal CompatibilityLevel Compatibility;
            internal readonly List<PatchMethodRecord> Methods = new List<PatchMethodRecord>();

            // Every Harmony method is confirmed detached but Reset threw. The record is kept so a
            // later shutdown can retry Reset. It is not live, and a re-enable must retry Reset and
            // reinstall rather than report "already installed"
            internal bool ResetPending;

            // Harmony.Patch threw but left at least one of our methods attached. Separate from
            // ResetPending because the next legal step is the opposite: patching on top of this
            // record would attach a second copy of the same prefix/postfix, so it must never be
            // installed again, only uninstalled
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

        // Resolves every PatchId that shares one configuration switch with the given one. Null means
        // every patch is its own group
        private Func<PatchId, IReadOnlyList<PatchId>> _groupResolver;

        public PatchInstaller(Harmony harmony, BuildFingerprint fingerprint, ManualLogSource log)
        {
            _harmony = harmony; _fingerprint = fingerprint; _log = log;
        }

        public void SetGroupResolver(Func<PatchId, IReadOnlyList<PatchId>> resolver) => _groupResolver = resolver;

        // Diagnostic only: the mode is reported, it never decides whether a patch is attempted
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

            // Group members share one switch and are only correct together, so the first one
            // encountered drives the install for all of them
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

        // True only when the patch is live. Both retained-but-not-live shapes report false, because
        // callers gate engine-wide state on a patch actually being active
        public bool IsInstalled(PatchId id) =>
            _installed.TryGetValue(id, out InstalledPatch installed) &&
            !installed.ResetPending && !installed.AttachedAfterFailure;

        // True while any record remains, including one kept only to retry a failed Reset or to clean
        // up a partially attached failed Patch. Shutdown uses this to decide whether static state was
        // proven clean
        public bool HasInstalledPatches => _installed.Count != 0;

        // True when a record exists at all. Group rollback uses this rather than IsInstalled, because
        // a record surviving an uninstall attempt means either live Harmony methods or unproven
        // static state, and neither is a clean withdrawal
        private bool IsRecorded(PatchId id) => _installed.ContainsKey(id);

        // Enabled installs through the same fail-closed path as startup and is idempotent
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

        // Degrades to the single id when the resolver is absent, empty, inconsistent or throwing.
        // That degradation is harmless in Install, which keeps iterating and installs every sibling
        // on its own turn, but not in Toggle, which only touches the ids returned here and would
        // leave the siblings half installed with no report entry for them. Left as-is because
        // reaching it requires GetGroupMembers to throw, and it is two dictionary lookups over
        // dictionaries built once in the config constructor
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
                // A resolver that omits the queried patch would install something the caller did not
                // ask for, so fall back to the single patch
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

        // Installs every member of a group and withdraws the ones that succeeded if any member did
        // not. A group exists because its members are only correct together, so a half-installed
        // group is a state nothing was reasoned about: for RemoteRagdollLod it would mean a remote
        // character has its animation graph skipped while still being driven towards animation
        // targets, or the reverse
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

                // Withdraw what this call brought up, so the group is either fully present or fully
                // absent
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
                // IsRecorded, not IsInstalled: one of the two failure shapes is ResetPending, which
                // IsInstalled excludes, so judging withdrawal by it reports a clean rollback while a
                // record with unproven static state is still in _installed
                if (IsRecorded(member))
                {
                    // Carry UninstallOne's reason over and name the shape, because the two need
                    // different things from the user: live Harmony methods mean the patch is still
                    // running, a failed Reset means it is detached but its state is not proven clean
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

            // Account for the members this call did not touch, so a half-installed switch is visible
            // rather than silent. A member that was already live before this call is deliberately
            // left alone: uninstalling what a previous install brought up introduces a failure mode
            // this rollback cannot recover from
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

        // Reason of the *last* entry with this name, mirroring PatchStatus.Replace, which rewrites the
        // *first*. Both rely on one entry per name, which Install and Toggle guarantee, so a resolver
        // returning a duplicated id would make them act on different entries
        private static string FindReason(PatchStatus status, string name)
        {
            for (int i = status.Entries.Count - 1; i >= 0; i--)
            {
                if (string.Equals(status.Entries[i].Name, name, StringComparison.Ordinal))
                    return status.Entries[i].Reason;
            }
            return null;
        }

        // Failed removals stay recorded, so their accessors are never reset underneath a Harmony
        // method that is still live
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
                    // A previous Patch call threw after Harmony attached part of this patch. Those
                    // methods are still live, so reinstalling would attach a second copy of the same
                    // prefix/postfix. Fail rather than Reject: this is our own leftover state, not a
                    // compatibility verdict about the game build, and it has to be an error in the log
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

                // Detached but not proven clean: retry Reset before reinstalling, and stay out if it
                // throws again rather than install on top of state the previous instance may own
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

            // No MVID gate on purpose. The only gate is the patch's own validation: Initialize
            // asserts the shape of every game member it reads or writes and ResolveTarget asserts the
            // target's own signature, so a member that kept its shape keeps working without a release
            // and a mismatch skips that one patch. A method body that kept its shape but changed its
            // semantics is not caught here; the MVID is in the startup log so a reader can tell
            // whether such a re-audit has happened for the build they are on
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
                // mismatch, not a plugin bug. Both outcomes leave the patch uninstalled
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
                    // HarmonyX lists the patch methods in the target's PatchInfo before it generates
                    // the wrapper, so a throw out of UpdateWrapper leaves our methods attached and
                    // this branch is the normal one. The record exists only so a later uninstall can
                    // detach them with per-method verification, and must never count as installed
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

            // Record only after a successful Patch, so a failed hot reload cannot leave a stale record
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
            // Keyed on the record, not on IsInstalled: the two not-live shapes are the ones that most
            // need cleaning up here
            if (!_installed.TryGetValue(id, out InstalledPatch installed))
            {
                status.Add(new PatchStatusEntry(name, false, PatchResult.SkippedNotInstalled, "not installed"));
                return;
            }

            // Unpatch first and keep the record until every method is confirmed detached, otherwise a
            // live patch is left with no record and no Reset
            var errors = new List<string>();
            foreach (PatchMethodRecord record in installed.Methods)
            {
                if (record.Removed)
                    continue;
                try
                {
                    // Per-method Unpatch keeps other patches and other mods intact, never UnpatchSelf
                    _harmony.Unpatch(installed.Target, record.Method);
                    record.Removed = true;
                }
                catch (Exception ex)
                {
                    // Re-query Harmony: if the method is already detached then the record is wrong,
                    // not the runtime state, and a retry must not unpatch it again
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
                // Record kept and Reset not called on purpose: the remaining methods are still live
                // and must keep their accessors
                status.Add(new PatchStatusEntry(name, false, PatchResult.FailedUninstall,
                    $"uninstall incomplete, patch left installed ({string.Join("; ", errors)})",
                    installed.Compatibility));
                return;
            }

            // Nothing is attached any more, so the failed-but-attached flag no longer holds. Clearing
            // it matters: if Reset then throws, the record survives as a plain ResetPending one, which
            // InstallOne may retry
            installed.AttachedAfterFailure = false;

            // Order matters: all Unpatch calls succeeded, then Reset, then drop the record
            if (installed.Reset != null)
            {
                try { installed.Reset(); }
                catch (Exception ex)
                {
                    _log?.LogError($"{name} reset failed safely after uninstall: {ex.GetType().Name}: {ex.Message}");
                    // All Harmony methods are detached but static state was not proven clean. Keep the
                    // record so a later shutdown can run Reset again, and mark it not live so a
                    // re-enable retries Reset and reinstalls instead of reporting "already installed"
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

        // The InvalidOperationException arm is default-yes on purpose: the validation helpers word a
        // shape mismatch one way, but roughly a hundred hand-rolled self-checks in the patch files word
        // it freely, so keying on a prefix would demote all of those to plugin failures, which is
        // backwards because they are the normal outcome of a game update. Carved out is the patches'
        // own accessor/delegate emission family, which means the shape was fine and our emission
        // returned null, a real fault that would otherwise blame the user's build.
        //
        // That family is matched by message suffix rather than by type, which is fragile with a known
        // failure direction: a guard worded differently is misreported as a compatibility skip. That
        // is the cheaper direction, since wrongly failing a structural mismatch costs an error storm
        // on every game update. The robust fix is a dedicated exception type thrown by the patches
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

        // True when this exception or any it wraps is one of the patches' own accessor/delegate
        // emission guards, see ClassifyValidationFailure for why the suffix is what identifies them
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

        // Unreachable today: no PatchDefinition passes a transpiler or finalizer, and no patch throws
        // a HarmonyException out of Initialize or ResolveTarget. Kept because an IL mismatch must
        // degrade to a compatibility skip rather than a plugin bug the day a Transpiler is added.
        // The wording comes from Harmony's own IL generation, so a substring test is all that is
        // available, and ClassifyValidationFailure consults this first, so a patch message that ever
        // contains one of these words would be reported as an IL mismatch
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

        // Compatibility verdict: logged as a warning, because nothing is broken
        private void Reject(
            PatchStatus status, string name, CompatibilityLevel? level, PatchResult result, string stage, Exception ex)
        {
            string reason = $"{stage}: {ex.GetType().Name}: {ex.Message}";
            status.Add(new PatchStatusEntry(name, true, result, reason, level));
            _log?.LogWarning($"{name} skipped safely ({result}) at {reason}");
        }

        // Genuine failure: logged as an error, and the patch is not installed either way
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
