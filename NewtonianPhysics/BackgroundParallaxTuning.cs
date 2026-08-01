using BBI.Unity.Game;
using UnityEngine;

namespace NewtonianPhysics
{
    // Confirmed via an in-game scene dump (SceneDiagnostics, F9) that Earth/Moon are NOT
    // rendered by a background-compositing camera (_3DBackgroundCam/_3DBackgroundDrawer exist
    // in the decompiled source but have zero live instances in this game) - they're ordinary
    // mesh objects, "PRF_PlanetCard_Octane" (the earth-like planet) and "PRF_PlanetCard_Moon",
    // both parented under a shared root, "PRF_Base_Planets", alongside a large flat glow mesh
    // "PRF_PlanetGlow". Like real astronomical bodies, they sit at a fixed position in the level
    // and are meant to look distant/unmoving - normal vanilla movement never got far enough for
    // that to break down, but this mod's own MaxVelocityMps/WorkAreaRadiusMultiplier let players
    // drift far enough that they visibly recede, which doesn't match how something that far away
    // should look.
    //
    // Each card is a flat disc, not a sphere - per the scene dump, MeshRenderer.bounds are large
    // in two axes but small in the third (confirmed by the disc/"poker chip" edge visible on
    // screen at steep angles). Each card's true face normal is derived directly from the MESH's
    // own local-space bounds (MeshFilter.sharedMesh.bounds - already local/unscaled/unrotated, so
    // its axes map straight to local right/up/forward) rather than assumed to be
    // transform.forward. This is deliberately NOT derived from the player's direction of travel or
    // the bay->planet direction - a player could easily be moving mostly sideways/vertically
    // relative to a given planet even while flying "away" in a broad sense, which would make an
    // axis derived from player motion or position unreliable. The mesh's own modeled orientation
    // is the one thing that's fixed and correct regardless of how the player flies.
    //
    // Octane and Moon are pinned independently (separate PlanetPin instances, each moving only its
    // own card transform) rather than by moving their shared PRF_Base_Planets parent, since the two
    // cards' face normals aren't guaranteed to point the same way and treating them as one rigid
    // group would let one card's depth axis incorrectly govern the other's. This means
    // PRF_PlanetGlow (also a child of the shared parent) is NOT moved by either pin, unlike an
    // earlier version of this fix that moved the shared parent and dragged the glow mesh along
    // with Octane specifically - the glow card stays wherever vanilla placed it now.
    //
    // Matching PRF_PlanetCard_Octane/HAB copy names appear TWICE in the scene (there's also an
    // inactive "PRF_Base_Planets_HAB" copy with a same-named sibling for each card), and
    // Resources.FindObjectsOfTypeAll has no defined order, so matching by name alone across the
    // whole scene could silently grab the wrong (inactive/HAB) instance instead of the one
    // actually being rendered - fixed by searching only within the active _planetsRoot's own
    // children.
    //
    // Lateral position (perpendicular to a card's own mesh normal) is always fully pinned - if it
    // ever slid sideways relative to the player, the card's flat/non-spherical geometry would
    // become visible edge-on, which looks far worse than any amount of drift along the depth
    // direction.
    //
    // "Pinned" means pinned to the PLAYER's position, not frozen at a fixed point in world space -
    // so depth needs asymmetric handling:
    //  - Moving TOWARD a planet must ALWAYS keep the same distance - the planet tracks the player
    //    1:1 along the depth axis. Freezing the planet's world-space depth instead (an earlier,
    //    wrong version of this) let a player who kept flying at it eventually reach and pass
    //    through its fixed position, which isn't what "pinned to you" means - it should stay the
    //    same distance away forever, receding from you exactly as fast as you approach it, like
    //    something infinitely far away really would.
    //  - Moving AWAY leaves the planet's depth-axis position untouched (matches vanilla's own
    //    full-speed recede, confirmed to look correct at this value), otherwise the gap between the
    //    (fully receding, unpinned) station and the (frozen-toward-you-only) planet would keep
    //    shrinking from the station's side, and the planet would end up visually clipping
    //    into/through the station once it's shrunk enough in the distance.
    // So each frame, the planet's depth-axis position is moved by the full playerDepthDelta while
    // approaching, or left unchanged while receding. This needs the planet's own depth-axis
    // position tracked incrementally frame to frame (not just a single value captured once at
    // activation), since "did the player just get closer or farther" is inherently about the delta
    // since last frame.
    //
    // FlatEarthMode is the escape hatch back to vanilla's full recede/approach behavior - true
    // disables this fix (misleadingly named after the visual artifact it produces when off, not
    // what it does).
    //
    // Only kicks in once the player has actually strayed more than PinDistanceMeters from the work
    // bay - close to the bay vanilla's own placement/scale already looks right (that's what it was
    // tuned for), and pinning from the very start would just fix the planets at whatever position
    // they happened to be at level load, which may not match vanilla's intended near-bay framing.
    internal static class BackgroundParallaxTuning
    {
        private const string PlanetsRootObjectName = "PRF_Base_Planets";

        private static readonly PlanetPin OctanePin = new("PRF_PlanetCard_Octane");
        private static readonly PlanetPin MoonPin = new("PRF_PlanetCard_Moon");

        private static Transform? _planetsRoot;
        private static Transform? _bayRoot;
        private static int _framesUntilRescan;
        private const int RescanIntervalFrames = 90;

        public static void Tick()
        {
            if (!Plugin.ConfigEnabled.Value || Plugin.ConfigFlatEarthMode.Value)
                return;

            if (_planetsRoot == null || _bayRoot == null)
            {
                if (_framesUntilRescan > 0)
                {
                    _framesUntilRescan--;
                    return;
                }
                _framesUntilRescan = RescanIntervalFrames;

                int totalTransformsScanned = 0;
                foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
                {
                    if (t == null)
                        continue;
                    totalTransformsScanned++;
                    if (t.parent == null && t.name == PlanetsRootObjectName)
                        _planetsRoot = t;
                    else if (t.parent == null && t.name == BackgroundConstants.BayRootObjectName)
                        _bayRoot = t;
                }
                if (Plugin.ConfigDebugPrint.Value)
                {
                    Plugin.Log.LogInfo($"BackgroundParallaxTuning: SCAN totalTransformsScanned={totalTransformsScanned} planetsRootFound={_planetsRoot != null} bayRootFound={_bayRoot != null}");
                }
                if (_planetsRoot == null || _bayRoot == null)
                {
                    _planetsRoot = null;
                    _bayRoot = null;
                    return;
                }

                if (Plugin.ConfigDebugPrint.Value)
                {
                    Plugin.Log.LogInfo($"BackgroundParallaxTuning: SCAN bayPos={_bayRoot.position} planetsRootPos={_planetsRoot.position} planetsRootActive={_planetsRoot.gameObject.activeInHierarchy} bayRootActive={_bayRoot.gameObject.activeInHierarchy}");
                }

                OctanePin.Reset(_planetsRoot, _bayRoot);
                MoonPin.Reset(_planetsRoot, _bayRoot);
            }

            Transform? player = LynxCameraController.MainCameraTransform;
            if (player == null)
                return;

            float distFromBay = Vector3.Distance(player.position, _bayRoot.position);
            bool shouldBePinned = distFromBay * distFromBay >= BackgroundConstants.PinDistanceMeters * BackgroundConstants.PinDistanceMeters;

            OctanePin.Tick(player, distFromBay, shouldBePinned);
            MoonPin.Tick(player, distFromBay, shouldBePinned);
        }

        // Encapsulates pin state for a single flat planet card, independent of any other card -
        // each instance finds its own card transform, derives its own depth axis from its own
        // mesh, and moves only that card's own Transform.position.
        private class PlanetPin
        {
            private readonly string _cardObjectName;

            private Transform? _planetsRoot;
            private Transform? _bayRoot;
            private Transform? _card;
            private Vector3 _depthAxis;
            private Vector3 _lateralOffset;
            private float _planetDepth;
            private float _previousPlayerDepth;
            private bool _isPinned;

            public PlanetPin(string cardObjectName)
            {
                _cardObjectName = cardObjectName;
            }

            // Re-locates the card and re-derives its depth axis. Called once per periodic rescan
            // (see RescanIntervalFrames in the enclosing class) rather than every frame, since
            // scene lookups are comparatively expensive and the card's mesh/orientation is static.
            public void Reset(Transform planetsRoot, Transform bayRoot)
            {
                _planetsRoot = planetsRoot;
                _bayRoot = bayRoot;
                _isPinned = false;

                Transform? card = null;
                foreach (Transform child in planetsRoot.GetComponentsInChildren<Transform>(includeInactive: false))
                {
                    if (child.name == _cardObjectName)
                    {
                        card = child;
                        break;
                    }
                }
                _card = card;

                if (_card == null)
                {
                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"BackgroundParallaxTuning[{_cardObjectName}]: SCAN card not found as child of active planetsRoot - this pin is inactive until the next rescan.");
                    return;
                }

                Vector3 bayToPlanet = _card.position - bayRoot.position;
                Vector3 fallbackAxis = bayToPlanet.sqrMagnitude > 0.0001f ? bayToPlanet.normalized : Vector3.forward;

                MeshFilter? meshFilter = _card.GetComponent<MeshFilter>();
                Renderer? renderer = _card.GetComponent<Renderer>();
                Vector3 localBounds = meshFilter != null && meshFilter.sharedMesh != null ? meshFilter.sharedMesh.bounds.size : Vector3.zero;
                Vector3 localRight = _card.right;
                Vector3 localUp = _card.up;
                Vector3 localFwd = _card.forward;

                if (meshFilter == null || meshFilter.sharedMesh == null)
                {
                    _depthAxis = fallbackAxis;
                }
                else if (localBounds.x <= localBounds.y && localBounds.x <= localBounds.z)
                    _depthAxis = localRight;
                else if (localBounds.y <= localBounds.z)
                    _depthAxis = localUp;
                else
                    _depthAxis = localFwd;

                if (Plugin.ConfigDebugPrint.Value)
                {
                    Plugin.Log.LogInfo($"BackgroundParallaxTuning[{_cardObjectName}]: SCAN cardPath={GetFullPath(_card)} cardWorldPos={_card.position} cardLocalScale={_card.localScale} hasMeshFilter={meshFilter != null} hasSharedMesh={meshFilter != null && meshFilter.sharedMesh != null} hasRenderer={renderer != null} rendererWorldBounds={(renderer != null ? renderer.bounds.size.ToString() : "n/a")} localMeshBounds={localBounds} localRight={localRight} localUp={localUp} localFwd={localFwd} fallbackBayToPlanetAxis={fallbackAxis} chosenDepthAxis={_depthAxis} usedFallback={meshFilter == null || meshFilter.sharedMesh == null}");
                }
            }

            public void Tick(Transform player, float distFromBay, bool shouldBePinned)
            {
                if (_card == null)
                    return;

                if (shouldBePinned && !_isPinned)
                {
                    Vector3 offset = _card.position - player.position;
                    _lateralOffset = offset - Vector3.Dot(offset, _depthAxis) * _depthAxis;
                    _planetDepth = Vector3.Dot(_card.position, _depthAxis);
                    _previousPlayerDepth = Vector3.Dot(player.position, _depthAxis);
                    _isPinned = true;

                    if (Plugin.ConfigDebugPrint.Value)
                    {
                        Plugin.Log.LogInfo($"BackgroundParallaxTuning[{_cardObjectName}]: ACTIVATED pinning. distFromBay={distFromBay} playerWorldPos={player.position} cardWorldPos={_card.position} lateralOffset={_lateralOffset} planetDepth={_planetDepth} previousPlayerDepth={_previousPlayerDepth}");
                    }
                }
                else if (!shouldBePinned && _isPinned)
                {
                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"BackgroundParallaxTuning[{_cardObjectName}]: DEACTIVATED pinning. distFromBay={distFromBay}");
                    _isPinned = false;
                }

                if (!_isPinned)
                    return;

                float playerDepth = Vector3.Dot(player.position, _depthAxis);
                float playerDepthDelta = playerDepth - _previousPlayerDepth;

                // "Pin" means pinned to the PLAYER's position, not frozen at a fixed world-space
                // point - so approaching tracks the player 1:1 (gap along the depth axis stays
                // exactly constant), while receding leaves the planet's depth untouched (matches
                // vanilla's own recede speed). Compared directly via |gap| each frame rather than a
                // pre-derived sign, so this still works correctly even if the player's depth passes
                // the planet's.
                float previousGap = Mathf.Abs(_planetDepth - _previousPlayerDepth);
                float candidateGap = Mathf.Abs(_planetDepth - playerDepth);
                bool isReceding = candidateGap > previousGap;
                if (!isReceding)
                    _planetDepth += playerDepthDelta;

                Vector3 newCardPos = player.position + _lateralOffset + (_planetDepth - playerDepth) * _depthAxis;

                if (Plugin.ConfigDebugPrint.Value)
                {
                    Plugin.Log.LogInfo($"BackgroundParallaxTuning[{_cardObjectName}]: frame={Time.frameCount} depthAxis={_depthAxis} lateralOffset={_lateralOffset} lateralOffsetMagnitude={_lateralOffset.magnitude} distFromBay={distFromBay} playerWorldPos={player.position} playerDepth={playerDepth} playerDepthDelta={playerDepthDelta} previousGap={previousGap} candidateGap={candidateGap} isReceding={isReceding} planetDepth={_planetDepth} gap={_planetDepth - playerDepth} oldCardPos={_card.position} newCardPos={newCardPos} actualMovement={newCardPos - _card.position}");
                }

                _card.position = newCardPos;
                _previousPlayerDepth = playerDepth;
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
