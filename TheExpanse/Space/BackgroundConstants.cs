// Copied from the NewtonianPhysics mod (mods share no code), so TheExpanse's flights look right without it. It runs
// only when NewtonianPhysics is not installed; with it installed, NewtonianPhysics does this itself. See SpaceLimits.
namespace TheExpanse
{
    // Shared constants for the background-planet tuning classes (BackgroundParallaxTuning,
    // PlanetGlowFadeTuning, FlatCardBillboarding) - all key off the same bay-root object name, so
    // it's centralized here rather than duplicated as a separate private const per file, which
    // could silently drift out of sync if one gets tuned without the others.
    //
    // The activation distance itself (formerly a const here, PinDistanceMeters) is now
    // Plugin.SpaceTriggerDistance - a live-reloadable tunable, not a compile-time constant -
    // since the user asked to be able to adjust how far from the bay pinning kicks in without a
    // rebuild. Read Plugin.SpaceTriggerDistance.Value directly at each call site instead.
    internal static class BackgroundConstants
    {
        public const string BayRootObjectName = "Work Bays";
    }
}
