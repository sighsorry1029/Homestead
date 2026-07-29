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

    private static bool TryValidatePlacement(
        ChestPrefabDefinition definition,
        string mode,
        string ownerPlatformId,
        int requestedCount,
        out GameObject prefab,
        out HomesteadCommandResult failure)
    {
        RegisterPrefab();
        prefab = null!;
        failure = HomesteadCommandResult.Fail("");
        if (!ZoneBlueprintChestLifecycle.CanPlaceChests(ownerPlatformId, requestedCount, out string limitReason))
        {
            failure = HomesteadCommandResult.Fail(limitReason);
            return false;
        }

        GameObject? resolvedPrefab = ZoneChestPlacement.GetRegisteredNetworkPrefab(
            definition.PrefabName,
            definition.PrefabHash);
        if (!resolvedPrefab)
        {
            failure = HomesteadCommandResult.Fail(GetPrefabNotReadyMessage(mode));
            return false;
        }

        prefab = resolvedPrefab;
        return true;
    }

    private static GameObject SpawnChest(StoreChestPlacementRequest request, GameObject prefab)
    {
        GameObject? chest = null;
        try
        {
            chest = Object.Instantiate(prefab, request.Position, request.Rotation);
            ZDO zdo = ZoneChestPlacement.RequireValidNetworkedSpawn(
                chest,
                request.Definition.PrefabHash,
                $"Blueprint Store {request.Mode} chest");
            zdo.Set(ZDOVars.s_creator, request.OwnerPlayerId);
            zdo.Set(ZDOVars.s_creatorName, request.OwnerName);
            ZoneBlueprintChestLifecycle.SetOwnerPlatformId(zdo, request.OwnerPlatformId);
            return chest;
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                $"Failed to create networked Homestead store chest '{request.Definition.PrefabName}' ({request.Mode}): {ex}");
            ZoneChestPlacement.DestroySpawned(chest);
            throw;
        }
    }

    private static ZoneBlueprintStoreChest GetStoreChest(GameObject chest)
    {
        return chest.GetComponent<ZoneBlueprintStoreChest>() ?? chest.AddComponent<ZoneBlueprintStoreChest>();
    }

    private static void CompletePlacement(StoreChestPlacementRequest request, GameObject chest, bool broadcast)
    {
        ZoneChestPlacement.PlayPlaceEffect(chest);
        ZoneChestPlacement.SafeOnPlaced(chest, _logger, "Store chest");
        if (broadcast)
        {
            ZoneBlueprintChestVfx.BroadcastPlace(request.Mode, ZoneTransformPayload.From(request.Position, request.Rotation), request.VfxExcludePeer);
        }
    }

    private static string GetPrefabNotReadyMessage(string mode)
    {
        return mode switch
        {
            ZoneBlueprintStoreChest.ModePrice => HomesteadLocalization.Text("hs_store_price_chest_prefab_not_ready"),
            ZoneBlueprintStoreChest.ModePayout => HomesteadLocalization.Text("hs_store_payout_chest_prefab_not_ready"),
            _ => HomesteadLocalization.Text("hs_store_purchase_chest_prefab_not_ready")
        };
    }

    public static HomesteadCommandResult PlacePurchaseChest(
        ZoneBlueprintStoreListing listing,
        IReadOnlyList<ZoneBlueprintStorePriceItem> priceItems,
        string offerId,
        long buyerPlayerId,
        string buyerName,
        string buyerPlatformId,
        Vector3 position,
        Quaternion rotation,
        Vector3 previewAnchor,
        Quaternion previewRotation,
        long vfxExcludePeer = 0L)
    {
        StoreChestPlacementRequest placement = new(
            PurchaseChest,
            ZoneBlueprintStoreChest.ModePurchase,
            buyerPlayerId,
            buyerName,
            buyerPlatformId,
            position,
            rotation,
            vfxExcludePeer);
        if (!TryValidatePlacement(
                PurchaseChest,
                ZoneBlueprintStoreChest.ModePurchase,
                buyerPlatformId,
                requestedCount: 1,
                out GameObject prefab,
                out HomesteadCommandResult failure))
        {
            return failure;
        }

        GameObject? chest = null;
        try
        {
            chest = SpawnChest(placement, prefab);
            ZoneBlueprintStoreChest storeChest = GetStoreChest(chest);
            storeChest.SetPurchase(listing, priceItems, offerId, buyerPlayerId, buyerName, buyerPlatformId, previewAnchor, previewRotation);
            CompletePlacement(placement, chest, broadcast: true);

            return HomesteadCommandResult.Ok(HomesteadLocalization.Format("hs_store_purchase_chest_placed", listing.Name, ZoneBlueprintStorePrices.FormatPrice(priceItems)));
        }
        catch (Exception ex)
        {
            ZoneChestPlacement.DestroySpawned(chest);
            return HomesteadCommandResult.Fail(FormatStoreChestPlaceFailed(ex));
        }
    }

    public static HomesteadCommandResult PlacePriceChest(
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
        Quaternion previewRotation,
        long vfxExcludePeer = 0L)
    {
        StoreChestPlacementRequest placement = new(
            PriceChest,
            ZoneBlueprintStoreChest.ModePrice,
            sellerPlayerId,
            sellerName,
            sellerPlatformId,
            position,
            rotation,
            vfxExcludePeer);
        if (!TryValidatePlacement(
                PriceChest,
                ZoneBlueprintStoreChest.ModePrice,
                sellerPlatformId,
                requestedCount: 1,
                out GameObject prefab,
                out HomesteadCommandResult failure))
        {
            return failure;
        }

        GameObject? chest = null;
        try
        {
            chest = SpawnChest(placement, prefab);
            ZoneBlueprintStoreChest storeChest = GetStoreChest(chest);
            storeChest.SetPriceDraft(listingId, blueprintName, blueprintFile, iconPngBase64, entryCount, sellerPlayerId, sellerName, sellerPlatformId, previewAnchor, previewRotation);
            CompletePlacement(placement, chest, broadcast: true);

            return HomesteadCommandResult.Ok(HomesteadLocalization.Format("hs_store_price_chest_placed", blueprintName));
        }
        catch (Exception ex)
        {
            ZoneChestPlacement.DestroySpawned(chest);
            return HomesteadCommandResult.Fail(FormatStoreChestPlaceFailed(ex));
        }
    }

    public static HomesteadCommandResult PlacePayoutChests(
        IReadOnlyList<ZoneBlueprintStorePriceItem> payoutItems,
        long sellerPlayerId,
        string sellerName,
        string sellerPlatformId,
        Vector3 basePosition,
        Quaternion rotation,
        bool positionIsAnchor,
        out List<ZoneBlueprintStoreTransformPayload> chestTransforms,
        long vfxExcludePeer = 0L)
    {
        chestTransforms = [];
        if (!TryPreparePayoutChests(
                payoutItems,
                sellerPlatformId,
                out List<ZoneBlueprintStorePriceItem> stacks,
                out int chestCount,
                out GameObject prefab,
                out HomesteadCommandResult failure))
        {
            return failure;
        }

        const int chestCapacity = 32;
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
                if (positionIsAnchor)
                {
                    position.y = basePosition.y;
                }
                else
                {
                    position.y = HomesteadTerrainSupport.SampleGroundY(position.x, position.z, position.y);
                }

                StoreChestPlacementRequest placement = new(
                    PayoutChest,
                    ZoneBlueprintStoreChest.ModePayout,
                    sellerPlayerId,
                    sellerName,
                    sellerPlatformId,
                    position,
                    rotation,
                    vfxExcludePeer);
                GameObject chest = SpawnChest(placement, prefab);
                spawned.Add(chest);
                ZoneBlueprintStoreChest storeChest = GetStoreChest(chest);
                storeChest.SetPayout(sellerPlayerId, sellerName, sellerPlatformId);
                CompletePlacement(placement, chest, broadcast: false);
                if (!storeChest.PreparePayoutInventory(chestStacks))
                {
                    throw new InvalidOperationException(HomesteadLocalization.Text("hs_store_payout_chest_fill_failed"));
                }

                chestTransforms.Add(ZoneTransformPayload.From(position, rotation));
            }
        }
        catch (Exception ex)
        {
            foreach (GameObject chest in spawned.Where(chest => chest != null && chest))
            {
                ZoneChestPlacement.DestroySpawned(chest);
            }

            return HomesteadCommandResult.Fail(HomesteadLocalization.Format("hs_store_payout_chest_place_failed", ex.Message));
        }

        ZoneBlueprintChestVfx.BroadcastPlace(ZoneBlueprintStoreChest.ModePayout, chestTransforms, vfxExcludePeer);
        return HomesteadCommandResult.Ok(HomesteadLocalization.Format("hs_store_payout_chest_placed", chestCount));
    }

    public static HomesteadCommandResult PreflightPayoutChests(
        IReadOnlyList<ZoneBlueprintStorePriceItem> payoutItems,
        string sellerPlatformId)
    {
        return TryPreparePayoutChests(
            payoutItems,
            sellerPlatformId,
            out _,
            out _,
            out _,
            out HomesteadCommandResult failure)
            ? HomesteadCommandResult.Ok("")
            : failure;
    }

    private static bool TryPreparePayoutChests(
        IReadOnlyList<ZoneBlueprintStorePriceItem> payoutItems,
        string sellerPlatformId,
        out List<ZoneBlueprintStorePriceItem> stacks,
        out int chestCount,
        out GameObject prefab,
        out HomesteadCommandResult failure)
    {
        prefab = null!;
        chestCount = 0;
        failure = HomesteadCommandResult.Fail("");
        if (!ZoneMaterialEscrow.TrySplitIntoStacks(payoutItems, out stacks, out string reason))
        {
            failure = HomesteadCommandResult.Fail(reason);
            return false;
        }

        const int chestCapacity = 32;
        chestCount = Mathf.CeilToInt(stacks.Count / (float)chestCapacity);
        if (chestCount <= 0)
        {
            failure = HomesteadCommandResult.Fail(HomesteadLocalization.Text("hs_store_no_payout_materials"));
            return false;
        }

        return TryValidatePlacement(
            PayoutChest,
            ZoneBlueprintStoreChest.ModePayout,
            sellerPlatformId,
            chestCount,
            out prefab,
            out failure);
    }

    private static string FormatStoreChestPlaceFailed(Exception ex)
    {
        return HomesteadLocalization.Format("hs_store_chest_place_failed", ex.Message);
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
        return ZoneChestPlacement.PlayPlaceEffect(prefab, position, rotation);
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

    private sealed class StoreChestPlacementRequest
    {
        public StoreChestPlacementRequest(
            ChestPrefabDefinition definition,
            string mode,
            long ownerPlayerId,
            string ownerName,
            string ownerPlatformId,
            Vector3 position,
            Quaternion rotation,
            long vfxExcludePeer)
        {
            Definition = definition;
            Mode = mode;
            OwnerPlayerId = ownerPlayerId;
            OwnerName = ownerName;
            OwnerPlatformId = ownerPlatformId;
            Position = position;
            Rotation = rotation;
            VfxExcludePeer = vfxExcludePeer;
        }

        public ChestPrefabDefinition Definition { get; }
        public string Mode { get; }
        public long OwnerPlayerId { get; }
        public string OwnerName { get; }
        public string OwnerPlatformId { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public long VfxExcludePeer { get; }
    }
}
