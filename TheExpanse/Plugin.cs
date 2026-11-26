using System;
using System.IO;
using BBI.Unity.Game;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace TheExpanse
{
    // Changes to ships, for large custom ships:
    //   RoomPressure    makes each room's own pressurization / decompression time count, so big rooms take longer
    //   PartSeparation  on custom ships, heavy parts that overlap their neighbors come free gently instead of being thrown
    //   ShipRoll        on custom ghost ships, a haunting action of its own: the ship rolls slowly about its long axis
    //   NodeRadiation   on custom ghost ships, being near a node hurts, as radiation
    // (Centering ships in the bay and moving the jacks, which started here, now live in the BiggerBays mod; the Mars
    // planet, loading screen and menu pictures live in the Themes mod.)
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance = null!;
        internal static ManualLogSource Log = null!;

        internal static ConfigEntry<bool> Enabled = null!;
        internal static ConfigEntry<bool> RoomPressureTimes = null!;
        internal static ConfigEntry<bool> ExtendPressureEffects = null!;
        internal static ConfigEntry<float> PressureEffectSeconds = null!;
        internal static ConfigEntry<bool> GentleSeparation = null!;
        internal static ConfigEntry<bool> RollShip = null!;
        internal static ConfigEntry<float> RollDegreesPerSecond = null!;
        internal static ConfigEntry<float> RollSeconds = null!;
        internal static ConfigEntry<float> RollMinInterval = null!;
        internal static ConfigEntry<float> RollMaxInterval = null!;
        internal static ConfigEntry<float> RollStartShare = null!;
        internal static ConfigEntry<float> RollFrequencyRamp = null!;
        internal static ConfigEntry<float> RollSpeedRamp = null!;
        internal static ConfigEntry<bool> HauntStockShips = null!;
        internal static ConfigEntry<bool> LightFlicker = null!;
        internal static ConfigEntry<bool> LightBlackout = null!;
        internal static ConfigEntry<float> LightMinInterval = null!;
        internal static ConfigEntry<float> LightMaxInterval = null!;
        internal static ConfigEntry<float> LightFrequencyRamp = null!;
        internal static ConfigEntry<float> LightRoomRadius = null!;
        internal static ConfigEntry<float> FlickerSeconds = null!;
        internal static ConfigEntry<float> BlackoutMinSeconds = null!;
        internal static ConfigEntry<float> BlackoutMaxSeconds = null!;
        internal static ConfigEntry<bool> HelmetFlicker = null!;
        internal static ConfigEntry<float> HelmetFlickerRadius = null!;
        internal static ConfigEntry<bool> RemoveGrowthAfterLastNode = null!;
        internal static ConfigEntry<bool> NodeRadiation = null!;
        internal static ConfigEntry<bool> TestTools = null!;
        internal static ConfigEntry<KeyboardShortcut> KillHalfNodesKey = null!;
        internal static ConfigEntry<float> NodeRadiationDamage = null!;
        internal static ConfigEntry<float> NodeRadiationRadius = null!;
        internal static ConfigEntry<float> NodeRadiationFalloff = null!;
        internal static ConfigEntry<bool> ReactorControl = null!;
        internal static ConfigEntry<float> ReactorBallSize = null!;
        internal static ConfigEntry<float> ReactorBallDepth = null!;
        internal static ConfigEntry<float> ReactorPulseSeconds = null!;
        internal static ConfigEntry<float> ReactorBrightness = null!;
        internal static ConfigEntry<float> ReactorLightLumens = null!;
        internal static ConfigEntry<float> ReactorRadiationRadius = null!;
        internal static ConfigEntry<float> ReactorRadiationDamage = null!;
        internal static ConfigEntry<float> ReactorHeatRadius = null!;
        internal static ConfigEntry<float> ReactorUnstableAt = null!;
        internal static ConfigEntry<float> ReactorPartDamage = null!;
        internal static ConfigEntry<float> ReactorShipDamage = null!;
        internal static ConfigEntry<bool> ReactorPlayerDamage = null!;
        internal static ConfigEntry<bool> ReactorHandleTiming = null!;
        internal static ConfigEntry<bool> ReactorHaptics = null!;
        internal static ConfigEntry<bool> ReactorModeBalance = null!;
        internal static ConfigEntry<bool> ReactorModeMemorize = null!;
        internal static ConfigEntry<bool> ReactorModeResonance = null!;
        internal static ConfigEntry<float> ReactorResonanceOffset = null!;
        internal static ConfigEntry<float> ReactorResonanceSpread = null!;
        internal static ConfigEntry<string> ReactorTerminals = null!;
        internal static ConfigEntry<string> ReactorScreenDorsal = null!;
        internal static ConfigEntry<float> ReactorScreenBrightness = null!;
        internal static ConfigEntry<bool> ReactorResonanceHint = null!;
        internal static ConfigEntry<string> ReactorScreenPosition = null!;
        internal static ConfigEntry<string> ReactorScreenRotation = null!;
        internal static ConfigEntry<string> ReactorScreenSize = null!;
        internal static ConfigEntry<float> ReactorMeltdownAt = null!;
        internal static ConfigEntry<bool> ReactorEscalation = null!;
        internal static ConfigEntry<float> ReactorEscalateSeconds = null!;
        internal static ConfigEntry<bool> ReactorSounds = null!;
        internal static ConfigEntry<string> ReactorHumSound = null!;
        internal static ConfigEntry<string> ReactorWarningSound = null!;
        internal static ConfigEntry<float> ReactorSoundNear = null!;
        internal static ConfigEntry<float> ReactorSoundFar = null!;
        internal static ConfigEntry<float> ReactorMeltdownSeconds = null!;
        internal static ConfigEntry<bool> ReactorMeltdown = null!;
        internal static ConfigEntry<KeyboardShortcut> ReactorRightKey = null!;
        internal static ConfigEntry<KeyboardShortcut> ReactorWrongKey = null!;
        internal static ConfigEntry<bool> DrivePlume = null!;
        internal static ConfigEntry<string> PlumeAt = null!;
        internal static ConfigEntry<float> PlumeLength = null!;
        internal static ConfigEntry<float> PlumeMouthDiameter = null!;
        internal static ConfigEntry<float> PlumeSpread = null!;
        internal static ConfigEntry<float> PlumeBrightness = null!;
        internal static ConfigEntry<UnityEngine.Color> PlumeColor = null!;
        internal static ConfigEntry<float> PlumeTurbulence = null!;
        internal static ConfigEntry<float> PlumeFlowSpeed = null!;
        internal static ConfigEntry<float> PlumeSheath = null!;
        internal static ConfigEntry<float> PlumeCore = null!;
        internal static ConfigEntry<float> PlumeCoreDiameter = null!;
        internal static ConfigEntry<int> PlumeShockDiamonds = null!;
        internal static ConfigEntry<float> PlumeStreak = null!;
        internal static ConfigEntry<float> PlumeLightLumens = null!;
        internal static ConfigEntry<float> PlumeTestThrottle = null!;
        internal static ConfigEntry<bool> FlightControl = null!;
        internal static ConfigEntry<float> FlightSitDistance = null!;
        internal static ConfigEntry<UnityEngine.Vector3> FlightSeatOffset = null!;
        internal static ConfigEntry<float> FlightSeatYaw = null!;
        internal static ConfigEntry<float> FlightCameraDistance = null!;
        internal static ConfigEntry<float> FlightDriveAcceleration = null!;
        internal static ConfigEntry<float> FlightThrusterAcceleration = null!;
        internal static ConfigEntry<float> FlightTurnAcceleration = null!;
        internal static ConfigEntry<float> FlightMaxTurnRate = null!;
        internal static ConfigEntry<float> FlightMouseSensitivity = null!;
        internal static ConfigEntry<bool> FlightFreeJacks = null!;
        internal static ConfigEntry<float> FlightAboardPull = null!;
        internal static ConfigEntry<float> FlightStandHeight = null!;
        internal static ConfigEntry<float> FlightMasterJackMass = null!;
        internal static ConfigEntry<float> FlightLightJackMass = null!;
        internal static ConfigEntry<bool> SpaceLimits = null!;
        internal static ConfigEntry<float> SpaceWorkAreaMultiplier = null!;
        internal static ConfigEntry<bool> SpaceFlatEarthMode = null!;
        internal static ConfigEntry<bool> SpaceAnglePinning = null!;
        internal static ConfigEntry<float> SpaceTriggerDistance = null!;
        internal static ConfigEntry<bool> SpaceDebugPrint = null!;
        internal static ConfigEntry<bool> SpaceDust = null!;

        const float HauntingSeconds = 0.5f;
        static float sHauntingDue;

        private FileSystemWatcher? _watcher;
        static volatile bool sSettingsFileChanged;
        internal static DateTime OwnWriteTimeUtc;          // when the EXPANSE options page last saved the file
        static float sSettingsFileChangedAt;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enable or disable TheExpanse.");

            RoomPressureTimes = Config.Bind("Rooms", "UsePressureTimes", true,
                "Make each room's own Pressurization Time and Decompression Time count. The game ignores them and changes every room's " +
                "pressure in the same few seconds; with this on, a room whose time is set longer than the game's takes that long. Connected " +
                "rooms change together and take the longest time among them. Rooms at or below the game's own time, and hull breaches, " +
                "behave as stock.");
            ExtendPressureEffects = Config.Bind("Rooms", "ExtendPressureEffects", true,
                "With UsePressureTimes on: keep an atmosphere regulator's hiss and vent effect going for as long as a slowed pressure " +
                "change lasts. They are a fixed few seconds long, so they are started again at even intervals until the change ends. " +
                "The spoken line is not repeated. Off: they play once and stop early, as they do without this setting.");
            PressureEffectSeconds = Config.Bind("Rooms", "PressureEffectSeconds", 2f,
                "With ExtendPressureEffects on: how many seconds one play of the regulator's hiss and vent effect covers. They are started " +
                "again no further apart than this. Lower it if you notice pauses between repeats; raise it if they pile up on each other.");

            GentleSeparation = Config.Bind("Parts", "GentleSeparation", true,
                "On custom ships, stop heavy parts being thrown when they are cut or yanked free. Parts collide as the convex shape " +
                "around their mesh, so on many custom ships neighboring parts overlap, and the game pushes overlapping parts apart at " +
                "up to 10 m/s the moment they separate. With this on, that push is limited by mass: parts of 5 tonnes or more are " +
                "eased apart at 0.3 m/s, parts up to 100 kg keep the game's speed, and parts in between fall on a smooth slide. The " +
                "grapple, tethers, explosions and decompression are not changed. The game's own ships are left alone.");

            HauntStockShips = Config.Bind("Haunting", "HauntStockShips", false,
                "Also add this mod's haunting (ship roll, haunted lights, helmet flicker, node radiation, growth removal) to the " +
                "game's own ghost ships. Off: only custom ghost ships get it. The game's own haunting is not changed either way.");
            RollShip = Config.Bind("Haunting", "RollShip", true,
                "On custom ghost ships, add a haunting action to the game's own three (doors, room pressure, electricity): now and " +
                "then the ship rolls slowly about its long axis, pushing loose parts and you with it. It stops for good when the last " +
                "node is destroyed. The game's own haunting is not changed.");
            RollDegreesPerSecond = Config.Bind("Haunting", "RollDegreesPerSecond", 2f,
                "How fast the ship turns during a roll, in degrees per second. The speed eases in and out over 3 seconds.");
            RollSeconds = Config.Bind("Haunting", "RollSeconds", 20f,
                "How long one roll lasts, in seconds. Speed times this is roughly how far the ship turns each time.");
            RollMinInterval = Config.Bind("Haunting", "RollMinInterval", 180f,
                "The shortest wait between rolls, in seconds. Each wait is a random time between this and RollMaxInterval.");
            RollMaxInterval = Config.Bind("Haunting", "RollMaxInterval", 480f,
                "The longest wait between rolls, in seconds.");
            RollStartShare = Config.Bind("Haunting", "RollStartShare", 1f,
                new ConfigDescription("Rolls happen while the share of nodes left is at or below this. 1: from the start of the shift. " +
                "0.5: only once half the nodes are destroyed, like the game's own haunting, which grows as nodes are lost.",
                new AcceptableValueRange<float>(0f, 1f)));

            RollFrequencyRamp = Config.Bind("Haunting", "RollFrequencyRamp", 3f,
                "Rolls come more often as nodes are destroyed: with the last node left, the waits are this many times shorter than " +
                "RollMinInterval and RollMaxInterval, and in between they shorten evenly with the share of nodes destroyed. 1: no change.");
            RollSpeedRamp = Config.Bind("Haunting", "RollSpeedRamp", 5f,
                "Rolls get faster as nodes are destroyed: with the last node left, the ship turns this many times faster than " +
                "RollDegreesPerSecond, so each roll turns further. In between the speed rises evenly. 1: no change.");

            LightFlicker = Config.Bind("Haunting", "LightFlicker", true,
                "On custom ghost ships, another haunting action: now and then the lights around one spot of the ship flicker for a " +
                "few seconds. Shares its timer with LightBlackout; with both on, each time is one or the other at random.");
            LightBlackout = Config.Bind("Haunting", "LightBlackout", true,
                "On custom ghost ships, another haunting action: now and then the lights around one spot of the ship go dark for a " +
                "while, stuttering as they go and as they come back.");
            LightMinInterval = Config.Bind("Haunting", "LightMinInterval", 60f,
                "The shortest wait between light hauntings, in seconds. Each wait is a random time between this and LightMaxInterval.");
            LightMaxInterval = Config.Bind("Haunting", "LightMaxInterval", 180f,
                "The longest wait between light hauntings, in seconds.");
            LightFrequencyRamp = Config.Bind("Haunting", "LightFrequencyRamp", 3f,
                "Light hauntings come more often as nodes are destroyed: with the last node left, the waits are this many times " +
                "shorter. 1: no change.");
            LightRoomRadius = Config.Bind("Haunting", "LightRoomRadius", 12f,
                "How far around the chosen spot lights are affected, in meters. About one room at the default.");
            FlickerSeconds = Config.Bind("Haunting", "FlickerSeconds", 4f,
                "How long a flicker lasts, in seconds.");
            BlackoutMinSeconds = Config.Bind("Haunting", "BlackoutMinSeconds", 10f,
                "The shortest blackout, in seconds. Each lasts a random time between this and BlackoutMaxSeconds.");
            BlackoutMaxSeconds = Config.Bind("Haunting", "BlackoutMaxSeconds", 30f,
                "The longest blackout, in seconds.");
            HelmetFlicker = Config.Bind("Haunting", "HelmetFlicker", true,
                "On custom ghost ships, the helmet light stutters near a node, more often and for longer the closer you get. Only " +
                "while it is switched on.");
            HelmetFlickerRadius = Config.Bind("Haunting", "HelmetFlickerRadius", 10f,
                "How close to a node the helmet light starts to stutter, in meters.");

            RemoveGrowthAfterLastNode = Config.Bind("Haunting", "RemoveGrowthAfterLastNode", true,
                "On custom ghost ships, the growth painted across the hull, walls and floors disappears when the last node is " +
                "destroyed, and stays gone if the ship is loaded from a save with no nodes left.");

            NodeRadiation = Config.Bind("Haunting", "NodeRadiation", true,
                "On custom ghost ships, being near a node damages your health as radiation. Radiation resistance counts. A node that is " +
                "destroyed stops radiating.");
            NodeRadiationDamage = Config.Bind("Haunting", "NodeRadiationDamage", 3f,
                "Health lost per second right at a node, as a percentage of full health. The doses of several nodes add up.");
            NodeRadiationRadius = Config.Bind("Haunting", "NodeRadiationRadius", 6f,
                "How far from a node its radiation reaches, in meters.");
            NodeRadiationFalloff = Config.Bind("Haunting", "NodeRadiationFalloff", 1f,
                "How the dose drops with distance inside the radius. 0: the same dose everywhere inside, which is how the game's own " +
                "radiation works. 1: falls evenly to nothing at the edge. 2 or more: strong close to the node, weak further out.");

            ReactorControl = Config.Bind("Reactor", "ReactorControl", true,
                "The MCRN Corvette is driven by a Solomon Epstein torch drive. Its core, a ball of fusion plasma, burns in the reactor " +
                "chamber, held in a magnetic bottle by the eight plasma injectors around it. Each injector pushes on the core; " +
                "disconnecting one leaves a gap, and the core drifts " +
                "toward it and pulses faster and larger. Disconnect them in balanced order, like the nuts on a car wheel (opposite " +
                "sides, then the pair in between): one gap at a time is safe, but two gaps side by side make the plasma unstable, " +
                "and it burns what it touches. With all eight disconnected it fades out. Off: the reactor is cold, with no core, no " +
                "light and nothing to shut down (\"Live Reactor\" in the options page).");
            ReactorBallSize = Config.Bind("Reactor", "BallSize", 2f,
                "Diameter of the core when it is in balance, in meters. It grows as the balance goes off.");
            ReactorBallDepth = Config.Bind("Reactor", "BallDepth", 3f,
                "Only used if the ship's reactor fire marker is not found: how far below the ring of injectors the ball sits, in meters.");
            ReactorPulseSeconds = Config.Bind("Reactor", "PulseSeconds", 1.5f,
                "How long one pulse (beat) of the core takes, in seconds; steady whatever the balance. The core swells to twice its " +
                "size and back, and is smaller as fewer injectors feed it. The handle lights step on these beats.");
            if (UnityEngine.Mathf.Approximately(ReactorPulseSeconds.Value, 4f)) ReactorPulseSeconds.Value = (float)ReactorPulseSeconds.DefaultValue;   // the old default
            ReactorBrightness = Config.Bind("Reactor", "Brightness", 10f,
                "How bright the plain stand-in ball glows, drawn only if the game's fire effect cannot be loaded.");
            ReactorLightLumens = Config.Bind("Reactor", "LightLumens", 30000f,
                "How much light the plasma casts on the chamber, in lumens.");
            ReactorRadiationRadius = Config.Bind("Reactor", "RadiationRadius", 4f,
                "How far the plasma's radiation reaches from the ball's surface, in meters. The dose falls evenly to nothing at the edge. " +
                "Radiation resistance counts.");
            ReactorRadiationDamage = Config.Bind("Reactor", "RadiationDamage", 4f,
                "Health lost per second at the ball's surface, as a percentage of full health.");
            ReactorHeatRadius = Config.Bind("Reactor", "HeatRadius", 0.5f,
                "Coming this close to the ball's surface, in meters, sets you on fire.");
            ReactorUnstableAt = Config.Bind("Reactor", "UnstableAt", 1.05f,
                "Imbalance (in injectors' worth) above which the plasma is unstable (level 2): a fire cloud wraps it (harmless). " +
                "1.05 lets any single gap, and any slightly off opposite pair (0.77), through safely.");
            ReactorPlayerDamage = Config.Bind("Reactor", "PlayerDamage", true,
                "The core hurts you: its radiation, its fire if you touch it, a flashback from a badly timed injector release, the " +
                "level 3 donut flare (heat) and its detonation. Off: it never hurts you, though a detonation still throws you.");
            ReactorHandleTiming = Config.Bind("Reactor", "PlasmaInjectorReleaseTiming", true,
                "Like the game's breakers: three lights on each injector's handle console light red one by one on the core's beats " +
                "and go dark again; the dark moment narrows as the core gets more unstable. Pull the handle while they are dark and it lets go safely; pull it at any " +
                "other moment and the plasma flashes back along it and sets you on fire (with PlayerDamage on). Cutting the " +
                "purple pipe instead is not timed. Off: no lights, and a handle can be pulled at any time.");
            ReactorShipDamage = Config.Bind("Reactor", "ShipDamage", 100f,
                new ConfigDescription("How hard the core hits the ship, in percent: the level 3 donut flare destroys the eight " +
                "injectors, and when the reactor above the core explodes or is pulled while the plasma burns, the core detonates " +
                "with twice the reactor's own reach (200: four times). 0: the donut destroys nothing, the reactor never melts down " +
                "and the core goes out without a blast. What it does to you is PlayerDamage.", new AcceptableValueRange<float>(0f, 200f)));
            ReactorPartDamage = Config.Bind("Reactor", "PartDamage", 4f,
                "Only if the reactor has no explosion settings of its own (the fallback blast): integrity an injector loses from it, at ShipDamage 100.");
            ReactorMeltdown = Config.Bind("Reactor", "Meltdown", true,
                "After the level 3 donut flare, start the game's own meltdown of the reactor above the core (losing the injectors " +
                "does not start it by itself); when that reactor explodes, the core detonates with it. Off, or ShipDamage 0: no " +
                "meltdown. The donut flare itself comes either way.");
            ReactorMeltdownAt = Config.Bind("Reactor", "MeltdownAt", 2.4f,
                "Imbalance (in injectors' worth) that counts toward a meltdown. Three gaps side by side are 2.41.");
            ReactorMeltdownSeconds = Config.Bind("Reactor", "MeltdownSeconds", 5f,
                "How long the plasma can stay at level 3 (MeltdownAt or more) before the donut flare, in seconds; the warning beep " +
                "races in the last 5.");
            if (UnityEngine.Mathf.Approximately(ReactorMeltdownSeconds.Value, 10f)) ReactorMeltdownSeconds.Value = 5f;   // the old default

            ReactorEscalation = Config.Bind("Reactor", "Escalation", true,
                "Plasma left off balance gets worse by itself: every EscalateSeconds it goes up a level (level 3 starts the donut " +
                "flare countdown). A disconnect that evens the balance out restarts the wait and takes a level off; a wrong one " +
                "leaves the clock running. Back in balance it is all gone. Off: only the injectors (and mistakes) decide the level.");
            ReactorEscalateSeconds = Config.Bind("Reactor", "EscalateSeconds", 45f,
                "With Escalation on: how long a problem can be left before the core goes up a level, in seconds.");
            if (UnityEngine.Mathf.Approximately(ReactorEscalateSeconds.Value, 30f)) ReactorEscalateSeconds.Value = 45f;   // the old default

            ReactorModeBalance = Config.Bind("Reactor", "ModeBalance", true,
                "Shutdown mode Balance: no set order; keep the plasma balanced. Each ship picks one of the modes switched on, at random.");
            ReactorModeMemorize = Config.Bind("Reactor", "ModeMemorize", true,
                "Shutdown mode Memorize: two engineering deck terminals flash the order to disconnect the injectors in.");
            ReactorModeResonance = Config.Bind("Reactor", "ModeResonance", true,
                "Shutdown mode Resonance: the handle lights of the injector to release next tick exactly on the core's beat; the others tick off it.");
            ReactorResonanceOffset = Config.Bind("Reactor", "ResonanceOffset", 0.5f,
                "Resonance: how far off the beat the other injectors' lights tick, in beats (0.5: half way between beats, the easiest " +
                "to tell; smaller is harder).");
            ReactorResonanceSpread = Config.Bind("Reactor", "ResonanceSpread", 0.12f,
                "Resonance: how much the other injectors' offsets differ from one another, in beats, either side of ResonanceOffset " +
                "(none ticks closer than 0.2 beat to the beat itself).");
            ReactorTerminals = Config.Bind("Reactor", "Terminals", "",
                "Which nav terminals show the shutdown screen: their numbers from the log line '[Reactor] nav terminals near the " +
                "core', comma separated (e.g. 0,2). Empty: any two of the four nearest, at random per ship.");
            if (ReactorTerminals.Value.Contains("PRF_")) ReactorTerminals.Value = "";
            ReactorScreenDorsal = Config.Bind("Reactor", "ScreenDorsal", "+Z",
                "Which of the ship's own axes is dorsal (up) on the shutdown screen's diagram: +X, -X, +Y, -Y, +Z or -Z. Change it if " +
                "DRSL / VNTR / PORT / STBD do not match the ship.");
            ReactorScreenBrightness = Config.Bind("Reactor", "ScreenBrightness", 1.5f,
                "How bright the shutdown screen is.");
            if (UnityEngine.Mathf.Approximately(ReactorScreenBrightness.Value, 4f)) ReactorScreenBrightness.Value = 1.5f;
            ReactorScreenPosition = Config.Bind("Reactor", "ScreenPosition", "",
                "Debug: where the shutdown screen sits on its terminal, in the terminal's own space, meters, as x,y,z. Empty: on " +
                "the terminal's front face, measured from its mesh. Re-read live from the settings file.");
            ReactorScreenRotation = Config.Bind("Reactor", "ScreenRotation", "",
                "Debug: the shutdown screen's turn on its terminal, degrees, as x,y,z. Empty: flush on the measured face.");
            if (ReactorScreenRotation.Value.Replace(" ", "") == "0,0,0" || ReactorScreenRotation.Value.Replace(" ", "") == "0,-90,0") ReactorScreenRotation.Value = "";
            ReactorScreenSize = Config.Bind("Reactor", "ScreenSize", "",
                "Debug: the shutdown screen's width and height, meters, as w,h. Empty: the measured face's.");
            if (ReactorScreenSize.Value.Replace(" ", "") == "0.45,0.7" || ReactorScreenSize.Value.Replace(" ", "") == "0.42,0.68") ReactorScreenSize.Value = "";
            ReactorResonanceHint = Config.Bind("Reactor", "ResonanceRingHint", false,
                "Resonance: the screen also rings the injector to release next. Off: find it by its pulse alone.");
            ReactorHaptics = Config.Bind("Reactor", "Haptics", true,
                "A controller rumbles with each pulse of the core while you are near it (stronger off balance, and while you hold " +
                "the reactor), following the game's own vibration setting.");
            ReactorSounds = Config.Bind("Reactor", "Sounds", true,
                "The reactor makes sounds, from the core: a hum, and a warning beep while unstable.");
            ReactorHumSound = Config.Bind("Reactor", "HumSound", "sfx_env_powerGenerator",
                "The game sound (Wwise event name) the reactor hums with. It loops.");
            ReactorWarningSound = Config.Bind("Reactor", "WarningSound", "sfx_hud_alert_shift_countdown",
                "The game sound played once a second while the plasma is unstable, and faster and faster in the last 5 seconds " +
                "before it goes up a level by itself or melts down. A helmet (sfx_hud_...) sound is heard clearly in vacuum too; " +
                "a sound from the ship itself is muffled there, as the game muffles all of them.");
            // The first choice, a beep from the ship's own breakers, was barely heard in the vacuum of the reactor room.
            if (ReactorWarningSound.Value == "sfx_powerGenerator_breaker_beep_unsafe") ReactorWarningSound.Value = (string)ReactorWarningSound.DefaultValue;
            ReactorSoundNear = Config.Bind("Reactor", "SoundNear", 8f,
                "Within this many meters of the ball the reactor is heard at full volume.");
            ReactorSoundFar = Config.Bind("Reactor", "SoundFar", 18f,
                "At this many meters from the ball the reactor can no longer be heard.");

            DrivePlume = Config.Bind("Drive", "DrivePlume", false,
                "Draw the MCRN Corvette's drive plume. Off until the flight controls come; when on, it burns all the time at TestThrottle.");
            PlumeAt = Config.Bind("Drive", "PlumeAt", "Bell",
                new ConfigDescription("Where the plume burns. Bell: out of the mouth of the ship's engine bell (its EngineNozzle parts if the " +
                    "bell is gone). MasterJack: a test spot 15 m above the bay's Master Jack, pointing up.",
                    new AcceptableValueList<string>("Bell", "MasterJack")));
            PlumeLength = Config.Bind("Drive", "Length", 75f,
                "How far the plume reaches from the engine bell at full throttle, in meters. Lower throttle shortens it in proportion.");
            PlumeTestThrottle = Config.Bind("Drive", "TestThrottle", 0.25f,
                "For testing until the flight controls drive it: the throttle the plume burns at, 0.01 to 1. Length and FlowSpeed scale with it.");
            PlumeMouthDiameter = Config.Bind("Drive", "MouthDiameter", 0f,
                "How wide the base plume is where it leaves the engine bell, in meters. 0: as wide as the bell's mouth, measured on the ship " +
                "(also at the test spot; 5 m there until the bell is found).");
            PlumeSpread = Config.Bind("Drive", "Spread", 1.4f,
                "How much wider the plume gets downstream: its widest, as a multiple of MouthDiameter. 1 keeps it a straight beam.");
            PlumeBrightness = Config.Bind("Drive", "Brightness", 1f,
                "How bright the whole plume glows. Core and Sheath set the two parts against each other.");
            PlumeColor = Config.Bind("Drive", "Color", new UnityEngine.Color(0.12f, 0.35f, 1f, 1f),
                "The color of the base plume; the core burns toward white. Keep red and green low or it washes out to white. The mouth glow takes a new color on the next level load.");
            PlumeTurbulence = Config.Bind("Drive", "Turbulence", 0.6f,
                "How much the sheath billows, 0 to 1. 0: smooth.");
            PlumeSheath = Config.Bind("Drive", "Sheath", 0.6f,
                "How bright the base plume is: the blue glow filling the bell's mouth around the core. Above about 1 it washes out to " +
                "white. 0: core only.");
            PlumeCore = Config.Bind("Drive", "Core", 4f,
                "How bright the white-hot core down the middle of the plume is, and its shock diamonds and the glow at the nozzle.");
            PlumeCoreDiameter = Config.Bind("Drive", "CoreDiameter", 3f,
                "How wide the white-hot core is at the bell, in meters.");
            PlumeFlowSpeed = Config.Bind("Drive", "FlowSpeed", 300f,
                "How fast the billows run down the plume at full throttle, in meters per second. Lower throttle slows them in proportion.");
            PlumeShockDiamonds = Config.Bind("Drive", "ShockDiamonds", 4,
                "How many bright shock diamonds sit in the core near the nozzle, 0 to 12.");
            PlumeStreak = Config.Bind("Drive", "Streak", 1f,
                "How bright the thin horizontal flare streak through the nozzle glow is. 0: none.");
            PlumeLightLumens = Config.Bind("Drive", "LightLumens", 150000f,
                "How much light the plume casts on the nozzle and the hull, in lumens.");

            FlightControl = Config.Bind("Flight", "FlightControl", false,
                "Sit at the MCRN Corvette's controls: near a command chair, press Interact. Seated, the view goes outside the ship " +
                "(the tool mode button switches between outside and the seat; hold the right mouse button or the View button to look " +
                "around and zoom) and Interact gets you up again. The flight controls themselves are still to come.");
            FlightSitDistance = Config.Bind("Flight", "SitDistance", 2.5f,
                "How close to a command chair you have to be to sit in it, in meters.");
            FlightSeatOffset = Config.Bind("Flight", "SeatOffset", new UnityEngine.Vector3(0f, 0.9f, 0f),
                "Where you sit, from the chair's origin, in the chair's own axes (x right, y up, z forward), in meters.");
            FlightSeatYaw = Config.Bind("Flight", "SeatYaw", 0f,
                "Which way you face in the chair: degrees turned about the chair's up from its forward.");
            FlightCameraDistance = Config.Bind("Flight", "CameraDistance", 0f,
                "How far the outside view starts from the ship's middle, in meters. 0: 1.3 times the ship's length.");
            FlightDriveAcceleration = Config.Bind("Flight", "DriveAcceleration", 5f,
                "How hard the main drive pushes at full throttle, in meters per second per second (9.8 is 1 g).");
            FlightThrusterAcceleration = Config.Bind("Flight", "ThrusterAcceleration", 1f,
                "How hard the side thrusters push the ship, in meters per second per second. The brake uses them too.");
            FlightTurnAcceleration = Config.Bind("Flight", "TurnAcceleration", 10f,
                "How quickly the ship starts and stops turning, in degrees per second per second.");
            FlightMaxTurnRate = Config.Bind("Flight", "MaxTurnRate", 20f,
                "The fastest the ship turns, in degrees per second.");
            FlightMouseSensitivity = Config.Bind("Flight", "MouseSensitivity", 1f,
                "How much turning the mouse gives: degrees per second of turn added per unit of mouse movement.");
            FlightAboardPull = Config.Bind("Flight", "AboardPull", 1f,
                "Out of the seat, aboard a ship under way: how strongly the drive's push pulls you toward the stern, as a share of " +
                "the ship's acceleration: artificial gravity toward the stern. 1 is the full push, 0 floats you with the ship as if it were coasting.");
            FlightStandHeight = Config.Bind("Flight", "StandHeight", 1.6f,
                "Out of the seat while the drive pulls you toward the stern: how high above the deck you are held, in meters, like " +
                "standing in magnetic boots (your helmet's height).");
            FlightFreeJacks = Config.Bind("Flight", "FreeJacks", true,
                "When the ship first moves under its own power, let go of the bay's jacks: they get a weight and are shoved out of " +
                "the way by the hull instead of being passed through. They stay where they drift until the next level load.");
            FlightMasterJackMass = Config.Bind("Flight", "MasterJackMass", 200f,
                "With FreeJacks on: the Master Jack's weight, in tonnes.");
            FlightLightJackMass = Config.Bind("Flight", "LightJackMass", 20f,
                "With FreeJacks on: each light jack's weight, in tonnes.");

            SpaceLimits = Config.Bind("Space", "ExtendedLimits", true,
                "Room to fly. The ship and loose pieces are not destroyed for straying from the work area (see WorkAreaRadiusMultiplier; " +
                "never while the ship is under way), the seated pilot has no speed cap, and, without the NewtonianPhysics mod, the " +
                "player's work-area warning is lifted the same way and the planets and background are kept right far from the bay. " +
                "With NewtonianPhysics installed, its own settings handle the player's work area and the background.");
            SpaceWorkAreaMultiplier = Config.Bind("Space", "WorkAreaRadiusMultiplier", 0f,
                "How far from the game's work areas things may go before the game warns, hurts or destroys: 1 is stock, 2 twice " +
                "as far, 0 no limit at all.");
            SpaceFlatEarthMode = Config.Bind("Space", "FlatEarthMode", false,
                "Without NewtonianPhysics: the celestial bodies stay at a fixed offset from you once you are TriggerDistance from " +
                "the bay, instead of drifting past as stock. On restores the stock behavior.");
            SpaceAnglePinning = Config.Bind("Space", "AnglePinning", false,
                "Without NewtonianPhysics: off keeps the flat background cards at a constant distance from you; on locks each " +
                "card's angle where it was when pinning started. Takes effect on the next shift.");
            SpaceTriggerDistance = Config.Bind("Space", "TriggerDistance", 250f,
                "Without NewtonianPhysics: how far from the work bay, in meters, the background pinning starts.");
            SpaceDebugPrint = Config.Bind("Space", "DebugPrint", false,
                "Extra lines in the log: the background pinning, and while flying the frame rate every few seconds and the suit's speed aboard twice a second.");
            SpaceDust = Config.Bind("Space", "SpaceDust", false,
                "Show the game's space dust: the drifting specks and speed streaks around you. Off by default.");

            TestTools = Config.Bind("Debug", "TestTools", false,
                "For testing the ghost ship additions and reactor control: show how many nodes are left and the reactor's state on " +
                "screen, destroy half of the nodes with KillHalfNodesKey, and pretend to disconnect injectors with ReactorRightKey " +
                "and ReactorWrongKey.");
            KillHalfNodesKey = Config.Bind("Debug", "KillHalfNodesKey", new KeyboardShortcut(UnityEngine.KeyCode.F8),
                "With TestTools on: destroys half of the nodes left (rounded up, so the last one goes too).");
            ReactorRightKey = Config.Bind("Debug", "ReactorRightKey", new KeyboardShortcut(UnityEngine.KeyCode.F11),
                "With TestTools on: treats the injector that keeps the plasma best balanced as disconnected, without touching the ship.");
            ReactorWrongKey = Config.Bind("Debug", "ReactorWrongKey", new KeyboardShortcut(UnityEngine.KeyCode.F11, UnityEngine.KeyCode.LeftShift),
                "With TestTools on: treats the injector that unbalances the plasma most as disconnected, without touching the ship.");

            RemoveRetiredSettings();

            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            CustomShips.Install(harmony);
            RoomPressure.Install(harmony);
            // The EXPANSE options page. Per class, so a patch whose target moved in a game update is logged and skipped.
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                if (!type.IsDefined(typeof(HarmonyPatch), inherit: false)) continue;
                // SpaceLimits patches its own classes in Start, some only when NewtonianPhysics is absent; patched twice,
                // each took the ship's speed off the suit twice and put it back twice (the doubled speed aboard)
                if (type.DeclaringType == typeof(TheExpanse.SpaceLimits)) continue;
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (Exception ex) { Log.LogError($"Patch {type.Name} failed; the EXPANSE options page may not work: {ex.GetBaseException().Message}"); }
            }

            Main.EventSystem.AddHandler<LevelLoadEvent>(OnLevelLoad);
            Main.EventSystem.AddHandler<ShipAllSpawnWrapperEvent>(ev =>
            {
                // A ship loaded from a save (a FreePlayPlus resume, a continued shift): make sure it is known as custom or not.
                if (ev.Type == ShipAllSpawnWrapperEvent.SpawnType.SpawnFromSave) CustomShips.FromSave(ev.ShipPreview);
                if (ev.State == ShipAllSpawnWrapperEvent.SpawnState.Start)
                {
                    try { FlightJacks.RecordBaked(); }
                    catch (Exception ex) { Log.LogError("[Flight] " + ex); }
                }
                if (ev.State != ShipAllSpawnWrapperEvent.SpawnState.Complete) return;
                GrowthCleanup.OnShipSpawned();
                TheExpanse.ReactorControl.OnShipSpawned();
            });
            // The moment a joint goes, for timing an injector's handle.
            Main.EventSystem.AddHandler<RemoveHierarchyJointsEvent>(ev => TheExpanse.ReactorControl.OnJointEvent(ev.RemoveJointsFor));
            Main.EventSystem.AddHandler<GrabEvent>(TheExpanse.ReactorControl.OnGrab);
            Main.EventSystem.AddHandler<InteractionEvent>(TheExpanse.ReactorControl.OnInteraction);
            Main.EventSystem.AddHandler<BreakableJointEvent>(ev =>
            {
                if (ev.State == BreakableJointEvent.BreakableJointState.Break && ev.BreakableJoint != null)
                    TheExpanse.ReactorControl.OnJointEvent(ev.BreakableJoint.StructurePart);
            });
            WatchSettingsFile();
            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} {PluginInfo.PLUGIN_VERSION} loaded.");
        }

        // ---- settings file ------------------------------------------------------------------------------------------
        // Saving the settings file while the game runs re-reads it within a second, for tuning during a shift.
        private void WatchSettingsFile()
        {
            try
            {
                string path = Config.ConfigFilePath;
                _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                };
                FileSystemEventHandler changed = (_, __) => sSettingsFileChanged = true;     // the watcher's own thread: no Unity calls
                _watcher.Changed += changed;
                _watcher.Created += changed;
                _watcher.Renamed += (_, __) => sSettingsFileChanged = true;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Log.LogWarning("Could not watch the settings file; changes apply when a ship loads: " + ex.Message); }
        }

        private void OnDestroy() => _watcher?.Dispose();

        // Settings that are no longer used are dropped from the settings file (BepInEx keeps unknown lines otherwise).
        static readonly string[] sRetired = { "Reactor/PulseSound", "Reactor/HapticDelay", "Reactor/HapticSeconds", "Reactor/BurnsParts", "Reactor/Damage",
                                              "Reactor/SafeWindow", "Reactor/MinSafeSeconds", "Reactor/HandleTiming", "Reactor/PulsesPerLight", "Reactor/BurnInterval", "Reactor/EscalateMaxSeconds", "Reactor/EscalateMinSeconds", "Reactor/ResonanceHint", "Debug/FxGalleryKey", "Debug/FxGalleryBackKey" };
        private void RemoveRetiredSettings()
        {
            try
            {
                var orphans = typeof(ConfigFile).GetProperty("OrphanedEntries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                    ?.GetValue(Config) as System.Collections.Generic.Dictionary<ConfigDefinition, string>;
                if (orphans == null) return;
                int removed = 0;
                foreach (var key in new System.Collections.Generic.List<ConfigDefinition>(orphans.Keys))
                    if (System.Array.IndexOf(sRetired, key.Section + "/" + key.Key) >= 0) { orphans.Remove(key); removed++; }
                if (removed > 0) { Config.Save(); Log.LogInfo($"Removed {removed} retired setting(s) from the settings file."); }
            }
            catch (Exception ex) { Log.LogWarning("Could not tidy retired settings from the settings file: " + ex.Message); }
        }

        // After the settings change, from the file or the EXPANSE options page.
        internal static void OnSettingsChanged()
        {
            ShipRoll.OnSettingsChanged();
            HauntedLights.OnSettingsChanged();
        }

        private void OnGUI()
        {
            try { NodeTestTools.Draw(); TheExpanse.ReactorControl.DrawTest(); }
            catch (Exception ex) { Log.LogError("[Test] " + ex); }
        }

        // After every plugin's Awake: whether NewtonianPhysics is there is known by now.
        private void Start()
        {
            try
            {
                TheExpanse.SpaceLimits.Install(new Harmony(PluginInfo.PLUGIN_GUID + ".space"));
                Main.EventSystem.AddHandler<GameStateChangedEvent>(TheExpanse.SpaceLimits.OnGameStateChanged);
                Main.EventSystem.AddHandler<GameStateChangedEvent>(TheExpanse.NoDragAboard.OnGameStateChanged);
            }
            catch (Exception ex) { Log.LogError("[Space] " + ex); }
        }

        private void Update()
        {
            try { NodeTestTools.Tick(); TheExpanse.ReactorControl.TestKeys(); }
            catch (Exception ex) { Log.LogError("[Test] " + ex); }
            try { TheExpanse.SpaceLimits.Tick(); }
            catch (Exception ex) { Log.LogError("[Space] " + ex); }
            try { FlightModel.AimCamera(); }
            catch (Exception ex) { Log.LogError("[Flight] " + ex); }
            try { FlightModel.SampleFrame(); }
            catch (Exception ex) { Log.LogError("[Flight] " + ex); }
            try { MagBootsGuard.Tick(); }
            catch (Exception ex) { Log.LogError($"MagBootsGuard.Tick failed: {ex}"); }
            try { DeckBoots.Tick(); }
            catch (Exception ex) { Log.LogError($"DeckBoots.Tick failed: {ex}"); }
            try { FlightSeat.Tick(); }
            catch (Exception ex) { Log.LogError("[Flight] " + ex); }
            // Wait for the editor to finish writing before reading the file.
            if (sSettingsFileChanged)
            {
                sSettingsFileChanged = false;
                sSettingsFileChangedAt = UnityEngine.Time.unscaledTime;
            }
            // Not while the EXPANSE options page is open (its unsaved changes would be lost), nor for the page's own save.
            else if (sSettingsFileChangedAt > 0f && UnityEngine.Time.unscaledTime - sSettingsFileChangedAt > 0.5f && !EditSession.IsOpen)
            {
                sSettingsFileChangedAt = 0f;
                try
                {
                    if (File.GetLastWriteTimeUtc(Config.ConfigFilePath) == OwnWriteTimeUtc) return;
                    Config.Reload();
                    OnSettingsChanged();
                    Log.LogInfo("Settings file re-read; the new values apply now.");
                }
                catch (Exception ex) { Log.LogWarning("Could not re-read the settings file; keeping the current settings: " + ex.Message); }
            }
        }

        private void LateUpdate()
        {
            try { RoomPressure.Tick(); }
            catch (Exception ex) { Log.LogError("[RoomPressure] " + ex); }
            try { PartSeparation.Tick(); }
            catch (Exception ex) { Log.LogError("[Parts] " + ex); }
            try { HauntingTick(); HauntedLights.Frame(); }
            catch (Exception ex) { Log.LogError("[Haunting] " + ex); }
            try { TheExpanse.ReactorControl.Frame(); }
            catch (Exception ex) { Log.LogError("[Reactor] " + ex); }
            try { TheExpanse.DrivePlume.Frame(); }
            catch (Exception ex) { Log.LogError("[Drive] " + ex); }
            try { FlightHud.Frame(); SuitAccelReadout.Frame(); }
            catch (Exception ex) { Log.LogError("[Flight] " + ex); }
            try { FlightJacks.Frame(); }
            catch (Exception ex) { Log.LogError("[Flight] " + ex); }
            try { FlightModel.LateFrame(); }
            catch (Exception ex) { Log.LogError("[Flight] " + ex); }
            // after the ship's frame is smoothed for this frame: the background is placed from it
            try { TheExpanse.SpaceLimits.LateTick(); }
            catch (Exception ex) { Log.LogError("[Space] " + ex); }
            try { FlightCamera.Frame(); }
            catch (Exception ex) { Log.LogError("[Flight] " + ex); }
        }

        // The haunting additions run on custom ships, and on the game's own ships too with HauntStockShips on.
        internal static bool HauntThisShip => PartSeparation.CustomShip || HauntStockShips.Value;

        // The ghost ship additions, a couple of times a second of game time (so not while paused).
        private static void HauntingTick()
        {
            if (UnityEngine.Time.time < sHauntingDue) return;
            sHauntingDue = UnityEngine.Time.time + HauntingSeconds;
            if (!Enabled.Value || !HauntThisShip)
            {
                GhostNodes.Reset();
                ShipRoll.Schedule();            // with no nodes counted, this eases any roll under way to a stop
                HauntedLights.Schedule();       // and this puts any haunted lights back
                return;
            }
            GhostNodes.Refresh();
            TheExpanse.NodeRadiation.Apply(HauntingSeconds);
            ShipRoll.Schedule();
            HauntedLights.Schedule();
            GrowthCleanup.Tick();
        }

        private void FixedUpdate()
        {
            FlightModel.MarkStep();
            try { ShipRoll.FixedStep(); }
            catch (Exception ex) { Log.LogError("[Haunting] " + ex); }
            try { FlightSeat.FixedTick(); }
            catch (Exception ex) { Log.LogError("[Flight] " + ex); }
        }

        // Start is posted before the scenes load.
        private static void OnLevelLoad(LevelLoadEvent ev)
        {
            if (ev.State != LevelLoadEvent.LoadState.Start) return;
            PartSeparation.CustomShip = false;
            GhostNodes.Reset();
            ShipRoll.Reset();
            HauntedLights.Reset();
            GrowthCleanup.Reset();
            TheExpanse.NodeRadiation.Reset();
            TheExpanse.ReactorControl.Reset();
            TheExpanse.DrivePlume.Reset();
            FlightSeat.Reset();
            DeckBoots.Reset();
            MagBootsGuard.Resume();
            FlightJacks.Reset();
            FlightHud.Reset();
            SuitAccelReadout.Reset();
            sHauntingDue = 0f;
            // Pick up edits made to the settings file while the game was running, before the next ship loads.
            if (!EditSession.IsOpen) try { Instance.Config.Reload(); }
            catch (Exception ex) { Log.LogWarning("Could not re-read the settings file; keeping the current settings: " + ex.Message); }
            RoomPressure.OnLevelLoadStart();
        }
    }
}
