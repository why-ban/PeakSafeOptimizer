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
        public const string PluginVersion = "1.4.1";

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
                // Group membership drives all-or-nothing installation: a switch that covers several
                // patches must never leave them half installed, because those sets are only correct
                // together (see OptimizerConfig.GetGroupMembers).
                _installer.SetGroupResolver(_settings.GetGroupMembers);
                PatchCatalogSnapshot catalog = PatchCatalog.Discover(typeof(Plugin).Assembly, _settings, _lifecycle, Logger);
                PatchStatus status = _installer.Install(catalog);
                // Multi-line, item-by-item report. Every line keeps the "PeakSafeOptimizer" prefix so
                // one grep still pulls the whole report out of a shared BepInEx log.
                foreach (string line in status.FormatReport())
                    Logger.LogInfo(line);

                // Non-Harmony lanes, both driven from Update and both restoring what they found.
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

        /// <summary>
        /// Best-effort rollback for a failed Awake: leave nothing patched, nothing subscribed and no
        /// registered cleanup pending. Each step is isolated so one failure cannot block the others.
        /// </summary>
        private void RollbackAfterFailedStartup()
        {
            // Unconditional: -= on a handler that was never added is a no-op, and this way the
            // rollback does not depend on how far Awake got before throwing.
            try
            {
                Config.SettingChanged -= OnSettingChanged;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Startup rollback: unsubscribe SettingChanged failed safely: {ex.GetType().Name}: {ex.Message}");
            }

            // Engine-global state first, in the same order as OnDestroy: Physics.reuseCollisionCallbacks
            // is only safe while the Snowball guards are live, so the flag has to come down before the
            // guards are detached, not after. It also runs unconditionally, because this component
            // keeps receiving Update after a failed Awake and leaving the lanes armed would keep
            // writing engine state for a plugin that has already given up.
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

                // ConfigEntry.Value changes before BepInEx raises SettingChanged. Queue background
                // writes instead of letting the visible setting and installed Harmony state diverge.
                //
                // One id per switch, not one per patch: the installer now installs and removes a whole
                // group as a unit, so handing it every member would repeat the same work and report
                // "already installed" for the rest.
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
            // Both non-Harmony lanes are polled rather than event driven. Their config entries are not
            // patch toggles, so BepInEx' SettingChanged path (which may fire off the main thread)
            // deliberately ignores them; polling makes hot reload work for them without a second queue.
            // Evaluate is idempotent and early-outs on a config read plus two dictionary lookups.
            _collisionReuse?.Evaluate(_installer);
        }

        private void ApplySettingChange(PatchId id)
        {
            if (_settings == null || _installer == null)
                return;
            bool enabled = _settings.IsEnabled(id);

            // Engine-global state comes down before the patch set changes, and goes back up from the
            // Update poll afterwards. Physics.reuseCollisionCallbacks is only safe while both Snowball
            // guards are live, and this call happens synchronously while Evaluate is polled once per
            // frame - so without this the window between them would run vanilla Snowball against a
            // reused Collision instance.
            _collisionReuse?.SuspendForPatchChange();

            PatchStatus status = _installer.Toggle(id, enabled);
            // Re-arm immediately rather than waiting for the next frame's poll, so a toggle that
            // leaves both guards installed does not lose the flag for a frame.
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

            // A group switch produces one entry per member; report the rest too, so a rollback is
            // visible in the log instead of only its first line.
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

            // Engine-global state first: it is not tied to whether the Harmony uninstall succeeds.
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

        /// <summary>
        /// Restores the graphics caps and the collision-reuse flag. Each step is isolated so one
        /// failure cannot block the other.
        /// </summary>
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
