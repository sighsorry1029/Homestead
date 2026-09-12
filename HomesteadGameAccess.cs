using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Homestead;

// Changed private game APIs: original 1.0.7 signatures, cached once, no per-frame lookup.
internal static class HomesteadGameAccess
{
    internal static readonly AccessTools.FieldRef<PieceTable, List<List<Piece>>> AvailableByCategory =
        AccessTools.FieldRefAccess<PieceTable, List<List<Piece>>>("m_availablePiecesByCategory");
    internal static readonly AccessTools.FieldRef<InventoryGrid, List<InventoryElement>> InventoryElements =
        AccessTools.FieldRefAccess<InventoryGrid, List<InventoryElement>>("m_elements");
    private static readonly Action<TerrainComp, bool> SaveTerrain = AccessTools.MethodDelegate<Action<TerrainComp, bool>>(
        AccessTools.Method(typeof(TerrainComp), "Save", new[] { typeof(bool) }));
    private static readonly Action<Inventory, bool, bool> Changed = AccessTools.MethodDelegate<Action<Inventory, bool, bool>>(
        AccessTools.Method(typeof(Inventory), "Changed", new[] { typeof(bool), typeof(bool) }));
    internal static readonly Func<VisEquipment, int, int, Transform, bool, bool, int, GameObject> AttachItem =
        AccessTools.MethodDelegate<Func<VisEquipment, int, int, Transform, bool, bool, int, GameObject>>(
            AccessTools.Method(typeof(VisEquipment), "AttachItem", new[] { typeof(int), typeof(int), typeof(Transform), typeof(bool), typeof(bool), typeof(int) }));
    internal static void PersistTerrain(TerrainComp compiler) => SaveTerrain(compiler, false);
    internal static void InventoryChanged(Inventory inventory) => Changed(inventory, false, false);
}
