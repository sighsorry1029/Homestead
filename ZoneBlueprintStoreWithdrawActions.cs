using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintStoreWithdrawAction
{
    public static ZoneBlueprintStoreRpcEnvelope Execute(ZoneBlueprintStoreWithdrawRequest request, Player? player, long sender)
    {
        if (!ZoneBlueprintStoreAccess.TryResolveRequester(player, sender, out long playerId, out string playerName, out Vector3 position, out Quaternion rotation, out string reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.Withdraw, reason);
        }

        ZoneBlueprintStoreActor seller = ZoneBlueprintStoreAccess.ResolveRequesterActor(player, sender, playerId);
        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
        List<ZoneBlueprintStoreBalance> balances = catalog.Balances
            .Where(item => seller.MatchesStored(item.SellerPlayerId, item.SellerPlatformId, BlueprintConfig.StoreIdentityMode))
            .ToList();
        int coins = balances.Sum(item => item.Coins);
        List<ZoneBlueprintStorePriceItem> materials = ZoneBlueprintStorePrices.NormalizePriceItems(balances.SelectMany(item => item.Materials));
        if (coins <= 0 && materials.Count == 0)
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.Withdraw, HomesteadLocalization.Text("hs_store_no_balance_withdraw"));
        }

        List<ZoneBlueprintStorePriceItem> payoutItems = ZoneBlueprintStorePrices.CreatePayoutItems(coins, materials);
        List<ZoneBlueprintStoreBalance> balanceSnapshots = CloneBalances(balances);
        ClearBalances(balances, playerName, seller.PlatformId);
        if (!ZoneBlueprintStoreDraftRepository.TrySaveCatalogImmediate(catalog, out string saveReason))
        {
            RestoreBalances(balances, balanceSnapshots);
            ZoneBlueprintStoreDraftRepository.SaveCatalog(catalog);
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.Withdraw, saveReason);
        }

        Vector3 payoutPosition = position;
        Quaternion payoutRotation = rotation;
        if (!ZoneBlueprintStorePlacement.TryReadOptionalStoreChestTarget(request.Target, position, rotation, out bool useTarget, out payoutPosition, out payoutRotation, out reason))
        {
            RestoreBalances(balances, balanceSnapshots);
            ZoneBlueprintStoreDraftRepository.TrySaveCatalogImmediate(catalog, out _);
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.Withdraw, reason);
        }

        HomesteadCommandResult payoutResult = ZoneBlueprintStoreChestPrefab.PlacePayoutChests(
            payoutItems,
            playerId,
            playerName,
            seller.PlatformId,
            useTarget ? payoutPosition : position,
            useTarget ? payoutRotation : rotation,
            positionIsAnchor: useTarget,
            out List<ZoneBlueprintStoreTransformPayload> chestTransforms,
            vfxExcludePeer: sender);
        if (!payoutResult.Success)
        {
            RestoreBalances(balances, balanceSnapshots);
            ZoneBlueprintStoreDraftRepository.TrySaveCatalogImmediate(catalog, out _);
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.Withdraw, payoutResult.Message);
        }

        return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.WithdrawComplete, new ZoneBlueprintStoreWithdrawResponse
        {
            Success = true,
            Message = HomesteadLocalization.Format("hs_store_withdraw_complete", payoutResult.Message, ZoneBlueprintStorePrices.FormatBalance(coins, materials)),
            Chests = chestTransforms
        });
    }

    private static List<ZoneBlueprintStoreBalance> CloneBalances(IEnumerable<ZoneBlueprintStoreBalance> balances)
    {
        return balances
            .Select(balance => new ZoneBlueprintStoreBalance
            {
                SellerPlayerId = balance.SellerPlayerId,
                SellerPlatformId = balance.SellerPlatformId,
                SellerName = balance.SellerName,
                Coins = balance.Coins,
                Materials = balance.Materials?
                    .Select(item => new ZoneBlueprintStorePriceItem
                    {
                        ItemName = item.ItemName,
                        PrefabName = item.PrefabName,
                        DisplayName = item.DisplayName,
                        Amount = item.Amount
                    })
                    .ToList() ?? []
            })
            .ToList();
    }

    private static void ClearBalances(IEnumerable<ZoneBlueprintStoreBalance> balances, string sellerName, string sellerPlatformId)
    {
        foreach (ZoneBlueprintStoreBalance balance in balances)
        {
            balance.Coins = 0;
            balance.SellerName = sellerName;
            balance.SellerPlatformId = sellerPlatformId;
            balance.Materials = [];
        }
    }

    private static void RestoreBalances(IReadOnlyList<ZoneBlueprintStoreBalance> balances, IReadOnlyList<ZoneBlueprintStoreBalance> snapshots)
    {
        for (int i = 0; i < balances.Count && i < snapshots.Count; i++)
        {
            ZoneBlueprintStoreBalance balance = balances[i];
            ZoneBlueprintStoreBalance snapshot = snapshots[i];
            balance.SellerPlayerId = snapshot.SellerPlayerId;
            balance.SellerPlatformId = snapshot.SellerPlatformId;
            balance.SellerName = snapshot.SellerName;
            balance.Coins = snapshot.Coins;
            balance.Materials = snapshot.Materials;
        }
    }
}
