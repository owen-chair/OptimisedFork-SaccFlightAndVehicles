
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.UdonNetworkCalling;

namespace SaccFlightAndVehicles
{
    [DefaultExecutionOrder(1400)]//before wheels
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class SaccGroundVehicle : UdonSharpBehaviour
    {
        public SaccEntity EntityControl;
        [Tooltip("The object containing all non-trigger colliders for the vehicle, their layers are changed when entering and exiting")]
        public Transform VehicleMesh;
        [Tooltip("Largest renderer on the vehicle, for optimization purposes, checking if visible")]
        public Renderer MainObjectRenderer;
        [Tooltip("Change all children of VehicleMesh, or just the objects with colliders?")]
        public bool OnlyChangeColliders = false;
        [UdonSynced] public float Health = 73f;
        public Animator VehicleAnimator;
        [System.NonSerialized] public Transform VehicleTransform;
        [System.NonSerialized] public Rigidbody VehicleRigidbody;
        [Tooltip("Number of steps per second engine speedup code should run, higher number = smoother engine, effects performance of vehicle somewhat")]
        public int NumStepsSec = 1000;
        int _numStepsSec = 1;
        [Tooltip("List of wheels to send Engine values to and from")]
        public UdonSharpBehaviour[] DriveWheels;
        [Tooltip("Wheels to get the 'Grounded' value from for autosteering")]
        public UdonSharpBehaviour[] SteerWheels;
        [Tooltip("All of the rest of the wheels")]
        public UdonSharpBehaviour[] OtherWheels;
        private UdonSharpBehaviour[] AllWheels;

        // Cached typed wheel refs for performance.
        // Public wheel arrays stay as UdonSharpBehaviour[] for inspector compatibility.
        [System.NonSerialized] private SaccWheel[] _DriveWheelsTyped;
        [System.NonSerialized] private SaccWheel[] _SteerWheelsTyped;
        [System.NonSerialized] private SaccWheel[] _OtherWheelsTyped;
        [System.NonSerialized] private int _DriveWheelsCount;
        [System.NonSerialized] private int _SteerWheelsCount;
        [System.NonSerialized] private int _OtherWheelsCount;
        [System.NonSerialized] private bool[] _WheelIsDupBuffer;
        //public Transform[] DriveWheelsTrans;
        //public sustest[] SteeringWheels;
        //public Transform[] SteeringWheelsTrans;
        [Tooltip("How many revs are added when accelerating")]
        public float DriveSpeed;
        [Tooltip("Max revs of the engine")]
        public float RevLimiter = 8000;
        [Tooltip("How many revs are taken away all the time")]
        public float EngineSlowDown = 1f;
        [Tooltip("Throttle that is applied when not touching the controls")]
        public float MinThrottle = .08f;
        [Tooltip("How agressively to reach minthrottle value when not touching the controls")]
        public float MinThrottle_PStrength = 2f;
        [Tooltip("Amount of max DriveSpeed that keyboard users have access to, to stop them spinning out")]
        public float DriveSpeedKeyboardMax = 1f;
        //public float SteerAngle;
        //public float CurrentSteerAngle;
        /*  [UdonSynced(UdonSyncMode.None)]  */

        [Tooltip("How far down you have to push the grip button to grab the joystick and throttle")]
        public float GripSensitivity = .75f;
        [Tooltip("How many degrees the wheel can turn away from neutral position (lock to lock / 2), animation should match this")]
        public float SteeringWheelDegrees = 450f;
        // [Tooltip("How much VR users must twist their hands to reach max throttle, animation should match this")]
        // public float ThrottleDegrees = 50f;
        [Tooltip("How long keyboard turning must be held down to reach full deflection")]
        public float SteeringKeyboardSecsToMax = 0.5f;
        [Tooltip("how fast steering wheel returns to neutral position in desktop mode 1 = 1 second, .2 = 5 seconds")]
        public float SteeringReturnSpeedDT = .5f;

        [Tooltip("Reduce desktop max steering linearly up to this speed M/s")]
        public float SteeringMaxSpeedDT = 40f;
        [Tooltip("Disable the above feature")]
        public bool SteeringMaxSpeedDTDisabled = false;
        [Tooltip("Steering is reduced but to a minimum of this value")]
        public float DesktopMinSteering = .2f;
        [Tooltip("how fast throttle reaches max in desktop mode in seconds")]
        public float ThrottleToMaxTimeDT = .0001f;
        [Tooltip("how fast throttle reaches zero in desktop mode in seconds")]
        public float ThrottleReturnTimeDT = .0001f;
        // [Tooltip("how fast steering wheel returns to neutral position in VR 1 = 1 second, .2 = 5 seconds")]
        // public float ThrottleReturnTimeVR = .1f;
        public float Drag = .02f;
        [Tooltip("Transform to base the pilot's throttle and joystick controls from. Used to make vertical throttle for helicopters, or if the cockpit of your vehicle can move, on transforming vehicle")]
        public Transform ControlsRoot;
        [Tooltip("Engine power curve over revs, 0=0, 1=revlimiter")]
        public AnimationCurve EngineResponseCurve = AnimationCurve.Linear(0, 1, 1, 1);
        [System.NonSerialized] public Vector3 CurrentVel;
        // [System.NonSerializedAttribute] public bool ThrottleGripLastFrame = false;
        [UdonSynced] public float Fuel = 900;
        [Tooltip("Fuel consumption per second at max revs")]
        public float FuelConsumption = 2;
        /*     [Tooltip("Amount of fuel at which throttle will start reducing")]
            [System.NonSerializedAttribute] public float LowFuel = 125; */
        [Tooltip("Use the left hand trigger to control throttle?")]
        public bool SwitchHandsJoyThrottle = false;
        [Tooltip("Use the left hand grip to grab the steering wheel??")]
        public bool SteeringHand_Left = true;
        [Tooltip("Use the right hand grip to grab the steering wheel??")]
        public bool SteeringHand_Right = true;
        [Header("ITR:")]
        [Tooltip("Adjust the rotation of Unity's inbuilt Inertia Tensor Rotation, which is a function of rigidbodies. If set to 0, the plane will be very stable and feel boring to fly.")]
        public float InertiaTensorRotationMulti = 1;
        [Tooltip("Inverts Z axis of the Inertia Tensor Rotation, causing the direction of the yawing experienced after rolling to invert")]
        public bool InvertITRYaw = false;
        [System.NonSerializedAttribute] public bool _HandBrakeOn;
        [System.NonSerializedAttribute, FieldChangeCallback(nameof(HandBrakeOn_))] public int HandBrakeOn = 0;
        public int HandBrakeOn_
        {
            set
            {
                _HandBrakeOn = value > 0;
                HandBrakeOn = value;
            }
            get => HandBrakeOn;
        }
        private Vector3 VehiclePosLastFrame;
        [Header("AutoSteer (Drift Mode)")]
        public bool Drift_AutoSteer;
        [Tooltip("Put in the max degrees the wheels can turn to in order to make autosteer work properly")]
        public float SteeringDegrees = 60;
        public float AutoSteerStrength = 5f;
        [Header("AutoSteer Disabled")]
        [Tooltip("how fast steering wheel returns to neutral position in VR 1 = 1 second, .2 = 5 seconds")]
        public float SteeringReturnSpeedVR = 5f;
        public bool UseStickSteering;
        [Header("Other")]
        [Tooltip("Time until vehicle reappears after exploding")]
        public float RespawnDelay = 10;
        [Tooltip("Time after reappearing the vehicle is invincible for")]
        public float InvincibleAfterSpawn = 2.5f;
        [Tooltip("Instantly explode locally instead of waiting for network confirmation if your client predicts target should, possible desync if target is healing when shot")]
        public bool PredictExplosion = true;
        [Tooltip("Send event when someone gets a kill on this vehicle (SFEXT_O_GotAKill)")]
        public bool SendKillEvents;
        [Tooltip("Speed at which vehicle will start to take damage from a crash (m/s)")]
        public float Crash_Damage_Speed = 10f;
        [Tooltip("Speed at which vehicle will take damage equal to its max health from a crash (m/s)")]
        public float Crash_Death_Speed = 100f;
        [Tooltip("Damage taken when hit by a bullet")]
        public float BulletDamageTaken = 10f;
        [Tooltip("Impact speed that defines a small crash")]
        public float SmallCrashSpeed = 1f;
        [Tooltip("Impact speed that defines a medium crash")]
        public float MediumCrashSpeed = 8f;
        [Tooltip("Impact speed that defines a big crash")]
        public float BigCrashSpeed = 25f;
        [Tooltip("Time in seconds it takes to repair fully from 0")]
        public float RepairTime = 30f;
        [Tooltip("Time in seconds it takes to refuel fully from 0")]
        public float RefuelTime = 25f;
        [Tooltip("Range at which vehicle becomes 'distant' for optimization")]
        public float DistantRange = 400f;
        public float RevLimiterDelay = .04f;
        public bool RepeatingWorld = true;
        [Tooltip("Distance you can travel away from world origin before being teleported to the other side of the map. Not recommended to increase, floating point innacuracy and game freezing issues may occur if larger than default")]
        public float RepeatingWorldDistance = 20000;
        [Header("Bike Stuff (WIP/Broken)")]
        [Tooltip("Max roll angle of head for leaning on bike")]
        public float LeanSensitivity_Roll = 25f;
        [Tooltip("How far head has to move to lean forward/back, high number = less movement required")]
        public float LeanSensitivity_Pitch = 2.5f;
        public bool EnableLeaning = false;
        public bool Bike_AutoSteer;
        public float Bike_AutoSteer_CounterStrength = .01f;
        public float Bike_AutoSteer_Strength = .01f;
        [Space(10)]
        [Tooltip("Completely change how the vehicle operates to behave like a tank, enables two throttle sliders, and turns DriveWheels/SteerWheels into Left/Right tracks\nCannot be changed during play")]
        public bool TankMode;
        [Tooltip("In desktop mode, use WASD or QAED to control the tank?")]
        public bool TANK_WASDMode = true;
        [Tooltip("Use just the left control stick to control tank movement")]
        public bool TANK_StickMode;
        [Tooltip("Sensitivity of the steering for tanks when UseStickSteering is enabled")]
        public float TANK_StickMode_SteeringSens = 2;
        [Tooltip("Make tank slower by this ratio when reversing")]
        public float TANK_ReverseSpeed = 0.75f;
        [Tooltip("Multiply how much the VR throttle moves from hand movement, for DFUNCS and TankMode")]
        [SerializeField] KeyCode TANK_CruiseKey = KeyCode.F2;
        bool TANK_Cruising;
        [Tooltip("Multiply how much the VR throttle moves relative to hand movement")]
        public float ThrottleSensitivity = 6f;
        [Header("Debug")]
        [UdonSynced] public float Revs;
        public float Clutch;
        public byte CurrentGear = 0;
        private bool LimitingRev = false;
        public float debugSpeedSteeringMulti = 0f;
        public bool InVR;
        public bool Sleeping = false;

        [Header("Optimization")]
        [Tooltip("When enabled, if the vehicle is unoccupied and has been at-rest for a short time, skip per-wheel physics (raycasts/forces). This is a big CPU win when many idle vehicles exist, especially for the instance master/owner.")]
        public bool IdleAtRestOptimization = true;

        [Tooltip("Seconds the vehicle must remain under the at-rest thresholds before wheel physics is skipped.")]
        public float IdleAtRestDelay = 2.0f;

        [Tooltip("Linear speed (m/s) below which the vehicle is considered at-rest.")]
        public float IdleAtRestSpeedThreshold = 0.05f;

        [Tooltip("Angular speed (rad/s) below which the vehicle is considered at-rest.")]
        public float IdleAtRestAngularSpeedThreshold = 0.05f;

        [System.NonSerialized] private float _idleAtRestSince;
        [System.NonSerialized] private bool _idleAtRestWheelsSlept;

        // UdonSharp GC-avoid: temps used in idle-at-rest FixedUpdate gating.
        [System.NonSerialized] private Vector3 _iatroV;
        [System.NonSerialized] private Vector3 _iatroAV;
        [System.NonSerialized] private float _iatroVSqr;
        [System.NonSerialized] private float _iatroAVSqr;
        [System.NonSerialized] private float _iatroVThr;
        [System.NonSerialized] private float _iatroAVThr;
        [System.NonSerialized] private float _iatroVThrSqr;
        [System.NonSerialized] private float _iatroAVThrSqr;
        [System.NonSerialized] private float _iatroNow;
        public bool Grounded_Steering;
        public bool Grounded;
        public float GearRatio = 0f;
        private float HandDistanceZLastFrame;
        private float VRThrottlePos;
        //twist throttle values
        // private float TempThrottle;
        // private float ThrottleValue;
        // private float ThrottleValueLastFrame;
        // private Quaternion ThrottleZeroPoint;
        private bool Piloting;
        private bool Passenger;
        [System.NonSerializedAttribute] public float PlayerThrottle;
        [System.NonSerializedAttribute] public float VehicleSpeed;//set by syncscript if not owner
        [System.NonSerializedAttribute] public bool MovingForward;
        //Quaternion VehicleRotLastFrameThrottle;
        Quaternion VehicleRotLastFrameR;
        [System.NonSerializedAttribute] public bool WheelGripLastFrameR = false;
        [System.NonSerializedAttribute] public bool WheelGrippingLastFrame_toggleR = false;
        Quaternion JoystickZeroPointR;
        Vector3 CompareAngleLastFrameR;
        private float JoystickValueLastFrameR;
        private float JoyStickValueR;
        private bool WheelGrabToggleR;
        private int WheelReleaseCountR;
        private float LastGripTimeR;
        Quaternion VehicleRotLastFrameL;
        [System.NonSerializedAttribute] public bool WheelGripLastFrameL = false;
        [System.NonSerializedAttribute] public bool WheelGrippingLastFrame_toggleL = false;
        Quaternion JoystickZeroPointL;
        Vector3 CompareAngleLastFrameL;
        private float JoystickValueLastFrameL;
        private float JoyStickValueL;
        private bool WheelGrabToggleL;
        private int WheelReleaseCountL;
        private float LastGripTimeL;
        //Vector3 CompareAngleLastFrameThrottle;
        float VRJoystickPosR = 0;
        float VRJoystickPosL = 0;
        [System.NonSerializedAttribute] public float AllGs;
        [System.NonSerializedAttribute] public Vector3 LastFrameVel = Vector3.zero;
        private float FinalThrottle;
        private float AutoSteerLerper;
        [System.NonSerializedAttribute][UdonSynced] public float YawInput;
        [System.NonSerializedAttribute][UdonSynced] public float ThrottleInput;

        private const float _ManualSyncPilotingInterval = 0.25f;   // ~6-7Hz while actively driven
        private const float _ManualSyncOccupiedInterval = 0.45f;   // passengers / recently used
        private const float _ManualSyncEmptyInterval = 1.0f;      // empty vehicles
        private const float _ManualSyncSleepingInterval = 4.20f;   // sleeping / far / inactive

        private const float _SyncEpsYaw = 0.01f;
        private const float _SyncEpsThrottle = 0.01f;
        private const float _SyncEpsRevs = 80f;
        private const float _SyncEpsFuel = 2.0f;
        private const float _SyncEpsHealth = 0.1f;

        private const float _FuelBucketSize = 10f;

        [System.NonSerialized] private float _nextManualSyncTime;
        [System.NonSerialized] private bool _manualSyncDirty = true;
        [System.NonSerialized] private float _lastSyncHealth;
        [System.NonSerialized] private float _lastSyncFuel;
        [System.NonSerialized] private int _lastSyncFuelBucket;
        [System.NonSerialized] private float _lastSyncRevs;
        [System.NonSerialized] private float _lastSyncYaw;
        [System.NonSerialized] private float _lastSyncThrottle;

        // Non-owner smoothing for low-rate synced inputs.
        // These inputs are synced relatively infrequently (manual sync), so remote clients should
        // not drive visuals/audio directly from the stepwise network values.
        private const float _RemoteYawHalfLifeSeconds = 0.45f;
        private const float _RemoteThrottleHalfLifeSeconds = 0.60f;
        private const float _RemoteRevsHalfLifeSeconds = 0.55f;
        [System.NonSerialized] private bool _remoteInputSmoothingInitialized;
        [System.NonSerialized] private float _remoteYawTarget;
        [System.NonSerialized] private float _remoteThrottleTarget;
        [System.NonSerialized] private float _remoteYawSmoothed;
        [System.NonSerialized] private float _remoteThrottleSmoothed;
        [System.NonSerialized] private float _remoteRevsTarget;
        [System.NonSerialized] private float _remoteRevsSmoothed;

        private void _InitRemoteInputSmoothingFromCurrent()
        {
            _remoteYawTarget = YawInput;
            _remoteThrottleTarget = ThrottleInput;
            _remoteRevsTarget = Revs;
            _remoteYawSmoothed = _remoteYawTarget;
            _remoteThrottleSmoothed = _remoteThrottleTarget;
            _remoteRevsSmoothed = _remoteRevsTarget;
            _remoteInputSmoothingInitialized = true;
        }

        private void _UpdateRemoteInputSmoothing(float deltaTime)
        {
            if (!_remoteInputSmoothingInitialized)
            {
                _InitRemoteInputSmoothingFromCurrent();
            }
            if (deltaTime <= 0f) { return; }

            _risYawT = 1f - Mathf.Pow(0.5f, deltaTime / _RemoteYawHalfLifeSeconds);
            _risThrottleT = 1f - Mathf.Pow(0.5f, deltaTime / _RemoteThrottleHalfLifeSeconds);
            _risRevsT = 1f - Mathf.Pow(0.5f, deltaTime / _RemoteRevsHalfLifeSeconds);

            _remoteYawSmoothed = Mathf.Lerp(_remoteYawSmoothed, _remoteYawTarget, _risYawT);
            _remoteThrottleSmoothed = Mathf.Lerp(_remoteThrottleSmoothed, _remoteThrottleTarget, _risThrottleT);
            _remoteRevsSmoothed = Mathf.Lerp(_remoteRevsSmoothed, _remoteRevsTarget, _risRevsT);

            // Overwrite the public synced fields locally so other scripts reading these variables
            // (e.g. effects/animation) get the interpolated value on non-owners.
            YawInput = _remoteYawSmoothed;
            ThrottleInput = _remoteThrottleSmoothed;
            Revs = _remoteRevsSmoothed;
        }

        public override void OnDeserialization()
        {
            // Capture stepwise network updates as targets, but keep the exposed values smooth.
            if (Networking.IsOwner(gameObject)) { return; }

            _remoteYawTarget = YawInput;
            _remoteThrottleTarget = ThrottleInput;
            _remoteRevsTarget = Revs;

            if (!_remoteInputSmoothingInitialized)
            {
                _remoteYawSmoothed = _remoteYawTarget;
                _remoteThrottleSmoothed = _remoteThrottleTarget;
                _remoteRevsSmoothed = _remoteRevsTarget;
                _remoteInputSmoothingInitialized = true;
            }

            YawInput = _remoteYawSmoothed;
            ThrottleInput = _remoteThrottleSmoothed;
            Revs = _remoteRevsSmoothed;
        }

        // Per-vehicle player proximity cache (avoid GetPlayers every frame).
        // Note: wheels previously each ran their own GetPlayers() scan for skid-sync throttling.
        // Centralizing this here avoids N_wheels * N_vehicles scans.
        private const float _NearPlayerRadius = 100f;
        private const float _NearPlayerRadiusSqr = _NearPlayerRadius * _NearPlayerRadius;
        private const float _WheelNearPlayerRadius = 250f;
        private const float _WheelNearPlayerRadiusSqr = _WheelNearPlayerRadius * _WheelNearPlayerRadius;
        private const float _PlayerCacheRefreshSeconds = 0.5f;
        [System.NonSerialized] private VRCPlayerApi[] _playerCache;
        [System.NonSerialized] private float _nextPlayerCacheRefresh;
        [System.NonSerialized] private bool _cachedAnyOtherPlayerNear;

        // Public (non-synced) cache for wheels/effects to share.
        [System.NonSerialized] public bool CachedAnyOtherPlayerNearWheels;

        // UdonSharp CPU: cached temps for per-tick/proximity/sync helpers (avoid local declarations).
        private float _risYawT;
        private float _risThrottleT;
        private float _risRevsT;

        private float _pncNow;
        private int _pncCount;
        private bool _pncAnyNear;
        private bool _pncAnyNearWheels;
        private int _pncI;
        private VRCPlayerApi _pncPlayer;
        private Vector3 _pncDp;
        private float _pncDpSqr;

        private float _msInterval;
        private Vector3 _msCenter;

        private float _mrsNow;
        private float _mrsInterval;

        // UdonSharp GC-avoid: temps used inside _HasMeaningfulSyncChange()
        [System.NonSerialized] private int _hmscBucket;

        private void _UpdateAnyOtherPlayerNearCache(Vector3 center)
        {
            _pncNow = Time.time;
            if (_pncNow < _nextPlayerCacheRefresh) { return; }
            _nextPlayerCacheRefresh = _pncNow + _PlayerCacheRefreshSeconds;

            _pncCount = VRCPlayerApi.GetPlayerCount();
            if (_pncCount <= 0)
            {
                _cachedAnyOtherPlayerNear = false;
                return;
            }
            if (_playerCache == null || _playerCache.Length != _pncCount)
            {
                _playerCache = new VRCPlayerApi[_pncCount];
            }
            _playerCache = VRCPlayerApi.GetPlayers(_playerCache);

            _pncAnyNear = false;
            _pncAnyNearWheels = false;
            for (_pncI = 0; _pncI < _playerCache.Length; _pncI++)
            {
                _pncPlayer = _playerCache[_pncI];
                if (!Utilities.IsValid(_pncPlayer)) { continue; }
                // Owner tick / sync runs on the owner; ignore local player so "only owner nearby" throttles.
                if (_pncPlayer.isLocal) { continue; }
                _pncDp = _pncPlayer.GetPosition() - center;
                _pncDpSqr = _pncDp.sqrMagnitude;

                if (_pncDpSqr <= _WheelNearPlayerRadiusSqr)
                {
                    _pncAnyNearWheels = true;
                }
                if (_pncDpSqr <= _NearPlayerRadiusSqr)
                {
                    // Within the stricter radius implies within wheel radius too.
                    _pncAnyNear = true;
                    _pncAnyNearWheels = true;
                    break;
                }
            }
            _cachedAnyOtherPlayerNear = _pncAnyNear;
            CachedAnyOtherPlayerNearWheels = _pncAnyNearWheels;
        }

        private void _MarkNetworkDirty()
        {
            _manualSyncDirty = true;
        }

        private float _GetManualSyncInterval()
        {
            if (Sleeping) { _msInterval = _ManualSyncSleepingInterval; }
            else if (Piloting) { _msInterval = _ManualSyncPilotingInterval; }
            else if (Occupied || Passenger) { _msInterval = _ManualSyncOccupiedInterval; }
            else { _msInterval = _ManualSyncEmptyInterval; }

            // If no other players are near this vehicle, cap to 1Hz.
            _msCenter = VehicleTransform ? VehicleTransform.position : transform.position;
            _UpdateAnyOtherPlayerNearCache(_msCenter);
            if (!_cachedAnyOtherPlayerNear)
            {
                _msInterval = Mathf.Max(_msInterval, 1f);
            }
            return _msInterval;
        }

        private bool _HasMeaningfulSyncChange()
        {
            if (Mathf.Abs(Health - _lastSyncHealth) > _SyncEpsHealth) { return true; }

            // Fuel changes constantly during driving; remote clients generally don't need per-frame accuracy.
            // While piloting, sync Fuel only when it crosses coarse buckets (or hits empty).
            if (Piloting)
            {
                if (Fuel <= 0f && _lastSyncFuel > 0f) { return true; }
                _hmscBucket = (int)(Fuel / _FuelBucketSize);
                if (_hmscBucket != _lastSyncFuelBucket) { return true; }
            }
            else
            {
                if (Mathf.Abs(Fuel - _lastSyncFuel) > _SyncEpsFuel) { return true; }
            }

            if (Mathf.Abs(Revs - _lastSyncRevs) > _SyncEpsRevs) { return true; }
            if (Mathf.Abs(YawInput - _lastSyncYaw) > _SyncEpsYaw) { return true; }
            if (Mathf.Abs(ThrottleInput - _lastSyncThrottle) > _SyncEpsThrottle) { return true; }
            return false;
        }

        private void _CacheLastSyncedState()
        {
            _lastSyncHealth = Health;
            _lastSyncFuel = Fuel;
            _lastSyncFuelBucket = (int)(Fuel / _FuelBucketSize);
            _lastSyncRevs = Revs;
            _lastSyncYaw = YawInput;
            _lastSyncThrottle = ThrottleInput;
        }

        private void _MaybeRequestSerialization()
        {
            if (!Networking.IsOwner(gameObject)) { return; }

            _mrsNow = Time.time;
            _mrsInterval = _GetManualSyncInterval();
            if (!_manualSyncDirty && _mrsNow < _nextManualSyncTime) { return; }

            if (!_manualSyncDirty)
            {
                if (!_HasMeaningfulSyncChange())
                {
                    _nextManualSyncTime = _mrsNow + _mrsInterval;
                    return;
                }
            }
            else
            {
                if (_mrsNow < _nextManualSyncTime && !_HasMeaningfulSyncChange())
                {
                    return;
                }
            }

            RequestSerialization();
            _CacheLastSyncedState();
            _manualSyncDirty = false;
            _nextManualSyncTime = _mrsNow + _mrsInterval;
        }

        private void _ForceRequestSerialization()
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            RequestSerialization();
            _CacheLastSyncedState();
            _manualSyncDirty = false;
            _nextManualSyncTime = Time.time + _GetManualSyncInterval();
        }
        private VRCPlayerApi localPlayer;
        [System.NonSerializedAttribute] public bool InEditor = true;
        [System.NonSerializedAttribute] public bool Initialized = false;
        private Vector3 LastTouchedTransform_Speed = Vector3.zero;
        private Transform CenterOfMass;
        public float NumGroundedSteerWheels = 0;
        public float NumGroundedWheels = 0;
        public int NumWheels = 4;
        public float CurrentDistance;
        public bool CurrentlyDistant = true;
        [System.NonSerializedAttribute] public Vector3 FinalWind;//unused (for compatability)
        float angleLast;
        int HandsOnWheel;
        [System.NonSerializedAttribute] public bool _KeepAwake;
        [System.NonSerializedAttribute, FieldChangeCallback(nameof(KeepAwake_))] public int KeepAwake = 0;
        public int KeepAwake_
        {
            set
            {
                if (value > 0 && KeepAwake == 0)
                {
                    EntityControl.SendEventToExtensions("SFEXT_O_KeepAwake_Activated");
                }
                else if (value == 0 && KeepAwake > 0)
                {
                    EntityControl.SendEventToExtensions("SFEXT_O_KeepAwake_Deactivated");
                }
                _KeepAwake = value > 0;
                KeepAwake = value;
            }
            get => KeepAwake;
        }
        [System.NonSerializedAttribute] public bool _DisableJoystickControl;
        [System.NonSerializedAttribute, FieldChangeCallback(nameof(DisableJoystickControl_))] public int DisableJoystickControl = 0;
        public int DisableJoystickControl_
        {
            set
            {
                if (value > 0 && DisableJoystickControl == 0)
                {
                    EntityControl.SendEventToExtensions("SFEXT_O_DisableJoystickControl_Activated");
                    if (WheelGripLastFrameL)
                    {
                        WheelGripLastFrameL = false;
                        EntityControl.SendEventToExtensions("SFEXT_O_WheelDroppedL");
                    }
                    if (WheelGripLastFrameR)
                    {
                        WheelGripLastFrameR = false;
                        EntityControl.SendEventToExtensions("SFEXT_O_WheelDroppedR");
                    }
                }
                else if (value == 0 && DisableJoystickControl > 0)
                {
                    EntityControl.SendEventToExtensions("SFEXT_O_DisableJoystickControl_Deactivated");
                }
                _DisableJoystickControl = value > 0;
                DisableJoystickControl = value;
            }
            get => DisableJoystickControl;
        }
        [System.NonSerializedAttribute] public bool _DisableThrottleControl;
        [System.NonSerializedAttribute, FieldChangeCallback(nameof(DisableThrottleControl_))] public int DisableThrottleControl = 0;
        public int DisableThrottleControl_
        {
            set
            {
                if (value > 0 && DisableThrottleControl == 0)
                {
                    EntityControl.SendEventToExtensions("SFEXT_O_DisableThrottleControl_Activated");
                    if (ThrottleGripLastFrame[0])
                    {
                        EntityControl.SendEventToExtensions("SFEXT_O_ThrottleGrabbed_L");
                        ThrottleGripLastFrame[0] = false;
                    }
                    if (ThrottleGripLastFrame[1])
                    {
                        EntityControl.SendEventToExtensions("SFEXT_O_ThrottleGrabbed_R");
                        ThrottleGripLastFrame[1] = false;
                    }
                }
                else if (value == 0 && DisableThrottleControl > 0)
                {
                    EntityControl.SendEventToExtensions("SFEXT_O_DisableThrottleControl_Deactivated");
                }
                _DisableThrottleControl = value > 0;
                DisableThrottleControl = value;
            }
            get => DisableThrottleControl;
        }
        // public float WheelFeedBack;
        [System.NonSerializedAttribute, FieldChangeCallback(nameof(HasFuel_))] public bool HasFuel = true;
        public bool HasFuel_
        {
            set
            {
                if (value)
                {
                    EntityControl.SendEventToExtensions("SFEXT_G_HasFuel");
                }
                else
                {
                    EntityControl.SendEventToExtensions("SFEXT_G_NoFuel");
                }
                HasFuel = value;
            }
            get => HasFuel;
        }
        public void SetHasFuel() { HasFuel_ = true; }
        public void SetNoFuel() { HasFuel_ = false; }
        public void SFEXT_L_EntityStart()
        {
            if (!Initialized) { Init(); }
            CenterOfMass = EntityControl.CenterOfMass;
            SetCoMMeshOffset();
            UsingManualSync = !EntityControl.EntityObjectSync;

            NumWheels = DriveWheels.Length + SteerWheels.Length + OtherWheels.Length;

            FullHealth = Health;
            FullFuel = Fuel;

            IsOwner = EntityControl.IsOwner;
            UpdateWheelIsOwner();

            if (!IsOwner)
            {
                _InitRemoteInputSmoothingFromCurrent();
            }

            _MarkNetworkDirty();
            _nextManualSyncTime = 0f;
            InVR = EntityControl.InVR;
            localPlayer = Networking.LocalPlayer;
            if (localPlayer == null)
            {
                InEditor = true;
            }
            else { InEditor = false; }
            EntityControl.Spawnposition = VehicleTransform.localPosition;
            EntityControl.Spawnrotation = VehicleTransform.localRotation;
            if (!ControlsRoot)
            { ControlsRoot = VehicleTransform; }

            _CacheWheelsTyped();
            for (int i = 0; i < DriveWheels.Length; i++)
            {
                var w = (i < _DriveWheelsCount) ? _DriveWheelsTyped[i] : null;
                if (w != null) { w.IsDriveWheel = true; }
                else if (DriveWheels[i] != null) { DriveWheels[i].SetProgramVariable("IsDriveWheel", true); }
            }
            for (int i = 0; i < SteerWheels.Length; i++)
            {
                var w = (i < _SteerWheelsCount) ? _SteerWheelsTyped[i] : null;
                if (w != null) { w.IsSteerWheel = true; }
                else if (SteerWheels[i] != null) { SteerWheels[i].SetProgramVariable("IsSteerWheel", true); }
            }
            for (int i = 0; i < OtherWheels.Length; i++)
            {
                var w = (i < _OtherWheelsCount) ? _OtherWheelsTyped[i] : null;
                if (w != null) { w.IsOtherWheel = true; }
                else if (OtherWheels[i] != null) { OtherWheels[i].SetProgramVariable("IsOtherWheel", true); }
            }
            if (TankMode)
            {
                for (int i = 0; i < SteerWheels.Length; i++)
                {
                    var w = (i < _SteerWheelsCount) ? _SteerWheelsTyped[i] : null;
                    if (w != null) { w.IsDriveWheel = true; }
                    else if (SteerWheels[i] != null) { SteerWheels[i].SetProgramVariable("IsDriveWheel", true); }
                }
            }
            // Create AllWheels array, making sure that any wheel that is in drivewheels and steerwheels isn't there twice
            // We assume that no one is stupid enough to put a drive or steer wheel in otherwheels at the same time as it's pointless.
            int driveLen = DriveWheels.Length;
            int steerLen = SteerWheels.Length;
            int otherLen = OtherWheels.Length;

            if (_WheelIsDupBuffer == null || _WheelIsDupBuffer.Length < driveLen)
            {
                _WheelIsDupBuffer = new bool[driveLen];
            }
            for (int i = 0; i < driveLen; i++) { _WheelIsDupBuffer[i] = false; }

            int uniqueDriveWheels = driveLen;
            for (int i = 0; i < driveLen; i++)
            {
                for (int o = 0; o < steerLen; o++)
                {
                    if (DriveWheels[i] == SteerWheels[o])
                    {
                        _WheelIsDupBuffer[i] = true;
                        uniqueDriveWheels--;
                        break;
                    }
                }
            }
            AllWheels = new SaccWheel[uniqueDriveWheels + steerLen + otherLen];
            int sub = 0;
            for (int i = 0; i < driveLen; i++)
            {
                if (_WheelIsDupBuffer[i])
                {
                    sub++;
                }
                else
                {
                    AllWheels[i - sub] = DriveWheels[i];
                }
            }
            int insertIndex = uniqueDriveWheels;
            for (int i = 0; i < steerLen; i++)
            {
                AllWheels[insertIndex++] = SteerWheels[i];
            }
            for (int i = 0; i < otherLen; i++)
            {
                AllWheels[insertIndex++] = OtherWheels[i];
            }

            CurrentlyDistant = true;
            // Start the distance loop. (CheckDistance() now early-returns unless this flag is true.)
            _checkDistanceLoopActive = true;
            SendCustomEventDelayedSeconds(nameof(CheckDistance), Random.Range(5f, 7f));//dont do all vehicles on same frame
            revUpDT = 1f / NumStepsSec;

            SetupGCalcValues();
        }

        private void _CacheWheelsTyped()
        {
            _DriveWheelsCount = 0;
            _SteerWheelsCount = 0;
            _OtherWheelsCount = 0;

            if (DriveWheels != null) { _DriveWheelsCount = DriveWheels.Length; }
            if (SteerWheels != null) { _SteerWheelsCount = SteerWheels.Length; }
            if (OtherWheels != null) { _OtherWheelsCount = OtherWheels.Length; }

            if (_DriveWheelsTyped == null || _DriveWheelsTyped.Length != _DriveWheelsCount)
            { _DriveWheelsTyped = new SaccWheel[_DriveWheelsCount]; }
            if (_SteerWheelsTyped == null || _SteerWheelsTyped.Length != _SteerWheelsCount)
            { _SteerWheelsTyped = new SaccWheel[_SteerWheelsCount]; }
            if (_OtherWheelsTyped == null || _OtherWheelsTyped.Length != _OtherWheelsCount)
            { _OtherWheelsTyped = new SaccWheel[_OtherWheelsCount]; }

            for (int i = 0; i < _DriveWheelsCount; i++)
            {
                SaccWheel w = null;
                UdonSharpBehaviour b = DriveWheels[i];
                if (b != null) { w = b.GetComponent<SaccWheel>(); }
                _DriveWheelsTyped[i] = w;
            }
            for (int i = 0; i < _SteerWheelsCount; i++)
            {
                SaccWheel w = null;
                UdonSharpBehaviour b = SteerWheels[i];
                if (b != null) { w = b.GetComponent<SaccWheel>(); }
                _SteerWheelsTyped[i] = w;
            }
            for (int i = 0; i < _OtherWheelsCount; i++)
            {
                SaccWheel w = null;
                UdonSharpBehaviour b = OtherWheels[i];
                if (b != null) { w = b.GetComponent<SaccWheel>(); }
                _OtherWheelsTyped[i] = w;
            }
        }
        public void SetupGCalcValues()
        {
            NumFUinAvgTime = (int)(GsAveragingTime / Time.fixedDeltaTime);
            FrameGs = new Vector3[NumFUinAvgTime];
            Gs_all = Vector3.zero;
        }
        private void Init()
        {
            Initialized = true;
            VehicleRigidbody = EntityControl.gameObject.GetComponent<Rigidbody>();
            VehicleTransform = EntityControl.transform;
        }
        private void Start()// awake function when
        {
            if (!Initialized) { Init(); }
        }
        public void SetCoMMeshOffset()
        {
            // IMPORTANT: This must be idempotent. If the object is enabled/disabled rapidly,
            // SetCoM_ITR may not have executed yet (it's delayed), and calling this multiple
            // times would keep shifting the mesh further each time.
            if (_CoMMeshOffsetApplied)
            {
                if (!SetCoM_ITR_initialized && EntityControl.gameObject.activeInHierarchy)
                {
                    _ScheduleSetCoM_ITR();
                }
                return;
            }
            _CoMMeshOffsetApplied = true;

            //move objects to so that the vehicle's main pivot is at the CoM so that syncscript's rotation is smoother
            Vector3 CoMOffset = CenterOfMass.position - VehicleTransform.position;
            _CoMMeshOffsetLastApplied = CoMOffset;
            int c = VehicleTransform.childCount;
            for (int i = 0; i < c; i++)
            {
                VehicleTransform.GetChild(i).position -= CoMOffset;
            }
            VehicleTransform.position += CoMOffset;
            VehicleRigidbody.position = VehicleTransform.position;//Unity 2022.3.6f1 bug workaround
            EntityControl.Spawnposition = VehicleTransform.localPosition;
            EntityControl.Spawnrotation = VehicleTransform.localRotation;
            // inertia tensor wont be set properly if object is disabled
            if (SetCoM_ITR_initialized || !EntityControl.gameObject.activeInHierarchy) return;
            _ScheduleSetCoM_ITR();//this has to be delayed because ?
        }
        public void SFEXT_L_OnEnable()
        {
            // Track the first enable time so we can detect the "startup disable" pattern:
            // level root starts active -> Game.Start disables all roots -> later re-enable.
            if (!_sawFirstEnable)
            {
                _sawFirstEnable = true;
                _firstEnableTime = Time.time;
            }

            // If this vehicle was active at world load and later disabled by game state,
            // re-enabling can leave PhysX/contact caches in a bad state. Do a short reinit
            // (freeze RB for 1 frame, sync pose, then resume) to prevent "forced into ground" spazz.
            if (_needsPhysicsReinitOnNextEnable)
            {
                _BeginPhysicsReinit();
                _needsPhysicsReinitOnNextEnable = false;
            }

            if (!SetCoM_ITR_initialized)
                if (Initialized) // don't set ITR if not initialized because the call in SetCoMMeshOffset() will do it
                    SetCoMMeshOffset();

            // Level roots are sometimes toggled off/on quickly; ensure we don't carry stale drive inputs.
            Piloting = false;
            Passenger = false;
            Occupied = false;
            TANK_Cruising = false;
            System.Array.Clear(TankThrottles, 0, 2);
            ThrottleInput = 0f;
            YawInput = 0f;
            PlayerThrottle = 0f;
            Revs = 0f;
            EngineForceUsed = 0f;

            Sleeping = false;
            if (VehicleRigidbody != null)
            {
                VehicleRigidbody.useGravity = true;
                VehicleRigidbody.WakeUp();
                if (!VehicleRigidbody.isKinematic)
                {
                    VehicleRigidbody.velocity = Vector3.zero;
                    VehicleRigidbody.angularVelocity = Vector3.zero;
                }
            }

            // If we were disabled (e.g., level root toggle), wheels may have been put to sleep via FallAsleep().
            // Wake them here so suspension, skid, and other per-wheel logic resumes immediately.
            if (AllWheels != null)
            {
                for (int i = 0; i < AllWheels.Length; i++)
                {
                    if (AllWheels[i] != null) { AllWheels[i].SendCustomEvent("WakeUp"); }
                }
            }
            CurrentVel = Vector3.zero;
            LastFrameVel = Vector3.zero;
            LastTouchedTransform_Speed = Vector3.zero;

            // Restart the distance loop exactly once after a disable/enable.
            if (!_checkDistanceLoopActive)
            {
                _checkDistanceLoopActive = true;
                SendCustomEventDelayedSeconds(nameof(CheckDistance), 0.25f);
            }
        }

        public void SFEXT_L_OnDisable()
        {
            // Stop any self-rescheduling loops from multiplying after rapid enable/disable.
            _checkDistanceLoopActive = false;

            // Invalidate any pending delayed CoM/ITR application from the previous enable.
            _setCoMITR_Generation++;
            _setCoMITR_Pending = false;

            // If we were enabled at world load and get disabled very soon after, this is almost
            // certainly the Game.Start "disable all level roots" pass. In that case, undo any
            // CoM mesh offset that already ran so that the next enable applies it cleanly.
            if (_sawFirstEnable && !_startupDisableHandled && (Time.time - _firstEnableTime) < 3f)
            {
                _startupDisableHandled = true;
                _UndoCoMMeshOffsetForStartupDisable();
            }

            // Mark for a physics reinit next time we enable. This specifically targets:
            // "level was active on load -> game disables all level roots -> later re-enable".
            if (Initialized)
            {
                _needsPhysicsReinitOnNextEnable = true;
            }

            // Hard reset drive inputs so a disabled vehicle can't come back with high revs/throttle and burn out in place.
            Piloting = false;
            Passenger = false;
            Occupied = false;
            TANK_Cruising = false;
            System.Array.Clear(TankThrottles, 0, 2);
            ThrottleInput = 0f;
            YawInput = 0f;
            PlayerThrottle = 0f;
            Revs = 0f;
            EngineForceUsed = 0f;

            Sleeping = true;
            if (AllWheels != null)
            {   
                for (int i = 0; i < AllWheels.Length; i++)
                {
                    if (AllWheels[i] != null) { AllWheels[i].SendCustomEvent("FallAsleep"); }
                }
            }
        }
        [System.NonSerialized] private bool _needsPhysicsReinitOnNextEnable;
        [System.NonSerialized] private bool _physicsReinitInProgress;
        [System.NonSerialized] private bool _rbReinitWasKinematic;
        [System.NonSerialized] private bool _rbReinitHadCollisions;
        [System.NonSerialized] private int _physicsReinitDelayFrames = 3;
        [System.NonSerialized] private bool _physicsReinitWasForOwnership;
        [System.NonSerialized] private float _physicsReinitStartTime;

        private void _BeginPhysicsReinit()
        {
            if (_physicsReinitInProgress) { return; }
            if (VehicleRigidbody == null || VehicleTransform == null) { return; }
            _physicsReinitInProgress = true;
            _physicsReinitStartTime = Time.time;

            _rbReinitWasKinematic = VehicleRigidbody.isKinematic;
            _rbReinitHadCollisions = VehicleRigidbody.detectCollisions;

            if (!VehicleRigidbody.isKinematic)
            {
                VehicleRigidbody.velocity = Vector3.zero;
                VehicleRigidbody.angularVelocity = Vector3.zero;
            }
            else
            {
                VehicleRigidbody.Sleep();
            }

            // Temporarily disable simulation so the first physics step after enable can't explode.
            VehicleRigidbody.isKinematic = true;
            VehicleRigidbody.detectCollisions = false;

            // Force pose sync (Unity 2022 RB pose can desync across enable/disable).
            VehicleRigidbody.position = VehicleTransform.position;
            VehicleRigidbody.rotation = VehicleTransform.rotation;
            Physics.SyncTransforms();

            // Clear wheel contact history so we don't compute huge point velocities on first contact.
            _ResetAllWheelsAfterTeleport();

            int delayFrames = _physicsReinitDelayFrames;
            if (delayFrames < 1) { delayFrames = 1; }
            SendCustomEventDelayedFrames(nameof(_FinishPhysicsReinit), delayFrames);
        }

        public void _FinishPhysicsReinit()
        {
            if (VehicleRigidbody == null || VehicleTransform == null)
            {
                _physicsReinitInProgress = false;
                _physicsReinitWasForOwnership = false;
                return;
            }

            VehicleRigidbody.position = VehicleTransform.position;
            VehicleRigidbody.rotation = VehicleTransform.rotation;
            Physics.SyncTransforms();

            VehicleRigidbody.detectCollisions = _rbReinitHadCollisions;
            VehicleRigidbody.isKinematic = _rbReinitWasKinematic;

            // For manual-sync vehicles, the owner must be simulating physics.
            // If we ever captured/restored a kinematic state (or finish runs late), force it back.
            if (_physicsReinitWasForOwnership && UsingManualSync)
            {
                VehicleRigidbody.detectCollisions = true;
                VehicleRigidbody.isKinematic = false;
                VehicleRigidbody.useGravity = true;
            }

            VehicleRigidbody.WakeUp();

            if (_physicsReinitWasForOwnership)
            {
                // One more wheel reset after the RB is live again to avoid any single-frame stale contacts.
                _ResetAllWheelsAfterTeleport();

                if (!VehicleRigidbody.isKinematic)
                {
                    Vector3 v = VehicleRigidbody.velocity;
                    Vector3 av = VehicleRigidbody.angularVelocity;
                    if (v.sqrMagnitude < 0.01f && av.sqrMagnitude < 0.01f)
                    {
                        VehicleRigidbody.velocity = Vector3.zero;
                        VehicleRigidbody.angularVelocity = Vector3.zero;
                    }
                    CurrentVel = VehicleRigidbody.velocity;
                    LastFrameVel = CurrentVel;
                }
                VehiclePosLastFrame = VehicleTransform.position;
                _physicsReinitWasForOwnership = false;
            }
            _physicsReinitInProgress = false;
        }
        [System.NonSerialized] private bool _CoMMeshOffsetApplied;
        [System.NonSerialized] private Vector3 _CoMMeshOffsetLastApplied;
        bool SetCoM_ITR_initialized;
        [System.NonSerialized] private bool _checkDistanceLoopActive;

        [System.NonSerialized] private bool _sawFirstEnable;
        [System.NonSerialized] private float _firstEnableTime;
        [System.NonSerialized] private bool _startupDisableHandled;

        private void _UndoCoMMeshOffsetForStartupDisable()
        {
            if (!_CoMMeshOffsetApplied) { return; }
            if (VehicleTransform == null || VehicleRigidbody == null) { return; }

            Vector3 off = _CoMMeshOffsetLastApplied;
            if (off == Vector3.zero) { return; }

            int c = VehicleTransform.childCount;
            for (int i = 0; i < c; i++)
            {
                VehicleTransform.GetChild(i).position += off;
            }
            VehicleTransform.position -= off;
            VehicleRigidbody.position = VehicleTransform.position;

            // Allow re-application on the next enable (behave like scenario where root started disabled).
            _CoMMeshOffsetApplied = false;
            SetCoM_ITR_initialized = false;
            if (EntityControl != null) { EntityControl.CoMSet = false; }

            // Reset RB mass properties; SetCoM_ITR will set them properly after the next enable.
            VehicleRigidbody.ResetCenterOfMass();
            VehicleRigidbody.ResetInertiaTensor();

            // Any already-scheduled SetCoM_ITR call from before the disable is now stale.
            _setCoMITR_Generation++;
            _setCoMITR_Pending = false;
        }

        [System.NonSerialized] private int _setCoMITR_Generation;
        [System.NonSerialized] private int _setCoMITR_ScheduledGeneration;
        [System.NonSerialized] private bool _setCoMITR_Pending;

        private void _ScheduleSetCoM_ITR()
        {
            if (SetCoM_ITR_initialized) { return; }
            if (EntityControl == null || !EntityControl.gameObject.activeInHierarchy) { return; }
            if (_setCoMITR_Pending) { return; }
            _setCoMITR_Pending = true;
            _setCoMITR_ScheduledGeneration = _setCoMITR_Generation;
            SendCustomEventDelayedSeconds(nameof(_RunSetCoM_ITR_Delayed), Time.fixedDeltaTime);
        }

        public void _RunSetCoM_ITR_Delayed()
        {
            _setCoMITR_Pending = false;
            if (_setCoMITR_ScheduledGeneration != _setCoMITR_Generation) { return; }
            if (EntityControl == null || !EntityControl.gameObject.activeInHierarchy) { return; }
            if (SetCoM_ITR_initialized) { return; }
            SetCoM_ITR();
        }
        public void SetCoM_ITR()
        {
            SetCoM_ITR_initialized = true;
            VehicleRigidbody.centerOfMass = VehicleTransform.InverseTransformDirection(CenterOfMass.position - VehicleTransform.position);//correct position if scaled
            EntityControl.CoMSet = true;
            VehicleRigidbody.inertiaTensor = VehicleRigidbody.inertiaTensor;
            VehicleRigidbody.inertiaTensorRotation = Quaternion.SlerpUnclamped(Quaternion.identity, VehicleRigidbody.inertiaTensorRotation, InertiaTensorRotationMulti);
            if (InvertITRYaw)
            {
                Vector3 ITR = VehicleRigidbody.inertiaTensorRotation.eulerAngles;
                ITR.x *= -1;
                VehicleRigidbody.inertiaTensorRotation = Quaternion.Euler(ITR);
            }
        }
        public void ReEnableRevs()
        {
            if (Revs < RevLimiter)
            {
                LimitingRev = false;
            }
            else
            {
                SendCustomEventDelayedSeconds(nameof(ReEnableRevs), RevLimiterDelay);
            }
        }
#if UNITY_EDITOR
        public bool ACCELTEST;
#endif
        private bool[] ThrottleGripLastFrame = new bool[2];
        float[] ThrottleZeroPoint = new float[2];
        float[] TankThrottles = new float[2];
        float[] TankTempThrottles = new float[2];
        private float ThrottleSlider(float Min, float Max, bool LeftHand, float DeadZone)
        {
            int SliderIndex;
            float ThrottleGrip;
            if (LeftHand)
            {
                SliderIndex = 0;
                ThrottleGrip = Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryHandTrigger");
            }
            else
            {
                SliderIndex = 1;
                ThrottleGrip = Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryHandTrigger");
            }
            //VR Throttle
            if (ThrottleGrip > GripSensitivity)
            {
                VRCPlayerApi lp = localPlayer;
                if (!Utilities.IsValid(lp) || lp.isSuspended)
                {
                    lp = Networking.LocalPlayer;
                    if (!Utilities.IsValid(lp) || lp.isSuspended)
                    {
                        // Cannot read tracking data right now (common during join/suspend). Keep last value.
                        return TankThrottles[SliderIndex];
                    }
                    localPlayer = lp;
                }

                Vector3 handdistance;
                if (LeftHand)
                { handdistance = ControlsRoot.position - lp.GetTrackingData(VRCPlayerApi.TrackingDataType.LeftHand).position; }
                else
                { handdistance = ControlsRoot.position - lp.GetTrackingData(VRCPlayerApi.TrackingDataType.RightHand).position; }
                handdistance = ControlsRoot.InverseTransformDirection(handdistance);

                float HandThrottleAxis = handdistance.z;

                if (!ThrottleGripLastFrame[SliderIndex])
                {
                    if (LeftHand)
                    {
                        lp.PlayHapticEventInHand(VRC_Pickup.PickupHand.Left, .05f, .222f, 35);
                        EntityControl.SendEventToExtensions("SFEXT_O_ThrottleGrabbed_L");
                    }
                    else
                    {
                        lp.PlayHapticEventInHand(VRC_Pickup.PickupHand.Right, .05f, .222f, 35);
                        EntityControl.SendEventToExtensions("SFEXT_O_ThrottleGrabbed_R");
                    }
                    ThrottleZeroPoint[SliderIndex] = HandThrottleAxis;
                    TankTempThrottles[SliderIndex] = TankThrottles[SliderIndex];
                    HandDistanceZLastFrame = 0;
                }
                float ThrottleDifference = ThrottleZeroPoint[SliderIndex] - HandThrottleAxis;
                ThrottleDifference *= ThrottleSensitivity;

                TankThrottles[SliderIndex] = Mathf.Clamp(TankTempThrottles[SliderIndex] + ThrottleDifference, Min, Max);

                HandDistanceZLastFrame = HandThrottleAxis;
                ThrottleGripLastFrame[SliderIndex] = true;
            }
            else
            {
                if (ThrottleGripLastFrame[SliderIndex])
                {
                    if (Mathf.Abs(TankThrottles[SliderIndex]) < DeadZone)
                    {
                        TankThrottles[SliderIndex] = 0;
                    }
                    if (LeftHand)
                    {
                        if (Utilities.IsValid(localPlayer) && !localPlayer.isSuspended)
                        { localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Left, .05f, .222f, 35); }
                        EntityControl.SendEventToExtensions("SFEXT_O_ThrottleDropped_L");
                    }
                    else
                    {
                        if (Utilities.IsValid(localPlayer) && !localPlayer.isSuspended)
                        { localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Right, .05f, .222f, 35); }
                        EntityControl.SendEventToExtensions("SFEXT_O_ThrottleDropped_R");
                    }
                    ThrottleGripLastFrame[SliderIndex] = false;
                }
            }
            float result = TankThrottles[SliderIndex];
            return result;
        }
        private void LateUpdate()
        {
            _luTickNow = Time.time;

            // Safety: if a delayed-frame finish never runs (rare timing/enable edge cases),
            // the rigidbody can remain kinematic/collisionless and appear "frozen".
            if (_physicsReinitInProgress && (_luTickNow - _physicsReinitStartTime) > 1.0f)
            {
                _FinishPhysicsReinit();
            }

            if (IsOwner)
            {
                if (!_lateUpdateWasOwner)
                {
                    _nextOwnerLateUpdateTime = 0f;
                    _lastOwnerLateUpdateTime = 0f;
                }
                if (_luTickNow < _nextOwnerLateUpdateTime) { return; }
                _luDeltaTime = (_lastOwnerLateUpdateTime > 0f) ? (_luTickNow - _lastOwnerLateUpdateTime) : Time.deltaTime;
                _lastOwnerLateUpdateTime = _luTickNow;
                _nextOwnerLateUpdateTime = _luTickNow + _OwnerLateUpdateInterval;
            }
            else
            {
                if (_lateUpdateWasOwner)
                {
                    _nextNonOwnerLateUpdateTime = 0f;
                    _lastNonOwnerLateUpdateTime = 0f;
                }
                if (_luTickNow < _nextNonOwnerLateUpdateTime) { return; }
                _luDeltaTime = (_lastNonOwnerLateUpdateTime > 0f) ? (_luTickNow - _lastNonOwnerLateUpdateTime) : Time.deltaTime;
                _lastNonOwnerLateUpdateTime = _luTickNow;
                _nextNonOwnerLateUpdateTime = _luTickNow + _NonOwnerLateUpdateInterval;
            }
            _lateUpdateWasOwner = IsOwner;

            if (!IsOwner)
            {
                _UpdateRemoteInputSmoothing(_luDeltaTime);
            }
            if (IsOwner)
            {
                if (!EntityControl._dead)
                {
                    if (Health <= 0f)//vehicle is ded
                    {
                        NetworkExplode();
                        return;
                    }
                }
                if (!Sleeping)
                {
                    DoRepeatingWorld();
                    VehicleSpeed = CurrentVel.magnitude;
                    NumGroundedWheels = 0;
                    NumGroundedSteerWheels = 0;

                    _luSteerLen = SteerWheels.Length;
                    _luDriveLen = DriveWheels.Length;
                    _luOtherLen = OtherWheels.Length;

                    for (_luI = 0; _luI < _luSteerLen; _luI++)
                    {
                        _luWheel = (_luI < _SteerWheelsCount) ? _SteerWheelsTyped[_luI] : null;
                        if (_luWheel != null)
                        {
                            if (_luWheel.Grounded) { NumGroundedSteerWheels++; }
                        }
                        else if (SteerWheels[_luI] != null)
                        {
                            if ((bool)SteerWheels[_luI].GetProgramVariable("Grounded")) { NumGroundedSteerWheels++; }
                        }
                    }

                    NumGroundedWheels = NumGroundedSteerWheels;
                    for (_luI = 0; _luI < _luDriveLen; _luI++)
                    {
                        _luWheel = (_luI < _DriveWheelsCount) ? _DriveWheelsTyped[_luI] : null;
                        if (_luWheel != null)
                        {
                            if (_luWheel.Grounded) { NumGroundedWheels++; }
                        }
                        else if (DriveWheels[_luI] != null)
                        {
                            if ((bool)DriveWheels[_luI].GetProgramVariable("Grounded")) { NumGroundedWheels++; }
                        }
                    }
                    for (_luI = 0; _luI < _luOtherLen; _luI++)
                    {
                        _luWheel = (_luI < _OtherWheelsCount) ? _OtherWheelsTyped[_luI] : null;
                        if (_luWheel != null)
                        {
                            if (_luWheel.Grounded) { NumGroundedWheels++; }
                        }
                        else if (OtherWheels[_luI] != null)
                        {
                            if ((bool)OtherWheels[_luI].GetProgramVariable("Grounded")) { NumGroundedWheels++; }
                        }
                    }
                    //send grounded events
                    if (NumGroundedSteerWheels > 0)
                    {
                        if (!Grounded_Steering)
                        {
                            Grounded_Steering = true;
                            EntityControl.SendEventToExtensions("SFEXT_O_SteeringGrounded");
                        }
                    }
                    else
                    {
                        if (Grounded_Steering)
                        {
                            Grounded_Steering = false;
                            EntityControl.SendEventToExtensions("SFEXT_O_SteeringAirborne");
                        }
                    }
                    if (NumGroundedWheels > 0)
                    {
                        if (!Grounded)
                        {
                            Grounded = true;
                            EntityControl.SendEventToExtensions("SFEXT_O_Grounded");
                        }
                    }
                    else
                    {
                        if (Grounded)
                        {
                            Grounded = false;
                            EntityControl.SendEventToExtensions("SFEXT_O_Airborne");
                        }
                    }
                }
                if (Piloting)
                {
                    if (TankMode)
                    {
                        if (!_DisableThrottleControl)
                        {
                            if (Input.GetKeyDown(TANK_CruiseKey))
                            {
                                TANK_Cruising = !TANK_Cruising;
                            }
                            float LeftThrottle;
                            float RightThrottle;
                            float VRThrottleL = 0;
                            float VRThrottleR = 0;
                            if (InVR)
                            {
                                VRThrottleL = ThrottleSlider(-1, 1, true, 0.2f);
                                VRThrottleR = ThrottleSlider(-1, 1, false, 0.2f);
                            }
                            int LeftTrackF = 0;
                            int LeftTrackB = 0;
                            int RightTrackF = 0;
                            int RightTrackB = 0;
                            if (TANK_WASDMode)
                            {
                                int Wi = Input.GetKey(KeyCode.W) ? 1 : 0;
                                int Ai = Input.GetKey(KeyCode.A) ? 1 : 0;
                                int Si = Input.GetKey(KeyCode.S) ? 1 : 0;
                                int Di = Input.GetKey(KeyCode.D) ? 1 : 0;
                                if (Vector3.Dot(CurrentVel, EntityControl.transform.forward) < -.5f)
                                {
                                    // invert steering when going backwards
                                    Ai *= -1;
                                    Di *= -1;
                                }
                                LeftTrackF = Wi - Ai + Di - Si;
                                LeftTrackB = -Si - Ai + Di + Wi;
                                RightTrackF = Wi - Di + Ai - Si;
                                RightTrackB = -Si - Di + Ai + Wi;
                            }
                            else
                            {
                                LeftTrackF = Input.GetKey(KeyCode.Q) ? 1 : 0;
                                LeftTrackB = Input.GetKey(KeyCode.A) ? -1 : 0;
                                RightTrackF = Input.GetKey(KeyCode.E) ? 1 : 0;
                                RightTrackB = Input.GetKey(KeyCode.D) ? -1 : 0;
                            }
                            if (TANK_StickMode)
                            {
                                _luStickPos.x = Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryThumbstickHorizontal");
                                _luStickPos.y = Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryThumbstickVertical");
                                _luStickAngle = Vector2.SignedAngle(Vector2.up, _luStickPos);
                                _luForwardAmount = _luStickPos.y;
                                if (_luStickAngle < 95 && _luStickAngle > -95)
                                {
                                    //going forward
                                    _luStickAngle = (_luStickAngle / 90) * _luStickPos.magnitude * TANK_StickMode_SteeringSens;
                                    _luForwardAmount = Mathf.Max(_luForwardAmount, 0);
                                }
                                else
                                {
                                    //going backward
                                    _luStickAngle = ((180 * Mathf.Sign(_luStickAngle)) - _luStickAngle);// flip 180 for backwards
                                    _luStickAngle = (-_luStickAngle / 85) * _luStickPos.magnitude * TANK_StickMode_SteeringSens;
                                    _luForwardAmount = Mathf.Min(_luForwardAmount, 0);
                                }
                                LeftThrottle = Mathf.Clamp(LeftTrackF + LeftTrackB + -_luStickAngle + _luForwardAmount, -1, 1);
                                RightThrottle = Mathf.Clamp(RightTrackF + RightTrackB + _luStickAngle + _luForwardAmount, -1, 1);
                            }
                            else
                            {
                                LeftThrottle = Mathf.Clamp(LeftTrackF + LeftTrackB + VRThrottleL, -1, 1);
                                RightThrottle = Mathf.Clamp(RightTrackF + RightTrackB + VRThrottleR, -1, 1);
                            }
                            if (TANK_Cruising)
                            {
                                if (RightThrottle != 0 || LeftThrottle != 0)
                                {
                                    if (Mathf.Abs(RightThrottle + LeftThrottle) == 2)
                                    { TANK_Cruising = false; }
                                }
                                else LeftThrottle = RightThrottle = 1;
                            }

                            //For animations
                            ThrottleInput = LeftThrottle * .5f + .5f;
                            YawInput = RightThrottle;
                            //
                            FinalThrottle = Mathf.Max(Mathf.Abs(LeftThrottle) + Mathf.Abs(RightThrottle));

                            // bool LeftNeg = LeftThrottle < 0;
                            // bool RightNeg = RightThrottle < 0;
                            // float RGearRatio = RightNeg ? -GearRatio : GearRatio;
                            // float LGearRatio = LeftNeg ? -GearRatio : GearRatio;
                            _luReverseSpeedL = LeftThrottle < 0 ? TANK_ReverseSpeed : 1;
                            _luReverseSpeedR = RightThrottle < 0 ? TANK_ReverseSpeed : 1;
                            _luLGearRatio = Mathf.LerpUnclamped(0, GearRatio, LeftThrottle * _luReverseSpeedL);
                            _luRGearRatio = Mathf.LerpUnclamped(0, GearRatio, RightThrottle * _luReverseSpeedR);

                            // float LClutch = Clutch;
                            // float RClutch = Clutch;
                            // if (LeftThrottle == 0) { LClutch = 1; }
                            // if (RightThrottle == 0) { RClutch = 1; }
                            for (_luI = 0; _luI < DriveWheels.Length; _luI++)
                            {
                                _luWheel = (_luI < _DriveWheelsCount) ? _DriveWheelsTyped[_luI] : null;
                                if (_luWheel != null)
                                {
                                    _luWheel.Clutch = Clutch;
                                    _luWheel.GearRatio = _luLGearRatio;
                                }
                                else if (DriveWheels[_luI] != null)
                                {
                                    DriveWheels[_luI].SetProgramVariable("Clutch", Clutch);
                                    DriveWheels[_luI].SetProgramVariable("_GearRatio", _luLGearRatio);
                                }
                            }
                            for (_luI = 0; _luI < SteerWheels.Length; _luI++)
                            {
                                _luWheel = (_luI < _SteerWheelsCount) ? _SteerWheelsTyped[_luI] : null;
                                if (_luWheel != null)
                                {
                                    _luWheel.Clutch = Clutch;
                                    _luWheel.GearRatio = _luRGearRatio;
                                }
                                else if (SteerWheels[_luI] != null)
                                {
                                    SteerWheels[_luI].SetProgramVariable("Clutch", Clutch);
                                    SteerWheels[_luI].SetProgramVariable("_GearRatio", _luRGearRatio);
                                }
                            }
                        }
                    }
                    else
                    {
                        _luWi = 0;
                        _luAi = 0;
                        _luDi = 0;
                        _luLGrip = 0;
                        _luRGrip = 0;
                        //inputs as ints

#if UNITY_EDITOR
                        _luWi = Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) || ACCELTEST ? 1 : 0;
#else
                            _luWi = Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1 : 0;
#endif
                        //int Si = Input.GetKey(KeyCode.S) ? -1 : 0;
                        _luAi = Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? -1 : 0;
                        _luDi = Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1 : 0;
                        if (!InEditor)
                        {
                            _luLGrip = Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryHandTrigger");
                            _luRGrip = Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryHandTrigger");
                        }
                        //float ThrottleGrip;
                        // if (SwitchHandsJoyThrottle)
                        // { ThrottleGrip = RGrip; }
                        // else
                        // // { ThrottleGrip = LGrip; }
                        // if (EnableLeaning)
                        // {
                        //     _luThreei = Input.GetKey(KeyCode.Alpha3) ? -1 : 0;
                        //     _luRi = Input.GetKey(KeyCode.R) ? 1 : 0;
                        //     _luVRLean = 0;
                        //     _luVRLeanPitch = 0;
                        //     _luLp = localPlayer;
                        //     if (!Utilities.IsValid(_luLp) || _luLp.isSuspended)
                        //     {
                        //         _luLp = Networking.LocalPlayer;
                        //         if (Utilities.IsValid(_luLp) && !_luLp.isSuspended) { localPlayer = _luLp; }
                        //         else { _luLp = null; }
                        //     }
                        //     if (_luLp != null)
                        //     {
                        //         if (InVR)
                        //         {
                        //             _luHeadLean = _luLp.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).rotation * Vector3.up;
                        //             _luHeadLeanRoll = Vector3.ProjectOnPlane(_luHeadLean, ControlsRoot.forward);
                        //             _luVRLean = Vector3.SignedAngle(_luHeadLeanRoll, ControlsRoot.up, ControlsRoot.forward);
                        //             _luVRLean = Mathf.Clamp(_luVRLean / LeanSensitivity_Roll, -1, 1);

                        //             _luHeadOffset = ControlsRoot.position - _luLp.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
                        //             _luHeadOffset = ControlsRoot.InverseTransformDirection(_luHeadOffset);
                        //             _luVRLeanPitch = Mathf.Clamp(_luHeadOffset.z * LeanSensitivity_Pitch, -1, 1);
                        //         }
                        //         else
                        //         {
                        //             _luHeadLean = _luLp.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).rotation * Vector3.forward;
                        //             _luHeadLeanRoll = Vector3.ProjectOnPlane(_luHeadLean, ControlsRoot.up);
                        //             _luVRLean = Vector3.SignedAngle(_luHeadLeanRoll, ControlsRoot.forward, ControlsRoot.up);
                        //             _luVRLean = -Mathf.Clamp(_luVRLean / 25f, -1, 1);
                        //         }
                        //     }

                        //     VehicleAnimator.SetFloat("lean", (_luVRLean * .5f) + .5f);
                        //     VehicleAnimator.SetFloat("leanpitch", (_luVRLeanPitch * .5f) + .5f);
                        //     VehicleRigidbody.centerOfMass = transform.InverseTransformDirection(CenterOfMass.position - transform.position);//correct position if scaled}
                        // }

                        ///VR Twist Throttle
                        /*                 if (ThrottleGrip > GripSensitivity)
                                        {
                                            Quaternion VehicleRotDif = ControlsRoot.rotation * Quaternion.Inverse(VehicleRotLastFrameThrottle);//difference in vehicle's rotation since last frame
                                            VehicleRotLastFrameThrottle = ControlsRoot.rotation;
                                            ThrottleZeroPoint = VehicleRotDif * ThrottleZeroPoint;//zero point rotates with the vehicle so it appears still to the pilot
                                            if (!ThrottleGripLastFrame)//first frame you gripped Throttle
                                            {
                                                EntityControl.SendEventToExtensions("SFEXT_O_ThrottleGrabbed");
                                                VehicleRotDif = Quaternion.identity;
                                                if (SwitchHandsJoyThrottle)
                                                { ThrottleZeroPoint = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.RightHand).rotation; }//rotation of the controller relative to the vehicle when it was pressed
                                                else
                                                { ThrottleZeroPoint = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.LeftHand).rotation; }
                                                ThrottleValue = -ThrottleInput * ThrottleDegrees;
                                                ThrottleValueLastFrame = 0;
                                                CompareAngleLastFrameThrottle = Vector3.up;
                                                ThrottleValueLastFrame = 0;
                                            }
                                            ThrottleGripLastFrame = true;
                                            //difference between the vehicle and the hand's rotation, and then the difference between that and the ThrottleZeroPoint
                                            Quaternion ThrottleDifference;
                                            ThrottleDifference = Quaternion.Inverse(ControlsRoot.rotation) *
                                                (SwitchHandsJoyThrottle ? localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.RightHand).rotation
                                                                        : localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.LeftHand).rotation)
                                            * Quaternion.Inverse(ThrottleZeroPoint)
                                             * ControlsRoot.rotation;

                                            Vector3 ThrottlePosPitch = (ThrottleDifference * Vector3.up);
                                            Vector3 CompareAngle = Vector3.ProjectOnPlane(ThrottlePosPitch, Vector3.right);
                                            ThrottleValue += (Vector3.SignedAngle(CompareAngleLastFrameThrottle, CompareAngle, Vector3.right));
                                            CompareAngleLastFrameThrottle = CompareAngle;
                                            ThrottleValueLastFrame = ThrottleValue;
                                            VRThrottlePos = Mathf.Max(-ThrottleValue / ThrottleDegrees, 0f);
                                        }
                                        else
                                        {
                                            VRThrottlePos = 0f;
                                            if (ThrottleGripLastFrame)//first frame you let go of Throttle
                                            { EntityControl.SendEventToExtensions("SFEXT_O_ThrottleDropped"); }
                                            ThrottleGripLastFrame = false;
                                        } */
                        if (SwitchHandsJoyThrottle)
                        {
                            VRThrottlePos = Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryIndexTrigger");
                        }
                        else
                        {
                            VRThrottlePos = Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryIndexTrigger");
                        }

                        HandsOnWheel = 0;
                        _luSteerInput = 0;
                        _luVRSteerInput = 0;
                        if (!_DisableJoystickControl)
                        {
                            if (SteeringHand_Right)
                            { RHandSteeringWheel(_luRGrip, false); }
                            if (SteeringHand_Left)
                            { LHandSteeringWheel(_luLGrip); }
                            if (InVR)
                            {
                                if (HandsOnWheel > 0)
                                {
                                    _luVRSteerInput = (VRJoystickPosL + VRJoystickPosR) / (float)HandsOnWheel;
                                }
                                else
                                {
                                    AutoSteerLerper = YawInput;
                                }
                            }
                            if (UseStickSteering)
                            {
                                _luSteerInput = _luAi + _luDi + Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryThumbstickHorizontal");
                            }
                            else
                            {
                                _luSteerInput = -_luVRSteerInput + _luAi + _luDi;
                            }
                        }
                        //get the average transform movement that the steering wheels are touching
                        LastTouchedTransform_Speed = Vector3.zero;
                        _luSteerLen2 = SteerWheels.Length;
                        _luDriveLen2 = DriveWheels.Length;
                        _luDenom = _luSteerLen2 + _luDriveLen2;
                        if (_luDenom > 0)
                        {
                            for (_luI = 0; _luI < _luSteerLen2; _luI++)
                            {
                                _luWheel = (_luI < _SteerWheelsCount) ? _SteerWheelsTyped[_luI] : null;
                                if (_luWheel != null) { LastTouchedTransform_Speed += _luWheel.LastTouchedTransform_Speed; }
                                else if (SteerWheels[_luI] != null) { LastTouchedTransform_Speed += (Vector3)SteerWheels[_luI].GetProgramVariable("LastTouchedTransform_Speed"); }
                            }
                            for (_luI = 0; _luI < _luDriveLen2; _luI++)
                            {
                                _luWheel = (_luI < _DriveWheelsCount) ? _DriveWheelsTyped[_luI] : null;
                                if (_luWheel != null) { LastTouchedTransform_Speed += _luWheel.LastTouchedTransform_Speed; }
                                else if (DriveWheels[_luI] != null) { LastTouchedTransform_Speed += (Vector3)DriveWheels[_luI].GetProgramVariable("LastTouchedTransform_Speed"); }
                            }
                            LastTouchedTransform_Speed = LastTouchedTransform_Speed / _luDenom;
                        }
                        _luAutoSteer = Vector3.SignedAngle(VehicleTransform.forward, Vector3.ProjectOnPlane(CurrentVel - LastTouchedTransform_Speed, VehicleTransform.up), VehicleTransform.up);
                        if (Mathf.Abs(_luAutoSteer) > 110)
                        { _luAutoSteer = 0; }

                        { _luAutoSteer = Mathf.Clamp(_luAutoSteer / SteeringDegrees, -1, 1); }

                        _luGroundedWheelsRatio = (SteerWheels.Length > 0) ? (NumGroundedSteerWheels / SteerWheels.Length) : 0f;
                        if (!_DisableJoystickControl)
                        {
                            if (InVR && !UseStickSteering)
                            {
                                AutoSteerLerper = Mathf.Lerp(AutoSteerLerper, _luAutoSteer, 1 - Mathf.Pow(0.5f, VehicleSpeed * AutoSteerStrength * _luGroundedWheelsRatio * _luDeltaTime));
                                _luYawAddAmount = _luSteerInput;
                                if (Mathf.Abs(_luYawAddAmount) > 0f)
                                {
                                    if (Drift_AutoSteer)
                                    {
                                        YawInput = Mathf.Clamp(AutoSteerLerper + _luYawAddAmount, -1f, 1f);
                                    }
                                    else
                                    {
                                        YawInput = _luYawAddAmount;
                                    }
                                }
                                else
                                {
                                    if (Drift_AutoSteer)
                                    {
                                        YawInput = Mathf.Lerp(YawInput, _luAutoSteer, 1 - Mathf.Pow(0.5f, VehicleSpeed * AutoSteerStrength * _luGroundedWheelsRatio * _luDeltaTime));
                                    }
                                    else
                                    {
                                        YawInput = Mathf.MoveTowards(YawInput, 0f, (1f / SteeringReturnSpeedVR) * _luDeltaTime);
                                    }
                                }
                            }
                            else if (UseStickSteering)
                            {
                                if (SteeringMaxSpeedDTDisabled || _HandBrakeOn)//no steering limit when handbarke on
                                {
                                    YawInput = Mathf.Clamp(_luSteerInput, -1, 1);
                                }
                                else
                                {
                                    _luSpeedSteeringLimitUpper = 1 - (VehicleSpeed / SteeringMaxSpeedDT);
                                    _luSpeedSteeringLimitUpper = Mathf.Clamp(_luSpeedSteeringLimitUpper, DesktopMinSteering, 1);
                                    _luSpeedSteeringLimitLower = -_luSpeedSteeringLimitUpper;

                                    if (_luAutoSteer < 0)
                                    {
                                        _luSpeedSteeringLimitLower = Mathf.Min(_luSpeedSteeringLimitLower, _luAutoSteer - DesktopMinSteering);
                                        YawInput = _luSteerInput * -_luSpeedSteeringLimitLower;
                                    }
                                    else
                                    {
                                        _luSpeedSteeringLimitUpper = Mathf.Max(_luSpeedSteeringLimitUpper, _luAutoSteer + DesktopMinSteering);
                                        YawInput = _luSteerInput * _luSpeedSteeringLimitUpper;
                                    }
                                }
                            }
                            else
                            {
                                _luYawAddAmount = _luSteerInput * _luDeltaTime * (1f / SteeringKeyboardSecsToMax);
                                if (_luYawAddAmount != 0f)
                                {
                                    if (SteeringMaxSpeedDTDisabled || _HandBrakeOn)//no steering limit when handbarke on
                                    {
                                        YawInput = Mathf.Clamp(YawInput + _luYawAddAmount, -1, 1);
                                    }
                                    else
                                    {
                                        _luSpeedSteeringLimitUpper = 1 - (VehicleSpeed / SteeringMaxSpeedDT);
                                        _luSpeedSteeringLimitUpper = Mathf.Clamp(_luSpeedSteeringLimitUpper, DesktopMinSteering, 1);
                                        _luSpeedSteeringLimitLower = -_luSpeedSteeringLimitUpper;

                                        if (_luAutoSteer < 0)
                                        {
                                            _luSpeedSteeringLimitLower = Mathf.Min(_luSpeedSteeringLimitLower, _luAutoSteer - DesktopMinSteering);
                                        }
                                        else
                                        {
                                            _luSpeedSteeringLimitUpper = Mathf.Max(_luSpeedSteeringLimitUpper, _luAutoSteer + DesktopMinSteering);
                                        }
                                        YawInput = Mathf.Clamp(YawInput + _luYawAddAmount, _luSpeedSteeringLimitLower, _luSpeedSteeringLimitUpper);
                                    }
                                    if ((_luSteerInput > 0 && YawInput < 0) || _luSteerInput < 0 && YawInput > 0)
                                    {
                                        YawInput = Mathf.MoveTowards(YawInput, 0f, (1f / SteeringReturnSpeedDT) * _luDeltaTime);
                                    }
                                }
                                else
                                {
                                    if (Drift_AutoSteer)
                                    { YawInput = Mathf.Lerp(YawInput, _luAutoSteer, 1 - Mathf.Pow(0.5f, VehicleSpeed * AutoSteerStrength * _luDeltaTime * _luGroundedWheelsRatio)); }
                                    else if (Bike_AutoSteer)
                                    {
                                        _luAngle = Vector3.SignedAngle(VehicleTransform.up, Vector3.up, VehicleTransform.forward);
                                        if (_luAngle != angleLast)
                                        {
                                            // if ((angle > 0 && YawInput < 0) || (angle < 0 && YawInput > 0))
                                            // {
                                            //     YawInput = 0;
                                            // }
                                            YawInput += _luAngle * Bike_AutoSteer_Strength * _luDeltaTime;
                                            YawInput *= (_luAngle - angleLast) * Bike_AutoSteer_CounterStrength * _luDeltaTime;
                                            angleLast = _luAngle;
                                        }
                                    }
                                    else
                                    { YawInput = Mathf.MoveTowards(YawInput, 0f, (1f / SteeringReturnSpeedDT) * _luDeltaTime); }
                                }
                            }
                            YawInput = Mathf.Clamp(YawInput, -1f, 1f);
                        }

                        if (!_DisableThrottleControl)
                        {
                            if (InVR)
                            {
                                ThrottleInput = Mathf.Min(VRThrottlePos + _luWi, 1f);
                                /*                                        else
                                                   {

                                                       float ReturnSpeedGrip = 1 - Mathf.Min(ThrottleGrip / GripSensitivity, 1f);
                                                       ThrottleInput = Mathf.MoveTowards(ThrottleInput, 0f, ReturnSpeedGrip * (1f / ThrottleReturnTimeVR) * DeltaTime);
                                                   } */
                            }
                            else
                            {
                                if (_luWi != 0)
                                {
                                    ThrottleInput = Mathf.Clamp(Mathf.MoveTowards(ThrottleInput, VRThrottlePos + (_luWi), (1 / ThrottleToMaxTimeDT) * _luDeltaTime), -DriveSpeedKeyboardMax, DriveSpeedKeyboardMax);
                                }
                                else
                                {
                                    ThrottleInput = Mathf.MoveTowards(ThrottleInput, 0f, (1 / ThrottleReturnTimeDT) * _luDeltaTime);
                                }
                            }
                            for (_luI = 0; _luI < DriveWheels.Length; _luI++)
                            {
                                _luWheel = (_luI < _DriveWheelsCount) ? _DriveWheelsTyped[_luI] : null;
                                if (_luWheel != null) { _luWheel.Clutch = Clutch; }
                                else if (DriveWheels[_luI] != null) { DriveWheels[_luI].SetProgramVariable("Clutch", Clutch); }
                            }
                            FinalThrottle = ThrottleInput;
                        }
                    }
                    if (Fuel > 0)
                    {
                        if (!HasFuel_)
                        {
                            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(SetHasFuel));
                        }
                        if (FinalThrottle < MinThrottle && Revs / RevLimiter < MinThrottle)
                        {
                            FinalThrottle = (MinThrottle - FinalThrottle) * MinThrottle_PStrength;
                            //P Controller for throttle
                        }
                        Fuel = Mathf.Max(Fuel - (FuelConsumption * _luDeltaTime * (Revs / RevLimiter)), 0);
                    }
                    else
                    {
                        if (HasFuel_)
                        {
                            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(SetNoFuel));
                        }
                        FinalThrottle = 0;
                    }
                }
                CurrentVel = VehicleRigidbody.velocity;//CurrentVel is set by SAV_SyncScript for non owners
            }
            else //TODO: Move this to an effects script / Have a timer to not do it while empty for more than 10s
            {
                VehicleSpeed = CurrentVel.magnitude;
                VehiclePosLastFrame = VehicleTransform.position;
                MovingForward = Vector3.Dot(VehicleTransform.forward, CurrentVel) < 0f;
            }

            if (IsOwner)
            {
                _MaybeRequestSerialization();
            }
        }
        public void UpdateGearRatio()
        {
            for (_ugI = 0; _ugI < DriveWheels.Length; _ugI++)
            {
                _ugWheel = (_ugI < _DriveWheelsCount) ? _DriveWheelsTyped[_ugI] : null;
                if (_ugWheel != null) { _ugWheel.GearRatio = GearRatio; }
                else if (DriveWheels[_ugI] != null) { DriveWheels[_ugI].SetProgramVariable("_GearRatio", GearRatio); }
            }
        }
        float Steps_Error;
        [System.NonSerialized] public float GsAveragingTime = .1f;
        [System.NonSerialized] public int NumFUinAvgTime = 1;
        [System.NonSerialized] public Vector3 Gs_all;
        private Vector3[] FrameGs;
        private int GsFrameCheck;
        private void FixedUpdate()
        {
            if (!IsOwner) { return; }
            if (_physicsReinitInProgress) { return; }

            // Defensive: ensure we never execute more than once per physics step.
            // If something causes duplicated callbacks, double-applying wheel forces will explode.
            if (Mathf.Abs(Time.fixedTime - _lastVehicleFixedTime) < 0.00001f) { return; }
            _lastVehicleFixedTime = Time.fixedTime;

            // Big perf win: if the vehicle is unoccupied and has settled, stop doing per-wheel raycasts/forces.
            // This allows PhysX to sleep naturally instead of being kept awake by tiny corrective forces.
            if (IdleAtRestOptimization && !Piloting && !Occupied && !Passenger && VehicleRigidbody != null && !VehicleRigidbody.isKinematic)
            {
                _iatroV = VehicleRigidbody.velocity;
                _iatroAV = VehicleRigidbody.angularVelocity;
                _iatroVSqr = _iatroV.sqrMagnitude;
                _iatroAVSqr = _iatroAV.sqrMagnitude;
                _iatroVThr = IdleAtRestSpeedThreshold;
                _iatroAVThr = IdleAtRestAngularSpeedThreshold;
                _iatroVThrSqr = _iatroVThr * _iatroVThr;
                _iatroAVThrSqr = _iatroAVThr * _iatroAVThr;

                if (_iatroVSqr <= _iatroVThrSqr && _iatroAVSqr <= _iatroAVThrSqr)
                {
                    _iatroNow = Time.time;
                    if (_idleAtRestSince <= 0f) { _idleAtRestSince = _iatroNow; }
                    if ((_iatroNow - _idleAtRestSince) >= IdleAtRestDelay)
                    {
                        // Put wheels to sleep once so they stop LateUpdate FX/visual work too.
                        if (!_idleAtRestWheelsSlept && AllWheels != null)
                        {
                            _idleAtRestWheelsSlept = true;
                            for (int i = 0; i < AllWheels.Length; i++)
                            {
                                if (AllWheels[i] != null) { AllWheels[i].SendCustomEvent("FallAsleep"); }
                            }
                        }

                        // Keep local state consistent for anything reading these while idle.
                        CurrentVel = Vector3.zero;
                        VehicleSpeed = 0f;
                        VehicleRigidbody.Sleep();
                        return;
                    }
                }
                else
                {
                    _idleAtRestSince = 0f;

                    // If we were in idle sleep, wake wheels back up immediately when motion resumes.
                    if (_idleAtRestWheelsSlept && AllWheels != null)
                    {
                        _idleAtRestWheelsSlept = false;
                        for (int i = 0; i < AllWheels.Length; i++)
                        {
                            if (AllWheels[i] != null) { AllWheels[i].SendCustomEvent("WakeUp"); }
                        }
                    }
                }
            }
            else
            {
                _idleAtRestSince = 0f;
                _idleAtRestWheelsSlept = false;
            }

            _fuDeltaTime = Time.fixedDeltaTime;
            _fuAbsVel = VehicleRigidbody.velocity;
            CurrentVel = _fuAbsVel - LastTouchedTransform_Speed;
            //calc Gs
            _fuGravity = 9.81f * _fuDeltaTime;
            LastFrameVel.y -= _fuGravity;
            _fuGs3 = VehicleTransform.InverseTransformDirection(CurrentVel - LastFrameVel);
            _fuThisFrameGs = _fuGs3 / _fuGravity;
            Gs_all -= FrameGs[GsFrameCheck];
            Gs_all += _fuThisFrameGs;
            FrameGs[GsFrameCheck] = _fuThisFrameGs;
            GsFrameCheck++;
            if (GsFrameCheck >= NumFUinAvgTime) { GsFrameCheck = 0; }
            AllGs = Gs_all.magnitude / NumFUinAvgTime;
            LastFrameVel = CurrentVel;

            if (Piloting)
            {
#if UNITY_EDITOR
                revUpDT = 1f / NumStepsSec; // so adjusting in play mode works
#endif
                _fuEngineTimeDif = Time.fixedTimeAsDouble - engineTime;
                // works out the number of steps, 
                _fuNumUpdates = (int)(_fuEngineTimeDif / revUpDT);
                // make sure its even (if doing too many, less will be done next frame so it's okay)
                if (_fuNumUpdates % 2 != 0)
                { _fuNumUpdates++; }
                // because the for loop starts at 0, the middle update is one less
                _fuMiddleUpdate = (int)(_fuNumUpdates / 2) - 1;
                _fuWheelUpdateDone = false;
                for (_fuI = 0; _fuI < _fuNumUpdates; _fuI++)
                {
                    RevUp(); // increases revs based on EngineResponseCurve
                    if (!_fuWheelUpdateDone)
                    {
                        if (_fuI == _fuMiddleUpdate)
                        {
                            // Apply EngineForceUsed at the middle step (that's why we needed an even number)
                            // This is required when applying delta time when iterating over a curve (integrals with deltatime)
                            _fuWheelUpdateDone = true;
                            Revs = Mathf.Max(Revs - EngineForceUsed, 0);
                            EngineForceUsed = 0;
                        }
                    }
                }
            }
            else
            {
                Revs = Mathf.Max(Mathf.Lerp(Revs, 0f, 1 - Mathf.Pow(0.5f, _fuDeltaTime * EngineSlowDown)), 0f);
            }
            for (_fuI = 0; _fuI < AllWheels.Length; _fuI++)
            { AllWheels[_fuI].SendCustomEvent("Wheel_FixedUpdate"); }//EngineForceUsed is updated in this function

            VehicleRigidbody.velocity = Vector3.Lerp(VehicleRigidbody.velocity, Vector3.zero, 1 - Mathf.Pow(0.5f, Drag * _fuDeltaTime));
        }

        [System.NonSerialized] private float _lastVehicleFixedTime = -999f;

        // UdonSharp CPU: cached temps for LateUpdate/UpdateGearRatio/FixedUpdate (avoid local declarations).
        private float _luDeltaTime;
        private int _luSteerLen;
        private int _luDriveLen;
        private int _luOtherLen;
        private int _luI;
        private SaccWheel _luWheel;

        private int _ugI;
        private SaccWheel _ugWheel;

        private float _fuDeltaTime;
        private Vector3 _fuAbsVel;
        private float _fuGravity;
        private Vector3 _fuGs3;
        private Vector3 _fuThisFrameGs;
        private double _fuEngineTimeDif;
        private int _fuNumUpdates;
        private int _fuMiddleUpdate;
        private bool _fuWheelUpdateDone;
        private int _fuI;

        // UdonSharp CPU: cached temps for LateUpdate input/leaning/steering (avoid local declarations).
        private Vector2 _luStickPos;
        private float _luStickAngle;
        private float _luForwardAmount;
        private float _luReverseSpeedL;
        private float _luReverseSpeedR;
        private float _luLGearRatio;
        private float _luRGearRatio;
        private float _luLeftThrottle;
        private float _luRightThrottle;
        private int _luWi;
        private int _luAi;
        private int _luDi;
        private float _luLGrip;
        private float _luRGrip;
        private int _luThreei;
        private int _luRi;
        private float _luVRLean;
        private float _luVRLeanPitch;
        private VRCPlayerApi _luLp;
        private Vector3 _luHeadLean;
        private Vector3 _luHeadLeanRoll;
        private Vector3 _luHeadOffset;
        private float _luSteerInput;
        private float _luVRSteerInput;
        private int _luSteerLen2;
        private int _luDriveLen2;
        private int _luDenom;
        private float _luAutoSteer;
        private float _luGroundedWheelsRatio;
        private float _luYawAddAmount;
        private float _luSpeedSteeringLimitUpper;
        private float _luSpeedSteeringLimitLower;
        private float _luAngle;

        private const float _OwnerLateUpdateInterval = 1f / 60f;
        private const float _NonOwnerLateUpdateInterval = 1f / 20f;
        [System.NonSerialized] private float _luTickNow;
        [System.NonSerialized] private float _nextOwnerLateUpdateTime;
        [System.NonSerialized] private float _nextNonOwnerLateUpdateTime;
        [System.NonSerialized] private float _lastOwnerLateUpdateTime;
        [System.NonSerialized] private float _lastNonOwnerLateUpdateTime;
        [System.NonSerialized] private bool _lateUpdateWasOwner;
        [System.NonSerialized] public float EngineForceUsed;
        float revUpDT;
        double engineTime;
        private void RevUp()
        {
            engineTime += revUpDT;
            Revs = Mathf.Max(Mathf.Lerp(Revs, 0f, 1 - Mathf.Pow(0.5f, revUpDT * EngineSlowDown)), 0f);
            if (!LimitingRev)
            {
                Revs += FinalThrottle * DriveSpeed * revUpDT * EngineResponseCurve.Evaluate(Revs / RevLimiter);
                if (Revs > RevLimiter)
                {
                    Revs = RevLimiter;
                    LimitingRev = true;
                    SendCustomEventDelayedSeconds(nameof(ReEnableRevs), RevLimiterDelay);
                }
            }
        }
        public void NetworkExplode()
        {
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(Explode));
        }
        public void Explode()
        {
            if (EntityControl._dead) { return; }

            {
                //this is in SFEXT_G_Wrecked in SAV.
                int killerID = -1;
                byte killerWeaponType = 0;
                if (Utilities.IsValid(EntityControl.LastHitByPlayer))
                {
                    if (Time.time == EntityControl.LastDamageSentTime)
                    {
                        killerID = EntityControl.LastHitByPlayer.playerId;
                        killerWeaponType = EntityControl.LastHitWeaponType;
                    }
                }
                if (SendKillEvents && IsOwner && (EntityControl.Using || (Time.time - EntityControl.PilotExitTime < 3)) && killerID > -1)
                {
                    EntityControl.SendEventToExtensions("SFEXT_O_GotKilled");
                    SendKillEvent(killerID, killerWeaponType);
                }
            }
            Health = FullHealth;
            HasFuel_ = true;
            if (!EntityControl.wrecked) { EntityControl.SetWrecked(); }
            EntityControl.dead = true;
            EntityControl.SendEventToExtensions("SFEXT_G_Explode");

            if (IsOwner)
            {
                YawInput = 0;
                ThrottleInput = 0;
                Fuel = FullFuel;
                EntityControl.SendEventToExtensions("SFEXT_O_Explode");
                _ForceRequestSerialization();
                SendCustomEventDelayedSeconds(nameof(MoveToSpawn), RespawnDelay - 3);
            }

            SendCustomEventDelayedSeconds(nameof(ReAppear), RespawnDelay + Time.fixedDeltaTime * 2);
            SendCustomEventDelayedSeconds(nameof(NotDead), RespawnDelay + InvincibleAfterSpawn);
            //pilot and passengers are dropped out of the vehicle
            if ((Piloting || Passenger) && !InEditor)
            {
                EntityControl.ExitStation();
            }
        }
        public void SendKillEvent(int killerID, byte weaponType)
        {
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(KillEvent), killerID, weaponType);
        }
        [NetworkCallable]
        public void KillEvent(int killerID, byte weaponType)
        {
            // this exists to tell the killer that they got a kill.
            if (killerID > -1)
            {
                VRCPlayerApi KillerAPI = VRCPlayerApi.GetPlayerById(killerID);
                if (Utilities.IsValid(KillerAPI))
                {
                    EntityControl.LastHitByPlayer = KillerAPI;
                    GameObject attackersVehicle = GameObject.Find(EntityControl.LastHitByPlayer.GetPlayerTag("SF_VehicleName"));
                    if (attackersVehicle)
                    {
                        EntityControl.LastAttacker = attackersVehicle.GetComponent<SaccEntity>();
                    }
                    else
                    {
                        EntityControl.LastAttacker = null;
                        return;
                    }
                }
                else
                {
                    EntityControl.LastHitByPlayer = null;
                    return;
                }
                EntityControl.LastHitWeaponType = weaponType;
                if (killerID == localPlayer.playerId)
                {
                    EntityControl.SendEventToExtensions("SFEXT_O_GotAKill");
                }
            }
        }
        public void ReAppear()
        {
            EntityControl.SendEventToExtensions("SFEXT_G_ReAppear");
            EntityControl.SetWreckedFalse();//compatability
            if (IsOwner)
            {
                if (!UsingManualSync)
                {
                    VehicleRigidbody.drag = 0;
                    VehicleRigidbody.angularDrag = 0;
                }
            }
        }
        public void MoveToSpawn()
        {
            PlayerThrottle = 0;//for editor test mode
            if (VehicleRigidbody != null)
            {
                if (!VehicleRigidbody.isKinematic)
                {
                    VehicleRigidbody.angularVelocity = Vector3.zero;
                    VehicleRigidbody.velocity = Vector3.zero;
                }
                else
                {
                    VehicleRigidbody.Sleep();
                }
            }
            //these could get set after death by lag, probably
            Health = FullHealth;
            SetRespawnPos();
            EntityControl.SendEventToExtensions("SFEXT_O_MoveToSpawn");
        }

        private void _ResetAllWheelsAfterTeleport()
        {
            if (AllWheels == null) { return; }
            for (int i = 0; i < AllWheels.Length; i++)
            {
                if (AllWheels[i] != null)
                {
                    // SaccWheel.ResetAfterTeleport()
                    AllWheels[i].SendCustomEvent("ResetAfterTeleport");
                }
            }
        }
        public void SetRespawnPos()
        {
            VehicleRigidbody.drag = 0;
            VehicleRigidbody.angularDrag = 0;
            if (!VehicleRigidbody.isKinematic)
            {
                VehicleRigidbody.angularVelocity = Vector3.zero;
                VehicleRigidbody.velocity = Vector3.zero;
            }
            else
            {
                VehicleRigidbody.Sleep();
            }
            if (InEditor || UsingManualSync)
            {
                VehicleTransform.localPosition = EntityControl.Spawnposition;
                VehicleTransform.localRotation = EntityControl.Spawnrotation;
                VehicleRigidbody.position = VehicleTransform.position;
                VehicleRigidbody.rotation = VehicleTransform.rotation;
            }
            else
            {
                if (EntityControl.EntityObjectSync) { EntityControl.EntityObjectSync.Respawn(); }
            }
            if (EntityControl.RespawnPoint)
            {
                VehicleTransform.position = EntityControl.RespawnPoint.position;
                VehicleTransform.rotation = EntityControl.RespawnPoint.rotation;
                VehicleRigidbody.position = VehicleTransform.position;
                VehicleRigidbody.rotation = VehicleTransform.rotation;
            }

            // Critical: after any teleport/respawn, wheel scripts must clear cached ground/contact state
            // or they can apply extreme suspension/grip forces and get stuck skidding.
            _ResetAllWheelsAfterTeleport();
        }
        public void NotDead()
        {
            Health = FullHealth;
            EntityControl.dead = false;
        }
        [System.NonSerializedAttribute] public float FullHealth;
        //unused variables that are just here for compatability with SAV DFuncs.
        [System.NonSerialized] public int DisablePhysicsAndInputs = 0;
        [System.NonSerialized] public int DisableTaxiRotation;
        [System.NonSerialized] public int DisableGroundDetection;
        [System.NonSerialized] public int DisablePhysicsApplication;
        [System.NonSerialized] public int ThrottleOverridden;
        [System.NonSerialized] public int JoystickOverridden;
        [System.NonSerialized] public bool Taxiing = false;
        //end of compatability variables
        [System.NonSerializedAttribute] public float FullFuel;
        [System.NonSerialized] public bool Occupied;
        [System.NonSerialized] public int NumPassengers;
        [System.NonSerializedAttribute] public bool IsOwner;
        [System.NonSerializedAttribute] public bool UsingManualSync;
        public void SFEXT_G_RespawnButton()//called globally when using respawn button
        {
            if (IsOwner)
            {
                IsOwner = true;
                Fuel = FullFuel;
                Health = FullHealth;
                YawInput = 0;
                AutoSteerLerper = 0;
                SetRespawnPos();
                _ForceRequestSerialization();
            }
            EntityControl.dead = true;
            SendCustomEventDelayedSeconds(nameof(NotDead), InvincibleAfterSpawn);
        }
        public void SFEXT_O_TakeOwnership()
        {
            IsOwner = true;
            AllGs = 0;

            // Ownership handoff can leave PhysX/wheel contact caches in a bad state (especially if the
            // previous owner exited and the vehicle went to sleep). When the new owner begins simulating,
            // stale wheel state can apply extreme forces and cause the vehicle to launch or sink.
            // Stabilize by syncing RB pose, clearing wheel caches, and zeroing near-rest velocities.
            if (VehicleRigidbody != null && VehicleTransform != null)
            {
                if (Sleeping)
                {
                    // Ensures gravity/wheels are re-enabled.
                    SFEXT_L_WakeUp();
                }

                VehicleRigidbody.useGravity = true;
                VehicleRigidbody.WakeUp();

                // Ensure rigidbody pose matches the visible transform before sim starts.
                VehicleRigidbody.position = VehicleTransform.position;
                VehicleRigidbody.rotation = VehicleTransform.rotation;
                Physics.SyncTransforms();

                // Clear cached wheel ground/contact state to avoid impulse spikes.
                _ResetAllWheelsAfterTeleport();

                if (!VehicleRigidbody.isKinematic)
                {
                    Vector3 v = VehicleRigidbody.velocity;
                    Vector3 av = VehicleRigidbody.angularVelocity;
                    if (Sleeping || (v.sqrMagnitude < 0.01f && av.sqrMagnitude < 0.01f))
                    {
                        VehicleRigidbody.velocity = Vector3.zero;
                        VehicleRigidbody.angularVelocity = Vector3.zero;
                    }
                    CurrentVel = VehicleRigidbody.velocity;
                    LastFrameVel = CurrentVel;
                }
                VehiclePosLastFrame = VehicleTransform.position;
            }

            // Manual sync is particularly sensitive to stale physics/contact state during ownership handoff.
            // Do a short RB reinit (freeze for a few frames) so the first owner-simulated steps don't explode.
            if (UsingManualSync && !_physicsReinitInProgress && VehicleRigidbody != null && VehicleTransform != null)
            {
                _physicsReinitWasForOwnership = true;
                int prevDelay = _physicsReinitDelayFrames;
                _physicsReinitDelayFrames = 6;
                _BeginPhysicsReinit();
                _physicsReinitDelayFrames = prevDelay;
            }

            UpdateWheelIsOwner();
            for (int i = 0; i < NumFUinAvgTime; i++) { FrameGs[i] = Vector3.zero; }
            SetupGCalcValues();
            _MarkNetworkDirty();
            _nextManualSyncTime = 0f;
            _ForceRequestSerialization();
        }
        public void SFEXT_O_LoseOwnership()
        {
            VehiclePosLastFrame = VehicleTransform.position;
            IsOwner = false;
            UpdateWheelIsOwner();
            _InitRemoteInputSmoothingFromCurrent();
        }
        public void UpdateWheelIsOwner()
        {
            if (IsOwner)
            {
                for (int i = 0; i < DriveWheels.Length; i++)
                {
                    Networking.SetOwner(Networking.LocalPlayer, DriveWheels[i].gameObject);
                }
                for (int i = 0; i < SteerWheels.Length; i++)
                {
                    Networking.SetOwner(Networking.LocalPlayer, SteerWheels[i].gameObject);
                }
                for (int i = 0; i < OtherWheels.Length; i++)
                {
                    Networking.SetOwner(Networking.LocalPlayer, OtherWheels[i].gameObject);
                }
            }
            for (int i = 0; i < DriveWheels.Length; i++)
            {
                DriveWheels[i].SendCustomEvent("UpdateOwner");
            }
            for (int i = 0; i < SteerWheels.Length; i++)
            {
                SteerWheels[i].SendCustomEvent("UpdateOwner");
            }
            for (int i = 0; i < OtherWheels.Length; i++)
            {
                OtherWheels[i].SendCustomEvent("UpdateOwner");
            }
        }
        public void SetWheelDriver()
        {
            for (int i = 0; i < DriveWheels.Length; i++)
            {
                SaccWheel w = (i < _DriveWheelsCount) ? _DriveWheelsTyped[i] : null;
                if (w != null) { w.Piloting = Piloting; }
                else if (DriveWheels[i] != null) { DriveWheels[i].SetProgramVariable("Piloting", Piloting); }
            }
            for (int i = 0; i < SteerWheels.Length; i++)
            {
                SaccWheel w = (i < _SteerWheelsCount) ? _SteerWheelsTyped[i] : null;
                if (w != null) { w.Piloting = Piloting; }
                else if (SteerWheels[i] != null) { SteerWheels[i].SetProgramVariable("Piloting", Piloting); }
            }
            for (int i = 0; i < OtherWheels.Length; i++)
            {
                SaccWheel w = (i < _OtherWheelsCount) ? _OtherWheelsTyped[i] : null;
                if (w != null) { w.Piloting = Piloting; }
                else if (OtherWheels[i] != null) { OtherWheels[i].SetProgramVariable("Piloting", Piloting); }
            }
        }
        /*     public void SetWheelSGV()
            {
                for (int i = 0; i < DriveWheels.Length; i++)
                {
                    DriveWheels[i].SetProgramVariable("SGVControl", this);
                }
                for (int i = 0; i < OtherWheels.Length; i++)
                {
                    OtherWheels[i].SetProgramVariable("SGVControl", this);
                }
            } */
        public void SFEXT_G_PilotEnter()
        {
            LimitingRev = false;
            Occupied = true;
            EntityControl.dead = false;//vehicle stops being invincible if someone gets in, also acts as redundancy incase someone missed the notdead event
        }
        public void SFEXT_G_PilotExit()
        {
            Occupied = false;
        }
        public void SFEXT_O_PilotEnter()
        {
            Piloting = true;
            engineTime = Time.fixedTimeAsDouble;
            TANK_Cruising = false;
            System.Array.Clear(TankThrottles, 0, 2);
            AllGs = 0f;
            InVR = EntityControl.InVR;
            SetCollidersLayer(EntityControl.OnboardVehicleLayer);
            SetWheelDriver();
            SetupGCalcValues();

            // Manual sync: ensure the local owner is actually simulating physics.
            // This is a last-resort unfreeze in case a prior ownership handoff left the RB kinematic.
            if (UsingManualSync && IsOwner && VehicleRigidbody != null)
            {
                VehicleRigidbody.detectCollisions = true;
                VehicleRigidbody.isKinematic = false;
                VehicleRigidbody.useGravity = true;
                VehicleRigidbody.WakeUp();
            }

            _MarkNetworkDirty();
            _nextManualSyncTime = 0f;
            _ForceRequestSerialization();
        }
        public void SFEXT_O_PilotExit()
        {
            Piloting = false;
            WheelGrippingLastFrame_toggleR = false;
            WheelReleaseCountR = 0;
            WheelGrabToggleR = false;
            TANK_Cruising = false;
            System.Array.Clear(TankThrottles, 0, 2);
            for (int i = 0; i < DriveWheels.Length; i++)
            {
                SaccWheel w = (i < _DriveWheelsCount) ? _DriveWheelsTyped[i] : null;
                if (w != null) { w.EngineRevs = 0f; }
                else if (DriveWheels[i] != null) { DriveWheels[i].SetProgramVariable("EngineRevs", 0f); }
            }
            for (int i = 0; i < SteerWheels.Length; i++)
            {
                SaccWheel w = (i < _SteerWheelsCount) ? _SteerWheelsTyped[i] : null;
                if (w != null) { w.EngineRevs = 0f; }
                else if (SteerWheels[i] != null) { SteerWheels[i].SetProgramVariable("EngineRevs", 0f); }//for TankMode
            }
            SetCollidersLayer(EntityControl.OutsideVehicleLayer);
            if (!EntityControl.MySeatIsExternal) { localPlayer.SetVelocity(CurrentVel); }
            SetWheelDriver();

            _MarkNetworkDirty();
            _nextManualSyncTime = 0f;
            _ForceRequestSerialization();
        }
        public void SFEXT_P_PassengerEnter()
        {
            Passenger = true;
            SetCollidersLayer(EntityControl.OnboardVehicleLayer);
        }
        public void SFEXT_P_PassengerExit()
        {
            Passenger = false;
            if (!EntityControl.MySeatIsExternal) { localPlayer.SetVelocity(CurrentVel); }
            SetCollidersLayer(EntityControl.OutsideVehicleLayer);
        }
        public void SFEXT_G_PassengerEnter()
        {
            NumPassengers++;
        }
        public void SFEXT_G_PassengerExit()
        {
            NumPassengers--;
        }
        public void SFEXT_L_FallAsleep()
        {
            VehicleRigidbody.useGravity = false;
            if (!VehicleRigidbody.isKinematic)
            {
                CurrentVel = LastFrameVel = VehicleRigidbody.velocity = Vector3.zero;
                VehicleRigidbody.angularVelocity = Vector3.zero;
            }
            else
            {
                CurrentVel = LastFrameVel = Vector3.zero;
                VehicleRigidbody.Sleep();
            }
            VehicleSpeed = 0;
            Sleeping = true;

            // Ensure wheel scripts stop applying forces and reset skid/effect state.
            if (AllWheels != null)
            {
                for (int i = 0; i < AllWheels.Length; i++)
                {
                    if (AllWheels[i] != null) { AllWheels[i].SendCustomEvent("FallAsleep"); }
                }
            }
        }
        public void SFEXT_L_WakeUp()
        {
            VehicleRigidbody.WakeUp();
            VehicleRigidbody.useGravity = true;
            Sleeping = false;

            // Re-enable wheel physics after wake.
            if (AllWheels != null)
            {
                for (int i = 0; i < AllWheels.Length; i++)
                {
                    if (AllWheels[i] != null) { AllWheels[i].SendCustomEvent("WakeUp"); }
                }
            }
        }
        float LastHitTime = -100, PredictedHealth;
        public void SFEXT_L_BulletHit()
        {
            if (IsOwner || EntityControl.dead || EntityControl.invincible) { return; }
            if (Time.time - EntityControl.LastResupplyTime < 2) return;//disable prediction if vehicle has recently been healing
            if (PredictExplosion)
            {
                if (Time.time - LastHitTime > 2)
                {
                    LastHitTime = Time.time;
                    PredictedHealth = Mathf.Min(Health - EntityControl.LastHitDamage, FullHealth);
                    if (PredictedHealth < 0)
                    {
                        Explode();
                    }
                }
                else
                {
                    LastHitTime = Time.time;
                    PredictedHealth = Mathf.Min(PredictedHealth - EntityControl.LastHitDamage, FullHealth);
                    if (PredictedHealth < 0)
                    {
                        Explode();
                    }
                }
            }
        }
        public void SFEXT_G_BulletHit()
        {
            if (!IsOwner || EntityControl.dead || EntityControl.invincible) { return; }
            Health = Mathf.Min(Health - EntityControl.LastHitDamage, FullHealth);
            if (Health <= 0f)
            {
                NetworkExplode();
            }
        }
        private float LastCollisionTime;
        const float MINCOLLISIONSOUNDDELAY = 0.1f;
        public void SFEXT_L_OnCollisionEnter()
        {
            if (!IsOwner) { return; }
            Collision col = EntityControl.LastCollisionEnter;
            if (col == null) { return; }
            float colmag = col.impulse.magnitude / VehicleRigidbody.mass;
            float colmag_dmg = colmag;
            if (colmag_dmg > Crash_Damage_Speed)
            {
                if (colmag_dmg < Crash_Death_Speed)
                {
                    float dif = Crash_Death_Speed - Crash_Damage_Speed;
                    float newcolT = (colmag_dmg - Crash_Damage_Speed) / dif;
                    colmag_dmg = Mathf.Lerp(0, Crash_Death_Speed, newcolT);
                }
                float thisGDMG = (colmag_dmg / Crash_Death_Speed) * FullHealth;
                Health -= thisGDMG;

                if (Health <= 0 /* && thisGDMG > FullHealth * 0.5f (This is for if wrecked is implemented) */)
                {
                    if (Piloting) { EntityControl.SendEventToExtensions("SFEXT_O_Suicide"); }
                    NetworkExplode();
                }
            }
            if (Time.time - LastCollisionTime > MINCOLLISIONSOUNDDELAY)
            {
                if (colmag > BigCrashSpeed)
                {
                    SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(SendBigCrash));
                }
                else if (colmag > MediumCrashSpeed)
                {
                    SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(SendMediumCrash));
                }
                else if (colmag > SmallCrashSpeed)
                {
                    SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(SendSmallCrash));
                }
            }
            LastCollisionTime = Time.time;
        }
        public void SendSmallCrash()
        {
            EntityControl.SendEventToExtensions("SFEXT_G_SmallCrash");
        }
        public void SendMediumCrash()
        {
            EntityControl.SendEventToExtensions("SFEXT_G_MediumCrash");
        }
        public void SendBigCrash()
        {
            EntityControl.SendEventToExtensions("SFEXT_G_BigCrash");
        }
        public void SFEXT_G_ReSupply()
        {
            if ((Fuel < FullFuel - 10) || (Health != FullHealth))
            {
                EntityControl.ReSupplied++;//used to only play the sound if we're actually repairing/getting ammo/fuel
            }
            float addedHealth = FullHealth / RepairTime; ;
            PredictedHealth = Mathf.Min(PredictedHealth + addedHealth, FullHealth);

            if (IsOwner)
            {
                Fuel = Mathf.Min(Fuel + (FullFuel / RefuelTime), FullFuel);
                Health = Mathf.Min(Health + addedHealth, FullHealth);
            }
        }
        public void SFEXT_G_ReFuel()
        {
            if (Fuel < FullFuel - 10)
            { EntityControl.ReSupplied++; }
            if (IsOwner)
            {
                Fuel = Mathf.Min(Fuel + (FullFuel / RefuelTime), FullFuel);
            }
        }
        public void SFEXT_G_RePair()
        {
            if (Health != FullHealth)
            { EntityControl.ReSupplied++; }
            if (IsOwner)
            {
                Health = Mathf.Min(Health + (FullHealth / RepairTime), FullHealth);
            }
        }
        public void CheckDistance()
        {
            if (!_checkDistanceLoopActive) { return; }

            // In VRChat runtime, calling GetPosition() on an invalid/suspended player can throw an Udon extern exception.
            // Also guard against CenterOfMass not being initialized yet.
            VRCPlayerApi lp = Networking.LocalPlayer;
            if (!Utilities.IsValid(lp) || lp.isSuspended || CenterOfMass == null)
            {
                if (_checkDistanceLoopActive) { SendCustomEventDelayedSeconds(nameof(CheckDistance), 2); }
                return;
            }

            CurrentDistance = Vector3.Distance(lp.GetPosition(), CenterOfMass.position);
            if (CurrentDistance > DistantRange)
            {
                if (!CurrentlyDistant)
                {
                    CurrentlyDistant = true;

                    // Wheels gate skid FX on their own CurrentlyDistant flag; keep them in sync.
                    if (AllWheels != null)
                    {
                        for (int i = 0; i < AllWheels.Length; i++)
                        {
                            UdonSharpBehaviour w = AllWheels[i];
                            if (w == null) { continue; }
                            w.SetProgramVariable("CurrentlyDistant", true);
                        }
                    }

                    EntityControl.SendEventToExtensions("SFEXT_L_BecomeDistant");
                }
            }
            else
            {
                if (CurrentlyDistant)
                {
                    CurrentlyDistant = false;

                    // Wheels gate skid FX on their own CurrentlyDistant flag; keep them in sync.
                    if (AllWheels != null)
                    {
                        for (int i = 0; i < AllWheels.Length; i++)
                        {
                            UdonSharpBehaviour w = AllWheels[i];
                            if (w == null) { continue; }
                            w.SetProgramVariable("CurrentlyDistant", false);
                        }
                    }

                    EntityControl.SendEventToExtensions("SFEXT_L_NotDistant");
                }
            }
            if (_checkDistanceLoopActive) { SendCustomEventDelayedSeconds(nameof(CheckDistance), 2); }
        }
        public void SetCollidersLayer(int NewLayer)
        {
            if (VehicleMesh)
            {
                if (OnlyChangeColliders)
                {
                    Collider[] children = VehicleMesh.GetComponentsInChildren<Collider>(true);
                    foreach (Collider child in children)
                    {
                        child.gameObject.layer = NewLayer;
                    }
                }
                else
                {
                    Transform[] children = VehicleMesh.GetComponentsInChildren<Transform>(true);
                    foreach (Transform child in children)
                    {
                        child.gameObject.layer = NewLayer;
                    }
                }
            }
        }
        void RHandSteeringWheel(float RGrip, bool isReGrab)
        {
            if (!Utilities.IsValid(localPlayer) || localPlayer.isSuspended)
            {
                VRJoystickPosR = 0f;
                return;
            }
            bool GrabbingR = RGrip > GripSensitivity;
            if (GrabbingR)
            {
                if (!WheelGrippingLastFrame_toggleR)
                {
                    if (Time.time - LastGripTimeR < .25f)
                    {
                        WheelGrabToggleR = true;
                        WheelReleaseCountR = 0;
                    }
                    LastGripTimeR = Time.time;
                }
                WheelGrippingLastFrame_toggleR = true;
            }
            else
            {
                if (WheelGrippingLastFrame_toggleR)
                {
                    WheelReleaseCountR++;
                    if (WheelReleaseCountR > 1)
                    {
                        WheelGrabToggleR = false;
                    }
                }
                WheelGrippingLastFrame_toggleR = false;
            }
            //VR SteeringWheel
            if (GrabbingR || WheelGrabToggleR)
            {
                //Toggle gripping the steering wheel if double tap grab
                HandsOnWheel++;
                Quaternion VehicleRotDif = ControlsRoot.rotation * Quaternion.Inverse(VehicleRotLastFrameR);//difference in vehicle's rotation since last frame
                VehicleRotLastFrameR = ControlsRoot.rotation;
                JoystickZeroPointR = VehicleRotDif * JoystickZeroPointR;//zero point rotates with the vehicle so it appears still to the pilot
                if (!WheelGripLastFrameR)//first frame you gripped joystick
                {
                    if (!isReGrab) { EntityControl.SendEventToExtensions("SFEXT_O_WheelGrabbedR"); }
                    VehicleRotDif = Quaternion.identity;
                    JoystickZeroPointR = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.RightHand).rotation;
                    if (Drift_AutoSteer) { JoyStickValueR = 0; }
                    else { JoyStickValueR = -YawInput * SteeringWheelDegrees; }
                    JoystickValueLastFrameR = 0f;
                    CompareAngleLastFrameR = Vector3.up;
                    localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Right, .05f, .222f, 35);
                }
                WheelGripLastFrameR = true;
                //difference between the vehicle and the hand's rotation, and then the difference between that and the JoystickZeroPoint
                Quaternion JoystickDifference;
                JoystickDifference = Quaternion.Inverse(ControlsRoot.rotation) * localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.RightHand).rotation
                    * Quaternion.Inverse(JoystickZeroPointR)
                    * ControlsRoot.rotation;

                Vector3 JoystickPosYaw = (JoystickDifference * Vector3.up);
                Vector3 CompareAngle = Vector3.ProjectOnPlane(JoystickPosYaw, Vector3.forward);
                JoyStickValueR += (Vector3.SignedAngle(CompareAngleLastFrameR, CompareAngle, Vector3.forward));
                CompareAngleLastFrameR = CompareAngle;
                JoystickValueLastFrameR = JoyStickValueR;
                VRJoystickPosR = JoyStickValueR / SteeringWheelDegrees;
            }
            else
            {
                VRJoystickPosR = 0f;
                if (WheelGripLastFrameR)//first frame you let go of wheel
                {
                    WheelGripLastFrameR = false;
                    EntityControl.SendEventToExtensions("SFEXT_O_WheelDroppedR");
                    WheelGripLastFrameL = false;
                    //LHandSteeringWheel hasn't run yet so don't need to do anything else
                    localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Right, .05f, .222f, 35);
                }
            }
        }
        void LHandSteeringWheel(float LGrip)
        {
            if (!Utilities.IsValid(localPlayer) || localPlayer.isSuspended)
            {
                VRJoystickPosL = 0f;
                return;
            }
            //Toggle gripping the steering wheel if double tap grab
            bool GrabbingL = LGrip > GripSensitivity;
            if (GrabbingL)
            {
                if (!WheelGrippingLastFrame_toggleL)
                {
                    if (Time.time - LastGripTimeL < .25f)
                    {
                        WheelGrabToggleL = true;
                        WheelReleaseCountL = 0;
                    }
                    LastGripTimeL = Time.time;
                }
                WheelGrippingLastFrame_toggleL = true;
            }
            else
            {
                if (WheelGrippingLastFrame_toggleL)
                {
                    WheelReleaseCountL++;
                    if (WheelReleaseCountL > 1)
                    {
                        WheelGrabToggleL = false;
                    }
                }
                WheelGrippingLastFrame_toggleL = false;
            }
            //VR SteeringWheel
            if (GrabbingL || WheelGrabToggleL)
            {
                HandsOnWheel++;
                Quaternion VehicleRotDif = ControlsRoot.rotation * Quaternion.Inverse(VehicleRotLastFrameL);//difference in vehicle's rotation since last frame
                VehicleRotLastFrameL = ControlsRoot.rotation;
                JoystickZeroPointL = VehicleRotDif * JoystickZeroPointL;//zero point rotates with the vehicle so it appears still to the pilot
                if (!WheelGripLastFrameL)//first frame you gripped joystick
                {
                    EntityControl.SendEventToExtensions("SFEXT_O_WheelGrabbedL");
                    VehicleRotDif = Quaternion.identity;
                    JoystickZeroPointL = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.LeftHand).rotation;
                    if (Drift_AutoSteer) { JoyStickValueL = 0; }
                    else { JoyStickValueL = -YawInput * SteeringWheelDegrees; }
                    JoystickValueLastFrameL = 0f;
                    CompareAngleLastFrameL = Vector3.up;
                    localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Left, .05f, .222f, 35);
                }
                WheelGripLastFrameL = true;
                //difference between the vehicle and the hand's rotation, and then the difference between that and the JoystickZeroPointL
                Quaternion JoystickDifference = Quaternion.Inverse(ControlsRoot.rotation) * localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.LeftHand).rotation
                   * Quaternion.Inverse(JoystickZeroPointL)
                    * ControlsRoot.rotation;

                Vector3 JoystickPosYaw = (JoystickDifference * Vector3.up);
                Vector3 CompareAngle = Vector3.ProjectOnPlane(JoystickPosYaw, Vector3.forward);
                JoyStickValueL += (Vector3.SignedAngle(CompareAngleLastFrameL, CompareAngle, Vector3.forward));
                CompareAngleLastFrameL = CompareAngle;
                JoystickValueLastFrameL = JoyStickValueL;
                VRJoystickPosL = JoyStickValueL / SteeringWheelDegrees;
            }
            else
            {
                VRJoystickPosL = 0f;
                if (WheelGripLastFrameL)//first frame you let go of wheel
                {
                    WheelGripLastFrameL = false;
                    EntityControl.SendEventToExtensions("SFEXT_O_WheelDroppedL");
                    localPlayer.PlayHapticEventInHand(VRC_Pickup.PickupHand.Left, .05f, .222f, 35);
                    if (WheelGripLastFrameR)
                    {
                        WheelGripLastFrameR = false;
                        //regrab the right hand to stop the wheel position teleporting
                        RHandSteeringWheel(Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryHandTrigger"), true);
                        HandsOnWheel--;//remove one because we ran R twice
                    }
                }
            }
        }
        private bool RepeatingWorldCheckAxis;
        public void DoRepeatingWorld()
        {
            if (RepeatingWorld)
            {
                if (RepeatingWorldCheckAxis)
                {
                    if (Mathf.Abs(CenterOfMass.position.z) > RepeatingWorldDistance)
                    {
                        if (CenterOfMass.position.z > 0)
                        {
                            Vector3 vehpos = VehicleTransform.position;
                            vehpos.z -= RepeatingWorldDistance * 2;
                            VehicleTransform.position = vehpos;
                            VehicleRigidbody.position = VehicleTransform.position;
                        }
                        else
                        {
                            Vector3 vehpos = VehicleTransform.position;
                            vehpos.z += RepeatingWorldDistance * 2;
                            VehicleTransform.position = vehpos;
                            VehicleRigidbody.position = VehicleTransform.position;
                        }
                    }
                }
                else
                {
                    if (Mathf.Abs(CenterOfMass.position.x) > RepeatingWorldDistance)
                    {
                        if (CenterOfMass.position.x > 0)
                        {
                            Vector3 vehpos = VehicleTransform.position;
                            vehpos.x -= RepeatingWorldDistance * 2;
                            VehicleTransform.position = vehpos;
                            VehicleRigidbody.position = VehicleTransform.position;
                        }
                        else
                        {
                            Vector3 vehpos = VehicleTransform.position;
                            vehpos.x += RepeatingWorldDistance * 2;
                            VehicleTransform.position = vehpos;
                            VehicleRigidbody.position = VehicleTransform.position;
                        }
                    }
                }
                RepeatingWorldCheckAxis = !RepeatingWorldCheckAxis;//Check one axis per frame
            }
        }
    }
}