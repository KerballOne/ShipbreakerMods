using BBI.Unity.Game;
using UnityEngine;

namespace NewtonianPhysics
{
    // Confirmed via an in-game scene dump (SceneDiagnostics, F9) that Earth/Moon are NOT
    // rendered by a background-compositing camera (_3DBackgroundCam/_3DBackgroundDrawer exist
    // in the decompiled source but have zero live instances in this game) - they're ordinary
    // mesh objects, "PRF_PlanetCard_Octane" (the earth-like planet) and "PRF_PlanetCard_Moon",
    // both parented under a single root, "PRF_Base_Planets", alongside a large flat glow mesh
    // "PRF_PlanetGlow". Like real astronomical bodies, they sit at a fixed position in the level
    // and are meant to look distant/unmoving - normal vanilla movement never got far enough for
    // that to break down, but this mod's own MaxVelocityMps/WorkAreaRadiusMultiplier let players
    // drift far enough that they visibly recede, which doesn't match how something that far away
    // should look. This keeps PRF_Base_Planets at a constant offset from the player instead, by
    // recording the offset once (on first sight, or whenever the config'd offset changes) and
    // re-applying it every frame - so foreground ships/station/debris still shrink normally with
    // real distance, but the planets themselves stay visually fixed like true background scenery.
    // FlatEarthMode is the escape hatch back to vanilla's recede/approach behavior - true disables
    // this fix (misleadingly named after the visual artifact it produces when off, not what it does).
    //
    // Only kicks in once the player has actually strayed more than PinDistanceMeters from the work
    // bay - close to the bay vanilla's own placement/scale already looks right (that's what it was
    // tuned for), and pinning from the very start would just fix the planets at whatever position
    // they happened to be at level load, which may not match vanilla's intended near-bay framing.
    internal static class BackgroundParallaxTuning
    {
        private const string PlanetsRootObjectName = "PRF_Base_Planets";
        private const string BayRootObjectName = "Work Bays";
        private const float PinDistanceMeters = 250f;

        private static Transform? _planetsRoot;
        private static Transform? _bayRoot;
        private static Vector3 _offsetFromPlayer;
        private static bool _isPinned;
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

                foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
                {
                    if (t == null || t.parent != null)
                        continue;
                    if (t.name == PlanetsRootObjectName)
                        _planetsRoot = t;
                    else if (t.name == BayRootObjectName)
                        _bayRoot = t;
                }
                if (_planetsRoot == null || _bayRoot == null)
                    return;
            }

            Transform? player = LynxCameraController.MainCameraTransform;
            if (player == null)
                return;

            bool shouldBePinned = (player.position - _bayRoot.position).sqrMagnitude >= PinDistanceMeters * PinDistanceMeters;

            if (shouldBePinned && !_isPinned)
            {
                _offsetFromPlayer = _planetsRoot.position - player.position;
                _isPinned = true;
            }
            else if (!shouldBePinned)
            {
                _isPinned = false;
            }

            if (_isPinned)
                _planetsRoot.position = player.position + _offsetFromPlayer;
        }
    }
}
