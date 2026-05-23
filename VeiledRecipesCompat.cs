using System;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;

namespace Homestead;

internal static class VeiledRecipesCompat
{
    private const string PluginGuid = "sighsorry.VeiledRecipes";
    private const string ApiTypeName = "VeiledRecipes.VeiledRecipesCompat";
    private static readonly BindingFlags PublicStaticFlags = BindingFlags.Public | BindingFlags.Static;

    private static ManualLogSource? _logger;
    private static bool _initialized;
    private static bool _registered;
    private static MethodInfo? _registerKnownPieceOverrideMethod;
    private static readonly Func<Piece, bool> KnownPieceOverride = IsHomesteadVirtualPiece;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
        Update();
    }

    public static void Update()
    {
        if (_registered)
        {
            return;
        }

        EnsureInitialized();
        RegisterKnownPieceOverride();
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var pluginInfo))
        {
            return;
        }

        Assembly? assembly = pluginInfo.Instance?.GetType().Assembly;
        Type? apiType = assembly?.GetType(ApiTypeName, throwOnError: false);
        _registerKnownPieceOverrideMethod = apiType?.GetMethod(
            "RegisterKnownPieceOverride",
            PublicStaticFlags,
            null,
            new[] { typeof(Func<Piece, bool>) },
            null);
    }

    private static void RegisterKnownPieceOverride()
    {
        if (_registerKnownPieceOverrideMethod == null)
        {
            return;
        }

        try
        {
            _registerKnownPieceOverrideMethod.Invoke(null, new object[] { KnownPieceOverride });
            _registered = true;
            _logger?.LogDebug("Registered Homestead virtual pieces with VeiledRecipes.");
        }
        catch (Exception ex)
        {
            _logger?.LogDebug($"Could not register Homestead virtual pieces with VeiledRecipes: {ex.Message}");
        }
    }

    private static bool IsHomesteadVirtualPiece(Piece piece)
    {
        return piece != null && piece.GetComponent<ZoneBlueprintSaveToolMarker>() != null;
    }
}
