using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.VFX;

namespace TheExpanse
{
    // The injectors' feeds and the plasma's leaks, drawn around the ball:
    //   feed beams: the game's grapple beam (FX_GrappleBeams) from each connected injector's nozzle to the ball, at
    //               half size; a beam shrinks away when its injector is
    //               disconnected, so the player sees which ones are already out. A full size beam comes down from the
    //               big nozzle above the ball to its middle. All beams draw behind the ball;
    //   leak flames: while the plasma is off balance, the game's fire (FX_Fire_01) bursts out of the ball in a cone
    //               (LeakConeDegrees wide) aimed at the middle of the gap, as far as the gap's control handles, in
    //               waves: each pulse of the ball sends one wave of flames out along the cone, filling it evenly
    //               across; the flames a wave leaves behind burn out on their own.
    // Everything is laid out in the reactor core's own space, so it moves and turns with the ship.
    internal static class ReactorFeeds
    {
        const string BeamMaterialPath = "Assets/Content/Art/FX/Materials/FX_M_GrappleBeam_01_Blue.mat";
        const string BeamPrefabPath = "Prefabs/Approved/FX_GrappleBeams.prefab";   // its address in the catalog (not its file path)
        const string FireGuid = "71965b2f0487c9947903596d99098ae9";     // FX_Fire_01

        internal struct Feed
        {
            internal bool Connected;
            internal Vector3 LocalNozzle, LocalSlot, LocalHandle;      // core space
            internal Vector3 LocalDirection;                           // unit, ball toward nozzle across the ring
        }

        const float FeedWidth = 0.5f;           // injector beams, against the big nozzle's full size one
        const int LeakFlames = 2;               // fire effects sharing each wave (two seeds: denser)
        const float WaveBand = 1.5f;            // m deep: the band of the cone each wave spawns flames in
        const float LeakConeDegrees = 30f;      // the cone's width past the gap: it spans the gap's injectors plus this
        static readonly GameObject?[] sLeak = new GameObject?[LeakFlames];
        static float sLeakReach;                // m the flames have crept out along the cone
        static Vector3 sLeakAxis;               // unit, core space, eased toward the middle of the gap
        static float sLeakLength;               // m the cone reaches: half way to the gap's handles per injector's worth
        static Vector3 sLeakApex;               // core space
        static float sBakedReach = -1f, sBakedFront = -1f;
        static Vector3 sBakedAxis;

        static GameObject? sCoreBeam;
        static float sLeakDegrees = LeakConeDegrees;    // the cone's full width now

        sealed class Lane
        {
            internal GameObject? Beam;
            internal float BeamShown;           // 0..1
        }

        static GameObject? sRoot;
        static Material? sBeamMaterial, sFallbackMaterial;
        static GameObject? sFirePrefab, sBeamPrefab;
        static bool sBeamTried, sBeamDescribed;
        static bool sLoading, sLogged;
        static Mesh? sQuad;
        static readonly List<Lane> sLanes = new List<Lane>();

        internal static void Destroy()
        {
            if (sRoot != null) Object.Destroy(sRoot);
            if (sFallbackMaterial != null) Object.Destroy(sFallbackMaterial);
            sRoot = null;
            sFallbackMaterial = null;
            sLanes.Clear();
            for (int k = 0; k < LeakFlames; k++) sLeak[k] = null;
            sCoreBeam = null;
            sLeakReach = 0f; sLeakAxis = Vector3.zero; sBakedReach = sBakedFront = -1f;
        }

        internal static void Build()
        {
            Destroy();
            sRoot = new GameObject("TheExpanse_ReactorFeeds");
            if (sLoading || (sBeamMaterial != null && sFirePrefab != null)) return;
            sLoading = true;
            try
            {
                Addressables.LoadAssetAsync<Material>(BeamMaterialPath).Completed += h =>
                {
                    if (h.Status == AsyncOperationStatus.Succeeded && h.Result != null) sBeamMaterial = h.Result;
                    else Plugin.Log.LogWarning("[Reactor] the grapple beam material could not be loaded; the feed beams are drawn plain blue.");
                };
                Addressables.LoadAssetAsync<GameObject>(BeamPrefabPath).Completed += h =>
                {
                    sBeamTried = true;
                    if (h.Status == AsyncOperationStatus.Succeeded && h.Result != null) sBeamPrefab = h.Result;
                    else Plugin.Log.LogWarning("[Reactor] FX_GrappleBeams could not be loaded; the feed beams are drawn as plain strips.");
                };
                Addressables.LoadAssetAsync<GameObject>(FireGuid).Completed += h =>
                {
                    if (h.Status == AsyncOperationStatus.Succeeded && h.Result != null) sFirePrefab = h.Result;
                    else Plugin.Log.LogWarning("[Reactor] the game's fire could not be loaded; no leak flames.");
                };
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[Reactor] feed effects could not be loaded: " + ex.Message); }
        }

        // A flat strip in the XZ plane: X across (-0.5..0.5), Z along (0..1); U runs along it.
        static Mesh Quad()
        {
            if (sQuad != null) return sQuad;
            sQuad = new Mesh
            {
                name = "TheExpanse_Beam",
                vertices = new[] { new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(-0.5f, 0f, 1f), new Vector3(0.5f, 0f, 1f) },
                uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 0f), new Vector2(1f, 1f) },
                triangles = new[] { 0, 2, 1, 1, 2, 3, 0, 1, 2, 1, 3, 2 },     // both faces
            };
            sQuad.RecalculateNormals();
            sQuad.RecalculateBounds();
            return sQuad;
        }

        // The game's grapple beam effect, posed and driven from nozzle to ball each frame by whatever it has: line
        // renderers get their points, effect graphs their start/end/length settings. Logged once, for tuning.
        static readonly string[] StartNames = { "Start", "StartPos", "StartPosition", "Origin", "Source", "Position_Start", "Pos_Start", "BeamStart" };
        static readonly string[] EndNames = { "End", "EndPos", "EndPosition", "Target", "TargetPosition", "Destination", "Position_End", "Pos_End", "BeamEnd", "Hit" };
        static readonly string[] LengthNames = { "Length", "BeamLength", "Distance" };

        static GameObject NewGrappleBeam()
        {
            var beam = Object.Instantiate(sBeamPrefab!, sRoot!.transform, false);
            beam.name = "GrappleBeam";
            beam.SetActive(true);
            DrawBehind(beam);
            foreach (var effect in beam.GetComponentsInChildren<VisualEffect>(true))
            {
                effect.enabled = true;
                effect.Reinit();
                effect.Play();
                effect.SendEvent("Start");
                effect.SendEvent("OnPlay");
            }
            foreach (var line in beam.GetComponentsInChildren<LineRenderer>(true)) line.enabled = true;
            if (!sBeamDescribed)
            {
                sBeamDescribed = true;
                var text = new System.Text.StringBuilder();
                foreach (var t in beam.GetComponentsInChildren<Transform>(true))
                {
                    var parts = new List<string>();
                    foreach (var c in t.GetComponents<Component>()) if (c != null && !(c is Transform)) parts.Add(c.GetType().Name);
                    text.Append($" [{t.name}: {string.Join(", ", parts)}; at {beam.transform.InverseTransformPoint(t.position)}]");
                }
                foreach (var line in beam.GetComponentsInChildren<LineRenderer>(true))
                    text.Append($" | line {line.name}: {line.positionCount} points, world {line.useWorldSpace}, width {line.startWidth:0.##}-{line.endWidth:0.##}, material {(line.sharedMaterial != null ? line.sharedMaterial.name : "none")}");
                Plugin.Log.LogInfo($"[Reactor] FX_GrappleBeams:{text}; effect settings: {FxShape.Describe(beam, false, default)}");
            }
            return beam;
        }

        // HDRP draws see-through things in order of their renderer priority before distance: the beams go first, so
        // the ball (ReactorVisuals.DrawOnTop) is drawn over them.
        internal static void DrawBehind(GameObject shown)
        {
            foreach (var renderer in shown.GetComponentsInChildren<Renderer>(true)) { renderer.rendererPriority = -10; renderer.sortingOrder = -10; }
        }

        static void SetIfAny(VisualEffect effect, string[] names, Vector3 value)
        {
            foreach (var name in names) if (effect.HasVector3(name)) effect.SetVector3(name, value);
        }

        static void Pose(GameObject beam, Vector3 from, Vector3 to, float width)
        {
            var along = to - from;
            beam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(along.sqrMagnitude > 1e-6f ? along : Vector3.forward));
            bool driven = false;
            foreach (var line in beam.GetComponentsInChildren<LineRenderer>(true))
            {
                line.useWorldSpace = true;
                const int Points = 24;
                line.positionCount = Points;
                for (int k = 0; k < Points; k++) line.SetPosition(k, Vector3.Lerp(from, to, k / (Points - 1f)));
                line.widthMultiplier = width;
                driven = true;
            }
            foreach (var effect in beam.GetComponentsInChildren<VisualEffect>(true))
            {
                // its settings are in its own space: from is its origin, to lies along +Z
                var localTo = effect.transform.InverseTransformPoint(to);
                var localFrom = effect.transform.InverseTransformPoint(from);
                SetIfAny(effect, StartNames, localFrom);
                SetIfAny(effect, EndNames, localTo);
                foreach (var name in LengthNames) if (effect.HasFloat(name)) { effect.SetFloat(name, along.magnitude); driven = true; }
                foreach (var name in EndNames) if (effect.HasVector3(name)) driven = true;
            }
            // nothing to drive: stretch it along its +Z instead
            beam.transform.localScale = driven ? Vector3.one * Mathf.Max(0.001f, width) : new Vector3(width, width, along.magnitude);
        }

        static GameObject NewBeam()
        {
            var beam = new GameObject("Beam");
            beam.transform.SetParent(sRoot!.transform, false);
            beam.AddComponent<MeshFilter>().sharedMesh = Quad();
            var renderer = beam.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            if (sBeamMaterial != null) renderer.sharedMaterial = sBeamMaterial;
            else renderer.sharedMaterial = sFallbackMaterial ??= FxShape.GlowMaterial(new Color(0.3f, 0.6f, 1f), 6f);
            if (!sLogged && sBeamMaterial != null)
            {
                sLogged = true;
                Plugin.Log.LogInfo($"[Reactor] feed beams drawn with {sBeamMaterial.name}; materials:{FxShape.DescribeMaterials(beam)}");
            }
            return beam;
        }

        // Flames in the band [from, to] meters out along the cone: evenly along it, evenly across it.
        static readonly Dictionary<long, Texture2D> sCones = new Dictionary<long, Texture2D>();

        static void BakeCone(GameObject flame, Vector3 apex, Vector3 axis, float from, float to, int part)
        {
            var random = new System.Random(4242 + part);
            var points = new Color[256];
            var across = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, Vector3.up)) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var across2 = Vector3.Cross(axis, across);
            float spread = Mathf.Tan(Mathf.Clamp(sLeakDegrees, 1f, 170f) * 0.5f * Mathf.Deg2Rad);
            for (int i = 0; i < points.Length; i++)
            {
                float d = Mathf.Lerp(from, to, (float)random.NextDouble());
                float r = d * spread * Mathf.Sqrt((float)random.NextDouble());
                float a = (float)(random.NextDouble() * Mathf.PI * 2.0);
                var at = apex + axis * d + (across * Mathf.Cos(a) + across2 * Mathf.Sin(a)) * r;
                points[i] = new Color(at.x, at.y, at.z, 0f);
            }
            // one texture per stretch, rewritten in place
            long key = part;
            if (!sCones.TryGetValue(key, out var texture) || texture == null)
                sCones[key] = texture = new Texture2D(16, 16, TextureFormat.RGBAHalf, false) { name = "TheExpanse_LeakPoints" };
            texture.SetPixels(points);
            texture.Apply();
            foreach (var effect in flame.GetComponentsInChildren<VisualEffect>(true))
            {
                if (effect.HasTexture("PositionMap")) effect.SetTexture("PositionMap", texture);
                if (effect.HasVector3("Size")) effect.SetVector3("Size", Vector3.one);
            }
        }

        // Every frame. core: the reactor core's transform (null once it is gone: nothing is drawn); localCenter: the
        // ball, core space; imbalance: the unbalanced push, core space (its length in injectors' worth); fade 1..0.
        internal static void Show(Transform? core, Vector3 localCenter, Vector3 localDown, Vector3 localCoreTip, Feed[] feeds,
                                  Vector3 imbalance, float fade, float pulse)
        {
            if (sRoot == null) return;
            if (core == null) { sRoot.SetActive(false); return; }
            sRoot.SetActive(true);
            sRoot.transform.SetPositionAndRotation(core.position, core.rotation);
            while (sLanes.Count < feeds.Length) sLanes.Add(new Lane());
            var camera = Camera.main;
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            float m = imbalance.magnitude;
            var push = m > 1e-3f ? imbalance / m : Vector3.zero;

            Leak(localCenter, feeds, imbalance, fade, dt, pulse);

            // The big nozzle's beam: down through the ball and on down the throat.
            if (sBeamPrefab != null && fade > 0.001f)
            {
                sCoreBeam ??= NewGrappleBeam();
                sCoreBeam.SetActive(true);
                Pose(sCoreBeam, core.TransformPoint(localCoreTip), core.TransformPoint(localCenter), fade);
            }
            else if (sCoreBeam != null) sCoreBeam.SetActive(false);
            for (int i = 0; i < feeds.Length; i++)
            {
                var f = feeds[i];
                var lane = sLanes[i];

                // Feed beam: nozzle to ball, turned to face the camera, shrinking away once disconnected.
                lane.BeamShown = Mathf.MoveTowards(lane.BeamShown, f.Connected ? 1f : 0f, dt * 2f);
                if (lane.BeamShown > 0.001f && fade > 0.001f && sBeamPrefab != null)
                {
                    lane.Beam ??= NewGrappleBeam();
                    lane.Beam.SetActive(true);
                    Pose(lane.Beam, core.TransformPoint(f.LocalNozzle), core.TransformPoint(localCenter), FeedWidth * lane.BeamShown * fade);
                }
                else if (lane.BeamShown > 0.001f && fade > 0.001f && sBeamTried)
                {
                    lane.Beam ??= NewBeam();
                    lane.Beam.SetActive(true);
                    var from = core.TransformPoint(f.LocalNozzle);
                    var to = core.TransformPoint(localCenter);
                    var along = to - from;
                    var facing = camera != null ? Vector3.ProjectOnPlane(camera.transform.position - from, along) : Vector3.up;
                    if (facing.sqrMagnitude < 1e-6f) facing = Vector3.Cross(along, Vector3.right);
                    var t = lane.Beam.transform;
                    t.SetPositionAndRotation(from, Quaternion.LookRotation(along, facing));
                    t.localScale = new Vector3(0.35f * lane.BeamShown * fade, 1f, along.magnitude);
                }
                else
                {
                    if (lane.Beam != null) lane.Beam.SetActive(false);
                }

            }
        }

        // Off balance: a cone of fire from the ball toward the middle of the gap (the disconnected injectors on the
        // unbalanced side), reaching their control handles. It creeps out and draws back; its aim eases.
        static void Leak(Vector3 localCenter, Feed[] feeds, Vector3 imbalance, float fade, float dt, float pulse)
        {
            float m = imbalance.magnitude;
            var push = m > 1e-3f ? imbalance / m : Vector3.zero;
            var handles = Vector3.zero; int gaps = 0;
            if (m > 0.05f && fade > 0.05f)
                foreach (var f in feeds)
                    if (!f.Connected && Vector3.Dot(f.LocalDirection, push) > 0.38f) { handles += f.LocalHandle; gaps++; }
            bool leaking = gaps > 0;
            if (leaking)
            {
                var toward = handles / gaps - localCenter;
                // one injector's worth of imbalance reaches half way to the gap's handles; two or more, all the way
                sLeakLength = toward.magnitude * Mathf.Min(1f, 0.5f * m);
                sLeakApex = localCenter;
                var aim = toward.normalized;
                // as wide as the gap's injectors spread around its middle, plus LeakConeDegrees
                float widest = 0f;
                foreach (var f in feeds)
                    if (!f.Connected && Vector3.Dot(f.LocalDirection, push) > 0.38f)
                        widest = Mathf.Max(widest, Vector3.Angle(aim, f.LocalHandle - localCenter));
                float degrees = 2f * widest + LeakConeDegrees;
                if (Mathf.Abs(degrees - sLeakDegrees) > 1f) sBakedReach = -1f;      // re-bake at the new width
                sLeakDegrees = Mathf.MoveTowards(sLeakDegrees, degrees, dt * 30f);
                sLeakAxis = sLeakAxis.sqrMagnitude < 0.5f ? aim : Vector3.Slerp(sLeakAxis, aim, 1f - Mathf.Exp(-dt * 2f)).normalized;
            }
            sLeakReach = Mathf.MoveTowards(sLeakReach, leaking ? sLeakLength : 0f, dt * (leaking ? 1.5f : 3f));
            bool show = sLeakReach > 0.05f && sFirePrefab != null && sLeakAxis.sqrMagnitude > 0.5f;
            // the wave's front runs from the ball to the reach once per pulse (pulse: 0..1 through the ball's cycle)
            // starting as the ball swells to its largest (halfway through its cycle)
            float front = Mathf.Repeat(pulse - 0.5f, 1f) * sLeakReach;
            bool rebake = show && (Mathf.Abs(front - sBakedFront) >= 0.1f || Mathf.Abs(sLeakReach - sBakedReach) >= 0.1f
                                   || Vector3.Angle(sLeakAxis, sBakedAxis) > 1.5f);
            for (int k = 0; k < LeakFlames; k++)
            {
                var flame = sLeak[k];
                if (!show) { if (flame != null && flame.activeSelf) flame.SetActive(false); continue; }
                bool fresh = false;
                if (flame == null)
                {
                    flame = sLeak[k] = Object.Instantiate(sFirePrefab!, sRoot!.transform, false);
                    flame.name = "Leak";
                    flame.transform.localPosition = Vector3.zero;
                    flame.transform.localRotation = Quaternion.identity;
                    fresh = true;
                }
                else if (!flame.activeSelf) { flame.SetActive(true); fresh = true; }
                if (rebake || fresh) BakeCone(flame, localCenter, sLeakAxis, Mathf.Max(0f, front - WaveBand), Mathf.Max(0.05f, front), k);
                if (fresh) Play(flame);
            }
            if (rebake) { sBakedReach = sLeakReach; sBakedAxis = sLeakAxis; sBakedFront = front; }
        }

        // The leak cone as it is now, in the world, for the hazards. False while there is none.
        internal static bool TryCone(out Vector3 apex, out Vector3 axis, out float reach, out float halfDegrees)
        {
            apex = axis = Vector3.zero; reach = halfDegrees = 0f;
            if (sRoot == null || !sRoot.activeInHierarchy || sLeakReach < 0.3f || sLeakAxis.sqrMagnitude < 0.5f) return false;
            apex = sRoot.transform.TransformPoint(sLeakApex);
            axis = sRoot.transform.TransformDirection(sLeakAxis).normalized;
            reach = sLeakReach;
            halfDegrees = sLeakDegrees * 0.5f;
            return true;
        }

        static void Play(GameObject shown)
        {
            foreach (var effect in shown.GetComponentsInChildren<VisualEffect>(true))
            {
                effect.enabled = true;
                effect.Reinit();
                effect.Play();
                effect.SendEvent("Start");
                effect.SendEvent("OnPlay");
            }
        }
    }
}
