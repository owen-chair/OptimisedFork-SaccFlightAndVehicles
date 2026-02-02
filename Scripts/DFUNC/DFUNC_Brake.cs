
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace SaccFlightAndVehicles
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class DFUNC_Brake : UdonSharpBehaviour
    {
        public UdonSharpBehaviour SAVControl;
        [Tooltip("Looping sound to play while brake is active")]
        public AudioSource Airbrake_snd;
        [Tooltip("Will Crash if not set")]
        public Animator BrakeAnimator;
        [Tooltip("Position the ground brake force will be applied at")]
        public Transform GroundBrakeForcePosition;
        [Tooltip("Because you have to hold the break, and the keyboardcontrols script can only send events, this option is here.")]
        public KeyCode KeyboardControl = KeyCode.B;
        [System.NonSerializedAttribute, UdonSynced(UdonSyncMode.None)] public float BrakeInput;
        private Rigidbody VehicleRigidbody;
        private bool HasAirBrake;
        public float AirbrakeStrength = 4f;
        public float GroundBrakeStrength = 6;
        [Tooltip("Water brake functionality requires that floatscript is being used")]
        public float WaterBrakeStrength = 1f;
        public bool NoPilotAlwaysGroundBrake = true;
        [Tooltip("Speed below which the ground break works meters/s")]
        public float GroundBrakeSpeed = 40f;
        //other functions can set this +1 to disable breaking
        [System.NonSerializedAttribute] public bool _DisableGroundBrake;
        [System.NonSerializedAttribute, FieldChangeCallback(nameof(DisableGroundBrake_))] public int DisableGroundBrake = 0;
        public int DisableGroundBrake_
        {
            set
            {
                _DisableGroundBrake = value > 0;
                DisableGroundBrake = value;
            }
            get => DisableGroundBrake;
        }
        [System.NonSerializedAttribute] public bool LeftDial = false;
        [System.NonSerializedAttribute] public int DialPosition = -999;
        [System.NonSerializedAttribute] public SaccEntity EntityControl;
        private float BrakeStrength;
        private int BRAKE_STRING = Animator.StringToHash("brake");
        private bool Braking;
        private bool Asleep;
        private bool BrakingLastFrame;
        private float LastDrag = 0;
        private float AirbrakeLerper;
        private float NonLocalActiveDelay;//this var is for adding a min delay for disabling for non-local users to account for lag
        private bool Selected;
        private bool IsOwner;
        private bool InVehicle;
        private float NextUpdateTime;
        private float _lastSentBrakeInput;
        private bool _hasSentBrakeInput;
        private bool _anyOtherPlayerNearLast;
        private float _nextNetSendTime;
        private float RotMultiMaxSpeedDivider;

        private const float _BrakeDeadzone = 0.02f;
        private const float _BrakeNetCooldownSeconds = 0.5f;

        // Distance-based throttling: if there are no other players within 50m, cap update rate to 1Hz.
        private const float _NearPlayerRadiusSqr = 50f * 50f;
        private const float _PlayerCacheRefreshSeconds = 0.5f;
        [System.NonSerialized] private VRCPlayerApi[] _playerCache;
        [System.NonSerialized] private float _nextPlayerCacheRefresh;
        [System.NonSerialized] private bool _cachedAnyOtherPlayerNear;

        // UdonSharp CPU: reuse temps instead of per-frame locals.
        private float _tmpNow;
        private float _tmpDeltaTime;
        private Vector3 _tmpCenter;
        private int _tmpPlayerCount;
        private bool _tmpAnyNear;
        private VRCPlayerApi _tmpPlayer;
        private Vector3 _tmpDp;
        private int _i;

        private float _tmpSpeed;
        private Vector3 _tmpCurrentVel;
        private bool _tmpTaxiing;
        private float _tmpKeyboardBrakeInput;
        private float _tmpVRBrakeInput;
        private float _tmpTrigger;

        private void _UpdateAnyOtherPlayerNearCache(Vector3 center)
        {
            _tmpNow = Time.time;
            if (_tmpNow < _nextPlayerCacheRefresh) { return; }
            _nextPlayerCacheRefresh = _tmpNow + _PlayerCacheRefreshSeconds;

            _tmpPlayerCount = VRCPlayerApi.GetPlayerCount();
            if (_tmpPlayerCount <= 0)
            {
                _cachedAnyOtherPlayerNear = false;
                return;
            }

            if (_playerCache == null || _playerCache.Length != _tmpPlayerCount)
            {
                _playerCache = new VRCPlayerApi[_tmpPlayerCount];
            }

            _playerCache = VRCPlayerApi.GetPlayers(_playerCache);

            _tmpAnyNear = false;
            for (_i = 0; _i < _playerCache.Length; _i++)
            {
                _tmpPlayer = _playerCache[_i];
                if (!Utilities.IsValid(_tmpPlayer)) { continue; }
                // This script runs on the owner; ignore local player.
                if (_tmpPlayer.isLocal) { continue; }

                _tmpDp = _tmpPlayer.GetPosition() - center;
                if (_tmpDp.sqrMagnitude <= _NearPlayerRadiusSqr)
                {
                    _tmpAnyNear = true;
                    break;
                }
            }
            _cachedAnyOtherPlayerNear = _tmpAnyNear;
        }
        public void SFEXT_L_EntityStart()
        {
            VehicleRigidbody = EntityControl.GetComponent<Rigidbody>();
            HasAirBrake = AirbrakeStrength != 0;
            RotMultiMaxSpeedDivider = 1 / (float)SAVControl.GetProgramVariable("RotMultiMaxSpeed");
            IsOwner = EntityControl.IsOwner;
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer != null && !EntityControl.IsOwner)
            { gameObject.SetActive(false); }
            else
            { gameObject.SetActive(true); }
            if (!GroundBrakeForcePosition) { GroundBrakeForcePosition = EntityControl.CenterOfMass; }
        }
        public void DFUNC_Selected()
        {
            Selected = true;
        }
        public void DFUNC_Deselected()
        {
            BrakeInput = 0;
            Selected = false;
        }
        public void SFEXT_O_PilotEnter()
        {
            InVehicle = true;
            if (!NoPilotAlwaysGroundBrake)
            {
                if ((bool)SAVControl.GetProgramVariable("Floating"))
                {
                    BrakeStrength = WaterBrakeStrength;
                }
                else if ((bool)SAVControl.GetProgramVariable("Taxiing"))
                {
                    BrakeStrength = GroundBrakeStrength;
                }
            }
        }
        public void SFEXT_O_PilotExit()
        {
            InVehicle = false;
            BrakeInput = 0;
            _hasSentBrakeInput = false;
            _lastSentBrakeInput = 0f;
            RequestSerialization();
            Selected = false;
            if (!NoPilotAlwaysGroundBrake)
            { BrakeStrength = 0; }
            if (Airbrake_snd)
            {
                Airbrake_snd.pitch = 0f;
                Airbrake_snd.volume = 0f;
            }
        }
        public void SFEXT_P_PassengerEnter()
        {
            InVehicle = true;
        }
        public void SFEXT_P_PassengerExit()
        {
            InVehicle = false;
            if (Airbrake_snd)
            {
                Airbrake_snd.pitch = 0f;
                Airbrake_snd.volume = 0f;
            }
        }
        public void SFEXT_G_Explode()
        {
            BrakeInput = 0;
            _hasSentBrakeInput = false;
            _lastSentBrakeInput = 0f;
            BrakeAnimator.SetFloat(BRAKE_STRING, 0);
        }
        public void SFEXT_O_TakeOwnership()
        {
            gameObject.SetActive(true);
            IsOwner = true;
        }
        public void SFEXT_O_LoseOwnership()
        {
            gameObject.SetActive(false);
            IsOwner = false;
        }
        public void EnableForAnimation()
        {
            if (!IsOwner)
            {
                if (Airbrake_snd) { Airbrake_snd.Play(); }
                gameObject.SetActive(true);
                NonLocalActiveDelay = 3;
            }
        }
        public void DisableForAnimation()
        {
            BrakeAnimator.SetFloat(BRAKE_STRING, 0);
            BrakeInput = 0;
            AirbrakeLerper = 0;
            if (Airbrake_snd)
            {
                Airbrake_snd.pitch = 0;
                Airbrake_snd.volume = 0;
            }
            gameObject.SetActive(false);
        }
        public void SFEXT_G_TouchDownWater()
        {
            BrakeStrength = WaterBrakeStrength;
        }
        public void SFEXT_G_TouchDown()
        {
            BrakeStrength = GroundBrakeStrength;
        }
        public void SFEXT_L_WakeUp() { Asleep = false; }
        public void SFEXT_L_FallAsleep() { Asleep = true; }
        private void Update()
        {
            _tmpDeltaTime = Time.deltaTime;
            if (IsOwner)
            {
                if (!Asleep)
                {
                    _tmpCenter = (EntityControl != null && EntityControl.CenterOfMass != null)
                        ? EntityControl.CenterOfMass.position
                        : transform.position;
                    _UpdateAnyOtherPlayerNearCache(_tmpCenter);

                    _tmpSpeed = (float)SAVControl.GetProgramVariable("Speed");
                    _tmpCurrentVel = (Vector3)SAVControl.GetProgramVariable("CurrentVel");
                    _tmpTaxiing = (bool)SAVControl.GetProgramVariable("Taxiing");
                    if ((bool)SAVControl.GetProgramVariable("Piloting"))
                    {
                        _tmpKeyboardBrakeInput = 0f;
                        _tmpVRBrakeInput = 0f;

                        if (Selected)
                        {
                            if (LeftDial)
                            { _tmpTrigger = Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryIndexTrigger"); }
                            else
                            { _tmpTrigger = Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryIndexTrigger"); }

                            _tmpVRBrakeInput = _tmpTrigger;
                        }

                        if (Input.GetKey(KeyboardControl))
                        {
                            _tmpKeyboardBrakeInput = 1f;
                        }
                        BrakeInput = Mathf.Max(_tmpVRBrakeInput, _tmpKeyboardBrakeInput);
                        if (BrakeInput < _BrakeDeadzone) { BrakeInput = 0f; }
                        if (_tmpTaxiing)
                        {
                            //ground brake checks if vehicle is on top of a rigidbody, and if it is, brakes towards its speed rather than zero
                            //does not work if owner of vehicle does not own the rigidbody 
                            Rigidbody gdhr = (Rigidbody)SAVControl.GetProgramVariable("GDHitRigidbody");
                            if (gdhr)
                            {
                                float RBSpeed = ((Vector3)SAVControl.GetProgramVariable("CurrentVel") - gdhr.velocity).magnitude;
                                if (BrakeInput > 0 && RBSpeed < GroundBrakeSpeed && !_DisableGroundBrake)
                                {
                                    Vector3 speed = (VehicleRigidbody.GetPointVelocity(GroundBrakeForcePosition.position) - gdhr.velocity).normalized;
                                    speed = Vector3.ProjectOnPlane(speed, EntityControl.transform.up);
                                    Vector3 BrakeForce = speed.normalized * BrakeInput * BrakeStrength * _tmpDeltaTime;
                                    if (speed.sqrMagnitude < BrakeForce.sqrMagnitude)
                                    { BrakeForce = speed; }
                                    VehicleRigidbody.AddForceAtPosition(-speed * BrakeInput * BrakeStrength * _tmpDeltaTime, GroundBrakeForcePosition.position, ForceMode.VelocityChange);
                                }
                            }
                            else
                            {
                                if (BrakeInput > 0 && _tmpSpeed < GroundBrakeSpeed && !_DisableGroundBrake)
                                {
                                    Vector3 speed = VehicleRigidbody.GetPointVelocity(GroundBrakeForcePosition.position);
                                    speed = Vector3.ProjectOnPlane(speed, EntityControl.transform.up);
                                    Vector3 BrakeForce = speed.normalized * BrakeInput * BrakeStrength * _tmpDeltaTime;
                                    if (speed.sqrMagnitude < BrakeForce.sqrMagnitude)
                                    { BrakeForce = speed; }//this'll stop the vehicle exactly
                                    VehicleRigidbody.AddForceAtPosition(-BrakeForce, GroundBrakeForcePosition.position, ForceMode.VelocityChange);
                                }
                            }
                        }
                        if (!HasAirBrake && !_tmpTaxiing)
                        {
                            BrakeInput = 0;
                        }
                        //remove the drag added last frame to add the new value for this frame
                        float extradrag = (float)SAVControl.GetProgramVariable("ExtraDrag");
                        float newdrag = (AirbrakeStrength * BrakeInput);
                        float dragtoadd = -LastDrag + newdrag;
                        extradrag += dragtoadd;
                        LastDrag = newdrag;
                        SAVControl.SetProgramVariable("ExtraDrag", extradrag);

                        //send events to other users to tell them to enable the script so they can see the animation
                        Braking = BrakeInput > 0f;

                        // Visual-only networking: if nobody is nearby, do not sync at all.
                        if (!_cachedAnyOtherPlayerNear)
                        {
                            _anyOtherPlayerNearLast = false;
                        }

                        bool becameNear = _cachedAnyOtherPlayerNear && !_anyOtherPlayerNearLast;
                        if (_cachedAnyOtherPlayerNear) { _anyOtherPlayerNearLast = true; }

                        bool canNet = _cachedAnyOtherPlayerNear && (Time.time >= _nextNetSendTime);
                        if (Braking)
                        {
                            if (!BrakingLastFrame)
                            {
                                if (Airbrake_snd && !Airbrake_snd.isPlaying) { Airbrake_snd.Play(); }
                                if (canNet)
                                {
                                    _nextNetSendTime = Time.time + _BrakeNetCooldownSeconds;
                                    SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.Others, nameof(EnableForAnimation));
                                    RequestSerialization();
                                    _hasSentBrakeInput = true;
                                    _lastSentBrakeInput = BrakeInput;
                                }
                            }

                            // If someone just came into range while braking, push state once.
                            if (becameNear && canNet)
                            {
                                _nextNetSendTime = Time.time + _BrakeNetCooldownSeconds;
                                SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.Others, nameof(EnableForAnimation));
                                RequestSerialization();
                                _hasSentBrakeInput = true;
                                _lastSentBrakeInput = BrakeInput;
                            }
                        }
                        else
                        {
                            if (BrakingLastFrame)
                            {
                                BrakeInput = 0;
                                if (canNet)
                                {
                                    _nextNetSendTime = Time.time + _BrakeNetCooldownSeconds;
                                    RequestSerialization();
                                    _hasSentBrakeInput = true;
                                    _lastSentBrakeInput = 0f;
                                }
                            }
                        }
                        if (AirbrakeLerper < .03 && BrakeInput < .03)
                        {
                            if (Airbrake_snd && Airbrake_snd.isPlaying) { Airbrake_snd.Stop(); }
                        }
                        BrakingLastFrame = Braking;
                    }
                    else
                    {
                        if (_tmpTaxiing)
                        {
                            //outside of vehicle, simpler version, ground brake always max
                            Rigidbody gdhr = null;
                            { gdhr = (Rigidbody)SAVControl.GetProgramVariable("GDHitRigidbody"); }
                            if (gdhr)
                            {
                                float RBSpeed = ((Vector3)SAVControl.GetProgramVariable("CurrentVel") - gdhr.velocity).magnitude;
                                if (RBSpeed < GroundBrakeSpeed && !_DisableGroundBrake)
                                {
                                    VehicleRigidbody.velocity = Vector3.MoveTowards(VehicleRigidbody.velocity, gdhr.GetPointVelocity(EntityControl.CenterOfMass.position), BrakeStrength * _tmpDeltaTime);
                                }
                            }
                            else
                            {
                                if (_tmpSpeed < GroundBrakeSpeed && !_DisableGroundBrake)
                                {
                                    VehicleRigidbody.velocity = Vector3.MoveTowards(VehicleRigidbody.velocity, Vector3.zero, BrakeStrength * _tmpDeltaTime);
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                //this object is enabled for non-owners only while animating
                NonLocalActiveDelay -= _tmpDeltaTime;
                if (NonLocalActiveDelay < 0 && AirbrakeLerper < 0.01)
                {
                    DisableForAnimation();
                    return;
                }
            }
            AirbrakeLerper = Mathf.Lerp(AirbrakeLerper, BrakeInput, 1 - Mathf.Pow(0.5f, 2f * _tmpDeltaTime));
            if (BrakeAnimator) { BrakeAnimator.SetFloat(BRAKE_STRING, AirbrakeLerper); }
            if (InVehicle && Airbrake_snd)
            {
                Airbrake_snd.pitch = AirbrakeLerper * .2f + .9f;
                Airbrake_snd.volume = AirbrakeLerper * Mathf.Min((float)SAVControl.GetProgramVariable("Speed") * RotMultiMaxSpeedDivider, 1);
            }
        }
    }
}