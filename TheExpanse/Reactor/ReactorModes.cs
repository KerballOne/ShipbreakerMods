using System.Collections.Generic;
using System.Linq;
using BBI.Unity.Game;
using UnityEngine;

namespace TheExpanse
{
    // The shutdown modes. One is picked at random for each ship from those switched on (seeded from the ship, so a
    // reload picks the same):
    //   Balance:   no set order; keep the plasma balanced (opposite sides, then the pair in between).
    //   Memorize:  the engineering deck terminals flash an order; disconnect the injectors in that order.
    //   Resonance: the handle lights of the injector to release next tick exactly on the core's beat; the others tick
    //              off the beat.
    // In Memorize and Resonance the order (any order, at random) is the only rule: the balance does not count. A wrong
    // injector is a mistake: the core flashes red and goes up a level (no fire; fire is only for a badly timed
    // release), and the clock starts: every EscalateSeconds until a correct one, up a level too. A correct one resets
    // the core to level 0 and stops the clock. The injector that was owed stays next; the wrong one is struck off.
    internal static partial class ReactorControl
    {
        internal enum Mode { Balance, Memorize, Resonance, Free }      // Free: all modes switched off, no challenge
        internal static Mode CurrentMode { get; private set; } = Mode.Balance;
        static readonly List<int> sOrder = new List<int>();     // still owed, in order (Memorize, Resonance)
        static int sPenalty;                                      // levels added by mistakes
        internal static int MistakeInjector { get; private set; } = -1;
        internal static float MistakeAt { get; private set; } = -100f;
        internal static IReadOnlyList<int> Order => sOrder;
        internal static int Seed { get; private set; }

        static void ChooseMode(StructurePart core)
        {
            Seed = SeedFor(core);
            var random = new System.Random(Seed);
            var enabled = new List<Mode>();
            if (Plugin.ReactorModeBalance.Value) enabled.Add(Mode.Balance);
            if (Plugin.ReactorModeMemorize.Value && TerminalScreens.Available) enabled.Add(Mode.Memorize);
            if (Plugin.ReactorModeResonance.Value) enabled.Add(Mode.Resonance);
            if (enabled.Count == 0) enabled.Add(Mode.Free);   // no challenge: no wrong choices, no levels; every effect stays
            CurrentMode = enabled[random.Next(enabled.Count)];

            // Any order.
            sOrder.Clear();
            sOrder.AddRange(Enumerable.Range(0, Count));
            for (int k = sOrder.Count - 1; k > 0; k--) { int r = random.Next(k + 1); (sOrder[k], sOrder[r]) = (sOrder[r], sOrder[k]); }
            sOrder.RemoveAll(i => !Connected(i));       // already out (a reloaded save)
            sPenalty = 0; MistakeInjector = -1; MistakeAt = -100f;
            Plugin.Log.LogInfo($"[Reactor] shutdown seed {Seed}, from {SeedSource()}.");
            Plugin.Log.LogInfo($"[Reactor] shutdown mode: {CurrentMode}" + (CurrentMode == Mode.Balance || CurrentMode == Mode.Free ? "." : $"; order {string.Join(" ", sOrder)}.") +
                               (Plugin.ReactorModeMemorize.Value && !TerminalScreens.Available ? " (Memorize left out: no terminal screen found.)" : ""));
        }

        // The ship's own random seed: the game's seed for this ship (picked at random for every ship it generates and
        // kept in the ship's save) with its generated name mixed in. A new spawn picks anew; the same ship (a reload,
        // also after flying it somewhere else) picks the same. (From the core's name and place, every fresh spawn of a
        // ship picked the same mode and order, and a ship flown and saved elsewhere picked anew.)
        internal static int SeedFor(StructurePart core)
        {
            ShipPreview? preview = null;
            try { preview = ModuleService.Instance?.CurrentShipPreview; } catch { }
            preview ??= ShipSetup.Preview;
            if (preview != null)
            {
                int name = StableHash(preview.ShipName ?? "");
                return (preview.Seed * 31 ^ name) & 0x7fffffff;
            }
            var p = core.transform.position;
            return (StableHash(core.name) * 31 + Mathf.RoundToInt(p.x) * 73856093 ^ Mathf.RoundToInt(p.y) * 19349663 ^ Mathf.RoundToInt(p.z) * 83492791) & 0x7fffffff;
        }

        // The same for the same text in every run (string.GetHashCode need not be)
        static int StableHash(string text)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in text) { h ^= c; h *= 16777619; }
                return (int)h;
            }
        }

        static string SeedSource()
        {
            ShipPreview? preview = null;
            try { preview = ModuleService.Instance?.CurrentShipPreview; } catch { }
            preview ??= ShipSetup.Preview;
            return preview != null ? $"the ship's seed {preview.Seed} and name '{preview.ShipName}'" : "the core's name and place (no ship preview)";
        }

        static int Wrap(int i) => ((i % Count) + Count) % Count;

        // The injector owed next, or -1 (Balance, or nothing left).
        internal static int Owed
        {
            get
            {
                if (CurrentMode == Mode.Balance || CurrentMode == Mode.Free) return -1;
                foreach (int i in sOrder) if (Connected(i)) return i;
                return -1;
            }
        }

        // Every disconnect: in Memorize and Resonance, was it the one owed?
        static void CheckOrder(int i)
        {
            if (CurrentMode == Mode.Balance || CurrentMode == Mode.Free || sOrder.Count == 0) return;
            sOrder.RemoveAll(x => x != i && !Connected(x));      // gone some other way (the donut flare, a test key)
            if (sOrder.Count == 0) return;
            if (sOrder[0] == i)
            {
                sOrder.RemoveAt(0);
                bool wasUp = sPenalty + sEscalation > 0;
                sPenalty = 0; sEscalation = 0; sEscalateFrom = -1f;          // back on course: no clock
                Plugin.Log.LogInfo($"[Reactor] injector {i} was the one owed ({CurrentMode}); {sOrder.Count} to go{(wasUp ? "; the core is back to level 0" : "")}.");
                return;
            }
            sOrder.Remove(i);
            sPenalty = Mathf.Min(3, sPenalty + 1);
            if (sEscalateFrom < 0f) sEscalateFrom = Time.time;   // the clock starts with the first mistake and keeps running
            MistakeInjector = i; MistakeAt = Time.time;
            ReactorVisuals.FlashRed(1.5f);
            ReactorSound.Play("sfx_hud_processor_reject");
            Plugin.Log.LogInfo($"[Reactor] MISTAKE: injector {i} disconnected, {sOrder[0]} was owed ({CurrentMode}); the core goes up a level " +
                               $"({sPenalty} mistake level(s)); {sOrder[0]} is still next.");
        }

        // Resonance: a light's offset from the core's beat, in beats (0 for the one owed and in the other modes).
        static float LightOffset(int i) => CurrentMode == Mode.Resonance && i != Owed ? ResonanceOffsetOf(i) : 0f;

        // Resonance: each other injector's own offset from the beat, in beats: near ResonanceOffset, spread a little
        // either way (ResonanceSpread) so they are not all alike, and never closer than 0.2 beat to the beat itself.
        internal static float ResonanceOffsetOf(int i)
        {
            float spread = Mathf.Clamp(Plugin.ReactorResonanceSpread.Value, 0f, 0.3f);
            float jitter = (((Seed ^ (i * 7919)) * 2654435761u) % 1000u) / 1000f * 2f - 1f;      // -1..1, fixed per ship and injector
            return Mathf.Clamp(Plugin.ReactorResonanceOffset.Value + jitter * spread, 0.2f, 0.8f);
        }
    }
}
