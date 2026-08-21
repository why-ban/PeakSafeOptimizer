using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace PeakSafeOptimizer.Infrastructure
{
    public sealed class LifecycleRegistry : IDisposable
    {
        private readonly List<Action> _cleanup = new List<Action>();
        private readonly ManualLogSource _log;
        public LifecycleRegistry(ManualLogSource log) => _log = log;
        public void Register(Action cleanup)
        {
            if (cleanup == null) throw new ArgumentNullException(nameof(cleanup));
            _cleanup.Add(cleanup);
        }
        public void Dispose()
        {
            for (int i = _cleanup.Count - 1; i >= 0; i--)
            {
                try { _cleanup[i](); }
                catch (Exception ex) { _log?.LogError($"Lifecycle cleanup failed safely: {ex}"); }
            }
            _cleanup.Clear();
        }
    }
}
