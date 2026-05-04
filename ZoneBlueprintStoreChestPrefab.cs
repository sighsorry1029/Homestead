using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Homestead;


internal static class ZoneBlueprintStoreChestPrefab
{
    internal const string PricePrefabName = "piece_chest_barrel_blueprint_store_price";
    internal const string PurchasePrefabName = "piece_chest_blueprint_store_purchase";
    internal const string PayoutPrefabName = "piece_chest_blackmetal_blueprint_store_payout";

    private const string PriceBasePrefabName = "piece_chest_barrel";
    private const string PurchaseBasePrefabName = "piece_chest";
    private const string PayoutBasePrefabName = "piece_chest_blackmetal";

    internal static readonly int PricePrefabHash = StringExtensionMethods.GetStableHashCode(PricePrefabName);
    internal static readonly int PurchasePrefabHash = StringExtensionMethods.GetStableHashCode(PurchasePrefabName);
    internal static readonly int PayoutPrefabHash = StringExtensionMethods.GetStableHashCode(PayoutPrefabName);

    private static readonly ChestPrefabDefinition PriceChest = new(
        PricePrefabName,
        PriceBasePrefabName,
        HomesteadLocalization.Token("hs_store_price_chest_name"),
        HomesteadLocalization.Token("hs_store_price_chest_desc"),
        8,
        1,
        Container.PrivacySetting.Public);
    private static readonly ChestPrefabDefinition PurchaseChest = new(
        PurchasePrefabName,
        PurchaseBasePrefabName,
        HomesteadLocalization.Token("hs_store_purchase_chest_name"),
        HomesteadLocalization.Token("hs_store_purchase_chest_desc"),
        8,
        1,
        Container.PrivacySetting.Public);
    private static readonly ChestPrefabDefinition PayoutChest = new(
        PayoutPrefabName,
        PayoutBasePrefabName,
        HomesteadLocalization.Token("hs_store_payout_chest_name"),
        HomesteadLocalization.Token("hs_store_payout_chest_desc"),
        8,
        4,
        Container.PrivacySetting.Private);

    private static readonly ChestPrefabDefinition[] StoreChests =
    [
        PriceChest,
        PurchaseChest,
        PayoutChest
    ];

    private static ManualLogSource? _logger;
    private static bool _initialized;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        PrefabManager.OnVanillaPrefabsAvailable += RegisterPrefab;
        RegisterPrefab();
    }

    public static ZoneBundleCommandResult PlacePurchaseChest(
        ZoneBlueprintStoreListing listing,
        IReadOnlyList<ZoneBlueprintStorePriceItem> priceItems,
        string offerId,
        long buyerPlayerId,
        string buyerName,
        string buyerPlatformId,
        Vector3 position,
        Quaternion rotation,
        Vector3 previewAnchor,
        Quaternion previewRotation)
    {
        RegisterPrefab();
        if (!ZoneBlueprintChestLifecycle.CanPlaceChests(buyerPlatformId, requestedCount: 1, out string limitReason))
        {
            return ZoneBundleCommandResult.Fail(limitReason);
        }

        GameObject? prefab = GetPrefab(PurchaseChest);
        if (!prefab)
        {
            return ZoneBundleCommandResult.Fail(HomesteadLocalization.Text("hs_store_purchase_chest_prefab_not_ready"));
        }

        GameObject chest = Object.Instantiate(prefab, position, rotation);
        ZNetView nview = chest.GetComponent<ZNetView>();
        if (nview != null && nview.IsValid())
        {
            ZDO zdo = nview.GetZDO();
            zdo.Set(ZDOVars.s_creator, buyerPlayerId);
            zdo.Set(ZDOVars.s_creatorName, buyerName);
            ZoneBlueprintChestLifecycle.SetOwnerPlatformId(zdo, buyerPlatformId);
        }

        Piece piece = chest.GetComponent<Piece>();
        if (piece != null)
        {
            piece.m_placeEffect.Create(chest.transform.position, chest.transform.rotation, chest.transform);
        }

        WearNTear wearNTear = chest.GetComponent<WearNTear>();
        wearNTear?.OnPlaced();

        ZoneBlueprintStoreChest storeChest = chest.GetComponent<ZoneBlueprintStoreChest>() ?? chest.AddComponent<ZoneBlueprintStoreChest>();
        storeChest.SetPurchase(listing, priceItems, offerId, buyerPlayerId, buyerName, buyerPlatformId, previewAnchor, previewRotation);
        return ZoneBundleCommandResult.Ok(HomesteadLocalization.Format("hs_store_purchase_chest_placed", listing.Name, ZoneBlueprintStore.FormatPrice(priceItems)));
    }

    public static ZoneBundleCommandResult PlacePriceChest(
        string listingId,
        string blueprintName,
        string blueprintFile,
        string iconPngBase64,
        int entryCount,
        long sellerPlayerId,
        string sellerName,
        string sellerPlatformId,
        Vector3 position,
        Quaternion rotation,
        Vector3 previewAnchor,
        Quaternion previewRotation)
    {
        RegisterPrefab();
        if (!ZoneBlueprintChestLifecycle.CanPlaceChests(sellerPlatformId, requestedCount: 1, out string limitReason))
        {
            return ZoneBundleCommandResult.Fail(limitReason);
        }

        GameObject? prefab = GetPrefab(PriceChest);
        if (!prefab)
        {
            return ZoneBundleCommandResult.Fail(HomesteadLocalization.Text("hs_store_price_chest_prefab_not_ready"));
        }

        GameObject chest = Object.Instantiate(prefab, position, rotation);
        ZNetView nview = chest.GetComponent<ZNetView>();
        if (nview != null && nview.IsValid())
        {
            ZDO zdo = nview.GetZDO();
            zdo.Set(ZDOVars.s_creator, sellerPlayerId);
            zdo.Set(ZDOVars.s_creatorName, sellerName);
            ZoneBlueprintChestLifecycle.SetOwnerPlatformId(zdo, sellerPlatformId);
        }

        Piece piece = chest.GetComponent<Piece>();
        if (piece != null)
        {
            piece.m_placeEffect.Create(chest.transform.position, chest.transform.rotation, chest.transform);
        }

        WearNTear wearNTear = chest.GetComponent<WearNTear>();
        wearNTear?.OnPlaced();

        ZoneBlueprintStoreChest storeChest = chest.GetComponent<ZoneBlueprintStoreChest>() ?? chest.AddComponent<ZoneBlueprintStoreChest>();
        storeChest.SetPriceDraft(listingId, blueprintName, blueprintFile, iconPngBase64, entryCount, sellerPlayerId, sellerName, sellerPlatformId, previewAnchor, previewRotation);
        return ZoneBundleCommandResult.Ok(HomesteadLocalization.Format("hs_store_price_chest_placed", blueprintName));
    }

    public static ZoneBundleCommandResult PlacePayoutChests(
        IReadOnlyList<ZoneBlueprintStorePriceItem> payoutItems,
        long sellerPlayerId,
        string sellerName,
        string sellerPlatformId,
        Vector3 basePosition,
        Quaternion rotation,
        bool positionIsAnchor)
    {
        RegisterPrefab();
        GameObject? prefab = GetPrefab(PayoutChest);
        if (!prefab)
        {
            return ZoneBundleCommandResult.Fail(HomesteadLocalization.Text("hs_store_payout_chest_prefab_not_ready"));
        }

        if (!ZoneMaterialEscrow.TrySplitIntoStacks(payoutItems, out List<ZoneBlueprintStorePriceItem> stacks, out string reason))
        {
            return ZoneBundleCommandResult.Fail(reason);
        }

        const int chestCapacity = 32;
        int chestCount = Mathf.CeilToInt(stacks.Count / (float)chestCapacity);
        if (chestCount <= 0)
        {
            return ZoneBundleCommandResult.Fail(HomesteadLocalization.Text("hs_store_no_payout_materials"));
        }
        if (!ZoneBlueprintChestLifecycle.CanPlaceChests(sellerPlatformId, chestCount, out string limitReason))
        {
            return ZoneBundleCommandResult.Fail(limitReason);
        }

        List<GameObject> spawned = [];
        try
        {
            for (int chestIndex = 0; chestIndex < chestCount; chestIndex++)
            {
                List<ZoneBlueprintStorePriceItem> chestStacks = stacks
                    .Skip(chestIndex * chestCapacity)
                    .Take(chestCapacity)
                    .ToList();
                float zOffset = (positionIsAnchor ? 0f : 2.2f) + (chestIndex / 4) * 1.8f;
                Vector3 localOffset = new((chestIndex % 4) * 1.8f - Mathf.Min(chestCount - 1, 3) * 0.9f, 0f, zOffset);
                Vector3 position = basePosition + rotation * localOffset;
                position.y = SampleGroundY(position.x, position.z, position.y);

                GameObject chest = Object.Instantiate(prefab, position, rotation);
                spawned.Add(chest);
                ZNetView nview = chest.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid())
                {
                    ZDO zdo = nview.GetZDO();
                    zdo.Set(ZDOVars.s_creator, sellerPlayerId);
                    zdo.Set(ZDOVars.s_creatorName, sellerName);
                    ZoneBlueprintChestLifecycle.SetOwnerPlatformId(zdo, sellerPlatformId);
                }

                Piece piece = chest.GetComponent<Piece>();
                if (piece != null)
                {
                    piece.m_placeEffect.Create(chest.transform.position, chest.transform.rotation, chest.transform);
                }

                WearNTear wearNTear = chest.GetComponent<WearNTear>();
                wearNTear?.OnPlaced();

                ZoneBlueprintStoreChest storeChest = chest.GetComponent<ZoneBlueprintStoreChest>() ?? chest.AddComponent<ZoneBlueprintStoreChest>();
                storeChest.SetPayout(sellerPlayerId, sellerName, sellerPlatformId);
                if (!storeChest.PreparePayoutInventory(chestStacks))
                {
                    throw new InvalidOperationException(HomesteadLocalization.Text("hs_store_payout_chest_fill_failed"));
                }
            }
        }
        catch (Exception ex)
        {
            foreach (GameObject chest in spawned.Where(chest => chest != null && chest))
            {
                Object.Destroy(chest);
            }

            return ZoneBundleCommandResult.Fail(HomesteadLocalization.Format("hs_store_payout_chest_place_failed", ex.Message));
        }

        return ZoneBundleCommandResult.Ok(HomesteadLocalization.Format("hs_store_payout_chest_placed", chestCount));
    }

    public static GameObject? CreatePreview(string mode)
    {
        RegisterPrefab();
        ChestPrefabDefinition definition = GetDefinitionForMode(mode);
        GameObject? prefab = GetPrefab(definition);
        if (!prefab)
        {
            return null;
        }

        GameObject root = new($"Homestead{definition.PrefabName}Preview");
        int copied = ZoneBlueprintVisuals.CopyVisuals(prefab.transform, root.transform);
        if (copied == 0)
        {
            Object.Destroy(root);
            return null;
        }

        return root;
    }

    public static bool PlayPlaceEffect(string mode, Vector3 position, Quaternion rotation)
    {
        RegisterPrefab();
        GameObject? prefab = GetPrefab(GetDefinitionForMode(mode));
        Piece piece = prefab != null && prefab ? prefab.GetComponent<Piece>() : null!;
        if (piece == null)
        {
            return false;
        }

        piece.m_placeEffect.Create(position, rotation, null);
        return true;
    }

    internal static Sprite? GetIconForPrefabHash(int prefabHash)
    {
        RegisterPrefab();
        ChestPrefabDefinition? definition = StoreChests.FirstOrDefault(item => item.PrefabHash == prefabHash);
        return definition == null ? null : GetPrefab(definition)?.GetComponent<Piece>()?.m_icon;
    }

    public static bool IsStorePrefab(int prefabHash)
    {
        return StoreChests.Any(definition => definition.PrefabHash == prefabHash);
    }

    public static bool IsStorePrefabName(string prefabName)
    {
        return StoreChests.Any(definition => string.Equals(definition.PrefabName, prefabName, StringComparison.OrdinalIgnoreCase));
    }

    private static GameObject? GetPrefab(ChestPrefabDefinition definition)
    {
        return PrefabManager.Instance.GetPrefab(definition.PrefabName) ?? ZNetScene.instance?.GetPrefab(definition.PrefabName);
    }

    private static float SampleGroundY(float x, float z, float fallbackY)
    {
        if (ZoneSystem.instance == null)
        {
            return fallbackY;
        }

        Vector3 point = new(x, fallbackY, z);
        ZoneSystem.instance.GetGroundData(ref point, out _, out _, out _, out _);
        return point.y;
    }

    private static void RegisterPrefab()
    {
        foreach (ChestPrefabDefinition definition in StoreChests)
        {
            RegisterPrefab(definition);
        }
    }

    private static void RegisterPrefab(ChestPrefabDefinition definition)
    {
        if (definition.Registered)
        {
            return;
        }

        if (PrefabManager.Instance.GetPrefab(definition.PrefabName))
        {
            definition.Registered = true;
            return;
        }

        if (!PrefabManager.Instance.GetPrefab(definition.BasePrefabName) && !(ZNetScene.instance?.GetPrefab(definition.BasePrefabName)))
        {
            return;
        }

        GameObject prefab = PrefabManager.Instance.CreateClonedPrefab(definition.PrefabName, definition.BasePrefabName);
        if (!prefab)
        {
            return;
        }

        ConfigurePrefab(prefab, definition);
        PrefabManager.Instance.AddPrefab(prefab);
        PrefabManager.Instance.RegisterToZNetScene(prefab);
        definition.Registered = true;
        _logger?.LogInfo($"Registered Homestead blueprint store chest prefab: {definition.PrefabName}.");
    }

    private static ChestPrefabDefinition GetDefinitionForMode(string mode)
    {
        return mode switch
        {
            ZoneBlueprintStoreChest.ModePrice => PriceChest,
            ZoneBlueprintStoreChest.ModePayout => PayoutChest,
            _ => PurchaseChest
        };
    }

    private static void ConfigurePrefab(GameObject prefab, ChestPrefabDefinition definition)
    {
        Container container = prefab.GetComponent<Container>();
        if (container != null)
        {
            container.m_name = definition.DisplayName;
            container.m_width = definition.Width;
            container.m_height = definition.Height;
            container.m_autoDestroyEmpty = false;
            container.m_privacy = definition.Privacy;
            container.m_defaultItems = new DropTable();
        }

        Piece piece = prefab.GetComponent<Piece>();
        if (piece != null)
        {
            piece.m_name = definition.DisplayName;
            piece.m_description = definition.Description;
            piece.m_resources = Array.Empty<Piece.Requirement>();
        }

        if (prefab.GetComponent<ZoneBlueprintStoreChest>() == null)
        {
            prefab.AddComponent<ZoneBlueprintStoreChest>();
        }
    }

    private sealed class ChestPrefabDefinition
    {
        public ChestPrefabDefinition(
            string prefabName,
            string basePrefabName,
            string displayName,
            string description,
            int width,
            int height,
            Container.PrivacySetting privacy)
        {
            PrefabName = prefabName;
            BasePrefabName = basePrefabName;
            DisplayName = displayName;
            Description = description;
            Width = width;
            Height = height;
            Privacy = privacy;
            PrefabHash = StringExtensionMethods.GetStableHashCode(prefabName);
        }

        public string PrefabName { get; }
        public string BasePrefabName { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public int Width { get; }
        public int Height { get; }
        public Container.PrivacySetting Privacy { get; }
        public int PrefabHash { get; }
        public bool Registered { get; set; }
    }
}
