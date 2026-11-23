using BBI.Unity.Game;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace TheExpanse
{
    // Testing aid for the ghost ship additions, off unless [Debug] TestTools is on: a line on screen with the nodes left,
    // and a key that destroys half of them, so the late-haunting behavior and the last node can be reached quickly.
    //
    // A node is destroyed as if it had gone into the furnace, by the game's own call for that.
    internal static class NodeTestTools
    {
        static GUIStyle? sStyle;

        static bool Active => Plugin.TestTools.Value && Plugin.Enabled.Value && PartSeparation.CustomShip;

        internal static void Tick()
        {
            if (!Active || !Plugin.KillHalfNodesKey.Value.IsDown()) return;
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;
            var em = world.EntityManager;

            GhostNodes.Refresh();
            int alive = GhostNodes.Entities.Count;
            if (alive == 0) { Plugin.Log.LogInfo("[Test] no nodes left to destroy."); return; }
            int want = (alive + 1) / 2, hit = 0, missed = 0;
            var nodes = GhostNodes.Entities.ToArray();
            for (int i = 0; i < nodes.Length && hit < want; i++)
            {
                if (Break(em, nodes[i])) hit++;
                else missed++;
            }
            Plugin.Log.LogInfo($"[Test] destroying {hit} of {alive} node(s)" + (missed > 0 ? $"; {missed} could not be damaged." : "."));
        }

        // As if the node had gone into the furnace: the game's own route for a destroyed part.
        static bool Break(EntityManager em, Entity node)
        {
            if (!em.Exists(node)) return false;
            var commands = new EntityCommandBuffer(Allocator.Temp);
            try
            {
                SalvageableUtility.MarkAsDestroyed(commands, em, node, SalvagedBy.Furnace);
                commands.Playback(em);
            }
            finally { commands.Dispose(); }
            // Marking it destroyed pays it out and takes it off the scanner, but the entity stays and the game's haunting
            // still counts it. Taking away what makes it a node finishes the job, for the game's count and ours.
            if (em.Exists(node) && em.HasComponent<HauntingCauser>(node)) em.RemoveComponent<HauntingCauser>(node);
            return !em.Exists(node) || !em.HasComponent<HauntingCauser>(node);
        }

        internal static void Draw()
        {
            if (!Active || GhostNodes.Original <= 0f) return;
            sStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            string text = $"Nodes alive: {GhostNodes.Entities.Count}   ({GhostNodes.ShareLeft * 100f:0}% of the haunting)   " +
                          $"{Plugin.KillHalfNodesKey.Value}: destroy half";
            var area = new Rect(20, 20, 900, 32);
            sStyle.normal.textColor = Color.black;
            GUI.Label(new Rect(area.x + 2, area.y + 2, area.width, area.height), text, sStyle);
            sStyle.normal.textColor = new Color(0.6f, 0.85f, 1f);
            GUI.Label(area, text, sStyle);
        }
    }
}
