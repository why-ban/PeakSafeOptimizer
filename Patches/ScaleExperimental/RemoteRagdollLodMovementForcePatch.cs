using System.Reflection;
using PeakSafeOptimizer.Patches.Scale;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Skips Bodypart.AddMovementForce for a character ShouldDowngrade selects. The original adds an
    /// acceleration along data.worldMovementInput_Lerp, a vector this client simulates from replicated
    /// input, while the authoritative position arrives through CharacterSyncer.InterpolateRigPositions,
    /// which moves the rigs directly and is not patched. Pure force addition, so it only removes energy.
    /// </summary>
    internal static class RemoteRagdollLodMovementForcePatch
    {
        private static bool _acquired;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            ScaleValidation.RequireInstanceField(
                typeof(CharacterData), "worldMovementInput_Lerp", typeof(UnityEngine.Vector3));

            RemoteRagdollLod.Acquire();
            _acquired = true;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart),
                "AddMovementForce",
                typeof(void),
                new[] { typeof(float) },
                requirePublic: false);
        }

        internal static void Reset()
        {
            if (!_acquired)
                return;
            _acquired = false;
            RemoteRagdollLod.Release();
        }

        internal static bool Prefix(Bodypart __instance, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;

            Character owner = RemoteRagdollLod.ResolveOwner(__instance);
            if (owner == null)
                return true;
            return !RemoteRagdollLod.ShouldDowngrade(owner);
        }
    }
}
