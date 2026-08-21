using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using PeakSafeOptimizer.Patches.Scale;

namespace PeakSafeOptimizer.Patches.Stability
{
    /// <summary>
    /// State guard for BugleSFX.Update() (audited source BugleSFX.cs:107-133).
    ///
    /// Root cause this fixes (confirmed by in-game logs, 654 occurrences in one session):
    /// the owner picks a clip index with Random.Range(0, bugle.Length) (BugleSFX.cs:58) and
    /// broadcasts it, but RPC_StartToot (BugleSFX.cs:72-76) assigns currentClip without any range
    /// check. When the sender has a custom bugle-song mod that extends its own bugle array and the
    /// receiver does not, the received index is out of range on the receiver. Update() then throws
    /// at `buglePlayer.clip = bugle[currentClip]` (BugleSFX.cs:112) *before* setting t = true, so
    /// the same block is retried and throws again on every single frame.
    ///
    /// This is not caused by this plugin: it is vanilla code missing a bounds check, combined with
    /// mod lists differing between clients.
    ///
    /// This Prefix always returns true. It never reproduces any part of the original method; it only
    /// repairs the state the original is about to read, so the original runs normally afterwards.
    /// </summary>
    internal static class BugleClipGuardPatch
    {
        private const string AudioClipArrayTypeName = "UnityEngine.AudioClip[]";
        private const string AudioSourceTypeName = "UnityEngine.AudioSource";

        private static AccessTools.FieldRef<BugleSFX, bool> _hold;
        private static AccessTools.FieldRef<BugleSFX, bool> _isProp;
        private static AccessTools.FieldRef<BugleSFX, bool> _t;
        private static AccessTools.FieldRef<BugleSFX, int> _currentClip;
        private static Func<BugleSFX, int> _bugleLength;
        private static Func<BugleSFX, UnityEngine.Object> _buglePlayer;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo holdField = StabilityPatchValidation.RequireInstanceField(
                typeof(BugleSFX), "hold", typeof(bool));
            FieldInfo isPropField = StabilityPatchValidation.RequireInstanceField(
                typeof(BugleSFX), "isProp", typeof(bool));
            FieldInfo tField = StabilityPatchValidation.RequireInstanceField(
                typeof(BugleSFX), "t", typeof(bool));
            FieldInfo currentClipField = StabilityPatchValidation.RequireInstanceField(
                typeof(BugleSFX), "currentClip", typeof(int));

            // The audited visibility matters: hold/isProp are public, t/currentClip are private.
            // A visibility flip would mean the class was reshaped, so fail closed.
            if (!holdField.IsPublic || !isPropField.IsPublic)
                throw new InvalidOperationException("BugleSFX.hold/isProp are no longer public instance fields");
            if (tField.IsPublic || currentClipField.IsPublic)
                throw new InvalidOperationException("BugleSFX.t/currentClip are no longer private instance fields");

            // AudioClip[] and AudioSource live in UnityEngine.AudioModule, which this project
            // intentionally does not reference, so these two are validated by type name.
            FieldInfo bugleField = RequireInstanceFieldByTypeName(
                typeof(BugleSFX), "bugle", AudioClipArrayTypeName, requirePublic: true);
            FieldInfo buglePlayerField = RequireInstanceFieldByTypeName(
                typeof(BugleSFX), "buglePlayer", AudioSourceTypeName, requirePublic: true);

            AccessTools.FieldRef<BugleSFX, bool> hold =
                AccessTools.FieldRefAccess<BugleSFX, bool>(holdField);
            AccessTools.FieldRef<BugleSFX, bool> isProp =
                AccessTools.FieldRefAccess<BugleSFX, bool>(isPropField);
            AccessTools.FieldRef<BugleSFX, bool> t =
                AccessTools.FieldRefAccess<BugleSFX, bool>(tField);
            AccessTools.FieldRef<BugleSFX, int> currentClip =
                AccessTools.FieldRefAccess<BugleSFX, int>(currentClipField);
            Func<BugleSFX, int> bugleLength = CreateArrayLengthGetter<BugleSFX>(bugleField);
            Func<BugleSFX, UnityEngine.Object> buglePlayer =
                ScaleEmit.CreateUnityObjectFieldGetter<BugleSFX>(buglePlayerField);
            if (hold == null || isProp == null || t == null || currentClip == null ||
                bugleLength == null || buglePlayer == null)
            {
                throw new InvalidOperationException("BugleClipGuard accessor creation failed");
            }

            _hold = hold;
            _isProp = isProp;
            _t = t;
            _currentClip = currentClip;
            _bugleLength = bugleLength;
            _buglePlayer = buglePlayer;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return StabilityPatchValidation.RequireInstanceMethod(
                typeof(BugleSFX), "Update", typeof(void), Type.EmptyTypes, requirePublic: false);
        }

        internal static void Reset()
        {
            _hold = null;
            _isProp = null;
            _t = null;
            _currentClip = null;
            _bugleLength = null;
            _buglePlayer = null;
        }

        internal static bool Prefix(BugleSFX __instance)
        {
            AccessTools.FieldRef<BugleSFX, bool> holdRef = _hold;
            AccessTools.FieldRef<BugleSFX, bool> isPropRef = _isProp;
            AccessTools.FieldRef<BugleSFX, bool> tRef = _t;
            AccessTools.FieldRef<BugleSFX, int> currentClipRef = _currentClip;
            Func<BugleSFX, int> bugleLengthRef = _bugleLength;
            Func<BugleSFX, UnityEngine.Object> buglePlayerRef = _buglePlayer;

            // 1. Accessors unavailable: nothing can be repaired safely.
            if (holdRef == null || isPropRef == null || tRef == null || currentClipRef == null ||
                bugleLengthRef == null || buglePlayerRef == null)
            {
                RateLimitedDiagnostics.Hit("BugleClipGuard.accessors-unavailable");
                return true;
            }

            // 2. The original only indexes bugle inside `if (hold && !t && !isProp)`.
            //    Outside that condition there is nothing to guard.
            if (!holdRef(__instance) || isPropRef(__instance))
                return true;

            // 3. Missing AudioSource is a different fault; do not mask it.
            if (buglePlayerRef(__instance) == null)
            {
                RateLimitedDiagnostics.Hit("BugleClipGuard.buglePlayer-null");
                return true;
            }

            int length = bugleLengthRef(__instance);

            // 4. No clip array at all: make the original skip the clip-assignment block by marking
            //    it as already started. BugleSFX.cs:129-132 resets t once hold goes false, so this
            //    cannot latch permanently.
            if (length <= 0)
            {
                if (!tRef(__instance))
                {
                    tRef(__instance) = true;
                    RateLimitedDiagnostics.Hit("BugleClipGuard.bugle-array-empty");
                }
                return true;
            }

            // 5. Index repair. A negative index and an over-range index are different faults and get
            //    different diagnostic reasons. Over-range is wrapped with modulo so a remote index
            //    from a longer, modded array still selects a real local clip instead of silencing it.
            int clip = currentClipRef(__instance);
            if (clip < 0)
            {
                currentClipRef(__instance) = 0;
                RateLimitedDiagnostics.Hit("BugleClipGuard.currentClip-negative");
                return true;
            }
            if (clip >= length)
            {
                currentClipRef(__instance) = clip % length;
                RateLimitedDiagnostics.Hit("BugleClipGuard.currentClip-out-of-range");
                return true;
            }

            // 6. Already valid: no write, no diagnostics.
            return true;
        }

        /// <summary>
        /// Emits a length reader for a single-rank reference-typed array field whose element type is
        /// not referenced at compile time. Returns -1 when the array field itself is null so callers
        /// can treat "missing" and "empty" identically without a second accessor.
        /// </summary>
        private static Func<TInstance, int> CreateArrayLengthGetter<TInstance>(FieldInfo arrayField)
        {
            if (arrayField == null) throw new ArgumentNullException(nameof(arrayField));
            if (arrayField.IsStatic || arrayField.DeclaringType != typeof(TInstance))
                throw new InvalidOperationException($"Unexpected field owner for {arrayField.Name}");
            Type fieldType = arrayField.FieldType;
            if (!fieldType.IsArray || fieldType.GetArrayRank() != 1)
                throw new InvalidOperationException($"{arrayField.Name} is not a single-rank array");

            var method = new DynamicMethod(
                "PSO_ArrayLength_" + arrayField.Name,
                typeof(int),
                new[] { typeof(TInstance) },
                typeof(BugleClipGuardPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            Label missing = il.DefineLabel();
            LocalBuilder array = il.DeclareLocal(fieldType);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, arrayField);
            il.Emit(OpCodes.Stloc, array);
            il.Emit(OpCodes.Ldloc, array);
            il.Emit(OpCodes.Brfalse, missing);
            il.Emit(OpCodes.Ldloc, array);
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(missing);
            il.Emit(OpCodes.Ldc_I4_M1);
            il.Emit(OpCodes.Ret);
            return (Func<TInstance, int>)method.CreateDelegate(typeof(Func<TInstance, int>));
        }

        private static FieldInfo RequireInstanceFieldByTypeName(
            Type declaringType,
            string name,
            string fieldTypeFullName,
            bool requirePublic)
        {
            FieldInfo field = declaringType.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field == null)
                throw new MissingFieldException(declaringType.FullName, name);
            if (field.DeclaringType != declaringType || field.IsStatic ||
                field.IsPublic != requirePublic ||
                !string.Equals(field.FieldType.FullName, fieldTypeFullName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unexpected field structure for {declaringType.FullName}.{name}");
            }
            return field;
        }
    }
}
