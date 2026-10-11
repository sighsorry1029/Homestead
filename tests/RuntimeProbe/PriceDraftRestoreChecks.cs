using System;
using System.Reflection;
using UnityEngine;

// Runs only through Probe's isolated world, against the original game runtime.
internal static class PriceDraftRestoreChecks
{
    public static void Run(Assembly mod, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        Type registry = mod.GetType("Homestead.ZoneBlueprintStoreChestRegistry", true);
        Type index = mod.GetType("Homestead.ZoneBlueprintChestZdoRegistry", true);
        Type chestType = mod.GetType("Homestead.ZoneBlueprintStoreChest", true);
        MethodInfo resolve = registry.GetMethod("TryResolvePriceDraftRestore", flags, null,
            new[] { typeof(string), typeof(string), typeof(string).MakeByRefType(), typeof(string).MakeByRefType() }, null);
        GameObject prefab = ZNetScene.instance.GetPrefab("piece_chest_barrel_blueprint_store_price");

        foreach (bool loaded in new[] { true, false })
        {
            string context = loaded ? "loaded" : "unloaded";
            string id = "Restore-" + context;
            string file = context + ".blueprint";
            GameObject instance = loaded ? UnityEngine.Object.Instantiate(prefab, Vector3.up * 5000f, Quaternion.identity) : null;
            Component chest = instance ? instance.GetComponent(chestType) : null;
            ZDO zdo = instance ? instance.GetComponent<ZNetView>().GetZDO() :
                ZDOMan.instance.CreateNewZDO(Vector3.up * 5000f, prefab.name.GetStableHashCode());
            // CreateNewZDO uses the hash for portal registration; ZNetView normally sets the stored prefab.
            if (!loaded) zdo.SetPrefab(prefab.name.GetStableHashCode());

            void Reset()
            {
                zdo.Set("hs_store_mode", "price");
                zdo.Set("hs_store_listing", id);
                zdo.Set("hs_store_blueprint_name", "Draft name");
                zdo.Set("hs_store_blueprint_file", "drafts/" + file);
                zdo.Set("hs_store_confirmed", false);
                zdo.Set("hs_store_draft_owned_by_chest", true);
                zdo.Set("hs_store_seller", 123456789L);
                index.GetMethod("Refresh", flags).Invoke(null, new object[] { zdo });
                if (chest) registry.GetMethod("Refresh", flags).Invoke(null, new object[] { chest });
            }

            void Case(string label, string requestedId, string requestedFile, bool expected,
                Action mutate = null, string expectedName = "", string expectedFile = "")
            {
                Reset();
                mutate?.Invoke();
                object[] args = { requestedId, requestedFile, "sentinel", "sentinel" };
                bool result = (bool)resolve.Invoke(null, args);
                check(result == expected && (string)args[2] == expectedName && (string)args[3] == expectedFile,
                    "price draft restore " + context + ": " + label);
            }

            try
            {
                check(zdo.IsValid() && zdo.GetPrefab() == prefab.name.GetStableHashCode() &&
                      (ZNetScene.instance.FindInstance(zdo) != null) == loaded,
                    "price draft fixture has " + context + " lifecycle");
                Case("ID alone", id, "", true, expectedName: "Draft name", expectedFile: file);
                Case("filename alone ignores case and directory", "other", "another/" + file.ToUpperInvariant(), true,
                    expectedName: "Draft name", expectedFile: file);
                Case("matching ID allows different filename", id, "different.blueprint", true,
                    expectedName: "Draft name", expectedFile: file);
                Case("ID case remains significant", id.ToUpperInvariant(), "", false);
                Case("ID is not trimmed", " " + id + " ", "", false);
                Case("null request", null, null, false);
                Case("empty request", "", "", false);
                Case("whitespace request", " ", " ", false);
                Case("neither identifier matches", "other", "other.blueprint", false);
                Case("confirmed draft", id, "", false, () => zdo.Set("hs_store_confirmed", true));
                Case("draft not owned by chest", id, "", false, () => zdo.Set("hs_store_draft_owned_by_chest", false));
                foreach (string mode in new[] { "buy", "payout", "PRICE", "" })
                    Case("mode " + mode, id, "", false, () => zdo.Set("hs_store_mode", mode));
                Case("empty file preserves failure outputs", id, "", false,
                    () => zdo.Set("hs_store_blueprint_file", ""), "Draft name", "");
                Case("blank file preserves failure outputs", id, "", false,
                    () => zdo.Set("hs_store_blueprint_file", " "), "Draft name", " ");
                Case("empty name remains allowed", id, "", true,
                    () => zdo.Set("hs_store_blueprint_name", ""), "", file);
            }
            finally
            {
                // Prevent a deferred Unity destroy from participating in the next fixture.
                zdo.Set("hs_store_confirmed", true);
                if (chest) registry.GetMethod("Unregister", flags).Invoke(null, new object[] { chest });
                if (instance) instance.GetComponent<ZNetView>().Destroy();
                else ZDOMan.instance.DestroyZDO(zdo);
            }
        }
    }
}
