using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Voxel-identity cache for LightVolume.SamplePositionAlpha: the audited body is one GetPixel at the
    /// truncated (worldPos - gridOffset) / raySpacing + 0.5, so caching that integer triple is exact.
    ///
    /// The bake identity is the Texture3D reference. The only two writers, CompressableLightMap's
    /// constructor and Decompress, both create a new Texture3D, so a reference change is exactly a bake
    /// change and nothing mutates a live volume. Holding the reference keeps the old wrapper alive,
    /// which makes ReferenceEquals exact where an instance id can be reused, and keeps the last resolved
    /// bake referenced until a later sample resolves a different one.
    ///
    /// The Postfix re-derives the key from the same arguments rather than carrying it through __state,
    /// which would allocate on every call; TryResolve is pure, so the two derivations cannot disagree.
    /// </summary>
    internal static class LightVolumeSampleCachePatch
    {
        private const string LightVolumeTypeName = "LightVolume";
        private const string CompressableLightMapTypeName = "Peak.CompressableLightMap";
        private const string Texture3DTypeName = "UnityEngine.Texture3D";

        private const int MaxEntries = 1024;

        private const int KeyBias = 1 << 20;
        private const int KeyMax = (1 << 20) - 1;
        private const long KeyMask = (1L << 21) - 1L;

        private static Func<object, Vector3> _gridOffset;
        private static Func<object, float> _raySpacing;
        private static Func<object, UnityEngine.Object> _lightmap;
        private static Dictionary<long, float> _cache;
        private static UnityEngine.Object _generationTexture;

        internal static void Initialize()
        {
            MethodInfo target = ResolveAndValidateTarget();
            Type lightVolume = target.DeclaringType;

            ParameterInfo[] targetParameters = target.GetParameters();
            if (targetParameters.Length != 2 ||
                !string.Equals(targetParameters[0].Name, "worldPos", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "LightVolume.SamplePositionAlpha no longer takes a first parameter named worldPos");
            }

            FieldInfo gridOffsetField = ScaleValidation.RequireInstanceField(
                lightVolume, "gridOffset", typeof(Vector3));
            FieldInfo raySpacingField = ScaleValidation.RequireInstanceField(
                lightVolume, "raySpacing", typeof(float));
            FieldInfo lightMapField = ScaleValidation.RequireInstanceFieldByTypeName(
                lightVolume, "lightMapData", CompressableLightMapTypeName);
            if (!lightMapField.FieldType.IsValueType)
                throw new InvalidOperationException("LightVolume.lightMapData is no longer a struct");

            ScaleValidation.RequireInstanceMethodByTypeNames(
                lightVolume,
                "WorldPosToLightmapCoords",
                "UnityEngine.Vector3",
                new[] { "UnityEngine.Vector3" });
            ScaleValidation.RequireInstanceMethodByTypeNames(
                lightVolume,
                "SamplePosition",
                "UnityEngine.Color",
                new[] { "UnityEngine.Vector3", typeof(bool).FullName });

            PropertyInfo uncompressed = ScaleValidation.RequireInstancePropertyByTypeName(
                lightMapField.FieldType, "Uncompressed", Texture3DTypeName, requireSetter: false);
            Type textureType = uncompressed.PropertyType;
            MethodInfo getPixel = textureType.GetMethod(
                "GetPixel",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(int), typeof(int), typeof(int) },
                null);
            if (getPixel == null || getPixel.IsStatic ||
                !string.Equals(getPixel.ReturnType.FullName, "UnityEngine.Color", StringComparison.Ordinal))
            {
                throw new MissingMethodException(textureType.FullName, "GetPixel(int, int, int)");
            }

            Func<object, Vector3> gridOffset = EmitVector3Field(lightVolume, gridOffsetField);
            Func<object, float> raySpacing = EmitFloatField(lightVolume, raySpacingField);
            Func<object, UnityEngine.Object> lightmap = EmitLightmapGetter(
                lightVolume, lightMapField, uncompressed);
            if (gridOffset == null || raySpacing == null || lightmap == null)
                throw new InvalidOperationException("LightVolumeSampleCache accessor creation failed");

            _gridOffset = gridOffset;
            _raySpacing = raySpacing;
            _lightmap = lightmap;
            _cache = new Dictionary<long, float>(MaxEntries);
            _generationTexture = null;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            Type lightVolume = AccessTools.TypeByName(LightVolumeTypeName);
            if (lightVolume == null)
                throw new TypeLoadException("LightVolume type not found");
            return ScaleValidation.RequireInstanceMethodByTypeNames(
                lightVolume,
                "SamplePositionAlpha",
                typeof(float).FullName,
                new[] { "UnityEngine.Vector3", typeof(bool).FullName });
        }

        internal static void Reset()
        {
            _gridOffset = null;
            _raySpacing = null;
            _lightmap = null;
            _cache = null;
            _generationTexture = null;
        }

        internal static bool Prefix(object __instance, Vector3 worldPos, ref float __result)
        {
            Dictionary<long, float> cache = _cache;
            if (cache == null)
                return true;

            if (!TryResolve(__instance, worldPos, out long key, out UnityEngine.Object texture))
                return true;

            if (!ReferenceEquals(_generationTexture, texture))
            {
                cache.Clear();
                _generationTexture = texture;
            }

            if (cache.TryGetValue(key, out float cached))
            {
                __result = cached;
                return false;
            }

            if (cache.Count >= MaxEntries)
                cache.Clear();

            return true;
        }

        internal static void Postfix(object __instance, Vector3 worldPos, float __result, bool __runOriginal)
        {
            if (!__runOriginal)
                return;
            Dictionary<long, float> cache = _cache;
            if (cache == null)
                return;

            if (!TryResolve(__instance, worldPos, out long key, out UnityEngine.Object texture))
                return;
            if (!ReferenceEquals(_generationTexture, texture))
                return;

            cache[key] = __result;
        }

        private static bool TryResolve(
            object instance, Vector3 worldPos, out long key, out UnityEngine.Object texture)
        {
            key = 0L;
            texture = null;

            Func<object, Vector3> gridOffset = _gridOffset;
            Func<object, float> raySpacing = _raySpacing;
            Func<object, UnityEngine.Object> lightmap = _lightmap;
            if (gridOffset == null || raySpacing == null || lightmap == null || instance == null)
                return false;

            float spacing = raySpacing(instance);
            if (!(spacing > 0f) || float.IsInfinity(spacing))
                return false;

            UnityEngine.Object resolved = lightmap(instance);
            if (resolved == null)
            {
                return false;
            }

            Vector3 coords = (worldPos - gridOffset(instance)) / spacing + 0.5f * Vector3.one;
            if (!TryTruncate(coords.x, out int x) || !TryTruncate(coords.y, out int y) ||
                !TryTruncate(coords.z, out int z))
            {
                return false;
            }

            texture = resolved;
            key = PackKey(x, y, z);
            return true;
        }

        private static bool TryTruncate(float value, out int result)
        {
            result = 0;
            if (float.IsNaN(value) || value <= -2147483000f || value >= 2147483000f)
                return false;
            int truncated = (int)value;
            if (truncated < -KeyBias || truncated > KeyMax)
                return false;
            result = truncated;
            return true;
        }

        private static long PackKey(int x, int y, int z)
        {
            long px = ((long)x + KeyBias) & KeyMask;
            long py = ((long)y + KeyBias) & KeyMask;
            long pz = ((long)z + KeyBias) & KeyMask;
            return (px << 42) | (py << 21) | pz;
        }

        private static Func<object, Vector3> EmitVector3Field(Type owner, FieldInfo field)
        {
            var method = new DynamicMethod(
                "PSO_LightVolumeGridOffset",
                typeof(Vector3),
                new[] { typeof(object) },
                typeof(LightVolumeSampleCachePatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, owner);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<object, Vector3>)method.CreateDelegate(typeof(Func<object, Vector3>));
        }

        private static Func<object, float> EmitFloatField(Type owner, FieldInfo field)
        {
            var method = new DynamicMethod(
                "PSO_LightVolumeRaySpacing",
                typeof(float),
                new[] { typeof(object) },
                typeof(LightVolumeSampleCachePatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, owner);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<object, float>)method.CreateDelegate(typeof(Func<object, float>));
        }

        private static Func<object, UnityEngine.Object> EmitLightmapGetter(
            Type owner, FieldInfo field, PropertyInfo property)
        {
            MethodInfo getter = property.GetGetMethod(true);
            if (getter == null || getter.IsStatic)
                throw new MissingMethodException(property.DeclaringType?.FullName, property.Name);

            var method = new DynamicMethod(
                "PSO_LightVolumeLightmap",
                typeof(UnityEngine.Object),
                new[] { typeof(object) },
                typeof(LightVolumeSampleCachePatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, owner);
            il.Emit(OpCodes.Ldflda, field);
            il.Emit(OpCodes.Call, getter);
            il.Emit(OpCodes.Ret);
            return (Func<object, UnityEngine.Object>)method.CreateDelegate(
                typeof(Func<object, UnityEngine.Object>));
        }
    }
}
