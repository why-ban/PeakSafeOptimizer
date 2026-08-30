using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Full equivalent replacement of AnimatedMouth.ProcessMicData(float[]) (audited source
    /// AnimatedMouth.cs:126-172). The original writes mouthRenderer.material.SetInt("_UseTalkSprites",
    /// ...) and SetTexture("_TalkSprite", ...) every frame. SetInt is idempotent, so it is only
    /// written when the _UseTalkSprites value changes; SetTexture is only written when amplitudeIndex
    /// changes. The previous values are tracked per instance in a ConditionalWeakTable keyed by the
    /// AnimatedMouth, so no instance or state object is ever strongly held by the patch.
    ///
    /// isSpeaking is still assigned every frame (PlayerEyeLook.cs:71 reads it), regardless of whether
    /// the material write is skipped. The outer guard, the level/decibel/peak-limiter/volume/volumePeak
    /// math, and the push-to-talk override are reproduced verbatim. audioSource and the
    /// PushToTalkSetting.Value property are reached through emitted accessors because their declaring
    /// types live in assemblies this project does not reference.
    /// </summary>
    internal static class AnimatedMouthMaterialPatch
    {
        private sealed class State
        {
            internal int LastUseTalkSprites = -1;
            internal int LastAmplitudeIndex = -1;
        }

        private static Func<AnimatedMouth, UnityEngine.Object> _getAudioSource;
        private static AccessTools.FieldRef<AnimatedMouth, float[]> _lastBuffer;
        private static AccessTools.FieldRef<AnimatedMouth, PushToTalkSetting> _pushToTalkSetting;
        private static AccessTools.FieldRef<AnimatedMouth, float> _volumePeak;
        private static Func<AnimatedMouth, float[], float> _micLevel;
        private static Func<PushToTalkSetting, PushToTalkSetting.PushToTalkType> _getPushToTalkValue;
        private static ConditionalWeakTable<AnimatedMouth, State> _state;
        // Shader property ids, resolved once in Initialize. Shader.PropertyToID is a native call
        // (Shader.cs:456-472, an extern behind a FreeFunction), and the string overloads of the four
        // Material members below do nothing but that call before delegating to the int overload
        // (Material.cs:1809-1817, 1869-1877, 2009-2017, 2069-2077), so caching the ids removes two
        // native hashes per AnimatedMouth per frame with no change in what the shader sees. Same
        // approach as Patches\ScaleExperimental\EyeLookComponentCachePatch.cs:144.
        private static int _useTalkSpritesId;
        private static int _talkSpriteId;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            Type owner = typeof(AnimatedMouth);
            FieldInfo audioSourceField = ScaleValidation.RequireInstanceFieldByTypeName(
                owner, "audioSource", "UnityEngine.AudioSource");
            ScaleValidation.RequireInstanceField(owner, "isGhost", typeof(bool));
            ScaleValidation.RequireInstanceField(owner, "character", typeof(Character));
            ScaleValidation.RequireInstanceField(owner, "mouthRenderer", typeof(Renderer));
            ScaleValidation.RequireInstanceField(owner, "mouthTextures", typeof(Texture2D[]));
            ScaleValidation.RequireInstanceField(owner, "amplitudeIndex", typeof(int));
            ScaleValidation.RequireInstanceField(owner, "isSpeaking", typeof(bool));
            ScaleValidation.RequireInstanceField(owner, "volume", typeof(float));
            ScaleValidation.RequireInstanceField(owner, "amplitudePeakLimiter", typeof(float));
            ScaleValidation.RequireInstanceField(owner, "minAmplitudeThreshold", typeof(float));
            ScaleValidation.RequireInstanceField(owner, "amplitudeHighestDecay", typeof(float));
            ScaleValidation.RequireInstanceField(owner, "amplitudeSmoothing", typeof(float));
            ScaleValidation.RequireInstanceField(owner, "talkThreshold", typeof(float));
            ScaleValidation.RequireInstanceField(owner, "amplitudeMult", typeof(float));
            ScaleValidation.RequireInstanceField(owner, "decibelToAmountCurve", typeof(AnimationCurve));
            FieldInfo lastBufferField = ScaleValidation.RequireInstanceField(
                owner, "m_lastSentLocalBuffer", typeof(float[]));
            FieldInfo pushToTalkField = ScaleValidation.RequireInstanceField(
                owner, "pushToTalkSetting", typeof(PushToTalkSetting));
            FieldInfo volumePeakField = ScaleValidation.RequireInstanceField(
                owner, "volumePeak", typeof(float));
            ScaleValidation.RequireInstanceProperty(owner, "NormalizedAmplitude", typeof(float));
            ScaleValidation.RequireInstanceProperty(owner, "VoiceHandler", typeof(CharacterVoiceHandler));
            MethodInfo micLevelMethod = ScaleValidation.RequireInstanceMethod(
                owner, "MicrophoneLevelMax", typeof(float), new[] { typeof(float[]) }, false);

            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceField(typeof(Character), "input", typeof(CharacterInput));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "IsLocal", typeof(bool));
            ScaleValidation.RequireInstanceProperty(typeof(CharacterData), "dead", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "passedOut", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterInput), "pushToTalkPressed", typeof(bool));
            ScaleValidation.RequireInstanceProperty(
                typeof(CharacterVoiceHandler), "LastReadSquaredMax", typeof(float));
            ScaleValidation.RequireInstanceProperty(
                typeof(CharacterVoiceHandler), "LastAudioBuffer", typeof(float[]));
            PropertyInfo valueProperty = ScaleValidation.RequireInheritedInstanceProperty(
                typeof(PushToTalkSetting), "Value", typeof(PushToTalkSetting.PushToTalkType));
            if (!Enum.IsDefined(typeof(PushToTalkSetting.PushToTalkType),
                PushToTalkSetting.PushToTalkType.PushToTalk) ||
                !Enum.IsDefined(typeof(PushToTalkSetting.PushToTalkType),
                PushToTalkSetting.PushToTalkType.PushToMute))
            {
                throw new InvalidOperationException("PushToTalkType values are no longer defined");
            }

            // The int-id path used on the hot path below. Both the id producer and all four int
            // overloads are asserted, because the equivalence argument rests on them: the string
            // overloads are literally PropertyToID plus the int overload (Material.cs:1811 vs :1816,
            // :1871 vs :1876, :2011 vs :2016, :2071 vs :2076).
            ScaleValidation.RequireStaticMethod(
                typeof(Shader), "PropertyToID", typeof(int), new[] { typeof(string) }, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Material), "GetInt", typeof(int), new[] { typeof(int) }, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Material), "SetInt", typeof(void), new[] { typeof(int), typeof(int) }, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Material), "GetTexture", typeof(Texture), new[] { typeof(int) }, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Material), "SetTexture", typeof(void), new[] { typeof(int), typeof(Texture) }, true);

            Func<AnimatedMouth, UnityEngine.Object> getAudioSource =
                ScaleEmit.CreateUnityObjectFieldGetter<AnimatedMouth>(audioSourceField);
            AccessTools.FieldRef<AnimatedMouth, float[]> lastBuffer =
                AccessTools.FieldRefAccess<AnimatedMouth, float[]>(lastBufferField);
            AccessTools.FieldRef<AnimatedMouth, PushToTalkSetting> pushToTalk =
                AccessTools.FieldRefAccess<AnimatedMouth, PushToTalkSetting>(pushToTalkField);
            AccessTools.FieldRef<AnimatedMouth, float> volumePeak =
                AccessTools.FieldRefAccess<AnimatedMouth, float>(volumePeakField);
            Func<AnimatedMouth, float[], float> micLevel =
                (Func<AnimatedMouth, float[], float>)micLevelMethod.CreateDelegate(
                    typeof(Func<AnimatedMouth, float[], float>));
            Func<PushToTalkSetting, PushToTalkSetting.PushToTalkType> getPushToTalkValue =
                EmitPushToTalkValueGetter(valueProperty.GetGetMethod(true));

            if (getAudioSource == null || lastBuffer == null || pushToTalk == null ||
                volumePeak == null || micLevel == null || getPushToTalkValue == null)
            {
                throw new InvalidOperationException("AnimatedMouthMaterial accessor creation failed");
            }

            _getAudioSource = getAudioSource;
            _lastBuffer = lastBuffer;
            _pushToTalkSetting = pushToTalk;
            _volumePeak = volumePeak;
            _micLevel = micLevel;
            _getPushToTalkValue = getPushToTalkValue;
            _state = new ConditionalWeakTable<AnimatedMouth, State>();
            // Resolved here and nowhere else: PropertyToID must not be called from the Prefix.
            _useTalkSpritesId = Shader.PropertyToID("_UseTalkSprites");
            _talkSpriteId = Shader.PropertyToID("_TalkSprite");
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(AnimatedMouth),
                "ProcessMicData",
                typeof(void),
                new[] { typeof(float[]) },
                false);
        }

        internal static void Reset()
        {
            _getAudioSource = null;
            _lastBuffer = null;
            _pushToTalkSetting = null;
            _volumePeak = null;
            _micLevel = null;
            _getPushToTalkValue = null;
            _state = null;
            _useTalkSpritesId = 0;
            _talkSpriteId = 0;
        }

        private static Func<PushToTalkSetting, PushToTalkSetting.PushToTalkType> EmitPushToTalkValueGetter(
            MethodInfo getter)
        {
            var method = new DynamicMethod(
                "PSO_GetPushToTalkValue",
                typeof(PushToTalkSetting.PushToTalkType),
                new[] { typeof(PushToTalkSetting) },
                typeof(AnimatedMouthMaterialPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, getter.DeclaringType);
            il.Emit(OpCodes.Callvirt, getter);
            il.Emit(OpCodes.Ret);
            return (Func<PushToTalkSetting, PushToTalkSetting.PushToTalkType>)method.CreateDelegate(
                typeof(Func<PushToTalkSetting, PushToTalkSetting.PushToTalkType>));
        }

        internal static bool Prefix(AnimatedMouth __instance, float[] buffer)
        {
            Func<AnimatedMouth, UnityEngine.Object> getAudioSource = _getAudioSource;
            AccessTools.FieldRef<AnimatedMouth, float[]> lastBufferRef = _lastBuffer;
            AccessTools.FieldRef<AnimatedMouth, PushToTalkSetting> pushToTalkRef = _pushToTalkSetting;
            AccessTools.FieldRef<AnimatedMouth, float> volumePeakRef = _volumePeak;
            Func<AnimatedMouth, float[], float> micLevel = _micLevel;
            Func<PushToTalkSetting, PushToTalkSetting.PushToTalkType> getPushToTalkValue = _getPushToTalkValue;
            ConditionalWeakTable<AnimatedMouth, State> state = _state;

            if (getAudioSource == null || lastBufferRef == null || pushToTalkRef == null ||
                volumePeakRef == null || micLevel == null || getPushToTalkValue == null || state == null)
            {
                RateLimitedDiagnostics.Hit("AnimatedMouthMaterial.accessors-unavailable");
                return true;
            }

            // AnimatedMouth.cs:128 outer guard, first disjunct. Evaluated before this patch's own
            // dependency checks on purpose: with a null audioSource the audited body is skipped in its
            // entirety and performs no write at all, so returning false here is observably identical to
            // handing the method back, and it is the only ordering under which the two diagnostics
            // below cannot fire on a mouth vanilla never faults on.
            //
            // The previous order tested mouthRenderer and mouthTextures first, which meant a mouth with
            // *no audio source and* an unassigned renderer or an empty texture array recorded a repair
            // reason on every single frame - the DetailBodypartThrottlePatch.cs:197-203 ban - for a
            // state in which vanilla is perfectly healthy. Nothing is lost by moving it: the checks
            // below are pure reads, and the replacement does not need either member on this path.
            if (getAudioSource(__instance) == null)
                return false;

            // Both members below are dereferenced unguarded by the audited body once the guard passes -
            // mouthRenderer.material at AnimatedMouth.cs:161/:167/:170 and mouthTextures.Length at
            // :169 - so reaching either check really is a vanilla fault, which is what makes the
            // diagnostics legitimate here rather than accounting for a legal state.
            Renderer mouthRenderer = __instance.mouthRenderer;
            if (mouthRenderer == null)
            {
                RateLimitedDiagnostics.Hit("AnimatedMouthMaterial.mouthRenderer-null");
                return true;
            }

            Texture2D[] mouthTextures = __instance.mouthTextures;
            if (mouthTextures == null || mouthTextures.Length == 0)
            {
                RateLimitedDiagnostics.Hit("AnimatedMouthMaterial.mouthTextures-empty");
                return true;
            }

            // AnimatedMouth.cs:128 outer guard, remaining disjuncts:
            //   isGhost || !(character != null) || (!dead && !passedOut)
            // Two of the three tolerate a character that is not there: `isGhost` and
            // `!(character != null)`. Both let the guard pass, and the audited body then dereferences
            // character immediately at AnimatedMouth.cs:130 (`character.IsLocal`), so a
            // *managed*-null character makes vanilla throw once per frame. Handing the method back
            // untouched is the honest reproduction of that; the replacement cannot continue, because
            // it reads character for the same IsLocal.
            //
            // Pure reference comparison, deliberately not Unity's `== null`. Everything the
            // replacement reads off character is managed and therefore survives a destroyed native
            // half: `data` and `input` are plain fields (Character.cs:121 and :119), `dead` is a
            // property whose getter is a bare `_dead` read (CharacterData.cs:320-325), `passedOut` is
            // a plain field (CharacterData.cs:17), and IsLocal is `this == localCharacter`
            // (Character.cs:234), a comparison rather than a dereference. Vanilla keeps running the
            // body for a destroyed character, so testing Unity-null here would abandon the method on
            // frames where vanilla does not - a regression, not a safety net.
            //
            // No RateLimitedDiagnostics.Hit on this path. It is neither a repair nor a fallback
            // around a fault of this patch's own making: it is a legal state the replacement does not
            // cover. A ghost mouth whose audioSource has no Character anywhere up its parent chain
            // gets exactly this (AnimatedMouth.cs:65-69), and it would then be reached on every
            // single frame - precisely what the ban in
            // Patches\ScaleExperimental\DetailBodypartThrottlePatch.cs:197-203 forbids - while also
            // poisoning a counter that is read as evidence of vanilla faults.
            Character character = __instance.character;
            if ((object)character == null)
                return true;

            // `character != null` is Unity's alive test, which is what the audited
            // `!(character != null)` disjunct is: once the native half is gone vanilla never looks at
            // dead/passedOut and runs the body regardless. So this term stays Unity-semantic even
            // though the two reads behind it are managed - dropping it would let a destroyed-but-dead
            // character skip a frame vanilla still animates.
            if (!__instance.isGhost && character != null &&
                (character.data.dead || character.data.passedOut))
            {
                return false;
            }

            bool isLocal = character.IsLocal;

            // AnimatedMouth.cs:130-134
            float level;
            if (isLocal)
            {
                level = micLevel(__instance, buffer);
            }
            else
            {
                level = __instance.VoiceHandler.LastReadSquaredMax;
            }
            if (!isLocal)
            {
                lastBufferRef(__instance) = __instance.VoiceHandler.LastAudioBuffer;
            }

            // AnimatedMouth.cs:135-139
            float time = AnimatedMouth.MicrophoneLevelMaxDecibels(level);
            PushToTalkSetting pushToTalk = pushToTalkRef(__instance);
            PushToTalkSetting.PushToTalkType pushToTalkValue = getPushToTalkValue(pushToTalk);
            if (isLocal &&
                ((pushToTalkValue == PushToTalkSetting.PushToTalkType.PushToTalk &&
                  !character.input.pushToTalkPressed) ||
                 (pushToTalkValue == PushToTalkSetting.PushToTalkType.PushToMute &&
                  character.input.pushToTalkPressed)))
            {
                time = -80f;
            }

            // AnimatedMouth.cs:140-158
            float num = __instance.decibelToAmountCurve.Evaluate(time);
            if (num > __instance.amplitudePeakLimiter)
                __instance.amplitudePeakLimiter = num;
            if (__instance.amplitudePeakLimiter > __instance.minAmplitudeThreshold)
            {
                __instance.amplitudePeakLimiter -= __instance.amplitudeHighestDecay * Time.deltaTime;
                __instance.amplitudePeakLimiter =
                    Mathf.Max(__instance.amplitudePeakLimiter, __instance.minAmplitudeThreshold);
            }

            __instance.volume = num / __instance.amplitudePeakLimiter;

            float volumePeak = volumePeakRef(__instance);
            if (__instance.volume > volumePeak)
            {
                volumePeak = Mathf.Lerp(volumePeak, __instance.volume, Time.deltaTime * __instance.amplitudeSmoothing);
            }
            else
            {
                volumePeak = Mathf.Lerp(volumePeak, 0f, Time.deltaTime * __instance.amplitudeSmoothing);
            }
            volumePeakRef(__instance) = volumePeak;

            // AnimatedMouth.cs:159-168
            bool speaking;
            int useTalkSprites;
            if (volumePeak > __instance.talkThreshold)
            {
                useTalkSprites = 1;
                speaking = true;
            }
            else
            {
                speaking = false;
                useTalkSprites = 0;
            }

            __instance.isSpeaking = speaking;

            State st = state.GetValue(__instance, _ => new State());
            Material material = mouthRenderer.material;
            // MushroomZombie.UpdateMouth and other mods may write the same material between calls.
            // The cache is therefore only a fast history hint; the actual material remains the
            // source of truth before an idempotent write is skipped. Because the cache comparison is
            // false in the steady state, the right-hand read runs every frame, so it uses the
            // cached int id rather than re-hashing the property name natively each time.
            if (st.LastUseTalkSprites != useTalkSprites ||
                material.GetInt(_useTalkSpritesId) != useTalkSprites)
            {
                material.SetInt(_useTalkSpritesId, useTalkSprites);
                st.LastUseTalkSprites = useTalkSprites;
            }

            // AnimatedMouth.cs:169-170
            int amplitudeIndex = (int)(__instance.NormalizedAmplitude * (float)(mouthTextures.Length - 1));
            __instance.amplitudeIndex = amplitudeIndex;
            Texture2D targetTexture = mouthTextures[amplitudeIndex];
            if (st.LastAmplitudeIndex != amplitudeIndex ||
                material.GetTexture(_talkSpriteId) != targetTexture)
            {
                material.SetTexture(_talkSpriteId, targetTexture);
                st.LastAmplitudeIndex = amplitudeIndex;
            }

            return false;
        }
    }
}
