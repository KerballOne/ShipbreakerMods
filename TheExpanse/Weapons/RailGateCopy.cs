using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;

namespace TheExpanse
{
    // The village rail gate's own firing, copied and played at the rail gun.
    //
    // The gate (RailGateBehaviour) plays a sequence: steps at set times from the start, each calling an entry of its
    // event table (TriggerableUnityEventTableAction), whose UnityEvents switch the beam effects on and off (set up in
    // the editor: target object, method, argument). Here, once per level, that sequence is read: every step and every
    // call is logged. On firing, each object under PRF_Village/FX_Village that the calls touch is copied (whole, under
    // a rig placed like FX_Village), the rig is put at the muzzle and turned onto the ship's nose, and parented to the
    // muzzle so it moves with the ship; then the same calls are made on the copies. Calls on objects that were not
    // copied are skipped (the real gate is never touched).
    //
    // Two parts of the gate: FX_Gate_Near is the beam (placed so it leaves the muzzle along the nose; RailGunGlowSize
    // sizes only its glow); FX_Gate is the gate's big flash (3 km across, 290 m off the beam's start at the gate): it is
    // the blast, centered on the muzzle at RailGunBlastSize, and goes off first; the beam's calls follow
    // RailGunBeamDelay later.
    //
    // Timing: the gate charges for 30 s before it fires (its 'Start' at 0 s, the beam's 'Step1' with both effects
    // switched on at 30 s) and switches off at 90 s. The rail gun has its own charge, so every step up to and
    // including the firing one (the first that switches an effect on) is played at once, in order, and the steps after
    // it are spread over RailGunBeamSeconds.
    internal static class RailGateCopy
    {
        sealed class Call
        {
            internal Object Target = null!;
            internal string Method = "";
            internal PersistentListenerMode Mode;
            internal object? Argument;
        }

        sealed class Step
        {
            internal float At;
            internal string Key = "";
            internal List<Call> Calls = new List<Call>();
        }

        static readonly List<Step> sSteps = new List<Step>();
        static bool sPoseLogged;
        static bool sRead, sReadFailed;
        static Transform? sVillageFx;
        static GameObject? sRig;
        static readonly Dictionary<Transform, Transform> sCopies = new Dictionary<Transform, Transform>();   // top original -> copy
        static float sStarted;
        static int sNext, sFireStep = -1;
        static GameObject? sBlastRig;
        static readonly List<(float at, Call call)> sSchedule = new List<(float, Call)>();

        internal static float Duration => sSteps.Count > 0 ? sSteps[sSteps.Count - 1].At : 0f;

        internal static void Reset()
        {
            sSteps.Clear();
            sRead = sReadFailed = false;
            sVillageFx = null;
            Stop();
        }

        static void Stop()
        {
            if (sRig != null) Object.Destroy(sRig);
            sRig = null;
            if (sBlastRig != null) Object.Destroy(sBlastRig);
            sBlastRig = null;
            sSchedule.Clear();
            sCopies.Clear();
            sNext = 0;
        }

        // ---- reading the gate's sequence (once per level) ----------------------------------------------------------

        static bool Read()
        {
            if (sRead) return !sReadFailed;
            sRead = true;
            sReadFailed = true;
            RailGateBehaviour? gate = null;
            foreach (var g in Resources.FindObjectsOfTypeAll<RailGateBehaviour>())
                if (g != null && g.gameObject.scene.IsValid() && !Path(g.transform).Contains("_HAB")) { gate = g; break; }
            var village = SceneLookup.Root("PRF_Village");
            sVillageFx = village != null ? village.Find("FX_Village") : null;
            if (gate == null || sVillageFx == null)
            {
                Plugin.Log.LogWarning($"[Weapons] rail gate copy: {(gate == null ? "no RailGateBehaviour" : "no PRF_Village/FX_Village")} found.");
                return false;
            }
            var table = AccessTools.Field(typeof(RailGateBehaviour), "m_EventTable").GetValue(gate) as TriggerableUnityEventTableAction;
            var steps = AccessTools.Field(typeof(RailGateBehaviour), "m_SequenceSteps").GetValue(gate) as IList;
            var entries = table != null ? AccessTools.Field(typeof(TriggerableUnityEventTableAction), "m_UnityEventTable").GetValue(table) as IList : null;
            if (steps == null || entries == null) { Plugin.Log.LogWarning("[Weapons] rail gate copy: its sequence could not be read."); return false; }

            var events = new Dictionary<string, UnityEvent>();
            foreach (var entry in entries)
            {
                if (entry == null) continue;
                var t = entry.GetType();
                var key = AccessTools.Field(t, "Key").GetValue(entry) as string;
                var ev = AccessTools.Field(t, "UnityEvent").GetValue(entry) as UnityEvent;
                if (key != null && ev != null) events[key] = ev;
            }
            var log = new StringBuilder($"[Weapons] rail gate '{Path(gate.transform)}': {steps.Count} step(s), {events.Count} event(s):");
            foreach (var step in steps)
            {
                var st = step.GetType();
                var s = new Step
                {
                    At = (float)AccessTools.Field(st, "SequenceStepDelay").GetValue(step),
                    Key = AccessTools.Field(st, "SequenceStepEventKey").GetValue(step) as string ?? "",
                };
                log.Append($"\n[Weapons]   at {s.At:0.00} s '{s.Key}'");
                if (events.TryGetValue(s.Key, out var ev)) ReadCalls(ev, s.Calls, log);
                sSteps.Add(s);
            }
            sSteps.Sort((a, b) => a.At.CompareTo(b.At));
            sFireStep = sSteps.FindIndex(st => st.Calls.Exists(c => c.Method == "SetActive" && c.Argument is bool on && on));
            if (sFireStep < 0) sFireStep = sSteps.Count - 1;
            log.Append($"\n[Weapons]   the beam fires at step '{(sFireStep >= 0 ? sSteps[sFireStep].Key : "-")}': played at once on a shot.");
            Plugin.Log.LogInfo(log.ToString());
            sReadFailed = false;
            return true;
        }

        static void ReadCalls(UnityEventBase ev, List<Call> calls, StringBuilder log)
        {
            var group = AccessTools.Field(typeof(UnityEventBase), "m_PersistentCalls").GetValue(ev);
            if (group == null) return;
            if (!(AccessTools.Field(group.GetType(), "m_Calls").GetValue(group) is IList list)) return;
            foreach (var pc in list)
            {
                var t = pc.GetType();
                var target = AccessTools.Field(t, "m_Target").GetValue(pc) as Object;
                string method = AccessTools.Field(t, "m_MethodName").GetValue(pc) as string ?? "";
                var mode = (PersistentListenerMode)AccessTools.Field(t, "m_Mode").GetValue(pc);
                var args = AccessTools.Field(t, "m_Arguments").GetValue(pc);
                object? arg = null;
                if (args != null)
                {
                    var at = args.GetType();
                    arg = mode switch
                    {
                        PersistentListenerMode.Bool => AccessTools.Field(at, "m_BoolArgument").GetValue(args),
                        PersistentListenerMode.Int => AccessTools.Field(at, "m_IntArgument").GetValue(args),
                        PersistentListenerMode.Float => AccessTools.Field(at, "m_FloatArgument").GetValue(args),
                        PersistentListenerMode.String => AccessTools.Field(at, "m_StringArgument").GetValue(args),
                        PersistentListenerMode.Object => AccessTools.Field(at, "m_ObjectArgument").GetValue(args),
                        _ => null,
                    };
                }
                var where = TargetTransform(target);
                bool ours = where != null && sVillageFx != null && where.IsChildOf(sVillageFx) && where != sVillageFx;
                log.Append($"\n[Weapons]     {(target != null ? target.GetType().Name : "null")} '{(where != null ? Path(where) : "-")}'.{method}({arg}) " +
                           (ours ? "" : "(not under FX_Village: skipped)"));
                if (target != null && ours) calls.Add(new Call { Target = target, Method = method, Mode = mode, Argument = arg });
            }
        }

        static Transform? TargetTransform(Object? target) => target is Component c ? c.transform : target is GameObject g ? g.transform : null;

        // ---- playing it at the muzzle ------------------------------------------------------------------------------

        // Copies what the calls touch and starts the sequence there. Scale: 1 is the gate's own size.
        internal static bool Fire(Transform? parent, Vector3 at, Vector3 dir, float scale, float blastScale, float beamDelay)
        {
            if (!Read() || sVillageFx == null) return false;
            Stop();
            sRig = new GameObject("TheExpanse_RailGateCopy");
            sRig.transform.SetPositionAndRotation(sVillageFx.position, sVillageFx.rotation);
            sRig.transform.localScale = sVillageFx.lossyScale;
            foreach (var step in sSteps)
                foreach (var call in step.Calls)
                {
                    var top = Top(TargetTransform(call.Target)!);
                    if (top == null || sCopies.ContainsKey(top)) continue;
                    var copy = Object.Instantiate(top.gameObject, sRig.transform, true);     // same world pose under the rig
                    copy.name = top.name + "_Copy";
                    copy.SetActive(top.gameObject.activeSelf);
                    Quiet(copy);
                    sCopies[top] = copy.transform;
                }
            // the beam's origin is FX_Gate_Near if copied (else the first copy); its axis is the copy's forward
            Transform? origin = null;
            foreach (var pair in sCopies) if (pair.Key.name == "FX_Gate_Near") origin = pair.Value;
            if (origin == null) foreach (var pair in sCopies) { origin = pair.Value; break; }
            if (origin != null)
            {
                // turn the rig so the origin's forward lies along the nose, then move it so the origin is at the muzzle
                var turn = Quaternion.FromToRotation(origin.forward, dir);
                sRig.transform.rotation = turn * sRig.transform.rotation;
                sRig.transform.localScale *= scale;
                sRig.transform.position += at - origin.position;
            }
            // the gate's flash: its own rig, centered on the muzzle, turned the same way, at the blast's size
            foreach (var pair in sCopies)
            {
                if (pair.Key.name != "FX_Gate") continue;
                sBlastRig = new GameObject("TheExpanse_RailGunBlast");
                sBlastRig.transform.SetPositionAndRotation(at, sRig.transform.rotation);
                sBlastRig.transform.localScale = Vector3.one * Mathf.Max(0.001f, blastScale);
                var flash = pair.Value;
                // its ring faces along the nose: the flash's ring axis (RailGunBlastAxis, its own x, y or z) turned onto it
                var original = pair.Key.rotation;
                var ringAxis = BlastAxis();
                var flashRotation = Quaternion.FromToRotation(original * ringAxis, dir) * original;
                flash.SetParent(sBlastRig.transform, false);
                flash.localPosition = Vector3.zero;
                flash.rotation = flashRotation;
                flash.localScale = pair.Key.lossyScale;
                // played faster (RailGunBlastSpeed)
                float speed = Mathf.Max(0.1f, Plugin.WeaponsRailGunBlastSpeed.Value);
                foreach (var effect in flash.GetComponentsInChildren<UnityEngine.VFX.VisualEffect>(true)) effect.playRate = speed;
                if (parent != null) sBlastRig.transform.SetParent(parent, true);
            }
            if (parent != null) sRig.transform.SetParent(parent, true);        // goes with the ship
            sStarted = Time.time;
            sNext = 0;
            // every call's time on this shot: the flash's at once, the beam's (FX_Gate_Near) after the delay, the
            // steps after the firing one over the hold time
            sSchedule.Clear();
            for (int i = 0; i < sSteps.Count; i++)
                foreach (var call in sSteps[i].Calls)
                {
                    float t = ShotTime(i);
                    var top = Top(TargetTransform(call.Target)!);
                    if (i <= sFireStep && top != null && top.name == "FX_Gate_Near") t = Mathf.Max(t, beamDelay);
                    else if (i > sFireStep) t += beamDelay;
                    int k = sSchedule.Count;
                    while (k > 0 && sSchedule[k - 1].at > t) k--;          // stable insert
                    sSchedule.Insert(k, (t, call));
                }
            if (!sPoseLogged)
            {
                sPoseLogged = true;
                foreach (var top in sCopies.Keys)
                {
                    var r = top.GetComponentInChildren<Renderer>(true);
                    Plugin.Log.LogInfo($"[Weapons] rail gate '{top.name}': at {top.position}, forward {top.forward}, up {top.up}, scale {top.lossyScale}, " +
                                       $"bounds center {(r != null ? r.bounds.center.ToString() : "-")} size {(r != null ? r.bounds.size.ToString() : "-")}.");
                }
            }
            Plugin.Log.LogInfo($"[Weapons] rail gate copy fired: {sCopies.Count} object(s) copied ({string.Join(", ", NamesOf())}), " +
                               $"origin '{(origin != null ? origin.name : "none")}', scale {scale}, sequence {Duration:0.0} s.");
            Tick();
            return true;
        }

        static bool sQuietLogged;

        // Takes out what lights up the whole view rather than the beam: lights and post-processing volumes (with the
        // rail gun's copy near the camera, the gate's own glow would cover the screen). Lists the copy's parts once.
        static void Quiet(GameObject copy)
        {
            var parts = new List<string>();
            int off = 0;
            foreach (var c in copy.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform) continue;
                bool quiet = c is Light || c is UnityEngine.Rendering.Volume || c is UnityEngine.Rendering.HighDefinition.HDAdditionalLightData
                             || c.GetType().Name.Contains("LensFlare");
                if (quiet && c is Behaviour b) { b.enabled = false; off++; }
                if (quiet && c is Light l) l.enabled = false;
                parts.Add($"{c.GetType().Name} '{c.name}'{(quiet ? " (off)" : "")}");
                if (c is UnityEngine.VFX.VisualEffect vfx && vfx.visualEffectAsset != null)
                {
                    var exposed = new List<UnityEngine.VFX.VFXExposedProperty>();
                    vfx.visualEffectAsset.GetExposedProperties(exposed);
                    parts.Add($"  effect '{vfx.visualEffectAsset.name}' properties: " + string.Join(", ", exposed.ConvertAll(p => $"{p.name} ({p.type.Name})")));
                }
            }
            if (sQuietLogged && copy.name != "FX_Gate_Near_Copy") return;
            Plugin.Log.LogInfo($"[Weapons] rail gate copy '{copy.name}': {off} light(s)/volume(s) switched off; parts: " + string.Join("; ", parts));
            if (copy.name == "FX_Gate_Near_Copy") sQuietLogged = true;
        }

        static IEnumerable<string> NamesOf() { foreach (var k in sCopies.Keys) yield return k.name; }

        // The child of FX_Village holding a target.
        static Transform? Top(Transform t)
        {
            while (t != null && t.parent != sVillageFx) t = t.parent;
            return t;
        }

        // Every frame: the steps due.
        internal static void Tick()
        {
            if (sRig == null) return;
            float elapsed = Time.time - sStarted;
            while (sNext < sSchedule.Count && elapsed >= sSchedule[sNext].at)
            {
                Invoke(sSchedule[sNext].call);
                sNext++;
            }
            if (sNext >= sSchedule.Count && elapsed > Hold + 1f) Stop();
        }

        static float Hold => Mathf.Max(0.5f, Plugin.WeaponsRailGunBeamSeconds.Value);

        static Vector3 BlastAxis()
        {
            string a = (Plugin.WeaponsRailGunBlastAxis.Value ?? "y").Trim().ToLowerInvariant();
            bool minus = a.StartsWith("-");
            var v = a.EndsWith("x") ? Vector3.right : a.EndsWith("z") ? Vector3.forward : Vector3.up;
            return minus ? -v : v;
        }

        // A step's time on a shot: up to the firing step at once; after it, spread over the hold time.
        static float ShotTime(int i)
        {
            if (i <= sFireStep) return 0f;
            float fire = sSteps[sFireStep].At, last = sSteps[sSteps.Count - 1].At;
            return last > fire ? Hold * (sSteps[i].At - fire) / (last - fire) : Hold;
        }

        static void Invoke(Call call)
        {
            var original = TargetTransform(call.Target);
            var top = original != null ? Top(original) : null;
            if (original == null || top == null || !sCopies.TryGetValue(top, out var copyTop)) return;
            var path = RelativePath(top, original);
            var copyT = path.Length == 0 ? copyTop : copyTop.Find(path);
            if (copyT == null) return;
            object? target = call.Target is GameObject ? copyT.gameObject : (object?)copyT.GetComponent(call.Target.GetType());
            if (target == null) return;
            System.Type[] types = call.Mode switch
            {
                PersistentListenerMode.Void => new System.Type[0],
                PersistentListenerMode.Bool => new[] { typeof(bool) },
                PersistentListenerMode.Int => new[] { typeof(int) },
                PersistentListenerMode.Float => new[] { typeof(float) },
                PersistentListenerMode.String => new[] { typeof(string) },
                PersistentListenerMode.Object => new[] { call.Argument != null ? call.Argument.GetType() : typeof(Object) },
                _ => new System.Type[0],
            };
            var method = UnityEventBase.GetValidMethodInfo(target, call.Method, types);
            if (method == null) { Plugin.Log.LogWarning($"[Weapons] rail gate copy: no {call.Method} on {target.GetType().Name}."); return; }
            try { method.Invoke(target, types.Length == 0 ? null : new[] { call.Argument }); }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[Weapons] rail gate copy: {call.Method} failed: {ex.InnerException?.Message ?? ex.Message}"); }
        }

        static string RelativePath(Transform top, Transform t)
        {
            var names = new List<string>();
            for (; t != null && t != top; t = t.parent) names.Add(t.name);
            names.Reverse();
            return string.Join("/", names);
        }

        static string Path(Transform t)
        {
            var names = new List<string>();
            for (int i = 0; t != null && i < 6; t = t.parent, i++) names.Add(t.name);
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
