using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Config;

namespace PeakSafeOptimizer.Infrastructure
{
    /// <summary>
    /// How much of a patch's correctness rests on something structural validation cannot see. A
    /// reported risk class, not an install gate: what decides whether a patch installs is its own
    /// Initialize/ResolveTarget assertions. Ordered by how much re-auditing a new game build deserves,
    /// Structural &lt; Fingerprint &lt; StrictIl.
    /// </summary>
    public enum CompatibilityLevel
    {
        /// <summary>Everything the patch depends on is a member shape Initialize verifies.</summary>
        Structural,

        /// <summary>
        /// Full-replacement Prefixes. Matching member shapes do not prove the replacement is still
        /// equivalent, so these are the patches to re-read after a game update.
        /// </summary>
        Fingerprint,

        /// <summary>
        /// Correctness also depends on Photon ownership/RPC semantics or on IL that a shape check
        /// cannot express. Highest re-audit priority after a game update.
        /// </summary>
        StrictIl
    }

    public sealed class PatchDefinition
    {
        /// <summary>
        /// Overload used by every patch module. <paramref name="requireCurrentMvid"/> no longer gates
        /// anything, it only classifies: a Transpiler maps to <see cref="CompatibilityLevel.StrictIl"/>,
        /// true to <see cref="CompatibilityLevel.Fingerprint"/>, false to
        /// <see cref="CompatibilityLevel.Structural"/>. The name is kept so the call sites do not have
        /// to change.
        /// </summary>
        public PatchDefinition(
            PatchId id,
            Action initialize,
            Func<MethodBase> resolveTarget,
            Func<HarmonyMethod> prefix = null,
            Func<HarmonyMethod> postfix = null,
            Func<HarmonyMethod> transpiler = null,
            Func<HarmonyMethod> finalizer = null,
            bool requireCurrentMvid = false,
            Action reset = null)
            : this(
                id,
                initialize,
                resolveTarget,
                InferCompatibility(id, transpiler, requireCurrentMvid),
                prefix,
                postfix,
                transpiler,
                finalizer,
                reset,
                requireCurrentMvid)
        {
        }

        /// <summary>
        /// Preferred constructor. <paramref name="compatibility"/> is mandatory so this overload can
        /// never be ambiguous with the legacy one.
        /// </summary>
        public PatchDefinition(
            PatchId id,
            Action initialize,
            Func<MethodBase> resolveTarget,
            CompatibilityLevel compatibility,
            Func<HarmonyMethod> prefix = null,
            Func<HarmonyMethod> postfix = null,
            Func<HarmonyMethod> transpiler = null,
            Func<HarmonyMethod> finalizer = null,
            Action reset = null,
            bool requiresCertifiedBuild = false)
        {
            Id = id;
            Initialize = initialize ?? (() => { });
            ResolveTarget = resolveTarget ?? throw new ArgumentNullException(nameof(resolveTarget));
            Prefix = prefix;
            Postfix = postfix;
            Transpiler = transpiler;
            Finalizer = finalizer;
            Compatibility = compatibility;
            Reset = reset;
            RequiresCertifiedBuild = requiresCertifiedBuild;
        }

        public PatchId Id { get; }
        public Action Initialize { get; }
        public Func<MethodBase> ResolveTarget { get; }
        public Func<HarmonyMethod> Prefix { get; }
        public Func<HarmonyMethod> Postfix { get; }
        public Func<HarmonyMethod> Transpiler { get; }
        public Func<HarmonyMethod> Finalizer { get; }

        /// <summary>Declared validation/risk classification shown in status reporting.</summary>
        public CompatibilityLevel Compatibility { get; }

        /// <summary>
        /// Reported only, deliberately not consulted by the installer: it used to short-circuit
        /// installation on an unknown build and nearly every module set it. See the comment at the
        /// corresponding point in PatchInstaller.InstallOne.
        /// </summary>
        public bool RequiresCertifiedBuild { get; }

        /// <summary>
        /// Cleanup for the patch's cached accessors. Called after a fully successful uninstall only,
        /// never while any of this patch's Harmony methods are still live.
        /// </summary>
        public Action Reset { get; }

        private static CompatibilityLevel InferCompatibility(
            PatchId id, Func<HarmonyMethod> transpiler, bool requireCurrentMvid)
        {
            CompatibilityLevel inferred = transpiler != null
                ? CompatibilityLevel.StrictIl
                : requireCurrentMvid ? CompatibilityLevel.Fingerprint : CompatibilityLevel.Structural;
            return PatchCompatibilityPolicy.Escalate(id, inferred);
        }
    }
}
