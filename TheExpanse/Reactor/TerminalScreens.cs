using System.Collections.Generic;
using System.Linq;
using BBI.Unity.Game;
using UnityEngine;

namespace TheExpanse
{
    // The shutdown screen on two of the nav terminals near the reactor (the engineering deck): a picture the mod draws
    // itself, redrawn once a second, on a panel laid flush on the terminal's front face (measured from its mesh). No words but the four ship directions, so nothing
    // needs translating. What it shows depends on the shutdown mode:
    //   Balance:   the ring of injectors seen from the engineering deck, each opposite pair in its own color, pulled ones gray;
    //   Memorize:  the order, one injector lit per second, then a pause, over and over; a mistake shows a red cross;
    //   Resonance: an oscilloscope: the core's beat as a wave, the injector owed riding on it, the others off it.
    internal static class TerminalScreens
    {
        static int W = 128, H = 208;      // set to the measured face's shape
        sealed class Screen
        {
            internal Transform Terminal = null!;
            internal GameObject? Overlay;
            internal GameObject? Bezel;
            internal Texture2D? Texture;
            internal Material? Material;
            internal float Here = float.NaN;    // the terminal's side of the ring on the diagram, radians
            internal Vector3 FaceCenter, FaceNormal, FaceUp;
            internal Vector2 FaceSize;
        }

        static IEnumerable<Vector3> Corners(Bounds b)
        {
            for (int c = 0; c < 8; c++)
                yield return new Vector3((c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z);
        }

        static Vector3 ParseVector(string? text, Vector3 fallback)
        {
            var parts = (text ?? "").Split(',');
            if (parts.Length < 2) return fallback;
            float Get(int k, float d) => k < parts.Length && float.TryParse(parts[k].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : d;
            return new Vector3(Get(0, fallback.x), Get(1, fallback.y), Get(2, fallback.z));
        }

        static readonly List<Screen> sScreens = new List<Screen>();
        static Material? sBezelMaterial;
        static float sHere = float.NaN;         // the screen being drawn: its side of the ring
        static Color32[] sPixels = new Color32[256 * 416];
        const int K = 2;                        // physical pixels per layout pixel: the picture is drawn at twice the layout's size
        static float sNextDraw;
        static int sFrame;
        static float sNextSecond;
        static Vector3 sDorsal, sStarboard;     // world directions for the diagram's up and right
        static readonly Vector3[] sInjectorDir = new Vector3[8];

        internal static bool Available => sScreens.Count > 0;

        internal static void Destroy()
        {
            foreach (var s in sScreens)
            {
                if (s.Overlay != null) Object.Destroy(s.Overlay);
                if (s.Bezel != null) Object.Destroy(s.Bezel);
                if (s.Material != null) Object.Destroy(s.Material);
                if (s.Texture != null) Object.Destroy(s.Texture);
            }
            sScreens.Clear();
        }

        // At setup: the nav terminals near the reactor, the two to use, and the diagram's directions.
        internal static void Find(StructurePart core, Vector3 center, Vector3 down)
        {
            Destroy();
            // The terminals: the game keeps the ship's parts by their own names (the editor's groups are gone at run time),
            // so the nav terminals' upper parts (SM_Prop_Terminal_Nav_Upper, where the big screen is) near the core. Of the
            // four on the engineering deck, the two farthest apart (the pair facing each other across it), or the ones
            // numbered in Terminals (their numbers in the log line below).
            var root = core.transform.root;
            var uppers = ReactorControl.Parts
                .Where(p => p.name.StartsWith("SM_Prop_Terminal_Nav_Upper"))
                .Select(p => (t: p.transform, d: Vector3.Distance(p.transform.position, center), up: -Vector3.Dot(p.transform.position - center, down)))
                .Where(x => x.d < 40f).OrderBy(x => x.d).ToList();
            Plugin.Log.LogInfo("[Reactor] nav terminals near the core: " + (uppers.Count == 0 ? "none." :
                string.Join("; ", uppers.Select((x, k) => $"{k}: {x.d:0.0} m away, {x.up:0.0} m above the core"))));
            var chosen = new List<Transform>();
            var numbers = (Plugin.ReactorTerminals.Value ?? "").Split(',').Select(x => int.TryParse(x.Trim(), out int n) ? n : -1).Where(n => n >= 0).ToList();
            if (numbers.Count > 0) chosen.AddRange(numbers.Where(n => n < uppers.Count).Select(n => uppers[n].t));
            else
            {
                // Any two of the four nearest, at random: the ship's own pick.
                var four = uppers.Take(4).Select(x => x.t).ToList();
                var random = new System.Random(ReactorControl.SeedFor(core) ^ 0x5eed);
                for (int k = four.Count - 1; k > 0; k--) { int r = random.Next(k + 1); (four[k], four[r]) = (four[r], four[k]); }
                chosen.AddRange(four.Take(2));
            }
            if (uppers.Count == 0)
            {
                var named = ReactorControl.Parts
                    .Select(p => (p.name, d: Vector3.Distance(p.transform.position, center), up: -Vector3.Dot(p.transform.position - center, down)))
                    .Where(x => x.up > 1f && x.d < 25f).OrderBy(x => x.d).Select(x => x.name).Distinct().Take(50);
                Plugin.Log.LogInfo("[Reactor] parts above the core (for finding the terminals): " + string.Join(", ", named));
            }
            // The diagram's directions: dorsal is one of the ship's own axes (ScreenDorsal), squared to the ring.
            var nose = -down.normalized;
            var dorsal = root.TransformDirection(AxisOf(Plugin.ReactorScreenDorsal.Value));
            sDorsal = Vector3.ProjectOnPlane(dorsal, nose).normalized;
            if (sDorsal.sqrMagnitude < 0.5f) sDorsal = Vector3.ProjectOnPlane(root.up, nose).normalized;
            sStarboard = Vector3.Cross(sDorsal, nose).normalized;
            foreach (var t in chosen) Attach(t, center, down);
        }

        static Vector3 AxisOf(string axis)
        {
            axis = (axis ?? "+Z").Trim().ToUpperInvariant();
            float sign = axis.StartsWith("-") ? -1f : 1f;
            char c = axis.Length > 0 ? axis[axis.Length - 1] : 'Z';
            return sign * (c == 'X' ? Vector3.right : c == 'Y' ? Vector3.up : Vector3.forward);
        }

        internal static void SetInjector(int i, Vector3 worldDirection) { if (i >= 0 && i < 8) sInjectorDir[i] = worldDirection; }

        static void Attach(Transform terminal, Vector3 center, Vector3 down)
        {
            var screen = new Screen { Terminal = terminal };
            // You are here: the terminal's side of the ring, on the diagram.
            var side = Vector3.ProjectOnPlane(terminal.position - center, down);
            screen.Here = side.sqrMagnitude > 0.01f ? Mathf.Atan2(Vector3.Dot(side, sDorsal), -Vector3.Dot(side, sStarboard)) : float.NaN;
            var all = terminal.GetComponentsInChildren<Renderer>(true);
            string described = string.Join(", ", all.Select(r => $"{r.name}[{string.Join("|", r.sharedMaterials.Select(m => m != null ? m.name : "-"))}]"));
            string how = Measure(terminal, screen);
            if (sScreens.Count == 0) Layout(screen.FaceSize.x / Mathf.Max(0.01f, screen.FaceSize.y));
            // each screen its own picture (each marks its own side)
            screen.Texture = new Texture2D(W * K, H * K, TextureFormat.RGBA32, false) { name = "TheExpanse_ReactorScreen", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            screen.Material = FxShape.GlowMaterial(Color.white, 0f);
            screen.Material.name = "TheExpanse_ReactorScreen";
            if (screen.Material.HasProperty("_UnlitColorMap")) screen.Material.SetTexture("_UnlitColorMap", screen.Texture);
            if (screen.Material.HasProperty("_BaseColorMap")) screen.Material.SetTexture("_BaseColorMap", screen.Texture);
            Brighten(screen.Material);
            // The terminal's screens are painted into its one texture, so a panel of ours goes over its face.
            var panel = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.Destroy(panel.GetComponent<Collider>());
            panel.name = "TheExpanse_ReactorScreenPanel";
            panel.transform.SetParent(terminal, false);
            panel.GetComponent<Renderer>().sharedMaterial = screen.Material;
            screen.Overlay = panel;
            screen.Bezel = new GameObject("TheExpanse_ReactorScreenBezel");
            screen.Bezel.transform.SetParent(terminal, false);
            sBezelMaterial ??= FxShape.GlowMaterial(new Color(0.06f, 0.065f, 0.07f), 0f);
            for (int k = 0; k < 4; k++)
            {
                var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.Destroy(bar.GetComponent<Collider>());
                bar.name = "Bar";
                bar.transform.SetParent(screen.Bezel.transform, false);
                bar.GetComponent<Renderer>().sharedMaterial = sBezelMaterial;
            }
            Place(screen);
            sScreens.Add(screen);
            Plugin.Log.LogInfo($"[Reactor] shutdown screen on '{terminal.name}': {how}; picture {W * K} x {H * K}. Its renderers: {described}.");
        }

        // The terminal's front face, from its own mesh: of the triangles facing its front (its -X side), the largest flat
        // area (grouped by facing and depth); the screen covers that face's extent, flush on it, tilt and all.
        static string Measure(Transform terminal, Screen screen)
        {
            var groups = new Dictionary<(int, int, int, int), (float Area, Vector3 Normal, List<Vector3> Points)>();
            int read = 0;
            foreach (var filter in terminal.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null || !mesh.isReadable || filter.name.StartsWith("TheExpanse_")) continue;
                var v = mesh.vertices; var tri = mesh.triangles;
                for (int k = 0; k + 2 < tri.Length; k += 3)
                {
                    var a = terminal.InverseTransformPoint(filter.transform.TransformPoint(v[tri[k]]));
                    var b = terminal.InverseTransformPoint(filter.transform.TransformPoint(v[tri[k + 1]]));
                    var c = terminal.InverseTransformPoint(filter.transform.TransformPoint(v[tri[k + 2]]));
                    var cross = Vector3.Cross(b - a, c - a);
                    float area = cross.magnitude * 0.5f;
                    if (area < 1e-6f) continue;
                    read++;
                    var n = cross.normalized;
                    if (n.x > 0f) n = -n;              // either winding: the face is whichever looks toward -X
                    if (n.x > -0.5f) continue;
                    var key = (Mathf.RoundToInt(n.x * 20f), Mathf.RoundToInt(n.y * 20f), Mathf.RoundToInt(n.z * 20f), Mathf.RoundToInt(Vector3.Dot(n, a) * 100f));
                    if (!groups.TryGetValue(key, out var g)) g = (0f, Vector3.zero, new List<Vector3>());
                    g.Points.Add(a); g.Points.Add(b); g.Points.Add(c);
                    groups[key] = (g.Area + area, g.Normal + n * area, g.Points);
                }
            }
            if (groups.Count == 0)
            {
                screen.FaceCenter = new Vector3(-0.26f, 0f, 0.35f); screen.FaceNormal = Vector3.left; screen.FaceUp = Vector3.up; screen.FaceSize = new Vector2(0.42f, 0.68f);
                return $"its mesh could not be read ({read} triangle(s)), so a guess";
            }
            var best = groups.Values.OrderByDescending(g => g.Area).First();
            var normal = best.Normal.normalized;                      // out of the face
            var up = Vector3.ProjectOnPlane(Vector3.up, normal).normalized;
            if (up.sqrMagnitude < 0.5f) up = Vector3.ProjectOnPlane(Vector3.forward, normal).normalized;
            var right = Vector3.Cross(normal, up).normalized;
            float minR = float.MaxValue, maxR = float.MinValue, minU = float.MaxValue, maxU = float.MinValue, depth = 0f;
            foreach (var p in best.Points)
            {
                float r = Vector3.Dot(p, right), u = Vector3.Dot(p, up);
                minR = Mathf.Min(minR, r); maxR = Mathf.Max(maxR, r); minU = Mathf.Min(minU, u); maxU = Mathf.Max(maxU, u);
                depth += Vector3.Dot(p, normal);
            }
            depth /= best.Points.Count;
            screen.FaceNormal = normal; screen.FaceUp = up;
            screen.FaceSize = new Vector2(maxR - minR, maxU - minU);
            screen.FaceCenter = right * ((minR + maxR) * 0.5f) + up * ((minU + maxU) * 0.5f) + normal * (depth + 0.004f);
            float tilt = Vector3.Angle(normal, Vector3.left);
            return $"its front face measured from its mesh: {screen.FaceSize.x:0.00} x {screen.FaceSize.y:0.00} m, tilted {tilt:0} degrees, {best.Area:0.00} m2, of {groups.Count} flat area(s)";
        }

        // The picture's shape from the face's (width / height): 176 pixels high, as wide as the face is.
        static void Layout(float aspect)
        {
            H = 176;
            W = Mathf.Clamp(Mathf.RoundToInt(H * aspect), 176, 440);
            sPixels = new Color32[W * K * H * K];
        }

        // The panel's pose on its terminal: on the measured face, unless the debug settings say otherwise (live).
        static void Place(Screen screen)
        {
            if (screen.Overlay == null) return;
            var t = screen.Overlay.transform;
            t.localPosition = ParseVector(Plugin.ReactorScreenPosition.Value, screen.FaceCenter);
            // a quad shows its -Z side: point -Z out of the face
            var rotation = Quaternion.LookRotation(-screen.FaceNormal, screen.FaceUp);
            if ((Plugin.ReactorScreenRotation.Value ?? "").Trim().Length > 0)
                rotation = Quaternion.Euler(ParseVector(Plugin.ReactorScreenRotation.Value, Vector3.zero)) * Quaternion.Euler(0f, 180f, 0f);
            t.localRotation = rotation;
            var size = ParseVector(Plugin.ReactorScreenSize.Value + ",1", new Vector3(screen.FaceSize.x, screen.FaceSize.y, 1f));
            // the bezel takes the face's edge; the picture fills the inside
            const float rim = 0.03f, proud = 0.014f;
            t.localScale = new Vector3(Mathf.Max(0.05f, size.x - 2f * rim), Mathf.Max(0.05f, size.y - 2f * rim), 1f);
            if (screen.Bezel != null && screen.Bezel.transform.childCount == 4)
            {
                var b = screen.Bezel.transform;
                b.localPosition = t.localPosition;
                b.localRotation = rotation;
                for (int k = 0; k < 4; k++)
                {
                    var bar = b.GetChild(k);
                    bool sideways = k < 2;
                    float sign = k % 2 == 0 ? -1f : 1f;
                    bar.localScale = sideways ? new Vector3(rim, size.y, proud) : new Vector3(size.x, rim, proud);
                    // the panel shows its -Z side, so "out of the face" is -Z here
                    bar.localPosition = sideways ? new Vector3(sign * (size.x - rim) * 0.5f, 0f, -proud * 0.5f + 0.002f)
                                                 : new Vector3(0f, sign * (size.y - rim) * 0.5f, -proud * 0.5f + 0.002f);
                }
            }
        }

        static void Brighten(Material? material)
        {
            if (material == null) return;
            float k = Mathf.Max(0f, Plugin.ReactorScreenBrightness.Value);
            if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", Color.white * k);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white * k);
            if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", Color.black);
        }

        // ---- drawing, once a second ------------------------------------------------------------------------------------
        internal struct State
        {
            internal ReactorControl.Mode Mode;
            internal bool[] Connected;
            internal IReadOnlyList<int> Order;
            internal int Owed, Mistake, Level;
            internal float MistakeAge, NextStep, StepSeconds, Beats;     // NextStep: seconds to the next level, -1 if none
            internal bool Live;
        }

        static readonly Color32 Back = new Color32(3, 10, 14, 255), Grid = new Color32(8, 22, 28, 255), Dim = new Color32(30, 70, 80, 255);
        static readonly Color32 Ink = new Color32(110, 170, 180, 255), Bright = new Color32(190, 240, 245, 255), Gray = new Color32(50, 55, 60, 255), Missing = new Color32(10, 20, 24, 255);
        static readonly Color32[] LevelColors =
        {
            new Color32(140, 190, 255, 255), new Color32(255, 140, 40, 255), new Color32(255, 40, 20, 255), new Color32(190, 50, 255, 255),
        };
        static Color32 PairColor(int i) => (i % 4) switch
        {
            0 => new Color32(40, 220, 255, 255),
            1 => new Color32(255, 70, 220, 255),
            2 => new Color32(255, 220, 40, 255),
            _ => new Color32(80, 255, 90, 255),
        };

        // Areas (pixels, origin bottom left): the title bar on top, the level strip at the bottom, the ring on the left,
        // the mode's own panel on the right.
        static int TitleY => H - 20;
        static int StripY => 16;
        const int LabelGap = 13, LabelW = 23, LabelH = 7;        // the four direction labels: gap from the dots, size
        static int RingCX => RingRad + 7 + LabelGap + LabelW + 8;
        static int RingCY => (StripY + TitleY) / 2 + 2;
        static int RingRad => Mathf.Min(Mathf.RoundToInt(W * 0.115f), (TitleY - StripY) / 2 - 7 - LabelGap - LabelH - 4);
        static int PanelX => Mathf.Max(Mathf.RoundToInt(W * 0.52f), RingCX + RingRad + 7 + LabelGap + LabelW + 10);

        internal static void Tick(State state)
        {
            if (sScreens.Count == 0 || Time.unscaledTime < sNextDraw) return;
            // once a second; Resonance 12 times a second, its dots pulsing with the beat
            sNextDraw = Time.unscaledTime + (state.Mode == ReactorControl.Mode.Resonance && state.Live ? 1f / 12f : 1f);
            if (Time.unscaledTime >= sNextSecond) { sNextSecond = Time.unscaledTime + 1f; sFrame++; }
            foreach (var screen in sScreens)
            {
                if (screen.Texture == null) continue;
                Brighten(screen.Material);
                Place(screen);
                sHere = screen.Here;
                Draw(state);
                screen.Texture.SetPixels32(sPixels);
                screen.Texture.Apply(false);
            }
        }

        static void Draw(State state)
        {
            Clear(Back);
            // a faint grid, scan lines and a thin inner edge
            for (int x = 0; x < W; x += 12) VLine(x, 0, H - 1, Grid);
            for (int y = 0; y < H; y += 12) HLine(0, W - 1, y, Grid);
            Rect(1, 1, W - 2, H - 2, Dim);
            Corners(3, 3, W - 4, H - 4, Ink);
            // title
            const string title = "FUSION TORCH DRIVE";
            Text(title, 8, TitleY + 4, Bright, TextWidth(title, 3) + 8 + 6 <= LampsLeft ? 3 : 2);
            HLine(6, W - 7, TitleY, Dim);
            DrawLevelBadge(state);
            if (!state.Live) { Text("OFFLINE", RingCX - 31, RingCY - 5, Dim); return; }
            DrawStrip(state);
            switch (state.Mode)
            {
                case ReactorControl.Mode.Balance: DrawBalance(state); break;
                case ReactorControl.Mode.Memorize: DrawMemorize(state); break;
                case ReactorControl.Mode.Resonance: DrawResonance(state); break;
            }
        }

        // Top right: the four levels as squares, the current one lit in its color.
        static int LampsLeft => W - 12 - 2 * 13 - 8;

        static void DrawLevelBadge(State state)
        {
            // Three alert lamps, one per level (orange, red, purple), lit up to the current level with a glow; all dark
            // at level 0.
            for (int k = 1; k <= 3; k++)
            {
                int x = W - 12 - (3 - k) * 13, y = TitleY + 9;
                bool lit = state.Live && state.Level >= k;
                var c = LevelColors[k];
                if (lit)
                {
                    Disc(x, y, 7, new Color32((byte)(c.r / 5), (byte)(c.g / 5), (byte)(c.b / 5), 255));
                    Disc(x, y, 6, new Color32((byte)(c.r / 2), (byte)(c.g / 2), (byte)(c.b / 2), 255));
                    Disc(x, y, 4, c);
                    Disc(x - 1, y + 1, 1, new Color32(255, 245, 230, 255));
                }
                else { Disc(x, y, 4, Back); Circle(x, y, 4, Dim); }
            }
        }

        // Bottom: the time left to the next level as a wide bar, in the next level's color, shrinking to nothing.
        static void DrawStrip(State state)
        {
            HLine(6, W - 7, StripY + 14, Dim);
            int t0 = 8, t1 = W - 10;
            Rect(t0, 4, t1, 14, Dim);
            if (state.NextStep >= 0f && state.StepSeconds > 0f)
            {
                float left = Mathf.Clamp01(state.NextStep / state.StepSeconds);
                var c = LevelColors[Mathf.Min(3, state.Level + 1)];
                int end = t0 + 2 + Mathf.RoundToInt((t1 - t0 - 4) * left);
                FillRect(t0 + 2, 6, end, 12, c);
                HLine(t0 + 2, end, 11, new Color32((byte)Mathf.Min(255, c.r + 60), (byte)Mathf.Min(255, c.g + 60), (byte)Mathf.Min(255, c.b + 60), 255));
            }
        }

        // The ring as seen from the engineering deck looking aft, dorsal up, starboard left: tick marks, crosshair, the four directions, and
        // a dot per injector (color(i)).
        static (int, int) DotAt(int i, int r)
        {
            var d = sInjectorDir[i];
            // seen from the engineering deck, looking aft: dorsal up, starboard on the left
            float x = -Vector3.Dot(d, sStarboard), y = Vector3.Dot(d, sDorsal), len = Mathf.Sqrt(x * x + y * y);
            if (len < 1e-4f) return (RingCX, RingCY);
            return (RingCX + Mathf.RoundToInt(x / len * r), RingCY + Mathf.RoundToInt(y / len * r));
        }

        static void DrawRing(State state, System.Func<int, Color32> color)
        {
            int r = RingRad;
            Circle(RingCX, RingCY, r, Dim);
            Circle(RingCX, RingCY, r + 9, Grid);
            for (int a = 0; a < 360; a += 15)
            {
                float rad = a * Mathf.Deg2Rad; int len = a % 45 == 0 ? 5 : 2;
                Line(RingCX + Mathf.RoundToInt(Mathf.Cos(rad) * (r + 9)), RingCY + Mathf.RoundToInt(Mathf.Sin(rad) * (r + 9)),
                     RingCX + Mathf.RoundToInt(Mathf.Cos(rad) * (r + 9 + len)), RingCY + Mathf.RoundToInt(Mathf.Sin(rad) * (r + 9 + len)), Dim);
            }
            HLine(RingCX - 4, RingCX + 4, RingCY, Dim); VLine(RingCX, RingCY - 4, RingCY + 4, Dim);
            Circle(RingCX, RingCY, 3, Ink);
            for (int i = 0; i < 8; i++)
            {
                var (x, y) = DotAt(i, r);
                Disc(x, y, 7, Back);
                if (state.Connected[i]) Disc(x, y, 6, color(i));
                else { Disc(x, y, 6, Missing); Circle(x, y, 6, new Color32(225, 235, 240, 255)); }
                if (state.Mistake == i && state.MistakeAge < 6f) Cross(x, y, 9, new Color32(255, 30, 30, 255));
            }
            // You are here: an upright map pin on this terminal's side of the ring, its point down on the spot (further
            // out below the ring, so its head stays clear of the dots).
            if (!float.IsNaN(sHere))
            {
                float ca = Mathf.Cos(sHere), sa = Mathf.Sin(sHere);
                float reach = r + 9f + 9f * Mathf.Max(0f, -sa) - 2f * Mathf.Max(0f, sa);
                int tx = RingCX + Mathf.RoundToInt(ca * reach), ty = RingCY + Mathf.RoundToInt(sa * reach);
                const float lift = 7f, rad = 3.5f;
                int hy = ty + Mathf.RoundToInt(lift);
                var white = new Color32(255, 255, 255, 255);
                Triangle(tx, ty, tx - Mathf.RoundToInt(rad * 0.9f), hy, tx + Mathf.RoundToInt(rad * 0.9f), hy, white);
                DiscF(tx, hy, rad, white);
                DiscF(tx, hy, rad * 0.4f, Back);
            }
            int edge = r + 7 + LabelGap;                          // the same gap all round
            Text("DRSL", RingCX - LabelW / 2, RingCY + edge, Ink, 2);
            Text("VNTR", RingCX - LabelW / 2, RingCY - edge - LabelH, Ink, 2);
            Text("STBD", RingCX - edge - LabelW, RingCY - LabelH / 2, Ink, 2);
            Text("PORT", RingCX + edge, RingCY - LabelH / 2, Ink, 2);
        }

        // Balance: each opposite pair in its own color, joined by a thin line; pulled ones gray. On the right, the four
        // pairs as a legend, each with its two lamps.
        static void DrawBalance(State state)
        {
            int r = RingRad;
            for (int i = 0; i < 4; i++)
            {
                var (ax, ay) = DotAt(i, r - 7); var (bx, by) = DotAt(i + 4, r - 7);
                Line(ax, ay, bx, by, Dim2(PairColor(i), state.Connected[i] || state.Connected[i + 4]));
            }
            DrawRing(state, i => state.Connected[i] ? PairColor(i) : Gray);
            for (int i = 0; i < 4; i++)
            {
                int y = TitleY - 22 - i * 24, x = PanelX;
                FillRect(x, y - 1, x + 12, y + 1, Dim2(PairColor(i), true));
                Lamp(x + 20, y, state.Connected[i], PairColor(i));
                HLine(x + 26, x + 32, y, Dim);
                Lamp(x + 38, y, state.Connected[i + 4], PairColor(i));
                bool done = !state.Connected[i] && !state.Connected[i + 4];
                if (done) Text("OK", x + 45, y - 3, Ink, 2);
            }
        }

        // A pair's lamp in the legend: its color while the injector is in, near-black with a white ring once it is out.
        static void Lamp(int x, int y, bool connected, Color32 c)
        {
            if (connected) Disc(x, y, 5, c);
            else { Disc(x, y, 5, Missing); Circle(x, y, 5, new Color32(225, 235, 240, 255)); }
        }

        static Color32 Dim2(Color32 c, bool on) => on ? new Color32((byte)(c.r / 2), (byte)(c.g / 2), (byte)(c.b / 2), 255) : Gray;

        // Memorize: the order, one injector lit per second (the owed one first), then two dark seconds, over and over;
        // on the right the order's progress as a row of lamps, and the step being shown, large.
        static void DrawMemorize(State state)
        {
            int n = state.Order.Count, cycle = n + 2, step = n > 0 ? sFrame % cycle : 0;
            int shown = n > 0 && step < n ? state.Order[step] : -1;
            DrawRing(state, i => i == shown ? new Color32(255, 255, 255, 255) : new Color32(30, 80, 90, 255));
            if (shown >= 0) { var (x, y) = DotAt(shown, RingRad); Circle(x, y, 10, Bright); }
            DrawPath(state, step, n);
        }

        // Memorize, on the right: the order drawn as a path. A small ring of the eight positions; as each injector is
        // shown, an arrow is drawn to it from the one before, the newest brightest; the start is ringed. The path stays
        // through the pause, so a glance at the end of the cycle shows the whole order; it starts again with the cycle.
        static void DrawPath(State state, int step, int n)
        {
            int cx = (PanelX + W - 10) / 2, cy = RingCY;
            int r = Mathf.Min((W - 10 - PanelX) / 2 - 8, (TitleY - StripY) / 2 - 14);
            if (r < 12 || n == 0) return;
            (int, int) At(int i) { float a = AngleOf(i); return (cx + Mathf.RoundToInt(Mathf.Cos(a) * r), cy + Mathf.RoundToInt(Mathf.Sin(a) * r)); }
            Circle(cx, cy, r, Grid);
            for (int i = 0; i < 8; i++)
            {
                var (x, y) = At(i);
                if (state.Connected[i]) Disc(x, y, 3, new Color32(30, 80, 90, 255));
                else Circle(x, y, 3, new Color32(225, 235, 240, 255));
            }
            int shownUpTo = Mathf.Min(step, n - 1);                      // in the pause: the whole order
            var (sx, sy) = At(state.Order[0]);
            Circle(sx, sy, 6, Bright);
            for (int k = 1; k <= shownUpTo; k++)
            {
                var (ax, ay) = At(state.Order[k - 1]);
                var (bx, by) = At(state.Order[k]);
                float age = shownUpTo == 0 ? 1f : (float)k / shownUpTo;
                byte v = (byte)(80 + 175 * age);
                var c = new Color32(v, v, (byte)Mathf.Min(255, v + 10), 255);
                Arrow(ax, ay, bx, by, c, 5);
            }
            if (step < n) { var (x, y) = At(state.Order[step]); Disc(x, y, 4, new Color32(255, 255, 255, 255)); }
        }

        // A line from a to b with an arrowhead short of b (so it does not cover b's dot).
        static void Arrow(int ax, int ay, int bx, int by, Color32 c, int stop)
        {
            float dx = bx - ax, dy = by - ay, len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len < 2f) return;
            float ux = dx / len, uy = dy / len;
            int tx = bx - Mathf.RoundToInt(ux * stop), ty = by - Mathf.RoundToInt(uy * stop);
            Line(ax + Mathf.RoundToInt(ux * stop), ay + Mathf.RoundToInt(uy * stop), tx, ty, c);
            for (int side = -1; side <= 1; side += 2)
            {
                float wx = -ux * 0.87f + side * -uy * 0.5f, wy = -uy * 0.87f + side * ux * 0.5f;
                Line(tx, ty, tx + Mathf.RoundToInt(wx * 5f), ty + Mathf.RoundToInt(wy * 5f), c);
            }
        }

        // An injector's angle on the diagrams (radians; 0 to the right, counterclockwise), as DotAt places it.
        static float AngleOf(int i)
        {
            var d = sInjectorDir[i];
            return Mathf.Atan2(Vector3.Dot(d, sDorsal), -Vector3.Dot(d, sStarboard));
        }

        // Resonance: the core's beat as a bright wave and the others' waves off it, scrolling a step a second; the ring
        // marks the injector to release (with ResonanceHint).
        static void DrawResonance(State state)
        {
            // Every injector still feeding pulses in its own rhythm, all the same color; the center pulses with the
            // core's beat, and the one owed pulses exactly with it. (With ResonanceHint, it is also ringed.)
            var teal = new Color32(60, 200, 215, 255);
            float Glow(float offset) { float f = Mathf.Repeat(state.Beats + offset - 0.5f, 1f); float d = Mathf.Min(f, 1f - f); return Mathf.Clamp01(1f - d * 4f); }
            Color32 Shade(float g) => new Color32((byte)(20 + (teal.r - 20) * g), (byte)(45 + (teal.g - 45) * g), (byte)(50 + (teal.b - 50) * g), 255);
            DrawRing(state, i => Shade(Glow(i == state.Owed ? 0f : ReactorControl.ResonanceOffsetOf(i))));
            if (Plugin.ReactorResonanceHint.Value && state.Owed >= 0) { var (x, y) = DotAt(state.Owed, RingRad); Circle(x, y, 9, Bright); }
            float core = Glow(0f);
            Disc(RingCX, RingCY, 5, Shade(core));
            Circle(RingCX, RingCY, 8, core > 0.5f ? teal : Dim);
            int x0 = PanelX, x1 = W - 10, mid = RingCY, amp = Mathf.Min(26, (TitleY - StripY) / 4);
            Rect(x0 - 4, mid - amp - 10, x1 + 2, mid + amp + 10, Dim);
            HLine(x0, x1, mid, Grid);
            for (int x = x0; x < x1; x += 16) VLine(x, mid - amp - 8, mid + amp + 8, Grid);
            float shift = -state.Beats * 0.5f;                         // scrolls with the beat
            int shade = 0;
            for (int i = 0; i < 8; i++)
            {
                if (!state.Connected[i] || i == state.Owed) continue;
                byte g = (byte)(55 + (shade++ * 9) % 36);
                Wave(x0, x1, mid, amp, shift + ReactorControl.ResonanceOffsetOf(i), new Color32(g, (byte)(g + 10), (byte)(g + 18), 255), 1);
            }
            Wave(x0, x1, mid, amp, shift, new Color32(255, 220, 40, 255), 2);
        }

        static void Wave(int x0, int x1, int mid, int amp, float phase, Color32 color, int thick)
        {
            int lastY = int.MinValue;
            for (int px = x0 * K; px < x1 * K; px++)
            {
                float x = px / (float)K;
                int py = Mathf.RoundToInt((mid + amp * Mathf.Sin(((x - x0) / 32f + phase) * Mathf.PI * 2f)) * K) + K / 2;
                int from = lastY == int.MinValue ? py : Mathf.Min(lastY, py), to = lastY == int.MinValue ? py : Mathf.Max(lastY, py);
                for (int y = from; y <= to; y++) for (int t = -(thick - 1); t <= thick - 1; t++) P(px, y + t, color);
                lastY = py;
            }
        }

        // ---- pixels: layout coordinates (W x H) in, drawn at K times that -------------------------------------------
        static void P(int x, int y, Color32 c) { if (x >= 0 && y >= 0 && x < W * K && y < H * K) sPixels[y * W * K + x] = c; }
        static int Px(int x) => x * K + K / 2;
        static void Clear(Color32 c) { for (int i = 0; i < sPixels.Length; i++) sPixels[i] = c; }
        static void Disc(int cx, int cy, int r, Color32 c)
        {
            int x0 = Px(cx), y0 = Px(cy), pr = r * K;
            float lim = (pr + 0.5f) * (pr + 0.5f);
            for (int y = -pr - 1; y <= pr + 1; y++) for (int x = -pr - 1; x <= pr + 1; x++) if (x * x + y * y <= lim) P(x0 + x, y0 + y, c);
        }
        static void Circle(int cx, int cy, int r, Color32 c)
        {
            int x0 = Px(cx), y0 = Px(cy); float pr = r * K;
            int n = Mathf.Max(32, Mathf.RoundToInt(pr * 7f));
            for (int a = 0; a < n; a++) { float t = a * Mathf.PI * 2f / n; P(x0 + Mathf.RoundToInt(pr * Mathf.Cos(t)), y0 + Mathf.RoundToInt(pr * Mathf.Sin(t)), c); }
        }
        static void HLine(int x0, int x1, int y, Color32 c) { int py = Px(y); for (int x = x0 * K; x <= x1 * K + K - 1; x++) P(x, py, c); }
        static void VLine(int x, int y0, int y1, Color32 c) { int px = Px(x); for (int y = y0 * K; y <= y1 * K + K - 1; y++) P(px, y, c); }
        static void Rect(int x0, int y0, int x1, int y1, Color32 c) { HLine(x0, x1, y0, c); HLine(x0, x1, y1, c); VLine(x0, y0, y1, c); VLine(x1, y0, y1, c); }
        static void FillRect(int x0, int y0, int x1, int y1, Color32 c) { for (int y = y0 * K; y <= y1 * K + K - 1; y++) for (int x = x0 * K; x <= x1 * K + K - 1; x++) P(x, y, c); }
        static void Corners(int x0, int y0, int x1, int y1, Color32 c)
        {
            const int k = 8;
            HLine(x0, x0 + k, y0, c); VLine(x0, y0, y0 + k, c); HLine(x1 - k, x1, y0, c); VLine(x1, y0, y0 + k, c);
            HLine(x0, x0 + k, y1, c); VLine(x0, y1 - k, y1, c); HLine(x1 - k, x1, y1, c); VLine(x1, y1 - k, y1, c);
        }
        static void Line(int x0, int y0, int x1, int y1, Color32 c)
        {
            int ax = Px(x0), ay = Px(y0), bx = Px(x1), by = Px(y1);
            int dx = Mathf.Abs(bx - ax), dy = -Mathf.Abs(by - ay), sx = ax < bx ? 1 : -1, sy = ay < by ? 1 : -1, err = dx + dy;
            while (true)
            {
                P(ax, ay, c);
                if (ax == bx && ay == by) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; ax += sx; }
                if (e2 <= dx) { err += dx; ay += sy; }
            }
        }
        // A disc with a fractional radius (layout units), at full resolution.
        static void DiscF(int cx, int cy, float r, Color32 c)
        {
            int x0 = Px(cx), y0 = Px(cy); float pr = r * K; int n = Mathf.CeilToInt(pr) + 1;
            for (int y = -n; y <= n; y++) for (int x = -n; x <= n; x++) if (x * x + y * y <= pr * pr) P(x0 + x, y0 + y, c);
        }

        // A filled triangle (layout coordinates), at full resolution.
        static void Triangle(int ax, int ay, int bx, int by, int cx, int cy, Color32 c)
        {
            float x0 = Px(ax), y0 = Px(ay), x1 = Px(bx), y1 = Px(by), x2 = Px(cx), y2 = Px(cy);
            int minX = Mathf.FloorToInt(Mathf.Min(x0, Mathf.Min(x1, x2))), maxX = Mathf.CeilToInt(Mathf.Max(x0, Mathf.Max(x1, x2)));
            int minY = Mathf.FloorToInt(Mathf.Min(y0, Mathf.Min(y1, y2))), maxY = Mathf.CeilToInt(Mathf.Max(y0, Mathf.Max(y1, y2)));
            float Edge(float ax_, float ay_, float bx_, float by_, float x, float y) => (bx_ - ax_) * (y - ay_) - (by_ - ay_) * (x - ax_);
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    float e0 = Edge(x0, y0, x1, y1, x, y), e1 = Edge(x1, y1, x2, y2, x, y), e2 = Edge(x2, y2, x0, y0, x, y);
                    if ((e0 >= 0 && e1 >= 0 && e2 >= 0) || (e0 <= 0 && e1 <= 0 && e2 <= 0)) P(x, y, c);
                }
        }

        static void Cross(int cx, int cy, int r, Color32 c)
        {
            int a = Px(cx), b = Px(cy);
            for (int t = -1; t <= 1; t++)
                for (int k = -r * K; k <= r * K; k++) { P(a + k + t, b + k, c); P(a + k + t, b - k, c); }
        }

        // A 5x7 terminal font, 3 physical pixels a dot (about 10 layout pixels high, 9 wide a letter); (x, y) is the
        // bottom left, in layout pixels.
        static readonly Dictionary<char, string> sGlyphs = new Dictionary<char, string>
        {
            ['A'] = "01110100011000111111100011000110001", ['B'] = "11110100011000111110100011000111110",
            ['C'] = "01110100011000010000100001000101110", ['D'] = "11110100011000110001100011000111110",
            ['E'] = "11111100001000011110100001000011111", ['F'] = "11111100001000011110100001000010000",
            ['G'] = "01110100011000010111100011000101111", ['H'] = "10001100011000111111100011000110001",
            ['I'] = "01110001000010000100001000010001110", ['K'] = "10001100101010011000101001001010001",
            ['L'] = "10000100001000010000100001000011111", ['N'] = "10001110011010110011100011000110001",
            ['O'] = "01110100011000110001100011000101110", ['P'] = "11110100011000111110100001000010000",
            ['R'] = "11110100011000111110101001001010001", ['S'] = "01111100001000001110000010000111110",
            ['T'] = "11111001000010000100001000010000100", ['U'] = "10001100011000110001100011000101110",
            ['V'] = "10001100011000110001100010101000100", ['Y'] = "10001100010101000100001000010000100",
        };
        static int TextWidth(string text, int dot) => (text.Length * 6 - 1) * dot / K;

        static void Text(string text, int x, int y, Color32 c, int dot = 3)
        {
            int px = x * K, py = y * K;
            foreach (char ch in text)
            {
                if (sGlyphs.TryGetValue(ch, out var g))
                    for (int row = 0; row < 7; row++)
                        for (int col = 0; col < 5; col++)
                            if (g[row * 5 + col] == '1')
                                for (int dy = 0; dy < dot; dy++) for (int dx = 0; dx < dot; dx++) P(px + col * dot + dx, py + (6 - row) * dot + dy, c);
                px += 6 * dot;
            }
        }
    }
}
