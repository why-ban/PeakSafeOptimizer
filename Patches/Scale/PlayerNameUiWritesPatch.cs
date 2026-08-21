using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Peak.Network;
using Photon.Pun;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Full equivalent replacement of UIPlayerNames.UpdateName(int, Vector3, bool, int) (audited
    /// source UIPlayerNames.cs:34-82). Screen position and all gameplay/UI state transitions remain
    /// ordered as in vanilla. Image.sprite, GameObject.SetActive and CanvasGroup.alpha are only
    /// written when their target differs. Host ownership is read once on the visible non-muted path.
    /// Per-instance state is a ConditionalWeakTable whose value never references its UIPlayerNames
    /// key; it tracks the last sprite written per PlayerName slot.
    /// </summary>
    internal static class PlayerNameUiWritesPatch
    {
        private sealed class State
        {
            internal UnityEngine.Object[] LastSprites;
        }

        private static Func<MainCamera, UnityEngine.Object> _getCamera;
        private static Func<UnityEngine.Object, Vector3, Vector3> _worldToScreenPoint;
        private static Func<UIPlayerNames, object> _getCannibalismSetting;
        private static Func<PhotonView, object> _getPhotonOwner;
        private static Func<object, bool> _getIsMasterClient;
        private static Func<PlayerName, UnityEngine.Object> _getGroup;
        private static Func<PlayerName, UnityEngine.Object> _getImage;
        private static Func<PlayerName, float> _getAlpha;
        private static Action<PlayerName, float> _setAlpha;
        private static Func<PlayerName, UnityEngine.Object> _getSprite;
        private static Action<PlayerName, UnityEngine.Object> _setSprite;
        private static Func<UIPlayerNames, Character, bool> _canCannibalize;
        private static ConditionalWeakTable<UIPlayerNames, State> _states;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            Type owner = typeof(UIPlayerNames);
            ScaleValidation.RequireInstanceField(owner, "playerNameText", typeof(PlayerName[]));
            ScaleValidation.RequireInstanceField(owner, "audioSprites", typeof(Sprite[]));
            ScaleValidation.RequireInstanceField(owner, "mutedAudioSprite", typeof(Sprite));
            FieldInfo cannibalismField = ScaleValidation.RequireInstanceField(
                owner, "localCannibalismSetting", typeof(CannibalismSetting));
            ScaleValidation.RequireInstanceField(owner, "audioImageTimeoutMax", typeof(float));
            FieldInfo thresholdField = ScaleValidation.RequireStaticField(
                owner, "CANNIBAL_HUNGER_THRESHOLD", typeof(float));
            if (!thresholdField.IsPublic)
                throw new InvalidOperationException("UIPlayerNames.CANNIBAL_HUNGER_THRESHOLD is not public");
            MethodInfo canCannibalize = ScaleValidation.RequireInstanceMethod(
                owner, "CanCannibalize", typeof(bool), new[] { typeof(Character) }, false);

            FieldInfo groupField = ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(PlayerName), "group", "UnityEngine.CanvasGroup");
            FieldInfo imageField = ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(PlayerName), "audioImage", "UnityEngine.UI.Image");
            ScaleValidation.RequireInstanceField(typeof(PlayerName), "hostStar", typeof(GameObject));
            ScaleValidation.RequireInstanceField(typeof(PlayerName), "audioImageTimeout", typeof(float));
            ScaleValidation.RequireInstanceField(
                typeof(PlayerName), "characterInteractable", typeof(CharacterInteractible));
            ScaleValidation.RequireInheritedInstanceProperty(
                typeof(PlayerName), "transform", typeof(Transform));
            ScaleValidation.RequireInheritedInstanceProperty(
                typeof(PlayerName), "gameObject", typeof(GameObject));
            ScaleValidation.RequireInstanceField(
                typeof(CharacterInteractible), "character", typeof(Character));

            ScaleValidation.RequireStaticField(typeof(Character), "localCharacter", typeof(Character));
            ScaleValidation.RequireInstanceField(typeof(Character), "isBot", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceField(typeof(Character), "refs", typeof(Character.CharacterRefs));
            ScaleValidation.RequireInstanceField(
                typeof(CharacterData), "fullyPassedOut", typeof(bool));
            ScaleValidation.RequireInstanceProperty(
                typeof(CharacterData), "fullyConscious", typeof(bool));
            ScaleValidation.RequireInstanceProperty(
                typeof(CharacterData), "cannibalismPermitted", typeof(bool));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "customization", typeof(CharacterCustomization));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "afflictions", typeof(CharacterAfflictions));
            ScaleValidation.RequireInstanceField(
                typeof(CharacterCustomization), "isCannibalizable", typeof(bool));
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterCustomization), "BecomeChicken", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterCustomization), "BecomeHuman", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterAfflictions),
                "GetCurrentStatus",
                typeof(float),
                new[] { typeof(CharacterAfflictions.STATUSTYPE) },
                true);
            if (!Enum.IsDefined(typeof(CharacterAfflictions.STATUSTYPE), CharacterAfflictions.STATUSTYPE.Hunger))
            {
                throw new InvalidOperationException("Player name enum values are no longer defined");
            }
            PropertyInfo cannibalismValue = ScaleValidation.RequireInstancePropertyByTypeName(
                typeof(CannibalismSetting), "Value", "Zorro.Settings.OffOnMode", requireSetter: false);
            if (!cannibalismValue.PropertyType.IsEnum ||
                !Enum.IsDefined(cannibalismValue.PropertyType, "ON"))
            {
                throw new InvalidOperationException("CannibalismSetting.Value structure mismatch");
            }

            ScaleValidation.RequireInstanceProperty(typeof(Character), "player", typeof(Player));
            ScaleValidation.RequireStaticMethod(
                typeof(NetworkingUtilities), "GetUserId", typeof(string), new[] { typeof(Player) }, true);
            ScaleValidation.RequireStaticMethod(
                typeof(AudioLevels), "GetPlayerLevel", typeof(float), new[] { typeof(string) }, true);
            ScaleValidation.RequireInheritedInstanceProperty(
                typeof(Character), "photonView", typeof(PhotonView));
            PropertyInfo ownerProperty = ScaleValidation.RequireInstancePropertyByTypeName(
                typeof(PhotonView), "Owner", "Photon.Realtime.Player", requireSetter: false);
            PropertyInfo isMasterClientProperty = ScaleValidation.RequireInstancePropertyByTypeName(
                ownerProperty.PropertyType, "IsMasterClient", typeof(bool).FullName, requireSetter: false);

            ScaleValidation.RequireStaticField(typeof(MainCamera), "instance", typeof(MainCamera));
            FieldInfo cameraField = ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(MainCamera), "cam", "UnityEngine.Camera");
            Type cameraType = cameraField.FieldType;
            MethodInfo worldToScreen = cameraType.GetMethod(
                "WorldToScreenPoint",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(Vector3) },
                null);
            if (worldToScreen == null || worldToScreen.DeclaringType != cameraType ||
                worldToScreen.ReturnType != typeof(Vector3) || worldToScreen.IsStatic)
                throw new MissingMethodException(cameraType.FullName, "WorldToScreenPoint(Vector3)");

            Type canvasGroupType = groupField.FieldType;
            Type imageType = imageField.FieldType;
            PropertyInfo alphaProperty = ScaleValidation.RequireInstancePropertyByTypeName(
                canvasGroupType, "alpha", typeof(float).FullName, requireSetter: true);
            PropertyInfo spriteProperty = ScaleValidation.RequireInstancePropertyByTypeName(
                imageType, "sprite", typeof(Sprite).FullName, requireSetter: true);
            ScaleValidation.RequireInstanceProperty(
                typeof(GameObject), "activeSelf", typeof(bool));
            ScaleValidation.RequireInstanceMethod(
                typeof(GameObject), "SetActive", typeof(void), new[] { typeof(bool) }, true);

            Func<MainCamera, UnityEngine.Object> getCamera =
                ScaleEmit.CreateUnityObjectFieldGetter<MainCamera>(cameraField);
            Func<UnityEngine.Object, Vector3, Vector3> worldToScreenDelegate =
                EmitWorldToScreen(cameraType, worldToScreen);
            Func<UIPlayerNames, object> getCannibalismSetting =
                EmitReferenceFieldGetter<UIPlayerNames>(cannibalismField, "PSO_GetCannibalismSetting");
            Func<PhotonView, object> getPhotonOwner =
                EmitReferencePropertyGetter<PhotonView>(ownerProperty, "PSO_GetPhotonOwner");
            Func<object, bool> getIsMasterClient =
                EmitBoolPropertyGetter(isMasterClientProperty, "PSO_GetIsMasterClient");
            Func<PlayerName, UnityEngine.Object> getGroup =
                ScaleEmit.CreateUnityObjectFieldGetter<PlayerName>(groupField);
            Func<PlayerName, UnityEngine.Object> getImage =
                ScaleEmit.CreateUnityObjectFieldGetter<PlayerName>(imageField);
            Func<PlayerName, float> getAlpha = EmitFloatFieldPropertyGetter(
                groupField, alphaProperty, "PSO_PlayerNameGetAlpha");
            Action<PlayerName, float> setAlpha = EmitFloatFieldPropertySetter(
                groupField, alphaProperty, "PSO_PlayerNameSetAlpha");
            Func<PlayerName, UnityEngine.Object> getSprite = EmitObjectFieldPropertyGetter(
                imageField, spriteProperty, "PSO_PlayerNameGetSprite");
            Action<PlayerName, UnityEngine.Object> setSprite = EmitObjectFieldPropertySetter(
                imageField, spriteProperty, "PSO_PlayerNameSetSprite");
            Func<UIPlayerNames, Character, bool> canCannibalizeDelegate =
                (Func<UIPlayerNames, Character, bool>)canCannibalize.CreateDelegate(
                    typeof(Func<UIPlayerNames, Character, bool>));
            if (getCamera == null || worldToScreenDelegate == null || getCannibalismSetting == null ||
                getPhotonOwner == null || getIsMasterClient == null || getGroup == null ||
                getImage == null || getAlpha == null || setAlpha == null || getSprite == null || setSprite == null ||
                canCannibalizeDelegate == null)
            {
                throw new InvalidOperationException("PlayerNameUiWrites accessor creation failed");
            }

            _getCamera = getCamera;
            _worldToScreenPoint = worldToScreenDelegate;
            _getCannibalismSetting = getCannibalismSetting;
            _getPhotonOwner = getPhotonOwner;
            _getIsMasterClient = getIsMasterClient;
            _getGroup = getGroup;
            _getImage = getImage;
            _getAlpha = getAlpha;
            _setAlpha = setAlpha;
            _getSprite = getSprite;
            _setSprite = setSprite;
            _canCannibalize = canCannibalizeDelegate;
            _states = new ConditionalWeakTable<UIPlayerNames, State>();
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(UIPlayerNames),
                "UpdateName",
                typeof(void),
                new[] { typeof(int), typeof(Vector3), typeof(bool), typeof(int) },
                true);
        }

        internal static void Reset()
        {
            _getCamera = null;
            _worldToScreenPoint = null;
            _getCannibalismSetting = null;
            _getPhotonOwner = null;
            _getIsMasterClient = null;
            _getGroup = null;
            _getImage = null;
            _getAlpha = null;
            _setAlpha = null;
            _getSprite = null;
            _setSprite = null;
            _canCannibalize = null;
            _states = null;
        }

        internal static bool Prefix(
            UIPlayerNames __instance,
            int index,
            Vector3 position,
            bool visible,
            int speakingAmplitude)
        {
            Func<MainCamera, UnityEngine.Object> getCamera = _getCamera;
            Func<UnityEngine.Object, Vector3, Vector3> worldToScreen = _worldToScreenPoint;
            Func<UIPlayerNames, object> getCannibalismSetting = _getCannibalismSetting;
            Func<PhotonView, object> getPhotonOwner = _getPhotonOwner;
            Func<object, bool> getIsMasterClient = _getIsMasterClient;
            Func<PlayerName, UnityEngine.Object> getGroup = _getGroup;
            Func<PlayerName, UnityEngine.Object> getImage = _getImage;
            Func<PlayerName, float> getAlpha = _getAlpha;
            Action<PlayerName, float> setAlpha = _setAlpha;
            Func<PlayerName, UnityEngine.Object> getSprite = _getSprite;
            Action<PlayerName, UnityEngine.Object> setSprite = _setSprite;
            Func<UIPlayerNames, Character, bool> canCannibalize = _canCannibalize;
            ConditionalWeakTable<UIPlayerNames, State> states = _states;
            if (getCamera == null || worldToScreen == null || getCannibalismSetting == null ||
                getPhotonOwner == null || getIsMasterClient == null || getGroup == null || getImage == null ||
                getAlpha == null || setAlpha == null || getSprite == null || setSprite == null ||
                canCannibalize == null || states == null)
            {
                return true;
            }

            Character local = Character.localCharacter;
            PlayerName[] names = __instance.playerNameText;
            if (local == null || names == null || index < 0 || index >= names.Length)
                return true;

            PlayerName name = names[index];
            MainCamera mainCamera = MainCamera.instance;
            if (name == null || mainCamera == null || getCamera(mainCamera) == null ||
                getGroup(name) == null || getImage(name) == null || name.hostStar == null ||
                name.characterInteractable == null || name.characterInteractable.character == null ||
                local.data == null || local.refs == null || local.refs.afflictions == null)
            {
                return true;
            }

            Character other = name.characterInteractable.character;
            if (other.data == null || other.refs == null || other.refs.customization == null)
                return true;

            Sprite[] audioSprites = __instance.audioSprites;
            Player otherPlayer = null;
            PhotonView view = null;
            object photonOwner = null;
            bool isMuted = false;
            if (visible)
            {
                otherPlayer = other.player;
                if (getCannibalismSetting(__instance) == null || audioSprites == null ||
                    audioSprites.Length == 0 || otherPlayer == null)
                {
                    return true;
                }

                string userId = NetworkingUtilities.GetUserId(otherPlayer);
                if (userId == null)
                    return true;
                isMuted = AudioLevels.GetPlayerLevel(userId) == 0f;
                if (!isMuted)
                {
                    view = other.photonView;
                    photonOwner = view != null ? getPhotonOwner(view) : null;
                    if (view == null || photonOwner == null)
                        return true;
                }
            }

            name.transform.position = worldToScreen(getCamera(mainCamera), position);

            if (visible)
            {
                if (canCannibalize(__instance, other))
                    other.refs.customization.BecomeChicken();

                SetActiveIfChanged(name.gameObject, true);
                MoveAlpha(name, 1f, getAlpha, setAlpha);

                if (name.characterInteractable && isMuted)
                {
                    SetSpriteIfChanged(__instance, name, index, __instance.mutedAudioSprite,
                        states, getSprite, setSprite);
                    return false;
                }

                if (speakingAmplitude <= 0)
                {
                    name.audioImageTimeout -= Time.deltaTime;
                    if (name.audioImageTimeout <= 0f)
                    {
                        SetSpriteIfChanged(__instance, name, index, audioSprites[0],
                            states, getSprite, setSprite);
                    }
                }
                else
                {
                    int spriteIndex = Mathf.Clamp(speakingAmplitude * 2, 0, audioSprites.Length - 1);
                    SetSpriteIfChanged(__instance, name, index, audioSprites[spriteIndex],
                        states, getSprite, setSprite);
                    name.audioImageTimeout = __instance.audioImageTimeoutMax;
                }

                bool isHost = getIsMasterClient(photonOwner);
                SetActiveIfChanged(name.hostStar, isHost);
            }
            else
            {
                MoveAlpha(name, 0f, getAlpha, setAlpha);
                if (getAlpha(name) < 0.01f && name.gameObject.activeSelf)
                {
                    other.refs.customization.BecomeHuman();
                    SetActiveIfChanged(name.gameObject, false);
                }
            }

            if ((local.data.fullyPassedOut ||
                 local.refs.afflictions.GetCurrentStatus(CharacterAfflictions.STATUSTYPE.Hunger) <
                 UIPlayerNames.CANNIBAL_HUNGER_THRESHOLD) && name.gameObject.activeSelf)
            {
                other.refs.customization.BecomeHuman();
            }

            return false;
        }

        private static void MoveAlpha(
            PlayerName name,
            float target,
            Func<PlayerName, float> getAlpha,
            Action<PlayerName, float> setAlpha)
        {
            float current = getAlpha(name);
            if (!Mathf.Approximately(current, target))
                setAlpha(name, Mathf.MoveTowards(current, target, Time.deltaTime * 5f));
        }

        private static void SetActiveIfChanged(GameObject gameObject, bool target)
        {
            if (gameObject.activeSelf != target)
                gameObject.SetActive(target);
        }

        private static void SetSpriteIfChanged(
            UIPlayerNames owner,
            PlayerName name,
            int index,
            Sprite target,
            ConditionalWeakTable<UIPlayerNames, State> states,
            Func<PlayerName, UnityEngine.Object> getSprite,
            Action<PlayerName, UnityEngine.Object> setSprite)
        {
            State state = states.GetValue(owner, _ => new State());
            UnityEngine.Object[] last = state.LastSprites;
            if (last == null || last.Length != owner.playerNameText.Length)
            {
                last = new UnityEngine.Object[owner.playerNameText.Length];
                state.LastSprites = last;
            }

            UnityEngine.Object current = getSprite(name);
            if (!ReferenceEquals(last[index], current))
                last[index] = current;
            if (ReferenceEquals(last[index], target))
                return;

            setSprite(name, target);
            last[index] = target;
        }

        private static Func<UnityEngine.Object, Vector3, Vector3> EmitWorldToScreen(
            Type cameraType, MethodInfo methodInfo)
        {
            var method = new DynamicMethod(
                "PSO_WorldToScreenPoint",
                typeof(Vector3),
                new[] { typeof(UnityEngine.Object), typeof(Vector3) },
                typeof(PlayerNameUiWritesPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, cameraType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, methodInfo);
            il.Emit(OpCodes.Ret);
            return (Func<UnityEngine.Object, Vector3, Vector3>)method.CreateDelegate(
                typeof(Func<UnityEngine.Object, Vector3, Vector3>));
        }

        private static Func<TInstance, object> EmitReferenceFieldGetter<TInstance>(
            FieldInfo field, string name)
        {
            if (field == null || field.IsStatic || field.DeclaringType != typeof(TInstance) ||
                field.FieldType.IsValueType)
            {
                throw new InvalidOperationException($"Unexpected reference field: {field?.Name}");
            }

            var method = new DynamicMethod(
                name,
                typeof(object),
                new[] { typeof(TInstance) },
                typeof(PlayerNameUiWritesPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<TInstance, object>)method.CreateDelegate(typeof(Func<TInstance, object>));
        }

        private static Func<TInstance, object> EmitReferencePropertyGetter<TInstance>(
            PropertyInfo property, string name)
        {
            MethodInfo getter = property?.GetGetMethod(true);
            if (property == null || getter == null || getter.IsStatic || property.PropertyType.IsValueType ||
                !property.DeclaringType.IsAssignableFrom(typeof(TInstance)))
            {
                throw new InvalidOperationException($"Unexpected reference property: {property?.Name}");
            }

            var method = new DynamicMethod(
                name,
                typeof(object),
                new[] { typeof(TInstance) },
                typeof(PlayerNameUiWritesPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, getter);
            il.Emit(OpCodes.Ret);
            return (Func<TInstance, object>)method.CreateDelegate(typeof(Func<TInstance, object>));
        }

        private static Func<object, bool> EmitBoolPropertyGetter(PropertyInfo property, string name)
        {
            MethodInfo getter = property?.GetGetMethod(true);
            if (property == null || getter == null || getter.IsStatic || property.PropertyType != typeof(bool))
                throw new InvalidOperationException($"Unexpected bool property: {property?.Name}");

            var method = new DynamicMethod(
                name,
                typeof(bool),
                new[] { typeof(object) },
                typeof(PlayerNameUiWritesPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, property.DeclaringType);
            il.Emit(OpCodes.Callvirt, getter);
            il.Emit(OpCodes.Ret);
            return (Func<object, bool>)method.CreateDelegate(typeof(Func<object, bool>));
        }

        private static Func<PlayerName, float> EmitFloatFieldPropertyGetter(
            FieldInfo field, PropertyInfo property, string name)
        {
            var method = new DynamicMethod(
                name,
                typeof(float),
                new[] { typeof(PlayerName) },
                typeof(PlayerNameUiWritesPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Callvirt, property.GetGetMethod(true));
            il.Emit(OpCodes.Ret);
            return (Func<PlayerName, float>)method.CreateDelegate(typeof(Func<PlayerName, float>));
        }

        private static Action<PlayerName, float> EmitFloatFieldPropertySetter(
            FieldInfo field, PropertyInfo property, string name)
        {
            var method = new DynamicMethod(
                name,
                typeof(void),
                new[] { typeof(PlayerName), typeof(float) },
                typeof(PlayerNameUiWritesPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, property.GetSetMethod(true));
            il.Emit(OpCodes.Ret);
            return (Action<PlayerName, float>)method.CreateDelegate(typeof(Action<PlayerName, float>));
        }

        private static Func<PlayerName, UnityEngine.Object> EmitObjectFieldPropertyGetter(
            FieldInfo field, PropertyInfo property, string name)
        {
            var method = new DynamicMethod(
                name,
                typeof(UnityEngine.Object),
                new[] { typeof(PlayerName) },
                typeof(PlayerNameUiWritesPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Callvirt, property.GetGetMethod(true));
            il.Emit(OpCodes.Ret);
            return (Func<PlayerName, UnityEngine.Object>)method.CreateDelegate(
                typeof(Func<PlayerName, UnityEngine.Object>));
        }

        private static Action<PlayerName, UnityEngine.Object> EmitObjectFieldPropertySetter(
            FieldInfo field, PropertyInfo property, string name)
        {
            var method = new DynamicMethod(
                name,
                typeof(void),
                new[] { typeof(PlayerName), typeof(UnityEngine.Object) },
                typeof(PlayerNameUiWritesPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Castclass, property.PropertyType);
            il.Emit(OpCodes.Callvirt, property.GetSetMethod(true));
            il.Emit(OpCodes.Ret);
            return (Action<PlayerName, UnityEngine.Object>)method.CreateDelegate(
                typeof(Action<PlayerName, UnityEngine.Object>));
        }

    }
}
