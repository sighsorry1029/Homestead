using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Homestead;


internal sealed class ZoneBlueprintStorePreviewTool : MonoBehaviour
{
    private const float MaxPreviewDistance = 128f;

    private enum PreviewMode
    {
        Purchase,
        Listing
    }

    private static ZoneBlueprintStorePreviewTool? _instance;

    private readonly Dictionary<string, LockedPreview> _lockedPreviews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<string>> _pendingListingPreviewKeysByName = new(StringComparer.OrdinalIgnoreCase);
    private string _listingId = "";
    private string _offerId = "";
    private string _name = "";
    private ZoneBlueprintFile? _blueprint;
    private GameObject? _previewRoot;
    private GameObject? _chestPreviewRoot;
    private Material? _lockedPreviewMaterial;
    private float _yaw;
    private float _heightOffset;
    private Vector3 _horizontalOffset;
    private Vector3 _currentAnchor;
    private Quaternion _currentRotation;
    private Vector3 _currentChestPosition;
    private Quaternion _currentChestRotation;
    private bool _allowPurchase;
    private bool _active;
    private bool _placementLocked;
    private bool _lockedPreviewMaterialApplied;
    private bool _waitForPlaceRelease;
    private int _activatedFrame;
    private int _lockedPreviewSequence;
    private string _lockedPreviewColorSignature = "";
    private PreviewMode _mode;

    public static void Activate(string listingId, string offerId, string name, ZoneBlueprintFile blueprint, bool allowPurchase)
    {
        EnsureInstance();
        _instance?.ActivateInternal(PreviewMode.Purchase, listingId, offerId, name, blueprint, allowPurchase);
    }

    public static void ActivateListing(string name, ZoneBlueprintFile blueprint)
    {
        ZoneBlueprintStore.CancelPendingPreview();
        EnsureInstance();
        _instance?.ActivateInternal(PreviewMode.Listing, "", "", name, blueprint, allowPurchase: false);
    }

    public static void DeactivateActive()
    {
        ZoneBlueprintStore.CancelPendingPreview();
        if (_instance?._placementLocked == true)
        {
            return;
        }

        _instance?.Deactivate();
    }

    public static void ResetForWorldSession()
    {
        if (_instance == null || !_instance)
        {
            return;
        }

        _instance.Deactivate();
        _instance.ClearLockedPreviews();
    }

    public static void NotifyStoreChestDestroyed(string mode, string listingId, string blueprintName)
    {
        if (_instance == null || !_instance)
        {
            return;
        }

        if (string.Equals(mode, ZoneBlueprintStoreChest.ModePurchase, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(listingId))
        {
            _instance.RemoveLockedPreviewsByPrefix(PurchasePreviewPrefix(listingId));
            return;
        }

        if (!string.Equals(mode, ZoneBlueprintStoreChest.ModePrice, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(listingId))
        {
            _instance.RemoveLockedPreview(ListingPreviewKey(listingId));
            return;
        }

        _instance.CancelPendingListingPreview(blueprintName);
    }

    public static void ConfirmPendingListingPreview(string blueprintName, string listingId)
    {
        if (_instance == null || !_instance || string.IsNullOrWhiteSpace(listingId))
        {
            return;
        }

        _instance.ConfirmPendingListingPreviewInternal(blueprintName, listingId);
    }

    public static void RemoveListingPreview(string listingId)
    {
        if (_instance == null || !_instance || string.IsNullOrWhiteSpace(listingId))
        {
            return;
        }

        _instance.RemoveLockedPreview(ListingPreviewKey(listingId));
    }

    public static void RemovePurchasePreview(string listingId, string offerId)
    {
        if (_instance == null || !_instance || string.IsNullOrWhiteSpace(listingId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(offerId))
        {
            _instance.RemoveLockedPreview(PurchasePreviewKey(listingId, offerId));
            return;
        }

        _instance.RemoveLockedPreviewsByPrefix(PurchasePreviewPrefix(listingId));
    }

    public static void CancelPendingPlacement(string action, string listingId, string blueprintName)
    {
        if (_instance == null || !_instance)
        {
            return;
        }

        if (string.Equals(action, "buy", StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(listingId))
            {
                _instance.RemoveLockedPreviewsByPrefix(PurchasePreviewPrefix(listingId));
            }

            return;
        }

        if (string.Equals(action, "price_chest", StringComparison.Ordinal))
        {
            _instance.CancelPendingListingPreview(blueprintName);
        }
    }

    public static bool TryTransferPreviewToChest(
        string mode,
        string listingId,
        string blueprintName,
        out GameObject? root,
        out Material? material)
    {
        root = null;
        material = null;
        if (_instance == null || !_instance)
        {
            return false;
        }

        return _instance.TryTransferPreviewToChestInternal(mode, listingId, blueprintName, out root, out material);
    }

    private static void EnsureInstance()
    {
        if (_instance != null && _instance)
        {
            return;
        }

        GameObject root = new("HomesteadBlueprintStorePreviewTool");
        Object.DontDestroyOnLoad(root);
        _instance = root.AddComponent<ZoneBlueprintStorePreviewTool>();
    }

    private void ActivateInternal(PreviewMode mode, string listingId, string offerId, string name, ZoneBlueprintFile blueprint, bool allowPurchase)
    {
        ClearPreview();
        _mode = mode;
        _listingId = listingId;
        _offerId = offerId ?? "";
        _name = name;
        _blueprint = blueprint;
        _allowPurchase = allowPurchase;
        _placementLocked = false;
        _lockedPreviewMaterialApplied = false;
        _waitForPlaceRelease = true;
        _activatedFrame = Time.frameCount;
        _lockedPreviewColorSignature = "";
        _yaw = 0f;
        _heightOffset = 0f;
        _horizontalOffset = Vector3.zero;
        _previewRoot = ZoneBlueprintVisuals.CreateBlueprintVisualRoot(blueprint, $"HomesteadStorePreview_{name}");
        _previewRoot.transform.SetParent(transform, false);
        _chestPreviewRoot = ZoneBlueprintStoreChestPrefab.CreatePreview(GetChestPreviewMode());
        _chestPreviewRoot?.transform.SetParent(transform, false);
        _chestPreviewRoot?.SetActive(false);
        _active = true;
    }

    private void Update()
    {
        if (!_active)
        {
            return;
        }

        Player player = Player.m_localPlayer;
        if (player == null)
        {
            Deactivate();
            return;
        }

        if (!_placementLocked && !ZonePlacementInput.IsHoldingBuildTool(player))
        {
            Deactivate();
            return;
        }

        if (!_placementLocked && Input.GetKeyDown(KeyCode.Escape))
        {
            Deactivate();
            return;
        }

        if (_placementLocked)
        {
            UpdateLockedStatusHud();
            return;
        }

        if (!ZoneAreaToolShared.ShouldBlockInput())
        {
            ZonePlacementInput.ApplyYawScroll(ref _yaw);
            ZonePlacementInput.ApplyOffset(ref _horizontalOffset, ref _heightOffset);
        }

        if (TryGetAimPoint(player, out Vector3 point) && _previewRoot != null)
        {
            Quaternion rotation = Quaternion.Euler(0f, _yaw, 0f);
            Quaternion chestRotation = GetAimYawRotation(player);
            Vector3 anchor = point + ZonePlacementOffset.ToWorldOffset(rotation, _horizontalOffset, _heightOffset);
            _previewRoot.SetActive(true);
            _previewRoot.transform.position = anchor;
            _previewRoot.transform.rotation = rotation;
            _currentAnchor = anchor;
            _currentRotation = rotation;
            _currentChestRotation = chestRotation;
            _currentChestPosition = GetChestPosition(anchor, rotation, chestRotation);
            UpdateChestPreview(visible: true);
            ZoneAreaToolStatusHud.ShowBlueprint(GetPreviewTitle(), _yaw, _horizontalOffset, _heightOffset);
            UpdatePlaceInputGuard();
            if (IsPlacePressed())
            {
                PlaceChest();
                return;
            }
        }
        else
        {
            _previewRoot?.SetActive(false);
            UpdateChestPreview(visible: false);
        }
    }

    private void PlaceChest()
    {
        if (_blueprint == null)
        {
            return;
        }

        string lockedPreviewKey = CreateLockedPreviewKey();
        RegisterCurrentLockedPreview(lockedPreviewKey);

        if (_mode == PreviewMode.Purchase)
        {
            if (!_allowPurchase)
            {
                RemoveLockedPreview(lockedPreviewKey);
                return;
            }

            ZoneBlueprintStore.RequestBuyAt(_listingId, _offerId, _currentChestPosition, _currentChestRotation, _currentAnchor, _currentRotation);
            FinishActivePlacementAfterLock();
            return;
        }

        ZoneBlueprintStore.OpenPriceChestAt(_name, _currentChestPosition, _currentChestRotation, _currentAnchor, _currentRotation);
        FinishActivePlacementAfterLock();
    }

    private string CreateLockedPreviewKey()
    {
        if (_mode == PreviewMode.Purchase)
        {
            return PurchasePreviewKey(_listingId, _offerId);
        }

        string key = $"price_pending:{++_lockedPreviewSequence}";
        if (!_pendingListingPreviewKeysByName.TryGetValue(_name, out Queue<string> queue))
        {
            queue = new Queue<string>();
            _pendingListingPreviewKeysByName[_name] = queue;
        }

        queue.Enqueue(key);
        return key;
    }

    private void RegisterCurrentLockedPreview(string key)
    {
        if (_previewRoot == null)
        {
            return;
        }

        ApplyLockedPreviewMaterial();
        RemoveLockedPreview(key);
        _previewRoot.name = $"HomesteadStoreLockedPreview_{key}";
        _previewRoot.transform.SetParent(transform, true);
        _lockedPreviews[key] = new LockedPreview
        {
            Root = _previewRoot,
            Material = _lockedPreviewMaterial
        };

        _previewRoot = null;
        _lockedPreviewMaterial = null;
        _lockedPreviewMaterialApplied = false;
        _lockedPreviewColorSignature = "";
    }

    private void FinishActivePlacementAfterLock()
    {
        _active = false;
        _placementLocked = false;
        _allowPurchase = false;
        _blueprint = null;
        _listingId = "";
        _offerId = "";
        _name = "";
        _heightOffset = 0f;
        _horizontalOffset = Vector3.zero;
        _waitForPlaceRelease = false;
        _activatedFrame = -1;
        ZoneAreaToolStatusHud.Hide();
        ClearPreview();
    }

    private void ConfirmPendingListingPreviewInternal(string blueprintName, string listingId)
    {
        string? pendingKey = DequeuePendingListingPreviewKey(blueprintName);
        if (pendingKey == null || !_lockedPreviews.TryGetValue(pendingKey, out LockedPreview preview))
        {
            return;
        }

        _lockedPreviews.Remove(pendingKey);
        string finalKey = ListingPreviewKey(listingId);
        RemoveLockedPreview(finalKey);
        _lockedPreviews[finalKey] = preview;
    }

    private bool TryTransferPreviewToChestInternal(
        string mode,
        string listingId,
        string blueprintName,
        out GameObject? root,
        out Material? material)
    {
        root = null;
        material = null;
        string? key = GetTransferPreviewKey(mode, listingId, blueprintName);
        if (key == null || !_lockedPreviews.TryGetValue(key, out LockedPreview preview))
        {
            return false;
        }

        _lockedPreviews.Remove(key);
        root = preview.Root;
        material = preview.Material;
        // Keep the restored preview out of the chest hierarchy. If it is parented
        // to a WearNTear chest, the chest destruction path can briefly render the
        // preview with the store tint as part of the break effect.
        return root != null && root;
    }

    private string? GetTransferPreviewKey(string mode, string listingId, string blueprintName)
    {
        if (string.Equals(mode, ZoneBlueprintStoreChest.ModePurchase, StringComparison.Ordinal))
        {
            string prefix = PurchasePreviewPrefix(listingId);
            return _lockedPreviews.Keys.FirstOrDefault(key => key.StartsWith(prefix, StringComparison.Ordinal));
        }

        if (!string.Equals(mode, ZoneBlueprintStoreChest.ModePrice, StringComparison.Ordinal))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(listingId))
        {
            string finalKey = ListingPreviewKey(listingId);
            if (_lockedPreviews.ContainsKey(finalKey))
            {
                return finalKey;
            }
        }

        return DequeuePendingListingPreviewKey(blueprintName);
    }

    private void CancelPendingListingPreview(string blueprintName)
    {
        string? pendingKey = DequeuePendingListingPreviewKey(blueprintName) ?? FindAnyPendingListingPreviewKey();
        if (pendingKey != null)
        {
            RemoveLockedPreview(pendingKey);
        }
    }

    private string? DequeuePendingListingPreviewKey(string blueprintName)
    {
        if (string.IsNullOrWhiteSpace(blueprintName) ||
            !_pendingListingPreviewKeysByName.TryGetValue(blueprintName, out Queue<string> queue))
        {
            return null;
        }

        while (queue.Count > 0)
        {
            string key = queue.Dequeue();
            if (_lockedPreviews.ContainsKey(key))
            {
                if (queue.Count == 0)
                {
                    _pendingListingPreviewKeysByName.Remove(blueprintName);
                }

                return key;
            }
        }

        _pendingListingPreviewKeysByName.Remove(blueprintName);
        return null;
    }

    private string? FindAnyPendingListingPreviewKey()
    {
        return _lockedPreviews.Keys.FirstOrDefault(key => key.StartsWith("price_pending:", StringComparison.Ordinal));
    }

    private void RemoveLockedPreview(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !_lockedPreviews.TryGetValue(key, out LockedPreview preview))
        {
            return;
        }

        if (preview.Root != null && preview.Root)
        {
            Object.Destroy(preview.Root);
        }

        if (preview.Material != null)
        {
            Object.Destroy(preview.Material);
        }

        _lockedPreviews.Remove(key);
    }

    private void ClearLockedPreviews()
    {
        foreach (string key in _lockedPreviews.Keys.ToList())
        {
            RemoveLockedPreview(key);
        }

        _pendingListingPreviewKeysByName.Clear();
    }

    private void RemoveLockedPreviewsByPrefix(string prefix)
    {
        foreach (string key in _lockedPreviews.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            RemoveLockedPreview(key);
        }
    }

    private void UpdateLockedStatusHud()
    {
        ApplyLockedPreviewMaterial();
        if (_previewRoot != null)
        {
            _previewRoot.SetActive(true);
        }

        UpdateChestPreview(visible: true);
        string suffix = _mode == PreviewMode.Purchase
            ? HomesteadLocalization.Text("hs_store_preview_deposit_price")
            : HomesteadLocalization.Text("hs_store_preview_set_price");
        ZoneAreaToolStatusHud.ShowBlueprint($"{GetPreviewTitle()} - {suffix}", _yaw, _horizontalOffset, _heightOffset);
    }

    private string GetPreviewTitle()
    {
        return _mode == PreviewMode.Purchase
            ? HomesteadLocalization.Format("hs_store_preview_purchase_title", _name)
            : HomesteadLocalization.Format("hs_store_preview_listing_title", _name);
    }

    private string GetChestPreviewMode()
    {
        return _mode == PreviewMode.Purchase
            ? ZoneBlueprintStoreChest.ModePurchase
            : ZoneBlueprintStoreChest.ModePrice;
    }

    private Vector3 GetChestPosition(Vector3 anchor, Quaternion anchorRotation, Quaternion chestRotation)
    {
        return _blueprint != null
            ? ZoneBlueprintCommands.GetPlanChestPosition(_blueprint, anchor, anchorRotation, chestRotation)
            : anchor + chestRotation * new Vector3(0f, 0f, 2.2f);
    }

    private void UpdateChestPreview(bool visible)
    {
        if (_chestPreviewRoot == null)
        {
            _chestPreviewRoot = ZoneBlueprintStoreChestPrefab.CreatePreview(GetChestPreviewMode());
            _chestPreviewRoot?.transform.SetParent(transform, false);
        }

        if (_chestPreviewRoot == null)
        {
            return;
        }

        _chestPreviewRoot.SetActive(visible);
        if (!visible)
        {
            return;
        }

        _chestPreviewRoot.transform.position = _currentChestPosition;
        _chestPreviewRoot.transform.rotation = _currentChestRotation;
    }

    private void ApplyLockedPreviewMaterial()
    {
        if (_previewRoot == null)
        {
            return;
        }

        Color color = GetLockedPreviewColor();
        string signature = ColorUtility.ToHtmlStringRGBA(color);
        if (_lockedPreviewMaterialApplied && string.Equals(signature, _lockedPreviewColorSignature, StringComparison.Ordinal))
        {
            ZoneBlueprintGhostOwner.UpdateMaterialColor(_previewRoot, color);
            if (_lockedPreviewMaterial != null)
            {
                _lockedPreviewMaterial.color = color;
            }

            return;
        }

        _lockedPreviewMaterial = ZoneBlueprintGhostOwner.ApplyMaterial(_previewRoot, color);
        _lockedPreviewMaterialApplied = true;
        _lockedPreviewColorSignature = signature;
    }

    private Color GetLockedPreviewColor()
    {
        return _mode == PreviewMode.Purchase
            ? BlueprintConfig.StorePurchasePreviewColor
            : BlueprintConfig.StoreListingPreviewColor;
    }

    private static string PurchasePreviewKey(string listingId, string offerId)
    {
        return $"{PurchasePreviewPrefix(listingId)}{offerId ?? ""}";
    }

    private static string PurchasePreviewPrefix(string listingId)
    {
        return $"buy:{listingId}:";
    }

    private static string ListingPreviewKey(string listingId)
    {
        return $"price:{listingId}";
    }

    private void Deactivate()
    {
        _active = false;
        _listingId = "";
        _offerId = "";
        _name = "";
        _blueprint = null;
        _allowPurchase = false;
        _placementLocked = false;
        _lockedPreviewMaterialApplied = false;
        _waitForPlaceRelease = false;
        _activatedFrame = -1;
        _lockedPreviewColorSignature = "";
        _heightOffset = 0f;
        _horizontalOffset = Vector3.zero;
        ZoneAreaToolStatusHud.Hide();
        ClearPreview();
    }

    private void OnDestroy()
    {
        ClearPreview();
        ClearLockedPreviews();
        if (_instance == this)
        {
            _instance = null;
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

        if (_lockedPreviewMaterial != null)
        {
            Object.Destroy(_lockedPreviewMaterial);
            _lockedPreviewMaterial = null;
        }

        _lockedPreviewMaterialApplied = false;
        _lockedPreviewColorSignature = "";
    }

    private static bool TryGetAimPoint(Player player, out Vector3 point)
    {
        Camera camera = Utils.GetMainCamera();
        if (camera != null)
        {
            Ray ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));
            if (Physics.Raycast(ray, out RaycastHit hit, MaxPreviewDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                point = hit.point;
                point.y = HomesteadTerrainSupport.SampleGroundY(point.x, point.z, point.y);

                return true;
            }
        }

        point = player.transform.position + player.transform.forward * 8f;
        return true;
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

    private void UpdatePlaceInputGuard()
    {
        if (!_waitForPlaceRelease)
        {
            return;
        }

        if (Time.frameCount == _activatedFrame || IsPlaceInputHeld())
        {
            return;
        }

        _waitForPlaceRelease = false;
    }

    private bool IsPlacePressed()
    {
        return !_waitForPlaceRelease && IsPlacePressedRaw();
    }

    private static bool IsPlacePressedRaw()
    {
        return ZInput.GetButtonDown("Attack") || ZInput.GetButtonDown("JoyPlace") || Input.GetMouseButtonDown(0);
    }

    private static bool IsPlaceInputHeld()
    {
        return ZInput.GetButton("Attack") ||
               ZInput.GetButton("JoyPlace") ||
               Input.GetMouseButton(0);
    }

    private sealed class LockedPreview
    {
        public GameObject? Root;
        public Material? Material;
    }
}


