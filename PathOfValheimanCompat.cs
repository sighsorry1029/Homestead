using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Homestead;

internal static class PathOfValheimanCompat
{
    internal const string PluginGuid = "treextr.pathofvalheiman";
    private static bool _installed;
    private static bool _ownsCamera;
    private static bool _ownsRepair;
    private static bool _protectsContainers;
    private static bool _queryFailed;
    private static ManualLogSource? _logger;
    private static Harmony? _harmony;
    private static readonly List<(MethodInfo Target, MethodInfo Patch)> Hooks = [];
    private static Func<Player, bool>? _cameraUnlocked;
    private static Func<Player, bool>? _progressReady;
    private static Func<float>? _repairRange;
    private static Func<bool>? _huginActive;
    private static Action<Player, string?>? _stopHugin;

    internal static void Initialize(ManualLogSource logger, Harmony harmony)
    {
        _logger = logger;
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin)) return;
        _installed = true;
        Assembly? assembly = plugin.Instance?.GetType().Assembly;
        if (assembly == null)
        {
            logger.LogWarning("Path of Valheiman instance unavailable; Homestead camera and area repair remain locked.");
            return;
        }
        Bind(assembly, harmony);
    }

    private static void Bind(Assembly assembly, Harmony harmony)
    {
        _installed = true;
        _harmony = harmony;
        try
        {
            Type progress = assembly.GetType("PassiveTree.Progress", throwOnError: true)!;
            Type stats = assembly.GetType("PassiveTree.StatSet", throwOnError: true)!;
            Type stat = assembly.GetType("PassiveTree.Stat", throwOnError: true)!;
            MethodInfo peek = RequireMethod(progress, "Peek", progress, typeof(Player));
            FieldInfo field = AccessTools.DeclaredField(progress, "Stats");
            MethodInfo has = AccessTools.DeclaredMethod(stats, "Has", [stat]);
            if (field?.FieldType != stats || field.IsStatic || has == null || has.IsStatic || has.ReturnType != typeof(bool))
                throw new MissingMemberException("Path of Valheiman progression contract");
            _progressReady = ProgressQuery(peek, field, null, null);
            _cameraUnlocked = ProgressQuery(peek, field, has, Enum.Parse(stat, "HuginsFlight"));

            Type hugin = assembly.GetType("PassiveTree.HuginFlight", throwOnError: true)!;
            MethodInfo start = RequireMethod(hugin, "Start", typeof(void), typeof(Player));
            MethodInfo stop = RequireMethod(hugin, "Stop", typeof(void), typeof(Player), typeof(string));
            MethodInfo active = RequireMethod(hugin, "get_Active", typeof(bool));
            _huginActive = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), active);
            _stopHugin = (Action<Player, string?>)Delegate.CreateDelegate(typeof(Action<Player, string?>), stop);
            Type effects = assembly.GetType("PassiveTree.Effects", throwOnError: true)!;
            MethodInfo refresh = RequireMethod(effects, "Refresh", typeof(void), typeof(Player));
            Type repair = assembly.GetType("PassiveTree.Conveniences+Player_Repair_Mass", throwOnError: true)!;
            MethodInfo range = RequireMethod(repair, "get_MassRepairRange", typeof(float));
            _repairRange = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), range);
            MethodInfo repairPostfix = RequireMethod(repair, "Postfix", typeof(void), typeof(Player), typeof(ItemDrop.ItemData), typeof(Piece));
            Type conveniences = assembly.GetType("PassiveTree.Conveniences", throwOnError: true)!;
            MethodInfo refused = RequireMethod(conveniences, "Refused", typeof(string), typeof(Container), typeof(long));

            // Validate all contracts first; only our exact hooks are rolled back on failure.
            Patch(start, nameof(AllowHuginStart));
            Patch(refresh, nameof(BeforeProgressRefresh), nameof(AfterProgressRefresh));
            Patch(repairPostfix, nameof(AllowPoVAreaRepair));
            Patch(refused, nameof(FilterContainer));
            StopHugin();
            _ownsCamera = _ownsRepair = _protectsContainers = true;
            _queryFailed = false;
            _logger?.LogInfo("Path of Valheiman compatibility enabled: passives unlock Homestead camera/area repair; Homestead settings control both. Blueprint containers are excluded from nearby crafting.");
        }
        catch (Exception ex)
        {
            RemoveHooks();
            _ownsCamera = _ownsRepair = _protectsContainers = false;
            _logger?.LogWarning($"Path of Valheiman compatibility unavailable; Homestead camera/area repair remain locked, PoV retains its implementations: {ex.Message}");
        }
    }

    private static MethodInfo RequireMethod(Type type, string name, Type result, params Type[] parameters)
    {
        MethodInfo method = AccessTools.DeclaredMethod(type, name, parameters);
        if (method == null || !method.IsStatic || method.ReturnType != result)
            throw new MissingMethodException(type.FullName, name);
        return method;
    }

    private static Func<Player, bool> ProgressQuery(MethodInfo peek, FieldInfo field, MethodInfo? has, object? flag)
    {
        var player = Expression.Parameter(typeof(Player));
        var progress = Expression.Variable(peek.ReturnType);
        var stats = Expression.Variable(field.FieldType);
        Expression check = has == null ? Expression.Constant(true) : Expression.Call(stats, has, Expression.Constant(flag));
        return Expression.Lambda<Func<Player, bool>>(
            Expression.Block([progress, stats],
                Expression.Assign(progress, Expression.Call(peek, player)),
                Expression.Condition(Expression.Equal(progress, Expression.Constant(null, progress.Type)),
                    Expression.Constant(false),
                    Expression.Block(Expression.Assign(stats, Expression.Field(progress, field)),
                        Expression.AndAlso(Expression.NotEqual(stats, Expression.Constant(null, stats.Type)), check)))), player).Compile();
    }

    private static void Patch(MethodInfo target, string prefixName, string? finalizerName = null)
    {
        MethodInfo prefix = AccessTools.DeclaredMethod(typeof(PathOfValheimanCompat), prefixName);
        Hooks.Add((target, prefix));
        MethodInfo? finalizer = finalizerName == null ? null : AccessTools.DeclaredMethod(typeof(PathOfValheimanCompat), finalizerName);
        if (finalizer != null) Hooks.Add((target, finalizer));
        _harmony!.Patch(target, prefix: new HarmonyMethod(prefix), finalizer: finalizer == null ? null : new HarmonyMethod(finalizer));
    }

    internal static bool CameraUnlocked(Player player) => Unlocked(player, camera: true);
    internal static bool AreaRepairUnlocked(Player player) => Unlocked(player, camera: false);

    private static bool Unlocked(Player player, bool camera)
    {
        if (!_installed) return true;
        if (!player || player != Player.m_localPlayer || _queryFailed) return false;
        try
        {
            return camera
                ? _ownsCamera && _cameraUnlocked!(player)
                : _ownsRepair && _progressReady!(player) && _repairRange!() > 0.01f;
        }
        catch (Exception ex)
        {
            _queryFailed = true;
            _logger?.LogWarning($"Path of Valheiman passive query failed; camera/area repair locked: {ex.Message}");
            return false;
        }
    }

    private static bool AllowHuginStart() => !_ownsCamera;
    private static bool AllowPoVAreaRepair() => !_ownsRepair;

    private static void StopHugin()
    {
        if (_huginActive?.Invoke() == true) _stopHugin!(Player.m_localPlayer, null);
    }

    private static void BeforeProgressRefresh(Player __0, out bool __state)
    {
        __state = _ownsCamera && ZoneBuildCamera.BeginOrdinaryDistanceRefresh(__0);
    }

    private static Exception? AfterProgressRefresh(Player __0, bool __state, Exception? __exception)
    {
        if (__state) ZoneBuildCamera.EndOrdinaryDistanceRefresh(__0, __exception == null);
        return __exception;
    }

    private static bool FilterContainer(Container __0, ref string? __result)
    {
        if (!_protectsContainers || !__0) return true;
        // Container.m_nview is private in the original game assembly.
        ZNetView view = __0.GetComponent<ZNetView>();
        ZDO? zdo = view ? view.GetZDO() : null;
        int prefab = zdo?.GetPrefab() ?? 0;
        if (prefab != ZoneBlueprintPlanChestPrefab.PrefabName.GetStableHashCode() &&
            prefab != ZoneBlueprintStoreChestPrefab.PricePrefabName.GetStableHashCode() &&
            prefab != ZoneBlueprintStoreChestPrefab.PurchasePrefabName.GetStableHashCode() &&
            prefab != ZoneBlueprintStoreChestPrefab.PayoutPrefabName.GetStableHashCode()) return true;
        __result = "Homestead blueprint container";
        return false;
    }

    private static void RemoveHooks()
    {
        if (_harmony != null)
            foreach (var hook in Hooks)
            {
                try { _harmony.Unpatch(hook.Target, hook.Patch); }
                catch (Exception ex) { _logger?.LogWarning($"Could not remove Path of Valheiman hook: {ex.Message}"); }
            }
        Hooks.Clear();
    }

    internal static void ResetForWorldSession() => _queryFailed = false;

    internal static void Shutdown()
    {
        RemoveHooks();
        _installed = _ownsCamera = _ownsRepair = _protectsContainers = _queryFailed = false;
        _cameraUnlocked = _progressReady = null;
        _repairRange = null;
        _huginActive = null;
        _stopHugin = null;
        _harmony = null;
    }

    // PoV 5.0.4 BuildingEconomy.PaidRequirements/Cost. Read the historical
    // payment on the actual ZDO, including unloaded pieces and worlds where
    // PoV has since been removed. Never use the dismantler's current passives.
    internal static int GetRefundAmount(ZDO zdo, Piece.Requirement requirement)
    {
        if (zdo.GetBool("PT_BuildSaved", false)) return 0;
        float paidFactor = Mathf.Clamp01(zdo.GetFloat("PT_BuildPaidFactor", 1f));
        float reduction = paidFactor <= 0f ? 0f : 1f - paidFactor;
        if (reduction <= 0f) return requirement.GetAmount(0);
        return Mathf.CeilToInt(requirement.m_amount * (1f - Mathf.Clamp(reduction, 0f, 0.5f)) - 0.00001f);
    }
}
