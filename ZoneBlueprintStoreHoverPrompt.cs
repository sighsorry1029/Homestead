using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace Homestead;

internal static class ZoneBlueprintStoreHoverPrompt
{
    private static GameObject? _root;
    private static Text? _text;
    private static float _hideAt;

    public static void Show(string message)
    {
        Ensure();
        if (_root == null || _text == null)
        {
            return;
        }

        _text.text = Localization.instance.Localize(message);
        _root.SetActive(true);
        _hideAt = Time.unscaledTime + 0.12f;
        RectTransform rect = (RectTransform)_root.transform;
        Vector3 mouse = Input.mousePosition;
        const float width = 420f;
        const float height = 42f;
        Vector3 position = mouse + new Vector3(18f, 28f, 0f);
        position.x = Mathf.Clamp(position.x, 8f, Mathf.Max(8f, Screen.width - width - 8f));
        position.y = Mathf.Clamp(position.y, height * 0.5f + 8f, Screen.height - height * 0.5f - 8f);
        rect.position = position;
    }

    public static void Update()
    {
        if (_root != null && _root && _root.activeSelf && Time.unscaledTime > _hideAt)
        {
            _root.SetActive(false);
        }
    }

    private static void Ensure()
    {
        if (_root != null && _root && _text != null && _text)
        {
            return;
        }

        if (GUIManager.CustomGUIFront == null)
        {
            return;
        }

        GUIManager gui = GUIManager.Instance;
        _root = new GameObject("HomesteadBlueprintStoreHoverPrompt", typeof(RectTransform));
        _root.transform.SetParent(GUIManager.CustomGUIFront.transform, false);
        RectTransform rect = (RectTransform)_root.transform;
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 0f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.sizeDelta = new Vector2(420f, 42f);

        Image image = _root.AddComponent<Image>();
        image.color = new Color(0.02f, 0.02f, 0.02f, 0.82f);

        _text = gui.CreateText("", _root.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, gui.AveriaSerifBold, 17, gui.ValheimOrange, true, Color.black, 390f, 30f, false).GetComponent<Text>();
        _text.alignment = TextAnchor.MiddleCenter;
        _root.SetActive(false);
    }
}
