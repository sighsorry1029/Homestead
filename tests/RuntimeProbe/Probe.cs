using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

[BepInPlugin("sighsorry.Homestead.CompatibilityProbe", "Homestead test probe", "1.0.0")]
[BepInDependency("sighsorry.Homestead")]
public sealed class Probe : BaseUnityPlugin
{
    private Assembly mod;
    private string report;
    private GameObject priceChestFixture;
    private Sprite[] uiSpritesBeforeShutdown;
    private UnityEngine.Object borrowedUiFont;
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private void Awake()
    {
        string root = Paths.GameRootPath;
        if (root.Contains("artifacts") && File.Exists(Path.Combine(root, "homestead-probe.enabled")))
            Utils.SetSaveDataPath(Path.Combine(root, "saves"));
    }
    private IEnumerator Start()
    {
        string root = Paths.GameRootPath;
        // Never run destructive fixtures in an ordinary game installation.
        if (!root.Contains("artifacts") || !File.Exists(Path.Combine(root, "homestead-probe.enabled"))) yield break;
        report = Path.Combine(root, "probe.txt");
        File.WriteAllText(report, "Original Unity/game DLL runtime probe\n");
        Check(Path.GetFullPath(Utils.GetSaveDataPath(FileHelpers.FileSource.Local)).TrimEnd(Path.DirectorySeparatorChar) == Path.GetFullPath(Path.Combine(root, "saves")), "isolated save directory");
        using (var hash = System.Security.Cryptography.SHA256.Create())
            File.AppendAllText(report, "Homestead SHA256 " + BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(Path.Combine(root, "BepInEx/plugins/Homestead.dll")))).Replace("-", "").ToLowerInvariant() + "\n");
        mod = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Homestead");
        bool headless = SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;
        bool uiOnly = Environment.GetCommandLineArgs().Contains("-homestead-ui-probe");
        if (!headless)
        {
            yield return new WaitForSeconds(6f);
            try
            {
                PlayerProfile profile = new PlayerProfile("homestead_probe", FileHelpers.FileSource.Local);
                profile.SetName("Homestead Probe");
                profile.Save();
                Game.SetProfile("homestead_probe", FileHelpers.FileSource.Local);
                World world = World.GetCreateWorld("HomesteadProbe", FileHelpers.FileSource.Local);
                ZNet.SetServer(true, false, false, "HomesteadProbe", "", world);
                FejdStartup startup = FindFirstObjectByType<FejdStartup>();
                AccessTools.Method(typeof(FejdStartup), "LoadMainScene").Invoke(startup, null);
            }
            catch (Exception ex) { Fail(ex); yield break; }
        }
        float deadline = Time.realtimeSinceStartup + 150f;
        while ((!ZNetScene.instance || !ZNetScene.instance.GetPrefab("piece_chest_wood") || (!headless && !Player.m_localPlayer)) && Time.realtimeSinceStartup < deadline)
            yield return null;
        yield return new WaitForSeconds(3f);
        if (!headless && Player.m_localPlayer)
        {
            Valkyrie valkyrie = FindFirstObjectByType<Valkyrie>();
            if (valkyrie && Player.m_localPlayer.InIntro()) valkyrie.DropPlayer();
            while (Player.m_localPlayer.InCutscene() && Time.realtimeSinceStartup < deadline) yield return null;
            yield return new WaitForSeconds(1f);
        }
        try
        {
            Check(ZNetScene.instance, "world loaded");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Jotunn"), "Jotunn not loaded");
            int count = Harmony.GetAllPatchedMethods().Count(m => Harmony.GetPatchInfo(m)?.Owners.Contains("sighsorry.Homestead") == true);
            Check(count > 50, "Harmony installed: " + count + " targets");
            Check(Localization.instance.Localize("$hs_area_save_name") != "$hs_area_save_name", "localization registered");
            CheckPrefabs();
            CheckContents();
            if (!headless && !uiOnly) CheckClient();
        }
        catch (Exception ex) { Fail(ex); yield break; }
        if (!headless)
        {
            yield return new WaitForSeconds(2f);
            try { if (!uiOnly) CaptureUi(FindObjectsByType<BuildUi>(FindObjectsInactive.Include, FindObjectsSortMode.None).First().GetComponentInParent<Canvas>().rootCanvas, "build-menu.png"); }
            catch (Exception ex) { Fail(ex); yield break; }
            yield return new WaitForSeconds(1f);
            try { CheckStoreAndIcon(); } catch (Exception ex) { Fail(ex); yield break; }
            yield return new WaitForSeconds(2f);
            try
            {
                PopulateStorePreview();
                CaptureUi(CustomUi.GetComponent<Canvas>().rootCanvas, "store-panel.png");
            }
            catch (Exception ex) { Fail(ex); yield break; }
            yield return new WaitForSeconds(1f);
            try
            {
                GameObject ui = (GameObject)mod.GetType("Homestead.HomesteadUi").GetProperty("CustomGUIFront", Any).GetValue(null);
                Check(ui && ui.activeInHierarchy, "custom UI active");
                var status = (UnityEngine.UI.Text)mod.GetType("Homestead.ZoneBlueprintStoreUi").GetField("_statusText", Any).GetValue(null);
                Check(!status.text.Contains(Localization.instance.Localize("$hs_store_loading")), "local store request completed");
                Call("ZoneBlueprintStoreUi", "ResetForWorldSession");
                Check(!(bool)mod.GetType("Homestead.HomesteadUi").GetProperty("InputBlocked", Any).GetValue(null), "input block released");
            }
            catch (Exception ex) { Fail(ex); yield break; }
            try { OpenPriceEditor(); } catch (Exception ex) { Fail(ex); yield break; }
            yield return new WaitForSeconds(1f);
            try
            {
                CaptureUi(CustomUi.GetComponent<Canvas>().rootCanvas, "price-editor.png");
                CheckPriceEditorSave();
            }
            catch (Exception ex) { Fail(ex); yield break; }
            yield return new WaitForSeconds(1f);
            try
            {
                CaptureUi(CustomUi.GetComponent<Canvas>().rootCanvas, "price-input.png");
                Call("ZoneBlueprintStorePriceInputUi", "ResetForWorldSession");
                Check(!(bool)mod.GetType("Homestead.HomesteadUi").GetProperty("InputBlocked", Any).GetValue(null), "price panels release input");
                priceChestFixture.GetComponent<ZNetView>().Destroy();
                BeginUiRecreation();
            }
            catch (Exception ex) { Fail(ex); yield break; }
            yield return null;
            try { CheckUiRecreation(); } catch (Exception ex) { Fail(ex); yield break; }
        }
        File.AppendAllText(report, "COMPLETE\n");
        Application.Quit();
    }

    private void CheckPrefabs()
    {
        foreach (string name in new[] { "piece_chest_wood_blueprint", "piece_chest_barrel_blueprint_store_price", "piece_chest_blueprint_store_purchase", "piece_chest_blackmetal_blueprint_store_payout" })
        {
            GameObject prefab = ZNetScene.instance.GetPrefab(name);
            // Plan prefab name is discovered from the owning production constant below.
            if (!prefab && name == "piece_chest_wood_blueprint")
                prefab = ZNetScene.instance.GetPrefab((string)mod.GetType("Homestead.ZoneBlueprintPlanChestPrefab").GetField("PrefabName", Any).GetRawConstantValue());
            Check(prefab && !prefab.activeInHierarchy && prefab.GetComponent<ZNetView>().GetZDO() == null, "inactive template without ZDO: " + name);
            Piece piece = prefab.GetComponent<Piece>();
            Check(piece && piece.m_craftingStation == null, "workbench-free placement and removal: " + name);
            GameObject spawned = Instantiate(prefab, new Vector3(0, 5000, 0), Quaternion.identity);
            ZNetView view = spawned.GetComponent<ZNetView>();
            Check(view && view.IsValid() && view.GetZDO().GetPrefab() == prefab.name.GetStableHashCode(), "spawned ZDO hash: " + name);
            view.Destroy();
        }
    }

    private void CheckContents()
    {
        GameObject chest = ZNetScene.instance.GetPrefab("piece_chest_wood");
        Inventory inventory = new Inventory("probe", null, 4, 4);
        ZPackage empty = new ZPackage(); inventory.Save(empty);
        Check(!(bool)Call("ZoneAreaDismantleTool", "HasStoredContainerItems", empty.GetArray()), "empty native inventory allowed");
        foreach (byte[] bad in new[] { new byte[0], new byte[] { 109, 0, 0, 0, 0 }, new byte[] { 110, 0, 0, 0, 0, 0 } })
            Check((bool)Call("ZoneAreaDismantleTool", "HasStoredContainerItems", bad), "unknown/truncated payload protected");
        inventory.AddItem(ObjectDB.instance.GetItemPrefab("Wood").GetComponent<ItemDrop>().m_itemData.Clone());
        ZPackage full = new ZPackage(); inventory.Save(full);
        ZDO zdo = ZDOMan.instance.CreateNewZDO(new Vector3(0, 5000, 0), chest.name.GetStableHashCode());
        try
        {
            zdo.Set(ZDOVars.s_items, "unsupported payload");
            Check((bool)Call("ZoneAreaDismantleTool", "HasProtectedContentsOrAttachments", zdo, chest), "unsupported string inventory protected");
            zdo.Set(ZDOVars.s_items, "");
            zdo.Set(ZDOVars.s_items, full.GetArray());
            Check(ZNetScene.instance.FindInstance(zdo) == null, "container fixture unloaded");
            Check((bool)Call("ZoneAreaDismantleTool", "HasProtectedContentsOrAttachments", zdo, chest), "unloaded container protected");
            foreach (Type standType in new[] { typeof(ItemStand), typeof(ArmorStand) })
            {
                GameObject stand = ZNetScene.instance.m_prefabs.First(p => p && p.GetComponent(standType));
                int key = standType == typeof(ItemStand) ? ZDOVars.s_item : "0_item".GetStableHashCode();
                zdo.Set(key, "Wood".GetStableHashCode());
                Check((bool)Call("ZoneAreaDismantleTool", "HasProtectedContentsOrAttachments", zdo, stand), "integer attachment protected: " + standType.Name);
                zdo.Set(key, 0);
            }
        }
        finally { ZDOMan.instance.DestroyZDO(zdo); }
    }

    private void CheckClient()
    {
        Player player = Player.m_localPlayer;
        GameObject hammerPrefab = ObjectDB.instance.GetItemPrefab("Hammer");
        ItemDrop.ItemData hammer = hammerPrefab.GetComponent<ItemDrop>().m_itemData.Clone();
        hammer.m_dropPrefab = hammerPrefab;
        player.GetInventory().AddItem(hammer);
        player.EquipItem(hammer);
        AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList").Invoke(player, null);
        PieceTable table = player.GetBuildTool();
        Check(table && table.m_availablePieces.Any(p => p.GetComponent(mod.GetType("Homestead.ZoneBlueprintSaveToolMarker"))), "Homestead build pieces available");
        BuildUi build = FindObjectsByType<BuildUi>(FindObjectsInactive.Include, FindObjectsSortMode.None).First();
        build.OpenBuildMenu();
        var lists = (System.Collections.IList)AccessTools.Field(typeof(BuildUi), "m_pieceLists").GetValue(build);
        int index = Enumerable.Range(0, lists.Count).Single(i => ((IPieceList)lists[i]).DisplayName == "Homestead");
        build.SelectPieceList(index);
        Check(((IPieceList)lists[index]).DisplayName == "Homestead", "native Homestead tab selectable");
        Check((UnityEngine.Object)AccessTools.Field(typeof(BuildUi), "m_specialPieceButton").GetValue(build), "repair button retained");
        var pieceButtons = (System.Collections.IList)AccessTools.Field(typeof(BuildUi), "m_pieceButtons").GetValue(build);
        Type markerType = mod.GetType("Homestead.ZoneBlueprintSaveToolMarker");
        FieldInfo markerKind = markerType.GetField("Kind", Any);
        Component[] homesteadMarkers = pieceButtons.Cast<BuildUiPieceButton>()
            .Where(button => button && button.gameObject.activeSelf && button.Piece)
            .Select(button => button.Piece.GetComponent(markerType))
            .Where(marker => marker)
            .ToArray();
        Check(homesteadMarkers.Length >= 6, "Homestead tools and blueprints retained in Homestead tab");
        Check(homesteadMarkers.Any(marker => markerKind.GetValue(marker).ToString() == "Blueprint"), "saved blueprints retained in Homestead tab");
        for (int tab = 0; tab < index; tab++)
        {
            build.SelectPieceList(tab);
            bool leaked = pieceButtons.Cast<BuildUiPieceButton>()
                .Where(button => button && button.gameObject.activeSelf && button.Piece)
                .Select(button => button.Piece.GetComponent(markerType))
                .Any(marker => marker);
            Check(!leaked, "Homestead pieces hidden from native tab: " + ((IPieceList)lists[tab]).DisplayName);
        }
        build.SelectPieceList(index);
        Canvas.ForceUpdateCanvases();
        var buttons = (System.Collections.IList)AccessTools.Field(typeof(BuildUi), "m_tabButtons").GetValue(build);
        foreach (UnityEngine.UI.Button button in buttons)
        {
            RectTransform rect = (RectTransform)button.transform;
            File.AppendAllText(report, "TAB " + button.name + " position=" + rect.position + " rect=" + rect.rect + "\n");
        }
        string wheel = (string)mod.GetType("Homestead.ZoneBlueprintToolIcons").GetProperty("MouseWheelInputLabel", Any).GetValue(null);
        Check(wheel.Contains("<sprite"), "native wheel sprite registered");
        GameObject hoePrefab = ObjectDB.instance.GetItemPrefab("Hoe");
        ItemDrop.ItemData hoe = hoePrefab.GetComponent<ItemDrop>().m_itemData.Clone();
        hoe.m_dropPrefab = hoePrefab;
        player.GetInventory().AddItem(hoe);
        player.EquipItem(hoe);
        AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList").Invoke(player, null);
        build.OpenBuildMenu();
        Check(!((UnityEngine.UI.Button)buttons[index]).gameObject.activeSelf, "Homestead tab hidden for hoe");
        player.EquipItem(hammer);
        AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList").Invoke(player, null);
        build.OpenBuildMenu();
        build.SelectPieceList(index);
        Check(((UnityEngine.UI.Button)buttons[index]).gameObject.activeSelf, "Homestead tab restored for hammer");
    }

    private void CheckStoreAndIcon()
    {
        FindObjectsByType<BuildUi>(FindObjectsInactive.Include, FindObjectsSortMode.None).First().Close();
        object blueprint = Call("ZoneBlueprintFileFormat", "ReadFile", Path.Combine(Paths.GameRootPath, "probe.blueprint"));
        Sprite sprite = (Sprite)Call("ZoneBlueprintVisuals", "RenderAndCacheIcon", "probe", blueprint);
        Check(sprite && sprite.texture.width == 256, "native icon rendered");
        File.WriteAllBytes(Path.Combine(Paths.GameRootPath, "probe-icon.png"), sprite.texture.EncodeToPNG());
        CheckBlueprintPlacementModesAreExclusive(blueprint);
        Check((bool)Call("ZoneBlueprintStoreUi", "Open"), "store panel created");
        Call("ZoneBlueprintStoreUi", "RequestCurrentPage", null, true);
        Check((bool)mod.GetType("Homestead.HomesteadUi").GetProperty("InputBlocked", Any).GetValue(null), "modal blocks game input");
        CheckStoreCameraZoomGuard();
    }

    private void CheckBlueprintPlacementModesAreExclusive(object blueprint)
    {
        Player player = Player.m_localPlayer;
        ItemDrop.ItemData hammer = ObjectDB.instance.GetItemPrefab("Hammer").GetComponent<ItemDrop>().m_itemData.Clone();
        FieldInfo rightItem = AccessTools.Field(typeof(Humanoid), "m_rightItem");
        FieldInfo buildPieces = AccessTools.Field(typeof(Player), "m_buildPieces");
        object originalRightItem = rightItem.GetValue(player);
        object originalBuildPieces = buildPieces.GetValue(player);
        rightItem.SetValue(player, hammer);
        buildPieces.SetValue(player, hammer.m_shared.m_buildPieces);

        Type normalType = mod.GetType("Homestead.ZoneBlueprintPlacementTool");
        Type storeType = mod.GetType("Homestead.ZoneBlueprintStorePreviewTool");
        Call("ZoneBlueprintPlacementTool", "Activate", player, "sample_001");
        Check((bool)normalType.GetProperty("IsActive", Any).GetValue(null), "normal blueprint placement active");
        Call("ZoneBlueprintStorePreviewTool", "ActivateListing", "probe", blueprint);
        Check(!(bool)normalType.GetProperty("IsActive", Any).GetValue(null) && IsStorePreviewActive(storeType),
            "listing placement replaces normal blueprint placement");

        object storeInstance = storeType.GetField("_instance", Any).GetValue(null);
        FieldInfo waitForRelease = storeType.GetField("_waitForPlaceRelease", Any);
        waitForRelease.SetValue(storeInstance, false);
        BuildUi build = FindObjectsByType<BuildUi>(FindObjectsInactive.Include, FindObjectsSortMode.None).First();
        build.gameObject.SetActive(true);
        Check(Hud.IsPieceSelectionVisible(), "Hammer menu visible during listing placement");
        storeType.GetMethod("Update", Any).Invoke(storeInstance, null);
        Check((bool)waitForRelease.GetValue(storeInstance), "Hammer menu rearms listing placement input guard");
        build.Close();

        Call("ZoneBlueprintPlacementTool", "Activate", player, "sample_001");
        Check((bool)normalType.GetProperty("IsActive", Any).GetValue(null) && !IsStorePreviewActive(storeType),
            "normal blueprint placement replaces listing placement");

        object normalInstance = normalType.GetField("_instance", Any).GetValue(null);
        FieldInfo suppressFrames = normalType.GetField("_suppressInputFrames", Any);
        suppressFrames.SetValue(normalInstance, 0);
        build.gameObject.SetActive(true);
        normalType.GetMethod("Update", Any).Invoke(normalInstance, null);
        Check((int)suppressFrames.GetValue(normalInstance) >= 2, "Hammer menu suppresses normal placement click-through");
        build.Close();
        Call("ZoneBlueprintPlacementTool", "Deactivate");
        rightItem.SetValue(player, originalRightItem);
        buildPieces.SetValue(player, originalBuildPieces);
    }

    private static bool IsStorePreviewActive(Type storeType)
    {
        object instance = storeType.GetField("_instance", Any).GetValue(null);
        return instance != null && (bool)storeType.GetField("_active", Any).GetValue(instance);
    }

    private void CheckStoreCameraZoomGuard()
    {
        GameCamera camera = GameCamera.instance;
        Check(camera, "game camera available for store zoom guard");
        FieldInfo distanceField = AccessTools.Field(typeof(GameCamera), "m_distance");
        float originalDistance = (float)distanceField.GetValue(camera);
        float originalSensitivity = camera.m_zoomSens;
        Type patch = mod.GetType("Homestead.ZoneAreaCameraZoomGuard+GameCameraUpdateCameraPatch");
        MethodInfo prefix = patch.GetMethod("Prefix", Any);
        MethodInfo postfix = patch.GetMethod("Postfix", Any);
        object[] prefixArguments = { camera, null };
        prefix.Invoke(null, prefixArguments);
        Check(camera.m_zoomSens == 0f, "store modal suppresses camera wheel zoom");
        distanceField.SetValue(camera, originalDistance + 3f);
        postfix.Invoke(null, new[] { camera, prefixArguments[1] });
        Check(Mathf.Approximately((float)distanceField.GetValue(camera), originalDistance) &&
              Mathf.Approximately(camera.m_zoomSens, originalSensitivity),
            "store zoom guard restores camera state");
    }

    private GameObject CustomUi => (GameObject)mod.GetType("Homestead.HomesteadUi").GetProperty("CustomGUIFront", Any).GetValue(null);

    private object CreateListing(string id, string name, bool own)
    {
        Type type = mod.GetType("Homestead.ZoneBlueprintStoreListingSummaryDto");
        object listing = Activator.CreateInstance(type);
        type.GetProperty("ListingId").SetValue(listing, id);
        type.GetProperty("Name").SetValue(listing, name);
        type.GetProperty("SellerName").SetValue(listing, "Homestead Probe");
        type.GetProperty("PurchaseCount").SetValue(listing, 12);
        type.GetProperty("OfferCount").SetValue(listing, 3);
        type.GetProperty("CanManage").SetValue(listing, own);
        type.GetProperty("CanDelist").SetValue(listing, own);
        IList items = (IList)type.GetProperty("PriceItems").GetValue(listing);
        Type itemType = mod.GetType("Homestead.ZoneBlueprintStorePriceItem");
        foreach (string prefab in new[] { "Wood", "Stone" })
        {
            object item = Activator.CreateInstance(itemType);
            itemType.GetProperty("PrefabName").SetValue(item, prefab);
            string itemName = ObjectDB.instance.GetItemPrefab(prefab).GetComponent<ItemDrop>().m_itemData.m_shared.m_name;
            itemType.GetProperty("ItemName").SetValue(item, itemName);
            itemType.GetProperty("DisplayName").SetValue(item, itemName);
            itemType.GetProperty("Amount").SetValue(item, 50);
            items.Add(item);
        }
        return listing;
    }

    private void PopulateStorePreview()
    {
        // UI fixtures only: no catalog publication or purchase RPCs.
        Type ui = mod.GetType("Homestead.ZoneBlueprintStoreUi");
        IList listings = (IList)ui.GetField("_listings", Any).GetValue(null);
        listings.Clear();
        listings.Add(CreateListing("probe-ui-1", "초원 오두막", false));
        listings.Add(CreateListing("probe-ui-2", "Homestead workshop", true));
        ui.GetField("_totalListings", Any).SetValue(null, 2);
        Call("ZoneBlueprintStoreUi", "RefreshRows");
        DumpUi("store");
    }

    private void OpenPriceEditor()
    {
        priceChestFixture = Instantiate(ZNetScene.instance.GetPrefab("piece_chest_barrel_blueprint_store_price"), Player.m_localPlayer.transform.position + Vector3.up * 2f, Quaternion.identity);
        ZDO zdo = priceChestFixture.GetComponent<ZNetView>().GetZDO();
        zdo.Set("hs_store_mode", "price");
        zdo.Set("hs_store_blueprint_name", "초원 오두막 / Homestead workshop");
        Component chest = priceChestFixture.GetComponent(mod.GetType("Homestead.ZoneBlueprintStoreChest"));
        Call("ZoneBlueprintStorePriceEditorUi", "Open", chest);
        IList rows = (IList)mod.GetType("Homestead.ZoneBlueprintStorePriceEditorUi").GetField("Rows", Any).GetValue(null);
        string[] prefabs = { "Wood", "Stone", "Flint", "LeatherScraps", "Resin", "Coal", "FineWood", "RoundLog" };
        Check(rows.Count == prefabs.Length, "price editor retains eight material rows");
        for (int i = 0; i < rows.Count; i++)
        {
            object row = rows[i];
            var item = (UnityEngine.UI.InputField)row.GetType().GetProperty("ItemInput").GetValue(row);
            var amount = (UnityEngine.UI.InputField)row.GetType().GetProperty("AmountInput").GetValue(row);
            item.text = prefabs[i];
            amount.text = ((i + 1) * 25).ToString();
            Check(item.characterLimit == 64 && amount.characterLimit == 9 && amount.contentType == UnityEngine.UI.InputField.ContentType.IntegerNumber, "price row validation retained: " + i);
        }
        DumpUi("price-editor");
    }

    private void CheckPriceEditorSave()
    {
        var panel = (GameObject)mod.GetType("Homestead.ZoneBlueprintStorePriceEditorUi").GetField("_panel", Any).GetValue(null);
        panel.GetComponentsInChildren<UnityEngine.UI.Button>().Single(b => b.GetComponentInChildren<UnityEngine.UI.Text>().text == Localization.instance.Localize("$hs_common_close")).onClick.Invoke();
        Check(!panel.activeSelf, "price editor close button works");
        Component chest = priceChestFixture.GetComponent(mod.GetType("Homestead.ZoneBlueprintStoreChest"));
        IList saved = (IList)AccessTools.Method(chest.GetType(), "ReadPriceItems").Invoke(chest, null);
        object wood = saved.Cast<object>().Single(item => (string)item.GetType().GetProperty("PrefabName").GetValue(item) == "Wood");
        Check(saved.Count == 8 && (int)wood.GetType().GetProperty("Amount").GetValue(wood) == 25, "price editor saves typed materials to chest");
        Call("ZoneBlueprintStorePriceInputUi", "OpenEditPrice", CreateListing("probe-ui-2", "초원 오두막 / Homestead workshop", true));
        DumpUi("price-input");
    }

    private void DumpUi(string label)
    {
        Canvas ownCanvas = CustomUi.GetComponent<Canvas>();
        Check(ownCanvas.isRootCanvas && ownCanvas.scaleFactor <= 1f, label + ": independent canvas preserves original maximum pixel size");
        if (Screen.width >= 1920 && Screen.height >= 1080)
            Check(Mathf.Approximately(ownCanvas.scaleFactor, 1f), label + ": Jotunn pixel scale at 1080p and above");
        Check(Mathf.Approximately(ownCanvas.referencePixelsPerUnit, 50f), label + ": original sprite border scale");
        GameObject panel = CustomUi.transform.Cast<Transform>().Select(t => t.gameObject)
            .Single(go => go.activeSelf && go.name == (label == "store" ? "HomesteadBlueprintStorePanel" : label == "price-editor" ? "HomesteadBlueprintStorePriceEditor" : "HomesteadBlueprintStorePriceInput"));
        CheckPanelDrag(panel);
        for (Transform t = CustomUi.transform; t != null; t = t.parent)
        {
            Canvas canvas = t.GetComponent<Canvas>();
            var scaler = t.GetComponent<UnityEngine.UI.CanvasScaler>();
            File.AppendAllText(report, "UI " + label + " " + t.name + " scale=" + t.lossyScale + " rect=" + (t is RectTransform rect ? rect.rect.ToString() : "none") +
                (canvas ? " canvas=" + canvas.renderMode + " ppu=" + canvas.referencePixelsPerUnit : "") + (scaler ? " scaler=" + scaler.uiScaleMode + " ref=" + scaler.referenceResolution : "") + "\n");
        }
        foreach (var img in CustomUi.GetComponentsInChildren<UnityEngine.UI.Image>().GroupBy(i => i.sprite ? i.sprite.name : "missing").Select(g => g.First()))
            File.AppendAllText(report, "SPRITE " + (img.sprite ? img.sprite.name + " texture=" + img.sprite.texture.name + " border=" + img.sprite.border + " ppu=" + img.sprite.pixelsPerUnit : "missing") + " material=" + img.material.name + "\n");
    }

    private void CheckPanelDrag(GameObject panel)
    {
        RectTransform rect = (RectTransform)panel.transform;
        Vector2 original = rect.anchoredPosition;
        var eventData = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
        {
            position = (Vector2)rect.position + new Vector2(20f, 15f)
        };
        try
        {
            UnityEngine.EventSystems.ExecuteEvents.Execute(panel, eventData, UnityEngine.EventSystems.ExecuteEvents.beginDragHandler);
            UnityEngine.EventSystems.ExecuteEvents.Execute(panel, eventData, UnityEngine.EventSystems.ExecuteEvents.dragHandler);
            Check(Vector2.Distance(original, rect.anchoredPosition) < 0.1f, panel.name + ": drag starts without jumping");
            foreach (float edge in new[] { -10000f, 10000f })
            {
                eventData.position = new Vector2(edge, edge);
                UnityEngine.EventSystems.ExecuteEvents.Execute(panel, eventData, UnityEngine.EventSystems.ExecuteEvents.dragHandler);
                Vector3[] corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                Check(corners[0].x >= -0.1f && corners[0].y >= -0.1f && corners[2].x <= Screen.width + 0.1f && corners[2].y <= Screen.height + 0.1f, panel.name + ": drag remains on screen at " + edge);
            }
        }
        finally { rect.anchoredPosition = original; }
    }

    private void BeginUiRecreation()
    {
        Type type = mod.GetType("Homestead.HomesteadUi");
        object helper = type.GetField("Instance", Any).GetValue(null);
        uiSpritesBeforeShutdown = ((IEnumerable)type.GetField("_ownedSprites", Any).GetValue(helper)).Cast<Sprite>().ToArray();
        borrowedUiFont = (UnityEngine.Object)type.GetProperty("AveriaSerif").GetValue(helper);
        Check(uiSpritesBeforeShutdown.Length == 3, "only three atlas sprite copies owned");
        Call("HomesteadUi", "Shutdown");
    }

    private void CheckUiRecreation()
    {
        Check(!CustomUi && uiSpritesBeforeShutdown.All(sprite => !sprite) && borrowedUiFont, "UI shutdown releases owned sprites and preserves borrowed font");
        Type type = mod.GetType("Homestead.HomesteadUi");
        object helper = type.GetField("Instance", Any).GetValue(null);
        AccessTools.Method(type, "CreateRoot").Invoke(helper, new object[] { Hud.instance });
        Check((bool)Call("ZoneBlueprintStoreUi", "Open"), "store UI can reopen after root recreation");
        Check(((ICollection)type.GetField("_ownedSprites", Any).GetValue(helper)).Count == 3, "atlas sprite copies do not accumulate on recreation");
        Call("ZoneBlueprintStoreUi", "ResetForWorldSession");
        Check(!(bool)type.GetProperty("InputBlocked", Any).GetValue(null), "recreated UI releases input");
    }

    private void CaptureUi(Canvas canvas, string name)
    {
        // Batchmode has no usable screen backbuffer. Render the actual existing
        // canvas through an isolated camera, then restore its original settings.
        RenderMode mode = canvas.renderMode;
        Camera previousCamera = canvas.worldCamera;
        float distance = canvas.planeDistance;
        RenderTexture previousTarget = RenderTexture.active;
        GameObject owner = new GameObject("Probe UI camera", typeof(Camera));
        Camera camera = owner.GetComponent<Camera>();
        camera.enabled = false;
        camera.transform.position = new Vector3(0, 0, -100);
        camera.cullingMask = 1 << 5;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.12f, 0.16f, 0.18f, 1f);
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = 5f;
        int width = Screen.width;
        int height = Screen.height;
        RenderTexture target = RenderTexture.GetTemporary(width, height, 24);
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        try
        {
            camera.targetTexture = target;
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 1f;
            Canvas.ForceUpdateCanvases();
            camera.Render();
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            texture.Apply();
            File.WriteAllBytes(Path.Combine(Paths.GameRootPath, name), texture.EncodeToPNG());
        }
        finally
        {
            canvas.renderMode = mode;
            canvas.worldCamera = previousCamera;
            canvas.planeDistance = distance;
            RenderTexture.active = previousTarget;
            camera.targetTexture = null;
            RenderTexture.ReleaseTemporary(target);
            Destroy(texture);
            Destroy(owner);
        }
    }

    private object Call(string type, string method, params object[] arguments) =>
        AccessTools.Method(mod.GetType("Homestead." + type), method).Invoke(null, arguments);
    private void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        File.AppendAllText(report, "PASS " + description + "\n");
    }
    private void Fail(Exception ex)
    {
        File.AppendAllText(report, "FAIL " + ex + "\n");
        Logger.LogError(ex);
        Application.Quit(1);
    }
}
