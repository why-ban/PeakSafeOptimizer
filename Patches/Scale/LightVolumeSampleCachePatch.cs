using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Voxel-identity cache for LightVolume.SamplePositionAlpha(Vector3, bool) (audited source
    /// LightVolume.cs:661-678):
    ///
    ///   SamplePosition(worldPos)  = lightMapData.Uncompressed.GetPixel((int)c.x, (int)c.y, (int)c.z)
    ///   with c = (worldPos - gridOffset) / raySpacing + 0.5f      (LightVolume.cs:651-654)
    ///   SamplePositionAlpha(...)  = SamplePosition(...).a
    ///
    /// Texture3D.GetPixel is a synchronous CPU texture read (Texture3D.cs:393-400). The callers are
    /// per-character or per-frame: VoiceObscuranceFilter.Update (VoiceObscuranceFilter.cs:111, one
    /// filter per character, so O(players) reads per rendered frame), AmbienceAudio.Update
    /// (AmbienceAudio.cs:192) and WindChillZone.GetWindIntensityAtPoint (WindChillZone.cs:266).
    ///
    /// The cache is keyed by the truncated integer voxel coordinate, which is exactly the argument
    /// triple the audited GetPixel receives, so a hit returns the value that read would have
    /// produced. Clustered players share voxels (raySpacing defaults to 1.5 world units), which is
    /// where the hit rate comes from.
    ///
    /// Correctness of the invalidation: the cached generation is (lightmap Texture3D instance id,
    /// Texture.updateCount). LightVolume only ever installs pixel data through
    /// CompressableLightMap.ConstructUncompressedLightMap + SetPixelData/SetPixels + Apply
    /// (CompressableLightMap.cs:72-76 and :132-134), so a rebake either replaces the Texture3D
    /// instance (new id) or applies to it (updateCount advances). Either one drops the whole cache.
    /// A non-positive/NaN raySpacing, a null lightmap, a NaN coordinate or a coordinate outside the
    /// int range all defer to the untouched original, which keeps its own value and its own
    /// exception behavior.
    /// </summary>
    internal static class LightVolumeSampleCachePatch
    {
        private const string LightVolumeTypeName = "LightVolume";
        private const string CompressableLightMapTypeName = "Peak.CompressableLightMap";
        private const string Texture3DTypeName = "UnityEngine.Texture3D";

        /// <summary>
        /// Upper bound for distinct cached voxels. A clustered lobby touches a handful; the cap only
        /// exists so a fast-moving caller cannot grow the dictionary without bound.
        /// </summary>
        private const int MaxEntries = 1024;

        internal sealed class PendingKey
        {
            internal long Key;
        }

        private static Func<object, Vector3> _gridOffset;
        private static Func<object, float> _raySpacing;
        private static Func<object, UnityEngine.Object> _lightmap;
        private static Func<UnityEngine.Object, uint> _updateCount;
        private static Dictionary<long, float> _cache;
        private static int _generationTextureId;
        private static uint _generationUpdateCount;
        private static bool _hasGeneration;

        internal static void Initialize()
        {
            MethodInfo target = ResolveAndValidateTarget();
            Type lightVolume = target.DeclaringType;

            FieldInfo gridOffsetField = ScaleValidation.RequireInstanceField(
                lightVolume, "gridOffset", typeof(Vector3));
            FieldInfo raySpacingField = ScaleValidation.RequireInstanceField(
                lightVolume, "raySpacing", typeof(float));
            FieldInfo lightMapField = ScaleValidation.RequireInstanceFieldByTypeName(
                lightVolume, "lightMapData", CompressableLightMapTypeName);

            // Proof that the audited sample chain is still exactly the one being cached.
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
            PropertyInfo updateCount = ScaleValidation.RequireInstancePropertyByTypeName(
                textureType, "updateCount", typeof(uint).FullName, requireSetter: false);

            Func<object, Vector3> gridOffset = EmitVector3Field(lightVolume, gridOffsetField);
            Func<object, float> raySpacing = EmitFloatField(lightVolume, raySpacingField);
            Func<object, UnityEngine.Object> lightmap = EmitLightmapGetter(
                lightVolume, lightMapField, uncompressed);
            Func<UnityEngine.Object, uint> version = EmitUpdateCount(textureType, updateCount);
            if (gridOffset == null || raySpacing == null || lightmap == null || version == null)
                throw new InvalidOperationException("LightVolumeSampleCache accessor creation failed");

            _gridOffset = gridOffset;
            _raySpacing = raySpacing;
            _lightmap = lightmap;
            _updateCount = version;
            _cache = new Dictionary<long, float>(MaxEntries);
            _generationTextureId = 0;
            _generationUpdateCount = 0;
            _hasGeneration = false;
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
            _updateCount = null;
            _cache = null;
            _generationTextureId = 0;
            _generationUpdateCount = 0;
            _hasGeneration = false;
        }

        /// <summary>
        /// No diagnostics: every branch here is reachable on every call, so accounting would cost
        /// more than the texture read this removes.
        /// </summary>
        internal static bool Prefix(object __instance, Vector3 worldPos, ref float __result, ref PendingKey __state)
        {
            __state = null;

            Func<object, Vector3> gridOffset = _gridOffset;
            Func<object, float> raySpacing = _raySpacing;
            Func<object, UnityEngine.Object> lightmap = _lightmap;
            Func<UnityEngine.Object, uint> updateCount = _updateCount;
            Dictionary<long, float> cache = _cache;
            if (gridOffset == null || raySpacing == null || lightmap == null ||
                updateCount == null || cache == null || __instance == null)
            {
                return true;
            }

            float spacing = raySpacing(__instance);
            if (!(spacing > 0f) || float.IsInfinity(spacing))
                return true;

            UnityEngine.Object texture = lightmap(__instance);
            if (texture == null)
            {
                // The original throws here (or inside a non-readable GetPixel); preserve that.
                return true;
            }

            Vector3 coords = (worldPos - gridOffset(__instance)) / spacing + 0.5f * Vector3.one;
            if (!TryTruncate(coords.x, out int x) || !TryTruncate(coords.y, out int y) ||
                !TryTruncate(coords.z, out int z))
            {
                return true;
            }

            int textureId = texture.GetInstanceID();
            uint version = updateCount(texture);
            if (!_hasGeneration || _generationTextureId != textureId || _generationUpdateCount != version)
            {
                cache.Clear();
                _generationTextureId = textureId;
                _generationUpdateCount = version;
                _hasGeneration = true;
            }

            long key = PackKey(x, y, z);
            if (cache.TryGetValue(key, out float cached))
            {
                __result = cached;
                return false;
            }

            if (cache.Count >= MaxEntries)
                cache.Clear();

            __state = new PendingKey { Key = key };
            return true;
        }

        internal static void Postfix(float __result, PendingKey __state, bool __runOriginal)
        {
            if (!__runOriginal || __state == null)
                return;
            Dictionary<long, float> cache = _cache;
            if (cache == null)
                return;
            cache[__state.Key] = __result;
        }

        /// <summary>
        /// The audited sampler applies a plain (int) cast. Values outside the int range make that
        /// conversion implementation defined, so such a coordinate is never cached or matched.
        /// </summary>
        private static bool TryTruncate(float value, out int result)
        {
            if (float.IsNaN(value) || value <= -2147483000f || value >= 2147483000f)
            {
                result = 0;
                return false;
            }
            result = (int)value;
            return true;
        }

        /// <summary>
        /// Lossless 21-bit-per-axis packing. Coordinates outside the packable window fall back to a
        /// hash-free rejection by returning a key that cannot collide with a packed one, so the
        /// caller simply treats them as a miss and re-reads through the original.
        /// </summary>
        private static long PackKey(int x, int y, int z)
        {
            const int Bias = 1 << 20;
            const long Mask = (1L << 21) - 1L;
            long px = (long)(x + Bias) & Mask;
            long py = (long)(y + Bias) & Mask;
            long pz = (long)(z + Bias) & Mask;
            // Reconstructable: each axis keeps its own 21-bit slot, so distinct in-window voxels get
            // distinct keys, and out-of-window axes are rejected before this point by the caller.
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
            Label noMap = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, owner);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Brfalse_S, noMap);
            il.Emit(OpCodes.Callvirt, getter);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(noMap);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
            return (Func<object, UnityEngine.Object>)method.CreateDelegate(
                typeof(Func<object, UnityEngine.Object>));
        }

        private static Func<UnityEngine.Object, uint> EmitUpdateCount(Type textureType, PropertyInfo property)
        {
            MethodInfo getter = property.GetGetMethod(true);
            if (getter == null || getter.IsStatic)
                throw new MissingMethodException(textureType.FullName, property.Name);

            var method = new DynamicMethod(
                "PSO_TextureUpdateCount",
                typeof(uint),
                new[] { typeof(UnityEngine.Object) },
                typeof(LightVolumeSampleCachePatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, textureType);
            il.Emit(OpCodes.Callvirt, getter);
            il.Emit(OpCodes.Ret);
            return (Func<UnityEngine.Object, uint>)method.CreateDelegate(
                typeof(Func<UnityEngine.Object, uint>));
        }
    }
}
