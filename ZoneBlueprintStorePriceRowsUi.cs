using System;
using System.Collections.Generic;
using System.Linq;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace Homestead;

internal readonly struct ZoneBlueprintStorePriceRow
{
    public ZoneBlueprintStorePriceRow(InputField itemInput, InputField amountInput)
    {
        ItemInput = itemInput;
        AmountInput = amountInput;
    }

    public InputField ItemInput { get; }
    public InputField AmountInput { get; }
}

internal static class ZoneBlueprintStorePriceRowsUi
{
    private const int SlotCount = ZoneBlueprintStoreChest.MaxPriceItemTypes;

    public static void CreateColumnHeaders(GUIManager gui, Transform panel)
    {
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_item_prefab_or_name"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-126f, -88f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 300f, 20f, false);
        _ = gui.CreateText(HomesteadLocalization.Text("hs_store_amount"), panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(160f, -88f), gui.AveriaSerifBold, 13, gui.ValheimOrange, true, Color.black, 120f, 20f, false);
    }

    public static List<ZoneBlueprintStorePriceRow> CreateRows(GUIManager gui, Transform panel)
    {
        List<ZoneBlueprintStorePriceRow> rows = [];
        for (int i = 0; i < SlotCount; i++)
        {
            float y = -118f - i * 35f;
            InputField itemInput = gui.CreateInputField(panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-126f, y), InputField.ContentType.Standard, "", 13, 300f, 28f).GetComponent<InputField>();
            itemInput.characterLimit = 64;

            InputField amountInput = gui.CreateInputField(panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(160f, y), InputField.ContentType.IntegerNumber, "", 13, 120f, 28f).GetComponent<InputField>();
            amountInput.characterLimit = 9;

            rows.Add(new ZoneBlueprintStorePriceRow(itemInput, amountInput));
        }

        return rows;
    }

    public static void LoadRows(IReadOnlyList<ZoneBlueprintStorePriceRow> rows, IEnumerable<ZoneBlueprintStorePriceItem> priceItems)
    {
        ClearRows(rows);
        List<ZoneBlueprintStorePriceItem> normalized = ZoneBlueprintStorePrices.NormalizePriceItems(priceItems).Take(SlotCount).ToList();
        for (int i = 0; i < rows.Count && i < normalized.Count; i++)
        {
            rows[i].ItemInput.text = normalized[i].PrefabName;
            rows[i].AmountInput.text = normalized[i].Amount.ToString();
        }
    }

    public static void ClearRows(IEnumerable<ZoneBlueprintStorePriceRow> rows)
    {
        foreach (ZoneBlueprintStorePriceRow row in rows)
        {
            row.ItemInput.text = "";
            row.AmountInput.text = "";
        }
    }

    public static bool TryReadRows(
        IReadOnlyList<ZoneBlueprintStorePriceRow> rows,
        bool requirePrice,
        Action<string> setStatus,
        out List<ZoneBlueprintStorePriceItem> priceItems)
    {
        priceItems = [];
        foreach (ZoneBlueprintStorePriceRow row in rows)
        {
            string token = row.ItemInput.text.Trim();
            string amountText = row.AmountInput.text.Trim();
            if (string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(amountText))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                setStatus(HomesteadLocalization.Text("hs_store_item_required"));
                return false;
            }

            if (!int.TryParse(amountText, out int amount) || amount <= 0)
            {
                setStatus(HomesteadLocalization.Format("hs_store_amount_min", token));
                return false;
            }

            if (!ZoneBlueprintStorePrices.TryResolvePriceItem(token, amount, out ZoneBlueprintStorePriceItem item, out string reason))
            {
                setStatus(reason);
                return false;
            }

            priceItems.Add(item);
        }

        if (priceItems.Count == 0 && !requirePrice)
        {
            return true;
        }

        if (!ZoneBlueprintStorePrices.TryValidatePriceItems(priceItems, out priceItems, out string validationReason))
        {
            setStatus(validationReason);
            return false;
        }

        return true;
    }
}
