using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GameMap = FFXIVClientStructs.FFXIV.Client.Game.UI.Map;

namespace Beeline;

/// <summary>A quest (or other game) marker: exact world position, area radius, and the NPC/object it points at.</summary>
internal readonly record struct MarkerHit(Vector3 Position, float Radius, uint DataId, string Label);

/// <summary>
/// Reads the markers the game itself draws on the map (active quests, new quests, levequests, guildleves,
/// Triple Triad, special NPCs...). Each comes with its exact 3D position, unlike the map cursor (0.1 = ~5 yalms).
/// </summary>
internal static unsafe class QuestMarkers
{
    /// <summary>How close the cursor must be to a marker icon to pick it (yalms).</summary>
    /// <summary>Smallest icon hit radius (yalms); the real one follows the map zoom, see <see cref="Find"/>.</summary>
    private const float MinIconWithin = 10f;

    /// <param name="iconWithin">How far from an icon the cursor can be and still be "on" it (yalms): an icon covers more
    /// ground on a zoomed-out map.</param>
    public static MarkerHit? Find(uint territory, Vector2 cursor, string tooltip, float iconWithin)
    {
        var IconWithin = MathF.Max(MinIconWithin, iconWithin);
        var map = GameMap.Instance();
        if (map == null)
            return null;

        MarkerHit? best = null;
        var bestScore = float.MaxValue;
        var diagnose = Plugin.Config_LogPresses; // only build the diagnostic list when it will be shown
        var nearest = new List<(float Dist, string Text)>();
        var list = "";

        void Consider(MarkerInfo* info)
        {
            if (info == null)
                return;
            var label = info->Label.ToString();
            foreach (var d in info->MarkerData.AsSpan())
            {
                var dist = Vector2.Distance(new Vector2(d.Position.X, d.Position.Z), cursor);
                if (diagnose)
                    nearest.Add((d.TerritoryTypeId == territory ? dist : 99999,
                    $"{list} \"{label}\" terr {d.TerritoryTypeId} at {d.Position} radius {d.Radius:0} id {d.DataId} type {d.MarkerType} icon {d.IconId}: {dist:0} y"));
                if (d.TerritoryTypeId != territory)
                    continue;
                var name = d.TooltipString != null ? d.TooltipString->ToString() : "";
                // The map tooltip names this marker (e.g. "Lv. 10 Stadium Envy"): it wins whatever the
                // cursor position says (that position is stale until the cursor moves).
                var named = !string.IsNullOrEmpty(tooltip)
                            && ((label.Length > 0 && tooltip.Contains(label)) || (name.Length > 0 && tooltip.Contains(name)));
                // Icon under the cursor, or cursor inside a quest area circle.
                var inside = dist <= IconWithin || (d.Radius > IconWithin && dist <= d.Radius);
                if (!inside && !named)
                    continue;
                // Named markers first, then icons right under the cursor, then big areas that merely contain it.
                var score = (named ? 0 : 100000) + (d.Radius > IconWithin ? dist + 1000 : dist);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = new MarkerHit(d.Position, d.Radius, d.DataId, string.IsNullOrEmpty(name) ? label : name);
                }
            }
        }

        list = "quest";
        foreach (ref var m in map->QuestMarkers)
            Consider((MarkerInfo*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref m));
        list = "leve";
        foreach (ref var m in map->LevequestMarkers)
            Consider((MarkerInfo*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref m));
        list = "active leve";
        Consider(&map->ActiveLevequestMarker);
        list = "other";
        foreach (var markerList in new[] { &map->UnacceptedQuestMarkers, &map->GuildLeveAssignmentMarkers, &map->GuildOrderGuideMarkers,
                                     &map->TripleTriadMarkers, &map->CustomTalkMarkers, &map->GemstoneTraderMarkers })
            foreach (ref var m in *markerList)
                Consider((MarkerInfo*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref m));

        if (best == null && diagnose)
        {
            Plugin.Info($"{Plugin.Tag} No game marker at the cursor. Nearest ones:");
            foreach (var n in nearest.OrderBy(n => n.Dist).Take(5))
                Plugin.Info($"{Plugin.Tag}   {n.Text}");
        }
        return best;
    }
}
