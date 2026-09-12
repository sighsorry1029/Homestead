using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Homestead;

internal static class ZoneBlueprintHammerTable
{
    private static readonly List<PieceTable> TempPieceTables = [];

    internal static Piece.PieceCategory AllocateCategory()
    {
        int next = (int)Piece.PieceCategory.Max + 1;
        // One discovery at registration, not a per-frame Resources scan.
        foreach (PieceTable table in Resources.FindObjectsOfTypeAll<PieceTable>())
        {
            foreach (Piece.PieceCategory category in table.m_categories) next = Math.Max(next, (int)category + 1);
            foreach (GameObject go in table.m_pieces)
                if (go && go.TryGetComponent<Piece>(out var piece)) next = Math.Max(next, (int)piece.m_category + 1);
        }
        if (next > 512) throw new InvalidOperationException("Unexpected build category range.");
        return (Piece.PieceCategory)next;
    }

    private sealed class HomesteadPieceList : IPieceList
    {
        public string DisplayName => "Homestead";
        // BuildUi also uses this flag to retain its special repair button.
        public bool ShowTags => true;
        public bool CanCustomizeTags => false;
        public int TagCount => 0;
        public int TagSeparatorIndex => -1;
        public string GetTagDisplayName(int index) => "";
        public int GetTagIdByIndex(int index) => -1;
        public void UpdateAvailableTags(PieceTable pieceTable) { }
        public void GetAvailablePiecesWithTag(int tagId, PieceTable table, IList<Piece> result)
        {
            // m_pieces already carries Homestead's stable menu order.
            foreach (GameObject go in table.m_pieces)
                if (go && go.TryGetComponent<Piece>(out var piece) && table.m_availablePieces.Contains(piece) &&
                    (piece.GetComponent<ZoneBlueprintSaveToolMarker>() || piece.m_repairPiece)) result.Add(piece);
        }
    }

    [HarmonyPatch]
    private static class NativePieceListFilterPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ByUsagePieceList), nameof(ByUsagePieceList.GetAvailablePiecesWithTag));
            yield return AccessTools.Method(typeof(ByMaterialPieceList), nameof(ByMaterialPieceList.GetAvailablePiecesWithTag));
            yield return AccessTools.Method(typeof(RecentPieceList), nameof(RecentPieceList.GetAvailablePiecesWithTag));
            yield return AccessTools.Method(typeof(FavoritePieceList), nameof(FavoritePieceList.GetAvailablePiecesWithTag));
        }

        private static void Postfix(IList<Piece> resultOut)
        {
            for (int i = resultOut.Count - 1; i >= 0; i--)
            {
                ZoneBlueprintSaveToolMarker? marker = resultOut[i]
                    ? resultOut[i].GetComponent<ZoneBlueprintSaveToolMarker>()
                    : null;
                if (marker != null)
                {
                    resultOut.RemoveAt(i);
                }
            }
        }
    }

    [HarmonyPatch(typeof(BuildUi))]
    private static class BuildUiPatch
    {
        private static readonly AccessTools.FieldRef<BuildUi, List<IPieceList>> Lists = AccessTools.FieldRefAccess<BuildUi, List<IPieceList>>("m_pieceLists");
        private static readonly AccessTools.FieldRef<BuildUi, List<Button>> Buttons = AccessTools.FieldRefAccess<BuildUi, List<Button>>("m_tabButtons");
        private static readonly AccessTools.FieldRef<BuildUi, TabHandler> Tabs = AccessTools.FieldRefAccess<BuildUi, TabHandler>("m_tabHandler");
        [HarmonyPostfix, HarmonyPatch("Awake")]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(BuildUi __instance)
        {
            List<IPieceList> lists = Lists(__instance);
            if (lists.Any(list => list is HomesteadPieceList)) return;
            List<Button> buttons = Buttons(__instance);
            if (buttons.Count == 0) return;
            int index = lists.Count;
            Button button = UnityEngine.Object.Instantiate(buttons[0], buttons[0].transform.parent);
            button.name = "HomesteadTab";
            button.onClick = new Button.ButtonClickedEvent();
            foreach (TMP_Text text in button.GetComponentsInChildren<TMP_Text>(true)) text.text = "Homestead";
            button.onClick.AddListener(() => __instance.SelectPieceList(index));
            lists.Add(new HomesteadPieceList());
            buttons.Add(button);
            UnityEvent select = new();
            select.AddListener(() => __instance.SelectPieceList(index));
            Tabs(__instance).m_tabs.Add(new TabHandler.Tab { m_button = button, m_onClick = select });
        }

        [HarmonyPostfix, HarmonyPatch(nameof(BuildUi.OpenBuildMenu))]
        private static void AfterOpen(BuildUi __instance)
        {
            PieceTable? table = Player.m_localPlayer?.GetBuildTool();
            foreach (Button button in Buttons(__instance))
                if (button && button.name == "HomesteadTab") button.gameObject.SetActive(table && LooksLike(table));
        }
    }

    public static bool LooksLike(PieceTable table)
    {
        string name = table.name.ToLowerInvariant();
        if (name.Contains("hammer"))
        {
            return true;
        }

        return table.m_pieces.Any(piece => piece && Utils.GetPrefabName(piece).Equals("piece_repair", StringComparison.OrdinalIgnoreCase));
    }

    public static void SanitizeLocalPlayerTables(bool removeBlueprintPieces)
    {
        Player? player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }

        if (player.m_buildPieces != null && LooksLike(player.m_buildPieces))
        {
            Sanitize(player.m_buildPieces, removeBlueprintPieces);
        }

        TempPieceTables.Clear();
        player.m_inventory?.GetAllPieceTables(TempPieceTables);
        foreach (PieceTable table in TempPieceTables)
        {
            if (table != null && LooksLike(table))
            {
                Sanitize(table, removeBlueprintPieces);
            }
        }

        TempPieceTables.Clear();
    }

    public static void Sanitize(PieceTable table, bool removeBlueprintPieces)
    {
        if (table == null)
        {
            return;
        }

        table.m_pieces.RemoveAll(pieceObject => ShouldRemovePieceObject(pieceObject, removeBlueprintPieces));
        table.m_availablePieces.RemoveWhere(piece => ShouldRemoveAvailablePiece(piece, removeBlueprintPieces));
        table.m_enabledPieces.RemoveWhere(piece => ShouldRemoveAvailablePiece(piece, removeBlueprintPieces));
        foreach (List<Piece> availablePieces in HomesteadGameAccess.AvailableByCategory(table))
        {
            availablePieces.RemoveAll(piece => ShouldRemoveAvailablePiece(piece, removeBlueprintPieces));
        }
    }

    public static bool EnsurePiece(PieceTable table, Piece piece, Piece.PieceCategory homesteadCategory, string homesteadLabel)
    {
        if (piece == null || !piece || !piece.gameObject)
        {
            return false;
        }

        bool changed = false;
        if (!table.m_categories.Contains(piece.m_category))
        {
            table.m_categories.Add(piece.m_category);
            changed = true;
        }

        changed |= EnsureCategoryLabels(table, homesteadCategory, homesteadLabel);

        if (!table.m_pieces.Contains(piece.gameObject))
        {
            table.m_pieces.Add(piece.gameObject);
            changed = true;
        }

        return changed;
    }

    public static bool EnsureCategoryLabels(PieceTable table, Piece.PieceCategory homesteadCategory, string homesteadLabel)
    {
        bool changed = false;
        while (table.m_categoryLabels.Count < table.m_categories.Count)
        {
            int labelIndex = table.m_categoryLabels.Count;
            Piece.PieceCategory category = table.m_categories[labelIndex];
            table.m_categoryLabels.Add(category == homesteadCategory ? homesteadLabel : category.ToString());
            changed = true;
        }

        int homesteadIndex = table.m_categories.IndexOf(homesteadCategory);
        if (homesteadIndex >= 0 && homesteadIndex < table.m_categoryLabels.Count)
        {
            if (!string.Equals(table.m_categoryLabels[homesteadIndex], homesteadLabel, StringComparison.Ordinal))
            {
                table.m_categoryLabels[homesteadIndex] = homesteadLabel;
                changed = true;
            }

            changed |= MoveHomesteadCategoryToEnd(table, homesteadIndex, homesteadLabel);
        }

        return changed;
    }

    public static void EnsureAvailableCategorySlots(PieceTable table)
    {
        int requiredSlots = table.m_categories.Count;
        foreach (Piece.PieceCategory category in table.m_categories)
        {
            requiredSlots = Mathf.Max(requiredSlots, (int)category + 1);
        }

        while (HomesteadGameAccess.AvailableByCategory(table).Count < requiredSlots)
        {
            HomesteadGameAccess.AvailableByCategory(table).Add([]);
        }

        if (table.m_selectedPiece.Length < requiredSlots)
        {
            Array.Resize(ref table.m_selectedPiece, requiredSlots);
        }

        if (table.m_lastSelectedPiece.Length < requiredSlots)
        {
            Array.Resize(ref table.m_lastSelectedPiece, requiredSlots);
        }
    }

    public static void EnsurePieceVisible(PieceTable table, Piece piece, Piece.PieceCategory homesteadCategory, string homesteadLabel)
    {
        int categoryListIndex = table.m_categories.IndexOf(piece.m_category);
        if (categoryListIndex < 0)
        {
            if (EnsurePiece(table, piece, homesteadCategory, homesteadLabel))
            {
                EnsureAvailableCategorySlots(table);
            }

            categoryListIndex = table.m_categories.IndexOf(piece.m_category);
            if (categoryListIndex < 0)
            {
                return;
            }
        }

        EnsureAvailableCategorySlots(table);
        table.m_enabledPieces.Add(piece);
        table.m_availablePieces.Add(piece);
        int availableIndex = (int)piece.m_category;
        if (availableIndex < 0 || availableIndex >= HomesteadGameAccess.AvailableByCategory(table).Count)
        {
            return;
        }

        List<Piece> availablePieces = HomesteadGameAccess.AvailableByCategory(table)[availableIndex];
        if (!availablePieces.Contains(piece))
        {
            availablePieces.Add(piece);
        }
    }

    public static void RefreshVisibleSelection(Player player, Piece.PieceCategory homesteadCategory, string homesteadLabel)
    {
        Hud hud = Hud.instance;
        if (hud == null || !hud.m_buildHud.activeSelf)
        {
            return;
        }

        PieceTable table = player.m_buildPieces;
        if (table != null && LooksLike(table))
        {
            EnsureCategoryLabels(table, homesteadCategory, homesteadLabel);
        }

        hud.m_lastPieceCategory = Piece.PieceCategory.Max;
        hud.UpdateBuild(player, forceUpdateAllBuildStatuses: true);
    }

    private static bool ShouldRemovePieceObject(GameObject? pieceObject, bool removeBlueprintPieces)
    {
        if (pieceObject == null || !pieceObject)
        {
            return true;
        }

        Piece? piece;
        try
        {
            piece = pieceObject.GetComponent<Piece>();
        }
        catch
        {
            return true;
        }

        return ShouldRemoveAvailablePiece(piece, removeBlueprintPieces);
    }

    private static bool ShouldRemoveAvailablePiece(Piece? piece, bool removeBlueprintPieces)
    {
        if (piece == null || !piece || piece.gameObject == null || !piece.gameObject)
        {
            return true;
        }

        return removeBlueprintPieces &&
               piece.GetComponent<ZoneBlueprintSaveToolMarker>() is { Kind: ZoneBlueprintToolKind.Blueprint };
    }

    private static bool MoveHomesteadCategoryToEnd(PieceTable table, int homesteadIndex, string homesteadLabel)
    {
        int lastIndex = table.m_categories.Count - 1;
        if (homesteadIndex < 0 || homesteadIndex >= lastIndex)
        {
            return false;
        }

        Piece.PieceCategory category = table.m_categories[homesteadIndex];
        table.m_categories.RemoveAt(homesteadIndex);
        table.m_categories.Add(category);

        if (homesteadIndex < table.m_categoryLabels.Count)
        {
            string label = table.m_categoryLabels[homesteadIndex];
            table.m_categoryLabels.RemoveAt(homesteadIndex);
            table.m_categoryLabels.Add(label);
        }

        int finalIndex = table.m_categories.Count - 1;
        if (finalIndex >= 0 && finalIndex < table.m_categoryLabels.Count)
        {
            table.m_categoryLabels[finalIndex] = homesteadLabel;
        }

        return true;
    }
}
