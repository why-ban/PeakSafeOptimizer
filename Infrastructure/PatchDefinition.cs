using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Config;

namespace PeakSafeOptimizer.Infrastructure
{
    /// <summary>
    /// How much of a patch's correctness rests on something structural validation cannot see. This is
    /// a reported risk class, not an install gate: what decides whether a patch installs is its own
    /// Initialize/ResolveTarget assertions. The level tells a reader of the startup log how much to
    /// distrust a patch that installed successfully on a build nobody has re-audited.
    ///
    /// Ordered by how much re-auditing a new game build deserves: Structural &lt; Fingerprint &lt; StrictIl.
    /// </summary>
    public enum CompatibilityLevel
    {
        /// <summary>
        /// Everything the patch depends on is a member shape that Initialize verifies: simple
        /// FieldRef/accessor patches and null guards. Structural validation is genuinely sufficient
        /// here, so an unknown build needs no special attention for these.
        /// </summary>
        Structural,

        /// <summary>
        /// Full-replacement Prefixes (Prefix returns bool and can skip the original). Matching member
        /// shapes are not by themselves proof that the replacement is still equivalent, so these are
        /// the patches to re-read against a fresh decompile after a game update.
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
        /// Overload used by every patch module. <c>requireCurrentMvid</c> no longer gates anything -
        /// it only classifies: a Transpiler maps to <see cref="CompatibilityLevel.StrictIl"/>, true maps
        /// to <see cref="CompatibilityLevel.Fingerprint"/> (a full replacement, worth re-reading after a
        /// game update), false to <see cref="CompatibilityLevel.Structural"/>. The name is kept so that
        /// forty call sites do not have to change; <see cref="RequiresCertifiedBuild"/> documents why it
        /// no longer means what it says.
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
        /// True when this patch replaces audited behavior. Retained for reporting and for any future
        /// method-level behavioral fingerprint; it is deliberately *not* consulted by the installer.
        /// It used to short-circuit installation on any build whose MVID was not the audited one, and
        /// since nearly every module set it, one game update disabled nearly the whole plugin without
        /// a single patch getting to check whether the members it reads had actually changed. See the
        /// comment at the corresponding point in PatchInstaller.InstallOne.
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
