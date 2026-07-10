using System;
using BepInEx.Logging;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Homestead;

internal sealed class ZoneBlueprintPlacementTool : MonoBehaviour
{
    private const float MaxPlacementDistance = 128f;

    private static ManualLogSource? _logger;
    private static ZoneBlueprintPlacementTool? _instance;

    private string _blueprintName = "";
    private ZoneBlueprintFile? _blueprint;
    private GameObject? _previewRoot;
    private GameObject? _chestPreviewRoot;
    private bool _active;
    private int _suppressInputFrames;
    private Vector3 _anchor;
    private Quaternion _anchorRotation = Quaternion.identity;
    private Quaternion _chestRotation = Quaternion.identity;
    private float _placementYaw;
    private float _heightOffset;
    private Vector3 _horizontalOffset;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
        EnsureInstance();
    }

    public static void Activate(Player player, string blueprintName)
    {
        EnsureInstance();
        _instance?.ActivateInternal(player, blueprintName);
    }

    public static void Deactivate()
    {
        _instance?.DeactivateInternal();
    }

    public static bool IsActive => _instance?._active == true;

    private static void EnsureInstance()
    {
        if (_instance != null && _instance)
        {
            return;
        }

        GameObject root = new("HomesteadBlueprintPlacementTool");
        DontDestroyOnLoad(root);
        _instance = root.AddComponent<ZoneBlueprintPlacementTool>();
    }

    private void ActivateInternal(Player player, string blueprintName)
    {
        if (_active && string.Equals(_blueprintName, blueprintName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ClearPreview();
        _active = true;
        _blueprintName = blueprintName;
        _suppressInputFrames = 2;
        _placementYaw = 0f;
        _chestRotation = GetAimYawRotation(player);
        _heightOffset = 0f;
        _horizontalOffset = Vector3.zero;

        if (!ZoneBlueprintCommands.TryLoadBlueprint(blueprintName, out ZoneBlueprintFile blueprint))
        {
            Message(player, HomesteadLocalization.Format("hs_blueprint_load_failed_plain", blueprintName));
            DeactivateInternal();
            return;
        }

        _blueprint = blueprint;
        _previewRoot = ZoneBlueprintVisuals.CreateBlueprintVisualRoot(blueprint, $"HomesteadBlueprintPreview_{blueprintName}");
        _previewRoot.transform.SetParent(transform, false);
        _chestPreviewRoot = ZoneBlueprintPlanChestPrefab.CreatePreview();
        _chestPreviewRoot?.transform.SetParent(transform, false);
    }

    private void DeactivateInternal()
    {
        _active = false;
        _blueprintName = "";
        _blueprint = null;
        _heightOffset = 0f;
        _horizontalOffset = Vector3.zero;
        ZoneAreaToolStatusHud.Hide();
        ClearPreview();
    }

    private void Update()
    {
        if (!_active)
        {
            return;
        }

        Player player = Player.m_localPlayer;
        if (player == null || !ZonePlacementInput.IsHoldingBuildTool(player))
        {
            DeactivateInternal();
            return;
        }

        if (!TryGetAimPoint(player, out Vector3 aimPoint))
        {
            SetPreviewVisible(false);
            ZoneAreaToolStatusHud.Hide();
            return;
        }

        _anchorRotation = Quaternion.Euler(0f, _placementYaw, 0f);
        _anchor = GetAdjustedAnchor(aimPoint, _anchorRotation);
        _chestRotation = GetAimYawRotation(player);

        if (ShouldBlockInput())
        {
            UpdatePreviewTransform();
            UpdateStatusHud();
            return;
        }

        if (_suppressInputFrames > 0)
        {
            _suppressInputFrames--;
            UpdatePreviewTransform();
            UpdateStatusHud();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            ResetOffsets();
        }

        ZonePlacementInput.ApplyYawScroll(ref _placementYaw);
        ZonePlacementInput.ApplyOffset(ref _horizontalOffset, ref _heightOffset);

        _anchorRotation = Quaternion.Euler(0f, _placementYaw, 0f);
        _anchor = GetAdjustedAnchor(aimPoint, _anchorRotation);
        UpdatePreviewTransform();
        UpdateStatusHud();

        if (Input.GetMouseButtonDown(0))
        {
            Place(player);
        }
    }

    private void UpdateStatusHud()
    {
        ZoneAreaToolStatusHud.ShowBlueprint("Blueprint Placement", _placementYaw, _horizontalOffset, _heightOffset);
    }

    private void OnDestroy()
    {
        ClearPreview();
        if (_instance == this)
        {
            _instance = null;
        }
    }

    private void Place(Player player)
    {
        if (_blueprint == null || string.IsNullOrWhiteSpace(_blueprintName))
        {
            return;
        }

        HomesteadCommandResult result = ZoneBlueprintCommands.PlaceBlueprintPlanAt(_blueprintName, player, _anchor, _anchorRotation, _chestRotation);
        Message(player, result.Message, result.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center);
    }

    private void UpdatePreviewTransform()
    {
        if (_previewRoot == null)
        {
            return;
        }

        _previewRoot.SetActive(true);
        _previewRoot.transform.position = _anchor;
        _previewRoot.transform.rotation = _anchorRotation;

        if (_blueprint != null)
        {
            if (_chestPreviewRoot == null)
            {
                _chestPreviewRoot = ZoneBlueprintPlanChestPrefab.CreatePreview();
                _chestPreviewRoot?.transform.SetParent(transform, false);
            }

            if (_chestPreviewRoot != null)
            {
                _chestPreviewRoot.SetActive(true);
                _chestPreviewRoot.transform.position = ZoneBlueprintCommands.GetPlanChestPosition(_blueprint, _anchor, _anchorRotation, _chestRotation);
                _chestPreviewRoot.transform.rotation = _chestRotation;
            }
        }
    }

    private void SetPreviewVisible(bool visible)
    {
        if (_previewRoot != null)
        {
            _previewRoot.SetActive(visible);
        }

        if (_chestPreviewRoot != null)
        {
            _chestPreviewRoot.SetActive(visible);
        }
    }

    private void ClearPreview()
    {
        if (_previewRoot != null)
        {
            Object.Destroy(_previewRoot);
            _previewRoot = null;
        }

        if (_chestPreviewRoot != null)
        {
            Object.Destroy(_chestPreviewRoot);
            _chestPreviewRoot = null;
        }
    }

    private static bool TryGetAimPoint(Player player, out Vector3 point)
    {
        Camera camera = Utils.GetMainCamera();
        if (camera != null)
        {
            Ray ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));
            if (Physics.Raycast(ray, out RaycastHit hit, MaxPlacementDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                point = hit.point;
                point.y = HomesteadTerrainSupport.SampleGroundY(point.x, point.z, point.y);
                return true;
            }
        }

        point = player.transform.position + player.transform.forward * 8f;
        point.y = HomesteadTerrainSupport.SampleGroundY(point.x, point.z, player.transform.position.y);
        return true;
    }

    private Vector3 GetAdjustedAnchor(Vector3 aimPoint, Quaternion rotation)
    {
        return aimPoint + ZonePlacementOffset.ToWorldOffset(rotation, _horizontalOffset, _heightOffset);
    }

    private void ResetOffsets()
    {
        _heightOffset = 0f;
        _horizontalOffset = Vector3.zero;
    }

    private static Quaternion GetYawRotation(Quaternion rotation)
    {
        return Quaternion.Euler(0f, rotation.eulerAngles.y, 0f);
    }

    private static Quaternion GetAimYawRotation(Player player)
    {
        Camera camera = Utils.GetMainCamera();
        if (camera != null)
        {
            Vector3 forward = camera.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude > 0.0001f)
            {
                return Quaternion.LookRotation(forward.normalized, Vector3.up);
            }
        }

        return GetYawRotation(player.transform.rotation);
    }

    private static bool ShouldBlockInput()
    {
        return ZoneAreaToolShared.ShouldBlockInput();
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
}
