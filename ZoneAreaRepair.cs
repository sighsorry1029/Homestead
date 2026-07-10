using System;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Homestead;

internal static class ZoneAreaRepair
{
    private const string AreaRepairGuid = "aruberuto.AreaRepair";
    private const string AzuAreaRepairGuid = "Azumatt.AzuAreaRepair";

    private static readonly List<Piece> CandidatePieces = [];
    private static ManualLogSource? _logger;
    private static bool _loggedExternalAreaRepair;

    private enum StopReason
    {
        None,
        Stamina,
        Durability
    }

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
    }

    public static bool TryBuildRepairPieceDescription(Piece piece, out string description)
    {
        description = "";
        if (!AreaRepairConfig.Enabled || HasExternalAreaRepair() || !IsRepairPiece(piece))
        {
            return false;
        }

        Player? player = Player.m_localPlayer;
        float baseRadius = AreaRepairConfig.BaseRadius;
        float comfortRadiusScale = AreaRepairConfig.ComfortRadiusScale;
        int comfort = GetComfortBonusLevel(player);

        if (comfortRadiusScale <= 0f)
        {
            return false;
        }

        if (comfort <= 0)
        {
            description = baseRadius > 0f
                ? HomesteadLocalization.Text("hs_area_repair_increase_range_desc")
                : HomesteadLocalization.Text("hs_area_repair_need_cozy_desc");
            return true;
        }

        float radius = GetRadius(comfort);
        if (radius <= 0f)
        {
            return false;
        }

        description = HomesteadLocalization.Format(
            "hs_area_repair_ready_desc",
            FormatMeters(radius),
            FormatMeters(baseRadius),
            FormatMeters(comfortRadiusScale),
            comfort,
            HomesteadLocalization.Text("hs_common_comfort"));
        return true;
    }

    private static bool TryAreaRepair(Player player, ItemDrop.ItemData toolItem, Piece repairPiece)
    {
        if (!AreaRepairConfig.Enabled ||
            HasExternalAreaRepair() ||
            toolItem == null ||
            repairPiece == null ||
            !player.InPlaceMode() ||
            !player.InRepairMode())
        {
            return false;
        }

        int comfort = GetComfortBonusLevel(player);
        float radius = GetRadius(comfort);
        if (radius <= 0f)
        {
            return false;
        }

        Piece hoveringPiece = player.GetHoveringPiece();
        Vector3 center = hoveringPiece ? hoveringPiece.transform.position : player.transform.position;
        CandidatePieces.Clear();
        Piece.GetAllPiecesInRadius(center, radius, CandidatePieces);
        CandidatePieces.Sort((left, right) =>
            (left.transform.position - center).sqrMagnitude.CompareTo((right.transform.position - center).sqrMagnitude));

        int repaired = 0;
        int noNeed = 0;
        int missingStation = 0;
        int noAccess = 0;
        StopReason stopReason = StopReason.None;

        try
        {
            foreach (Piece piece in CandidatePieces)
            {
                if (!piece)
                {
                    continue;
                }

                if (!HasRepairStamina(player, toolItem))
                {
                    stopReason = StopReason.Stamina;
                    break;
                }

                if (!HasToolDurability(toolItem))
                {
                    stopReason = StopReason.Durability;
                    break;
                }

                RepairAttempt attempt = TryRepairPiece(player, toolItem, piece);
                switch (attempt)
                {
                    case RepairAttempt.Repaired:
                        repaired++;
                        break;
                    case RepairAttempt.NoNeed:
                        noNeed++;
                        break;
                    case RepairAttempt.MissingStation:
                        missingStation++;
                        break;
                    case RepairAttempt.NoAccess:
                        noAccess++;
                        break;
                }
            }
        }
        finally
        {
            CandidatePieces.Clear();
        }

        ShowResult(player, repaired, noNeed, missingStation, noAccess, radius, stopReason);
        return true;
    }

    private static RepairAttempt TryRepairPiece(Player player, ItemDrop.ItemData toolItem, Piece piece)
    {
        if (!CanUseRepairStation(player, piece))
        {
            return RepairAttempt.MissingStation;
        }

        if (!PrivateArea.CheckAccess(piece.transform.position, 0f, flash: false, wardCheck: true))
        {
            return RepairAttempt.NoAccess;
        }

        WearNTear wearNTear = piece.GetComponent<WearNTear>();
        if (!wearNTear || !wearNTear.Repair())
        {
            return RepairAttempt.NoNeed;
        }

        player.FaceLookDirection();
        player.m_zanim.SetTrigger(toolItem.m_shared.m_attack.m_attackAnimation);
        piece.m_placeEffect.Create(piece.transform.position, piece.transform.rotation);
        player.UseStamina(player.GetBuildStamina());
        player.UseEitr(toolItem.m_shared.m_attack.m_attackEitr);
        if (toolItem.m_shared.m_useDurability)
        {
            toolItem.m_durability -= toolItem.m_shared.m_useDurabilityDrain;
        }

        return RepairAttempt.Repaired;
    }

    private static bool CanUseRepairStation(Player player, Piece piece)
    {
        if (player.m_noPlacementCost || !piece.m_craftingStation)
        {
            return true;
        }

        bool noWorkbench = ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench);
        return noWorkbench || CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name, player.transform.position);
    }

    private static bool HasRepairStamina(Player player, ItemDrop.ItemData toolItem)
    {
        return player.HaveStamina(toolItem.m_shared.m_attack.m_attackStamina);
    }

    private static bool HasToolDurability(ItemDrop.ItemData toolItem)
    {
        return !toolItem.m_shared.m_useDurability || toolItem.m_durability > 0f;
    }

    private static void ShowResult(
        Player player,
        int repaired,
        int noNeed,
        int missingStation,
        int noAccess,
        float radius,
        StopReason stopReason)
    {
        string message;
        if (repaired > 0)
        {
            message = stopReason switch
            {
                StopReason.Stamina => HomesteadLocalization.Format("hs_area_repair_stopped_stamina", repaired),
                StopReason.Durability => HomesteadLocalization.Format("hs_area_repair_stopped_durability", repaired),
                _ => HomesteadLocalization.Format("hs_area_repair_done", repaired, FormatMeters(radius))
            };
        }
        else if (missingStation > 0)
        {
            message = HomesteadLocalization.Text("hs_area_repair_missing_station");
        }
        else if (noAccess > 0)
        {
            message = HomesteadLocalization.Text("hs_area_repair_no_access");
        }
        else if (noNeed > 0)
        {
            message = HomesteadLocalization.Text("hs_area_repair_none_needed");
        }
        else
        {
            message = HomesteadLocalization.Text("hs_area_repair_no_pieces");
        }

        player.Message(MessageHud.MessageType.TopLeft, message);
    }

    private static float GetRadius(int comfort)
    {
        return AreaRepairConfig.BaseRadius + AreaRepairConfig.ComfortRadiusScale * Mathf.Pow(comfort, 1f / 3f);
    }

    private static int GetComfortBonusLevel(Player? player)
    {
        if (player == null || !IsComfortActive(player))
        {
            return 0;
        }

        return Mathf.Max(0, player.GetComfortLevel());
    }

    private static bool IsComfortActive(Player player)
    {
        SEMan seMan = player.GetSEMan();
        if (seMan == null)
        {
            return false;
        }

        if (seMan.HaveStatusEffect(SEMan.s_statusEffectResting))
        {
            return true;
        }

        bool nearFire = seMan.HaveStatusEffect(SEMan.s_statusEffectCampFire);
        bool shelterOrSitting = player.InShelter() || player.IsSitting();
        bool enemyAlert = player.IsSensed();
        bool coldOrFreezing = seMan.HaveStatusEffect(SEMan.s_statusEffectCold) || seMan.HaveStatusEffect(SEMan.s_statusEffectFreezing);
        bool burning = seMan.HaveStatusEffect(SEMan.s_statusEffectBurning);
        bool warmCozyArea = EffectArea.IsPointInsideArea(player.transform.position, EffectArea.Type.WarmCozyArea, 1f);
        bool wetWithoutWarmth = seMan.HaveStatusEffect(SEMan.s_statusEffectWet) && !warmCozyArea;
        return nearFire && shelterOrSitting && !enemyAlert && !coldOrFreezing && !wetWithoutWarmth && !burning;
    }

    private static bool IsRepairPiece(Piece piece)
    {
        return piece &&
               string.Equals(Utils.GetPrefabName(piece.gameObject), "piece_repair", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExternalAreaRepair()
    {
        bool external = Chainloader.PluginInfos.ContainsKey(AreaRepairGuid) ||
                        Chainloader.PluginInfos.ContainsKey(AzuAreaRepairGuid);
        if (external && !_loggedExternalAreaRepair)
        {
            _loggedExternalAreaRepair = true;
            _logger?.LogInfo("Homestead area repair is inactive because AreaRepair or AzuAreaRepair is installed.");
        }

        return external;
    }

    private static string FormatMeters(float value)
    {
        return Mathf.Approximately(value, Mathf.Round(value))
            ? Mathf.RoundToInt(value).ToString()
            : value.ToString("0.#");
    }

    private enum RepairAttempt
    {
        Repaired,
        NoNeed,
        MissingStation,
        NoAccess
    }

    [HarmonyPatch(typeof(Hud), nameof(Hud.SetupPieceInfo))]
    private static class HudSetupRepairPieceInfoPatch
    {
        private static void Postfix(Hud __instance, Piece piece)
        {
            if (__instance?.m_pieceDescription != null &&
                TryBuildRepairPieceDescription(piece, out string repairDescription))
            {
                __instance.m_pieceDescription.text = repairDescription;
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.Repair))]
    private static class PlayerRepairPatch
    {
        private static bool Prefix(Player __instance, ItemDrop.ItemData toolItem, Piece repairPiece)
        {
            try
            {
                return !TryAreaRepair(__instance, toolItem, repairPiece);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Homestead area repair failed: {ex}");
                return true;
            }
        }
    }
}
