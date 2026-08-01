using System.Linq;
using BBI.Unity.Game;
using UnityEngine;

namespace NewtonianPhysics
{
    // Keeps distant flat background cards (GateCard, the Quad/CardRing gate-ring set, and the
    // VillageSalvageStation/VillageWaystation sprite families) at a CONSTANT angle relative to the
    // player once the player strays past Plugin.ConfigTriggerDistanceMeters from the
    // work bay - these cards are hand-placed at a single fixed world rotation, correct only when
    // viewed from roughly the bay's own vantage point. At NewtonianPhysics's extended flight
    // range, the player can end up viewing them from steep angles (confirmed up to ~73 degrees off
    // face-on for GateCard alone), making a thin flat card appear edge-on/nearly invisible.
    //
    // VillageWaystation added after a follow-on investigation (project_newtonianphysics_todo in
    // memory) into an unidentified "spire looking" object the user spotted - confirmed via texture
    // export that all VillageWaystation instances share ONE texture (a 2048x2048 9-cell angle atlas,
    // spritesScanned=172 uniqueTexturesExported=1), the same angle-baked-atlas authoring pattern as
    // VillageSalvageStation. Despite some instances reporting a near-cubic world-space
    // Renderer.bounds (not the thin/flat signature seen on GateCard/Quad), these are still ordinary
    // SpriteRenderers - a single flat quad has no real depth to produce a genuinely 3D bounding box;
    // a "cubic-looking" AABB here just means the flat quad is rotated away from world axes, which
    // inflates the world-space bounds on axes that would read as thin if the object were axis-
    // aligned. So these need the same angle-pinning fix for the same underlying reason.
    //
    // Deliberately does NOT force full face-on (0 degrees) tracking - the design goal (per the
    // user) is to PIN the angle the card happened to be at relative to the player the moment the
    // 250m trigger fired, and hold that constant from then on, the same "pin whatever state you
    // find it in" philosophy BackgroundParallaxTuning's PlanetPin uses for position. This means a
    // card that started off-axis stays exactly that off-axis, rather than snapping to face-on -
    // only the WORSENING (drifting further edge-on as the player moves) is what gets prevented.
    //
    // Confirmed via F9 diagnostics that none of these objects are statically batched
    // (isPartOfStaticBatch=False across GateCard, all 3 Quad/CardRing instances, and all 34
    // VillageSalvageStation instances found) - unlike SunCard, no procedural-replacement
    // workaround is needed here; direct Transform.rotation writes render correctly.
    //
    // PinningMode ("angle" or "distance", default "distance") - added per user request to offer a
    // second pinning strategy for this SAME object set (GateCard/CardRing/VillageSalvageStation/
    // VillageWaystation only - explicitly NOT Earth/Moon/Sun/glow, which always use
    // BackgroundParallaxTuning's own PlanetPin regardless of this setting). "angle" is the
    // originally-implemented behavior above (rotation locked, position untouched/vanilla).
    // "distance" instead pins each object to a constant distance from the player using the same
    // lateral-offset/asymmetric-approach-vs-recede math PlanetPin uses for Octane/Moon, and does
    // NOT touch rotation at all - the angle-pin logic doesn't even run for an object in this mode.
    // Read once per ScanForCards() (i.e. once per shift, at the 250m trigger) rather than checked
    // live every frame - a mode change mid-shift only takes effect on the next shift/re-trigger,
    // by design (simpler than handling a live mode switch mid-pin, and consistent with how the
    // 250m-deferred scan already works for everything else in this file).
    internal static class FlatCardBillboarding
    {
        private static readonly RotationPin GateCardPin = new("GateCard");
        private static readonly RotationPin QuadPin = new("Quad");
        private static readonly RotationPin Quad1Pin = new("Quad (1)");
        private static readonly RotationPin Quad2Pin = new("Quad (2)");

        private static readonly DistancePin GateCardDistancePin = new("GateCard");
        private static readonly DistancePin QuadDistancePin = new("Quad");
        private static readonly DistancePin Quad1DistancePin = new("Quad (1)");
        private static readonly DistancePin Quad2DistancePin = new("Quad (2)");

        // FX_Gate/FX_Gate_Near are the rail gate's actual laser-fire VFX (Visual Effect Graph
        // particle systems) - confirmed via project_gatefx_investigation that the mesh-based
        // GateFX(N)/GateFXPlaceholder cards are NOT what renders the beam (every scan, including
        // one taken live during an active firing sequence via GateFXDebugTrigger, only ever found
        // GateFX(N) under the inactive PRF_Village_HAB duplicate hierarchy - FX_Gate/FX_Gate_Near
        // are the ones that are actually active=True during firing). Both sit at fixed WORLD
        // positions near GateCard (478m/718m away respectively, ~18 degrees apart as seen from
        // GateCard) but are siblings under PRF_Village/FX_Village, not children of GateCard - so
        // GateCardPin's own rotation write does nothing to them. SatellitePin instead treats each
        // as rigidly attached to GateCard's pivot: it orbits/rotates by the SAME delta GateCard
        // itself has rotated by since pinning activated, rather than being independently angle-
        // pinned to the player like the flat cards are.
        private static readonly SatellitePin FxGatePin = new("FX_Gate", "GateCard");
        private static readonly SatellitePin FxGateNearPin = new("FX_Gate_Near", "GateCard");

        // VillageSalvageStation instances share a fixed authored rotation across most of them
        // (confirmed via F9: forward=(0,0,1) for the large majority) - each is pinned
        // independently since they're scattered at different positions/existing angles, not a
        // single shared object.
        private static RotationPin[]? _salvageStationPins;
        private static DistancePin[]? _salvageStationDistancePins;

        // Same bulk-scan/independent-pin treatment as VillageSalvageStation - see the class-level
        // comment above for why VillageWaystation needs this despite not always LOOKING flat in
        // its world-space bounds.
        private static RotationPin[]? _waystationPins;
        private static DistancePin[]? _waystationDistancePins;

        private static Transform? _bayRoot;
        private static bool _cardsScanned;

        // Cached once per ScanForCards() call (see class-level PinningMode comment above) - which
        // mode this shift's pins were built for, so Tick() knows which pin arrays/objects to drive
        // without re-reading the config every frame.
        private static bool _useAngleMode;

        // Clears all cached scene state so the next Tick() re-scans from scratch - called on the
        // Gameplay/new-shift transition (see Plugin.OnGameStateChanged) since every Transform
        // reference cached here belongs to the PREVIOUS shift's now-destroyed scene otherwise.
        // Confirmed via direct testing this was a real bug: without this reset, angle-pinning
        // silently does nothing on the second and subsequent shifts in the same game session.
        public static void ResetState()
        {
            _bayRoot = null;
            _cardsScanned = false;
            _salvageStationPins = null;
            _salvageStationDistancePins = null;
            _waystationPins = null;
            _waystationDistancePins = null;
            GateCardPin.ClearCache();
            QuadPin.ClearCache();
            Quad1Pin.ClearCache();
            Quad2Pin.ClearCache();
            GateCardDistancePin.ClearCache();
            QuadDistancePin.ClearCache();
            Quad1DistancePin.ClearCache();
            Quad2DistancePin.ClearCache();
            FxGatePin.ClearCache();
            FxGateNearPin.ClearCache();
        }

        // Finding the bay root itself is cheap (one Transform scan, needed immediately to know
        // distFromBay at all) and safe to do at load - but the actual card scan (GateCard/Quad
        // set/VillageSalvageStation) is deferred until the player first crosses
        // Plugin.ConfigTriggerDistanceMeters, rather than running shortly after level load.
        // Confirmed via testing that scanning at load can race the distant PRF_Village hierarchy
        // still streaming in (observed salvageStationCount=0, and Quad/Quad (1)/Quad (2) all
        // missing, on the very first scan) - by the time the player has actually flown that far
        // out, the scene has had time to fully settle, and there's no need to touch any of this
        // machinery at all while the player is still near the bay where none of these cards need
        // pinning yet anyway.
        public static void Tick()
        {
            if (!Plugin.ConfigEnabled.Value || Plugin.ConfigFlatEarthMode.Value)
                return;

            if (_bayRoot == null)
            {
                foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
                {
                    if (t != null && t.parent == null && t.name == BackgroundConstants.BayRootObjectName)
                    {
                        _bayRoot = t;
                        break;
                    }
                }
                if (_bayRoot == null)
                    return;
            }

            Transform? player = LynxCameraController.MainCameraTransform;
            if (player == null)
                return;

            float triggerDistance = Plugin.ConfigTriggerDistanceMeters.Value;
            float distFromBay = Vector3.Distance(player.position, _bayRoot.position);
            bool shouldBePinned = distFromBay * distFromBay >= triggerDistance * triggerDistance;

            if (shouldBePinned && !_cardsScanned)
            {
                _cardsScanned = true;
                ScanForCards();
            }

            if (_useAngleMode)
            {
                GateCardPin.Tick(player, shouldBePinned);
                QuadPin.Tick(player, shouldBePinned);
                Quad1Pin.Tick(player, shouldBePinned);
                Quad2Pin.Tick(player, shouldBePinned);

                if (_salvageStationPins != null)
                {
                    foreach (RotationPin pin in _salvageStationPins)
                        pin.Tick(player, shouldBePinned);
                }

                if (_waystationPins != null)
                {
                    foreach (RotationPin pin in _waystationPins)
                        pin.Tick(player, shouldBePinned);
                }
            }
            else
            {
                GateCardDistancePin.Tick(player, distFromBay, shouldBePinned);
                QuadDistancePin.Tick(player, distFromBay, shouldBePinned);
                Quad1DistancePin.Tick(player, distFromBay, shouldBePinned);
                Quad2DistancePin.Tick(player, distFromBay, shouldBePinned);

                if (_salvageStationDistancePins != null)
                {
                    foreach (DistancePin pin in _salvageStationDistancePins)
                        pin.Tick(player, distFromBay, shouldBePinned);
                }

                if (_waystationDistancePins != null)
                {
                    foreach (DistancePin pin in _waystationDistancePins)
                        pin.Tick(player, distFromBay, shouldBePinned);
                }
            }

            // FX_Gate/FX_Gate_Near always follow GateCard's pivot regardless of mode - in angle
            // mode this reproduces the rotation-sync fix from project_gatefx_investigation; in
            // distance mode GateCard never rotates, so SatellitePin's rotation write becomes a
            // harmless no-op and only the position-follow actually does anything, which is exactly
            // what should happen (the beam VFX should track GateCard's pinned position either way).
            FxGatePin.Tick(shouldBePinned);
            FxGateNearPin.Tick(shouldBePinned);
        }

        // One-time scan, triggered the first time the player crosses 250m from the bay rather
        // than shortly after level load - by this point the scene has had time to fully stream
        // in, so there's no need for the periodic-retry machinery an earlier version of this file
        // used to work around a load-time race.
        private static void ScanForCards()
        {
            _useAngleMode = Plugin.ConfigPinningMode.Value == "angle";

            FxGatePin.Reset();
            FxGateNearPin.Reset();

            if (_useAngleMode)
            {
                GateCardPin.Reset();
                QuadPin.Reset();
                Quad1Pin.Reset();
                Quad2Pin.Reset();

                // Excludes any path containing "_HAB" - confirmed via a mismatched cardWorldPos
                // that at least one VillageSalvageStation duplicate exists under a "_HAB"
                // hierarchy the same way GateCard does, and an unfiltered scan grabbed that
                // duplicate instead of the real, visible instance.
                _salvageStationPins = Resources.FindObjectsOfTypeAll<Transform>()
                    .Where(t => t != null && t.gameObject.activeInHierarchy && t.name.StartsWith("VillageSalvageStation"))
                    .Where(t => !GetFullPath(t).Contains("_HAB"))
                    .Select(t => new RotationPin(t))
                    .ToArray();

                _waystationPins = Resources.FindObjectsOfTypeAll<Transform>()
                    .Where(t => t != null && t.gameObject.activeInHierarchy && t.name.StartsWith("VillageWaystation"))
                    .Where(t => !GetFullPath(t).Contains("_HAB"))
                    .Select(t => new RotationPin(t))
                    .ToArray();

                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo($"FlatCardBillboarding: SCAN (triggered at 250m, mode=angle) salvageStationCount={_salvageStationPins.Length} waystationCount={_waystationPins.Length}");
            }
            else
            {
                GateCardDistancePin.Reset(_bayRoot!);
                QuadDistancePin.Reset(_bayRoot!);
                Quad1DistancePin.Reset(_bayRoot!);
                Quad2DistancePin.Reset(_bayRoot!);

                _salvageStationDistancePins = Resources.FindObjectsOfTypeAll<Transform>()
                    .Where(t => t != null && t.gameObject.activeInHierarchy && t.name.StartsWith("VillageSalvageStation"))
                    .Where(t => !GetFullPath(t).Contains("_HAB"))
                    .Select(t => new DistancePin(t, _bayRoot!))
                    .ToArray();

                _waystationDistancePins = Resources.FindObjectsOfTypeAll<Transform>()
                    .Where(t => t != null && t.gameObject.activeInHierarchy && t.name.StartsWith("VillageWaystation"))
                    .Where(t => !GetFullPath(t).Contains("_HAB"))
                    .Select(t => new DistancePin(t, _bayRoot!))
                    .ToArray();

                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo($"FlatCardBillboarding: SCAN (triggered at 250m, mode=distance) salvageStationCount={_salvageStationDistancePins.Length} waystationCount={_waystationDistancePins.Length}");
            }
        }

        // Encapsulates angle-pin state for a single flat card - captures the rotational offset
        // between the card's forward axis and the direction-to-player the moment the 250m trigger
        // activates, then re-applies that same offset every frame as the player moves, so the
        // card's angle relative to the player never worsens (drifts more edge-on) from whatever it
        // was pinned at.
        private class RotationPin
        {
            private readonly string? _cardObjectName;
            private Transform? _card;

            // Exposed so SatellitePin can read GateCard's live Transform as its pivot/rotation
            // reference without a second independent scan for the same object.
            public Transform? Card => _card;
            private Quaternion _pinnedOffset;
            private bool _isPinned;
            private float _nextThrottledLogTime;

            // Drops the cached card reference and pinned state so the next Reset() re-searches
            // from scratch, instead of trusting a Transform that belongs to a now-destroyed
            // previous shift's scene. Only meaningful for name-based pins (GateCard/Quad set) -
            // the VillageSalvageStation array is rebuilt wholesale by the outer class's
            // ResetState() instead, so those instances are simply discarded, not reused.
            public void ClearCache()
            {
                if (_cardObjectName != null)
                    _card = null;
                _isPinned = false;
            }

            // Name-based constructor: card is (re-)located by exact active-object name search
            // during Reset(), matching PlanetPin's whole-scene search pattern (these cards, like
            // SunCard, aren't all under one common parent worth restricting the search to).
            public RotationPin(string cardObjectName)
            {
                _cardObjectName = cardObjectName;
            }

            // Direct-Transform constructor: used for the VillageSalvageStation set, where the
            // Transform is already known from a one-time bulk scan (searching by name again per
            // instance would be wasteful and ambiguous - many share the same base name).
            public RotationPin(Transform card)
            {
                _card = card;
            }

            public void Reset()
            {
                _isPinned = false;
                if (_cardObjectName == null)
                    return; // pre-supplied Transform (VillageSalvageStation case) - nothing to look up

                // GateCard has a known inactive duplicate under PRF_Village_HAB (confirmed via
                // FlatCardDiagnostic, pos=(-936.1, 542.7, 1690.3) vs the real one's
                // pos=(-99.2, 891.9, 1538.2)) - matching by name + activeInHierarchy alone isn't
                // reliable, since Resources.FindObjectsOfTypeAll has no defined enumeration order
                // and this pin was confirmed (via a mismatched cardWorldPos in the ACTIVATED log
                // line) to have grabbed the _HAB copy instead of the real one. Also excluding any
                // path containing "_HAB" defensively covers the Quad/CardRing set and any future
                // card sharing this same duplication pattern, even though none were confirmed to
                // have a HAB duplicate as of this writing.
                Transform? card = null;
                foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
                {
                    if (t == null || t.name != _cardObjectName || !t.gameObject.activeInHierarchy)
                        continue;
                    if (GetFullPath(t).Contains("_HAB"))
                        continue;
                    card = t;
                    break;
                }
                _card = card;

                if (Plugin.ConfigDebugPrint.Value && _card == null)
                    Plugin.Log.LogInfo($"FlatCardBillboarding[{_cardObjectName}]: SCAN card not found - this pin is inactive until the next rescan.");
                else if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo($"FlatCardBillboarding[{_cardObjectName}]: SCAN found card at path={GetFullPath(_card!)} pos={_card!.position}");
            }

            public void Tick(Transform player, bool shouldBePinned)
            {
                if (_card == null)
                    return;

                if (shouldBePinned && !_isPinned)
                {
                    // Capture the rotational offset between "facing the player directly" and the
                    // card's actual current rotation - re-applying this offset every frame (see
                    // below) preserves exactly the angle the card was at relative to the player
                    // the moment pinning activated, rather than snapping to face-on.
                    Vector3 toPlayer = (player.position - _card.position).normalized;
                    Quaternion lookAtPlayer = Quaternion.LookRotation(toPlayer, Vector3.up);
                    _pinnedOffset = Quaternion.Inverse(lookAtPlayer) * _card.rotation;
                    _isPinned = true;

                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"FlatCardBillboarding[{_card.name}]: ACTIVATED pinning. cardWorldPos={_card.position} playerWorldPos={player.position} pinnedOffset={_pinnedOffset.eulerAngles}");
                }
                else if (!shouldBePinned && _isPinned)
                {
                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"FlatCardBillboarding[{_card.name}]: DEACTIVATED pinning.");
                    _isPinned = false;
                }

                if (!_isPinned)
                    return;

                Vector3 toPlayerNow = (player.position - _card.position).normalized;
                Quaternion lookAtPlayerNow = Quaternion.LookRotation(toPlayerNow, Vector3.up);
                Quaternion newRotation = lookAtPlayerNow * _pinnedOffset;
                _card.rotation = newRotation;

                // Throttled to once/second (not every frame) - just enough to confirm the pin is
                // still actively writing rotation over time, without full per-frame log spam.
                if (Plugin.ConfigDebugPrint.Value && Time.time >= _nextThrottledLogTime)
                {
                    _nextThrottledLogTime = Time.time + 1f;
                    Plugin.Log.LogInfo($"FlatCardBillboarding[{_card.name}]: playerWorldPos={player.position} cardRot={_card.rotation.eulerAngles}");
                }
            }
        }

        // "distance" PinningMode - pins an object to a constant distance from the player instead of
        // locking its angle, reusing the same lateral-offset/asymmetric-approach-vs-recede math
        // BackgroundParallaxTuning's PlanetPin uses for Octane/Moon, but with a FIXED depth axis
        // (the straight bay-to-object direction, captured once at Reset() time) rather than a
        // mesh-bounds-derived axis - simpler and works uniformly across mesh cards (GateCard/Quad)
        // and sprite cards (VillageSalvageStation/VillageWaystation) alike, none of which have the
        // kind of reliable single "flat normal" PlanetPin's axis picker depends on. Never touches
        // rotation at all - RotationPin's angle-pin logic doesn't even run for an object in this
        // mode (see FlatCardBillboarding.Tick()'s _useAngleMode dispatch).
        private class DistancePin
        {
            private readonly string? _cardObjectName;
            private Transform? _card;

            // Exposed so SatellitePin can read GateCard's live Transform as its pivot when
            // distance mode is active, same role RotationPin.Card plays for angle mode.
            public Transform? Card => _card;

            private Vector3 _depthAxis;
            private Vector3 _lateralOffset;
            private float _objectDepth;
            private float _originalObjectDepth;
            private float _previousPlayerDepth;
            private bool _isPinned;
            private float _nextThrottledLogTime;

            public void ClearCache()
            {
                if (_cardObjectName != null)
                    _card = null;
                _isPinned = false;
            }

            // Name-based constructor - matches RotationPin's own two-constructor split (see below
            // for the direct-Transform equivalent).
            public DistancePin(string cardObjectName)
            {
                _cardObjectName = cardObjectName;
            }

            public DistancePin(Transform card, Transform bayRoot)
            {
                _card = card;
                _depthAxis = ComputeDepthAxis(card, bayRoot);
            }

            public void Reset(Transform bayRoot)
            {
                _isPinned = false;
                if (_cardObjectName == null)
                    return; // pre-supplied Transform (VillageSalvageStation/VillageWaystation case)

                // Same _HAB-exclusion whole-scene search pattern as RotationPin.Reset().
                Transform? card = null;
                foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
                {
                    if (t == null || t.name != _cardObjectName || !t.gameObject.activeInHierarchy)
                        continue;
                    if (GetFullPath(t).Contains("_HAB"))
                        continue;
                    card = t;
                    break;
                }
                _card = card;

                if (_card == null)
                {
                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"FlatCardBillboarding[{_cardObjectName}]: SCAN (distance mode) card not found - this pin is inactive until the next rescan.");
                    return;
                }

                _depthAxis = ComputeDepthAxis(_card, bayRoot);

                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo($"FlatCardBillboarding[{_cardObjectName}]: SCAN (distance mode) found card at path={GetFullPath(_card)} pos={_card.position} depthAxis={_depthAxis}");
            }

            // Bay-to-object direction, same fallbackAxis PlanetPin uses when it has no reliable
            // mesh-bounds axis to derive - chosen deliberately over a per-object mesh-bounds picker
            // since it works uniformly regardless of renderer type (SpriteRenderers have no
            // MeshFilter at all) and doesn't depend on any given card's mesh actually having a
            // well-defined flat axis.
            private static Vector3 ComputeDepthAxis(Transform card, Transform bayRoot)
            {
                Vector3 bayToCard = card.position - bayRoot.position;
                return bayToCard.sqrMagnitude > 0.0001f ? bayToCard.normalized : Vector3.forward;
            }

            public void Tick(Transform player, float distFromBay, bool shouldBePinned)
            {
                if (_card == null)
                    return;

                if (shouldBePinned && !_isPinned)
                {
                    Vector3 offset = _card.position - player.position;
                    _lateralOffset = offset - Vector3.Dot(offset, _depthAxis) * _depthAxis;
                    _objectDepth = Vector3.Dot(_card.position, _depthAxis);
                    _originalObjectDepth = _objectDepth;
                    _previousPlayerDepth = Vector3.Dot(player.position, _depthAxis);
                    _isPinned = true;

                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"FlatCardBillboarding[{_card.name}]: ACTIVATED distance pinning. distFromBay={distFromBay} playerWorldPos={player.position} cardWorldPos={_card.position} lateralOffset={_lateralOffset} objectDepth={_objectDepth}");
                }
                else if (!shouldBePinned && _isPinned)
                {
                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"FlatCardBillboarding[{_card.name}]: DEACTIVATED distance pinning.");
                    _isPinned = false;
                }

                if (!_isPinned)
                    return;

                // Same asymmetric-but-bounded approach-vs-recede handling as PlanetPin: approaching
                // tracks the player 1:1 along the depth axis (the object stays exactly as far away,
                // pushed away from the player). Receding PULLS the object back toward wherever it
                // was when pinning first activated (_originalObjectDepth), clamped so it can never
                // overshoot past that original depth - see PlanetPin's matching comment in
                // BackgroundParallaxTuning.cs for the full rationale (fixes a one-way ratchet where
                // repeated approach/retreat cycles would otherwise push the object permanently
                // farther away with no way back, confirmed as a real issue via direct testing).
                float playerDepth = Vector3.Dot(player.position, _depthAxis);
                float playerDepthDelta = playerDepth - _previousPlayerDepth;

                float previousGap = Mathf.Abs(_objectDepth - _previousPlayerDepth);
                float candidateGap = Mathf.Abs(_objectDepth - playerDepth);
                bool isReceding = candidateGap > previousGap;
                if (!isReceding)
                {
                    _objectDepth += playerDepthDelta;
                }
                else
                {
                    float movedTowardOriginal = _objectDepth + playerDepthDelta;
                    bool wasAboveOriginal = _objectDepth >= _originalObjectDepth;
                    _objectDepth = wasAboveOriginal
                        ? Mathf.Max(movedTowardOriginal, _originalObjectDepth)
                        : Mathf.Min(movedTowardOriginal, _originalObjectDepth);
                }

                Vector3 newCardPos = player.position + _lateralOffset + (_objectDepth - playerDepth) * _depthAxis;
                _card.position = newCardPos;
                _previousPlayerDepth = playerDepth;

                if (Plugin.ConfigDebugPrint.Value && Time.time >= _nextThrottledLogTime)
                {
                    _nextThrottledLogTime = Time.time + 1f;
                    Plugin.Log.LogInfo($"FlatCardBillboarding[{_card.name}]: playerWorldPos={player.position} cardWorldPos={_card.position} objectDepth={_objectDepth}");
                }
            }
        }

        // Rigidly follows a "pivot" RotationPin (GateCard) rather than independently angle-pinning
        // to the player - captures its own position/rotation offset RELATIVE TO the pivot's
        // position/rotation the moment pinning activates, then every frame re-applies that same
        // offset against the pivot's CURRENT (possibly rotated) transform. This makes the satellite
        // orbit/rotate exactly as if it were a child of the pivot object, without actually
        // reparenting it (reparenting a live VFX Graph object risks disturbing its own internal
        // simulation-space assumptions, which this avoids entirely).
        private class SatellitePin
        {
            private readonly string _objectName;
            private readonly string _pivotObjectName;
            private Transform? _satellite;
            private Vector3 _localOffsetFromPivot;
            private Quaternion _localRotationFromPivot;
            private bool _isPinned;
            private float _nextThrottledLogTime;

            public SatellitePin(string objectName, string pivotObjectName)
            {
                _objectName = objectName;
                _pivotObjectName = pivotObjectName;
            }

            public void ClearCache()
            {
                _satellite = null;
                _isPinned = false;
            }

            public void Reset()
            {
                _isPinned = false;

                // Same _HAB-exclusion whole-scene search pattern as RotationPin.Reset() - FX_Gate
                // and FX_Gate_Near are both confirmed (via SpireDiagnostic F9, including a scan
                // taken live during an active firing sequence) to have inactive _HAB duplicates,
                // same recurring pattern as GateCard/VillageSalvageStation/planet cards.
                Transform? found = null;
                foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
                {
                    if (t == null || t.name != _objectName || !t.gameObject.activeInHierarchy)
                        continue;
                    if (GetFullPath(t).Contains("_HAB"))
                        continue;
                    found = t;
                    break;
                }
                _satellite = found;

                if (Plugin.ConfigDebugPrint.Value && _satellite == null)
                    Plugin.Log.LogInfo($"FlatCardBillboarding[{_objectName}]: SCAN satellite not found - this pin is inactive until the next rescan.");
                else if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo($"FlatCardBillboarding[{_objectName}]: SCAN found satellite at path={GetFullPath(_satellite!)} pos={_satellite!.position}");
            }

            public void Tick(bool shouldBePinned)
            {
                if (_satellite == null)
                    return;

                // Reads whichever of GateCard's two pins is actually active this shift (see
                // _useAngleMode) - only one of GateCardPin/GateCardDistancePin.Card is ever non-null
                // at a time, since ScanForCards() only Reset()s the pin matching the selected mode.
                Transform? pivot = GateCardPin.Card ?? GateCardDistancePin.Card;
                if (pivot == null)
                    return;

                if (shouldBePinned && !_isPinned)
                {
                    // Capture the satellite's position/rotation expressed RELATIVE TO the pivot's
                    // current transform - Quaternion.Inverse(pivot.rotation) * satellite.rotation is
                    // the satellite's rotation as seen in the pivot's local space, and the inverse-
                    // rotated position delta is the equivalent local-space offset. Re-deriving the
                    // satellite's world transform from these local values against the pivot's LATER
                    // (rotated) transform reproduces a true rigid-child relationship.
                    _localOffsetFromPivot = Quaternion.Inverse(pivot.rotation) * (_satellite.position - pivot.position);
                    _localRotationFromPivot = Quaternion.Inverse(pivot.rotation) * _satellite.rotation;
                    _isPinned = true;

                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"FlatCardBillboarding[{_objectName}]: ACTIVATED satellite pinning to pivot={_pivotObjectName}. satelliteWorldPos={_satellite.position} pivotWorldPos={pivot.position} localOffset={_localOffsetFromPivot}");
                }
                else if (!shouldBePinned && _isPinned)
                {
                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"FlatCardBillboarding[{_objectName}]: DEACTIVATED satellite pinning.");
                    _isPinned = false;
                }

                if (!_isPinned)
                    return;

                _satellite.position = pivot.position + pivot.rotation * _localOffsetFromPivot;
                _satellite.rotation = pivot.rotation * _localRotationFromPivot;

                if (Plugin.ConfigDebugPrint.Value && Time.time >= _nextThrottledLogTime)
                {
                    _nextThrottledLogTime = Time.time + 1f;
                    Plugin.Log.LogInfo($"FlatCardBillboarding[{_objectName}]: satelliteWorldPos={_satellite.position} satelliteRot={_satellite.rotation.eulerAngles} pivotRot={pivot.rotation.eulerAngles}");
                }
            }
        }

        private static string GetFullPath(Transform t)
        {
            string path = t.name;
            Transform? parent = t.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
    }
}
