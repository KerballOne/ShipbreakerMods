using System;
using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace TheExpanse
{
    // Makes a room's own Pressurization Time and Decompression Time count.
    //
    // Every room definition (RoomContainerDefinition) carries those two times, but the game never reads them: its jobs step
    // every room at one global rate (RoomSettings; 5 s each way in the shipped game). Those jobs are Burst-compiled and
    // take a single rate for everything, so they cannot be made per-room by patching. Instead:
    //
    //   1. InitializeRoomSystem.TryInitializeRoom is where a room definition becomes a room entity; a postfix there
    //      remembers each room entity's two times.
    //   2. Every frame, after the game's jobs have run, anything in a controlled pressure change gets its pressure (0..1)
    //      rewritten to where it would be at its own pace. The game still starts and ends the change itself.
    //
    // Pressure lives in one of two places. Rooms that share air form a RoomGroup, whose AirPressure they all copy; a room
    // on its own keeps it in its AirPressure component (this is the usual case). Both are handled. A group takes the
    // longest time among its rooms. Times at or below the game's own are ignored, so a room left at the default (2 s)
    // behaves exactly as stock. A hull breach venting is not a controlled change and is left to the game.
    internal static class RoomPressure
    {
        struct Times { public float Pressurize, Decompress; public string Name; }
        struct Progress { public float Pressure; public int Direction; }

        static readonly Dictionary<Entity, Times> sRoomTimes = new Dictionary<Entity, Times>();
        static readonly Dictionary<Entity, Progress> sProgress = new Dictionary<Entity, Progress>();
        static readonly List<Entity> sDone = new List<Entity>();
        static EntityQuery sGroups, sRooms;
        static World? sQueryWorld;

        internal static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(InitializeRoomSystem), "TryInitializeRoom");
            if (target == null)
            {
                Plugin.Log.LogError("[RoomPressure] InitializeRoomSystem.TryInitializeRoom not found; rooms keep the game's own pressure timing.");
                return;
            }
            try { harmony.Patch(target, postfix: new HarmonyMethod(typeof(RoomPressure), nameof(RoomInitialized))); }
            catch (Exception ex) { Plugin.Log.LogError("[RoomPressure] could not hook room creation; rooms keep the game's own pressure timing: " + ex.Message); }
        }

        // A new level starts with new rooms.
        internal static void OnLevelLoadStart()
        {
            sRoomTimes.Clear();
            sProgress.Clear();
        }

        static void RoomInitialized(RoomContainerDefinition roomContainerDef, Entity roomEntity, bool __result)
        {
            try
            {
                if (!__result || roomContainerDef == null || roomEntity == Entity.Null) return;
                sRoomTimes[roomEntity] = new Times { Pressurize = roomContainerDef.PressurizationTime, Decompress = roomContainerDef.DecompressionTime, Name = roomContainerDef.name };
            }
            catch (Exception ex) { Plugin.Log.LogError("[RoomPressure] " + ex); }
        }

        // Called every frame from LateUpdate, i.e. after the game's simulation systems have run this frame's step.
        internal static void Tick()
        {
            if (!Plugin.Enabled.Value || !Plugin.RoomPressureTimes.Value || sRoomTimes.Count == 0) { sProgress.Clear(); return; }
            float dt = Time.deltaTime;
            if (dt <= 0f) return;                                       // paused: the game's jobs are not stepping either
            var world = World.DefaultGameObjectInjectionWorld;
            var settings = Main.Instance?.MainSettings?.RoomSettings;
            if (world == null || !world.IsCreated || settings == null) return;

            float stockPressurize = settings.AirlockPressurizeTime > 0f ? settings.AirlockPressurizeTime : 4f;
            float stockDecompress = settings.AirlockDecompressTime > 0f ? settings.AirlockDecompressTime : 4f;

            var em = world.EntityManager;
            if (sQueryWorld != world)
            {
                sGroups = em.CreateEntityQuery(ComponentType.ReadWrite<RoomGroup>(), ComponentType.ReadOnly<RoomConnection>());
                sRooms = em.CreateEntityQuery(ComponentType.ReadWrite<RoomComponent>(), ComponentType.ReadWrite<AirPressure>());
                sQueryWorld = world;
                sProgress.Clear();
            }

            // ---- groups of rooms that share air -----------------------------------------------------------------------
            using (var entities = sGroups.ToEntityArray(Allocator.TempJob))
            {
                foreach (var e in entities)
                {
                    var group = em.GetComponentData<RoomGroup>(e);
                    int direction = Math.Sign(group.AirPressureChangeRequest);
                    // Only a controlled change in a sealed group is ours; a breach or an open imbalanced door is the game's.
                    if (direction == 0 || !group.Sealed || group.HasUnsealedImbalancedOpening) { sProgress.Remove(e); continue; }

                    float stock = direction > 0 ? stockPressurize : stockDecompress;
                    float own = 0f; string name = "";
                    var rooms = em.GetBuffer<RoomConnection>(e);
                    for (int i = 0; i < rooms.Length; i++)
                    {
                        if (!sRoomTimes.TryGetValue(rooms[i].EntityConnection, out var t)) continue;
                        float time = direction > 0 ? t.Pressurize : t.Decompress;
                        if (time > own) { own = time; name = t.Name; }
                    }
                    if (own <= stock) { sProgress.Remove(e); continue; }        // not longer than stock: leave it alone

                    group.AirPressure = Step(e, group.AirPressure, direction, dt, stock, own, $"{rooms.Length} connected room(s), longest '{name}',");
                    em.SetComponentData(e, group);
                }
            }

            // ---- rooms on their own -----------------------------------------------------------------------------------
            using (var entities = sRooms.ToEntityArray(Allocator.TempJob))
            {
                foreach (var e in entities)
                {
                    var room = em.GetComponentData<RoomComponent>(e);
                    if (room.RoomGroupEntity != Entity.Null && em.Exists(room.RoomGroupEntity) && em.HasComponent<RoomGroup>(room.RoomGroupEntity)) continue;
                    int direction = Math.Sign(room.AirPressureChangeRequest);
                    if (direction == 0 || !room.Sealed || !sRoomTimes.TryGetValue(e, out var t)) { sProgress.Remove(e); continue; }

                    float stock = direction > 0 ? stockPressurize : stockDecompress;
                    float own = direction > 0 ? t.Pressurize : t.Decompress;
                    if (own <= stock) { sProgress.Remove(e); continue; }

                    float p = Step(e, room.AirPressure, direction, dt, stock, own, $"room '{t.Name}'");
                    room.AirPressure = p;
                    em.SetComponentData(e, room);
                    em.SetComponentData(e, new AirPressure { Value = p });
                }
            }

            // Forget things that no longer exist.
            if (sProgress.Count > 0)
            {
                sDone.Clear();
                foreach (var kv in sProgress) if (!em.Exists(kv.Key)) sDone.Add(kv.Key);
                foreach (var e in sDone) sProgress.Remove(e);
            }
        }

        // Our own progress for one group or room: advance it at its own pace and return the pressure to write back.
        static float Step(Entity e, float gamePressure, int direction, float dt, float stock, float own, string what)
        {
            if (!sProgress.TryGetValue(e, out var p) || p.Direction != direction)
            {
                // First frame of this change: start from where it was before the game's own step this frame.
                p = new Progress { Pressure = Mathf.Clamp01(gamePressure - direction * dt / stock), Direction = direction };
                Plugin.Log.LogInfo($"[RoomPressure] {what} {(direction > 0 ? "pressurizing" : "decompressing")} over {own:0.#} s (the game's own time is {stock:0.#} s).");
            }
            p.Pressure = Mathf.Clamp01(p.Pressure + direction * dt / own);
            sProgress[e] = p;
            return p.Pressure;
        }
    }
}
