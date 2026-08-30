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
    ///
    /// Two exposure surfaces remain, and neither is a cross-frame reference:
    ///   * The game hands the live Collision to mods itself, through
    ///     <c>Bodypart.collisionEnterAction</c>/<c>collisionStayAction</c>
    ///     (<c>public Action&lt;Collision&gt;</c>, Bodypart.cs:55/57, invoked at :242/:251) and
    ///     <c>CollisionModifier.onCollide</c>
    ///     (<c>public Action&lt;Character, CollisionModifier, Collision, Bodypart&gt;</c>,
    ///     CollisionModifier.cs:30, invoked at :67). A subscriber that only reads inside the callback
    ///     is unaffected; one that stores the instance is not, which is why third-party code stays
    ///     the surface this lane cannot enumerate and why the flag is off by default.
    ///   * "Zero allocation" is bounded by who touches <c>.contacts</c>: with reuse on that property
    ///     is lazy (Collision.cs:66-77 allocates on first read), not absent. The game reads
    ///     <c>contacts[0]</c> in plenty of places - CharacterMovement.cs:928/948/953,
    ///     StickyCactus.cs:16, TumbleWeed.cs:90, Breakable.cs:110, SpikeRoller.cs:65 among others -
    ///     and CharacterMovement.OnCollision is on the bodypart path, so a ragdoll pile touching
    ///     terrain still allocates one array per such contact. The saving is the Collision object on
    ///     every reported contact plus the array on every contact nobody inspects.
    ///
    /// One consequence of the flag being a bool deserves spelling out, because it looks like a bug and
    /// is not. The lane cannot tell its own true from another mod's true, so "is this still my write?"
    /// is answered by comparing against <c>true</c>. If another mod sets the flag back to false while
    /// this lane holds it, the next <see cref="SuspendForPatchChange"/> or <see cref="Shutdown"/> sees
    /// false, takes the "not the value this lane wrote" branch, and drops the snapshot anyway - and
    /// then the following <see cref="Evaluate"/> re-samples (getting that mod's false as the new
    /// original) and writes true again. Net effect: on every patch toggle this lane wins, and the
    /// other mod's false survives only until the next toggle. That is the price of a shared bool with
    /// no ownership token; the alternative - refusing to re-apply after someone else cleared the flag -
    /// would let any mod silently disable this lane for the rest of the session. The restore direction
    /// stays conservative either way: this lane never writes false over someone else's true.
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

        internal CollisionCallbackReuse(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>
        /// Idempotent; called after installation and again on any patch toggle. <paramref name="installer"/>
        /// is queried rather than the configuration, so a guard that failed its own structure check
        /// also blocks the flag.
        ///
        /// The snapshot/restore contract is the same one BoneWeightCap keeps, because this is a
        /// process-wide flag other mods may also write:
        ///   off -&gt; on:  sample the current value, then write true and remember what was written.
        ///   on  -&gt; on:  nothing happens; the whole method early-outs before any engine access.
        ///   on  -&gt; off: write the sampled value back only while the flag still holds this lane's
        ///               own write, then drop the snapshot so the next enable samples afresh.
        /// Sampling therefore happens on the rising edge only, never on the per-frame poll, and a
        /// mod that set the flag after this lane did keeps its value.
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
                    // Rising edge: re-sample. An off->on->off->on cycle must restore whatever the
                    // flag held at the start of the second on, not a value from before the first.
                    _originalValue = Physics.reuseCollisionCallbacks;
                    _hasOriginalValue = true;
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
        /// Ordering matters here. The flag's whole safety argument is that Snowball's cross-frame
        /// <c>Collision</c> reference is guarded, and hot reload removes that guard synchronously
        /// while <see cref="Evaluate"/> is only polled once per frame from Plugin.Update. Without
        /// this call the interval between the two would run vanilla
        /// <c>Snowball.OnCollisionStay</c>/<c>ProcessLastCollision</c> with reuse still on, i.e.
        /// exactly the aliasing this lane exists to prevent - and <c>contacts[0]</c> on a reused
        /// instance whose pair has no points would also throw. Turning the flag off first cannot
        /// break anything: off is the engine default and the state the game ships with.
        /// </summary>
        internal void SuspendForPatchChange()
        {
            if (_applied)
                Restore("patch set changing");
        }

        /// <summary>
        /// Undoes this lane's own write. Deliberately asymmetric with BoneWeightCap.Restore, which
        /// clears its state in a finally: there <c>_hasSnapshot</c> doubles as the rising-edge detector
        /// for a periodic reassert, so a throwing write-back that skipped the teardown would poison the
        /// next enable. Here the rising edge in <see cref="Evaluate"/> re-samples
        /// <c>_originalValue</c> unconditionally (:108-109) and <c>_hasOriginalValue</c> is only ever read
        /// as a "nothing to undo" short-circuit, so leaving it set after a failed write-back costs
        /// nothing - the next enable overwrites it regardless. No finally is added, to keep the teardown
        /// on the path that actually logged what it did.
        /// </summary>
        private void Restore(string reason)
        {
            _applied = false;
            if (!_hasOriginalValue)
                return;
            try
            {
                // Only undo this lane's own write, and only while it is still the live value: any
                // other value belongs to whoever wrote it after this lane did.
                if (_wrote && Physics.reuseCollisionCallbacks == _wroteValue)
                {
                    Physics.reuseCollisionCallbacks = _originalValue;
                    _log?.LogInfo(
                        $"PeakSafeOptimizer | Physics.reuseCollisionCallbacks restored to {_originalValue} ({reason}).");
                }
                else
                {
                    _log?.LogInfo(
                        "PeakSafeOptimizer | Physics.reuseCollisionCallbacks left as is " +
                        $"({reason}); its current value is not the one this lane wrote.");
                }
                _wrote = false;
                // Drop the snapshot so a later enable samples the value that is current then.
                _hasOriginalValue = false;
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
