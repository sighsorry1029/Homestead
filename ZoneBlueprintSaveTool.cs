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
    private const float TargetOverlayRefreshInterval = 0.3f;
    private const float IconRenderIntervalSeconds = 1.25f;
    private const float IconRenderPlacementDelaySeconds = 1f;

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

    private static float MaxSelectableSide => Mathf.Max(MinSideLength, BlueprintConfig.AreaSaveMaxSide);

    private ZoneAreaToolController AreaTool => _areaTool ??= new ZoneAreaToolController(
        this,
        new ZoneAreaToolController.Options
        {
            MinSide = MinSideLength,
            SizeStep = SizeStep,
            MaxSide = () => MaxSelectableSide,
            DefaultWidth = () => BlueprintConfig.AreaSaveDefaultWidth,
            DefaultDepth = () => BlueprintConfig.AreaSaveDefaultDepth,
            Color = () => BlueprintConfig.AreaSaveBoundaryColor,
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
            reason = HomesteadLocalization.Format("hs_blueprint_no_preview_selected", "Wheel", ZoneAreaToolShared.FormatScaleInput(), ZoneAreaToolShared.FormatDepthInput(), ZoneAreaToolShared.FormatWidthInput());
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
            Message(player, HomesteadLocalization.Format("hs_blueprint_no_targets_in_area", BlueprintConfig.AreaSaveEligibleTargetLabel, AreaTool.FormattedSize));
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

        HomesteadCommandResult result = ZoneBlueprintCommands.SaveSelectedBlueprint(_saveName, player);
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

            if (!ZoneBlueprintCommands.TryReadSavableWearNTear(zdo, out _) ||
                !IsLoadedWearNTear(zdo))
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
            if (ShouldDelayIconRenderForPlacement())
            {
                yield return new WaitForSecondsRealtime(IconRenderPlacementDelaySeconds);
                continue;
            }

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

    private static bool ShouldDelayIconRenderForPlacement()
    {
        Player player = Player.m_localPlayer;
        return player != null &&
               player.InPlaceMode() &&
               !Hud.IsPieceSelectionVisible();
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

internal static class ZoneBlueprintSaveToolMenu
{
    private const string CategoryId = "Homestead";
    private const string CategoryLabelKey = "hs_hammer_category";
    private const string HammerTable = "Hammer";
    private const float BlueprintListRefreshCooldownSeconds = 15f;
    private const float HammerRefreshDelaySeconds = 0.08f;
    private const float HammerRefreshBulkDelaySeconds = 0.75f;
    private const float HammerRefreshMinIntervalSeconds = 0.25f;
    private const int BlueprintPieceRegisterBudget = 64;

    private static readonly Dictionary<string, Piece> BlueprintPieces = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RegisteredPrefabs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> CachedBlueprintNames = [];
    private static readonly Queue<string> PendingBlueprintPieceNames = new();
    private static readonly HashSet<string> PendingBlueprintPieceNameSet = new(StringComparer.OrdinalIgnoreCase);
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
    private static readonly ZoneHammerRefreshScheduler HammerRefreshScheduler = new(
        HammerRefreshDelaySeconds,
        HammerRefreshBulkDelaySeconds,
        HammerRefreshMinIntervalSeconds);
    private static bool _forceHammerRefreshOnNextTableUpdate;
    private static int _lastBlueprintPieceRegisterFrame = -1;
    private static int _blueprintPiecesRegisteredThisFrame;
    private static Piece.PieceCategory _homesteadCategory = Piece.PieceCategory.Max;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _ = HomesteadCategory;
        EnsureToolPiece();
        EnsureDismantleToolPiece();
        EnsureStoreToolPiece();
    }

    private static Piece.PieceCategory HomesteadCategory
    {
        get
        {
            if (_homesteadCategory == Piece.PieceCategory.Max)
            {
                _homesteadCategory = PieceManager.Instance.AddPieceCategory(CategoryId);
            }

            return _homesteadCategory;
        }
    }

    private static string CategoryLabel => HomesteadLocalization.Text(CategoryLabelKey);

    public static void Update()
    {
        ZoneBlueprintDirectoryWatcher.Update(ProcessBlueprintDirectoryChange);
        ZoneBlueprintStoreHoverPrompt.Update();
        ProcessPendingBlueprintPieceRefresh();
        ProcessPendingHammerRefresh();
    }

    public static void ResetForWorldSession()
    {
        ClearBlueprintPiecesForWorldSession();
        CachedBlueprintNames.Clear();
        PendingBlueprintPieceNames.Clear();
        PendingBlueprintPieceNameSet.Clear();
        ZoneBlueprintDirectoryWatcher.Reset();
        _blueprintListDirty = true;
        _blueprintRefreshRequested = true;
        _nextBlueprintListRefreshAt = 0f;
        HammerRefreshScheduler.Reset();
        _lastBlueprintPieceRegisterFrame = -1;
        _blueprintPiecesRegisteredThisFrame = 0;
        _forceHammerRefreshOnNextTableUpdate = true;
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

        Piece? savedPiece = EnsureBlueprintPiece(name, blueprint, queueMissingIcon: false);
        MarkBlueprintListDirty();
        RefreshBlueprintPieces(forceScan: true, processNow: true);
        if (savedPiece != null && savedPiece)
        {
            ForceRefreshLocalHammerTableNow(name);
        }
        else
        {
            RequestHammerTableRefresh(name);
        }

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
        // Background icon renders are picked up by the next normal hammer UI refresh.
        // Forcing a full HUD rebuild here scales with every hammer piece from every mod.
    }

    public static void ForceRefreshLocalHammerTable(string? highlightName = null)
    {
        RequestHammerTableRefresh(highlightName);
    }

    public static void RequestHammerTableRefresh(string? highlightName = null)
    {
        RequestHammerTableRefresh(highlightName, deferForBatch: false);
    }

    private static void ForceRefreshLocalHammerTableNow(string? highlightName)
    {
        HammerRefreshScheduler.ClearPending();
        if (RefreshLocalHammerTableNow(highlightName))
        {
            HammerRefreshScheduler.MarkCompleted();
        }
        else
        {
            RequestHammerTableRefresh(highlightName);
        }
    }

    private static void RequestHammerTableRefresh(string? highlightName, bool deferForBatch)
    {
        HammerRefreshScheduler.Request(highlightName, deferForBatch);
    }

    private static void ProcessPendingHammerRefresh()
    {
        if (!HammerRefreshScheduler.TryConsumeDue(out string highlightName))
        {
            return;
        }

        if (RefreshLocalHammerTableNow(highlightName))
        {
            HammerRefreshScheduler.MarkCompleted();
        }
    }

    private static bool RefreshLocalHammerTableNow(string? highlightName = null)
    {
        Player player = Player.m_localPlayer;
        if (player == null)
        {
            return false;
        }

        PieceTable table = player.m_buildPieces;
        if (table == null || !ZoneBlueprintHammerTable.LooksLike(table))
        {
            return false;
        }

        Stopwatch timer = Stopwatch.StartNew();
        EnsureToolPiece();
        EnsureDismantleToolPiece();
        EnsureStoreToolPiece();
        bool tableChanged = false;
        bool visibleSelectionChanged = false;
        if (_toolPiece != null && _toolPiece)
        {
            tableChanged |= ZoneBlueprintHammerTable.EnsurePiece(table, _toolPiece, HomesteadCategory, CategoryLabel);
        }

        if (_dismantleToolPiece != null && _dismantleToolPiece)
        {
            tableChanged |= ZoneBlueprintHammerTable.EnsurePiece(table, _dismantleToolPiece, HomesteadCategory, CategoryLabel);
        }

        if (_storeToolPiece != null && _storeToolPiece)
        {
            tableChanged |= ZoneBlueprintHammerTable.EnsurePiece(table, _storeToolPiece, HomesteadCategory, CategoryLabel);
        }

        foreach (Piece piece in GetBlueprintPiecesInMenuOrder())
        {
            if (piece != null && piece)
            {
                tableChanged |= ZoneBlueprintHammerTable.EnsurePiece(table, piece, HomesteadCategory, CategoryLabel);
            }
        }

        bool pieceOrderChanged = SortHomesteadPiecesInPieceTable(table);
        if (tableChanged)
        {
            player.UpdateKnownRecipesList();
        }

        bool availableListRefreshNeeded = tableChanged || pieceOrderChanged || !string.IsNullOrWhiteSpace(highlightName);
        if (availableListRefreshNeeded)
        {
            player.UpdateAvailablePiecesList();
            EnsureBlueprintPiecesVisibleInHammerTable(table);
        }

        if (highlightName is { Length: > 0 } name &&
            BlueprintPieces.TryGetValue(name, out Piece savedPiece) &&
            savedPiece != null &&
            savedPiece)
        {
            ZoneBlueprintHammerTable.EnsureCategoryLabels(table, HomesteadCategory, CategoryLabel);
            int categoryListIndex = table.m_categories.IndexOf(savedPiece.m_category);
            if (categoryListIndex >= 0)
            {
                table.SetCategory(categoryListIndex);
                visibleSelectionChanged = true;
            }
        }

        availableListRefreshNeeded |= visibleSelectionChanged;
        if (availableListRefreshNeeded)
        {
            ZoneBlueprintHammerTable.RefreshVisibleSelection(player, HomesteadCategory, CategoryLabel);
        }

        timer.Stop();
        HomesteadPlugin.HomesteadLogger.LogDebug($"Homestead hammer table refresh completed in {timer.Elapsed.TotalMilliseconds:0.0} ms; blueprints={BlueprintPieces.Count}; tableChanged={tableChanged}; visibleSelectionChanged={visibleSelectionChanged}; availableListRefreshNeeded={availableListRefreshNeeded}.");
        return true;
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

        if (!forceScan &&
            !_blueprintListDirty &&
            PendingBlueprintPieceNames.Count == 0 &&
            Time.realtimeSinceStartup < _nextBlueprintListRefreshAt)
        {
            return;
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

        if (!_blueprintRefreshRequested && PendingBlueprintPieceNames.Count == 0)
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
        bool registeredAny = false;
        string refreshHighlightName = "";
        while (remainingBudget > 0 && PendingBlueprintPieceNames.Count > 0)
        {
            string name = PendingBlueprintPieceNames.Dequeue();
            PendingBlueprintPieceNameSet.Remove(name);
            if (BlueprintPieces.TryGetValue(name, out Piece cached) && cached)
            {
                registeredAny = true;
                continue;
            }

            Stopwatch timer = Stopwatch.StartNew();
            Piece? piece = EnsureBlueprintPiece(name);
            timer.Stop();
            remainingBudget--;
            _blueprintPiecesRegisteredThisFrame++;
            if (piece != null && piece)
            {
                registeredAny = true;
                refreshHighlightName = name;
                HomesteadPlugin.HomesteadLogger.LogDebug($"Homestead blueprint piece registered '{name}' in {timer.Elapsed.TotalMilliseconds:0.0} ms; pending={PendingBlueprintPieceNames.Count}.");
            }
        }

        if (registeredAny)
        {
            RequestHammerTableRefresh(refreshHighlightName, deferForBatch: PendingBlueprintPieceNames.Count > 0);
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
            HashSet<string> currentNames = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
            int removed = RemoveMissingBlueprintPieces(currentNames);
            RemoveStalePendingBlueprintPieces(currentNames);
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
            if (removed > 0)
            {
                ZoneBlueprintHammerTable.SanitizeLocalPlayerTables(removeBlueprintPieces: true);
                RequestHammerTableRefresh();
            }

            HomesteadPlugin.HomesteadLogger.LogDebug($"Homestead blueprint file scan completed in {timer.Elapsed.TotalMilliseconds:0.0} ms; names={names.Count}, queued={queued}, removed={removed}.");
        }
        catch (Exception ex)
        {
            _blueprintRefreshRequested = false;
            _nextBlueprintListRefreshAt = now + BlueprintListRefreshCooldownSeconds;
            HomesteadPlugin.HomesteadLogger.LogDebug($"Could not refresh Homestead blueprint pieces yet: {ex.Message}");
        }
    }

    private static void ProcessBlueprintDirectoryChange(IReadOnlyList<string> iconInvalidations)
    {
        foreach (string name in iconInvalidations)
        {
            ZoneBlueprintVisuals.InvalidateIcon(name);
            if (BlueprintPieces.TryGetValue(name, out Piece piece) &&
                piece != null &&
                piece &&
                ZoneBlueprintCommands.TryLoadBlueprint(name, out ZoneBlueprintFile blueprint))
            {
                UpdateBlueprintPiece(piece, name, blueprint, queueMissingIcon: false);
            }
        }

        MarkBlueprintListDirty();
        RequestHammerTableRefresh();
    }

    private static int RemoveMissingBlueprintPieces(HashSet<string> currentNames)
    {
        List<string> missing = BlueprintPieces.Keys
            .Where(name => !currentNames.Contains(name))
            .ToList();
        foreach (string name in missing)
        {
            if (BlueprintPieces.TryGetValue(name, out Piece piece))
            {
                BlueprintPieces.Remove(name);
                if (piece != null && piece && piece.gameObject != null && piece.gameObject)
                {
                    RegisteredPrefabs.Remove(piece.gameObject.name);
                    Object.Destroy(piece.gameObject);
                }
            }

            RegisteredPrefabs.Remove(ZoneBlueprintToolPieceFactory.BlueprintPrefabName(name));
            ZoneBlueprintVisuals.InvalidateIcon(name);
        }

        return missing.Count;
    }

    private static void RemoveStalePendingBlueprintPieces(HashSet<string> currentNames)
    {
        if (PendingBlueprintPieceNames.Count == 0)
        {
            return;
        }

        List<string> retained = [];
        PendingBlueprintPieceNameSet.Clear();
        while (PendingBlueprintPieceNames.Count > 0)
        {
            string name = PendingBlueprintPieceNames.Dequeue();
            if (currentNames.Contains(name) && PendingBlueprintPieceNameSet.Add(name))
            {
                retained.Add(name);
            }
        }

        foreach (string name in retained)
        {
            PendingBlueprintPieceNames.Enqueue(name);
        }
    }

    private static void ClearBlueprintPiecesForWorldSession()
    {
        ZoneBlueprintHammerTable.SanitizeLocalPlayerTables(removeBlueprintPieces: true);
        foreach (Piece piece in BlueprintPieces.Values)
        {
            if (piece != null && piece && piece.gameObject)
            {
                Object.Destroy(piece.gameObject);
            }
        }

        BlueprintPieces.Clear();
        RegisteredPrefabs.RemoveWhere(name => name.StartsWith("Homestead_Blueprint_", StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureBlueprintPiecesInHammerTable(PieceTable table)
    {
        if (table == null || !ZoneBlueprintHammerTable.LooksLike(table))
        {
            return;
        }

        ZoneBlueprintHammerTable.Sanitize(table, removeBlueprintPieces: false);
        RefreshBlueprintPieces(processNow: true);
        EnsureToolPiece();
        EnsureDismantleToolPiece();
        EnsureStoreToolPiece();

        if (_toolPiece != null && _toolPiece)
        {
            ZoneBlueprintHammerTable.EnsurePiece(table, _toolPiece, HomesteadCategory, CategoryLabel);
        }

        if (_dismantleToolPiece != null && _dismantleToolPiece)
        {
            ZoneBlueprintHammerTable.EnsurePiece(table, _dismantleToolPiece, HomesteadCategory, CategoryLabel);
        }

        if (_storeToolPiece != null && _storeToolPiece)
        {
            ZoneBlueprintHammerTable.EnsurePiece(table, _storeToolPiece, HomesteadCategory, CategoryLabel);
        }

        foreach (Piece piece in GetBlueprintPiecesInMenuOrder())
        {
            if (piece != null && piece)
            {
                ZoneBlueprintHammerTable.EnsurePiece(table, piece, HomesteadCategory, CategoryLabel);
            }
        }

        SortHomesteadPiecesInPieceTable(table);
    }

    private static void EnsureBlueprintPiecesVisibleInHammerTable(PieceTable table)
    {
        if (table == null || !ZoneBlueprintHammerTable.LooksLike(table))
        {
            return;
        }

        ZoneBlueprintHammerTable.EnsureCategoryLabels(table, HomesteadCategory, CategoryLabel);
        ZoneBlueprintHammerTable.EnsureAvailableCategorySlots(table);
        if (_toolPiece != null && _toolPiece)
        {
            ZoneBlueprintHammerTable.EnsurePieceVisible(table, _toolPiece, HomesteadCategory, CategoryLabel);
        }

        if (_dismantleToolPiece != null && _dismantleToolPiece)
        {
            ZoneBlueprintHammerTable.EnsurePieceVisible(table, _dismantleToolPiece, HomesteadCategory, CategoryLabel);
        }

        if (_storeToolPiece != null && _storeToolPiece)
        {
            ZoneBlueprintHammerTable.EnsurePieceVisible(table, _storeToolPiece, HomesteadCategory, CategoryLabel);
        }

        foreach (Piece piece in GetBlueprintPiecesInMenuOrder())
        {
            if (piece != null && piece)
            {
                ZoneBlueprintHammerTable.EnsurePieceVisible(table, piece, HomesteadCategory, CategoryLabel);
            }
        }

        SortHomesteadAvailablePiecesInHammerTable(table);
    }

    private static List<Piece> GetBlueprintPiecesInMenuOrder()
    {
        return BlueprintPieces
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Value)
            .Where(IsValidMenuPiece)
            .ToList();
    }

    private static List<Piece> GetHomesteadPiecesInMenuOrder()
    {
        List<Piece> pieces = [];
        AddMenuPiece(pieces, _toolPiece);
        AddMenuPiece(pieces, _dismantleToolPiece);
        AddMenuPiece(pieces, _storeToolPiece);
        foreach (Piece piece in GetBlueprintPiecesInMenuOrder())
        {
            AddMenuPiece(pieces, piece);
        }

        return pieces;
    }

    private static void AddMenuPiece(List<Piece> pieces, Piece? piece)
    {
        if (!IsValidMenuPiece(piece))
        {
            return;
        }

        Piece validPiece = piece!;
        if (!pieces.Contains(validPiece))
        {
            pieces.Add(validPiece);
        }
    }

    private static bool SortHomesteadPiecesInPieceTable(PieceTable table)
    {
        if (table == null || table.m_pieces == null)
        {
            return false;
        }

        List<Piece> orderedPieces = GetHomesteadPiecesInMenuOrder();
        if (orderedPieces.Count == 0)
        {
            return false;
        }

        List<GameObject> sorted = new(table.m_pieces.Count + orderedPieces.Count);
        List<GameObject> orderedObjects = [];
        int insertIndex = -1;
        for (int i = 0; i < table.m_pieces.Count; i++)
        {
            GameObject pieceObject = table.m_pieces[i];
            if (IsHomesteadPieceObject(pieceObject))
            {
                if (insertIndex < 0)
                {
                    insertIndex = sorted.Count;
                }

                continue;
            }

            sorted.Add(pieceObject);
        }

        foreach (Piece piece in orderedPieces)
        {
            if (IsValidMenuPiece(piece) && !orderedObjects.Contains(piece.gameObject))
            {
                orderedObjects.Add(piece.gameObject);
            }
        }

        if (orderedObjects.Count == 0)
        {
            return false;
        }

        if (insertIndex < 0)
        {
            insertIndex = sorted.Count;
        }

        sorted.InsertRange(Mathf.Clamp(insertIndex, 0, sorted.Count), orderedObjects);
        return ReplacePieceObjectsIfChanged(table.m_pieces, sorted);
    }

    private static bool SortHomesteadAvailablePiecesInHammerTable(PieceTable table)
    {
        if (table == null || table.m_availablePieces == null)
        {
            return false;
        }

        int availableIndex = (int)HomesteadCategory;
        if (availableIndex < 0 || availableIndex >= table.m_availablePieces.Count)
        {
            return false;
        }

        List<Piece> availablePieces = table.m_availablePieces[availableIndex];
        Piece? selectedPiece = GetMarker(table.GetSelectedPiece()) != null ? table.GetSelectedPiece() : null;
        List<Piece> sorted = new(availablePieces.Count + BlueprintPieces.Count + 3);
        foreach (Piece piece in availablePieces)
        {
            if (IsRepairPiece(piece))
            {
                AddMenuPiece(sorted, piece);
            }
        }

        foreach (Piece piece in GetHomesteadPiecesInMenuOrder())
        {
            AddMenuPiece(sorted, piece);
        }

        foreach (Piece piece in availablePieces)
        {
            if (!IsRepairPiece(piece) && !IsHomesteadPiece(piece) && IsValidMenuPiece(piece))
            {
                sorted.Add(piece);
            }
        }

        bool changed = ReplacePiecesIfChanged(availablePieces, sorted);
        if (selectedPiece != null && selectedPiece)
        {
            int selectedIndex = availablePieces.IndexOf(selectedPiece);
            if (selectedIndex >= 0)
            {
                table.SetSelected(new Vector2Int(selectedIndex % 15, selectedIndex / 15));
            }
        }

        return changed;
    }

    private static bool ReplacePieceObjectsIfChanged(List<GameObject> target, List<GameObject> sorted)
    {
        if (target.Count == sorted.Count)
        {
            bool same = true;
            for (int i = 0; i < target.Count; i++)
            {
                if (target[i] != sorted[i])
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return false;
            }
        }

        target.Clear();
        target.AddRange(sorted);
        return true;
    }

    private static bool ReplacePiecesIfChanged(List<Piece> target, List<Piece> sorted)
    {
        if (target.Count == sorted.Count)
        {
            bool same = true;
            for (int i = 0; i < target.Count; i++)
            {
                if (target[i] != sorted[i])
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return false;
            }
        }

        target.Clear();
        target.AddRange(sorted);
        return true;
    }

    private static bool IsHomesteadPieceObject(GameObject? pieceObject)
    {
        return pieceObject != null &&
               pieceObject &&
               pieceObject.GetComponent<ZoneBlueprintSaveToolMarker>() != null;
    }

    private static bool IsHomesteadPiece(Piece? piece)
    {
        return GetMarker(piece) != null;
    }

    private static bool IsRepairPiece(Piece? piece)
    {
        return IsValidMenuPiece(piece) &&
               string.Equals(Utils.GetPrefabName(piece!.gameObject), "piece_repair", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidMenuPiece(Piece? piece)
    {
        return piece != null &&
               piece &&
               piece.gameObject != null &&
               piece.gameObject;
    }

    public static bool TrySelectTool(Player player)
    {
        PieceTable table = player.m_buildPieces;
        if (table == null || !ZoneBlueprintHammerTable.LooksLike(table))
        {
            return false;
        }

        RefreshBlueprintPieces();
        player.UpdateAvailablePiecesList();
        if (_toolPiece == null)
        {
            return false;
        }

        EnsureBlueprintPiecesVisibleInHammerTable(table);
        int categoryListIndex = table.m_categories.IndexOf(_toolPiece.m_category);
        int availableIndex = (int)_toolPiece.m_category;
        int pieceIndex = availableIndex >= 0 && availableIndex < table.m_availablePieces.Count
            ? table.m_availablePieces[availableIndex].IndexOf(_toolPiece)
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

    private static void EnsureToolPiece()
    {
        if (_toolPiece != null && _toolPiece)
        {
            ZoneBlueprintToolPieceFactory.RefreshAreaSave(_toolPiece);
            RegisterWithJotunn(_toolPiece.gameObject);
            return;
        }

        _toolPiece = ZoneBlueprintToolPieceFactory.CreateAreaSave(HomesteadCategory);
        RegisterWithJotunn(_toolPiece.gameObject);
    }

    private static void EnsureDismantleToolPiece()
    {
        if (_dismantleToolPiece != null && _dismantleToolPiece)
        {
            ZoneBlueprintToolPieceFactory.RefreshAreaDismantle(_dismantleToolPiece);
            RegisterWithJotunn(_dismantleToolPiece.gameObject);
            return;
        }

        _dismantleToolPiece = ZoneBlueprintToolPieceFactory.CreateAreaDismantle(HomesteadCategory);
        RegisterWithJotunn(_dismantleToolPiece.gameObject);
    }

    private static void EnsureStoreToolPiece()
    {
        if (_storeToolPiece != null && _storeToolPiece)
        {
            ZoneBlueprintToolPieceFactory.RefreshStore(_storeToolPiece);
            RegisterWithJotunn(_storeToolPiece.gameObject);
            return;
        }

        _storeToolPiece = ZoneBlueprintToolPieceFactory.CreateStore(HomesteadCategory);
        RegisterWithJotunn(_storeToolPiece.gameObject);
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

        Piece piece = ZoneBlueprintToolPieceFactory.CreateBlueprint(name, blueprint, HomesteadCategory, GetStoreListInputLabel(), queueMissingIcon);
        BlueprintPieces[name] = piece;
        RegisterWithJotunn(piece.gameObject);
        return piece;
    }

    private static void UpdateBlueprintPiece(Piece piece, string name, ZoneBlueprintFile blueprint, bool queueMissingIcon = true)
    {
        ZoneBlueprintToolPieceFactory.UpdateBlueprint(piece, name, blueprint, HomesteadCategory, GetStoreListInputLabel(), queueMissingIcon);
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

        ZoneBlueprintSaveToolMarker? marker = prefab.GetComponent<ZoneBlueprintSaveToolMarker>();
        if (marker != null && marker.Kind == ZoneBlueprintToolKind.Blueprint)
        {
            RegisteredPrefabs.Add(prefab.name);
            return;
        }

        CustomPiece customPiece = new(prefab, HammerTable, false)
        {
            Category = CategoryId
        };
        _ = PieceManager.Instance.AddPiece(customPiece);

        try
        {
            PieceManager.Instance.RegisterPieceInPieceTable(prefab, HammerTable, CategoryId);
        }
        catch
        {
            // Jotunn will register queued custom pieces when ObjectDB is ready.
        }

        RegisteredPrefabs.Add(prefab.name);
    }

    [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable))]
    private static class PieceTableUpdateAvailablePatch
    {
        [HarmonyPriority(Priority.High)]
        private static void Prefix(PieceTable __instance)
        {
            if (__instance != null && ZoneBlueprintHammerTable.LooksLike(__instance))
            {
                EnsureBlueprintPiecesInHammerTable(__instance);
                if (_forceHammerRefreshOnNextTableUpdate)
                {
                    _forceHammerRefreshOnNextTableUpdate = false;
                    RequestHammerTableRefresh();
                }
            }
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(PieceTable __instance)
        {
            if (__instance != null && ZoneBlueprintHammerTable.LooksLike(__instance))
            {
                EnsureBlueprintPiecesVisibleInHammerTable(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(Hud), nameof(Hud.UpdateBuild))]
    private static class HudUpdateBuildCategoryLabelPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix()
        {
            Player? player = Player.m_localPlayer;
            PieceTable? table = player != null ? player.m_buildPieces : null;
            if (table != null && ZoneBlueprintHammerTable.LooksLike(table))
            {
                ZoneBlueprintHammerTable.EnsureCategoryLabels(table, HomesteadCategory, CategoryLabel);
            }
        }

        private static void Postfix(Hud __instance, Player player)
        {
            if (__instance == null || __instance.m_pieceDescription == null || player == null)
            {
                return;
            }

            Piece? selectedPiece = player.m_buildPieces != null ? player.m_buildPieces.GetSelectedPiece() : null;
            if (selectedPiece != null &&
                ZoneAreaRepair.TryBuildRepairPieceDescription(selectedPiece, out string repairDescription))
            {
                __instance.m_pieceDescription.text = repairDescription;
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.UpdateKnownRecipesList))]
    private static class PlayerUpdateKnownRecipesListCleanupPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(Player __instance)
        {
            if (__instance == Player.m_localPlayer)
            {
                ZoneBlueprintHammerTable.SanitizeLocalPlayerTables(removeBlueprintPieces: true);
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

            if (__instance.m_pieceDescription != null &&
                ZoneAreaRepair.TryBuildRepairPieceDescription(piece, out string repairDescription))
            {
                __instance.m_pieceDescription.text = repairDescription;
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
