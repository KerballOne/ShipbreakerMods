using System.Linq;
using BBI.Unity.Game;
using UnityEngine;

namespace NewtonianPhysics
{
    // Shrinks PRF_PlanetGlow to zero scale as the player travels from
    // BackgroundConstants.PinDistanceMeters (250m) out to FadeEndMeters (500m) from the work bay,
    // and grows it back symmetrically if the player returns closer - distance-based, not
    // time-based, so the shrink always lines up with the same physical distance regardless of how
    // fast the player is flying.
    //
    // Confirmed via direct testing that color-based fading doesn't work here at all: neither
    // Material.SetColor with a computed intermediate alpha, nor MaterialPropertyBlock (the
    // SRP-Batcher-safe per-renderer override path), ever visibly changed the glow's brightness -
    // only a literal Material.SetColor(propName, Color.clear) had any visible effect, and only as
    // a binary on/off, never a gradual fade. Whatever this shader does with its color/alpha
    // properties, intermediate values between full and Color.clear aren't rendered.
    //
    // Transform.localScale, by contrast, is proven to work smoothly and continuously -
    // BackgroundParallaxTuning already reads and writes Transform.position on these same kinds of
    // objects every frame with no rendering issues, so scale (also just a Transform property, not
    // a shader property) should behave the same way. Animating scale down to zero is a simpler,
    // more reliable way to "fade out" a flat card mesh than fighting the material any further.
    //
    // IMPORTANT: caching the glow Transform reference (found once, then reused every frame) was
    // tried and confirmed BROKEN - the scale writes/reads all succeeded with no errors, but the
    // shrink was never visible in-game. The working theory is that the cached reference silently
    // goes stale (points at an object no longer actually being rendered, e.g. replaced/rebuilt by
    // something else in the game) even though Unity's null-check doesn't catch that case - only
    // true destruction is caught that way, not replacement. So the glow Transform is instead
    // re-found fresh via Resources.FindObjectsOfTypeAll every time it's actually touched, matching
    // the original confirmed-working approach.
    //
    // What IS safe to cache: the bay root. It's only ever used for a distance calculation, never
    // mutated, and its identity doesn't need to match some other system's live rendered object -
    // even a "stale" cached reference to it would still report accurate world position as long as
    // it isn't destroyed outright, which nothing in this mod or the base game has any reason to do
    // to a static station structure. So the expensive full-scene re-scan only happens for the
    // glow, and only while actually in fade range (>=250m) - not every frame regardless of
    // distance, and not for the bay root at all after the first successful find.
    internal static class PlanetGlowFadeTuning
    {
        private const float FadeEndMeters = 500f;

        private static Transform? _bayRoot;
        private static Vector3? _originalScale;

        // Tracks whether the glow is known to already be fully shrunk, so a player sitting past
        // 500m (e.g. loading a save already that far out) doesn't leave it stuck at full size
        // forever just because the normal 250m-500m fade window is never entered - this forces
        // one snap-to-zero the first time distance is found to be >=500m, then leaves it alone.
        private static bool _confirmedFullyShrunk;

        // Clears cached scene state so the next Tick() re-scans from scratch - called on the
        // Gameplay/new-shift transition (see Plugin.OnGameStateChanged). _originalScale is also
        // cleared since it's captured from whatever glow Transform is found in the CURRENT shift's
        // scene - reusing a scale value captured from a previous shift's (possibly differently
        // sized/scaled) glow object would be wrong.
        public static void ResetState()
        {
            _bayRoot = null;
            _originalScale = null;
            _confirmedFullyShrunk = false;
        }

        public static void Tick()
        {
            if (!Plugin.ConfigEnabled.Value)
                return;

            if (_bayRoot == null)
            {
                FindBayRoot();
                if (_bayRoot == null)
                    return;
            }

            Transform? player = LynxCameraController.MainCameraTransform;
            if (player == null)
                return;

            float distFromBay = Vector3.Distance(player.position, _bayRoot.position);

            // Cheap every-frame check using only the cached bay root - the glow itself is never
            // touched at all inside 250m (still full size, nothing to do) or once already
            // confirmed fully shrunk past 500m (nothing left to do either).
            if (distFromBay < BackgroundConstants.PinDistanceMeters)
                return;
            if (distFromBay >= FadeEndMeters && _confirmedFullyShrunk)
                return;

            float scale01 = 1f - Mathf.Clamp01((distFromBay - BackgroundConstants.PinDistanceMeters) / (FadeEndMeters - BackgroundConstants.PinDistanceMeters));

            Transform? glowTransform = Resources.FindObjectsOfTypeAll<Transform>()
                .FirstOrDefault(t => t != null && t.name == "PRF_PlanetGlow" && t.gameObject.activeInHierarchy);
            if (glowTransform == null)
                return;

            if (_originalScale == null)
                _originalScale = glowTransform.localScale;

            glowTransform.localScale = _originalScale.Value * scale01;

            // Only meaningful past FadeEndMeters (see the early-out above) - explicitly false
            // whenever this runs from inside the 250m-500m window so re-entering that window after
            // being fully shrunk doesn't get skipped by a stale true value.
            _confirmedFullyShrunk = distFromBay >= FadeEndMeters;
        }

        private static void FindBayRoot()
        {
            foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t != null && t.parent == null && t.name == BackgroundConstants.BayRootObjectName)
                {
                    _bayRoot = t;
                    break;
                }
            }
        }
    }
}
