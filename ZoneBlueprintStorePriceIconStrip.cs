using System.Collections.Generic;
using System.Linq;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace Homestead;

internal static class ZoneBlueprintStorePriceIconStrip
{
    public static void CreateSlots(
        GUIManager gui,
        Transform parent,
        int slotCount,
        Vector2 start,
        float columnStep,
        float rowStep,
        int columns,
        List<Image> icons,
        List<Text> amounts)
    {
        for (int slot = 0; slot < slotCount; slot++)
        {
            int column = slot % columns;
            int row = slot / columns;
            Vector2 position = new(start.x + column * columnStep, start.y + row * rowStep);
            Image icon = CreateImage(parent, $"PriceIcon{slot}", position, new Vector2(24f, 24f));
            Text amount = gui.CreateText("", icon.transform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(6f, -1f), gui.AveriaSerifBold, 11, Color.white, true, Color.black, 34f, 14f, false).GetComponent<Text>();
            icons.Add(icon);
            amounts.Add(amount);
        }
    }

    public static void Refresh(
        IReadOnlyList<ZoneBlueprintStorePriceItem> priceItems,
        int slotCount,
        List<Image> icons,
        List<Text> amounts)
    {
        List<ZoneBlueprintStorePriceItem> normalized = ZoneBlueprintStorePrices.NormalizePriceItems(priceItems).Take(slotCount).ToList();
        for (int slot = 0; slot < icons.Count; slot++)
        {
            bool active = slot < normalized.Count;
            icons[slot].gameObject.SetActive(active);
            amounts[slot].gameObject.SetActive(active);
            if (!active)
            {
                continue;
            }

            ZoneBlueprintStorePriceItem item = normalized[slot];
            icons[slot].sprite = ZoneBlueprintStoreUi.GetItemIcon(item);
            icons[slot].preserveAspect = true;
            amounts[slot].text = ZoneBlueprintStoreUi.FormatAmount(item.Amount);
        }
    }

    private static Image CreateImage(Transform parent, string name, Vector2 anchoredPosition, Vector2 size)
    {
        GameObject gameObject = new(name, typeof(RectTransform));
        gameObject.transform.SetParent(parent, false);
        RectTransform rect = gameObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;
        Image image = gameObject.AddComponent<Image>();
        image.raycastTarget = false;
        return image;
    }
}
