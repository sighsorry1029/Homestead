using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Homestead;

internal static class BlueprintTerrainContactSampler
{
    public static List<TerrainWorldContact> CaptureWorldContacts(IEnumerable<TerrainContactSource> sources, float tolerance)
    {
        Dictionary<long, TerrainWorldContact> lowestByCell = [];
        foreach (TerrainContactSource source in sources)
        {
            if (!source.Prefab || source.Prefab.GetComponent<WearNTear>() == null)
            {
                continue;
            }

            if (!HomesteadTerrainSupport.TryGetWearNTearBounds(source.Prefab, source.Position, source.Rotation, source.Scale, out Bounds bounds))
            {
                continue;
            }

            AddLowestBoundsFootprintContacts(bounds, lowestByCell);
        }

        List<TerrainWorldContact> contacts = [];
        foreach (TerrainWorldContact candidate in lowestByCell.Values.OrderBy(contact => contact.WorldZ).ThenBy(contact => contact.WorldX))
        {
            if (!HomesteadTerrainSupport.TryGetTerrainHeight(candidate.WorldX, candidate.WorldZ, out float terrainY) ||
                Mathf.Abs(terrainY - candidate.WorldY) > tolerance)
            {
                continue;
            }

            contacts.Add(candidate);
        }

        return contacts;
    }

    public static List<ZoneBlueprintTerrainContact> ToBlueprintContacts(
        Vector3 anchor,
        Quaternion inverseAnchorRotation,
        IEnumerable<TerrainWorldContact> contacts)
    {
        return contacts
            .Select(contact =>
            {
                Vector3 local = inverseAnchorRotation * (contact.ToVector3() - anchor);
                return new ZoneBlueprintTerrainContact
                {
                    LocalX = Round(local.x),
                    LocalY = Round(local.y),
                    LocalZ = Round(local.z)
                };
            })
            .OrderBy(contact => contact.LocalZ)
            .ThenBy(contact => contact.LocalX)
            .ToList();
    }

    private static void AddLowestBoundsFootprintContacts(Bounds bounds, Dictionary<long, TerrainWorldContact> lowestByCell)
    {
        float bottomY = bounds.min.y;
        int minX = Mathf.FloorToInt(bounds.min.x);
        int maxX = Mathf.CeilToInt(bounds.max.x);
        int minZ = Mathf.FloorToInt(bounds.min.z);
        int maxZ = Mathf.CeilToInt(bounds.max.z);

        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                long key = PackCell(x, z);
                TerrainWorldContact contact = new(x, z, bottomY);
                if (!lowestByCell.TryGetValue(key, out TerrainWorldContact existing) || contact.WorldY < existing.WorldY)
                {
                    lowestByCell[key] = contact;
                }
            }
        }
    }

    private static long PackCell(int x, int z)
    {
        return ((long)x << 32) ^ (uint)z;
    }

    private static float Round(float value)
    {
        return Mathf.Round(value * 1000f) / 1000f;
    }
}

internal readonly struct TerrainContactSource
{
    public TerrainContactSource(GameObject prefab, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Prefab = prefab;
        Position = position;
        Rotation = rotation;
        Scale = scale;
    }

    public GameObject Prefab { get; }
    public Vector3 Position { get; }
    public Quaternion Rotation { get; }
    public Vector3 Scale { get; }
}

internal readonly struct TerrainWorldContact
{
    public TerrainWorldContact(int cellX, int cellZ, float worldY)
    {
        WorldX = cellX;
        WorldZ = cellZ;
        WorldY = worldY;
    }

    public float WorldX { get; }
    public float WorldZ { get; }
    public float WorldY { get; }

    public Vector3 ToVector3()
    {
        return new Vector3(WorldX, WorldY, WorldZ);
    }
}
