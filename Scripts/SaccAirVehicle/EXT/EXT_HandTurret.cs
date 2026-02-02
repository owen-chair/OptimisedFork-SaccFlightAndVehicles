using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace SaccFlightAndVehicles
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class EXT_HandTurret : UdonSharpBehaviour
    {
        [Tooltip("Transform that dictates the up direction of the turret")]
        public Transform TurretTransform;
        [Tooltip("Transform that rotates the gun")]
        public Transform Gun;
        [Tooltip("OPTIONAL: Use a separate transform for the pitch rotation")]
        public Transform GunPitch;
        public bool UseLeftHand;
        [Tooltip("Just use the direction that hand is pointing to aim?")]
        public bool Aim_HandDirection;
        [Tooltip("Use look direction for aiming, even in VR")]
        public bool Aim_HeadDirectionVR;
        [System.NonSerializedAttribute] public bool LeftDial = false;
        [System.NonSerializedAttribute] public int DialPosition = -999;
        [System.NonSerializedAttribute] public SaccEntity EntityControl;

        [Header("Networking")]
        [Tooltip("Optional: reference to the gun function so we can throttle aim syncing when not firing")]
        public DFUNC_Gun GunFunction;

        [Tooltip("Seconds between aim syncs when NOT firing (requires GunFunction reference)")]
        public float NetSendIntervalNotFiring = 0.50f;

        [Tooltip("Seconds between aim syncs when firing (requires GunFunction reference)")]
        public float NetSendIntervalFiring = 0.10f;

        // Always 8-bit aim sync (one axis per update):
        // bit7 = axis (0 = yaw, 1 = pitch)
        // bits0-6 = quantized angle within [-180..180] (0..127)
        [UdonSynced(UdonSyncMode.None)] private byte PackedGunRotationU8;

        private bool InVR;
        private VRCPlayerApi localPlayer;
        private bool Piloting;
        private float TimeSinceSerialization;
        private Quaternion NonOwnerGunAngleSlerper;
        private Quaternion _remoteTargetRot;
        private byte _lastSentPackedU8;

        private float _remotePitch;
        private float _remoteYaw;
        private float _lastSentPitchDeg;
        private float _lastSentYawDeg;
        private int _sendAxisSelector;

        private bool _netWasFiring;

        // Tick rate locking
        private const float _PilotingTickInterval = 1f / 45f;
        private const float _NotPilotingTickInterval = 1f / 20f;
        private float _nextPilotingTickTime;
        private float _nextNotPilotingTickTime;
        private float _lastPilotingTickTime;
        private float _lastNotPilotingTickTime;

        private bool _EnsureLocalNetOwnership()
        {
            if (Networking.IsOwner(gameObject)) { return true; }
            VRCPlayerApi lp = Networking.LocalPlayer;
            if (lp == null) { return false; }
            Networking.SetOwner(lp, gameObject);
            return Networking.IsOwner(gameObject);
        }

        private static int _Quantize7(float angleDeg, float minDeg, float maxDeg)
        {
            if (maxDeg <= minDeg) { return 0; }
            float clamped = Mathf.Clamp(angleDeg, minDeg, maxDeg);
            float t = (clamped - minDeg) / (maxDeg - minDeg);
            return Mathf.Clamp(Mathf.RoundToInt(t * 127f), 0, 127);
        }

        private static float _Dequantize7(int q, float minDeg, float maxDeg)
        {
            if (maxDeg <= minDeg) { return minDeg; }
            float t = Mathf.Clamp01(q / 127f);
            return minDeg + t * (maxDeg - minDeg);
        }

        private void _DecodePackedU8(byte packed)
        {
            int axis = (packed >> 7) & 0x01; // 0 yaw, 1 pitch
            int q = packed & 0x7F;

            const float minYaw = -180f;
            const float maxYaw = 180f;
            const float minPitch = -180f;
            const float maxPitch = 180f;

            if (axis == 1)
            {
                _remotePitch = _Dequantize7(q, minPitch, maxPitch);
            }
            else
            {
                _remoteYaw = _Dequantize7(q, minYaw, maxYaw);
            }

            _remoteTargetRot = Quaternion.Euler(new Vector3(_remotePitch, _remoteYaw, 0f));
        }

        public override void OnDeserialization()
        {
            _DecodePackedU8(PackedGunRotationU8);
        }

        private void _ResetNetAim()
        {
            PackedGunRotationU8 = 0;
            _lastSentPackedU8 = PackedGunRotationU8;
            _remoteTargetRot = Quaternion.identity;
            _remotePitch = 0f;
            _remoteYaw = 0f;
            _lastSentPitchDeg = 0f;
            _lastSentYawDeg = 0f;
            _sendAxisSelector = 0;
        }
        public void SFEXT_L_EntityStart()
        {
            localPlayer = Networking.LocalPlayer;
            InVR = EntityControl.InVR;
            _ResetNetAim();
            NonOwnerGunAngleSlerper = Quaternion.identity;
            _netWasFiring = false;

            _nextPilotingTickTime = 0f;
            _nextNotPilotingTickTime = 0f;
            _lastPilotingTickTime = 0f;
            _lastNotPilotingTickTime = 0f;
        }
        public void SFEXT_O_PilotEnter()
        {
            Piloting = true;
            InVR = EntityControl.InVR;

            // Manual sync: PackedGunRotationU8 only replicates from the owner.
            _EnsureLocalNetOwnership();

            // Run immediately on enter.
            _nextPilotingTickTime = 0f;
            _lastPilotingTickTime = 0f;
        }
        public void SFEXT_O_PilotExit()
        {
            Piloting = false;
            // Start smoothing from the last received network aim.
            NonOwnerGunAngleSlerper = _remoteTargetRot;

            // Run immediately on exit.
            _nextNotPilotingTickTime = 0f;
            _lastNotPilotingTickTime = 0f;
        }
        public void SFEXT_G_PilotEnter() { gameObject.SetActive(true); }
        public void SFEXT_G_PilotExit() { gameObject.SetActive(false); }
        public void SFEXT_G_Explode()
        {
            Gun.localRotation = Quaternion.identity;
            if (GunPitch) GunPitch.localRotation = Quaternion.identity;
            _ResetNetAim();
            gameObject.SetActive(false);
        }
        public void SFEXT_G_RespawnButton()
        {
            Gun.localRotation = Quaternion.identity;
            if (GunPitch) GunPitch.localRotation = Quaternion.identity;
            _ResetNetAim();
        }
        private void LateUpdate()
        {
            float now = Time.time;
            if (Piloting)
            {
                if (now < _nextPilotingTickTime) { return; }
                float dt = (_lastPilotingTickTime > 0f) ? (now - _lastPilotingTickTime) : Time.deltaTime;
                _lastPilotingTickTime = now;
                _nextPilotingTickTime = now + _PilotingTickInterval;
                PilotingTick(dt);
            }
            else
            {
                if (now < _nextNotPilotingTickTime) { return; }
                float dt = (_lastNotPilotingTickTime > 0f) ? (now - _lastNotPilotingTickTime) : Time.deltaTime;
                _lastNotPilotingTickTime = now;
                _nextNotPilotingTickTime = now + _NotPilotingTickInterval;
                NotPilotingTick(dt);
            }
        }

        private void PilotingTick(float dt)
        {
            // Ensure we can actually replicate aim updates.
            _EnsureLocalNetOwnership();

            if (InVR && !Aim_HeadDirectionVR)
            {
                if (Aim_HandDirection)
                {
                    if (UseLeftHand)
                    { Gun.rotation = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.LeftHand).rotation * Quaternion.Euler(0, 60, 0); }
                    else
                    { Gun.rotation = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.RightHand).rotation * Quaternion.Euler(0, 60, 0); }
                }
                else
                {
                    Vector3 lookpoint;
                    if (UseLeftHand)
                    {
                        lookpoint = ((Gun.position - localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.LeftHand).position) * 500) + Gun.position;
                    }
                    else
                    {
                        lookpoint = ((Gun.position - localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.RightHand).position) * 500) + Gun.position;
                    }
                    Gun.LookAt(lookpoint, TurretTransform.up);
                }
            }
            else
            {
                Gun.rotation = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).rotation;
            }

            //set gun's roll to 0 and do pitch rotator if used
            Vector3 mgrotH = Gun.localEulerAngles;
            if (GunPitch)
            {
                mgrotH.z = 0;
                Vector3 mgrotP = mgrotH;
                mgrotH.x = 0;
                mgrotP.y = 0;
                GunPitch.localRotation = Quaternion.Euler(mgrotP);
            }
            else
            { mgrotH.z = 0; }
            Gun.localRotation = Quaternion.Euler(mgrotH);

            TimeSinceSerialization += dt;

            // Send interval is driven by firing state (GunFunction is expected to be assigned).
            float pitchNow;
            float yawNow;
            if (GunPitch)
            {
                pitchNow = GunPitch.localEulerAngles.x;
                // When GunPitch is used, yaw is on Gun (GunPitch.y is forced to 0 by the split math above).
                yawNow = Gun.localEulerAngles.y;
            }
            else
            {
                pitchNow = Gun.localEulerAngles.x;
                yawNow = Gun.localEulerAngles.y;
            }

            float desiredInterval;
            bool netIsFiring = (GunFunction != null) && GunFunction.Firing;
            bool firingStarted = netIsFiring && !_netWasFiring;
            _netWasFiring = netIsFiring;

            desiredInterval = netIsFiring
                ? Mathf.Max(0.02f, NetSendIntervalFiring)
                : Mathf.Max(0.02f, NetSendIntervalNotFiring);

            if (firingStarted)
            {
                // Allow an immediate aim update when firing starts.
                TimeSinceSerialization = desiredInterval;
            }

            if (TimeSinceSerialization >= desiredInterval)
            {
                // Always 8-bit: send whichever axis changed more (or alternate on tie).
                const float minYaw = -180f;
                const float maxYaw = 180f;
                const float minPitch = -180f;
                const float maxPitch = 180f;

                // One quantization step for 7 bits mapped across 360 degrees.
                const float quantStepDeg = 360f / 127f;

                float pitchSigned = Mathf.DeltaAngle(0f, pitchNow);
                float yawSigned = Mathf.DeltaAngle(0f, yawNow);

                int pitchQ = _Quantize7(pitchSigned, minPitch, maxPitch);
                int yawQ = _Quantize7(yawSigned, minYaw, maxYaw);

                // Hard gate: don't send unless the angle moved by >= the precision of our compression.
                // This reduces boundary-jitter spam when values hover around a rounding threshold.
                float pitchDeltaDeg = Mathf.Abs(Mathf.DeltaAngle(_lastSentPitchDeg, pitchSigned));
                float yawDeltaDeg = Mathf.Abs(Mathf.DeltaAngle(_lastSentYawDeg, yawSigned));

                if (pitchDeltaDeg >= quantStepDeg || yawDeltaDeg >= quantStepDeg)
                {
                    int axis;
                    if (pitchDeltaDeg > yawDeltaDeg) { axis = 1; }
                    else if (yawDeltaDeg > pitchDeltaDeg) { axis = 0; }
                    else
                    {
                        axis = _sendAxisSelector;
                        _sendAxisSelector = 1 - _sendAxisSelector;
                    }

                    int q = (axis == 1) ? pitchQ : yawQ;
                    byte packedU8 = (byte)(((axis & 0x01) << 7) | (q & 0x7F));
                    if (packedU8 != _lastSentPackedU8)
                    {
                        TimeSinceSerialization = 0f;
                        _lastSentPackedU8 = packedU8;
                        PackedGunRotationU8 = packedU8;

                        // Update last-sent for the axis we actually transmitted.
                        if (axis == 1)
                        {
                            _lastSentPitchDeg = _Dequantize7(pitchQ, minPitch, maxPitch);
                        }
                        else
                        {
                            _lastSentYawDeg = _Dequantize7(yawQ, minYaw, maxYaw);
                        }
                        if (_EnsureLocalNetOwnership())
                        {
                            RequestSerialization();
                        }
                    }
                }
            }
        }

        private void NotPilotingTick(float dt)
        {
            // Smooth toward last received aim.
            // IMPORTANT: aim is sampled/sent in local space (localEulerAngles), so apply in local space too.
            NonOwnerGunAngleSlerper = Quaternion.Slerp(NonOwnerGunAngleSlerper, _remoteTargetRot, 4f * dt);

            // Apply the combined (pitch+yaw) local rotation, then split exactly like the pilot does.
            Gun.localRotation = NonOwnerGunAngleSlerper;
            Vector3 mgrotH = Gun.localEulerAngles;
            if (GunPitch)
            {
                mgrotH.z = 0;
                Vector3 mgrotP = mgrotH;
                mgrotH.x = 0;
                mgrotP.y = 0;
                GunPitch.localRotation = Quaternion.Euler(mgrotP);
            }
            else
            {
                mgrotH.z = 0;
            }
            Gun.localRotation = Quaternion.Euler(mgrotH);
        }
    }
}