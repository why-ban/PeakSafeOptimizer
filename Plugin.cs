using System;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Diagnostics;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.peak.safeoptimizer";
        public const string PluginName = "PeakSafeOptimizer";
        public const string PluginVersion = "0.1.0";

        private Harmony _harmony;
        private LifecycleRegistry _lifecycle;
        private OptimizerConfig _settings;
        private PatchInstaller _installer;
        private int _mainThreadId;

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
                PatchCatalogSnapshot catalog = PatchCatalog.Discover(typeof(Plugin).Assembly, _settings, _lifecycle, Logger);
                PatchStatus status = _installer.Install(catalog);
                // Multi-line, item-by-item report. Every line keeps the "PeakSafeOptimizer" prefix so
                // one grep still pulls the whole report out of a shared BepInEx log.
                foreach (string line in status.FormatReport())
                    Logger.LogInfo(line);

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

            try
            {
                _harmony?.UnpatchSelf();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Startup rollback: UnpatchSelf failed safely: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                _lifecycle?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Startup rollback: lifecycle dispose failed safely: {ex.GetType().Name}: {ex.Message}");
            }

            _lifecycle = null;
            _harmony = null;
            _installer = null;
            _settings = null;
            Logger.LogWarning("PeakSafeOptimizer | startup rolled back: no patch from this plugin is installed.");
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs args)
        {
            try
            {
                // ConfigurationManager fires this on the Unity main thread; if it ever arrives
                // elsewhere, ignore it defensively rather than mutating Harmony state off-thread.
                if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                {
                    Logger.LogWarning("PeakSafeOptimizer setting change ignored: not on the main thread.");
                    return;
                }
                if (args?.ChangedSetting == null)
                    return;
                if (_settings == null || !_settings.TryGetPatchId(args.ChangedSetting, out PatchId id))
                    return; // Value-typed entries already take effect at runtime; nothing to do.

                bool enabled = _settings.IsEnabled(id);
                PatchStatus status = _installer.Toggle(id, enabled);
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
            }
            catch (Exception ex)
            {
                Logger.LogError($"HotReload failed safely: {ex.GetType().Name}: {ex.Message}");
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

            try
            {
                _harmony?.UnpatchSelf();
            }
            catch (Exception ex)
            {
                Logger.LogError($"UnpatchSelf failed during shutdown: {ex}");
            }
            finally
            {
                _lifecycle?.Dispose();
                _lifecycle = null;
                _harmony = null;
                _installer = null;
                _settings = null;
            }
        }
    }
}
