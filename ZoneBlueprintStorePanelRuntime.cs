using System;
using Jotunn.Managers;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintStorePanelRuntime
{
    private static int _inputBlockCount;

    public static bool BeginUpdate(
        GameObject? panel,
        ZoneBlueprintStorePanelKind panelKind,
        bool inputBlocked,
        Action<bool> setInputBlocked)
    {
        if (inputBlocked && !IsVisible(panel))
        {
            setInputBlocked(false);
        }

        if (!IsVisible(panel))
        {
            return false;
        }

        ZoneBlueprintStorePanelLayout.Apply(panel, panelKind);
        return true;
    }

    public static bool ConsumeEscape(Action close)
    {
        if (!Input.GetKeyDown(KeyCode.Escape))
        {
            return false;
        }

        close();
        return true;
    }

    public static void SetInputBlocked(ref bool panelBlocked, bool blocked)
    {
        if (panelBlocked == blocked)
        {
            return;
        }

        panelBlocked = blocked;
        if (blocked)
        {
            _inputBlockCount++;
            if (_inputBlockCount == 1)
            {
                GUIManager.BlockInput(true);
            }

            return;
        }

        _inputBlockCount = Math.Max(0, _inputBlockCount - 1);
        if (_inputBlockCount == 0)
        {
            GUIManager.BlockInput(false);
        }
    }

    public static void ResetInputBlocks()
    {
        _inputBlockCount = 0;
        GUIManager.BlockInput(false);
    }

    public static bool IsVisible(GameObject? panel)
    {
        return panel != null && panel && panel.activeInHierarchy;
    }
}
