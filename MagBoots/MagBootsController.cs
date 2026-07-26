using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace MagBoots
{
    internal enum MagBootsState
    {
        Off,
        Locking,
        Locked,
        Error,
        NoPower,
    }

    internal class MagBootsController
    {
        private GrabController? _grabController;
        private Rigidbody? _playerRigidbody;
        private Camera? _playerCamera;

        private MagBootsState _state = MagBootsState.Off;

        private Vector3 _attachNormal;
        private Vector3 _attachPoint;
        private Transform? _attachHitTransform;

        private float _snapTimer;
        private Vector3 _snapStartPos;
        private Quaternion _snapStartRot;
        private Vector3 _snapTargetPos;
        private Quaternion _snapTargetRot;

        private float _errorTimer;

        private float _batteryMinutesRemaining;

        public MagBootsState State => _state;
        public bool IsAttached => _state == MagBootsState.Locked;
        public float BatteryFraction => Plugin.ConfigBatteryCapacityMinutes.Value <= 0f
            ? 1f
            : Mathf.Clamp01(_batteryMinutesRemaining / Plugin.ConfigBatteryCapacityMinutes.Value);
        public float BatteryMinutesRemaining => _batteryMinutesRemaining;

        private bool TryCacheReferences()
        {
            if (_playerRigidbody != null)
                return true;

            _grabController = Object.FindObjectOfType<GrabController>();
            if (_grabController == null)
                return false;

            var traverse = Traverse.Create(_grabController);
            _playerRigidbody = traverse.Field("m_PlayerRigidbodyReference").GetValue<Rigidbody>();
            _playerCamera = traverse.Field("m_PlayerCamera").GetValue<Camera>();
            return _playerRigidbody != null;
        }

        public void OnTogglePressed()
        {
            if (!TryCacheReferences())
            {
                Plugin.Log.LogWarning("MagBoots: could not find player rigidbody, ignoring toggle.");
                return;
            }

            if (_state == MagBootsState.Locked || _state == MagBootsState.Locking)
            {
                Detach();
                return;
            }

            if (BatteryDepleted())
            {
                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo("MagBoots: battery depleted, cannot attach.");
                return; // Stays in/enters NoPower - handled continuously in FixedUpdate.
            }

            TryAttach();
        }

        private bool BatteryDepleted() =>
            Plugin.ConfigBatteryCapacityMinutes.Value > 0f && _batteryMinutesRemaining <= 0f;

        private void TryAttach()
        {
            Transform playerTransform = _playerRigidbody!.transform;
            Vector3 origin = playerTransform.position;
            Vector3 castDir = -playerTransform.up;

            if (!TryFindValidSurface(origin, castDir, Plugin.ConfigMaxAttachDistance.Value, out RaycastHit hit))
            {
                EnterError();
                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo("MagBoots: no valid surface found below player.");
                return;
            }

            BeginSnap(hit.point, hit.normal, hit.transform);
        }

        private void EnterError()
        {
            _state = MagBootsState.Error;
            _errorTimer = 0f;
        }

        private void BeginSnap(Vector3 point, Vector3 normal, Transform hitTransform)
        {
            _attachPoint = point;
            _attachNormal = normal;
            _attachHitTransform = hitTransform;

            _snapStartPos = _playerRigidbody!.position;
            _snapStartRot = _playerRigidbody.rotation;
            _snapTargetPos = point + normal * Plugin.ConfigStandoffDistance.Value;
            Vector3 currentUp = _snapStartRot * Vector3.up;
            _snapTargetRot = Quaternion.FromToRotation(currentUp, normal) * _snapStartRot;

            _snapTimer = 0f;
            _state = MagBootsState.Locking;

            _playerRigidbody.velocity = Vector3.zero;
            _playerRigidbody.angularVelocity = Vector3.zero;

            if (Plugin.ConfigDebugPrint.Value)
                Plugin.Log.LogInfo($"MagBoots: attaching to surface at {point}, normal {normal}.");
        }

        private void Detach()
        {
            _state = MagBootsState.Off;
            _attachHitTransform = null;

            if (Plugin.ConfigDebugPrint.Value)
                Plugin.Log.LogInfo("MagBoots: detached.");
        }

        // Called on the Gameplay-after-LoadingComplete transition (start of a new shift), the same
        // hook GrabController itself uses for its own per-shift resets.
        public void OnShiftStart()
        {
            _batteryMinutesRemaining = Plugin.ConfigBatteryCapacityMinutes.Value;
            if (_state != MagBootsState.Off)
                Detach();
        }

        // Called whenever the game state leaves flight Gameplay (pause, Hab, loading, NIS, etc.), since
        // Plugin stops calling Update/FixedUpdate outside Gameplay - without this, a player who paused
        // mid-snap or mid-attach would stay stuck in that state with stale forces/tween data when they
        // resumed, even though nothing was ticking to progress or clean it up.
        public void OnLeaveGameplay()
        {
            if (_state == MagBootsState.Locking || _state == MagBootsState.Locked)
                Detach();
        }

        public void FixedUpdate()
        {
            if (_state == MagBootsState.Error)
            {
                _errorTimer += Time.fixedDeltaTime;
                if (_errorTimer >= Mathf.Max(Plugin.ConfigSnapDuration.Value, 0.01f))
                    _state = BatteryDepleted() ? MagBootsState.NoPower : MagBootsState.Off;
                return;
            }

            if (_state == MagBootsState.Off || _state == MagBootsState.NoPower)
            {
                // Idle: continuously reflect current battery state so NoPower shows the instant the
                // battery empties, and clears automatically if it's ever topped up (e.g. OnShiftStart).
                _state = BatteryDepleted() ? MagBootsState.NoPower : MagBootsState.Off;
                return;
            }

            if (_state != MagBootsState.Locking && _state != MagBootsState.Locked)
                return;

            if (!TryCacheReferences())
            {
                Detach();
                return;
            }

            if (_state == MagBootsState.Locking)
            {
                UpdateSnap();
                return;
            }

            UpdateAttached();
            DrainBattery();
        }

        private void DrainBattery()
        {
            if (Plugin.ConfigBatteryCapacityMinutes.Value <= 0f)
                return; // 0 or negative = unlimited/disabled battery.

            _batteryMinutesRemaining -= Time.fixedDeltaTime / 60f;
            if (_batteryMinutesRemaining <= 0f)
            {
                _batteryMinutesRemaining = 0f;
                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo("MagBoots: battery depleted, detaching.");
                Detach();
            }
        }

        private void UpdateSnap()
        {
            _snapTimer += Time.fixedDeltaTime;
            float duration = Mathf.Max(Plugin.ConfigSnapDuration.Value, 0.01f);
            float t = Mathf.Clamp01(_snapTimer / duration);
            float eased = Mathf.SmoothStep(0f, 1f, t);

            Vector3 pos = Vector3.Lerp(_snapStartPos, _snapTargetPos, eased);
            Quaternion rot = Quaternion.Slerp(_snapStartRot, _snapTargetRot, eased);

            _playerRigidbody!.MovePosition(pos);
            _playerRigidbody.MoveRotation(rot);

            if (t >= 1f)
            {
                _state = MagBootsState.Locked;
                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo("MagBoots: snap complete, now attached.");
            }
        }

        private void UpdateAttached()
        {
            Rigidbody rb = _playerRigidbody!;
            Transform playerTransform = rb.transform;

            // Re-orient roll/yaw so "down" stays aligned with -attachNormal, without touching pitch:
            // rebuild a target rotation from the player's own *flattened* forward (its pitch component
            // discarded) plus the attach normal as up, then re-apply the original pitch (clamped so the
            // player can't tip past ConfigMaxLookDownAngle toward the surface) on top. This way pitch
            // (torque already applied by the game's own OrientationController) is fully preserved except
            // for the clamp, and only roll/yaw drift relative to the surface - e.g. from walking onto a
            // new face - gets corrected.
            Vector3 rawForward = playerTransform.forward;
            Vector3 flatForward = Vector3.ProjectOnPlane(rawForward, _attachNormal);
            if (flatForward.sqrMagnitude > 0.0001f)
            {
                flatForward.Normalize();
                Vector3 rightAxis = Vector3.Cross(_attachNormal, flatForward).normalized;
                float pitchAngle = Vector3.SignedAngle(flatForward, rawForward, rightAxis);
                float clampedPitch = Mathf.Min(pitchAngle, Plugin.ConfigMaxLookDownAngle.Value);

                Quaternion levelRot = Quaternion.LookRotation(flatForward, _attachNormal);
                Quaternion pitchRot = Quaternion.AngleAxis(clampedPitch, rightAxis);
                Quaternion targetRot = pitchRot * levelRot;

                rb.MoveRotation(Quaternion.Slerp(playerTransform.rotation, targetRot, Time.fixedDeltaTime * 10f));
            }

            Vector3 tangentialMove = ReadTangentialMoveInput(playerTransform, _attachNormal);

            Vector3 desiredPos = _attachPoint + _attachNormal * Plugin.ConfigStandoffDistance.Value + tangentialMove * Plugin.ConfigMoveSpeed.Value * Time.fixedDeltaTime;

            Vector3 springForce = Plugin.ConfigSpring.Value * (desiredPos - rb.position)
                                   + Plugin.ConfigDamper.Value * (Vector3.zero - rb.velocity);
            rb.AddForce(springForce, ForceMode.Acceleration);

            if (tangentialMove.sqrMagnitude > 0.0001f)
            {
                Vector3 aheadOrigin = rb.position + tangentialMove.normalized * Plugin.ConfigAheadCastDistance.Value;
                float aheadDistance = Plugin.ConfigStandoffDistance.Value * 1.5f;
                if (TryFindValidSurface(aheadOrigin, -_attachNormal, aheadDistance, out RaycastHit aheadHit))
                {
                    _attachPoint = aheadHit.point;
                    _attachNormal = aheadHit.normal;
                    _attachHitTransform = aheadHit.transform;
                }
                // else: hold last valid attach point/normal (per design) - do nothing.
            }
        }

        private static Vector3 ReadTangentialMoveInput(Transform playerTransform, Vector3 normal)
        {
            float rightAxis = LynxControls.Instance.GetOneAxisInputControlValue(GameplayActions.GameplayActionSet.ThrustMoveLeftRightComposite);
            float forwardAxis = LynxControls.Instance.GetOneAxisInputControlValue(GameplayActions.GameplayActionSet.ThrustMoveBackForwardComposite);

            Vector3 right = Vector3.ProjectOnPlane(playerTransform.right, normal).normalized;
            Vector3 forward = Vector3.ProjectOnPlane(playerTransform.forward, normal).normalized;

            return right * rightAxis + forward * forwardAxis;
        }

        private bool TryFindValidSurface(Vector3 origin, Vector3 direction, float maxDistance, out RaycastHit result)
        {
            LayerMask mask = Main.Instance.MainSettings.RaycastSettings.GrabValidLayerMask;

            if (!Physics.Raycast(origin, direction, out RaycastHit hit, maxDistance, mask))
            {
                result = default;
                return false;
            }

            float angle = Vector3.Angle(hit.normal, -direction);
            if (angle > Plugin.ConfigMaxNormalAngle.Value)
            {
                result = default;
                return false;
            }

            if (!SurfaceGeometry.HasMinimumFaceArea(hit, Plugin.ConfigMinFaceArea.Value))
            {
                result = default;
                return false;
            }

            result = hit;
            return true;
        }
    }
}
