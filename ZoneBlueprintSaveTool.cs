using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Homestead;

internal sealed class ZoneBlueprintSaveTool : MonoBehaviour
{
    private const float MinSideLength = 2f;
    private const float SizeStep = 2f;
    private const float PreviewLift = 4f;
    private const int PreviewBuildBatchSize = 64;
    private const int SaveNameMaxLength = 64;
    private const float TargetOverlayRefreshInterval = 0.12f;
    private const float IconRenderIntervalSeconds = 0.5f;

    private static ManualLogSource? _logger;
    private static ZoneBlueprintSaveTool? _instance;
    private static float? _lastAreaYaw;

    private readonly List<GameObject> _previewVisuals = [];
    private readonly List<ZDO> _nearbyTargetZdos = [];
    private readonly List<ZDO> _targetCandidateZdos = [];
    private readonly Queue<string> _iconRenderQueue = new();
    private readonly HashSet<string> _queuedIconRenders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ZoneBlueprintFile> _queuedIconBlueprints = new(StringComparer.OrdinalIgnoreCase);
    private ZoneAreaToolController? _areaTool;
    private GameObject? _selectionPreviewRoot;
    private SaveSelection? _selection;
    private Coroutine? _iconRenderCoroutine;
    private Coroutine? _previewBuildCoroutine;
    private bool _active;
    private GameObject? _savePanel;
    private InputField? _saveNameInput;
    private Text? _saveCountText;
    private Text? _saveStatusText;
    private string _saveName = "";
    private string _saveStatus = "";
    private bool _focusSaveName;
    private bool _saveInputBlocked;
    private float _selectionPreviewYawOffset;

    public static bool HasSelection => _instance?._selection != null;
    public static bool IsActive => _instance?._areaTool?.Active == true;

    private static float MaxSelectableSide => Mathf.Max(MinSideLength, AreaToolConfig.BlueprintSaveMaxSide);

    private ZoneAreaToolController AreaTool => _areaTool ??= new ZoneAreaToolController(
        this,
        new ZoneAreaToolController.Options
        {
            MinSide = MinSideLength,
            SizeStep = SizeStep,
            MaxSide = () => MaxSelectableSide,
            DefaultWidth = () => AreaToolConfig.BlueprintSaveDefaultWidth,
            DefaultDepth = () => AreaToolConfig.BlueprintSaveDefaultDepth,
            Color = () => AreaToolConfig.BlueprintSaveColor,
            RangeLineName = "HomesteadBlueprintRadius",
            TargetOverlayName = "HomesteadAreaSaveTarget",
            TargetOverlayRefreshInterval = TargetOverlayRefreshInterval,
            GetSavedYaw = () => _lastAreaYaw,
            SetSavedYaw = yaw => _lastAreaYaw = yaw,
            IsLocked = () => _selection != null,
            GetEffectiveYaw = yaw => _selection == null
                ? yaw
                : ZoneAreaSelection.NormalizeYaw(yaw + _selectionPreviewYawOffset),
            OnLockedScroll = scroll =>
            {
                float deltaYaw = scroll > 0f ? PlacementControlConfig.RotationStep : -PlacementControlConfig.RotationStep;
                _selectionPreviewYawOffset = ZoneAreaSelection.NormalizeYaw(_selectionPreviewYawOffset + deltaYaw);
            },
            OnFrame = _ =>
            {
                UpdateSelectionPreview();
                UpdateSavePanel();
            },
            ShouldBlockToolInput = () => _selection != null && IsSaveNameInputFocused(),
            StatusTitle = () => _selection == null
                ? HomesteadLocalization.Text("hs_area_save_name")
                : HomesteadLocalization.Text("hs_area_save_preview_name"),
            FindCandidates = FindSaveBoundaryPreviewCandidates,
            OnClick = PickSelection
        });

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
        ZoneBlueprintSaveToolMenu.Initialize();
        EnsureInstance();
    }

    public static void Activate(Player player)
    {
        EnsureInstance();
        _instance?.ActivateInternal(player);
    }

    public static void Deactivate()
    {
        _instance?.DeactivateInternal();
    }

    public static void ClearSelection()
    {
        _instance?.ClearSelectionInternal();
    }

    public static void QueueMenuRefresh(string blueprintName)
    {
        EnsureInstance();
        ZoneBlueprintSaveToolMenu.RequestHammerTableRefresh(blueprintName);
    }

    public static void QueueIconRender(string blueprintName, ZoneBlueprintFile? blueprint = null)
    {
        EnsureInstance();
        _instance?.QueueIconRenderInternal(blueprintName, blueprint);
    }

    public static bool TryGetSelectedBlueprint(string name, Player player, out ZoneBlueprintFile blueprint, out string reason)
    {
        blueprint = null!;
        reason = "";

        if (_instance?._selection == null)
        {
            reason = HomesteadLocalization.Text("hs_blueprint_no_preview_selected");
            return false;
        }

        if (ZDOMan.instance == null)
        {
            reason = HomesteadLocalization.Text("hs_common_world_not_ready");
            return false;
        }

        SaveSelection selection = _instance._selection;
        List<ZDO> zdos = [];
        foreach (ZDOID id in selection.Zdos)
        {
            ZDO zdo = ZDOMan.instance.GetZDO(id);
            if (zdo != null && zdo.IsValid())
            {
                zdos.Add(zdo);
            }
        }

        if (zdos.Count == 0)
        {
            reason = HomesteadLocalization.Text("hs_blueprint_preview_invalid");
            return false;
        }

        blueprint = ZoneBlueprintCommands.CaptureBlueprintFromZdos(
            name,
            player,
            selection.Anchor,
            selection.AnchorRotation,
            zdos,
            selection.Radius);
        return true;
    }

    private static void EnsureInstance()
    {
        if (_instance != null && _instance)
        {
            return;
        }

        GameObject root = new("HomesteadBlueprintSaveTool");
        DontDestroyOnLoad(root);
        _instance = root.AddComponent<ZoneBlueprintSaveTool>();
    }

    private void ActivateInternal(Player player)
    {
        _active = true;
        AreaTool.Activate(player);
    }

    private void DeactivateInternal()
    {
        _active = false;
        _areaTool?.Deactivate();
        _saveStatus = "";
        _focusSaveName = false;
        _selectionPreviewYawOffset = 0f;
        SetSaveUiInputBlocked(false);
        SetSavePanelVisible(false);

        _selection = null;
        ClearPreviewLines();
    }

    private void ClearSelectionInternal()
    {
        _selection = null;
        _saveStatus = "";
        _focusSaveName = false;
        _selectionPreviewYawOffset = 0f;
        _areaTool?.ResetOffsets();
        SetSaveUiInputBlocked(false);
        SetSavePanelVisible(false);
        ClearPreviewLines();
        ZoneAreaToolStatusHud.Hide();
        Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, HomesteadLocalization.Text("hs_blueprint_preview_cleared"));
    }

    private void Update()
    {
        if (!_active && _areaTool?.Active != true)
        {
            return;
        }

        if (!AreaTool.Tick())
        {
            DeactivateInternal();
        }
    }

    private void OnDestroy()
    {
        SetSaveUiInputBlocked(false);
        DestroySavePanel();
        _areaTool?.Destroy();
        _areaTool = null;
        ClearPreviewLines();

        if (_instance == this)
        {
            _instance = null;
        }
    }

    private void PickSelection(Player player)
    {
        PickSelection(player, AreaTool.CurrentArea);
    }

    private void PickSelection(Player player, ZoneAreaSelection area)
    {
        if (!AreaTool.HasAimPoint)
        {
            return;
        }

        BlueprintAreaSaveCreatorMode creatorMode = BlueprintConfig.AreaSaveCreatorMode;
        List<ZDO> zdos = ZoneBlueprintCommands.FindBlueprintWearNTearZdos(player, area, creatorMode);
        if (zdos.Count == 0)
        {
            _selection = null;
            ClearPreviewLines();
            Message(player, $"No {BlueprintConfig.AreaSaveEligibleTargetLabel} found within {AreaTool.FormattedSize}.");
            return;
        }

        Quaternion anchorRotation = area.Rotation;
        _selection = new SaveSelection(
            area.Center,
            anchorRotation,
            area.HalfDiagonal,
            zdos.Select(zdo => zdo.m_uid).ToList());
        _selectionPreviewYawOffset = 0f;
        _saveName = GenerateDefaultBlueprintName();
        _saveStatus = "";
        _focusSaveName = false;
        EnsureSavePanel();
        RefreshSavePanel();
        SetSavePanelVisible(true);
        ReleaseSaveNameFocus();

        DrawSelectionPreview(zdos);
    }

    private void SaveSelectionFromUi()
    {
        Player player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }

        ZoneBundleCommandResult result = ZoneBlueprintCommands.SaveSelectedBlueprint(_saveName, player);
        _saveStatus = result.Success ? "Saved." : result.Message;
        Message(player, result.Message, result.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center);
        if (result.Success)
        {
            _selection = null;
            _selectionPreviewYawOffset = 0f;
            _focusSaveName = false;
            SetSaveUiInputBlocked(false);
            SetSavePanelVisible(false);
            ClearPreviewLines();
        }
    }

    private IReadOnlyList<ZDO> FindSaveBoundaryPreviewCandidates(Player player, ZoneAreaSelection area)
    {
        _targetCandidateZdos.Clear();
        if (ZDOMan.instance == null || ZNetScene.instance == null || player == null)
        {
            return _targetCandidateZdos;
        }

        long playerId = player.GetPlayerID();
        if (playerId == 0L)
        {
            return _targetCandidateZdos;
        }

        ZoneAreaTargetOverlay.CollectNearbyZdos(area, _nearbyTargetZdos);
        foreach (ZDO zdo in _nearbyTargetZdos)
        {
            if (ZoneBlueprintCommands.IsHomesteadBlueprintChest(zdo))
            {
                continue;
            }

            if (!IsLoadedWearNTear(zdo) ||
                !ZoneBlueprintCommands.TryReadSavableWearNTear(zdo, out _))
            {
                continue;
            }

            long creator = zdo.GetLong(ZDOVars.s_creator, 0L);
            if (BlueprintConfig.AreaSaveAllowsCreator(playerId, creator))
            {
                _targetCandidateZdos.Add(zdo);
            }
        }

        return _targetCandidateZdos;
    }

    private static bool IsLoadedWearNTear(ZDO zdo)
    {
        if (ZNetScene.instance == null)
        {
            return false;
        }

        ZNetView view = ZNetScene.instance.FindInstance(zdo);
        return view != null && view.GetComponent<WearNTear>() != null;
    }

    private void DrawSelectionPreview(IReadOnlyList<ZDO> zdos)
    {
        ClearPreviewLines();
        if (_selection == null)
        {
            return;
        }

        _selectionPreviewRoot = new GameObject("HomesteadBlueprintHeldPreview");
        _selectionPreviewRoot.transform.SetParent(transform, false);
        _selectionPreviewRoot.transform.position = AreaTool.AimPoint + Vector3.up * PreviewLift;
        _selectionPreviewRoot.transform.rotation = _selection.AnchorRotation;

        _previewBuildCoroutine = StartCoroutine(BuildSelectionPreview(zdos.ToList(), _selection, _selectionPreviewRoot));
    }

    private IEnumerator BuildSelectionPreview(IReadOnlyList<ZDO> zdos, SaveSelection selection, GameObject root)
    {
        Quaternion inverseAnchorRotation = Quaternion.Inverse(selection.AnchorRotation);

        int count = 0;
        foreach (ZDO zdo in zdos)
        {
            if (_selection != selection || _selectionPreviewRoot != root)
            {
                _previewBuildCoroutine = null;
                yield break;
            }

            if (!ZoneBlueprintCommands.TryReadSavableWearNTear(zdo, out GameObject prefab))
            {
                continue;
            }

            Vector3 scale = zdo.GetVec3(ZDOVars.s_scaleHash, prefab.transform.localScale);
            Vector3 localPosition = inverseAnchorRotation * (zdo.GetPosition() - selection.Anchor);
            Quaternion localRotation = inverseAnchorRotation * zdo.GetRotation();

            GameObject? visual = CreateVisualPreview(prefab, localPosition, localRotation, scale, count, _selectionPreviewRoot.transform);
            if (visual != null)
            {
                _previewVisuals.Add(visual);
            }

            count++;
            if (count % PreviewBuildBatchSize == 0)
            {
                yield return null;
            }
        }

        _previewBuildCoroutine = null;
    }

    private void UpdateSelectionPreview()
    {
        if (_selectionPreviewRoot == null || _selection == null)
        {
            return;
        }

        _selectionPreviewRoot.transform.position = AreaTool.AimPoint + Vector3.up * PreviewLift;
        _selectionPreviewRoot.transform.rotation = _selection.AnchorRotation * Quaternion.Euler(0f, _selectionPreviewYawOffset, 0f);
    }

    private void EnsureSavePanel()
    {
        if (_savePanel != null && _savePanel)
        {
            return;
        }

        if (GUIManager.CustomGUIFront == null)
        {
            return;
        }

        GUIManager gui = GUIManager.Instance;
        _savePanel = gui.CreateWoodpanel(
            GUIManager.CustomGUIFront.transform,
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            Vector2.zero,
            382f,
            214f,
            draggable: true);
        _savePanel.name = "HomesteadBlueprintSavePanel";

        Transform panel = _savePanel.transform;
        _ = gui.CreateText(
            HomesteadLocalization.Text("hs_blueprint_save_title"),
            panel,
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f),
            new Vector2(0f, -28f),
            gui.AveriaSerifBold,
            20,
            gui.ValheimOrange,
            outline: true,
            Color.black,
            320f,
            28f,
            addContentSizeFitter: false);

        _saveCountText = gui.CreateText(
            "",
            panel,
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f),
            new Vector2(0f, -62f),
            gui.AveriaSerif,
            15,
            gui.ValheimBeige,
            outline: true,
            Color.black,
            320f,
            24f,
            addContentSizeFitter: false).GetComponent<Text>();

        _ = gui.CreateText(
            HomesteadLocalization.Text("hs_blueprint_name_label"),
            panel,
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f),
            new Vector2(0f, -88f),
            gui.AveriaSerifBold,
            15,
            gui.ValheimOrange,
            outline: true,
            Color.black,
            320f,
            22f,
            addContentSizeFitter: false);

        GameObject inputObject = gui.CreateInputField(
            panel,
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f),
            new Vector2(0f, -122f),
            InputField.ContentType.Standard,
            HomesteadLocalization.Text("hs_blueprint_name_placeholder"),
            16,
            292f,
            34f);
        _saveNameInput = inputObject.GetComponent<InputField>();
        _saveNameInput.characterLimit = SaveNameMaxLength;
        _saveNameInput.onValueChanged.AddListener(value => _saveName = value);

        Button saveButton = gui.CreateButton(
            HomesteadLocalization.Text("hs_common_save"),
            panel,
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f),
            new Vector2(-76f, -166f),
            132f,
            34f).GetComponent<Button>();
        saveButton.onClick.AddListener(SaveSelectionFromUi);

        Button clearButton = gui.CreateButton(
            HomesteadLocalization.Text("hs_common_clear"),
            panel,
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f),
            new Vector2(76f, -166f),
            132f,
            34f).GetComponent<Button>();
        clearButton.onClick.AddListener(ClearSelectionInternal);

        _saveStatusText = gui.CreateText(
            "",
            panel,
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f),
            new Vector2(0f, -197f),
            gui.AveriaSerif,
            14,
            gui.ValheimYellow,
            outline: true,
            Color.black,
            320f,
            22f,
            addContentSizeFitter: false).GetComponent<Text>();

        RefreshSavePanel();
    }

    private void UpdateSavePanel()
    {
        if (_selection == null)
        {
            SetSaveUiInputBlocked(false);
            SetSavePanelVisible(false);
            return;
        }

        EnsureSavePanel();
        RefreshSavePanel();
        SetSavePanelVisible(_savePanel != null);

        bool shouldBlock = _saveNameInput != null && _saveNameInput.isFocused;
        SetSaveUiInputBlocked(shouldBlock);
        if (_focusSaveName && _saveNameInput != null)
        {
            _saveNameInput.ActivateInputField();
            _focusSaveName = false;
        }
    }

    private void RefreshSavePanel()
    {
        if (_savePanel == null)
        {
            return;
        }

        if (_saveNameInput != null && _saveNameInput.text != _saveName)
        {
            _saveNameInput.text = _saveName;
        }

        if (_saveCountText != null)
        {
            _saveCountText.text = HomesteadLocalization.Format("hs_blueprint_selected_count", _selection?.Zdos.Count ?? 0);
        }

        if (_saveStatusText != null)
        {
            _saveStatusText.text = _saveStatus;
        }
    }

    private void SetSavePanelVisible(bool visible)
    {
        if (_savePanel != null && _savePanel.activeSelf != visible)
        {
            _savePanel.SetActive(visible);
        }
    }

    private void DestroySavePanel()
    {
        if (_savePanel != null)
        {
            Destroy(_savePanel);
            _savePanel = null;
            _saveNameInput = null;
            _saveCountText = null;
            _saveStatusText = null;
        }
    }

    private void ReleaseSaveNameFocus()
    {
        if (_saveNameInput == null)
        {
            return;
        }

        _saveNameInput.DeactivateInputField();
    }

    private bool IsSaveNameInputFocused()
    {
        return _saveNameInput != null && _saveNameInput.isFocused;
    }

    private void SetSaveUiInputBlocked(bool blocked)
    {
        if (_saveInputBlocked == blocked)
        {
            return;
        }

        GUIManager.BlockInput(blocked);
        _saveInputBlocked = blocked;
    }

    private static string GenerateDefaultBlueprintName()
    {
        HashSet<string> existing;
        try
        {
            existing = ZoneBlueprintCommands.GetBlueprintNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            existing = [];
        }

        for (int index = 1; index <= 9999; index++)
        {
            string candidate = $"blueprint_{index:D3}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        return "blueprint_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
    }

    private GameObject? CreateVisualPreview(GameObject prefab, Vector3 localPosition, Quaternion localRotation, Vector3 scale, int index, Transform parent)
    {
        return ZoneBlueprintPreviewBuilder.CreateVisualPreview(prefab, localPosition, localRotation, scale, index, parent);
    }

    private void ClearPreviewLines()
    {
        if (_previewBuildCoroutine != null)
        {
            StopCoroutine(_previewBuildCoroutine);
            _previewBuildCoroutine = null;
        }

        if (_selectionPreviewRoot != null)
        {
            Destroy(_selectionPreviewRoot);
            _selectionPreviewRoot = null;
        }

        foreach (GameObject visual in _previewVisuals)
        {
            if (visual != null)
            {
                Destroy(visual);
            }
        }

        _previewVisuals.Clear();
    }

    private void QueueIconRenderInternal(string blueprintName, ZoneBlueprintFile? blueprint)
    {
        if (string.IsNullOrWhiteSpace(blueprintName))
        {
            return;
        }

        if (blueprint != null)
        {
            _queuedIconBlueprints[blueprintName] = blueprint;
        }

        if (!_queuedIconRenders.Add(blueprintName))
        {
            return;
        }

        _iconRenderQueue.Enqueue(blueprintName);
        _iconRenderCoroutine ??= StartCoroutine(RenderQueuedIcons());
    }

    private IEnumerator RenderQueuedIcons()
    {
        while (_iconRenderQueue.Count > 0)
        {
            Stopwatch renderTimer = Stopwatch.StartNew();
            string blueprintName = _iconRenderQueue.Dequeue();
            _queuedIconRenders.Remove(blueprintName);

            if (!_queuedIconBlueprints.TryGetValue(blueprintName, out ZoneBlueprintFile blueprint) &&
                !ZoneBlueprintCommands.TryLoadBlueprint(blueprintName, out blueprint))
            {
                _queuedIconBlueprints.Remove(blueprintName);
                yield return null;
                continue;
            }

            _queuedIconBlueprints.Remove(blueprintName);

            Sprite? icon = null;
            bool renderFinished = false;
            try
            {
                _ = ZoneBlueprintVisuals.EnqueueRenderAndCacheIcon(blueprintName, blueprint, renderedIcon =>
                {
                    icon = renderedIcon;
                    renderFinished = true;
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to render Homestead blueprint icon '{blueprintName}': {ex.Message}");
                renderFinished = true;
            }

            while (!renderFinished)
            {
                yield return null;
            }

            ZoneBlueprintSaveToolMenu.ApplyBlueprintIcon(blueprintName, icon);
            renderTimer.Stop();
            _logger?.LogDebug($"Homestead blueprint icon render completed for '{blueprintName}' in {renderTimer.Elapsed.TotalMilliseconds:0.0} ms; queued={_iconRenderQueue.Count}.");
            yield return new WaitForSecondsRealtime(IconRenderIntervalSeconds);
        }

        _iconRenderCoroutine = null;
    }

    private static void Message(Player player, string message)
    {
        Message(player, message, MessageHud.MessageType.TopLeft);
    }

    private static void Message(Player player, string message, MessageHud.MessageType type)
    {
        _logger?.LogInfo(message);
        player.Message(type, message);
    }

    private sealed class SaveSelection
    {
        public SaveSelection(Vector3 anchor, Quaternion anchorRotation, float radius, List<ZDOID> zdos)
        {
            Anchor = anchor;
            AnchorRotation = anchorRotation;
            Radius = radius;
            Zdos = zdos;
        }

        public Vector3 Anchor { get; }
        public Quaternion AnchorRotation { get; }
        public float Radius { get; }
        public List<ZDOID> Zdos { get; }
    }
}

internal sealed class ZoneBlueprintSaveToolMarker : MonoBehaviour
{
    public ZoneBlueprintToolKind Kind;
    public string BlueprintName = "";
}

internal enum ZoneBlueprintToolKind
{
    AreaSave,
    AreaDismantle,
    Blueprint,
    Store
}

internal static class ZoneBlueprintSaveToolMenu
{
    private const string CategoryLabel = "Homestead";
    private const string ToolObjectName = "Homestead_BlueprintSaveTool";
    private const string DismantleToolObjectName = "Homestead_AreaDismantleTool";
    private const string StoreToolObjectName = "Homestead_BlueprintStoreTool";
    private const string HammerTable = "Hammer";
    private const float BlueprintListRefreshCooldownSeconds = 3f;
    private const float HammerRefreshDelaySeconds = 0.08f;
    private const float HammerRefreshMinIntervalSeconds = 0.25f;
    private const int BlueprintPieceRegisterBudget = 2;

    private static readonly Dictionary<string, Piece> BlueprintPieces = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RegisteredPrefabs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> CachedBlueprintNames = [];
    private static readonly Queue<string> PendingBlueprintPieceNames = new();
    private static readonly HashSet<string> PendingBlueprintPieceNameSet = new(StringComparer.OrdinalIgnoreCase);
    private static Sprite? _areaSaveIcon;
    private static Sprite? _areaDismantleIcon;
    private static Sprite? _storeIcon;
    private static Sprite? _fallbackIcon;
    private static Piece? _toolPiece;
    private static Piece? _dismantleToolPiece;
    private static Piece? _storeToolPiece;
    private static int _lastStoreListFrame = -1;
    private static int _storeListIntentFrame = -1;
    private static string _storeListIntentBlueprintName = "";
    private static bool _initialized;
    private static bool _blueprintListDirty = true;
    private static bool _blueprintRefreshRequested;
    private static float _nextBlueprintListRefreshAt;
    private static bool _hammerRefreshPending;
    private static float _hammerRefreshAt;
    private static float _lastHammerRefreshAt = -999f;
    private static string _pendingHammerRefreshHighlightName = "";
    private static int _lastBlueprintPieceRegisterFrame = -1;
    private static int _blueprintPiecesRegisteredThisFrame;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        PieceManager.Instance.AddPieceCategory(CategoryLabel);
        EnsureToolPiece();
        EnsureDismantleToolPiece();
        EnsureStoreToolPiece();
    }

    public static void Update()
    {
        ZoneBlueprintStoreHoverPrompt.Update();
        ProcessPendingBlueprintPieceRefresh();
        ProcessPendingHammerRefresh();
    }

    public static bool IsToolPiece(Piece? piece)
    {
        return piece != null && piece.GetComponent<ZoneBlueprintSaveToolMarker>() != null;
    }

    public static void InvalidateBlueprint(string name)
    {
        ZoneBlueprintVisuals.InvalidateIcon(name);
        MarkBlueprintListDirty();
        if (BlueprintPieces.TryGetValue(name, out Piece piece) && piece)
        {
            if (ZoneBlueprintCommands.TryLoadBlueprint(name, out ZoneBlueprintFile blueprint))
            {
                UpdateBlueprintPiece(piece, name, blueprint);
            }
        }

        RefreshBlueprintPieces(forceScan: true);
    }

    public static void RefreshAfterBlueprintSaved(string name, ZoneBlueprintFile blueprint, bool iconReady)
    {
        if (!iconReady)
        {
            ZoneBlueprintVisuals.InvalidateIcon(name);
        }

        _ = EnsureBlueprintPiece(name, blueprint, queueMissingIcon: false);
        MarkBlueprintListDirty();
        RequestHammerTableRefresh(name);
        ZoneBlueprintSaveTool.QueueMenuRefresh(name);
        if (!iconReady)
        {
            ZoneBlueprintSaveTool.QueueIconRender(name, blueprint);
        }
    }

    public static void ApplyBlueprintIcon(string name, Sprite? icon)
    {
        if (icon == null || !BlueprintPieces.TryGetValue(name, out Piece piece) || piece == null || !piece)
        {
            return;
        }

        piece.m_icon = icon;
        RequestHammerTableRefresh(name);
    }

    public static void ForceRefreshLocalHammerTable(string? highlightName = null)
    {
        RequestHammerTableRefresh(highlightName);
    }

    public static void RequestHammerTableRefresh(string? highlightName = null)
    {
        if (!string.IsNullOrWhiteSpace(highlightName))
        {
            _pendingHammerRefreshHighlightName = highlightName!;
        }

        float now = Time.realtimeSinceStartup;
        float earliest = Mathf.Max(now + HammerRefreshDelaySeconds, _lastHammerRefreshAt + HammerRefreshMinIntervalSeconds);
        _hammerRefreshAt = _hammerRefreshPending ? Mathf.Min(_hammerRefreshAt, earliest) : earliest;
        _hammerRefreshPending = true;
    }

    private static void ProcessPendingHammerRefresh()
    {
        if (!_hammerRefreshPending || Time.realtimeSinceStartup < _hammerRefreshAt)
        {
            return;
        }

        string highlightName = _pendingHammerRefreshHighlightName;
        _pendingHammerRefreshHighlightName = "";
        _hammerRefreshPending = false;
        RefreshLocalHammerTableNow(highlightName);
    }

    private static void RefreshLocalHammerTableNow(string? highlightName = null)
    {
        Player player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }

        PieceTable table = player.m_buildPieces;
        if (table == null || !LooksLikeHammerTable(table))
        {
            return;
        }

        Stopwatch timer = Stopwatch.StartNew();
        EnsureToolPiece();
        EnsureDismantleToolPiece();
        EnsureStoreToolPiece();
        if (_toolPiece != null && _toolPiece)
        {
            EnsurePieceInTable(table, _toolPiece);
        }

        if (_dismantleToolPiece != null && _dismantleToolPiece)
        {
            EnsurePieceInTable(table, _dismantleToolPiece);
        }

        if (_storeToolPiece != null && _storeToolPiece)
        {
            EnsurePieceInTable(table, _storeToolPiece);
        }

        foreach (Piece piece in BlueprintPieces.Values.ToList())
        {
            if (piece != null && piece)
            {
                EnsurePieceInTable(table, piece);
            }
        }

        player.UpdateKnownRecipesList();

        if (highlightName is { Length: > 0 } name &&
            BlueprintPieces.TryGetValue(name, out Piece savedPiece) &&
            savedPiece != null &&
            savedPiece)
        {
            int categoryListIndex = table.m_categories.IndexOf(savedPiece.m_category);
            if (categoryListIndex >= 0)
            {
                table.SetCategory(categoryListIndex);
            }
        }

        player.UpdateAvailablePiecesList();
        RefreshVisiblePieceSelection(player);
        _lastHammerRefreshAt = Time.realtimeSinceStartup;
        timer.Stop();
        HomesteadPlugin.HomesteadLogger.LogDebug($"Homestead hammer table refresh completed in {timer.Elapsed.TotalMilliseconds:0.0} ms; blueprints={BlueprintPieces.Count}.");
    }

    public static bool IsToolSelected(Player player)
    {
        PieceTable table = player.m_buildPieces;
        return table != null && IsToolPiece(table.GetSelectedPiece());
    }

    public static bool IsStoreToolSelected(Player? player)
    {
        PieceTable? table = player?.m_buildPieces;
        return GetMarker(table?.GetSelectedPiece()) is { Kind: ZoneBlueprintToolKind.Store };
    }

    private static ZoneBlueprintSaveToolMarker? GetMarker(Piece? piece)
    {
        return piece == null ? null : piece.GetComponent<ZoneBlueprintSaveToolMarker>();
    }

    public static void RefreshBlueprintPieces(bool forceScan = false, bool processNow = true)
    {
        Initialize();
        if (ZNetScene.instance == null)
        {
            return;
        }

        if (forceScan)
        {
            MarkBlueprintListDirty();
        }

        _blueprintRefreshRequested = true;
        if (processNow)
        {
            ProcessPendingBlueprintPieceRefresh();
        }
    }

    private static void MarkBlueprintListDirty()
    {
        _blueprintListDirty = true;
        _nextBlueprintListRefreshAt = 0f;
        _blueprintRefreshRequested = true;
    }

    private static void ProcessPendingBlueprintPieceRefresh()
    {
        if (ZNetScene.instance == null)
        {
            return;
        }

        if (_blueprintRefreshRequested)
        {
            TryRefreshBlueprintNameCache();
        }

        if (_lastBlueprintPieceRegisterFrame != Time.frameCount)
        {
            _lastBlueprintPieceRegisterFrame = Time.frameCount;
            _blueprintPiecesRegisteredThisFrame = 0;
        }

        int remainingBudget = BlueprintPieceRegisterBudget - _blueprintPiecesRegisteredThisFrame;
        while (remainingBudget > 0 && PendingBlueprintPieceNames.Count > 0)
        {
            string name = PendingBlueprintPieceNames.Dequeue();
            PendingBlueprintPieceNameSet.Remove(name);
            if (BlueprintPieces.TryGetValue(name, out Piece cached) && cached)
            {
                continue;
            }

            Stopwatch timer = Stopwatch.StartNew();
            Piece? piece = EnsureBlueprintPiece(name);
            timer.Stop();
            remainingBudget--;
            _blueprintPiecesRegisteredThisFrame++;
            if (piece != null && piece)
            {
                RequestHammerTableRefresh(name);
                HomesteadPlugin.HomesteadLogger.LogDebug($"Homestead blueprint piece registered '{name}' in {timer.Elapsed.TotalMilliseconds:0.0} ms; pending={PendingBlueprintPieceNames.Count}.");
            }
        }
    }

    private static void TryRefreshBlueprintNameCache()
    {
        float now = Time.realtimeSinceStartup;
        if (!_blueprintListDirty && now < _nextBlueprintListRefreshAt)
        {
            return;
        }

        try
        {
            Stopwatch timer = Stopwatch.StartNew();
            List<string> names = ZoneBlueprintCommands.GetBlueprintNames();
            CachedBlueprintNames.Clear();
            CachedBlueprintNames.AddRange(names);
            int queued = 0;
            foreach (string name in CachedBlueprintNames)
            {
                if (BlueprintPieces.TryGetValue(name, out Piece cached) && cached ||
                    !PendingBlueprintPieceNameSet.Add(name))
                {
                    continue;
                }

                PendingBlueprintPieceNames.Enqueue(name);
                queued++;
            }

            _blueprintListDirty = false;
            _blueprintRefreshRequested = false;
            _nextBlueprintListRefreshAt = now + BlueprintListRefreshCooldownSeconds;
            timer.Stop();
            HomesteadPlugin.HomesteadLogger.LogDebug($"Homestead blueprint file scan completed in {timer.Elapsed.TotalMilliseconds:0.0} ms; names={names.Count}, queued={queued}.");
        }
        catch (Exception ex)
        {
            _blueprintRefreshRequested = false;
            _nextBlueprintListRefreshAt = now + BlueprintListRefreshCooldownSeconds;
            HomesteadPlugin.HomesteadLogger.LogDebug($"Could not refresh Homestead blueprint pieces yet: {ex.Message}");
        }
    }

    public static bool TrySelectTool(Player player)
    {
        PieceTable table = player.m_buildPieces;
        if (table == null || !LooksLikeHammerTable(table))
        {
            return false;
        }

        RefreshBlueprintPieces();
        player.UpdateAvailablePiecesList();
        if (_toolPiece == null)
        {
            return false;
        }

        int categoryListIndex = table.m_categories.IndexOf(_toolPiece.m_category);
        int categoryIndex = (int)_toolPiece.m_category;
        int pieceIndex = categoryIndex >= 0 && categoryIndex < table.m_availablePieces.Count
            ? table.m_availablePieces[categoryIndex].IndexOf(_toolPiece)
            : -1;
        if (categoryListIndex < 0 || pieceIndex < 0)
        {
            return false;
        }

        table.SetCategory(categoryListIndex);
        table.SetSelected(new Vector2Int(pieceIndex % 15, pieceIndex / 15));
        ZoneBlueprintSaveTool.Activate(player);
        ZoneAreaDismantleTool.Deactivate();
        ZoneBlueprintPlacementTool.Deactivate();
        Hud.HidePieceSelection();
        return true;
    }

    private static bool LooksLikeHammerTable(PieceTable table)
    {
        string name = table.name.ToLowerInvariant();
        if (name.Contains("hammer"))
        {
            return true;
        }

        return table.m_pieces.Any(piece => piece && Utils.GetPrefabName(piece).Equals("piece_repair", StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsurePieceInTable(PieceTable table, Piece piece)
    {
        if (piece == null || !piece || !piece.gameObject)
        {
            return;
        }

        if (!table.m_categories.Contains(piece.m_category))
        {
            table.m_categories.Add(piece.m_category);
        }

        if (!table.m_pieces.Contains(piece.gameObject))
        {
            table.m_pieces.Add(piece.gameObject);
        }
    }

    private static void RefreshVisiblePieceSelection(Player player)
    {
        Hud hud = Hud.instance;
        if (hud == null || !hud.m_buildHud.activeSelf)
        {
            return;
        }

        hud.m_lastPieceCategory = Piece.PieceCategory.Max;
        hud.UpdateBuild(player, forceUpdateAllBuildStatuses: true);
    }

    private static void EnsureToolPiece()
    {
        if (_toolPiece != null && _toolPiece)
        {
            _toolPiece.m_icon = GetAreaSaveIcon();
            _toolPiece.m_description = FormatAreaSaveDescription();
            RegisterWithJotunn(_toolPiece.gameObject);
            return;
        }

        GameObject toolObject = new(ToolObjectName);
        Object.DontDestroyOnLoad(toolObject);
        ZoneBlueprintSaveToolMarker marker = toolObject.AddComponent<ZoneBlueprintSaveToolMarker>();
        marker.Kind = ZoneBlueprintToolKind.AreaSave;
        Piece piece = toolObject.AddComponent<Piece>();
        piece.m_name = HomesteadLocalization.Token("hs_area_save_name");
        piece.m_description = FormatAreaSaveDescription();
        piece.m_category = PieceManager.Instance.AddPieceCategory(CategoryLabel);
        piece.m_resources = Array.Empty<Piece.Requirement>();
        piece.m_icon = GetAreaSaveIcon();
        piece.m_enabled = true;
        piece.m_canRotate = false;
        piece.m_clipEverything = true;
        _toolPiece = piece;
        RegisterWithJotunn(toolObject);
    }

    private static void EnsureDismantleToolPiece()
    {
        if (_dismantleToolPiece != null && _dismantleToolPiece)
        {
            _dismantleToolPiece.m_icon = GetAreaDismantleIcon();
            _dismantleToolPiece.m_description = FormatAreaDismantleDescription();
            RegisterWithJotunn(_dismantleToolPiece.gameObject);
            return;
        }

        GameObject toolObject = new(DismantleToolObjectName);
        Object.DontDestroyOnLoad(toolObject);
        ZoneBlueprintSaveToolMarker marker = toolObject.AddComponent<ZoneBlueprintSaveToolMarker>();
        marker.Kind = ZoneBlueprintToolKind.AreaDismantle;
        Piece piece = toolObject.AddComponent<Piece>();
        piece.m_name = HomesteadLocalization.Token("hs_area_dismantle_name");
        piece.m_description = FormatAreaDismantleDescription();
        piece.m_category = PieceManager.Instance.AddPieceCategory(CategoryLabel);
        piece.m_resources = Array.Empty<Piece.Requirement>();
        piece.m_icon = GetAreaDismantleIcon();
        piece.m_enabled = true;
        piece.m_canRotate = false;
        piece.m_clipEverything = true;
        _dismantleToolPiece = piece;
        RegisterWithJotunn(toolObject);
    }

    private static void EnsureStoreToolPiece()
    {
        if (_storeToolPiece != null && _storeToolPiece)
        {
            _storeToolPiece.m_icon = GetStoreIcon();
            RegisterWithJotunn(_storeToolPiece.gameObject);
            return;
        }

        GameObject toolObject = new(StoreToolObjectName);
        Object.DontDestroyOnLoad(toolObject);
        ZoneBlueprintSaveToolMarker marker = toolObject.AddComponent<ZoneBlueprintSaveToolMarker>();
        marker.Kind = ZoneBlueprintToolKind.Store;
        Piece piece = toolObject.AddComponent<Piece>();
        piece.m_name = HomesteadLocalization.Token("hs_blueprint_store_name");
        piece.m_description = HomesteadLocalization.Token("hs_blueprint_store_desc");
        piece.m_category = PieceManager.Instance.AddPieceCategory(CategoryLabel);
        piece.m_resources = Array.Empty<Piece.Requirement>();
        piece.m_icon = GetStoreIcon();
        piece.m_enabled = true;
        piece.m_canRotate = false;
        piece.m_clipEverything = true;
        _storeToolPiece = piece;
        RegisterWithJotunn(toolObject);
    }

    private static string FormatAreaSaveDescription()
    {
        return HomesteadLocalization.Format("hs_area_save_desc", "Wheel", FormatAreaRotationDescription(), "Mouse0") +
               "\n" +
               HomesteadLocalization.Text("hs_area_save_color_hint");
    }

    private static string FormatAreaDismantleDescription()
    {
        return HomesteadLocalization.Format("hs_area_dismantle_desc", "Wheel", FormatAreaRotationDescription(), "Mouse0");
    }

    private static string FormatAreaRotationDescription()
    {
        return string.IsNullOrWhiteSpace(BlueprintConfig.AreaToolRotationInputLabel)
            ? ""
            : HomesteadLocalization.Format("hs_area_rotate_suffix", BlueprintConfig.AreaToolRotationInputLabel);
    }

    private static Piece? EnsureBlueprintPiece(string name, ZoneBlueprintFile? loadedBlueprint = null, bool queueMissingIcon = true)
    {
        if (BlueprintPieces.TryGetValue(name, out Piece cached) && cached)
        {
            if (loadedBlueprint != null)
            {
                UpdateBlueprintPiece(cached, name, loadedBlueprint, queueMissingIcon);
            }

            RegisterWithJotunn(cached.gameObject);
            return cached;
        }

        ZoneBlueprintFile blueprint;
        if (loadedBlueprint != null)
        {
            blueprint = loadedBlueprint;
        }
        else if (!ZoneBlueprintCommands.TryLoadBlueprint(name, out blueprint))
        {
            return null;
        }

        GameObject toolObject = new("Homestead_Blueprint_" + SanitizePrefabName(name));
        Object.DontDestroyOnLoad(toolObject);
        ZoneBlueprintSaveToolMarker marker = toolObject.AddComponent<ZoneBlueprintSaveToolMarker>();
        marker.Kind = ZoneBlueprintToolKind.Blueprint;
        marker.BlueprintName = name;

        Piece piece = toolObject.AddComponent<Piece>();
        UpdateBlueprintPiece(piece, name, blueprint, queueMissingIcon);
        piece.m_enabled = true;
        piece.m_canRotate = false;
        piece.m_clipEverything = true;

        BlueprintPieces[name] = piece;
        RegisterWithJotunn(toolObject);
        return piece;
    }

    private static void UpdateBlueprintPiece(Piece piece, string name, ZoneBlueprintFile blueprint, bool queueMissingIcon = true)
    {
        ZoneBlueprintSaveToolMarker marker = piece.GetComponent<ZoneBlueprintSaveToolMarker>() ?? piece.gameObject.AddComponent<ZoneBlueprintSaveToolMarker>();
        marker.Kind = ZoneBlueprintToolKind.Blueprint;
        marker.BlueprintName = name;

        piece.m_name = name;
        piece.m_description = HomesteadLocalization.Format("hs_blueprint_piece_desc", blueprint.Entries.Count, GetStoreListInputLabel());
        piece.m_category = PieceManager.Instance.AddPieceCategory(CategoryLabel);
        piece.m_resources = Array.Empty<Piece.Requirement>();
        bool hasCachedIcon = ZoneBlueprintVisuals.TryGetIcon(name, out Sprite? icon);
        piece.m_icon = icon ?? GetFallbackIcon();
        if (!hasCachedIcon && queueMissingIcon)
        {
            ZoneBlueprintSaveTool.QueueIconRender(name);
        }
    }

    private static void RegisterWithJotunn(GameObject prefab)
    {
        if (!prefab || RegisteredPrefabs.Contains(prefab.name))
        {
            return;
        }

        Piece piece = prefab.GetComponent<Piece>();
        if (piece == null || piece.m_icon == null)
        {
            return;
        }

        CustomPiece customPiece = new(prefab, HammerTable, false)
        {
            Category = CategoryLabel
        };
        _ = PieceManager.Instance.AddPiece(customPiece);

        try
        {
            PieceManager.Instance.RegisterPieceInPieceTable(prefab, HammerTable, CategoryLabel);
        }
        catch
        {
            // Jotunn will register queued custom pieces when ObjectDB is ready.
        }

        RegisteredPrefabs.Add(prefab.name);
    }

    private static Sprite GetFallbackIcon()
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

    private static Sprite GetStoreIcon()
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

    private static Sprite GetAreaSaveIcon()
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

    private static Sprite GetAreaDismantleIcon()
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

    private static string SanitizePrefabName(string name)
    {
        char[] chars = name.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_').ToArray();
        string sanitized = new(chars);
        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed" : sanitized;
    }

    private static class ZoneBlueprintStoreHoverPrompt
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

    [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable))]
    private static class PieceTableUpdateAvailablePatch
    {
        [HarmonyPriority(Priority.High)]
        private static void Prefix(PieceTable __instance)
        {
            if (__instance != null && LooksLikeHammerTable(__instance))
            {
                RefreshBlueprintPieces(processNow: false);
            }
        }
    }

    private static void TryOpenStoreListing(string blueprintName)
    {
        _storeListIntentFrame = Time.frameCount;
        _storeListIntentBlueprintName = blueprintName ?? "";
        if (Time.frameCount == _lastStoreListFrame)
        {
            return;
        }

        _lastStoreListFrame = Time.frameCount;
        ZoneBlueprintStore.OpenSellDialog(blueprintName ?? "");
        Hud.HidePieceSelection();
    }

    private static bool TryHandleStoreListingIntent(ZoneBlueprintSaveToolMarker marker)
    {
        if (marker.Kind != ZoneBlueprintToolKind.Blueprint)
        {
            return false;
        }

        if (IsStoreListClickActive())
        {
            TryOpenStoreListing(marker.BlueprintName);
            DeactivateNormalBlueprintTools();
            return true;
        }

        if (IsRecentStoreListIntent(marker.BlueprintName))
        {
            DeactivateNormalBlueprintTools();
            return true;
        }

        return false;
    }

    private static bool IsRecentStoreListIntent(string blueprintName)
    {
        return Time.frameCount - _storeListIntentFrame <= 3 &&
               string.Equals(_storeListIntentBlueprintName, blueprintName ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private static void DeactivateNormalBlueprintTools()
    {
        ZoneBlueprintSaveTool.Deactivate();
        ZoneAreaDismantleTool.Deactivate();
        ZoneBlueprintPlacementTool.Deactivate();
    }

    private static bool IsStoreListClickDown()
    {
        return BlueprintConfig.IsStoreListModifierHeld() && Input.GetMouseButtonDown(0);
    }

    private static bool IsStoreListClickActive()
    {
        return BlueprintConfig.IsStoreListModifierHeld() && IsPrimaryClickDownOrHeld();
    }

    private static bool IsPrimaryClickDownOrHeld()
    {
        return Input.GetMouseButtonDown(0) || Input.GetMouseButton(0) || ZInput.GetButton("Attack");
    }

    private static bool IsAnyBlueprintMenuModifierHeld()
    {
        return Input.GetKey(KeyCode.LeftAlt) ||
               Input.GetKey(KeyCode.RightAlt) ||
               Input.GetKey(KeyCode.LeftControl) ||
               Input.GetKey(KeyCode.RightControl) ||
               Input.GetKey(KeyCode.LeftShift) ||
               Input.GetKey(KeyCode.RightShift) ||
               BlueprintConfig.IsStoreListModifierHeld();
    }

    private static string GetStoreListInputLabel()
    {
        string modifier = BlueprintConfig.StoreListModifierLabel;
        if (string.IsNullOrWhiteSpace(modifier) || modifier.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return "Click";
        }

        return $"{modifier}+Click";
    }

    [HarmonyPatch(typeof(Hud), nameof(Hud.SetupPieceInfo))]
    private static class HudSetupPieceInfoPatch
    {
        private static void Postfix(Hud __instance, Piece piece)
        {
            if (__instance == null || piece == null || __instance.m_hoveredPiece != piece)
            {
                return;
            }

            ZoneBlueprintSaveToolMarker? marker = GetMarker(piece);
            if (marker == null)
            {
                return;
            }

            if (marker.Kind != ZoneBlueprintToolKind.Blueprint || __instance.m_pieceDescription == null)
            {
                return;
            }

            ZoneBlueprintStoreHoverPrompt.Show(HomesteadLocalization.Format("hs_blueprint_store_hover", GetStoreListInputLabel()));
            if (IsStoreListClickDown())
            {
                TryOpenStoreListing(marker.BlueprintName);
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.SetSelectedPiece), new Type[] { typeof(Vector2Int) })]
    private static class PlayerSetSelectedPiecePatch
    {
        private static bool Prefix(Player __instance, Vector2Int p)
        {
            PieceTable table = __instance.m_buildPieces;
            if (table == null)
            {
                return true;
            }

            Piece piece = table.GetPiece(p);
            ZoneBlueprintSaveToolMarker? marker = GetMarker(piece);
            if (marker == null)
            {
                ZoneBlueprintStorePreviewTool.DeactivateActive();
                return true;
            }

            if (TryHandleStoreListingIntent(marker) ||
                marker.Kind == ZoneBlueprintToolKind.Blueprint && IsPrimaryClickDownOrHeld() && IsAnyBlueprintMenuModifierHeld())
            {
                return false;
            }

            ZoneBlueprintStorePreviewTool.DeactivateActive();
            table.SetSelected(p);
            __instance.m_placePressedTime = -9998f;
            if (marker.Kind == ZoneBlueprintToolKind.AreaSave)
            {
                ZoneAreaDismantleTool.Deactivate();
                ZoneBlueprintPlacementTool.Deactivate();
                ZoneBlueprintSaveTool.Activate(__instance);
            }
            else if (marker.Kind == ZoneBlueprintToolKind.AreaDismantle)
            {
                ZoneBlueprintSaveTool.Deactivate();
                ZoneBlueprintPlacementTool.Deactivate();
                ZoneAreaDismantleTool.Activate(__instance);
            }
            else if (marker.Kind == ZoneBlueprintToolKind.Store)
            {
                ZoneBlueprintSaveTool.Deactivate();
                ZoneAreaDismantleTool.Deactivate();
                ZoneBlueprintPlacementTool.Deactivate();
                ZoneBlueprintStore.Open(__instance);
            }
            else
            {
                ZoneBlueprintSaveTool.Deactivate();
                ZoneAreaDismantleTool.Deactivate();
                ZoneBlueprintPlacementTool.Activate(__instance, marker.BlueprintName);
            }

            Hud.HidePieceSelection();
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), "SetupPlacementGhost")]
    private static class PlayerSetupPlacementGhostPatch
    {
        private static bool Prefix(Player __instance)
        {
            ZoneBlueprintSaveToolMarker? marker = GetMarker(__instance.m_buildPieces?.GetSelectedPiece());
            if (marker == null)
            {
                ZoneBlueprintStorePreviewTool.DeactivateActive();
                ZoneBlueprintSaveTool.Deactivate();
                ZoneAreaDismantleTool.Deactivate();
                ZoneBlueprintPlacementTool.Deactivate();
                return true;
            }

            if (TryHandleStoreListingIntent(marker))
            {
                if (__instance.m_placementGhost != null)
                {
                    Object.Destroy(__instance.m_placementGhost);
                    __instance.m_placementGhost = null;
                }

                return false;
            }

            ZoneBlueprintStorePreviewTool.DeactivateActive();
            if (__instance.m_placementGhost != null)
            {
                Object.Destroy(__instance.m_placementGhost);
                __instance.m_placementGhost = null;
            }

            if (marker.Kind == ZoneBlueprintToolKind.AreaSave)
            {
                ZoneAreaDismantleTool.Deactivate();
                ZoneBlueprintPlacementTool.Deactivate();
                ZoneBlueprintSaveTool.Activate(__instance);
            }
            else if (marker.Kind == ZoneBlueprintToolKind.AreaDismantle)
            {
                ZoneBlueprintSaveTool.Deactivate();
                ZoneBlueprintPlacementTool.Deactivate();
                ZoneAreaDismantleTool.Activate(__instance);
            }
            else if (marker.Kind == ZoneBlueprintToolKind.Store)
            {
                ZoneBlueprintSaveTool.Deactivate();
                ZoneAreaDismantleTool.Deactivate();
                ZoneBlueprintPlacementTool.Deactivate();
                ZoneBlueprintStore.Open(__instance);
            }
            else
            {
                ZoneBlueprintSaveTool.Deactivate();
                ZoneAreaDismantleTool.Deactivate();
                ZoneBlueprintPlacementTool.Activate(__instance, marker.BlueprintName);
            }

            return false;
        }
    }
}
