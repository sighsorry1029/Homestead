using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.U2D;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Homestead;

// Only the controls used by Homestead. Atlas sprite copies are owned here;
// borrowed fonts, materials, atlases and fallback sprites are never destroyed.
internal sealed class HomesteadUi
{
    public static readonly HomesteadUi Instance = new();
    public static GameObject? CustomGUIFront { get; private set; }
    public readonly Color ValheimBeige = new(0.8529f, 0.725f, 0.5331f);
    public readonly Color ValheimOrange = new(1f, 0.631f, 0.235f);
    public readonly Color ValheimYellow = new(1f, 0.889f, 0f);
    public Font AveriaSerif { get; private set; } = null!;
    public Font AveriaSerifBold { get; private set; } = null!;
    private DefaultControls.Resources _resources;
    private Sprite? _wood;
    private Material? _woodMaterial;
    private GameObject? _buttonSound;
    private GameObject? _selectSound;
    private readonly List<Sprite> _ownedSprites = [];
    private static int _inputBlocks;
    internal static bool InputBlocked => _inputBlocks > 0;

    [HarmonyPatch(typeof(Hud), "Awake")]
    private static class HudAwakePatch
    {
        private static void Postfix(Hud __instance) => Instance.CreateRoot(__instance);
    }

    private void CreateRoot(Hud hud)
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null || CustomGUIFront) return;
        Transform? parent = null;
        foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (root.name == "GuiRoot") parent = root.transform.Find("GUI");
            else if (root.name == "_GameMain") parent = root.transform.Find("LoadingGUI");
            if (parent) break;
        }
        if (!parent) return;

        ReleaseSprites();
        Font[] fonts = Resources.FindObjectsOfTypeAll<Font>();
        AveriaSerif = fonts.FirstOrDefault(f => f.name == "AveriaSerifLibre-Regular") ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        AveriaSerifBold = fonts.FirstOrDefault(f => f.name == "AveriaSerifLibre-Bold") ?? AveriaSerif;
        Sprite[] sprites = Resources.FindObjectsOfTypeAll<Sprite>();
        SpriteAtlas[] atlases = Resources.FindObjectsOfTypeAll<SpriteAtlas>();
        SpriteAtlas? uiAtlas = atlases.FirstOrDefault(a => a.name == "UIAtlas");
        SpriteAtlas? iconAtlas = atlases.FirstOrDefault(a => a.name == "IconAtlas");
        _wood = FindSprite("woodpanel_trophys", uiAtlas, iconAtlas, sprites)
            ?? FindSprite("woodpanel_settings", uiAtlas, iconAtlas, sprites);
        _woodMaterial = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m => m.name == "litpanel");
        ButtonSfx? sound = hud.GetComponentsInChildren<ButtonSfx>(true).FirstOrDefault(s => s.m_sfxPrefab && s.m_selectSfxPrefab);
        _buttonSound = sound ? sound.m_sfxPrefab : null;
        _selectSound = sound ? sound.m_selectSfxPrefab : null;
        Sprite? inputField = FindSprite("text_field", uiAtlas, iconAtlas, sprites);
        _resources = new DefaultControls.Resources
        {
            standard = FindSprite("button", uiAtlas, iconAtlas, sprites),
            background = inputField,
            inputField = inputField
        };
        // Match Jotunn's independent in-world canvas. Nesting under the HUD
        // instead makes these panels inherit the game's HUD scaling preference.
        CustomGUIFront = new GameObject("HomesteadUI", typeof(RectTransform), typeof(Canvas), typeof(HomesteadCanvasScaler), typeof(GraphicRaycaster), typeof(GuiPixelFix));
        CustomGUIFront.layer = 5;
        CustomGUIFront.transform.SetParent(parent, false);
        CustomGUIFront.transform.SetAsLastSibling();
        RectTransform rect = (RectTransform)CustomGUIFront.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        Canvas canvas = CustomGUIFront.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 2000;
        canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.Normal | AdditionalCanvasShaderChannels.Tangent;
        CustomGUIFront.GetComponent<CanvasScaler>().referencePixelsPerUnit = 50f;
    }

    private Sprite? FindSprite(string name, SpriteAtlas? uiAtlas, SpriteAtlas? iconAtlas, Sprite[] fallback)
    {
        Sprite? sprite = uiAtlas ? uiAtlas.GetSprite(name) : null;
        if (!sprite && iconAtlas) sprite = iconAtlas.GetSprite(name);
        if (!sprite) return fallback.FirstOrDefault(candidate => candidate.name == name);
        _ownedSprites.Add(sprite);
        return sprite;
    }

    private void ReleaseSprites()
    {
        foreach (Sprite sprite in _ownedSprites)
            if (sprite) Object.Destroy(sprite);
        _ownedSprites.Clear();
        _resources = default;
        _wood = null;
    }

    public static void BlockInput(bool blocked)
    {
        _inputBlocks = Math.Max(0, _inputBlocks + (blocked ? 1 : -1));
        if (GameCamera.instance) GameCamera.instance.UpdateMouseCapture();
    }
    internal static void ResetInput() => _inputBlocks = 0;
    internal static void Shutdown()
    {
        ResetInput();
        if (CustomGUIFront) Object.Destroy(CustomGUIFront);
        CustomGUIFront = null;
        Instance.ReleaseSprites();
    }

    [HarmonyPatch(typeof(PlayerController), "TakeInput")]
    private static class PlayerInputPatch
    {
        private static void Postfix(ref bool __result) { if (InputBlocked) __result = false; }
    }
    [HarmonyPatch(typeof(TextInput), nameof(TextInput.IsVisible))]
    private static class VisibleInputPatch
    {
        private static void Postfix(ref bool __result) { if (InputBlocked) __result = true; }
    }
    private static GameObject Place(GameObject go, Transform parent, Vector2 min, Vector2 max, Vector2 position, float width, float height)
    {
        go.transform.SetParent(parent, false);
        go.layer = 5;
        RectTransform rect = (RectTransform)go.transform;
        rect.anchorMin = min;
        rect.anchorMax = max;
        rect.anchoredPosition = position;
        if (width > 0f) rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
        if (height > 0f) rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        return go;
    }

    public GameObject CreateText(string text, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 position,
        Font font, int fontSize, Color color, bool outline, Color outlineColor, float width, float height, bool addContentSizeFitter)
    {
        GameObject go = Place(DefaultControls.CreateText(_resources), parent, anchorMin, anchorMax, position, width, height);
        Text label = go.GetComponent<Text>();
        label.text = text;
        label.font = font;
        label.fontSize = fontSize;
        label.color = color;
        label.raycastTarget = false;
        if (outline) go.AddComponent<Outline>().effectColor = outlineColor;
        if (addContentSizeFitter)
        {
            ContentSizeFitter fitter = go.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }
        return go;
    }

    public GameObject CreateButton(string text, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 position, float width = 0f, float height = 0f)
    {
        GameObject go = Place(DefaultControls.CreateButton(_resources), parent, anchorMin, anchorMax, position, width, height);
        Text label = go.GetComponentInChildren<Text>();
        label.text = text;
        label.font = AveriaSerifBold;
        label.fontSize = 16;
        label.color = ValheimOrange;
        label.gameObject.AddComponent<Outline>().effectColor = Color.black;
        go.GetComponent<Button>().colors = new ColorBlock
        {
            normalColor = new Color(0.824f, 0.824f, 0.824f),
            highlightedColor = new Color(1.3f, 1.3f, 1.3f),
            pressedColor = new Color(0.537f, 0.556f, 0.556f),
            selectedColor = new Color(0.824f, 0.824f, 0.824f),
            disabledColor = new Color(0.566f, 0.566f, 0.566f, 0.502f),
            colorMultiplier = 1f, fadeDuration = 0.1f
        };
        ButtonSfx sound = go.AddComponent<ButtonSfx>();
        sound.m_sfxPrefab = _buttonSound;
        sound.m_selectSfxPrefab = _selectSound;
        return go;
    }

    public GameObject CreateInputField(Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 position,
        InputField.ContentType contentType = InputField.ContentType.Standard, string? placeholderText = null, int fontSize = 16, float width = 0f, float height = 0f)
    {
        GameObject go = Place(DefaultControls.CreateInputField(_resources), parent, anchorMin, anchorMax, position, width, height);
        InputField field = go.GetComponent<InputField>();
        field.contentType = contentType;
        foreach (Text label in go.GetComponentsInChildren<Text>())
        {
            label.font = AveriaSerifBold;
            label.fontSize = fontSize;
            label.color = label == field.textComponent ? Color.white : Color.grey;
            RectTransform rect = label.rectTransform;
            if (width > 0f) rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width - 20f);
            if (height > 0f) rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height - 10f);
        }
        field.textComponent.gameObject.AddComponent<Outline>().effectColor = Color.black;
        if (field.placeholder is Text placeholder) placeholder.text = placeholderText ?? "";
        return go;
    }

    public GameObject CreateWoodpanel(Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 position, float width = 0f, float height = 0f, bool draggable = true)
    {
        GameObject go = Place(DefaultControls.CreatePanel(_resources), parent, anchorMin, anchorMax, position, width, height);
        Image image = go.GetComponent<Image>();
        image.sprite = _wood;
        image.type = Image.Type.Sliced;
        image.material = _woodMaterial;
        image.color = _wood ? Color.white : new Color(0.12f, 0.10f, 0.07f, 0.97f);
        if (draggable) go.AddComponent<HomesteadPanelDrag>();
        return go;
    }
}

internal sealed class HomesteadCanvasScaler : CanvasScaler
{
    protected override void HandleConstantPixelSize()
    {
        // Preserve the former fixed pixel size at 1080p and above. At smaller
        // resolutions, fit the original layout so its bottom buttons stay usable.
        SetScaleFactor(Mathf.Min(scaleFactor, Mathf.Min(Screen.width / 1920f, Screen.height / 1080f)));
        SetReferencePixelsPerUnit(referencePixelsPerUnit);
    }
}

internal sealed class HomesteadPanelDrag : MonoBehaviour, IBeginDragHandler, IDragHandler
{
    private Vector2 _pointerOffset;

    public void OnBeginDrag(PointerEventData data)
    {
        _pointerOffset = data.position - (Vector2)transform.position;
    }

    public void OnDrag(PointerEventData data)
    {
        RectTransform rect = (RectTransform)transform;
        Vector2 position = data.position - _pointerOffset;
        Vector2 halfSize = Vector2.Scale(rect.rect.size, rect.lossyScale) * 0.5f;
        // Keep the same screen-bounds behavior as the former draggable panels.
        // Oversized panels stay centered instead of using an inverted clamp.
        position.x = halfSize.x * 2f >= Screen.width ? Screen.width * 0.5f : Mathf.Clamp(position.x, halfSize.x, Screen.width - halfSize.x);
        position.y = halfSize.y * 2f >= Screen.height ? Screen.height * 0.5f : Mathf.Clamp(position.y, halfSize.y, Screen.height - halfSize.y);
        rect.position = position;
    }
}
