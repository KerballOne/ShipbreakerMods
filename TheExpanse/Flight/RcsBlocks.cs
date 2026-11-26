using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TheExpanse
{
    // The RCS thruster blocks placed in setup mode (ShipSetup): a matte black truncated square pyramid on the hull (base
    // 1, top 0.6, height 0.4, in blocks: the top and each side the same area), with a nozzle in the middle of each side
    // and of the top, pointing straight out of its face (the sides 26 degrees off the hull). While flying, each nozzle
    // puffs a short white jet when its push helps what the pilot asks: the side thrusters (and the brake, hold attitude,
    // flip and burn) moving the ship, or turning it about its middle.
    internal static class RcsBlocks
    {
        const float Top = 0.6f, Height = 0.4f;          // of the base
        const float JetLength = 6f, JetWidth = 0.88f;    // of the base (4x the first try: too small to see)

        sealed class Nozzle { internal Transform Mouth = null!; internal Transform Jet = null!; internal float Level; }
        static readonly List<Nozzle> sNozzles = new List<Nozzle>();
        static readonly List<GameObject> sBlocks = new List<GameObject>();
        static Mesh? sBlockMesh, sNozzleMesh, sJetMesh;
        static Material? sBlockMaterial, sNozzleMaterial, sJetMaterial;
        static bool sHooked;

        internal static void Clear()
        {
            foreach (var b in sBlocks) if (b != null) Object.Destroy(b);
            sBlocks.Clear();
            sNozzles.Clear();
        }

        internal static void Add(Transform parent, Vector3 position, Vector3 up, Vector3 forward, float size, bool withBlock = true)
        {
            Make();
            bool drawBlock = withBlock;
            var blockObject = new GameObject("TheExpanse_RcsBlock");
            var block = blockObject;
            block.transform.SetParent(parent, false);
            block.transform.SetPositionAndRotation(position, Quaternion.LookRotation(forward, up));
            block.transform.localScale = Vector3.one;
            if (drawBlock)
            {
                var body = new GameObject("Body");
                body.transform.SetParent(block.transform, false);
                body.transform.localScale = Vector3.one * size;
                body.AddComponent<MeshFilter>().sharedMesh = sBlockMesh;
                var r = body.AddComponent<MeshRenderer>();
                r.sharedMaterial = sBlockMaterial;
            }
            sBlocks.Add(block);

            // the five nozzles: the middle of each side and of the top, pointing out of the face
            float b = 0.5f, t = Top * 0.5f, h = Height;
            var faces = new (Vector3 center, Vector3 normal)[]
            {
                (new Vector3(0, h, 0), Vector3.up),
                (new Vector3(0, h * 0.5f, (b + t) * 0.5f), new Vector3(0, b - t, h).normalized),
                (new Vector3(0, h * 0.5f, -(b + t) * 0.5f), new Vector3(0, b - t, -h).normalized),
                (new Vector3((b + t) * 0.5f, h * 0.5f, 0), new Vector3(h, b - t, 0).normalized),
                (new Vector3(-(b + t) * 0.5f, h * 0.5f, 0), new Vector3(-h, b - t, 0).normalized),
            };
            foreach (var (center, normal) in faces)
            {
                var mouth = new GameObject("Nozzle").transform;
                mouth.SetParent(block.transform, false);
                // with no block the jets leave from the hull panel itself, a hair off its surface
                mouth.localPosition = drawBlock ? center * size : Vector3.up * (0.02f * size);
                mouth.localRotation = Quaternion.LookRotation(normal, Mathf.Abs(normal.y) > 0.9f ? Vector3.forward : Vector3.up);
                if (drawBlock)
                {
                    var bell = new GameObject("Bell");
                    bell.transform.SetParent(mouth, false);
                    bell.transform.localScale = Vector3.one * size;
                    bell.AddComponent<MeshFilter>().sharedMesh = sNozzleMesh;
                    bell.AddComponent<MeshRenderer>().sharedMaterial = sNozzleMaterial;
                }
                var jet = new GameObject("Jet");
                jet.transform.SetParent(mouth, false);
                jet.transform.localPosition = Vector3.forward * (0.06f * size);
                jet.AddComponent<MeshFilter>().sharedMesh = sJetMesh;
                var jr = jet.AddComponent<MeshRenderer>();
                jr.sharedMaterial = sJetMaterial;
                jr.shadowCastingMode = ShadowCastingMode.Off;
                jr.receiveShadows = false;
                jet.SetActive(false);
                sNozzles.Add(new Nozzle { Mouth = mouth, Jet = jet.transform });
            }
            sSize = size;
        }

        static float sSize = 1f;

        // ---- firing (every frame, LateUpdate) --------------------------------------------------------------------------
        internal static void Frame()
        {
            if (sNozzles.Count == 0) return;
            bool flying = FlightModel.UnderWay && !FlightModel.Parked;
            var linear = flying ? FlightModel.RcsCommand : Vector3.zero;
            var turn = flying ? FlightModel.SpinCommand : Vector3.zero;
            var middle = FlightModel.ShipPosition;
            float reach = Mathf.Max(1f, FlightModel.HalfLength);
            float dt = Time.deltaTime;
            foreach (var n in sNozzles)
            {
                if (n.Mouth == null) continue;
                var push = -n.Mouth.forward;                 // what the nozzle does to the ship
                float want = Mathf.Max(0f, Vector3.Dot(push, linear));
                var torque = Vector3.Cross(n.Mouth.position - middle, push) / reach;
                want += Mathf.Max(0f, Vector3.Dot(torque, turn)) * 1.5f;
                want = Mathf.Clamp01(want);
                n.Level = Mathf.MoveTowards(n.Level, want, dt * (want > n.Level ? 20f : 10f));
                bool on = n.Level > 0.05f;
                if (n.Jet.gameObject.activeSelf != on) n.Jet.gameObject.SetActive(on);
                if (!on) continue;
                float flicker = 0.85f + 0.15f * Mathf.PerlinNoise(Time.time * 30f, n.Mouth.GetInstanceID() * 0.01f);
                n.Jet.localScale = new Vector3(JetWidth * sSize * (0.6f + 0.4f * n.Level), 1f, JetLength * sSize * n.Level * flicker);
            }
        }

        // each jet turns about its own axis to face the camera
        static void FaceCamera(ScriptableRenderContext context, Camera camera)
        {
            if (camera == null) return;
            foreach (var n in sNozzles)
            {
                if (n.Jet == null || !n.Jet.gameObject.activeInHierarchy) continue;
                var axis = n.Mouth.forward;
                var facing = Vector3.ProjectOnPlane(camera.transform.position - n.Jet.position, axis);
                if (facing.sqrMagnitude < 1e-6f) continue;
                n.Jet.rotation = Quaternion.LookRotation(axis, facing);
            }
        }

        // ---- meshes and materials --------------------------------------------------------------------------------------
        static void Make()
        {
            if (!sHooked) { RenderPipelineManager.beginCameraRendering += FaceCamera; sHooked = true; }
            if (sBlockMesh != null) return;
            sBlockMesh = BlockMesh();
            sNozzleMesh = NozzleMesh();
            sJetMesh = JetMesh();
            sBlockMaterial = Matte(new Color(0.16f, 0.16f, 0.17f), PanelTexture(), 0.25f);      // dark grey
            sNozzleMaterial = Matte(new Color(0.08f, 0.08f, 0.085f), null, 0.45f);    // darker than the block
            sJetMaterial = DrivePlume.AdditiveMaterial(JetTexture());
            sJetMaterial.name = "TheExpanse_RcsJet";
            DrivePlume.SetBrightness(sJetMaterial, 1.6f);
        }

        // the truncated pyramid: four sides and the top, flat-shaded, each face its own 0..1 texture square
        static Mesh BlockMesh()
        {
            float b = 0.5f, t = Top * 0.5f, h = Height;
            var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var tris = new List<int>();
            void Quad(Vector3 a, Vector3 bb, Vector3 c, Vector3 d)       // a,b at the bottom, c,d at the top, counter-clockwise from outside
            {
                int i = verts.Count;
                verts.Add(a); verts.Add(bb); verts.Add(c); verts.Add(d);
                uvs.Add(new Vector2(0, 0)); uvs.Add(new Vector2(1, 0)); uvs.Add(new Vector2(1, 1)); uvs.Add(new Vector2(0, 1));
                tris.Add(i); tris.Add(i + 2); tris.Add(i + 1); tris.Add(i); tris.Add(i + 3); tris.Add(i + 2);
            }
            var B = new[] { new Vector3(-b, 0, -b), new Vector3(b, 0, -b), new Vector3(b, 0, b), new Vector3(-b, 0, b) };
            var T = new[] { new Vector3(-t, h, -t), new Vector3(t, h, -t), new Vector3(t, h, t), new Vector3(-t, h, t) };
            for (int k = 0; k < 4; k++) Quad(B[k], B[(k + 1) % 4], T[(k + 1) % 4], T[k]);
            Quad(T[0], T[1], T[2], T[3]);
            var mesh = new Mesh { name = "TheExpanse_RcsBlock" };
            mesh.SetVertices(verts); mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
            // the winding above faces inward for some sides: make every face look outward
            FixWinding(mesh);
            return mesh;
        }

        static void FixWinding(Mesh mesh)
        {
            var v = mesh.vertices; var tr = mesh.triangles;
            var center = new Vector3(0, Height * 0.4f, 0);
            for (int i = 0; i < tr.Length; i += 3)
            {
                var n = Vector3.Cross(v[tr[i + 1]] - v[tr[i]], v[tr[i + 2]] - v[tr[i]]);
                var mid = (v[tr[i]] + v[tr[i + 1]] + v[tr[i + 2]]) / 3f;
                if (Vector3.Dot(n, mid - center) < 0f) { int s = tr[i + 1]; tr[i + 1] = tr[i + 2]; tr[i + 2] = s; }
            }
            mesh.triangles = tr;
            mesh.RecalculateNormals(); mesh.RecalculateTangents();
        }

        // a short flared bell along +z: radius 0.06 to 0.085 of the base, 0.06 long, open
        static Mesh NozzleMesh()
        {
            const int N = 12;
            var verts = new List<Vector3>(); var tris = new List<int>();
            for (int i = 0; i < N; i++)
            {
                float a = i * Mathf.PI * 2f / N;
                var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                verts.Add(dir * 0.06f); verts.Add(dir * 0.085f + Vector3.forward * 0.06f);
            }
            for (int i = 0; i < N; i++)
            {
                int a = i * 2, b2 = ((i + 1) % N) * 2;
                tris.AddRange(new[] { a, b2, a + 1, a + 1, b2, b2 + 1 });          // outside
                tris.AddRange(new[] { a, a + 1, b2, a + 1, b2 + 1, b2 });          // inside
            }
            var mesh = new Mesh { name = "TheExpanse_RcsNozzle" };
            mesh.SetVertices(verts); mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }

        // the jet: a flat strip along +z from the nozzle, turned to face the camera
        static Mesh JetMesh()
        {
            var mesh = new Mesh { name = "TheExpanse_RcsJet" };
            mesh.vertices = new[] { new Vector3(-0.5f, 0, 0), new Vector3(0.5f, 0, 0), new Vector3(-0.5f, 0, 1), new Vector3(0.5f, 0, 1) };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            mesh.RecalculateNormals();
            mesh.bounds = new Bounds(new Vector3(0, 0, 0.5f), Vector3.one);
            return mesh;
        }

        static Texture2D JetTexture()
        {
            const int W = 16, H = 64;
            var t = new Texture2D(W, H, TextureFormat.RGBAHalf, false) { wrapMode = TextureWrapMode.Clamp, name = "TheExpanse_RcsJet" };
            var px = new Color[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W * 2f - 1f, v = (y + 0.5f) / H;
                    float width = 0.35f + 0.65f * v;                              // widens a little downstream
                    float across = Mathf.Exp(-(u / width) * (u / width) * 4f);
                    float along = Mathf.Pow(1f - v, 1.6f) * Mathf.SmoothStep(0f, 1f, v * 12f);
                    float c = across * along;
                    px[y * W + x] = new Color(c, c, c * 1.05f, 1f);
                }
            t.SetPixels(px); t.Apply(false, true);
            return t;
        }

        // panel lines just inside each face's edge, a fine border, a few bolt dots in the corners
        static Texture2D PanelTexture()
        {
            const int N = 64;
            var t = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "TheExpanse_RcsPanel" };
            var px = new Color[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N, v = (y + 0.5f) / N;
                    float edge = Mathf.Min(Mathf.Min(u, 1 - u), Mathf.Min(v, 1 - v));
                    float c = 1f;
                    if (edge < 0.03f) c = 1.6f;                                     // a light worn edge
                    else if (Mathf.Abs(edge - 0.1f) < 0.012f) c = 0.45f;           // the panel line
                    foreach (var corner in new[] { new Vector2(0.16f, 0.16f), new Vector2(0.84f, 0.16f), new Vector2(0.16f, 0.84f), new Vector2(0.84f, 0.84f) })
                        if ((new Vector2(u, v) - corner).magnitude < 0.028f) c = 2.2f; // bolt heads
                    px[y * N + x] = new Color(c * 0.5f, c * 0.5f, c * 0.5f, 1f);
                }
            t.SetPixels(px); t.Apply(true, true);
            return t;
        }

        // a matte HDRP/Lit material, copied from one of the game's own opaque Lit materials (so the shader is in the build)
        static Material Matte(Color color, Texture? map, float smoothness)
        {
            Material? template = null;
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                if (m != null && m.shader != null && m.shader.name == "HDRP/Lit" && !m.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT")) { template = m; break; }
            var material = template != null ? new Material(template) : new Material(Shader.Find("Standard"));
            material.name = "TheExpanse_RcsMatte";
            foreach (var k in new[] { "_NORMALMAP", "_MASKMAP", "_EMISSIVE_COLOR_MAP", "_DETAIL_MAP", "_BENTNORMALMAP", "_HEIGHTMAP" }) material.DisableKeyword(k);
            foreach (var p in new[] { "_NormalMap", "_MaskMap", "_EmissiveColorMap", "_DetailMap", "_BentNormalMap", "_HeightMap" })
                if (material.HasProperty(p)) material.SetTexture(p, null);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_BaseColorMap")) material.SetTexture("_BaseColorMap", map);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0.2f);
            if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", Color.black);
            material.color = color;
            if (map != null) material.mainTexture = map;
            return material;
        }
    }
}
