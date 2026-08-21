using System;
using System.Reflection;

namespace PeakSafeOptimizer.Patches.Equivalent.MathAndScan
{
    internal static class MathAndScanValidation
    {
        private const BindingFlags DeclaredInstance = BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const BindingFlags DeclaredStatic = BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        internal static FieldInfo RequireInstanceField(Type owner, string name, Type fieldType)
        {
            FieldInfo field = owner.GetField(name, DeclaredInstance);
            if (field == null)
                throw new MissingFieldException(owner.FullName, name);
            if (field.DeclaringType != owner || field.IsStatic || field.FieldType != fieldType)
                throw new InvalidOperationException($"Unexpected field structure: {owner.FullName}.{name}");
            return field;
        }

        internal static FieldInfo RequireStaticField(Type owner, string name, Type fieldType)
        {
            FieldInfo field = owner.GetField(name, DeclaredStatic);
            if (field == null)
                throw new MissingFieldException(owner.FullName, name);
            if (field.DeclaringType != owner || !field.IsStatic || field.FieldType != fieldType)
                throw new InvalidOperationException($"Unexpected static field structure: {owner.FullName}.{name}");
            return field;
        }

        internal static PropertyInfo RequireInstanceProperty(Type owner, string name, Type propertyType)
        {
            PropertyInfo property = owner.GetProperty(name, DeclaredInstance);
            MethodInfo getter = property?.GetGetMethod(true);
            if (property == null || property.DeclaringType != owner || property.PropertyType != propertyType ||
                property.GetIndexParameters().Length != 0 || getter == null || getter.IsStatic)
            {
                throw new InvalidOperationException($"Unexpected property structure: {owner.FullName}.{name}");
            }
            return property;
        }

        internal static MethodInfo RequireInstanceMethod(
            Type owner,
            string name,
            Type returnType,
            Type[] parameterTypes,
            bool requirePublic)
        {
            MethodInfo method = owner.GetMethod(name, DeclaredInstance, null, parameterTypes, null);
            ValidateMethod(method, owner, name, returnType, parameterTypes, false, requirePublic);
            return method;
        }

        internal static MethodInfo RequireStaticMethod(
            Type owner,
            string name,
            Type returnType,
            Type[] parameterTypes,
            bool requirePublic)
        {
            MethodInfo method = owner.GetMethod(name, DeclaredStatic, null, parameterTypes, null);
            ValidateMethod(method, owner, name, returnType, parameterTypes, true, requirePublic);
            return method;
        }

        private static void ValidateMethod(
            MethodInfo method,
            Type owner,
            string name,
            Type returnType,
            Type[] parameterTypes,
            bool requireStatic,
            bool requirePublic)
        {
            if (method == null)
                throw new MissingMethodException(owner.FullName, name);
            if (method.DeclaringType != owner || method.IsStatic != requireStatic ||
                method.ReturnType != returnType || method.ContainsGenericParameters ||
                (requirePublic && !method.IsPublic))
            {
                throw new InvalidOperationException($"Unexpected method structure: {owner.FullName}.{name}");
            }

            ParameterInfo[] actual = method.GetParameters();
            if (actual.Length != parameterTypes.Length)
                throw new InvalidOperationException($"Unexpected parameter count: {owner.FullName}.{name}");
            for (int i = 0; i < actual.Length; i++)
            {
                if (actual[i].ParameterType != parameterTypes[i])
                    throw new InvalidOperationException($"Unexpected parameter {i}: {owner.FullName}.{name}");
            }
        }
    }
}
