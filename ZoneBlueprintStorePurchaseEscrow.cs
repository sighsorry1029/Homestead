using System;
using System.Collections.Generic;
using UnityEngine;

namespace Homestead;

internal sealed class ZoneBlueprintStorePurchaseEscrow
{
    private readonly Component _source;
    private readonly Func<IEnumerable<ZoneBlueprintRequirement>> _requirementsFactory;
    private readonly Func<string, int> _getDeposited;
    private readonly Action<ZoneBlueprintRequirement, int> _addDeposit;

    public ZoneBlueprintStorePurchaseEscrow(
        Component source,
        Func<IEnumerable<ZoneBlueprintRequirement>> requirementsFactory,
        Func<string, int> getDeposited,
        Action<ZoneBlueprintRequirement, int> addDeposit)
    {
        _source = source;
        _requirementsFactory = requirementsFactory;
        _getDeposited = getDeposited;
        _addDeposit = addDeposit;
    }

    public bool HasAllRequired(out string deposited)
    {
        return CreateSession().HasAllRequired(out deposited);
    }

    public int AcceptNeededOnly(Inventory sourceInventory, ItemDrop.ItemData item, int requestedAmount)
    {
        return CreateSession().AcceptNeededOnly(sourceInventory, item, requestedAmount);
    }

    public int AcceptAllNeeded(Inventory sourceInventory, Func<ItemDrop.ItemData, bool>? skip = null)
    {
        return CreateSession().AcceptAllNeeded(sourceInventory, skip);
    }

    public int PullNearbyContainers()
    {
        ZoneMaterialEscrow.Session session = CreateSession();
        return AzuCraftyBoxesCompat.PullMissingMaterials(_source, session.GetMissingRequirements(), (requirement, amount) => session.AcceptPulled(requirement, amount));
    }

    public ZoneMaterialEscrow.AbsorbResult AbsorbUnexpectedInventoryItems(Inventory inventory, Vector3 dropPosition, bool preferInventory)
    {
        return CreateSession().AbsorbUnexpectedInventoryItems(inventory, dropPosition, preferInventory);
    }

    public List<ZoneBlueprintRequirement> GetMissingRequirements()
    {
        return CreateSession().GetMissingRequirements();
    }

    public string FormatDeposited()
    {
        return CreateSession().FormatDeposited();
    }

    private ZoneMaterialEscrow.Session CreateSession()
    {
        return new ZoneMaterialEscrow.Session(_requirementsFactory(), _getDeposited, _addDeposit);
    }
}
