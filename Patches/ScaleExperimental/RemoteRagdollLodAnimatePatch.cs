using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Patches.Scale;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Skips Bodypart.Animate for a character ShouldDowngrade selects: FollowRotation_Joint, the
    /// pose-following torque, and the pose-following force with its two reaction forces, all of which only
    /// push the ragdoll towards its animation pose. Animate's kinematic branch is unreachable here because
    /// ToggleKinematic is the only writer of a part's isKinematic and it also sets data.isKinecmatic.
    /// </summary>
    internal static class RemoteRagdollLodAnimatePatch
    {
        private static bool _acquired;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();
            RemoteRagdollLod.Acquire();
            _acquired = true;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart),
                "Animate",
                typeof(void),
                new[] { typeof(float), typeof(float) },
                requirePublic: false);
        }

        internal static void Reset()
        {
            if (!_acquired)
                return;
            _acquired = false;
            RemoteRagdollLod.Release();
        }

        [HarmonyPriority(Priority.High)]
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
