using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using PeakSafeOptimizer.Config;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace PeakSafeOptimizer.Graphics
{
    /// <summary>
    /// Non-Harmony lane: one-directional caps on the render pipeline asset and QualitySettings.
    ///
    /// Every write is a min(): a value that is already cheaper than the cap is left untouched, so the
    /// worst case is zero effect rather than a regression. The values seen at startup are captured
    /// once and restored on shutdown or when the lane is switched off, which also covers the bool-like
    /// caps where "cheaper" is not a numeric comparison.
    ///
    /// Scope rule: this lane only touches knobs the game does not expose. Shadow distance and cascade
    /// count used to be capped here and are not any more - the game has a Shadow Distance setting that
    /// writes both (ShadowDistanceSettings.cs:17-41), so capping them meant silently overriding a
    /// choice the player had already made, and the single-cascade fit made distant shadow edges shift
    /// while walking. Anything a player can set in the graphics menu belongs to the player.
    ///
    /// Why writing these at runtime is safe on this build (read from the decompiled URP sources):
    ///   * msaaSampleCount (UniversalRenderPipelineAsset.cs:560-570) is a plain managed field
    ///     assignment - no pipeline rebuild, no render target reallocation, no OnValidate. Only the three
    ///     GPU-resident-drawer setters call OnValidate, and this lane never touches those.
    ///   * It is re-read per camera per frame while building the frame data
    ///     (UniversalRenderPipeline.cs:1021-1033), so a write takes effect on the next rendered frame.
    ///   * The game already writes the same properties at runtime from its own settings
    ///     (RenderScaleSetting.ApplyValue writes renderScale and upscalingFilter), so this is an
    ///     established pattern rather than a new one. Those ApplyValue calls run at settings load and
    ///     on user change only - nothing re-asserts them per frame - which is why a periodic
    ///     reassert is enough to stay the last writer.
    ///   * Writing the asset cannot leak into the user's saved settings: Setting.Save only ever
    ///     serialises the setting's own value, and nothing reads the asset back into a setting.
    ///
    /// The MSAA cap is the reason this lane still exists, because it changes image quality in the
    /// player's favour and the game offers no MSAA setting at all (no anti-aliasing or TAA setting
    /// exists in Assembly-CSharp). In this build MSAA and STP upscaling are mutually exclusive:
    /// IsSTPEnabled requires IsTemporalAAEnabled (UniversalCameraData.cs:409-416), which requires
    /// cameraTargetDescriptor.msaaSamples == 1 (:390-398). The shipped default is render scale 0.8
    /// with upscalingFilter=STP (RenderScaleSetting.cs:19-34, GetDefaultValue returns High=0.8), and
    /// the log shows the resulting warning ("Disabling TAA and STP because MSAA is on") repeatedly. So
    /// with MSAA on, the game pays for MSAA at full price and still resolves 0.8 to native with a
    /// plain bilinear blit, because no case in RenderFinalPassRenderGraph matches upscalingFilter=STP
    /// when STP did not run. Setting msaaSampleCount to 1 removes the MSAA cost and lets STP do the
    /// upscale it was configured for.
    ///
    /// Bones per vertex has no setting either, and it is a per-vertex cost of every skinned character,
    /// so it scales with how many players are on screen.
    /// </summary>
    internal sealed class GraphicsBudget
    {
        /// <summary>Seconds between reasserts, to win against the settings menu writing its own value.</summary>
        private const float ReassertInterval = 2f;

        /// <summary>
        /// Grace period before the first apply. The game's own graphics settings are applied once from
        /// SettingsHandler's constructor (GameHandler.cs:154 -> SettingsHandler.cs:71-76), which writes
        /// renderScale among others. Snapshotting before that would capture the values
        /// serialised into the asset rather than the user's chosen ones, and the restore would then put
        /// those back instead. Waiting a few seconds makes the snapshot the user's actual settings.
        /// </summary>
        private const float StartupGrace = 5f;

        private readonly ManualLogSource _log;

        private bool _hasSnapshot;
        private int _snapshotMsaaSampleCount;
        private int _snapshotAntiAliasing;
        private SkinWeights _snapshotSkinWeights;
        private int _snapshotAssetId;

        private bool _applied;
        private float _sinceReassert;
        private float _sinceStartup;
        private bool _loggedMissingAsset;

        // Last value this lane wrote for each cap, so a restore only reverts its own writes and never
        // stamps a stale snapshot over a value the user changed in the settings menu meanwhile.
        private int _wroteMsaaSampleCount;
        private int _wroteAntiAliasing = -1;
        private SkinWeights _wroteSkinWeights;
        private bool _wroteAnySkinWeights;

        internal GraphicsBudget(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>
        /// Called every frame from the plugin. Cheap when the lane is off: one config read plus a
        /// timer, and the caps themselves are only touched on the reassert tick.
        /// </summary>
        internal void Update(float deltaTime)
        {
            if (_sinceStartup < StartupGrace)
            {
                _sinceStartup += deltaTime;
                if (_sinceStartup < StartupGrace)
                    return;
            }

            bool enabled = OptimizerConfig.GraphicsBudgetEnabled?.Value ?? false;
            if (!enabled)
            {
                if (_applied)
                    Restore("configuration");
                return;
            }

            _sinceReassert += deltaTime;
            if (_applied && _sinceReassert < ReassertInterval)
                return;
            _sinceReassert = 0f;
            Apply();
        }

        /// <summary>
        /// Restores the launch snapshot. Safe to call when nothing was applied.
        /// </summary>
        internal void Shutdown()
        {
            if (_applied)
                Restore("shutdown");
        }

        private void Apply()
        {
            UniversalRenderPipelineAsset asset = ResolveAsset();
            if (asset == null)
                return;

            try
            {
                CaptureSnapshot(asset);

                bool disableMsaa = OptimizerConfig.GraphicsDisableMsaa?.Value ?? false;
                if (disableMsaa)
                {
                    // The asset value is what feeds cameraTargetDescriptor.msaaSamples, i.e. the check
                    // that decides whether TAA/STP may run (UniversalRenderPipeline.cs:1021-1033,
                    // UniversalCameraData.cs:390-398).
                    if (asset.msaaSampleCount > 1)
                    {
                        asset.msaaSampleCount = 1;
                        _wroteMsaaSampleCount = 1;
                    }
                    // The backbuffer path takes its sample count from Screen.msaaSamples instead
                    // (ScriptableRenderer.cs:1003-1018), which is fed by QualitySettings.antiAliasing.
                    // URP only syncs that from the asset in its constructor
                    // (UniversalRenderPipeline.cs:321-324), so it has to be lowered separately.
                    if (QualitySettings.antiAliasing > 1)
                    {
                        QualitySettings.antiAliasing = 0;
                        _wroteAntiAliasing = 0;
                    }
                }
                else
                {
                    // Turning one cap off has to hand that value back on its own, otherwise the only
                    // way to undo it would be disabling the whole lane.
                    RestoreMsaa(asset);
                }

                ApplySkinWeightCap();
                _applied = true;
            }
            catch (Exception ex)
            {
                _log?.LogError($"Graphics budget apply failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Bones per vertex is a per-vertex cost of every skinned character, so it is one of the few
        /// global knobs that scales with how many players are on screen. Only lowered, never raised.
        /// </summary>
        private void ApplySkinWeightCap()
        {
            int cap = OptimizerConfig.GraphicsSkinWeightCap?.Value ?? 0;
            if (!TryMapSkinWeights(cap, out SkinWeights capped))
            {
                // Includes "cap turned off": hand the launch value back.
                RestoreSkinWeights();
                return;
            }
            if (Rank(QualitySettings.skinWeights) > Rank(capped))
            {
                QualitySettings.skinWeights = capped;
                _wroteSkinWeights = capped;
                _wroteAnySkinWeights = true;
            }
        }

        private static bool TryMapSkinWeights(int value, out SkinWeights result)
        {
            switch (value)
            {
                case 1: result = SkinWeights.OneBone; return true;
                case 2: result = SkinWeights.TwoBones; return true;
                case 4: result = SkinWeights.FourBones; return true;
                case 255: result = SkinWeights.Unlimited; return true;
                default: result = SkinWeights.Unlimited; return false;
            }
        }

        /// <summary>Cost order of the enum, which is also its numeric order (SkinWeights.cs:3-10).</summary>
        private static int Rank(SkinWeights value) => (int)value;

        private void CaptureSnapshot(UniversalRenderPipelineAsset asset)
        {
            int assetId = asset.GetInstanceID();
            // A different pipeline asset means a different set of launch values; treat it as new.
            if (_hasSnapshot && _snapshotAssetId == assetId)
                return;
            _snapshotAssetId = assetId;
            _snapshotMsaaSampleCount = asset.msaaSampleCount;
            _snapshotAntiAliasing = QualitySettings.antiAliasing;
            _snapshotSkinWeights = QualitySettings.skinWeights;
            _hasSnapshot = true;
            _log?.LogInfo(
                "PeakSafeOptimizer | graphics budget snapshot: " +
                $"msaaSampleCount={_snapshotMsaaSampleCount}, antiAliasing={_snapshotAntiAliasing}, " +
                $"skinWeights={_snapshotSkinWeights}");
        }

        private void Restore(string reason)
        {
            _applied = false;
            if (!_hasSnapshot)
                return;
            try
            {
                UniversalRenderPipelineAsset asset = ResolveAsset();
                if (asset != null && asset.GetInstanceID() == _snapshotAssetId)
                {
                    RestoreMsaa(asset);
                }
                RestoreSkinWeights();
                _log?.LogInfo($"PeakSafeOptimizer | graphics budget restored launch values ({reason}).");
            }
            catch (Exception ex)
            {
                _log?.LogError($"Graphics budget restore failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Reverts the MSAA pair, but only where this lane wrote the value and the value is still the
        /// one it wrote. Anything else is the user's or the game's own newer value and is left alone.
        /// </summary>
        private void RestoreMsaa(UniversalRenderPipelineAsset asset)
        {
            if (_wroteMsaaSampleCount != 0 && asset != null &&
                asset.msaaSampleCount == _wroteMsaaSampleCount)
            {
                asset.msaaSampleCount = _snapshotMsaaSampleCount;
            }
            _wroteMsaaSampleCount = 0;

            if (_wroteAntiAliasing >= 0 && QualitySettings.antiAliasing == _wroteAntiAliasing)
                QualitySettings.antiAliasing = _snapshotAntiAliasing;
            _wroteAntiAliasing = -1;
        }

        private void RestoreSkinWeights()
        {
            if (_wroteAnySkinWeights && QualitySettings.skinWeights == _wroteSkinWeights)
                QualitySettings.skinWeights = _snapshotSkinWeights;
            _wroteAnySkinWeights = false;
        }

        private UniversalRenderPipelineAsset ResolveAsset()
        {
            // Re-fetched every time, exactly as URP itself does (UniversalRenderPipeline.asset).
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset == null && !_loggedMissingAsset)
            {
                _loggedMissingAsset = true;
                _log?.LogWarning(
                    "PeakSafeOptimizer | graphics budget idle: the active render pipeline is not a " +
                    "UniversalRenderPipelineAsset, so no cap was applied.");
            }
            return asset;
        }
    }
}
