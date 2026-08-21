using System;
using System.Reflection;
using System.Reflection.Emit;
using PeakSafeOptimizer.Diagnostics;
using PeakSafeOptimizer.Patches.Scale;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Opt-in: downgrades joint projection for every ragdoll joint created by
    /// RigCreator.SpawnJoint(Rigidbody, Rigidbody, float) (audited source RigCreator.cs:168-192).
    /// The original ends with projectionMode = JointProjectionMode.PositionAndRotation
    /// (RigCreator.cs:189); this Postfix rewrites that single field to
    /// JointProjectionMode.PositionOnly.
    ///
    /// Cost model: projection is a per-solver-iteration correction that PhysX applies to every
    /// joint of every character ragdoll. PositionOnly keeps the anchor-separation correction, which
    /// is what prevents limbs from visibly detaching, and drops the rotational correction, which is
    /// the expensive half. The visible difference is limited to slightly softer joint angle limits
    /// under heavy load.
    ///
    /// This is a one-time write at joint creation, so there is no per-frame state, no allocation and
    /// nothing to reset per character. Nothing else about the joint (limits, motions, drives,
    /// connectedBody) is touched, and the original method is always allowed to run first, so the
    /// joint is fully configured before this write happens.
    ///
    /// UnityEngine.PhysicsModule is deliberately not referenced by this project, so
    /// ConfigurableJoint and JointProjectionMode are resolved by full type name during Initialize.
    /// __result is declared as UnityEngine.Object, which Harmony accepts because it is assignable
    /// from the audited ConfigurableJoint return type, and the setter is reached through a delegate
    /// emitted once in Initialize rather than MethodInfo.Invoke.
    ///
    /// Audited caveat, deliberately not worked around here: in this Unity build
    /// Call-graph caveat, audited and important: SpawnJoint has exactly one caller,
    /// RigCreator.AddJointToPart (RigCreator.cs:160), reached only from InitPart / SyncJointFromData
    /// -> GenerateData -> the public RigCreator.AutoGenerate (RigCreator.cs:29), and AutoGenerate has
    /// no caller anywhere in the decompiled game or its bundled assemblies. RigCreator is editor
    /// tooling (its companion RigCreatorJoint destroys itself on Awake outside the editor,
    /// RigCreatorJoint.cs:14-20), so shipped ragdoll joints come from the prefab rather than from
    /// SpawnJoint. This patch is therefore expected to never fire in a normal game session; it
    /// installs and validates cleanly, and it is a no-op unless something (another mod, or a future
    /// game version) starts building rigs at runtime.
    ///
    /// Enum caveat: in this Unity build
    /// JointProjectionMode.PositionOnly carries
    /// [Obsolete("JointProjectionMode.PositionOnly is no longer supported", true)]
    /// (UnityEngine.PhysicsModule\UnityEngine\JointProjectionMode.cs:10-12). The enum value still
    /// exists and the property setter passes it straight to native
    /// (ConfigurableJoint.cs:615-623), so this write is accepted at runtime, but PhysX exposes
    /// projection as a single on/off constraint flag, so "position only" most likely resolves to the
    /// same enabled projection as PositionAndRotation. In other words this patch is expected to be
    /// behaviour-neutral and may deliver no measurable saving; the value that actually disables
    /// projection is JointProjectionMode.None, which is a different and riskier trade (visible joint
    /// separation under load) and is intentionally not chosen here. That the member is Obsolete with
    /// error:true is also a second, independent reason the value cannot be referenced from C# source
    /// and has to be resolved by name.
    /// </summary>
    internal static class JointProjectionPatch
    {
        private const string ConfigurableJointTypeName = "UnityEngine.ConfigurableJoint";
        private const string RigidbodyTypeName = "UnityEngine.Rigidbody";
        private const string JointProjectionModeTypeName = "UnityEngine.JointProjectionMode";
        private const string PositionOnlyName = "PositionOnly";
        private const string PositionAndRotationName = "PositionAndRotation";

        private static Action<UnityEngine.Object, int> _setProjectionMode;
        private static int _positionOnly;

        internal static void Initialize()
        {
            MethodInfo spawnJoint = ResolveAndValidateTarget();
            Type jointType = spawnJoint.ReturnType;
            if (!typeof(UnityEngine.Object).IsAssignableFrom(jointType))
                throw new InvalidOperationException("RigCreator.SpawnJoint does not return a UnityEngine.Object");

            PropertyInfo projectionMode = ScaleValidation.RequireInstancePropertyByTypeName(
                jointType, "projectionMode", JointProjectionModeTypeName, requireSetter: true);

            Type modeType = projectionMode.PropertyType;
            if (!modeType.IsEnum || Enum.GetUnderlyingType(modeType) != typeof(int))
                throw new InvalidOperationException("JointProjectionMode is not an int-backed enum");
            // PositionAndRotation is the value the audited original writes; requiring both proves
            // this build still has the mode pair this patch reasons about.
            if (!Enum.IsDefined(modeType, PositionOnlyName) || !Enum.IsDefined(modeType, PositionAndRotationName))
                throw new InvalidOperationException("JointProjectionMode values are no longer defined");

            int positionOnly = Convert.ToInt32(Enum.Parse(modeType, PositionOnlyName));
            int positionAndRotation = Convert.ToInt32(Enum.Parse(modeType, PositionAndRotationName));
            if (positionOnly == positionAndRotation)
                throw new InvalidOperationException("JointProjectionMode values are ambiguous");

            Action<UnityEngine.Object, int> setter = CreateEnumPropertySetter(jointType, projectionMode);
            if (setter == null)
                throw new InvalidOperationException("JointProjection accessor creation failed");

            _positionOnly = positionOnly;
            _setProjectionMode = setter;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethodByTypeNames(
                typeof(RigCreator),
                "SpawnJoint",
                ConfigurableJointTypeName,
                new[] { RigidbodyTypeName, RigidbodyTypeName, typeof(float).FullName });
        }

        internal static void Reset()
        {
            _setProjectionMode = null;
            _positionOnly = 0;
        }

        internal static void Postfix(UnityEngine.Object __result)
        {
            Action<UnityEngine.Object, int> setter = _setProjectionMode;
            if (setter == null)
            {
                RateLimitedDiagnostics.Hit("JointProjection.accessor-unavailable");
                return;
            }
            // Unity equality also covers a joint that was destroyed inside the original call.
            if (__result == null)
            {
                RateLimitedDiagnostics.Hit("JointProjection.joint-null");
                return;
            }
            setter(__result, _positionOnly);
        }

        /// <summary>
        /// Emits a setter for an int-backed enum property declared on a type this project cannot
        /// reference at compile time. Created once during Initialize.
        /// </summary>
        private static Action<UnityEngine.Object, int> CreateEnumPropertySetter(
            Type declaringType, PropertyInfo property)
        {
            MethodInfo setter = property.GetSetMethod(true);
            if (setter == null || setter.IsStatic || !property.DeclaringType.IsAssignableFrom(declaringType))
                throw new InvalidOperationException($"{property.Name} setter is unavailable");

            var method = new DynamicMethod(
                "PSO_SetEnum_" + property.Name,
                typeof(void),
                new[] { typeof(UnityEngine.Object), typeof(int) },
                typeof(JointProjectionPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, declaringType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, setter);
            il.Emit(OpCodes.Ret);
            return (Action<UnityEngine.Object, int>)method.CreateDelegate(
                typeof(Action<UnityEngine.Object, int>));
        }
    }
}
