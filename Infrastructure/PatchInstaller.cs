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
        }

        private const string CertifiedMode = "certified";
        private const string StructuralMode = "structural-validation";

        private readonly Harmony _harmony;
        private readonly BuildFingerprint _fingerprint;
        private readonly ManualLogSource _log;
        private Dictionary<PatchId, Func<PatchDefinition>> _factories;
        private readonly Dictionary<PatchId, InstalledPatch> _installed =
            new Dictionary<PatchId, InstalledPatch>();

        public PatchInstaller(Harmony harmony, BuildFingerprint fingerprint, ManualLogSource log)
        {
            _harmony = harmony; _fingerprint = fingerprint; _log = log;
        }

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
            foreach (PatchRegistration registration in catalog.Registrations)
            {
                if (registration.Enabled)
                    InstallOne(registration.Id, status);
                else
                    status.Add(new PatchStatusEntry(
                        registration.Id.ToString(), false, PatchResult.SkippedDisabled,
                        "disabled by configuration"));
            }
            return status;
        }

        public bool IsInstalled(PatchId id) => _installed.ContainsKey(id);
        public bool HasInstalledPatches => _installed.Count != 0;

        /// <summary>
        /// Hot-reload entry point. Enabled=true installs (idempotent; reuses the same isolated
        /// factory→Initialize→ResolveTarget→HarmonyMethod→Patch path, so a patch that cannot validate
        /// itself against the running build still fails closed here).
        /// Enabled=false uninstalls by the recorded patch methods, then runs the optional Reset.
        /// Every branch is fail-closed and isolated to this one PatchId.
        /// </summary>
        public PatchStatus Toggle(PatchId id, bool enabled)
        {
            PatchStatus status = NewStatus();
            if (enabled)
                InstallOne(id, status);
            else
                UninstallOne(id, status);
            return status;
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
                status.Add(new PatchStatusEntry(
                    name, true, PatchResult.SkippedAlreadyInstalled, "already installed", existing.Compatibility));
                return;
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
                    _installed[id] = partial;
                    _log?.LogWarning(
                        $"{name} Patch threw after attaching at least one Harmony method; retained for verified cleanup.");
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

            // Order matters: all Unpatch calls succeeded → Reset → drop the record.
            if (installed.Reset != null)
            {
                try { installed.Reset(); }
                catch (Exception ex)
                {
                    _log?.LogError($"{name} reset failed safely after uninstall: {ex.GetType().Name}: {ex.Message}");
                    // All Harmony methods are detached, but static state was not proven clean. Keep
                    // the record so a later shutdown/retry can run Reset again instead of reporting
                    // a successful uninstall with stale accessors.
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
        /// Maps a validation exception onto a compatibility verdict. Missing or reshaped members are a
        /// structural mismatch; IL-pattern and IL-generation complaints are an IL mismatch; anything
        /// else stays a genuine failure. Every branch leaves the patch uninstalled.
        /// </summary>
        private static PatchResult ClassifyValidationFailure(Exception ex, PatchResult fallback)
        {
            if (LooksLikeIlFailure(ex))
                return PatchResult.SkippedIlMismatch;
            if (ex is MissingMemberException || ex is TypeLoadException || ex is InvalidOperationException)
                return PatchResult.SkippedStructureMismatch;
            return fallback;
        }

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
