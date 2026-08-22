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
    /// case is no effect rather than a regression. The value seen at startup is captured once and
    /// written back when the lane is switched off or the plugin shuts down, and only if the current
    /// value is still the one this lane wrote - anything else is the game's or the player's newer
    /// value.
    ///
    /// There is no game code to fight here (no per-frame writer of skinWeights exists), but the
    /// periodic reassert is kept because the quality-level machinery can reset QualitySettings
    /// wholesale.
    /// </summary>
    internal sealed class BoneWeightCap
    {
        /// <summary>
        /// Two is the point where the saving is real and the deformation is merely a little coarser.
        /// One bone per vertex is visibly wrong at the shoulders.
        /// </summary>
        private const SkinWeights Cap = SkinWeights.TwoBones;

        /// <summary>Seconds between reasserts.</summary>
        private const float ReassertInterval = 2f;

        /// <summary>
        /// Grace period before the first apply. The game's own graphics settings are applied once from
        /// SettingsHandler's constructor (GameHandler.cs:154 -> SettingsHandler.cs:71-76). Snapshotting
        /// before that could capture a value the player never chose, and the restore would then put
        /// that back instead.
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

        private void Apply()
        {
            try
            {
                if (!_hasSnapshot)
                {
                    _snapshot = QualitySettings.skinWeights;
                    _hasSnapshot = true;
                    _log?.LogInfo($"PeakSafeOptimizer | bone weight cap: launch value was {_snapshot}.");
                }

                // Cost order is the enum's numeric order (SkinWeights.cs:3-10).
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

        private void Restore(string reason)
        {
            _applied = false;
            if (!_hasSnapshot)
                return;
            try
            {
                if (_wrote && QualitySettings.skinWeights == _wroteValue)
                    QualitySettings.skinWeights = _snapshot;
                _wrote = false;
                _log?.LogInfo($"PeakSafeOptimizer | bone weight cap restored {_snapshot} ({reason}).");
            }
            catch (Exception ex)
            {
                _log?.LogError($"Bone weight cap restore failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
