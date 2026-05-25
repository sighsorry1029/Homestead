using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private const string CatalogFileName = "catalog.yml";
    private const int MaxListingNameLength = 64;
    private static readonly TimeSpan CatalogFlushDelay = TimeSpan.FromSeconds(2.5);

    private static ManualLogSource? _logger;
    private static ZoneBlueprintStoreCatalog? _cachedCatalog;
    private static DateTime _cachedCatalogWriteUtc;
    private static DateTime _nextCatalogFlushUtc;
    private static bool _catalogCacheLoaded;
    private static bool _catalogDirty;

    public static string StoreDirectory => HomesteadPlugin.BlueprintStoreStorageFullPath;
    public static string CatalogPath => Path.Combine(StoreDirectory, CatalogFileName);

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
    }

    public static void Update()
    {
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
        string blueprintFile = listingId + ".hsbp.yml";
        blueprint.Name = name;
        blueprint.SavedAt = HomesteadTimestamp.Now();
        File.WriteAllText(Path.Combine(StoreDirectory, blueprintFile), HomesteadYaml.Serialize(blueprint));
        return new ZoneBlueprintStoreDraftLease(listingId, blueprintFile);
    }

    public static ZoneBlueprintStoreCatalog LoadCatalogForEdit()
    {
        return LoadCatalogSnapshot();
    }

    public static ZoneBlueprintStoreCatalog LoadActiveCatalog()
    {
        ZoneBlueprintStoreCatalog catalog = LoadCatalogSnapshot();
        DeactivateExpiredListings(catalog);
        return catalog;
    }

    public static ZoneBlueprintStoreCatalog LoadCatalogSnapshot()
    {
        Directory.CreateDirectory(StoreDirectory);
        if (_catalogDirty && _cachedCatalog != null)
        {
            return CloneCatalog(_cachedCatalog);
        }

        if (!File.Exists(CatalogPath))
        {
            ZoneBlueprintStoreCatalog empty = new();
            SaveCatalog(empty, immediate: true);
            return CloneCatalog(empty);
        }

        DateTime writeUtc = File.GetLastWriteTimeUtc(CatalogPath);
        if (_catalogCacheLoaded && _cachedCatalog != null && writeUtc == _cachedCatalogWriteUtc)
        {
            return CloneCatalog(_cachedCatalog);
        }

        try
        {
            ZoneBlueprintStoreCatalog catalog = HomesteadYaml.Deserialize<ZoneBlueprintStoreCatalog>(File.ReadAllText(CatalogPath));
            NormalizeCatalog(catalog);
            _cachedCatalog = CloneCatalog(catalog);
            _cachedCatalogWriteUtc = writeUtc;
            _catalogCacheLoaded = true;
            return CloneCatalog(catalog);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Failed to load blueprint store catalog: {ex.Message}");
            return new ZoneBlueprintStoreCatalog();
        }
    }

    public static void SaveCatalog(ZoneBlueprintStoreCatalog catalog, bool immediate = false)
    {
        Directory.CreateDirectory(StoreDirectory);
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
        SaveCatalog(catalog, immediate: true);
        if (_catalogDirty)
        {
            reason = "Blueprint store catalog could not be saved. Try again shortly.";
            return false;
        }

        reason = "";
        return true;
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
        File.WriteAllText(tempPath, HomesteadYaml.Serialize(catalog));
        if (!File.Exists(CatalogPath))
        {
            File.Move(tempPath, CatalogPath);
            return;
        }

        try
        {
            File.Replace(tempPath, CatalogPath, null);
        }
        catch
        {
            File.Copy(tempPath, CatalogPath, overwrite: true);
            File.Delete(tempPath);
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
            BuyerPlatformId = source.BuyerPlatformId,
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
            RecipientPlatformId = source.RecipientPlatformId,
            RecipientPlayerId = source.RecipientPlayerId,
            RecipientName = source.RecipientName,
            ActorName = source.ActorName,
            ListingId = source.ListingId,
            ListingName = source.ListingName,
            OfferId = source.OfferId,
            Message = source.Message,
            CreatedAt = source.CreatedAt,
            Read = source.Read,
            ReadByPlatformIds = source.ReadByPlatformIds?.ToList() ?? [],
            ReadByPlayerIds = source.ReadByPlayerIds?.ToList() ?? []
        };
    }

    private static ZoneBlueprintStoreBalance CloneBalance(ZoneBlueprintStoreBalance source)
    {
        return new ZoneBlueprintStoreBalance
        {
            SellerPlayerId = source.SellerPlayerId,
            SellerPlatformId = source.SellerPlatformId,
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

    public static bool DeactivateExpiredListings(ZoneBlueprintStoreCatalog catalog)
    {
        DateTime utcNow = DateTime.UtcNow;
        bool changed = false;
        foreach (ZoneBlueprintStoreListing listing in catalog.Listings)
        {
            if (!listing.Active || string.IsNullOrWhiteSpace(listing.ExpiresAt))
            {
                continue;
            }

            if (listing.PurchaseCount <= BlueprintConfig.StoreSettings.AutoDelistMaxPurchases &&
                HomesteadTimestamp.IsExpired(listing.ExpiresAt, utcNow))
            {
                listing.Active = false;
                changed = true;
            }
        }

        if (changed)
        {
            SaveCatalog(catalog);
        }

        return changed;
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

        string path = Path.Combine(StoreDirectory, fileName);
        if (!File.Exists(path))
        {
            reason = "Blueprint store file is missing.";
            return false;
        }

        try
        {
            blueprint = HomesteadYaml.Deserialize<ZoneBlueprintFile>(File.ReadAllText(path));
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

            foreach (string path in Directory.GetFiles(StoreDirectory, "*.hsbp.yml", SearchOption.TopDirectoryOnly))
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

            foreach (string path in Directory.GetFiles(StoreDirectory, "*.hsbp.yml", SearchOption.TopDirectoryOnly))
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
}
