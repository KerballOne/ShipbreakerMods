namespace NewtonianPhysics
{
    // Shared constants for the background-planet tuning classes (BackgroundParallaxTuning,
    // PlanetGlowFadeTuning, FlatCardBillboarding) - all key off the same bay-root object name, so
    // it's centralized here rather than duplicated as a separate private const per file, which
    // could silently drift out of sync if one gets tuned without the others.
    //
    // The activation distance itself (formerly a const here, PinDistanceMeters) is now
    // Plugin.ConfigTriggerDistanceMeters - a live-reloadable tunable, not a compile-time constant -
    // since the user asked to be able to adjust how far from the bay pinning kicks in without a
    // rebuild. Read Plugin.ConfigTriggerDistanceMeters.Value directly at each call site instead.
    internal static class BackgroundConstants
    {
        public const string BayRootObjectName = "Work Bays";
    }
}
