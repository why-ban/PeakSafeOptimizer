using System;
using BepInEx.Logging;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;
using UnityEngine;

namespace PeakSafeOptimizer
{
    /// <summary>
    /// Global engine flag lane: <c>Physics.reuseCollisionCallbacks</c>.
    ///
    /// Default is false, and the game never sets it. In that mode every reported collision allocates
    /// two managed objects before the callback even runs: the <c>Collision</c> instance plus a
    /// <c>ContactPoint[]</c> that the internal constructor fills eagerly (Collision.cs:87-94),
    /// whether or not the callback ever looks at the contacts. Dispatch happens once per side of
    /// every contact pair, per event kind (Physics.cs:1440-1454), so a clustered lobby of ragdolls -
    /// roughly a hundred bodyparts each, at 50 Hz - is exactly the case where that dominates.
    ///
    /// With the flag on, the engine reuses one shared instance (<c>Physics.s_ReusableCollision</c>,
    /// Physics.cs:28/1464-1466) and drops its contact array, so <c>.contacts</c> becomes a lazy
    /// allocation and <c>GetContact(index)</c> becomes allocation free entirely.
    ///
    /// The one behavioural consequence is that a reference kept past the callback aliases that shared
    /// instance. A full-assembly audit found exactly one such reference in the game:
    /// <c>Snowball._unprocessedContact</c>. This lane therefore only flips the flag once both Snowball
    /// guards are installed, and flips it back when either one is not.
    /// </summary>
    internal sealed class CollisionCallbackReuse
    {
        private readonly ManualLogSource _log;
        private bool _applied;
        private bool _originalValue;
        private bool _hasOriginalValue;
        private bool _loggedBlocked;

        internal CollisionCallbackReuse(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>
        /// Idempotent; called after installation and again on any patch toggle. <paramref name="installer"/>
        /// is queried rather than the configuration, so a guard that failed its own structure check
        /// also blocks the flag.
        /// </summary>
        internal void Evaluate(PatchInstaller installer)
        {
            bool wanted = (OptimizerConfig.CollisionCallbackReuse?.Value ?? false) &&
                installer != null &&
                installer.IsInstalled(PatchId.SnowballContactSnapshot) &&
                installer.IsInstalled(PatchId.SnowballContactConsume);

            if (wanted == _applied)
            {
                if (!wanted && !_loggedBlocked && (OptimizerConfig.CollisionCallbackReuse?.Value ?? false))
                    LogBlocked(installer);
                return;
            }
            // A later successful install clears the warning so a re-block is reported again.
            _loggedBlocked = false;

            try
            {
                if (wanted)
                {
                    if (!_hasOriginalValue)
                    {
                        _originalValue = Physics.reuseCollisionCallbacks;
                        _hasOriginalValue = true;
                    }
                    Physics.reuseCollisionCallbacks = true;
                    _applied = true;
                    _log?.LogInfo(
                        "PeakSafeOptimizer | Physics.reuseCollisionCallbacks = true " +
                        $"(was {_originalValue}); the Snowball cross-frame collision reference is guarded.");
                }
                else
                {
                    Restore("guard unavailable");
                }
            }
            catch (Exception ex)
            {
                _log?.LogError($"Collision callback reuse toggle failed safely: {ex.GetType().Name}: {ex.Message}");
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
            if (!_hasOriginalValue)
                return;
            try
            {
                Physics.reuseCollisionCallbacks = _originalValue;
                _log?.LogInfo(
                    $"PeakSafeOptimizer | Physics.reuseCollisionCallbacks restored to {_originalValue} ({reason}).");
            }
            catch (Exception ex)
            {
                _log?.LogError($"Collision callback reuse restore failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void LogBlocked(PatchInstaller installer)
        {
            _loggedBlocked = true;
            bool snapshot = installer != null && installer.IsInstalled(PatchId.SnowballContactSnapshot);
            bool consume = installer != null && installer.IsInstalled(PatchId.SnowballContactConsume);
            _log?.LogWarning(
                "PeakSafeOptimizer | CollisionCallbackReuse stays off: it needs both Snowball guards " +
                $"(SnowballContactSnapshot installed={snapshot}, SnowballContactConsume installed={consume}).");
        }
    }
}
