using System.Linq;
using UnityEngine;

namespace Homestead;

internal readonly struct ZoneBlueprintStorePreviewDescriptor
{
    public ZoneBlueprintStorePreviewDescriptor(Vector3 anchor, Quaternion rotation, string blueprintFile)
    {
        Anchor = anchor;
        Rotation = rotation;
        BlueprintFile = blueprintFile;
    }

    public Vector3 Anchor { get; }
    public Quaternion Rotation { get; }
    public string BlueprintFile { get; }
}

internal static class ZoneBlueprintStorePreviewPayload
{
    private const string PreviewAnchorValidKey = "hs_store_preview_anchor";
    private const string PreviewAnchorXKey = "hs_store_preview_anchor_x";
    private const string PreviewAnchorYKey = "hs_store_preview_anchor_y";
    private const string PreviewAnchorZKey = "hs_store_preview_anchor_z";
    private const string PreviewAnchorRotXKey = "hs_store_preview_anchor_rx";
    private const string PreviewAnchorRotYKey = "hs_store_preview_anchor_ry";
    private const string PreviewAnchorRotZKey = "hs_store_preview_anchor_rz";
    private const string PreviewAnchorRotWKey = "hs_store_preview_anchor_rw";

    public static bool CanCreateLocalPreview => Player.m_localPlayer != null;

    public static void Write(ZDO zdo, Vector3 anchor, Quaternion rotation)
    {
        zdo.Set(PreviewAnchorValidKey, true);
        zdo.Set(PreviewAnchorXKey, anchor.x);
        zdo.Set(PreviewAnchorYKey, anchor.y);
        zdo.Set(PreviewAnchorZKey, anchor.z);
        zdo.Set(PreviewAnchorRotXKey, rotation.x);
        zdo.Set(PreviewAnchorRotYKey, rotation.y);
        zdo.Set(PreviewAnchorRotZKey, rotation.z);
        zdo.Set(PreviewAnchorRotWKey, rotation.w);
    }

    public static bool TryRead(ZDO zdo, string blueprintFile, out ZoneBlueprintStorePreviewDescriptor descriptor)
    {
        descriptor = default;
        if (!zdo.GetBool(PreviewAnchorValidKey, false) || string.IsNullOrWhiteSpace(blueprintFile))
        {
            return false;
        }

        Vector3 anchor = new(
            ReadRequiredFloat(zdo, PreviewAnchorXKey),
            ReadRequiredFloat(zdo, PreviewAnchorYKey),
            ReadRequiredFloat(zdo, PreviewAnchorZKey));
        Quaternion rotation = new(
            ReadRequiredFloat(zdo, PreviewAnchorRotXKey),
            ReadRequiredFloat(zdo, PreviewAnchorRotYKey),
            ReadRequiredFloat(zdo, PreviewAnchorRotZKey),
            ReadRequiredFloat(zdo, PreviewAnchorRotWKey));
        if (!ZoneTransformPayload.IsFinite(anchor) || !ZoneTransformPayload.IsFinite(rotation))
        {
            return false;
        }

        descriptor = new ZoneBlueprintStorePreviewDescriptor(anchor, rotation, blueprintFile);
        return true;
    }

    private static float ReadRequiredFloat(ZDO zdo, string key)
    {
        return zdo.GetFloat(key, float.NaN);
    }

    public static ZoneBlueprintFile CreatePreviewBlueprint(ZoneBlueprintFile source)
    {
        ZoneBlueprintFile preview = new()
        {
            Version = source.Version,
            Name = source.Name,
            Creator = source.Creator,
            World = source.World,
            SavedAt = source.SavedAt,
            Radius = source.Radius,
            TerrainContacts = []
        };

        foreach (ZoneBlueprintEntry entry in source.Entries)
        {
            preview.Entries.Add(new ZoneBlueprintEntry
            {
                Prefab = entry.Prefab,
                LocalPos = entry.LocalPos.ToArray(),
                LocalRot = entry.LocalRot.ToArray(),
                Scale = entry.Scale.ToArray(),
                Text = ""
            });
        }

        return preview;
    }
}
