using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintToolIcons
{
    private static Sprite? _areaSaveIcon;
    private static Sprite? _areaDismantleIcon;
    private static Sprite? _blueprintSnapPointIcon;
    private static Sprite? _storeIcon;
    private static Sprite? _fallbackIcon;

    public static Sprite Fallback()
    {
        if (_fallbackIcon != null)
        {
            return _fallbackIcon;
        }

        Texture2D texture = new(2, 2, TextureFormat.RGBA32, false);
        texture.SetPixels([new Color(0.15f, 0.75f, 1f, 1f), new Color(0.05f, 0.2f, 0.35f, 1f), new Color(0.05f, 0.2f, 0.35f, 1f), new Color(0.15f, 0.75f, 1f, 1f)]);
        texture.Apply();
        _fallbackIcon = Sprite.Create(texture, new Rect(0f, 0f, 2f, 2f), new Vector2(0.5f, 0.5f));
        return _fallbackIcon;
    }

    public static Sprite Store()
    {
        if (_storeIcon != null)
        {
            return _storeIcon;
        }

        const int size = 64;
        Texture2D texture = new(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        Color clear = new(0f, 0f, 0f, 0f);
        Color panel = new(0.08f, 0.11f, 0.10f, 0.95f);
        Color ring = new(0.22f, 0.82f, 1f, 1f);
        Color ringSoft = new(0.22f, 0.82f, 1f, 0.34f);
        Color coinColor = new(1f, 0.72f, 0.18f, 1f);
        Color chest = new(0.62f, 0.35f, 0.14f, 1f);
        Color chestDark = new(0.21f, 0.12f, 0.06f, 1f);
        Color blueprint = new(0.22f, 0.78f, 1f, 1f);
        Color blueprintDark = new(0.04f, 0.20f, 0.26f, 1f);
        Vector2 center = new((size - 1) * 0.5f, (size - 1) * 0.5f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Vector2 p = new(x, y);
                float distance = Vector2.Distance(p, center);
                Color color = clear;
                if (distance <= 28f)
                {
                    color = panel;
                }

                if (distance is >= 21f and <= 25f)
                {
                    color = ring;
                }
                else if (distance is >= 17.5f and < 21f)
                {
                    color = Color.Lerp(color, ringSoft, 0.65f);
                }

                bool chestBody = x >= 17 && x <= 47 && y >= 18 && y <= 38;
                bool chestLid = x >= 20 && x <= 44 && y >= 37 && y <= 47;
                bool chestEdge = (chestBody || chestLid) && (x <= 19 || x >= 45 || y <= 20 || y >= 45 || y == 37);
                if (chestBody || chestLid)
                {
                    color = chestEdge ? chestDark : chest;
                }

                bool paper = x >= 25 && x <= 43 && y >= 25 && y <= 48;
                bool paperEdge = paper && (x <= 27 || x >= 41 || y <= 27 || y >= 46);
                if (paper)
                {
                    color = paperEdge ? blueprintDark : blueprint;
                }

                bool coin = (x - 46) * (x - 46) + (y - 18) * (y - 18) <= 36;
                if (coin)
                {
                    color = coinColor;
                }

                texture.SetPixel(x, y, color);
            }
        }

        texture.Apply();
        _storeIcon = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
        return _storeIcon;
    }

    public static Sprite AreaSave()
    {
        if (_areaSaveIcon != null)
        {
            return _areaSaveIcon;
        }

        const int size = 64;
        Texture2D texture = new(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        Color clear = new(0f, 0f, 0f, 0f);
        Color panel = new(0.05f, 0.12f, 0.14f, 0.95f);
        Color ring = new(1f, 0.74f, 0.22f, 1f);
        Color ringSoft = new(1f, 0.74f, 0.22f, 0.34f);
        Color piece = new(0.22f, 0.82f, 1f, 1f);
        Color pieceDark = new(0.04f, 0.22f, 0.28f, 1f);
        Vector2 center = new((size - 1) * 0.5f, (size - 1) * 0.5f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Vector2 p = new(x, y);
                float distance = Vector2.Distance(p, center);
                Color color = clear;
                if (distance <= 28f)
                {
                    color = panel;
                }

                if (distance is >= 21f and <= 25f)
                {
                    color = ring;
                }
                else if (distance is >= 17.5f and < 21f)
                {
                    color = Color.Lerp(color, ringSoft, 0.65f);
                }

                bool inPiece = x >= 24 && x <= 40 && y >= 23 && y <= 39;
                bool pieceBorder = inPiece && (x <= 26 || x >= 38 || y <= 25 || y >= 37);
                if (inPiece)
                {
                    color = pieceBorder ? piece : pieceDark;
                }

                bool crosshair = (Mathf.Abs(x - 32) <= 1 && y >= 11 && y <= 18) ||
                                 (Mathf.Abs(x - 32) <= 1 && y >= 46 && y <= 53) ||
                                 (Mathf.Abs(y - 32) <= 1 && x >= 11 && x <= 18) ||
                                 (Mathf.Abs(y - 32) <= 1 && x >= 46 && x <= 53);
                if (crosshair)
                {
                    color = ring;
                }

                texture.SetPixel(x, y, color);
            }
        }

        texture.Apply();
        _areaSaveIcon = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
        return _areaSaveIcon;
    }

    public static Sprite AreaDismantle()
    {
        if (_areaDismantleIcon != null)
        {
            return _areaDismantleIcon;
        }

        const int size = 64;
        Texture2D texture = new(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        Color clear = new(0f, 0f, 0f, 0f);
        Color panel = new(0.13f, 0.07f, 0.05f, 0.95f);
        Color ring = new(1f, 0.31f, 0.12f, 1f);
        Color ringSoft = new(1f, 0.31f, 0.12f, 0.34f);
        Color stack = new(0.86f, 0.68f, 0.42f, 1f);
        Color stackDark = new(0.28f, 0.17f, 0.09f, 1f);
        Color slash = new(1f, 0.92f, 0.7f, 1f);
        Vector2 center = new((size - 1) * 0.5f, (size - 1) * 0.5f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Vector2 p = new(x, y);
                float distance = Vector2.Distance(p, center);
                Color color = clear;
                if (distance <= 28f)
                {
                    color = panel;
                }

                if (distance is >= 21f and <= 25f)
                {
                    color = ring;
                }
                else if (distance is >= 17.5f and < 21f)
                {
                    color = Color.Lerp(color, ringSoft, 0.65f);
                }

                bool bottomStack = x >= 20 && x <= 44 && y >= 21 && y <= 29;
                bool middleStack = x >= 23 && x <= 47 && y >= 30 && y <= 38;
                bool topStack = x >= 17 && x <= 41 && y >= 39 && y <= 47;
                bool inStack = bottomStack || middleStack || topStack;
                bool stackEdge = inStack && (
                    x is 17 or 20 or 23 or 41 or 44 or 47 ||
                    y is 21 or 29 or 30 or 38 or 39 or 47);
                if (inStack)
                {
                    color = stackEdge ? stackDark : stack;
                }

                bool slashPixel = Mathf.Abs(y - (55 - x)) <= 1 && x >= 17 && x <= 47 && y >= 17 && y <= 47;
                if (slashPixel)
                {
                    color = slash;
                }

                texture.SetPixel(x, y, color);
            }
        }

        texture.Apply();
        _areaDismantleIcon = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
        return _areaDismantleIcon;
    }

    public static Sprite BlueprintSnapPoint()
    {
        if (_blueprintSnapPointIcon != null)
        {
            return _blueprintSnapPointIcon;
        }

        const int size = 64;
        Texture2D texture = new(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        Color clear = new(0f, 0f, 0f, 0f);
        Color panel = new(0.04f, 0.10f, 0.14f, 0.95f);
        Color ring = new(0.25f, 0.88f, 1f, 1f);
        Color ringSoft = new(0.25f, 0.88f, 1f, 0.34f);
        Color node = new(1f, 0.77f, 0.19f, 1f);
        Color nodeCore = new(1f, 0.96f, 0.72f, 1f);
        Vector2 center = new((size - 1) * 0.5f, (size - 1) * 0.5f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Vector2 p = new(x, y);
                float distance = Vector2.Distance(p, center);
                Color color = clear;
                if (distance <= 28f)
                {
                    color = panel;
                }

                if (distance is >= 21f and <= 25f)
                {
                    color = ring;
                }
                else if (distance is >= 17.5f and < 21f)
                {
                    color = Color.Lerp(color, ringSoft, 0.65f);
                }

                bool horizontal = Mathf.Abs(y - 32) <= 1 && x >= 16 && x <= 48;
                bool vertical = Mathf.Abs(x - 32) <= 1 && y >= 16 && y <= 48;
                if (horizontal || vertical)
                {
                    color = ring;
                }

                float nodeDistance = Vector2.Distance(p, center);
                if (nodeDistance <= 8f)
                {
                    color = node;
                }

                if (nodeDistance <= 3f)
                {
                    color = nodeCore;
                }

                texture.SetPixel(x, y, color);
            }
        }

        texture.Apply();
        _blueprintSnapPointIcon = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
        return _blueprintSnapPointIcon;
    }
}
