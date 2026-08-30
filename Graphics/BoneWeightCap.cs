using System;
using BepInEx.Logging;
using PeakSafeOptimizer.Config;
using UnityEngine;

namespace PeakSafeOptimizer.Graphics
{
    /// <summary>
    /// Non-Harmony lane: caps <see cref="QualitySettings.skinWeights"/> at two bones per vertex.
    ///
    /// Bones per vertex is a per-vertex cost of every skinned character, so it scales with how many
    /// players are on screen - which is the case this plugin cares about - and the game exposes no
    /// setting for it (nothing in Assembly-CSharp writes skinWeights). Off by default, because fewer
    /// weights makes joint deformation at the shoulders and hips visibly coarser: that trade belongs
    /// to whoever installed the mod.
    ///
    /// The write is one-directional. A value already at or below the cap is left alone, so the worst
    /// case is no effect rather than a regression. The value in force when the lane turns on is
    /// captured on that rising edge and written back when the lane is switched off or the plugin
    /// shuts down, and only if the current value is still the one this lane wrote - anything else is
    /// the game's or the player's newer value. Switching the lane off drops the snapshot, so a player
    /// who changes graphics quality while the lane is off is not sent back to an older value when it
    /// is turned on again.
    ///
    /// Nothing in the shipped game or in URP competes for this particular setting. A sweep of the
    /// decompiled assemblies finds zero writes to skinWeights anywhere: Assembly-CSharp only writes
    /// vSyncCount (VSyncSetting.cs:16), globalTextureMipmapLimit (TextureQualitySetting.cs:23-32),
    /// lodBias (LodQuality.cs:17) and pixelLightCount (MirrorReflection.cs:40/65,
    /// MirrorCameraScript.cs:124/180). Nor does anything switch quality level: SetQualityLevel has no
    /// caller outside QualitySettings itself. URP does write QualitySettings, but from its
    /// constructor only - antiAliasing at UniversalRenderPipeline.cs:321-323 and enableLODCrossFade
    /// at :336 are both inside the ctor that starts at :310, not on any per-frame path.
    ///
    /// The periodic reassert therefore has no known adversary. It is kept as cheap insurance against
    /// the two writers that cannot be ruled out from the decompiled source: another mod calling the
    /// public QualitySettings.SetQualityLevel - directly, or via QualitySettings.ForEach, which
    /// restores the level through it (QualitySettings.cs:528-547/549-568) - which resets the level
    /// wholesale, and whatever the native side does to QualitySettings on a graphics device reset,
    /// resolution change or fullscreen-mode change. One float add per frame plus one native getter
    /// every 2 s is a cheaper price than a cap that silently stops holding.
    /// </summary>
    internal sealed class BoneWeightCap
    {
        /// <summary>
        /// Two is the point where the saving is real and the deformation is merely a little coarser.
        /// One bone per vertex is visibly wrong at the shoulders.
        /// </summary>
        private const SkinWeights Cap = SkinWeights.TwoBones;

        /// <summary>
        /// Seconds between reasserts. Insurance only - see the class docstring for why there is no
        /// known writer to fight and why the poll is kept anyway.
        /// </summary>
        private const float ReassertInterval = 2f;

        /// <summary>
        /// Grace period before the first apply. The game's own graphics settings are applied once from
        /// SettingsHandler's constructor (GameHandler.cs:154 -> SettingsHandler.cs:71-76, where
        /// AddSetting calls Load then ApplyValue immediately). None of those 30 settings writes
        /// skinWeights, so for the vanilla game this window is inert; it is kept because it costs one
        /// float compare and it is the only thing standing between the snapshot and any other early
        /// writer - a mod's own settings apply, or a quality-level switch during startup - that would
        /// otherwise be captured as if the player had chosen it and written back on restore. It only
        /// constrains when the first apply may happen; every later off-&gt;on transition re-samples
        /// immediately, since by then startup is long over.
        /// </summary>
        private const float StartupGrace = 5f;

        private readonly ManualLogSource _log;

        private bool _applied;
        private float _sinceReassert;
        private float _sinceStartup;

        private bool _hasSnapshot;
        private SkinWeights _snapshot;
        private bool _wrote;
        private SkinWeights _wroteValue;
        /// <summary>Only affects log wording: the first snapshot is the launch value, later ones are not.</summary>
        private bool _sampledBefore;

        internal BoneWeightCap(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>
        /// Called every frame from the plugin. Cheap when off: one config read plus a timer.
        /// </summary>
        internal void Update(float deltaTime)
        {
            if (_sinceStartup < StartupGrace)
            {
                _sinceStartup += deltaTime;
                if (_sinceStartup < StartupGrace)
                    return;
            }

            if (!(OptimizerConfig.GraphicsCapBoneWeights?.Value ?? false))
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

        internal void Shutdown()
        {
            if (_applied)
                Restore("shutdown");
        }

        /// <summary>
        /// Samples on the rising edge and reasserts afterwards. <see cref="_hasSnapshot"/> is the
        /// edge detector: <see cref="Restore"/> clears it - in a finally, so even a failed write-back
        /// cannot leave a stale snapshot behind - so a re-enable snapshots the value that is current
        /// then, while the 2 s reassert path finds it set and leaves the snapshot alone.
        /// </summary>
        private void Apply()
        {
            try
            {
                if (!_hasSnapshot)
                {
                    _snapshot = QualitySettings.skinWeights;
                    _hasSnapshot = true;
                    _log?.LogInfo(_sampledBefore
                        ? $"PeakSafeOptimizer | bone weight cap: value on re-enable was {_snapshot}."
                        : $"PeakSafeOptimizer | bone weight cap: launch value was {_snapshot}.");
                    _sampledBefore = true;
                }

                // Cost order is the enum's numeric order
                // (SkinWeights.cs:3-10: None=0, OneBone=1, TwoBones=2, FourBones=4, Unlimited=255).
                if ((int)QualitySettings.skinWeights > (int)Cap)
                {
                    QualitySettings.skinWeights = Cap;
                    _wroteValue = Cap;
                    _wrote = true;
                }
                _applied = true;
            }
            catch (Exception ex)
            {
                _log?.LogError($"Bone weight cap apply failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Falling edge: undo this lane's own write if it is still the live value, then drop the
        /// snapshot so it cannot be written back long after the player has moved on from it.
        ///
        /// The state teardown is in a finally, and it has to be: <see cref="_hasSnapshot"/> doubles as
        /// the rising-edge detector for <see cref="Apply"/>, so a throwing write-back that left it set
        /// would make the next enable skip re-sampling and the enable after that write a stale value
        /// over whatever the player chose in between. Failing to restore is acceptable; poisoning the
        /// snapshot is not.
        /// </summary>
        private void Restore(string reason)
        {
            _applied = false;
            if (!_hasSnapshot)
                return;
            try
            {
                if (_wrote && QualitySettings.skinWeights == _wroteValue)
                {
                    QualitySettings.skinWeights = _snapshot;
                    _log?.LogInfo($"PeakSafeOptimizer | bone weight cap restored {_snapshot} ({reason}).");
                }
                else if (_wrote)
                {
                    _log?.LogInfo(
                        "PeakSafeOptimizer | bone weight cap left skinWeights as is " +
                        $"({reason}); {QualitySettings.skinWeights} is not the value this lane wrote.");
                }
                else
                {
                    _log?.LogInfo(
                        "PeakSafeOptimizer | bone weight cap had nothing to restore " +
                        $"({reason}); skinWeights was already at or below the cap.");
                }
            }
            catch (Exception ex)
            {
                _log?.LogError($"Bone weight cap restore failed safely: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _wrote = false;
                _hasSnapshot = false;
            }
        }
    }
}
