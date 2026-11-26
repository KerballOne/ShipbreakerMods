using System.Collections.Generic;
using BBI.Unity.Game;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace TheExpanse
{
    // The Corvette's drive plume, drawn by the mod (none of the game's effects are blue enough, and their colors are
    // baked into their graphs). Always on for now; the flight mechanics come later.
    //   beam:   one quad along the drive axis, turned about that axis to face each camera, showing a texture the mod
    //           paints itself a couple of dozen times a second: a white-hot core with shock diamonds inside a blue sheath
    //           that billows downstream and fades out;
    //   mouth:  a camera-facing glow at the nozzle exit and a thin horizontal streak through it;
    //   light:  a blue point light just past the mouth, lighting the bell and the hull.
    // Where it burns: [Drive] PlumeAt. "MasterJack" (the test spot) puts it 15 m above the bay's Master Jack, pointing up;
    // "Bell" puts it at the mouth of the ship's engine bell. The bell and nozzle are looked for and logged either way.
    internal static class DrivePlume
    {
        const string NozzlePrefix = "EngineNozzle";
        const float TestHeight = 15f;
        const float TestMouthDiameter = 5f;
        const int TexAcross = 32, TexAlong = 160;     // painted every frame: kept small (48 x 192 took 2.2 ms)
        const float PaintInterval = 0f;             // every frame: at drive speeds the billows jump otherwise
        const float BillowStretch = 4f;             // billows this many times longer along the flow than across

        static GameObject? sRoot, sBeam, sGlow, sStreak;
        static Material? sBeamMaterial, sGlowMaterial, sStreakMaterial;
        static Texture2D? sBeamTexture, sGlowTexture, sStreakTexture;
        static Color[]? sPixels;
        static HDAdditionalLightData? sLight;
        static bool sHooked, sMaterialLogged;
        static float sNextLook, sNextPaint, sNextNozzleLook;
        static bool sNoNozzleLogged;
        static int sNozzleTries;
        static float sMeasuredDiameter;         // the bell's (or nozzle's) mouth, once found; the test spot uses it too

        // Where the plume is: the mouth's center and the direction the exhaust goes, both in the anchor's space.
        static Transform? sAnchor;
        static Vector3 sLocalMouth, sLocalAxis;
        static float sMouthDiameter;
        static string sAnchoredTo = "";
        static bool sNozzleLogged;

        // More drives from setup: a copy of the plume at each one past the first (same look, own size and place).
        sealed class Extra { internal GameObject Root = null!; internal Transform? Beam, Glow, Streak, Light; internal Vector3 Axis = Vector3.up; }
        static readonly List<Extra> sExtras = new List<Extra>();

        static Vector3 Mouth => sAnchor != null ? sAnchor.TransformPoint(sLocalMouth) : Vector3.zero;
        static Vector3 Axis => sAnchor != null ? sAnchor.TransformDirection(sLocalAxis).normalized : Vector3.up;
        // 0..1. For now a fixed test value from the settings; the flight controls will drive it later.
        // At the controls: the pilot's throttle. Otherwise the test value.
        static float Throttle => FlightModel.UnderWay ? Mathf.Clamp(FlightModel.Throttle, 0.01f, 1f) : Mathf.Clamp(Plugin.PlumeTestThrottle.Value, 0.01f, 1f);
        static bool Burning => FlightModel.UnderWay ? FlightModel.Throttle > 0.001f : Plugin.DrivePlume.Value;
        static bool AtNozzle => string.Equals(Plugin.PlumeAt.Value.Trim(), "Bell", System.StringComparison.OrdinalIgnoreCase);

        internal static void Reset()
        {
            if (sRoot != null) Object.Destroy(sRoot);
            foreach (var x in sExtras) if (x.Root != null) Object.Destroy(x.Root);
            sExtras.Clear();
            foreach (var o in new Object?[] { sBeamMaterial, sGlowMaterial, sStreakMaterial, sBeamTexture, sGlowTexture, sStreakTexture })
                if (o != null) Object.Destroy(o);
            sRoot = sBeam = sGlow = sStreak = null;
            sBeamMaterial = sGlowMaterial = sStreakMaterial = null;
            sBeamTexture = sGlowTexture = sStreakTexture = null;
            sLight = null;
            sAnchor = null;
            sAnchoredTo = "";
            sNozzleLogged = sNoNozzleLogged = false;
            sNextLook = sNextNozzleLook = 0f;
            sNozzleTries = 0;
            sMeasuredDiameter = 0f;
        }

        // Every frame (LateUpdate).
        internal static void Frame()
        {
            if (!Plugin.Enabled.Value || !(Plugin.DrivePlume.Value || FlightModel.UnderWay)) { if (sRoot != null) Reset(); return; }

            // lost the anchor (cut off, level change) or the setting changed: look again, once a second
            // the ship may finish spawning after the bay has loaded: keep looking for the nozzle (every 2 s) until found
            if (!sNozzleLogged && sNozzleTries < 30 && Time.unscaledTime >= sNextNozzleLook && !ShipSetup.TryDrive(out _, out _))
            {
                sNextNozzleLook = Time.unscaledTime + 2f;
                if (++sNozzleTries == 30) Plugin.Log.LogInfo("[Drive] stopped looking for the engine nozzle (none after a minute).");
                FindNozzle(out _, out _, out _, out _, log: true);
            }

            bool wantNozzle = AtNozzle;
            if (sAnchor == null || (sAnchoredTo == "nozzle") != wantNozzle)
            {
                if (sRoot != null) { sRoot.SetActive(false); }
                foreach (var x in sExtras) if (x.Root != null) x.Root.SetActive(false);
                sAnchor = null;
                if (Time.unscaledTime < sNextLook) return;
                sNextLook = Time.unscaledTime + 1f;
                if (!Anchor(wantNozzle)) return;
            }

            if (sRoot == null) Build();
            if (sRoot == null) return;
            sRoot.SetActive(Burning);
            if (!Burning) { foreach (var x in sExtras) if (x.Root != null) x.Root.SetActive(false); return; }
            Show();
        }

        // ---- where -----------------------------------------------------------------------------------------------

        static bool Anchor(bool nozzle)
        {
            if (nozzle && ShipSetup.TryDrive(out var mount, out var mountDiameter))
            {
                sAnchor = mount;
                sLocalMouth = Vector3.zero;
                sLocalAxis = Vector3.forward;
                sMouthDiameter = mountDiameter;
                sAnchoredTo = "nozzle";
                Plugin.Log.LogInfo($"[Drive] plume at the drive set up for '{ShipSetup.Model}': {mountDiameter:0.0} m across.");
                return true;
            }
            if (nozzle)
            {
                if (!FindNozzle(out var reference, out var mouth, out var axis, out var diameter, log: false)) return false;
                sAnchor = reference;
                sLocalMouth = reference.InverseTransformPoint(mouth);
                sLocalAxis = reference.InverseTransformDirection(axis);
                sMouthDiameter = diameter;
                sAnchoredTo = "nozzle";
                Plugin.Log.LogInfo($"[Drive] plume at the engine bell: mouth {mouth}, pointing {axis}, {diameter:0.0} m across " +
                                   $"(following part '{reference.name}').");
                return true;
            }
            var jack = FindMasterJack();
            if (jack == null) return false;
            sAnchor = jack;
            sLocalMouth = jack.InverseTransformPoint(jack.position + Vector3.up * TestHeight);
            sLocalAxis = jack.InverseTransformDirection(Vector3.up);
            sMouthDiameter = sMeasuredDiameter > 0f ? sMeasuredDiameter : TestMouthDiameter;
            sAnchoredTo = "jack";
            Plugin.Log.LogInfo($"[Drive] plume at the test spot: {TestHeight:0} m above '{jack.name}' at {jack.position}, pointing up.");
            return true;
        }

        // The way the exhaust goes (aft), from the engine bell or nozzle. False if neither is on the ship.
        internal static bool TryDriveAxis(out Vector3 axis)
        {
            if (ShipSetup.TryDrive(out var mount, out _)) { axis = mount.forward; return true; }
            bool found = FindNozzle(out _, out _, out axis, out _, log: false);
            return found && axis.sqrMagnitude > 0.5f;
        }

        static Transform? FindMasterJack()
        {
            var named = GameObject.Find("PRF_MasterJack_NoSpawner");
            if (named != null) return named.transform;
            foreach (var t in SceneLookup.Roots())
                if (t.name.StartsWith("PRF_MasterJack", System.StringComparison.Ordinal)) return t;
            return null;
        }

        // Measures a ring of parts (the bell or the nozzle): the StructureParts with a matching name. Its axis comes from how their centers lie: in a
        // ring (staves around the bell) the axis is the ring's normal; along a line (hoops stacked) it is the line. It
        // points away from the rest of the ship. The mouth is the far end along the axis; its size is how far the
        // nozzle's mesh reaches from the axis there.
        static bool IsBell(string name) => name.IndexOf("Engine_Bell", System.StringComparison.OrdinalIgnoreCase) >= 0
                                           && name.IndexOf("ExtPanel", System.StringComparison.OrdinalIgnoreCase) < 0;
        static bool IsNozzle(string name) => name.StartsWith(NozzlePrefix, System.StringComparison.OrdinalIgnoreCase);

        // The exhaust leaves through the engine bell (1_Engine_Bell_segNN, its outer panels excluded); the EngineNozzle
        // parts stand in if the bell is gone. With log on, both are measured and logged.
        static bool FindNozzle(out Transform reference, out Vector3 mouth, out Vector3 axis, out float diameter, bool log)
        {
            bool bell = Measure("engine bell", IsBell, out reference, out mouth, out axis, out diameter, log);
            if (log)
            {
                bool nozzle = Measure("engine nozzle", IsNozzle, out var r, out var m, out var a, out var d, log);
                if (!bell && nozzle) { reference = r; mouth = m; axis = a; diameter = d; }
                if (bell || nozzle)
                {
                    sNozzleLogged = true;
                    sMeasuredDiameter = diameter;
                    if (sAnchoredTo == "jack") sMouthDiameter = diameter;
                }
                else if (PartSeparation.CustomShip && !sNoNozzleLogged)
                {
                    sNoNozzleLogged = true;
                    Plugin.Log.LogInfo("[Drive] no Engine_Bell or EngineNozzle parts (yet); still looking.");
                }
                return bell || nozzle;
            }
            return bell || Measure("engine nozzle", IsNozzle, out reference, out mouth, out axis, out diameter, false);
        }

        static bool Measure(string label, System.Func<string, bool> match, out Transform reference, out Vector3 mouth, out Vector3 axis,
                            out float diameter, bool log)
        {
            reference = null!; mouth = axis = Vector3.zero; diameter = 0f;
            var parts = new List<StructurePart>();
            Vector3 shipSum = Vector3.zero; int shipCount = 0;
            // the name may be on the part itself or on an object inside it (in game: EngineNozzle_00_Wall)
            var nozzleParts = new HashSet<StructurePart>();
            foreach (var t in Object.FindObjectsOfType<Transform>())
                if (t != null && match(t.name))
                {
                    var owner = t.GetComponentInParent<StructurePart>();
                    if (owner != null && nozzleParts.Add(owner)) parts.Add(owner);
                }
            foreach (var part in Object.FindObjectsOfType<StructurePart>())
            {
                if (part == null || nozzleParts.Contains(part)) continue;
                if (part.name.IndexOf("Barge", System.StringComparison.OrdinalIgnoreCase) < 0) { shipSum += part.transform.position; shipCount++; }
            }
            if (parts.Count == 0) return false;

            // each part's middle: its renderers' bounds
            var centers = new List<Vector3>();
            var renderers = new List<Renderer>();
            foreach (var part in parts)
            {
                bool any = false; var b = new Bounds();
                foreach (var r in part.GetComponentsInChildren<Renderer>(false))
                {
                    if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                    renderers.Add(r);
                }
                centers.Add(any ? b.center : part.transform.position);
            }
            var center = Vector3.zero;
            foreach (var c in centers) center += c;
            center /= centers.Count;

            if (centers.Count >= 3)
            {
                var (values, vectors) = Eigen(centers, center);      // largest first
                bool line = values[0] - values[1] > values[1] - values[2];
                axis = line ? vectors[0] : vectors[2];
                if (log) Plugin.Log.LogInfo($"[Drive] nozzle part centers spread {values[0]:0.00} / {values[1]:0.00} / {values[2]:0.00}: " +
                                            (line ? "the widest spread stands apart, axis = that direction." : "in a ring, axis = its normal."));
            }
            else axis = parts[0].transform.forward;
            var shipCenter = shipCount > 0 ? shipSum / shipCount : center - axis;
            if (Vector3.Dot(center - shipCenter, axis) < 0f) axis = -axis;

            // the far end, and the widest the mesh gets within half a meter of it
            float far = float.MinValue;
            var points = new List<Vector3>();
            foreach (var r in renderers)
            {
                var filter = r.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh != null && mesh.isReadable)
                {
                    var m = r.transform.localToWorldMatrix;
                    foreach (var v in mesh.vertices) points.Add(m.MultiplyPoint3x4(v));
                }
                else
                {
                    var b = r.bounds;
                    for (int c = 0; c < 8; c++)
                        points.Add(new Vector3((c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z));
                }
            }
            foreach (var p in points) far = Mathf.Max(far, Vector3.Dot(p - center, axis));
            float radius = 0f;
            foreach (var p in points)
            {
                var d = p - center;
                float along = Vector3.Dot(d, axis);
                if (along < far - 0.5f) continue;
                radius = Mathf.Max(radius, (d - axis * along).magnitude);
            }
            mouth = center + axis * far;
            diameter = Mathf.Max(0.5f, 2f * radius);

            // follow the part nearest the mouth (the plume goes with it if the nozzle is cut loose)
            float best = float.MaxValue;
            for (int i = 0; i < parts.Count; i++)
            {
                float d = (centers[i] - mouth).sqrMagnitude;
                if (d < best) { best = d; reference = parts[i].transform; }
            }

            if (log)
            {
                Plugin.Log.LogInfo($"[Drive] {label}: {parts.Count} part(s) ({string.Join(", ", parts.ConvertAll(p => p.name))}); " +
                                   $"center {center}, ship center {shipCenter}, axis {axis}, mouth {mouth}, {diameter:0.00} m across " +
                                   $"({points.Count} points, {(renderers.Count > 0 && renderers[0].GetComponent<MeshFilter>()?.sharedMesh?.isReadable == true ? "mesh vertices" : "renderer boxes")}).");
            }
            return reference != null;
        }

        // Principal axes of a set of points (Jacobi on the 3x3 covariance), largest spread first.
        static (float[] values, Vector3[] vectors) Eigen(List<Vector3> points, Vector3 mean)
        {
            var a = new double[3, 3];
            foreach (var p in points)
            {
                var d = p - mean;
                double[] v = { d.x, d.y, d.z };
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) a[i, j] += v[i] * v[j] / points.Count;
            }
            var e = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (int sweep = 0; sweep < 32; sweep++)
            {
                for (int p = 0; p < 2; p++)
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (System.Math.Abs(a[p, q]) < 1e-12) continue;
                        double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        double t = System.Math.Sign(theta) / (System.Math.Abs(theta) + System.Math.Sqrt(theta * theta + 1));
                        if (theta == 0) t = 1;
                        double c = 1 / System.Math.Sqrt(t * t + 1), s = t * c;
                        for (int k = 0; k < 3; k++)
                        {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = c * akp - s * akq; a[k, q] = s * akp + c * akq;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = c * apk - s * aqk; a[q, k] = s * apk + c * aqk;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double ekp = e[k, p], ekq = e[k, q];
                            e[k, p] = c * ekp - s * ekq; e[k, q] = s * ekp + c * ekq;
                        }
                    }
            }
            var order = new List<int> { 0, 1, 2 };
            order.Sort((x, y) => a[y, y].CompareTo(a[x, x]));
            var values = new float[3]; var vectors = new Vector3[3];
            for (int i = 0; i < 3; i++)
            {
                int k = order[i];
                values[i] = (float)a[k, k];
                vectors[i] = new Vector3((float)e[0, k], (float)e[1, k], (float)e[2, k]).normalized;
            }
            return (values, vectors);
        }

        // ---- what ------------------------------------------------------------------------------------------------

        static void Build()
        {
            sRoot = new GameObject("TheExpanse_DrivePlume");
            sBeamTexture = new Texture2D(TexAcross, TexAlong, TextureFormat.RGBAHalf, false) { name = "TheExpanse_PlumeBeam", wrapMode = TextureWrapMode.Clamp };
            sPixels = new Color[TexAcross * TexAlong];
            sGlowTexture = RadialTexture();
            sStreakTexture = StreakTexture();
            sBeamMaterial = AdditiveMaterial(sBeamTexture);
            sGlowMaterial = AdditiveMaterial(sGlowTexture);
            sStreakMaterial = AdditiveMaterial(sStreakTexture);
            // beam quad: x across (-0.5..0.5), z along (0..1); glow quads: x, y in -0.5..0.5, facing -z
            sBeam = Quad("Beam", sBeamMaterial, beam: true);
            sGlow = Quad("Glow", sGlowMaterial, beam: false);
            sStreak = Quad("Streak", sStreakMaterial, beam: false);
            var lightObject = new GameObject("Light");
            lightObject.transform.SetParent(sRoot.transform, false);
            sLight = lightObject.AddHDLight(HDLightTypeAndShape.Point);
            sLight.SetRange(40f);
            sNextPaint = 0f;
            if (!sHooked) { RenderPipelineManager.beginCameraRendering += FaceCamera; sHooked = true; }
        }

        static GameObject Quad(string name, Material material, bool beam)
        {
            var go = new GameObject(name);
            go.transform.SetParent(sRoot!.transform, false);
            var mesh = new Mesh { name = "TheExpanse_Plume" + name };
            mesh.vertices = beam
                ? new[] { new Vector3(-0.5f, 0, 0), new Vector3(0.5f, 0, 0), new Vector3(-0.5f, 0, 1), new Vector3(0.5f, 0, 1) }
                : new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0) };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            mesh.RecalculateNormals();
            mesh.bounds = new Bounds(beam ? new Vector3(0, 0, 0.5f) : Vector3.zero, Vector3.one);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            return go;
        }

        // HDRP/Unlit, transparent, additive, both sides, no depth write. Starts from one of the game's own transparent
        // Unlit materials when one is loaded (so the shader variant is surely in the build), else from a plain one.
        internal static Material AdditiveMaterial(Texture texture)
        {
            Material? template = null;
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || m.shader == null || m.shader.name != "HDRP/Unlit" || !m.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT")) continue;
                if (template == null || m.IsKeywordEnabled("_BLENDMODE_ADD")) template = m;
                if (m.IsKeywordEnabled("_BLENDMODE_ADD")) break;
            }
            var material = template != null ? new Material(template) : new Material(PlainUnlit());
            if (!sMaterialLogged)
            {
                sMaterialLogged = true;
                Plugin.Log.LogInfo(template != null
                    ? $"[Drive] plume drawn with a copy of the game's '{template.name}' (keywords: {string.Join(" ", template.shaderKeywords)})."
                    : $"[Drive] no transparent HDRP/Unlit material loaded; plume drawn with a plain {material.shader?.name} set up by hand.");
            }
            material.name = "TheExpanse_PlumeAdditive";
            material.SetFloat("_SurfaceType", 1f);
            material.SetFloat("_BlendMode", 1f);                  // additive
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_BLENDMODE_ALPHA");
            material.DisableKeyword("_BLENDMODE_PRE_MULTIPLY");
            material.EnableKeyword("_BLENDMODE_ADD");
            material.DisableKeyword("_ENABLE_FOG_ON_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.SetFloat("_SrcBlend", (float)BlendMode.One);
            material.SetFloat("_DstBlend", (float)BlendMode.One);
            material.SetFloat("_AlphaSrcBlend", (float)BlendMode.Zero);
            material.SetFloat("_AlphaDstBlend", (float)BlendMode.One);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_TransparentZWrite", 0f);
            material.SetFloat("_ZTestTransparent", (float)CompareFunction.LessEqual);
            material.SetFloat("_CullMode", (float)CullMode.Off);
            material.SetFloat("_CullModeForward", (float)CullMode.Off);
            material.SetFloat("_EnableFogOnTransparent", 0f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)RenderQueue.Transparent;
            material.SetShaderPassEnabled("TransparentDepthPrepass", false);
            material.SetShaderPassEnabled("TransparentDepthPostpass", false);
            material.SetShaderPassEnabled("TransparentBackface", false);
            material.SetShaderPassEnabled("MotionVectors", false);
            material.SetShaderPassEnabled("ShadowCaster", false);
            if (material.HasProperty("_UnlitColorMap")) material.SetTexture("_UnlitColorMap", texture);
            if (material.HasProperty("_UnlitColorMap_ST")) material.SetVector("_UnlitColorMap_ST", new Vector4(1, 1, 0, 0));
            if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", Color.black);
            material.mainTexture = texture;
            return material;
        }

        // Shader.Find cannot see the game's HDRP shaders (not in its always-included list): look among those loaded.
        static Shader PlainUnlit()
        {
            Shader? lit = null;
            foreach (var shader in Resources.FindObjectsOfTypeAll<Shader>())
            {
                if (shader == null) continue;
                if (shader.name == "HDRP/Unlit") return shader;
                if (shader.name == "HDRP/Lit") lit = shader;
            }
            return lit ?? Shader.Find("Hidden/InternalErrorShader");
        }

        internal static void SetBrightness(Material? material, float brightness)
        {
            if (material == null) return;
            var c = new Color(brightness, brightness, brightness, 1f);
            if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", c);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", c);
        }

        static Color PlumeColor() => Plugin.PlumeColor.Value;

        // Soft round glow: a white-hot middle fading out through the plume's color.
        static Texture2D RadialTexture()
        {
            const int N = 64;
            var t = new Texture2D(N, N, TextureFormat.RGBAHalf, false) { name = "TheExpanse_PlumeGlow", wrapMode = TextureWrapMode.Clamp };
            var px = new Color[N * N];
            var tint = PlumeColor();
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                    float r2 = u * u + v * v;
                    float window = Mathf.Clamp01(1f - r2); window *= window;
                    float hot = Mathf.Exp(-r2 * 30f) * 3f, soft = Mathf.Exp(-r2 * 5f);
                    var c = (Color.white * hot + tint * soft) * window;
                    c.a = 1f;
                    px[y * N + x] = c;
                }
            t.SetPixels(px); t.Apply(false, false);
            return t;
        }

        // Thin horizontal streak, like a lens's flare.
        static Texture2D StreakTexture()
        {
            const int W = 128, H = 16;
            var t = new Texture2D(W, H, TextureFormat.RGBAHalf, false) { name = "TheExpanse_PlumeStreak", wrapMode = TextureWrapMode.Clamp };
            var px = new Color[W * H];
            var tint = Color.Lerp(PlumeColor(), Color.white, 0.3f);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W * 2f - 1f, v = (y + 0.5f) / H * 2f - 1f;
                    float window = Mathf.Clamp01(1f - u * u) * Mathf.Clamp01(1f - v * v);
                    float s = Mathf.Exp(-Mathf.Abs(u) * 5f) * Mathf.Exp(-v * v * 12f) * window;
                    var c = tint * s; c.a = 1f;
                    px[y * W + x] = c;
                }
            t.SetPixels(px); t.Apply(false, false);
            return t;
        }

        // The beam's texture, painted again a couple of dozen times a second so the sheath billows downstream.
        // Across (x): -1..1 of the beam's full width (the mouth is 1/Spread of it). Along (y): 0 at the mouth, 1 at the tip.
        static void PaintBeam(float length, float mouth, float spread)
        {
            if (sBeamTexture == null || sPixels == null) return;
            float t = Time.time;
            var tint = PlumeColor();
            float turbulence = Mathf.Clamp01(Plugin.PlumeTurbulence.Value);
            float flow = Plugin.PlumeFlowSpeed.Value * Throttle;
            int diamonds = Mathf.Clamp(Plugin.PlumeShockDiamonds.Value, 0, 12);
            float r0 = 1f / spread;                          // mouth radius, as a share of the half-width
            float halfWidth = mouth * spread * 0.5f;         // m
            float billow = Mathf.Max(0.3f, mouth * 0.6f);    // size of a billow, m
            float fullLength = Mathf.Max(1f, Plugin.PlumeLength.Value);
            float coreDiameter = Mathf.Clamp(Plugin.PlumeCoreDiameter.Value, 0.1f, mouth);
            float diamondGap = Mathf.Max(0.01f, 2.2f * coreDiameter / length);
            float sheathGain = Mathf.Max(0f, Plugin.PlumeSheath.Value);
            float coreGain = Mathf.Max(0f, Plugin.PlumeCore.Value);
            var hot = Color.Lerp(tint, Color.white, 0.85f);

            for (int j = 0; j < TexAlong; j++)
            {
                float v = j / (float)(TexAlong - 1);
                float meters = v * length;
                // The sheath's shape is set along the FULL-throttle plume (`full`, 0 at the bell, 1 at the full plume's tip):
                // it fans out of the bell, rounds over smoothly and tapers to a sharp point. A lower throttle draws only
                // the start of that shape and fades out at its own end (`tip` below), so it fans out and is gone before
                // any taper shows.
                float full = meters / fullLength;
                float fan = 1f - Mathf.Exp(-full * 7f);                                   // out of the bell, easing off
                float taper = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.15f, 1f, full));
                float radius = r0 + (1f - r0) * fan * 0.95f;
                radius = Mathf.Lerp(0.02f, radius, Mathf.Pow(taper, 0.85f));
                // core: its own width in meters (as a share of the half-width), thinning a little downstream
                float coreRadius = coreDiameter * 0.5f / halfWidth * (1f - 0.8f * Mathf.Min(1f, v * 2.5f));
                float sheathFall = Mathf.Exp(-v * 2.2f);
                float coreFall = Mathf.Exp(-v * 6f);            // the white-hot core: short
                float tip = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.6f, 1f, v));
                float diamond = 0f;
                for (int k = 0; k < diamonds; k++)
                {
                    float at = diamondGap * (k + 0.6f);
                    float d = (v - at) / (diamondGap * 0.18f);
                    diamond += Mathf.Exp(-d * d) * Mathf.Exp(-k * 0.35f) * Mathf.Exp(-at * 5f);
                }
                // billows stretched along the flow (they streak at speed); the pattern also churns slowly as it goes
                float ny = (meters - t * flow) / (billow * BillowStretch);
                for (int i = 0; i < TexAcross; i++)
                {
                    float u = (i + 0.5f) / TexAcross * 2f - 1f;
                    float nx = u * halfWidth / billow;
                    float n = 0.65f * Mathf.PerlinNoise(nx + 17.3f, ny) + 0.35f * Mathf.PerlinNoise(nx * 2.3f + 5.1f + t * 0.7f, ny * 2.3f);
                    float edgeWobble = 1f + turbulence * 0.5f * (n - 0.5f) * Mathf.Sqrt(v) * 2f;
                    float x = u / Mathf.Max(0.01f, radius * edgeWobble);
                    // the base plume: filled out to the bell's rim, under the core. A glowing cylinder seen side-on is
                    // brightest down the middle (the longest path through it): the chord, softened at the edge.
                    float chord = Mathf.Sqrt(Mathf.Max(0f, 1f - x * x));
                    float sheath = sheathGain * Mathf.SmoothStep(0f, 1f, chord) * sheathFall
                                 * Mathf.Lerp(1f, 0.25f + 1.5f * n, turbulence * Mathf.Clamp01(v * 4f));
                    float xc = u / Mathf.Max(0.005f, coreRadius);
                    float core = coreGain * Mathf.Exp(-xc * xc * 2f) * (coreFall + diamond * 1.2f);
                    float window = Mathf.Clamp01(1f - u * u); window *= window;
                    // a soft start out of the bell (over its first few tenths of a mouth width), not a hard edge
                    float mouthFade = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(meters / Mathf.Max(0.2f, mouth * 0.35f)));
                    var c = (tint * sheath + hot * core) * (tip * window * mouthFade);
                    c.a = 1f;
                    sPixels[j * TexAcross + i] = c;
                }
            }
            sBeamTexture.SetPixels(sPixels);
            sBeamTexture.Apply(false, false);
        }

        // How long painting the beam takes, summed since last read (for the flight's frame-time line).
        static readonly System.Diagnostics.Stopwatch sPaintWatch = new System.Diagnostics.Stopwatch();
        internal static float PaintMs;
        internal static int PaintCount;

        static void Show()
        {
            // Length and FlowSpeed are full throttle. The beam is always as wide as the full-throttle plume: its shape is
            // painted along the full-throttle plume (PaintBeam), so a lower throttle shows the same flare out of the bell
            // and fades sooner. (Narrowed with the throttle, a low-throttle plume was a straight-sided box.)
            float throttle = Throttle;
            float length = Mathf.Max(1f, Plugin.PlumeLength.Value * throttle);
            float mouth = Plugin.PlumeMouthDiameter.Value > 0f ? Plugin.PlumeMouthDiameter.Value : sMouthDiameter;
            float spread = Mathf.Max(1f, Plugin.PlumeSpread.Value);
            float brightness = Mathf.Max(0f, Plugin.PlumeBrightness.Value);
            float flicker = 1f + 0.06f * (Mathf.PerlinNoise(Time.time * 9f, 2.1f) - 0.5f);

            // only painted while some camera drew the beam last frame (not from the seat or the nose)
            bool seen = sBeam != null && sBeam.GetComponent<MeshRenderer>().isVisible;
            if (seen && Time.unscaledTime >= sNextPaint)
            {
                sNextPaint = Time.unscaledTime + PaintInterval;
                sPaintWatch.Restart();
                PaintBeam(length, mouth, spread);
                sPaintWatch.Stop();
                PaintMs += (float)sPaintWatch.Elapsed.TotalMilliseconds;
                PaintCount++;
            }
            sBeamBrightness = brightness * flicker;
            SetBrightness(sBeamMaterial, sBeamBrightness);
            SetBrightness(sGlowMaterial, brightness * 0.5f * Mathf.Max(0.1f, Plugin.PlumeCore.Value / 4f) * flicker);
            SetBrightness(sStreakMaterial, brightness * 0.35f * Mathf.Max(0f, Plugin.PlumeStreak.Value) * flicker);
            if (sStreak != null) sStreak.SetActive(Plugin.PlumeStreak.Value > 0f);

            // sizes (the turning toward the camera happens just before each camera draws)
            if (sBeam != null) sBeam.transform.localScale = new Vector3(mouth * spread, 1f, length);
            float glow = Mathf.Max(Plugin.PlumeCoreDiameter.Value * 3f, mouth * 0.5f);
            if (sGlow != null) sGlow.transform.localScale = Vector3.one * glow * flicker;
            if (sStreak != null) sStreak.transform.localScale = new Vector3(glow * 6f, glow * 0.2f, 1f);
            var axis = Axis;
            if (sRoot != null) sRoot.transform.position = Mouth;
            if (sLight != null)
            {
                sLight.transform.position = Mouth + axis * (mouth * 0.6f);
                sLight.SetColor(Color.Lerp(PlumeColor(), Color.white, 0.3f));
                sLight.SetIntensity(Mathf.Max(0f, Plugin.PlumeLightLumens.Value) * flicker, LightUnit.Lumen);
                sLight.SetRange(Mathf.Clamp(length, 5f, mouth * 3f));      // the bell and the hull near it; a 150 m reach cost frame rate
            }
            ShowExtras(length, spread, flicker);
        }

        static void ShowExtras(float length, float spread, float flicker)
        {
            var drives = ShipSetup.Drives;
            int need = sAnchoredTo == "nozzle" && drives.Count > 1 && sAnchor == drives[0].mount ? drives.Count - 1 : 0;
            while (sExtras.Count > need) { var last = sExtras[sExtras.Count - 1]; if (last.Root != null) Object.Destroy(last.Root); sExtras.RemoveAt(sExtras.Count - 1); }
            while (sExtras.Count < need && sRoot != null)
            {
                var root = Object.Instantiate(sRoot);
                root.name = "TheExpanse_DrivePlume_" + (sExtras.Count + 2);
                sExtras.Add(new Extra { Root = root, Beam = root.transform.Find("Beam"), Glow = root.transform.Find("Glow"),
                                        Streak = root.transform.Find("Streak"), Light = root.transform.Find("Light") });
                Plugin.Log.LogInfo($"[Drive] plume {sExtras.Count + 1} of {drives.Count} at a drive set up for '{ShipSetup.Model}': {drives[sExtras.Count].diameter:0.0} m across.");
            }
            for (int i = 0; i < sExtras.Count; i++)
            {
                var x = sExtras[i];
                var (mount, diameter) = drives[i + 1];
                if (x.Root == null || mount == null) continue;
                float mouth = Plugin.PlumeMouthDiameter.Value > 0f ? Plugin.PlumeMouthDiameter.Value : diameter;
                x.Root.SetActive(true);
                x.Axis = mount.forward;
                x.Root.transform.position = mount.position;
                if (x.Beam != null) x.Beam.localScale = new Vector3(mouth * spread, 1f, length);
                float glow = Mathf.Max(Plugin.PlumeCoreDiameter.Value * 3f, mouth * 0.5f);
                if (x.Glow != null) x.Glow.localScale = Vector3.one * glow * flicker;
                if (x.Streak != null) { x.Streak.gameObject.SetActive(Plugin.PlumeStreak.Value > 0f); x.Streak.localScale = new Vector3(glow * 6f, glow * 0.2f, 1f); }
                if (x.Light != null) x.Light.position = mount.position + x.Axis * (mouth * 0.6f);
            }
        }

        // Before each camera draws: the beam turns about the drive axis to face it, the glow and streak face it.
        static float sBeamBrightness;

        static void FaceCamera(ScriptableRenderContext context, Camera camera)
        {
            if (sRoot == null || !sRoot.activeInHierarchy || camera == null) return;
            foreach (var x in sExtras)
                if (x.Root != null && x.Root.activeInHierarchy)
                    FaceOne(camera, x.Root.transform.position, x.Axis, x.Beam, x.Glow, x.Streak);
            var mouth = sRoot.transform.position;
            var axis = Axis;
            var toCamera = camera.transform.position - mouth;
            if (sBeam != null)
            {
                // the beam is a flat strip turned toward the camera about the drive's axis: seen nearly along the axis
                // its straight edges showed as sharp lines; it fades out there and the glow carries the plume end-on
                float endOn = toCamera.sqrMagnitude > 1e-6f ? Mathf.Abs(Vector3.Dot(toCamera.normalized, axis)) : 1f;
                SetBrightness(sBeamMaterial, sBeamBrightness * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, 0.9f, endOn))));
                var facing = Vector3.ProjectOnPlane(toCamera, axis);
                if (facing.sqrMagnitude < 1e-6f) facing = Vector3.ProjectOnPlane(camera.transform.up, axis);
                if (facing.sqrMagnitude > 1e-8f) sBeam.transform.rotation = Quaternion.LookRotation(axis, facing);
                sBeam.transform.position = mouth;
            }
            var look = Quaternion.LookRotation(-camera.transform.forward, camera.transform.up);
            float mouthSize = sGlow != null ? sGlow.transform.localScale.x : 1f;
            // just past the mouth and pulled a little toward the camera, so the bell's rim doesn't cut it in half
            var glowAt = mouth + axis * (mouthSize * 0.15f) + toCamera.normalized * Mathf.Min(mouthSize * 0.5f, toCamera.magnitude * 0.5f);
            if (sGlow != null) { sGlow.transform.rotation = look; sGlow.transform.position = glowAt; }
            if (sStreak != null) { sStreak.transform.rotation = look; sStreak.transform.position = glowAt; }
        }

        // An extra plume turned toward the camera the same way (the materials are shared: the end-on fade follows the first).
        static void FaceOne(Camera camera, Vector3 mouth, Vector3 axis, Transform? beam, Transform? glow, Transform? streak)
        {
            var toCamera = camera.transform.position - mouth;
            if (beam != null)
            {
                var facing = Vector3.ProjectOnPlane(toCamera, axis);
                if (facing.sqrMagnitude < 1e-6f) facing = Vector3.ProjectOnPlane(camera.transform.up, axis);
                if (facing.sqrMagnitude > 1e-8f) beam.rotation = Quaternion.LookRotation(axis, facing);
                beam.position = mouth;
            }
            var look = Quaternion.LookRotation(-camera.transform.forward, camera.transform.up);
            float mouthSize = glow != null ? glow.localScale.x : 1f;
            var glowAt = mouth + axis * (mouthSize * 0.15f) + toCamera.normalized * Mathf.Min(mouthSize * 0.5f, toCamera.magnitude * 0.5f);
            if (glow != null) { glow.rotation = look; glow.position = glowAt; }
            if (streak != null) { streak.rotation = look; streak.position = glowAt; }
        }
    }
}
