using System;
using BepInEx.Logging;
using PeakSafeOptimizer.Config;
using UnityEngine;

namespace PeakSafeOptimizer.Graphics
{
    /// <summary>
    /// Non-Harmony lane: forces the global heat-haze amount to zero, which removes Mesa's full-screen
    /// air-distortion effect.
    ///
    /// The value is the shader global <c>_GlobalHazeAmount</c>, which DayNightManager drives from the
    /// active segment's DayNightProfile (name mapping at DayNightManager.cs:134-150, enum member
    /// <c>ShaderParams.AirDistortion</c>). Writers, all in DayNightManager:
    ///   * <c>clearAllShaderParams</c> at Awake sets every one of these globals to 0 (:152-158).
    ///   * <c>blendRoutine</c> Lerps the incoming profile's value in while a segment transition runs
    ///     (:205-228), i.e. every frame during the blend, from a coroutine.
    ///   * <c>UpdateCycle</c> re-evaluates the profile's animated curve every frame outside a blend
    ///     (:416-421), called from <c>Update</c> (:313).
    ///
    /// So the only writers are Update and a coroutine, and a coroutine resumes before LateUpdate.
    /// Writing from LateUpdate is therefore the last write of the frame with no Harmony patch needed.
    /// The two other LateUpdate writers of shader globals in the game (LavaPost, StormVisual) set
    /// different property names, so there is nothing to arbitrate against.
    ///
    /// Why this is safe to force: nothing in the game reads the value back. The only C# read anywhere
    /// is DayNightManager's own <c>HazeDebug</c> field (:288), which no code consumes - a repo-wide
    /// search finds only its declaration and that assignment. It is a purely visual quantity consumed
    /// by shaders.
    ///
    /// Why it needs no biome check: profiles that do not configure air distortion leave the global at
    /// 0, so writing 0 is a no-op outside Mesa. The suppressor does not need to know which segment is
    /// active, and it cannot affect any other biome's look.
    ///
    /// Rollback: the last non-zero value seen before overwriting is remembered and written back once
    /// when the lane is turned off or the plugin shuts down. That matters because a profile's static
    /// entries are only written during a blend, so simply stopping would otherwise leave the haze at 0
    /// until the next segment transition.
    /// </summary>
    internal sealed class HeatHazeSuppressor
    {
        private const string HazePropertyName = "_GlobalHazeAmount";

        private static readonly int HazeId = Shader.PropertyToID(HazePropertyName);

        private readonly ManualLogSource _log;

        private bool _applied;
        private bool _hasOriginal;
        private float _original;
        private bool _loggedFirstSuppression;

        internal HeatHazeSuppressor(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>
        /// Call from LateUpdate so this is the frame's last writer. One global read plus a conditional
        /// global write; both are cheap next to the full-screen effect they remove.
        /// </summary>
        internal void LateUpdate()
        {
            bool enabled = OptimizerConfig.SuppressHeatHaze?.Value ?? false;
            if (!enabled)
            {
                if (_applied)
                    Restore("configuration");
                return;
            }

            try
            {
                float current = Shader.GetGlobalFloat(HazeId);
                if (current == 0f)
                {
                    // Either another biome, or our own write from a previous frame that nobody has
                    // overwritten since. Nothing to do either way.
                    _applied = true;
                    return;
                }

                // Remember what DayNightManager wanted, so the effect can be handed back intact.
                _original = current;
                _hasOriginal = true;
                Shader.SetGlobalFloat(HazeId, 0f);
                _applied = true;

                if (!_loggedFirstSuppression)
                {
                    _loggedFirstSuppression = true;
                    _log?.LogInfo(
                        $"PeakSafeOptimizer | heat haze suppressed: {HazePropertyName} was {current}, " +
                        "forced to 0 every LateUpdate.");
                }
            }
            catch (Exception ex)
            {
                _log?.LogError($"Heat haze suppression failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        internal void Shutdown()
        {
            if (_applied)
                Restore("shutdown");
        }

        private void Restore(string reason)
        {
            _applied = false;
            if (!_hasOriginal)
                return;
            try
            {
                // Only restore over our own zero. A non-zero value means DayNightManager has already
                // taken the global back and its value is newer than ours.
                if (Shader.GetGlobalFloat(HazeId) == 0f)
                    Shader.SetGlobalFloat(HazeId, _original);
                _hasOriginal = false;
                _loggedFirstSuppression = false;
                _log?.LogInfo($"PeakSafeOptimizer | heat haze restored to {_original} ({reason}).");
            }
            catch (Exception ex)
            {
                _log?.LogError($"Heat haze restore failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
