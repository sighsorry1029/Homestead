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
    private bool failed;
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
        bool iconOnly = Environment.GetCommandLineArgs().Contains("-homestead-icon-probe");
        bool cameraTooltipOnly = Environment.GetCommandLineArgs().Contains("-homestead-camera-tooltip-probe");
        bool placementOnly = Environment.GetCommandLineArgs().Contains("-homestead-placement-probe");
        bool gridOnly = Environment.GetCommandLineArgs().Contains("-homestead-grid-probe");
        bool rotationOnly = Environment.GetCommandLineArgs().Contains("-homestead-rotation-probe");
        if (!headless)
        {
            yield return new WaitForSeconds(6f);
            FejdStartup startup = FindFirstObjectByType<FejdStartup>();
            float startupDeadline = Time.realtimeSinceStartup + 30f;
            while (!startup && Time.realtimeSinceStartup < startupDeadline)
            {
                yield return null;
                startup = FindFirstObjectByType<FejdStartup>();
            }
            try
            {
                Check(startup, "main-menu startup object available");
                PlayerProfile profile = new PlayerProfile("homestead_probe", FileHelpers.FileSource.Local);
                profile.SetName("Homestead Probe");
                profile.Save();
                Game.SetProfile("homestead_probe", FileHelpers.FileSource.Local);
                World world = World.GetCreateWorld("HomesteadProbe", FileHelpers.FileSource.Local);
                ZNet.SetServer(true, false, false, "HomesteadProbe", "", world);
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
        if ((placementOnly || gridOnly || rotationOnly) && !headless)
        {
            IEnumerator placement = rotationOnly ? CheckRotationControls() : gridOnly ? CheckCultivatorGrid() : CheckCultivatorRotation();
            int runtimeErrors = 0;
            Application.LogCallback onError = (message, stack, type) =>
            {
                if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert) runtimeErrors++;
            };
            Application.logMessageReceived += onError;
            try
            {
                while (true)
                {
                    try { if (!placement.MoveNext()) break; }
                    catch (Exception ex) { Fail(ex); yield break; }
                    yield return placement.Current;
                }
            }
            finally { Application.logMessageReceived -= onError; }
            if (runtimeErrors != 0) { Fail(new Exception("Placement runtime logged " + runtimeErrors + " errors; inspect unity.log.")); yield break; }
            Check(true, "no Unity errors during placement checks");
            File.AppendAllText(report, rotationOnly ? "COMPLETE rotation\n" : gridOnly ? "COMPLETE standalone grid\n" : "COMPLETE placement\n");
            Application.Quit();
            yield break;
        }
        if (iconOnly)
        {
            try
            {
                Check(!headless && ZNetScene.instance, "graphical world loaded for icon probe");
                CheckIconFixtures();
                BeginIconCacheRecovery();
            }
            catch (Exception ex) { Fail(ex); yield break; }
            yield return new WaitForSeconds(8f);
            try { CheckIconCacheRecovery(); }
            catch (Exception ex) { Fail(ex); yield break; }
            File.AppendAllText(report, "COMPLETE\n");
            Application.Quit();
            yield break;
        }
        if (cameraTooltipOnly && !headless)
        {
            yield return CheckBuildCameraTooltip();
            if (!failed) File.AppendAllText(report, "COMPLETE camera tooltip\n");
            Application.Quit(failed ? 1 : 0);
            yield break;
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
            if (!uiOnly) yield return CheckBuildCameraTooltip();
            if (!uiOnly) yield return CheckSharedBuild();
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

    private void PreparePlacementPlayer()
    {
        Player player = Player.m_localPlayer;
        Check(player && ObjectDB.instance, "world loaded for placement probe");
        // Keep this short fixture above water so the player's next Update does
        // not automatically unequip the tool between coroutine frames.
        Rigidbody body = player.GetComponent<Rigidbody>();
        body.position = new Vector3(body.position.x, Mathf.Max(body.position.y, 100f), body.position.z);
        body.linearVelocity = Vector3.zero;
        // Skipping the ride leaves the disabled intro animator's visible state
        // behind; clear it before checking the normal gameplay input gates.
        TextViewer.instance.Hide();
        TextViewer.instance.m_introRoot.SetActive(true);
        Animator intro = TextViewer.instance.m_introRoot.GetComponent<Animator>();
        intro.Rebind(); intro.Update(0f);
        TextViewer.instance.HideIntro();
    }

    private static KeyCode rotationHeld, rotationDown;
    private static float rotationScroll;
    private static bool RotationHeld(BepInEx.Configuration.KeyboardShortcut shortcut, ref bool __result)
    {
        __result = shortcut.MainKey != KeyCode.None && shortcut.MainKey == rotationHeld;
        return false;
    }
    private static bool RotationDown(BepInEx.Configuration.KeyboardShortcut shortcut, ref bool __result)
    {
        __result = shortcut.MainKey != KeyCode.None && shortcut.MainKey == rotationDown;
        return false;
    }
    private static bool RotationScroll(ref float __result) { __result = rotationScroll; return false; }

    private IEnumerator CheckRotationControls()
    {
        PreparePlacementPlayer();
        Player player = Player.m_localPlayer;
        player.SetGodMode(true);
        AccessTools.Field(typeof(Player), "m_noPlacementCost").SetValue(player, true);
        ItemDrop.ItemData Equip(string name)
        {
            GameObject prefab = ObjectDB.instance.GetItemPrefab(name);
            ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = prefab;
            item.m_durability = item.GetMaxDurability();
            Check(player.GetInventory().AddItem(item), "add rotation fixture " + name);
            AccessTools.Field(typeof(Character), "m_swimTimer").SetValue(player, 1f);
            Check(player.EquipItem(item), "equip rotation fixture " + name);
            return item;
        }
        ItemDrop.ItemData hammer = Equip("Hammer");
        yield return new WaitForSeconds(1f);
        Vector3 center = player.transform.position + Vector3.up * 1000f;
        player.transform.position = center;
        player.GetComponent<Rigidbody>().position = center;
        player.GetComponent<Rigidbody>().linearVelocity = Vector3.zero;
        Hud.CloseBuildUi();
        Piece wall = ZNetScene.instance.GetPrefab("woodwall").GetComponent<Piece>();
        Check(player.SetSelectedPiece(wall), "rotation fixture selects wall");
        Type adjust = mod.GetType("Homestead.ZonePlacementAdjust");
        Type config = mod.GetType("Homestead.PlacementControlConfig");
        BepInEx.Configuration.ConfigEntryBase Setting(string name) => (BepInEx.Configuration.ConfigEntryBase)config.GetField(name, Any).GetValue(null);
        Setting("_placementXAxisRotation").BoxedValue = 0f;
        Setting("_placementZAxisRotation").BoxedValue = 0f;
        Setting("_placementRotationStep").BoxedValue = 22.5f;
        var index = AccessTools.Field(typeof(Player), "m_placeRotation");
        var ghostField = AccessTools.Field(typeof(Player), "m_placementGhost");
        var updatePlacement = AccessTools.Method(typeof(Player), "UpdatePlacement");
        var updateGhost = AccessTools.Method(typeof(Player), "UpdatePlacementGhost");
        GameObject Ghost() => (GameObject)ghostField.GetValue(player);
        void GhostUpdate() => updateGhost.Invoke(player, new object[] { false });
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "RotationProbeFloor";
        floor.layer = LayerMask.NameToLayer("piece");
        floor.transform.position = center + new Vector3(0, -1f, 3);
        floor.transform.localScale = new Vector3(15, 0.2f, 15);
        Transform camera = GameCamera.instance.transform;
        void Aim(Vector3 target) { camera.position = center + new Vector3(0, 2f, -2); camera.LookAt(target); Physics.SyncTransforms(); }
        Aim(center + Vector3.forward * 3f);
        index.SetValue(player, 0);
        GhostUpdate();
        Check(Ghost().activeInHierarchy, "active native placement ghost");
        var input = new Harmony("sighsorry.Homestead.RotationInputProbe");
        Type helpers = mod.GetType("Homestead.ConfigValueHelpers");
        input.Patch(AccessTools.Method(helpers, "IsShortcutHeld"), prefix: new HarmonyMethod(typeof(Probe), nameof(RotationHeld)));
        input.Patch(AccessTools.Method(helpers, "IsShortcutDown"), prefix: new HarmonyMethod(typeof(Probe), nameof(RotationDown)));
        input.Patch(AccessTools.Method(typeof(ZInput), "GetMouseScrollWheel"), prefix: new HarmonyMethod(typeof(Probe), nameof(RotationScroll)));
        GameObject target = null;
        GameObject placed = null;
        void Press(KeyCode held, KeyCode down, float scroll, bool takeInput = true)
        {
            // Separate simulated input frames; repeated calls below deliberately
            // retain the frame stamp to test duplicate UpdatePlacement/ghost calls.
            adjust.GetField("_rotationInputFrame", Any).SetValue(null, -1);
            adjust.GetField("_rotationWheelFrame", Any).SetValue(null, -1);
            rotationHeld = held; rotationDown = down; rotationScroll = scroll;
            updatePlacement.Invoke(player, new object[] { takeInput, 0.016f });
            GhostUpdate();
        }
        bool Same(Quaternion a, Quaternion b) => Quaternion.Angle(a, b) < 0.05f;
        try
        {
            Check((bool)adjust.GetField("_rotationWheelFilterInstalled", Any).GetValue(null), "native wheel call isolated by transpiler");
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("bruce.valheim.comfymods.gizmo", out var gizmo))
            {
                Type rotations = gizmo.Instance.GetType().Assembly.GetType("ComfyGizmo.RotationManager");
                target = Instantiate(wall.gameObject, center + Vector3.forward * 3f, Quaternion.Euler(27, 47.3f, 18));
                rotations.GetMethod("MatchPieceRotation").Invoke(null, new object[] { target.GetComponent<Piece>() });
                Press(KeyCode.Mouse3, KeyCode.LeftBracket, 1f);
                Check(!(bool)Call("ZonePlacementAdjust", "CanAdjustRotation", player), "actual Gizmo owns ordinary rotation");
                Check(!(bool)adjust.GetField("_hasTemporaryRotation", Any).GetValue(null), "Gizmo prevents Homestead temporary rotation input");
                Check((int)index.GetValue(player) == 1, "Gizmo wheel is not suppressed by Homestead");
                Check(Same(Ghost().transform.rotation, (Quaternion)rotations.GetMethod("GetRotation").Invoke(null, null)), "native ghost uses actual Gizmo quaternion through both transpilers");
                Check((string)Call("ZonePlacementAdjust", "GetRotationHelp", player) == "", "Homestead rotation help hidden for Gizmo");
                yield break;
            }
            Press(KeyCode.Mouse3, KeyCode.None, 1f);
            Check((int)index.GetValue(player) == 0 && Same(Ghost().transform.rotation, Quaternion.Euler(22.5f, 0, 0)), "X side button rotates X only, native Y unchanged");
            updatePlacement.Invoke(player, new object[] { true, 0.016f });
            GhostUpdate(); GhostUpdate();
            Check(Same(Ghost().transform.rotation, Quaternion.Euler(22.5f, 0, 0)), "repeated placement/ghost updates consume input once per frame");
            var snapIndex = AccessTools.Field(typeof(Player), "m_manualSnapPoint");
            snapIndex.SetValue(player, 0);
            GhostUpdate();
            var snapPoints = new System.Collections.Generic.List<Transform>();
            Ghost().GetComponent<Piece>().GetSnapPoints(snapPoints);
            int mask = (int)AccessTools.Field(typeof(Player), "m_placeRayMask").GetValue(player);
            Check(Physics.Raycast(camera.position, camera.forward, out RaycastHit snapHit, 50f, mask), "manual-snap fixture ray hits floor");
            Check(Vector3.Distance(Ghost().transform.TransformPoint(snapPoints[0].localPosition), snapHit.point) < 0.01f,
                "native manual snap uses the tilted quaternion before computing position");
            snapIndex.SetValue(player, -1);
            GhostUpdate();
            var existingPieces = new System.Collections.Generic.HashSet<int>(FindObjectsByType<Piece>(FindObjectsSortMode.None).Select(p => p.GetInstanceID()));
            Check(player.TryPlacePiece(wall), "native TryPlacePiece accepts tilted wall");
            placed = FindObjectsByType<Piece>(FindObjectsSortMode.None).First(p => !existingPieces.Contains(p.GetInstanceID()) && p.name.StartsWith("woodwall")).gameObject;
            Check(Same(placed.transform.rotation, Quaternion.Euler(22.5f, 0, 0)) && Same(placed.GetComponent<ZNetView>().GetZDO().GetRotation(), placed.transform.rotation),
                "placed instance and original ZDO retain preview rotation without applying input twice");
            placed.SetActive(false); placed.GetComponent<ZNetView>().Destroy(); placed = null;
            Quaternion previous = Ghost().transform.rotation;
            Press(KeyCode.Mouse4, KeyCode.None, -1f);
            Check((int)index.GetValue(player) == 0 && Same(Ghost().transform.rotation, previous * Quaternion.AngleAxis(-22.5f, Vector3.forward)), "Z side button rotates local Z only");
            previous = Ghost().transform.rotation;
            Press(KeyCode.None, KeyCode.None, 1f);
            Check((int)index.GetValue(player) == 1 && Same(Ghost().transform.rotation, Quaternion.AngleAxis(22.5f, Vector3.up) * previous), "unmodified wheel retains native world Y rotation");
            Press(KeyCode.None, KeyCode.RightBracket, 1f);
            Check((int)index.GetValue(player) == 0 && Same(Ghost().transform.rotation, Quaternion.identity), "reset clears all three axes");
            target = Instantiate(wall.gameObject, center + Vector3.forward * 3f, Quaternion.Euler(32, 47.3f, 21));
            target.GetComponent<WearNTear>().enabled = false;
            Aim(target.GetComponentInChildren<Collider>().bounds.center);
            GameObject selected = player.GetBuildTool().GetSelectedPrefab();
            Press(KeyCode.None, KeyCode.LeftBracket, 0f);
            Check(player.GetBuildTool().GetSelectedPrefab() == selected && Same(Ghost().transform.rotation, target.transform.rotation), "copy preserves full off-step quaternion and selected piece");
            foreach (float pitch in new[] { 89.9f, 90f, 90.1f })
            {
                target.transform.rotation = Quaternion.Euler(pitch, 47.3f, 21f);
                Aim(target.GetComponentInChildren<Collider>().bounds.center);
                Press(KeyCode.None, KeyCode.LeftBracket, 1f);
                Check(Same(Ghost().transform.rotation, target.transform.rotation), "full quaternion copy near X singularity: " + pitch);
            }
            target.transform.rotation = Quaternion.Euler(32, 47.3f, 21);
            Aim(target.GetComponentInChildren<Collider>().bounds.center);
            Press(KeyCode.None, KeyCode.LeftBracket, 0f);
            Quaternion copied = Ghost().transform.rotation;
            GhostUpdate();
            Check(Same(Ghost().transform.rotation, copied), "copy survives repeat ghost evaluation");
            Setting("_placementRotationStep").BoxedValue = 10f;
            GhostUpdate();
            Check(Same(Ghost().transform.rotation, copied), "live rotation step change preserves copied quaternion");
            Setting("_placementRotationStep").BoxedValue = 22.5f;
            GhostUpdate();
            Check(player.SetSelectedPiece(ZNetScene.instance.GetPrefab("wood_beam").GetComponent<Piece>()), "switch to another ordinary piece");
            GhostUpdate();
            Check(Same(Ghost().transform.rotation, copied), "temporary rotation survives ordinary piece switch");
            Check((string)Call("ZonePlacementAdjust", "GetRotationHelp", player) is string help && help.Contains("Mouse4") && help.Contains("Mouse5") && help.Contains("[") && help.Contains("]"), "rotation help includes side-button labels and bracket actions");
            Call("HomesteadUi", "BlockInput", true);
            try
            {
                Press(KeyCode.None, KeyCode.RightBracket, 0f);
                Check(Same(Ghost().transform.rotation, copied), "custom store/UI input block prevents reset");
            }
            finally { Call("HomesteadUi", "BlockInput", false); }
            Type cameraConfig = mod.GetType("Homestead.BuildCameraConfig");
            var station = (BepInEx.Configuration.ConfigEntryBase)cameraConfig.GetField("_requireCraftingStation", Any).GetValue(null);
            var comfort = (BepInEx.Configuration.ConfigEntryBase)cameraConfig.GetField("_minimumComfortLevel", Any).GetValue(null);
            station.BoxedValue = Enum.Parse(station.SettingType, "Off"); comfort.BoxedValue = 0;
            Check((bool)Call("ZoneBuildCamera", "EnableBuildMode"), "enable build camera for distant copy");
            Vector3 targetPosition = target.transform.position;
            target.transform.position += Vector3.forward * 30f;
            target.transform.rotation = Quaternion.Euler(14, 73.2f, -18);
            Physics.SyncTransforms();
            camera.position = target.transform.position + new Vector3(0, 2f, -3);
            camera.LookAt(target.GetComponentInChildren<Collider>().bounds.center); Physics.SyncTransforms();
            GhostUpdate(); Press(KeyCode.None, KeyCode.LeftBracket, 0f);
            File.AppendAllText(report, $"Camera copy: eyeDistance={Vector3.Distance(player.GetEyePoint(), target.transform.position)}, active={Ghost().activeInHierarchy}, angleError={Quaternion.Angle(Ghost().transform.rotation, target.transform.rotation)}, maxRange={player.m_maxPlaceDistance}\n");
            Check(Vector3.Distance(player.GetEyePoint(), target.transform.position) > 20f && Same(Ghost().transform.rotation, target.transform.rotation), "build-camera copy uses camera origin rather than distant player's eye");
            Call("ZoneBuildCamera", "DisableBuildMode");
            target.transform.position = targetPosition; target.transform.rotation = copied;
            Physics.SyncTransforms();
            Aim(target.GetComponentInChildren<Collider>().bounds.center);
            GhostUpdate(); Press(KeyCode.None, KeyCode.LeftBracket, 0f);
            Call("ZoneAreaToolStatusHud", "EnsureInstance");
            Type hudType = mod.GetType("Homestead.ZoneAreaToolStatusHud");
            var hudState = Hud.instance.GetComponents(hudType).Cast<Behaviour>().First(b => b.enabled);
            hudType.GetField("_nextContextRefresh", Any).SetValue(hudState, 0f);
            hudType.GetMethod("Update", Any).Invoke(hudState, null);
            var label = (TMPro.TextMeshProUGUI)hudType.GetField("_text", Any).GetValue(hudState);
            label.ForceMeshUpdate();
            Check(label.text.Contains("Mouse4") && label.text.Contains("RX 32") && label.preferredWidth <= label.rectTransform.rect.width, "unified HUD shows actual copied angles and rotation help within bounds");
            CaptureUi(Hud.instance.m_buildHud.GetComponentInParent<Canvas>().rootCanvas, "rotation-hud.png");
            Press(KeyCode.Mouse3, KeyCode.None, 1f, takeInput: false);
            Check(Same(Ghost().transform.rotation, copied), "takeInput false blocks new rotation controls");
            Setting("_xRotationModifier").BoxedValue = new BepInEx.Configuration.KeyboardShortcut(KeyCode.None);
            int beforeIndex = (int)index.GetValue(player);
            Press(KeyCode.Mouse3, KeyCode.None, 1f);
            Check((int)index.GetValue(player) == beforeIndex + 1, "unbound axis modifier leaves native wheel available");
            Setting("_xRotationModifier").BoxedValue = new BepInEx.Configuration.KeyboardShortcut(KeyCode.Mouse3);
            Setting("_placementXAxisRotation").BoxedValue = 15f;
            GhostUpdate();
            Quaternion nativeYaw = Quaternion.Euler(0, (int)index.GetValue(player) * 22.5f, 0);
            Check(Same(Ghost().transform.rotation, nativeYaw * Quaternion.Euler(15, 0, 0)), "live X default clears temporary rotation");
            Press(KeyCode.None, KeyCode.RightBracket, 0f);
            Check(Same(Ghost().transform.rotation, Quaternion.identity) && (float)Setting("_placementXAxisRotation").BoxedValue == 15f, "reset overrides nonzero default without writing it");
            foreach (string tool in new[] { "ZoneBlueprintSaveTool", "ZoneAreaDismantleTool", "ZoneBlueprintSnapPointTool" })
            {
                Call(tool, "Activate", player);
                Check(!(bool)Call("ZonePlacementAdjust", "CanAdjustRotation", player) && (string)Call("ZonePlacementAdjust", "GetRotationHelp", player) == "", "rotation controls and help excluded for " + tool);
                Call(tool, "Deactivate");
            }
            ItemDrop.ItemData cultivator = Equip("Cultivator");
            Check(!(bool)Call("ZonePlacementAdjust", "CanAdjustRotation", player) && !(bool)adjust.GetField("_hasTemporaryRotation", Any).GetValue(null), "cultivator transition releases temporary rotation");
            AccessTools.Field(typeof(Character), "m_swimTimer").SetValue(player, 1f);
            player.EquipItem(hammer);
            Check(player.SetSelectedPiece(wall), "return to hammer wall");
            Aim(center + Vector3.forward * 3f); GhostUpdate();
            Press(KeyCode.Mouse3, KeyCode.None, 1f);
            Call("ZonePlacementAdjust", "ResetForWorldSession");
            Check(!(bool)adjust.GetField("_hasTemporaryRotation", Any).GetValue(null) && adjust.GetField("_inputPlayer", Any).GetValue(null) == null, "session cleanup releases input and quaternion state");
        }
        finally
        {
            rotationHeld = rotationDown = KeyCode.None; rotationScroll = 0;
            input.UnpatchSelf();
            if (target) target.GetComponent<ZNetView>().Destroy();
            if (placed) placed.GetComponent<ZNetView>().Destroy();
            Destroy(floor);
        }
    }

    private IEnumerator CheckCultivatorGrid()
    {
        Check(!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("advize.PlantEasily"), "standalone grid without PlantEasily");
        PreparePlacementPlayer();
        Player player = Player.m_localPlayer;
        AccessTools.Field(typeof(Player), "m_noPlacementCost").SetValue(player, true);
        foreach (string tool in new[] { "Cultivator", "Hammer", "Cultivator" })
        {
            GameObject prefab = ObjectDB.instance.GetItemPrefab(tool);
            ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = prefab;
            item.m_durability = item.GetMaxDurability();
            Check(player.GetInventory().AddItem(item), "add grid fixture tool: " + tool);
            AccessTools.Field(typeof(Character), "m_swimTimer").SetValue(player, 1f);
            Check(player.EquipItem(item), "equip grid fixture tool: " + tool);
            yield return null;
            Piece piece = tool == "Hammer"
                ? ZNetScene.instance.GetPrefab("woodwall").GetComponent<Piece>()
                : player.GetBuildTool().m_pieces.Where(p => p && p.GetComponent<Plant>()).Select(p => p.GetComponent<Piece>()).First();
            Check(player.SetSelectedPiece(piece), "select grid fixture: " + piece.name);
            CheckGridSnap(player, tool == "Hammer");
        }
    }

    // Simulate the shortcut result, not physical keyboard input. The production
    // Update gate, toggle, ghost postfix and native hint widgets still run.
    private static bool GridShortcutPressed(ref bool __result)
    {
        __result = true;
        return false;
    }

    private void CheckGridSnap(Player player, bool hammer)
    {
        Type grid = mod.GetType("Homestead.ZoneGridSnap");
        var active = grid.GetField("_active", Any);
        var config = mod.GetType("Homestead.PlacementControlConfig");
        var spacing = (BepInEx.Configuration.ConfigEntry<float>)config.GetField("_gridSnapSize", Any).GetValue(null);
        var shortcut = (BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut>)config.GetField("_gridSnapToggleHotkey", Any).GetValue(null);
        spacing.Value = 0.5f;
        shortcut.Value = new BepInEx.Configuration.KeyboardShortcut(KeyCode.G);
        active.SetValue(null, false);
        var input = new Harmony("sighsorry.Homestead.GridInputProbe");
        var shortcutMethod = AccessTools.Method(grid, "IsShortcutDownLenient");
        input.Patch(shortcutMethod, prefix: new HarmonyMethod(typeof(Probe), nameof(GridShortcutPressed)));
        try
        {
            Call("ZoneGridSnap", "Update");
            Check((bool)active.GetValue(null), "grid shortcut toggles on: " + player.GetBuildTool().name);
            var ghost = (GameObject)AccessTools.Field(typeof(Player), "m_placementGhost").GetValue(player);
            Vector3 position = new Vector3(1.24f, 50.123f, -1.24f);
            ghost.transform.position = position;
            Quaternion rotation = ghost.transform.rotation;
            Call("ZoneGridSnap+PlayerUpdatePlacementGhostPatch", "Postfix", player);
            Check(Vector3.Distance(ghost.transform.position, new Vector3(1f, position.y, -1f)) < 0.0001f &&
                  Quaternion.Angle(ghost.transform.rotation, rotation) < 0.0001f, "grid aligns crop/wall XZ without changing height or rotation");
            AccessTools.Method(typeof(Player), "UpdatePlacementGhost").Invoke(player, new object[] { false });
            Vector3 nativePosition = ghost.transform.position;
            Check(Mathf.Abs(nativePosition.x * 2f - Mathf.Round(nativePosition.x * 2f)) < 0.001f &&
                  Mathf.Abs(nativePosition.z * 2f - Mathf.Round(nativePosition.z * 2f)) < 0.001f, "native ghost update retains half-meter grid alignment");
            if (!hammer) CheckGridBoundaries(player, ghost);
            KeyHints hints = FindFirstObjectByType<KeyHints>();
            AccessTools.Field(typeof(KeyHints), "m_keyHintsEnabled").SetValue(hints, true);
            Call("ZoneBuildKeyHints", "UpdateHints", hints);
            Type hintType = mod.GetType("Homestead.ZoneBuildKeyHints");
            GameObject Hint(string field) => (GameObject)hintType.GetField(field, Any).GetValue(null);
            Check(Hint("_gridHint").activeSelf && Hint("_gridHint").GetComponentsInChildren<TMPro.TMP_Text>().Any(t => t.text == "G"), "grid G hint visible");
            Check(Hint("_offsetHint").activeSelf == hammer, "offset hint remains hammer-only");
            AccessTools.Field(typeof(KeyHints), "m_keyHintsEnabled").SetValue(hints, false);
            Call("ZoneBuildKeyHints", "UpdateHints", hints);
            Check(!Hint("_gridHint").activeSelf, "grid respects disabled key hints");
            AccessTools.Field(typeof(KeyHints), "m_keyHintsEnabled").SetValue(hints, true);
            Call("ZoneGridSnap", "Update");
            Check(!(bool)active.GetValue(null), "grid shortcut toggles off");
            ghost.transform.position = position;
            Call("ZoneGridSnap+PlayerUpdatePlacementGhostPatch", "Postfix", player);
            Check(ghost.transform.position == position, "disabled grid leaves crop/wall position unchanged");
        }
        finally { input.Unpatch(shortcutMethod, HarmonyPatchType.Prefix, input.Id); }
    }

    private void CheckGridBoundaries(Player player, GameObject ghost)
    {
        var status = AccessTools.Field(typeof(Player), "m_placementStatus");
        object priorStatus = status.GetValue(player);
        Vector3 priorPosition = ghost.transform.position;
        Vector3 inside = new Vector3(10000f, 100f, 10000f);
        Vector3 outside = inside + new Vector3(0.24f, 0f, 0.24f);
        GameObject noBuild = new GameObject("GridProbeNoBuild");
        noBuild.transform.position = inside;
        Location location = noBuild.AddComponent<Location>();
        location.m_exteriorRadius = 0.1f;
        GameObject ward = null;
        void SnapAndValidate(string initialStatus)
        {
            status.SetValue(player, Enum.Parse(status.FieldType, initialStatus));
            ghost.transform.position = outside;
            Call("ZoneGridSnap+PlayerUpdatePlacementGhostPatch", "Postfix", player);
            Call("ZonePlacementAdjust+PlayerUpdatePlacementGhostPatch", "Postfix", player);
        }
        try
        {
            Check(!Location.IsInsideNoBuildLocation(outside) && Location.IsInsideNoBuildLocation(inside), "grid fixture crosses native no-build boundary");
            SnapAndValidate("Valid");
            Check(status.GetValue(player).ToString() == "NoBuildZone", "snapped crop inside no-build area is rejected");
            location.m_noBuild = false;
            ward = Instantiate(ZNetScene.instance.GetPrefab("guard_stone"), inside, Quaternion.identity);
            ward.GetComponent<Piece>().SetCreator(player.GetPlayerID() + 1, default);
            ward.GetComponent<PrivateArea>().m_radius = 0.1f;
            ward.GetComponent<ZNetView>().GetZDO().Set(ZDOVars.s_enabled, true);
            Check(PrivateArea.CheckAccess(outside, flash: false) && !PrivateArea.CheckAccess(inside, flash: false), "grid fixture crosses native denied ward boundary");
            SnapAndValidate("Valid");
            Check(status.GetValue(player).ToString() == "PrivateZone", "snapped crop inside another player's ward is rejected");
            SnapAndValidate("Invalid");
            Check(status.GetValue(player).ToString() == "Invalid", "grid validation preserves earlier placement rejection");
        }
        finally
        {
            location.m_noBuild = false;
            Destroy(noBuild);
            if (ward) ward.GetComponent<ZNetView>().Destroy();
            ghost.transform.position = priorPosition;
            status.SetValue(player, priorStatus);
        }
    }

    private IEnumerator CheckCultivatorRotation()
    {
        PreparePlacementPlayer();
        Player player = Player.m_localPlayer;
        var plantPlugin = BepInEx.Bootstrap.Chainloader.PluginInfos["advize.PlantEasily"].Instance;
        var rows = plantPlugin.Config.Bind("General", "Rows", 2);
        var columns = plantPlugin.Config.Bind("General", "Columns", 2);
        plantPlugin.Config.Bind("General", "ModActive", true).Value = true;
        var rotation = AccessTools.Field(typeof(Player), "m_placeRotation");
        var step = AccessTools.Field(typeof(Player), "m_placeRotationDegrees");
        var setup = AccessTools.Method(typeof(Player), "SetupPlacementGhost");
        var updateGhost = AccessTools.Method(typeof(Player), "UpdatePlacementGhost");
        var config = mod.GetType("Homestead.PlacementControlConfig");
        BepInEx.Configuration.ConfigEntryBase Setting(string name) =>
            (BepInEx.Configuration.ConfigEntryBase)config.GetField(name, Any).GetValue(null);
        var rotationStep = Setting("_placementRotationStep");
        rotationStep.BoxedValue = 22.5f;
        AccessTools.Field(typeof(Player), "m_noPlacementCost").SetValue(player, true);
        void SwitchTool(ItemDrop.ItemData item)
        {
            // The isolated intro can drop the fixture into water.
            AccessTools.Field(typeof(Character), "m_swimTimer").SetValue(player, 1f);
            Check(player.IsItemEquiped(item) || player.EquipItem(item), "equip fixture tool: " + item.m_shared.m_name);
            Check(player.GetBuildTool(), "build table available: " + item.m_shared.m_name);
        }
        ItemDrop.ItemData Equip(string name)
        {
            GameObject prefab = ObjectDB.instance.GetItemPrefab(name);
            ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = prefab;
            item.m_durability = item.GetMaxDurability();
            Check(player.GetInventory().AddItem(item), "add fixture tool: " + name);
            SwitchTool(item);
            return item;
        }

        ItemDrop.ItemData cultivator = Equip("Cultivator");
        Piece plant = player.GetBuildTool().m_pieces
            .Where(p => p && p.GetComponent<Plant>()).Select(p => p.GetComponent<Piece>())
            .First(p => p && p.m_randomInitBuildRotation);
        Check(player.SetSelectedPiece(plant), "select random-initial-rotation plant: " + plant.name);
        Check(Harmony.GetPatchInfo(setup).Owners.Contains("advize.PlantEasily"), "actual PlantEasily patches installed");
        step.SetValue(player, 22.5f);
        rotation.SetValue(player, 4);
        UnityEngine.Random.InitState(2718);
        // These are the real config events fired by PE's LB + D-pad handler.
        // Physical controller input is not synthesized by this fixture.
        for (int i = 0; i < 8; i++)
        {
            if (i % 2 == 0) rows.Value = rows.Value == 2 ? 3 : 2;
            else columns.Value = columns.Value == 2 ? 3 : 2;
            updateGhost.Invoke(player, new object[] { false });
            Check((int)rotation.GetValue(player) == 4 && (float)step.GetValue(player) == 22.5f,
                "PlantEasily resize preserves 90-degree yaw, iteration " + i);
        }

        ItemDrop.ItemData hammer = Equip("Hammer");
        // Exercise the same frame before Unity destroys the old plant root.
        // The old PE hooks would recreate this hammer ghost and index an empty pool.
        var playerGhost = AccessTools.Field(typeof(Player), "m_placementGhost");
        GameObject hammerGhost = (GameObject)playerGhost.GetValue(player);
        updateGhost.Invoke(player, new object[] { false });
        Check((GameObject)playerGhost.GetValue(player) == hammerGhost, "same-frame cultivator-to-hammer update preserves current ghost without exceptions");
        yield return null;
        Piece wall = ZNetScene.instance.GetPrefab("woodwall").GetComponent<Piece>();
        Check(player.SetSelectedPiece(wall), "select ordinary hammer wall");
        rotationStep.BoxedValue = 5f;
        updateGhost.Invoke(player, new object[] { false });
        Check((float)step.GetValue(player) == 5f, "hammer uses configured 5-degree rotation step");
        rotation.SetValue(player, 18);
        SwitchTool(cultivator);
        Check((float)step.GetValue(player) == 22.5f && (int)rotation.GetValue(player) == 4,
            "hammer-to-cultivator restores step before PlantEasily snapshots yaw");
        yield return null;
        Check(player.SetSelectedPiece(plant), "reselect plant after tool change");
        rows.Value = rows.Value == 2 ? 3 : 2;
        updateGhost.Invoke(player, new object[] { false });
        Check((float)step.GetValue(player) == 22.5f && (int)rotation.GetValue(player) == 4,
            "custom hammer step does not leak into subsequent plant resize");

        Setting("_placementXAxisRotation").BoxedValue = 45f;
        Setting("_placementZAxisRotation").BoxedValue = 30f;
        mod.GetType("Homestead.ZoneGridSnap").GetField("_active", Any).SetValue(null, true);
        Check(!(bool)Call("ZonePlacementAdjust", "IsLocalPlacementContext", player), "cultivator excludes hammer offsets and axis tilt");
        CheckGridSnap(player, false);
        rows.Value = columns.Value = 1;
        updateGhost.Invoke(player, new object[] { false });
        player.PlacePiece(plant, player.transform.position + Vector3.forward * 3f, Quaternion.Euler(0, 90, 0), false);
        Check((int)rotation.GetValue(player) == 4, "PlantEasily planting preserves the next preview rotation");

        SwitchTool(hammer);
        yield return null;
        Check(player.SetSelectedPiece(wall), "hammer reselected after planting");
        updateGhost.Invoke(player, new object[] { false });
        Check((float)step.GetValue(player) == 5f, "hammer custom rotation restored on return");
        Check((bool)Call("ZonePlacementAdjust", "IsLocalPlacementContext", player), "hammer placement adjustments retained");
        Check((bool)Call("ZoneGridSnap", "IsLocalPlacementContext", player), "hammer grid retained");
        // A later mod's write must not be undone when leaving the hammer.
        step.SetValue(player, 15f);
        rotation.SetValue(player, 6);
        SwitchTool(cultivator);
        Check((float)step.GetValue(player) == 15f && (int)rotation.GetValue(player) == 6,
            "leaving hammer preserves a later rotation owner's step and yaw");
        step.SetValue(player, 22.5f);
        SwitchTool(hammer);
        Call("ZonePlacementAdjust", "ResetForWorldSession");
        Check((float)step.GetValue(player) == 22.5f, "session cleanup restores owned rotation step");
        Check(mod.GetType("Homestead.ZonePlacementAdjust").GetField("_rotationStepPlayer", Any).GetValue(null) == null,
            "session cleanup releases player reference");

        var peState = plantPlugin.GetType().Assembly.GetType("Advize_PlantEasily.PlacementState");
        var peGhost = peState.GetField("PlacementGhost", Any);
        var peGrid = plantPlugin.GetType().Assembly.GetType("Advize_PlantEasily.GhostGrid");
        var extraGhosts = (IList)peGrid.GetField("ExtraGhosts", Any).GetValue(null);
        rows.Value = columns.Value = 2;
        ItemDrop.ItemData hoe = Equip("Hoe");
        foreach (string target in new[] { "hammer", "hoe", "cultivate", "unequip", "disabled" })
        {
            SwitchTool(cultivator);
            Check(player.SetSelectedPiece(plant), "select plant before transition: " + target);
            updateGhost.Invoke(player, new object[] { false });
            Check(extraGhosts.Count >= 3 && (GameObject)peGhost.GetValue(null) == (GameObject)playerGhost.GetValue(player), "PlantEasily 2x2 grid rebuilt before transition: " + target);
            if (target == "hammer") SwitchTool(hammer);
            else if (target == "hoe") SwitchTool(hoe);
            else if (target == "cultivate")
                Check(player.SetSelectedPiece(player.GetBuildTool().m_pieces.Select(p => p.GetComponent<Piece>()).First(p => p && p.GetComponent<TerrainOp>())), "select cultivator terrain action");
            else if (target == "unequip") player.UnequipItem(cultivator);
            else
            {
                plantPlugin.Config.Bind("General", "ModActive", true).Value = false;
                setup.Invoke(player, null);
            }

            GameObject currentGhost = (GameObject)playerGhost.GetValue(player);
            updateGhost.Invoke(player, new object[] { false });
            Check((GameObject)playerGhost.GetValue(player) == currentGhost, "same-frame update preserves ghost without exceptions: " + target);
            yield return null;
            updateGhost.Invoke(player, new object[] { false });
            Check((GameObject)playerGhost.GetValue(player) == currentGhost, "next-frame update preserves ghost without exceptions: " + target);
            plantPlugin.Config.Bind("General", "ModActive", true).Value = true;
        }

        SwitchTool(cultivator);
        Check(player.SetSelectedPiece(plant), "return to planting after transitions");
        rows.Value = 3;
        updateGhost.Invoke(player, new object[] { false });
        Check(extraGhosts.Count >= 5 && (GameObject)peGhost.GetValue(null) == (GameObject)playerGhost.GetValue(player), "PlantEasily resizing resumes after transitions");

        Type peHooks = plantPlugin.GetType().Assembly.GetType("Advize_PlantEasily.PlacementPatches+PlayerUpdatePlacementGhost");
        bool GuardInstalled(string name) => Harmony.GetPatchInfo(AccessTools.Method(peHooks, name))?.Prefixes
            .Any(p => p.PatchMethod.DeclaringType.FullName == "Homestead.PlantEasilyCompat") == true;
        Check(GuardInstalled("Prefix") && GuardInstalled("Postfix"), "both PlantEasily update hooks guarded");
        Call("PlantEasilyCompat", "Shutdown");
        Check(!GuardInstalled("Prefix") && !GuardInstalled("Postfix"), "compatibility shutdown removes only its guards");
        Call("PlantEasilyCompat", "Initialize", mod.GetType("Homestead.HomesteadPlugin").GetField("HomesteadLogger", Any).GetValue(null), new Harmony("sighsorry.Homestead"));
        Check(GuardInstalled("Prefix") && GuardInstalled("Postfix"), "compatibility can initialize again after shutdown");
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
        CheckMaterialRefunds(player);
        CheckConfirmationPermissions(player);
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
        CheckTabAccess(build, index);
    }

    private void CheckTabAccess(BuildUi build, int index)
    {
        Type config = mod.GetType("Homestead.GeneralConfig");
        var setting = (BepInEx.Configuration.ConfigEntryBase)config.GetField("_tabAccess", Any).GetValue(null);
        object previous = setting.BoxedValue;
        object sync = mod.GetType("Homestead.HomesteadPlugin").GetField("ConfigSync", Any).GetValue(null);
        FieldInfo source = sync.GetType().GetField("isSourceOfTruth", Any);
        FieldInfo exempt = sync.GetType().GetField("lockExempt", Any);
        bool oldSource = (bool)source.GetValue(sync), oldExempt = (bool)exempt.GetValue(null);
        var buttons = (System.Collections.IList)AccessTools.Field(typeof(BuildUi), "m_tabButtons").GetValue(build);
        try
        {
            setting.BoxedValue = Enum.Parse(setting.SettingType, "AdminsOnly");
            source.SetValue(sync, false); exempt.SetValue(null, false);
            Call("ZoneBlueprintHammerTable", "UpdateAccess");
            Check(!((UnityEngine.UI.Button)buttons[index]).gameObject.activeSelf, "non-admin tab hidden on live permission change (simulated ServerSync state)");
            Check((int)AccessTools.Field(typeof(BuildUi), "m_currentPieceList").GetValue(build) == 0, "hidden selected tab returns to native tab");
            var lists = (System.Collections.IList)AccessTools.Field(typeof(BuildUi), "m_pieceLists").GetValue(build);
            var pieces = new System.Collections.Generic.List<Piece>();
            ((IPieceList)lists[index]).GetAvailablePiecesWithTag(-1, Player.m_localPlayer.GetBuildTool(), pieces);
            Check(pieces.Count == 0, "restricted Homestead list returns no tools");
            Check(ZNetScene.instance.GetPrefab("piece_chest_wood_blueprint"), "hidden tab keeps network chest registered");
            exempt.SetValue(null, true);
            Call("ZoneBlueprintHammerTable", "UpdateAccess");
            Check(((UnityEngine.UI.Button)buttons[index]).gameObject.activeSelf, "admin tab restored on live permission change");
        }
        finally
        {
            source.SetValue(sync, oldSource); exempt.SetValue(null, oldExempt);
            setting.BoxedValue = previous;
            Call("ZoneBlueprintHammerTable", "UpdateAccess");
            build.SelectPieceList(index);
        }
    }

    private IEnumerator CheckBuildCameraTooltip()
    {
        Hud hud = Hud.instance;
        Player player = Player.m_localPlayer;
        KeyHints hints = FindFirstObjectByType<KeyHints>();
        FieldInfo keyHintsEnabled = AccessTools.Field(typeof(KeyHints), "m_keyHintsEnabled");
        bool previousKeyHints = (bool)keyHintsEnabled.GetValue(hints);
        BepInEx.Configuration.ConfigEntryBase Setting(string type, string field) =>
            (BepInEx.Configuration.ConfigEntryBase)mod.GetType("Homestead." + type).GetField(field, Any).GetValue(null);
        var enabled = Setting("ClientConfig", "_hudControlsHelp");
        var cameraEnabled = Setting("BuildCameraConfig", "_enabled");
        var comfort = Setting("BuildCameraConfig", "_minimumComfortLevel");
        var shortcut = Setting("BuildCameraConfig", "_toggleHotkey");
        var requireStation = Setting("BuildCameraConfig", "_requireCraftingStation");
        object[] before = { enabled.BoxedValue, cameraEnabled.BoxedValue, comfort.BoxedValue, shortcut.BoxedValue, requireStation.BoxedValue };
        var positionAdjust = Setting("PlacementControlConfig", "_placementAdjustEnabled");
        var gridShortcut = Setting("PlacementControlConfig", "_gridSnapToggleHotkey");
        var gridSize = Setting("PlacementControlConfig", "_gridSnapSize");
        object[] placementBefore = { positionAdjust.BoxedValue, gridShortcut.BoxedValue, gridSize.BoxedValue };
        FieldInfo gridActive = mod.GetType("Homestead.ZoneGridSnap").GetField("_active", Any);
        bool previousGridActive = (bool)gridActive.GetValue(null);
        bool previousGodMode = player.InGodMode();
        FieldInfo noCost = AccessTools.Field(typeof(Player), "m_noPlacementCost");
        bool previousNoCost = (bool)noCost.GetValue(player);
        GameObject station = null;
        var hudX = Setting("ClientConfig", "_statusHudX");
        var hudFont = Setting("ClientConfig", "_statusHudFontSize");
        object previousHudX = hudX.BoxedValue, previousHudFont = hudFont.BoxedValue;
        Type statusType = mod.GetType("Homestead.ZoneAreaToolStatusHud");
        Component State() => hud.GetComponents(statusType).Cast<Behaviour>().First(c => c.enabled);
        TMPro.TextMeshProUGUI Label() => (TMPro.TextMeshProUGUI)statusType.GetField("_text", Any).GetValue(State());
        bool Visible() => Label().GetComponent<CanvasGroup>().alpha > 0f;
        string language = Localization.instance.GetSelectedLanguage();
        void LoadLanguage(string value)
        {
            // Match SetLanguage's cache clearing without writing shared platform preferences.
            AccessTools.Method(typeof(Localization), "Clear").Invoke(Localization.instance, null);
            Localization.instance.SetupLanguage(value);
        }
        void Refresh()
        {
            Call("ZoneAreaToolStatusHud", "EnsureInstance");
            statusType.GetField("_nextContextRefresh", Any).SetValue(State(), 0f);
            mod.GetType("Homestead.ZoneBuildCamera").GetField("_nextConditionRefresh", Any).SetValue(null, 0f);
            statusType.GetMethod("Update", Any).Invoke(State(), null);
            Canvas.ForceUpdateCanvases();
        }
        try
        {
            try
            {
                enabled.BoxedValue = Enum.Parse(enabled.SettingType, "On");
                cameraEnabled.BoxedValue = Enum.Parse(cameraEnabled.SettingType, "On");
                requireStation.BoxedValue = Enum.Parse(requireStation.SettingType, "On");
                comfort.BoxedValue = 0;
                positionAdjust.BoxedValue = Enum.Parse(positionAdjust.SettingType, "On");
                gridShortcut.BoxedValue = new BepInEx.Configuration.KeyboardShortcut(KeyCode.G);
                gridSize.BoxedValue = 0.5f;
                gridActive.SetValue(null, false);
                keyHintsEnabled.SetValue(hints, false);
                LoadLanguage("English");
                PreparePlacementPlayer();
                // Keep the visual fixture alive while the skipped intro fall resolves,
                // and away from stations left by any interrupted prior probe.
                player.SetGodMode(true);
                player.GetComponent<Rigidbody>().position += Vector3.up * 200f;
                noCost.SetValue(player, true);
                if (!player.InPlaceMode())
                {
                    GameObject prefab = ObjectDB.instance.GetItemPrefab("Hammer");
                    ItemDrop.ItemData hammer = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
                    hammer.m_dropPrefab = prefab;
                    hammer.m_durability = hammer.GetMaxDurability();
                    player.GetInventory().AddItem(hammer);
                    AccessTools.Field(typeof(Character), "m_swimTimer").SetValue(player, 1f);
                    player.EquipItem(hammer);
                }
                AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList").Invoke(player, null);
                Hud.CloseBuildUi();
                Check(player.SetSelectedPiece(ZNetScene.instance.GetPrefab("woodwall").GetComponent<Piece>()), "wood wall selected for camera tooltip fixture");
            }
            catch (Exception ex) { Fail(ex); yield break; }
            yield return new WaitForSeconds(1f);
            try
            {
                // Rigidbody interpolation can restore the intro pose during the first
                // frame. Move both representations after that frame, clear of old fixtures.
                float aboveStations = player.transform.position.y;
                foreach (CraftingStation priorStation in (IEnumerable)AccessTools.Field(typeof(CraftingStation), "m_allStations").GetValue(null))
                    if (priorStation) aboveStations = Mathf.Max(aboveStations, priorStation.transform.position.y + priorStation.m_rangeBuild);
                Vector3 away = new Vector3(player.transform.position.x, aboveStations + 1000f, player.transform.position.z);
                player.transform.position = away;
                player.GetComponent<Rigidbody>().position = away;
                player.GetComponent<Rigidbody>().linearVelocity = Vector3.zero;
                Refresh();
                var label = Label();
                Check(!(bool)Call("ZoneBuildCamera", "IsInputBlocked", true), "camera tooltip fixture has no blocking menu or intro");
                File.AppendAllText(report, $"Tooltip context: enabled={enabled.BoxedValue}, place={player.InPlaceMode()}, dead={player.IsDead()}, hud={hud.IsVisible()}, buildHud={hud.m_buildHud.activeInHierarchy}, tool={Call("ZoneBuildCamera", "ToolIsEquipped", player)}, label={(label ? label.text : "missing")}\n");
                Check(label && Visible(), "unified HUD visible with key hints disabled");
                Check(!hud.GetComponentsInChildren<TMPro.TMP_Text>(true).Any(t => t.name == "Homestead_BuildCameraTooltip"), "no separate piece-panel tooltip remains");
                string cachedText = label.text;
                DestroyImmediate(label.gameObject);
                Call("ZoneAreaToolStatusHud", "EnsureInstance");
                label = Label();
                Check(label.text == cachedText, "text-only recreation restores unchanged cached content immediately");
                Check(label.text.Contains((string)Call("ZoneBuildCamera", "GetConditionText", player)), "tooltip uses actual camera requirement");
                Check(requireStation.DefaultValue.ToString() == "On", "station requirement defaults to existing enabled policy");
                Check(!(bool)Call("ZoneBuildCamera", "BuildStationInRange", player), "camera fixture starts outside station range");
                Check(!(bool)Call("ZoneBuildCamera", "EnableBuildMode"), "required station blocks camera entry without a station");
                LoadLanguage("Korean"); Refresh();
                Check(label.text.Contains("작업대 필요") && !label.text.Contains("제작대"), "Korean station requirement says 작업대 필요");
                CaptureUi(hud.m_buildHud.GetComponentInParent<Canvas>().rootCanvas, "build-camera-need-station-ko.png");
                requireStation.BoxedValue = Enum.Parse(requireStation.SettingType, "Off"); Refresh();
                Check(label.text.Contains("사용 가능") && !label.text.Contains("작업대"), "station-free Korean tooltip shows ready");
                Check((bool)Call("ZoneBuildCamera", "EnableBuildMode"), "station toggle off permits camera entry without a station");
                requireStation.BoxedValue = Enum.Parse(requireStation.SettingType, "On");
                Check(!(bool)Call("ZoneBuildCamera", "ShouldDeactivateBuildMode", player), "station requirement still applies only to entry, not an active session");
                Call("ZoneBuildCamera", "DisableBuildMode");
                Check(!(bool)Call("ZoneBuildCamera", "EnableBuildMode"), "live station toggle on restores the entry requirement");
                requireStation.BoxedValue = Enum.Parse(requireStation.SettingType, "Off");
                comfort.BoxedValue = 30; Refresh();
                Check(label.text.Contains("안락함 30 필요") && !(bool)Call("ZoneBuildCamera", "EnableBuildMode"), "station toggle off preserves unmet comfort restriction");
                comfort.BoxedValue = 0;
                requireStation.BoxedValue = Enum.Parse(requireStation.SettingType, "On");
                LoadLanguage("English"); Refresh();
                label.ForceMeshUpdate();
                Check(!label.raycastTarget && !label.isTextTruncated && label.preferredHeight <= label.rectTransform.rect.height, "English unified HUD fits its dynamic height");
                Check(label.GetParsedText().Contains("↑↓←→/PgUp/PgDn: Move") && label.GetParsedText().Contains("G: Grid off 0.5m") && label.text.IndexOf("Move") < label.text.IndexOf("Build camera"), "placement and grid precede camera block");
                Vector2 positionBefore = label.rectTransform.anchoredPosition;
                hudX.BoxedValue = (float)previousHudX + 40f; hudFont.BoxedValue = 22; Refresh();
                Check(Mathf.Approximately(label.rectTransform.anchoredPosition.x, positionBefore.x + 40f) && label.fontSize == 22f, "existing HUD position and font settings apply live");
                hudX.BoxedValue = previousHudX; hudFont.BoxedValue = previousHudFont; Refresh();
                CaptureUi(hud.m_buildHud.GetComponentInParent<Canvas>().rootCanvas, "unified-hud-en.png");

                Call("ZoneAreaToolStatusHud", "ShowDefaultPlacement", Vector3.right * 0.5f, 1f, 315f, 15f, 30f, true);
                enabled.BoxedValue = Enum.Parse(enabled.SettingType, "Off"); Refresh();
                Check(Visible() && label.text.Contains("RX 15") && !label.text.Contains("PgUp") && !label.text.Contains("Build camera") && !label.text.Contains("Copy rotation"), "single help toggle preserves numeric HUD");
                enabled.BoxedValue = Enum.Parse(enabled.SettingType, "On"); Refresh();
                Check(label.gameObject.activeSelf, "client toggle reuses camera tooltip");
                shortcut.BoxedValue = new BepInEx.Configuration.KeyboardShortcut(KeyCode.K, KeyCode.LeftShift); Refresh();
                Check(label.text.Contains("Shift+K"), "tooltip follows rebound camera shortcut");
                cameraEnabled.BoxedValue = Enum.Parse(cameraEnabled.SettingType, "Off"); Refresh();
                Check(label.gameObject.activeSelf && !label.text.Contains("Build camera") && label.text.Contains("Move"), "disabled camera leaves available placement help visible");
                gridActive.SetValue(null, true); gridSize.BoxedValue = 0.75f;
                gridShortcut.BoxedValue = new BepInEx.Configuration.KeyboardShortcut(KeyCode.H, KeyCode.LeftShift); Refresh();
                Check(label.GetParsedText().Contains("Shift+H: Grid on 0.75m"), "placement tooltip follows grid state, spacing and rebound shortcut");
                positionAdjust.BoxedValue = Enum.Parse(positionAdjust.SettingType, "Off"); Refresh();
                Check(!label.text.Contains("PgUp") && label.text.Contains("Grid on"), "disabled offset hides only position help");
                gridShortcut.BoxedValue = new BepInEx.Configuration.KeyboardShortcut(KeyCode.None); Refresh();
                Check(!label.text.Contains("PgUp") && !label.text.Contains("Grid ") && !label.text.Contains("Build camera") && label.text.Contains("Copy rotation"), "disabled helpers leave rotation help available");
                positionAdjust.BoxedValue = Enum.Parse(positionAdjust.SettingType, "On");
                gridShortcut.BoxedValue = new BepInEx.Configuration.KeyboardShortcut(KeyCode.G);
                gridSize.BoxedValue = 0.5f;
                gridActive.SetValue(null, false);
                cameraEnabled.BoxedValue = Enum.Parse(cameraEnabled.SettingType, "On");

                foreach (string tool in new[] { "ZoneBlueprintSaveTool", "ZoneAreaDismantleTool", "ZoneBlueprintSnapPointTool" })
                {
                    Call(tool, "Activate", player); Refresh();
                    Check(!label.text.Contains("Grid "), "grid help hidden for " + tool);
                    Check(!label.text.Contains("Copy rotation"), "rotation help hidden for " + tool);
                    Check(label.text.Contains("PgUp") == (tool != "ZoneBlueprintSnapPointTool"), "position help matches " + tool);
                    Call(tool, "Deactivate");
                }
                ItemDrop.ItemData hammerItem = (ItemDrop.ItemData)AccessTools.Method(typeof(Humanoid), "GetRightItem").Invoke(player, null);
                GameObject cultivatorPrefab = ObjectDB.instance.GetItemPrefab("Cultivator");
                ItemDrop.ItemData cultivator = cultivatorPrefab.GetComponent<ItemDrop>().m_itemData.Clone();
                cultivator.m_dropPrefab = cultivatorPrefab; cultivator.m_durability = cultivator.GetMaxDurability();
                Check(player.GetInventory().AddItem(cultivator) && player.EquipItem(cultivator), "equip cultivator for tooltip");
                Check(player.SetSelectedPiece(player.GetBuildTool().m_pieces.First(p => p && p.GetComponent<Plant>()).GetComponent<Piece>()), "select plant for tooltip");
                Refresh();
                Check(!label.text.Contains("PgUp") && label.GetParsedText().Contains("G: Grid off 0.5m"), "cultivator shows grid without hammer-only movement help");
                Check(player.EquipItem(hammerItem), "restore hammer after tooltip fixture");
                Check(player.SetSelectedPiece(ZNetScene.instance.GetPrefab("woodwall").GetComponent<Piece>()), "restore wall after tooltip fixture");
                player.GetInventory().RemoveItem(cultivator);
                shortcut.BoxedValue = new BepInEx.Configuration.KeyboardShortcut(KeyCode.B);

                station = Instantiate(ZNetScene.instance.GetPrefab("piece_workbench"), player.transform.position + Vector3.right * 2f, Quaternion.identity);
            }
            catch (Exception ex) { Fail(ex); yield break; }
            // CraftingStation joins the game's station list in Start, on the next frame.
            yield return null;
            try
            {
                var label = Label();
                comfort.BoxedValue = 30; Refresh();
                Check(label.text.Contains((string)Call("HomesteadLocalization", "Format", "hs_build_camera_need_cozy", new object[] { 30 })), "tooltip explains unmet comfort condition");
                comfort.BoxedValue = 0; Refresh();
                Check(label.text.Contains((string)Call("HomesteadLocalization", "Text", "hs_build_camera_station_ready")), "tooltip reflects ready station without comfort restriction");
                Check((bool)Call("ZoneBuildCamera", "EnableBuildMode"), "camera activates for tooltip fixture"); Refresh();
                Check(label.text.Contains((string)Call("HomesteadLocalization", "Text", "hs_build_camera_active")), "tooltip reflects active camera");
                LoadLanguage("Korean");
                AccessTools.Method(typeof(Hud), "SetupPieceInfo").Invoke(hud, new object[] { ZNetScene.instance.GetPrefab("woodwall").GetComponent<Piece>() });
                Call("ZoneAreaToolStatusHud", "ShowDefaultPlacement", new Vector3(0.5f, 0f, -0.5f), 1f, 315f, 15f, 30f, true);
                Call("ZoneAreaToolStatusHud", "ShowBuildCameraDistance", 12f, 62f, "32+3*10(편안함)", 25f, "5+2*10(편안함)", 7f, "2+0.5*10(편안함)");
                Call("ZoneAreaToolStatusHud", "ShowDvergrCirclet", true, 1.5f, 1.2f);
                Refresh();
                Check(label.text.Contains("건축 카메라"), "camera tooltip localized in Korean");
                label.ForceMeshUpdate();
                Check(!label.isTextTruncated && label.preferredHeight <= label.rectTransform.rect.height && label.preferredWidth <= label.rectTransform.rect.width, "full Korean HUD fits dynamic bounds");
                Check(label.GetParsedText().Contains("↑↓←→/PgUp/PgDn: 위치 이동") && label.GetParsedText().Contains("G: 격자 끔 0.5m") && label.text.Contains("회전 복사") && label.text.EndsWith("조명 켬 | 범위 120% | 밝기 150%"), "grouped Korean help and final circlet row");
                CaptureUi(hud.m_buildHud.GetComponentInParent<Canvas>().rootCanvas, "unified-hud-full-ko.png");
                hudFont.BoxedValue = 26; Refresh(); label.ForceMeshUpdate();
                Check(label.preferredHeight <= label.rectTransform.rect.height && label.preferredWidth <= label.rectTransform.rect.width, "larger HUD font expands content bounds");
                hudFont.BoxedValue = previousHudFont;
                CheckAreaRepairHud(Refresh, Label);
                Check(player.SetSelectedPiece(ZNetScene.instance.GetPrefab("woodwall").GetComponent<Piece>()), "leave repair selection"); Refresh();
                Check(!label.text.Contains("범위 수리"), "repair status clears when another piece is selected");
                Call("ZoneBuildCamera", "DisableBuildMode"); Refresh();
                Check(!label.text.Contains("줍기 거리"), "camera distance rows clear on camera exit");

                Call("ZoneBlueprintSaveTool", "Activate", player);
                Call("ZoneAreaToolStatusHud", "Show", "20x16m", 315f, Vector3.right * 0.5f, 1f); Refresh();
                Check(label.text.StartsWith("지정 범위 20m | 16m") && !label.text.Contains("RX ") && !label.text.Contains("격자"), "area layout replaces axis values and excludes grid");
                CaptureUi(hud.m_buildHud.GetComponentInParent<Canvas>().rootCanvas, "unified-hud-area-ko.png");
                Call("ZoneBlueprintSaveTool", "Deactivate"); Call("ZoneAreaToolStatusHud", "Hide"); Refresh();
                Check(!label.text.Contains("지정 범위"), "area status clears on tool exit");

                var root = (GameObject)AccessTools.Field(typeof(Hud), "m_rootObject").GetValue(hud);
                Vector3 rootPosition = root.transform.localPosition;
                root.transform.localPosition = new Vector3(10000, 0, 0); Refresh();
                Check(!Visible(), "hidden HUD hides the unified display");
                statusType.GetField("_dvergrHideAfter", Any).SetValue(State(), Time.unscaledTime - 1f);
                root.transform.localPosition = rootPosition;
                Refresh(); Check(!label.text.Contains("조명"), "expired circlet row does not return after HUD is shown");
                var font = label.font;
                Call("ZoneAreaToolStatusHud", "Shutdown"); Refresh();
                var rebuilt = Label();
                Check(rebuilt && rebuilt != label && font && rebuilt.font == font && !rebuilt.text.Contains("RX "), "HUD recreation preserves native font and clears old session values");
                FindFirstObjectByType<BuildUi>(FindObjectsInactive.Include).OpenBuildMenu(); Refresh();
                Check(!rebuilt.text.Contains("건축 카메라") && !rebuilt.text.Contains("PgUp"), "build selection menu hides build information");
                Hud.CloseBuildUi();
                player.UnequipAllItems();
                Call("ZoneAreaToolStatusHud", "ShowDvergrCirclet", false, 1.1f, 1.3f); Refresh();
                Check(Visible() && rebuilt.text == "조명 끔 | 범위 130% | 밝기 110%", "circlet feedback remains outside build mode");
            }
            catch (Exception ex) { Fail(ex); yield break; }
            yield return new WaitForSeconds(1.2f);
            try
            {
                Refresh();
                Check(!Visible(), "circlet feedback expires after one second outside build mode");
                Check(hud.GetComponents(statusType).Length == 1 && hud.GetComponentsInChildren<TMPro.TMP_Text>(true).Count(t => t.name == "HomesteadUnifiedStatusHud") == 1, "one HUD component and text remain after deferred destruction");
            }
            catch (Exception ex) { Fail(ex); yield break; }
        }
        finally
        {
            Call("ZoneBuildCamera", "DisableBuildMode");
            enabled.BoxedValue = before[0]; cameraEnabled.BoxedValue = before[1]; comfort.BoxedValue = before[2]; shortcut.BoxedValue = before[3]; requireStation.BoxedValue = before[4];
            positionAdjust.BoxedValue = placementBefore[0]; gridShortcut.BoxedValue = placementBefore[1]; gridSize.BoxedValue = placementBefore[2];
            gridActive.SetValue(null, previousGridActive);
            player.SetGodMode(previousGodMode);
            Call("ZoneBlueprintSaveTool", "Deactivate"); Call("ZoneAreaDismantleTool", "Deactivate"); Call("ZoneBlueprintSnapPointTool", "Deactivate");
            keyHintsEnabled.SetValue(hints, previousKeyHints);
            noCost.SetValue(player, previousNoCost);
            LoadLanguage(language);
            hudX.BoxedValue = previousHudX; hudFont.BoxedValue = previousHudFont;
            if (station) ZNetScene.instance.Destroy(station);
        }
    }

    private static int hudRepairComfort;
    private static bool HudRepairComfortPrefix(ref int __result)
    {
        __result = hudRepairComfort;
        return false;
    }

    private void CheckAreaRepairHud(Action refresh, Func<TMPro.TextMeshProUGUI> label)
    {
        Player player = Player.m_localPlayer;
        BepInEx.Configuration.ConfigEntryBase Setting(string type, string field) =>
            (BepInEx.Configuration.ConfigEntryBase)mod.GetType("Homestead." + type).GetField(field, Any).GetValue(null);
        var baseRadius = Setting("AreaRepairConfig", "_baseRadius");
        var scale = Setting("AreaRepairConfig", "_comfortRadiusScale");
        var help = Setting("ClientConfig", "_hudControlsHelp");
        object[] before = { baseRadius.BoxedValue, scale.BoxedValue, help.BoxedValue };
        Piece selectedBefore = player.GetBuildTool().GetSelectedPiece();
        Piece repair = player.GetBuildTool().m_pieces.Select(p => p.GetComponent<Piece>()).First(p => p && p.m_repairPiece);
        var harmony = new Harmony("sighsorry.Homestead.UnifiedHudProbe");
        MethodInfo comfortMethod = AccessTools.Method(mod.GetType("Homestead.ZoneAreaRepair"), "GetComfortBonusLevel");
        // Control only the environment input; production radius calculation and HUD still run.
        harmony.Patch(comfortMethod, prefix: new HarmonyMethod(typeof(Probe), nameof(HudRepairComfortPrefix)));
        const string externalGuid = "Azumatt.AzuAreaRepair";
        var plugins = BepInEx.Bootstrap.Chainloader.PluginInfos;
        bool hadExternal = plugins.TryGetValue(externalGuid, out var external);
        try
        {
            Check(player.SetSelectedPiece(repair), "select native repair tool for unified HUD");
            baseRadius.BoxedValue = 0f; scale.BoxedValue = 4f; hudRepairComfort = 10; refresh();
            string text = label().text;
            Check(text.Contains("범위 수리 반경 8.6m") && text.Contains("0+4*세제곱근(10 편안함)"), "repair HUD uses production cube-root radius");
            Check(text.IndexOf("범위 수리") > text.IndexOf("줍기 거리") && !text.Contains("PgUp") && !text.Contains("격자"), "repair follows pickup range without ineffective placement controls");
            AccessTools.Method(typeof(Hud), "SetupPieceInfo").Invoke(Hud.instance, new object[] { repair });
            Check(Hud.instance.m_pieceDescription.text == Localization.instance.Localize(repair.m_description), "repair piece description remains vanilla");
            CaptureUi(Hud.instance.m_buildHud.GetComponentInParent<Canvas>().rootCanvas, "unified-hud-repair-ko.png");
            help.BoxedValue = Enum.Parse(help.SettingType, "Off"); refresh();
            Check(label().text.Contains("범위 수리 반경") && label().text.Contains("줍기 거리") && !label().text.Contains("건축 카메라"), "help toggle retains repair and camera values");
            Call("ZoneBuildCamera", "DisableBuildMode"); refresh();
            Check(label().text.Contains("범위 수리 반경") && !label().text.Contains("줍기 거리"), "repair remains when camera is off");
            hudRepairComfort = 0; refresh();
            Check(label().text.Contains("범위 수리 — 안락한 상태 필요"), "zero effective radius explains cozy requirement");
            baseRadius.BoxedValue = 3f; refresh();
            Check(label().text.Contains("범위 수리 반경 3m") && !label().text.Contains("안락한 상태 필요"), "non-cozy base radius remains available");
            scale.BoxedValue = 0f; refresh();
            Check(label().text.Contains("범위 수리 반경 3m"), "fixed-radius configuration is displayed");
            baseRadius.BoxedValue = 0f; refresh();
            Check(!label().text.Contains("범위 수리"), "disabled repair has no HUD row");
            baseRadius.BoxedValue = 3f;
            plugins[externalGuid] = null; refresh();
            Check(!label().text.Contains("범위 수리"), "external area repair suppresses Homestead repair row");
        }
        finally
        {
            harmony.UnpatchSelf();
            if (hadExternal) plugins[externalGuid] = external; else plugins.Remove(externalGuid);
            baseRadius.BoxedValue = before[0]; scale.BoxedValue = before[1]; help.BoxedValue = before[2];
            player.SetSelectedPiece(selectedBefore); refresh();
        }
    }

    private IEnumerator CheckSharedBuild()
    {
        Player player = Player.m_localPlayer;
        FieldInfo noCost = AccessTools.Field(typeof(Player), "m_noPlacementCost");
        bool previousNoCost = (bool)noCost.GetValue(player);
        GameObject chest = null;
        GameObject[] before = FindObjectsByType<Piece>(FindObjectsSortMode.None).Select(piece => piece.gameObject).ToArray();
        long creator = player.GetPlayerID() + 1;
        try
        {
            try
            {
                noCost.SetValue(player, true);
                object blueprint = Call("ZoneBlueprintFileFormat", "Deserialize", "#Name:shared_confirm_probe\n#Pieces\nwood_floor;Building;0;0;0;0;0;0;1;\"\";1;1;1\n", "shared_confirm_probe");
                string path = (string)Call("ZoneBlueprintCommands", "GetBlueprintPath", "shared_confirm_probe");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                mod.GetType("Homestead.ZoneBlueprintFileFormat").GetMethod("WriteFile", Any, null,
                    new[] { typeof(string), blueprint.GetType() }, null).Invoke(null, new[] { (object)path, blueprint });
                chest = Instantiate(ZNetScene.instance.GetPrefab("piece_chest_wood_blueprint"), player.transform.position + Vector3.right * 2f, Quaternion.identity);
                ZDO zdo = chest.GetComponent<ZNetView>().GetZDO();
                zdo.Set(ZDOVars.s_creator, creator); zdo.Set(ZDOVars.s_creatorName, "Original Builder");
                Component anchor = chest.GetComponent(mod.GetType("Homestead.ZoneBlueprintPlanAnchor"));
                anchor.GetType().GetMethod("SetPlan", Any).Invoke(anchor, new object[] { "shared_confirm_probe", player.transform.position + Vector3.right * 4f, Quaternion.identity });
                Check((bool)anchor.GetType().GetMethod("TryConfirm", Any).Invoke(anchor, new object[] { player }), "non-creator starts actual shared confirmation");
            }
            catch (Exception ex) { Fail(ex); yield break; }
            float deadline = Time.realtimeSinceStartup + 15f;
            while (chest && Time.realtimeSinceStartup < deadline) yield return null;
            try
            {
                Check(!chest, "shared confirmation consumes completed plan chest");
                Piece[] built = FindObjectsByType<Piece>(FindObjectsSortMode.None).Where(piece => !before.Contains(piece.gameObject) &&
                    piece.GetComponent<ZNetView>() && piece.GetComponent<ZNetView>().IsValid() &&
                    piece.GetComponent<ZNetView>().GetZDO().GetBool("sighsorry.Homestead.blueprint_piece", false)).ToArray();
                Check(built.Length == 1, "shared confirmation creates exactly one planned piece");
                ZDO zdo = built[0].GetComponent<ZNetView>().GetZDO();
                Check(zdo.GetLong(ZDOVars.s_creator) == creator && zdo.GetString(ZDOVars.s_creatorName) == "Original Builder", "finished piece retains original builder, not confirmer");
            }
            catch (Exception ex) { Fail(ex); yield break; }
        }
        finally
        {
            noCost.SetValue(player, previousNoCost);
            if (chest) ZNetScene.instance.Destroy(chest);
            foreach (Piece piece in FindObjectsByType<Piece>(FindObjectsSortMode.None))
                if (!before.Contains(piece.gameObject) && piece.GetComponent<ZNetView>() && piece.GetComponent<ZNetView>().IsValid()) ZNetScene.instance.Destroy(piece.gameObject);
            Call("ZoneBlueprintConfirmation", "Reset");
        }
    }

    private void CheckConfirmationPermissions(Player player)
    {
        GameObject chest = Instantiate(ZNetScene.instance.GetPrefab("piece_chest_wood_blueprint"), player.transform.position + Vector3.right * 2f, Quaternion.identity);
        ZDO zdo = chest.GetComponent<ZNetView>().GetZDO();
        long creator = player.GetPlayerID() + 1;
        zdo.Set(ZDOVars.s_creator, creator);
        zdo.Set(ZDOVars.s_creatorName, "Original Builder");
        Type permits = mod.GetType("Homestead.ZoneBlueprintConfirmation");
        var server = (System.Collections.IDictionary)permits.GetField("Permits", Any).GetValue(null);
        void Request(int action, string token, long sender = 0)
        {
            ZPackage package = new ZPackage();
            package.Write(action); package.Write(zdo.m_uid); package.Write(token); package.Write(99); package.SetPos(0);
            permits.GetMethod("ReceiveRequest", Any).Invoke(null, new object[] { sender, package });
        }
        try
        {
            object permit = Call("ZoneBlueprintConfirmation", "Begin", zdo);
            Type handle = permit.GetType();
            Check((bool)handle.GetProperty("IsValid", Any).GetValue(permit), "shared confirmation receives server permit");
            Check((long)handle.GetField("Creator", Any).GetValue(permit) == creator &&
                (string)handle.GetField("CreatorName", Any).GetValue(permit) == "Original Builder", "server preserves original builder identity");
            string token = (string)handle.GetField("Token", Any).GetValue(permit);
            Request(0, new string('a', 32));
            object held = server[zdo.m_uid];
            Check((string)held.GetType().GetField("Token", Any).GetValue(held) == token, "competing confirmation cannot replace permit");
            Request(2, token, 123456L);
            Check(server.Contains(zdo.m_uid), "another peer cannot release permit");
            Request(2, new string('b', 32));
            Check(server.Contains(zdo.m_uid), "stale token cannot release permit");
            held.GetType().GetField("Expires", Any).SetValue(held, Time.realtimeSinceStartup - 1f);
            Request(1, token);
            Check((float)held.GetType().GetField("Expires", Any).GetValue(held) < Time.realtimeSinceStartup,
                "expired server permit cannot be revived by renewal");
            handle.GetField("ValidUntil", Any).SetValue(permit, Time.realtimeSinceStartup - 1f);
            Check(!(bool)handle.GetProperty("IsValid", Any).GetValue(permit), "expired client permit stops confirmation");
            Call("ZoneBlueprintConfirmation", "End", permit, false);
            Check(!server.Contains(zdo.m_uid), "aborted confirmation releases permit");
            object committed = Call("ZoneBlueprintConfirmation", "Begin", zdo);
            Call("ZoneBlueprintConfirmation", "End", committed, true);
            Request(0, new string('c', 32));
            held = server[zdo.m_uid];
            Check((bool)held.GetType().GetField("Committed", Any).GetValue(held), "commit blocks reentry before confirmed ZDO replication");
        }
        finally
        {
            Call("ZoneBlueprintConfirmation", "Reset");
            ZNetScene.instance.Destroy(chest);
        }
    }

    private void CheckMaterialRefunds(Player player)
    {
        Inventory inventory = player.GetInventory();
        var saved = inventory.GetAllItems().ToArray();
        FieldInfo width = AccessTools.Field(typeof(Inventory), "m_width");
        FieldInfo height = AccessTools.Field(typeof(Inventory), "m_height");
        int oldWidth = (int)width.GetValue(inventory);
        int oldHeight = (int)height.GetValue(inventory);
        GameObject prefab = ObjectDB.instance.GetItemPrefab("Wood");
        ItemDrop.ItemData prototype = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
        prototype.m_dropPrefab = prefab;
        prototype.m_cheated = false;
        prototype.m_customData["homestead-refund-probe"] = "preserve";
        try
        {
            height.SetValue(inventory, 1);
            foreach (string scenario in new[] { "cheated mismatch", "partial merge", "compatible merge", "full inventory" })
            {
                inventory.GetAllItems().Clear();
                width.SetValue(inventory, scenario == "partial merge" ? 2 : 1);
                ItemDrop.ItemData existing = prototype.Clone();
                existing.m_stack = scenario == "full inventory" ? existing.m_shared.m_maxStackSize : 1;
                existing.m_cheated = scenario == "cheated mismatch";
                existing.m_gridPos = new Vector2i(0, 0);
                if (scenario == "partial merge") existing.m_stack = existing.m_shared.m_maxStackSize - 1;
                inventory.GetAllItems().Add(existing);
                if (scenario == "partial merge")
                {
                    ItemDrop.ItemData incompatible = prototype.Clone();
                    incompatible.m_stack = 1;
                    incompatible.m_cheated = true;
                    incompatible.m_gridPos = new Vector2i(1, 0);
                    inventory.GetAllItems().Add(incompatible);
                }
                int beforeCount = inventory.GetAllItems().Sum(item => item.m_stack);
                int[] beforeDrops = FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).Select(drop => drop.GetInstanceID()).ToArray();
                Call("ZoneMaterialEscrow", "GiveOrDropItem", prototype, 3, player.transform.position + Vector3.up * 20f, true, prefab);
                ItemDrop[] drops = FindObjectsByType<ItemDrop>(FindObjectsSortMode.None)
                    .Where(drop => !beforeDrops.Contains(drop.GetInstanceID())).ToArray();
                try
                {
                    int added = inventory.GetAllItems().Sum(item => item.m_stack) - beforeCount;
                    int dropped = drops.Sum(drop => drop.m_itemData.m_stack);
                    Check(added + dropped == 3, "refund preserves total quantity: " + scenario);
                    int expectedAdded = scenario == "compatible merge" ? 3 : scenario == "partial merge" ? 1 : 0;
                    Check(added == expectedAdded && dropped == 3 - expectedAdded, "refund only drops unaccepted remainder: " + scenario);
                    Check(drops.All(drop => !drop.m_itemData.m_cheated &&
                        drop.m_itemData.m_customData.TryGetValue("homestead-refund-probe", out string value) && value == "preserve"),
                        "refund preserves dropped item metadata: " + scenario);
                    Check(existing.m_cheated == (scenario == "cheated mismatch"), "refund does not change destination cheat flag: " + scenario);
                }
                finally
                {
                    foreach (ItemDrop drop in drops) ZNetScene.instance.Destroy(drop.gameObject);
                }
            }
        }
        finally
        {
            inventory.GetAllItems().Clear();
            inventory.GetAllItems().AddRange(saved);
            width.SetValue(inventory, oldWidth);
            height.SetValue(inventory, oldHeight);
            AccessTools.Method(typeof(Inventory), "Changed").Invoke(inventory, new object[] { true, false });
        }
    }

    private void CheckStoreAndIcon()
    {
        FindObjectsByType<BuildUi>(FindObjectsInactive.Include, FindObjectsSortMode.None).First().Close();
        object blueprint = Call("ZoneBlueprintFileFormat", "ReadFile", Path.Combine(Paths.GameRootPath, "probe.blueprint"));
        Sprite sprite = (Sprite)Call("ZoneBlueprintVisuals", "RenderAndCacheIcon", "probe", blueprint);
        Check(sprite && sprite.texture.width == 256, "native icon rendered");
        File.WriteAllBytes(Path.Combine(Paths.GameRootPath, "probe-icon.png"), sprite.texture.EncodeToPNG());
        CheckIconPixels(sprite.texture, "sample icon");
        CheckBlueprintPlacementModesAreExclusive(blueprint);
        Check((bool)Call("ZoneBlueprintStoreUi", "Open"), "store panel created");
        Call("ZoneBlueprintStoreUi", "RequestCurrentPage", null, true);
        Check((bool)mod.GetType("Homestead.HomesteadUi").GetProperty("InputBlocked", Any).GetValue(null), "modal blocks game input");
        CheckStoreCameraZoomGuard();
    }

    private void CheckIconFixtures()
    {
        string fixtures = Path.Combine(Paths.GameRootPath, "icon-fixtures");
        string[] paths = Directory.Exists(fixtures) ? Directory.GetFiles(fixtures, "*.blueprint") : new string[0];
        foreach (string path in new[] { Path.Combine(Paths.GameRootPath, "probe.blueprint") }.Concat(paths))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            object blueprint = Call("ZoneBlueprintFileFormat", "ReadFile", path);
            Sprite sprite = (Sprite)Call("ZoneBlueprintVisuals", "RenderAndCacheIcon", name, blueprint);
            Check(sprite && sprite.texture.width == 256, "icon rendered: " + name);
            File.WriteAllBytes(Path.Combine(Paths.GameRootPath, name + "-rendered.png"), sprite.texture.EncodeToPNG());
            CheckIconPixels(sprite.texture, name);
        }
    }

    private void CheckIconPixels(Texture2D texture, string name)
    {
        Color32[] pixels = texture.GetPixels32();
        int visible = pixels.Count(pixel => pixel.a > 0);
        int hiddenColor = pixels.Count(pixel => pixel.a == 0 && (pixel.r > 0 || pixel.g > 0 || pixel.b > 0));
        File.AppendAllText(report, $"ICON {name}: visible={visible}, hiddenColor={hiddenColor}, total={pixels.Length}\n");
        Check(visible > 100 && visible < pixels.Length, "icon has visible geometry and transparent background: " + name);
        Check(hiddenColor < visible / 10, "icon geometry does not lose its alpha: " + name);
    }

    private Piece iconRecoveryPiece;
    private byte[] validIconBytes;
    private void BeginIconCacheRecovery()
    {
        string validPath = (string)Call("ZoneBlueprintCommands", "GetBlueprintIconPath", "probe");
        validIconBytes = File.ReadAllBytes(validPath);
        Call("ZoneBlueprintVisuals", "InvalidateIcon", "probe");
        object[] validArgs = { "probe", null };
        Check((bool)Call("ZoneBlueprintVisuals", "TryGetIcon", validArgs), "valid disk icon accepted");

        const string name = "icon_recovery_probe";
        string iconPath = (string)Call("ZoneBlueprintCommands", "GetBlueprintIconPath", name);
        string blueprintPath = Path.ChangeExtension(iconPath, ".blueprint");
        File.Copy(Path.Combine(Paths.GameRootPath, "probe.blueprint"), blueprintPath, true);
        Texture2D blank = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        blank.SetPixels(Enumerable.Repeat(new Color(0.8f, 0.4f, 0.1f, 0f), 4).ToArray());
        blank.Apply();
        byte[] blankBytes = blank.EncodeToPNG();
        Destroy(blank);
        File.WriteAllBytes(validPath, blankBytes);
        Check((string)Call("ZoneBlueprintVisuals", "GetIconPngBase64", "probe") == "", "upload validates disk bytes despite stale valid sprite");
        File.WriteAllBytes(validPath, validIconBytes);
        Call("ZoneBlueprintVisuals", "SetCachedIcon", "probe", null);
        Check((string)Call("ZoneBlueprintVisuals", "GetIconPngBase64", "probe") == Convert.ToBase64String(validIconBytes),
            "valid disk icon can be uploaded despite cached null");
        Call("ZoneBlueprintVisuals", "InvalidateIcon", "probe");
        File.WriteAllBytes(iconPath, blankBytes);
        Call("ZoneBlueprintVisuals", "InvalidateIcon", name);
        object[] invalidArgs = { name, null };
        Check(!(bool)Call("ZoneBlueprintVisuals", "TryGetIcon", invalidArgs), "fully transparent disk icon rejected");
        Check(File.ReadAllBytes(iconPath).SequenceEqual(blankBytes), "invalid icon retained until replacement is ready");
        Check((string)Call("ZoneBlueprintVisuals", "GetIconPngBase64", name) == "", "invalid icon is not uploaded while awaiting replacement");
        Check((Sprite)Call("ZoneBlueprintVisuals", "CreateIconFromBase64", name, Convert.ToBase64String(blankBytes)) == null,
            "fully transparent store icon falls back");
        object blueprint = Call("ZoneBlueprintFileFormat", "ReadFile", blueprintPath);
        iconRecoveryPiece = (Piece)Call("ZoneBlueprintToolPieceFactory", "CreateBlueprint", name, blueprint,
            default(Piece.PieceCategory), "Alt", true);
    }

    private void CheckIconCacheRecovery()
    {
        object[] args = { "icon_recovery_probe", null };
        Check((bool)Call("ZoneBlueprintVisuals", "TryGetIcon", args), "normal hammer icon queue regenerated invalid capture");
        Sprite icon = (Sprite)args[1];
        CheckIconPixels(icon.texture, "recovered icon");
        Check((string)Call("ZoneBlueprintVisuals", "GetIconPngBase64", "icon_recovery_probe") != "", "recovered icon can be uploaded");
        string validPath = (string)Call("ZoneBlueprintCommands", "GetBlueprintIconPath", "probe");
        Check(File.ReadAllBytes(validPath).SequenceEqual(validIconBytes), "valid icon file left unchanged");
        Destroy(iconRecoveryPiece.gameObject);
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
        failed = true;
        File.AppendAllText(report, "FAIL " + ex + "\n");
        Logger.LogError(ex);
        Application.Quit(1);
    }
}
