using System;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using PeakSafeOptimizer.Diagnostics;
using PeakSafeOptimizer.Patches.Scale;
using UnityEngine;
using Zorro.Core;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Settled-state filter for the remote ground-item interpolation in
    /// ItemPhysicsSyncer.FixedUpdate() (audited source ItemPhysicsSyncer.cs:44-98).
    ///
    /// Harmony type: Prefix. A Transpiler was rejected here on purpose. The audited method body has
    /// three early returns, four debug branches, a sinceLastPackage mutation on the protected base
    /// class and two Rigidbody calls interleaved with a lastRecievedPosition write; a narrow unique
    /// IL match that guarantees the mutations stay ordered could not be proven, whereas the Prefix
    /// can decide up front and otherwise hand the untouched original the whole frame.
    ///
    /// Semantics: the patch only ever returns false (skipping the original) when all of the
    /// following hold, i.e. the remote item is already visually settled at its target:
    ///   - rig is present, PhotonNetwork.InRoom, and !m_photonView.IsMine
    ///   - RemoteValue.IsSome, shouldSync, itemState == Ground, m_lastPos.IsSome
    ///   - debug == false (never suppress the original's diagnostics)
    ///   - the interpolated target equals rig.position within 0.0005 m
    ///   - the remote rotation is within 0.05 degrees of rig.rotation
    /// In that state the original would call MovePosition(position + delta * 0.5f) with a delta far
    /// below float precision relevance and RotateTowards over a sub-0.05 degree gap.
    ///
    /// Everything else returns true. Crucially, when the patch skips it also advances
    /// sinceLastPackage exactly like the original does (fixedDeltaTime * 0.6f) and writes
    /// lastRecievedPosition to the same interpolated value, so interpolation phase and the inspector
    /// field never drift. ShouldSendData, GetDataToWrite, OnDataReceived, forceSyncFrames, shouldSync
    /// and kinematic state are never touched.
    /// </summary>
    internal static class RemoteItemInterpolationThresholdPatch
    {
        private const string RigidbodyTypeName = "UnityEngine.Rigidbody";
        private const float PositionEpsilon = 0.0005f;
        private const float PositionEpsilonSqr = PositionEpsilon * PositionEpsilon;
        private const float AngleEpsilonDegrees = 0.05f;

        private static AccessTools.FieldRef<ItemPhysicsSyncer, Item> _item;
        private static AccessTools.FieldRef<ItemPhysicsSyncer, PhotonView> _photonView;
        private static AccessTools.FieldRef<ItemPhysicsSyncer, Optionable<Vector3>> _lastPos;
        private static AccessTools.FieldRef<ItemPhysicsSyncer, bool> _shouldSync;
        private static AccessTools.FieldRef<ItemPhysicsSyncer, bool> _debug;
        private static AccessTools.FieldRef<ItemPhysicsSyncer, Vector3> _lastRecievedPosition;
        private static AccessTools.FieldRef<ItemPhysicsSyncer, Optionable<ItemPhysicsSyncData>> _remoteValue;
        private static AccessTools.FieldRef<ItemPhysicsSyncer, float> _sinceLastPackage;
        private static Func<Item, UnityEngine.Object> _rig;
        private static Func<UnityEngine.Object, Vector3> _rigPosition;
        private static Func<UnityEngine.Object, Quaternion> _rigRotation;
        private static Func<ItemPhysicsSyncData, Vector3> _dataPosition;
        private static Func<ItemPhysicsSyncData, Quaternion> _dataRotation;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            Type syncer = typeof(ItemPhysicsSyncer);
            FieldInfo itemField = ScaleValidation.RequireInstanceField(syncer, "m_item", typeof(Item));
            FieldInfo viewField = ScaleValidation.RequireInstanceField(syncer, "m_photonView", typeof(PhotonView));
            FieldInfo lastPosField = ScaleValidation.RequireInstanceField(
                syncer, "m_lastPos", typeof(Optionable<Vector3>));
            FieldInfo shouldSyncField = ScaleValidation.RequireInstanceField(syncer, "shouldSync", typeof(bool));
            FieldInfo debugField = ScaleValidation.RequireInstanceField(syncer, "debug", typeof(bool));
            FieldInfo lastRecievedPositionField = ScaleValidation.RequireInstanceField(
                syncer, "lastRecievedPosition", typeof(Vector3));
            ScaleValidation.RequireInstanceField(syncer, "forceSyncFrames", typeof(int));

            Type baseType = syncer.BaseType;
            if (baseType == null || !baseType.IsGenericType ||
                baseType.GetGenericTypeDefinition().FullName != "PhotonBinaryStreamSerializer`1")
            {
                throw new InvalidOperationException("ItemPhysicsSyncer base type structure mismatch");
            }
            Type[] baseArguments = baseType.GetGenericArguments();
            if (baseArguments.Length != 1 || baseArguments[0] != typeof(ItemPhysicsSyncData))
                throw new InvalidOperationException("ItemPhysicsSyncer stream payload type mismatch");

            FieldInfo remoteValueField = baseType.GetField(
                "RemoteValue", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo sinceLastPackageField = baseType.GetField(
                "sinceLastPackage", BindingFlags.Instance | BindingFlags.NonPublic);
            if (remoteValueField == null || remoteValueField.IsStatic ||
                remoteValueField.FieldType != typeof(Optionable<ItemPhysicsSyncData>))
            {
                throw new InvalidOperationException("PhotonBinaryStreamSerializer.RemoteValue structure mismatch");
            }
            if (sinceLastPackageField == null || sinceLastPackageField.IsStatic ||
                sinceLastPackageField.FieldType != typeof(float))
            {
                throw new InvalidOperationException("PhotonBinaryStreamSerializer.sinceLastPackage structure mismatch");
            }

            ScaleValidation.RequireInstanceProperty(typeof(Item), "itemState", typeof(ItemState));
            if (!Enum.IsDefined(typeof(ItemState), ItemState.Ground))
                throw new InvalidOperationException("ItemState.Ground is no longer defined");
            FieldInfo dataPositionField = ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(ItemPhysicsSyncData), "position", "Unity.Mathematics.float3");
            FieldInfo dataRotationField = ScaleValidation.RequireInstanceField(
                typeof(ItemPhysicsSyncData), "rotation", typeof(Quaternion));

            // Prove the sending/receiving side is untouched by this patch.
            ScaleValidation.RequireInstanceMethod(
                syncer, "ShouldSendData", typeof(bool), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                syncer, "GetDataToWrite", typeof(ItemPhysicsSyncData), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                syncer, "OnDataReceived", typeof(void), new[] { typeof(ItemPhysicsSyncData) }, true);

            FieldInfo rigField = ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(Item), "rig", RigidbodyTypeName);
            Type rigidbodyType = rigField.FieldType;
            PropertyInfo positionProperty = ScaleValidation.RequireInstancePropertyByTypeName(
                rigidbodyType, "position", "UnityEngine.Vector3", requireSetter: true);
            PropertyInfo rotationProperty = ScaleValidation.RequireInstancePropertyByTypeName(
                rigidbodyType, "rotation", "UnityEngine.Quaternion", requireSetter: true);
            // Proof that the writes this patch suppresses are the audited ones.
            ScaleValidation.RequireInstanceMethodByTypeNames(
                rigidbodyType, "MovePosition", typeof(void).FullName, new[] { "UnityEngine.Vector3" });
            ScaleValidation.RequireInstanceMethodByTypeNames(
                rigidbodyType, "MoveRotation", typeof(void).FullName, new[] { "UnityEngine.Quaternion" });

            AccessTools.FieldRef<ItemPhysicsSyncer, Item> item =
                AccessTools.FieldRefAccess<ItemPhysicsSyncer, Item>(itemField);
            AccessTools.FieldRef<ItemPhysicsSyncer, PhotonView> view =
                AccessTools.FieldRefAccess<ItemPhysicsSyncer, PhotonView>(viewField);
            AccessTools.FieldRef<ItemPhysicsSyncer, Optionable<Vector3>> lastPos =
                AccessTools.FieldRefAccess<ItemPhysicsSyncer, Optionable<Vector3>>(lastPosField);
            AccessTools.FieldRef<ItemPhysicsSyncer, bool> shouldSync =
                AccessTools.FieldRefAccess<ItemPhysicsSyncer, bool>(shouldSyncField);
            AccessTools.FieldRef<ItemPhysicsSyncer, bool> debug =
                AccessTools.FieldRefAccess<ItemPhysicsSyncer, bool>(debugField);
            AccessTools.FieldRef<ItemPhysicsSyncer, Vector3> lastRecievedPosition =
                AccessTools.FieldRefAccess<ItemPhysicsSyncer, Vector3>(lastRecievedPositionField);
            AccessTools.FieldRef<ItemPhysicsSyncer, Optionable<ItemPhysicsSyncData>> remoteValue =
                AccessTools.FieldRefAccess<ItemPhysicsSyncer, Optionable<ItemPhysicsSyncData>>(remoteValueField);
            AccessTools.FieldRef<ItemPhysicsSyncer, float> sinceLastPackage =
                AccessTools.FieldRefAccess<ItemPhysicsSyncer, float>(sinceLastPackageField);
            Func<Item, UnityEngine.Object> rig =
                ScaleEmit.CreateUnityObjectFieldGetter<Item>(rigField);
            Func<UnityEngine.Object, Vector3> rigPosition =
                ScaleExperimentalEmit.CreateVector3Getter(rigidbodyType, positionProperty);
            Func<UnityEngine.Object, Quaternion> rigRotation =
                ScaleExperimentalEmit.CreateQuaternionGetter(rigidbodyType, rotationProperty);
            Func<ItemPhysicsSyncData, Vector3> dataPosition =
                ScaleExperimentalEmit.CreateStructVector3FieldGetter<ItemPhysicsSyncData>(dataPositionField);
            Func<ItemPhysicsSyncData, Quaternion> dataRotation =
                ScaleExperimentalEmit.CreateStructQuaternionFieldGetter<ItemPhysicsSyncData>(dataRotationField);
            if (item == null || view == null || lastPos == null || shouldSync == null || debug == null ||
                lastRecievedPosition == null || remoteValue == null || sinceLastPackage == null ||
                rig == null || rigPosition == null || rigRotation == null ||
                dataPosition == null || dataRotation == null)
            {
                throw new InvalidOperationException("RemoteItemInterpolationThreshold accessor creation failed");
            }

            _item = item;
            _photonView = view;
            _lastPos = lastPos;
            _shouldSync = shouldSync;
            _debug = debug;
            _lastRecievedPosition = lastRecievedPosition;
            _remoteValue = remoteValue;
            _sinceLastPackage = sinceLastPackage;
            _rig = rig;
            _rigPosition = rigPosition;
            _rigRotation = rigRotation;
            _dataPosition = dataPosition;
            _dataRotation = dataRotation;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(ItemPhysicsSyncer), "FixedUpdate", typeof(void), Type.EmptyTypes, false);
        }

        internal static void Reset()
        {
            _item = null;
            _photonView = null;
            _lastPos = null;
            _shouldSync = null;
            _debug = null;
            _lastRecievedPosition = null;
            _remoteValue = null;
            _sinceLastPackage = null;
            _rig = null;
            _rigPosition = null;
            _rigRotation = null;
            _dataPosition = null;
            _dataRotation = null;
        }

        internal static bool Prefix(ItemPhysicsSyncer __instance)
        {
            AccessTools.FieldRef<ItemPhysicsSyncer, Item> itemRef = _item;
            AccessTools.FieldRef<ItemPhysicsSyncer, PhotonView> viewRef = _photonView;
            AccessTools.FieldRef<ItemPhysicsSyncer, Optionable<Vector3>> lastPosRef = _lastPos;
            AccessTools.FieldRef<ItemPhysicsSyncer, bool> shouldSyncRef = _shouldSync;
            AccessTools.FieldRef<ItemPhysicsSyncer, bool> debugRef = _debug;
            AccessTools.FieldRef<ItemPhysicsSyncer, Vector3> lastRecievedPositionRef = _lastRecievedPosition;
            AccessTools.FieldRef<ItemPhysicsSyncer, Optionable<ItemPhysicsSyncData>> remoteValueRef = _remoteValue;
            AccessTools.FieldRef<ItemPhysicsSyncer, float> sinceLastPackageRef = _sinceLastPackage;
            Func<Item, UnityEngine.Object> rigRef = _rig;
            Func<UnityEngine.Object, Vector3> rigPositionRef = _rigPosition;
            Func<UnityEngine.Object, Quaternion> rigRotationRef = _rigRotation;
            Func<ItemPhysicsSyncData, Vector3> dataPositionRef = _dataPosition;
            Func<ItemPhysicsSyncData, Quaternion> dataRotationRef = _dataRotation;
            if (itemRef == null || viewRef == null || lastPosRef == null || shouldSyncRef == null ||
                debugRef == null || lastRecievedPositionRef == null || remoteValueRef == null ||
                sinceLastPackageRef == null || rigRef == null || rigPositionRef == null ||
                rigRotationRef == null || dataPositionRef == null || dataRotationRef == null)
            {
                RateLimitedDiagnostics.Hit("RemoteItemInterpolationThreshold.accessors-unavailable");
                return true;
            }

            // Never interfere with the audited debug paths.
            if (debugRef(__instance))
                return true;

            Item item = itemRef(__instance);
            PhotonView view = viewRef(__instance);
            if (item == null || view == null)
                return true;

            UnityEngine.Object rig = rigRef(item);
            if (rig == null || !PhotonNetwork.InRoom || view.IsMine)
                return true;

            if (!shouldSyncRef(__instance))
                return true;

            Optionable<ItemPhysicsSyncData> remote = remoteValueRef(__instance);
            if (remote.IsNone)
                return true;

            if (item.itemState != ItemState.Ground)
                return true;

            Optionable<Vector3> lastPos = lastPosRef(__instance);
            if (lastPos.IsNone)
                return true;

            int serializationRate = PhotonNetwork.SerializationRate;
            if (serializationRate <= 0)
                return true;

            // Reproduce the audited interpolation inputs exactly (ItemPhysicsSyncer.cs:79-87),
            // including the same sinceLastPackage advance, so skipping cannot change phase.
            double packageInterval = 1f / (float)serializationRate;
            float advanced = sinceLastPackageRef(__instance) + Time.fixedDeltaTime * 0.6f;
            float t = (float)((double)advanced / packageInterval);

            ItemPhysicsSyncData data = remote.Value;
            Vector3 target = dataPositionRef(data);
            Vector3 interpolated = Vector3.Lerp(lastPos.Value, target, t);
            Vector3 rigPosition = rigPositionRef(rig);
            Vector3 delta = interpolated - rigPosition;
            if (delta.sqrMagnitude >= PositionEpsilonSqr)
                return true;

            Quaternion rigRotation = rigRotationRef(rig);
            if (Quaternion.Angle(rigRotation, dataRotationRef(data)) >= AngleEpsilonDegrees)
                return true;

            // Settled: apply the original's non-physics side effects, then skip only the two
            // Rigidbody move calls whose remaining error is below the thresholds above.
            // No diagnostics here on purpose: a settled remote item is normal steady state, not a
            // repaired fault, so it must not be accounted for on the hot path.
            sinceLastPackageRef(__instance) = advanced;
            lastRecievedPositionRef(__instance) = interpolated;
            return false;
        }
    }
}
