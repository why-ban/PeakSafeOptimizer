using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Config;

namespace PeakSafeOptimizer.Infrastructure
{
    /// <summary>
    /// How much of its own validation a patch must pass before it may be installed.
    ///
    /// Structural patches can independently validate an unknown build. Patches that replace audited
    /// behavior additionally require the audited build MVID until they have a dedicated method-level
    /// behavioral fingerprint. This is per patch, never a global all-or-nothing gate.
    ///
    /// Ordered by strictness: Structural &lt; Fingerprint &lt; StrictIl.
    /// </summary>
    public enum CompatibilityLevel
    {
        /// <summary>
        /// Structural validation is sufficient to run on a build with an unknown MVID: simple
        /// FieldRef/accessor patches, null guards and return-value Prefixes whose behaviour only
        /// depends on member shapes that Initialize verifies.
        /// </summary>
        Structural,

        /// <summary>
        /// Structural validation plus a certified audited build. Used by full-replacement Prefixes
        /// (Prefix returns bool and can skip the original), where matching member shapes are not by
        /// themselves proof that the replacement is still equivalent.
        /// </summary>
        Fingerprint,

        /// <summary>
        /// Requires the certified build plus a unique IL match, label/exception-block safety and
        /// post-generation validation where the patch supplies an IL matcher. Used by Transpilers
        /// and network-facing rewrites.
        /// </summary>
        StrictIl
    }

    public sealed class PatchDefinition
    {
        /// <summary>
        /// Backwards-compatible overload kept for existing patch modules. <c>requireCurrentMvid</c>
        /// is a per-patch audited-build requirement and also infers a <see cref="CompatibilityLevel"/>:
        /// a Transpiler maps to <see cref="CompatibilityLevel.StrictIl"/>, a legacy
        /// <c>requireCurrentMvid: true</c> (always a full-replacement Prefix in this codebase) maps to
        /// <see cref="CompatibilityLevel.Fingerprint"/>, everything else to
        /// <see cref="CompatibilityLevel.Structural"/>.
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
        /// Preferred constructor. <paramref name="compatibility"/> is mandatory so that this overload
        /// can never be ambiguous with the legacy one, and <paramref name="reset"/> is invoked by
        /// <see cref="PatchInstaller"/> only after every Harmony method of this patch has been
        /// removed successfully.
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
        /// True when this patch replaces audited behavior and lacks an independent behavioral
        /// fingerprint suitable for an unknown game build.
        /// </summary>
        public bool RequiresCertifiedBuild { get; }

        /// <summary>
        /// Optional cleanup for the patch's cached accessors. Called by the installer after a fully
        /// successful uninstall only, never while any of this patch's Harmony methods are still live.
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
