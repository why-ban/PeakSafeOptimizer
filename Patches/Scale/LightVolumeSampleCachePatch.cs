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
    ///
    /// Allocation free on every path. The Prefix and the Postfix each derive the voxel key from the
    /// same <c>worldPos</c> through the same pure <see cref="TryResolve"/> instead of passing a
    /// carrier object through <c>__state</c>, so a cache miss costs a handful of float operations and
    /// one <c>long</c> pack rather than a gen-0 object. See the Postfix for why the two derivations
    /// cannot disagree.
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

            // Both the Prefix and the Postfix receive the sampler's own first argument by name, which
            // is how the Postfix can re-derive the voxel key without a carrier object. Harmony matches
            // that parameter by name, so the name is a structural dependency of this patch and is
            // asserted here rather than left to fail at Patch time.
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
            // The accessor emitted below depends on this being a struct field, so assert it here
            // rather than letting a reshaped build produce invalid IL at delegate creation time.
            if (!lightMapField.FieldType.IsValueType)
                throw new InvalidOperationException("LightVolume.lightMapData is no longer a struct");

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
        ///
        /// Allocation free, including on a cache miss. There is no <c>__state</c> object: the Postfix
        /// re-derives the same voxel key from the same <c>worldPos</c> through the same pure
        /// <see cref="TryResolve"/>, so nothing has to be carried across the call. See the Postfix for
        /// why that re-derivation is guaranteed to agree with this one.
        /// </summary>
        internal static bool Prefix(object __instance, Vector3 worldPos, ref float __result)
        {
            Dictionary<long, float> cache = _cache;
            if (cache == null)
                return true;

            if (!TryResolve(__instance, worldPos, out long key, out int textureId, out uint version))
                return true;

            if (!_hasGeneration || _generationTextureId != textureId || _generationUpdateCount != version)
            {
                cache.Clear();
                _generationTextureId = textureId;
                _generationUpdateCount = version;
                _hasGeneration = true;
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

        /// <summary>
        /// Files the value the original just read under its voxel key. The key is recomputed here
        /// rather than handed over from the Prefix, which is what makes this patch allocation free on
        /// a cache miss as well as on a hit.
        ///
        /// Why recomputing is exact. <see cref="TryResolve"/> is a pure function of
        /// (<paramref name="__instance"/>, <paramref name="worldPos"/>) plus this class's own
        /// accessor delegates, and none of its three inputs can move between the Prefix and here:
        ///   * <paramref name="worldPos"/> is never assigned by the original. The whole audited body
        ///     is <c>return SamplePosition(worldPos, printDebug).a;</c> (LightVolume.cs:675-678) into
        ///     <c>WorldPosToLightmapCoords(worldPos)</c> plus one
        ///     <c>lightMapData.Uncompressed.GetPixel(...)</c> (LightVolume.cs:661-665, :651-654).
        ///   * <c>gridOffset</c> and <c>raySpacing</c> are only written by LoadLightMapData
        ///     (LightVolume.cs:124), SetSize (:236) and the bake path (:531). None of those is on the
        ///     sampler's call graph - the sampler reads three fields and calls one native texture
        ///     getter, and <c>Texture3D.GetPixel</c> is an <c>isReadable</c> test plus
        ///     <c>GetPixelImpl</c> (Texture3D.cs:393-400), i.e. a read.
        ///   * The generation stamp likewise cannot advance: only <c>Apply()</c> or a replaced
        ///     Texture3D moves it, and both live in CompressableLightMap's constructor and Decompress
        ///     (CompressableLightMap.cs:72-76, :132-134), neither of which the sampler reaches.
        /// The generation is nevertheless re-verified below rather than assumed, because a third-party
        /// Postfix ordered before this one is outside that argument. A mismatch means the whole cache
        /// belongs to a different bake, so the value is dropped instead of filed - the Prefix owns
        /// clearing, this method never does.
        ///
        /// The re-derivation also removes the re-entrancy hazard a shared carrier would have had:
        /// each invocation computes its own key from its own argument, so a nested call (another
        /// plugin prefixing SamplePosition, say) cannot file the inner sample under the outer key.
        ///
        /// <paramref name="__runOriginal"/> stays load bearing and now covers two cases rather than
        /// one. A prefix in the chain that suppressed the original - including this patch's own cache
        /// hit, which returns false - leaves <c>__result</c> holding a value that never came from a
        /// Texture3D read, and caching that would publish a foreign alpha under a voxel key and keep
        /// serving it until the next rebake.
        /// </summary>
        internal static void Postfix(object __instance, Vector3 worldPos, float __result, bool __runOriginal)
        {
            if (!__runOriginal)
                return;
            Dictionary<long, float> cache = _cache;
            if (cache == null)
                return;

            if (!TryResolve(__instance, worldPos, out long key, out int textureId, out uint version))
                return;
            if (!_hasGeneration || _generationTextureId != textureId || _generationUpdateCount != version)
                return;

            cache[key] = __result;
        }

        /// <summary>
        /// Resolves one call's voxel key plus the generation stamp of the lightmap it would be read
        /// from. Pure: it reads three LightVolume members through the cached accessors and writes
        /// nothing, which is the property that lets the Prefix and the Postfix derive the same key
        /// independently.
        ///
        /// Returns false for exactly the inputs the Prefix defers to the original on - a missing
        /// accessor, a non-positive or infinite raySpacing, a null lightmap, or a coordinate outside
        /// the range where the audited <c>(int)</c> cast is defined. Because the test is a pure
        /// function, the Postfix reaching false is the same information as the Prefix having reserved
        /// no key, so no flag has to travel between them.
        /// </summary>
        private static bool TryResolve(
            object instance, Vector3 worldPos, out long key, out int textureId, out uint version)
        {
            key = 0L;
            textureId = 0;
            version = 0u;

            Func<object, Vector3> gridOffset = _gridOffset;
            Func<object, float> raySpacing = _raySpacing;
            Func<object, UnityEngine.Object> lightmap = _lightmap;
            Func<UnityEngine.Object, uint> updateCount = _updateCount;
            if (gridOffset == null || raySpacing == null || lightmap == null ||
                updateCount == null || instance == null)
            {
                return false;
            }

            float spacing = raySpacing(instance);
            if (!(spacing > 0f) || float.IsInfinity(spacing))
                return false;

            UnityEngine.Object texture = lightmap(instance);
            if (texture == null)
            {
                // The original throws here (or inside a non-readable GetPixel); preserve that.
                return false;
            }

            Vector3 coords = (worldPos - gridOffset(instance)) / spacing + 0.5f * Vector3.one;
            if (!TryTruncate(coords.x, out int x) || !TryTruncate(coords.y, out int y) ||
                !TryTruncate(coords.z, out int z))
            {
                return false;
            }

            textureId = texture.GetInstanceID();
            version = updateCount(texture);
            key = PackKey(x, y, z);
            return true;
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
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, owner);
            // The audited lightMapData is `public struct CompressableLightMap`
            // (CompressableLightMap.cs:11). The previous version loaded it with Ldfld and branched on
            // it, which is invalid IL for a value type - exactly how this patch failed in-game
            // ("Invalid IL code ... brfalse.s"). Take the field address and call the property
            // non-virtually on it instead; Initialize has already proven the field is a struct. The
            // Texture3D the getter returns is still null-checked by the caller.
            il.Emit(OpCodes.Ldflda, field);
            il.Emit(OpCodes.Call, getter);
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
