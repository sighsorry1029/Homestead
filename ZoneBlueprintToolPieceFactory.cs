using System;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Homestead;

internal static class ZoneBlueprintToolPieceFactory
{
    private const string ToolObjectName = "Homestead_BlueprintSaveTool";
    private const string DismantleToolObjectName = "Homestead_AreaDismantleTool";
    private const string SnapPointToolObjectName = "Homestead_BlueprintSnapPointTool";
    private const string StoreToolObjectName = "Homestead_BlueprintStoreTool";
    private const string DataFolderToolObjectName = "Homestead_DataFolderTool";
    private const string BlueprintObjectPrefix = "Homestead_Blueprint_";

    public static string BlueprintPrefabName(string name)
    {
        return BlueprintObjectPrefix + SanitizePrefabName(name);
    }

    public static Piece CreateAreaSave(Piece.PieceCategory category)
    {
        Piece piece = CreateToolPiece(
            ToolObjectName,
            ZoneBlueprintToolKind.AreaSave,
            HomesteadLocalization.Token("hs_area_save_name"),
            FormatAreaSaveDescription(),
            category,
            ZoneBlueprintToolIcons.AreaSave());
        return piece;
    }

    public static void RefreshAreaSave(Piece piece)
    {
        piece.m_icon = ZoneBlueprintToolIcons.AreaSave();
        piece.m_description = FormatAreaSaveDescription();
    }

    public static Piece CreateAreaDismantle(Piece.PieceCategory category)
    {
        Piece piece = CreateToolPiece(
            DismantleToolObjectName,
            ZoneBlueprintToolKind.AreaDismantle,
            HomesteadLocalization.Token("hs_area_dismantle_name"),
            FormatAreaDismantleDescription(),
            category,
            ZoneBlueprintToolIcons.AreaDismantle());
        return piece;
    }

    public static void RefreshAreaDismantle(Piece piece)
    {
        piece.m_icon = ZoneBlueprintToolIcons.AreaDismantle();
        piece.m_description = FormatAreaDismantleDescription();
    }

    public static Piece CreateBlueprintSnapPoint(Piece.PieceCategory category)
    {
        return CreateToolPiece(
            SnapPointToolObjectName,
            ZoneBlueprintToolKind.BlueprintSnapPoint,
            HomesteadLocalization.Token("hs_blueprint_snappoint_name"),
            HomesteadLocalization.Token("hs_blueprint_snappoint_desc"),
            category,
            ZoneBlueprintToolIcons.BlueprintSnapPoint());
    }

    public static void RefreshBlueprintSnapPoint(Piece piece)
    {
        piece.m_icon = ZoneBlueprintToolIcons.BlueprintSnapPoint();
        piece.m_description = HomesteadLocalization.Token("hs_blueprint_snappoint_desc");
    }

    public static Piece CreateStore(Piece.PieceCategory category)
    {
        return CreateToolPiece(
            StoreToolObjectName,
            ZoneBlueprintToolKind.Store,
            HomesteadLocalization.Token("hs_blueprint_store_name"),
            HomesteadLocalization.Token("hs_blueprint_store_desc"),
            category,
            ZoneBlueprintToolIcons.Store());
    }

    public static void RefreshStore(Piece piece)
    {
        piece.m_icon = ZoneBlueprintToolIcons.Store();
        piece.m_description = HomesteadLocalization.Token("hs_blueprint_store_desc");
    }

    public static Piece CreateDataFolder(Piece.PieceCategory category)
    {
        return CreateToolPiece(
            DataFolderToolObjectName,
            ZoneBlueprintToolKind.DataFolder,
            HomesteadLocalization.Token("hs_data_folder_name"),
            HomesteadLocalization.Token("hs_data_folder_desc"),
            category,
            ZoneBlueprintToolIcons.DataFolder());
    }

    public static void RefreshDataFolder(Piece piece)
    {
        piece.m_icon = ZoneBlueprintToolIcons.DataFolder();
        piece.m_description = HomesteadLocalization.Token("hs_data_folder_desc");
    }

    public static Piece CreateBlueprint(
        string name,
        ZoneBlueprintFile blueprint,
        Piece.PieceCategory category,
        string storeListInputLabel,
        bool queueMissingIcon)
    {
        GameObject toolObject = new(BlueprintPrefabName(name));
        Object.DontDestroyOnLoad(toolObject);
        Piece piece = toolObject.AddComponent<Piece>();
        UpdateBlueprint(piece, name, blueprint, category, storeListInputLabel, queueMissingIcon);
        piece.m_enabled = true;
        piece.m_canRotate = false;
        piece.m_clipEverything = true;
        return piece;
    }

    public static void UpdateBlueprint(
        Piece piece,
        string name,
        ZoneBlueprintFile blueprint,
        Piece.PieceCategory category,
        string storeListInputLabel,
        bool queueMissingIcon)
    {
        ZoneBlueprintSaveToolMarker marker = piece.GetComponent<ZoneBlueprintSaveToolMarker>() ?? piece.gameObject.AddComponent<ZoneBlueprintSaveToolMarker>();
        marker.Kind = ZoneBlueprintToolKind.Blueprint;
        marker.BlueprintName = name;

        piece.m_name = name;
        piece.m_description = HomesteadLocalization.Format("hs_blueprint_piece_desc", blueprint.Entries.Count, storeListInputLabel);
        piece.m_category = category;
        piece.m_resources = Array.Empty<Piece.Requirement>();
        bool hasCachedIcon = ZoneBlueprintVisuals.TryGetIcon(name, out Sprite? icon);
        piece.m_icon = icon ?? ZoneBlueprintToolIcons.Fallback();
        if (!hasCachedIcon && queueMissingIcon)
        {
            ZoneBlueprintSaveTool.QueueIconRender(name);
        }
    }

    private static Piece CreateToolPiece(
        string objectName,
        ZoneBlueprintToolKind kind,
        string name,
        string description,
        Piece.PieceCategory category,
        Sprite icon)
    {
        GameObject toolObject = new(objectName);
        Object.DontDestroyOnLoad(toolObject);
        ZoneBlueprintSaveToolMarker marker = toolObject.AddComponent<ZoneBlueprintSaveToolMarker>();
        marker.Kind = kind;
        Piece piece = toolObject.AddComponent<Piece>();
        piece.m_name = name;
        piece.m_description = description;
        piece.m_category = category;
        piece.m_resources = Array.Empty<Piece.Requirement>();
        piece.m_icon = icon;
        piece.m_enabled = true;
        piece.m_canRotate = false;
        piece.m_clipEverything = true;
        return piece;
    }

    private static string FormatAreaSaveDescription()
    {
        return HomesteadLocalization.Format("hs_area_save_desc", ZoneAreaToolShared.FormatCompactShapeInput()) +
               "\n" +
               HomesteadLocalization.Text("hs_area_save_color_hint");
    }

    private static string FormatAreaDismantleDescription()
    {
        return HomesteadLocalization.Format("hs_area_dismantle_desc", ZoneAreaToolShared.FormatCompactShapeInput());
    }

    private static string SanitizePrefabName(string name)
    {
        char[] chars = name.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_').ToArray();
        string sanitized = new(chars);
        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed" : sanitized;
    }
}
