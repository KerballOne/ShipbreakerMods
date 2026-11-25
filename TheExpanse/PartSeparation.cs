using UnityEngine;

namespace TheExpanse
{
    // Heavy parts of a custom ship come free gently instead of being thrown.
    //
    // Every part collides as the convex shape around its mesh, so on a custom ship built from large concave hull pieces
    // many neighboring parts overlap each other. While they are one body that does not matter. The moment one is cut or
    // yanked free, the physics engine pushes the overlapping bodies apart, at up to 10 m/s (the game's
    // Physics.defaultMaxDepenetrationVelocity). Measured on a 6.6 tonne part: 0.8 -> 9.7 m/s in a single physics step,
    // too fast for a tether to hold.
    //
    // That push speed is a limit each body carries, so it is set here from the body's mass: light bodies keep the game's
    // own value and still pop apart, heavy ones are eased apart slowly. Forces are not touched: the grapple, tethers,
    // explosions and decompression move parts exactly as before.
    //
    // A piece that comes free is a new body, and the push starts in its first physics step. So while this is on, new
    // bodies start with the gentle limit, and a pass a few times a second raises it for the light ones and follows any
    // change of mass (a body that is cut in two).
    internal static class PartSeparation
    {
        const float HeavyLimit = 0.3f;      // m/s, for bodies of HeavyMass or more; also what every new body starts with
        const float LightMass = 100f;       // kg; bodies this light or lighter keep the game's own limit
        const float HeavyMass = 5000f;      // kg
        const float SweepSeconds = 0.25f;

        // Set when a ship starts spawning: true for a ship that comes from a mod. Stock ships are left as the game has them.
        internal static bool CustomShip;

        static float sStockLimit = -1f;
        static bool sApplied;
        static float sNextSweep;

        internal static void Tick()
        {
            if (sStockLimit < 0f) sStockLimit = Physics.defaultMaxDepenetrationVelocity;
            bool on = Plugin.Enabled.Value && Plugin.GentleSeparation.Value && CustomShip;
            if (!on)
            {
                if (sApplied) Restore();
                return;
            }
            if (!sApplied)
            {
                Physics.defaultMaxDepenetrationVelocity = HeavyLimit;
                sApplied = true;
                sNextSweep = 0f;
                Plugin.Log.LogInfo($"[Parts] gentle separation on for this ship: bodies of {HeavyMass:F0} kg or more are pushed apart at no more than " +
                                   $"{HeavyLimit} m/s, bodies up to {LightMass:F0} kg at the game's own {sStockLimit} m/s.");
            }
            if (Time.unscaledTime < sNextSweep) return;
            sNextSweep = Time.unscaledTime + SweepSeconds;
            foreach (var body in Object.FindObjectsOfType<Rigidbody>())
            {
                if (body == null || body.GetComponent<BBI.Unity.Game.Player>() != null) continue;     // the suit keeps the game's own
                float want = LimitFor(body.mass);
                if (Mathf.Abs(body.maxDepenetrationVelocity - want) > 0.01f * want) body.maxDepenetrationVelocity = want;
            }
        }

        // Slides on a log scale in both mass and speed: each doubling of mass lowers the limit by the same factor.
        static float LimitFor(float mass)
        {
            float light = Mathf.Max(HeavyLimit, sStockLimit);
            float t = Mathf.Clamp01(Mathf.Log(Mathf.Max(mass, 0.01f) / LightMass) / Mathf.Log(HeavyMass / LightMass));
            return light * Mathf.Pow(HeavyLimit / light, t);
        }

        static void Restore()
        {
            Physics.defaultMaxDepenetrationVelocity = sStockLimit;
            foreach (var body in Object.FindObjectsOfType<Rigidbody>())
                if (body != null) body.maxDepenetrationVelocity = sStockLimit;
            sApplied = false;
            Plugin.Log.LogInfo("[Parts] gentle separation off; the game's own limit is back on every body.");
        }
    }
}
