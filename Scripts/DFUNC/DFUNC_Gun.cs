
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace SaccFlightAndVehicles
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class DFUNC_Gun : UdonSharpBehaviour
    {
        public UdonSharpBehaviour SAVControl;
        public Animator GunAnimator;
        [Tooltip("Animator bool that is true when the gun is firing")]
        public string GunFiringBoolName = "gunfiring";
        [Tooltip("Animator bool for local-only spool animation (should NOT fire particles/sounds)")]
        public string GunSpoolBoolName = "gunspool";
        [Tooltip("Animator bool for local-only spool-off (wind-down) animation/sound")]
        public string GunSpoolOffBoolName = "gunspooloff";
        [Tooltip("Optional animator state name to jump into for spool-off timing (leave blank to not force time)")]
        public string GunSpoolOffStateName = "";
        [Tooltip("Animator layer index for GunSpoolOffStateName (usually 0)")]
        public int GunSpoolOffStateLayer = 0;

        [Header("Local Spool Audio (optional)")]
        [Tooltip("Optional: if assigned, the script will seek + play this during spool-up so cancelling mid-way can transition smoothly")]
        public AudioSource SpoolUpAudioSource;
        [Tooltip("Optional: if assigned, the script will seek + play this during spool-off; cancel mid-spool will start it at the matching timestamp")]
        public AudioSource SpoolOffAudioSource;
        [Tooltip("Seconds before spool completes that the gun is allowed to actually start firing (creates overlap without audio tricks)")]
        public float FireLeadBeforeSpoolEndSec = 0.05f;
        [Tooltip("Seconds the trigger must be held before the gun actually fires (and syncs firing)")]
        public float SpoolUpTimeSec = 2.256f;
        [Tooltip("Seconds after stopping firing before the gun can fire again (local-only)")]
        public float SpoolDownLockoutSec = 1.488f;
        [Tooltip("Desktop key for firing when selected")]
        public KeyCode FireKey = KeyCode.Space;
        [Tooltip("Desktop key for firing when not selected")]
        public KeyCode FireNowKey = KeyCode.None;
        [Tooltip("Forward direction used for targeting (leave empty for vehicle's forward)")]
        [SerializeField] private Transform TargetingTransform;
        [Tooltip("Transform of which its X scale scales with ammo")]
        public Transform[] AmmoBars;
        [Tooltip("Position at which recoil forces are added, not required for recoil to work. Only use this if you want the vehicle to rotate when shooting")]
        public Transform GunRecoilEmpty;
        [Tooltip("There is a separate particle system for doing damage that is only enabled for the user of the gun. This object is the parent of that particle system, is enabled when entering the seat, and disabled when exiting")]
        public Transform GunDamageParticle;
        [Tooltip("Crosshair to switch to when gun is selected")]
        public GameObject HudCrosshairGun;
        [Tooltip("Vehicle's normal crosshair")]
        public GameObject HudCrosshair;
        [Tooltip("How long it takes to fully reload from empty in seconds")]
        public float FullReloadTimeSec = 20;
        [UdonSynced(UdonSyncMode.None)] public float GunAmmoInSeconds = 12;
        public float RecoilForce = 1;
        [Tooltip("Set a boolean value in the animator when switching to this weapon?")]
        public bool DoAnimBool = false;
        [Tooltip("Animator bool that is true when this function is selected")]
        public string AnimBoolName = "GunSelected";
        [Tooltip("Should the boolean stay true if the pilot exits with it selected?")]
        public bool AnimBoolStayTrueOnExit;
        [Tooltip("Allow gun to fire while vehicle is on the ground?")]
        public bool AllowFiringGrounded = true;
        [Tooltip("Disable the weapon if wind is enabled, to prevent people gaining an unfair advantage")]
        public bool DisallowFireIfWind = false;
        [Tooltip("Enable these objects when GUN selected")]
        public GameObject[] EnableOnSelected;
        [Tooltip("On desktop mode, fire even when not selected if OnPickupUseDown is pressed")]
        [SerializeField] bool DT_UseToFire;
        private bool Grounded;
        bool KeepingAwake;
        [System.NonSerializedAttribute] public bool LeftDial = false;
        [System.NonSerializedAttribute] public int DialPosition = -999;
        [System.NonSerializedAttribute] public SaccEntity EntityControl;
        private bool AnimOn;
        private int AnimBool_STRING;
        [System.NonSerializedAttribute] public float FullGunAmmoInSeconds;
        private Rigidbody VehicleRigidbody;
        [System.NonSerializedAttribute, UdonSynced, FieldChangeCallback(nameof(Firing))] public bool _firing;
        public bool Firing
        {
            set
            {
                if (EntityControl.IsOwner && RecoilForce > 0)
                {
                    if (value)
                    {
                        if (!KeepingAwake)
                        {
                            KeepingAwake = true;
                            EntityControl.KeepAwake_++;
                        }
                    }
                    else
                    {
                        if (KeepingAwake)
                        {
                            KeepingAwake = false;
                            EntityControl.KeepAwake_--;
                        }
                    }
                }
                GunAnimator.SetBool(GunFiringBoolName, value);
                _firing = value;
            }
            get => _firing;
        }
        private float FullGunAmmoDivider;
        private bool Selected = false;
        bool inVR;
        private bool Selected_HUD = false;
        private float reloadspeed;
        private bool Piloting = false;
        private Vector3 AmmoBarScaleStart;
        private Vector3[] AmmoBarScaleStarts;

        [System.NonSerialized] private bool _spooling;
        [System.NonSerialized] private float _spoolStartTime;
        [System.NonSerialized] private bool _spoolPrevAnimBool;
        [System.NonSerialized] private bool _spoolHasPrevAnimBool;

        [System.NonSerialized] private float _spoolOffUntil;
        [System.NonSerialized] private bool _spoolOffOn;
        [System.NonSerialized] private bool _spoolOffPrevAnimBool;
        [System.NonSerialized] private bool _spoolOffHasPrevAnimBool;

        [System.NonSerialized] private bool _spoolUpAudioStarted;
        [System.NonSerialized] private bool _spoolOffAudioStarted;
        [System.NonSerialized] private bool _spoolEndPending;
        [System.NonSerialized] private float _spoolEndAt;

        private const float _AmmoSyncInterval = 0.35f;
        private const float _AmmoSyncEps = 0.25f;
        [System.NonSerialized] private float _nextAmmoSyncTime;
        [System.NonSerialized] private float _lastSyncedAmmo;

        // Network optimization: keep local firing responsive, but avoid spamming
        // RequestSerialization() when click-firing.
        // - Only serialize a STOP if the trigger has remained released for a short delay.
        // - Coalesce multiple changes and cap firing sync rate.
        private const float _FiringNetStopDelay = 0.15f;
        private const float _FiringNetSyncInterval = 0.25f;
        [System.NonSerialized] private float _nextFiringSyncTime;
        [System.NonSerialized] private bool _stopSyncPending;
        [System.NonSerialized] private float _stopSyncAt;
        [System.NonSerialized] private bool _lastSerializedFiring;
        [System.NonSerialized] private bool _firingSyncDirty;

        private bool _EnsureLocalNetOwnership()
        {
            if (Networking.IsOwner(gameObject)) { return true; }
            VRCPlayerApi lp = Networking.LocalPlayer;
            if (lp == null) { return false; }
            Networking.SetOwner(lp, gameObject);
            bool nowOwner = Networking.IsOwner(gameObject);
            IsOwner = nowOwner;
            return nowOwner;
        }

        private void _MarkFiringSyncDirty()
        {
            _firingSyncDirty = true;
            _TrySyncFiringNow(false);
        }

        private void _SyncFiringStartIfNeeded()
        {
            // If remote clients are currently "not firing", we must send a start immediately.
            // Otherwise rapid stop/start cycles can apply damage without any visible firing on remotes.
            if (!_EnsureLocalNetOwnership()) { return; }
            if (_firing && !_lastSerializedFiring)
            {
                RequestSerialization();
                _lastSerializedFiring = true;
                _firingSyncDirty = false;
                _nextFiringSyncTime = Time.time + _FiringNetSyncInterval;
            }
        }

        private bool _AnimatorHasBoolParam(string paramName)
        {
            if (!GunAnimator || string.IsNullOrEmpty(paramName)) { return false; }
            var ps = GunAnimator.parameters;
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].type == AnimatorControllerParameterType.Bool && ps[i].name == paramName)
                { return true; }
            }
            return false;
        }

        private void _SetLocalSpoolAnim(bool on)
        {
            if (!GunAnimator) { return; }
            if (string.IsNullOrEmpty(GunSpoolBoolName)) { return; }
            // Don't cache this: some setups swap runtime controllers on enable/seat.
            if (!_AnimatorHasBoolParam(GunSpoolBoolName)) { return; }

            if (on)
            {
                if (!_spoolHasPrevAnimBool)
                {
                    _spoolPrevAnimBool = GunAnimator.GetBool(GunSpoolBoolName);
                    _spoolHasPrevAnimBool = true;
                }
                GunAnimator.SetBool(GunSpoolBoolName, true);
            }
            else
            {
                if (_spoolHasPrevAnimBool)
                {
                    GunAnimator.SetBool(GunSpoolBoolName, _spoolPrevAnimBool);
                    _spoolHasPrevAnimBool = false;
                }
                else
                {
                    GunAnimator.SetBool(GunSpoolBoolName, false);
                }
            }
        }

        private static void _AudioPlayFromNormalizedTime(AudioSource src, float normalized01)
        {
            if (!src) { return; }
            AudioClip clip = src.clip;
            if (!clip) { return; }

            float t01 = Mathf.Clamp01(normalized01);

            // Prefer sample-accurate seeking when possible.
            int samples = clip.samples;
            if (samples > 0)
            {
                int targetSamples = Mathf.Clamp(Mathf.RoundToInt(t01 * (samples - 1)), 0, samples - 1);
                src.timeSamples = targetSamples;
            }
            else
            {
                src.time = Mathf.Clamp(t01 * clip.length, 0f, clip.length);
            }

            if (!src.isPlaying)
            {
                src.Play();
            }
        }

        private static void _AudioStop(AudioSource src)
        {
            if (!src) { return; }
            if (src.isPlaying)
            {
                src.Stop();
            }
        }

        private void _SetLocalSpoolOffAnim(bool on)
        {
            if (!GunAnimator) { return; }
            if (string.IsNullOrEmpty(GunSpoolOffBoolName)) { return; }
            if (!_AnimatorHasBoolParam(GunSpoolOffBoolName)) { return; }

            if (on)
            {
                if (!_spoolOffHasPrevAnimBool)
                {
                    _spoolOffPrevAnimBool = GunAnimator.GetBool(GunSpoolOffBoolName);
                    _spoolOffHasPrevAnimBool = true;
                }
                GunAnimator.SetBool(GunSpoolOffBoolName, true);
            }
            else
            {
                if (_spoolOffHasPrevAnimBool)
                {
                    GunAnimator.SetBool(GunSpoolOffBoolName, _spoolOffPrevAnimBool);
                    _spoolOffHasPrevAnimBool = false;
                }
                else
                {
                    GunAnimator.SetBool(GunSpoolOffBoolName, false);
                }
            }
        }

        private void _StartLocalSpoolOff(float now)
        {
            _StartLocalSpoolOff(now, 1f);
        }

        private void _StartLocalSpoolOff(float now, float spoolProgress01)
        {
            float clampedProgress = Mathf.Clamp01(spoolProgress01);
            float lockout = Mathf.Max(0f, SpoolDownLockoutSec) * clampedProgress;
            _spoolOffUntil = now + lockout;
            _spoolOffOn = (SpoolDownLockoutSec > 0f) && (clampedProgress > 0f);

            // Spool-off is mutually exclusive with spool-up.
            if (_spooling)
            {
                _spooling = false;
                _SetLocalSpoolAnim(false);
            }

            // Audio: cancelling during spool-up should transition smoothly to spool-off.
            // Assumes spool-off clip is authored from full->idle.
            _AudioStop(SpoolUpAudioSource);
            if (_spoolOffOn)
            {
                _spoolOffAudioStarted = true;
                float spoolOffStart01 = 1f - clampedProgress;
                _AudioPlayFromNormalizedTime(SpoolOffAudioSource, spoolOffStart01);
            }
            else
            {
                _spoolOffAudioStarted = false;
                _AudioStop(SpoolOffAudioSource);
            }

            if (_spoolOffOn)
            {
                _SetLocalSpoolOffAnim(true);
                _TryJumpSpoolOffAnimToMatchProgress(clampedProgress);
            }
            else
            {
                _SetLocalSpoolOffAnim(false);
            }
        }

        private void _TryJumpSpoolOffAnimToMatchProgress(float spoolProgress01)
        {
            if (!GunAnimator) { return; }
            if (string.IsNullOrEmpty(GunSpoolOffStateName)) { return; }

            // Map current spool-up progress to spool-down clip time.
            // Assumes spool-off clip is authored from full->idle over time (start: full spool, end: idle).
            float normalizedTime = Mathf.Clamp01(1f - Mathf.Clamp01(spoolProgress01));
            GunAnimator.Play(GunSpoolOffStateName, GunSpoolOffStateLayer, normalizedTime);
            // Apply immediately so pitch curves etc start at the expected point.
            GunAnimator.Update(0f);
        }

        private void _StopLocalSpoolOff()
        {
            _spoolOffOn = false;
            _spoolOffUntil = 0f;
            _SetLocalSpoolOffAnim(false);
            _spoolOffHasPrevAnimBool = false;
            _spoolOffAudioStarted = false;
            _AudioStop(SpoolOffAudioSource);
        }

        private void _TrySyncFiringNow(bool force)
        {
            if (!_EnsureLocalNetOwnership()) { return; }
            if (!_firingSyncDirty && !force) { return; }

            float now = Time.time;
            if (!force && now < _nextFiringSyncTime) { return; }

            if (!force && _firing == _lastSerializedFiring)
            {
                _firingSyncDirty = false;
                _nextFiringSyncTime = now + _FiringNetSyncInterval;
                return;
            }

            RequestSerialization();
            _lastSerializedFiring = _firing;
            _firingSyncDirty = false;
            _nextFiringSyncTime = now + _FiringNetSyncInterval;
        }
        public void SFEXT_L_EntityStart()
        {
            FullGunAmmoInSeconds = GunAmmoInSeconds;
            reloadspeed = FullGunAmmoInSeconds / FullReloadTimeSec;

            _lastSyncedAmmo = GunAmmoInSeconds;
            _nextAmmoSyncTime = 0f;

            _lastSerializedFiring = _firing;
            _nextFiringSyncTime = 0f;
            _stopSyncPending = false;
            _stopSyncAt = 0f;
            _firingSyncDirty = false;

            _spooling = false;
            _spoolStartTime = 0f;
            _spoolPrevAnimBool = false;
            _spoolHasPrevAnimBool = false;

            _spoolOffUntil = 0f;
            _spoolOffOn = false;
            _spoolOffPrevAnimBool = false;
            _spoolOffHasPrevAnimBool = false;

            _spoolUpAudioStarted = false;
            _spoolOffAudioStarted = false;
            _spoolEndPending = false;
            _spoolEndAt = 0f;

            AmmoBarScaleStarts = new Vector3[AmmoBars.Length];
            for (int i = 0; i < AmmoBars.Length; i++)
            {
                AmmoBarScaleStarts[i] = AmmoBars[i].localScale;
            }

            VehicleRigidbody = EntityControl.GetComponent<Rigidbody>();
            IsOwner = EntityControl.IsOwner;
            FullGunAmmoDivider = 1f / (FullGunAmmoInSeconds > 0 ? FullGunAmmoInSeconds : 10000000);
            AAMTargets = EntityControl.AAMTargets;
            NumAAMTargets = AAMTargets.Length;
            if (!TargetingTransform) TargetingTransform = EntityControl.transform;
            CenterOfMass = EntityControl.CenterOfMass;
            OutsideVehicleLayer = EntityControl.OutsideVehicleLayer;
            if (GunDamageParticle) GunDamageParticle.gameObject.SetActive(false);

            //HUD
            if (HUDControl)
            {
                distance_from_head = (float)HUDControl.GetProgramVariable("distance_from_head");
            }
            if (distance_from_head == 0) { distance_from_head = 1.333f; }
        }
        public void ReInitAmmo()//set FullAAMs then run this to change vehicles max gun ammo
        {
            GunAmmoInSeconds = FullGunAmmoInSeconds;
            reloadspeed = FullGunAmmoInSeconds / FullReloadTimeSec;
            FullGunAmmoDivider = 1f / (FullGunAmmoInSeconds > 0 ? FullGunAmmoInSeconds : 10000000);
            UpdateAmmoVisuals();
        }
        public void DFUNC_Selected()
        {
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(Set_Selected));
            Selected = true;
            if (DoAnimBool && !AnimOn)
            { SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(SetBoolOn)); }
        }
        public void DFUNC_Deselected()
        {
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(Set_Unselected));
            Selected = false;
            PickupTrigger = 0;
            _spooling = false;
            _SetLocalSpoolAnim(false);
            _StopLocalSpoolOff();
            _spoolUpAudioStarted = false;
            _AudioStop(SpoolUpAudioSource);
            _spoolEndPending = false;
            if (_firing)
            {
                Firing = false;
                RequestSerialization();
            }
            if (DoAnimBool && AnimOn)
            { SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(SetBoolOff)); }
        }
        public void SFEXT_O_PilotEnter()
        {
            Piloting = true;
            inVR = EntityControl.InVR;
            if (GunDamageParticle) { GunDamageParticle.gameObject.SetActive(true); }
            if (_firing) { Firing = false; }
            gameObject.SetActive(true);
            RequestSerialization();
            OnDeserialization();
        }
        byte numUsers;
        public void SFEXT_G_PilotEnter()
        {
            numUsers++;
            if (numUsers > 1) return;

            Set_Active();
        }
        public void SFEXT_G_PilotExit()
        {
            numUsers--;
            if (numUsers != 0) return;

            Set_Inactive();
            Set_Unselected();
            if (DoAnimBool && !AnimBoolStayTrueOnExit && AnimOn)
            { SetBoolOff(); }
        }
        public void SFEXT_O_PilotExit()
        {
            Piloting = false;
            _spooling = false;
            _SetLocalSpoolAnim(false);
            _StopLocalSpoolOff();
            _spoolUpAudioStarted = false;
            _AudioStop(SpoolUpAudioSource);
            _spoolEndPending = false;
            _StopFiringAndSyncIfOwner();
            if (Selected) { SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(Set_Unselected)); }//unselect 
            Selected = false;
            if (GunDamageParticle) { GunDamageParticle.gameObject.SetActive(false); }
        }
        public void SFEXT_G_ReSupply()
        {
            if (IsOwner)
            {
                GunAmmoInSeconds = Mathf.Min(GunAmmoInSeconds + reloadspeed, FullGunAmmoInSeconds);
                // Resupply can be called very frequently; throttle serialization to reduce bandwidth.
                float now = Time.time;
                if (GunAmmoInSeconds >= FullGunAmmoInSeconds || (now >= _nextAmmoSyncTime && Mathf.Abs(GunAmmoInSeconds - _lastSyncedAmmo) >= _AmmoSyncEps))
                {
                    _lastSyncedAmmo = GunAmmoInSeconds;
                    _nextAmmoSyncTime = now + _AmmoSyncInterval;
                    RequestSerialization();
                    OnDeserialization();
                }
            }
            if (SAVControl && GunAmmoInSeconds != FullGunAmmoInSeconds)
            { EntityControl.SetProgramVariable("ReSupplied", (int)EntityControl.GetProgramVariable("ReSupplied") + 1); }
        }
        public void SFEXT_G_ReArm() { SFEXT_G_ReSupply(); }
        public override void OnDeserialization()
        {
            UpdateAmmoVisuals();
        }
        public void UpdateAmmoVisuals()
        {
            for (int i = 0; i < AmmoBars.Length; i++)
            {
                AmmoBars[i].localScale = new Vector3((GunAmmoInSeconds * FullGunAmmoDivider) * AmmoBarScaleStarts[i].x, AmmoBarScaleStarts[i].y, AmmoBarScaleStarts[i].z);
            }
        }
        public void SFEXT_G_RespawnButton()
        {
            if (IsOwner)
            {
                GunAmmoInSeconds = FullGunAmmoInSeconds;
                RequestSerialization();
                UpdateAmmoVisuals();
            }
            if (DoAnimBool && AnimOn)
            { SetBoolOff(); }
        }
        public void Set_Selected()
        {
            if (HudCrosshairGun) { HudCrosshairGun.SetActive(true); }
            if (HudCrosshair) { HudCrosshair.SetActive(false); }
            for (int i = 0; i < EnableOnSelected.Length; i++)
            { EnableOnSelected[i].SetActive(true); }
            Selected_HUD = true;
        }
        public void Set_Unselected()
        {
            if (HudCrosshairGun) { HudCrosshairGun.SetActive(false); }
            if (HudCrosshair) { HudCrosshair.SetActive(true); }
            if (TargetIndicator) { TargetIndicator.gameObject.SetActive(false); }
            if (GUNLeadIndicator) { GUNLeadIndicator.gameObject.SetActive(false); }
            for (int i = 0; i < EnableOnSelected.Length; i++)
            { EnableOnSelected[i].SetActive(false); }
            Selected_HUD = false;
        }
        public void Set_Active()
        {
            gameObject.SetActive(true);
        }
        
        public void Set_Inactive()
        {
            // Don't bypass firing-sync bookkeeping.
            _StopFiringAndSyncIfOwner();
            gameObject.SetActive(false);
        }

        private void _StopFiringLocal()
        {
            PickupTrigger = 0;
            _spooling = false;
            _SetLocalSpoolAnim(false);
            _StopLocalSpoolOff();
            _spoolUpAudioStarted = false;
            _AudioStop(SpoolUpAudioSource);
            _spoolEndPending = false;
            if (GunAnimator) { GunAnimator.SetBool(GunFiringBoolName, false); }
            if (_firing) { Firing = false; }
            _stopSyncPending = false;
        }

        private void _StopFiringAndSyncIfOwner()
        {
            // If remotes last received "firing=true" but we're now false locally (e.g. stopped via Set_Inactive),
            // we MUST serialize even if _firing is already false, otherwise others will never see the stop/start correctly.
            bool wasDirtyLocal = _firing || PickupTrigger != 0;

            _StopFiringLocal();

            bool serializedMismatch = (_lastSerializedFiring != _firing);
            if ((wasDirtyLocal || serializedMismatch) && EntityControl != null && EntityControl.IsOwner)
            {
                RequestSerialization();
                _lastSerializedFiring = _firing;
                _firingSyncDirty = false;
                _nextFiringSyncTime = Time.time + _FiringNetSyncInterval;
            }
        }

        void OnDisable()
        {
            // If disabled mid-trigger, Update/LateUpdate won't run to clear firing.
            ResetEverything();
        }

        void OnEnable()
        {
            // If re-enabled while nobody is using it, ensure firing can't stay latched on.
            // (e.g. object was disabled mid-fire, then enabled later by seat / respawn logic)
            ResetEverything();
            if (!Piloting && numUsers == 0)
            {
                _StopFiringAndSyncIfOwner();
            }
        }


        public void ResetEverything()
        {
            if(GunAnimator != null) GunAnimator.keepAnimatorStateOnDisable = false;
            if(GunAnimator != null) GunAnimator.enabled = false;
            // Reset runtime state so the gun cannot come up "already firing" after enable.
            PickupTrigger = 0;

            // Local spool/firing state.
            _spooling = false;
            _spoolStartTime = 0f;
            _spoolPrevAnimBool = false;
            _spoolHasPrevAnimBool = false;

            _spoolOffUntil = 0f;
            _spoolOffOn = false;
            _spoolOffPrevAnimBool = false;
            _spoolOffHasPrevAnimBool = false;

            _spoolUpAudioStarted = false;
            _spoolOffAudioStarted = false;
            _spoolEndPending = false;
            _spoolEndAt = 0f;

            // Stop any local spool audio.
            _AudioStop(SpoolUpAudioSource);
            _AudioStop(SpoolOffAudioSource);

            // Clear network-sync bookkeeping so we don't keep pending stop/start work.
            _stopSyncPending = false;
            _stopSyncAt = 0f;
            _firingSyncDirty = false;
            _nextFiringSyncTime = 0f;

            // Ensure KeepAwake isn't left incremented.
            if (KeepingAwake && EntityControl != null && EntityControl.IsOwner)
            {
                KeepingAwake = false;
                EntityControl.KeepAwake_--;
            }
            KeepingAwake = false;

            // Force animator + local firing flag off.
            if (GunAnimator != null)
            {
                GunAnimator.SetBool(GunFiringBoolName, false);
                GunAnimator.SetBool(GunSpoolBoolName, false);
                GunAnimator.SetBool(GunSpoolOffBoolName, false);
                GunAnimator.SetBool(AnimBoolName, false);
                GunAnimator.Rebind();
                GunAnimator.Update(0f);
            }

            _firing = false;
            Grounded = false;

            if(GunAnimator != null) GunAnimator.enabled = true;
        }

        public void SFEXT_L_OnDisable()
        {
            _StopFiringAndSyncIfOwner();
        }

        bool IsOwner;
        public void SFEXT_O_TakeOwnership()
        {
            IsOwner = true;
            if (gameObject.activeSelf)//if someone times out, tell weapon to stop firing if you take ownership.
            { SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(Set_Inactive)); }
            if (Selected_HUD)
            { SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(Set_Unselected)); }
        }
        public void SFEXT_O_LoseOwnership()
        {
            IsOwner = false;
        }
        public void SFEXT_G_Explode()
        {
            _StopFiringAndSyncIfOwner();
            GunAmmoInSeconds = FullGunAmmoInSeconds;
            if (DoAnimBool && AnimOn)
            { SetBoolOff(); }
        }

        private bool _HasFireInputContext()
        {
            return Selected || Input.GetKey(FireNowKey) || (!inVR && DT_UseToFire);
        }

        private float _GetFireTrigger()
        {
            if (EntityControl.Holding || (!inVR && DT_UseToFire))
            {
                return PickupTrigger;
            }
            if (!Selected)
            {
                return 0f;
            }
            return LeftDial
                ? Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryIndexTrigger")
                : Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryIndexTrigger");
        }

        private bool _TickSpoolOffLockout(float now)
        {
            bool active = (SpoolDownLockoutSec > 0f) && (now < _spoolOffUntil);
            if (active)
            {
                if (!_spoolOffOn)
                {
                    _spoolOffOn = true;
                }
                _SetLocalSpoolOffAnim(true);

                // Lockout cancels spool-up (but does NOT start spool-off again).
                if (_spooling)
                {
                    _spooling = false;
                    _SetLocalSpoolAnim(false);
                    _spoolUpAudioStarted = false;
                    _AudioStop(SpoolUpAudioSource);
                    _spoolEndPending = false;
                }
            }
            else if (_spoolOffOn)
            {
                _spoolOffOn = false;
                _SetLocalSpoolOffAnim(false);
            }
            return active;
        }

        private bool _TickSpoolUpCanFire(float now, bool fireAllowed)
        {
            // Default: if no spool-up configured, or already firing, just follow fireAllowed.
            if (SpoolUpTimeSec <= 0f || _firing)
            {
                if (_spooling)
                {
                    _spooling = false;
                    _SetLocalSpoolAnim(false);
                    _spoolUpAudioStarted = false;
                    _AudioStop(SpoolUpAudioSource);
                    _spoolEndPending = false;
                }
                return fireAllowed;
            }

            if (fireAllowed && GunAmmoInSeconds > 0f)
            {
                if (!_spooling)
                {
                    _spooling = true;
                    _spoolStartTime = now;
                    _SetLocalSpoolAnim(true);
                    _spoolUpAudioStarted = true;
                    _AudioPlayFromNormalizedTime(SpoolUpAudioSource, 0f);
                    _spoolEndPending = false;
                }

                float spoolElapsed = now - _spoolStartTime;
                float lead = Mathf.Clamp(FireLeadBeforeSpoolEndSec, 0f, SpoolUpTimeSec);
                float fireAt = Mathf.Max(0f, SpoolUpTimeSec - lead);
                return spoolElapsed >= fireAt;
            }

            // Cancel spool-up if input released / not allowed / out of ammo.
            if (_spooling)
            {
                float spoolProgress01 = 0f;
                if (SpoolUpTimeSec > 0f)
                {
                    spoolProgress01 = Mathf.Clamp01((now - _spoolStartTime) / SpoolUpTimeSec);
                }
                _spooling = false;
                _SetLocalSpoolAnim(false);
                if (spoolProgress01 > 0f)
                {
                    _StartLocalSpoolOff(now, spoolProgress01);
                }
                _spoolUpAudioStarted = false;
                _AudioStop(SpoolUpAudioSource);
                _spoolEndPending = false;
            }
            return false;
        }

        private void _TickSpoolEnd(float now)
        {
            if (_spoolEndPending && now >= _spoolEndAt)
            {
                _spoolEndPending = false;
                _SetLocalSpoolAnim(false);
            }
        }

        private void _TickFiringSerialization(float now, bool callOnDeserializationOnStop)
        {
            if (_stopSyncPending && now >= _stopSyncAt && !_firing)
            {
                _stopSyncPending = false;
                _MarkFiringSyncDirty();
                _TrySyncFiringNow(true);
                if (callOnDeserializationOnStop)
                {
                    OnDeserialization();
                }
            }
            else
            {
                _TrySyncFiringNow(false);
            }
        }

        public void LateUpdate()
        {
            if (!Piloting) { return; }

            float now = Time.time;
            bool spoolOffActive = _TickSpoolOffLockout(now);

            bool hasInputContext = _HasFireInputContext();
            if (hasInputContext)
            {
                float trigger = _GetFireTrigger();
                bool wantsFireInput = (trigger > 0.75f) || Input.GetKey(FireKey) || Input.GetKey(FireNowKey);

                bool fireAllowed = wantsFireInput && (!Grounded || AllowFiringGrounded) && !spoolOffActive;
                bool canActuallyFireNow = _TickSpoolUpCanFire(now, fireAllowed);

                _TickSpoolEnd(now);

                if (GunAmmoInSeconds <= 0f)
                {
                    if (_firing)
                    {
                        Firing = false;
                        _StartLocalSpoolOff(now);
                        _stopSyncPending = false;
                        _MarkFiringSyncDirty();
                        _TrySyncFiringNow(true);
                    }
                }
                else if (canActuallyFireNow)
                {
                    if (DisallowFireIfWind && SAVControl)
                    {
                        if (((Vector3)SAVControl.GetProgramVariable("FinalWind")).sqrMagnitude > 0f)
                        { return; }
                    }

                    _stopSyncPending = false;
                    if (!_firing)
                    {
                        if (_spooling)
                        {
                            _spooling = false;
                            float lead = Mathf.Clamp(FireLeadBeforeSpoolEndSec, 0f, SpoolUpTimeSec);
                            if (lead > 0f)
                            {
                                _spoolEndPending = true;
                                _spoolEndAt = now + lead;
                            }
                            else
                            {
                                _spoolEndPending = false;
                                _SetLocalSpoolAnim(false);
                            }
                        }
                        if (_spoolOffOn)
                        {
                            _StopLocalSpoolOff();
                        }
                        _spoolUpAudioStarted = false;
                        Firing = true;
                        _stopSyncPending = false;
                        _MarkFiringSyncDirty();
                        _SyncFiringStartIfNeeded();
                    }

                    GunAmmoInSeconds = Mathf.Max(GunAmmoInSeconds - Time.deltaTime, 0f);
                }
                else
                {
                    if (_firing)
                    {
                        Firing = false;
                        _StartLocalSpoolOff(now);
                        _stopSyncPending = true;
                        _stopSyncAt = now + _FiringNetStopDelay;
                    }
                }

                _TickFiringSerialization(now, false);
                Hud();
                UpdateAmmoVisuals();
            }
            else
            {
                if (_firing)
                {
                    Firing = false;
                    _StartLocalSpoolOff(now);
                    _stopSyncPending = true;
                    _stopSyncAt = now + _FiringNetStopDelay;
                }

                _spoolEndPending = false;
                _TickFiringSerialization(now, true);
            }
        }
        private GameObject[] AAMTargets;
        private int AAMTarget;
        private int AAMTargetChecker;
        public UdonSharpBehaviour HUDControl;
        private Transform CenterOfMass;
        private SaccAirVehicle AAMCurrentTargetSAVControl;
        private int OutsideVehicleLayer;
        public float MaxTargetDistance = 6000;
        private int NumAAMTargets;
        private Vector3 AAMCurrentTargetDirection;
        private float AAMTargetObscuredDelay = 999f;
        private bool GUNHasTarget;
        private void FixedUpdate()//this is just the old  AAMTargeting adjusted slightly
                                  //there may unnecessary stuff in here because it doesn't need to do missile related stuff any more 
        {
            if (_firing && EntityControl.IsOwner)
            {
                if (!GunRecoilEmpty)
                {
                    VehicleRigidbody.AddRelativeForce(-Vector3.forward * RecoilForce, ForceMode.Acceleration);
                }
                else
                {
                    VehicleRigidbody.AddForceAtPosition(-GunRecoilEmpty.forward * RecoilForce, GunRecoilEmpty.position, ForceMode.Acceleration);
                }
            }
            if (!IsOwner) return;
            if (Selected)
            {
                float DeltaTime = Time.fixedDeltaTime;
                var AAMCurrentTargetPosition = AAMTargets[AAMTarget].transform.position;
                Vector3 HudControlPosition = HUDControl ? HUDControl.transform.position : Networking.LocalPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
                float AAMCurrentTargetAngle = Vector3.Angle(TargetingTransform.forward, (AAMCurrentTargetPosition - HudControlPosition));

                //check 1 target per frame to see if it's infront of us and worthy of being our current target
                var TargetChecker = AAMTargets[AAMTargetChecker];
                var TargetCheckerTransform = TargetChecker.transform;
                var TargetCheckerParent = TargetCheckerTransform.parent;

                Vector3 AAMNextTargetDirection = (TargetCheckerTransform.position - HudControlPosition);
                float NextTargetAngle = Vector3.Angle(TargetingTransform.forward, AAMNextTargetDirection);
                float NextTargetDistance = Vector3.Distance(CenterOfMass.position, TargetCheckerTransform.position);

                if (TargetChecker.activeInHierarchy)
                {
                    SaccAirVehicle NextTargetSAVControl = null;

                    if (TargetCheckerParent)
                    {
                        NextTargetSAVControl = TargetCheckerParent.GetComponent<SaccAirVehicle>();
                    }
                    //if target EngineController is null then it's a dummy target (or hierarchy isn't set up properly)
                    if ((!NextTargetSAVControl || (!NextTargetSAVControl.Taxiing && !NextTargetSAVControl.EntityControl._dead)))
                    {
                        RaycastHit hitnext;
                        //raycast to check if it's behind something
                        bool LineOfSightNext = Physics.Raycast(HudControlPosition, AAMNextTargetDirection, out hitnext, 99999999, 133137 /* Default, Water, Environment, and Walkthrough */, QueryTriggerInteraction.Collide);
#if UNITY_EDITOR
                        if (hitnext.collider)
                            Debug.DrawLine(HudControlPosition, hitnext.point, Color.red);
                        else
                            Debug.DrawRay(HudControlPosition, AAMNextTargetDirection, Color.yellow);
#endif
                        /*                 Debug.Log(string.Concat("LoS_next ", LineOfSightNext));
                                        if (hitnext.collider != null) Debug.Log(string.Concat("RayCastCorrectLayer_next ", (hitnext.collider.gameObject.layer == OutsidePlaneLayer)));
                                        if (hitnext.collider != null) Debug.Log(string.Concat("RayCastLayer_next ", hitnext.collider.gameObject.layer));
                                        Debug.Log(string.Concat("LowerAngle_next ", NextTargetAngle < AAMCurrentTargetAngle));
                                        Debug.Log(string.Concat("InAngle_next ", NextTargetAngle < 70));
                                        Debug.Log(string.Concat("BelowMaxDist_next ", NextTargetDistance < AAMMaxTargetDistance)); */

                        if (LineOfSightNext
                            && (hitnext.collider && hitnext.collider.gameObject.layer == OutsideVehicleLayer) //did raycast hit an object on the layer planes are on?
                                && NextTargetAngle < 70//lock angle
                                    && NextTargetAngle < AAMCurrentTargetAngle
                                        && NextTargetDistance < MaxTargetDistance
                                            || ((AAMCurrentTargetSAVControl && AAMCurrentTargetSAVControl.Taxiing)//prevent being unable to switch target if it's angle is higher than your current target and your current target happens to be taxiing and is therefore untargetable
                                                || !AAMTargets[AAMTarget].activeInHierarchy// always switch if target inactive/destroyed
                                                || AAMTargetObscuredDelay > .25f))// always switch if target is obscured
                        {
                            //found new target
                            AAMCurrentTargetAngle = NextTargetAngle;
                            AAMTarget = AAMTargetChecker;
                            AAMCurrentTargetPosition = AAMTargets[AAMTarget].transform.position;
                            AAMCurrentTargetSAVControl = NextTargetSAVControl;
                            RelativeTargetVelLastFrame = Vector3.zero;
                            GUN_TargetSpeedLerper = 0f;
                            GUN_TargetDirOld = AAMNextTargetDirection * 1.00001f; //so the difference isn't 0
                        }

                    }
                }
                //increase target checker ready for next frame
                AAMTargetChecker++;
                if (AAMTargetChecker == AAMTarget && AAMTarget == NumAAMTargets - 1)
                { AAMTargetChecker = 0; }
                else if (AAMTargetChecker == AAMTarget)
                { AAMTargetChecker++; }
                else if (AAMTargetChecker == NumAAMTargets)
                { AAMTargetChecker = 0; }

                //if target is currently in front of plane, lock onto it
                if (!AAMCurrentTargetSAVControl)
                { AAMCurrentTargetDirection = AAMCurrentTargetPosition - HudControlPosition; }
                else
                { AAMCurrentTargetDirection = AAMCurrentTargetSAVControl.CenterOfMass.position - HudControlPosition; }
                float AAMCurrentTargetDistance = AAMCurrentTargetDirection.magnitude;
                //check if target is active, and if it's enginecontroller is null(dummy target), or if it's not null(plane) make sure it's not taxiing or dead.
                //raycast to check if it's behind something
                RaycastHit hitcurrent;
                bool LineOfSightCur = Physics.Raycast(HudControlPosition, AAMCurrentTargetDirection, out hitcurrent, 99999999, 133137 /* Default, Water, Environment, and Walkthrough */, QueryTriggerInteraction.Collide);
#if UNITY_EDITOR
                if (hitcurrent.collider)
                    Debug.DrawLine(HudControlPosition, hitcurrent.point, Color.green);
                else
                    Debug.DrawRay(HudControlPosition, AAMNextTargetDirection, Color.blue);
#endif
                //used to make lock remain for .25 seconds after target is obscured
                if (!LineOfSightCur || (hitcurrent.collider && hitcurrent.collider.gameObject.layer != OutsideVehicleLayer))
                { AAMTargetObscuredDelay += DeltaTime; }
                else
                { AAMTargetObscuredDelay = 0; }

                if ((SAVControl && !(bool)SAVControl.GetProgramVariable("Taxiing"))
                    && (AAMTargetObscuredDelay < .25f)
                        && AAMCurrentTargetDistance < MaxTargetDistance
                            && AAMTargets[AAMTarget].activeInHierarchy
                                && (!AAMCurrentTargetSAVControl || (!AAMCurrentTargetSAVControl.Taxiing && !AAMCurrentTargetSAVControl.EntityControl._dead)))
                {
                    if ((AAMTargetObscuredDelay < .25f) && AAMCurrentTargetDistance < MaxTargetDistance)
                    {
                        GUNHasTarget = true;
                    }
                }
                else
                {
                    GUNHasTarget = false;
                }
                /*         Debug.Log(string.Concat("AAMTarget ", AAMTarget));
                        Debug.Log(string.Concat("HasTarget ", AAMHasTarget));
                        Debug.Log(string.Concat("AAMTargetObscuredDelay ", AAMTargetObscuredDelay));
                        Debug.Log(string.Concat("LoS ", LineOfSightCur));
                        Debug.Log(string.Concat("RayCastCorrectLayer ", (hitcurrent.collider.gameObject.layer == OutsidePlaneLayer)));
                        Debug.Log(string.Concat("RayCastLayer ", hitcurrent.collider.gameObject.layer));
                        Debug.Log(string.Concat("NotObscured ", AAMTargetObscuredDelay < .25f));
                        Debug.Log(string.Concat("InAngle ", AAMCurrentTargetAngle < 70));
                        Debug.Log(string.Concat("BelowMaxDist ", AAMCurrentTargetDistance < AAMMaxTargetDistance)); */
            }
        }
        public void SFEXT_G_TouchDown() { Grounded = true; }
        public void SFEXT_G_TouchDownWater() { Grounded = true; }
        public void SFEXT_G_TakeOff() { Grounded = false; }
        private int PickupTrigger = 0;
        public void SFEXT_O_OnPickupUseDown()
        {
            PickupTrigger = 1;
        }
        public void SFEXT_O_OnPickupUseUp()
        {
            PickupTrigger = 0;
        }
        public void SFEXT_O_OnPickup()
        {
            SFEXT_O_PilotEnter();
        }
        public void SFEXT_O_OnDrop()
        {
            SFEXT_O_PilotExit();
            PickupTrigger = 0;
        }
        public void SFEXT_G_OnPickup() { SFEXT_G_PilotEnter(); }
        public void SFEXT_G_OnDrop() { SFEXT_G_PilotExit(); }
        //hud stuff
        public Transform TargetIndicator;
        public Transform GUNLeadIndicator;
        [Range(0.01f, 1)]
        [Tooltip("1 = max accuracy, 0.01 = smooth but innacurate")]
        [SerializeField] private float GunLeadResponsiveness = 1f;
        private float GUN_TargetSpeedLerper;
        private Vector3 RelativeTargetVelLastFrame;
        private Vector3 RelativeTargetVel;
        private Vector3 GUN_TargetDirOld;
        private float distance_from_head;
        [Tooltip("Put the speed from the bullet particle system in here so that the lead indicator works with the correct offset")]
        public float BulletSpeed;
        private void Hud()
        {
            if (GUNHasTarget)
            {
                Vector3 HudControlPosition = HUDControl ? HUDControl.transform.position : Networking.LocalPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
                if (TargetIndicator)
                {
                    //Target Indicator
                    TargetIndicator.gameObject.SetActive(true);
                    TargetIndicator.position = HudControlPosition + AAMCurrentTargetDirection;
                    TargetIndicator.localPosition = TargetIndicator.localPosition.normalized * distance_from_head;
                    TargetIndicator.rotation = Quaternion.LookRotation(TargetIndicator.position - HUDControl.transform.position, TargetingTransform.transform.up);//This makes it not stretch when off to the side by fixing the rotation.
                }

                if (GUNLeadIndicator)
                {
                    //GUN Lead Indicator
                    float deltaTime = Time.deltaTime;
                    GUNLeadIndicator.gameObject.SetActive(true);
                    Vector3 TargetPos;
                    if (!AAMCurrentTargetSAVControl)//target is a dummy target
                    { TargetPos = AAMTargets[AAMTarget].transform.position; }
                    else
                    { TargetPos = AAMCurrentTargetSAVControl.CenterOfMass.position; }
                    Vector3 TargetDir = TargetPos - HudControlPosition;

                    Vector3 RelativeTargetVel = TargetDir - GUN_TargetDirOld;

                    GUN_TargetDirOld = Vector3.Lerp(GUN_TargetDirOld, TargetDir, GunLeadResponsiveness);
                    GUN_TargetSpeedLerper = RelativeTargetVel.magnitude * GunLeadResponsiveness / deltaTime;

                    float interceptTime = vintercept(HudControlPosition, BulletSpeed, TargetPos, RelativeTargetVel.normalized * GUN_TargetSpeedLerper);
                    Vector3 PredictedPos = (TargetPos + (RelativeTargetVel.normalized * GUN_TargetSpeedLerper) * interceptTime);

                    //Bulletdrop, technically incorrect implementation because it should be integrated into vintercept() but that'd be very difficult
                    Vector3 gravity = new Vector3(0, -Physics.gravity.y * .5f * interceptTime * interceptTime, 0);
                    // Vector3 TargetAccel = RelativeTargetVel - RelativeTargetVelLastFrame;
                    // Vector3 accel = ((TargetAccel / Time.deltaTime) * 0.5f * interceptTime * interceptTime); // accel causes jitter
                    PredictedPos += gravity /* + accel */;

                    GUNLeadIndicator.position = PredictedPos;
                    //move lead indicator to match the distance of the rest of the hud
                    GUNLeadIndicator.localPosition = GUNLeadIndicator.localPosition.normalized * distance_from_head;
                    GUNLeadIndicator.rotation = Quaternion.LookRotation(GUNLeadIndicator.position - HudControlPosition, TargetingTransform.transform.up);//This makes it not stretch when off to the side by fixing the rotation.

                    RelativeTargetVelLastFrame = RelativeTargetVel;
                }
            }
            else
            {
                if (TargetIndicator)
                { TargetIndicator.gameObject.SetActive(false); }
                if (GUNLeadIndicator)
                { GUNLeadIndicator.gameObject.SetActive(false); }
            }
            /////////////////
        }

        //not mine
        float vintercept(Vector3 fireorg, float missilespeed, Vector3 tgtorg, Vector3 tgtvel)
        {
            if (missilespeed <= 0)
                return (tgtorg - fireorg).magnitude / missilespeed;

            float tgtspd = tgtvel.magnitude;
            Vector3 dir = fireorg - tgtorg;
            float d = dir.magnitude;
            float a = missilespeed * missilespeed - tgtspd * tgtspd;
            float b = 2 * Vector3.Dot(dir, tgtvel);
            float c = -d * d;

            float t = 0;
            if (a == 0)
            {
                if (b == 0)
                    return 0f;
                else
                    t = -c / b;
            }
            else
            {
                float s0 = b * b - 4 * a * c;
                if (s0 <= 0)
                    return 0f;
                float s = Mathf.Sqrt(s0);
                float div = 1.0f / (2f * a);
                float t1 = -(s + b) * div;
                float t2 = (s - b) * div;
                if (t1 <= 0 && t2 <= 0)
                    return 0f;
                t = (t1 > 0 && t2 > 0) ? Mathf.Min(t1, t2) : Mathf.Max(t1, t2);
            }
            return t;
        }
        public void SetBoolOn()
        {
            AnimOn = true;
            GunAnimator.SetBool(AnimBoolName, AnimOn);
        }
        public void SetBoolOff()
        {
            AnimOn = false;
            GunAnimator.SetBool(AnimBoolName, AnimOn);
        }
        public void KeyboardInput()
        {
            if (EntityControl.VehicleSeats[EntityControl.MySeat].PassengerFunctions)
            {
                EntityControl.VehicleSeats[EntityControl.MySeat].PassengerFunctions.ToggleStickSelection(this);
            }
            else
            {
                EntityControl.ToggleStickSelection(this);
            }
        }
    }
}
