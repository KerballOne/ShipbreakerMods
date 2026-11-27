using UnityEngine;

namespace TheExpanse
{
    // Weapon control from the pilot's seat. The mode is picked on the weapon wheel (hold R / Y, as the suit's tool
    // wheel) or with 1-4:
    //   Helm     the flight controls as they are;
    //   PDC      the mouse aims the point defense cannons; the ship holds its attitude;
    //   Torpedo  the mouse aims; a torpedo is locked on the aim point when fired; the ship holds its attitude;
    //   Rail gun the flight controls stay; the gun fires along the ship's nose.
    internal enum WeaponMode { Helm, Pdc, Torpedo, RailGun }

    internal static class Weapons
    {
        internal static WeaponMode Mode { get; private set; } = WeaponMode.Helm;

        internal static bool Allowed => Plugin.Enabled.Value && Plugin.WeaponsEnabled.Value && FlightModel.Flying;

        // the mouse (and right stick) aim instead of turning the ship
        internal static bool Aiming => Allowed && (Mode == WeaponMode.Pdc || Mode == WeaponMode.Torpedo);

        // the thrusters hold the ship's attitude while aiming
        internal static bool HoldAttitude => Aiming;

        internal static readonly string[] Names = { "HELM", "PDC", "TORPEDO", "RAIL GUN" };
        internal static string ModeName => Names[(int)Mode];

        internal static void SetMode(WeaponMode mode, string how)
        {
            if (mode == Mode) return;
            Mode = mode;
            Plugin.Log.LogInfo($"[Weapons] mode {ModeName} ({how}).");
        }

        internal static void Reset()
        {
            Mode = WeaponMode.Helm;
            WeaponWheel.Reset();
            WeaponMounts.Reset();
            PdcFire.Reset();
            RailGun.Reset();
        }

        // Every frame while seated (from FlightSeat.Tick, before the flight controls are read).
        internal static void Tick()
        {
            if (!Allowed)
            {
                WeaponWheel.Close(cancel: true);
                if (Mode != WeaponMode.Helm) SetMode(WeaponMode.Helm, "weapons off");
                return;
            }
            WeaponMounts.LogOnce();
            WeaponWheel.Tick();
            if (!WeaponWheel.Open) { Pdc.ReadAim(); PdcFire.ReadInput(); RailGun.ReadInput(); }
            if (WeaponWheel.Open) return;
            // 1-4 as on the suit, not with Alt (setup mode's keys)
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) return;
            if (Input.GetKeyDown(KeyCode.Alpha1)) SetMode(WeaponMode.Helm, "key 1");
            else if (Input.GetKeyDown(KeyCode.Alpha2)) SetMode(WeaponMode.Pdc, "key 2");
            else if (Input.GetKeyDown(KeyCode.Alpha3)) SetMode(WeaponMode.Torpedo, "key 3");
            else if (Input.GetKeyDown(KeyCode.Alpha4)) SetMode(WeaponMode.RailGun, "key 4");
        }

        // Leaving the seat: back to the helm, wheel shut.
        internal static void Stand()
        {
            WeaponWheel.Close(cancel: true);
            if (Mode != WeaponMode.Helm) SetMode(WeaponMode.Helm, "left the seat");
        }

        // After the game's own UI has updated (LateUpdate).
        internal static void LateFrame() => WeaponWheel.LateFrame();

        // After the ship's frame is placed for this frame (LateUpdate, after FlightModel.LateFrame).
        internal static void ShipFrame()
        {
            Pdc.Frame();
            PdcFire.Frame();
            RailGun.Frame();
        }
    }
}
