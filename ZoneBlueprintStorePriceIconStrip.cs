using System;
using System.Collections.Generic;
using System.Linq;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace Homestead;

internal static class ZoneBlueprintStorePriceIconStrip
{
    private static readonly Dictionary<string, Sprite> ItemIconCache = new(StringComparer.OrdinalIgnoreCase);
    private static Sprite? _missingPriceIcon;

    public static void ResetForWorldSession()
    {
        // ObjectDB owns item sprites; only release the fallback sprite created here.
        ItemIconCache.Clear();
        if (_missingPriceIcon == null)
        {
            return;
        }

        Texture2D texture = _missingPriceIcon.texture;
        UnityEngine.Object.Destroy(_missingPriceIcon);
        if (texture != null)
        {
            UnityEngine.Object.Destroy(texture);
        }

        _missingPriceIcon = null;
    }

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
            icons[slot].sprite = GetItemIcon(item);
            icons[slot].preserveAspect = true;
            amounts[slot].text = FormatAmount(item.Amount);
        }
    }

    private static Sprite GetItemIcon(ZoneBlueprintStorePriceItem item)
    {
        string key = !string.IsNullOrWhiteSpace(item.PrefabName)
            ? item.PrefabName
            : !string.IsNullOrWhiteSpace(item.ItemName)
                ? item.ItemName
                : item.DisplayName ?? "";
        if (!string.IsNullOrWhiteSpace(key) && ItemIconCache.TryGetValue(key, out Sprite cached))
        {
            return cached;
        }

        GameObject? prefab = ZoneBlueprintStoreVisuals.FindItemPrefab(item.PrefabName);
        ItemDrop? drop = prefab ? prefab.GetComponent<ItemDrop>() : null;
        Sprite icon = drop != null ? drop.m_itemData.GetIcon() : GetMissingPriceIcon();
        if (!string.IsNullOrWhiteSpace(key))
        {
            ItemIconCache[key] = icon;
        }

        return icon;
    }

    private static Sprite GetMissingPriceIcon()
    {
        if (_missingPriceIcon != null)
        {
            return _missingPriceIcon;
        }

        Texture2D texture = new(16, 16, TextureFormat.RGBA32, false);
        Color dark = new(0.08f, 0.06f, 0.04f, 1f);
        Color light = new(1f, 0.72f, 0.18f, 1f);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                bool dot = (x - 8) * (x - 8) + (y - 8) * (y - 8) <= 36;
                texture.SetPixel(x, y, dot ? light : dark);
            }
        }

        texture.Apply();
        _missingPriceIcon = Sprite.Create(texture, new Rect(0f, 0f, 16f, 16f), new Vector2(0.5f, 0.5f), 16f);
        return _missingPriceIcon;
    }

    private static string FormatAmount(int amount)
    {
        return amount >= 1000 ? $"{amount / 1000f:0.#}k" : amount.ToString();
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
