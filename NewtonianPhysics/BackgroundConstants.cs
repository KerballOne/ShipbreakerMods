namespace NewtonianPhysics
{
    // Shared constants for the background-planet tuning classes (BackgroundParallaxTuning,
    // PlanetGlowFadeTuning) - both key off the same distance-from-bay threshold and object names,
    // so they're centralized here rather than duplicated as separate private consts per file,
    // which could silently drift out of sync if one gets tuned without the other.
    internal static class BackgroundConstants
    {
        public const string BayRootObjectName = "Work Bays";

        // Distance from the work bay at which the planet pin (BackgroundParallaxTuning) activates
        // and the glow fade (PlanetGlowFadeTuning) begins - close to the bay, vanilla's own
        // placement/scale/brightness already looks right, so neither fix should touch anything
        // until the player has actually strayed this far.
        public const float PinDistanceMeters = 250f;
    }
}
