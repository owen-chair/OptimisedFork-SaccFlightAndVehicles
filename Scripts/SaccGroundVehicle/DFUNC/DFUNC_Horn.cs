
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace SaccFlightAndVehicles
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class DFUNC_Horn : UdonSharpBehaviour
    {
        public AudioSource Horn;
        [System.NonSerializedAttribute] public bool LeftDial = false;
        [System.NonSerializedAttribute] public int DialPosition = -999;
        [System.NonSerializedAttribute] public SaccEntity EntityControl;
        private bool TriggerLastFrame;

        // Network efficiency: avoid broadcasting if nobody is nearby.
        private const float _NearPlayerRadiusSqr = 50f * 50f;
        private const float _PlayerCacheRefreshSeconds = 0.5f;
        private const float _HornNetCooldownSeconds = 1.0f;
        [System.NonSerialized] private VRCPlayerApi[] _playerCache;
        [System.NonSerialized] private float _nextPlayerCacheRefresh;
        [System.NonSerialized] private bool _cachedAnyOtherPlayerNear;
        [System.NonSerialized] private float _nextHornNetTime;

        // UdonSharp CPU: reuse temps instead of per-frame locals.
        private float _tmpNow;
        private Vector3 _tmpCenter;
        private int _tmpPlayerCount;
        private bool _tmpAnyNear;
        private VRCPlayerApi _tmpPlayer;
        private Vector3 _tmpDp;
        private int _i;
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

        private void _PlayHornLocalAndMaybeRemote()
        {
            _tmpNow = Time.time;
            if (Horn) { Horn.Play(); }

            _tmpCenter = (EntityControl != null && EntityControl.CenterOfMass != null)
                ? EntityControl.CenterOfMass.position
                : transform.position;
            _UpdateAnyOtherPlayerNearCache(_tmpCenter);

            // If nobody else is nearby, don't spend bandwidth.
            if (!_cachedAnyOtherPlayerNear) { return; }

            if (_tmpNow < _nextHornNetTime) { return; }
            _nextHornNetTime = _tmpNow + _HornNetCooldownSeconds;

            // Send to Others (local already played).
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.Others, nameof(PlayHorn));
        }

        void Update()
        {
            if (LeftDial)
            { _tmpTrigger = Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryIndexTrigger"); }
            else
            { _tmpTrigger = Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryIndexTrigger"); }

            if (_tmpTrigger > 0.75)
            {
                if (!TriggerLastFrame)
                {
                    _PlayHornLocalAndMaybeRemote();
                }
                TriggerLastFrame = true;
            }
            else { TriggerLastFrame = false; }
        }
        public void PlayHorn()
        {
            if (Horn)
            {
                Horn.Play();
            }
        }
        public void DFUNC_Selected()
        {
            TriggerLastFrame = true;
            gameObject.SetActive(true);
        }
        public void DFUNC_Deselected()
        {
            gameObject.SetActive(false);
        }
        public void SFEXT_O_PilotExit()
        {
            gameObject.SetActive(false);
        }
        public void KeyboardInput()
        {
            _PlayHornLocalAndMaybeRemote();
        }
    }
}