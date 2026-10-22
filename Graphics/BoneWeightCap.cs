using System;
using BepInEx.Logging;
using PeakSafeOptimizer.Config;
using UnityEngine;

namespace PeakSafeOptimizer.Graphics
{
    /// <summary>
    /// Non-Harmony lane: caps <see cref="QualitySettings.skinWeights"/> at two bones per vertex, a
    /// per-vertex cost of every skinned character that therefore scales with how many players are on
    /// screen. Nothing in Assembly-CSharp writes skinWeights and nothing calls SetQualityLevel, so the
    /// game offers no setting for it. Off by default, because fewer weights makes joint deformation at
    /// the shoulders and hips visibly coarser.
    ///
    /// The write is one-directional, so the worst case is no effect rather than a regression. The value
    /// in force when the lane turns on is captured on that rising edge and written back on switch-off
    /// or shutdown, and only while the current value is still the one this lane wrote. Switching off
    /// drops the snapshot, so a quality change made while the lane was off is not overwritten later.
    ///
    /// The periodic reassert has no known adversary and is insurance against the two writers the
    /// decompile cannot rule out: another mod calling SetQualityLevel, which resets the level
    /// wholesale, and the native side on a device reset, resolution change or fullscreen-mode change.
    /// </summary>
    internal sealed class BoneWeightCap
    {
        // Two is where the saving is real and deformation is merely a little coarser. One bone is
        // visibly wrong at the shoulders
        private const SkinWeights Cap = SkinWeights.TwoBones;

        // Insurance only, see the class docstring for why there is no known writer to fight
        private const float ReassertInterval = 2f;

        /// <summary>
        /// Grace period before the first apply. The game applies its own graphics settings once from
        /// SettingsHandler's constructor and none of them writes skinWeights, so for the vanilla game
        /// this window is inert. It is kept because it is the only thing standing between the snapshot
        /// and another early writer, whose value would otherwise be captured as if the player had
        /// chosen it. Only the first apply is delayed, later off-to-on transitions re-sample at once.
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
        // Only affects log wording: the first snapshot is the launch value, later ones are not
        private bool _sampledBefore;

        internal BoneWeightCap(ManualLogSource log)
        {
            _log = log;
        }

        // Called every frame from the plugin. Cheap when off: one config read plus a timer
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
        /// Samples on the rising edge and reasserts afterwards. The snapshot flag is the edge detector:
        /// <see cref="Restore"/> clears it in a finally, so even a failed write-back cannot leave a
        /// stale snapshot behind and a re-enable samples the value that is current then.
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
        /// The teardown has to be in a finally because the snapshot flag doubles as the rising-edge
        /// detector for <see cref="Apply"/>. Failing to restore is acceptable, poisoning the snapshot
        /// is not.
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
