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
    /// Full equivalent replacement of AnimatedMouth.ProcessMicData(float[]). Both material writes are
    /// idempotent and the material stays the source of truth, so a write is skipped only while it already
    /// holds the value. isSpeaking is still assigned every frame because PlayerEyeLook.Update reads it.
    /// The character aliveness test stays a Unity one because vanilla's guard is one, while everything read
    /// off it is managed. The push-to-talk setting is read only for a local, alive character, and a remote
    /// mouth with no voice handler is skipped the way vanilla's guard skips it.
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

            ScaleValidation.RequireStaticMethod(
                typeof(AnimatedMouth), "MicrophoneLevelMaxDecibels", typeof(float),
                new[] { typeof(float) }, true);
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
            // Resolved here and nowhere else, PropertyToID must never run on the Prefix path
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

            // A null audioSource skips the whole body, so returning false matches handing it back
            if (getAudioSource(__instance) == null)
                return false;

            Character character = __instance.character;
            if ((object)character == null)
                return true;

            bool characterAlive = character != null;
            if (!__instance.isGhost && characterAlive &&
                (character.data.dead || character.data.passedOut))
            {
                return false;
            }

            bool isLocal = character.IsLocal;
            // Vanilla's guard skips the body for a remote character without a voice handler, so the two
            // reads below would fault where vanilla never reaches them. It sits above the renderer
            // guards so that a legal state cannot report a repair.
            if (!isLocal && __instance.VoiceHandler == null)
                return false;

            // Dereferenced unguarded once the guard passes, so a null here is a vanilla fault
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

            float time = AnimatedMouth.MicrophoneLevelMaxDecibels(level);
            if (characterAlive && isLocal)
            {
                PushToTalkSetting pushToTalk = pushToTalkRef(__instance);
                PushToTalkSetting.PushToTalkType pushToTalkValue = getPushToTalkValue(pushToTalk);
                if ((pushToTalkValue == PushToTalkSetting.PushToTalkType.PushToTalk &&
                     !character.input.pushToTalkPressed) ||
                    (pushToTalkValue == PushToTalkSetting.PushToTalkType.PushToMute &&
                     character.input.pushToTalkPressed))
                {
                    time = -80f;
                }
            }

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
            if (st.LastUseTalkSprites != useTalkSprites ||
                material.GetInt(_useTalkSpritesId) != useTalkSprites)
            {
                material.SetInt(_useTalkSpritesId, useTalkSprites);
                st.LastUseTalkSprites = useTalkSprites;
            }

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
