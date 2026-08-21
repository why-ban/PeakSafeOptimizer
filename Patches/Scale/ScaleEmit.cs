using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Accessor factories for audited game members whose declared types live in assemblies this
    /// plugin intentionally does not reference at compile time (for example
    /// UnityEngine.PhysicsModule) or that are declared internal in Assembly-CSharp.
    ///
    /// Everything is emitted once during Initialize so the hot paths stay allocation free: no
    /// MethodInfo.Invoke, no boxed enum results and no per-call reflection.
    /// </summary>
    internal static class ScaleEmit
    {
        internal static Func<TInstance, UnityEngine.Object> CreateUnityObjectFieldGetter<TInstance>(FieldInfo field)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));
            if (field.IsStatic || field.DeclaringType != typeof(TInstance))
                throw new InvalidOperationException($"Unexpected field owner for {field.Name}");
            if (!typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType))
                throw new InvalidOperationException($"{field.Name} is not a UnityEngine.Object field");

            var method = new DynamicMethod(
                "PSO_Get_" + field.Name,
                typeof(UnityEngine.Object),
                new[] { typeof(TInstance) },
                typeof(ScaleEmit).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<TInstance, UnityEngine.Object>)method.CreateDelegate(
                typeof(Func<TInstance, UnityEngine.Object>));
        }

        /// <summary>
        /// Reads an int-backed enum property through an intermediate reference-typed field.
        /// The caller must have verified that the field value is non-null.
        /// </summary>
        internal static Func<TInstance, int> CreateEnumPropertyGetterViaField<TInstance>(
            FieldInfo field,
            PropertyInfo property)
        {
            MethodInfo getter = RequireAccessor(field, property, wantSetter: false);
            var method = new DynamicMethod(
                "PSO_GetEnum_" + property.Name,
                typeof(int),
                new[] { typeof(TInstance) },
                typeof(ScaleEmit).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Callvirt, getter);
            il.Emit(OpCodes.Ret);
            return (Func<TInstance, int>)method.CreateDelegate(typeof(Func<TInstance, int>));
        }

        internal static Action<TInstance, int> CreateEnumPropertySetterViaField<TInstance>(
            FieldInfo field,
            PropertyInfo property)
        {
            MethodInfo setter = RequireAccessor(field, property, wantSetter: true);
            var method = new DynamicMethod(
                "PSO_SetEnum_" + property.Name,
                typeof(void),
                new[] { typeof(TInstance), typeof(int) },
                typeof(ScaleEmit).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, setter);
            il.Emit(OpCodes.Ret);
            return (Action<TInstance, int>)method.CreateDelegate(typeof(Action<TInstance, int>));
        }

        /// <summary>
        /// Returns the Count of a collection-typed field, or -1 when the field itself is null.
        /// </summary>
        internal static Func<TInstance, int> CreateCollectionCountGetter<TInstance>(FieldInfo field)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));
            if (field.IsStatic || field.DeclaringType != typeof(TInstance))
                throw new InvalidOperationException($"Unexpected field owner for {field.Name}");
            PropertyInfo count = field.FieldType.GetProperty(
                "Count", BindingFlags.Instance | BindingFlags.Public);
            MethodInfo getter = count?.GetGetMethod(false);
            if (count == null || getter == null || count.PropertyType != typeof(int))
                throw new InvalidOperationException($"{field.Name} has no int Count property");

            var method = new DynamicMethod(
                "PSO_Count_" + field.Name,
                typeof(int),
                new[] { typeof(TInstance) },
                typeof(ScaleEmit).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            Label hasValue = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Brtrue_S, hasValue);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldc_I4_M1);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(hasValue);
            il.Emit(OpCodes.Callvirt, getter);
            il.Emit(OpCodes.Ret);
            return (Func<TInstance, int>)method.CreateDelegate(typeof(Func<TInstance, int>));
        }

        /// <summary>
        /// Accumulates a hash of the nested Unity object identity reached through every element of
        /// <paramref name="listField"/>[i] -> <paramref name="elementField"/> -> <paramref name="objectProperty"/>.
        /// Returns the supplied sentinel when the list is null or empty. A null element, a null
        /// intermediate field, or a null resolved object contributes 0 to the hash.
        ///
        /// Used to fold the real applied material identity of every collider into a redundancy
        /// signature without referencing UnityEngine.PhysicsModule at compile time.
        /// </summary>
        internal static Func<TInstance, int, int> CreateAllElementsNestedObjectIdHash<TInstance>(
            FieldInfo listField,
            FieldInfo elementField,
            PropertyInfo objectProperty)
        {
            NestedAccessorMethods accessors = ResolveNestedObjectAccessors<TInstance>(
                listField, elementField, objectProperty);
            Type listType = listField.FieldType;
            Type elementType = listType.GetGenericArguments()[0];
            Type nestedType = elementField.FieldType;
            Type resolvedType = objectProperty.PropertyType;

            var method = new DynamicMethod(
                "PSO_AllNestedObjectIdHash_" + listField.Name,
                typeof(int),
                new[] { typeof(TInstance), typeof(int) },
                typeof(ScaleEmit).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            Label sentinel = il.DefineLabel();
            Label loopStart = il.DefineLabel();
            Label loopEnd = il.DefineLabel();
            Label advance = il.DefineLabel();
            LocalBuilder list = il.DeclareLocal(listType);
            LocalBuilder count = il.DeclareLocal(typeof(int));
            LocalBuilder index = il.DeclareLocal(typeof(int));
            LocalBuilder hash = il.DeclareLocal(typeof(int));
            LocalBuilder element = il.DeclareLocal(elementType);
            LocalBuilder nested = il.DeclareLocal(nestedType);
            LocalBuilder resolved = il.DeclareLocal(resolvedType);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, listField);
            il.Emit(OpCodes.Stloc, list);
            il.Emit(OpCodes.Ldloc, list);
            il.Emit(OpCodes.Brfalse, sentinel);
            il.Emit(OpCodes.Ldloc, list);
            il.Emit(OpCodes.Callvirt, accessors.CountGetter);
            il.Emit(OpCodes.Stloc, count);
            il.Emit(OpCodes.Ldloc, count);
            il.Emit(OpCodes.Brfalse, sentinel);
            il.Emit(OpCodes.Ldc_I4, 17);
            il.Emit(OpCodes.Stloc, hash);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, index);

            il.MarkLabel(loopStart);
            il.Emit(OpCodes.Ldloc, index);
            il.Emit(OpCodes.Ldloc, count);
            il.Emit(OpCodes.Bge, loopEnd);
            il.Emit(OpCodes.Ldloc, list);
            il.Emit(OpCodes.Ldloc, index);
            il.Emit(OpCodes.Callvirt, accessors.ItemGetter);
            il.Emit(OpCodes.Stloc, element);
            il.Emit(OpCodes.Ldloc, element);
            il.Emit(OpCodes.Brfalse, advance);
            il.Emit(OpCodes.Ldloc, element);
            il.Emit(OpCodes.Ldfld, elementField);
            il.Emit(OpCodes.Stloc, nested);
            il.Emit(OpCodes.Ldloc, nested);
            il.Emit(OpCodes.Brfalse, advance);
            il.Emit(OpCodes.Ldloc, nested);
            il.Emit(OpCodes.Callvirt, accessors.ObjectGetter);
            il.Emit(OpCodes.Stloc, resolved);
            il.Emit(OpCodes.Ldloc, resolved);
            il.Emit(OpCodes.Brfalse, advance);
            il.Emit(OpCodes.Ldc_I4, 31);
            il.Emit(OpCodes.Ldloc, hash);
            il.Emit(OpCodes.Mul);
            il.Emit(OpCodes.Ldloc, resolved);
            il.Emit(OpCodes.Callvirt, accessors.InstanceIdGetter);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, hash);

            il.MarkLabel(advance);
            il.Emit(OpCodes.Ldloc, index);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, index);
            il.Emit(OpCodes.Br, loopStart);

            il.MarkLabel(loopEnd);
            il.Emit(OpCodes.Ldloc, hash);
            il.Emit(OpCodes.Ret);

            il.MarkLabel(sentinel);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ret);

            return (Func<TInstance, int, int>)method.CreateDelegate(typeof(Func<TInstance, int, int>));
        }

        private sealed class NestedAccessorMethods
        {
            internal MethodInfo CountGetter;
            internal MethodInfo ItemGetter;
            internal MethodInfo ObjectGetter;
            internal MethodInfo InstanceIdGetter;
        }

        private static NestedAccessorMethods ResolveNestedObjectAccessors<TInstance>(
            FieldInfo listField,
            FieldInfo elementField,
            PropertyInfo objectProperty)
        {
            if (listField == null) throw new ArgumentNullException(nameof(listField));
            if (elementField == null) throw new ArgumentNullException(nameof(elementField));
            if (objectProperty == null) throw new ArgumentNullException(nameof(objectProperty));
            if (listField.IsStatic || listField.DeclaringType != typeof(TInstance))
                throw new InvalidOperationException($"Unexpected field owner for {listField.Name}");

            Type listType = listField.FieldType;
            if (!listType.IsGenericType || listType.GetGenericTypeDefinition() != typeof(List<>))
                throw new InvalidOperationException($"{listField.Name} is not a List<>");
            Type elementType = listType.GetGenericArguments()[0];
            if (elementType.IsValueType)
                throw new InvalidOperationException($"{listField.Name} element type must be a reference type");
            if (elementField.IsStatic || elementField.DeclaringType != elementType)
                throw new InvalidOperationException($"{elementField.Name} is not declared on the element type");
            if (!typeof(UnityEngine.Object).IsAssignableFrom(elementField.FieldType))
                throw new InvalidOperationException($"{elementField.Name} is not a UnityEngine.Object field");
            if (!objectProperty.DeclaringType.IsAssignableFrom(elementField.FieldType))
                throw new InvalidOperationException($"{objectProperty.Name} is not reachable from {elementField.Name}");
            if (!typeof(UnityEngine.Object).IsAssignableFrom(objectProperty.PropertyType) ||
                objectProperty.GetIndexParameters().Length != 0)
            {
                throw new InvalidOperationException($"{objectProperty.Name} is not a parameterless UnityEngine.Object property");
            }
            MethodInfo objectGetter = objectProperty.GetGetMethod(true);
            if (objectGetter == null || objectGetter.IsStatic)
                throw new InvalidOperationException($"{objectProperty.Name} getter is unavailable");

            PropertyInfo countProperty = listType.GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
            MethodInfo countGetter = countProperty?.GetGetMethod(false);
            PropertyInfo itemProperty = listType.GetProperty("Item", BindingFlags.Instance | BindingFlags.Public);
            MethodInfo itemGetter = itemProperty?.GetGetMethod(false);
            if (countGetter == null || itemGetter == null || countProperty.PropertyType != typeof(int))
                throw new InvalidOperationException($"{listField.Name} has no usable Count/Item accessors");

            MethodInfo instanceIdGetter = typeof(UnityEngine.Object).GetMethod(
                "GetInstanceID", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (instanceIdGetter == null || instanceIdGetter.ReturnType != typeof(int))
                throw new InvalidOperationException("UnityEngine.Object.GetInstanceID structure mismatch");

            return new NestedAccessorMethods
            {
                CountGetter = countGetter,
                ItemGetter = itemGetter,
                ObjectGetter = objectGetter,
                InstanceIdGetter = instanceIdGetter
            };
        }

        internal static bool IsClosedGenericListOf(Type type, string elementTypeFullName)
        {
            if (type == null || !type.IsGenericType || type.IsGenericTypeDefinition)
                return false;
            if (type.GetGenericTypeDefinition() != typeof(List<>))
                return false;
            Type[] arguments = type.GetGenericArguments();
            return arguments.Length == 1 &&
                string.Equals(arguments[0].FullName, elementTypeFullName, StringComparison.Ordinal);
        }

        private static MethodInfo RequireAccessor(FieldInfo field, PropertyInfo property, bool wantSetter)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));
            if (property == null) throw new ArgumentNullException(nameof(property));
            if (field.IsStatic)
                throw new InvalidOperationException($"{field.Name} must be an instance field");
            if (!property.DeclaringType.IsAssignableFrom(field.FieldType))
                throw new InvalidOperationException(
                    $"{property.Name} is not reachable through field {field.Name}");
            if (!property.PropertyType.IsEnum || Enum.GetUnderlyingType(property.PropertyType) != typeof(int))
                throw new InvalidOperationException($"{property.Name} is not an int-backed enum");
            MethodInfo accessor = wantSetter ? property.GetSetMethod(true) : property.GetGetMethod(true);
            if (accessor == null || accessor.IsStatic)
                throw new InvalidOperationException($"{property.Name} accessor is unavailable");
            return accessor;
        }
    }
}
