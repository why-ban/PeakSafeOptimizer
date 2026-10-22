using System.Collections.Generic;
using PeakSafeOptimizer.Config;

namespace PeakSafeOptimizer.Infrastructure
{
    /// <summary>
    /// Escalates the reported re-audit priority of patches whose correctness rests on Photon ownership
    /// or RPC semantics, which a member-shape check cannot see going wrong. Reported only; nothing here
    /// gates installation.
    /// </summary>
    internal static class PatchCompatibilityPolicy
    {
        private static readonly HashSet<PatchId> NetworkFacing = new HashSet<PatchId>
        {
            PatchId.HeatEmissionScan,
            PatchId.WeightRefreshCoalescing,
        };

        internal static CompatibilityLevel Escalate(PatchId id, CompatibilityLevel inferred) =>
            NetworkFacing.Contains(id) && inferred < CompatibilityLevel.StrictIl
                ? CompatibilityLevel.StrictIl
                : inferred;
    }
}
