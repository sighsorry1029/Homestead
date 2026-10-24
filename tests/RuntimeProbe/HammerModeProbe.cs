using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

// Runs in a disposable original-game client/server world, never the user's save.
public sealed partial class Probe
{
    private object hammerResult;
    private static bool hammerTerrainIgnoredWard;
    private static int hammerTerrainCalls;

    private object HammerInvoke(object target, string name, params object[] args)
    {
        Type type = target as Type ?? target.GetType();
        bool isStatic = target is Type;
        MethodInfo method = type.GetMethods(Any).Single(m => m.Name == name && m.IsStatic == isStatic &&
            m.GetParameters().Length >= args.Length &&
            m.GetParameters().Skip(args.Length).All(p => p.IsOptional) &&
            m.GetParameters().Take(args.Length).Select((p, i) => args[i] == null ||
                (p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType).IsInstanceOfType(args[i])).All(ok => ok));
        object[] supplied = args.Concat(method.GetParameters().Skip(args.Length).Select(p => p.DefaultValue)).ToArray();
        object result = method.Invoke(isStatic ? null : target, supplied);
        Array.Copy(supplied, args, args.Length);
        return result;
    }

    private Type HammerType(string name) => mod.GetType("Homestead." + name, true);
    private object HammerPolicy(Player player) => HammerInvoke(HammerType("BlueprintBuildCostPolicy"), "Capture", player);
    private void HammerRecordResult<T>(T result) => hammerResult = result;
    private static void HammerDrain(IEnumerator routine)
    {
        int remaining = 100000;
        while (routine.MoveNext())
        {
            if (--remaining <= 0) throw new Exception("Hammer fixture coroutine did not terminate.");
            if (routine.Current is IEnumerator nested) HammerDrain(nested);
        }
    }
    private static void HammerSetKey(GlobalKeys key, bool value)
    {
        if (value) ZoneSystem.instance.SetGlobalKey(key);
        else ZoneSystem.instance.RemoveGlobalKey(key);
        if (ZoneSystem.instance.GetGlobalKey(key) != value) throw new Exception("World modifier did not synchronize: " + key);
    }
    private object HammerPayableRequirements(IList entries, object policy)
    {
        Type listType = typeof(List<>).MakeGenericType(HammerType("ZoneBlueprintRequirement"));
        Delegate callback = Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(listType), this,
            GetType().GetMethod(nameof(HammerRecordResult), Any).MakeGenericMethod(listType));
        hammerResult = null;
        HammerDrain((IEnumerator)HammerInvoke(HammerType("ZoneBlueprintCommands"), "CollectRequirementsAsync", entries, policy, callback));
        return hammerResult;
    }
    private int HammerReadyCount(Component anchor)
    {
        object[] args = { "" };
        return ((IEnumerable)HammerInvoke(anchor, "GetReadyEntryIndices", args)).Cast<object>().Count();
    }
    private bool HammerPreviewHasPendingMaterial(Component anchor)
    {
        HammerInvoke(anchor, "UpdatePreview");
        object ghost = anchor.GetType().GetField("_previewGhost", Any).GetValue(anchor);
        GameObject root = (GameObject)ghost.GetType().GetProperty("Root", Any).GetValue(ghost);
        Check(root && root.transform.childCount == 1 && root.GetComponentsInChildren<Renderer>(true).Length > 0,
            "blueprint preview contains the original floor mesh");
        return root.transform.GetChild(0).GetComponent(HammerType("ZoneBlueprintGhostOwner+ZoneBlueprintGhostMaterialSet")) != null;
    }
    private void CheckHammerStationPreview(Component anchor, string context)
    {
        HammerInvoke(anchor, "UpdatePreview");
        object ghost = anchor.GetType().GetField("_stationGhost", Any).GetValue(anchor);
        GameObject root = (GameObject)ghost.GetType().GetProperty("Root", Any).GetValue(ghost);
        Check(root && root.transform.childCount > 0 && root.GetComponentsInChildren<Renderer>(true).Length > 0,
            context + " retains the required station mesh visual");
        Check(root.GetComponentsInChildren<CraftingStation>(true).Length == 0 &&
            root.transform.Cast<Transform>().All(child =>
                Vector2.Distance(new Vector2(child.position.x, child.position.z),
                    new Vector2(anchor.transform.position.x, anchor.transform.position.z)) > 1f),
            context + " keeps station previews around the chest without creating functional workstations");
    }
    private static IEnumerator HammerRejectTerrain(Action<bool, bool, Func<int>> completed)
    {
        completed(false, false, null);
        yield break;
    }
    private static bool HammerTerrainBoundary(bool ignoreWardAccess, Action<bool, bool, Func<int>> onComplete, ref IEnumerator __result)
    {
        hammerTerrainCalls++;
        hammerTerrainIgnoredWard = ignoreWardAccess;
        __result = HammerRejectTerrain(onComplete);
        return false;
    }

    private object HammerBlueprint(string name)
    {
        object blueprint = Call("ZoneBlueprintFileFormat", "Deserialize",
            "#Name:" + name + "\n#Pieces\nwood_floor;Building;0;0;0;0;0;0;1;\"\";1;1;1\n", name);
        string path = (string)Call("ZoneBlueprintCommands", "GetBlueprintPath", name);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
        HammerInvoke(HammerType("ZoneBlueprintFileFormat"), "WriteFile", path, blueprint);
        return blueprint;
    }
    private Component HammerChest(string name, Vector3 position, ICollection<GameObject> fixtures)
    {
        GameObject chest = Instantiate(ZNetScene.instance.GetPrefab("piece_chest_wood_blueprint"), position, Quaternion.identity);
        fixtures.Add(chest);
        Component anchor = chest.GetComponent(HammerType("ZoneBlueprintPlanAnchor"));
        // Manual ticking keeps the focused fixture deterministic.
        ((MonoBehaviour)anchor).CancelInvoke();
        HammerInvoke(anchor, "SetPlan", name, position + Vector3.right * 4f, Quaternion.identity);
        return anchor;
    }
    private ItemDrop.ItemData HammerWood(int amount)
    {
        GameObject prefab = ObjectDB.instance.GetItemPrefab("Wood");
        ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
        item.m_dropPrefab = prefab;
        item.m_stack = amount;
        return item;
    }
    private static int HammerNewWood(IEnumerable<ItemDrop> before) =>
        FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).Where(item => !before.Contains(item) && item &&
            item.m_itemData.m_shared.m_name == "$item_wood").Sum(item => item.m_itemData.m_stack);

    private IEnumerator CheckHammerMode()
    {
        var fixtures = new List<GameObject>();
        ItemDrop[] originalDrops = FindObjectsByType<ItemDrop>(FindObjectsSortMode.None);
        Piece[] originalPieces = FindObjectsByType<Piece>(FindObjectsSortMode.None);
        var keys = new Dictionary<GlobalKeys, bool>();
        foreach (GlobalKeys key in new[] { GlobalKeys.NoBuildCost, GlobalKeys.NoCraftCost, GlobalKeys.NoWorkbench })
            keys.Add(key, ZoneSystem.instance.GetGlobalKey(key));
        Player player = Player.m_localPlayer;
        FieldInfo debug = AccessTools.Field(typeof(Player), "m_noPlacementCost");
        bool oldDebug = player && (bool)debug.GetValue(player);
        FieldInfo pickup = AccessTools.Field(typeof(Player), "m_enableAutoPickup");
        bool oldPickup = (bool)pickup.GetValue(null);
        var settings = new Dictionary<ConfigEntryBase, object>();
        HashSet<string> known = player ? (HashSet<string>)AccessTools.Field(typeof(Player), "m_knownMaterial").GetValue(player) : null;
        Dictionary<string, int> stations = player ? (Dictionary<string, int>)AccessTools.Field(typeof(Player), "m_knownStations").GetValue(player) : null;
        string[] oldKnown = known?.ToArray();
        KeyValuePair<string, int>[] oldStations = stations?.ToArray();
        ItemDrop.ItemData[] oldInventory = player ? player.GetInventory().GetAllItems().ToArray() : null;
        var hooks = new Harmony("sighsorry.Homestead.HammerModeProbe");
        void SetSetting(string field, string value)
        {
            ConfigEntryBase setting = (ConfigEntryBase)HammerType("BlueprintConfig").GetField(field, Any).GetValue(null);
            if (!settings.ContainsKey(setting)) settings.Add(setting, setting.BoxedValue);
            setting.BoxedValue = Enum.Parse(setting.SettingType, value);
        }
        try
        {
            pickup.SetValue(null, false);
            if (player) debug.SetValue(player, false);
            foreach (GlobalKeys key in keys.Keys) HammerSetKey(key, false);
            SetSetting("_terrainSupport", "Off");
            SetSetting("_azuCraftyBoxesPullMode", "OpenAndConfirm");
            Piece floor = ZNetScene.instance.GetPrefab("wood_floor").GetComponent<Piece>();
            Check(floor && floor.m_resources.Sum(r => r.m_amount) > 1, "Hammer fixture uses original registered floor recipe");
            object normal = HammerPolicy(player);
            Check(!(bool)HammerInvoke(normal, "IsFree", floor), "normal world charges construction materials");
            HammerSetKey(GlobalKeys.NoBuildCost, true);
            object free = HammerPolicy(player);
            Check((bool)HammerInvoke(free, "IsFree", floor) && !(bool)free.GetType().GetField("DebugNoCost", Any).GetValue(free),
                "world Hammer mode waives construction cost without debug privileges");
            Check(!(bool)HammerInvoke(normal, "MatchesCurrent", player), "changing world cost invalidates a captured transaction");

            // Inactive test prefab preserves original component access restrictions.
            GameObject feastObject = new GameObject("HammerPolicyFeastFixture");
            feastObject.SetActive(false);
            fixtures.Add(feastObject);
            Piece feast = feastObject.AddComponent<Piece>();
            feastObject.AddComponent<Feast>();
            feast.m_resources = floor.m_resources;
            Check(feast.FreeBuildKey() == GlobalKeys.NoCraftCost && !(bool)HammerInvoke(free, "IsFree", feast),
                "Hammer mode does not waive a NoCraftCost piece");
            Type entryType = HammerType("ZoneBlueprintCommands+BlueprintLoadEntry");
            IList entries = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType));
            foreach (GameObject prefab in new[] { floor.gameObject, feastObject })
            {
                object entry = Activator.CreateInstance(HammerType("ZoneBlueprintEntry"));
                entry.GetType().GetProperty("Prefab").SetValue(entry, prefab.name);
                entries.Add(Activator.CreateInstance(entryType, new object[] { entry, prefab, Vector3.zero, Quaternion.identity, Vector3.one }));
            }
            object mixed = Activator.CreateInstance(HammerType("ZoneBlueprintCommands+BlueprintLoadPlan"),
                new object[] { entries, new List<Vector3>(), new List<Vector3>() });
            int Total(object requirements) => ((IEnumerable)requirements).Cast<object>().Sum(r => (int)r.GetType().GetProperty("Amount").GetValue(r));
            int raw = Total(HammerInvoke(HammerType("ZoneBlueprintCommands"), "CollectRequirements", mixed));
            int payable = Total(HammerPayableRequirements(entries, free));
            Check(raw == payable * 2 && payable > 0, "mixed plan retains craft costs and immutable full recipe quantities");
            HammerSetKey(GlobalKeys.NoCraftCost, true);
            Check(Total(HammerPayableRequirements(entries, HammerPolicy(player))) == 0,
                "both native world keys waive their corresponding pieces");
            HammerSetKey(GlobalKeys.NoCraftCost, false);

            CheckHammerDismantle(floor, feast, fixtures);
            CheckHammerStorePayment();

            Vector3 center = player ? player.transform.position + Vector3.up * 30f : new Vector3(0, 5000, 0);
            string name = "hammer_probe_" + Guid.NewGuid().ToString("N");
            object blueprint = HammerBlueprint(name);
            HammerSetKey(GlobalKeys.NoBuildCost, false);
            Component anchor = HammerChest(name, center, fixtures);
            ZDO chestZdo = anchor.GetComponent<ZNetView>().GetZDO();
            int woodCost = floor.m_resources.Where(r => r.m_resItem && r.m_resItem.m_itemData.m_shared.m_name == "$item_wood").Sum(r => r.m_amount);
            Check(woodCost > 1 && woodCost == floor.m_resources.Sum(r => r.m_amount), "floor fixture has a single multi-wood recipe");
            Inventory source = new Inventory("HammerSource", null, 4, 2);
            ItemDrop.ItemData wood = HammerWood(woodCost + 3);
            Check(source.AddItem(wood), "source inventory prepared for ordinary deposits");
            Check((int)HammerInvoke(anchor, "AcceptMaterialFromInventory", source, wood, 1) == 1,
                "normal construction accepts an actual partial deposit");
            string depositedPayload = chestZdo.GetString("hs_plan_refund_payload", "");
            Check(depositedPayload.Length > 0 && source.CountItems("$item_wood") == woodCost + 2, "partial deposit is recorded once");
            Check((int)HammerInvoke(anchor, "GetRequiredTotal") == woodCost && (int)HammerInvoke(anchor, "GetDepositedTotal") == 1 &&
                Total(HammerInvoke(anchor, "GetMissingRequirementList")) == woodCost - 1 && HammerReadyCount(anchor) == 0,
                "normal chest progress and preview follow the real recipe and deposited amount");
            if (player)
            {
                Check(HammerPreviewHasPendingMaterial(anchor), "incomplete normal blueprint keeps its pending mesh material");
                CheckHammerStationPreview(anchor, "normal plan");
            }
            HammerSetKey(GlobalKeys.NoBuildCost, true);
            Check((int)HammerInvoke(anchor, "GetRequiredTotal") == woodCost && (int)HammerInvoke(anchor, "GetDepositedTotal") == 1 &&
                Total(HammerInvoke(anchor, "GetMissingRequirementList")) == woodCost - 1 && HammerReadyCount(anchor) == 0,
                "Hammer mode does not zero the requirement UI or make incomplete previews ready");
            Check(chestZdo.GetString("hs_plan_refund_payload", "") == depositedPayload, "world mode change leaves the partial deposit untouched");
            if (player)
            {
                Check(HammerPreviewHasPendingMaterial(anchor), "incomplete Hammer blueprint retains the same pending mesh material");
                CheckHammerStationPreview(anchor, "Hammer plan");
                debug.SetValue(player, true);
                Check((int)HammerInvoke(anchor, "GetRequiredTotal") == woodCost && HammerReadyCount(anchor) == 0 &&
                    HammerPreviewHasPendingMaterial(anchor), "personal debug no-cost also preserves the old material-progress visual");
                debug.SetValue(player, false);
            }
            Check((int)HammerInvoke(anchor, "AcceptMaterialFromInventory", source, wood, woodCost - 1) == woodCost - 1 &&
                source.CountItems("$item_wood") == 3, "Hammer mode still accepts individual materials normally");
            Check(Total(HammerInvoke(anchor, "GetMissingRequirementList")) == 0 && HammerReadyCount(anchor) == 1,
                "deposits advance the Hammer preview to its original material-ready state");
            if (player) Check(!HammerPreviewHasPendingMaterial(anchor), "fully funded Hammer preview uses the normal mesh material");

            // Original successful settlement consumes deposits, even in a free world.
            ItemDrop[] beforeConsumed = FindObjectsByType<ItemDrop>(FindObjectsSortMode.None);
            chestZdo.Set("hs_plan_confirmed", true);
            HammerInvoke(anchor, "CompleteCommittedConfirmation");
            yield return null;
            Check(!anchor && HammerNewWood(beforeConsumed) == 0, "confirmed plan consumes its deposits and chest without a free-build refund");

            if (player)
            {
                ItemDrop.ItemData carried = HammerWood(woodCost * 3 + 3);
                Check(player.GetInventory().AddItem(carried), "carry materials for configured automatic collection");
                foreach (string trigger in new[] { "open", "confirm" })
                {
                    Component pullChest = HammerChest(name, center + Vector3.forward * (trigger == "open" ? 10f : 20f), fixtures);
                    int beforePull = player.GetInventory().CountItems("$item_wood");
                    Check((int)HammerInvoke(pullChest, "TryPullAvailableMaterials", player, trigger, false) == woodCost &&
                        player.GetInventory().CountItems("$item_wood") == beforePull - woodCost &&
                        (int)HammerInvoke(pullChest, "GetDepositedTotal") == woodCost,
                        "configured " + trigger + " collection keeps the original material-deposit behavior in Hammer mode");
                    Check((int)HammerInvoke(pullChest, "TryPullAvailableMaterials", player, trigger, false) == 0 &&
                        player.GetInventory().CountItems("$item_wood") == beforePull - woodCost,
                        "repeated " + trigger + " collection does not exceed the recipe requirement");
                    pullChest.GetComponent<ZNetView>().GetZDO().Set("hs_plan_confirmed", true);
                    HammerInvoke(pullChest, "CompleteCommittedConfirmation");
                }

                SetSetting("_azuCraftyBoxesPullMode", "Off");
                HammerSetKey(GlobalKeys.NoBuildCost, false);
                Component rejected = HammerChest(name, center + Vector3.back * 10f, fixtures);
                Inventory rejectedSource = new Inventory("HammerRejectedDeposit", null, 2, 1);
                ItemDrop.ItemData rejectedWood = HammerWood(1);
                rejectedSource.AddItem(rejectedWood);
                Check((int)HammerInvoke(rejected, "AcceptMaterialFromInventory", rejectedSource, rejectedWood, 1) == 1,
                    "failed-confirmation fixture holds an actual deposit");
                string rejectedPayload = rejected.GetComponent<ZNetView>().GetZDO().GetString("hs_plan_refund_payload", "");
                known.Clear();
                ItemDrop[] beforeRejected = FindObjectsByType<ItemDrop>(FindObjectsSortMode.None);
                Check((bool)HammerInvoke(rejected, "TryConfirm", player), "undiscovered normal-world plan starts its confirmation attempt");
                float rejectedDeadline = Time.realtimeSinceStartup + 15f;
                while (rejected && (bool)rejected.GetType().GetField("_confirmInProgress", Any).GetValue(rejected) &&
                    Time.realtimeSinceStartup < rejectedDeadline) yield return null;
                Check(rejected && !(bool)rejected.GetType().GetField("_confirmInProgress", Any).GetValue(rejected) &&
                    (int)HammerInvoke(rejected, "GetDepositedTotal") == 1 &&
                    rejected.GetComponent<ZNetView>().GetZDO().GetString("hs_plan_refund_payload", "") == rejectedPayload &&
                    HammerNewWood(beforeRejected) == 0, "rejected normal-world confirmation leaves its chest and deposit available without consumption");
                rejected.GetComponent<ZNetView>().GetZDO().Set("hs_plan_confirmed", true);
                HammerInvoke(rejected, "CompleteCommittedConfirmation");

                CheckHammerPlayerValidation(player, floor, blueprint, center, known, stations, SetSetting, hooks);
                HammerSetKey(GlobalKeys.NoWorkbench, false);
                known.Clear(); stations.Clear();
                for (int deposit = 0; deposit <= 1; deposit++)
                {
                    // Real reservation -> finalizer -> original settlement, with
                    // both empty and partially funded construction chests.
                    Component finish = HammerChest(name, center + Vector3.right * (10f + deposit * 10f), fixtures);
                    if (deposit > 0)
                    {
                        ItemDrop.ItemData one = HammerWood(deposit);
                        Inventory depositSource = new Inventory("HammerFinalDeposit", null, 2, 1);
                        depositSource.AddItem(one);
                        Check((int)HammerInvoke(finish, "AcceptMaterialFromInventory", depositSource, one, deposit) == deposit,
                            "end-to-end Hammer plan accepts its optional manual deposit");
                    }
                    Check((int)HammerInvoke(finish, "GetDepositedTotal") == deposit && HammerReadyCount(finish) == 0,
                        "unfinished preview remains material-incomplete before free confirmation (deposit " + deposit + ")");
                    CheckHammerStationPreview(finish, "Hammer confirmation fixture with undiscovered recipe/station (deposit " + deposit + ")");
                    ItemDrop[] beforeBuildDrops = FindObjectsByType<ItemDrop>(FindObjectsSortMode.None);
                    int inventoryBeforeBuild = player.GetInventory().CountItems("$item_wood");
                    long creator = player.GetPlayerID() + 50 + deposit;
                    finish.GetComponent<ZNetView>().GetZDO().Set(ZDOVars.s_creator, creator);
                    Check((bool)HammerInvoke(finish, "TryConfirm", player), "Hammer plan starts normal server-reserved confirmation (deposit " + deposit + ")");
                    float deadline = Time.realtimeSinceStartup + 15f;
                    while (finish && Time.realtimeSinceStartup < deadline) yield return null;
                    Check(!finish, "Hammer plan finishes without known recipe, known/nearby workbench, or full materials (deposit " + deposit + ")");
                    Check(HammerNewWood(beforeBuildDrops) == 0 && player.GetInventory().CountItems("$item_wood") == inventoryBeforeBuild,
                        "free confirmation consumes optional deposits without refunds or disabled automatic pulls (deposit " + deposit + ")");
                    Piece[] built = FindObjectsByType<Piece>(FindObjectsSortMode.None).Where(p => p && p.GetComponent<ZNetView>() &&
                        p.GetComponent<ZNetView>().IsValid() && p.GetComponent<ZNetView>().GetZDO().GetLong(ZDOVars.s_creator, 0) == creator).ToArray();
                    Check(built.Length == 1, "free confirmation creates exactly one piece with its original creator (deposit " + deposit + ")");
                }
                CheckConfirmationPermissions(player);
            }
        }
        finally
        {
            hooks.UnpatchSelf();
            Call("ZoneBlueprintConfirmation", "Reset");
            foreach (var setting in settings) setting.Key.BoxedValue = setting.Value;
            if (player)
            {
                debug.SetValue(player, oldDebug);
                known.Clear(); foreach (string value in oldKnown) known.Add(value);
                stations.Clear(); foreach (var value in oldStations) stations.Add(value.Key, value.Value);
                player.GetInventory().GetAllItems().Clear(); player.GetInventory().GetAllItems().AddRange(oldInventory);
            }
            pickup.SetValue(null, oldPickup);
            foreach (var key in keys) HammerSetKey(key.Key, key.Value);
            foreach (GameObject fixture in fixtures)
                if (fixture)
                {
                    ZNetView view = fixture.GetComponent<ZNetView>();
                    if (view && view.IsValid()) { view.GetZDO().Set("hs_plan_confirmed", true); ZNetScene.instance.Destroy(fixture); }
                    else Destroy(fixture);
                }
            foreach (Piece piece in FindObjectsByType<Piece>(FindObjectsSortMode.None))
                if (!originalPieces.Contains(piece) && piece && piece.GetComponent<ZNetView>() && piece.GetComponent<ZNetView>().IsValid())
                    ZNetScene.instance.Destroy(piece.gameObject);
            foreach (ItemDrop drop in FindObjectsByType<ItemDrop>(FindObjectsSortMode.None))
                if (!originalDrops.Contains(drop) && drop && drop.GetComponent<ZNetView>() && drop.GetComponent<ZNetView>().IsValid())
                    ZNetScene.instance.Destroy(drop.gameObject);
        }
    }

    private void CheckHammerDismantle(Piece floor, Piece feast, ICollection<GameObject> fixtures)
    {
        Type refundType = HammerType("ZoneAreaDismantleTool+MaterialRefund");
        int Refund(GameObject prefab, ZDO zdo)
        {
            IDictionary refunds = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), refundType));
            HammerInvoke(HammerType("ZoneAreaDismantleTool"), "CollectRefundMaterials", prefab, zdo, refunds);
            return refunds.Values.Cast<object>().Sum(r => (int)refundType.GetProperty("Amount", Any).GetValue(r));
        }
        foreach (bool loaded in new[] { false, true })
        {
            GameObject instance = loaded ? Instantiate(floor.gameObject, new Vector3(0, 5000, 0), Quaternion.identity) : null;
            if (instance) fixtures.Add(instance);
            ZDO zdo = instance ? instance.GetComponent<ZNetView>().GetZDO() :
                ZDOMan.instance.CreateNewZDO(new Vector3(0, 5000, 0), floor.name.GetStableHashCode());
            try
            {
                HammerSetKey(GlobalKeys.NoBuildCost, false);
                int normal = Refund(floor.gameObject, zdo);
                Check(normal > 0, "ordinary dismantle refunds original materials (" + loaded + ")");
                HammerSetKey(GlobalKeys.NoBuildCost, true);
                Check(Refund(floor.gameObject, zdo) == 0, "Hammer dismantle suppresses material refunds (" + loaded + ")");
                Check(Refund(feast.gameObject, zdo) == normal, "NoBuildCost does not suppress NoCraftCost refunds (" + loaded + ")");
                HammerSetKey(GlobalKeys.NoCraftCost, true);
                Check(Refund(feast.gameObject, zdo) == 0, "NoCraftCost suppresses corresponding refunds (" + loaded + ")");
                HammerSetKey(GlobalKeys.NoCraftCost, false);
                HammerSetKey(GlobalKeys.NoBuildCost, false);
                Check(Refund(floor.gameObject, zdo) == normal, "turning off Hammer restores native current-mode refunds (" + loaded + ")");
                zdo.Set("PT_BuildPaidFactor", 0.5f);
                Check(Refund(floor.gameObject, zdo) == floor.m_resources.Where(r => r.m_recover && r.m_resItem).Sum(r =>
                    (int)Call("PathOfValheimanCompat", "GetRefundAmount", zdo, r)), "ordinary dismantle preserves saved PoV payment (" + loaded + ")");
                zdo.Set("PT_BuildSaved", true);
                Check(Refund(floor.gameObject, zdo) == 0, "ordinary dismantle preserves saved PoV free-build marker (" + loaded + ")");
            }
            finally { if (!instance) ZDOMan.instance.DestroyZDO(zdo); }
        }
        HammerSetKey(GlobalKeys.NoBuildCost, true);
    }

    private void CheckHammerStorePayment()
    {
        Type priceType = HammerType("ZoneBlueprintStorePriceItem");
        object price = Activator.CreateInstance(priceType);
        priceType.GetProperty("ItemName").SetValue(price, "$item_wood");
        priceType.GetProperty("PrefabName").SetValue(price, "Wood");
        priceType.GetProperty("Amount").SetValue(price, 3);
        IList prices = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(priceType));
        prices.Add(price);
        ZDO zdo = ZDOMan.instance.CreateNewZDO(Vector3.up * 5000, "piece_chest_blueprint_store_purchase".GetStableHashCode());
        try
        {
            HammerSetKey(GlobalKeys.NoCraftCost, true);
            object[] args = { zdo, prices, "" };
            Check(!(bool)HammerInvoke(HammerType("ZoneBlueprintStoreChest"), "CanTakePurchasePriceItems", args),
                "Hammer/NoCraftCost cannot purchase an unpaid store blueprint");
            HammerInvoke(HammerType("ZoneBlueprintStoreChest"), "SetDepositedPriceItems", zdo, prices);
            Check((bool)HammerInvoke(HammerType("ZoneBlueprintStoreChest"), "TryTakePurchasePriceItems", args),
                "store purchase still consumes its actual three-material price");
            Check(!(bool)HammerInvoke(HammerType("ZoneBlueprintStoreChest"), "CanTakePurchasePriceItems", args),
                "store payment cannot be reused after purchase");
        }
        finally { ZDOMan.instance.DestroyZDO(zdo); HammerSetKey(GlobalKeys.NoCraftCost, false); }
    }

    private void CheckHammerPlayerValidation(Player player, Piece floor, object blueprint, Vector3 center,
        HashSet<string> known, Dictionary<string, int> stations, Action<string, string> setSetting, Harmony hooks)
    {
        Type resultType = HammerType("HomesteadCommandResult");
        Delegate callback = Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(resultType), this,
            GetType().GetMethod(nameof(HammerRecordResult), Any).MakeGenericMethod(resultType));
        int commits = 0;
        object Run(object policy = null)
        {
            hammerResult = null;
            IEnumerator routine = (IEnumerator)HammerInvoke(HammerType("ZoneBlueprintCommands"), "FinalizeBlueprintPlanAsync",
                "hammer_validation", blueprint, player, center, Quaternion.identity, new Dictionary<string, int>(),
                policy ?? HammerPolicy(player), player.GetPlayerID(), player.GetPlayerName(), -1,
                (Func<bool>)(() => true), (Func<bool>)(() => { commits++; return false; }), callback);
            HammerDrain(routine);
            Check(hammerResult != null, "confirmation returns an explicit result");
            return hammerResult;
        }
        bool Success(object result) => (bool)result.GetType().GetProperty("Success").GetValue(result);
        bool Failure(object result, string key) => !Success(result) &&
            (string)result.GetType().GetProperty("Message").GetValue(result) ==
            (string)HammerInvoke(HammerType("HomesteadLocalization"), "Format", key, new object[] { floor.name });
        HammerSetKey(GlobalKeys.NoBuildCost, false);
        HammerSetKey(GlobalKeys.NoCraftCost, false);
        known.Clear(); stations.Clear();
        if (floor.m_craftingStation) stations[floor.m_craftingStation.m_name] = 1;
        HammerSetKey(GlobalKeys.NoWorkbench, true);
        Check(Failure(Run(), "hs_blueprint_missing_known_station_or_materials") && commits == 0,
            "normal-world confirmation still rejects an undiscovered recipe");
        HammerSetKey(GlobalKeys.NoCraftCost, true);
        Check(Failure(Run(), "hs_blueprint_missing_known_station_or_materials") && commits == 0,
            "NoCraftCost alone does not bypass recipe discovery");
        HammerSetKey(GlobalKeys.NoCraftCost, false);
        foreach (Piece.Requirement req in floor.m_resources) if (req.m_resItem) known.Add(req.m_resItem.m_itemData.m_shared.m_name);
        stations.Clear();
        Check(Failure(Run(), "hs_blueprint_missing_known_station_or_materials") && commits == 0,
            "normal-world confirmation still requires discovery of the required station");
        if (floor.m_craftingStation) stations[floor.m_craftingStation.m_name] = 1;
        HammerSetKey(GlobalKeys.NoWorkbench, false);
        Check(floor.m_craftingStation && !CraftingStation.HaveBuildStationInRange(floor.m_craftingStation.m_name, player.transform.position),
            "station fixture has no nearby required workbench");
        Check(Failure(Run(), "hs_blueprint_missing_crafting_station") && commits == 0,
            "normal-world confirmation still requires an actual nearby workbench");
        HammerSetKey(GlobalKeys.NoCraftCost, true);
        Check(Failure(Run(), "hs_blueprint_missing_crafting_station") && commits == 0,
            "NoCraftCost alone does not bypass the nearby workbench requirement");
        HammerSetKey(GlobalKeys.NoCraftCost, false);
        HammerSetKey(GlobalKeys.NoWorkbench, true);
        Check(!Success(Run()) && commits == 0, "normal confirmation still requires paid materials despite known recipe and NoWorkbench");
        HammerSetKey(GlobalKeys.NoBuildCost, true);
        HammerSetKey(GlobalKeys.NoWorkbench, false);
        known.Clear(); stations.Clear();
        string originalDlc = floor.m_dlc;
        try
        {
            floor.m_dlc = "HomesteadProbeMissingDlc_" + Guid.NewGuid().ToString("N");
            Check(!Success(Run()) && commits == 0, "Hammer confirmation retains the unrelated DLC ownership requirement");
        }
        finally { floor.m_dlc = originalDlc; }
        int before = FindObjectsByType<Piece>(FindObjectsSortMode.None).Count(p => p.GetComponent<ZNetView>() &&
            p.GetComponent<ZNetView>().IsValid() && p.GetComponent<ZNetView>().GetZDO().GetBool("sighsorry.Homestead.blueprint_piece", false));
        Check(!Success(Run()) && commits == 1,
            "Hammer confirmation reaches commit without known recipe, station discovery, nearby workbench, or paid materials");
        int after = FindObjectsByType<Piece>(FindObjectsSortMode.None).Count(p => p.GetComponent<ZNetView>() &&
            p.GetComponent<ZNetView>().IsValid() && p.GetComponent<ZNetView>().GetZDO().GetBool("sighsorry.Homestead.blueprint_piece", false));
        Check(after == before, "failed commit rolls back the free structure instead of leaving unpaid pieces");
        object stale = HammerPolicy(player);
        HammerSetKey(GlobalKeys.NoBuildCost, false);
        Check(!Success(Run(stale)) && commits == 1, "changed world policy aborts before another commit");
        HammerSetKey(GlobalKeys.NoBuildCost, true);

        // Intercept the terrain boundary, not the policy/permission implementation.
        // This verifies the actual finalizer never promotes world-free to ward bypass.
        IList contacts = (IList)blueprint.GetType().GetProperty("TerrainContacts").GetValue(blueprint);
        contacts.Add(Activator.CreateInstance(HammerType("ZoneBlueprintTerrainContact")));
        setSetting("_terrainSupport", "On");
        hooks.Patch(AccessTools.Method(HammerType("HomesteadTerrainSupport"), "ApplyWorldSupportContactsAsync"),
            prefix: new HarmonyMethod(typeof(Probe), nameof(HammerTerrainBoundary)));
        hammerTerrainCalls = 0;
        hammerTerrainIgnoredWard = true;
        try
        {
            Check(!Success(Run()) && hammerTerrainCalls == 1 && !hammerTerrainIgnoredWard,
                "world-free finalizer retains the terrain ward-access boundary");
        }
        finally
        {
            hooks.UnpatchSelf();
            contacts.Clear();
            setSetting("_terrainSupport", "Off");
        }
    }
}
