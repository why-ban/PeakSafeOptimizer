using System;
using System.Reflection;
using PeakSafeOptimizer.Diagnostics;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Redundant-write filter for Item.UpdateCollisionDetectionMode() (audited source
    /// Item.cs:537-547, called every Update from Item.cs:534). The target value is still derived
    /// solely from itemState exactly as the original does:
    /// Ground -> ContinuousDynamic, everything else -> Discrete.
    /// Only the assignment is skipped when the Rigidbody already holds that value.
    ///
    /// UnityEngine.PhysicsModule is deliberately not referenced by this project, so the Rigidbody
    /// field and its collisionDetectionMode property are reached through accessors emitted once
    /// during Initialize; the enum is handled as its int backing value.
    /// </summary>
    internal static class ItemCollisionModePatch
    {
        private const string RigidbodyTypeName = "UnityEngine.Rigidbody";
        private const string CollisionDetectionModeTypeName = "UnityEngine.CollisionDetectionMode";
        private const string ContinuousDynamicName = "ContinuousDynamic";
        private const string DiscreteName = "Discrete";

        private static Func<Item, UnityEngine.Object> _getRig;
        private static Func<Item, int> _getMode;
        private static Action<Item, int> _setMode;
        private static int _continuousDynamic;
        private static int _discrete;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo rigField = ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(Item), "rig", RigidbodyTypeName);
            ScaleValidation.RequireInstanceProperty(typeof(Item), "itemState", typeof(ItemState));
            if (!Enum.IsDefined(typeof(ItemState), ItemState.Ground))
                throw new InvalidOperationException("ItemState.Ground is no longer defined");

            PropertyInfo modeProperty = ScaleValidation.RequireInstancePropertyByTypeName(
                rigField.FieldType, "collisionDetectionMode", CollisionDetectionModeTypeName, requireSetter: true);

            Type modeType = modeProperty.PropertyType;
            if (!modeType.IsEnum || Enum.GetUnderlyingType(modeType) != typeof(int))
                throw new InvalidOperationException("CollisionDetectionMode is not an int-backed enum");
            if (!Enum.IsDefined(modeType, ContinuousDynamicName) || !Enum.IsDefined(modeType, DiscreteName))
                throw new InvalidOperationException("CollisionDetectionMode values are no longer defined");

            _continuousDynamic = Convert.ToInt32(Enum.Parse(modeType, ContinuousDynamicName));
            _discrete = Convert.ToInt32(Enum.Parse(modeType, DiscreteName));
            if (_continuousDynamic == _discrete)
                throw new InvalidOperationException("CollisionDetectionMode values are ambiguous");

            Func<Item, UnityEngine.Object> getRig =
                ScaleEmit.CreateUnityObjectFieldGetter<Item>(rigField);
            Func<Item, int> getMode =
                ScaleEmit.CreateEnumPropertyGetterViaField<Item>(rigField, modeProperty);
            Action<Item, int> setMode =
                ScaleEmit.CreateEnumPropertySetterViaField<Item>(rigField, modeProperty);
            if (getRig == null || getMode == null || setMode == null)
                throw new InvalidOperationException("ItemCollisionMode accessor creation failed");

            _getRig = getRig;
            _getMode = getMode;
            _setMode = setMode;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(Item), "UpdateCollisionDetectionMode", typeof(void), Type.EmptyTypes, false);
        }

        internal static void Reset()
        {
            _getRig = null;
            _getMode = null;
            _setMode = null;
            _continuousDynamic = 0;
            _discrete = 0;
        }

        internal static bool Prefix(Item __instance)
        {
            Func<Item, UnityEngine.Object> getRig = _getRig;
            Func<Item, int> getMode = _getMode;
            Action<Item, int> setMode = _setMode;
            if (getRig == null || getMode == null || setMode == null)
            {
                RateLimitedDiagnostics.Hit("ItemCollisionMode.accessors-unavailable");
                return true;
            }

            if (getRig(__instance) == null)
            {
                // Includes destroyed Rigidbody: let the original method run and surface the same
                // behavior the game would have on its own.
                RateLimitedDiagnostics.Hit("ItemCollisionMode.rig-null");
                return true;
            }

            int target = __instance.itemState == ItemState.Ground ? _continuousDynamic : _discrete;
            if (getMode(__instance) == target)
                return false;

            setMode(__instance, target);
            return false;
        }
    }
}
