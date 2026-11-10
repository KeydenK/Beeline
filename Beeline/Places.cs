using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Beeline;

/// <summary>An aetheryte or aethernet shard with its world position (X/Z).</summary>
/// <remarks>Residential (housing district) shards use the HousingAethernet row as Id and their own Group.</remarks>
internal sealed record Place(uint Id, string Name, uint Territory, byte Group, bool IsMain, Vector2? Position, bool Residential = false);

/// <summary>Aetheryte data from the game sheets.</summary>
internal sealed class Places
{
    private readonly Dictionary<uint, Place> _byId = new();
    private readonly List<Place> _residential = new();

    public Places(IDataManager data)
    {
        // Map markers give icon positions on each map: DataType 3 = aetheryte (key = Aetheryte row),
        // 4 = aethernet shard (key = its PlaceName row). Same source Lifestream uses.
        var mapByRange = new Dictionary<uint, Map>();
        foreach (var m in data.GetExcelSheet<Map>())
            if (m.MapMarkerRange != 0) mapByRange.TryAdd(m.MapMarkerRange, m);

        var markers = new Dictionary<(byte Type, uint Key), List<(uint Range, short X, short Y)>>();
        foreach (var sub in data.GetSubrowExcelSheet<MapMarker>())
            foreach (var m in sub)
                if ((m.DataType == 3 || m.DataType == 4) && m.DataKey.RowId != 0)
                {
                    var k = (m.DataType, m.DataKey.RowId);
                    if (!markers.TryGetValue(k, out var list)) markers[k] = list = new();
                    list.Add((sub.RowId, m.X, m.Y));
                }

        foreach (var a in data.GetExcelSheet<Aetheryte>())
        {
            if (a.Territory.RowId == 0) continue;
            var name = a.IsAetheryte
                ? (a.PlaceName.IsValid ? a.PlaceName.Value.Name.ExtractText() : "")
                : (a.AethernetName.IsValid ? a.AethernetName.Value.Name.ExtractText() : "");
            if (string.IsNullOrEmpty(name)) continue;

            Vector2? pos = null;
            var key = a.IsAetheryte ? ((byte)3, a.RowId) : ((byte)4, a.AethernetName.RowId);
            if (markers.TryGetValue(key, out var found))
            {
                // Prefer the marker on this territory's own map.
                var ownRange = a.Territory.Value.Map.IsValid ? a.Territory.Value.Map.Value.MapMarkerRange : 0u;
                var mk = found.FirstOrDefault(f => f.Range == ownRange);
                if (mk.Range == 0) mk = found[0];
                if (mapByRange.TryGetValue(mk.Range, out var map))
                    pos = new Vector2(PixelToWorld(mk.X, map.SizeFactor, map.OffsetX), PixelToWorld(mk.Y, map.SizeFactor, map.OffsetY));
            }
            if (pos == null && a.Level[0].IsValid)
            {
                var l = a.Level[0].Value;
                pos = new Vector2(l.X, l.Z);
            }

            _byId[a.RowId] = new Place(a.RowId, name, a.Territory.RowId, a.AethernetGroup, a.IsAetheryte, pos);
        }

        // Housing district shards (Mist, Lavender Beds, Goblet, Shirogane, Empyreum...): one network per district.
        var groups = new Dictionary<uint, byte>();
        foreach (var h in data.GetExcelSheet<HousingAethernet>())
        {
            if (h.TerritoryType.RowId == 0 || !h.PlaceName.IsValid) continue;
            var name = h.PlaceName.Value.Name.ExtractText();
            if (name.Length == 0) continue;
            if (!groups.TryGetValue(h.TerritoryType.RowId, out var group))
                groups[h.TerritoryType.RowId] = group = (byte)(200 + groups.Count);
            Vector2? pos = null;
            if (markers.TryGetValue(((byte)4, h.PlaceName.RowId), out var found) && h.TerritoryType.Value.Map.IsValid)
            {
                var ownRange = h.TerritoryType.Value.Map.Value.MapMarkerRange;
                var mk = found.FirstOrDefault(f => f.Range == ownRange);
                if (mk.Range != 0 && mapByRange.TryGetValue(mk.Range, out var map))
                    pos = new Vector2(PixelToWorld(mk.X, map.SizeFactor, map.OffsetX), PixelToWorld(mk.Y, map.SizeFactor, map.OffsetY));
            }
            _residential.Add(new Place(h.RowId, name, h.TerritoryType.RowId, group, false, pos, Residential: true));
        }
    }

    public Place? ById(uint id) => _byId.GetValueOrDefault(id);

    public IEnumerable<Place> InTerritory(uint territory) => _byId.Values.Concat(_residential).Where(p => p.Territory == territory);

    public Place? Residential(uint housingAethernetId) => _residential.FirstOrDefault(p => p.Id == housingAethernetId);

    public Place? MasterOf(Place shard)
        => shard.Group == 0 ? null : _byId.Values.FirstOrDefault(p => p.IsMain && p.Group == shard.Group);

    /// <summary>Aethernet shard whose name matches the tooltip, preferring the shown map.</summary>
    public Place? FindShard(string name, uint territory)
    {
        Place? other = null;
        foreach (var p in _byId.Values.Concat(_residential))
        {
            if (p.IsMain || p.Group == 0 || p.Name != name) continue;
            if (p.Territory == territory) return p;
            other ??= p;
        }
        return other;
    }

    /// <summary>Main aetheryte on the shown map whose name matches the tooltip.</summary>
    public Place? FindMain(string name, uint territory)
        => _byId.Values.FirstOrDefault(p => p.IsMain && p.Territory == territory && p.Name == name);

    /// <summary>Map texture pixel (0..2048) to world coordinate.</summary>
    public static float PixelToWorld(float pixel, ushort sizeFactor, short offset)
        => (pixel - 1024f) / (sizeFactor / 100f) - offset;

    /// <summary>Map coordinate as shown in game (e.g. 10.5) to world coordinate.</summary>
    public static float MapToWorld(float coord, ushort sizeFactor, short offset)
        => 50f * (coord - 1f) - 102400f / sizeFactor - offset;
}
