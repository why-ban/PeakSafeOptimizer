using System;
using BepInEx.Logging;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;
using UnityEngine;

namespace PeakSafeOptimizer
{
    /// <summary>
    /// Global engine flag lane: <c>Physics.reuseCollisionCallbacks</c>. The game never sets it and off
    /// is what it ships with. Off, every reported collision allocates a <c>Collision</c> plus a
    /// <c>ContactPoint[]</c> filled eagerly whether or not the callback reads the contacts, and
    /// dispatch runs once per side of every contact pair per event kind. On, the engine reuses one
    /// shared instance and drops its contact array, so <c>.contacts</c> becomes lazy and
    /// <c>GetContact(index)</c> is allocation free.
    ///
    /// A reference kept past the callback therefore aliases that shared instance. The game's only
    /// cross-frame reference is <c>Snowball._unprocessedContact</c>, which is why the flag only goes on
    /// while both Snowball guards are installed. The game also hands the live Collision to mods through
    /// <c>Bodypart.collisionEnterAction</c>/<c>collisionStayAction</c> and
    /// <c>CollisionModifier.onCollide</c>, and third-party code is the surface this lane cannot
    /// enumerate.
    ///
    /// The saving is the Collision object on every reported contact plus the array on every contact
    /// nobody inspects. Not "zero allocation": <c>contacts[0]</c> is read on the bodypart collision
    /// path, so a ragdoll pile on terrain still allocates one array per such contact.
    ///
    /// A shared bool carries no ownership token, so "is this still my write" is answered by comparing
    /// against the value this lane wrote: a mod that clears the flag while this lane holds it is
    /// overwritten on the next patch toggle, and a mod that sets it to the same true this lane wrote is
    /// indistinguishable from this lane, so the restore can clear that mod's intent. The comparison
    /// only rules out writing back over a value this lane provably did not write.
    /// </summary>
    internal sealed class CollisionCallbackReuse
    {
        private readonly ManualLogSource _log;
        private bool _applied;
        private bool _originalValue;
        private bool _hasOriginalValue;
        private bool _wrote;
        private bool _wroteValue;
        private bool _loggedBlocked;

        // True while the engine flag is set by this lane. The dependent patches read this instead of
        // the engine property, so they stay inert whenever reuse is off
        internal static bool Active { get; private set; }

        internal CollisionCallbackReuse(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>
        /// Idempotent, called after installation and again on any patch toggle. <paramref name="installer"/>
        /// is queried rather than the configuration, so a guard that failed its own structure check
        /// also blocks the flag.
        ///
        /// Snapshot/restore, as in BoneWeightCap, because this is a process-wide flag other mods may
        /// also write: off to on samples the current value then writes and remembers what was written,
        /// on to on does nothing at all, on to off writes the sample back only while the flag still
        /// holds this lane's own write and then drops it so the next enable samples afresh.
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
            // A later successful install clears the warning so a re-block is reported again
            _loggedBlocked = false;

            try
            {
                if (wanted)
                {
                    // Rising edge: re-sample, so an off-on-off-on cycle restores what the flag held at
                    // the start of the second on rather than a value from before the first
                    _originalValue = Physics.reuseCollisionCallbacks;
                    _hasOriginalValue = true;
                    // Guards first: they must never be inert while the engine is already reusing
                    Active = true;
                    Physics.reuseCollisionCallbacks = true;
                    _wroteValue = true;
                    _wrote = true;
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

        /// <summary>
        /// Restores the flag before a patch is uninstalled, and is a no-op when the flag is not
        /// currently held by this lane.
        ///
        /// Ordering matters: hot reload removes the Snowball guard synchronously while
        /// <see cref="Evaluate"/> is only polled once per frame, so without this the interval between
        /// the two would run vanilla Snowball against a reused Collision. Off is the engine default, so
        /// turning the flag off first cannot break anything.
        /// </summary>
        internal void SuspendForPatchChange()
        {
            if (_applied)
                Restore("patch set changing");
        }

        /// <summary>
        /// Undoes this lane's own write. Deliberately asymmetric with BoneWeightCap.Restore, which
        /// clears its state in a finally: there the snapshot flag doubles as the rising-edge detector,
        /// so a failed write-back that skipped teardown would poison the next enable. Here the rising
        /// edge re-samples unconditionally and the snapshot flag is only a "nothing to undo"
        /// short-circuit, so leaving it set after a failed write-back costs nothing.
        /// </summary>
        private void Restore(string reason)
        {
            _applied = false;
            if (!_hasOriginalValue)
            {
                Active = false;
                return;
            }
            try
            {
                // Only undo this lane's own write, and only while it is still the live value: anything
                // else belongs to whoever wrote it after this lane did
                if (_wrote && Physics.reuseCollisionCallbacks == _wroteValue)
                {
                    Physics.reuseCollisionCallbacks = _originalValue;
                    // Only now is the engine back to not reusing, so only now may the guards go inert
                    Active = false;
                    _log?.LogInfo(
                        $"PeakSafeOptimizer | Physics.reuseCollisionCallbacks restored to {_originalValue} ({reason}).");
                }
                else
                {
                    Active = false;
                    _log?.LogInfo(
                        "PeakSafeOptimizer | Physics.reuseCollisionCallbacks left as is " +
                        $"({reason}); its current value is not the one this lane wrote.");
                }
                _wrote = false;
                // Drop the snapshot so a later enable samples the value that is current then
                _hasOriginalValue = false;
            }
            catch (Exception ex)
            {
                // The write-back failed, so the engine may still be reusing: leave the guards active
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
