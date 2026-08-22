using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Makes Snowball independent of the lifetime of a UnityEngine.Collision instance, which is the
    /// one prerequisite for turning on <c>Physics.reuseCollisionCallbacks</c>.
    ///
    /// Snowball is the only place in the whole game assembly that keeps a Collision reference past
    /// its callback: <c>private Collision _unprocessedContact</c> (Snowball.cs:105) is assigned in
    /// OnCollisionStay (:227) and read in the next Update through ProcessLastCollision (:267-294) via
    /// <c>.collider</c> (:273), <c>.contacts[0].normal</c> (:301) and <c>.gameObject</c> (:310).
    /// With callback reuse enabled that reference would alias the single shared
    /// <c>Physics.s_ReusableCollision</c>, whose header/pair are overwritten by every later contact
    /// pair and whose contact pointer only stays valid for the duration of the dispatch.
    ///
    /// Two Prefixes, both full equivalent replacements of very short methods:
    ///   <see cref="StayPrefix"/>   replaces OnCollisionStay (9 lines) and stores a value snapshot of
    ///                              exactly the three members ProcessLastCollision reads.
    ///   <see cref="ProcessPrefix"/> replaces ProcessLastCollision plus the Scale/ClassifyCurrentContact
    ///                              helpers it drives, reading the snapshot instead of the Collision.
    /// Every branch, comparison and field write is reproduced in original order, so with or without
    /// callback reuse the observable behaviour is the vanilla one.
    /// </summary>
    internal static class SnowballContactPatch
    {
        private enum ContactType
        {
            Unknown,
            RegularGround,
            SnowyGround,
            Snowball
        }

        /// <summary>
        /// Value copy of the parts of the collision that survive the callback in vanilla. Captured
        /// while the engine data is still valid, which is what makes callback reuse safe here.
        /// Collider and GameObject are independent managed objects, so holding those references is
        /// unaffected by reuse.
        /// </summary>
        private sealed class Snapshot
        {
            internal Collider Collider;
            internal GameObject GameObject;
            internal Vector3 Normal;
            internal bool HasNormal;
        }

        private const string SnowName = "snow";
        private const string IceName = "ice";
        private const string StoneTag = "Stone1";
        private const string SnowballTag = "Snowball";

        private static readonly ConditionalWeakTable<Snowball, Snapshot> Snapshots =
            new ConditionalWeakTable<Snowball, Snapshot>();

        private static AccessTools.FieldRef<Snowball, Collision> _unprocessedContact;
        private static AccessTools.FieldRef<Snowball, Collider> _lastCollision;
        private static AccessTools.FieldRef<Snowball, int> _terrainLayer;
        private static AccessTools.FieldRef<Snowball, int> _characterLayer;
        private static AccessTools.FieldRef<Snowball, int> _lastContactType;
        private static AccessTools.FieldRef<Snowball, float> _timeLastCollided;
        private static AccessTools.FieldRef<Snowball, float> _timeLastTouchedScout;
        private static AccessTools.FieldRef<Snowball, Vector3?> _previousPosition;
        private static AccessTools.FieldRef<Snowball, float> _scaleRate;
        private static AccessTools.FieldRef<Snowball, float> _minimumSpeedToGrow;
        private static AccessTools.FieldRef<Snowball, ItemScaleSyncer> _scaleSyncer;
        private static Action<Snowball> _updateMass;

        internal static void Initialize()
        {
            ResolveStayTarget();
            ResolveProcessTarget();

            // Every member the two replacements read or write, verified exactly once here.
            FieldInfo unprocessedContact = ScaleValidation.RequireInstanceField(
                typeof(Snowball), "_unprocessedContact", typeof(Collision));
            FieldInfo lastCollision = ScaleValidation.RequireInstanceField(
                typeof(Snowball), "_lastCollision", typeof(Collider));
            FieldInfo terrainLayer = ScaleValidation.RequireInstanceField(
                typeof(Snowball), "terrainLayer", typeof(int));
            FieldInfo characterLayer = ScaleValidation.RequireInstanceField(
                typeof(Snowball), "characterLayer", typeof(int));
            FieldInfo lastContactType = RequireContactTypeField();
            FieldInfo timeLastCollided = ScaleValidation.RequireInstanceField(
                typeof(Snowball), "_timeLastCollided", typeof(float));
            FieldInfo timeLastTouchedScout = ScaleValidation.RequireInstanceField(
                typeof(Snowball), "_timeLastTouchedScout", typeof(float));
            FieldInfo previousPosition = ScaleValidation.RequireInstanceField(
                typeof(Snowball), "_previousPosition", typeof(Vector3?));
            FieldInfo scaleRate = ScaleValidation.RequireInstanceField(
                typeof(Snowball), "scaleRate", typeof(float));
            FieldInfo minimumSpeedToGrow = ScaleValidation.RequireInstanceField(
                typeof(Snowball), "minimumSpeedToGrow", typeof(float));
            FieldInfo scaleSyncer = ScaleValidation.RequireInstanceField(
                typeof(Snowball), "scaleSyncer", typeof(ItemScaleSyncer));
            MethodInfo updateMass = ScaleValidation.RequireInstanceMethod(
                typeof(Snowball), "UpdateMass", typeof(void), Type.EmptyTypes, false);
            ScaleValidation.RequireInstanceProperty(typeof(Snowball), "CanGrow", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(ItemScaleSyncer), "currentScale", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(Item), "rig", typeof(Rigidbody));
            // ItemComponent.item, i.e. inherited rather than declared on Snowball.
            ScaleValidation.RequireInstanceField(typeof(ItemComponent), "item", typeof(Item));
            ScaleValidation.RequireInstanceMethod(
                typeof(Snowball), "Scale", typeof(void), new[] { typeof(float) }, false);
            ScaleValidation.RequireInstanceMethod(
                typeof(Snowball), "ClassifyCurrentContact", RequireContactTypeField().FieldType,
                Type.EmptyTypes, false);
            ScaleValidation.RequireInstanceMethod(
                typeof(Item), "SetKinematic", typeof(void), new[] { typeof(bool) }, true);

            AccessTools.FieldRef<Snowball, Collision> unprocessedRef =
                AccessTools.FieldRefAccess<Snowball, Collision>(unprocessedContact);
            AccessTools.FieldRef<Snowball, Collider> lastCollisionRef =
                AccessTools.FieldRefAccess<Snowball, Collider>(lastCollision);
            AccessTools.FieldRef<Snowball, int> terrainLayerRef =
                AccessTools.FieldRefAccess<Snowball, int>(terrainLayer);
            AccessTools.FieldRef<Snowball, int> characterLayerRef =
                AccessTools.FieldRefAccess<Snowball, int>(characterLayer);
            // The private enum is int backed (verified by RequireContactTypeField), so it can be
            // written through an int FieldRef without referencing the type.
            AccessTools.FieldRef<Snowball, int> lastContactTypeRef =
                AccessTools.FieldRefAccess<Snowball, int>(lastContactType);
            AccessTools.FieldRef<Snowball, float> timeLastCollidedRef =
                AccessTools.FieldRefAccess<Snowball, float>(timeLastCollided);
            AccessTools.FieldRef<Snowball, float> timeLastTouchedScoutRef =
                AccessTools.FieldRefAccess<Snowball, float>(timeLastTouchedScout);
            AccessTools.FieldRef<Snowball, Vector3?> previousPositionRef =
                AccessTools.FieldRefAccess<Snowball, Vector3?>(previousPosition);
            AccessTools.FieldRef<Snowball, float> scaleRateRef =
                AccessTools.FieldRefAccess<Snowball, float>(scaleRate);
            AccessTools.FieldRef<Snowball, float> minimumSpeedRef =
                AccessTools.FieldRefAccess<Snowball, float>(minimumSpeedToGrow);
            AccessTools.FieldRef<Snowball, ItemScaleSyncer> scaleSyncerRef =
                AccessTools.FieldRefAccess<Snowball, ItemScaleSyncer>(scaleSyncer);
            Action<Snowball> updateMassDelegate =
                (Action<Snowball>)updateMass.CreateDelegate(typeof(Action<Snowball>));
            if (unprocessedRef == null || lastCollisionRef == null || terrainLayerRef == null ||
                characterLayerRef == null || lastContactTypeRef == null || timeLastCollidedRef == null ||
                timeLastTouchedScoutRef == null || previousPositionRef == null || scaleRateRef == null ||
                minimumSpeedRef == null || scaleSyncerRef == null || updateMassDelegate == null)
            {
                throw new InvalidOperationException("SnowballContact accessor creation failed");
            }

            _unprocessedContact = unprocessedRef;
            _lastCollision = lastCollisionRef;
            _terrainLayer = terrainLayerRef;
            _characterLayer = characterLayerRef;
            _lastContactType = lastContactTypeRef;
            _timeLastCollided = timeLastCollidedRef;
            _timeLastTouchedScout = timeLastTouchedScoutRef;
            _previousPosition = previousPositionRef;
            _scaleRate = scaleRateRef;
            _minimumSpeedToGrow = minimumSpeedRef;
            _scaleSyncer = scaleSyncerRef;
            _updateMass = updateMassDelegate;
        }

        internal static MethodInfo ResolveStayTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(Snowball), "OnCollisionStay", typeof(void), new[] { typeof(Collision) }, false);
        }

        internal static MethodInfo ResolveProcessTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(Snowball), "ProcessLastCollision", typeof(void), Type.EmptyTypes, false);
        }

        internal static void Reset()
        {
            _unprocessedContact = null;
            _lastCollision = null;
            _terrainLayer = null;
            _characterLayer = null;
            _lastContactType = null;
            _timeLastCollided = null;
            _timeLastTouchedScout = null;
            _previousPosition = null;
            _scaleRate = null;
            _minimumSpeedToGrow = null;
            _scaleSyncer = null;
            _updateMass = null;
        }

        /// <summary>
        /// Equivalent replacement of Snowball.OnCollisionStay (Snowball.cs:223-232). The vanilla
        /// branch structure is preserved; the only difference is that the non-character branch stores
        /// a value snapshot next to the original field write instead of relying on the field alone.
        /// The field is still written so that anything else reading it - including the untouched
        /// vanilla ProcessLastCollision if this pair of patches is only half installed - keeps seeing
        /// the same non-null marker.
        /// </summary>
        internal static bool StayPrefix(Snowball __instance, Collision other)
        {
            AccessTools.FieldRef<Snowball, int> characterLayerRef = _characterLayer;
            AccessTools.FieldRef<Snowball, Collision> unprocessedRef = _unprocessedContact;
            AccessTools.FieldRef<Snowball, float> timeLastTouchedScoutRef = _timeLastTouchedScout;
            if (characterLayerRef == null || unprocessedRef == null || timeLastTouchedScoutRef == null ||
                __instance == null || other == null)
            {
                return true;
            }

            GameObject gameObject;
            try
            {
                gameObject = other.gameObject;
            }
            catch (Exception)
            {
                RateLimitedDiagnostics.Hit("SnowballContactSnapshot.gameObject-unavailable");
                return true;
            }
            if (gameObject == null)
                return true;

            if (gameObject.layer != characterLayerRef(__instance))
            {
                Snapshot snapshot = Snapshots.GetOrCreateValue(__instance);
                snapshot.GameObject = gameObject;
                snapshot.Collider = other.collider;
                // contactCount can legitimately be 0 for a persisting pair, in which case vanilla
                // would throw inside Scale(). Record the absence instead and reproduce the vanilla
                // outcome (no growth) rather than the exception.
                if (other.contactCount > 0)
                {
                    snapshot.Normal = other.GetContact(0).normal;
                    snapshot.HasNormal = true;
                }
                else
                {
                    snapshot.Normal = Vector3.zero;
                    snapshot.HasNormal = false;
                }
                unprocessedRef(__instance) = other;
                return false;
            }

            Item item = __instance.item;
            if (item == null)
            {
                // The original dereferences item here; leave its behaviour with it.
                RateLimitedDiagnostics.Hit("SnowballContactSnapshot.item-unavailable");
                return true;
            }
            timeLastTouchedScoutRef(__instance) = Time.time;
            item.SetKinematic(value: false);
            return false;
        }

        /// <summary>
        /// Equivalent replacement of Snowball.ProcessLastCollision (Snowball.cs:267-294) inlining the
        /// Scale() and ClassifyCurrentContact() helpers, which are the two places that dereference the
        /// stored Collision. Only reached when a snapshot exists, so the vanilla path stays in charge
        /// of any collision recorded before this patch was installed.
        /// </summary>
        internal static bool ProcessPrefix(Snowball __instance)
        {
            AccessTools.FieldRef<Snowball, Collision> unprocessedRef = _unprocessedContact;
            AccessTools.FieldRef<Snowball, Collider> lastCollisionRef = _lastCollision;
            AccessTools.FieldRef<Snowball, int> lastContactTypeRef = _lastContactType;
            AccessTools.FieldRef<Snowball, float> timeLastCollidedRef = _timeLastCollided;
            AccessTools.FieldRef<Snowball, Vector3?> previousPositionRef = _previousPosition;
            if (unprocessedRef == null || lastCollisionRef == null || lastContactTypeRef == null ||
                timeLastCollidedRef == null || previousPositionRef == null || __instance == null)
            {
                return true;
            }

            // Vanilla's own early-out, evaluated on the same field.
            if (unprocessedRef(__instance) == null)
                return false;
            if (!Snapshots.TryGetValue(__instance, out Snapshot snapshot) || snapshot == null ||
                snapshot.GameObject == null)
            {
                // No snapshot: this collision was captured by the untouched original, so let the
                // untouched original consume it.
                RateLimitedDiagnostics.Hit("SnowballContactConsume.snapshot-unavailable");
                return true;
            }

            lastCollisionRef(__instance) = snapshot.Collider;
            ContactType contactType = Classify(__instance, snapshot);
            if (contactType == ContactType.SnowyGround)
            {
                if (CanGrow(__instance))
                    Grow(__instance, snapshot);
                previousPositionRef(__instance) = __instance.transform.position;
            }

            timeLastCollidedRef(__instance) = Time.time;
            lastContactTypeRef(__instance) = (int)contactType;
            unprocessedRef(__instance) = null;
            snapshot.Collider = null;
            snapshot.GameObject = null;
            snapshot.HasNormal = false;
            return false;
        }

        /// <summary>
        /// Snowball.CanGrow (Snowball.cs:115), which is a private expression-bodied property:
        /// <c>item.rig.linearVelocity.magnitude &gt; minimumSpeedToGrow</c>. Inlined rather than
        /// invoked, because it reads two public members plus one field this patch already resolves.
        /// A missing rig would make the original throw; reporting "cannot grow" keeps the snowball
        /// intact instead, which is the same outcome the caller sees for a slow-moving ball.
        /// </summary>
        private static bool CanGrow(Snowball instance)
        {
            Item item = instance.item;
            Rigidbody rig = item != null ? item.rig : null;
            if (rig == null)
                return false;
            return rig.linearVelocity.magnitude > _minimumSpeedToGrow(instance);
        }

        /// <summary>Snowball.Scale(1f) (Snowball.cs:296-306) reading the snapshot normal.</summary>
        private static void Grow(Snowball instance, Snapshot snapshot)
        {
            Vector3? previous = _previousPosition(instance);
            if (!previous.HasValue)
                return;
            // Vanilla dereferences _unprocessedContact.contacts[0] here, which throws when the pair
            // reported no contact points. Without a normal there is nothing to project onto, so skip
            // the growth for that tick instead of throwing.
            if (!snapshot.HasNormal)
            {
                RateLimitedDiagnostics.Hit("SnowballContactConsume.no-contact-point");
                return;
            }
            ItemScaleSyncer syncer = _scaleSyncer(instance);
            if (syncer == null)
                return;

            Vector3 vector = instance.transform.position - previous.Value;
            vector = Vector3.ProjectOnPlane(vector, snapshot.Normal);
            float num = Mathf.Clamp(1f * _scaleRate(instance) * vector.magnitude, 0f, 0.1f);
            syncer.currentScale += num / syncer.currentScale;
            _updateMass(instance);
        }

        /// <summary>
        /// Snowball.ClassifyCurrentContact (Snowball.cs:308-325) reading the snapshot GameObject.
        /// </summary>
        private static ContactType Classify(Snowball instance, Snapshot snapshot)
        {
            GameObject gameObject = snapshot.GameObject;
            if ((gameObject.layer == _terrainLayer(instance) || gameObject.CompareTag(StoneTag)) &&
                gameObject.TryGetComponent<Renderer>(out Renderer component))
            {
                string text = component.sharedMaterial.name.ToLowerInvariant();
                if (!text.Contains(IceName) && !text.Contains(SnowName))
                    return ContactType.RegularGround;
                return ContactType.SnowyGround;
            }
            if (!gameObject.CompareTag(SnowballTag))
                return ContactType.Unknown;
            return ContactType.Snowball;
        }

        /// <summary>
        /// Proves the private ContactType enum still has the audited int-backed shape and member
        /// order, because the replacement writes the field as a plain int.
        /// </summary>
        private static FieldInfo RequireContactTypeField()
        {
            FieldInfo field = typeof(Snowball).GetField(
                "_lastContactType",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field == null)
                throw new MissingFieldException(typeof(Snowball).FullName, "_lastContactType");
            Type enumType = field.FieldType;
            if (!enumType.IsEnum || Enum.GetUnderlyingType(enumType) != typeof(int))
                throw new InvalidOperationException("Snowball._lastContactType is not an int-backed enum");
            string[] expected = { "Unknown", "RegularGround", "SnowyGround", "Snowball" };
            string[] actual = Enum.GetNames(enumType);
            if (actual.Length != expected.Length)
                throw new InvalidOperationException("Snowball ContactType member count changed");
            for (int i = 0; i < expected.Length; i++)
            {
                if (!string.Equals(actual[i], expected[i], StringComparison.Ordinal))
                    throw new InvalidOperationException($"Snowball ContactType.{expected[i]} moved");
                if ((int)Enum.Parse(enumType, expected[i]) != i)
                    throw new InvalidOperationException($"Snowball ContactType.{expected[i]} changed value");
            }
            return field;
        }
    }
}
