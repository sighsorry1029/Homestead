using HarmonyLib;
using UnityEngine;

namespace Homestead;

[HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.HaveBuildStationInRange))]
internal static class ZoneBuildCameraCraftingStationHaveBuildStationInRangePatch
{
    private static void Prefix(ref Vector3 point)
    {
        if (ZoneBuildCamera.TryGetBuildCameraOrigin(out Vector3 origin))
        {
            point = origin;
        }
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.PieceRayTest))]
internal static class ZoneBuildCameraPlayerPieceRayTestPatch
{
    private static void Prefix(Player __instance, ref ZoneBuildCamera.EyeOriginOverrideState __state)
    {
        __state = ZoneBuildCamera.BeginEyeOriginOverride(__instance);
    }

    private static void Postfix(Player __instance, ZoneBuildCamera.EyeOriginOverrideState __state)
    {
        ZoneBuildCamera.EndEyeOriginOverride(__instance, __state);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.UpdateWearNTearHover))]
internal static class ZoneBuildCameraPlayerUpdateWearNTearHoverPatch
{
    private static void Prefix(Player __instance, ref ZoneBuildCamera.EyeOriginOverrideState __state)
    {
        __state = ZoneBuildCamera.BeginEyeOriginOverride(__instance);
    }

    private static void Postfix(Player __instance, ZoneBuildCamera.EyeOriginOverrideState __state)
    {
        ZoneBuildCamera.EndEyeOriginOverride(__instance, __state);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.CopyPiece))]
internal static class ZoneBuildCameraPlayerCopyPiecePatch
{
    private static void Prefix(Player __instance, ref ZoneBuildCamera.EyeOriginOverrideState __state)
    {
        __state = ZoneBuildCamera.BeginEyeOriginOverride(__instance);
    }

    private static void Postfix(Player __instance, ZoneBuildCamera.EyeOriginOverrideState __state)
    {
        ZoneBuildCamera.EndEyeOriginOverride(__instance, __state);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.RemovePiece))]
internal static class ZoneBuildCameraPlayerRemovePiecePatch
{
    private static void Prefix(Player __instance, ref ZoneBuildCamera.EyeOriginOverrideState __state)
    {
        __state = ZoneBuildCamera.BeginEyeOriginOverride(__instance);
    }

    private static void Postfix(Player __instance, ZoneBuildCamera.EyeOriginOverrideState __state)
    {
        ZoneBuildCamera.EndEyeOriginOverride(__instance, __state);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.Update))]
internal static class ZoneBuildCameraPlayerUpdatePatch
{
    private static void Prefix(Player __instance, ref bool __runOriginal)
    {
        if (!ZoneBuildCamera.IsLocalPlayer(__instance) || !ZoneBuildCamera.InBuildMode())
        {
            return;
        }

        if (ZoneBuildCamera.ShouldDeactivateBuildMode(__instance))
        {
            ZoneBuildCamera.DisableBuildMode();
            return;
        }

        __runOriginal = false;
        ZoneBuildCamera.ApplyMaxPlaceDistanceOverride(__instance);

        if (ZoneBuildCamera.IsInputBlocked(blockPieceSelection: false) || !__instance.TakeInput())
        {
            return;
        }

        UpdateHotbarAndHideInputs(__instance);
        __instance.UpdatePlacement(takeInput: true, Time.deltaTime);
    }

    private static void Postfix(Player __instance)
    {
        if (!ZoneBuildCamera.IsLocalPlayer(__instance) || !ZoneBuildCamera.IsEnabled())
        {
            return;
        }

        if (!BuildCameraConfig.ToggleHotkey.IsDown())
        {
            return;
        }

        if (ZoneBuildCamera.IsInputBlocked(blockPieceSelection: true) || !__instance.TakeInput())
        {
            return;
        }

        if (ZoneBuildCamera.InBuildMode())
        {
            ZoneBuildCamera.DisableBuildMode();
            return;
        }

        if (!ZoneBuildCamera.ToolIsEquipped(__instance))
        {
            return;
        }

        if (!ZoneBuildCamera.BuildStationInRange(__instance))
        {
            return;
        }

        ZoneBuildCamera.EnableBuildMode();
    }

    private static void UpdateHotbarAndHideInputs(Player player)
    {
        if (Input.GetKeyDown(KeyCode.Alpha1) || ZInput.GetButtonDown("Hotbar1"))
        {
            player.UseHotbarItem(1);
        }

        if (Input.GetKeyDown(KeyCode.Alpha2) || ZInput.GetButtonDown("Hotbar2"))
        {
            player.UseHotbarItem(2);
        }

        if (Input.GetKeyDown(KeyCode.Alpha3) || ZInput.GetButtonDown("Hotbar3"))
        {
            player.UseHotbarItem(3);
        }

        if (Input.GetKeyDown(KeyCode.Alpha4) || ZInput.GetButtonDown("Hotbar4"))
        {
            player.UseHotbarItem(4);
        }

        if (Input.GetKeyDown(KeyCode.Alpha5) || ZInput.GetButtonDown("Hotbar5"))
        {
            player.UseHotbarItem(5);
        }

        if (Input.GetKeyDown(KeyCode.Alpha6) || ZInput.GetButtonDown("Hotbar6"))
        {
            player.UseHotbarItem(6);
        }

        if (Input.GetKeyDown(KeyCode.Alpha7) || ZInput.GetButtonDown("Hotbar7"))
        {
            player.UseHotbarItem(7);
        }

        if (Input.GetKeyDown(KeyCode.Alpha8) || ZInput.GetButtonDown("Hotbar8"))
        {
            player.UseHotbarItem(8);
        }

        if ((ZInput.GetButtonDown("Hide") || ZInput.GetButtonDown("JoyHide")) &&
            (player.GetRightItem() != null || player.GetLeftItem() != null) &&
            !player.InAttack())
        {
            player.HideHandItems();
        }
    }
}

[HarmonyPatch(typeof(PlayerController), nameof(PlayerController.TakeInput))]
internal static class ZoneBuildCameraPlayerControllerTakeInputPatch
{
    private static void Prefix(ref bool __result, ref bool __runOriginal)
    {
        if (!ZoneBuildCamera.InBuildMode())
        {
            return;
        }

        __result = false;
        __runOriginal = false;
    }
}

[HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateCamera))]
[HarmonyBefore("Azumatt.FirstPersonMode")]
[HarmonyPriority(Priority.VeryHigh)]
internal static class ZoneBuildCameraGameCameraUpdatePatch
{
    private static void Prefix(float dt, GameCamera __instance, ref bool __runOriginal)
    {
        if (!ZoneBuildCamera.InBuildMode())
        {
            return;
        }

        ZoneBuildCamera.UpdateBuildCamera(dt, __instance);
        if (ZoneBuildCameraWardCompat.CheckAccess(__instance.transform.position, flash: false))
        {
            ZoneBuildCamera.AutoPickup(dt, __instance);
        }

        __runOriginal = false;
    }
}
