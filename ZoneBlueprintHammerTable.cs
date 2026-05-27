using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintHammerTable
{
    private static readonly List<PieceTable> TempPieceTables = [];

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
        foreach (List<Piece> availablePieces in table.m_availablePieces)
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

        while (table.m_availablePieces.Count < requiredSlots)
        {
            table.m_availablePieces.Add([]);
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
        int availableIndex = (int)piece.m_category;
        if (availableIndex < 0 || availableIndex >= table.m_availablePieces.Count)
        {
            return;
        }

        List<Piece> availablePieces = table.m_availablePieces[availableIndex];
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
