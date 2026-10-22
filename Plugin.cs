using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Diagnostics;
using PeakSafeOptimizer.Graphics;
using PeakSafeOptimizer.Infrastructure;
using UnityEngine;

namespace PeakSafeOptimizer
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.peak.safeoptimizer";
        public const string PluginName = "PeakSafeOptimizer";
        public const string PluginVersion = "1.5.0";

        private Harmony _harmony;
        private LifecycleRegistry _lifecycle;
        private OptimizerConfig _settings;
        private PatchInstaller _installer;
        private BoneWeightCap _boneWeightCap;
        private CollisionCallbackReuse _collisionReuse;
        private int _mainThreadId;
        private readonly ConcurrentQueue<PatchId> _pendingSettingChanges = new ConcurrentQueue<PatchId>();

        private void Awake()
        {
            try
            {
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;

                _settings = OptimizerConfig.Bind(Config);
                RateLimitedDiagnostics.Initialize(Logger, _settings.DetailedDiagnostics);
                _lifecycle = new LifecycleRegistry(Logger);
                _lifecycle.Register(RateLimitedDiagnostics.Reset);

                BuildFingerprint fingerprint = BuildFingerprint.Capture();
                Logger.LogInfo(fingerprint.ToDiagnosticString());

                _harmony = new Harmony(PluginGuid);
                _installer = new PatchInstaller(_harmony, fingerprint, Logger);
                // Group membership drives all-or-nothing installation, see OptimizerConfig.GetGroupMembers
                _installer.SetGroupResolver(_settings.GetGroupMembers);
                PatchCatalogSnapshot catalog = PatchCatalog.Discover(typeof(Plugin).Assembly, _settings, _lifecycle, Logger);
                PatchStatus status = _installer.Install(catalog);
                foreach (string line in status.FormatReport())
                    Logger.LogInfo(line);

                _boneWeightCap = new BoneWeightCap(Logger);
                _collisionReuse = new CollisionCallbackReuse(Logger);
                _collisionReuse.Evaluate(_installer);

                Config.SettingChanged += OnSettingChanged;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Plugin startup failed safely; no failure will escape Awake. {ex}");
                RollbackAfterFailedStartup();
            }
        }

        // Best-effort rollback for a failed Awake: nothing patched, nothing subscribed, no cleanup left
        // pending. Each step is isolated so one failure cannot block the others
        private void RollbackAfterFailedStartup()
        {
            // Unconditional: -= on a handler that was never added is a no-op, so the rollback does not
            // depend on how far Awake got
            try
            {
                Config.SettingChanged -= OnSettingChanged;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Startup rollback: unsubscribe SettingChanged failed safely: {ex.GetType().Name}: {ex.Message}");
            }

            // Engine-global state first, as in OnDestroy: the flag is only safe while the Snowball
            // guards are live, so it has to come down before they are detached. Also unconditional,
            // because this component keeps receiving Update after a failed Awake
            ShutdownGlobalLanes();

            bool fullyUninstalled = UninstallAll("Startup rollback");
            if (fullyUninstalled)
            {
                DisposeLifecycle("Startup rollback");
                _lifecycle = null;
                _harmony = null;
                _installer = null;
                _settings = null;
                Logger.LogWarning("PeakSafeOptimizer | startup rolled back: all known patches were removed.");
            }
            else
            {
                Logger.LogError(
                    "PeakSafeOptimizer | startup rollback could not account for every patch: one or " +
                    "more records remain, either with Harmony methods still attached or detached but " +
                    "with their Reset still failing. Their state was retained rather than reset " +
                    "underneath a possibly live Harmony method.");
            }
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs args)
        {
            try
            {
                if (args?.ChangedSetting == null)
                    return;
                if (_settings == null ||
                    !_settings.TryGetPatchIds(args.ChangedSetting, out IReadOnlyList<PatchId> ids))
                {
                    return; // Value-typed entries already take effect at runtime; nothing to do.
                }

                // ConfigEntry.Value is already updated when BepInEx raises SettingChanged, so a write
                // from a background thread is queued to Update instead of letting the visible setting
                // and the installed Harmony state diverge.
                //
                // One id per switch: the installer handles the whole group, so passing every member
                // would repeat the same work
                if (ids.Count == 0)
                    return;
                PatchId representative = ids[0];
                bool offMainThread = Thread.CurrentThread.ManagedThreadId != _mainThreadId;
                if (offMainThread)
                    _pendingSettingChanges.Enqueue(representative);
                else
                    ApplySettingChange(representative);
            }
            catch (Exception ex)
            {
                Logger.LogError($"HotReload failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void Update()
        {
            while (_pendingSettingChanges.TryDequeue(out PatchId id))
                ApplySettingChange(id);

            _boneWeightCap?.Update(Time.unscaledDeltaTime);
            // Both non-Harmony lanes are polled, not event driven: their config entries are not patch
            // toggles, so SettingChanged ignores them, and polling makes hot reload work for them
            // without a second queue
            _collisionReuse?.Evaluate(_installer);
        }

        private void ApplySettingChange(PatchId id)
        {
            if (_settings == null || _installer == null)
                return;
            bool enabled = _settings.IsEnabled(id);

            // Engine-global state comes down before the patch set changes and goes back up from the
            // Update poll afterwards: the flag is only safe while both Snowball guards are live, and
            // this call is synchronous while Evaluate is polled once per frame
            _collisionReuse?.SuspendForPatchChange();

            PatchStatus status = _installer.Toggle(id, enabled);
            // Re-arm immediately rather than waiting for the next poll, so a toggle that leaves both
            // guards installed does not lose the flag for a frame
            _collisionReuse?.Evaluate(_installer);

            PatchStatusEntry entry = status.Entries.Count > 0 ? status.Entries[0] : null;
            if (entry == null)
                return;

            switch (entry.Outcome)
            {
                case PatchOutcome.Applied:
                    Logger.LogInfo(enabled
                        ? $"HotReload: {id} enabled→installed"
                        : $"HotReload: {id} disabled→uninstalled");
                    break;
                case PatchOutcome.Skipped:
                    Logger.LogInfo(enabled
                        ? $"HotReload: {id} enabled→{entry.Result}: {entry.Reason}"
                        : $"HotReload: {id} disabled→{entry.Result}: {entry.Reason}");
                    break;
                case PatchOutcome.Failed:
                    Logger.LogError($"HotReload: {id} {entry.Result}: {entry.Reason}");
                    break;
            }

            // A group switch produces one entry per member. Report the rest too, so a rollback is
            // visible instead of only its first line
            for (int i = 1; i < status.Entries.Count; i++)
            {
                PatchStatusEntry extra = status.Entries[i];
                if (extra.Outcome == PatchOutcome.Failed)
                    Logger.LogError($"HotReload: {extra.Name} {extra.Result}: {extra.Reason}");
                else
                    Logger.LogInfo($"HotReload: {extra.Name} {extra.Result}: {extra.Reason}");
            }
        }

        private void OnDestroy()
        {
            try
            {
                Config.SettingChanged -= OnSettingChanged;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Unsubscribe SettingChanged failed: {ex}");
            }

            // Engine-global state first: it is not tied to whether the Harmony uninstall succeeds
            ShutdownGlobalLanes();

            bool fullyUninstalled = UninstallAll("Shutdown");
            if (fullyUninstalled)
            {
                DisposeLifecycle("Shutdown");
                _lifecycle = null;
                _harmony = null;
                _installer = null;
                _settings = null;
            }
            else
            {
                Logger.LogError(
                    "PeakSafeOptimizer shutdown could not account for every patch: one or more records " +
                    "remain, either with Harmony methods still attached or detached but with their " +
                    "Reset still failing. Their state was retained to keep those methods safe until " +
                    "process exit.");
            }
        }

        private bool UninstallAll(string phase)
        {
            if (_installer == null)
                return true;
            try
            {
                PatchStatus status = _installer.UninstallAll();
                foreach (PatchStatusEntry entry in status.Entries)
                {
                    if (entry.Outcome == PatchOutcome.Failed)
                        Logger.LogError($"{phase}: {entry.Name} {entry.Result}: {entry.Reason}");
                }
                return !_installer.HasInstalledPatches;
            }
            catch (Exception ex)
            {
                Logger.LogError($"{phase}: verified uninstall failed safely: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // Restores the graphics caps and the collision-reuse flag. Each step is isolated so one
        // failure cannot block the other
        private void ShutdownGlobalLanes()
        {
            try
            {
                _collisionReuse?.Shutdown();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Collision reuse shutdown failed safely: {ex.GetType().Name}: {ex.Message}");
            }
            _collisionReuse = null;

            try
            {
                _boneWeightCap?.Shutdown();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Bone weight cap shutdown failed safely: {ex.GetType().Name}: {ex.Message}");
            }
            _boneWeightCap = null;
        }

        private void DisposeLifecycle(string phase)
        {
            try
            {
                _lifecycle?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogError($"{phase}: lifecycle dispose failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
