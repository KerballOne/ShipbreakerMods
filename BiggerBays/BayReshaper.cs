using System;
using System.Collections.Generic;
using BBI.Unity.Game;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.VFX;
using Object = UnityEngine.Object;

namespace BiggerBays
{
    // Makes the work bay wider and longer by sliding pieces of the station apart, and fills the added length with a
    // second furnace and processor on each side. Nothing is scaled.
    //
    // The stock bay, in world space (from PartInfoLogger's bay_geometry.csv; +Z runs from the back wall to the open end,
    // +X is the "Left" arm, +Y is up):
    //   back wall face     z = -17.9, a single piece 337 m wide, so the arms can slide along it without opening a gap
    //   side walls         inner faces at x = -45.2 and x = +34.4 (middle x = -5.5)
    //   furnaces           z = -4, one per side;  processors: z = 24, one per side, 9 m further out than the furnace, so
    //                      the line through the two mouths runs about 18 degrees off the bay's axis. Together with their
    //                      tunnels, ramps and trim they fill each arm from the back wall to z = 50
    //   dry dock walls     from z = 50 to z = 163 on each side, one mesh each; antenna tips to z = 250
    //   Master Jack        z = 90 to 118, in the middle
    //
    // The bay is reshaped once the ship has spawned, and only as far as that ship needs (see Apply): a ship that fits the
    // stock bay leaves it untouched.
    //
    // Width: everything that belongs to an arm moves outward, far enough to leave SideClearanceMeters beside the ship.
    // Things in the middle (back wall, grain elevator, barge) stay.
    //
    // Length: on each arm, everything that starts at or past z = 50 (the dry dock) moves toward the open end by
    // ExtraLengthMeters. Everything before that (the furnace and processor section) is copied that far along, so the
    // space left behind the dry dock is filled by a second furnace and processor. The copied section can be turned about
    // its processor so its two mouths stand in line with the bay instead of 18 degrees off it. Pieces that run the whole
    // length of an arm (roof, floor, outer wall) are copied too, straight along, which stretches them.
    //
    // How a piece is moved or copied depends on what it is:
    //   statically batched mesh   cannot be moved, see BatchedRedraw: it is hidden and redrawn, or drawn a second time
    //   anything else             its transform is moved; a copy is a clone of the object
    //   furnace, processor,       cloned as whole sets, so the copy's triggers, flows, lights and effects stay wired to
    //   their flows, lights,      each other; the triggers are plain colliders, so the copy salvages like the original.
    //   doors and effects         The bay's start-up and shutdown sequences only know the originals, so every copy is kept
    //                             in step with the object it was copied from (see Mirror)
    internal static class BayReshaper
    {
        const string MarkerName = "BiggerBays_Applied";
        const float Tol = 3f;
        // The furnace and processor section ends a few meters past the cut (its longest pieces reach z = 54.5), and one dry
        // dock wall collider starts 8 m before it. So "runs on past the cut" means by more than this, and a piece that
        // starts no more than this far before the cut and does run on belongs to the dry dock and moves with it.
        const float DryDockLead = 10f;
        // In the stock bay each side's furnace and processor sit in a recess shaped like a triangle: a side wall by the
        // back wall reaching about 25 m toward the middle of the bay, and from its tip a long slanted edge back out to
        // the dry dock wall, with plates and rails filling the triangle above and below the mouths. Every mesh and every
        // collider of the recess is copied, so what is drawn is also solid. Turned into line with the bay, the copy's
        // plates stand at least 24.5 m plus the added width from the center line, 4.7 m clear of a ship kept 20 m from
        // the side walls; a copied collider that would still reach into the ship is dropped (see step 6).
        // Pieces this far out are not part of the bay at all (the station's calipers) and are never copied.
        const float FarOut = 300f;
        // The two groups that make up each side's recess; they stay where they are and are copied, never moved along.
        const int MouthUnitA = 2, MouthUnitB = 3;
        const float BehindWall = ShipCentering.BackWallZ + 1.5f;
        // A stretched piece overlaps the piece it was copied from, face on face; this much apart they do not flicker.
        const float Nudge = 0.04f;
        internal const float StockHalfWidth = 39.8f;
        // The furthest each side of the bay is ever moved out.
        const float MaxExtraWidth = 40f;
        // How much longer the bay is made: the length of the furnace and processor section, so a copy of it fits exactly.
        const float ExtraLength = 76f;
        // The position along the bay where the furnace and processor section ends and the dry dock begins.
        const float Cut = 50f;
        // Pieces whose middle lies within this distance of the bay's center line stay put when the bay is widened.
        const float MiddleStrip = 10f;

        static readonly string[] Roots =
        {
            "Work Bays", "PRF_Bay_Collision", "FX_Salvagetest_Light_01", "PRF_BayAnimatedEmmissiveLighting",
            "Bay_Audio_Proximity_Assets", "TutorialLightRig",
        };
        // Only these get copies; the rest are only ever moved.
        static readonly HashSet<string> CopyRoots = new HashSet<string> { "Work Bays", "PRF_Bay_Collision", "FX_Salvagetest_Light_01" };

        // Groups that belong to one arm as a whole, however far toward the middle some of their pieces reach.
        static readonly string[] SideUnits =
        {
            "Work Bays/PRF_HexStation_Composite/PRF_HexStation_Bay/ArmLeft",
            "Work Bays/PRF_HexStation_Composite/PRF_HexStation_Bay/ArmRight",
            "Work Bays/PRF_HexStation_Composite/PRF_HexStation_ProcessorMouth",
            "Work Bays/PRF_HexStation_Composite/PRF_HexStation_ProcessorMouth (1)",
            "Work Bays/PRF_Bay_ScriptingandEffects (Left)",
            "Work Bays/PRF_Bay_ScriptingandEffects (Right)",
            "PRF_Bay_Collision/Arm (1)",
            "PRF_Bay_Collision/Arm (2)",
        };

        // The two sets that hold each side's furnace and processor triggers; they give the section's angle and pivot.
        static readonly string[] SalvageSets =
        {
            "Work Bays/PRF_Bay_ScriptingandEffects (Left)",
            "Work Bays/PRF_Bay_ScriptingandEffects (Right)",
        };

        // Groups copied in one piece so the scripts inside keep pointing at each other. The last name of a path may match
        // several objects (the two mouth doors share a name).
        static readonly string[] WholeClones =
        {
            "Work Bays/PRF_Bay_ScriptingandEffects (Left)",
            "Work Bays/PRF_Bay_ScriptingandEffects (Right)",
            "FX_Salvagetest_Light_01/Anim_Furnace_Processor_Root",
            "FX_Salvagetest_Light_01/AnimSalvageDoorIntroSequence/Anim_SalvageMouthDoor_Root_01",
        };

        // The visible middle of the back wall: one panel with the three doors, as wide as the gap between the two recesses'
        // side walls. When the arms move out it is stretched sideways to keep filling that gap.
        const string BackWallPanel = "Work Bays/PRF_HexStation_Composite/PRF_HexStation_Bay/BackBoard/SM_HexStation_Bay_Backboard_ThreeDoor";

        // Left exactly as the game has them: they belong to the barge and the Master Jack, which are not part of an arm,
        // or are switched off.
        static readonly string[] LeftAlone =
        {
            "PRF_BayAnimatedEmmissiveLighting/BargeEmissiveLights",
            "PRF_BayAnimatedEmmissiveLighting/HexEmissiveLights_MasterJack",
            "Work Bays/Play Zone",
        };

        // Inside a whole-set copy, the parts that are about the barge and must not appear a second time.
        const string BargePrefix = "Anim_BargeStartUp_Root";

        enum Along { Stay, Move, Copy }

        sealed class Node
        {
            public Transform T = null!;
            public string Root = "";
            public string Group = "";
            public Vector3 Stock, LocalStock;
            public Quaternion StockRot;
            public Bounds Box;
            public MeshRenderer? Batched;
            public int Unit = -1;
            public Transform? Whole;        // the whole-set copy root this is under, if any
            public bool Animated;
            public int Side;
            public Along Z;
            public bool Spans;              // runs across the cut: copied straight along, not with the section
        }

        // Where one side's copied furnace and processor section goes: turned about a pivot, then moved.
        sealed class Section
        {
            public Vector3 Pivot, Offset;
            public Quaternion Rot = Quaternion.identity;
            public Matrix4x4 Matrix;
            public Vector3 Point(Vector3 p) => Pivot + Rot * (p - Pivot) + Offset;
        }

        sealed class Watch { public Transform T = null!; public Vector3 Delta, Last; }

        // One copied object and the object it was copied from.
        sealed class Puppet
        {
            public Transform O = null!, C = null!;
            public bool Transforms;                 // follow the original's animated position, rotation and scale
            public Vector3 OPlaced, CBase;
            public Light[] OLights = Array.Empty<Light>(), CLights = Array.Empty<Light>();
            public Renderer[] ORenderers = Array.Empty<Renderer>(), CRenderers = Array.Empty<Renderer>();
            public Behaviour[] OBehaviours = Array.Empty<Behaviour>(), CBehaviours = Array.Empty<Behaviour>();
            public Collider[] OColliders = Array.Empty<Collider>(), CColliders = Array.Empty<Collider>();
            public VisualEffect[] OEffects = Array.Empty<VisualEffect>(), CEffects = Array.Empty<VisualEffect>();
            public float NextEffectCheck;
        }

        static readonly List<Watch> sWatch = new List<Watch>();
        static readonly List<Puppet> sPuppets = new List<Puppet>();
        static readonly MaterialPropertyBlock sBlock = new MaterialPropertyBlock();

        // What the bay loaded now was given; the lights are compensated from this.
        internal static float AppliedWidth, AppliedLength;

        static string V(Vector3 v) => $"({v.x:0.0}, {v.y:0.0}, {v.z:0.0})";

        internal static void OnLevelLoadStart()
        {
            AppliedWidth = AppliedLength = 0f;
            sWatch.Clear();
            sPuppets.Clear();
            BatchedRedraw.Failures = 0;
        }

        // ---- the pass -----------------------------------------------------------------------------------------------
        internal static void Apply()
        {
            if (!Plugin.BiggerBay.Value) return;

            var workBays = FindRoot("Work Bays");
            if (workBays == null) return;                                       // not a bay level (menu, hab)
            if (workBays.transform.Find(MarkerName) != null) return;            // this bay has been done already

            float cx = ShipCentering.BayCenterX;
            float strip = MiddleStrip;
            float cut = Cut;

            // The bay is only made as big as the ship in it needs.
            //   width    each arm moves out just far enough to leave SideClearanceMeters between the ship and the side wall
            //   length   the dry docks move out, by ExtraLengthMeters, only when the ship reaches more than
            //            LengthTriggerMeters past the far edge of the stock processor section (the cut)
            if (!JackMover.MeasureShip(out var ship, out _)) return;            // no ship in this level
            float reach = Mathf.Max(ship.max.x - cx, cx - ship.min.x);
            float gap = StockHalfWidth - reach;
            float W = Mathf.Clamp(Plugin.SideClearance.Value - gap, 0f, MaxExtraWidth);
            float past = ship.max.z - cut;
            float L = past > Plugin.LengthTrigger.Value ? ExtraLength : 0f;
            // A ship loaded from a save gets the bay it had when it was new (see BayMemory); a new ship's is written down.
            bool recalled = BayMemory.TryRecall(out float savedW, out float savedL);
            if (recalled)
            {
                W = Mathf.Clamp(savedW, 0f, 100f);
                L = Mathf.Clamp(savedL, 0f, 300f);
            }
            else BayMemory.Remember(W, L);
            if (W <= 0f && L <= 0f) return;
            var started = DateTime.UtcNow;

            // 1. Every transform of the station, parents before children, with where it is and what it holds.
            var nodes = new List<Node>();
            var byT = new Dictionary<Transform, Node>();
            var rootObjects = new Dictionary<string, Transform>();
            foreach (var name in Roots)
            {
                var go = FindRoot(name);
                if (go == null) continue;
                rootObjects[name] = go.transform;
            }

            var unitRoots = new Dictionary<Transform, int>();
            for (int i = 0; i < SideUnits.Length; i++)
            {
                var t = Resolve(rootObjects, SideUnits[i]);
                if (t == null) Plugin.Log.LogWarning($"[Bay] '{SideUnits[i]}' not found; its pieces are placed one by one.");
                else unitRoots[t] = i;
            }
            var wholeRoots = new List<Transform>();
            if (L > 0f)
                foreach (var path in WholeClones)
                {
                    var found = ResolveAll(rootObjects, path);
                    if (found.Count == 0) Plugin.Log.LogWarning($"[Bay] '{path}' not found; it is not copied.");
                    wholeRoots.AddRange(found);
                }

            var skipped = new HashSet<Transform>();
            foreach (var path in LeftAlone)
            {
                var t = Resolve(rootObjects, path);
                if (t != null) skipped.Add(t);
            }

            foreach (var kv in rootObjects)
                foreach (var t in kv.Value.GetComponentsInChildren<Transform>(true))
                {
                    if (skipped.Contains(t)) continue;
                    if (t != kv.Value && t.parent != null && skipped.Contains(t.parent)) { skipped.Add(t); continue; }
                    byT.TryGetValue(t.parent != null ? t.parent : t, out var parent);
                    if (t == kv.Value) parent = null;
                    var n = new Node { T = t, Root = kv.Key, Stock = t.position, LocalStock = t.localPosition, StockRot = t.rotation };
                    n.Group = parent == null ? kv.Key : parent.Group.IndexOf('/') < 0 ? parent.Group + "/" + t.name : parent.Group;
                    n.Unit = unitRoots.TryGetValue(t, out int u) ? u : parent?.Unit ?? -1;
                    n.Whole = wholeRoots.Contains(t) ? t : parent?.Whole;
                    n.Animated = t.GetComponent<Animator>() != null || (parent?.Animated ?? false);
                    Measure(n);
                    nodes.Add(n);
                    byT[t] = n;
                }

            // 2. Which arm each side group is on: the side most of its pieces are on.
            var unitSum = new float[SideUnits.Length];
            var unitCount = new int[SideUnits.Length];
            foreach (var n in nodes)
                if (n.Unit >= 0 && n.Box.size != Vector3.zero) { unitSum[n.Unit] += n.Box.center.x - cx; unitCount[n.Unit]++; }

            // 3. What happens to each transform.
            foreach (var n in nodes)
            {
                if (n.Unit >= 0) n.Side = unitCount[n.Unit] == 0 ? 0 : unitSum[n.Unit] > 0f ? 1 : -1;
                else if (n.Whole == null && n.Box.max.z < BehindWall) n.Side = 0;   // the station behind the back wall stays whole
                else
                {
                    float dx = n.Box.center.x - cx;
                    n.Side = dx > strip ? 1 : dx < -strip ? -1 : 0;
                }

                n.Z = Along.Stay;
                if (n.Whole != null) n.Z = Along.Copy;                          // the whole set is copied, wherever its pieces are
                else if (L > 0f && n.Side != 0)
                {
                    // The recess's own pieces are never dry dock: its far side wall stands right at the cut.
                    bool recess = n.Unit == MouthUnitA || n.Unit == MouthUnitB;
                    n.Spans = !recess && n.Box.max.z > cut + DryDockLead;
                    if (!recess && n.Box.min.z >= cut - (n.Spans ? DryDockLead : Tol)) { n.Z = Along.Move; n.Spans = false; }
                    else if (CopyRoots.Contains(n.Root) && !LeftOut(n, cx)) n.Z = Along.Copy;
                }
            }

            // Where each side's copied section goes.
            var sections = new Dictionary<int, Section>();
            if (L > 0f)
                foreach (int side in new[] { 1, -1 }) sections[side] = MakeSection(side, W, L, rootObjects, byT);

            // 4. Whole-set copies, made while everything still stands where the game put it. A set that has pieces on both
            //    sides of the bay is copied once per side, each copy keeping only its own side's pieces.
            foreach (var root in wholeRoots)
            {
                try
                {
                    var originals = root.GetComponentsInChildren<Transform>(true);
                    var sides = new List<int>();
                    foreach (var o in originals)
                        if (byT.TryGetValue(o, out var n) && n.Side != 0 && !sides.Contains(n.Side)) sides.Add(n.Side);
                    if (sides.Count == 0) { Plugin.Log.LogWarning($"[Bay] '{root.name}' has nothing on either side; it is not copied."); continue; }

                    bool firstCopy = true;
                    foreach (int side in sides)
                    {
                        var sec = sections[side];
                        var clone = Object.Instantiate(root.gameObject, root.parent);
                        clone.name = root.name + " (BiggerBays)";
                        var copies = clone.GetComponentsInChildren<Transform>(true);
                        if (copies.Length != originals.Length)
                        {
                            Plugin.Log.LogWarning($"[Bay] copy of '{root.name}' has {copies.Length} objects against {originals.Length}; it is left out.");
                            Object.Destroy(clone);
                            continue;
                        }
                        var rn = byT[root];
                        clone.transform.SetPositionAndRotation(sec.Point(rn.Stock), sec.Rot * rn.StockRot);

                        var barge = new HashSet<Transform>();
                        for (int i = 0; i < originals.Length; i++)
                        {
                            if (!byT.TryGetValue(originals[i], out var n)) continue;
                            var c = copies[i];
                            if (i > 0 && (barge.Contains(c.parent) || c.name.StartsWith(BargePrefix, StringComparison.Ordinal)))
                            {
                                if (!barge.Contains(c.parent)) c.gameObject.SetActive(false);
                                barge.Add(c);
                                continue;
                            }
                            // Every object follows its original's on/off state and movement, so that groups switch on with
                            // the bay; only this side's own pieces keep their lights, meshes and triggers.
                            bool mine = n.Side == side || (n.Side == 0 && firstCopy);
                            Silence(c);
                            if (!mine) SwitchOff(c);
                            var p = Pair(originals[i], c, n.Batched != null, mine);
                            p.Transforms = i > 0;
                        }
                        firstCopy = false;
                    }
                }
                catch (Exception ex) { Plugin.Log.LogError($"[Bay] could not copy '{root.name}': {ex}"); }
            }

            // 5. Single-piece copies, and the batched renderers.
            var placements = new List<Matrix4x4>(2);
            var copyTargets = new List<KeyValuePair<Transform, Node>>();
            foreach (var n in nodes)
            {
                var first = new Vector3(n.Side * W, 0f, n.Z == Along.Move ? L : 0f);
                sections.TryGetValue(n.Side, out var sec);
                bool copied = n.Z == Along.Copy && (n.Spans || sec != null);

                if (n.Batched != null)
                {
                    placements.Clear();
                    placements.Add(Matrix4x4.Translate(first));
                    if (copied) placements.Add(n.Spans ? Matrix4x4.Translate(Stretched(n, W, L)) : sec!.Matrix);
                    if (placements.Count > 1 || first != Vector3.zero) BatchedRedraw.Set(n.Batched, placements);
                }

                if (copied && n.Whole == null && n.T.gameObject.activeInHierarchy)
                {
                    try
                    {
                        var copy = CopyPiece(n, out bool exact);
                        if (copy != null)
                        {
                            copyTargets.Add(new KeyValuePair<Transform, Node>(copy, n));
                            if (exact) Pair(n.T, copy, n.Batched != null, true);
                        }
                    }
                    catch (Exception ex) { Plugin.Log.LogError($"[Bay] could not copy '{n.T.name}': {ex.Message}"); }
                }
            }

            // 6. Move the originals, parents first, each to its own place; then the single-piece copies.
            foreach (var n in nodes)
            {
                var delta = new Vector3(n.Side * W, 0f, n.Z == Along.Move ? L : 0f);
                Place(n.T, n.Stock + delta);
            }
            foreach (var kv in copyTargets)
            {
                if (kv.Key == null) continue;
                var n = kv.Value;
                if (n.Spans) Place(kv.Key, n.Stock + Stretched(n, W, L));
                else
                {
                    var sec = sections[n.Side];
                    kv.Key.SetPositionAndRotation(sec.Point(n.Stock), sec.Rot * n.StockRot);
                }
            }

            // A copied collider with no mesh of its own (the recess's plates and rails) must not stand inside the ship.
            Physics.SyncTransforms();
            int dropped = 0;
            foreach (var kv in copyTargets)
            {
                if (kv.Key == null || kv.Value.Spans || IsMesh(kv.Value)) continue;
                bool inShip = false;
                foreach (var c in kv.Key.GetComponents<Collider>())
                    if (!c.isTrigger && c.enabled && c.bounds.Intersects(ship)) inShip = true;
                if (!inShip) continue;
                Object.Destroy(kv.Key.gameObject);
                dropped++;
            }

            if (W > 0f)
            {
                try { StretchBackWall(Resolve(rootObjects, BackWallPanel), W); }
                catch (Exception ex) { Plugin.Log.LogError("[Bay] could not widen the back wall's door panel: " + ex); }
            }

            // 7. Anything under an Animator may have its position written back by an animation; those are watched. Copies
            //    follow their originals from where those now stand.
            foreach (var n in nodes)
                if (n.Animated) AddWatch(n.T, n.LocalStock);
            foreach (var p in sPuppets) p.OPlaced = p.O.localPosition;

            Physics.SyncTransforms();

            try { ExtendPlayableArea(W, L, cx, strip, cut); }
            catch (Exception ex) { Plugin.Log.LogError("[Bay] could not extend the playable area: " + ex); }

            var marker = new GameObject(MarkerName);
            marker.transform.SetParent(workBays.transform, false);
            AppliedWidth = W;
            AppliedLength = L;

            Plugin.Log.LogInfo($"[Bay] ship reaches {reach:0.0} m from the bay's center line and {past:0.0} m past the processor section" +
                               $"{(recalled ? " (loaded from a save: the bay recorded for it is used)" : "")}: each arm moved out {W:0.0} m, bay made {L:0.0} m longer" +
                               $"{(L > 0f ? " with a second furnace and processor on each side" : "")}" +
                               $"{(dropped > 0 ? $"; {dropped} copied collider(s) left out because the ship reaches them" : "")}" +
                               $"{(BatchedRedraw.Failures > 0 ? $"; {BatchedRedraw.Failures} mesh(es) could not be matched and stay where they were" : "")}.");
        }

        static bool IsMesh(Node n) => n.Batched != null || n.T.GetComponent<MeshRenderer>() != null;

        static float Inner(Node n, float cx) => n.Side > 0 ? n.Box.min.x - cx : cx - n.Box.max.x;

        // True for a piece that is not part of the bay and is not copied (see FarOut).
        static bool LeftOut(Node n, float cx) => n.Whole == null && Inner(n, cx) > FarOut;

        // Widens the back wall's door panel by `W` on each side, about its own middle. Its collider is on the same object
        // and widens with it; the wall behind it is already solid across the whole station.
        static void StretchBackWall(Transform? panel, float W)
        {
            if (panel == null) { Plugin.Log.LogWarning("[Bay] the back wall's door panel was not found; there is a gap on each side of it."); return; }
            var box = BoxOf(panel, out var batched);
            float half = box.size.x * 0.5f;
            if (half < 1f) return;
            float k = (half + W) / half;
            if (batched != null)
            {
                var c = box.center;
                var m = Matrix4x4.Translate(c) * Matrix4x4.Scale(new Vector3(k, 1f, 1f)) * Matrix4x4.Translate(-c);
                BatchedRedraw.Set(batched, new[] { m });
                return;
            }
            // Scale along whichever of the panel's own axes lies across the bay.
            int axis = 0; float best = -1f;
            for (int a = 0; a < 3; a++)
            {
                var dir = panel.rotation * (a == 0 ? Vector3.right : a == 1 ? Vector3.up : Vector3.forward);
                if (Mathf.Abs(dir.x) > best) { best = Mathf.Abs(dir.x); axis = a; }
            }
            var s = panel.localScale;
            s[axis] *= k;
            panel.localScale = s;
            // Keep the middle of the panel where it was, in case it is not built around its own origin.
            var after = BoxOf(panel, out _);
            panel.position += new Vector3(box.center.x - after.center.x, 0f, 0f);
        }

        // A stretched piece's copy: straight along, and a hair off the original so their faces do not flicker.
        static Vector3 Stretched(Node n, float W, float L) => new Vector3(n.Side * (W + Nudge), Nudge, L);

        // The section is turned about the stock position of its processor trigger, so the processor's mouth stays where a
        // plain copy would put it and the furnace's swings out into line with it. That way nothing swings inward, toward
        // the ship. The angle is the one between the bay's axis and the line from the furnace to the processor.
        static Section MakeSection(int side, float W, float L, Dictionary<string, Transform> roots, Dictionary<Transform, Node> byT)
        {
            var sec = new Section { Offset = new Vector3(side * W, 0f, L) };
            Transform? furnace = null, processor = null;
            foreach (var path in SalvageSets)
            {
                var set = Resolve(roots, path);
                if (set == null || !byT.TryGetValue(set, out var n) || n.Side != side) continue;
                furnace = set.Find("Trigger_Furnace");
                processor = set.Find("Trigger_Processor");
            }
            float degrees = 0f;
            if (furnace != null && processor != null)
            {
                sec.Pivot = processor.position;
                var line = processor.position - furnace.position;
                degrees = -Mathf.Atan2(line.x, line.z) * Mathf.Rad2Deg;
            }
            else Plugin.Log.LogWarning($"[Bay] the {(side > 0 ? "left" : "right")} side's furnace and processor triggers were not found; its copied section is not turned.");
            sec.Rot = Quaternion.AngleAxis(degrees, Vector3.up);
            sec.Matrix = Matrix4x4.Translate(sec.Pivot + sec.Offset) * Matrix4x4.Rotate(sec.Rot) * Matrix4x4.Translate(-sec.Pivot);

            return sec;
        }

        // A scene's top-level object by name (GameObject.Find would also return a child of that name).
        static GameObject? FindRoot(string name)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var go in scene.GetRootGameObjects())
                    if (go.name == name && go.activeInHierarchy) return go;
            }
            return null;
        }

        static Transform? Resolve(Dictionary<string, Transform> roots, string path)
        {
            int slash = path.IndexOf('/');
            string root = slash < 0 ? path : path.Substring(0, slash);
            if (!roots.TryGetValue(root, out var t)) return null;
            return slash < 0 ? t : t.Find(path.Substring(slash + 1));
        }

        // Every object at a path; only the last name may be shared by several.
        static List<Transform> ResolveAll(Dictionary<string, Transform> roots, string path)
        {
            var found = new List<Transform>();
            int slash = path.LastIndexOf('/');
            if (slash < 0)
            {
                var one = Resolve(roots, path);
                if (one != null) found.Add(one);
                return found;
            }
            var parent = Resolve(roots, path.Substring(0, slash));
            if (parent == null) return found;
            string name = path.Substring(slash + 1);
            foreach (Transform child in parent)
                if (child.name == name) found.Add(child);
            return found;
        }

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (var p = t; p != null && parts.Count < 6; p = p.parent) parts.Add(p.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        // ---- measuring ----------------------------------------------------------------------------------------------
        // The world-space box of what a transform itself holds: its mesh and its colliders. A transform that holds
        // neither counts as the point it stands at.
        static void Measure(Node n)
        {
            var t = n.T;
            bool any = false;
            Bounds box = default;
            void Add(Bounds b)
            {
                if (b.size == Vector3.zero) return;
                if (any) box.Encapsulate(b); else { box = b; any = true; }
            }

            var mr = t.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                if (mr.isPartOfStaticBatch) { n.Batched = mr; Add(mr.bounds); }
                else
                {
                    var b = mr.bounds;
                    if (b.size == Vector3.zero)
                    {
                        var mf = t.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null) b = ToWorld(t, mf.sharedMesh.bounds.center, mf.sharedMesh.bounds.extents);
                    }
                    Add(b);
                }
            }
            foreach (var c in t.GetComponents<Collider>())
            {
                if (c is BoxCollider bc) Add(ToWorld(t, bc.center, bc.size * 0.5f));
                else if (c is MeshCollider mc && mc.sharedMesh != null) Add(ToWorld(t, mc.sharedMesh.bounds.center, mc.sharedMesh.bounds.extents));
                else Add(c.bounds);
            }
            n.Box = any ? box : new Bounds(t.position, Vector3.zero);
        }

        // The same measure for other classes: the box, and the renderer if it is statically batched.
        internal static Bounds BoxOf(Transform t, out MeshRenderer? batched)
        {
            var n = new Node { T = t };
            Measure(n);
            batched = n.Batched;
            return n.Box;
        }

        static Bounds ToWorld(Transform t, Vector3 center, Vector3 extents)
        {
            var m = t.localToWorldMatrix;
            var ax = m.MultiplyVector(new Vector3(extents.x, 0f, 0f));
            var ay = m.MultiplyVector(new Vector3(0f, extents.y, 0f));
            var az = m.MultiplyVector(new Vector3(0f, 0f, extents.z));
            var half = new Vector3(Mathf.Abs(ax.x) + Mathf.Abs(ay.x) + Mathf.Abs(az.x),
                                   Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y),
                                   Mathf.Abs(ax.z) + Mathf.Abs(ay.z) + Mathf.Abs(az.z));
            return new Bounds(m.MultiplyPoint3x4(center), half * 2f);
        }

        // ---- moving and copying -------------------------------------------------------------------------------------
        static void Place(Transform t, Vector3 target)
        {
            if ((t.position - target).sqrMagnitude < 1e-8f) return;
            t.position = target;
            var rb = t.GetComponent<Rigidbody>();
            if (rb != null) rb.position = target;
        }

        // A copy of one object without its children (they are copied, or not, in their own right). An object with no
        // children is cloned outright ("exact"). One with children would bring them all along, so only what matters is
        // rebuilt: its solid colliders and its mesh. Returns null when there is nothing worth copying.
        static Transform? CopyPiece(Node n, out bool exact)
        {
            exact = false;
            var t = n.T;
            var src = t.gameObject;
            var mr = t.GetComponent<MeshRenderer>();
            var mf = t.GetComponent<MeshFilter>();
            bool ownMesh = n.Batched == null && mr != null && mf != null && mf.sharedMesh != null;
            GameObject go;

            if (t.childCount == 0)
            {
                if (!ownMesh && t.GetComponent<Collider>() == null && t.GetComponent<Light>() == null) return null;
                go = Object.Instantiate(src, t.parent);
                Silence(go.transform);
                exact = true;
            }
            else
            {
                bool solid = false;
                foreach (var c in t.GetComponents<Collider>()) if (!c.isTrigger && c.enabled) solid = true;
                if (!solid && !ownMesh) return null;

                go = new GameObject();
                go.layer = src.layer;
                go.tag = src.tag;
                go.transform.SetParent(t.parent, false);
                go.transform.localPosition = t.localPosition;
                go.transform.localRotation = t.localRotation;
                go.transform.localScale = t.localScale;
                foreach (var c in t.GetComponents<Collider>())
                {
                    if (c.isTrigger || !c.enabled) continue;
                    if (c is BoxCollider b)
                    {
                        var nb = go.AddComponent<BoxCollider>();
                        nb.center = b.center; nb.size = b.size; nb.sharedMaterial = b.sharedMaterial;
                    }
                    else if (c is MeshCollider m && m.sharedMesh != null)
                    {
                        var nm = go.AddComponent<MeshCollider>();
                        nm.sharedMesh = m.sharedMesh; nm.convex = m.convex; nm.sharedMaterial = m.sharedMaterial;
                    }
                }
                if (ownMesh)
                {
                    go.AddComponent<MeshFilter>().sharedMesh = mf!.sharedMesh;
                    var nr = go.AddComponent<MeshRenderer>();
                    nr.sharedMaterials = mr!.sharedMaterials;
                    nr.shadowCastingMode = mr.shadowCastingMode;
                    nr.receiveShadows = mr.receiveShadows;
                    nr.enabled = mr.enabled;
                }
            }
            go.name = t.name + " (BiggerBays)";
            return go.transform;
        }

        // ---- copies that follow their originals ---------------------------------------------------------------------
        // A copy must not run anything of its own that the original already runs for the bay: its animations and
        // timelines (it follows the original instead) and the scripts that fire when a shift starts or an object is
        // switched on (they would fire a second time).
        static bool IsDriver(Behaviour b)
        {
            if (b is Animator) return true;
            string name = b.GetType().Name;
            return name == "PlayableDirector" || name == "TriggerableOnShiftStart" || name == "TriggerableOnEnable" || name == "TriggerableComponent";
        }

        static void Silence(Transform c)
        {
            foreach (var b in c.GetComponents<Behaviour>())
                if (b != null && IsDriver(b)) b.enabled = false;
        }

        // For the pieces of a two-sided set that belong to the other side's copy.
        static void SwitchOff(Transform c)
        {
            foreach (var r in c.GetComponents<Renderer>()) r.enabled = false;
            foreach (var col in c.GetComponents<Collider>()) col.enabled = false;
            foreach (var b in c.GetComponents<Behaviour>()) if (b != null) b.enabled = false;
        }

        // Ties a copy to its original. Both have the same components in the same order.
        static Puppet Pair(Transform o, Transform c, bool batched, bool components)
        {
            var p = new Puppet { O = o, C = c, CBase = c.localPosition };
            var oc = o.GetComponents<Component>();
            var cc = c.GetComponents<Component>();
            if (components && oc.Length == cc.Length)
            {
                var ol = new List<Light>(); var cl = new List<Light>();
                var or = new List<Renderer>(); var cr = new List<Renderer>();
                var ob = new List<Behaviour>(); var cb = new List<Behaviour>();
                var ocol = new List<Collider>(); var ccol = new List<Collider>();
                var ofx = new List<VisualEffect>(); var cfx = new List<VisualEffect>();
                for (int i = 0; i < oc.Length; i++)
                {
                    if (oc[i] == null || cc[i] == null || oc[i].GetType() != cc[i].GetType()) continue;
                    if (oc[i] is Light l) { ol.Add(l); cl.Add((Light)cc[i]); }
                    else if (oc[i] is Renderer r)
                    {
                        // The copy of a batched renderer would draw on top of the original; BatchedRedraw draws it instead.
                        if (batched && r is MeshRenderer) { var hidden = (Renderer)cc[i]; hidden.enabled = false; hidden.forceRenderingOff = true; }
                        else { or.Add(r); cr.Add((Renderer)cc[i]); }
                    }
                    else if (oc[i] is Collider col) { ocol.Add(col); ccol.Add((Collider)cc[i]); }
                    else if (oc[i] is Behaviour b && !IsDriver(b))
                    {
                        ob.Add(b); cb.Add((Behaviour)cc[i]);
                        if (b is VisualEffect fx) { ofx.Add(fx); cfx.Add((VisualEffect)cc[i]); }
                    }
                }
                p.OLights = ol.ToArray(); p.CLights = cl.ToArray();
                p.ORenderers = or.ToArray(); p.CRenderers = cr.ToArray();
                p.OBehaviours = ob.ToArray(); p.CBehaviours = cb.ToArray();
                p.OColliders = ocol.ToArray(); p.CColliders = ccol.ToArray();
                p.OEffects = ofx.ToArray(); p.CEffects = cfx.ToArray();
            }
            sPuppets.Add(p);
            return p;
        }

        // Called every frame, after animations and timelines have run and after Tick. Whatever the game has done to an
        // original - switched it on or off, moved it, lit it, recolored it - is done to its copy.
        internal static void Mirror()
        {
            for (int i = sPuppets.Count - 1; i >= 0; i--)
            {
                var p = sPuppets[i];
                if (p.O == null || p.C == null) { sPuppets.RemoveAt(i); continue; }

                bool on = p.O.gameObject.activeSelf;
                if (p.C.gameObject.activeSelf != on) p.C.gameObject.SetActive(on);
                if (!p.C.gameObject.activeInHierarchy) continue;

                if (p.Transforms)
                {
                    var want = p.CBase + (p.O.localPosition - p.OPlaced);
                    if (p.C.localPosition != want) p.C.localPosition = want;
                    var rot = p.O.localRotation;
                    if (p.C.localRotation != rot) p.C.localRotation = rot;
                    var scale = p.O.localScale;
                    if (p.C.localScale != scale) p.C.localScale = scale;
                }

                for (int k = 0; k < p.OLights.Length; k++)
                {
                    var o = p.OLights[k]; var c = p.CLights[k];
                    if (o == null || c == null) continue;
                    if (c.enabled != o.enabled) c.enabled = o.enabled;
                    if (c.intensity != o.intensity) c.intensity = o.intensity;
                    if (c.range != o.range) c.range = o.range;
                    if (c.color != o.color) c.color = o.color;
                    if (c.type == LightType.Spot && c.spotAngle != o.spotAngle) c.spotAngle = o.spotAngle;
                }
                for (int k = 0; k < p.ORenderers.Length; k++)
                {
                    var o = p.ORenderers[k]; var c = p.CRenderers[k];
                    if (o == null || c == null) continue;
                    if (c.enabled != o.enabled) c.enabled = o.enabled;
                    if (!o.enabled) continue;
                    if (c.sharedMaterial != o.sharedMaterial) c.sharedMaterials = o.sharedMaterials;
                    if (o.HasPropertyBlock()) { o.GetPropertyBlock(sBlock); c.SetPropertyBlock(sBlock); }
                    else if (c.HasPropertyBlock()) c.SetPropertyBlock(null);
                }
                for (int k = 0; k < p.OBehaviours.Length; k++)
                {
                    var o = p.OBehaviours[k]; var c = p.CBehaviours[k];
                    if (o != null && c != null && c.enabled != o.enabled) c.enabled = o.enabled;
                }
                for (int k = 0; k < p.OColliders.Length; k++)
                {
                    var o = p.OColliders[k]; var c = p.CColliders[k];
                    if (o != null && c != null && c.enabled != o.enabled) c.enabled = o.enabled;
                }
                // A particle effect is started and stopped by events the copy never receives. Twice a second: if the
                // original has particles and the copy has none, the copy is started; the other way round, stopped.
                if (p.OEffects.Length > 0 && Time.unscaledTime >= p.NextEffectCheck)
                {
                    p.NextEffectCheck = Time.unscaledTime + 0.5f;
                    for (int k = 0; k < p.OEffects.Length; k++)
                    {
                        var o = p.OEffects[k]; var c = p.CEffects[k];
                        if (o == null || c == null || !c.isActiveAndEnabled) continue;
                        bool live = o.aliveParticleCount > 0, copyLive = c.aliveParticleCount > 0;
                        if (live && !copyLive) c.Play();
                        else if (!live && copyLive) c.Stop();
                    }
                }
            }
        }

        // ---- animated objects ---------------------------------------------------------------------------------------
        // An animation that drives an object's position writes its local position every frame, which would put a moved
        // object straight back. Any animated object now standing somewhere else relative to its parent is watched: when
        // its local position is no longer the one set here, the animation has written it, and the shift is added again.
        static void AddWatch(Transform t, Vector3 localStock)
        {
            var delta = t.localPosition - localStock;
            if (delta.sqrMagnitude < 1e-8f) return;
            sWatch.Add(new Watch { T = t, Delta = delta, Last = t.localPosition });
        }

        // Called every frame, after animations have run.
        internal static void Tick()
        {
            for (int i = sWatch.Count - 1; i >= 0; i--)
            {
                var w = sWatch[i];
                if (w.T == null) { sWatch.RemoveAt(i); continue; }
                var lp = w.T.localPosition;
                if ((lp - w.Last).sqrMagnitude < 1e-8f) continue;
                lp += w.Delta;
                w.T.localPosition = lp;
                w.Last = lp;
            }
        }

        // ---- playable area ------------------------------------------------------------------------------------------
        // The game counts the player and loose objects as in bounds while they are within a set distance of any of a list
        // of points spread through the bay. Points are added where the bay has grown; none are moved, so nowhere that
        // was in bounds stops being so. The list lives in two places: on the PlayableArea object (used for the player)
        // and in a buffer copied from it when the level loaded (used for loose objects).
        static int ExtendPlayableArea(float W, float L, float cx, float strip, float cut)
        {
            var area = Object.FindObjectOfType<PlayableArea>();
            if (area == null) { Plugin.Log.LogWarning("[Bay] no playable area in this level; its limits are unchanged."); return 0; }

            var add = new List<Vector3>();
            foreach (var node in area.PlayableNodes)
            {
                if (node == null) continue;
                var p = node.position;
                float dx = p.x - cx;
                float sx = W <= 0f ? 0f : dx > strip ? W : dx < -strip ? -W : 0f;
                bool along = L > 0f && p.z > cut - 30f;
                if (sx != 0f) add.Add(p + new Vector3(sx, 0f, 0f));
                if (along) add.Add(p + new Vector3(0f, 0f, L));
                if (along && sx != 0f) add.Add(p + new Vector3(sx, 0f, L));
            }
            if (add.Count == 0) return 0;

            var world = World.DefaultGameObjectInjectionWorld;
            bool haveBuffer = false;
            DynamicBuffer<PlayableAreaNodeElement> buffer = default;
            if (world != null)
            {
                var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SingletonPlayableArea>(), ComponentType.ReadOnly<PlayableAreaNodeElement>());
                if (query.CalculateEntityCount() == 1)
                {
                    buffer = world.EntityManager.GetBuffer<PlayableAreaNodeElement>(query.GetSingletonEntity());
                    haveBuffer = true;
                }
                query.Dispose();
            }
            if (!haveBuffer) Plugin.Log.LogWarning("[Bay] the playable area's object list was not found; loose objects keep the stock limits.");

            foreach (var p in add)
            {
                var go = new GameObject("Playable Area Node (BiggerBays)");
                go.transform.SetParent(area.transform, false);
                go.transform.position = p;
                area.PlayableNodes.Add(go.transform);
                if (haveBuffer) buffer.Add(new PlayableAreaNodeElement { Position = (float3)p });
            }
            return add.Count;
        }
    }
}
