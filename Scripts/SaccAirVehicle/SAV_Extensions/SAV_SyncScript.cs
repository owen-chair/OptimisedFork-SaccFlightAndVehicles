using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace SaccFlightAndVehicles
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    [DefaultExecutionOrder(10)]
    public class SAV_SyncScript : UdonSharpBehaviour
    {
        public UdonSharpBehaviour SAVControl;

        [Tooltip("Delay between updates in seconds")]
        [Range(0.05f, 1f)]
        public float updateInterval = 0.2f;

        [Tooltip("Delay between updates in seconds when the sync has entered idle mode")]
        public float IdleModeUpdateInterval = 3f;

        [Tooltip("Freeze the vehicle's position when it's dead? Turn off for boats that sink etc")]
        public bool FreezePositionOnDeath = true;

        [Tooltip("If vehicle moves less than this distance since it's last update, it'll be considered to be idle, may need to be increased for vehicles that want to be idle on water. If the vehicle floats away sometimes, this value is probably too big")]
        public float IdleMovementRange = .35f;

        [Tooltip("If vehicle rotates less than this many degrees since it's last update, it'll be considered to be idle")]
        public float IdleRotationRange = 5f;

        [Tooltip("Angle Difference between movement direction and rigidbody velocity that will cause the vehicle to teleport instead of interpolate")]
        public float TeleportAngleDifference = 20;

        [Tooltip("How much vehicle accelerates extra towards its 'raw' position when not owner in order to correct positional errors")]
        public float CorrectionTime = 8f;

        [Tooltip("How quickly non-owned vehicle's velocity vector lerps towards its new value")]
        public float SpeedLerpTime = 4f;

        [Tooltip("Strength of force to stop correction overshooting target")]
        public float CorrectionDStrength = 1.666666f;

        [Tooltip("How much vehicle accelerates extra towards its 'raw' rotation when not owner in order to correct rotational errors")]
        public float CorrectionTime_Rotation = 1f;

        [Tooltip("How quickly non-owned vehicle's rotation slerps towards its new value")]
        public float RotationSpeedLerpTime = 10f;

        [Tooltip("Teleports owned vehicles forward by real time * velocity if frame takes too long to render and simulation slows down. Prevents other players from seeing you warp.")]
        public bool AntiWarp = true;

        [Tooltip("Enable physics whilst not owner of the vehicle, can prevent some clipping through walls/ground, probably some performance hit. Not recommended for Quest")]
        public bool NonOwnerEnablePhysics = false;

        [Header("Fill SyncRigid to enable Object Mode (No SAVControl)")]
        public Rigidbody SyncRigid;

        [Header("DEBUG:")]
        [Tooltip("LEAVE THIS EMPTY UNLESS YOU WANT TO TEST THE NETCODE OFFLINE WITH CLIENT SIM")]
        public Transform SyncTransform;

        [Tooltip("LEAVE THIS EMPTY UNLESS YOU WANT TO TEST THE NETCODE OFFLINE WITH CLIENT SIM")]
        public Transform SyncTransform_Raw;

        // Use float for time to reduce payload; precision is sufficient for per-update deltas.
        [UdonSynced] private float O_UpdateTime;
        // Delta-compressed position/rotation.
        // Normal packets: O_PosDelta* is position delta (meters * _PosDeltaQuantInv), O_RotDelta* is Euler delta (degrees * _RotDeltaQuantInv).
        // Keyframes: O_PosDelta* is low16 of absolute position (meters * _PosAbsQuantInv), O_RotDelta* is high16 of absolute position.
        [UdonSynced] private byte O_Flags;
        [UdonSynced] private short O_PosDeltaX;
        [UdonSynced] private short O_PosDeltaY;
        [UdonSynced] private short O_PosDeltaZ;
        [UdonSynced] private short O_RotDeltaX;
        [UdonSynced] private short O_RotDeltaY;
        [UdonSynced] private short O_RotDeltaZ;

        // Velocity compressed into shorts to reduce bandwidth.
        // Normal packets: velocity. Keyframes: quaternion xyz.
        [UdonSynced] private short O_VelX;
        [UdonSynced] private short O_VelY;
        [UdonSynced] private short O_VelZ;

        // Angular velocity compressed into shorts (rad/s).
        // Normal packets: angular velocity. Keyframes: velocity.
        [UdonSynced] private short O_AngVelX;
        [UdonSynced] private short O_AngVelY;
        [UdonSynced] private short O_AngVelZ;

        private Vector3 O_CurVel = Vector3.zero;
        private Vector3 O_CurAngVel = Vector3.zero;

        [System.NonSerialized] public SaccEntity EntityControl;
        public bool IdleUpdateMode;

        private const float _MinUpdateGap = 0.05f;
        private const float _MaxUpdateGap = 5f;
        private const float _HardSnapDistance = 25f;
        private const float _MaxExtrapSeconds = 0.35f;
        private const float _TimeHitchThreshold = 0.099f;
        private const float _NonOwnerBackTime = 0.05f;
        private const float _MaxEstimatedAccel = 60f;
        private const float _MaxEstimatedAngAccel = 10f;
        private const float _MaxEstimatedAngVel = 12f;
        private const float _AngVelQuant = 0.01f;
        private const float _AngVelQuantInv = 100f;
        private const int _AntiWarpMask = 133121;
        private const float _PilotingStableInterval = 0.25f;
        private const float _CollisionForceSendCooldown = 0.10f;
        private const float _TempNonOwnerPhysicsSeconds = 1.25f;
        private const float _OwnershipGrabCooldownSeconds = 0.25f;
        private const float _SettleAssistSeconds = 3.0f;
        private const float _SettleUpdateInterval = 0.35f;
        private const float _SettleDrag = 2.0f;
        private const float _SettleAngDrag = 4.0f;

        private const float _PlayerCacheRefreshSeconds = 0.5f;
        private const float _NearPlayerRadiusSqr = 500f * 500f;
        private const float _VelQuant = 0.1f;
        private const float _VelQuantInv = 10f;

        private const byte _FlagKeyframe = 1;
        private const int _KeyframeEveryPackets = 30;
        private const float _PosDeltaQuant = 0.01f;
        private const float _PosDeltaQuantInv = 100f;
        private const float _PosAbsQuant = 0.01f;
        private const float _PosAbsQuantInv = 100f;
        private const float _RotDeltaQuant = 0.1f;
        private const float _RotDeltaQuantInv = 10f;

        private VRCPlayerApi[] _playerCache;
        private float _nextPlayerCacheRefresh;
        private bool _cachedAnyOtherPlayerNear;

        private bool _initialized;
        private bool _objectMode;
        private bool _isOwner;
        private bool _piloting;

        // Delta-accumulator (last reconstructed/serialized state). Used on owner for UT-style error-threshold simulation and on non-owners to reconstruct deltas.
        private Vector3 _deltaAccumPos;
        private Quaternion _deltaAccumRot;
        private bool _hasDeltaBase;
        private int _deltaPacketsSinceKeyframe;
        private bool _forceKeyframe;
        private bool _antiWarpArmed;
        private bool _disableAntiWarp;

        private Transform _vehicleTransform;
        private Rigidbody _vehicleRigid;
        private float _startDrag;
        private float _startAngDrag;
        private float _cosTeleportAngleDifference;

        private double _startupServerTime;
        private double _startupLocalTime;
        private double _nextSendTime = double.MaxValue;
        private double _lastOwnerFrameServerTime;
        private double _nextCollisionForceSendTime;

        [Tooltip("Pos error (m) to force send")]
        public float SendPosErrorThreshold = 0.5f;

        [Tooltip("Rot error (deg) to force send")]
        public float SendRotErrorThreshold = 5f;

        [Header("Tick Rates")]
        [Tooltip("How often to run OwnerTick() in Hz. Higher = smoother/more CPU/network responsiveness.")]
        [Range(1f, 120f)]
        public float OwnerTickRateHz = 60f;

        [Tooltip("How often to run NonOwnerTick() in Hz. Higher = smoother remote motion/more CPU.")]
        [Range(1f, 120f)]
        public float NonOwnerTickRateHz = 30f;

        private int _updatesSentWhileStill;
        private int _stillToEnterIdleCount;
        private float _stillToEnterIdleInv;

        private bool _tempNonOwnerPhysicsActive;
        private double _tempNonOwnerPhysicsUntil;

        private double _settleAssistUntil;
        private bool _settleDragApplied;
        private double _lastOwnershipGrabTime;

        // Tick scheduling (Time.time seconds)
        private float _nextOwnerTickTime;
        private float _nextNonOwnerTickTime;
        private float _lastOwnerTickTime;
        private float _lastNonOwnerTickTime;

        private bool _hasSnap0;
        private bool _hasSnap1;
        private double _snap0Time;
        private double _snap1Time;
        private Vector3 _snap0Pos;
        private Vector3 _snap1Pos;
        private Quaternion _snap0Rot = Quaternion.identity;
        private Quaternion _snap1Rot = Quaternion.identity;
        private Vector3 _snap0Vel;
        private Vector3 _snap1Vel;
        private Vector3 _snap0Accel;
        private Vector3 _snap1Accel;
        private Vector3 _snap0AngVel;
        private Vector3 _snap1AngVel;
        private Vector3 _snap0AngAccel;
        private Vector3 _snap1AngAccel;
        private float _remoteDelta = 0.25f;

        private float _dbgPing;
        private static short ClampToShort(int v)
        {
            if (v > short.MaxValue) { return short.MaxValue; }
            if (v < short.MinValue) { return short.MinValue; }
            return (short)v;
        }

        private void Start()
        {
            if (SyncRigid)
            {
                _objectMode = true;
                _vehicleRigid = SyncRigid;
                _vehicleTransform = SyncRigid.transform;
                if (!SyncTransform) { SyncTransform = _vehicleTransform; }
                if (!_initialized) { SFEXT_L_EntityStart(); }
                return;
            }

            if (!SyncTransform)
            {
                // In some setups this can be left enabled accidentally.
                // Keep behaviour safe but prefer the proper EntityStart path.
                if (SAVControl)
                {
                    SaccEntity ec = (SaccEntity)SAVControl.GetProgramVariable("EntityControl");
                    if (ec)
                    {
                        _vehicleRigid = ec.GetComponent<Rigidbody>();
                        _vehicleTransform = _vehicleRigid ? _vehicleRigid.transform : null;
                        SyncTransform = _vehicleTransform;
                    }
                }
            }
        }

        public void SFEXT_L_EntityStart()
        {
            _initialized = true;

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            bool inEditor = !Utilities.IsValid(localPlayer);

            if (SyncRigid)
            {
                _objectMode = true;
                _vehicleRigid = SyncRigid;
                _vehicleTransform = SyncRigid.transform;
            }
            else
            {
                _vehicleTransform = EntityControl ? EntityControl.transform : null;
                _vehicleRigid = EntityControl ? EntityControl.VehicleRigidbody : null;
            }

            if (!_vehicleTransform || !_vehicleRigid)
            {
                enabled = false;
                return;
            }

            if (!SyncTransform) { SyncTransform = _vehicleTransform; }

            _startDrag = _vehicleRigid.drag;
            _startAngDrag = _vehicleRigid.angularDrag;
            _cosTeleportAngleDifference = Mathf.Cos(TeleportAngleDifference * Mathf.Deg2Rad);

            if (!inEditor)
            {
                _isOwner = _objectMode ? Networking.IsOwner(SyncRigid.gameObject) : (EntityControl && EntityControl.IsOwner);
            }
            else
            {
                _isOwner = true;
            }

            _stillToEnterIdleCount = Mathf.Max(2, Mathf.CeilToInt(0.75f / Mathf.Max(0.01f, updateInterval)));
            _stillToEnterIdleInv = 1f / (float)_stillToEnterIdleCount;

            ApplyOwnerStatePhysics();
            InitLocalTimeBase();
            InitSnapshotsFromCurrent();

            SendCustomEventDelayedSeconds(nameof(ActivateScript), 5);
        }

        public void ActivateScript()
        {
            InitLocalTimeBase();
            InitSnapshotsFromCurrent();
            gameObject.SetActive(true);
            _vehicleRigid.constraints = RigidbodyConstraints.None;
            SetPhysics();
            _antiWarpArmed = AntiWarp;
            if (EntityControl) { EntityControl.SendEventToExtensions("SFEXT_L_WakeUp"); }
        }

        private void InitLocalTimeBase()
        {
            ResetSyncTimes();
            double now = GetNowServerTime();
            _nextSendTime = now + Random.Range(0f, updateInterval);
            _lastOwnerFrameServerTime = now;
        }

        private void InitSnapshotsFromCurrent()
        {
            Vector3 p = _vehicleTransform.position;
            Quaternion r = _vehicleTransform.rotation;
            Vector3 v = _vehicleRigid.velocity;
            double now = GetNowServerTime();

            _deltaAccumPos = p;
            _deltaAccumRot = r;
            NormalizeQuaternion(ref _deltaAccumRot);
            _hasDeltaBase = true;
            _deltaPacketsSinceKeyframe = 0;
            _forceKeyframe = true;

            // Seed synced values as a keyframe-shaped state so offline testing and late init have consistent data.
            WriteKeyframeInternal(p, r, v, now);

            _snap0Pos = p;
            _snap1Pos = p;
            _snap0Rot = r;
            _snap1Rot = r;
            _snap0Vel = v;
            _snap1Vel = v;
            _snap0Accel = Vector3.zero;
            _snap1Accel = Vector3.zero;
            _snap0AngVel = Vector3.zero;
            _snap1AngVel = Vector3.zero;
            _snap0AngAccel = Vector3.zero;
            _snap1AngAccel = Vector3.zero;
            _snap0Time = now - updateInterval;
            _snap1Time = now;
            _remoteDelta = updateInterval;
            _hasSnap0 = true;
            _hasSnap1 = true;

            if (!SyncTransform) { SyncTransform = _vehicleTransform; }
            if (SyncTransform) { SyncTransform.SetPositionAndRotation(p, r); }
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            // Ensure late joiners get a self-contained state quickly.
            if (!_initialized || !_isOwner) { return; }
            _forceKeyframe = true;
            _nextSendTime = GetNowServerTime();
        }

        private double GetNowServerTime()
        {
            return _startupServerTime + (double)(Time.time - (float)_startupLocalTime);
        }

        public void ResetSyncTimes()
        {
            _startupServerTime = Networking.GetServerTimeInSeconds();
            _startupLocalTime = Time.time;
        }

        public void SFEXT_L_OwnershipTransfer() { ExitIdleMode(); }
        public void SFEXT_O_TakeOwnership() { TakeOwnerStuff(); }
        public void SFEXT_O_LoseOwnership() { LoseOwnerStuff(); }

        private void TakeOwnerStuff()
        {
            _isOwner = true;
            ApplyOwnerStatePhysics();
            InitLocalTimeBase();
            ExitIdleMode();
            _nextSendTime = GetNowServerTime() - 0.01d;
            _updatesSentWhileStill = 0;
            _forceKeyframe = true;
            _deltaPacketsSinceKeyframe = 0;
        }

        private void LoseOwnerStuff()
        {
            _isOwner = false;
            ApplyOwnerStatePhysics();
            InitLocalTimeBase();

            // Seed snapshots from our last known reconstructed state so we don't jump to (0,0,0).
            Quaternion r = _deltaAccumRot;
            NormalizeQuaternion(ref r);
            _snap0Pos = _deltaAccumPos;
            _snap1Pos = _deltaAccumPos;
            _snap0Rot = r;
            _snap1Rot = r;
            _snap0Vel = O_CurVel;
            _snap1Vel = O_CurVel;
            _snap0Accel = Vector3.zero;
            _snap1Accel = Vector3.zero;
            _snap0AngVel = Vector3.zero;
            _snap1AngVel = Vector3.zero;
            _snap0AngAccel = Vector3.zero;
            _snap1AngAccel = Vector3.zero;
            _snap0Time = (double)O_UpdateTime - updateInterval;
            _snap1Time = (double)O_UpdateTime;
            _remoteDelta = updateInterval;
            _hasSnap0 = true;
            _hasSnap1 = true;

            _hasDeltaBase = true;

            if (SyncTransform) { SyncTransform.SetPositionAndRotation(_deltaAccumPos, r); }
        }

        public void SFEXT_O_PilotEnter()
        {
            _piloting = true;
            ExitIdleMode();
            _nextSendTime = GetNowServerTime() - 0.01d;
            _forceKeyframe = true;
            SendCustomEventDelayedFrames(nameof(ResetSyncTimes), 1);
        }

        public void SFEXT_O_PilotExit() { _piloting = false; }

        public void SFEXT_G_RespawnButton()
        {
            ExitIdleMode();
            if (_isOwner)
            {
                ResetSyncTimes();
                _nextSendTime = GetNowServerTime() - 0.01d;
                _forceKeyframe = true;
            }

            // Reset non-owner smoothing state so we snap cleanly to next snapshot.
            _hasSnap0 = false;
            _hasSnap1 = false;
        }

        private void Update()
        {
            if (!_initialized || !_vehicleTransform || !_vehicleRigid || !SyncTransform) { return; }

            double now = GetNowServerTime();
            float nowLocal = Time.time;
            float dt = Time.deltaTime;
            if (dt > _TimeHitchThreshold)
            {
                ResetSyncTimes();
                now = Networking.GetServerTimeInSeconds();

                // Time base changed; resync tick schedulers so we don't stall.
                _nextOwnerTickTime = 0f;
                _nextNonOwnerTickTime = 0f;
                _lastOwnerTickTime = 0f;
                _lastNonOwnerTickTime = 0f;
            }

            // If non-owner physics is usually disabled, temporarily enable it around collisions so
            // the rigidbody can still settle under gravity/contacts (prevents "hovering" while kinematic).
            if (!_isOwner && !NonOwnerEnablePhysics)
            {
                if (_tempNonOwnerPhysicsActive)
                {
                    if (now >= _tempNonOwnerPhysicsUntil)
                    {
                        _tempNonOwnerPhysicsActive = false;
                        ApplyOwnerStatePhysics();
                    }
                }
                else
                {
                    if (now < _tempNonOwnerPhysicsUntil)
                    {
                        _tempNonOwnerPhysicsActive = true;
                        ApplyOwnerStatePhysics();
                    }
                }
            }

            if (_isOwner)
            {
                float interval = 1f / Mathf.Max(1f, OwnerTickRateHz);
                if (nowLocal < _nextOwnerTickTime) { return; }

                float tickDt = (_lastOwnerTickTime > 0f) ? (nowLocal - _lastOwnerTickTime) : dt;
                _lastOwnerTickTime = nowLocal;
                _nextOwnerTickTime = nowLocal + interval;

                OwnerTick(now, tickDt);
            }
            else
            {
                float interval = 1f / Mathf.Max(1f, NonOwnerTickRateHz);
                if (nowLocal < _nextNonOwnerTickTime) { return; }

                float tickDt = (_lastNonOwnerTickTime > 0f) ? (nowLocal - _lastNonOwnerTickTime) : dt;
                _lastNonOwnerTickTime = nowLocal;
                _nextNonOwnerTickTime = nowLocal + interval;

                NonOwnerTick(now, tickDt);
            }
        }

        private void OwnerTick(double now, float dt)
        {
            if(EntityControl != null && EntityControl.transform.position.y < -1000f)
            {
                EntityControl.SendRespawn();
                return;
            }

            if (dt > _TimeHitchThreshold && _antiWarpArmed && !_disableAntiWarp)
            {
                double accurateDelta = now - _lastOwnerFrameServerTime;
                if (accurateDelta > 0.001d)
                {
                    Vector3 movedByUnity = _vehicleRigid.velocity * dt;
                    Vector3 movedByAccurate = _vehicleRigid.velocity * (float)accurateDelta;
                    Vector3 extra = movedByAccurate - movedByUnity;
                    float extraMag = extra.magnitude;
                    if (extraMag > 0.001f)
                    {
                        if (!Physics.Raycast(_vehicleRigid.position, _vehicleRigid.velocity, extraMag, _AntiWarpMask, QueryTriggerInteraction.Ignore))
                        {
                            Vector3 corrected = _vehicleRigid.position + extra;
                            _vehicleTransform.position = corrected;
                            _vehicleRigid.position = corrected;
                        }
                    }
                }
            }
            _lastOwnerFrameServerTime = now;

            bool instanceClogged = Networking.IsClogged;

            Vector3 pos = _vehicleTransform.position;
            Quaternion rot = _vehicleTransform.rotation;
            Vector3 vel = _vehicleRigid.velocity;

            bool unoccupied = IsUnoccupiedVehicle();
            bool settleAssist = unoccupied && (now < _settleAssistUntil);
            if (settleAssist)
            {
                ApplySettleDrag(true);
            }
            else
            {
                ApplySettleDrag(false);
            }

            float sendInterval = updateInterval;
            if (settleAssist)
            {
                sendInterval = Mathf.Max(sendInterval, _SettleUpdateInterval);
            }

            // Distance-based throttling: if there are no other players within 500m, cap update rate to 1Hz.
            _UpdateAnyOtherPlayerNearCache(pos);
            if (!_cachedAnyOtherPlayerNear)
            {
                sendInterval = Mathf.Max(sendInterval, 1f);
            }

            float dist = Vector3.Distance(pos, _deltaAccumPos);
            float ang = Quaternion.Angle(rot, _deltaAccumRot);
            bool still = !_piloting && dist < IdleMovementRange && ang < IdleRotationRange;

            // Error-threshold sending (UT-style): simulate what a non-owner would predict from the last sent snapshot,
            // and only send early when the prediction error exceeds tolerances.
            if (now < _nextSendTime)
            {
                double dtSinceSent = now - (double)O_UpdateTime;
                // Don't allow error-threshold early-send to effectively double (or worse) the base send rate.
                // Collisions already force an immediate send via SFEXT_L_OnCollisionEnter().
                double earlySendGate = (double)sendInterval * 0.75d;
                if (earlySendGate < (double)_MinUpdateGap) { earlySendGate = (double)_MinUpdateGap; }

                if (dtSinceSent > earlySendGate)
                {
                    float exf = (float)dtSinceSent;
                    if (exf < 0.001f) { exf = 0.001f; }

                    Vector3 basePos = _deltaAccumPos;
                    Quaternion baseRot = _deltaAccumRot;
                    Vector3 baseVel = O_CurVel;
                    Vector3 baseAngVel = O_CurAngVel;

                    Vector3 accelEst = (vel - baseVel) / exf;
                    float aMag = accelEst.magnitude;
                    if (aMag > _MaxEstimatedAccel) { accelEst = accelEst * (_MaxEstimatedAccel / aMag); }

                    Vector3 predictedPos = basePos + (baseVel * exf) + (0.5f * accelEst * (exf * exf));

                    Quaternion predictedRot;
                    float wMag = baseAngVel.magnitude;
                    if (wMag > 0.0001f)
                    {
                        float angDeg = wMag * exf * Mathf.Rad2Deg;
                        predictedRot = baseRot * Quaternion.AngleAxis(angDeg, baseAngVel / wMag);
                    }
                    else
                    {
                        predictedRot = baseRot;
                    }

                    float posErr = Vector3.Distance(pos, predictedPos);
                    float rotErr = Quaternion.Angle(rot, predictedRot);
                    if (posErr > SendPosErrorThreshold || rotErr > SendRotErrorThreshold)
                    {
                        _nextSendTime = now;
                    }
                }

                if (now < _nextSendTime) { return; }
            }

            if (still)
            {
                _updatesSentWhileStill++;
                if (_updatesSentWhileStill >= _stillToEnterIdleCount) { EnterIdleMode(); }
            }
            else
            {
                ExitIdleMode();
            }

            bool keepAliveDue = (now - O_UpdateTime) > IdleModeUpdateInterval;
            bool shouldSend = _piloting || !still || keepAliveDue;

            if (instanceClogged && !_piloting && !keepAliveDue)
            {
                shouldSend = false;
            }

            if (shouldSend)
            {
                Vector3 av = _vehicleRigid.angularVelocity;

                bool sendKeyframe = _forceKeyframe || !_hasDeltaBase || (_deltaPacketsSinceKeyframe >= _KeyframeEveryPackets);

                // Try delta encoding; if it overflows, force a keyframe.
                short pdx = 0, pdy = 0, pdz = 0;
                short rdx = 0, rdy = 0, rdz = 0;
                Vector3 qDeltaPos = Vector3.zero;
                Vector3 qDeltaEuler = Vector3.zero;

                if (!sendKeyframe)
                {
                    Vector3 dp = pos - _deltaAccumPos;
                    int ix = Mathf.RoundToInt(dp.x * _PosDeltaQuantInv);
                    int iy = Mathf.RoundToInt(dp.y * _PosDeltaQuantInv);
                    int iz = Mathf.RoundToInt(dp.z * _PosDeltaQuantInv);
                    if (ix < short.MinValue || ix > short.MaxValue || iy < short.MinValue || iy > short.MaxValue || iz < short.MinValue || iz > short.MaxValue)
                    {
                        sendKeyframe = true;
                    }
                    else
                    {
                        pdx = (short)ix;
                        pdy = (short)iy;
                        pdz = (short)iz;
                        qDeltaPos = new Vector3(pdx * _PosDeltaQuant, pdy * _PosDeltaQuant, pdz * _PosDeltaQuant);

                        Quaternion dq = rot * Quaternion.Inverse(_deltaAccumRot);
                        NormalizeQuaternion(ref dq);
                        Vector3 e = dq.eulerAngles;
                        e.x = Wrap180(e.x);
                        e.y = Wrap180(e.y);
                        e.z = Wrap180(e.z);

                        int ex = Mathf.RoundToInt(e.x * _RotDeltaQuantInv);
                        int ey = Mathf.RoundToInt(e.y * _RotDeltaQuantInv);
                        int ez = Mathf.RoundToInt(e.z * _RotDeltaQuantInv);
                        if (ex < short.MinValue || ex > short.MaxValue || ey < short.MinValue || ey > short.MaxValue || ez < short.MinValue || ez > short.MaxValue)
                        {
                            sendKeyframe = true;
                        }
                        else
                        {
                            rdx = (short)ex;
                            rdy = (short)ey;
                            rdz = (short)ez;
                            qDeltaEuler = new Vector3(rdx * _RotDeltaQuant, rdy * _RotDeltaQuant, rdz * _RotDeltaQuant);
                        }
                    }
                }

                if (sendKeyframe)
                {
                    WriteKeyframe(pos, rot, vel, now);
                }
                else
                {
                    WriteDelta(pdx, pdy, pdz, rdx, rdy, rdz, qDeltaPos, qDeltaEuler, vel, av, now);
                }
            }

            if (_piloting || !still)
            {
                // During stable piloting, stretch interval a bit to reduce bandwidth.
                if (_piloting && !keepAliveDue && !_disableAntiWarp)
                {
                    sendInterval = Mathf.Max(sendInterval, _PilotingStableInterval);
                }
                _nextSendTime = now + sendInterval;
                return;
            }

            if (!IdleUpdateMode)
            {
                float ramp = (_updatesSentWhileStill - 1) * _stillToEnterIdleInv;
                if (ramp < 0f) { ramp = 0f; }
                else if (ramp > 1f) { ramp = 1f; }
                _nextSendTime = now + Mathf.Lerp(sendInterval, IdleModeUpdateInterval, ramp);
            }
            else
            {
                _nextSendTime = now + IdleModeUpdateInterval;
            }
        }

        private bool IsUnoccupiedVehicle()
        {
            if (_objectMode) { return false; }
            if (!EntityControl) { return false; }
            return !EntityControl.Occupied;
        }

        private void _UpdateAnyOtherPlayerNearCache(Vector3 center)
        {
            float now = Time.time;
            if (now < _nextPlayerCacheRefresh) { return; }
            _nextPlayerCacheRefresh = now + _PlayerCacheRefreshSeconds;

            int count = VRCPlayerApi.GetPlayerCount();
            if (count <= 0)
            {
                _cachedAnyOtherPlayerNear = false;
                return;
            }

            if (_playerCache == null || _playerCache.Length != count)
            {
                _playerCache = new VRCPlayerApi[count];
            }

            _playerCache = VRCPlayerApi.GetPlayers(_playerCache);

            bool anyNear = false;
            for (int i = 0; i < _playerCache.Length; i++)
            {
                VRCPlayerApi p = _playerCache[i];
                if (!Utilities.IsValid(p)) { continue; }
                // Owner tick / sync runs on the owner; ignore local player so "only owner nearby" throttles.
                if (p.isLocal) { continue; }
                Vector3 dp = p.GetPosition() - center;
                if (dp.sqrMagnitude <= _NearPlayerRadiusSqr)
                {
                    anyNear = true;
                    break;
                }
            }
            _cachedAnyOtherPlayerNear = anyNear;
        }

        private void ApplySettleDrag(bool enable)
        {
            if (_settleDragApplied == enable) { return; }
            if (!_isOwner) { return; }
            if (_objectMode) { return; }
            if (!_vehicleRigid) { return; }

            // Don't fight "frozen" or special states.
            if (_vehicleRigid.drag > 1000f || _vehicleRigid.angularDrag > 1000f) { return; }

            if (enable)
            {
                _vehicleRigid.drag = Mathf.Max(_vehicleRigid.drag, _SettleDrag);
                _vehicleRigid.angularDrag = Mathf.Max(_vehicleRigid.angularDrag, _SettleAngDrag);
            }
            else
            {
                // Normal SaccVehicles behaviour for owned non-object vehicles.
                _vehicleRigid.drag = 0f;
                _vehicleRigid.angularDrag = 0f;
            }

            _settleDragApplied = enable;
        }

        private void NonOwnerTick(double now, float dt)
        {
            if (!_hasSnap1)
            {
                // No network data yet.
                return;
            }

            if (!_hasSnap0 || _snap1Time <= _snap0Time)
            {
                // Only one snapshot: just converge to it.
                ApplySmoothedTarget(_snap1Pos, _snap1Rot, dt);
                return;
            }

            float delta = _remoteDelta;
            if (delta < _MinUpdateGap) { delta = _MinUpdateGap; }
            else if (delta > _MaxUpdateGap) { delta = _MaxUpdateGap; }

            // Fixed small back-time reduces perceived input delay; prediction handles the extra jitter risk.
            double backTime = (double)_NonOwnerBackTime;
            double renderTime = now - backTime;

            double span = _snap1Time - _snap0Time;
            double t = (renderTime - _snap0Time) / span;

            Vector3 targetPos;
            Quaternion targetRot;

            if (t <= 0d)
            {
                targetPos = _snap0Pos;
                targetRot = _snap0Rot;
            }
            else if (t < 1d)
            {
                float tf = (float)t;
                targetPos = Vector3.Lerp(_snap0Pos, _snap1Pos, tf);
                targetRot = Quaternion.Slerp(_snap0Rot, _snap1Rot, tf);
            }
            else
            {
                double ex = renderTime - _snap1Time;
                if (ex < 0d) { ex = 0d; }
                if (ex > _MaxExtrapSeconds) { ex = _MaxExtrapSeconds; }
                float exf = (float)ex;
                targetPos = _snap1Pos + (_snap1Vel * exf) + (0.5f * _snap1Accel * (exf * exf));
                Vector3 rotVec = (_snap1AngVel * exf) + (0.5f * _snap1AngAccel * (exf * exf));
                float rotMag = rotVec.magnitude;
                if (rotMag > 0.0001f)
                {
                    targetRot = _snap1Rot * Quaternion.AngleAxis(rotMag * Mathf.Rad2Deg, rotVec / rotMag);
                }
                else
                {
                    targetRot = _snap1Rot;
                }
            }

            ApplySmoothedTarget(targetPos, targetRot, dt);

            if (SyncTransform_Raw)
            {
                SyncTransform_Raw.SetPositionAndRotation(targetPos, targetRot);
            }

            if (NonOwnerEnablePhysics)
            {
                // Give the rigidbody a best-effort velocity so dynamic contacts are less weird.
                float exForVel = (float)Mathf.Clamp((float)(renderTime - _snap1Time), 0f, _MaxExtrapSeconds);
                Vector3 predictedVel = _snap1Vel + (_snap1Accel * exForVel);
                _vehicleRigid.velocity = Vector3.Lerp(_vehicleRigid.velocity, predictedVel, Mathf.Clamp01(SpeedLerpTime * dt));

                // And a best-effort angular velocity so turns / spins don't feel like they lag behind.
                Vector3 predictedAngVel = _snap1AngVel + (_snap1AngAccel * exForVel);
                _vehicleRigid.angularVelocity = Vector3.Lerp(_vehicleRigid.angularVelocity, predictedAngVel, Mathf.Clamp01(SpeedLerpTime * dt));
            }
        }

        private void ApplySmoothedTarget(Vector3 targetPos, Quaternion targetRot, float dt)
        {
            bool physicsActive = NonOwnerEnablePhysics || _tempNonOwnerPhysicsActive;
            bool driveRigidbody = physicsActive && _vehicleRigid && !_vehicleRigid.isKinematic;

            Vector3 curPos = driveRigidbody ? _vehicleRigid.position : SyncTransform.position;
            Quaternion curRot = driveRigidbody ? _vehicleRigid.rotation : SyncTransform.rotation;

            Vector3 err = targetPos - curPos;
            if (err.sqrMagnitude > (_HardSnapDistance * _HardSnapDistance))
            {
                if (driveRigidbody)
                {
                    _vehicleRigid.position = targetPos;
                    _vehicleRigid.rotation = targetRot;
                    _vehicleRigid.velocity = Vector3.zero;
                    _vehicleRigid.angularVelocity = Vector3.zero;
                }
                else
                {
                    SyncTransform.SetPositionAndRotation(targetPos, targetRot);
                }
                return;
            }

            float posAlpha = 1f - Mathf.Exp(-Mathf.Max(0.01f, CorrectionTime) * dt);
            float rotAlpha = 1f - Mathf.Exp(-Mathf.Max(0.01f, CorrectionTime_Rotation) * dt);

            Vector3 newPos = Vector3.Lerp(curPos, targetPos, posAlpha);
            Quaternion newRot = Quaternion.Slerp(curRot, targetRot, rotAlpha);

            if (driveRigidbody)
            {
                float denom = Mathf.Max(0.02f, dt);
                Vector3 desiredVel = (targetPos - curPos) / denom;

                // Damping term helps prevent overshoot/bounce when corrections are large (common after rams).
                desiredVel -= _vehicleRigid.velocity * CorrectionDStrength * dt;

                float maxVel = 60f;
                if (desiredVel.sqrMagnitude > (maxVel * maxVel))
                {
                    desiredVel = Vector3.ClampMagnitude(desiredVel, maxVel);
                }
                _vehicleRigid.velocity = Vector3.Lerp(_vehicleRigid.velocity, desiredVel, Mathf.Clamp01(SpeedLerpTime * dt));
                _vehicleRigid.MoveRotation(newRot);
            }
            else
            {
                SyncTransform.SetPositionAndRotation(newPos, newRot);
            }
        }

        public override void OnDeserialization()
        {
            if (!_initialized || _isOwner) { return; }

            double updateTime = (double)O_UpdateTime;
            if (_hasSnap1 && updateTime <= _snap1Time) { return; }

            if (_hasSnap1)
            {
                _snap0Pos = _snap1Pos;
                _snap0Rot = _snap1Rot;
                _snap0Vel = _snap1Vel;
                _snap0Accel = _snap1Accel;
                _snap0AngVel = _snap1AngVel;
                _snap0AngAccel = _snap1AngAccel;
                _snap0Time = _snap1Time;
                _hasSnap0 = true;
            }
            else
            {
                _hasSnap0 = false;
            }

            bool keyframe = (O_Flags & _FlagKeyframe) != 0;
            if (keyframe)
            {
                int px = UnpackInt32FromShorts(O_PosDeltaX, O_RotDeltaX);
                int py = UnpackInt32FromShorts(O_PosDeltaY, O_RotDeltaY);
                int pz = UnpackInt32FromShorts(O_PosDeltaZ, O_RotDeltaZ);
                _deltaAccumPos = new Vector3(px * _PosAbsQuant, py * _PosAbsQuant, pz * _PosAbsQuant);

                Quaternion q = DecodeQuatFromShortXYZ(O_VelX, O_VelY, O_VelZ);
                _deltaAccumRot = q;
                _hasDeltaBase = true;

                _snap1Pos = _deltaAccumPos;
                _snap1Rot = _deltaAccumRot;

                // Keyframes repurpose O_AngVel* to carry linear velocity.
                _snap1Vel = new Vector3(O_AngVelX * _VelQuant, O_AngVelY * _VelQuant, O_AngVelZ * _VelQuant);
                _snap1AngVel = Vector3.zero;
                O_CurVel = _snap1Vel;
            }
            else
            {
                // Can't apply deltas until we have a base.
                if (!_hasDeltaBase) { return; }

                Vector3 dp = new Vector3(O_PosDeltaX * _PosDeltaQuant, O_PosDeltaY * _PosDeltaQuant, O_PosDeltaZ * _PosDeltaQuant);
                Vector3 de = new Vector3(O_RotDeltaX * _RotDeltaQuant, O_RotDeltaY * _RotDeltaQuant, O_RotDeltaZ * _RotDeltaQuant);

                _deltaAccumPos += dp;
                Quaternion dqr = Quaternion.Euler(de);
                NormalizeQuaternion(ref dqr);
                _deltaAccumRot = dqr * _deltaAccumRot;
                NormalizeQuaternion(ref _deltaAccumRot);

                _snap1Pos = _deltaAccumPos;
                _snap1Rot = _deltaAccumRot;

                // Reconstruct velocity from compressed form.
                _snap1Vel = new Vector3(O_VelX * _VelQuant, O_VelY * _VelQuant, O_VelZ * _VelQuant);
                O_CurVel = _snap1Vel;

                // Reconstruct angular velocity from compressed form.
                _snap1AngVel = new Vector3(O_AngVelX * _AngVelQuant, O_AngVelY * _AngVelQuant, O_AngVelZ * _AngVelQuant);
            }

            _snap1Time = updateTime;
            _hasSnap1 = true;

            float gap = _hasSnap0 ? (float)(_snap1Time - _snap0Time) : updateInterval;
            if (gap < _MinUpdateGap) { gap = _MinUpdateGap; }
            else if (gap > _MaxUpdateGap) { gap = _MaxUpdateGap; }
            _remoteDelta = gap;

            if (_hasSnap0 && gap > 0.0001f)
            {
                Vector3 est = (_snap1Vel - _snap0Vel) / gap;
                float m = est.magnitude;
                if (m > _MaxEstimatedAccel) { est = est * (_MaxEstimatedAccel / m); }
                _snap1Accel = est;
            }
            else
            {
                _snap1Accel = Vector3.zero;
            }

            if (_hasSnap0 && gap > 0.0001f)
            {
                Vector3 est = (_snap1AngVel - _snap0AngVel) / gap;
                float m = est.magnitude;
                if (m > _MaxEstimatedAngAccel) { est = est * (_MaxEstimatedAngAccel / m); }
                _snap1AngAccel = est;
            }
            else
            {
                _snap1AngAccel = Vector3.zero;
            }

            // _snap1AngVel comes from sync (or zero on keyframes); no need to estimate from rotation deltas.

            if (!_objectMode && SAVControl)
            {
                SAVControl.SetProgramVariable("CurrentVel", _snap1Vel);
            }

            // Teleport detection / hard snap when the stream jumps.
            if (_hasSnap0)
            {
                Vector3 deltaPos = _snap1Pos - _snap0Pos;
                float deltaMag = deltaPos.magnitude;

                float expected = _snap1Vel.magnitude * gap;
                bool hugeJump = deltaMag > (expected * 2f + 10f);

                bool dirMismatch = false;
                float denom = deltaMag * _snap1Vel.magnitude;
                if (denom > 0.0001f)
                {
                    float cos = Vector3.Dot(deltaPos, _snap1Vel) / denom;
                    dirMismatch = cos < _cosTeleportAngleDifference;
                }

                if (hugeJump || (dirMismatch && deltaMag > 2f))
                {
                    SyncTransform.SetPositionAndRotation(_snap1Pos, _snap1Rot);
                }
            }
        }

        private void NormalizeQuaternion(ref Quaternion q)
        {
            float mag = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (mag > 0.00000001f)
            {
                float inv = 1f / Mathf.Sqrt(mag);
                q = new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
            }
            else
            {
                q = Quaternion.identity;
            }
        }

        private static float Wrap180(float degrees)
        {
            degrees = Mathf.Repeat(degrees + 180f, 360f) - 180f;
            return degrees;
        }

        // Udon runtime does NOT reliably emulate C#'s unchecked cast wraparound for int->short.
        // Converting 0..65535 to Int16 can throw instead of wrapping, so we explicitly bias into [-32768,32767].
        private static int ShortToUShortInt(short s)
        {
            int v = (int)s;
            return v < 0 ? (v + 65536) : v;
        }

        private static short UShortIntToShort(int u16)
        {
            // u16 is expected in [0,65535]
            if (u16 > 32767) { return (short)(u16 - 65536); }
            return (short)u16;
        }

        private static int UnpackInt32FromShorts(short lo, short hi)
        {
            int loU = ShortToUShortInt(lo);
            int hiU = ShortToUShortInt(hi);
            return (hiU << 16) | loU;
        }

        private static short PackLow16(int v)
        {
            int u16 = v & 0xFFFF;
            return UShortIntToShort(u16);
        }

        private static short PackHigh16(int v)
        {
            int u16 = (v >> 16) & 0xFFFF;
            return UShortIntToShort(u16);
        }

        private static Quaternion DecodeQuatFromShortXYZ(short sx, short sy, short sz)
        {
            float smv = short.MaxValue;
            float x = sx / smv;
            float y = sy / smv;
            float z = sz / smv;
            float ww = 1f - (x * x + y * y + z * z);
            float w = ww > 0f ? Mathf.Sqrt(ww) : 0f;
            Quaternion q = new Quaternion(x, y, z, w);
            float mag = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (mag > 0.00000001f)
            {
                float inv = 1f / Mathf.Sqrt(mag);
                q = new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
            }
            else
            {
                q = Quaternion.identity;
            }
            return q;
        }

        private void WriteKeyframe(Vector3 pos, Quaternion rot, Vector3 vel, double now)
        {
            WriteKeyframeInternal(pos, rot, vel, now);
            RequestSerialization();
        }

        private void WriteKeyframeInternal(Vector3 pos, Quaternion rot, Vector3 vel, double now)
        {
            O_Flags = _FlagKeyframe;

            int px = Mathf.RoundToInt(pos.x * _PosAbsQuantInv);
            int py = Mathf.RoundToInt(pos.y * _PosAbsQuantInv);
            int pz = Mathf.RoundToInt(pos.z * _PosAbsQuantInv);

            O_PosDeltaX = PackLow16(px);
            O_PosDeltaY = PackLow16(py);
            O_PosDeltaZ = PackLow16(pz);
            O_RotDeltaX = PackHigh16(px);
            O_RotDeltaY = PackHigh16(py);
            O_RotDeltaZ = PackHigh16(pz);

            NormalizeQuaternion(ref rot);
            if (rot.w < 0f)
            {
                rot.x = -rot.x;
                rot.y = -rot.y;
                rot.z = -rot.z;
                rot.w = -rot.w;
            }
            float smv = short.MaxValue;
            O_VelX = ClampToShort(Mathf.RoundToInt(rot.x * smv));
            O_VelY = ClampToShort(Mathf.RoundToInt(rot.y * smv));
            O_VelZ = ClampToShort(Mathf.RoundToInt(rot.z * smv));

            // Keyframes repurpose O_AngVel* to carry linear velocity.
            O_AngVelX = ClampToShort(Mathf.RoundToInt(vel.x * _VelQuantInv));
            O_AngVelY = ClampToShort(Mathf.RoundToInt(vel.y * _VelQuantInv));
            O_AngVelZ = ClampToShort(Mathf.RoundToInt(vel.z * _VelQuantInv));

            O_UpdateTime = (float)now;

            // Update local base to match exactly what remotes reconstruct.
            _deltaAccumPos = new Vector3(px * _PosAbsQuant, py * _PosAbsQuant, pz * _PosAbsQuant);
            _deltaAccumRot = DecodeQuatFromShortXYZ(O_VelX, O_VelY, O_VelZ);
            _hasDeltaBase = true;
            _deltaPacketsSinceKeyframe = 0;
            _forceKeyframe = false;

            O_CurVel = new Vector3(O_AngVelX * _VelQuant, O_AngVelY * _VelQuant, O_AngVelZ * _VelQuant);
            O_CurAngVel = Vector3.zero;
        }

        private void WriteDelta(short pdx, short pdy, short pdz, short rdx, short rdy, short rdz, Vector3 qDeltaPos, Vector3 qDeltaEuler, Vector3 vel, Vector3 angVel, double now)
        {
            O_Flags = 0;
            O_PosDeltaX = pdx;
            O_PosDeltaY = pdy;
            O_PosDeltaZ = pdz;
            O_RotDeltaX = rdx;
            O_RotDeltaY = rdy;
            O_RotDeltaZ = rdz;

            O_VelX = ClampToShort(Mathf.RoundToInt(vel.x * _VelQuantInv));
            O_VelY = ClampToShort(Mathf.RoundToInt(vel.y * _VelQuantInv));
            O_VelZ = ClampToShort(Mathf.RoundToInt(vel.z * _VelQuantInv));

            O_AngVelX = ClampToShort(Mathf.RoundToInt(angVel.x * _AngVelQuantInv));
            O_AngVelY = ClampToShort(Mathf.RoundToInt(angVel.y * _AngVelQuantInv));
            O_AngVelZ = ClampToShort(Mathf.RoundToInt(angVel.z * _AngVelQuantInv));

            O_UpdateTime = (float)now;
            RequestSerialization();

            // Update local base to match exactly what remotes reconstruct.
            _deltaAccumPos += qDeltaPos;
            Quaternion dq = Quaternion.Euler(qDeltaEuler);
            NormalizeQuaternion(ref dq);
            _deltaAccumRot = dq * _deltaAccumRot;
            NormalizeQuaternion(ref _deltaAccumRot);

            _hasDeltaBase = true;
            _deltaPacketsSinceKeyframe++;
            if (_deltaPacketsSinceKeyframe >= _KeyframeEveryPackets) { _forceKeyframe = true; }

            O_CurVel = new Vector3(O_VelX * _VelQuant, O_VelY * _VelQuant, O_VelZ * _VelQuant);
            O_CurAngVel = new Vector3(O_AngVelX * _AngVelQuant, O_AngVelY * _AngVelQuant, O_AngVelZ * _AngVelQuant);
        }

        private void EnterIdleMode() { IdleUpdateMode = true; }
        private void ExitIdleMode() { IdleUpdateMode = false; _updatesSentWhileStill = 0; }

        public void SFEXT_O_Explode()
        {
            if (_isOwner && FreezePositionOnDeath)
            {
                _vehicleRigid.drag = 9999;
                _vehicleRigid.angularDrag = 9999;
            }
        }

        public void SFEXT_G_ReAppear()
        {
            if (_isOwner)
            {
                _vehicleRigid.drag = 0;
                _vehicleRigid.angularDrag = 0;
            }
        }

        public void SFEXT_O_MoveToSpawn()
        {
            if (_isOwner)
            {
                _vehicleRigid.drag = 9999;
                _vehicleRigid.angularDrag = 9999;
            }
        }

        public void SetPhysics()
        {
            ApplyOwnerStatePhysics();
        }

        private void ApplyOwnerStatePhysics()
        {
            if (!_vehicleRigid) { return; }

            if (_isOwner)
            {
                _vehicleRigid.isKinematic = false;
                _vehicleRigid.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                _vehicleRigid.interpolation = RigidbodyInterpolation.Extrapolate;

                if (_objectMode)
                {
                    _vehicleRigid.drag = _startDrag;
                    _vehicleRigid.angularDrag = _startAngDrag;
                }
                else
                {
                    _vehicleRigid.drag = 0;
                    _vehicleRigid.angularDrag = 0;
                }
            }
            else
            {
                bool allowPhysics = NonOwnerEnablePhysics || _tempNonOwnerPhysicsActive;
                if (!allowPhysics)
                {
                    _vehicleRigid.isKinematic = true;
                    _vehicleRigid.drag = 9999;
                    _vehicleRigid.angularDrag = 9999;
                }
                else
                {
                    _vehicleRigid.isKinematic = false;
                    _vehicleRigid.drag = _startDrag;
                    _vehicleRigid.angularDrag = _startAngDrag;
                }

                _vehicleRigid.collisionDetectionMode = CollisionDetectionMode.Discrete;
                _vehicleRigid.interpolation = RigidbodyInterpolation.None;
            }
        }

        public void SFEXT_L_OnCollisionEnter()
        {
            ExitIdleMode();
            if (_isOwner)
            {
                double now = GetNowServerTime();
                if (now >= _nextCollisionForceSendTime)
                {
                    _nextSendTime = now - 0.01d;
                    _nextCollisionForceSendTime = now + (double)_CollisionForceSendCooldown;
                }

                // If we're unoccupied and got bumped into a pile, add damping briefly so it settles faster.
                if (!_piloting && IsUnoccupiedVehicle())
                {
                    _settleAssistUntil = now + (double)_SettleAssistSeconds;
                }

                // If we're the piloted vehicle, consolidate ownership of collided unoccupied vehicles
                // to this client to avoid multi-owner pileup spam.
                if (_piloting && EntityControl)
                {
                    TryConsolidateOwnershipFromCollision(GetNowServerTime());
                }
            }
            else
            {
                // If we don't usually simulate physics for non-owners, temporarily enable it so the
                // object can react to contact/gravity instead of remaining kinematic and potentially hovering.
                if (!NonOwnerEnablePhysics)
                {
                    double now = GetNowServerTime();
                    _tempNonOwnerPhysicsUntil = now + (double)_TempNonOwnerPhysicsSeconds;
                    if (!_tempNonOwnerPhysicsActive)
                    {
                        _tempNonOwnerPhysicsActive = true;
                        ApplyOwnerStatePhysics();
                    }
                }
            }
        }

        private void TryConsolidateOwnershipFromCollision(double now)
        {
            if (!EntityControl) { return; }
            if (!Utilities.IsValid(Networking.LocalPlayer)) { return; }
            if ((now - _lastOwnershipGrabTime) < (double)_OwnershipGrabCooldownSeconds) { return; }
            _lastOwnershipGrabTime = now;

            Collision col = EntityControl.LastCollisionEnter;
            if (col == null) { return; }

            Rigidbody otherRb = col.rigidbody;
            if (!otherRb && col.collider) { otherRb = col.collider.attachedRigidbody; }
            if (!otherRb) { return; }
            if (otherRb == _vehicleRigid) { return; }

            GameObject target = null;

            SaccEntity otherEntity = otherRb.GetComponentInParent<SaccEntity>();
            if (otherEntity)
            {
                if (otherEntity == EntityControl) { return; }
                if (otherEntity.Occupied) { return; }
                target = otherEntity.gameObject;
            }
            else
            {
                // Object-mode fallback
                SAV_SyncScript otherSync = otherRb.GetComponentInParent<SAV_SyncScript>();
                if (otherSync && otherSync.SyncRigid)
                {
                    target = otherSync.SyncRigid.gameObject;
                }
            }

            if (!target)
            {
                target = otherRb.gameObject;
            }

            if (!Networking.IsOwner(target))
            {
                Networking.SetOwner(Networking.LocalPlayer, target);
            }
        }

        public void SFEXT_L_FinishRace() { _disableAntiWarp = false; }
        public void SFEXT_L_StartRace() { _disableAntiWarp = true; }
        public void SFEXT_L_CancelRace() { _disableAntiWarp = false; }
        public void SFEXT_L_WakeUp() { ExitIdleMode(); }

        // unity slerp always uses shortest route to orientation rather than slerping to the actual quat. This undoes that
        public Quaternion RealSlerp(Quaternion p, Quaternion q, float t)
        {
            if (Quaternion.Dot(p, q) < 0)
            {
                float angle = Quaternion.Angle(p, q);
                if (angle == 0f) { return p; }
                float newvalue = (360f - angle) / angle;
                return Quaternion.SlerpUnclamped(p, q, -t * newvalue);
            }
            return Quaternion.SlerpUnclamped(p, q, t);
        }
    }
}
