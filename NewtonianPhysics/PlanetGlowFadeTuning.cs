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
    // only the literal Material.SetColor(propName, Color.clear) used by SceneDiagnostics's F9
    // handler had any visible effect, and only as a binary on/off, never a gradual fade. Whatever
    // this shader does with its color/alpha properties, intermediate values between full and
    // Color.clear aren't rendered.
    //
    // Transform.localScale, by contrast, is proven to work smoothly and continuously -
    // BackgroundParallaxTuning already reads and writes Transform.position on these same kinds of
    // objects every frame with no rendering issues, so scale (also just a Transform property, not
    // a shader property) should behave the same way. Animating scale down to zero is a simpler,
    // more reliable way to "fade out" a flat card mesh than fighting the material any further.
    internal static class PlanetGlowFadeTuning
    {
        private const float FadeEndMeters = 500f;

        private static Vector3? _originalScale;

        public static void Tick()
        {
            if (!Plugin.ConfigEnabled.Value)
                return;

            Transform? player = LynxCameraController.MainCameraTransform;
            if (player == null)
                return;

            Transform? bayRoot = null;
            foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t != null && t.parent == null && t.name == BackgroundConstants.BayRootObjectName)
                {
                    bayRoot = t;
                    break;
                }
            }
            if (bayRoot == null)
                return;

            float distFromBay = Vector3.Distance(player.position, bayRoot.position);
            float scale01 = 1f - Mathf.Clamp01((distFromBay - BackgroundConstants.PinDistanceMeters) / (FadeEndMeters - BackgroundConstants.PinDistanceMeters));

            ApplyGlowScale(scale01);
        }

        private static void ApplyGlowScale(float scale01)
        {
            var glowTransforms = Resources.FindObjectsOfTypeAll<Transform>()
                .Where(t => t != null && t.name == "PRF_PlanetGlow" && t.gameObject.activeInHierarchy);

            foreach (var t in glowTransforms)
            {
                // Captured once per object, the first time it's seen at full scale - so a rescan
                // (or the object appearing fresh) doesn't mistake an already-shrunk scale for the
                // real original.
                if (_originalScale == null && scale01 >= 1f)
                    _originalScale = t.localScale;

                if (_originalScale == null)
                    continue;

                t.localScale = _originalScale.Value * scale01;
            }
        }
    }
}
