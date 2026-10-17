using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

public sealed partial class Probe
{
    private static int povVanillaRepairs;
    private static int povMassRepairCalls;
    private static int povMassRepairBodies;
    private static bool PovRepairSentinel() { povVanillaRepairs++; return false; }
    private static void PovMassRepairObserved(bool __runOriginal) { povMassRepairCalls++; if (__runOriginal) povMassRepairBodies++; }
    private static void PovRefreshFailure() { throw new InvalidOperationException("probe refresh failure"); }

    private void CheckPathOfValheiman()
    {
        // Load the supplied original outside plugins: no PoV Awake, networking,
        // tree updater or unrelated gameplay patches run in this focused fixture.
        string path = File.ReadAllText(Path.Combine(Paths.GameRootPath, "pov-path.txt")).Trim();
        Assembly pov = Assembly.LoadFrom(path);
        using (var sha = System.Security.Cryptography.SHA256.Create())
            File.AppendAllText(report, "PoV SHA256 " + BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "") + "\n");
        Type TypeOf(string name) => pov.GetType("PassiveTree." + name, true);
        object Invoke(string type, string name, params object[] args) => AccessTools.Method(TypeOf(type), name).Invoke(null, args);
        Type compat = mod.GetType("Homestead.PathOfValheimanCompat", true);
        var hooks = new Harmony("homestead.probe.pov");
        var failureHooks = new Harmony("homestead.probe.pov.failure");
        var spawned = new List<GameObject>();
        var settings = new Dictionary<ConfigEntryBase, object>();
        Player player = Player.m_localPlayer;
        object oldProgress = null;
        IDictionary progressCache = null;
        IList chestRegistry = null;
        object[] oldChests = null;
        GameObject Spawn(string name, Vector3 position)
        {
            GameObject value = Instantiate(ZNetScene.instance.GetPrefab(name), position, Quaternion.identity);
            spawned.Add(value);
            return value;
        }
        void Set(string type, string field, object value)
        {
            var entry = (ConfigEntryBase)mod.GetType("Homestead." + type).GetField(field, Any).GetValue(null);
            if (!settings.ContainsKey(entry)) settings.Add(entry, entry.BoxedValue);
            entry.BoxedValue = value is string text && entry.SettingType.IsEnum ? Enum.Parse(entry.SettingType, text) : value;
        }
        try
        {
            Check((bool)Call("PathOfValheimanCompat", "CameraUnlocked", player), "without PoV camera keeps existing policy");
            Check((bool)Call("PathOfValheimanCompat", "AreaRepairUnlocked", player), "without PoV area repair keeps existing policy");
            // Install the actual PoV repair postfix first, matching plugin load order.
            MethodInfo repairPostfix = AccessTools.Method(TypeOf("Conveniences+Player_Repair_Mass"), "Postfix");
            MethodInfo repair = AccessTools.Method(typeof(Player), "Repair");
            hooks.Patch(repair, postfix: new HarmonyMethod(repairPostfix));
            hooks.Patch(repairPostfix, postfix: new HarmonyMethod(typeof(Probe), nameof(PovMassRepairObserved)));
            compat.GetField("_logger", Any).SetValue(null, Logger);
            Call("PathOfValheimanCompat", "Bind", pov, hooks);
            Check((bool)compat.GetField("_ownsCamera", Any).GetValue(null) && (bool)compat.GetField("_ownsRepair", Any).GetValue(null), "all original PoV contracts bind under Unity Mono");

            Vector3 center = player ? player.transform.position + Vector3.up * 150 : new Vector3(0, 5000, 0);
            GameObject wall = Spawn("woodwall", center);
            Piece piece = wall.GetComponent<Piece>();
            ZDO zdo = wall.GetComponent<ZNetView>().GetZDO();
            Piece.Requirement[] original = piece.m_resources;
            foreach (float factor in new[] { 1f, 0.75f, 0.5f, 0.1f, 0f })
            {
                zdo.Set("PT_BuildPaidFactor", factor);
                foreach (bool saved in new[] { false, true })
                {
                    zdo.Set("PT_BuildSaved", saved);
                    var expected = (Piece.Requirement[])Invoke("BuildingEconomy", "PaidRequirements", piece);
                    int actual = original.Sum(req => (int)Call("PathOfValheimanCompat", "GetRefundAmount", zdo, req));
                    Check(actual == expected.Sum(req => req.GetAmount(0)), "refund matches original PoV: factor=" + factor + ", saved=" + saved);
                    Check(ReferenceEquals(original, piece.m_resources), "refund leaves shared requirements unchanged");
                }
            }
            // Same production refund collector with a ZDO that has no loaded Piece.
            ZDO unloaded = ZDOMan.instance.CreateNewZDO(center + Vector3.right * 100, "woodwall".GetStableHashCode());
            unloaded.Set("PT_BuildPaidFactor", 0.5f);
            Type refund = mod.GetType("Homestead.ZoneAreaDismantleTool+MaterialRefund", true);
            var refunds = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), refund));
            Call("ZoneAreaDismantleTool", "CollectRefundMaterials", ZNetScene.instance.GetPrefab("woodwall"), unloaded, refunds);
            zdo.Set("PT_BuildPaidFactor", 0.5f); zdo.Set("PT_BuildSaved", false);
            int paid = ((Piece.Requirement[])Invoke("BuildingEconomy", "PaidRequirements", piece)).Where(r => r.m_recover).Sum(r => r.GetAmount(0));
            Check(refunds.Values.Cast<object>().Sum(r => (int)refund.GetProperty("Amount", Any).GetValue(r)) == paid, "unloaded ZDO collector preserves paid refund on this runtime role");
            ZDOMan.instance.DestroyZDO(unloaded);

            chestRegistry = (IList)TypeOf("Registry").GetField("Chests").GetValue(null);
            oldChests = chestRegistry.Cast<object>().ToArray();
            chestRegistry.Clear();
            string[] names = { "piece_chest_wood", "piece_chest_wood_blueprint", "piece_chest_barrel_blueprint_store_price", "piece_chest_blueprint_store_purchase", "piece_chest_blackmetal_blueprint_store_payout" };
            foreach (string name in names)
            {
                Container chest = Spawn(name, center + Vector3.right * 3).GetComponent<Container>();
                chestRegistry.Add(chest);
                string refused = (string)Invoke("Conveniences", "Refused", chest, player ? player.GetPlayerID() : 0L);
                Check((name == "piece_chest_wood") == (refused == null), "material source filter: " + name);
            }
            var nearby = new List<Container>();
            Invoke("Conveniences", "Around", center, 10f, nearby);
            Check(nearby.Count == 1 && Utils.GetPrefabName(nearby[0].gameObject) == "piece_chest_wood", "original nearby-material query excludes all four Homestead chest types");

            if (player)
            {
                Type progressType = TypeOf("Progress");
                progressCache = (IDictionary)progressType.GetField("cache", Any).GetValue(null);
                oldProgress = progressCache[player];
                progressCache.Remove(player);
                Check(!(bool)Call("PathOfValheimanCompat", "CameraUnlocked", player) && !(bool)Call("PathOfValheimanCompat", "AreaRepairUnlocked", player), "unprepared progression stays locked");
                Check(!progressCache.Contains(player), "HUD/gate queries do not load or create progression");
                object progress = Activator.CreateInstance(progressType, Any, null, new object[] { player }, null);
                progressCache[player] = progress;
                object stats = progressType.GetField("Stats").GetValue(progress);
                Type stat = TypeOf("Stat");
                MethodInfo add = stats.GetType().GetMethod("Add", new[] { stat, typeof(float) });
                void Clear() => stats.GetType().GetMethod("Clear").Invoke(stats, null);
                void Add(string name, float value) => add.Invoke(stats, new[] { Enum.Parse(stat, name), (object)value });
                Check(!(bool)Call("PathOfValheimanCompat", "CameraUnlocked", player), "no Hugin passive blocks camera");
                Add("RepairRadiusFlat", 2f);
                Check((bool)Call("PathOfValheimanCompat", "AreaRepairUnlocked", player), "repair radius alone unlocks area repair");
                Set("AreaRepairConfig", "_baseRadius", 3f);
                Set("AreaRepairConfig", "_comfortRadiusScale", 0f);
                Check((float)Call("ZoneAreaRepair", "GetRadius", 0) == 3f, "Homestead radius controls unlocked repair");
                Add("RepairRadiusFlat", 20f);
                Check((float)Call("ZoneAreaRepair", "GetRadius", 0) == 3f, "extra PoV radius does not stack");

                PreparePlacementPlayer();
                player.SetGodMode(true);
                GameObject hammerPrefab = ObjectDB.instance.GetItemPrefab("Hammer");
                ItemDrop.ItemData hammer = hammerPrefab.GetComponent<ItemDrop>().m_itemData.Clone();
                hammer.m_dropPrefab = hammerPrefab;
                player.GetInventory().AddItem(hammer);
                AccessTools.Field(typeof(Character), "m_swimTimer").SetValue(player, 1f);
                player.EquipItem(hammer);
                Set("BuildCameraConfig", "_enabled", "On");
                Set("BuildCameraConfig", "_requireCraftingStation", "Off");
                Set("BuildCameraConfig", "_minimumComfortLevel", 0);
                Set("BuildCameraConfig", "_maxPlaceDistance", 25f);
                Set("BuildCameraConfig", "_maxPlaceDistancePerComfortLevel", 0f);
                Check((bool)Call("ZoneBuildCamera", "ToolIsEquipped", player), "hammer fixture equipped");
                Check(!(bool)Call("ZoneBuildCamera", "EnableBuildMode"), "real camera entry blocked while locked");
                Add("HuginsFlight", 1f);
                Add("BuildRangeFlat", 3f);
                var distance = AccessTools.Field(typeof(Player), "m_maxPlaceDistance");
                float baseline = (float)distance.GetValue(player);
                Check((bool)Call("ZoneBuildCamera", "EnableBuildMode"), "Hugin unlock enters Homestead camera");
                Check((float)distance.GetValue(player) == 25f, "camera uses Homestead configured range");
                Invoke("Effects", "Refresh", player);
                Check((float)TypeOf("Effects").GetField("basePlaceDistance", Any).GetValue(null) == baseline, "first PoV refresh inside camera does not capture camera range as its baseline");
                float ordinary = (float)mod.GetType("Homestead.ZoneBuildCamera").GetField("_originalMaxPlaceDistance", Any).GetValue(null);
                Invoke("HuginFlight", "Start", player);
                Check(!(bool)TypeOf("HuginFlight").GetProperty("Active").GetValue(null), "original Hugin Start is suppressed");
                Add("BuildRangeFlat", 4f);
                Invoke("Effects", "Refresh", player);
                Check((float)distance.GetValue(player) == 25f, "live PoV range refresh preserves active camera range");
                float refreshed = (float)mod.GetType("Homestead.ZoneBuildCamera").GetField("_originalMaxPlaceDistance", Any).GetValue(null);
                Check(refreshed > ordinary, "live PoV change updates ordinary range for camera exit");
                MethodInfo refresh = AccessTools.Method(TypeOf("Effects"), "Refresh");
                failureHooks.Patch(refresh, prefix: new HarmonyMethod(typeof(Probe), nameof(PovRefreshFailure)) { priority = Priority.Last });
                bool propagated = false;
                try { Invoke("Effects", "Refresh", player); }
                catch (TargetInvocationException ex) { propagated = ex.InnerException is InvalidOperationException; }
                finally { failureHooks.UnpatchSelf(); }
                Check(propagated && (float)distance.GetValue(player) == 25f, "Refresh exception propagates while camera range is restored");
                Clear();
                Invoke("Effects", "Refresh", player);
                float withoutNodes = (float)mod.GetType("Homestead.ZoneBuildCamera").GetField("_originalMaxPlaceDistance", Any).GetValue(null);
                Call("ZoneBuildCamera", "Update");
                Check(!(bool)Call("ZoneBuildCamera", "InBuildMode") && (float)distance.GetValue(player) == withoutNodes, "passive refund exits camera and restores latest ordinary range");
                Check(!(bool)Call("PathOfValheimanCompat", "AreaRepairUnlocked", player), "repair passive refund locks area repair");
                Add("HuginsFlight", 1f);
                Set("BuildCameraConfig", "_enabled", "Off");
                Check(!(bool)Call("ZoneBuildCamera", "EnableBuildMode"), "Homestead camera Off still blocks an unlocked passive");
                Invoke("HuginFlight", "Start", player);
                Check(!(bool)TypeOf("HuginFlight").GetProperty("Active").GetValue(null), "camera Off does not reactivate the PoV implementation");
                Check(Localization.instance.Localize("$hs_build_camera_need_pov") != "$hs_build_camera_need_pov" &&
                      Localization.instance.Localize("$hs_area_repair_need_pov") != "$hs_area_repair_need_pov", "unlock HUD messages registered");

                // Count the original-repair branch without executing a ray-based
                // repair fixture. The real PoV postfix must stay suppressed.
                Set("AreaRepairConfig", "_baseRadius", 0f);
                Add("RepairRadiusFlat", 20f);
                MethodInfo sentinel = AccessTools.Method(typeof(Probe), nameof(PovRepairSentinel));
                hooks.Patch(repair, prefix: new HarmonyMethod(sentinel) { priority = Priority.Last });
                povVanillaRepairs = 0;
                povMassRepairCalls = povMassRepairBodies = 0;
                repair.Invoke(player, new object[] { hammer, piece });
                Check(povVanillaRepairs == 1, "disabled area repair leaves the vanilla repair branch available");
                Check(povMassRepairCalls == 1 && povMassRepairBodies == 0, "actual PoV repair postfix is called but its mass-repair body is skipped");
                Check(!(bool)Call("PathOfValheimanCompat", "AllowPoVAreaRepair"), "PoV mass repair remains suppressed when Homestead area repair is off");
            }
            else Check(!Player.m_localPlayer, "refund and container checks ran without a local player on dedicated runtime");
        }
        finally
        {
            Call("ZoneBuildCamera", "DisableBuildMode");
            Call("PathOfValheimanCompat", "Shutdown");
            hooks.UnpatchSelf();
            failureHooks.UnpatchSelf();
            foreach (var setting in settings) setting.Key.BoxedValue = setting.Value;
            if (progressCache != null) { progressCache.Remove(player); if (oldProgress != null) progressCache[player] = oldProgress; }
            if (chestRegistry != null) { chestRegistry.Clear(); foreach (object chest in oldChests) chestRegistry.Add(chest); }
            foreach (GameObject obj in spawned) if (obj) ZNetScene.instance.Destroy(obj);
        }
    }
}
