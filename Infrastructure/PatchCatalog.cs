using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using PeakSafeOptimizer.Config;

namespace PeakSafeOptimizer.Infrastructure
{
    public interface IPatchModule
    {
        void Register(PatchModuleContext context);
    }

    public sealed class PatchModuleContext
    {
        private readonly Dictionary<PatchId, Func<PatchDefinition>> _factories =
            new Dictionary<PatchId, Func<PatchDefinition>>();
        private readonly List<Action> _cleanup = new List<Action>();

        public void Register(PatchId id, Func<PatchDefinition> factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            if (_factories.ContainsKey(id)) throw new InvalidOperationException($"Module registered {id} more than once.");
            _factories.Add(id, factory);
        }

        public void RegisterCleanup(Action cleanup)
        {
            if (cleanup == null) throw new ArgumentNullException(nameof(cleanup));
            _cleanup.Add(cleanup);
        }

        internal IReadOnlyDictionary<PatchId, Func<PatchDefinition>> Factories => _factories;
        internal void CommitCleanup(LifecycleRegistry lifecycle)
        {
            foreach (Action cleanup in _cleanup) lifecycle.Register(cleanup);
        }
    }

    public sealed class PatchRegistration
    {
        internal PatchRegistration(PatchId id, bool enabled, Func<PatchDefinition> factory)
        {
            Id = id; Enabled = enabled; Factory = factory;
        }
        public PatchId Id { get; }
        public bool Enabled { get; }
        internal Func<PatchDefinition> Factory { get; }
    }

    public sealed class ModuleDiscoveryFailure
    {
        internal ModuleDiscoveryFailure(string module, string reason) { Module = module; Reason = reason; }
        public string Module { get; }
        public string Reason { get; }
    }

    public sealed class PatchCatalogSnapshot
    {
        internal PatchCatalogSnapshot(IReadOnlyList<PatchRegistration> registrations, IReadOnlyList<ModuleDiscoveryFailure> failures)
        {
            Registrations = registrations; Failures = failures;
        }
        public IReadOnlyList<PatchRegistration> Registrations { get; }
        public IReadOnlyList<ModuleDiscoveryFailure> Failures { get; }
    }

    public static class PatchCatalog
    {
        public static PatchCatalogSnapshot Discover(
            Assembly assembly, OptimizerConfig config, LifecycleRegistry lifecycle, ManualLogSource log)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (lifecycle == null) throw new ArgumentNullException(nameof(lifecycle));

            var factories = new Dictionary<PatchId, Func<PatchDefinition>>();
            var failures = new List<ModuleDiscoveryFailure>();
            IEnumerable<Type> types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(type => type != null);
                foreach (Exception loader in ex.LoaderExceptions ?? Array.Empty<Exception>())
                    AddFailure(failures, log, "assembly type discovery", loader);
            }
            catch (Exception ex)
            {
                AddFailure(failures, log, "assembly type discovery", ex);
                types = Array.Empty<Type>();
            }

            foreach (Type type in types
                .Where(type => typeof(IPatchModule).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
                .OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                string moduleName = type.FullName ?? type.Name;
                IPatchModule module;
                try { module = (IPatchModule)Activator.CreateInstance(type, true); }
                catch (Exception ex) { AddFailure(failures, log, moduleName + " construction", Unwrap(ex)); continue; }

                var context = new PatchModuleContext();
                try { module.Register(context); }
                catch (Exception ex) { AddFailure(failures, log, moduleName + " registration", Unwrap(ex)); continue; }

                PatchId duplicate = context.Factories.Keys.FirstOrDefault(id => factories.ContainsKey(id));
                if (context.Factories.Keys.Any(id => factories.ContainsKey(id)))
                {
                    AddFailure(failures, log, moduleName + " registration",
                        new InvalidOperationException($"PatchId {duplicate} was already registered by another module."));
                    continue;
                }
                foreach (KeyValuePair<PatchId, Func<PatchDefinition>> pair in context.Factories)
                    factories.Add(pair.Key, pair.Value);
                context.CommitCleanup(lifecycle);
            }

            var registrations = new List<PatchRegistration>();
            foreach (PatchId id in Enum.GetValues(typeof(PatchId)))
            {
                factories.TryGetValue(id, out Func<PatchDefinition> factory);
                registrations.Add(new PatchRegistration(id, config.IsEnabled(id), factory));
            }
            return new PatchCatalogSnapshot(registrations, failures);
        }

        private static Exception Unwrap(Exception ex) => ex is TargetInvocationException invocation && invocation.InnerException != null
            ? invocation.InnerException : ex;

        private static void AddFailure(List<ModuleDiscoveryFailure> failures, ManualLogSource log, string module, Exception ex)
        {
            string reason = $"{ex.GetType().Name}: {ex.Message}";
            failures.Add(new ModuleDiscoveryFailure(module, reason));
            log?.LogError($"Patch module failure isolated: {module}: {reason}");
        }
    }
}
