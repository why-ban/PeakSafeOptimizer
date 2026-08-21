using System.Collections.Generic;
using PeakSafeOptimizer.Config;

namespace PeakSafeOptimizer.Infrastructure
{
    /// <summary>
    /// Default compatibility levels for patches that were registered through the legacy
    /// <c>requireCurrentMvid</c> constructor overload and therefore cannot state their own level yet.
    ///
    /// Inference rules applied by <see cref="PatchDefinition"/>:
    ///   a Transpiler                  → <see cref="CompatibilityLevel.StrictIl"/>
    ///   legacy requireCurrentMvid=true→ <see cref="CompatibilityLevel.Fingerprint"/> (full-replacement Prefix)
    ///   otherwise                     → <see cref="CompatibilityLevel.Structural"/>
    /// and then, on top of that, the network-facing set below is escalated to StrictIl, because the
    /// correctness of those replacements depends on Photon ownership / RPC semantics rather than on
    /// member shapes alone.
    /// </summary>
    internal static class PatchCompatibilityPolicy
    {
        private static readonly HashSet<PatchId> NetworkFacing = new HashSet<PatchId>
        {
            // Reads CharacterAfflictions/photonView.IsMine to decide whose affliction scan it replaces.
            PatchId.HeatEmissionScan,
            // Must never suppress the owner-side RPC_SyncScale branch.
            PatchId.ItemScaleRedundantWrite,
            // Replaces remote interpolation on a PhotonBinaryStreamSerializer subclass.
            PatchId.RemoteItemInterpolationThreshold,
            // Patch target is the RefreshAllCharacterCarryWeightRPC entry point itself.
            PatchId.WeightRefreshCoalescing
        };

        internal static CompatibilityLevel Escalate(PatchId id, CompatibilityLevel inferred) =>
            NetworkFacing.Contains(id) && inferred < CompatibilityLevel.StrictIl
                ? CompatibilityLevel.StrictIl
                : inferred;
    }
}
