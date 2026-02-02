
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace SaccFlightAndVehicles
{
    [DefaultExecutionOrder(1500)]
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class SaccWheel : UdonSharpBehaviour
    {
        [Header("Sync is only used for skid effects,\nset Synchronization Method:\nNone to save bandwidth (Use for tanks)\nManual to sync skid sounds/effects\nDo Not Use Continuous")]
        [Space(10)]
        public Rigidbody CarRigid;
        public SaccGroundVehicle SGVControl;
        public float SyncInterval = 0.45f;
        public Transform WheelPoint;
        [Tooltip("Position to apply forces on the rigidbody at. Leave empty to use wheel-ground contact point")]
        public Transform WheelForceApplyPoint;
        public Transform WheelVisual;
        public Transform WheelVisual_Ground;
        [Tooltip("For if wheel is part of a caterpillar track, so wheels can match rotation with each other, prevent (visual) wheelspinning")]
        public SaccWheel WheelVisual_RotationSource;
        public float SuspensionDistance;
        public float WheelRadius;
        public float SpringForceMulti = 8f;
        [Tooltip("Multiplier for suspension strength when suspension is compressing")]
        public float Damping_Bump = 0.75f;
        [Tooltip("Multiplier for suspension strength(reduction) when suspension is decompressing")]
        public float Damping_Rebound = 0.7f;
        [Tooltip("Limit suspension force so that the car doesn't jump up when going over a step")]
        public float MaxSusForce = 60f;
        // [Tooltip("Limit Damping when suspension is decomopressing?")]
        // public float MaxNegDamping = 999999f;
        [Tooltip("Extra height on the raycast origin to prevent the wheel from sticking through the floor")]
        public float ExtraRayCastDistance = .5f;
        public float Grip = 350f;
        public float GripGain = 1f;
        public AnimationCurve GripCurve = AnimationCurve.Linear(0, 1, 1, 1);
        [Tooltip("Multiply forward grip by this value for sideways grip")]
        public float LateralGrip = 1f;
        public AnimationCurve GripCurveLateral = AnimationCurve.Linear(0, 1, 1, 1);
        [Tooltip("!!THE LATERAL GRIP VARS ARE STILL USED WHEN THIS IS FALSE, JUST DIFFERENTLY!!\nCompletely separate wheel's sideways and forward grip calculations (More arcadey)")]
        public bool SeparateLongLatGrip = false;
        [Tooltip("Choose how much separation there is\n0 is still different from SeparateLongLatGrip being disabled\nRECOMMENDED: SkidRatioMode 1")]
        public float LongLatSeparation = 1;
        [Tooltip("How quickly grip falls off with roll")]
        public float WheelRollGrip_Power = 1;
        [Range(0, 2), Tooltip("3 Different ways to calculate amount of engine force used when sliding + accelerating, for testing. 0 = old way, 1 = keeps more energy, 2 = loses more energy")]
        public int SkidRatioMode = 0;
        [Tooltip("Only effects DriveWheels. Behaves like engine torque. How much forces on the wheel from the ground can influence the engine speed, low values will make the car skid more")]
        public float EngineInfluence = 20000f;
        [Tooltip("Only effects DriveWheels. How much (extra) the engine slows down when braking.")]
        public float EngineBrakeStrength = 200f;
        [Tooltip("Only effects DriveWheels. Brakes are weaker when not pressing the clutch because you have to slow down the engine too.\n\nLess than 2 will make braking FASTER when clutch ISNT pressed.")]
        public float Brake_EngineWeight = 2.5f;
        [Tooltip("Max angle of ground at which vehicle can park on without sliding down")]
        [SerializeField] float MaxParkingIncline = 30;
        public LayerMask WheelLayers;
        public float ClutchStrength = 100f;
        [Tooltip("Skip sound and skid effects completely")]
        public bool DisableEffects = false;
        public float[] SurfaceType_Grips = { 1f, 0.7f, 0.2f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };
        public float[] SurfaceType_Slowdown = { 0.1f, 4f, 0.05f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f };
        public AudioSource[] SurfaceType_SkidSounds;
        public ParticleSystem[] SurfaceType_SkidParticles;
        public ParticleSystem.EmissionModule[] SurfaceType_SkidParticlesEM;
        public float[] SurfaceType_SkidParticles_Amount = { .3f, .3f, .3f, .3f, .3f, .3f, .3f, .3f, .3f, .3f };
        private float SkidSound_Min_THREEQUARTER, SkidSound_Min_TWOTHRID; // sync a bit before the skid speed so it's more accurate
        [Tooltip("Lower number = less skid required for sound to start")]
        public float SkidSound_Min = 3f;
        [Tooltip("How quickly volume increases as skid speed increases")]
        public float SkidSound_VolumeIncrease = 0.5f;
        [Tooltip("How quickly pitch increases as skid speed increases")]
        public float SkidSound_PitchIncrease = 0.02f;
        public float SkidSound_Pitch = 1f;
        [Tooltip("Reduce volume of skid swhilst i   n the car")]
        public float SkidVolInVehicleMulti = .4f;
        [Header("Debug")]
        public Transform LastTouchedTransform;
        public Rigidbody LastTouchedTransform_RB;
        public Vector3 LastTouchedTransform_Position;
        public Vector3 LastTouchedTransform_Speed;
        public float CurrentGrip = 7f;
        public float CurrentNumParticles = 0f;
        public float CurrentWheelSlowDown = 0f;

        [Tooltip("Sanity-check and clamp per-step wheel forces to prevent NaN/Inf or runaway impulses after disable/enable cycles.")]
        public bool Safety_ForceSanityChecks = true;
        [Tooltip("Max suspension velocity change applied per FixedUpdate (ForceMode.VelocityChange). Set <= 0 to disable clamping.")]
        public float Safety_MaxSuspensionDeltaV = 25f;
        [Tooltip("Max grip velocity change applied per FixedUpdate (ForceMode.VelocityChange). Set <= 0 to disable clamping.")]
        public float Safety_MaxGripDeltaV = 60f;
        [Tooltip("Debug: prevent suspension forces from being applied")]
        public bool DebugDisableSuspensionForces = false;
        [Tooltip("Debug: prevent grip forces from being applied")]
        public bool DebugDisableGripForces = false;
        [System.NonSerialized] public bool Piloting;
        [System.NonSerialized] public float EngineRevs;
        [System.NonSerialized] public bool IsDriveWheel = false;
        [System.NonSerialized] public bool IsSteerWheel = false;
        [System.NonSerialized] public bool IsOtherWheel = false;
        private AudioSource SkidSound;
        private ParticleSystem SkidParticle;
        private ParticleSystem.EmissionModule SkidParticleEM;
        // Local skid magnitude (m/s-ish, derived from slip). Not synced directly.
        private float SkidLength;
        // Network skid intensity level (0..3). Coarse by design to avoid spamming during long burnouts.
        [UdonSynced] private byte SkidPacked;
        private float SkidLength_Smoothed;
        public float SkidLength_SmoothStep = 0.11f;
        private bool SyncSkid_Running;
        private bool SkidLength_SkiddingLast;
        private float lastSync;

        private const float _SkidNetKeepAliveSeconds = 1.5f;
        private const float _SkidNetStaleSeconds = 2.25f;
        private const byte _SkidMaxLevel = 3;
        private byte _skidPackedLastSent;
        private bool _skidWasSkiddingLastSent;
        private float _lastNetSkidUpdateTime;

        private byte _skidLevelOwner;

        private void OnDisable()
        {
            // Hard stop effects + reset contact state so nothing can get stuck playing/forcing.
            ResetAfterTeleport();
        }

        public void ResetAfterTeleport()
        {
            // Effects
            SkidLength = 0f;
            SkidPacked = 0;
            SkidLength_Smoothed = 0f;
            SyncSkid_Running = false;
            SkidLength_SkiddingLast = false;
            StopSkidSound();
            StopSkidParticle();

            // Wheel/suspension state (prevents huge forces after a rigidbody move)
            WheelRotationSpeedRPS = 0f;
            WheelRotationSpeedRPM = 0f;
            WheelRotationSpeedSurf = 0f;

            Grounded = false;
            compressionLast = 0f;
            SusForce = Vector3.zero;
            PointVelocity = Vector3.zero;

            LastTouchedTransform = null;
            LastTouchedTransform_RB = null;
            LastTouchedTransform_Speed = Vector3.zero;
            _surfaceColliderLast = null;
            SurfaceType = -1;

            if (WheelPoint != null)
            {
                GroundPointLast = WheelPoint.position;
                LastTouchedTransform_Position = WheelPoint.position;
            }
        }
        public float Clutch = 1f;
        public float BrakeStrength;
        public float WheelRotation;
        public float WheelRotationSpeedRPS;
        public float WheelRotationSpeedRPM;
        public float WheelRotationSpeedSurf;
        public bool Grounded;
        [System.NonSerialized] public float HandBrake;
        [System.NonSerialized] public float Brake;
        public bool Sleeping;
        private int SurfaceType = -1;
        private float SkidVolumeMulti = 1;
        private bool SkidSoundPlayingLast;
        private bool SkidParticlePlayingLast;
        bool TankMode;
        private Vector3 SusDirection;
        private Renderer WheelRenderer;
        [FieldChangeCallback(nameof(GearRatio))] public float _GearRatio = 0f;
        public float GearRatio
        {
            set
            {
                if (value == 0f && !TankMode)
                {
                    GearNeutral = true;
                }
                else
                {
                    GearNeutral = false;
                }
                _GearRatio = value;
            }
            get => _GearRatio;
        }
        public bool GearNeutral;
        private float WheelDiameter;
        private float WheelCircumference;
        private float WheelCircumferenceInv;
        private float compressionLast;
        public bool IsOwner = false;
        public bool CurrentlyDistant = true;

        // Per-wheel player proximity cache (avoid GetPlayers every frame).
        private const float _NearPlayerRadius = 250f;
        private const float _NearPlayerRadiusSqr = _NearPlayerRadius * _NearPlayerRadius;
        private const float _PlayerCacheRefreshSeconds = 0.5f;
        [System.NonSerialized] private VRCPlayerApi[] _playerCache;
        [System.NonSerialized] private float _nextPlayerCacheRefresh;
        [System.NonSerialized] private bool _cachedAnyOtherPlayerNear;

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
                // Wheels are owned by the vehicle owner; ignore local so "only owner nearby" throttles.
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

        [System.NonSerialized] private Transform _selfTransform;
        [System.NonSerialized] private float _cosMaxParkingIncline;
        [System.NonSerialized] private Collider _surfaceColliderLast;
        
        [System.NonSerialized] private float _tmpOtherSpeed;
        [System.NonSerialized] private Quaternion _tmpWheelRotQuat;

        // Hot-path temp vars as fields (UdonSharp local-var allocation workaround)
        [System.NonSerialized] private Vector3 _tmpWpPos;
        [System.NonSerialized] private Vector3 _tmpWpUp;
        [System.NonSerialized] private Vector3 _tmpRayOrigin;
        [System.NonSerialized] private float _tmpCompression;
        [System.NonSerialized] private float _tmpFixedDT;
        [System.NonSerialized] private Vector3 _tmpPointVel;
        [System.NonSerialized] private float _tmpUpDot;
        [System.NonSerialized] private float _tmpDamping;
        [System.NonSerialized] private Vector3 _tmpSpringForce;
        [System.NonSerialized] private Vector3 _tmpDampingForce;
        [System.NonSerialized] private float _tmpSusDot;

        [System.NonSerialized] private float _tmpDeltaTime;
        [System.NonSerialized] private float _tmpForwardSpeed;
        [System.NonSerialized] private float _tmpForwardSideRatio;
        [System.NonSerialized] private float _tmpForceUsed;
        [System.NonSerialized] private float _tmpForwardSlip;
        [System.NonSerialized] private float _tmpWheelRotationSpeedSurfPrev;
        [System.NonSerialized] private Vector3 _tmpWpForward;
        [System.NonSerialized] private Vector3 _tmpWpRight;
        [System.NonSerialized] private Vector3 _tmpWheelForwardSpeed;
        [System.NonSerialized] private float _tmpForwardSpeedAbs;
        [System.NonSerialized] private Vector3 _tmpForwardTangent;
        [System.NonSerialized] private float _tmpForwardTangentSqrMag;
        [System.NonSerialized] private float _tmpForwardLen;
        [System.NonSerialized] private Vector3 _tmpForwardSkid;
        [System.NonSerialized] private Vector3 _tmpSideSkid;
        [System.NonSerialized] private float _tmpWfSqr;
        [System.NonSerialized] private float _tmpDeltaSurf;
        [System.NonSerialized] private Vector3 _tmpSkidVec;
        [System.NonSerialized] private Vector3 _tmpFullSkid;
        [System.NonSerialized] private float _tmpFullSkidSqrMag;
        [System.NonSerialized] private float _tmpFullSkidMag;
        [System.NonSerialized] private float _tmpSideLen;
        [System.NonSerialized] private float _tmpFullLen;
        [System.NonSerialized] private Vector3 _tmpGripForce3;
        [System.NonSerialized] private float _tmpSusForceMag;
        [System.NonSerialized] private float _tmpMaxGrip;
        [System.NonSerialized] private float _tmpMaxGripLat;
        [System.NonSerialized] private Vector3 _tmpGripForceForward;
        [System.NonSerialized] private Vector3 _tmpGripForceLat;
        [System.NonSerialized] private float _tmpRollDot;
        [System.NonSerialized] private float _tmpWheelRollGrip;
        [System.NonSerialized] private float _tmpEvalSkid;
        [System.NonSerialized] private float _tmpGripPc;
        [System.NonSerialized] private float _tmpEvalSkidLat;
        [System.NonSerialized] private float _tmpGripPcLat;
        [System.NonSerialized] private float _tmpInvFull;
        [System.NonSerialized] private float _tmpPvSqr;
        [System.NonSerialized] private float _tmpPvMag;
        [System.NonSerialized] private float _tmpNumWheels;
        [System.NonSerialized] private float _tmpGripClamp;
        [System.NonSerialized] private float _tmpGfMag;
        [System.NonSerialized] private float _tmpEngineRevs;

        [System.NonSerialized] private float _tmpNow;
        [System.NonSerialized] private bool _tmpVisible;
        [System.NonSerialized] private bool _tmpSkidding;
        [System.NonSerialized] private bool _tmpShouldDoSkidFx;
        [System.NonSerialized] private bool _tmpShouldStop;
        [System.NonSerialized] private float _tmpSkidVol;

        [System.NonSerialized] private Collider _tmpCol;
        [System.NonSerialized] private Transform _tmpHitTransform;
        [System.NonSerialized] private Rigidbody _tmpHitRb;
        [System.NonSerialized] private Rigidbody _tmpRb;
        [System.NonSerialized] private Vector3 _tmpPos;

#if UNITY_EDITOR
        bool running;
        public bool SetVel;
        public bool PrintDebugValues;
        public Vector3 DebugMoveSpeed = Vector3.zero;
        public bool DebugFreezeWheel = false;
        public float AccelTestLength = 4;
        public float DistResultPush;
        public float DistResultAccel;
        public float SpeedResultAccel;
        private Vector3 pushstartpoint;
        private Vector3 accelstartpoint;
        public void DEBUGPushCar()
        {
            pushstartpoint = CarRigid.position;
            CarRigid.velocity = CarRigid.transform.TransformDirection(DebugMoveSpeed);
            WheelRotationSpeedRPM = 0;
            WheelRotationSpeedRPS = 0;
            WheelRotationSpeedSurf = 0;
        }
        public void DEBUGAccelCar()
        {
            SGVControl.SendCustomEvent("setStepsSec");
            CarRigid.velocity = Vector3.zero;
            CarRigid.angularVelocity = Vector3.zero;
            SendCustomEventDelayedSeconds(nameof(DEBUGAccelCar_2), Time.fixedDeltaTime * 2);
            SGVControl.Revs = 0f;
            accelstartpoint = CarRigid.position;
            WheelRotationSpeedRPM = 0;
            WheelRotationSpeedRPS = 0;
            WheelRotationSpeedSurf = 0;
        }
        public void DEBUGAccelCar_2()
        {
            CarRigid.velocity = Vector3.zero;
            CarRigid.angularVelocity = Vector3.zero;
            SendCustomEventDelayedSeconds(nameof(DEBUGMeasureAccel), AccelTestLength);
            SGVControl.ACCELTEST = true;
            SGVControl.Revs = 0f;
            accelstartpoint = CarRigid.position;
            WheelRotationSpeedRPM = 0;
            WheelRotationSpeedRPS = 0;
            WheelRotationSpeedSurf = 0;
        }
        private bool boolRevingUp;
        private int RevUpCount;
        public float RevUpResult;
        public float RPMTestTarget = 500;
        public void DEBUGAccelCar_Revup()
        {
            CarRigid.velocity = Vector3.zero;
            CarRigid.angularVelocity = Vector3.zero;
            SendCustomEventDelayedSeconds(nameof(DEBUGAccelCar_Revup_2), Time.fixedDeltaTime * 2);
            SGVControl.Revs = 0f;
            accelstartpoint = CarRigid.position;
            WheelRotationSpeedRPM = 0;
            WheelRotationSpeedRPS = 0;
            WheelRotationSpeedSurf = 0;
        }
        public void DEBUGAccelCar_Revup_2()
        {
            CarRigid.velocity = Vector3.zero;
            CarRigid.angularVelocity = Vector3.zero;
            SGVControl.ACCELTEST = true;
            SGVControl.Revs = 0f;
            accelstartpoint = CarRigid.position;
            WheelRotationSpeedRPM = 0;
            WheelRotationSpeedRPS = 0;
            WheelRotationSpeedSurf = 0;
            boolRevingUp = true;
            RevUpCount = 0;
        }
        public void DEBUGMeasureAccel()
        {
            SGVControl.ACCELTEST = false;
            DistResultAccel = Vector3.Distance(accelstartpoint, CarRigid.position);
            SpeedResultAccel = CarRigid.velocity.magnitude;
        }
#endif
        void Start()
        {
            _selfTransform = transform;
            _cosMaxParkingIncline = Mathf.Cos(MaxParkingIncline * Mathf.Deg2Rad);
            _surfaceColliderLast = null;
            TankMode = SGVControl.TankMode;
            SkidSound_Min_THREEQUARTER = SkidSound_Min * .75f;
            SkidSound_Min_TWOTHRID = SkidSound_Min * .66f;

            _skidPackedLastSent = 0;
            _skidWasSkiddingLastSent = false;
            _lastNetSkidUpdateTime = Time.time;
            _skidLevelOwner = 0;
            WheelRenderer = SGVControl.MainObjectRenderer;
            if (!WheelRenderer)
            {
                SaccEntity EC = SGVControl.EntityControl;
                WheelRenderer = (Renderer)EC.GetComponentInChildren(typeof(Renderer));
                Debug.LogWarning(EC.gameObject.name + "'s SaccGroundVehicle's 'Main Object Renderer' is not set");
            }
            WheelDiameter = WheelRadius * 2f;
            WheelCircumference = WheelDiameter * Mathf.PI;
            WheelCircumferenceInv = WheelCircumference > 0.0001f ? (1f / WheelCircumference) : 0f;
            GearRatio = _GearRatio;
            if (SurfaceType_SkidSounds.Length > 0)
            {
                if (SurfaceType_SkidSounds[0])
                { SkidSound = SurfaceType_SkidSounds[0]; }
            }
            SurfaceType_SkidParticlesEM = new ParticleSystem.EmissionModule[SurfaceType_SkidParticles.Length];
            for (int i = 0; i < SurfaceType_SkidParticles.Length; i++)
            {
                if (SurfaceType_SkidParticles[i])
                { SurfaceType_SkidParticlesEM[i] = SurfaceType_SkidParticles[i].emission; }
            }
            if (SurfaceType_SkidParticles.Length > 0)
            {
                if (SurfaceType_SkidParticles[0])
                {
                    SkidParticle = SurfaceType_SkidParticles[0];
                    SkidParticleEM = SurfaceType_SkidParticlesEM[0];
                }
            }
            DisableEffects = SurfaceType_SkidSounds.Length == 0 && SurfaceType_SkidParticles.Length == 0;
#if UNITY_EDITOR
            running = true;
#endif
        }
        public void ChangeSurface()
        {
            if (SurfaceType < 0) { return; }
            CurrentGrip = Grip * SurfaceType_Grips[SurfaceType];
            CurrentWheelSlowDown = SurfaceType_Slowdown[SurfaceType] * .01f;//*.01 to offset removed *deltatime 
            CurrentNumParticles = SurfaceType_SkidParticles_Amount[SurfaceType];
            StopSkidSound();
            if (SurfaceType < SurfaceType_SkidSounds.Length)
            {
                if (SurfaceType_SkidSounds[SurfaceType])
                {
                    SkidSound = SurfaceType_SkidSounds[SurfaceType];
                }
            }
            else
            {
                SkidSound = null;
            }

            StopSkidParticle();
            if (SurfaceType < SurfaceType_SkidParticles.Length)
            {
                if (SurfaceType_SkidParticles[SurfaceType])
                {
                    SkidParticle = SurfaceType_SkidParticles[SurfaceType];
                    SkidParticleEM = SurfaceType_SkidParticlesEM[SurfaceType];
                }
            }
            else
            {
                SkidParticle = null;
            }
        }
        public void Wheel_FixedUpdate()
        {
            // Defensive: if this event is triggered multiple times in the same physics step
            // (e.g., duplicated delayed/event loops), applying suspension/grip twice will explode.
            if (Mathf.Abs(Time.fixedTime - _lastWheelFixedTime) < 0.00001f) { return; }
            _lastWheelFixedTime = Time.fixedTime;

            // If the parent vehicle is sleeping (distant/inactive), do not apply suspension/grip forces.
            // This avoids wheels fighting rigidbody sleep/zeroing during rapid enable/disable cycles.
            if (Sleeping || (SGVControl != null && SGVControl.Sleeping)) { return; }
            Suspension();
            WheelPhysics();
        }

        [System.NonSerialized] private float _lastWheelFixedTime = -999f;
        float gripLast;
        private void Suspension()
        {
            _tmpCompression = 0f;
            _tmpWpPos = WheelPoint.position;
            _tmpWpUp = WheelPoint.up;
            _tmpRayOrigin = _tmpWpPos + _tmpWpUp * ExtraRayCastDistance;
            if (Physics.Raycast(_tmpRayOrigin, -_tmpWpUp, out SusOut, SuspensionDistance + ExtraRayCastDistance, WheelLayers, QueryTriggerInteraction.Ignore))
            {
                _tmpFixedDT = Time.fixedDeltaTime;
                // If the hit collider changes (or the contact point jumps a long distance),
                // (SusOut.point - GroundPointLast)/dt can explode and inject massive forces.
                // Treat these cases as a fresh contact and use rigidbody point velocity instead.
                _tmpCol = SusOut.collider;
                bool colliderChanged = (_tmpCol != _surfaceColliderLast);
                if (colliderChanged)
                {
                    _surfaceColliderLast = _tmpCol;
                    CheckSurface();
                }

                Vector3 gpDelta = SusOut.point - GroundPointLast;
                // 1 meter jump threshold is generous; normal suspension motion is much smaller.
                bool resetGroundPointHistory = colliderChanged || !Grounded || (gpDelta.sqrMagnitude > 1f);
                if (resetGroundPointHistory)
                {
                    GroundPointLast = SusOut.point;
                    GetTouchingTransformSpeed();
                    PointVelocity = CarRigid.GetPointVelocity(SusOut.point) - LastTouchedTransform_Speed;
                    compressionLast = 0f;
                }
                else
                {
                    _tmpPointVel = gpDelta / _tmpFixedDT;
                    GroundPointLast = SusOut.point;
                    GetTouchingTransformSpeed();
                    PointVelocity = _tmpPointVel - LastTouchedTransform_Speed;
                }
                Grounded = true;
                //SusDirection is closer to straight up the slower vehicle is moving, so that it can stop on slopes
                _tmpUpDot = Vector3.Dot(SusOut.normal, Vector3.up);
                if (_tmpUpDot > _cosMaxParkingIncline && !SGVControl.Bike_AutoSteer)
                { SusDirection = Vector3.Lerp(Vector3.up, SusOut.normal, (SGVControl.VehicleSpeed / 1f)); }
                else
                { SusDirection = SusOut.normal; }

#if UNITY_EDITOR
                // make changing 'grip' value work instantly in editor play mode
                if (gripLast != Grip)
                {
                    SurfaceType = -1;
                    gripLast = Grip;
                }
#endif
                // Surface type is already updated above when collider changes.
                //SUSPENSION//
                _tmpCompression = 1f - ((SusOut.distance - ExtraRayCastDistance) / SuspensionDistance);
                //Spring force: More compressed = more force
                _tmpSpringForce = SusDirection * _tmpCompression * SpringForceMulti * _tmpFixedDT;
                _tmpDamping = _tmpCompression - compressionLast;
                if (_tmpDamping > 0)
                {
                    _tmpDamping *= Damping_Bump;
                }
                else
                {
                    _tmpDamping *= Damping_Rebound;
                }
                compressionLast = _tmpCompression;
                //Damping force: The more the difference in compression between updates, the more force
                _tmpDampingForce = SusDirection * _tmpDamping/*  * Vector3.Dot(SusOut.normal, WheelPoint.up) */;
                //these are added together, but both contain deltatime, potential deltatime problem source?
                SusForce = _tmpSpringForce + _tmpDampingForce;//The total weight on this suspension

                if (SusForce.magnitude / _tmpFixedDT > MaxSusForce)
                {
                    SusForce = SusForce.normalized * MaxSusForce * _tmpFixedDT;
                }

                if (!DebugDisableSuspensionForces)
                {
                    Vector3 susDV = SusForce;
                    if (Safety_ForceSanityChecks)
                    {
                        if (!_IsFiniteVec3(susDV))
                        {
                            ResetAfterTeleport();
                            return;
                        }
                        susDV = _ClampDeltaV(susDV, Safety_MaxSuspensionDeltaV);
                    }
                    _tmpSusDot = Vector3.Dot(_tmpWpUp, susDV);
                    if (_tmpSusDot > 0)// don't let the suspension force push the car down
                    { CarRigid.AddForceAtPosition(susDV, _tmpWpPos, ForceMode.VelocityChange); }
                }

                //set wheel's visual position
                if (SusOut.distance > ExtraRayCastDistance)
                {
                    WheelVisual.position = SusOut.point + (_tmpWpUp * WheelRadius);
                    if (WheelVisual_Ground) { WheelVisual_Ground.position = SusOut.point; }
                }
                else
                {
                    WheelVisual.position = _tmpWpPos + (_tmpWpUp * WheelRadius);
                    if (WheelVisual_Ground) { WheelVisual_Ground.position = _tmpWpPos; }
                }
                //END OF SUSPENSION//
                //GRIP//
                //Wheel's velocity vector projected to the normal of the ground
                WheelGroundUp = Vector3.ProjectOnPlane(SusOut.normal, WheelPoint.right).normalized;
#if UNITY_EDITOR
                ContactPoint = SusOut.point;
#endif
            }
            else
            {
                //wheel not touching ground
                if (SkidSoundPlayingLast) { StopSkidSound(); }
                if (SkidParticlePlayingLast) { StopSkidParticle(); }
                WheelVisual.position = _tmpWpPos - (_tmpWpUp * (SuspensionDistance - WheelRadius));
                if (WheelVisual_Ground) { WheelVisual_Ground.position = _tmpWpPos - (_tmpWpUp * (SuspensionDistance)); }
                SusForce = Vector3.zero;
                Grounded = false;
                compressionLast = 0f;
            }
        }
        RaycastHit SusOut;
        Vector3 SusForce;
        Vector3 WheelGroundUp = Vector3.up;
        Vector3 GroundPointLast;
        Vector3 PointVelocity;
        private void WheelPhysics()
        {
            _tmpDeltaTime = Time.fixedDeltaTime;
            _tmpForwardSpeed = 0f;
            _tmpForwardSideRatio = 0f;
            _tmpForceUsed = 0f;
            _tmpForwardSlip = 0f;
            _tmpWheelRotationSpeedSurfPrev = WheelRotationSpeedSurf;

            _tmpWpForward = WheelPoint.forward;
            _tmpWpRight = WheelPoint.right;

            if (IsDriveWheel && !GearNeutral)
            {
                _tmpEngineRevs = SGVControl.Revs;
                WheelRotationSpeedRPM = Mathf.Lerp(WheelRotationSpeedRPM, _tmpEngineRevs * _GearRatio, 1 - Mathf.Pow(0.5f, (1f - Clutch) * ClutchStrength));
                WheelRotationSpeedRPS = WheelRotationSpeedRPM * (1f / 60f);
                WheelRotationSpeedSurf = WheelCircumference * WheelRotationSpeedRPS;
            }

#if UNITY_EDITOR
            DistResultPush = Vector3.Distance(pushstartpoint, CarRigid.position);
            if (SetVel) CarRigid.velocity = DebugMoveSpeed;
            if (DebugFreezeWheel)
            {
                WheelRotationSpeedRPM = 0;
                WheelRotationSpeedRPS = 0;
                WheelRotationSpeedSurf = 0;
            }
            if (boolRevingUp)
            {
                RevUpCount++;
                if (WheelRotationSpeedRPM > RPMTestTarget)
                {
                    boolRevingUp = false;
                    RevUpResult = Time.fixedDeltaTime * (float)RevUpCount;
                    SGVControl.ACCELTEST = false;
                }
            }
#endif
            if (Grounded)
            {
                //Wheel's velocity vector projected to be only forward/back
                _tmpWheelForwardSpeed = PointVelocity - Vector3.Project(PointVelocity, _tmpWpRight);
                _tmpWheelForwardSpeed -= WheelGroundUp * Vector3.Dot(_tmpWheelForwardSpeed, WheelGroundUp);

                _tmpForwardSpeedAbs = _tmpWheelForwardSpeed.magnitude;
                _tmpForwardSpeed = _tmpForwardSpeedAbs;
                if (Vector3.Dot(_tmpWheelForwardSpeed, _tmpWpForward) < 0f)
                { _tmpForwardSpeed = -_tmpForwardSpeed; }

                _tmpForwardSlip = _tmpForwardSpeed - WheelRotationSpeedSurf;
                //How much the wheel is slipping (difference between speed of wheel rotation at it's surface, and the speed of the ground beneath it), as a vector3
                _tmpForwardTangent = _tmpWpForward - SusOut.normal * Vector3.Dot(_tmpWpForward, SusOut.normal);
                _tmpForwardTangentSqrMag = _tmpForwardTangent.sqrMagnitude;
                _tmpForwardLen = 0f;
                if (_tmpForwardTangentSqrMag > 0.00000001f)
                {
                    _tmpForwardTangent *= (1f / Mathf.Sqrt(_tmpForwardTangentSqrMag));
                    _tmpForwardLen = Mathf.Abs(_tmpForwardSlip);
                }
                else
                {
                    _tmpForwardTangent = Vector3.zero;
                    _tmpForwardLen = 0f;
                }
                _tmpForwardSkid = _tmpForwardTangent * _tmpForwardSlip;

                // Side skid: remove forward component, then remove normal component
                _tmpSideSkid = PointVelocity;
                _tmpWfSqr = _tmpWheelForwardSpeed.sqrMagnitude;
                if (_tmpWfSqr > 0.00000001f)
                {
                    _tmpSideSkid -= _tmpWheelForwardSpeed * (Vector3.Dot(PointVelocity, _tmpWheelForwardSpeed) / _tmpWfSqr);
                }
                _tmpSideSkid -= SusOut.normal * Vector3.Dot(_tmpSideSkid, SusOut.normal);

                _tmpDeltaSurf = _tmpWheelRotationSpeedSurfPrev - _tmpForwardSpeed;
                _tmpSkidVec = _tmpWpForward * _tmpDeltaSurf + _tmpSideSkid;
                SkidLength = _tmpSkidVec.magnitude;

                //add both skid axis together to get total 'skid'
                _tmpFullSkid = _tmpSideSkid + _tmpForwardSkid;
                _tmpFullSkidSqrMag = _tmpFullSkid.sqrMagnitude;
                _tmpFullSkidMag = 0f;
                if (_tmpFullSkidSqrMag > 0.00000001f)
                {
                    _tmpFullSkidMag = Mathf.Sqrt(_tmpFullSkidSqrMag);
                }
                //find out how much of the skid is on the forward axis 
                if (_tmpFullSkidMag > 0.0001f)
                {
                    if (SkidRatioMode == 0)
                    {
                        // (ForwardSkid/|Full|) dot (Full/|Full|) == (ForwardSkid dot Full) / |Full|^2
                        _tmpForwardSideRatio = Vector3.Dot(_tmpForwardSkid, _tmpFullSkid) / _tmpFullSkidSqrMag;
                    }
                    else
                    {
                        //these might produce different/more arcadey feel idk
                        _tmpSideLen = _tmpSideSkid.magnitude;
                        _tmpFullLen = _tmpForwardLen + _tmpSideLen;
                        if (SkidRatioMode == 1)
                        {
                            if (_tmpFullLen > 0.0001f) { _tmpForwardSideRatio = _tmpForwardLen / _tmpFullLen; }
                            else { _tmpForwardSideRatio = 0f; }
                        }
                        else
                        {
                            _tmpForwardSideRatio = _tmpForwardLen / _tmpFullSkidMag;
                        }
                    }
                }
                _tmpGripForce3 = Vector3.zero;
                //SusForce has deltatime built in
                _tmpSusForceMag = SusForce.magnitude / _tmpDeltaTime;
                _tmpMaxGrip = (_tmpSusForceMag * CurrentGrip);
                _tmpMaxGripLat = _tmpMaxGrip * LateralGrip;
                _tmpGripForceForward = Vector3.zero;
                _tmpGripForceLat = Vector3.zero;
                _tmpRollDot = Vector3.Dot(_selfTransform.up, SusOut.normal);
                if (_tmpRollDot < 0f) { _tmpRollDot = 0f; }
                _tmpWheelRollGrip = Mathf.Pow(_tmpRollDot, WheelRollGrip_Power);
                if (_tmpWheelRollGrip < .3f) { _tmpWheelRollGrip = .3f; }

                if (SeparateLongLatGrip)
                {
                    _tmpEvalSkid = (_tmpMaxGrip > 0.0001f) ? (_tmpForwardLen / _tmpMaxGrip) : 0f;
                    _tmpGripPc = GripCurve.Evaluate(_tmpEvalSkid);
                    if (_tmpForwardLen > 0.0001f)
                    {
                        _tmpGripForceForward = _tmpForwardSkid * (-(_tmpGripPc * _tmpMaxGrip) / _tmpForwardLen);
                    }
                    else
                    {
                        _tmpGripForceForward = Vector3.zero;
                    }

                    _tmpSideLen = _tmpSideSkid.magnitude;
                    _tmpEvalSkidLat = (_tmpMaxGripLat > 0.0001f) ? (_tmpSideLen / _tmpMaxGripLat) : 0f;
                    _tmpGripPcLat = GripCurveLateral.Evaluate(_tmpEvalSkidLat);
                    if (_tmpSideLen > 0.0001f)
                    {
                        _tmpGripForceLat = _tmpSideSkid * (-(_tmpGripPcLat * _tmpMaxGripLat) / _tmpSideLen);
                    }
                    else
                    {
                        _tmpGripForceLat = Vector3.zero;
                    }
                    _tmpGripForce3 = (_tmpGripForceForward + _tmpGripForceLat) * _tmpDeltaTime;
                    _tmpSkidVec = Vector3.Slerp(_tmpGripForceLat, _tmpGripForceForward, _tmpForwardSideRatio) * _tmpDeltaTime;
                    _tmpGripForce3 = Vector3.Lerp(_tmpSkidVec, _tmpGripForce3, LongLatSeparation) * _tmpWheelRollGrip;
                }
                else
                {
                    _tmpEvalSkid = (_tmpMaxGrip > 0.0001f) ? (_tmpFullSkidMag / _tmpMaxGrip) : 0f;
                    _tmpGripPc = GripCurve.Evaluate(_tmpEvalSkid);
                    _tmpEvalSkidLat = (_tmpMaxGripLat > 0.0001f) ? (_tmpFullSkidMag / _tmpMaxGripLat) : 0f;
                    _tmpGripPcLat = GripCurveLateral.Evaluate(_tmpEvalSkidLat);
                    if (_tmpFullSkidMag > 0.0001f)
                    {
                        _tmpInvFull = 1f / _tmpFullSkidMag;
                        _tmpGripForceForward = _tmpFullSkid * (-(_tmpGripPc * _tmpMaxGrip) * _tmpInvFull);
                        _tmpGripForceLat = _tmpFullSkid * (-(_tmpGripPcLat * _tmpMaxGripLat) * _tmpInvFull);
                    }
                    else
                    {
                        _tmpGripForceForward = Vector3.zero;
                        _tmpGripForceLat = Vector3.zero;
                    }
                    _tmpGripForce3 = Vector3.Lerp(_tmpGripForceLat, _tmpGripForceForward, _tmpForwardSideRatio) * _tmpDeltaTime * _tmpWheelRollGrip;
                }
                _tmpGripForce3 *= GripGain;
                _tmpPvSqr = PointVelocity.sqrMagnitude;
                if (_tmpPvSqr > 0.00000001f)
                {
                    _tmpPvMag = Mathf.Sqrt(_tmpPvSqr);
                    _tmpNumWheels = SGVControl.NumWheels;
                    if (_tmpNumWheels < 1) { _tmpNumWheels = 1; }
                    _tmpGripClamp = _tmpPvMag / _tmpNumWheels;
                    _tmpGfMag = _tmpGripForce3.magnitude;
                    if (_tmpGfMag > _tmpGripClamp)
                    {
                        _tmpGripForce3 *= (_tmpGripClamp / _tmpGfMag);
                    }
                }

                if (DebugDisableGripForces)
                {
                    _tmpGripForce3 = Vector3.zero;
                }
                else
                {
                    if (Safety_ForceSanityChecks)
                    {
                        if (!_IsFiniteVec3(_tmpGripForce3))
                        {
                            ResetAfterTeleport();
                            return;
                        }
                        _tmpGripForce3 = _ClampDeltaV(_tmpGripForce3, Safety_MaxGripDeltaV);
                    }

                    if (WheelForceApplyPoint)
                        CarRigid.AddForceAtPosition(_tmpGripForce3, WheelForceApplyPoint.position, ForceMode.VelocityChange);
                    else
                        CarRigid.AddForceAtPosition(_tmpGripForce3, SusOut.point, ForceMode.VelocityChange);
                }
                if (_tmpForwardSpeedAbs > 0.0001f)
                {
                    _tmpForceUsed = Vector3.Dot(_tmpWheelForwardSpeed, _tmpGripForce3) / _tmpForwardSpeedAbs;
                }
                else
                {
                    _tmpForceUsed = 0f;
                }
#if UNITY_EDITOR
                ForceVector = _tmpGripForce3;
                ForceUsedDBG = _tmpForceUsed;
#endif
                if (IsDriveWheel && !TankMode && !GearNeutral)
                    WheelRotationSpeedSurf = Mathf.Lerp(WheelRotationSpeedSurf, _tmpForwardSpeed, Clutch);
                else
                    WheelRotationSpeedSurf = _tmpForwardSpeed;
                // I don't know why these changes to WheelRotationSpeedSurf don't need delta time multiplied in. Doing so makes lower dt = slower car & worse brakes
                //wheels slow down due to ?friction
                WheelRotationSpeedSurf = Mathf.Lerp(WheelRotationSpeedSurf, 0, 1 - Mathf.Pow(0.5f, CurrentWheelSlowDown));
            }
            //brake
            if (Brake + HandBrake > 0)
            {
                float prevSurf = WheelRotationSpeedSurf;
                float engineWeight = Mathf.Lerp(Brake_EngineWeight, 1, Clutch);
                WheelRotationSpeedSurf = Mathf.MoveTowards(WheelRotationSpeedSurf, 0f, (BrakeStrength * Brake) / engineWeight);
                WheelRotationSpeedSurf = Mathf.Lerp(WheelRotationSpeedSurf, 0f, HandBrake / engineWeight);
                if (IsDriveWheel && !GearNeutral)
                {
                    float prevRPM = (prevSurf * WheelCircumferenceInv) * 60f;
                    float curRPM = (WheelRotationSpeedSurf * WheelCircumferenceInv) * 60f;
                    float brakeUsedForce = prevRPM - curRPM;
                    bool reversing = _GearRatio < 0;
                    float reverseMul = reversing ? -1f : 1f;
                    SGVControl.EngineForceUsed += _tmpDeltaTime * brakeUsedForce * EngineBrakeStrength * reverseMul * (1f - Clutch);
                }
            }
            WheelRotationSpeedRPS = WheelRotationSpeedSurf * WheelCircumferenceInv;
            WheelRotationSpeedRPM = WheelRotationSpeedRPS * 60f;
            // adjust engine speed
            if (IsDriveWheel && !GearNeutral)
            {
                bool slowing = ((_tmpForwardSlip < 0 && (_GearRatio > 0)) || ((_tmpForwardSlip > 0) && (_GearRatio < 0)));
                bool gearbackwards = Mathf.Sign(_tmpForwardSpeed) != Mathf.Sign(GearRatio);
                // (slowing ? 1 : -(1f - Clutch)) means use clutch if speeding up the engine, but don't use clutch if slowing down the engine.
                // because clutch was already used in the input in the slowing down case.
                // if removed, using the clutch can give a speed boost since the engine doesn't slow down by the correct amount relative to force produced.
                float ThisEngineForceUsed = Mathf.Abs(_tmpForceUsed) * Mathf.Abs(_GearRatio) * EngineInfluence *
                (slowing ?
                    // gearbackwards covers an edge case - handbraking while revs are at zero while sliding backwards (without it, you can't rev up, even though clutch is pressed (drive wheels))
                    gearbackwards ? (1f - Clutch) : 1
                    : -(1f - Clutch));
                SGVControl.EngineForceUsed += ThisEngineForceUsed;
            }
        }
        private void LateUpdate()
        {
            if (Sleeping) return;

            _tmpNow = Time.time;
            _tmpVisible = WheelRenderer && WheelRenderer.isVisible;

            if (IsOwner)
            {
                if (!_lateUpdateWasOwner) { _nextOwnerLateUpdateTime = 0f; }
                if (_tmpNow >= _nextOwnerLateUpdateTime)
                {
                    _nextOwnerLateUpdateTime = _tmpNow + _OwnerLateUpdateInterval;
                    OwnerLateUpdate();
                }
            }
            else
            {
                if (_lateUpdateWasOwner) { _nextNonOwnerLateUpdateTime = 0f; }
                if (_tmpNow >= _nextNonOwnerLateUpdateTime)
                {
                    _nextNonOwnerLateUpdateTime = _tmpNow + _NonOwnerLateUpdateInterval;
                    NonOwnerLateUpdate();
                }
            }
            _lateUpdateWasOwner = IsOwner;

            if (DisableEffects) { return; }

            _tmpShouldDoSkidFx = Grounded && !CurrentlyDistant;
            _tmpShouldStop = true;
            _tmpSkidVol = 0f;
            if (_tmpShouldDoSkidFx)
            {
                _tmpSkidVol = (SkidLength_Smoothed - SkidSound_Min) * SkidSound_VolumeIncrease;
                if (_tmpSkidVol > 1f) { _tmpSkidVol = 1f; }
                if (_tmpSkidVol > 0f) { _tmpShouldStop = false; }
            }

            if (_tmpShouldStop)
            {
                if (SkidSoundPlayingLast) { StopSkidSound(); }
                if (SkidParticlePlayingLast) { StopSkidParticle(); }
                return;
            }

            if (SkidSound)
            {
                if (!SkidSoundPlayingLast) { StartSkidSound(); }
                SkidSound.volume = _tmpSkidVol * SkidVolumeMulti;
                SkidSound.pitch = (SkidLength_Smoothed * SkidSound_PitchIncrease) + SkidSound_Pitch;
            }
            if (SkidParticle)
            {
                if (!SkidParticlePlayingLast) { StartSkidParticle(); }
                SkidParticleEM.rateOverTime = SkidLength_Smoothed * CurrentNumParticles;
            }
        }

        private const float _OwnerLateUpdateInterval = 1f / 60f;
        private const float _NonOwnerLateUpdateInterval = 1f / 30f;
        [System.NonSerialized] private float _nextOwnerLateUpdateTime;
        [System.NonSerialized] private float _nextNonOwnerLateUpdateTime;
        [System.NonSerialized] private bool _lateUpdateWasOwner;

        private void OwnerLateUpdate()
        {
            // Sync is only used for skid effects; if they're disabled, don't spend bandwidth.
            float effectiveSyncInterval = SyncInterval;
            Vector3 center = (SGVControl && SGVControl.VehicleTransform) ? SGVControl.VehicleTransform.position : (_selfTransform ? _selfTransform.position : transform.position);

            // Avoid per-wheel GetPlayers() scans: reuse the per-vehicle proximity cache when available.
            bool anyOtherPlayerNear = false;
            if (SGVControl != null)
            {
                anyOtherPlayerNear = SGVControl.CachedAnyOtherPlayerNearWheels;
            }
            else
            {
                _UpdateAnyOtherPlayerNearCache(center);
                anyOtherPlayerNear = _cachedAnyOtherPlayerNear;
            }

            if (!anyOtherPlayerNear)
            {
                effectiveSyncInterval = Mathf.Max(effectiveSyncInterval, 1f);
            }

            if (!DisableEffects)
            {
                // Coarse skid level prevents continuous resync spam during long burnouts.
                byte packedNow = ComputeSkidLevel(SkidLength);
                bool skiddingNow = packedNow != 0;

                bool shouldSend = false;

                // Always send a stop packet promptly when we transition out of skidding.
                if (_skidWasSkiddingLastSent && !skiddingNow)
                {
                    packedNow = 0;
                    shouldSend = true;
                }
                else
                {
                    if (packedNow != _skidPackedLastSent)
                    {
                        // Only rate-limit while actively skidding (or just after).
                        if (_tmpNow - lastSync > effectiveSyncInterval) { shouldSend = true; }
                    }
                    else if (skiddingNow)
                    {
                        // Keepalive so remotes don't stale-stop during long steady burnouts.
                        if ((_tmpNow - lastSync) > _SkidNetKeepAliveSeconds) { shouldSend = true; }
                    }
                }

                if (shouldSend)
                {
                    lastSync = _tmpNow;
                    SkidPacked = packedNow;
                    _skidPackedLastSent = packedNow;
                    _skidWasSkiddingLastSent = skiddingNow;
                    //RequestSerialization();
                }
            }
            SkidLength_Smoothed = SkidLength;
            if (_tmpVisible) { RotateWheelOwner(); }
        }

        private void NonOwnerLateUpdate()
        {
            if (_tmpVisible)
            {
                RotateWheelOther();
                Suspension_VisualOnly();
            }
        }
        public void PlayerEnterVehicle()
        {
            SkidVolumeMulti = SkidVolInVehicleMulti;
        }
        public void PlayerExitVehicle()
        {
            SkidVolumeMulti = 1;
        }
        public void StartSkidParticle()
        {
            if (SkidParticle)
            {
                SkidParticleEM.enabled = true;
            }
            SkidParticlePlayingLast = true;
        }
        public void StopSkidParticle()
        {
            if (SkidParticle)
            {
                SkidParticleEM.enabled = false;
            }
            SkidParticlePlayingLast = false;
        }

        public void StartSkidSound()
        {
            if (SkidSound)
            {
                SkidSound.gameObject.SetActive(true);
                SkidSound.time = Random.Range(0, SkidSound.clip.length);
            }
            SkidSoundPlayingLast = true;
        }
        public void StopSkidSound()
        {
            if (SkidSound)
            {
                SkidSound.gameObject.SetActive(false);
            }
            SkidSoundPlayingLast = false;
        }
        private void RotateWheelOwner()
        {
            if (WheelVisual_RotationSource)
                WheelVisual.localRotation = WheelVisual_RotationSource.WheelVisual.localRotation;
            else
            {
                WheelRotation += WheelRotationSpeedRPS * 360f * Time.deltaTime;
                Quaternion newrot = Quaternion.AngleAxis(WheelRotation, Vector3.right);
                WheelVisual.localRotation = newrot;
            }
        }
        private void RotateWheelOther()
        {
            if (WheelVisual_RotationSource)
                WheelVisual.localRotation = WheelVisual_RotationSource.WheelVisual.localRotation;
            else
            {
                _tmpOtherSpeed = SGVControl.VehicleSpeed;
                WheelRotationSpeedRPS = _tmpOtherSpeed * WheelCircumferenceInv;
                if (SGVControl.MovingForward) { WheelRotationSpeedRPS = -WheelRotationSpeedRPS; }
                WheelRotation += WheelRotationSpeedRPS * 360f * Time.deltaTime;
                _tmpWheelRotQuat = Quaternion.AngleAxis(WheelRotation, Vector3.right);
                WheelVisual.localRotation = _tmpWheelRotQuat;
            }
        }
        public void FallAsleep()
        {
            // When sleeping we stop effects AND reset cached contact state.
            // Otherwise, when waking up after being distant for a while, the first frame can see a huge
            // (SusOut.point - GroundPointLast) delta and apply extreme forces.
            ResetAfterTeleport();
            Sleeping = true;

            // Ensure remote clients stop skid FX promptly.
            if (IsOwner && !DisableEffects)
            {
                SkidLength_SkiddingLast = false;
                lastSync = Time.time;
                SkidPacked = 0;
                _skidPackedLastSent = 0;
                _skidWasSkiddingLastSent = false;
                //RequestSerialization();
            }
        }
        public void WakeUp()
        {
            // Reset cached suspension/contact state when resuming physics.
            // This prevents a one-frame impulse from stale GroundPointLast/compressionLast.
            ResetAfterTeleport();
            Sleeping = false;
        }
        private void GetTouchingTransformSpeed()
        {
            //Surface Movement
            _tmpCol = SusOut.collider;
            _tmpHitTransform = _tmpCol.transform;
            _tmpHitRb = _tmpCol.attachedRigidbody;

            if (_tmpHitTransform != LastTouchedTransform)
            {
                LastTouchedTransform = _tmpHitTransform;
                LastTouchedTransform_Position = _tmpHitTransform.position;
                LastTouchedTransform_RB = _tmpHitRb;
            }

            _tmpRb = LastTouchedTransform_RB;
            if (_tmpRb && !_tmpRb.isKinematic)
            {
                LastTouchedTransform_Speed = _tmpRb.GetPointVelocity(SusOut.point);
                return;
            }

            _tmpPos = LastTouchedTransform.position;
            LastTouchedTransform_Speed = (_tmpPos - LastTouchedTransform_Position) / Time.fixedDeltaTime;
            LastTouchedTransform_Position = _tmpPos;
        }
        private void CheckSurface()
        {
            //last character of surface object is its type
            string n = SusOut.collider.gameObject.name;
            if (string.IsNullOrEmpty(n)) { return; }
            int SurfLastChar = n[n.Length - 1];
            if (SurfLastChar >= '0' && SurfLastChar <= '9')
            {
                if (SurfaceType != SurfLastChar - '0')
                {
                    SurfaceType = SurfLastChar - '0';
                    ChangeSurface();
                }
            }
            else
            {
                if (SurfaceType != 0)
                {
                    SurfaceType = 0;
                    ChangeSurface();
                }
            }
        }
        private void Suspension_VisualOnly()
        {
            _tmpWpPos = WheelPoint.position;
            _tmpWpUp = WheelPoint.up;
            _tmpRayOrigin = _tmpWpPos + _tmpWpUp * ExtraRayCastDistance;
            if (Physics.Raycast(_tmpRayOrigin, -_tmpWpUp, out SusOut, SuspensionDistance + ExtraRayCastDistance, WheelLayers, QueryTriggerInteraction.Ignore))
            {
                // disabled because not worth it just to see wheels spinning properly on other people's cars when they're on a moving object
                // also needs code in other places to work properly (subtract from value from Velocity)
                // GetTouchingTransformSpeed();

                Grounded = true;
                // Only parse surface string if collider changed.
                _tmpCol = SusOut.collider;
                if (_tmpCol != _surfaceColliderLast)
                {
                    _surfaceColliderLast = _tmpCol;
                    CheckSurface();
                }
                if (SusOut.distance > ExtraRayCastDistance)
                {
                    WheelVisual.position = SusOut.point + (_tmpWpUp * WheelRadius);
                    if (WheelVisual_Ground) { WheelVisual_Ground.position = SusOut.point; }
                }
                else
                {
                    WheelVisual.position = _tmpWpPos + (_tmpWpUp * WheelRadius);
                    if (WheelVisual_Ground) { WheelVisual_Ground.position = _tmpWpPos; }
                }
            }
            else
            {
                StopSkidSound();
                StopSkidParticle();
                WheelVisual.position = _tmpWpPos - (_tmpWpUp * (SuspensionDistance - WheelRadius));
                if (WheelVisual_Ground) { WheelVisual_Ground.position = _tmpWpPos - (_tmpWpUp * (SuspensionDistance)); }
                Grounded = false;
            }
        }
        private void OnEnable()
        {
            ResetAfterTeleport();
            Sleeping = false;
        }

        private bool _IsFinite(float v)
        {
            return !(float.IsNaN(v) || float.IsInfinity(v));
        }

        private bool _IsFiniteVec3(Vector3 v)
        {
            return _IsFinite(v.x) && _IsFinite(v.y) && _IsFinite(v.z);
        }

        private Vector3 _ClampDeltaV(Vector3 dv, float max)
        {
            if (max <= 0f) { return dv; }
            float sqr = dv.sqrMagnitude;
            float maxSqr = max * max;
            if (sqr <= maxSqr) { return dv; }
            float mag = Mathf.Sqrt(sqr);
            if (mag <= 0.00000001f) { return Vector3.zero; }
            return dv * (max / mag);
        }
        public void UpdateOwner()
        {
            bool IsOwner_New = SGVControl.IsOwner;
            /*             if (IsOwner && !IsOwner_New)
                        {
                            //lose ownership
                        }
                        else  */
            // if (!IsOwner && IsOwner_New)
            // {
            //take ownership
            GroundPointLast = WheelPoint.position; // prevent 1 frame skid from last owned position
            // }
            IsOwner = IsOwner_New;
        }
        public void ResetGrip() { GroundPointLast = WheelPoint.position; }
        public void SyncSkid()
        {
            if (!SyncSkid_Running) { return; }
            if (IsOwner)
            {
                SyncSkid_Running = false;
                return;
            }

            // If the network stream stalls (missed final stop packet, owner left, etc), force skid to decay to zero.
            float now = Time.time;
            if ((now - _lastNetSkidUpdateTime) > _SkidNetStaleSeconds)
            {
                SkidLength = 0f;
            }

            if (SkidLength < SkidSound_Min_THREEQUARTER && SkidLength_Smoothed < SkidSound_Min_THREEQUARTER)
            {
                SkidLength = 0;
                SyncSkid_Running = false;
                return;
            }
            SkidLength_Smoothed = Mathf.SmoothStep(SkidLength_Smoothed, SkidLength, SkidLength_SmoothStep);
            SendCustomEventDelayedFrames(nameof(SyncSkid), 1);
        }
        public override void OnDeserialization()
        {
            _lastNetSkidUpdateTime = Time.time;
            SkidLength = SkidLevelToSkidLength(SkidPacked);

            if (SkidPacked != 0 && !SyncSkid_Running)
            {
                SyncSkid_Running = true;
                SyncSkid();
            }
        }

        private byte ComputeSkidLevel(float skidLen)
        {
            // Big bucket thresholds so the value stays stable during burnouts.
            if (skidLen <= SkidSound_Min_THREEQUARTER) { _skidLevelOwner = 0; return 0; }

            // Use thresholds relative to SkidSound_Min so tuning stays intuitive.
            float over = skidLen - SkidSound_Min;
            byte lvl;
            if (over <= 2f) { lvl = 1; }
            else if (over <= 7f) { lvl = 2; }
            else { lvl = 3; }

            _skidLevelOwner = lvl;
            return lvl;
        }

        private float SkidLevelToSkidLength(byte level)
        {
            if (level == 0) { return 0f; }
            if (level == 1) { return SkidSound_Min + 1.5f; }
            if (level == 2) { return SkidSound_Min + 5f; }
            return SkidSound_Min + 12f;
        }
#if UNITY_EDITOR
        [Header("Editor Only, use in play mode")]
        public bool ShowWheelForceLines;
        public float ForceUsedDBG;
        public Vector3 ContactPoint;
        public Vector3 ForceVector;
        private void OnDrawGizmosSelected()
        {
            if (ShowWheelForceLines)
            {
                Gizmos.DrawLine(ContactPoint, ContactPoint + ForceVector);
            }
            Matrix4x4 newmatrix = transform.localToWorldMatrix;
            Gizmos.matrix = Matrix4x4.TRS(newmatrix.GetPosition(), newmatrix.rotation, Vector3.one);// saccwheel does not respect scale
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube(Vector3.up * ExtraRayCastDistance * .5f, new Vector3(.01f, ExtraRayCastDistance, .01f));
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(-Vector3.up * SuspensionDistance * .5f, new Vector3(.01f, SuspensionDistance, .01f));
            if (WheelForceApplyPoint)
            {
                Gizmos.color = Color.magenta;
                Gizmos.DrawWireSphere(transform.InverseTransformDirection(WheelForceApplyPoint.position - transform.position), .04f);
            }
            Gizmos.color = Color.white;
            //flatten matrix and draw a sphere to draw a circle for the wheel
            newmatrix = transform.localToWorldMatrix;
            Vector3 scale = new Vector3(0, 1, 1);// Flatten the x scale to make disc + saccwheel does not respect object scale so 1
            Gizmos.matrix = Matrix4x4.TRS(newmatrix.GetPosition(), newmatrix.rotation, scale);
            // UnityEditor.Handles.DrawWireDisc(transform.position + transform.up * WheelRadius, transform.right, WheelRadius); not exposed
            if (running)
                Gizmos.DrawWireSphere(Quaternion.Inverse(transform.rotation) * (WheelVisual.position - transform.position), WheelRadius);
            else
                Gizmos.DrawWireSphere(Vector3.up * WheelRadius, WheelRadius);
        }
#endif
    }
}