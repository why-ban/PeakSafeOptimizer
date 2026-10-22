using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using Zorro.Core.Serizalization;

namespace PeakSafeOptimizer.Patches.Stability
{
    /// <summary>
    /// Null guard for <c>SerializableRunBasedValues.SerializeRunBasedValues(BinarySerializer)</c>
    /// (audited source SerializableRunBasedValues.cs:79-95), whose first statement is
    /// <c>int count = runBasedInts.Count;</c> on an <c>internal</c> field that nothing on this path
    /// guarantees is allocated. <c>runBasedFloats</c> is dereferenced the same way at :88. Both are
    /// only ever assigned by the private <c>Allocate()</c> (:34-45), reached from
    /// <c>ConstructNew()</c> (:26-32) and from <c>Deserialize</c> (:68-77) - so a
    /// <c>default(SerializableRunBasedValues)</c> that reaches serialization carries two nulls and
    /// the original throws NullReferenceException.
    ///
    /// The Prefix returns void, so the original always runs; the only thing it does is allocate an
    /// empty dictionary into whichever of the two fields is null. It reproduces no control flow.
    ///
    /// WHY THE REPAIR DOES NOT PERSIST - and why the diagnostic counter must be read accordingly.
    /// <c>SerializableRunBasedValues</c> is a <c>struct</c> (SerializableRunBasedValues.cs:6), and the
    /// value that reaches the serializer on the hot path is read out of a *property*:
    /// <c>AchievementManager.runBasedValueData { get; private set; }</c>
    /// (AchievementManager.cs:150), consumed by
    /// <c>Player.Update</c> → <c>photonView.RPC("UpdateAchievementProgress", RpcTarget.All,
    /// Singleton&lt;AchievementManager&gt;.Instance.runBasedValueData)</c> (Player.cs:77), which Photon
    /// then hands to <c>CustomTypeRPCSerialization.SerializeAchievementProgress</c>
    /// (CustomTypeRPCSerialization.cs:28-39, registered for this type at :17) and that calls
    /// <c>Serialize</c> → <c>SerializeRunBasedValues</c>. Reading a value-type property *copies the
    /// struct*: the argument Photon boxes, the local the serializer unboxes into, and therefore the
    /// <c>ref __instance</c> this Prefix writes through, are all a temporary copy. The two nulls in
    /// the field behind the property are untouched.
    ///
    /// Consequence: the underlying fault is not healed, it is bypassed once per serialization. Every
    /// send allocates two fresh dictionaries and hits <c>RunBasedValues.runBasedInts-null</c> again.
    /// A high hit count on that reason therefore means "the same latent fault was worked around N
    /// times", not "the fault recurred N times" - it is expected to grow steadily for as long as a
    /// session runs with an unallocated <c>runBasedValueData</c>, and the 30s aggregation is the only
    /// thing keeping it out of the log. Do not read it as an escalating problem.
    ///
    /// This is not a defect of the patch. Preventing the throw is the whole job, and it succeeds: a
    /// copy is exactly what the serializer serializes.
    ///
    /// BEHAVIOUR DIFFERENCE - not "identical except for the exception". Vanilla throws out of
    /// <c>SerializeRunBasedValues</c>, which propagates through <c>Serialize</c> and
    /// <c>SerializeAchievementProgress</c>, so the RPC never leaves: no client receives anything.
    /// With the guard the RPC is sent with <c>count = 0</c> for both dictionaries, and the receiving
    /// side's <c>Deserialize</c> calls <c>Allocate()</c> before
    /// <c>DeserializeRunBasedValues</c> (SerializableRunBasedValues.cs:70-71), which clears and then
    /// reads zero entries (:97-115) - so every receiving client, the sender included because the RPC
    /// goes to <c>RpcTarget.All</c> as of game 2.6.b, ends up with allocated-but-empty
    /// <c>runBasedInts</c>/<c>runBasedFloats</c>, and <c>ReconnectHandler.UpdateAchievementProgress</c>
    /// (ReconnectHandler.cs:143-151) stores that record. The change is "RPC fails, nothing is
    /// recorded" → "RPC succeeds, this group of values is recorded as empty".
    ///
    /// That is a strict improvement, because the vanilla outcome loses the *whole* payload: the same
    /// <c>Serialize</c> also writes runBasedFruitsEaten / shroomBerriesEaten /
    /// gourmandRequirementsEaten / achievementsEarnedThisRun (:59-66), and those lists have their own
    /// null tolerance built in (:117-152). Vanilla throws before any of them are written; the guard
    /// costs only the two dictionaries that were null anyway - a struct with null dictionaries has no
    /// counter values to lose - and delivers the four lists. It is still a behavioural difference and
    /// is recorded here as one.
    ///
    /// WHY NO MVID GATE. This is the only patch in the plugin registered without
    /// <c>requireCurrentMvid</c> in Patches\Stability\StabilityPatchModule.cs, and the only Stability
    /// patch that writes game state. The asymmetry against <c>ParachutePatch</c> holds up: that one is
    /// a full-replacement Prefix returning bool, so matching member shapes are no proof that the
    /// replacement is still equivalent to a body it does not read. This Prefix reproduces nothing. Its
    /// correctness rests on exactly three facts, and <see cref="Initialize"/> asserts all three
    /// against the running build: the declaring type is still a non-enum value type; both fields still
    /// exist with exactly <c>Dictionary&lt;RUNBASEDVALUETYPE, int&gt;</c> and
    /// <c>Dictionary&lt;RUNBASEDVALUETYPE, float&gt;</c> and are not readonly; and
    /// <c>runBasedValueData</c> is still a property of that struct type, which is what makes the
    /// paragraph above true. Plus <see cref="ResolveAndValidateTarget"/> pins the target signature.
    /// A build that changed any of them fails closed on structural validation, so the certified-MVID
    /// requirement would add nothing this patch does not already prove for itself.
    /// </summary>
    internal static class RunBasedValuesPatch
    {
        private static AccessTools.StructFieldRef<SerializableRunBasedValues,
            Dictionary<RUNBASEDVALUETYPE, int>> _ints;
        private static AccessTools.StructFieldRef<SerializableRunBasedValues,
            Dictionary<RUNBASEDVALUETYPE, float>> _floats;

        internal static void Initialize()
        {
            Type owner = typeof(SerializableRunBasedValues);
            if (!owner.IsValueType || owner.IsEnum)
                throw new InvalidOperationException(
                    "SerializableRunBasedValues is no longer a non-enum value type");

            // Load-bearing for the docstring, not for the write itself: the "the repair does not
            // persist" reasoning holds only because the hot path reads this struct out of a property
            // (AchievementManager.cs:150), which copies it. If a future build turned it into a field,
            // Photon would still box a copy for the RPC argument - but every other consumer that
            // currently gets a snapshot (AchievementManager's own mutators at :559/:589,
            // EndScreen.cs:344/:483/:513, Quicksave's PlayerRunData) would start sharing storage, and
            // the reach of this Prefix's write would no longer be the temporary it was audited as.
            // Failing closed on that reshape is cheaper than discovering it from a bug report.
            StabilityPatchValidation.RequireInstanceProperty(
                typeof(AchievementManager),
                "runBasedValueData",
                owner);

            FieldInfo intsField = StabilityPatchValidation.RequireInstanceField(
                owner,
                "runBasedInts",
                typeof(Dictionary<RUNBASEDVALUETYPE, int>));
            FieldInfo floatsField = StabilityPatchValidation.RequireInstanceField(
                owner,
                "runBasedFloats",
                typeof(Dictionary<RUNBASEDVALUETYPE, float>));
            if (intsField.IsInitOnly || floatsField.IsInitOnly)
                throw new InvalidOperationException(
                    "RunBasedValues dictionary fields are unexpectedly readonly");

            AccessTools.StructFieldRef<SerializableRunBasedValues,
                Dictionary<RUNBASEDVALUETYPE, int>> ints =
                AccessTools.StructFieldRefAccess<SerializableRunBasedValues,
                    Dictionary<RUNBASEDVALUETYPE, int>>(intsField);
            AccessTools.StructFieldRef<SerializableRunBasedValues,
                Dictionary<RUNBASEDVALUETYPE, float>> floats =
                AccessTools.StructFieldRefAccess<SerializableRunBasedValues,
                    Dictionary<RUNBASEDVALUETYPE, float>>(floatsField);
            if (ints == null || floats == null)
                throw new InvalidOperationException(
                    "RunBasedValues StructFieldRef accessor creation failed");

            _ints = ints;
            _floats = floats;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return StabilityPatchValidation.RequireInstanceMethod(
                typeof(SerializableRunBasedValues),
                "SerializeRunBasedValues",
                typeof(void),
                new[] { typeof(BinarySerializer) });
        }

        internal static void Reset()
        {
            _ints = null;
            _floats = null;
        }

        internal static void Prefix(ref SerializableRunBasedValues __instance)
        {
            if (_ints(ref __instance) == null)
            {
                _ints(ref __instance) = new Dictionary<RUNBASEDVALUETYPE, int>();
                RateLimitedDiagnostics.Hit("RunBasedValues.runBasedInts-null");
            }

            if (_floats(ref __instance) == null)
            {
                _floats(ref __instance) = new Dictionary<RUNBASEDVALUETYPE, float>();
                RateLimitedDiagnostics.Hit("RunBasedValues.runBasedFloats-null");
            }
        }
    }
}
