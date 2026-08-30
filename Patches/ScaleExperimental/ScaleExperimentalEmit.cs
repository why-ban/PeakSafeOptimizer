using System;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Accessor factories for audited game members that cannot be bound with plain
    /// <c>typeof(...)</c> at compile time - either because the declaring or element type lives in an
    /// assembly this project does not reference (Unity.Mathematics for ItemPhysicsSyncData.position,
    /// and the enum parameter of Rigidbody.AddForce is resolved by name for the same reason), or
    /// because the member is non-public. UnityEngine.PhysicsModule itself *is* referenced by the
    /// csproj (PeakSafeOptimizer.csproj:44-47), so Rigidbody and Collider are available as types;
    /// the by-name lookups that remain are kept deliberately, because UnityEngine.dll is a
    /// type-forwarding facade and some modules are still unreferenced, and mixing the two styles is
    /// harder to audit than using by-name consistently in this helper.
    /// Everything is emitted once during Initialize so hot paths stay allocation free and never
    /// use MethodInfo.Invoke.
    /// </summary>
    internal static class ScaleExperimentalEmit
    {
        /// <summary>
        /// Binds an audited non-public instance method as a strongly typed delegate so the hot path
        /// never uses MethodInfo.Invoke.
        /// </summary>
        internal static TDelegate CreateDelegate<TDelegate>(MethodInfo method) where TDelegate : Delegate
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            if (method.IsStatic || method.ContainsGenericParameters)
                throw new InvalidOperationException($"Unexpected method structure: {method.Name}");
            return (TDelegate)method.CreateDelegate(typeof(TDelegate));
        }

        internal static Func<TInstance, TValue> CreateFieldGetter<TInstance, TValue>(FieldInfo field)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));
            if (field.IsStatic || field.DeclaringType != typeof(TInstance) || field.FieldType != typeof(TValue))
                throw new InvalidOperationException($"Unexpected field structure: {field.Name}");

            var method = new DynamicMethod(
                "PSO_GetField_" + field.Name,
                typeof(TValue),
                new[] { typeof(TInstance) },
                typeof(ScaleExperimentalEmit).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<TInstance, TValue>)method.CreateDelegate(typeof(Func<TInstance, TValue>));
        }

        /// <summary>
        /// Returns the Transform of the first element of a reference-typed array field, or null when
        /// the array is null or empty. Element type is taken from the FieldInfo rather than named at
        /// compile time (for example UnityEngine.Collider), which keeps this helper usable for array
        /// fields whose element type this project does not reference - see the by-name note on the
        /// type.
        /// </summary>
        internal static Func<TInstance, Transform> CreateFirstElementTransformGetter<TInstance>(FieldInfo arrayField)
        {
            if (arrayField == null) throw new ArgumentNullException(nameof(arrayField));
            if (arrayField.IsStatic || arrayField.DeclaringType != typeof(TInstance))
                throw new InvalidOperationException($"Unexpected field owner for {arrayField.Name}");
            Type fieldType = arrayField.FieldType;
            if (!fieldType.IsArray || fieldType.GetArrayRank() != 1)
                throw new InvalidOperationException($"{arrayField.Name} is not a single-rank array");
            Type elementType = fieldType.GetElementType();
            if (elementType == null || elementType.IsValueType ||
                !typeof(Component).IsAssignableFrom(elementType))
            {
                throw new InvalidOperationException($"{arrayField.Name} is not a Component array");
            }
            PropertyInfo transformProperty = typeof(Component).GetProperty(
                "transform", BindingFlags.Instance | BindingFlags.Public);
            MethodInfo transformGetter = transformProperty?.GetGetMethod(false);
            if (transformProperty == null || transformGetter == null ||
                transformProperty.PropertyType != typeof(Transform))
            {
                throw new InvalidOperationException("Component.transform structure mismatch");
            }

            var method = new DynamicMethod(
                "PSO_FirstTransform_" + arrayField.Name,
                typeof(Transform),
                new[] { typeof(TInstance) },
                typeof(ScaleExperimentalEmit).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            Label noValue = il.DefineLabel();
            LocalBuilder array = il.DeclareLocal(fieldType);
            LocalBuilder element = il.DeclareLocal(elementType);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, arrayField);
            il.Emit(OpCodes.Stloc, array);
            il.Emit(OpCodes.Ldloc, array);
            il.Emit(OpCodes.Brfalse, noValue);
            il.Emit(OpCodes.Ldloc, array);
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Brfalse, noValue);
            il.Emit(OpCodes.Ldloc, array);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldelem_Ref);
            il.Emit(OpCodes.Stloc, element);
            il.Emit(OpCodes.Ldloc, element);
            il.Emit(OpCodes.Brfalse, noValue);
            il.Emit(OpCodes.Ldloc, element);
            il.Emit(OpCodes.Callvirt, transformGetter);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(noValue);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
            return (Func<TInstance, Transform>)method.CreateDelegate(typeof(Func<TInstance, Transform>));
        }

        /// <summary>
        /// Reads a Vector3-convertible field from a value type, applying an implicit conversion
        /// operator when the field type is not Vector3 itself. Used for
        /// ItemPhysicsSyncData.position, whose declared type lives in Unity.Mathematics which this
        /// project does not reference.
        /// </summary>
        internal static Func<TStruct, Vector3> CreateStructVector3FieldGetter<TStruct>(FieldInfo field)
            where TStruct : struct
        {
            if (field == null) throw new ArgumentNullException(nameof(field));
            if (field.IsStatic || field.DeclaringType != typeof(TStruct))
                throw new InvalidOperationException($"Unexpected field owner for {field.Name}");

            MethodInfo conversion = null;
            if (field.FieldType != typeof(Vector3))
            {
                MethodInfo[] operators = field.FieldType.GetMethods(BindingFlags.Static | BindingFlags.Public);
                for (int i = 0; i < operators.Length; i++)
                {
                    MethodInfo candidate = operators[i];
                    if (candidate.Name != "op_Implicit" || candidate.ReturnType != typeof(Vector3))
                        continue;
                    ParameterInfo[] parameters = candidate.GetParameters();
                    if (parameters.Length != 1 || parameters[0].ParameterType != field.FieldType)
                        continue;
                    if (conversion != null)
                        throw new InvalidOperationException(
                            $"Ambiguous Vector3 conversion for {field.FieldType.FullName}");
                    conversion = candidate;
                }
                if (conversion == null)
                    throw new InvalidOperationException(
                        $"{field.FieldType.FullName} has no implicit Vector3 conversion");
            }

            var method = new DynamicMethod(
                "PSO_GetStructV3_" + field.Name,
                typeof(Vector3),
                new[] { typeof(TStruct) },
                typeof(ScaleExperimentalEmit).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarga_S, (byte)0);
            il.Emit(OpCodes.Ldfld, field);
            if (conversion != null)
                il.Emit(OpCodes.Call, conversion);
            il.Emit(OpCodes.Ret);
            return (Func<TStruct, Vector3>)method.CreateDelegate(typeof(Func<TStruct, Vector3>));
        }

        internal static Func<TStruct, Quaternion> CreateStructQuaternionFieldGetter<TStruct>(FieldInfo field)
            where TStruct : struct
        {
            if (field == null) throw new ArgumentNullException(nameof(field));
            if (field.IsStatic || field.DeclaringType != typeof(TStruct) ||
                field.FieldType != typeof(Quaternion))
            {
                throw new InvalidOperationException($"Unexpected field structure: {field.Name}");
            }

            var method = new DynamicMethod(
                "PSO_GetStructQ_" + field.Name,
                typeof(Quaternion),
                new[] { typeof(TStruct) },
                typeof(ScaleExperimentalEmit).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarga_S, (byte)0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<TStruct, Quaternion>)method.CreateDelegate(typeof(Func<TStruct, Quaternion>));
        }

        internal static Func<UnityEngine.Object, Vector3> CreateVector3Getter(
            Type declaringType,
            PropertyInfo property)
        {
            MethodInfo getter = RequireGetter(declaringType, property, typeof(Vector3));
            var method = new DynamicMethod(
                "PSO_GetV3_" + property.Name,
                typeof(Vector3),
                new[] { typeof(UnityEngine.Object) },
                typeof(ScaleExperimentalEmit).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, declaringType);
            il.Emit(OpCodes.Callvirt, getter);
            il.Emit(OpCodes.Ret);
            return (Func<UnityEngine.Object, Vector3>)method.CreateDelegate(
                typeof(Func<UnityEngine.Object, Vector3>));
        }

        internal static Func<UnityEngine.Object, Quaternion> CreateQuaternionGetter(
            Type declaringType,
            PropertyInfo property)
        {
            MethodInfo getter = RequireGetter(declaringType, property, typeof(Quaternion));
            var method = new DynamicMethod(
                "PSO_GetQ_" + property.Name,
                typeof(Quaternion),
                new[] { typeof(UnityEngine.Object) },
                typeof(ScaleExperimentalEmit).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, declaringType);
            il.Emit(OpCodes.Callvirt, getter);
            il.Emit(OpCodes.Ret);
            return (Func<UnityEngine.Object, Quaternion>)method.CreateDelegate(
                typeof(Func<UnityEngine.Object, Quaternion>));
        }

        private static MethodInfo RequireGetter(Type declaringType, PropertyInfo property, Type expected)
        {
            if (declaringType == null) throw new ArgumentNullException(nameof(declaringType));
            if (property == null) throw new ArgumentNullException(nameof(property));
            if (property.DeclaringType != declaringType || property.PropertyType != expected ||
                property.GetIndexParameters().Length != 0)
            {
                throw new InvalidOperationException($"Unexpected property structure: {property.Name}");
            }
            MethodInfo getter = property.GetGetMethod(true);
            if (getter == null || getter.IsStatic)
                throw new InvalidOperationException($"{property.Name} getter is unavailable");
            if (!typeof(UnityEngine.Object).IsAssignableFrom(declaringType))
                throw new InvalidOperationException($"{declaringType.FullName} is not a UnityEngine.Object");
            return getter;
        }

        /// <summary>
        /// Emits a call to an instance method taking (Vector3, intBackedEnum), resolving both the
        /// declaring type and the enum by name rather than with typeof - see the by-name note on the
        /// type. The single caller is Rigidbody.AddForce(Vector3, ForceMode).
        /// </summary>
        internal static Action<UnityEngine.Object, Vector3, int> CreateVector3EnumAction(
            Type declaringType, MethodInfo target)
        {
            if (declaringType == null) throw new ArgumentNullException(nameof(declaringType));
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (target.DeclaringType != declaringType || target.IsStatic ||
                target.ReturnType != typeof(void) || target.ContainsGenericParameters)
            {
                throw new InvalidOperationException($"Unexpected method structure: {target.Name}");
            }
            ParameterInfo[] parameters = target.GetParameters();
            if (parameters.Length != 2 || parameters[0].ParameterType != typeof(Vector3) ||
                !parameters[1].ParameterType.IsEnum ||
                Enum.GetUnderlyingType(parameters[1].ParameterType) != typeof(int))
            {
                throw new InvalidOperationException($"Unexpected parameter structure: {target.Name}");
            }
            if (!typeof(UnityEngine.Object).IsAssignableFrom(declaringType))
                throw new InvalidOperationException($"{declaringType.FullName} is not a UnityEngine.Object");

            var method = new DynamicMethod(
                "PSO_CallV3Enum_" + target.Name,
                typeof(void),
                new[] { typeof(UnityEngine.Object), typeof(Vector3), typeof(int) },
                typeof(ScaleExperimentalEmit).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, declaringType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Callvirt, target);
            il.Emit(OpCodes.Ret);
            return (Action<UnityEngine.Object, Vector3, int>)method.CreateDelegate(
                typeof(Action<UnityEngine.Object, Vector3, int>));
        }
    }
}
