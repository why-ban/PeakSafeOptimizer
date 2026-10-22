using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Fast path for CharacterMovement.StandOnPlayer(Collision). CharacterRagdoll registers every
    /// bodypart capsule with its Character, so a dictionary hit is the same Character that
    /// GetComponentInParent would return. A miss defers to the original, which keeps non-bodypart
    /// colliders behaving as before.
    /// </summary>
    internal static class CollisionCharacterLookupPatch
    {
        private const string CollisionTypeName = "UnityEngine.Collision";
        private const string ColliderTypeName = "UnityEngine.Collider";
        private const string RigidbodyTypeName = "UnityEngine.Rigidbody";
        private const string CapsuleColliderTypeName = "UnityEngine.CapsuleCollider";

        private static AccessTools.FieldRef<CharacterMovement, Character> _character;
        private static Func<object, bool> _hasRigidbody;
        private static Func<object, Character> _tryGetCharacter;

        internal static void Initialize()
        {
            MethodInfo target = ResolveAndValidateTarget();

            FieldInfo characterField = ScaleValidation.RequireInstanceField(
                typeof(CharacterMovement), "character", typeof(Character));
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "sincePalJump", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "isCrouching", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "sinceStandOnPlayer", typeof(float));
            ScaleValidation.RequireInstanceField(
                typeof(CharacterData), "lastStoodOnPlayer", typeof(Character));

            Type collisionType = target.GetParameters()[0].ParameterType;
            if (!string.Equals(collisionType.FullName, CollisionTypeName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("CharacterMovement.StandOnPlayer collision type mismatch");
            }

            PropertyInfo colliderProperty = ScaleValidation.RequireInstancePropertyByTypeName(
                collisionType, "collider", ColliderTypeName, requireSetter: false);
            PropertyInfo rigidbodyProperty = ScaleValidation.RequireInstancePropertyByTypeName(
                collisionType, "rigidbody", RigidbodyTypeName, requireSetter: false);
            Type colliderType = colliderProperty.PropertyType;
            Type colliderMapType = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(
                colliderType, typeof(Character));

            MethodInfo tryGetCharacter = ScaleValidation.RequireStaticMethod(
                typeof(CharacterRagdoll),
                "TryGetCharacterFromCollider",
                typeof(bool),
                new[] { colliderType, typeof(Character).MakeByRefType() },
                true);

            ScaleValidation.RequireStaticField(
                typeof(CharacterRagdoll), "COLLIDERS_TO_CHARACTERS", colliderMapType);
            ScaleValidation.RequireInstanceField(
                typeof(CharacterRagdoll), "partList", typeof(System.Collections.Generic.List<Bodypart>));
            ScaleValidation.RequireInstanceField(typeof(CharacterRagdoll), "character", typeof(Character));
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "Start", typeof(void), Type.EmptyTypes, false);
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "RegisterBodypartColliders", typeof(void), Type.EmptyTypes, false);
            ScaleValidation.RequireInstanceField(
                typeof(Bodypart), "colliders", typeof(System.Collections.Generic.List<RigCreatorCollider>));
            MethodInfo colMethod = ScaleValidation.RequireInstanceMethodByTypeNames(
                typeof(RigCreatorCollider), "Col", CapsuleColliderTypeName, Array.Empty<string>());
            if (!colliderType.IsAssignableFrom(colMethod.ReturnType))
                throw new InvalidOperationException("RigCreatorCollider.Col is not a Collider");

            AccessTools.FieldRef<CharacterMovement, Character> character =
                AccessTools.FieldRefAccess<CharacterMovement, Character>(characterField);
            Func<object, bool> hasRigidbody = EmitHasRigidbody(
                collisionType, rigidbodyProperty.GetGetMethod(true));
            Func<object, Character> lookup = EmitCharacterLookup(
                collisionType, colliderProperty.GetGetMethod(true), tryGetCharacter);
            if (character == null || hasRigidbody == null || lookup == null)
                throw new InvalidOperationException("CollisionCharacterLookup accessor creation failed");

            _character = character;
            _hasRigidbody = hasRigidbody;
            _tryGetCharacter = lookup;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethodByTypeNames(
                typeof(CharacterMovement),
                "StandOnPlayer",
                typeof(bool).FullName,
                new[] { CollisionTypeName });
        }

        internal static void Reset()
        {
            _character = null;
            _hasRigidbody = null;
            _tryGetCharacter = null;
        }

        internal static bool Prefix(CharacterMovement __instance, object collision, ref bool __result)
        {
            AccessTools.FieldRef<CharacterMovement, Character> characterRef = _character;
            Func<object, bool> hasRigidbody = _hasRigidbody;
            Func<object, Character> lookup = _tryGetCharacter;
            if (characterRef == null || hasRigidbody == null || lookup == null)
                return true;

            Character character = characterRef(__instance);
            if (character == null || character.data == null || collision == null)
                return true;

            CharacterData data = character.data;
            if (data.sincePalJump < 0.5f || data.isCrouching)
            {
                __result = false;
                return false;
            }

            if (!hasRigidbody(collision))
            {
                __result = false;
                return false;
            }

            Character stoodOn = lookup(collision);
            if (stoodOn == null || stoodOn.data == null)
                return true;

            if (stoodOn == character || !stoodOn.data.isCrouching)
            {
                __result = false;
                return false;
            }

            data.sinceStandOnPlayer = 0f;
            data.lastStoodOnPlayer = stoodOn;
            __result = true;
            return false;
        }

        private static Func<object, bool> EmitHasRigidbody(
            Type collisionType, MethodInfo rigidbodyGetter)
        {
            if (rigidbodyGetter == null || rigidbodyGetter.IsStatic)
                throw new MissingMethodException(CollisionTypeName, "get_rigidbody");

            var method = new DynamicMethod(
                "PSO_CollisionHasRigidbody",
                typeof(bool),
                new[] { typeof(object) },
                typeof(CollisionCharacterLookupPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, collisionType);
            il.Emit(OpCodes.Callvirt, rigidbodyGetter);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Call, ResolveUnityObjectInequality());
            il.Emit(OpCodes.Ret);
            return (Func<object, bool>)method.CreateDelegate(typeof(Func<object, bool>));
        }

        private static Func<object, Character> EmitCharacterLookup(
            Type collisionType, MethodInfo colliderGetter, MethodInfo tryGetCharacter)
        {
            if (colliderGetter == null || colliderGetter.IsStatic)
                throw new MissingMethodException(CollisionTypeName, "get_collider");

            var method = new DynamicMethod(
                "PSO_CollisionCharacterLookup",
                typeof(Character),
                new[] { typeof(object) },
                typeof(CollisionCharacterLookupPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            LocalBuilder result = il.DeclareLocal(typeof(Character));
            Label hit = il.DefineLabel();
            Label noCollider = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, collisionType);
            il.Emit(OpCodes.Callvirt, colliderGetter);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Call, ResolveUnityObjectInequality());
            il.Emit(OpCodes.Brfalse_S, noCollider);
            il.Emit(OpCodes.Ldloca_S, result);
            il.Emit(OpCodes.Call, tryGetCharacter);
            il.Emit(OpCodes.Brtrue_S, hit);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(noCollider);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(hit);
            il.Emit(OpCodes.Ldloc, result);
            il.Emit(OpCodes.Ret);
            return (Func<object, Character>)method.CreateDelegate(typeof(Func<object, Character>));
        }

        private static MethodInfo ResolveUnityObjectInequality()
        {
            MethodInfo method = typeof(UnityEngine.Object).GetMethod(
                "op_Inequality",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(UnityEngine.Object), typeof(UnityEngine.Object) },
                null);
            if (method == null || method.ReturnType != typeof(bool))
                throw new MissingMethodException("UnityEngine.Object", "op_Inequality");
            return method;
        }
    }
}
