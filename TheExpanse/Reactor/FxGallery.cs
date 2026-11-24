using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.VFX;

namespace TheExpanse
{
    // Testing aid (off unless [Debug] TestTools is on): lines up the game's own effects that could serve the reactor
    // in front of the player, each with a numbered label, so the right ones can be picked by eye. Press the key again
    // to clear them.
    internal static class FxGallery
    {
        enum Kind { Prefab, Vfx, MaterialSphere, MaterialQuad, PrefabPlusVfx, PrefabSet, BallAndPlume }

        sealed class Entry
        {
            internal string Label = "", Key = "";
            internal Kind Kind;
            internal GameObject? Shown;
            internal string Status = "loading";
            // pulsing like the reactor (entries 19 and 21-23)
            internal Transform? Ball, Stretched;
            internal float Radius, Reach, Across;
            internal int Level;
            internal bool MaterialsLogged;
        }

        // The reactor's pulse: 1x..2x for the ball alone, 1x..1.2x while a flare is out; quicker at higher levels.
        // The stretched copy keeps its front the level's reach past the pulsing ball, as in the reactor.
        static float sShownAt;

        // Once per effect, 1.5 s after it shows: what it draws with, to see whether the flames can be tinted.
        static void LogMaterials()
        {
            if (sEntries.Count == 0 || Time.time < sShownAt + 1.5f) return;
            var entry = sEntries[0];
            if (entry.Shown == null || entry.MaterialsLogged) return;
            entry.MaterialsLogged = true;
            Plugin.Log.LogInfo($"[Test] gallery {entry.Label} materials:{FxShape.DescribeMaterials(entry.Shown)}");
        }

        static void Pulse()
        {
            if (sEntries.Count == 0) return;
            var entry = sEntries[0];
            if (entry.Ball == null) return;
            bool flare = entry.Stretched != null;
            float period = Mathf.Max(0.2f, Plugin.ReactorPulseSeconds.Value) / (1f + 0.5f * (entry.Level - 1));
            float depth = flare ? 0.1f : 0.5f;
            float k = 1f + depth * (1f - Mathf.Cos(Time.time * Mathf.PI * 2f / period));
            entry.Ball.localScale = Vector3.one * k;
            if (entry.Stretched == null) return;
            float r = entry.Radius * k;
            float stretch = entry.Reach / (2f * r);
            entry.Stretched.localPosition = Vector3.forward * (r + entry.Reach * 0.5f);
            entry.Stretched.localScale = new Vector3(entry.Across * k, entry.Across * k, stretch * k);
        }

        static readonly (string label, string key, Kind kind)[] Candidates =
        {
            // the ball
            ("FX_ReactorAura (vfx)",        "48f5b20bbe3112a4dacc77192d9657d9", Kind.Vfx),
            ("FX_FireCloud_01 (vfx)",       "62748f0a0c9b5d146a059cda041f367e", Kind.Vfx),
            ("FX_Radiation_Aura",           "5c71cb977ec6f1842a7a57b68673c687", Kind.Prefab),
            ("FX_VolumetricFog_Reactor",    "f056fa443f848d44aa7dae5428bcb0b0", Kind.Prefab),
            ("M_SunCard (quad)",            "5e6dfabfcd4a5d047bb5d4e84a567c09", Kind.MaterialQuad),
            ("M_FX_Glow_Orange_01 (sphere)","7ab26a4d94b32814d889e6e6eaef1a15", Kind.MaterialSphere),
            ("M_FX_Flare_01 (quad)",        "a27d24c29e83ac14eb01865343b6d951", Kind.MaterialQuad),
            ("M_FX_Fire_10 (sphere)",       "7a96b113eb60fe14ebc5258319d890b9", Kind.MaterialSphere),
            // the flare
            ("FX_Leak_Fire_01",             "55bfe8f76f9cb184eac184dbda23c227", Kind.Prefab),
            ("FX_Pipe_Ignition",            "9d56c877197eae24cbcff5f6c74115ee", Kind.Prefab),
            ("FX_Trail_Fire_01",            "42bbec3ebf0338d48aca3a7cc6ba1354", Kind.Prefab),
            ("FX_ThrusterIgnition (vfx)",   "2c85d77c5e3b2be4aa8371fed9cb9925", Kind.Vfx),
            ("FX_Fire_Pipes (vfx)",         "d250c5869f68ba04f8fd13eab21ceadf", Kind.Vfx),
            ("FX_Fire_01",                  "71965b2f0487c9947903596d99098ae9", Kind.Prefab),
            // meltdown
            ("FX_ElectricArc_ReactorCore",  "70a38169d1e5fdc48acd0d17d0e2a9e5", Kind.Prefab),
            ("FX_Electricity_Reactor_01",   "8ed948e8943221944a7e5cee2133b633", Kind.Prefab),
            ("PRF_Arc_Reactor",             "284e69cf468544449b35737df7c49c20", Kind.Prefab),
            ("FX_Explosion_ReactorCore",    "c620190dd56b9b64badf8501cd8939ef", Kind.Prefab),
            // mixes: prefab|vfx graph, both on the same spot
            ("14 + 13 (the reactor ball)",  "71965b2f0487c9947903596d99098ae9|d250c5869f68ba04f8fd13eab21ceadf", Kind.PrefabPlusVfx),
            // the leak fire with one of its two lengths shut: which one is the blue jet?
            ("9 with Length = 0",           "55bfe8f76f9cb184eac184dbda23c227|Length=0|Length 1=4", Kind.PrefabSet),
        };

        static readonly List<Entry> sEntries = new List<Entry>();
        static readonly List<Vector3> sSpots = new List<Vector3>();
        static GUIStyle? sStyle;

        internal static void Reset() { Clear(); sShown = -1; }

        static void Clear()
        {
            foreach (var entry in sEntries) if (entry.Shown != null) Object.Destroy(entry.Shown);
            sEntries.Clear();
            sSpots.Clear();
            if (sMarker != null) sMarker.SetActive(false);
            sMeasureTries = 0;
        }

        static int sShown = -1;                 // index of the effect on show, -1 for none
        static GameObject? sMarker;             // small white dot at the spot the effect was spawned at
        static float sMeasureFrom;              // when to measure the effect, once it has drawn a little
        static int sMeasureTries;

        // A white dot at the spot, and the effect's own axes as rods from it: red +X, green +Y, blue +Z (1.5 m). The
        // effects spawn unturned, so a flame running along the blue rod runs along its own +Z.
        static void ShowMarker(Vector3 spot)
        {
            if (sMarker == null)
            {
                sMarker = Piece(PrimitiveType.Sphere, Color.white, null);
                sMarker.name = "TheExpanse_GalleryMarker";
                sMarker.transform.localScale = Vector3.one * 0.12f;
                Rod(Vector3.right, Color.red);
                Rod(Vector3.up, Color.green);
                Rod(Vector3.forward, Color.blue);
            }
            sMarker.transform.SetPositionAndRotation(spot, Quaternion.identity);
            sMarker.SetActive(true);
            var view = Camera.main != null ? Camera.main.transform : null;
            if (view != null)
                Plugin.Log.LogInfo($"[Test] gallery view: looking along {view.forward:0.00}, screen up {view.up:0.00}, screen right {view.right:0.00} (world; the effect spawns unturned).");
        }

        static void Rod(Vector3 axis, Color color)
        {
            var rod = Piece(PrimitiveType.Cylinder, color, sMarker!.transform);
            // the marker is scaled 0.12, so local units are 1/0.12 m; a cylinder is 2 long along its Y
            const float Length = 1.5f / 0.12f, Thick = 0.04f / 0.12f;
            rod.transform.localRotation = Quaternion.FromToRotation(Vector3.up, axis);
            rod.transform.localPosition = axis * (Length * 0.5f);
            rod.transform.localScale = new Vector3(Thick, Length * 0.5f, Thick);
        }

        static GameObject Piece(PrimitiveType type, Color color, Transform? parent)
        {
            var piece = GameObject.CreatePrimitive(type);
            Object.Destroy(piece.GetComponent<Collider>());
            if (parent != null) piece.transform.SetParent(parent, false);
            var renderer = piece.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = FxShape.GlowMaterial(color, 4f);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return piece;
        }

        // Once the effect has drawn for a moment: log its shape, then move it so what it draws is centered on the dot.
        static void Measure()
        {
            if (sEntries.Count == 0 || sMeasureTries <= 0 || Time.time < sMeasureFrom) return;
            var entry = sEntries[0];
            if (entry.Shown == null) return;
            bool measured = FxShape.TryMeasure(entry.Shown, out var shape);
            if (!measured && --sMeasureTries > 0) { sMeasureFrom = Time.time + 0.25f; return; }
            sMeasureTries = 0;
            Plugin.Log.LogInfo($"[Test] gallery {entry.Label} shape: {FxShape.Describe(entry.Shown, measured, shape)}");
            if (!measured) { entry.Status += "\nits box is a placeholder: left where it spawns"; return; }
            var shift = entry.Shown.transform.TransformVector(shape.Local.center);
            entry.Shown.transform.position -= shift;
            entry.Status += $"\ncentered on the dot: moved {shift.magnitude:0.##} m; size {shape.Local.size.x:0.#} x {shape.Local.size.y:0.#} x {shape.Local.size.z:0.#} m";
        }

        // One effect at a time, at its real size, 10 m in front of the view. Forward past the last clears; back from
        // nothing shown goes to the last.
        internal static void Tick()
        {
            if (!Plugin.TestTools.Value) return;
            Measure();
            Pulse();
            LogMaterials();
            int step = Plugin.FxGalleryKey.Value.IsDown() ? 1 : Plugin.FxGalleryBackKey.Value.IsDown() ? -1 : 0;
            if (step == 0) return;
            Clear();
            int next = sShown + step;
            if (sShown < 0 && step < 0) next = Candidates.Length - 1;
            if (next < 0 || next >= Candidates.Length)
            {
                sShown = -1;
                Plugin.Log.LogInfo("[Test] effect gallery cleared.");
                return;
            }
            var camera = Camera.main;
            if (camera == null) return;
            sShown = next;
            var view = camera.transform;
            var spot = view.position + view.forward * 10f;
            var (label, key, kind) = Candidates[sShown];
            var entry = new Entry { Label = $"{sShown + 1} of {Candidates.Length}. {label}", Key = key, Kind = kind };
            sEntries.Add(entry);
            sSpots.Add(spot);
            ShowMarker(spot);
            sShownAt = Time.time;
            sMeasureFrom = Time.time + 1f;
            sMeasureTries = 8;
            Load(entry, spot, view.rotation);
        }

        static void Load(Entry entry, Vector3 spot, Quaternion facing)
        {
            try
            {
                switch (entry.Kind)
                {
                    case Kind.Prefab:
                        Addressables.LoadAssetAsync<GameObject>(entry.Key).Completed += h =>
                        {
                            if (!Ok(entry, h)) return;
                            entry.Shown = Object.Instantiate(h.Result, spot, Quaternion.identity);
                            bool sphere = FxShape.GiveSphere(entry.Shown, 3f);
                            Start(entry);
                            if (sphere) entry.Status += ", spawning on a 3 m ball";
                        };
                        break;
                    case Kind.PrefabSet:
                    {
                        var parts = entry.Key.Split('|');
                        Addressables.LoadAssetAsync<GameObject>(parts[0]).Completed += h =>
                        {
                            if (!Ok(entry, h)) return;
                            entry.Shown = Object.Instantiate(h.Result, spot, Quaternion.identity);
                            foreach (var effect in entry.Shown.GetComponentsInChildren<VisualEffect>(true))
                                for (int i = 1; i < parts.Length; i++)
                                {
                                    var pair = parts[i].Split('=');
                                    if (pair.Length == 2 && effect.HasFloat(pair[0]) && float.TryParse(pair[1], out float value))
                                        effect.SetFloat(pair[0], value);
                                }
                            Start(entry);
                            entry.Status += "; set " + string.Join(", ", parts, 1, parts.Length - 1);
                        };
                        break;
                    }
                    case Kind.PrefabPlusVfx:
                    case Kind.BallAndPlume:
                    {
                        var keys = entry.Key.Split('|');
                        Addressables.LoadAssetAsync<GameObject>(keys[0]).Completed += h =>
                        {
                            if (!Ok(entry, h)) return;
                            var prefab = h.Result;
                            Addressables.LoadAssetAsync<VisualEffectAsset>(keys[1]).Completed += g =>
                            {
                                if (!Ok(entry, g)) return;
                                entry.Shown = new GameObject("Gallery_" + entry.Label);
                                entry.Shown.transform.position = spot;
                                var ballHolder = new GameObject("Ball").transform;
                                ballHolder.SetParent(entry.Shown.transform, false);
                                entry.Ball = ballHolder;
                                var ball = Object.Instantiate(prefab, ballHolder, false);
                                float size = Mathf.Max(0.1f, Plugin.ReactorBallSize.Value);     // as in the reactor
                                FxShape.GiveSphere(ball, size);
                                var layer = new GameObject("Layer");
                                layer.transform.SetParent(ballHolder, false);
                                layer.AddComponent<VisualEffect>().visualEffectAsset = g.Result;
                                FxShape.GiveSphere(layer, FxShape.CoreGlowPoints, sizes: false);
                                float scale = FxShape.CoreGlowScalePerMeter * size;
                                layer.transform.localScale = Vector3.one * scale;
                                float reach = 0f;
                                int level = keys.Length > 2 && int.TryParse(keys[2], out int l) ? Mathf.Clamp(l, 1, 3) : 1;
                                entry.Level = level;
                                if (entry.Kind == Kind.BallAndPlume)
                                {
                                    // as the reactor draws it: a second ball stretched along +Z, its trailing edge on the
                                    // ball's surface and its front the level's reach past it
                                    float radius = size * 0.5f;
                                    reach = level <= 1 ? 1f : level == 2 ? 2f : 3.5f;
                                    float stretch = reach / (2f * radius);
                                    float across = 0.35f + 0.05f * (level - 1);
                                    var holder = new GameObject("Stretched");
                                    holder.transform.SetParent(entry.Shown.transform, false);
                                    holder.transform.localPosition = Vector3.forward * (radius + reach * 0.5f);
                                    holder.transform.localScale = new Vector3(across, across, stretch);
                                    var copy = Object.Instantiate(prefab, holder.transform, false);
                                    FxShape.GiveSphere(copy, size);
                                    var glow = new GameObject("Glow");
                                    glow.transform.SetParent(holder.transform, false);
                                    glow.AddComponent<VisualEffect>().visualEffectAsset = g.Result;
                                    FxShape.GiveSphere(glow, FxShape.CoreGlowPoints, sizes: false);
                                    glow.transform.localScale = Vector3.one * scale;
                                    entry.Stretched = holder.transform;
                                    entry.Radius = radius; entry.Reach = reach; entry.Across = across;
                                }
                                Start(entry);
                                sMeasureTries = 0;          // already centered by hand
                                entry.Status += $", 14 on a {size:0.#} m ball (BallSize), 13 packed and scaled {scale:0.#}x" +
                                                (entry.Kind == Kind.BallAndPlume ? $", stretched copy reaching {reach:0.#} m past the ball along blue" : "");
                            };
                        };
                        break;
                    }
                    case Kind.Vfx:
                        Addressables.LoadAssetAsync<VisualEffectAsset>(entry.Key).Completed += h =>
                        {
                            if (!Ok(entry, h)) return;
                            entry.Shown = new GameObject("Gallery_" + entry.Label);
                            entry.Shown.transform.position = spot;
                            var effect = entry.Shown.AddComponent<VisualEffect>();
                            effect.visualEffectAsset = h.Result;
                            Start(entry);
                        };
                        break;
                    default:
                        Addressables.LoadAssetAsync<Material>(entry.Key).Completed += h =>
                        {
                            if (!Ok(entry, h)) return;
                            entry.Shown = GameObject.CreatePrimitive(entry.Kind == Kind.MaterialQuad ? PrimitiveType.Quad : PrimitiveType.Sphere);
                            Object.Destroy(entry.Shown.GetComponent<Collider>());
                            entry.Shown.transform.SetPositionAndRotation(spot, facing);
                            entry.Shown.transform.localScale = Vector3.one * 2.5f;
                            entry.Shown.GetComponent<MeshRenderer>().sharedMaterial = h.Result;
                            entry.Status = "shown (" + (h.Result.shader != null ? h.Result.shader.name : "no shader") + ")";
                        };
                        break;
                }
            }
            catch (System.Exception ex) { entry.Status = "failed: " + ex.Message; }
        }

        static bool Ok<T>(Entry entry, AsyncOperationHandle<T> handle) where T : Object
        {
            if (sEntries.Contains(entry) && handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null) return true;
            entry.Status = "could not load";
            Plugin.Log.LogInfo($"[Test] gallery {entry.Label}: could not load.");
            return false;
        }

        // Effects made to be fired by the game wait for a start event; send the usual ones and play everything.
        static void Start(Entry entry)
        {
            if (entry.Shown == null) return;
            entry.Shown.SetActive(true);
            int effects = 0, particles = 0;
            foreach (var effect in entry.Shown.GetComponentsInChildren<VisualEffect>(true))
            {
                effect.enabled = true;
                effect.Reinit();
                effect.Play();
                effect.SendEvent("Start");
                effect.SendEvent("OnPlay");
                effects++;
            }
            foreach (var system in entry.Shown.GetComponentsInChildren<ParticleSystem>(true)) { system.Play(true); particles++; }
            entry.Status = $"shown ({effects} vfx, {particles} particle system(s))";
            Plugin.Log.LogInfo($"[Test] gallery {entry.Label}: {entry.Status}.");
        }

        internal static void Draw()
        {
            if (sEntries.Count == 0) return;
            var camera = Camera.main;
            if (camera == null) return;
            sStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter };
            for (int i = 0; i < sEntries.Count; i++)
            {
                // Fixed near the top of the screen, so a large effect cannot hide it.
                var area = new Rect(Screen.width * 0.5f - 400f, 100f, 800f, 96f);
                string text = sEntries[i].Label + "\n" + sEntries[i].Status;
                sStyle.normal.textColor = Color.black;
                GUI.Label(new Rect(area.x + 1, area.y + 1, area.width, area.height), text, sStyle);
                sStyle.normal.textColor = Color.white;
                GUI.Label(area, text, sStyle);
            }
        }
    }
}
