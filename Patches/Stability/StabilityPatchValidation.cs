using System;
using System.Reflection;

namespace PeakSafeOptimizer.Patches.Stability
{
    internal static class StabilityPatchValidation
    {
        private const BindingFlags DeclaredInstance = BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        internal static FieldInfo RequireInstanceField(Type declaringType, string name, Type fieldType)
        {
            FieldInfo field = declaringType.GetField(name, DeclaredInstance);
            if (field == null)
                throw new MissingFieldException(declaringType.FullName, name);
            if (field.DeclaringType != declaringType || field.IsStatic || field.FieldType != fieldType)
                throw new InvalidOperationException(
                    $"Unexpected field structure for {declaringType.FullName}.{name}");
            return field;
        }

        internal static PropertyInfo RequireInstanceProperty(
            Type declaringType,
            string name,
            Type propertyType,
            bool requirePublicGetter = true)
        {
            PropertyInfo property = declaringType.GetProperty(name, DeclaredInstance);
            MethodInfo getter = property?.GetGetMethod(true);
            if (property == null || property.DeclaringType != declaringType ||
                property.PropertyType != propertyType || property.GetIndexParameters().Length != 0 ||
                getter == null || getter.IsStatic || getter.ContainsGenericParameters ||
                (requirePublicGetter && !getter.IsPublic))
            {
                throw new InvalidOperationException(
                    $"Unexpected property structure for {declaringType.FullName}.{name}");
            }
            return property;
        }

        internal static MethodInfo RequireInstanceMethod(
            Type declaringType,
            string name,
            Type returnType,
            Type[] parameterTypes,
            bool requirePublic = true)
        {
            MethodInfo method = declaringType.GetMethod(
                name,
                DeclaredInstance,
                null,
                parameterTypes,
                null);
            if (method == null)
                throw new MissingMethodException(declaringType.FullName, name);
            if (method.DeclaringType != declaringType || method.IsStatic ||
                method.ReturnType != returnType || method.ContainsGenericParameters ||
                (requirePublic && !method.IsPublic))
            {
                throw new InvalidOperationException(
                    $"Unexpected method structure for {declaringType.FullName}.{name}");
            }

            ParameterInfo[] actualParameters = method.GetParameters();
            if (actualParameters.Length != parameterTypes.Length)
                throw new InvalidOperationException(
                    $"Unexpected parameter count for {declaringType.FullName}.{name}");
            for (int i = 0; i < actualParameters.Length; i++)
            {
                if (actualParameters[i].ParameterType != parameterTypes[i])
                    throw new InvalidOperationException(
                        $"Unexpected parameter {i} for {declaringType.FullName}.{name}");
            }
            return method;
        }
    }
}
