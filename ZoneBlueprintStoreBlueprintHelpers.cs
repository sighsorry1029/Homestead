using System;
using System.Linq;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintStoreBlueprints
{
    public static bool TryLoadListingBlueprint(string listingId, out ZoneBlueprintStoreListing listing, out ZoneBlueprintFile blueprint, out string reason)
    {
        blueprint = null!;
        if (!TryLoadListing(listingId, out listing, out reason))
        {
            return false;
        }

        if (!ZoneBlueprintStoreDraftRepository.TryLoadBlueprintFile(listing.BlueprintFile, out blueprint, out reason))
        {
            return false;
        }

        return true;
    }

    public static bool TryLoadListing(string listingId, out ZoneBlueprintStoreListing listing, out string reason)
    {
        listing = null!;
        reason = "";

        ZoneBlueprintStoreCatalog catalog = ZoneBlueprintStoreDraftRepository.LoadActiveCatalog();
        listing = catalog.Listings.FirstOrDefault(item => item.Active && item.ListingId == listingId)!;
        if (listing == null)
        {
            reason = HomesteadLocalization.Text("hs_store_listing_not_found");
            return false;
        }

        return true;
    }

    public static string ValidateStoreBlueprint(ZoneBlueprintFile blueprint)
    {
        if (blueprint.Entries.Count == 0)
        {
            return HomesteadLocalization.Text("hs_store_blueprint_no_entries");
        }

        if (ZNetScene.instance == null)
        {
            return HomesteadLocalization.Text("hs_common_world_not_ready");
        }

        foreach (ZoneBlueprintEntry entry in blueprint.Entries)
        {
            GameObject prefab = ZNetScene.instance.GetPrefab(entry.Prefab);
            if (!prefab || prefab.GetComponent<WearNTear>() == null || !ZoneBlueprintCommands.HasBuildRecipe(prefab))
            {
                return HomesteadLocalization.Format("hs_store_blueprint_unsupported_prefab", entry.Prefab);
            }
        }

        return "";
    }
}
