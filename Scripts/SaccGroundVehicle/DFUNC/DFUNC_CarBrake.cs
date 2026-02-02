
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace SaccFlightAndVehicles
{
    [DefaultExecutionOrder(900)]//before gearbox
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class DFUNC_CarBrake : UdonSharpBehaviour
    {
        public UdonSharpBehaviour SGVControl;
        public Animator BrakeAnimator;
        public SaccWheel[] BrakeWheels_Front;
        public SaccWheel[] BrakeWheels_Back;
        [Tooltip("Handbrake DOES NOT USE BrakeStrength")]
        public float BrakeStrength = 1f;
        [Range(0, 1)]
        public float Brake_FrontStrengthMulti = 1f;
        [Range(0, 1)]
        public float Brake_BackStrengthMulti = 1f;
        [Tooltip("Change keyboard brake strength to prevent skidding")]
        public float KeyboardBrakeMulti = 1f;
        [Tooltip("Because you have to hold the break, and the keyboardcontrols script can only send events, this option is here.")]
        public KeyCode KeyboardControl = KeyCode.S;
        // [Tooltip("If input is above this amount, input is clamped to max")]
        // public float UpperDeadZone = .9f;
        // [Tooltip("If input is bleow this amount, input is clamped to min")]
        // public float LowerDeadZone = .1f;
        private int AnimFloatName_STRING;
        [FieldChangeCallback(nameof(AnimFloatName))] public string _AnimFloatName = "brake";
        public string AnimFloatName
        {
            set
            {
                AnimFloatName_STRING = Animator.StringToHash(value);
                _AnimFloatName = value;
            }
            get => _AnimFloatName;
        }
        [Tooltip("FALSE:\n0=no brake, 1=movement speed - brake strength\nTRUE: 0=no brake, 1=wheel stopped")]
        public bool IsHandBrake = false;
        public bool EnableBrakeOnExit = true;
        [Header("For autoclutch")]
        public bool AutoClutch = true;
        public UdonSharpBehaviour GearBox;
        private bool ClutchOverrideLast = false;
        private string Brake_VariableName = "Brake";
        private string BrakeStrength_VariableName = "BrakeStrength";
        [System.NonSerializedAttribute] public bool LeftDial = false;
        [System.NonSerializedAttribute] public int DialPosition = -999;
        [System.NonSerializedAttribute] public SaccEntity EntityControl;
        private bool InVR = false;
        private bool DoFrontWheelBrakes = false;
        private bool DoBackWheelBrakes = false;
        private bool Selected = false;
        private bool Piloting = false;
        private bool Occupied = false;
        private bool BrakingLastFrame = false;
        private bool IsOwner = false;
        private float DeadZoneSize;
        private bool AnimatingBrake;
        private float _nextSyncTime;
        private float _lastSentBrakeInput;
        private bool _hasSentBrakeInput;
        private bool _anyOtherPlayerNearLast;
        private float _nextNetSendTime;

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
        private Vector3 _tmpCenter;
        private int _tmpPlayerCount;
        private bool _tmpAnyNear;
        private VRCPlayerApi _tmpPlayer;
        private Vector3 _tmpDp;
        private int _i;
        private float _tmpTrigger;
        private float _tmpVRBrakeInput;
        private float _tmpKeyboardBrakeInput;
        private float _tmpNewBrake;

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
                // Owner tick / sync runs on the owner; ignore local player.
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

        private void _MaybeRequestSerialization(float brakeInput)
        {
            if (!IsOwner) { return; }
            _tmpNow = Time.time;

            // Visual-only networking: if nobody is nearby, do not sync at all.
            if (!_cachedAnyOtherPlayerNear)
            {
                _anyOtherPlayerNearLast = false;
                return;
            }

            bool becameNear = !_anyOtherPlayerNearLast;
            _anyOtherPlayerNearLast = true;

            float prev = _hasSentBrakeInput ? _lastSentBrakeInput : 0f;
            bool wasBraking = prev > 0f;
            bool isBraking = brakeInput > 0f;
            bool edge = (wasBraking != isBraking);

            // If someone just came into range, push the current state once.
            if (becameNear) { edge = true; }

            if (!edge) { return; }
            if (_tmpNow < _nextNetSendTime) { return; }
            _nextNetSendTime = _tmpNow + _BrakeNetCooldownSeconds;

            RequestSerialization();
            _hasSentBrakeInput = true;
            _lastSentBrakeInput = brakeInput;
            _nextSyncTime = _tmpNow + _BrakeNetCooldownSeconds;
        }
        [UdonSynced, System.NonSerialized, FieldChangeCallback(nameof(BrakeInput))] public float _BrakeInput;
        public float BrakeInput
        {
            set
            {
                _BrakeInput = value;
                if (IsOwner)
                {
                    if (BrakeAnimator) { BrakeAnimator.SetFloat(AnimFloatName_STRING, value); }
                }
                else
                {
                    if (!AnimatingBrake)
                    {
                        AnimatingBrake = true;
                        AnimateBrake();
                    }
                }
            }
            get => _BrakeInput;
        }
        public void SFEXT_L_EntityStart()
        {
            AnimFloatName = _AnimFloatName;
            // DeadZoneSize = (1 - UpperDeadZone) + LowerDeadZone;
            InVR = EntityControl.InVR;
            IsOwner = (bool)SGVControl.GetProgramVariable("IsOwner");
            if (IsHandBrake)
            {
                Brake_VariableName = "HandBrake";
            }
            else
            {
                Brake_VariableName = "Brake";
            }
            if (BrakeWheels_Back.Length > 0) { DoBackWheelBrakes = true; }
            if (BrakeWheels_Front.Length > 0) { DoFrontWheelBrakes = true; }
            if (!DoFrontWheelBrakes && !DoBackWheelBrakes)
            { Debug.LogWarning("WARNING: DFUNC_CarBrake has no brakewheels set."); }
            if (EnableBrakeOnExit)
            {
                SetBrakeOne();
                if (IsOwner)
                {
                    gameObject.SetActive(true);
                    RequestSerialization();
                }
            }
            if (!IsHandBrake)
            {
                for (int i = 0; i < BrakeWheels_Front.Length; i++)
                { BrakeWheels_Front[i].SetProgramVariable(BrakeStrength_VariableName, BrakeStrength); }
                for (int i = 0; i < BrakeWheels_Back.Length; i++)
                { BrakeWheels_Back[i].SetProgramVariable(BrakeStrength_VariableName, BrakeStrength); }
            }
        }
        private void TurnOnOverrides()
        {
            if (AutoClutch && !ClutchOverrideLast)
            {
                GearBox.SetProgramVariable("ClutchOverride", (int)GearBox.GetProgramVariable("ClutchOverride") + 1);
                ClutchOverrideLast = true;
            }
            if (IsHandBrake && !BrakingLastFrame)
            {
                SGVControl.SetProgramVariable("HandBrakeOn", (int)SGVControl.GetProgramVariable("HandBrakeOn") + 1);
                BrakingLastFrame = true;
            }
        }
        private void TurnOffOverrides()
        {
            if (AutoClutch && ClutchOverrideLast)
            {
                GearBox.SetProgramVariable("ClutchOverride", (int)GearBox.GetProgramVariable("ClutchOverride") - 1);
                ClutchOverrideLast = false;
            }
            if (IsHandBrake && BrakingLastFrame)
            {
                SGVControl.SetProgramVariable("HandBrakeOn", (int)SGVControl.GetProgramVariable("HandBrakeOn") - 1);
                BrakingLastFrame = false;
            }
        }
        public void DFUNC_Selected()
        {
            Selected = true;
        }
        public void DFUNC_Deselected()
        {
            Selected = false;
            SetBrakeZero();
            TurnOffOverrides();
            _nextSyncTime = 0f;
            _hasSentBrakeInput = false;
            _lastSentBrakeInput = 0f;
            if (IsOwner) { RequestSerialization(); }
        }
        public void SFEXT_O_PilotEnter()
        {
            SetBrakeZero();
            Piloting = true;
            InVR = EntityControl.InVR;
            _nextSyncTime = 0f;
            _hasSentBrakeInput = false;
            _lastSentBrakeInput = 0f;
            if (IsOwner) { RequestSerialization(); }
        }
        public void SFEXT_O_PilotExit()
        {
            Selected = false;
            Piloting = false;
            if (EnableBrakeOnExit && !GrappleActive)
            {
                SetBrakeOne();
            }
            TurnOffOverrides();
        }
        public void SFEXT_G_PilotEnter()
        {
            Occupied = true;
            gameObject.SetActive(true);
        }
        public void SFEXT_G_PilotExit()
        {
            Occupied = false;
            AnimatingBrake = false;
            if (!IsOwner)
            {
                gameObject.SetActive(false);
                if (EnableBrakeOnExit)
                {
                    BrakeInput = 1;
                }
                else
                {
                    BrakeInput = 0;
                }
            }
        }
        private float BrakeMover;
        public void AnimateBrake()
        {
            if (AnimatingBrake)
            {
                BrakeMover = Mathf.MoveTowards(BrakeMover, _BrakeInput, 2 * Time.deltaTime);
                if (BrakeAnimator) { BrakeAnimator.SetFloat(AnimFloatName_STRING, BrakeMover); }
                if (BrakeMover == _BrakeInput)
                {
                    AnimatingBrake = false;
                }
                else
                {
                    SendCustomEventDelayedFrames(nameof(AnimateBrake), 1);
                }
            }
        }
        private void LateUpdate()
        {
            if (Piloting)
            {
                _tmpCenter = (EntityControl != null && EntityControl.CenterOfMass != null)
                    ? EntityControl.CenterOfMass.position
                    : transform.position;
                _UpdateAnyOtherPlayerNearCache(_tmpCenter);

                if (!InVR || Selected)
                {
                    if (LeftDial)
                    { _tmpTrigger = Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryIndexTrigger"); }
                    else
                    { _tmpTrigger = Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryIndexTrigger"); }

                    _tmpVRBrakeInput = _tmpTrigger;
                    /*         if (VRBrakeInput > LowerDeadZone)
                            {
                                float normalizedInput = Mathf.Min((VRBrakeInput - LowerDeadZone) * (1 / (VRBrakeInput - DeadZoneSize)), 1);
                                VRBrakeInput = LowerDeadZone + normalizedInput;
                            }
                            else
                            { VRBrakeInput = 0; } */
                    // if (VRBrakeInput > UpperDeadZone)
                    // { VRBrakeInput = 1f; }
                    // if (VRBrakeInput < LowerDeadZone)
                    // { VRBrakeInput = 0f; }

                    _tmpKeyboardBrakeInput = 0f;

                    if (Input.GetKey(KeyboardControl))
                    {
                        _tmpKeyboardBrakeInput = KeyboardBrakeMulti;
                    }
                    // if (VRBrakeInput < .1f) { VRBrakeInput = 0f; }//deadzone so there isnt constant brake applied
                    _tmpNewBrake = Mathf.Max(_tmpVRBrakeInput, _tmpKeyboardBrakeInput);
                    if (_tmpNewBrake < _BrakeDeadzone) { _tmpNewBrake = 0f; }
                    BrakeInput = _tmpNewBrake;
                    if (BrakeInput > 0f)
                    {
                        TurnOnOverrides();
                    }
                    else
                    {
                        TurnOffOverrides();
                    }
#if UNITY_EDITOR
                    if (!IsHandBrake)
                    {
                        for (int i = 0; i < BrakeWheels_Front.Length; i++)
                        { BrakeWheels_Front[i].SetProgramVariable(BrakeStrength_VariableName, BrakeStrength); }
                        for (int i = 0; i < BrakeWheels_Back.Length; i++)
                        { BrakeWheels_Back[i].SetProgramVariable(BrakeStrength_VariableName, BrakeStrength); }
                    }
#endif
                    if (DoFrontWheelBrakes)
                    {
                        for (_i = 0; _i < BrakeWheels_Front.Length; _i++)
                        {
                            BrakeWheels_Front[_i].SetProgramVariable(Brake_VariableName, BrakeInput * Brake_FrontStrengthMulti);
                        }
                    }
                    if (DoBackWheelBrakes)
                    {
                        for (_i = 0; _i < BrakeWheels_Back.Length; _i++)
                        {
                            BrakeWheels_Back[_i].SetProgramVariable(Brake_VariableName, BrakeInput * Brake_BackStrengthMulti);
                        }
                    }
                    _MaybeRequestSerialization(BrakeInput);
                }
            }
        }
        public void SetBrakeZero()
        {
            BrakeInput = 0;
            if (DoFrontWheelBrakes)
            {
                for (int i = 0; i < BrakeWheels_Front.Length; i++)
                {
                    BrakeWheels_Front[i].SetProgramVariable(Brake_VariableName, 0f);
                }
            }
            if (DoBackWheelBrakes)
            {
                for (int i = 0; i < BrakeWheels_Back.Length; i++)
                {
                    BrakeWheels_Back[i].SetProgramVariable(Brake_VariableName, 0f);
                }
            }
        }
        public void SetBrakeOne()
        {
            BrakeInput = 1;
            if (DoFrontWheelBrakes)
            {
                for (int i = 0; i < BrakeWheels_Front.Length; i++)
                {
                    BrakeWheels_Front[i].SetProgramVariable(Brake_VariableName, 1f * Brake_FrontStrengthMulti);
                }
            }
            if (DoBackWheelBrakes)
            {
                for (int i = 0; i < BrakeWheels_Back.Length; i++)
                {
                    BrakeWheels_Back[i].SetProgramVariable(Brake_VariableName, 1f * Brake_BackStrengthMulti);
                }
            }
        }
        public void SFEXT_O_TakeOwnership()
        {
            IsOwner = true;
            if (EnableBrakeOnExit && !Piloting)
            {
                gameObject.SetActive(true);
                SetBrakeOne();
            }
        }
        public void SFEXT_O_LoseOwnership()
        {
            if (!Occupied)
            {
                gameObject.SetActive(false);
            }
            IsOwner = false;
        }
        //Don't enable the brake if the car is using it's grapple when you exit to make towing easier
        private bool GrappleActive;
        private int NumGrapplesActive;
        public void SFEXT_G_GrappleInactive()
        {
            NumGrapplesActive--;
            if (NumGrapplesActive == 0)
            { GrappleActive = false; }
        }
        public void SFEXT_G_GrappleActive()
        {
            NumGrapplesActive++;
            GrappleActive = true;
        }
    }
}