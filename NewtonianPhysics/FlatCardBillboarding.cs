using System.Linq;
using BBI.Unity.Game;
using UnityEngine;

namespace NewtonianPhysics
{
    // Keeps distant flat background cards (GateCard, the Quad/CardRing gate-ring set, and the
    // VillageSalvageStation/VillageWaystation sprite families) at a CONSTANT angle relative to the
    // player once the player strays past BackgroundConstants.PinDistanceMeters (250m) from the
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
    internal static class FlatCardBillboarding
    {
        private static readonly RotationPin GateCardPin = new("GateCard");
        private static readonly RotationPin QuadPin = new("Quad");
        private static readonly RotationPin Quad1Pin = new("Quad (1)");
        private static readonly RotationPin Quad2Pin = new("Quad (2)");

        // VillageSalvageStation instances share a fixed authored rotation across most of them
        // (confirmed via F9: forward=(0,0,1) for the large majority) - each is pinned
        // independently since they're scattered at different positions/existing angles, not a
        // single shared object.
        private static RotationPin[]? _salvageStationPins;

        // Same bulk-scan/independent-pin treatment as VillageSalvageStation - see the class-level
        // comment above for why VillageWaystation needs this despite not always LOOKING flat in
        // its world-space bounds.
        private static RotationPin[]? _waystationPins;

        private static Transform? _bayRoot;
        private static bool _cardsScanned;

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
            _waystationPins = null;
            GateCardPin.ClearCache();
            QuadPin.ClearCache();
            Quad1Pin.ClearCache();
            Quad2Pin.ClearCache();
        }

        // Finding the bay root itself is cheap (one Transform scan, needed immediately to know
        // distFromBay at all) and safe to do at load - but the actual card scan (GateCard/Quad
        // set/VillageSalvageStation) is deferred until the player first crosses
        // BackgroundConstants.PinDistanceMeters (250m), rather than running shortly after level
        // load. Confirmed via testing that scanning at load can race the distant PRF_Village
        // hierarchy still streaming in (observed salvageStationCount=0, and Quad/Quad (1)/Quad (2)
        // all missing, on the very first scan) - by the time the player has actually flown 250m
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

            float distFromBay = Vector3.Distance(player.position, _bayRoot.position);
            bool shouldBePinned = distFromBay * distFromBay >= BackgroundConstants.PinDistanceMeters * BackgroundConstants.PinDistanceMeters;

            if (shouldBePinned && !_cardsScanned)
            {
                _cardsScanned = true;
                ScanForCards();
            }

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

        // One-time scan, triggered the first time the player crosses 250m from the bay rather
        // than shortly after level load - by this point the scene has had time to fully stream
        // in, so there's no need for the periodic-retry machinery an earlier version of this file
        // used to work around a load-time race.
        private static void ScanForCards()
        {
            GateCardPin.Reset();
            QuadPin.Reset();
            Quad1Pin.Reset();
            Quad2Pin.Reset();

            // Excludes any path containing "_HAB" - confirmed via a mismatched cardWorldPos that
            // at least one VillageSalvageStation duplicate exists under a "_HAB" hierarchy the
            // same way GateCard does, and an unfiltered scan grabbed that duplicate instead of the
            // real, visible instance.
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
                Plugin.Log.LogInfo($"FlatCardBillboarding: SCAN (triggered at 250m) salvageStationCount={_salvageStationPins.Length} waystationCount={_waystationPins.Length}");
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
