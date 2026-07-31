using BBI;
using UnityEngine;

namespace NewtonianPhysics
{
    // Vanilla's Camera.farClipPlane is 2700 (set by CameraDefaultSettings.Default.Lens.FarClipPlane
    // via the ECS CameraInitSystem, BBI.Unity.Game.CameraInitSystem.OnUpdate) - past that distance
    // nothing renders at all, which this mod's own MaxVelocityMps/WorkAreaRadiusMultiplier settings
    // make it easy to fly past. CameraInitSystem is an ECS one-shot (runs only when a camera entity
    // is tagged CameraNeedsInit, e.g. on respawn/level load) that can reset farClipPlane back to
    // 2700 at any time, so rather than patch its ECS internals directly, this just re-applies the
    // configured value to the live camera every frame - cheap, and self-correcting regardless of
    // when/how often the vanilla system happens to run.
    internal static class FarClipTuning
    {
        public static void Tick()
        {
            if (!Plugin.ConfigEnabled.Value)
                return;

            float configured = Plugin.ConfigFarClipPlane.Value;
            if (configured <= 0f)
                return;

            Camera? camera = LynxCameraController.MainCamera;
            if (camera != null && camera.farClipPlane != configured)
                camera.farClipPlane = configured;
        }
    }
}
