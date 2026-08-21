using System;
using System.Collections.Concurrent;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace PeakSafeOptimizer.Diagnostics
{
    public static class RateLimitedDiagnostics
    {
        private sealed class ReasonState
        {
            internal long Count;
            internal long LastLogTicks;
            internal long LastLoggedCount;
        }

        private static readonly ConcurrentDictionary<string, ReasonState> States =
            new ConcurrentDictionary<string, ReasonState>(StringComparer.Ordinal);
        private static ManualLogSource _log;
        private static ConfigEntry<bool> _detailed;

        public static void Initialize(ManualLogSource log, ConfigEntry<bool> detailed)
        {
            _log = log;
            _detailed = detailed;
        }

        public static void Hit(string reason)
        {
            if (string.IsNullOrEmpty(reason))
                reason = "unspecified";
            ReasonState state = States.GetOrAdd(reason, _ => new ReasonState());
            long count = Interlocked.Increment(ref state.Count);
            if (count == 1)
            {
                // First hit is always reported; only this path needs a timestamp baseline.
                long firstTicks = DateTime.UtcNow.Ticks;
                Interlocked.Exchange(ref state.LastLogTicks, firstTicks);
                Interlocked.Exchange(ref state.LastLoggedCount, 1);
                _log?.LogInfo($"Repair reason '{reason}' first hit");
                return;
            }
            // Steady state with DetailedLogging off: dictionary lookup + Interlocked.Increment only.
            // No DateTime.UtcNow read, no formatting, no allocation.
            if (!(_detailed?.Value ?? false))
                return;

            long now = DateTime.UtcNow.Ticks;
            long previous = Interlocked.Read(ref state.LastLogTicks);
            if (now - previous < TimeSpan.TicksPerSecond * 30 ||
                Interlocked.CompareExchange(ref state.LastLogTicks, now, previous) != previous)
                return;
            long priorCount = Interlocked.Exchange(ref state.LastLoggedCount, count);
            _log?.LogInfo($"Repair reason '{reason}' aggregate: {count - priorCount} new, {count} total (30s minimum interval)");
        }

        public static void Reset()
        {
            States.Clear();
            _log = null;
            _detailed = null;
        }
    }
}
