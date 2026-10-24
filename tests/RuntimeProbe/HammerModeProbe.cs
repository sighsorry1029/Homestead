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
    private static bool HammerFailDrop() => throw new InvalidOperationException("Injected refund drop failure.");
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
            int payable = Total(HammerInvoke(HammerType("ZoneBlueprintCommands"), "CollectRequirements", mixed, free));
            Check(raw == payable * 2 && payable > 0, "mixed plan retains craft costs and immutable full recipe quantities");
            HammerSetKey(GlobalKeys.NoCraftCost, true);
            Check(Total(HammerInvoke(HammerType("ZoneBlueprintCommands"), "CollectRequirements", mixed, HammerPolicy(player))) == 0,
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
            Inventory source = new Inventory("HammerSource", null, 4, 2);
            ItemDrop.ItemData wood = HammerWood(5);
            Check(source.AddItem(wood), "source inventory prepared for deposit preservation");
            Check((int)HammerInvoke(anchor, "AcceptMaterialFromInventory", source, wood, 1) == 1,
                "normal construction accepts an actual partial deposit");
            string depositedPayload = chestZdo.GetString("hs_plan_refund_payload", "");
            Check(depositedPayload.Length > 0 && source.CountItems("$item_wood") == 4, "partial deposit is durably recorded once");
            HammerSetKey(GlobalKeys.NoBuildCost, true);
            Check(((IList)HammerInvoke(anchor, "GetMissingRequirementList")).Count == 0, "existing chest updates payable requirements after world mode change");
            Check((int)HammerInvoke(anchor, "AcceptMaterialFromInventory", source, wood, 1) == 0 && source.CountItems("$item_wood") == 4,
                "world-free chest refuses additional material consumption");
            Check(chestZdo.GetString("hs_plan_refund_payload", "") == depositedPayload, "world mode change preserves earlier deposit ledger");
            object[] readyArgs = { "" };
            Check(((IEnumerable)HammerInvoke(anchor, "GetReadyEntryIndices", readyArgs)).Cast<object>().Count() == 1, "free plan preview is material-ready");
            if (player)
            {
                ItemDrop.ItemData carried = HammerWood(7);
                Check(player.GetInventory().AddItem(carried), "carry spare building materials for automatic pull check");
                int beforePull = player.GetInventory().CountItems("$item_wood");
                foreach (string trigger in new[] { "open", "confirm" })
                    Check((int)HammerInvoke(anchor, "TryPullAvailableMaterials", player, trigger, false) == 0 &&
                        player.GetInventory().CountItems("$item_wood") == beforePull, "free " + trigger + " does not auto-pull player/nearby materials");
            }

            // Simulate a persisted committed chest resumed after interrupted cleanup.
            chestZdo.Set("hs_plan_confirmed_refund", depositedPayload);
            chestZdo.Set("hs_plan_confirmed", true);
            ItemDrop[] beforeRefund = FindObjectsByType<ItemDrop>(FindObjectsSortMode.None);
            Container refundContainer = anchor.GetComponent<Container>();
            AccessTools.Field(typeof(Container), "m_inUse").SetValue(refundContainer, true);
            Check(!(bool)HammerInvoke(anchor, "TryReturnCommittedMaterials") && HammerNewWood(beforeRefund) == 0 &&
                chestZdo.GetString("hs_plan_confirmed_refund", "") == depositedPayload,
                "open confirmed chest defers refund until its inventory can synchronize");
            AccessTools.Field(typeof(Container), "m_inUse").SetValue(refundContainer, false);
            hooks.Patch(AccessTools.Method(typeof(ItemDrop), "DropItem"), prefix: new HarmonyMethod(typeof(Probe), nameof(HammerFailDrop)));
            HammerInvoke(anchor, "CompleteCommittedConfirmation");
            Check(anchor && chestZdo.IsValid() &&
                chestZdo.GetString("hs_plan_confirmed_refund", "") == depositedPayload && HammerNewWood(beforeRefund) == 0,
                "failed refund leaves the committed durable claim intact");
            hooks.UnpatchSelf();
            HammerSetKey(GlobalKeys.NoBuildCost, false);
            Check((int)HammerInvoke(anchor, "AcceptMaterialFromInventory", source, wood, 1) == 0 && source.CountItems("$item_wood") == 4,
                "committed chest awaiting refund refuses further deposits after world becomes paid");
            if (player)
            {
                int beforePull = player.GetInventory().CountItems("$item_wood");
                Check((int)HammerInvoke(anchor, "TryPullAvailableMaterials", player, "confirm", false) == 0 &&
                    player.GetInventory().CountItems("$item_wood") == beforePull, "committed chest awaiting refund cannot auto-pull more materials");
            }
            ZPackage missingRefund = new ZPackage();
            missingRefund.Write(1); missingRefund.Write(1); missingRefund.Write("$item_wood");
            missingRefund.Write("HomesteadMissingRefundFixture"); missingRefund.Write(1);
            string missingPayload = missingRefund.GetBase64();
            chestZdo.Set("hs_plan_confirmed_refund", missingPayload);
            Check(!(bool)HammerInvoke(anchor, "TryReturnCommittedMaterials") &&
                chestZdo.GetString("hs_plan_confirmed_refund", "") == missingPayload && HammerNewWood(beforeRefund) == 0,
                "missing refund prefab preserves the pending claim without destroying the chest");
            ZNetScene.instance.Destroy(anchor.gameObject);
            Check(anchor && chestZdo.IsValid() && chestZdo.GetString("hs_plan_confirmed_refund", "") == missingPayload,
                "actual scene destruction cannot erase an unresolved committed refund");
            chestZdo.Set("hs_plan_confirmed_refund", depositedPayload);
            HammerSetKey(GlobalKeys.NoBuildCost, true);
            Check((bool)HammerInvoke(anchor, "TryReturnCommittedMaterials") && HammerNewWood(beforeRefund) == 1,
                "committed refund retry returns exactly the original deposited quantity");
            Check((bool)HammerInvoke(anchor, "TryReturnCommittedMaterials") && HammerNewWood(beforeRefund) == 1 &&
                chestZdo.GetString("hs_plan_confirmed_refund", "").Length == 0,
                "repeated committed cleanup does not duplicate deposited material");
            HammerInvoke(anchor, "CompleteCommittedConfirmation");

            if (player)
            {
                CheckHammerPlayerValidation(player, floor, blueprint, center, known, stations, SetSetting, hooks);
                // Actual chest reservation -> free build -> durable unused-material refund.
                HammerSetKey(GlobalKeys.NoBuildCost, false);
                HammerSetKey(GlobalKeys.NoWorkbench, true);
                Component finish = HammerChest(name, center + Vector3.right * 10f, fixtures);
                ItemDrop.ItemData one = HammerWood(1);
                Inventory depositSource = new Inventory("HammerFinalDeposit", null, 2, 1);
                depositSource.AddItem(one);
                Check((int)HammerInvoke(finish, "AcceptMaterialFromInventory", depositSource, one, 1) == 1, "end-to-end plan receives one paid material before Hammer mode");
                HammerSetKey(GlobalKeys.NoBuildCost, true);
                ItemDrop[] beforeBuildDrops = FindObjectsByType<ItemDrop>(FindObjectsSortMode.None);
                int inventoryBeforeBuild = player.GetInventory().CountItems("$item_wood");
                long creator = player.GetPlayerID() + 50;
                finish.GetComponent<ZNetView>().GetZDO().Set(ZDOVars.s_creator, creator);
                Check((bool)HammerInvoke(finish, "TryConfirm", player), "Hammer plan starts normal server-reserved confirmation");
                float deadline = Time.realtimeSinceStartup + 15f;
                while (finish && Time.realtimeSinceStartup < deadline) yield return null;
                Check(!finish, "Hammer plan finishes with no additional materials");
                Check(HammerNewWood(beforeBuildDrops) == 1 && player.GetInventory().CountItems("$item_wood") == inventoryBeforeBuild,
                    "actual free confirmation refunds earlier deposit and leaves carried materials untouched");
                Piece[] built = FindObjectsByType<Piece>(FindObjectsSortMode.None).Where(p => p && p.GetComponent<ZNetView>() &&
                    p.GetComponent<ZNetView>().IsValid() && p.GetComponent<ZNetView>().GetZDO().GetLong(ZDOVars.s_creator, 0) == creator).ToArray();
                Check(built.Length == 1, "actual free confirmation creates exactly one piece with original creator");
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
        known.Clear(); stations.Clear();
        if (floor.m_craftingStation) stations[floor.m_craftingStation.m_name] = 1;
        HammerSetKey(GlobalKeys.NoWorkbench, true);
        Check(!Success(Run()) && commits == 0, "Hammer confirmation keeps undiscovered material/recipe restriction");
        foreach (Piece.Requirement req in floor.m_resources) if (req.m_resItem) known.Add(req.m_resItem.m_itemData.m_shared.m_name);
        HammerSetKey(GlobalKeys.NoBuildCost, false);
        Check(!Success(Run()) && commits == 0, "normal confirmation still requires paid materials despite known recipe and NoWorkbench");
        HammerSetKey(GlobalKeys.NoBuildCost, true);
        HammerSetKey(GlobalKeys.NoWorkbench, false);
        Check(floor.m_craftingStation && !CraftingStation.HaveBuildStationInRange(floor.m_craftingStation.m_name, player.transform.position),
            "station fixture has no nearby required workbench");
        Check(!Success(Run()) && commits == 0, "Hammer confirmation keeps actual nearby-workbench restriction");
        HammerSetKey(GlobalKeys.NoWorkbench, true);
        int before = FindObjectsByType<Piece>(FindObjectsSortMode.None).Count(p => p.GetComponent<ZNetView>() &&
            p.GetComponent<ZNetView>().IsValid() && p.GetComponent<ZNetView>().GetZDO().GetBool("sighsorry.Homestead.blueprint_piece", false));
        Check(!Success(Run()) && commits == 1, "separate NoWorkbench key allows build to reach commit without material payment");
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
