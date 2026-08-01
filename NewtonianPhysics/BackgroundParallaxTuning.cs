using System.Linq;
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
    // Only kicks in once the player has actually strayed more than Plugin.ConfigTriggerDistanceMeters
    // from the work bay - close to the bay vanilla's own placement/scale already looks right (that's
    // what it was tuned for), and pinning from the very start would just fix the planets at whatever
    // position they happened to be at level load, which may not match vanilla's intended near-bay
    // framing.
    internal static class BackgroundParallaxTuning
    {
        private const string PlanetsRootObjectName = "PRF_Base_Planets";

        private static readonly PlanetPin OctanePin = new("PRF_PlanetCard_Octane", searchWholeScene: false);
        private static readonly PlanetPin MoonPin = new("PRF_PlanetCard_Moon", searchWholeScene: false);

        // Unlike Octane/Moon, SunCard is NOT a child of PRF_Base_Planets - confirmed via a
        // distance-based renderer scan that it's parented under a lighting fixture instead
        // ("FX_Salvagetest_Light_01/Salvage Bay Directional  (MAIN)/SunCard"), tied to the scene's
        // directional light rather than the planet group - so it's searched for across the whole
        // scene by name instead of restricted to planetsRoot's children.
        //
        // Also unlike Octane/Moon, whose flatness comes from the MESH's own unscaled geometry
        // (MeshFilter.sharedMesh.bounds), SunCard's mesh has real volume in all three of its own
        // local dimensions (~1046 x 944 x 958 - no thin axis at all) - it's a lens-flare-style
        // radial/star burst mesh, not a flat disc quad. What actually makes it flat in the world is
        // its Transform.localScale, which has a literal 0 on its Z component
        // (cardLocalScale=(567.1, 680.5, 0.0), confirmed via in-game log) - the object is
        // deliberately squashed to zero thickness along its own local Z at the transform level,
        // independent of the mesh asset underneath. Using the mesh-bounds axis picker here (which
        // works by comparing raw unscaled mesh dimensions) picked an essentially arbitrary
        // direction and was confirmed in-game to produce inverted-looking approach/recede behavior.
        // useZeroScaleAxis makes Reset() instead look for a ~0 component directly on localScale and
        // use that local axis (mapped to world space) as the depth axis - the mathematically
        // correct signal for an object whose flatness is a scale property, not a mesh property.
        private static readonly PlanetPin SunPin = new("SunCard", searchWholeScene: true, useZeroScaleAxis: true);

        private static Transform? _planetsRoot;
        private static Transform? _bayRoot;
        private static int _framesUntilRescan;
        private const int RescanIntervalFrames = 90;

        // Clears cached scene state so the next Tick() re-scans from scratch - called on the
        // Gameplay/new-shift transition (see Plugin.OnGameStateChanged). Without this, _planetsRoot/
        // _bayRoot and each PlanetPin's cached card Transform keep pointing at the PREVIOUS shift's
        // now-destroyed scene after abandoning a shift and reloading, silently breaking position
        // pinning for the new shift (confirmed as a real bug for FlatCardBillboarding's angle
        // pinning, which shares this exact caching pattern - applying the same fix here defensively,
        // even though this specific breakage wasn't independently confirmed for this file).
        public static void ResetState()
        {
            _planetsRoot = null;
            _bayRoot = null;
            _framesUntilRescan = 0;
            OctanePin.ClearCache();
            MoonPin.ClearCache();
            SunPin.ClearCache();
        }

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
                    DumpAllNamed("SunCard");
                    DumpAllLights();
                }

                OctanePin.Reset(_planetsRoot, _bayRoot);
                MoonPin.Reset(_planetsRoot, _bayRoot);
                SunPin.Reset(_planetsRoot, _bayRoot);
            }

            Transform? player = LynxCameraController.MainCameraTransform;
            if (player == null)
                return;

            float triggerDistance = Plugin.ConfigTriggerDistanceMeters.Value;
            float distFromBay = Vector3.Distance(player.position, _bayRoot.position);
            bool shouldBePinned = distFromBay * distFromBay >= triggerDistance * triggerDistance;

            OctanePin.Tick(player, distFromBay, shouldBePinned);
            MoonPin.Tick(player, distFromBay, shouldBePinned);
            SunPin.Tick(player, distFromBay, shouldBePinned);
        }

        // Encapsulates pin state for a single flat planet card, independent of any other card -
        // each instance finds its own card transform, derives its own depth axis from its own
        // mesh, and moves only that card's own Transform.position.
        private class PlanetPin
        {
            private readonly string _cardObjectName;
            private readonly bool _searchWholeScene;
            private readonly bool _useZeroScaleAxis;

            private Transform? _planetsRoot;
            private Transform? _bayRoot;
            private Transform? _card;
            private Vector3 _depthAxis;
            private Vector3 _lateralOffset;
            private float _planetDepth;
            private float _originalPlanetDepth;
            private float _previousPlayerDepth;
            private bool _isPinned;

            // Only used for debug logging - lets Reset() report how much the derived depth axis
            // drifted since the last rescan. If this is ever non-zero for SunCard (unlike
            // Octane/Moon, which sit under a static, non-rotating parent), that would mean the
            // axis derived from SunCard's local right/up/forward isn't actually stable over time -
            // likely because SunCard is parented under the directional light rather than the
            // static PRF_Base_Planets root, so any rotation on that light's Transform propagates
            // into the world-space axis this pin relies on staying constant.
            private Vector3? _previousDepthAxis;

            // Drops every cached Transform reference and pinned state so the next Reset() rebuilds
            // everything from scratch, instead of trusting references that belong to a now-destroyed
            // previous shift's scene.
            public void ClearCache()
            {
                _planetsRoot = null;
                _bayRoot = null;
                _card = null;
                _isPinned = false;
                _previousDepthAxis = null;
            }

            // searchWholeScene: false restricts the lookup to children of the active planetsRoot
            // (PRF_Base_Planets) - correct for Octane/Moon, and avoids accidentally grabbing a
            // same-named object under the inactive PRF_Base_Planets_HAB copy. true searches the
            // whole scene by name instead, for objects that aren't children of planetsRoot at all
            // (e.g. SunCard, which is parented under a lighting fixture) - here matching by name
            // alone is the only option, but SunCard's name is specific enough that this is safe.
            //
            // useZeroScaleAxis: false uses the mesh-bounds-based axis picker (correct for
            // Octane/Moon, whose flatness is a property of the mesh geometry itself). true instead
            // looks for a ~0 component on the card's own Transform.localScale and uses that local
            // axis - correct for objects (like SunCard) whose flatness comes from being squashed at
            // the transform level rather than from the underlying mesh's own shape.
            public PlanetPin(string cardObjectName, bool searchWholeScene, bool useZeroScaleAxis = false)
            {
                _cardObjectName = cardObjectName;
                _searchWholeScene = searchWholeScene;
                _useZeroScaleAxis = useZeroScaleAxis;
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
                if (_searchWholeScene)
                {
                    foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
                    {
                        if (t != null && t.name == _cardObjectName && t.gameObject.activeInHierarchy)
                        {
                            card = t;
                            break;
                        }
                    }
                }
                else
                {
                    foreach (Transform child in planetsRoot.GetComponentsInChildren<Transform>(includeInactive: false))
                    {
                        if (child.name == _cardObjectName)
                        {
                            card = child;
                            break;
                        }
                    }
                }
                _card = card;

                if (_card == null)
                {
                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"BackgroundParallaxTuning[{_cardObjectName}]: SCAN card not found as child of active planetsRoot - this pin is inactive until the next rescan.");
                    return;
                }

                // Confirmed via extensive F9 diagnostics that SunCard's Renderer.isPartOfStaticBatch
                // is true, permanently and read-only (no managed backing field reachable via
                // reflection either - it's native engine state) - Unity's static batcher baked its
                // geometry into a combined multi-object mesh at build time. Object.Instantiate()
                // clones were confirmed (via a full field-by-field before/after comparison log) to
                // be byte-for-byte identical to the original, including isPartOfStaticBatch=true -
                // cloning the GameObject does NOT escape the batch on this Unity version (2020.3).
                // The only thing that actually works (confirmed via a separate F9 toggle test) is a
                // genuinely NEW GameObject with its own MeshFilter/MeshRenderer built from scratch -
                // never processed by the build-time batcher at all. Uses a plain procedural quad
                // (4 vertices, standard UVs) rather than trying to reuse SunCard's mesh reference,
                // since that reference points at the same combined batch mesh, not SunCard's own
                // geometry - a simple quad reproduces the same on-screen look since T_SunSprite's
                // own transparency defines the visible star shape.
                if (_card.GetComponent<Renderer>() is MeshRenderer originalRenderer && originalRenderer.isPartOfStaticBatch)
                {
                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"BackgroundParallaxTuning[{_cardObjectName}]: SCAN card's renderer is part of a static batch - building a procedural quad replacement so position writes actually render.");

                    Transform original = _card;
                    string originalName = original.name;

                    GameObject quadObj = new GameObject(originalName + " (ProceduralQuad)");
                    quadObj.transform.SetParent(original.parent, worldPositionStays: true);
                    quadObj.transform.position = original.position;
                    quadObj.transform.rotation = original.rotation;
                    quadObj.transform.localScale = original.localScale;

                    Mesh mesh = new Mesh { name = "ProceduralSunQuad" };
                    mesh.vertices = new[]
                    {
                        new Vector3(-0.5f, -0.5f, 0f),
                        new Vector3(0.5f, -0.5f, 0f),
                        new Vector3(-0.5f, 0.5f, 0f),
                        new Vector3(0.5f, 0.5f, 0f),
                    };
                    mesh.uv = new[]
                    {
                        new Vector2(0, 0),
                        new Vector2(1, 0),
                        new Vector2(0, 1),
                        new Vector2(1, 1),
                    };
                    mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
                    mesh.RecalculateNormals();
                    mesh.RecalculateBounds();

                    MeshFilter newMeshFilter = quadObj.AddComponent<MeshFilter>();
                    newMeshFilter.sharedMesh = mesh;

                    MeshRenderer newRenderer = quadObj.AddComponent<MeshRenderer>();
                    newRenderer.sharedMaterials = originalRenderer.sharedMaterials.Select(m => new Material(m)).ToArray();

                    // Rename so a FUTURE Reset()/rescan (whole-scene search matches by exact name)
                    // finds the live quad instead of dead-ending on the disabled original - the
                    // original is renamed out of the way instead of left with the canonical name,
                    // since it's now permanently inert (SetActive(false)).
                    original.name = originalName + " (OriginalBatched, inactive)";
                    original.gameObject.SetActive(false);
                    quadObj.name = originalName;
                    _card = quadObj.transform;

                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"BackgroundParallaxTuning[{_cardObjectName}]: procedural quad built - pos={quadObj.transform.position} rot={quadObj.transform.rotation.eulerAngles} localScale={quadObj.transform.localScale} isPartOfStaticBatch={newRenderer.isPartOfStaticBatch} materialCount={newRenderer.sharedMaterials.Length}");
                }

                Vector3 bayToPlanet = _card.position - bayRoot.position;
                Vector3 fallbackAxis = bayToPlanet.sqrMagnitude > 0.0001f ? bayToPlanet.normalized : Vector3.forward;

                MeshFilter? meshFilter = _card.GetComponent<MeshFilter>();
                Renderer? renderer = _card.GetComponent<Renderer>();
                Vector3 localBounds = meshFilter != null && meshFilter.sharedMesh != null ? meshFilter.sharedMesh.bounds.size : Vector3.zero;
                Vector3 localScale = _card.localScale;
                Vector3 localRight = _card.right;
                Vector3 localUp = _card.up;
                Vector3 localFwd = _card.forward;
                bool usedZeroScaleAxis = false;

                if (_useZeroScaleAxis)
                {
                    // Originally used whichever localScale component was closest to 0 as the
                    // depth axis (the axis the object is squashed flat along), mapped from local
                    // to world space via the card's own rotation. Confirmed via debug logging this
                    // was ~19 degrees off from the true bay->card direction for SunCard - because
                    // SunCard is parented under a directional light (not the static PRF_Base_Planets
                    // root Octane/Moon use), the light's own aim rotation gets baked into that
                    // local-to-world mapping, so the "flat" axis and the "toward/away from the bay"
                    // axis aren't the same direction at all for this object. A world-space bay->card
                    // vector is hierarchy-independent and therefore immune to this - always use that
                    // for the actual depth/distance axis. The zero-scale-derived local axis is kept
                    // only as the flatness reference so lateral offset still avoids pushing the card
                    // edge-on (see below).
                    _depthAxis = fallbackAxis;
                    usedZeroScaleAxis = true;
                }
                else if (meshFilter == null || meshFilter.sharedMesh == null)
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
                    float axisDriftDegrees = _previousDepthAxis.HasValue ? Vector3.Angle(_previousDepthAxis.Value, _depthAxis) : 0f;
                    Plugin.Log.LogInfo($"BackgroundParallaxTuning[{_cardObjectName}]: SCAN cardPath={GetFullPath(_card)} cardWorldPos={_card.position} cardLocalScale={localScale} cardWorldRotation={_card.rotation.eulerAngles} parentWorldRotation={(_card.parent != null ? _card.parent.rotation.eulerAngles.ToString() : "no parent")} hasMeshFilter={meshFilter != null} hasSharedMesh={meshFilter != null && meshFilter.sharedMesh != null} hasRenderer={renderer != null} rendererWorldBounds={(renderer != null ? renderer.bounds.size.ToString() : "n/a")} localMeshBounds={localBounds} localRight={localRight} localUp={localUp} localFwd={localFwd} fallbackBayToPlanetAxis={fallbackAxis} chosenDepthAxis={_depthAxis} usedZeroScaleAxis={usedZeroScaleAxis} usedFallback={!usedZeroScaleAxis && (meshFilter == null || meshFilter.sharedMesh == null)} axisDriftSinceLastScanDegrees={axisDriftDegrees}");
                }
                _previousDepthAxis = _depthAxis;
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
                    _originalPlanetDepth = _planetDepth;
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
                // exactly constant, pushing the planet away). Compared directly via |gap| each
                // frame rather than a pre-derived sign, so this still works correctly even if the
                // player's depth passes the planet's.
                float previousGap = Mathf.Abs(_planetDepth - _previousPlayerDepth);
                float candidateGap = Mathf.Abs(_planetDepth - playerDepth);
                bool isReceding = candidateGap > previousGap;
                if (!isReceding)
                {
                    _planetDepth += playerDepthDelta;
                }
                else
                {
                    // Receding PULLS the planet back toward wherever it was when pinning first
                    // activated (_originalPlanetDepth), rather than leaving it frozen at whatever
                    // depth the last approach pushed it to. Without this, repeated approach/retreat
                    // cycles in one session ratchet the planet permanently farther away each time
                    // you approach, since depth only ever grew and never shrank back - confirmed via
                    // direct testing that this asymmetry is real and compounds indefinitely with no
                    // decay.
                    //
                    // CRITICAL: this must only ever move _planetDepth TOWARD _originalPlanetDepth,
                    // never past it, and never AWAY from it either - confirmed via a real bug where
                    // clamping only against overshoot (using a "was above original" sign computed
                    // BEFORE applying playerDepthDelta) let the planet get pulled ALONGSIDE the
                    // player's own recede whenever _planetDepth was already at/near
                    // _originalPlanetDepth (e.g. a session that only ever recedes, never
                    // approaches first) - playerDepthDelta was being applied unconditionally even
                    // though the planet had never been pushed out in the first place. Clamping the
                    // FINAL result into the closed interval between the ORIGINAL _planetDepth (this
                    // frame's starting point) and _originalPlanetDepth - regardless of which is
                    // larger - guarantees the planet only ever moves toward home and never beyond
                    // either bound, with no dependence on a precomputed direction that can go stale.
                    float depthBeforeThisFrame = _planetDepth;
                    float movedTowardOriginal = _planetDepth + playerDepthDelta;
                    float lowerBound = Mathf.Min(depthBeforeThisFrame, _originalPlanetDepth);
                    float upperBound = Mathf.Max(depthBeforeThisFrame, _originalPlanetDepth);
                    _planetDepth = Mathf.Clamp(movedTowardOriginal, lowerBound, upperBound);
                }

                Vector3 newCardPos = player.position + _lateralOffset + (_planetDepth - playerDepth) * _depthAxis;

                if (Plugin.ConfigDebugPrint.Value)
                {
                    // gap/previousGap/candidateGap only measure distance ALONG _depthAxis - if that
                    // axis isn't actually pointing toward/away from the player (e.g. because it was
                    // derived from a rotating parent, see axisDriftSinceLastScanDegrees in Reset()),
                    // this dot-product gap can stay perfectly constant while the true 3D distance to
                    // the card keeps changing, which is exactly what "gap math says constant but it
                    // visibly gets closer/farther" would look like. realWorldDistance is logged
                    // separately so the two can be directly compared frame to frame.
                    float realWorldDistanceOld = Vector3.Distance(player.position, _card.position);
                    float realWorldDistanceNew = Vector3.Distance(player.position, newCardPos);
                    Plugin.Log.LogInfo($"BackgroundParallaxTuning[{_cardObjectName}]: frame={Time.frameCount} depthAxis={_depthAxis} lateralOffset={_lateralOffset} lateralOffsetMagnitude={_lateralOffset.magnitude} distFromBay={distFromBay} playerWorldPos={player.position} playerDepth={playerDepth} playerDepthDelta={playerDepthDelta} previousGap={previousGap} candidateGap={candidateGap} isReceding={isReceding} planetDepth={_planetDepth} gap={_planetDepth - playerDepth} realWorldDistanceOld={realWorldDistanceOld} realWorldDistanceNew={realWorldDistanceNew} oldCardPos={_card.position} newCardPos={newCardPos} actualMovement={newCardPos - _card.position}");
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

        // Diagnostic: list EVERY object in the whole scene with this exact name, active or not, no
        // distance filter - to check whether PlanetPin's FirstOrDefault search (which stops at the
        // first match) might be missing a second/closer duplicate the way PRF_Base_Planets_HAB had
        // a duplicate PRF_PlanetGlow.
        private static void DumpAllNamed(string name)
        {
            int count = 0;
            foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || t.name != name)
                    continue;
                count++;
                Plugin.Log.LogInfo($"BackgroundParallaxTuning: DumpAllNamed[{name}] #{count} path={GetFullPath(t)} active={t.gameObject.activeInHierarchy} pos={t.position} instanceId={t.GetInstanceID()}");
            }
            Plugin.Log.LogInfo($"BackgroundParallaxTuning: DumpAllNamed[{name}] total count={count}");
        }

        // Diagnostic: list every Light component in the scene - checking whether the bright
        // sun-like core seen up close near the station might be a separate real-time light's own
        // flare/bloom, rendered independent of SunCard's transform, rather than SunCard itself.
        private static void DumpAllLights()
        {
            int count = 0;
            foreach (Light light in Resources.FindObjectsOfTypeAll<Light>())
            {
                if (light == null)
                    continue;
                count++;
                Plugin.Log.LogInfo($"BackgroundParallaxTuning: DumpAllLights #{count} name={light.name} path={GetFullPath(light.transform)} active={light.gameObject.activeInHierarchy} type={light.type} pos={light.transform.position} intensity={light.intensity} range={light.range} hasFlare={light.flare != null}");
            }
            Plugin.Log.LogInfo($"BackgroundParallaxTuning: DumpAllLights total count={count}");
        }
    }
}
