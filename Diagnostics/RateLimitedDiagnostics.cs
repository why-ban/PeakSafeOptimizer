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
            // Two steady states. With DetailedLogging on - which is the default (OptimizerConfig.cs:370
            // binds it to true) and what the test machines run - every hit past the first also pays a
            // DateTime.UtcNow read, an Interlocked.Read and a subtraction before deciding to say
            // nothing; only the 30 s winner formats or allocates. With it off, a hit is a dictionary
            // lookup plus an Interlocked.Increment and nothing else. Either way GetOrAdd's factory is a
            // non-capturing lambda, which Roslyn caches in a static field, so it allocates no closure
            // and no delegate per call - do not turn it into a capturing one.
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
