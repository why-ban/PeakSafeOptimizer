using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using Photon.Pun;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Full equivalent replacement of CharacterMovement.OnCollision(Collision, bool, Bodypart)
    /// (audited source CharacterMovement.cs:923-957). The original evaluates the
    /// <c>Collision.contacts</c> property six times (:928, :948, twice each at :953 and :955); this
    /// version reads contact 0 exactly once through <c>Collision.GetContact(0)</c>.
    ///
    /// Why that is bit-identical: <c>GetContact</c> (Collision.cs:104-117) returns
    /// <c>m_LegacyContacts[index]</c> whenever the array exists, and builds the same ContactPoint from
    /// <c>m_Pair.GetContactPoint_Internal</c> otherwise - with the same flip logic
    /// (<c>m_Flipped ? -1f : 1f</c> on the normal, swapped collider entity ids) that
    /// <c>ContactPair.ExtractContactsArray</c> (ContactPair.cs:110-137) applies when filling the
    /// array. So both accessors produce the same value for index 0 in either mode.
    ///
    /// Why it matters: with <c>Physics.reuseCollisionCallbacks</c> enabled the eager
    /// <c>ContactPoint[]</c> allocation in the Collision constructor (Collision.cs:87-94) is gone and
    /// <c>.contacts</c> becomes a lazy allocation per callback (Collision.cs:66-77). This bodypart
    /// callback is by far the most frequent contact consumer in a clustered lobby, so removing its
    /// <c>.contacts</c> use is what turns that lazy allocation into no allocation at all.
    ///
    /// Everything else is reproduced in original order: the CollisionModifier lookup and
    /// Collide/standable/CanStand gate, the StandOnPlayer / StandableRig decision, the rocket RPC
    /// branch, the AcceptableAngle test and both AddGroundSample calls with the same arguments.
    /// StandOnPlayer, StandableRig, DoGroundChecks and AcceptableAngle are invoked through delegates
    /// bound to the game's own methods, so other patches on them (CollisionCharacterLookup) keep
    /// applying. The zero-contact edge case is the one intentional deviation: vanilla throws
    /// IndexOutOfRangeException there, and this Prefix defers to the untouched original so that
    /// behaviour - including the exception - stays with the game.
    /// </summary>
    internal static class CollisionContactsNoAllocPatch
    {
        private const string RocketRpcName = "RocketExplodeRPC";

        private static AccessTools.FieldRef<CharacterMovement, Character> _character;
        private static AccessTools.FieldRef<CharacterMovement, bool> _rocketActive;
        private static AccessTools.FieldRef<CharacterMovement, float> _currentRocketTime;
        private static Func<CharacterMovement, Collision, bool> _standOnPlayer;
        private static Func<CharacterMovement, Rigidbody, bool> _standableRig;
        private static Func<CharacterMovement, bool> _doGroundChecks;
        private static Func<CharacterMovement, float, bool> _acceptableAngle;
        private static Func<CollisionModifier, Character, bool> _canStand;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo characterField = ScaleValidation.RequireInstanceField(
                typeof(CharacterMovement), "character", typeof(Character));
            FieldInfo rocketActiveField = ScaleValidation.RequireInstanceField(
                typeof(CharacterMovement), "rocketActive", typeof(bool));
            FieldInfo currentRocketTimeField = ScaleValidation.RequireInstanceField(
                typeof(CharacterMovement), "_currentRocketTime", typeof(float));
            MethodInfo standOnPlayer = ScaleValidation.RequireInstanceMethod(
                typeof(CharacterMovement), "StandOnPlayer", typeof(bool), new[] { typeof(Collision) }, false);
            MethodInfo standableRig = ScaleValidation.RequireInstanceMethod(
                typeof(CharacterMovement), "StandableRig", typeof(bool), new[] { typeof(Rigidbody) }, false);
            MethodInfo doGroundChecks = ScaleValidation.RequireInstanceMethod(
                typeof(CharacterMovement), "DoGroundChecks", typeof(bool), Type.EmptyTypes, false);
            MethodInfo acceptableAngle = ScaleValidation.RequireInstanceMethod(
                typeof(CharacterMovement), "AcceptableAngle", typeof(bool), new[] { typeof(float) }, false);
            // internal, so it is bound as a delegate rather than called directly.
            MethodInfo canStand = ScaleValidation.RequireInstanceMethod(
                typeof(CollisionModifier), "CanStand", typeof(bool), new[] { typeof(Character) }, false);

            // Members the replacement calls directly, proven to still have the audited shape.
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterMovement), "AddGroundSample", typeof(void),
                new[] { typeof(CharacterMovement.PlayerGroundSample) }, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterMovement), "AddGroundSample_All", typeof(void),
                new[] { typeof(CharacterMovement.PlayerGroundSample) }, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(CollisionModifier),
                "Collide",
                typeof(void),
                new[] { typeof(Character), typeof(ContactPoint), typeof(Collision), typeof(Bodypart) },
                true);
            ScaleValidation.RequireInstanceField(typeof(CollisionModifier), "standable", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(Character), "refs", typeof(Character.CharacterRefs));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "view", typeof(PhotonView));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "IsLocal", typeof(bool));
            RequireRocketRpc();
            RequireGroundSampleShape();

            AccessTools.FieldRef<CharacterMovement, Character> character =
                AccessTools.FieldRefAccess<CharacterMovement, Character>(characterField);
            AccessTools.FieldRef<CharacterMovement, bool> rocketActive =
                AccessTools.FieldRefAccess<CharacterMovement, bool>(rocketActiveField);
            AccessTools.FieldRef<CharacterMovement, float> currentRocketTime =
                AccessTools.FieldRefAccess<CharacterMovement, float>(currentRocketTimeField);
            var standOnPlayerDelegate = (Func<CharacterMovement, Collision, bool>)
                standOnPlayer.CreateDelegate(typeof(Func<CharacterMovement, Collision, bool>));
            var standableRigDelegate = (Func<CharacterMovement, Rigidbody, bool>)
                standableRig.CreateDelegate(typeof(Func<CharacterMovement, Rigidbody, bool>));
            var doGroundChecksDelegate = (Func<CharacterMovement, bool>)
                doGroundChecks.CreateDelegate(typeof(Func<CharacterMovement, bool>));
            var acceptableAngleDelegate = (Func<CharacterMovement, float, bool>)
                acceptableAngle.CreateDelegate(typeof(Func<CharacterMovement, float, bool>));
            var canStandDelegate = (Func<CollisionModifier, Character, bool>)
                canStand.CreateDelegate(typeof(Func<CollisionModifier, Character, bool>));
            if (character == null || rocketActive == null || currentRocketTime == null ||
                standOnPlayerDelegate == null || standableRigDelegate == null ||
                doGroundChecksDelegate == null || acceptableAngleDelegate == null || canStandDelegate == null)
            {
                throw new InvalidOperationException("CollisionContactsNoAlloc accessor creation failed");
            }

            _character = character;
            _rocketActive = rocketActive;
            _currentRocketTime = currentRocketTime;
            _standOnPlayer = standOnPlayerDelegate;
            _standableRig = standableRigDelegate;
            _doGroundChecks = doGroundChecksDelegate;
            _acceptableAngle = acceptableAngleDelegate;
            _canStand = canStandDelegate;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(CharacterMovement),
                "OnCollision",
                typeof(void),
                new[] { typeof(Collision), typeof(bool), typeof(Bodypart) },
                requirePublic: false);
        }

        internal static void Reset()
        {
            _character = null;
            _rocketActive = null;
            _currentRocketTime = null;
            _standOnPlayer = null;
            _standableRig = null;
            _doGroundChecks = null;
            _acceptableAngle = null;
            _canStand = null;
        }

        /// <summary>
        /// Hot path: once per reported bodypart contact, i.e. thousands per second in a cluster. No
        /// diagnostics on the reachable branches for that reason.
        /// </summary>
        internal static bool Prefix(
            CharacterMovement __instance, Collision collision, bool collisionEnter, Bodypart bodypart)
        {
            AccessTools.FieldRef<CharacterMovement, Character> characterRef = _character;
            Func<CharacterMovement, Collision, bool> standOnPlayer = _standOnPlayer;
            Func<CharacterMovement, Rigidbody, bool> standableRig = _standableRig;
            Func<CharacterMovement, bool> doGroundChecks = _doGroundChecks;
            Func<CharacterMovement, float, bool> acceptableAngle = _acceptableAngle;
            Func<CollisionModifier, Character, bool> canStand = _canStand;
            if (characterRef == null || standOnPlayer == null || standableRig == null ||
                doGroundChecks == null || acceptableAngle == null || canStand == null ||
                __instance == null || collision == null)
            {
                return true;
            }

            Character character = characterRef(__instance);
            if (character == null)
                return true;

            Collider collider = collision.collider;
            if (collider == null)
            {
                // The original dereferences collision.collider unconditionally; keep its behaviour.
                return true;
            }

            // Vanilla would throw IndexOutOfRangeException on contacts[0] with no contact points.
            // Leave that to the original rather than inventing a different outcome.
            if (collision.contactCount <= 0)
                return true;

            ContactPoint contact = collision.GetContact(0);

            CollisionModifier component = collider.GetComponent<CollisionModifier>();
            if (component != null)
            {
                component.Collide(character, contact, collision, bodypart);
                if (!component.standable || !canStand(component, character))
                    return false;
            }

            bool flag = false;
            if (standOnPlayer(__instance, collision))
            {
                flag = true;
            }
            else if (!standableRig(__instance, collision.rigidbody))
            {
                return false;
            }

            if (_rocketActive(__instance) && _currentRocketTime(__instance) > 1f && character.IsLocal)
            {
                Character.CharacterRefs refs = character.refs;
                PhotonView view = refs != null ? refs.view : null;
                if (view == null)
                {
                    RateLimitedDiagnostics.Hit("CollisionContactsNoAlloc.rocket-view-unavailable");
                    return true;
                }
                view.RPC(RocketRpcName, RpcTarget.All);
                _rocketActive(__instance) = false;
            }

            float angle = Vector3.Angle(Vector3.up, contact.normal);
            if (doGroundChecks(__instance))
            {
                if (acceptableAngle(__instance, angle) || flag)
                    __instance.AddGroundSample(new CharacterMovement.PlayerGroundSample(contact.point, contact.normal));
                __instance.AddGroundSample_All(new CharacterMovement.PlayerGroundSample(contact.point, contact.normal));
            }

            return false;
        }

        private static void RequireRocketRpc()
        {
            MethodInfo rpc = typeof(CharacterMovement).GetMethod(
                RocketRpcName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (rpc == null || rpc.ReturnType != typeof(void) || rpc.GetParameters().Length != 0)
                throw new MissingMethodException(typeof(CharacterMovement).FullName, RocketRpcName);
            if (rpc.GetCustomAttribute<PunRPC>() == null)
                throw new InvalidOperationException("CharacterMovement.RocketExplodeRPC is no longer a PunRPC");
        }

        /// <summary>
        /// PlayerGroundSample is a readonly struct with a primary constructor (point, normal) and an
        /// IsValid field that is true for every constructed value (CharacterMovement.cs:13-23). The
        /// replacement constructs it directly, so its shape has to be proven.
        /// </summary>
        private static void RequireGroundSampleShape()
        {
            Type sample = typeof(CharacterMovement.PlayerGroundSample);
            if (!sample.IsValueType)
                throw new InvalidOperationException("PlayerGroundSample is no longer a struct");
            ConstructorInfo constructor = sample.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(Vector3), typeof(Vector3) },
                null);
            if (constructor == null)
                throw new MissingMethodException(sample.FullName, ".ctor(Vector3, Vector3)");
            ScaleValidation.RequireInstanceField(sample, "point", typeof(Vector3));
            ScaleValidation.RequireInstanceField(sample, "normal", typeof(Vector3));
            ScaleValidation.RequireInstanceField(sample, "IsValid", typeof(bool));
        }
    }
}
