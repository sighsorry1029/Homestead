using System;
#if DEBUG
using System.Collections;
#endif
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if DEBUG
using System.Reflection;
#endif
using BepInEx.Logging;

namespace Homestead;

internal readonly struct ZoneBlueprintStoreDraftLease
{
    public ZoneBlueprintStoreDraftLease(string listingId, string blueprintFile)
    {
        ListingId = listingId;
        BlueprintFile = blueprintFile;
    }

    public string ListingId { get; }
    public string BlueprintFile { get; }
}

internal static class ZoneBlueprintStoreDraftRepository
{
    internal enum CatalogRecoveryStatus
    {
        RestoredDurably,
        QueuedForRetry,
        QueueFailed
    }

    private const int CurrentCatalogVersion = 1;
    private const string CatalogFileName = "catalog.yml";
    private const string CatalogBackupSuffix = ".bak";
    private const int MaxListingNameLength = 64;
    private static readonly TimeSpan CatalogFlushDelay = TimeSpan.FromSeconds(2.5);

    private static ManualLogSource? _logger;
    private static ZoneBlueprintStoreCatalog? _cachedCatalog;
    private static DateTime _cachedCatalogWriteUtc;
    private static DateTime _nextCatalogFlushUtc;
    private static bool _catalogCacheLoaded;
    private static bool _catalogDirty;
    private static bool _flushCatalogOnNextUpdate;

    public static string StoreDirectory => HomesteadPlugin.BlueprintStoreStorageFullPath;
    public static string CatalogPath => Path.Combine(StoreDirectory, CatalogFileName);
    private static string CatalogBackupPath => CatalogPath + CatalogBackupSuffix;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
#if DEBUG
        ValidateCatalogCloneMapping();
#endif
    }

    public static void Update()
    {
        if (_flushCatalogOnNextUpdate)
        {
            _flushCatalogOnNextUpdate = false;
            _nextCatalogFlushUtc = DateTime.MinValue;
        }

        Flush(force: false);
    }

    public static string TrimName(string name)
    {
        name = (name ?? "").Trim();
        return name.Length <= MaxListingNameLength ? name : name.Substring(0, MaxListingNameLength);
    }

    public static ZoneBlueprintStoreDraftLease CreateDraft(string name, ZoneBlueprintFile blueprint)
    {
        Directory.CreateDirectory(StoreDirectory);
        string listingId = CreateListingId(name);
        string blueprintFile = listingId + ZoneBlueprintFileFormat.BlueprintExtension;
        blueprint.Name = name;
        blueprint.SavedAt = HomesteadTimestamp.Now();
        ZoneBlueprintFileFormat.WriteFile(Path.Combine(StoreDirectory, blueprintFile), blueprint);
        return new ZoneBlueprintStoreDraftLease(listingId, blueprintFile);
    }

    public static ZoneBlueprintStoreCatalog LoadCatalogForEdit()
    {
        return LoadCatalogSnapshot();
    }

    public static ZoneBlueprintStoreCatalog LoadActiveCatalog()
    {
        ZoneBlueprintStoreCatalog catalog = LoadCatalogSnapshot();
        RemoveInactiveAndExpiredListings(catalog);
        return catalog;
    }

    public static ZoneBlueprintStoreCatalog LoadCatalogSnapshot()
    {
        Directory.CreateDirectory(StoreDirectory);
        if (_catalogDirty && _cachedCatalog != null)
        {
            return CloneCatalog(_cachedCatalog);
        }

        if (!File.Exists(CatalogPath) && !File.Exists(CatalogBackupPath))
        {
            ZoneBlueprintStoreCatalog empty = new();
            if (!TrySaveCatalogImmediate(empty, out string reason))
            {
                throw new IOException(reason);
            }

            return CloneCatalog(empty);
        }

        DateTime writeUtc = File.Exists(CatalogPath) ? File.GetLastWriteTimeUtc(CatalogPath) : DateTime.MinValue;
        if (_catalogCacheLoaded && _cachedCatalog != null && writeUtc == _cachedCatalogWriteUtc)
        {
            return CloneCatalog(_cachedCatalog);
        }

        try
        {
            return CacheLoadedCatalog(ReadCatalogFile(CatalogPath), writeUtc);
        }
        catch (UnsupportedCatalogVersionException versionError)
        {
            _logger?.LogError($"Blueprint store catalog version is not supported. The catalog was left untouched: {versionError.Message}");
            throw new InvalidDataException(
                "Blueprint store catalog was created by an incompatible Homestead version. Existing data was left untouched.",
                versionError);
        }
        catch (Exception primaryError)
        {
            if (TryReadCatalogFile(CatalogBackupPath, out ZoneBlueprintStoreCatalog backupCatalog, out Exception? backupError))
            {
                _logger?.LogWarning($"Blueprint store catalog could not be loaded; recovering from backup: {primaryError.Message}");
                TryRestorePrimaryFromBackup();
                DateTime recoveredWriteUtc = File.Exists(CatalogPath)
                    ? File.GetLastWriteTimeUtc(CatalogPath)
                    : File.GetLastWriteTimeUtc(CatalogBackupPath);
                return CacheLoadedCatalog(backupCatalog, recoveredWriteUtc);
            }

            _logger?.LogError($"Blueprint store catalog and backup could not be loaded. Existing files were left untouched. " +
                              $"Catalog: {primaryError.Message}; Backup: {backupError?.Message ?? "missing"}");
            throw new InvalidDataException(
                "Blueprint store catalog could not be loaded. Existing data was left untouched; check the server log.",
                primaryError);
        }
    }

    public static void SaveCatalog(ZoneBlueprintStoreCatalog catalog, bool immediate = false)
    {
        Directory.CreateDirectory(StoreDirectory);
        ValidateCatalogVersion(catalog);
        NormalizeCatalog(catalog);
        _cachedCatalog = CloneCatalog(catalog);
        _catalogCacheLoaded = true;
        _catalogDirty = true;
        _nextCatalogFlushUtc = DateTime.UtcNow + CatalogFlushDelay;
        if (immediate)
        {
            Flush(force: true);
        }
    }

    public static bool TrySaveCatalogImmediate(ZoneBlueprintStoreCatalog catalog, out string reason)
    {
        try
        {
            Directory.CreateDirectory(StoreDirectory);
            ValidateCatalogVersion(catalog);
            NormalizeCatalog(catalog);
            ZoneBlueprintStoreCatalog snapshot = CloneCatalog(catalog);
            WriteCatalogAtomic(snapshot);
            _cachedCatalog = snapshot;
            _cachedCatalogWriteUtc = File.GetLastWriteTimeUtc(CatalogPath);
            _catalogCacheLoaded = true;
            _catalogDirty = false;
            _flushCatalogOnNextUpdate = false;
            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Failed to save blueprint store catalog immediately: {ex.Message}");
            reason = "Blueprint store catalog could not be saved. Try again shortly.";
            return false;
        }
    }

    public static CatalogRecoveryStatus RestoreCatalogAfterFailedMutation(
        ZoneBlueprintStoreCatalog catalog,
        string operation)
    {
        if (TrySaveCatalogImmediate(catalog, out string immediateReason))
        {
            _logger?.LogInfo($"Blueprint store catalog rollback for {operation} was saved durably.");
            return CatalogRecoveryStatus.RestoredDurably;
        }

        try
        {
            SaveCatalog(catalog);
            _flushCatalogOnNextUpdate = true;
            _logger?.LogWarning($"Blueprint store catalog rollback for {operation} could not be saved immediately; restored state is queued for retry. {immediateReason}");
            return CatalogRecoveryStatus.QueuedForRetry;
        }
        catch (Exception ex)
        {
            _logger?.LogError($"Blueprint store catalog rollback for {operation} could not be saved or queued. {immediateReason} {ex}");
            return CatalogRecoveryStatus.QueueFailed;
        }
    }

    public static void Flush(bool force)
    {
        if (!_catalogDirty || _cachedCatalog == null)
        {
            return;
        }

        if (!force && DateTime.UtcNow < _nextCatalogFlushUtc)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(StoreDirectory);
            WriteCatalogAtomic(_cachedCatalog);
            _cachedCatalogWriteUtc = File.GetLastWriteTimeUtc(CatalogPath);
            _catalogDirty = false;
            _flushCatalogOnNextUpdate = false;
        }
        catch (Exception ex)
        {
            _nextCatalogFlushUtc = DateTime.UtcNow + CatalogFlushDelay;
            _logger?.LogWarning($"Failed to flush blueprint store catalog: {ex.Message}");
        }
    }

    private static void WriteCatalogAtomic(ZoneBlueprintStoreCatalog catalog)
    {
        string tempPath = CatalogPath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, HomesteadYaml.Serialize(catalog));
            ReadCatalogFile(tempPath);
            if (!File.Exists(CatalogPath))
            {
                File.Move(tempPath, CatalogPath);
                EnsureCatalogBackup();
                return;
            }

            if (!TryReadCatalogFile(CatalogPath, out _, out Exception? currentCatalogError))
            {
                throw new InvalidDataException(
                    "The existing blueprint store catalog changed or became invalid; refusing to overwrite it.",
                    currentCatalogError);
            }

            File.Replace(tempPath, CatalogPath, CatalogBackupPath);

            EnsureCatalogBackup();
        }
        finally
        {
            TryDeleteTransientFile(tempPath);
        }
    }

    private static ZoneBlueprintStoreCatalog ReadCatalogFile(string path)
    {
        ZoneBlueprintStoreCatalog catalog = HomesteadYaml.Deserialize<ZoneBlueprintStoreCatalog>(File.ReadAllText(path));
        if (catalog == null)
        {
            throw new InvalidDataException("Blueprint store catalog is empty.");
        }

        ValidateCatalogVersion(catalog);
        NormalizeCatalog(catalog);
        return catalog;
    }

    private static bool TryReadCatalogFile(
        string path,
        out ZoneBlueprintStoreCatalog catalog,
        out Exception? error)
    {
        catalog = null!;
        error = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            catalog = ReadCatalogFile(path);
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    private static ZoneBlueprintStoreCatalog CacheLoadedCatalog(ZoneBlueprintStoreCatalog catalog, DateTime writeUtc)
    {
        _cachedCatalog = CloneCatalog(catalog);
        _cachedCatalogWriteUtc = writeUtc;
        _catalogCacheLoaded = true;
        _catalogDirty = false;
        return CloneCatalog(catalog);
    }

    private static void ValidateCatalogVersion(ZoneBlueprintStoreCatalog catalog)
    {
        if (catalog.Version != CurrentCatalogVersion)
        {
            throw new UnsupportedCatalogVersionException(
                $"Unsupported blueprint store catalog version {catalog.Version}; expected {CurrentCatalogVersion}.");
        }
    }

    private static void EnsureCatalogBackup()
    {
        try
        {
            if (!File.Exists(CatalogBackupPath))
            {
                File.Copy(CatalogPath, CatalogBackupPath, overwrite: false);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Blueprint store catalog was saved, but its backup could not be created: {ex.Message}");
        }
    }

    private static void TryRestorePrimaryFromBackup()
    {
        string recoveryPath = CatalogPath + ".recovery.tmp";
        try
        {
            File.Copy(CatalogBackupPath, recoveryPath, overwrite: true);
            ReadCatalogFile(recoveryPath);
            if (File.Exists(CatalogPath))
            {
                File.Replace(recoveryPath, CatalogPath, null);
            }
            else
            {
                File.Move(recoveryPath, CatalogPath);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Blueprint store backup was loaded, but the primary catalog could not be restored: {ex.Message}");
        }
        finally
        {
            TryDeleteTransientFile(recoveryPath);
        }
    }

    private static void TryDeleteTransientFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug($"Failed to remove blueprint store temporary file '{Path.GetFileName(path)}': {ex.Message}");
        }
    }

    private static void NormalizeCatalog(ZoneBlueprintStoreCatalog catalog)
    {
        catalog.Listings ??= [];
        catalog.Offers ??= [];
        catalog.Notifications ??= [];
        catalog.Balances ??= [];
    }

    public static ZoneBlueprintStoreCatalog CloneCatalog(ZoneBlueprintStoreCatalog source)
    {
        NormalizeCatalog(source);
        return new ZoneBlueprintStoreCatalog
        {
            Version = source.Version,
            Listings = source.Listings.Select(CloneListing).ToList(),
            Offers = source.Offers.Select(CloneOffer).ToList(),
            Notifications = source.Notifications.Select(CloneNotification).ToList(),
            Balances = source.Balances.Select(CloneBalance).ToList()
        };
    }

    private static ZoneBlueprintStoreListing CloneListing(ZoneBlueprintStoreListing source)
    {
        return new ZoneBlueprintStoreListing
        {
            ListingId = source.ListingId,
            Name = source.Name,
            SellerName = source.SellerName,
            SellerPlayerId = source.SellerPlayerId,
            SellerPlatformId = source.SellerPlatformId,
            CreatedAt = source.CreatedAt,
            ExpiresAt = source.ExpiresAt,
            PriceItems = ClonePriceItems(source.PriceItems),
            EntryCount = source.EntryCount,
            PurchaseCount = source.PurchaseCount,
            BlueprintFile = source.BlueprintFile,
            IconPngBase64 = source.IconPngBase64,
            Active = source.Active
        };
    }

    private static ZoneBlueprintStoreOffer CloneOffer(ZoneBlueprintStoreOffer source)
    {
        return new ZoneBlueprintStoreOffer
        {
            OfferId = source.OfferId,
            ListingId = source.ListingId,
            BuyerName = source.BuyerName,
            BuyerPlayerId = source.BuyerPlayerId,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            Status = source.Status,
            PriceItems = ClonePriceItems(source.PriceItems)
        };
    }

    private static ZoneBlueprintStoreNotification CloneNotification(ZoneBlueprintStoreNotification source)
    {
        return new ZoneBlueprintStoreNotification
        {
            NotificationId = source.NotificationId,
            Type = source.Type,
            RecipientPlayerId = source.RecipientPlayerId,
            RecipientName = source.RecipientName,
            ActorName = source.ActorName,
            ListingId = source.ListingId,
            ListingName = source.ListingName,
            OfferId = source.OfferId,
            Message = source.Message,
            CreatedAt = source.CreatedAt,
            Read = source.Read,
            ReadByPlayerIds = source.ReadByPlayerIds?.ToList() ?? []
        };
    }

    private static ZoneBlueprintStoreBalance CloneBalance(ZoneBlueprintStoreBalance source)
    {
        return new ZoneBlueprintStoreBalance
        {
            SellerPlayerId = source.SellerPlayerId,
            SellerName = source.SellerName,
            Coins = source.Coins,
            Materials = ClonePriceItems(source.Materials)
        };
    }

    private static List<ZoneBlueprintStorePriceItem> ClonePriceItems(IEnumerable<ZoneBlueprintStorePriceItem>? source)
    {
        return source?.Select(item => new ZoneBlueprintStorePriceItem
        {
            ItemName = item.ItemName,
            PrefabName = item.PrefabName,
            DisplayName = item.DisplayName,
            Amount = item.Amount
        }).ToList() ?? [];
    }

#if DEBUG
    private static void ValidateCatalogCloneMapping()
    {
        int seed = 0;
        ZoneBlueprintStoreCatalog source = (ZoneBlueprintStoreCatalog)CreateCloneProbe(typeof(ZoneBlueprintStoreCatalog), ref seed, 0);
        ZoneBlueprintStoreCatalog clone = CloneCatalog(source);
        if (!string.Equals(HomesteadYaml.Serialize(source), HomesteadYaml.Serialize(clone), StringComparison.Ordinal) ||
            ReferenceEquals(source.Listings, clone.Listings) ||
            ReferenceEquals(source.Offers, clone.Offers) ||
            ReferenceEquals(source.Notifications, clone.Notifications) ||
            ReferenceEquals(source.Balances, clone.Balances) ||
            ReferenceEquals(source.Listings[0].PriceItems, clone.Listings[0].PriceItems) ||
            ReferenceEquals(source.Offers[0].PriceItems, clone.Offers[0].PriceItems) ||
            ReferenceEquals(source.Notifications[0].ReadByPlayerIds, clone.Notifications[0].ReadByPlayerIds) ||
            ReferenceEquals(source.Balances[0].Materials, clone.Balances[0].Materials))
        {
            throw new InvalidOperationException("Blueprint store catalog clone mapping is incomplete or shares mutable state.");
        }
    }

    private static object CreateCloneProbe(Type type, ref int seed, int depth)
    {
        if (depth > 8)
        {
            throw new InvalidOperationException($"Clone probe exceeded the supported object depth at {type.FullName}.");
        }

        if (type == typeof(string))
        {
            return $"clone_probe_{++seed}";
        }

        if (type == typeof(int))
        {
            return ++seed;
        }

        if (type == typeof(long))
        {
            return (long)++seed;
        }

        if (type == typeof(bool))
        {
            return true;
        }

        Type? nullableType = Nullable.GetUnderlyingType(type);
        if (nullableType != null)
        {
            return CreateCloneProbe(nullableType, ref seed, depth + 1);
        }

        if (type.IsEnum)
        {
            Array values = Enum.GetValues(type);
            return values.GetValue(values.Length - 1)!;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            IList list = (IList)Activator.CreateInstance(type)!;
            list.Add(CreateCloneProbe(type.GetGenericArguments()[0], ref seed, depth + 1));
            return list;
        }

        object instance = Activator.CreateInstance(type) ??
                          throw new InvalidOperationException($"Cannot create clone probe for {type.FullName}.");
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
            {
                property.SetValue(instance, CreateCloneProbe(property.PropertyType, ref seed, depth + 1));
            }
        }

        return instance;
    }
#endif

    public static bool TryRemoveListingsImmediate(
        ZoneBlueprintStoreCatalog catalog,
        IEnumerable<ZoneBlueprintStoreListing> listings,
        out string reason)
    {
        HashSet<ZoneBlueprintStoreListing> listingsToRemove = listings
            .Where(listing => listing != null)
            .ToHashSet();
        if (listingsToRemove.Count == 0)
        {
            reason = "";
            return true;
        }

        HashSet<string> listingIds = listingsToRemove
            .Select(listing => listing.ListingId)
            .Where(listingId => !string.IsNullOrWhiteSpace(listingId))
            .ToHashSet(StringComparer.Ordinal);

        List<string> blueprintFiles = catalog.Listings
            .Where(listing => listingsToRemove.Contains(listing) || listingIds.Contains(listing.ListingId))
            .Select(listing => listing.BlueprintFile)
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .ToList();
        catalog.Listings.RemoveAll(listing => listingsToRemove.Contains(listing) || listingIds.Contains(listing.ListingId));
        catalog.Offers.RemoveAll(offer => listingIds.Contains(offer.ListingId));
        if (!TrySaveCatalogImmediate(catalog, out reason))
        {
            return false;
        }

        DeleteUnreferencedListingFiles(catalog, blueprintFiles);
        return true;
    }

    private static void DeleteUnreferencedListingFiles(
        ZoneBlueprintStoreCatalog catalog,
        IEnumerable<string> blueprintFiles)
    {
        try
        {
            HashSet<string> referencedFiles = catalog.Listings
                .Select(listing => Path.GetFileName(listing.BlueprintFile ?? ""))
                .Where(file => !string.IsNullOrWhiteSpace(file))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string blueprintFile in blueprintFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!referencedFiles.Contains(Path.GetFileName(blueprintFile)))
                {
                    DeleteFile(blueprintFile);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Blueprint store listing data was saved, but an unreferenced draft could not be cleaned up: {ex.Message}");
        }
    }

    private static void RemoveInactiveAndExpiredListings(ZoneBlueprintStoreCatalog catalog)
    {
        DateTime utcNow = DateTime.UtcNow;
        int autoDelistMaxPurchases = BlueprintConfig.StoreSettings.AutoDelistMaxPurchases;
        List<ZoneBlueprintStoreListing> listingsToRemove = catalog.Listings
            .Where(listing =>
                !listing.Active ||
                (!string.IsNullOrWhiteSpace(listing.ExpiresAt) &&
                 listing.PurchaseCount <= autoDelistMaxPurchases &&
                 HomesteadTimestamp.IsExpired(listing.ExpiresAt, utcNow)))
            .ToList();
        HashSet<string> retainedListingIds = catalog.Listings
            .Except(listingsToRemove)
            .Select(listing => listing.ListingId)
            .Where(listingId => !string.IsNullOrWhiteSpace(listingId))
            .ToHashSet(StringComparer.Ordinal);
        int removedOfferCount = catalog.Offers.RemoveAll(offer =>
            string.Equals(offer.Status, ZoneBlueprintStoreOfferStatus.Deleted, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(offer.ListingId) && !retainedListingIds.Contains(offer.ListingId)));
        if (listingsToRemove.Count == 0 && removedOfferCount == 0)
        {
            return;
        }

        bool saved = listingsToRemove.Count > 0
            ? TryRemoveListingsImmediate(catalog, listingsToRemove, out string reason)
            : TrySaveCatalogImmediate(catalog, out reason);
        if (!saved)
        {
            throw new IOException(reason);
        }
    }

    public static bool TryLoadBlueprintFile(string blueprintFile, out ZoneBlueprintFile blueprint, out string reason)
    {
        blueprint = null!;
        reason = "";
        string fileName = Path.GetFileName(blueprintFile ?? "");
        if (string.IsNullOrWhiteSpace(fileName))
        {
            reason = "Blueprint store file is missing.";
            return false;
        }

        if (!fileName.EndsWith(ZoneBlueprintFileFormat.BlueprintExtension, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Blueprint store file is not a .blueprint file.";
            return false;
        }

        string path = Path.Combine(StoreDirectory, fileName);
        if (!File.Exists(path))
        {
            reason = "Blueprint store file is missing.";
            return false;
        }

        try
        {
            blueprint = ZoneBlueprintFileFormat.ReadFile(path);
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Failed to load blueprint store file: {ex.Message}";
            return false;
        }
    }

    public static bool TryGetBlueprintFileWriteUtc(string blueprintFile, out DateTime writeUtc)
    {
        writeUtc = default;
        string fileName = Path.GetFileName(blueprintFile ?? "");
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        string path = Path.Combine(StoreDirectory, fileName);
        if (!File.Exists(path))
        {
            return false;
        }

        writeUtc = File.GetLastWriteTimeUtc(path);
        return true;
    }

    public static void DeleteFile(string blueprintFile)
    {
        if (string.IsNullOrWhiteSpace(blueprintFile))
        {
            return;
        }

        try
        {
            string fileName = Path.GetFileName(blueprintFile);
            string path = Path.Combine(StoreDirectory, fileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Failed to delete blueprint store draft '{blueprintFile}': {ex.Message}");
        }
    }

    public static bool HasOrphanDraftCandidates(TimeSpan grace)
    {
        try
        {
            if (!Directory.Exists(StoreDirectory))
            {
                return false;
            }

            ZoneBlueprintStoreCatalog catalog = LoadCatalogSnapshot();
            HashSet<string> catalogFiles = catalog.Listings
                .Select(listing => Path.GetFileName(listing.BlueprintFile ?? ""))
                .Where(file => !string.IsNullOrWhiteSpace(file))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            DateTime cutoff = DateTime.UtcNow - grace;

            foreach (string path in Directory.GetFiles(StoreDirectory, "*" + ZoneBlueprintFileFormat.BlueprintExtension, SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(path);
                if (!catalogFiles.Contains(fileName) && File.GetLastWriteTimeUtc(path) <= cutoff)
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Blueprint store orphan draft precheck failed: {ex.Message}");
        }

        return false;
    }

    public static void SweepOrphanDrafts(HashSet<string> liveDraftFiles, TimeSpan grace)
    {
        try
        {
            if (!Directory.Exists(StoreDirectory))
            {
                return;
            }

            ZoneBlueprintStoreCatalog catalog = LoadCatalogSnapshot();
            HashSet<string> catalogFiles = catalog.Listings
                .Select(listing => Path.GetFileName(listing.BlueprintFile ?? ""))
                .Where(file => !string.IsNullOrWhiteSpace(file))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            DateTime cutoff = DateTime.UtcNow - grace;
            int deleted = 0;

            foreach (string path in Directory.GetFiles(StoreDirectory, "*" + ZoneBlueprintFileFormat.BlueprintExtension, SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(path);
                if (catalogFiles.Contains(fileName) || liveDraftFiles.Contains(fileName))
                {
                    continue;
                }

                if (File.GetLastWriteTimeUtc(path) > cutoff)
                {
                    continue;
                }

                File.Delete(path);
                deleted++;
            }

            if (deleted > 0)
            {
                _logger?.LogInfo($"Blueprint store orphan draft sweep deleted {deleted} file(s).");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Blueprint store orphan draft sweep failed: {ex.Message}");
        }
    }

    private static string CreateListingId(string name)
    {
        string safeName = new(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        safeName = string.IsNullOrWhiteSpace(safeName) ? "blueprint" : safeName.Trim('_');
        string id = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{safeName}_{Guid.NewGuid():N}".ToLowerInvariant();
        return id.Length <= 64 ? id : id.Substring(0, 64);
    }

    private sealed class UnsupportedCatalogVersionException : Exception
    {
        public UnsupportedCatalogVersionException(string message) : base(message)
        {
        }
    }
}
