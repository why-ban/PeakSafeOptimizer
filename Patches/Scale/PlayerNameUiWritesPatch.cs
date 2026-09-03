using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Peak.Network;
using PeakSafeOptimizer.Diagnostics;
using Photon.Pun;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Full equivalent replacement of UIPlayerNames.UpdateName(int, Vector3, bool, int). Screen position
    /// and all gameplay/UI state transitions remain ordered as in vanilla. Image.sprite,
    /// GameObject.SetActive and CanvasGroup.alpha are only written when their target differs. Host
    /// ownership is read once on the visible non-muted path. Per-instance state is a
    /// ConditionalWeakTable whose value never references its UIPlayerNames key; it tracks the last
    /// sprite written per PlayerName slot.
    ///
    /// The game has since hoisted <c>playerNameText[index]</c> into a local of its own and added a null
    /// test on it, which is a subset of what this Prefix already did - the twenty repeated array index
    /// operations it used to perform are gone. The remaining redundancy this Prefix removes is the
    /// native accessors: the current body still reads <c>playerName.gameObject</c> up to four times,
    /// <c>group.alpha</c> twice on the invisible path, and <c>MainCamera.instance.cam</c> once per call,
    /// and it still writes sprite / SetActive / alpha unconditionally. Those are the writes and native
    /// calls this patch elides.
    ///
    /// The new null test is reproduced below (the <c>name == null</c> deferral), and it is a Unity alive
    /// test on the same reference, so a slot whose widget was destroyed is handled the same way in both
    /// - vanilla returns, this Prefix hands the call back to vanilla which returns.
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
            if (name == null || mainCamera == null)
                return true;

            // Every game member below is resolved exactly once and reused. All of them are stable for
            // the duration of this call, which is what makes the reuse bit-identical to the repeated
            // reads the previous revision performed:
            //   camera        - MainCamera.cam is written only in MainCamera.Awake (MainCamera.cs:17),
            //                   and the accessor is a plain Ldfld on that field, so a second read can
            //                   only return the same reference.
            //   nameObject    - Component.gameObject is a native FreeFunction (Component.cs:46-58) and
            //                   was previously called four times per invocation. A component's owning
            //                   GameObject cannot be reassigned, and nothing this method calls destroys
            //                   the name widget: the two customization calls only touch the character's
            //                   own renderers, particle and hat transform (CharacterCustomization.cs:
            //                   596-618 and :620-642).
            //   interactable  - PlayerName.characterInteractable is a plain managed field
            //                   (PlayerName.cs:17) written only by UIPlayerNames.Init
            //                   (UIPlayerNames.cs:24), which is not on this call path. The Unity alive
            //                   test vanilla applies to it at UIPlayerNames.cs:49 is preserved below on
            //                   this same reference, so a widget whose native half dies mid-call is
            //                   still treated exactly as before.
            //   other         - CharacterInteractible.character is a plain managed field written only
            //                   by CharacterInteractible.Start (CharacterInteractible.cs:14), which is
            //                   not on this call path, so the null test and the four later uses share
            //                   one read.
            //   localData / localRefs / localAfflictions / otherRefs / otherCustomization
            //                 - plain managed fields (Character.cs:119-123 and :18-58) written only by
            //                   Character.InitializeRefs (Character.cs:350-395), which is not reachable
            //                   from here.
            //
            // The guard is split across three blocks rather than being one long disjunction. That is
            // deliberate: a chained `interactable == null || interactable.character == null` would read
            // the field twice and alive-test twice, which is one native test more than vanilla pays at
            // UIPlayerNames.cs:49, so each Unity-typed reference is hoisted before it is tested. The
            // set of conditions and the verdict are unchanged - all of them are pure reads and every
            // one of them defers - so their order carries no meaning.
            UnityEngine.Object camera = getCamera(mainCamera);
            CharacterInteractible interactable = name.characterInteractable;
            Character.CharacterRefs localRefs = local.refs;
            if (camera == null || getGroup(name) == null || getImage(name) == null ||
                name.hostStar == null || interactable == null || localRefs == null)
            {
                return true;
            }

            Character other = interactable.character;
            CharacterData localData = local.data;
            CharacterAfflictions localAfflictions = localRefs.afflictions;
            if (other == null || localData == null || localAfflictions == null)
                return true;

            Character.CharacterRefs otherRefs = other.refs;
            if (otherRefs == null)
                return true;

            CharacterCustomization otherCustomization = otherRefs.customization;
            if (other.data == null || otherCustomization == null)
                return true;

            Sprite[] audioSprites = __instance.audioSprites;
            Player otherPlayer = null;
            PhotonView view = null;
            object photonOwner = null;
            bool isMuted = false;
            if (visible)
            {
                // Vanilla reads the Photon owner and the audio level after its first UI writes. Any
                // exception in that chain must reproduce through the untouched original, so it is caught
                // here and handed back before this patch writes anything.
                //
                // The chain got longer in this build and the catch is now doing real work rather than
                // guarding a theoretical case. NetworkingUtilities.GetUserId used to be a single
                // photonView.Owner.UserId read; it now dereferences Player.character.data, and on a
                // first call for a player it reads photonView.Owner.CustomProperties and runs
                // ulong.Parse on the stored value. Player.character, the Owner and the parse are three
                // independent throw sites where there used to be one.
                //
                // GetUserId also became side-effecting: on that first call it caches the parsed id into
                // CharacterData.userID. That is why the call stays exactly where vanilla makes it, on
                // the visible path only and only for a Unity-alive characterInteractable - the
                // interactable-null deferral above is what guarantees the second half, because vanilla
                // reaches GetUserId through a `(bool)characterInteractable &&` short circuit on the same
                // reference. The caching write is idempotent and order-independent with respect to
                // everything else here, so performing it before this patch's UI writes rather than after
                // them, as vanilla does, is not observable: a handed-back call re-runs GetUserId, finds
                // the id already cached and returns the same string.
                //
                // The one visible difference is a log line. When the player has no UserID custom
                // property GetUserId writes a Debug.LogError and leaves the id at 0; if this Prefix then
                // defers on the owner lookup below, vanilla runs the whole method again and logs it a
                // second time. That is a duplicated diagnostic in a state that is already broken, and
                // the alternative - deferring before calling GetUserId at all - would give up the patch
                // on every visible name.
                try
                {
                    otherPlayer = other.player;
                    if (getCannibalismSetting(__instance) == null || audioSprites == null ||
                        audioSprites.Length == 0 || otherPlayer == null)
                    {
                        return true;
                    }

                    // GetUserId now returns CharacterData.userID.ToString(), which cannot be null. The
                    // test is kept because it costs one branch and is what makes the following line
                    // safe if the accessor ever goes back to returning Owner.UserId, which could.
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
                catch (Exception)
                {
                    RateLimitedDiagnostics.Hit("PlayerNameUiWrites.owner-lookup-failed");
                    return true;
                }
            }

            name.transform.position = worldToScreen(camera, position);
            // Resolved after the last deferral so a handed-back call pays nothing for it, and before
            // both branches because each of them reads it at least once.
            GameObject nameObject = name.gameObject;
            // GameObject.activeSelf is a native property, and both the visible and the invisible branch
            // read it before the shared tail reads it again. It is tracked in a local instead, updated
            // in lockstep with every SetActive this method performs, so exactly one native read happens
            // per invocation where the previous revision did two.
            //
            // Nothing else between here and the tail can move it. BecomeChicken/BecomeHuman touch the
            // character's own renderers, chicken particle and hat transform only
            // (CharacterCustomization.cs:596-618, :620-642); SetSpriteIfChanged writes Image.sprite;
            // audioImageTimeout is a plain field (PlayerName.cs:15); and the hostStar SetActive below
            // is a different GameObject, whose own active flag cannot alter this one's.
            bool nameActive = nameObject.activeSelf;

            if (visible)
            {
                if (canCannibalize(__instance, other))
                    otherCustomization.BecomeChicken();

                // Vanilla calls SetActive(true) unconditionally (UIPlayerNames.cs:47); the read that
                // makes it conditional is the one folded into nameActive above.
                if (!nameActive)
                {
                    nameObject.SetActive(true);
                    nameActive = true;
                }
                MoveAlpha(name, 1f, getAlpha, setAlpha);

                if (interactable && isMuted)
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
                // MoveAlpha reports the value it left in CanvasGroup.alpha, which removes the second
                // native get_alpha (CanvasGroup.cs:12-22) this branch used to perform. It is not a
                // merge of two reads into one: vanilla writes alpha and then reads it back
                // (UIPlayerNames.cs:71-72), so the value tested here must be the post-write one. See
                // MoveAlpha for why its return value is exactly what the field holds afterwards.
                float alpha = MoveAlpha(name, 0f, getAlpha, setAlpha);
                if (alpha < 0.01f && nameActive)
                {
                    otherCustomization.BecomeHuman();
                    nameObject.SetActive(false);
                    nameActive = false;
                }
            }

            if ((localData.fullyPassedOut ||
                 localAfflictions.GetCurrentStatus(CharacterAfflictions.STATUSTYPE.Hunger) <
                 UIPlayerNames.CANNIBAL_HUNGER_THRESHOLD) && nameActive)
            {
                otherCustomization.BecomeHuman();
            }

            return false;
        }

        /// <summary>
        /// Vanilla's alpha step (UIPlayerNames.cs:48 and :71) with the write skipped when it would not
        /// move the value, and returning whatever CanvasGroup.alpha holds when it returns.
        ///
        /// The returned value is exactly the field contents, in both branches: when the write is taken
        /// it is the value just written, and when it is skipped the field still holds
        /// <c>current</c>. Nothing runs between the write and the caller's use of the result, so the
        /// caller can test it instead of paying a second native get_alpha (CanvasGroup.cs:12-22).
        ///
        /// The skip itself is invisible to the one consumer that reads the value back. Skipping
        /// requires Mathf.Approximately(current, target), and for the target 0f that leaves a value
        /// far below the 0.01f threshold the caller compares against - while vanilla, which always
        /// writes, would have landed on exactly 0f there. Both are below the threshold.
        /// </summary>
        private static float MoveAlpha(
            PlayerName name,
            float target,
            Func<PlayerName, float> getAlpha,
            Action<PlayerName, float> setAlpha)
        {
            float current = getAlpha(name);
            if (Mathf.Approximately(current, target))
                return current;
            float next = Mathf.MoveTowards(current, target, Time.deltaTime * 5f);
            setAlpha(name, next);
            return next;
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
