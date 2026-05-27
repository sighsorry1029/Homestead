using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintCommands
{
    private const int BlueprintPlanBatchSize = 250;
    private const float PlanGhostCleanupStartupDelaySeconds = 600f;
    private const float PlanGhostCleanupRegistryRetrySeconds = 60f;
    private const float PlanGhostCleanupIntervalSeconds = 6f * 60f * 60f;
    private const string BlueprintPieceMarkerKey = "sighsorry.Homestead.blueprint_piece";
    private static readonly int BlueprintPlacedHash = StringExtensionMethods.GetStableHashCode(BlueprintPieceMarkerKey);
    private static readonly Dictionary<string, bool> BuildRecipeCache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> MissingPrefabWarnings = new(StringComparer.OrdinalIgnoreCase);

    private static ManualLogSource _logger = null!;
    private static bool _initialized;
    private static int _buildRecipeCacheObjectDbCount = -1;
    private static float _nextPlanGhostCleanupAt;

    public static void Initialize(ManualLogSource logger)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _logger = logger;
        ResetForWorldSession();
        ZoneBlueprintPlanRpc.Initialize(logger);
    }

    public static void Update()
    {
        CleanupPlanGhostFilesIfDue();
    }

    public static void ResetForWorldSession()
    {
        _nextPlanGhostCleanupAt = Time.realtimeSinceStartup + PlanGhostCleanupStartupDelaySeconds;
        MissingPrefabWarnings.Clear();
    }

    internal static HomesteadCommandResult SaveSelectedBlueprint(string name, Player player)
    {
        EnsureWorldReady();
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return HomesteadCommandResult.Fail(HomesteadLocalization.Text("hs_blueprint_name_required"));
        }

        if (!ZoneBlueprintSaveTool.TryGetSelectedBlueprint(name, player, out ZoneBlueprintFile blueprint, out string selectionError))
        {
            return HomesteadCommandResult.Fail(selectionError);
        }

        if (blueprint.Entries.Count == 0)
        {
            return HomesteadCommandResult.Fail(HomesteadLocalization.Text("hs_blueprint_no_selected_wearnttear"));
        }

        string path = SaveBlueprint(name, blueprint);
        return HomesteadCommandResult.Ok(
            HomesteadLocalization.Format("hs_blueprint_saved_to_path", name, blueprint.Entries.Count, blueprint.TerrainContacts.Count, path));
    }

    internal static HomesteadCommandResult PlaceBlueprintPlanAt(string name, Player player, Vector3 anchor, Quaternion anchorRotation)
    {
        return PlaceBlueprintPlanAt(name, player, anchor, anchorRotation, anchorRotation);
    }

    internal static HomesteadCommandResult PlaceBlueprintPlanAt(string name, Player player, Vector3 anchor, Quaternion anchorRotation, Quaternion chestRotation)
    {
        ZoneBlueprintFile blueprint = LoadBlueprint(name);
        Vector3 chestPosition = GetPlanChestPosition(blueprint, anchor, anchorRotation, chestRotation);
        if (ZNet.instance != null && !ZNet.instance.IsServer())
        {
            ZoneBlueprintPlanRpc.RequestPlace(name, blueprint, anchor, anchorRotation, chestPosition, chestRotation);
            return HomesteadCommandResult.Ok(HomesteadLocalization.Format("hs_blueprint_plan_request_sent", name));
        }

        BlueprintLoadPlan plan = CreateLoadPlan(blueprint, anchor, anchorRotation);
        if (plan.Entries.Count == 0)
        {
            return HomesteadCommandResult.Fail(HomesteadLocalization.Format("hs_blueprint_no_valid_entries", name));
        }

        return ZoneBlueprintPlanChestPrefab.PlacePlanChest(name, player, anchor, anchorRotation, chestPosition, chestRotation);
    }

    internal static HomesteadCommandResult FinalizeBlueprintPlan(
        string name,
        Player player,
        Vector3 anchor,
        Quaternion anchorRotation,
        IReadOnlyDictionary<string, int> depositedMaterials)
    {
        ZoneBlueprintFile blueprint = LoadBlueprint(name);
        BlueprintLoadPlan plan = CreateLoadPlan(blueprint, anchor, anchorRotation);
        if (plan.Entries.Count == 0)
        {
            return HomesteadCommandResult.Fail(HomesteadLocalization.Format("hs_blueprint_no_valid_entries", name));
        }

        if (!ZoneLimitCompat.CanAddWearNTears(plan.Positions, out string limitReason))
        {
            return HomesteadCommandResult.Fail(limitReason);
        }

        bool noCost = player.NoCostCheat();
        if (!noCost)
        {
            string accessReason = ValidateBuildAccessWithoutInventory(player, plan.Entries);
            if (!string.IsNullOrEmpty(accessReason))
            {
                return HomesteadCommandResult.Fail(accessReason);
            }

            foreach (ZoneBlueprintRequirement requirement in CollectRequirements(plan))
            {
                depositedMaterials.TryGetValue(requirement.ItemName, out int deposited);
                if (deposited < requirement.Amount)
                {
                    return HomesteadCommandResult.Fail(HomesteadLocalization.Format(
                        "hs_blueprint_missing_deposited",
                        HomesteadLocalization.MaybeLocalize(requirement.DisplayName),
                        deposited,
                        requirement.Amount));
                }
            }
        }

        bool terrainApplied = false;
        if (BlueprintConfig.ShouldApplyTerrainSupport(player) && plan.SupportContacts.Count > 0)
        {
            terrainApplied = BlueprintTerrainApplier.ApplySupportContacts(plan.SupportContacts);
        }

        int created = SpawnPlan(plan, player);
        return HomesteadCommandResult.Ok(
            HomesteadLocalization.Format(
                "hs_blueprint_confirmed",
                name,
                created,
                terrainApplied ? HomesteadLocalization.Text("hs_common_yes") : HomesteadLocalization.Text("hs_common_no")));
    }

    internal static IEnumerator FinalizeBlueprintPlanAsync(
        string name,
        Player player,
        Vector3 anchor,
        Quaternion anchorRotation,
        Dictionary<string, int> depositedMaterials,
        Action<HomesteadCommandResult> onComplete)
    {
        ZoneBlueprintFile blueprint;
        try
        {
            blueprint = LoadPlanBlueprint(name);
        }
        catch (Exception ex)
        {
            onComplete(HomesteadCommandResult.Fail(ex.Message));
            yield break;
        }

        BlueprintLoadPlan? plan = null;
        string planError = "";
        yield return CreateLoadPlanAsync(blueprint, anchor, anchorRotation, (value, error) =>
        {
            plan = value;
            planError = error;
        });

        if (plan == null)
        {
            onComplete(HomesteadCommandResult.Fail(string.IsNullOrWhiteSpace(planError) ? HomesteadLocalization.Format("hs_blueprint_load_failed_plain", name) : planError));
            yield break;
        }

        if (plan.Entries.Count == 0)
        {
            onComplete(HomesteadCommandResult.Fail(HomesteadLocalization.Format("hs_blueprint_no_valid_entries", name)));
            yield break;
        }

        if (!ZoneLimitCompat.CanAddWearNTears(plan.Positions, out string limitReason))
        {
            onComplete(HomesteadCommandResult.Fail(limitReason));
            yield break;
        }

        bool noCost = player.NoCostCheat();
        if (!noCost)
        {
            string accessReason = "";
            yield return ValidateBuildAccessWithoutInventoryAsync(player, plan.Entries, value => accessReason = value);
            if (!string.IsNullOrEmpty(accessReason))
            {
                onComplete(HomesteadCommandResult.Fail(accessReason));
                yield break;
            }

            List<ZoneBlueprintRequirement> requirements = [];
            yield return CollectRequirementsAsync(plan.Entries, value => requirements = value);
            foreach (ZoneBlueprintRequirement requirement in requirements)
            {
                depositedMaterials.TryGetValue(requirement.ItemName, out int deposited);
                if (deposited < requirement.Amount)
                {
                    onComplete(HomesteadCommandResult.Fail(HomesteadLocalization.Format(
                        "hs_blueprint_missing_deposited",
                        HomesteadLocalization.MaybeLocalize(requirement.DisplayName),
                        deposited,
                        requirement.Amount)));
                    yield break;
                }
            }
        }

        bool terrainApplied = false;
        if (BlueprintConfig.ShouldApplyTerrainSupport(player) && plan.SupportContacts.Count > 0)
        {
            yield return BlueprintTerrainApplier.ApplySupportContactsAsync(plan.SupportContacts, result => terrainApplied = result);
        }

        int created = 0;
        yield return SpawnPlanAsync(plan, player, value => created = value);
        onComplete(HomesteadCommandResult.Ok(
            HomesteadLocalization.Format(
                "hs_blueprint_confirmed",
                name,
                created,
                terrainApplied ? HomesteadLocalization.Text("hs_common_yes") : HomesteadLocalization.Text("hs_common_no"))));
    }

    internal static List<string> GetBlueprintNames()
    {
        string directory = GetWorldBlueprintDirectory();
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.GetFiles(directory, "*" + ZoneBlueprintFileFormat.BlueprintExtension)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool TryLoadBlueprint(string name, out ZoneBlueprintFile blueprint)
    {
        try
        {
            blueprint = LoadBlueprint(name);
            return true;
        }
        catch
        {
            blueprint = null!;
            return false;
        }
    }

    internal static string SerializeBlueprintForStore(string name)
    {
        ZoneBlueprintFile blueprint = LoadBlueprint(name);
        return ZoneBlueprintFileFormat.Serialize(blueprint);
    }

    internal static string SaveBlueprintFromStore(string preferredName, ZoneBlueprintFile blueprint)
    {
        string name = GetUniqueBlueprintName(preferredName);
        blueprint.Name = name;
        string path = SaveBlueprint(name, blueprint);
        return path;
    }

    internal static List<ZDO> FindBlueprintWearNTearZdos(Player player, ZoneAreaSelection selection, BlueprintAreaSaveCreatorMode creatorMode)
    {
        long playerId = player.GetPlayerID();
        List<ZDO> zdos = [];
        List<ZDO> nearby = [];

        ZoneAreaTargetOverlay.CollectNearbyZdos(selection, nearby);
        foreach (ZDO zdo in nearby)
        {
            if (IsHomesteadBlueprintChest(zdo))
            {
                continue;
            }

            if (!TryReadSavableWearNTear(zdo, out _))
            {
                continue;
            }

            if (!selection.Contains(zdo.GetPosition()))
            {
                continue;
            }

            long creator = zdo.GetLong(ZDOVars.s_creator, 0L);
            if (!IsAreaSaveCreatorAllowed(playerId, creator, creatorMode))
            {
                continue;
            }

            zdos.Add(zdo);
        }

        return zdos;
    }

    internal static bool IsAreaSaveCreatorAllowed(long playerId, long creator, BlueprintAreaSaveCreatorMode creatorMode)
    {
        if (creator == playerId)
        {
            return true;
        }

        return creatorMode switch
        {
            BlueprintAreaSaveCreatorMode.AllCreators => true,
            BlueprintAreaSaveCreatorMode.OwnedAndCreatorless => creator == 0L,
            _ => false
        };
    }

    internal static bool IsHomesteadBlueprintChest(ZDO zdo)
    {
        if (zdo == null || !zdo.IsValid())
        {
            return false;
        }

        int prefab = zdo.GetPrefab();
        return prefab == ZoneBlueprintPlanChestPrefab.PrefabHash ||
               ZoneBlueprintStoreChestPrefab.IsStorePrefab(prefab);
    }

    internal static bool IsHomesteadBlueprintChestPrefab(GameObject? prefab)
    {
        if (!prefab)
        {
            return false;
        }

        string prefabName = Utils.GetPrefabName(prefab);
        return string.Equals(prefabName, ZoneBlueprintPlanChestPrefab.PrefabName, StringComparison.OrdinalIgnoreCase) ||
               ZoneBlueprintStoreChestPrefab.IsStorePrefabName(prefabName);
    }

    internal static ZoneBlueprintFile CaptureBlueprintFromZdos(
        string name,
        Player player,
        Vector3 anchor,
        Quaternion anchorRotation,
        IEnumerable<ZDO> sourceZdos,
        float radius)
    {
        Quaternion inverseAnchorRotation = Quaternion.Inverse(anchorRotation);
        List<ZoneBlueprintEntry> entries = [];
        List<TerrainContactSource> terrainContactSources = [];

        foreach (ZDO zdo in sourceZdos.ToList())
        {
            if (!TryReadSavableWearNTear(zdo, out GameObject prefab))
            {
                continue;
            }

            Vector3 position = zdo.GetPosition();
            Quaternion rotation = zdo.GetRotation();
            Vector3 scale = ReadScale(zdo, prefab);

            Vector3 localPosition = inverseAnchorRotation * (position - anchor);
            Quaternion localRotation = inverseAnchorRotation * rotation;
            entries.Add(new ZoneBlueprintEntry
            {
                Prefab = Utils.GetPrefabName(prefab),
                LocalPos = ToArray(localPosition),
                LocalRot = ToArray(localRotation),
                Scale = ToArray(scale),
                Text = ReadBlueprintText(zdo, prefab)
            });

            terrainContactSources.Add(new TerrainContactSource(prefab, position, rotation, scale));
        }

        return new ZoneBlueprintFile
        {
            Name = name,
            Creator = player.GetPlayerName(),
            World = GetWorldName(),
            SavedAt = HomesteadTimestamp.Now(),
            Radius = radius,
            Entries = entries
                .OrderBy(entry => entry.Prefab, StringComparer.Ordinal)
                .ThenBy(entry => entry.LocalPos[0])
                .ThenBy(entry => entry.LocalPos[2])
                .ThenBy(entry => entry.LocalPos[1])
                .ToList(),
            TerrainContacts = BlueprintTerrainApplier.CaptureContacts(anchor, inverseAnchorRotation, terrainContactSources)
        };
    }

    internal static ZoneBlueprintFile LoadBlueprintForPlan(string name)
    {
        return LoadPlanBlueprint(name);
    }

    internal static BlueprintLoadPlan CreateLoadPlanForBlueprint(string name, Vector3 anchor, Quaternion anchorRotation)
    {
        return CreateLoadPlan(LoadPlanBlueprint(name), anchor, anchorRotation);
    }

    internal static BlueprintLoadPlan CreateLoadPlanForBlueprint(ZoneBlueprintFile blueprint, Vector3 anchor, Quaternion anchorRotation)
    {
        return CreateLoadPlan(blueprint, anchor, anchorRotation);
    }

    internal static HomesteadCommandResult SaveUploadedBlueprintForPlan(
        string preferredName,
        ZoneBlueprintFile blueprint,
        long playerId,
        out string savedName)
    {
        savedName = "";
        string validationError = ValidateBlueprintFile(blueprint);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            return HomesteadCommandResult.Fail(validationError);
        }

        string requestedName = SanitizePathSegment(string.IsNullOrWhiteSpace(preferredName) ? blueprint.Name : preferredName);
        string requestedPath = GetPlanGhostBlueprintPath(requestedName);
        Directory.CreateDirectory(Path.GetDirectoryName(requestedPath)!);
        blueprint.World = GetWorldName();

        if (File.Exists(requestedPath))
        {
            try
            {
                ZoneBlueprintFile existing = ZoneBlueprintFileFormat.ReadFile(requestedPath);
                string existingText = ZoneBlueprintFileFormat.Serialize(existing);
                string incomingText = ZoneBlueprintFileFormat.Serialize(CloneForName(blueprint, requestedName));
                if (string.Equals(existingText, incomingText, StringComparison.Ordinal))
                {
                    savedName = requestedName;
                    return HomesteadCommandResult.Ok(HomesteadLocalization.Format("hs_blueprint_server_already_has", savedName));
                }
            }
            catch
            {
                // If the existing file is unreadable, avoid overwriting it and create a unique upload name.
            }

            requestedName = GetUniquePlanGhostBlueprintName($"{requestedName}_p{Math.Abs(playerId)}");
        }

        blueprint.Name = requestedName;
        string path = GetPlanGhostBlueprintPath(requestedName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ZoneBlueprintFileFormat.WriteFile(path, blueprint);
        savedName = requestedName;
        return HomesteadCommandResult.Ok(HomesteadLocalization.Format("hs_blueprint_uploaded_to_server", savedName));
    }

    internal static string SerializePreviewBlueprintForPlan(string name)
    {
        ZoneBlueprintFile blueprint = LoadPlanBlueprint(name);
        if (!ZoneBlueprintNetworkPayload.TryCreatePreviewText(blueprint, out string blueprintText, out string reason))
        {
            throw new InvalidOperationException(reason);
        }

        return blueprintText;
    }

    internal static void EnsureLocalPlanBlueprintCopy(string name, string blueprintText)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(blueprintText))
        {
            return;
        }

        ZoneBlueprintFile blueprint = ZoneBlueprintFileFormat.Deserialize(blueprintText, name);
        blueprint.Name = name;
        string path = GetPlanGhostBlueprintPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ZoneBlueprintFileFormat.WriteFile(path, blueprint);
    }

    internal static List<ZoneBlueprintRequirement> CollectRequirements(BlueprintLoadPlan plan)
    {
        return CollectRequirements(plan.Entries);
    }

    internal static List<ZoneBlueprintCraftingStationRequirement> CollectCraftingStations(BlueprintLoadPlan plan)
    {
        Dictionary<string, ZoneBlueprintCraftingStationRequirement> stations = new(StringComparer.Ordinal);
        foreach (BlueprintLoadEntry entry in plan.Entries)
        {
            Piece piece = entry.Prefab.GetComponent<Piece>();
            CraftingStation? station = piece != null ? piece.m_craftingStation : null;
            if (station == null)
            {
                continue;
            }

            string stationName = station.m_name;
            if (string.IsNullOrWhiteSpace(stationName) || stations.ContainsKey(stationName))
            {
                continue;
            }

            stations[stationName] = new ZoneBlueprintCraftingStationRequirement
            {
                StationName = stationName,
                PrefabName = Utils.GetPrefabName(station.gameObject),
                DisplayName = stationName
            };
        }

        return stations.Values.OrderBy(station => station.StationName, StringComparer.Ordinal).ToList();
    }

    internal static Vector3 GetPlanChestPosition(ZoneBlueprintFile blueprint, Vector3 anchor, Quaternion anchorRotation)
    {
        return GetPlanChestPosition(blueprint, anchor, anchorRotation, anchorRotation);
    }

    internal static Vector3 GetPlanChestPosition(ZoneBlueprintFile blueprint, Vector3 anchor, Quaternion anchorRotation, Quaternion chestRotation)
    {
        if (blueprint.Entries.Count == 0)
        {
            return anchor;
        }

        Quaternion inverseChestRotation = Quaternion.Inverse(chestRotation);
        List<Vector3> chestLocalPositions = blueprint.Entries
            .Where(IsLoadableBlueprintEntry)
            .Select(entry => inverseChestRotation * (anchorRotation * FromVector(entry.LocalPos)))
            .ToList();
        if (chestLocalPositions.Count == 0)
        {
            return anchor;
        }

        float minX = chestLocalPositions.Min(position => position.x);
        float maxX = chestLocalPositions.Max(position => position.x);
        float minZ = chestLocalPositions.Min(position => position.z);
        Vector3 local = new((minX + maxX) * 0.5f, 0f, minZ - 2.5f);
        Vector3 world = anchor + chestRotation * local;
        world.y = SampleGroundY(world.x, world.z, anchor.y);
        return world;
    }

    private static BlueprintLoadPlan CreateLoadPlan(ZoneBlueprintFile blueprint, Vector3 anchor, Quaternion anchorRotation)
    {
        List<BlueprintLoadEntry> entries = [];
        List<Vector3> positions = [];

        foreach (ZoneBlueprintEntry entry in blueprint.Entries)
        {
            if (!TryCreateLoadEntry(blueprint, entry, anchor, anchorRotation, out BlueprintLoadEntry? loadEntry, out string error))
            {
                throw new InvalidOperationException(error);
            }

            if (loadEntry == null)
            {
                continue;
            }

            entries.Add(loadEntry);
            positions.Add(loadEntry.Position);
        }

        List<Vector3> supportContacts = blueprint.TerrainContacts
            .Select(contact => ToWorldTerrainContact(contact, anchor, anchorRotation))
            .ToList();

        return new BlueprintLoadPlan(entries, positions, supportContacts);
    }

    private static IEnumerator CreateLoadPlanAsync(ZoneBlueprintFile blueprint, Vector3 anchor, Quaternion anchorRotation, Action<BlueprintLoadPlan?, string> onComplete)
    {
        List<BlueprintLoadEntry> entries = [];
        List<Vector3> positions = [];
        int processedSinceYield = 0;

        foreach (ZoneBlueprintEntry entry in blueprint.Entries)
        {
            if (!TryCreateLoadEntry(blueprint, entry, anchor, anchorRotation, out BlueprintLoadEntry? loadEntry, out string error))
            {
                onComplete(null, error);
                yield break;
            }

            if (loadEntry != null)
            {
                entries.Add(loadEntry);
                positions.Add(loadEntry.Position);
            }

            processedSinceYield++;
            if (processedSinceYield >= BlueprintPlanBatchSize)
            {
                processedSinceYield = 0;
                yield return null;
            }
        }

        List<Vector3> supportContacts = new(blueprint.TerrainContacts.Count);
        foreach (ZoneBlueprintTerrainContact contact in blueprint.TerrainContacts)
        {
            supportContacts.Add(ToWorldTerrainContact(contact, anchor, anchorRotation));
            processedSinceYield++;
            if (processedSinceYield >= BlueprintPlanBatchSize)
            {
                processedSinceYield = 0;
                yield return null;
            }
        }

        onComplete(new BlueprintLoadPlan(entries, positions, supportContacts), "");
    }

    private static bool TryCreateLoadEntry(
        ZoneBlueprintFile blueprint,
        ZoneBlueprintEntry entry,
        Vector3 anchor,
        Quaternion anchorRotation,
        out BlueprintLoadEntry? loadEntry,
        out string error)
    {
        loadEntry = null;
        error = "";
        GameObject prefab = ZNetScene.instance.GetPrefab(entry.Prefab);
        if (!prefab)
        {
            LogMissingPrefabOnce(blueprint.Name, entry.Prefab);
            return true;
        }

        if (prefab.GetComponent<WearNTear>() == null || !HasBuildRecipe(prefab))
        {
            return true;
        }

        if (entry.LocalPos.Length < 3 || entry.LocalRot.Length < 4 || entry.Scale.Length < 3)
        {
            error = $"Blueprint '{blueprint.Name}' contains an invalid transform for '{entry.Prefab}'.";
            return false;
        }

        Vector3 position = anchor + anchorRotation * FromVector(entry.LocalPos);
        Quaternion rotation = anchorRotation * FromQuaternion(entry.LocalRot);
        Vector3 scale = FromVector(entry.Scale);
        loadEntry = new BlueprintLoadEntry(entry, prefab, position, rotation, scale);
        return true;
    }

    internal static bool IsLoadableBlueprintEntry(ZoneBlueprintEntry entry)
    {
        return IsLoadableBlueprintEntry(entry, out _);
    }

    internal static bool IsLoadableBlueprintEntry(ZoneBlueprintEntry entry, out bool missingPrefab)
    {
        missingPrefab = false;
        if (entry.LocalPos.Length < 3 || entry.LocalRot.Length < 4 || entry.Scale.Length < 3)
        {
            return false;
        }

        GameObject? prefab = ZNetScene.instance?.GetPrefab(entry.Prefab);
        if (!prefab)
        {
            missingPrefab = true;
            return false;
        }

        return prefab && prefab.GetComponent<WearNTear>() != null && HasBuildRecipe(prefab);
    }

    private static void LogMissingPrefabOnce(string blueprintName, string prefabName)
    {
        string key = $"{blueprintName}\n{prefabName}";
        if (MissingPrefabWarnings.Add(key))
        {
            _logger.LogWarning($"Skipping missing prefab '{prefabName}' while loading blueprint '{blueprintName}'.");
        }
    }

    private static Vector3 ToWorldTerrainContact(ZoneBlueprintTerrainContact contact, Vector3 anchor, Quaternion anchorRotation)
    {
        return anchor + anchorRotation * new Vector3(contact.LocalX, contact.LocalY, contact.LocalZ);
    }

    private static int SpawnPlan(BlueprintLoadPlan plan, Player player)
    {
        long playerId = player.GetPlayerID();
        string playerName = player.GetPlayerName();
        int created = 0;

        foreach (BlueprintLoadEntry item in plan.Entries)
        {
            created += TrySpawnPlanEntry(item, playerId, playerName) ? 1 : 0;
        }

        return created;
    }

    private static IEnumerator SpawnPlanAsync(BlueprintLoadPlan plan, Player player, Action<int> onComplete)
    {
        long playerId = player.GetPlayerID();
        string playerName = player.GetPlayerName();
        int created = 0;
        int processedSinceYield = 0;

        foreach (BlueprintLoadEntry item in plan.Entries)
        {
            created += TrySpawnPlanEntry(item, playerId, playerName) ? 1 : 0;

            processedSinceYield++;
            if (processedSinceYield >= BlueprintPlanBatchSize)
            {
                processedSinceYield = 0;
                yield return null;
            }
        }

        onComplete(created);
    }

    private static bool TrySpawnPlanEntry(BlueprintLoadEntry item, long playerId, string playerName)
    {
        ZDO? zdo = InitBlueprintZdo(item.Prefab, item.Position, item.Rotation, item.Scale);
        if (zdo == null)
        {
            return false;
        }

        ApplyBlueprintText(item, zdo);
        zdo.Set(ZDOVars.s_creator, playerId);
        zdo.Set(ZDOVars.s_creatorName, playerName);
        zdo.Set(BlueprintPlacedHash, true);
        ZNetScene.instance.CreateObject(zdo);
        return true;
    }

    private static ZDO? InitBlueprintZdo(GameObject prefab, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        if (ZDOMan.instance == null || !prefab)
        {
            return null;
        }

        int prefabHash = StringExtensionMethods.GetStableHashCode(prefab.name);
        ZDO zdo = ZDOMan.instance.CreateNewZDO(position, prefabHash);
        zdo.SetPrefab(prefabHash);
        zdo.SetRotation(rotation);

        ZNetView prefabView = prefab.GetComponent<ZNetView>();
        if (prefabView != null)
        {
            zdo.Persistent = prefabView.m_persistent;
            zdo.Distant = prefabView.m_distant;
            zdo.Type = prefabView.m_type;
        }

        zdo.Set(ZDOVars.s_scaleHash, scale);
        return zdo;
    }

    private static void ApplyBlueprintText(BlueprintLoadEntry item, ZDO zdo)
    {
        if (string.IsNullOrEmpty(item.Entry.Text) || item.Prefab.GetComponent<TextReceiver>() == null)
        {
            return;
        }

        zdo.Set(ZDOVars.s_text, item.Entry.Text);
    }

    private static List<ZoneBlueprintRequirement> CollectRequirements(IEnumerable<BlueprintLoadEntry> entries)
    {
        Dictionary<string, ZoneBlueprintRequirement> requirements = [];
        foreach (BlueprintLoadEntry entry in entries)
        {
            AccumulateRequirements(requirements, entry);
        }

        return requirements.Values.OrderBy(requirement => requirement.ItemName, StringComparer.Ordinal).ToList();
    }

    private static IEnumerator CollectRequirementsAsync(IEnumerable<BlueprintLoadEntry> entries, Action<List<ZoneBlueprintRequirement>> onComplete)
    {
        Dictionary<string, ZoneBlueprintRequirement> requirements = [];
        int processedSinceYield = 0;

        foreach (BlueprintLoadEntry entry in entries)
        {
            AccumulateRequirements(requirements, entry);

            processedSinceYield++;
            if (processedSinceYield >= BlueprintPlanBatchSize)
            {
                processedSinceYield = 0;
                yield return null;
            }
        }

        onComplete(requirements.Values.OrderBy(requirement => requirement.ItemName, StringComparer.Ordinal).ToList());
    }

    private static void AccumulateRequirements(Dictionary<string, ZoneBlueprintRequirement> requirements, BlueprintLoadEntry entry)
    {
        Piece piece = entry.Prefab.GetComponent<Piece>();
        if (piece == null)
        {
            return;
        }

        foreach (Piece.Requirement requirement in piece.m_resources)
        {
            if (!requirement.m_resItem || requirement.m_amount <= 0)
            {
                continue;
            }

            string itemName = requirement.m_resItem.m_itemData.m_shared.m_name;
            if (!requirements.TryGetValue(itemName, out ZoneBlueprintRequirement aggregate))
            {
                aggregate = new ZoneBlueprintRequirement
                {
                    ItemName = itemName,
                    PrefabName = Utils.GetPrefabName(requirement.m_resItem.gameObject),
                    DisplayName = requirement.m_resItem.m_itemData.m_shared.m_name
                };
                requirements[itemName] = aggregate;
            }

            aggregate.Amount += requirement.GetAmount(0);
        }
    }

    private static string ValidateBuildAccessWithoutInventory(Player player, IEnumerable<BlueprintLoadEntry> entries)
    {
        foreach (BlueprintLoadEntry entry in entries)
        {
            Piece piece = entry.Prefab.GetComponent<Piece>();
            if (piece == null)
            {
                continue;
            }

            if (!player.HaveRequirements(piece, Player.RequirementMode.IsKnown))
            {
                return HomesteadLocalization.Format("hs_blueprint_missing_known_station_or_materials", entry.Entry.Prefab);
            }

            if (piece.m_craftingStation != null &&
                !CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name, player.transform.position) &&
                !ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench))
            {
                return HomesteadLocalization.Format("hs_blueprint_missing_crafting_station", entry.Entry.Prefab);
            }
        }

        return "";
    }

    private static IEnumerator ValidateBuildAccessWithoutInventoryAsync(Player player, IEnumerable<BlueprintLoadEntry> entries, Action<string> onComplete)
    {
        int processedSinceYield = 0;

        foreach (BlueprintLoadEntry entry in entries)
        {
            Piece piece = entry.Prefab.GetComponent<Piece>();
            if (piece != null)
            {
                if (!player.HaveRequirements(piece, Player.RequirementMode.IsKnown))
                {
                    onComplete(HomesteadLocalization.Format("hs_blueprint_missing_known_station_or_materials", entry.Entry.Prefab));
                    yield break;
                }

                if (piece.m_craftingStation != null &&
                    !CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name, player.transform.position) &&
                    !ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench))
                {
                    onComplete(HomesteadLocalization.Format("hs_blueprint_missing_crafting_station", entry.Entry.Prefab));
                    yield break;
                }
            }

            processedSinceYield++;
            if (processedSinceYield >= BlueprintPlanBatchSize)
            {
                processedSinceYield = 0;
                yield return null;
            }
        }

        onComplete("");
    }

    internal static bool TryReadWearNTear(ZDO zdo, out GameObject prefab)
    {
        prefab = null!;
        if (zdo == null || !zdo.IsValid())
        {
            return false;
        }

        prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
        return prefab && prefab.GetComponent<WearNTear>() != null && prefab.GetComponent<ZNetView>() != null;
    }

    internal static bool TryReadSavableWearNTear(ZDO zdo, out GameObject prefab)
    {
        return TryReadWearNTear(zdo, out prefab) && HasBuildRecipe(prefab);
    }

    internal static bool HasBuildRecipe(GameObject? prefab)
    {
        if (!prefab)
        {
            return false;
        }

        Piece piece = prefab.GetComponent<Piece>();
        if (piece == null || piece.m_resources == null)
        {
            return false;
        }

        return HasResourceCost(piece) && IsRegisteredPlayerBuildPiece(prefab);
    }

    private static bool HasResourceCost(Piece piece)
    {
        return piece.m_resources.Any(requirement => requirement.m_resItem && requirement.m_amount > 0);
    }

    private static bool IsRegisteredPlayerBuildPiece(GameObject prefab)
    {
        string prefabName = Utils.GetPrefabName(prefab);
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return false;
        }

        ObjectDB? objectDb = ObjectDB.instance;
        if (objectDb == null || objectDb.m_items == null)
        {
            return false;
        }

        if (_buildRecipeCacheObjectDbCount != objectDb.m_items.Count)
        {
            BuildRecipeCache.Clear();
            _buildRecipeCacheObjectDbCount = objectDb.m_items.Count;
        }

        if (BuildRecipeCache.TryGetValue(prefabName, out bool cached))
        {
            return cached;
        }

        foreach (GameObject itemPrefab in objectDb.m_items)
        {
            ItemDrop itemDrop = itemPrefab ? itemPrefab.GetComponent<ItemDrop>() : null!;
            PieceTable? pieceTable = itemDrop?.m_itemData.m_shared.m_buildPieces;
            if (pieceTable?.m_pieces == null)
            {
                continue;
            }

            if (pieceTable.m_pieces.Any(piecePrefab =>
                    piecePrefab &&
                    string.Equals(Utils.GetPrefabName(piecePrefab), prefabName, StringComparison.Ordinal)))
            {
                BuildRecipeCache[prefabName] = true;
                return true;
            }
        }

        BuildRecipeCache[prefabName] = false;
        return false;
    }

    private static ZoneBlueprintFile LoadBlueprint(string name)
    {
        string path = GetBlueprintPath(name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Homestead blueprint not found: {path}");
        }

        return ZoneBlueprintFileFormat.ReadFile(path);
    }

    private static ZoneBlueprintFile LoadPlanBlueprint(string name)
    {
        string planGhostPath = GetPlanGhostBlueprintPath(name);
        if (File.Exists(planGhostPath))
        {
            return ZoneBlueprintFileFormat.ReadFile(planGhostPath);
        }

        return LoadBlueprint(name);
    }

    private static string SaveBlueprint(string name, ZoneBlueprintFile blueprint)
    {
        string path = GetBlueprintPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ZoneBlueprintFileFormat.WriteFile(path, blueprint);
        bool iconReady = false;
        try
        {
            iconReady = ZoneBlueprintVisuals.RenderAndCacheIcon(name, blueprint) != null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to render Homestead blueprint icon '{name}' immediately: {ex.Message}");
        }

        ZoneBlueprintSaveToolMenu.RefreshAfterBlueprintSaved(name, blueprint, iconReady);
        return path;
    }

    private static ZoneBlueprintFile CloneForName(ZoneBlueprintFile blueprint, string name)
    {
        return new ZoneBlueprintFile
        {
            Version = blueprint.Version,
            Name = name,
            Creator = blueprint.Creator,
            World = blueprint.World,
            SavedAt = blueprint.SavedAt,
            Radius = blueprint.Radius,
            Entries = blueprint.Entries
                .Select(entry => new ZoneBlueprintEntry
                {
                    Prefab = entry.Prefab,
                    LocalPos = entry.LocalPos.ToArray(),
                    LocalRot = entry.LocalRot.ToArray(),
                    Scale = entry.Scale.ToArray(),
                    Text = entry.Text
                })
                .ToList(),
            TerrainContacts = blueprint.TerrainContacts
                .Select(contact => new ZoneBlueprintTerrainContact
                {
                    LocalX = contact.LocalX,
                    LocalY = contact.LocalY,
                    LocalZ = contact.LocalZ
                })
                .ToList()
        };
    }

    private static string ValidateBlueprintFile(ZoneBlueprintFile blueprint)
    {
        if (blueprint.Entries.Count == 0)
        {
            return HomesteadLocalization.Text("hs_blueprint_no_entries");
        }

        if (ZNetScene.instance == null)
        {
            return HomesteadLocalization.Text("hs_common_world_not_ready");
        }

        int validEntries = 0;
        foreach (ZoneBlueprintEntry entry in blueprint.Entries)
        {
            if (IsLoadableBlueprintEntry(entry, out bool missingPrefab))
            {
                validEntries++;
            }

            if (missingPrefab)
            {
                LogMissingPrefabOnce(blueprint.Name, entry.Prefab);
            }
        }

        return validEntries > 0 ? "" : HomesteadLocalization.Format("hs_blueprint_no_valid_entries", blueprint.Name);
    }

    private static string GetUniqueBlueprintName(string preferredName)
    {
        string baseName = SanitizePathSegment(string.IsNullOrWhiteSpace(preferredName) ? "store_blueprint" : preferredName.Trim());
        string candidate = baseName;
        int index = 2;
        while (File.Exists(GetBlueprintPath(candidate)))
        {
            candidate = $"{baseName}_{index++}";
        }

        return candidate;
    }

    private static string GetUniquePlanGhostBlueprintName(string preferredName)
    {
        string baseName = SanitizePathSegment(string.IsNullOrWhiteSpace(preferredName) ? "plan_ghost" : preferredName.Trim());
        string candidate = baseName;
        int index = 2;
        while (File.Exists(GetPlanGhostBlueprintPath(candidate)))
        {
            candidate = $"{baseName}_{index++}";
        }

        return candidate;
    }

    private static void EnsureWorldReady()
    {
        if (ZNet.instance == null || ZNetScene.instance == null || ZDOMan.instance == null || ZoneSystem.instance == null)
        {
            throw new InvalidOperationException("World is not ready.");
        }
    }

    private static string GetBlueprintPath(string name)
    {
        return Path.Combine(GetWorldBlueprintDirectory(), SanitizePathSegment(name) + ZoneBlueprintFileFormat.BlueprintExtension);
    }

    private static string GetPlanGhostBlueprintPath(string name)
    {
        return Path.Combine(GetPlanGhostBlueprintDirectory(), SanitizePathSegment(name) + ZoneBlueprintFileFormat.BlueprintExtension);
    }

    internal static string GetBlueprintIconPath(string name)
    {
        return Path.Combine(GetWorldBlueprintDirectory(), SanitizePathSegment(name) + ZoneBlueprintFileFormat.IconExtension);
    }

    private static string GetWorldBlueprintDirectory()
    {
        return HomesteadPlugin.BlueprintStorageFullPath;
    }

    private static string GetPlanGhostBlueprintDirectory()
    {
        return HomesteadPlugin.PlanGhostStorageFullPath;
    }

    private static void CleanupPlanGhostFilesIfDue()
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (_nextPlanGhostCleanupAt <= 0f)
        {
            _nextPlanGhostCleanupAt = now + PlanGhostCleanupStartupDelaySeconds;
            return;
        }

        if (now < _nextPlanGhostCleanupAt)
        {
            return;
        }

        _nextPlanGhostCleanupAt = now + PlanGhostCleanupIntervalSeconds;
        try
        {
            CleanupPlanGhostFiles();
        }
        catch (Exception ex)
        {
            _nextPlanGhostCleanupAt = Time.realtimeSinceStartup + PlanGhostCleanupRegistryRetrySeconds;
            _logger.LogWarning($"Homestead plan ghost cleanup failed: {ex.Message}");
        }
    }

    private static void CleanupPlanGhostFiles()
    {
        if (!ZoneBlueprintChestZdoRegistry.IsReady)
        {
            _nextPlanGhostCleanupAt = Time.realtimeSinceStartup + PlanGhostCleanupRegistryRetrySeconds;
            return;
        }

        string directory = GetPlanGhostBlueprintDirectory();
        if (!Directory.Exists(directory))
        {
            return;
        }

        HashSet<string> liveNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZDO zdo in ZoneBlueprintChestZdoRegistry.EnumerateChestZdos())
        {
            if (zdo.GetPrefab() != ZoneBlueprintPlanChestPrefab.PrefabHash)
            {
                continue;
            }

            string name = zdo.GetString(ZoneBlueprintPlanAnchor.BlueprintNameKey, "");
            if (!string.IsNullOrWhiteSpace(name))
            {
                liveNames.Add(SanitizePathSegment(name));
            }
        }

        int deleted = 0;
        foreach (string path in Directory.GetFiles(directory, "*" + ZoneBlueprintFileFormat.BlueprintExtension, SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(name) || liveNames.Contains(name))
            {
                continue;
            }

            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to delete orphan Homestead plan ghost blueprint '{path}': {ex.Message}");
            }
        }

        if (deleted > 0)
        {
            _logger.LogInfo($"Homestead plan ghost cleanup deleted {deleted} orphan blueprint file(s).");
        }
    }

    private static string GetWorldName()
    {
        return ZNet.instance?.GetWorldName() ?? "unknown";
    }

    private static string SanitizePathSegment(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string sanitized = new string(value.Select(character => invalidChars.Contains(character) ? '_' : character).ToArray()).Trim();
        if (sanitized.Length == 0)
        {
            throw new InvalidOperationException("Blueprint name resolves to an empty path segment.");
        }

        return sanitized;
    }

    private static Vector3 ReadScale(ZDO zdo, GameObject prefab)
    {
        return zdo.GetVec3(ZDOVars.s_scaleHash, prefab.transform.localScale);
    }

    private static string ReadBlueprintText(ZDO zdo, GameObject prefab)
    {
        if (prefab.GetComponent<TextReceiver>() == null)
        {
            return "";
        }

        ZNetView? instance = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
        TextReceiver? receiver = instance != null ? instance.GetComponent<TextReceiver>() : null;
        string text = receiver != null ? receiver.GetText() : zdo.GetString(ZDOVars.s_text, "");
        return (text ?? "").Replace(";", "").Replace("\0", "");
    }

    private static float[] ToArray(Vector3 value)
    {
        return [Round(value.x), Round(value.y), Round(value.z)];
    }

    private static float[] ToArray(Quaternion value)
    {
        return [Round(value.x), Round(value.y), Round(value.z), Round(value.w)];
    }

    private static Vector3 FromVector(float[] value)
    {
        return new Vector3(value[0], value[1], value[2]);
    }

    private static Quaternion FromQuaternion(float[] value)
    {
        return new Quaternion(value[0], value[1], value[2], value[3]);
    }

    private static float Round(float value)
    {
        return Mathf.Round(value * 1000f) / 1000f;
    }

    private static float SampleGroundY(float x, float z, float fallbackY)
    {
        if (ZoneSystem.instance == null)
        {
            return fallbackY;
        }

        Vector3 point = new(x, fallbackY, z);
        ZoneSystem.instance.GetGroundData(ref point, out _, out _, out _, out _);
        return point.y;
    }

    internal sealed class BlueprintLoadPlan
    {
        public BlueprintLoadPlan(List<BlueprintLoadEntry> entries, List<Vector3> positions, List<Vector3> supportContacts)
        {
            Entries = entries;
            Positions = positions;
            SupportContacts = supportContacts;
        }

        public List<BlueprintLoadEntry> Entries { get; }
        public List<Vector3> Positions { get; }
        public List<Vector3> SupportContacts { get; }
    }

    internal sealed class BlueprintLoadEntry
    {
        public BlueprintLoadEntry(ZoneBlueprintEntry entry, GameObject prefab, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            Entry = entry;
            Prefab = prefab;
            Position = position;
            Rotation = rotation;
            Scale = scale;
        }

        public ZoneBlueprintEntry Entry { get; }
        public GameObject Prefab { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public Vector3 Scale { get; }
    }
}
