using System;
using System.Reflection;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Structural validation helpers shared by the Scale lane. Every check is exact:
    /// declaring type, instance/static shape, member type and full signature must match
    /// the audited game build, otherwise the caller throws and the infrastructure fails closed.
    /// </summary>
    internal static class ScaleValidation
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

        /// <summary>
        /// Same exactness as <see cref="RequireInstanceField"/> but identifies the field type by
        /// full name. Used for game fields whose type lives in an assembly this plugin does not
        /// reference at compile time (for example UnityEngine.PhysicsModule).
        /// </summary>
        internal static FieldInfo RequireInstanceFieldByTypeName(Type owner, string name, string fieldTypeFullName)
        {
            FieldInfo field = owner.GetField(name, DeclaredInstance);
            if (field == null)
                throw new MissingFieldException(owner.FullName, name);
            if (field.DeclaringType != owner || field.IsStatic ||
                !string.Equals(field.FieldType.FullName, fieldTypeFullName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected field structure: {owner.FullName}.{name}");
            }
            return field;
        }

        internal static PropertyInfo RequireInstancePropertyByTypeName(
            Type owner,
            string name,
            string propertyTypeFullName,
            bool requireSetter)
        {
            PropertyInfo property = owner.GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo getter = property?.GetGetMethod(true);
            MethodInfo setter = property?.GetSetMethod(true);
            if (property == null || getter == null || getter.IsStatic ||
                property.GetIndexParameters().Length != 0 ||
                !string.Equals(property.PropertyType.FullName, propertyTypeFullName, StringComparison.Ordinal) ||
                (requireSetter && (setter == null || setter.IsStatic)))
            {
                throw new InvalidOperationException($"Unexpected property structure: {owner.FullName}.{name}");
            }
            return property;
        }

        internal static MethodInfo RequireInstanceMethodByTypeNames(
            Type owner,
            string name,
            string returnTypeFullName,
            string[] parameterTypeFullNames)
        {
            MethodInfo[] candidates = owner.GetMethods(DeclaredInstance);
            MethodInfo found = null;
            for (int i = 0; i < candidates.Length; i++)
            {
                MethodInfo candidate = candidates[i];
                if (!string.Equals(candidate.Name, name, StringComparison.Ordinal) ||
                    candidate.IsStatic || candidate.ContainsGenericParameters)
                    continue;
                if (!string.Equals(candidate.ReturnType.FullName, returnTypeFullName, StringComparison.Ordinal))
                    continue;
                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length != parameterTypeFullNames.Length)
                    continue;
                bool match = true;
                for (int p = 0; p < parameters.Length; p++)
                {
                    if (!string.Equals(parameters[p].ParameterType.FullName, parameterTypeFullNames[p], StringComparison.Ordinal))
                    {
                        match = false;
                        break;
                    }
                }
                if (!match)
                    continue;
                if (found != null)
                    throw new InvalidOperationException($"Ambiguous method structure: {owner.FullName}.{name}");
                found = candidate;
            }
            if (found == null)
                throw new MissingMethodException(owner.FullName, name);
            return found;
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
            if (property == null || property.DeclaringType != owner ||
                property.PropertyType != propertyType || property.GetIndexParameters().Length != 0 ||
                getter == null || getter.IsStatic)
            {
                throw new InvalidOperationException($"Unexpected property structure: {owner.FullName}.{name}");
            }
            return property;
        }

        internal static PropertyInfo RequireStaticProperty(Type owner, string name, Type propertyType)
        {
            PropertyInfo property = owner.GetProperty(name, DeclaredStatic);
            MethodInfo getter = property?.GetGetMethod(true);
            if (property == null || property.DeclaringType != owner ||
                property.PropertyType != propertyType || property.GetIndexParameters().Length != 0 ||
                getter == null || !getter.IsStatic)
            {
                throw new InvalidOperationException($"Unexpected static property structure: {owner.FullName}.{name}");
            }
            return property;
        }

        internal static PropertyInfo RequireInheritedInstanceProperty(Type owner, string name, Type propertyType)
        {
            PropertyInfo property = owner.GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo getter = property?.GetGetMethod(true);
            if (property == null || property.PropertyType != propertyType ||
                property.GetIndexParameters().Length != 0 || getter == null || getter.IsStatic)
            {
                throw new InvalidOperationException($"Unexpected inherited property structure: {owner.FullName}.{name}");
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
            Validate(method, owner, name, returnType, parameterTypes, false, requirePublic);
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
            Validate(method, owner, name, returnType, parameterTypes, true, requirePublic);
            return method;
        }

        private static void Validate(
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
