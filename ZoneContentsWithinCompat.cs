using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Homestead;

internal static class ZoneContentsWithinBlueprintChestPreview
{
    private const string ContentsWithinPatchTypeName = "ContentsWithin.ContentsWithin+InventoryGuiPatch";
    private const int PreviewWidth = 8;
    private const int MaxPreviewRows = 8;
    private static readonly Color RequirementIconColor = new(1f, 1f, 1f, 0.45f);
    private static readonly Color RequirementAmountColor = new(0.86f, 0.86f, 0.86f, 0.75f);
    private static readonly Color RequirementBackgroundColor = new(0.18f, 0.18f, 0.18f, 0.55f);
    private static readonly Dictionary<string, PreviewCache> PreviewInventories = new(StringComparer.Ordinal);
    private static Type? _contentsWithinPatchType;
    private static bool _contentsWithinTypeResolved;

    public static bool IsContentsWithinLoaded()
    {
        return TryGetContentsWithinPatchType(out _);
    }

    public static bool TryGetContentsWithinPatchType(out Type type)
    {
        if (_contentsWithinTypeResolved)
        {
            type = _contentsWithinPatchType!;
            return type != null;
        }

        _contentsWithinTypeResolved = true;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? candidate = assembly.GetType(ContentsWithinPatchTypeName, throwOnError: false, ignoreCase: false);
            if (candidate == null)
            {
                continue;
            }

            _contentsWithinPatchType = candidate;
            type = candidate;
            return true;
        }

        type = null!;
        return false;
    }

    public static void ResetForWorldSession()
    {
        PreviewInventories.Clear();
    }

    public static bool TryHandleAccess(Container? container, ref bool result)
    {
        if (!container)
        {
            return false;
        }

        if (IsBlockedPriceChest(container))
        {
            result = false;
            return true;
        }

        if (CanShowVirtualRequirements(container))
        {
            result = true;
            return true;
        }

        return false;
    }

    public static bool TryReplacePreviewInventory(Inventory? source, out Inventory preview)
    {
        preview = null!;
        if (source == null || Player.m_localPlayer == null)
        {
            return false;
        }

        GameObject hoverObject = Player.m_localPlayer.GetHoverObject();
        Container? container = hoverObject != null ? hoverObject.GetComponentInParent<Container>() : null;
        if (!container || !ReferenceEquals(container.GetInventory(), source))
        {
            return false;
        }

        return TryCreateVirtualRequirementInventory(container, out preview);
    }

    public static bool ShouldBlockContainerPreview(Container? container)
    {
        return container != null && IsBlockedPriceChest(container);
    }

    public static bool TryCreateContainerPreviewInventory(Container? container, out Inventory preview)
    {
        preview = null!;
        return container != null && TryCreateVirtualRequirementInventory(container, out preview);
    }

    public static void GreyOutRequirementGrid(InventoryGrid? grid)
    {
        if (grid == null || !IsVirtualRequirementInventory(grid.GetInventory()))
        {
            return;
        }

        Inventory inventory = grid.GetInventory();
        int width = Mathf.Max(1, grid.m_width);
        int index = 0;
        foreach (InventoryGrid.Element element in grid.m_elements)
        {
            ItemDrop.ItemData? item = inventory.GetItemAt(index % width, index / width);
            index++;
            if (!element.m_used)
            {
                continue;
            }

            ApplyRequirementSlotStyle(element, item);
        }
    }

    private static bool CanShowVirtualRequirements(Container container)
    {
        return TryGetMissingRequirements(container, out _, out _);
    }

    private static bool IsVirtualRequirementInventory(Inventory? inventory)
    {
        if (inventory == null)
        {
            return false;
        }

        foreach (PreviewCache cache in PreviewInventories.Values)
        {
            if (ReferenceEquals(cache.Inventory, inventory))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryCreateVirtualRequirementInventory(Container container, out Inventory preview)
    {
        preview = null!;
        if (!TryGetMissingRequirements(container, out List<ZoneBlueprintRequirement> requirements, out string cacheKey))
        {
            return false;
        }

        string signature = CreateRequirementSignature(requirements);
        if (PreviewInventories.TryGetValue(cacheKey, out PreviewCache cached) &&
            string.Equals(cached.Signature, signature, StringComparison.Ordinal))
        {
            cached.LastUsedAt = Time.realtimeSinceStartup;
            preview = cached.Inventory;
            return true;
        }

        int height = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(1, requirements.Count) / (float)PreviewWidth), 1, MaxPreviewRows);
        Inventory inventory = new("HomesteadRequirementsPreview", container.m_bkg, PreviewWidth, height);
        FillPreviewInventory(inventory, requirements);
        PreviewInventories[cacheKey] = new PreviewCache(inventory, signature, Time.realtimeSinceStartup);
        PrunePreviewInventories();
        preview = inventory;
        return true;
    }

    private static bool TryGetMissingRequirements(
        Container container,
        out List<ZoneBlueprintRequirement> requirements,
        out string cacheKey)
    {
        requirements = [];
        cacheKey = "";

        if (ZoneBlueprintPlanAnchor.TryGetAnchor(container, out ZoneBlueprintPlanAnchor anchor))
        {
            requirements = anchor.GetMissingRequirementList();
            cacheKey = CreateCacheKey(container, "plan");
            return true;
        }

        ZoneBlueprintStoreChest storeChest = container.GetComponent<ZoneBlueprintStoreChest>();
        if (storeChest != null && storeChest.IsPurchaseChest())
        {
            requirements = storeChest.GetMissingPurchaseRequirementList();
            cacheKey = CreateCacheKey(container, "purchase");
            return true;
        }

        return false;
    }

    private static void FillPreviewInventory(Inventory inventory, IReadOnlyList<ZoneBlueprintRequirement> requirements)
    {
        int slots = inventory.m_width * inventory.m_height;
        int count = Mathf.Min(requirements.Count, slots);
        for (int i = 0; i < count; i++)
        {
            ZoneBlueprintRequirement requirement = requirements[i];
            GameObject? prefab = ZoneBlueprintStoreVisuals.FindItemPrefab(requirement.PrefabName) ??
                                 ZoneBlueprintStoreVisuals.FindItemPrefab(requirement.ItemName);
            ItemDrop? itemDrop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (itemDrop == null)
            {
                continue;
            }

            ItemDrop.ItemData item = itemDrop.m_itemData.Clone();
            item.m_stack = Mathf.Max(1, requirement.Amount);
            item.m_dropPrefab = prefab;
            item.m_gridPos = new Vector2i(i % inventory.m_width, i / inventory.m_width);
            inventory.m_inventory.Add(item);
        }

        inventory.Changed();
    }

    private static void ApplyRequirementSlotStyle(InventoryGrid.Element element, ItemDrop.ItemData? item)
    {
        if (element.m_icon != null)
        {
            Transform? slotRoot = element.m_icon.transform.parent;
            if (slotRoot != null)
            {
                foreach (Image image in slotRoot.GetComponentsInChildren<Image>(includeInactive: true))
                {
                    if (image == element.m_icon)
                    {
                        continue;
                    }

                    image.color = RequirementBackgroundColor;
                }
            }

            element.m_icon.color = RequirementIconColor;
        }

        if (element.m_amount != null)
        {
            if (item != null)
            {
                element.m_amount.text = item.m_stack.ToString();
            }

            element.m_amount.color = RequirementAmountColor;
        }

        if (element.m_quality != null)
        {
            element.m_quality.enabled = false;
        }

        if (element.m_equiped != null)
        {
            element.m_equiped.enabled = false;
        }

        if (element.m_queued != null)
        {
            element.m_queued.enabled = false;
        }

        if (element.m_noteleport != null)
        {
            element.m_noteleport.enabled = false;
        }

        if (element.m_food != null)
        {
            element.m_food.enabled = false;
        }

        if (element.m_durability != null)
        {
            element.m_durability.gameObject.SetActive(false);
        }
    }

    private static bool IsBlockedPriceChest(Container container)
    {
        ZoneBlueprintStoreChest chest = container.GetComponent<ZoneBlueprintStoreChest>();
        if (chest != null && (chest.IsPriceChest() || chest.IsPriceChestPrefab()))
        {
            return true;
        }

        ZNetView nview = container.GetComponent<ZNetView>();
        return nview != null &&
               nview.IsValid() &&
               nview.GetZDO().GetPrefab() == ZoneBlueprintStoreChestPrefab.PricePrefabHash;
    }

    private static string CreateCacheKey(Container container, string kind)
    {
        ZNetView nview = container.GetComponent<ZNetView>();
        if (nview != null && nview.IsValid())
        {
            return $"{kind}:{nview.GetZDO().m_uid}";
        }

        return $"{kind}:local:{container.GetInstanceID()}";
    }

    private static string CreateRequirementSignature(IReadOnlyList<ZoneBlueprintRequirement> requirements)
    {
        if (requirements.Count == 0)
        {
            return "empty";
        }

        List<string> parts = new(requirements.Count);
        foreach (ZoneBlueprintRequirement requirement in requirements)
        {
            parts.Add($"{requirement.ItemName}|{requirement.PrefabName}|{requirement.Amount}");
        }

        return string.Join(";", parts);
    }

    private static void PrunePreviewInventories()
    {
        if (PreviewInventories.Count <= 64)
        {
            return;
        }

        float cutoff = Time.realtimeSinceStartup - 60f;
        List<string> remove = [];
        foreach (KeyValuePair<string, PreviewCache> pair in PreviewInventories)
        {
            if (pair.Value.LastUsedAt < cutoff)
            {
                remove.Add(pair.Key);
            }
        }

        foreach (string key in remove)
        {
            PreviewInventories.Remove(key);
        }

        while (PreviewInventories.Count > 64)
        {
            string oldestKey = "";
            float oldest = float.PositiveInfinity;
            foreach (KeyValuePair<string, PreviewCache> pair in PreviewInventories)
            {
                if (pair.Value.LastUsedAt >= oldest)
                {
                    continue;
                }

                oldest = pair.Value.LastUsedAt;
                oldestKey = pair.Key;
            }

            if (string.IsNullOrEmpty(oldestKey))
            {
                break;
            }

            PreviewInventories.Remove(oldestKey);
        }
    }

    private sealed class PreviewCache
    {
        public PreviewCache(Inventory inventory, string signature, float lastUsedAt)
        {
            Inventory = inventory;
            Signature = signature;
            LastUsedAt = lastUsedAt;
        }

        public Inventory Inventory { get; }
        public string Signature { get; }
        public float LastUsedAt { get; set; }
    }
}

[HarmonyPatch]
internal static class ZoneContentsWithinAccessCompat
{
    private static bool Prepare()
    {
        return ZoneContentsWithinBlueprintChestPreview.IsContentsWithinLoaded();
    }

    private static MethodBase? TargetMethod()
    {
        return ZoneContentsWithinBlueprintChestPreview.TryGetContentsWithinPatchType(out Type type)
            ? AccessTools.Method(type, "HasContainerAccess", [typeof(Container)])
            : null;
    }

    private static bool Prefix(Container container, ref bool __result)
    {
        return !ZoneContentsWithinBlueprintChestPreview.TryHandleAccess(container, ref __result);
    }
}

[HarmonyPatch]
internal static class ZoneContentsWithinPreviewCompat
{
    private static bool Prepare()
    {
        return ZoneContentsWithinBlueprintChestPreview.IsContentsWithinLoaded();
    }

    private static MethodBase? TargetMethod()
    {
        return ZoneContentsWithinBlueprintChestPreview.TryGetContentsWithinPatchType(out Type type)
            ? AccessTools.Method(type, "ShowPreviewContainer", [typeof(Inventory)])
            : null;
    }

    private static void Prefix(ref Inventory container)
    {
        if (ZoneContentsWithinBlueprintChestPreview.TryReplacePreviewInventory(container, out Inventory preview))
        {
            container = preview;
        }
    }
}

[HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.UpdateGui))]
internal static class ZoneContentsWithinRequirementGridCompat
{
    private static void Postfix(InventoryGrid __instance)
    {
        ZoneContentsWithinBlueprintChestPreview.GreyOutRequirementGrid(__instance);
    }
}
