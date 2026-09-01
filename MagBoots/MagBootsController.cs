using System.Collections.Generic;
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

        // _attachNormal is the raw target normal, updated instantly whenever the ahead-cast finds a new
        // surface. _smoothedNormal eases toward it and is what everything else reads from (position,
        // ahead-cast direction) - keeping them on separate paces caused the raycast to fire from a
        // position/direction pair that no longer matched the player, producing jitter around corners.
        private Vector3 _attachNormal;
        private Vector3 _smoothedNormal;
        private Vector3 _attachPoint;
        private Transform? _attachHitTransform;

        // Cached alongside _attachHitTransform (via SetAttach) rather than GetComponent every tick - null
        // for static hull geometry, in which case the surface's own velocity is zero.
        private Rigidbody? _attachHitRigidbody;

        // _attachPoint/_attachNormal above are the world-space values actually used everywhere else in
        // the file - but a moving/rotating hit part (an airlock, a ship section under thrust) would leave
        // them stale, since nothing previously re-derived them from the part's own motion. These track the
        // same point/normal in _attachHitTransform's LOCAL space instead, and RefreshAttachFromHitTransform
        // (called once per tick, before anything else reads _attachPoint/_attachNormal) converts them back
        // to world space via TransformPoint/TransformDirection - so the player rides along with the part.
        // Kept in sync with _attachPoint/_attachNormal/_attachHitTransform at every assignment site.
        private Vector3 _attachLocalPoint;
        private Vector3 _attachLocalNormal;

        // Splits a detected step into two phases: _attachPoint holds still the instant a real step is
        // detected, and the vertical/normal correction only applies once the player physically catches up
        // (via the standoff spring) or Timeout_Step elapses. Without this, a steep staircase combined a
        // full lateral stride with a large vertical drop every tick, compounding downward velocity past
        // BreakawayVelocity.
        private bool _pendingStepCorrection;
        private float _pendingStepTimer;
        private Vector3 _pendingStepPoint;
        private Vector3 _pendingStepNormal;
        private Transform? _pendingStepHitTransform;

        // Mirror of the step-down pending fields above, but inverted: a significant step UP snaps the
        // anchor's height/normal immediately, then HOLDS lateral advance until the player's body catches
        // up (or Timeout_Step elapses) - otherwise it looked like teleporting forward and up onto the tread.
        private bool _pendingStepUpCorrection;
        private float _pendingStepUpTimer;
        private Vector3 _pendingStepUpPoint;
        private Vector3 _pendingStepUpNormal;
        private Transform? _pendingStepUpHitTransform;

        // Eases between 1.0 (standing) and 0.5 (crouched) while the thrust-down input is held, rather
        // than snapping instantly, so crouching in/out of a low gap or under an obstacle feels like a
        // deliberate crouch rather than a jarring pop.
        private float _standoffFraction = 1f;
        private const float CrouchTransitionDuration = 0.25f;

        private float _snapTimer;
        private Vector3 _snapStartPos;
        private Quaternion _snapStartRot;
        private Vector3 _snapTargetPos;
        private Quaternion _snapTargetRot;

        // Rotation-only counterpart to the _snap* tween above, used for a step transition instead of the
        // initial attach - same duration/easing as BeginSnap's tween, but only rotation moves (position
        // keeps following the ordinary attach-point easing already in UpdateAttached) and there's no state
        // change or power draw, since the player is already Locked and walking.
        private bool _rotationSnapActive;
        private float _rotationSnapTimer;
        private Quaternion _rotationSnapStartRot;
        private Quaternion _rotationSnapTargetRot;

        private float _errorTimer;

        private float _batteryMinutesRemaining;

        // Toggle mode flips _runToggledOn on each press; Hold mode drives _runHeld directly from
        // Plugin.Update(). Both gated on IsAttached, so Run can't be silently armed while detached.
        private bool _runToggledOn;
        private bool _runHeld;
        public bool IsRunning => IsAttached && (Plugin.ConfigRunActivationMode.Value == RunActivationMode.Toggle ? _runToggledOn : _runHeld);

        public void OnRunTogglePressed()
        {
            if (!IsAttached)
                return;

            _runToggledOn = !_runToggledOn;
        }

        public void SetRunHeld(bool held) => _runHeld = IsAttached && held;

        public MagBootsState State => _state;
        public bool IsAttached => _state == MagBootsState.Locked;
        public float BatteryFraction => Plugin.ConfigBatteryCapacityMinutes.Value <= 0f
            ? 1f
            : Mathf.Clamp01(_batteryMinutesRemaining / Plugin.ConfigBatteryCapacityMinutes.Value);
        public float BatteryMinutesRemaining => _batteryMinutesRemaining;

        // Stride distance actually used this tick, shown in the HUD so pitch's effect on stride is visible.
        public float CurrentStrideDistance { get; private set; }

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

            // Cast from the viewpoint down through full standing height plus the step-down allowance -
            // not just Height_StepDown alone, since the viewpoint sits PlayerHeight above the feet.
            float castDistance = Plugin.ConfigPlayerHeight.Value + Plugin.ConfigStepDownHeight.Value;

            if (!TryFindValidSurface(origin, castDir, castDistance, Plugin.ConfigMaxNormalAngle.Value, out RaycastHit hit))
            {
                EnterError();
                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo("MagBoots: no valid surface found below player.");
                return;
            }

            // No rigidbody (static hull geometry) means it isn't moving, so relative velocity is just the player's own.
            Vector3 surfaceVelocity = hit.rigidbody != null ? hit.rigidbody.velocity : Vector3.zero;
            float relativeVelocity = (_playerRigidbody.velocity - surfaceVelocity).magnitude;
            if (relativeVelocity > Plugin.ConfigAttachMaxVelocity.Value)
            {
                EnterError();
                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo($"MagBoots: relative velocity {relativeVelocity} exceeded AttachMaxVelocity {Plugin.ConfigAttachMaxVelocity.Value}, refusing to attach.");
                return;
            }

            BeginSnap(hit.point, hit.normal, hit.transform);
        }

        private void EnterError()
        {
            _state = MagBootsState.Error;
            _errorTimer = 0f;
        }

        // Sets _attachPoint/_attachNormal/_attachHitTransform together with their local-space counterparts
        // (see the fields' own comment) - always go through this rather than assigning those fields
        // individually, so a moving/rotating hit part is never silently left out of sync.
        private void SetAttach(Vector3 point, Vector3 normal, Transform? hitTransform)
        {
            _attachPoint = point;
            _attachNormal = normal;
            _attachHitTransform = hitTransform;
            _attachHitRigidbody = hitTransform != null ? hitTransform.GetComponent<Rigidbody>() : null;

            if (hitTransform != null)
            {
                _attachLocalPoint = hitTransform.InverseTransformPoint(point);
                _attachLocalNormal = hitTransform.InverseTransformDirection(normal);
            }
        }

        // Re-derives _attachPoint/_attachNormal from the hit transform's current world position/rotation,
        // so a part that moved or rotated since the last assignment carries the player along with it.
        // Called once per tick, before anything else in UpdateAttached reads _attachPoint/_attachNormal.
        // No-op if there's no hit transform (shouldn't happen while attached, but cheap to guard).
        private void RefreshAttachFromHitTransform()
        {
            if (_attachHitTransform == null)
                return;

            _attachPoint = _attachHitTransform.TransformPoint(_attachLocalPoint);
            _attachNormal = _attachHitTransform.TransformDirection(_attachLocalNormal).normalized;
        }

        private void BeginSnap(Vector3 point, Vector3 normal, Transform hitTransform)
        {
            SetAttach(point, normal, hitTransform);
            _smoothedNormal = normal;
            _standoffFraction = 1f;
            _pendingStepCorrection = false;

            _snapStartPos = _playerRigidbody!.position;
            _snapStartRot = _playerRigidbody.rotation;
            _snapTargetPos = point + normal * Plugin.ConfigPlayerHeight.Value;
            Vector3 currentUp = _snapStartRot * Vector3.up;
            _snapTargetRot = Quaternion.FromToRotation(currentUp, normal) * _snapStartRot;

            _snapTimer = 0f;
            _state = MagBootsState.Locking;

            _playerRigidbody.velocity = Vector3.zero;
            _playerRigidbody.angularVelocity = Vector3.zero;

            // Suppress from the start of the tween, not just once Locked - thrust fighting the snap-in is
            // just as pointless as fighting the standoff spring once attached.
            ThrustSuppression.SetSuppressed(true);

            if (Plugin.ConfigDebugPrint.Value)
                Plugin.Log.LogInfo($"MagBoots: attaching to surface at {point}, normal {normal}.");
        }

        // Same rotation math as BeginSnap, eased over the same SnapDuration - like detaching and
        // immediately reattaching to the new surface, but skipping the state change and power draw.
        // Only rotation tweens here; position keeps following the ordinary attach-point easing.
        private void SnapRotationToNormal(Vector3 normal)
        {
            Quaternion currentRot = _playerRigidbody!.rotation;
            Vector3 currentUp = currentRot * Vector3.up;

            _rotationSnapActive = true;
            _rotationSnapTimer = 0f;
            _rotationSnapStartRot = currentRot;
            _rotationSnapTargetRot = Quaternion.FromToRotation(currentUp, normal) * currentRot;
        }

        private void UpdateRotationSnap()
        {
            _rotationSnapTimer += Time.fixedDeltaTime;
            float duration = Mathf.Max(Plugin.ConfigSnapDuration.Value, 0.01f);
            float t = Mathf.Clamp01(_rotationSnapTimer / duration);
            float eased = Mathf.SmoothStep(0f, 1f, t);

            _playerRigidbody!.MoveRotation(Quaternion.Slerp(_rotationSnapStartRot, _rotationSnapTargetRot, eased));

            if (t >= 1f)
                _rotationSnapActive = false;
        }

        private void Detach()
        {
            _state = MagBootsState.Off;
            _attachHitTransform = null;
            _attachHitRigidbody = null;
            ThrustSuppression.SetSuppressed(false);
            PreciseRotation.SetActive(false);

            // Run never carries over to the next attach - always starts back off.
            _runToggledOn = false;
            _runHeld = false;

            if (Plugin.ConfigDebugPrint.Value)
                Plugin.Log.LogInfo("MagBoots: detached.");
        }

        // Same hook GrabController uses for its own per-shift resets.
        public void OnShiftStart()
        {
            _batteryMinutesRemaining = Plugin.ConfigBatteryCapacityMinutes.Value;
            if (_state != MagBootsState.Off)
                Detach();
        }

        // Plugin stops calling Update/FixedUpdate outside Gameplay - without this, pausing mid-snap would
        // leave the player stuck in that state with stale tween data on resume.
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
                DrainBattery(Plugin.ConfigLockingPowerMultiplier.Value);
                return;
            }

            bool wasStandingStill = !_lastTangentialMoveWasActive;
            UpdateAttached();

            float movementMultiplier = wasStandingStill
                ? Plugin.ConfigIdlePowerMultiplier.Value
                : (IsRunning ? Plugin.ConfigRunSpeed.Value / Plugin.ConfigMoveSpeed.Value : 1f);
            DrainBattery(movementMultiplier);
        }

        // The initial snap drains at LockingPowerMultiplier, so quick attach/detach cycling isn't a free
        // way to dodge battery cost. While attached, idle drains at IdlePowerMultiplier; running scales
        // with Speed_Run/Speed_Walk. Captured from the previous tick's UpdateAttached since tangential
        // input is only read there.
        private bool _lastTangentialMoveWasActive;

        private void DrainBattery(float multiplier)
        {
            if (Plugin.ConfigBatteryCapacityMinutes.Value <= 0f)
                return; // 0 or negative = unlimited/disabled battery.

            _batteryMinutesRemaining -= Time.fixedDeltaTime / 60f * multiplier;
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

                // Only once Locked (unlike ThrustSuppression) - precise rotation would otherwise fight
                // UpdateSnap's own MoveRotation mid-tween.
                PreciseRotation.SetActive(true);

                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo("MagBoots: snap complete, now attached.");
            }
        }

        private void UpdateAttached()
        {
            Rigidbody rb = _playerRigidbody!;
            Transform playerTransform = rb.transform;

            // A strong enough impact should knock the player free instead of the spring always winning -
            // its restoring force scales unboundedly, so without this nothing could ever pull free.
            if (rb.velocity.magnitude > Plugin.ConfigBreakawayVelocity.Value)
            {
                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo($"MagBoots: breakaway - velocity={rb.velocity.magnitude} exceeded {Plugin.ConfigBreakawayVelocity.Value}, detaching.");
                Detach();
                return;
            }

            // Carry the player along with a moving/rotating hit part before anything below reads
            // _attachPoint/_attachNormal - see the fields' own comment.
            RefreshAttachFromHitTransform();

            // Ease toward the raw target (_attachNormal); rotation, move direction, the ahead-cast, and
            // the standoff offset all derive from this smoothed value so they transition together.
            _smoothedNormal = Vector3.Slerp(_smoothedNormal, _attachNormal, Time.fixedDeltaTime * Plugin.ConfigCornerSmoothingSpeed.Value).normalized;

            Vector3 rawForward = playerTransform.forward;
            Vector3 flatForward = Vector3.ProjectOnPlane(rawForward, _smoothedNormal);

            // While a step-transition rotation snap is tweening, skip the ongoing re-level below - it
            // rebuilds rotation from scratch every tick and would fight the tween.
            bool isReorienting = false;
            float clampedPitch = 0f;
            if (_rotationSnapActive)
            {
                UpdateRotationSnap();
            }
            else
            {
                // Re-orient roll/yaw to the smoothed normal without touching pitch: rebuild from the
                // player's flattened forward plus the normal as up, then re-apply pitch (clamped to
                // Pitch_MaxLookDown) on top. Settle check compares against this level orientation, not the
                // full rotation, since pitch alone would otherwise always read as "still reorienting."
                if (flatForward.sqrMagnitude > 0.0001f)
                {
                    flatForward.Normalize();
                    Vector3 rightAxis = Vector3.Cross(_smoothedNormal, flatForward).normalized;
                    float pitchAngle = Vector3.SignedAngle(flatForward, rawForward, rightAxis);
                    // Looking up has no limit; MaxLookUpAngle just keeps the near-90-degree degenerate
                    // case (flatForward collapsing) out of reach - not a player-facing tunable.
                    const float MaxLookUpAngle = 85f;
                    clampedPitch = Mathf.Clamp(pitchAngle, -MaxLookUpAngle, Plugin.ConfigMaxLookDownAngle.Value);

                    Quaternion levelRot = Quaternion.LookRotation(flatForward, _smoothedNormal);
                    Quaternion pitchRot = Quaternion.AngleAxis(clampedPitch, rightAxis);
                    Quaternion targetRot = pitchRot * levelRot;

                    isReorienting = Quaternion.Angle(playerTransform.rotation, targetRot) > Plugin.ConfigReorientSettledAngle.Value;
                    rb.MoveRotation(Quaternion.Slerp(playerTransform.rotation, targetRot, Time.fixedDeltaTime * 10f));
                }
            }

            Vector3 tangentialMove = ReadTangentialMoveInput(playerTransform, _smoothedNormal);
            _lastTangentialMoveWasActive = tangentialMove.sqrMagnitude > 0.0001f;

            float moveIntensity = Mathf.Clamp01(tangentialMove.magnitude);

            // Pitching the view up or down shortens the stride, like a person taking smaller steps while
            // looking down at uneven ground. No change within +/-Pitch_MaxStride degrees of level; falls
            // off linearly to a 0.01m floor at +/-Pitch_MinStride (an absolute distance, not a fraction of
            // Distance_MaxStride, so it stays meaningful if that's reconfigured).
            const float MinPitchStrideDistance = 0.01f;
            float pitchMagnitude = Mathf.Abs(clampedPitch);
            float pitchLerpAmount = Mathf.InverseLerp(Plugin.ConfigMaxStridePitch.Value, Plugin.ConfigMinStridePitch.Value, pitchMagnitude);
            float pitchAdjustedCastDistance = Mathf.Lerp(Plugin.ConfigAheadCastDistance.Value, MinPitchStrideDistance, pitchLerpAmount);

            // Preview value only, so the HUD shows pitch's effect even while standing still (moveIntensity
            // would otherwise always read 0). Overwritten below with the real search value once moving.
            CurrentStrideDistance = pitchAdjustedCastDistance;

            // rb.position is the helmet/POV; undo the standoff offset to get the actual feet-plane
            // position. The search reads from this, never _attachPoint, which can already be ahead of the
            // player's real body from a prior committed step.
            Vector3 feetPosition = rb.position - _smoothedNormal * (Plugin.ConfigPlayerHeight.Value * _standoffFraction);

            // While a step's vertical correction is pending, _attachPoint holds still - the queued
            // height/normal snap applies once the player physically catches up (standoff spring) or
            // Timeout_Step elapses, since continuous movement could otherwise keep the catch-up fraction
            // from ever fully closing.
            if (_pendingStepCorrection)
            {
                _pendingStepTimer += Time.fixedDeltaTime;

                // Lateral-only distance to the held anchor - the along-normal gap (still old, pre-step) is
                // expected and irrelevant; only whether the spring has caught up laterally matters here.
                Vector3 lateralError = Vector3.ProjectOnPlane(_attachPoint - rb.position, _attachNormal);
                float settleDistance = Plugin.ConfigAheadCastDistance.Value * Plugin.ConfigStepLateralSettled.Value;
                bool lateralSettled = lateralError.magnitude <= settleDistance;
                bool timedOut = _pendingStepTimer >= Plugin.ConfigStepTimeout.Value;

                if (lateralSettled || timedOut)
                {
                    Vector3 offset = _attachPoint - _pendingStepPoint;
                    Vector3 resolvedPoint = _attachPoint - Vector3.Dot(offset, _pendingStepNormal) * _pendingStepNormal;
                    SnapRotationToNormal(_pendingStepNormal);
                    SetAttach(resolvedPoint, _pendingStepNormal, _pendingStepHitTransform);
                    _pendingStepCorrection = false;

                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"MagBoots: deferred step correction applied - lateralError={lateralError.magnitude}, timedOut={timedOut}");
                }
            }
            // Mirror of the step-down block, but height/normal already snapped when the step up was
            // detected - what's held here is lateral advance, released once the player catches up to the
            // new standoff height (or Timeout_Step elapses).
            else if (_pendingStepUpCorrection)
            {
                _pendingStepUpTimer += Time.fixedDeltaTime;

                // Distance to the STANDOFF height above the anchor, not the anchor point itself - comparing
                // directly against _attachPoint always showed a ~PlayerHeight gap that could never settle.
                Vector3 standoffTarget = _attachPoint + _attachNormal * (Plugin.ConfigPlayerHeight.Value * _standoffFraction);
                float heightError = Mathf.Abs(Vector3.Dot(standoffTarget - rb.position, _attachNormal));
                float settleDistance = Plugin.ConfigAheadCastDistance.Value * Plugin.ConfigStepUpSettled.Value;
                bool heightSettled = heightError <= settleDistance;
                bool timedOut = _pendingStepUpTimer >= Plugin.ConfigStepTimeout.Value;

                if (heightSettled || timedOut)
                {
                    _pendingStepUpCorrection = false;

                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"MagBoots: deferred step-up lateral release - heightError={heightError}, timedOut={timedOut}");
                }
            }
            // Pause looking for the next surface while still catching up to the last normal change -
            // recasting mid-reorientation was casting at a still-rotating angle and could pick up a
            // slightly different/adjacent face before settling, producing visible jitter.
            else if (!isReorienting && tangentialMove.sqrMagnitude > 0.0001f)
            {
                // Scale cast distance down for a gentle analog push rather than always probing the full
                // Distance_MaxStride - clamped to 1 since a full diagonal push can exceed unit length.
                // Combines with the pitch-based distance so neither overrides the other.
                float maxCastDistance = pitchAdjustedCastDistance * moveIntensity;
                CurrentStrideDistance = maxCastDistance;

                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo($"MagBoots: pitch stride scale - clampedPitch={clampedPitch}, pitchAdjustedCastDistance={pitchAdjustedCastDistance}, maxCastDistance={maxCastDistance}");

                // A foothold within Angle_FwdSweep of the player's facing gets the looser
                // Angle_MaxNormalFwd/Height_StepUpFwd instead of the strict values. Uses flatForward rather
                // than the raw camera forward, which collapses toward zero when pitching down and made
                // this check incorrectly fall back to the strict values while looking at the ground.
                float halfSweep = Plugin.ConfigFwdSweepAngle.Value * 0.5f;
                bool inFwdSweep = flatForward.sqrMagnitude > 0.0001f &&
                    Vector3.Angle(tangentialMove, flatForward) <= halfSweep;
                float maxNormalAngle = inFwdSweep ? Plugin.ConfigMaxNormalFwdAngle.Value : Plugin.ConfigMaxNormalAngle.Value;
                float stepUpHeight = inFwdSweep ? Plugin.ConfigStepUpFwdHeight.Value : Plugin.ConfigStepUpHeight.Value;

                // Probe starts stepUpHeight above the feet-plane and casts down through
                // stepUpHeight + Height_StepDown, covering both a step up and step down in one raycast.
                Vector3 stepUpOffset = _smoothedNormal * stepUpHeight;
                float aheadDepth = stepUpHeight + Plugin.ConfigStepDownHeight.Value;

                bool foundSurface;
                RaycastHit aheadHit = default;
                float hitCastDistance = 0f;

                // Full near-to-far ranked candidate search every tick, rather than a cheaper single
                // far-probe fast path - that missed the benefit of comparing against farther candidates
                // whenever a nearby surface was misleading.
                List<StepCandidate> candidates = FindStepCandidates(feetPosition, tangentialMove, stepUpOffset, aheadDepth, maxNormalAngle, maxCastDistance, clampedPitch);
                foundSurface = candidates.Count > 0;

                if (foundSurface)
                {
                    StepCandidate winner = candidates[0];
                    aheadHit = winner.Hit;
                    hitCastDistance = winner.CastDistance;
                }

                if (foundSurface)
                {
                    // Advance the anchor only as far as the successful probe's step distance, not the full
                    // requested stride, scaled as a fraction of the full stride so a step reeled in near a
                    // ledge moves the player correspondingly less.
                    float strideFraction = maxCastDistance > 0.0001f ? hitCastDistance / maxCastDistance : 0f;
                    float moveSpeed = IsRunning ? Plugin.ConfigRunSpeed.Value : Plugin.ConfigMoveSpeed.Value;
                    Vector3 fullStride = tangentialMove * moveSpeed * Time.fixedDeltaTime;
                    // Advance from feetPosition, not _attachPoint - the damper's target velocity is fed
                    // forward from _attachPoint's own motion instead, so zero lateral lead here doesn't
                    // cap effective walking speed the way it used to.
                    Vector3 candidatePoint = feetPosition + fullStride * strideFraction;

                    // How far the new hit deviates vertically (along the CURRENT normal) from the plane the
                    // player is already standing on - positive means the hit is further along +normal, i.e.
                    // a step UP (hit.point sits higher, in the outward-normal direction, than feetPosition);
                    // negative means a step DOWN.
                    float signedHeightDeviation = Vector3.Dot(aheadHit.point - feetPosition, _attachNormal);
                    float heightDeviation = Mathf.Abs(signedHeightDeviation);

                    // A significant step DOWN holds-then-snaps to avoid a full lateral stride and a large
                    // vertical drop compounding downward velocity past BreakawayVelocity. A significant
                    // step UP is the mirror: snap immediately (no downward-velocity risk), but hold lateral
                    // advance until the player catches up. Anything below Height_StepSignificant falls
                    // through to the ordinary eased branch.
                    if (signedHeightDeviation < -Plugin.ConfigStepSignificantHeight.Value)
                    {
                        // Advance the anchor laterally only this tick (project onto the CURRENT plane, so
                        // height doesn't change yet), and queue the vertical correction for once input
                        // eases off, on a separate tick from the lateral move.
                        Vector3 advancedPoint = candidatePoint - Vector3.Dot(candidatePoint - feetPosition, _attachNormal) * _attachNormal;
                        SetAttach(advancedPoint, _attachNormal, _attachHitTransform);

                        _pendingStepCorrection = true;
                        _pendingStepTimer = 0f;
                        _pendingStepPoint = aheadHit.point;
                        _pendingStepNormal = aheadHit.normal;
                        _pendingStepHitTransform = aheadHit.transform;

                        if (Plugin.ConfigDebugPrint.Value)
                            Plugin.Log.LogInfo($"MagBoots: step detected, deferring vertical correction - signedHeightDeviation={signedHeightDeviation}, moveIntensity={moveIntensity}, hitCastDistance={hitCastDistance}");
                    }
                    else if (signedHeightDeviation > Plugin.ConfigStepSignificantHeight.Value)
                    {
                        // Snap height/normal immediately, but hold lateral advance at the CURRENT position
                        // (not candidatePoint) so the player doesn't also jump forward the full stride.
                        // Catch-up handled in the pending-state block above.
                        Vector3 currentLateral = feetPosition - Vector3.Dot(feetPosition, aheadHit.normal) * aheadHit.normal;
                        float targetHeightUp = Vector3.Dot(aheadHit.point, aheadHit.normal);
                        SnapRotationToNormal(aheadHit.normal);
                        SetAttach(currentLateral + targetHeightUp * aheadHit.normal, aheadHit.normal, aheadHit.transform);

                        _pendingStepUpCorrection = true;
                        _pendingStepUpTimer = 0f;
                        _pendingStepUpPoint = aheadHit.point;
                        _pendingStepUpNormal = aheadHit.normal;
                        _pendingStepUpHitTransform = aheadHit.transform;

                        if (Plugin.ConfigDebugPrint.Value)
                            Plugin.Log.LogInfo($"MagBoots: step up detected, snapping height and deferring lateral - signedHeightDeviation={signedHeightDeviation}, moveIntensity={moveIntensity}, hitCastDistance={hitCastDistance}");
                    }
                    else
                    {
                        // Point-onto-plane projection: subtract the component of (candidatePoint -
                        // aheadHit.point) along the new normal, keeping the incrementally-advanced lateral
                        // position but adopting the new surface's height.
                        Vector3 offset = candidatePoint - aheadHit.point;
                        Vector3 targetAttachPoint = candidatePoint - Vector3.Dot(offset, aheadHit.normal) * aheadHit.normal;

                        // Ease the height component toward the new plane at a bounded rate rather than
                        // snapping - a large one-tick jump here (a small ledge just under
                        // Height_StepSignificant) still yanked the spring hard otherwise. Lateral position
                        // is applied immediately, same as always.
                        Vector3 targetLateral = targetAttachPoint - Vector3.Dot(targetAttachPoint, aheadHit.normal) * aheadHit.normal;
                        float currentHeight = Vector3.Dot(_attachPoint, aheadHit.normal);
                        float targetHeight = Vector3.Dot(targetAttachPoint, aheadHit.normal);
                        float easedHeight = Mathf.MoveTowards(currentHeight, targetHeight,
                            Plugin.ConfigAttachHeightFollowSpeed.Value * Time.fixedDeltaTime);
                        SetAttach(targetLateral + easedHeight * aheadHit.normal, aheadHit.normal, aheadHit.transform);

                        if (Plugin.ConfigDebugPrint.Value)
                        {
                            Plugin.Log.LogInfo($"MagBoots: ahead-cast hit - moveIntensity={moveIntensity}, hitCastDistance={hitCastDistance}, normalAngle={Vector3.Angle(aheadHit.normal, _smoothedNormal)}, collider={(aheadHit.collider != null ? aheadHit.collider.name : "null")}, hitPoint={aheadHit.point}");
                            Plugin.Log.LogInfo($"MagBoots: attach height eased - currentHeight={currentHeight}, targetHeight={targetHeight}, easedHeight={easedHeight}");
                        }
                    }
                }
                else
                {
                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"MagBoots: ahead-cast MISSED at all step distances - moveIntensity={moveIntensity}, maxCastDistance={maxCastDistance}, position={rb.position}, dir={-_smoothedNormal}, maxDepth={aheadDepth}, moveDir={tangentialMove.normalized}, rawForward={rawForward}, feetPosition={feetPosition}, attachPoint={_attachPoint}, attachNormal={_attachNormal}, velocity={rb.velocity}, speed={rb.velocity.magnitude}");

                    // Recovery fallback: a total miss otherwise leaves _attachPoint frozen forever, since
                    // the ordinary search only updates it on success - a closed loop with no exit. Cast
                    // straight down from rb.position with a generous depth to re-anchor to whatever the
                    // player is actually resting on, giving the ordinary search a fresh height next tick.
                    float fallbackDepth = Plugin.ConfigPlayerHeight.Value + Plugin.ConfigStepDownHeight.Value;
                    bool fallbackHit = TryFindValidSurface(rb.position, -_smoothedNormal, fallbackDepth, maxNormalAngle, out RaycastHit recoveryHit, out string fallbackMissReason);

                    if (Plugin.ConfigDebugPrint.Value)
                    {
                        if (fallbackHit)
                            Plugin.Log.LogInfo($"MagBoots: stuck recovery - re-anchored to collider={(recoveryHit.collider != null ? recoveryHit.collider.name : "null")}, hitPoint={recoveryHit.point}, normal={recoveryHit.normal}, oldAttachPoint={_attachPoint}, oldAttachNormal={_attachNormal}");
                        else
                            Plugin.Log.LogInfo($"MagBoots: stuck recovery FAILED - no surface within {fallbackDepth}m straight below rb.position={rb.position}, reason={fallbackMissReason}");
                    }

                    if (fallbackHit)
                        SetAttach(recoveryHit.point, recoveryHit.normal, recoveryHit.transform);
                    // else: nothing below even at this depth - hold last valid attach point/normal.
                }
            }

            if (Plugin.ConfigDebugPrint.Value)
            {
                float attachHeight = Vector3.Dot(_attachPoint, _attachNormal);
                Plugin.Log.LogInfo($"MagBoots: chart - feetPosition={feetPosition}, rbPosition={rb.position}, attachHeight={attachHeight}, tangentialMove={tangentialMove}, rawForward={rawForward}, attachPoint={_attachPoint}, attachNormal={_attachNormal}, velocity={rb.velocity}, speed={rb.velocity.magnitude}");
            }

            // While thrust-down is held, ease the standoff distance toward half its value instead of
            // snapping, so crouching under an obstacle feels deliberate rather than a jarring pop.
            float verticalAxis = LynxControls.Instance.GetOneAxisInputControlValue(GameplayActions.GameplayActionSet.ThrustMoveUpDownComposite);
            bool crouchHeld = verticalAxis < -0.0001f;
            float targetStandoffFraction = crouchHeld ? 0.5f : 1f;
            _standoffFraction = Mathf.MoveTowards(_standoffFraction, targetStandoffFraction, Time.fixedDeltaTime / CrouchTransitionDuration);

            Vector3 desiredPos = _attachPoint + _smoothedNormal * (Plugin.ConfigPlayerHeight.Value * _standoffFraction);

            // Feed-forward: target the damper at the INTENDED velocity (tangentialMove * moveSpeed), not
            // derived from _attachPoint's own delta - differencing that would bake in any velocity
            // overshoot and feed an inflated target back into the damper, an unbounded runaway that let
            // the player accelerate past BreakawayVelocity. A fixed target can't bootstrap that runaway.
            // Adds the hit part's own velocity (zero for static hull geometry) so the damper targets
            // riding along with a moving surface instead of fighting to hold a fixed world position while
            // it drifts away - without this, only the spring's positional term pulled the player back,
            // producing a constant drag/lag against a moving or accelerating part.
            float desiredSpeed = IsRunning ? Plugin.ConfigRunSpeed.Value : Plugin.ConfigMoveSpeed.Value;
            Vector3 surfaceVelocity = _attachHitRigidbody != null ? _attachHitRigidbody.velocity : Vector3.zero;
            Vector3 desiredVelocity = tangentialMove * desiredSpeed + surfaceVelocity;

            Vector3 springForce = Plugin.ConfigSpring.Value * (desiredPos - rb.position)
                                   + Plugin.ConfigDamper.Value * (desiredVelocity - rb.velocity);
            rb.AddForce(springForce, ForceMode.Acceleration);

            if (Plugin.ConfigDebugPrint.Value)
            {
                Plugin.Log.LogInfo($"MagBoots: spring - desiredPos={desiredPos}, desiredVelocity={desiredVelocity}, springForce={springForce}, springForceMag={springForce.magnitude}, positionError={(desiredPos - rb.position).magnitude}");
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

        private readonly struct StepCandidate
        {
            public readonly RaycastHit Hit;
            public readonly float CastDistance;
            public readonly float RoundedHeight;

            public StepCandidate(RaycastHit hit, float castDistance, float roundedHeight)
            {
                Hit = hit;
                CastDistance = castDistance;
                RoundedHeight = roundedHeight;
            }
        }

        // Casts at every stride increment and ranks every valid surface found, rather than using whichever
        // a single far-probe search hit first - on steep stairs that could land two or three treads down
        // instead of the very next one. Height deviation is rounded to the nearest 0.1m so near-equal
        // heights sort as the same tread. Sorted highest first, farthest-distance breaking ties: stepping
        // up, the highest reachable tread is also nearest; stepping down, the highest candidate is the
        // near edge of the current landing.
        private List<StepCandidate> FindStepCandidates(Vector3 feetPosition, Vector3 tangentialMove, Vector3 stepUpOffset,
            float aheadDepth, float maxNormalAngle, float maxCastDistance, float clampedPitch)
        {
            var candidates = new List<StepCandidate>();
            var missLog = Plugin.ConfigDebugPrint.Value ? new List<string>() : null;
            Vector3 moveDir = tangentialMove.normalized;

            const int castSteps = 10;
            for (int i = 1; i <= castSteps; i++)
            {
                float castDistance = maxCastDistance * i / castSteps;
                Vector3 aheadOrigin = feetPosition + moveDir * castDistance + stepUpOffset;
                if (!TryFindValidSurface(aheadOrigin, -_smoothedNormal, aheadDepth, maxNormalAngle, out RaycastHit candidateHit, out string missReason))
                {
                    missLog?.Add($"({castDistance:F3}, {missReason}, origin={aheadOrigin})");
                    continue;
                }

                float candidateHeight = Vector3.Dot(candidateHit.point - feetPosition, _attachNormal);
                float roundedHeight = Mathf.Round(candidateHeight * 10f) / 10f;
                candidates.Add(new StepCandidate(candidateHit, castDistance, roundedHeight));
            }

            if (missLog != null && missLog.Count > 0)
                Plugin.Log.LogInfo("MagBoots: candidate misses (aheadDist, reason): " + string.Join(", ", missLog));

            // Direction is decided by where the player is looking, not the candidates' own heights - looking
            // up or level means heading up a step, looking down means heading down, like tilting your head
            // while climbing real stairs. clampedPitch is positive when looking down, so it's negated here
            // to match "up-or-level (>=0) -> step up."
            bool isStepUpGroup = -clampedPitch >= 0f;

            if (isStepUpGroup)
            {
                // Highest (most-up) first; farthest distance breaks ties.
                candidates.Sort((a, b) =>
                {
                    int heightCompare = b.RoundedHeight.CompareTo(a.RoundedHeight);
                    return heightCompare != 0 ? heightCompare : b.CastDistance.CompareTo(a.CastDistance);
                });
            }
            else
            {
                // Highest (least-down/most-up) first; farthest distance breaks ties.
                candidates.Sort((a, b) =>
                {
                    int heightCompare = a.RoundedHeight.CompareTo(b.RoundedHeight);
                    return heightCompare != 0 ? heightCompare : b.CastDistance.CompareTo(a.CastDistance);
                });
            }

            if (Plugin.ConfigDebugPrint.Value && candidates.Count > 0 && candidates[0].RoundedHeight != 0f)
            {
                var rows = new List<StepCandidate>(candidates);
                rows.Sort((a, b) => a.CastDistance.CompareTo(b.CastDistance));

                var sb = new System.Text.StringBuilder();
                sb.Append("MagBoots: candidate table (aheadDist, heightAboveFeet, collider): ");
                for (int i = 0; i < rows.Count; i++)
                {
                    bool isWinner = rows[i].CastDistance == candidates[0].CastDistance && rows[i].RoundedHeight == candidates[0].RoundedHeight;
                    string colliderName = rows[i].Hit.collider != null ? rows[i].Hit.collider.name : "null";
                    string entry = $"({rows[i].CastDistance:F3}, {rows[i].RoundedHeight:F1}, {colliderName})";
                    if (isWinner)
                        entry = $"**{entry}**";
                    sb.Append(entry);
                    if (i < rows.Count - 1)
                        sb.Append(", ");
                }
                Plugin.Log.LogInfo(sb.ToString());
                Plugin.Log.LogInfo($"MagBoots: winner world position - hitPoint={candidates[0].Hit.point}, collider={(candidates[0].Hit.collider != null ? candidates[0].Hit.collider.name : "null")}");
            }

            return candidates;
        }

        private bool TryFindValidSurface(Vector3 origin, Vector3 direction, float maxDistance, float maxNormalAngle, out RaycastHit result)
        {
            return TryFindValidSurface(origin, direction, maxDistance, maxNormalAngle, out result, out _);
        }

        private bool TryFindValidSurface(Vector3 origin, Vector3 direction, float maxDistance, float maxNormalAngle, out RaycastHit result, out string missReason)
        {
            LayerMask mask = Main.Instance.MainSettings.RaycastSettings.GrabValidLayerMask;

            if (!Physics.Raycast(origin, direction, out RaycastHit hit, maxDistance, mask))
            {
                result = default;
                missReason = "no hit";
                return false;
            }

            float angle = Vector3.Angle(hit.normal, -direction);
            if (angle > maxNormalAngle)
            {
                result = default;
                missReason = $"angle {angle:F1} > {maxNormalAngle:F1}";
                return false;
            }

            if (!SurfaceGeometry.HasMinimumFaceArea(hit, Plugin.ConfigMinFaceArea.Value))
            {
                result = default;
                missReason = "face area too small";
                return false;
            }

            result = hit;
            missReason = null;
            return true;
        }
    }
}
