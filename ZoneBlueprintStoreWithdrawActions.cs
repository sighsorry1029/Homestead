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
            .Where(item => seller.MatchesPlayer(item.SellerPlayerId))
            .ToList();
        int coins = balances.Sum(item => item.Coins);
        List<ZoneBlueprintStorePriceItem> materials = ZoneBlueprintStorePrices.NormalizePriceItems(balances.SelectMany(item => item.Materials));
        if (coins <= 0 && materials.Count == 0)
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.Withdraw, HomesteadLocalization.Text("hs_store_no_balance_withdraw"));
        }

        List<ZoneBlueprintStorePriceItem> payoutItems = ZoneBlueprintStorePrices.CreatePayoutItems(coins, materials);
        Vector3 payoutPosition = position;
        Quaternion payoutRotation = rotation;
        if (!ZoneBlueprintStorePlacement.TryReadOptionalStoreChestTarget(request.Target, position, rotation, out bool useTarget, out payoutPosition, out payoutRotation, out reason))
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.Withdraw, reason);
        }

        HomesteadCommandResult preflight = ZoneBlueprintStoreChestPrefab.PreflightPayoutChests(payoutItems, seller.PlatformId);
        if (!preflight.Success)
        {
            return ZoneBlueprintStoreDtos.Fail(ZoneBlueprintStoreRpcType.Withdraw, preflight.Message);
        }

        ZoneBlueprintStoreCatalog rollbackCatalog = ZoneBlueprintStoreDraftRepository.CloneCatalog(catalog);
        ClearBalances(balances, playerName);
        if (!ZoneBlueprintStoreDraftRepository.TrySaveCatalogImmediate(catalog, out string saveReason))
        {
            return FailWithCatalogRecovery(saveReason, rollbackCatalog, "withdraw debit save");
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
            return FailWithCatalogRecovery(payoutResult.Message, rollbackCatalog, "withdraw payout placement");
        }

        return ZoneBlueprintStoreRpcTransport.CreateEnvelope(ZoneBlueprintStoreRpcType.WithdrawComplete, new ZoneBlueprintStoreWithdrawResponse
        {
            Success = true,
            Message = HomesteadLocalization.Format("hs_store_withdraw_complete", payoutResult.Message, ZoneBlueprintStorePrices.FormatBalance(coins, materials)),
            Chests = chestTransforms
        });
    }

    private static ZoneBlueprintStoreRpcEnvelope FailWithCatalogRecovery(
        string failure,
        ZoneBlueprintStoreCatalog rollbackCatalog,
        string operation)
    {
        ZoneBlueprintStoreDraftRepository.CatalogRecoveryStatus recovery =
            ZoneBlueprintStoreDraftRepository.RestoreCatalogAfterFailedMutation(rollbackCatalog, operation);
        string key = recovery switch
        {
            ZoneBlueprintStoreDraftRepository.CatalogRecoveryStatus.RestoredDurably => "hs_store_catalog_recovery_saved",
            ZoneBlueprintStoreDraftRepository.CatalogRecoveryStatus.QueuedForRetry => "hs_store_catalog_recovery_queued",
            _ => "hs_store_catalog_recovery_failed"
        };
        return ZoneBlueprintStoreDtos.Fail(
            ZoneBlueprintStoreRpcType.Withdraw,
            HomesteadLocalization.Format(key, failure));
    }

    private static void ClearBalances(IEnumerable<ZoneBlueprintStoreBalance> balances, string sellerName)
    {
        foreach (ZoneBlueprintStoreBalance balance in balances)
        {
            balance.Coins = 0;
            balance.SellerName = sellerName;
            balance.Materials = [];
        }
    }

}
