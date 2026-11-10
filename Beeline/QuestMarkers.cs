using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GameMap = FFXIVClientStructs.FFXIV.Client.Game.UI.Map;

namespace Beeline;

/// <summary>
/// Something on the map with an exact position: area radius (0 for a single spot), the NPC/object it points at (0 if
/// none), and whether <see cref="Position"/>.Y is a real height (false: only X/Z are known).
/// </summary>
internal readonly record struct MarkerHit(Vector3 Position, float Radius, uint DataId, string Label, bool HasHeight = true);

/// <summary>
/// Finds what's under the map cursor, from everything the game knows the exact position of: quest, leve, guildleve,
/// Triple Triad, special NPC and housing markers, FATEs, party members, your flag, temporary markers (treasure maps...)
/// and the map's own icons (shops, guilds, exits...). Much more precise than the map cursor (0.1 = ~5 yalms).
/// </summary>
internal static unsafe class QuestMarkers
{
    /// <summary>Smallest icon hit radius (yalms); the real one follows the map zoom (<c>iconWithin</c>).</summary>
    private const float MinIconWithin = 10f;

    /// <param name="iconWithin">How far from an icon the cursor can be and still be "on" it (yalms): an icon covers more
    /// ground on a zoomed-out map.</param>
    public static MarkerHit? Find(uint territory, uint mapId, Vector2 cursor, string tooltip, float iconWithin)
    {
        var IconWithin = MathF.Max(MinIconWithin, iconWithin);
        MarkerHit? best = null;
        var bestScore = float.MaxValue;
        var diagnose = Plugin.Config_LogPresses; // only build the diagnostic list when it will be shown
        // Diagnostics: keep just the raw data of the nearest few; text is only built at the end, if needed.
        var nearest = new List<(float Dist, string List, string Label, Vector3 Pos, float Radius, uint DataId, uint Territory)>();
        var list = "";

        // One candidate: picked if the tooltip names it, or the cursor is on its icon / inside its area.
        void Add(Vector3 pos, float radius, uint dataId, string label, string name, bool hasHeight, uint inTerritory, bool exactName = false)
        {
            var dist = Vector2.Distance(new Vector2(pos.X, pos.Z), cursor);
            if (diagnose && inTerritory == territory)
            {
                nearest.Add((dist, list, label, pos, radius, dataId, inTerritory));
                if (nearest.Count > 64) // keep it small: drop the farthest
                {
                    nearest.Sort((a, b) => a.Dist.CompareTo(b.Dist));
                    nearest.RemoveRange(5, nearest.Count - 5);
                }
            }
            if (inTerritory != territory)
                return;
            // The map tooltip names it (e.g. "Lv. 10 Stadium Envy"): it wins whatever the cursor position says
            // (that position can be stale until the cursor moves).
            // Short names (map icons like "Inn") must match the whole tooltip, not just appear somewhere in it.
            var named = !string.IsNullOrEmpty(tooltip) && (exactName
                ? name.Length > 0 && tooltip.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)
                : (label.Length > 0 && tooltip.Contains(label)) || (name.Length > 0 && tooltip.Contains(name)));
            var inside = dist <= IconWithin || (radius > IconWithin && dist <= radius);
            if (!inside && !named)
                return;
            // Named first (nearest of that name), then icons right under the cursor, then big areas that merely contain it.
            var score = (named ? 0 : 100000) + (radius > IconWithin ? dist + 1000 : dist);
            if (score < bestScore)
            {
                bestScore = score;
                best = new MarkerHit(pos, radius, dataId, string.IsNullOrEmpty(name) ? label : name, hasHeight);
            }
        }

        // ---- The game's marker lists (quests, leves, housing...) ----
        var map = GameMap.Instance();
        if (map != null)
        {
            void Consider(MarkerInfo* info)
            {
                if (info == null)
                    return;
                var label = info->Label.ToString();
                foreach (var d in info->MarkerData.AsSpan())
                {
                    var name = d.TooltipString != null ? d.TooltipString->ToString() : "";
                    Add(d.Position, d.Radius, d.DataId, label, name, true, d.TerritoryTypeId);
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
            list = "housing";
            foreach (ref var m in map->HousingMarkers)
                Consider((MarkerInfo*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref m));
            list = "other";
            foreach (var markerList in new[] { &map->UnacceptedQuestMarkers, &map->GuildLeveAssignmentMarkers, &map->GuildOrderGuideMarkers,
                                               &map->TripleTriadMarkers, &map->CustomTalkMarkers, &map->GemstoneTraderMarkers })
                foreach (ref var m in *markerList)
                    Consider((MarkerInfo*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref m));
        }

        // ---- FATEs (the zone you're in) ----
        list = "fate";
        foreach (var fate in Plugin.Fates)
        {
            var name = fate.Name.ToString();
            Add(fate.Position, fate.Radius, 0, name, name, true, fate.TerritoryType.RowId);
        }

        // ---- Party members on this map ----
        list = "party";
        var here = Plugin.ClientState.TerritoryType;
        foreach (var member in Plugin.Party)
        {
            if (member.Territory.RowId != here || member.GameObject == null)
                continue;
            Add(member.Position, 0, 0, "party member", member.Name.ToString(), true, here);
        }

        var agent = AgentMap.Instance();
        if (agent != null)
        {
            // ---- Your flag ----
            list = "flag";
            if (agent->FlagMarkerCount > 0)
            {
                var flag = agent->FlagMapMarkers[0];
                Add(new Vector3(flag.XFloat, 0, flag.YFloat), 0, 0, "flag", "", false, flag.TerritoryId);
            }

            // ---- Temporary markers (treasure maps, some events): world * 16, on some maps also scaled by the
            // map size. Both readings are offered; only one right under the cursor (or named) can be picked. ----
            list = "temporary";
            var size = agent->SelectedMapSizeFactorFloat > 0 ? agent->SelectedMapSizeFactorFloat : 1f;
            for (var i = 0; i < agent->TempMapMarkerCount && i < agent->TempMapMarkers.Length; i++)
            {
                var t = agent->TempMapMarkers[i];
                var name = t.TooltipText.ToString();
                var raw = new Vector2(t.MapMarker.X / 16f, t.MapMarker.Y / 16f);
                Add(new Vector3(raw.X, 0, raw.Y), 0, 0, name, name, false, territory);
                Add(new Vector3(raw.X / size, 0, raw.Y / size), 0, 0, name, name, false, territory);
            }
        }

        // ---- The map's own icons (shops, guilds, inns, exits...), from the game data (read once per map) ----
        list = "map icon";
        foreach (var (name, pos) in MapIcons(mapId))
            Add(new Vector3(pos.X, 0, pos.Y), 0, 0, name, name, false, territory, exactName: true);

        if (best == null && diagnose)
        {
            Plugin.Info($"{Plugin.Tag} Nothing known at the cursor. Nearest ones:");
            foreach (var n in nearest.OrderBy(n => n.Dist).Take(5))
                Plugin.Info($"{Plugin.Tag}   {n.List} \"{Plugin.Redact(n.Label)}\" at {n.Pos} radius {n.Radius:0} id {n.DataId}: {n.Dist:0} y");
        }
        return best;
    }

    private static readonly Dictionary<uint, List<(string Name, Vector2 Pos)>> IconCache = new();

    /// <summary>The map's named icons (not aetherytes/shards) with their world positions; built once per map.</summary>
    private static List<(string Name, Vector2 Pos)> MapIcons(uint mapId)
    {
        if (IconCache.TryGetValue(mapId, out var cached))
            return cached;
        var result = new List<(string, Vector2)>();
        if (Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Map>().TryGetRow(mapId, out var mapRow)
            && Plugin.Data.GetSubrowExcelSheet<Lumina.Excel.Sheets.MapMarker>().TryGetRow(mapRow.MapMarkerRange, out var icons))
        {
            foreach (var icon in icons)
            {
                if (icon.DataType is 3 or 4 || !icon.PlaceNameSubtext.IsValid)
                    continue; // aetherytes and shards are handled elsewhere
                var name = icon.PlaceNameSubtext.Value.Name.ExtractText();
                if (name.Length == 0)
                    continue;
                result.Add((name, new Vector2(Places.PixelToWorld(icon.X, mapRow.SizeFactor, mapRow.OffsetX),
                                              Places.PixelToWorld(icon.Y, mapRow.SizeFactor, mapRow.OffsetY))));
            }
        }
        IconCache[mapId] = result;
        return result;
    }
}
