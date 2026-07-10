using System;
using System.Collections.Generic;
using System.Linq;
using Jotunn.Managers;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Homestead;

internal static class ZoneMaterialEscrow
{
    public static int AddAmountsSaturating(int left, int right)
    {
        long total = (long)Math.Max(0, left) + Math.Max(0, right);
        return total >= int.MaxValue ? int.MaxValue : (int)total;
    }

    public sealed class Session
    {
        private readonly List<ZoneBlueprintRequirement> _requirements;
        private readonly Dictionary<string, ZoneBlueprintRequirement> _requirementsByItemName;
        private readonly Func<string, int> _getDeposited;
        private readonly Action<ZoneBlueprintRequirement, int> _acceptMaterial;

        public Session(
            IEnumerable<ZoneBlueprintRequirement> requirements,
            Func<string, int> getDeposited,
            Action<ZoneBlueprintRequirement, int> acceptMaterial)
        {
            _requirements = NormalizeRequirements(requirements);
            _requirementsByItemName = _requirements.ToDictionary(item => item.ItemName, StringComparer.Ordinal);
            _getDeposited = getDeposited;
            _acceptMaterial = acceptMaterial;
        }

        public int AcceptNeededOnly(Inventory sourceInventory, ItemDrop.ItemData item, int requestedAmount)
        {
            if (!TryGetAllowedRequirement(item, requestedAmount, out ZoneBlueprintRequirement requirement, out int allowed))
            {
                return 0;
            }

            int take = TakeAllowedAmount(sourceInventory, item, requestedAmount, allowed);
            if (take <= 0)
            {
                return 0;
            }

            _acceptMaterial(requirement, take);
            return take;
        }

        public int AcceptAllNeeded(Inventory sourceInventory, Func<ItemDrop.ItemData, bool>? skip = null)
        {
            if (sourceInventory == null)
            {
                return 0;
            }

            int accepted = 0;
            foreach (ItemDrop.ItemData item in sourceInventory.GetAllItems().ToList())
            {
                if (skip?.Invoke(item) == true)
                {
                    continue;
                }

                accepted = AddAmountsSaturating(accepted, AcceptNeededOnly(sourceInventory, item, item.m_stack));
            }

            return accepted;
        }

        public int AcceptPulled(ZoneBlueprintRequirement requirement, int amount)
        {
            if (requirement == null || amount <= 0)
            {
                return 0;
            }

            if (!_requirementsByItemName.TryGetValue(requirement.ItemName, out ZoneBlueprintRequirement target))
            {
                return 0;
            }

            int remaining = target.Amount - Mathf.Max(0, _getDeposited(target.ItemName));
            int accepted = Mathf.Min(Mathf.Max(0, amount), Mathf.Max(0, remaining));
            if (accepted <= 0)
            {
                return 0;
            }

            _acceptMaterial(target, accepted);
            return accepted;
        }

        public AbsorbResult AbsorbUnexpectedInventoryItems(Inventory inventory, Vector3 dropPosition, bool preferInventory)
        {
            if (inventory == null)
            {
                return default;
            }

            bool changed = false;
            int acceptedTotal = 0;
            int returnedTotal = 0;
            foreach (ItemDrop.ItemData item in inventory.GetAllItems().ToList())
            {
                int beforeStack = item.m_stack;
                int accepted = AcceptNeededOnly(inventory, item, beforeStack);
                acceptedTotal += accepted;

                int leftover = beforeStack - accepted;
                if (leftover > 0)
                {
                    ReturnOrDropFromInventory(inventory, item, leftover, dropPosition, preferInventory);
                    returnedTotal += leftover;
                    changed = true;
                }

                changed |= accepted > 0;
            }

            return new AbsorbResult(changed, acceptedTotal, returnedTotal);
        }

        public List<ZoneBlueprintRequirement> GetMissingRequirements()
        {
            List<ZoneBlueprintRequirement> missing = [];
            foreach (ZoneBlueprintRequirement requirement in _requirements)
            {
                int amount = requirement.Amount - Mathf.Max(0, _getDeposited(requirement.ItemName));
                if (amount <= 0)
                {
                    continue;
                }

                missing.Add(new ZoneBlueprintRequirement
                {
                    ItemName = requirement.ItemName,
                    PrefabName = requirement.PrefabName,
                    DisplayName = requirement.DisplayName,
                    Amount = amount
                });
            }

            return missing;
        }

        public string FormatDeposited()
        {
            List<string> parts = [];
            foreach (ZoneBlueprintRequirement item in _requirements)
            {
                string displayName = Localization.instance != null ? Localization.instance.Localize(item.DisplayName) : item.DisplayName;
                parts.Add($"{displayName}: {_getDeposited(item.ItemName)}/{item.Amount}");
            }

            return parts.Count == 0 ? "No price" : string.Join("\n", parts);
        }

        public bool HasAllRequired(out string deposited)
        {
            deposited = FormatDeposited();
            if (_requirements.Count == 0)
            {
                return false;
            }

            foreach (ZoneBlueprintRequirement requirement in _requirements)
            {
                if (_getDeposited(requirement.ItemName) < requirement.Amount)
                {
                    return false;
                }
            }

            return true;
        }

        private static void ReturnOrDropFromInventory(Inventory inventory, ItemDrop.ItemData item, int amount, Vector3 dropPosition, bool preferInventory)
        {
            if (amount <= 0 || item == null)
            {
                return;
            }

            ItemDrop.ItemData refund = item.Clone();
            if (inventory.ContainsItem(item))
            {
                inventory.RemoveItem(item, amount);
            }

            GiveOrDropItem(refund, amount, dropPosition, preferInventory, item.m_dropPrefab);
        }

        private bool TryGetAllowedRequirement(ItemDrop.ItemData item, int requestedAmount, out ZoneBlueprintRequirement requirement, out int allowed)
        {
            requirement = null!;
            allowed = 0;
            if (item == null || requestedAmount <= 0)
            {
                return false;
            }

            if (!_requirementsByItemName.TryGetValue(item.m_shared.m_name, out requirement))
            {
                return false;
            }

            allowed = requirement.Amount - Mathf.Max(0, _getDeposited(requirement.ItemName));
            return allowed > 0;
        }
    }

    public readonly struct AbsorbResult
    {
        public AbsorbResult(bool changed, int accepted, int returned)
        {
            Changed = changed;
            Accepted = accepted;
            Returned = returned;
        }

        public bool Changed { get; }
        public int Accepted { get; }
        public int Returned { get; }
    }

    public static List<ZoneBlueprintRequirement> NormalizeRequirements(IEnumerable<ZoneBlueprintRequirement> items, int maxTypes = int.MaxValue)
    {
        Dictionary<string, ZoneBlueprintRequirement> result = new(StringComparer.Ordinal);
        foreach (ZoneBlueprintRequirement item in items)
        {
            if (item.Amount <= 0 || string.IsNullOrWhiteSpace(item.ItemName))
            {
                continue;
            }

            if (!result.TryGetValue(item.ItemName, out ZoneBlueprintRequirement aggregate))
            {
                aggregate = new ZoneBlueprintRequirement
                {
                    ItemName = item.ItemName,
                    PrefabName = item.PrefabName,
                    DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? item.ItemName : item.DisplayName
                };
                result[item.ItemName] = aggregate;
            }

            if (string.IsNullOrWhiteSpace(aggregate.PrefabName))
            {
                aggregate.PrefabName = item.PrefabName;
            }

            if (string.IsNullOrWhiteSpace(aggregate.DisplayName))
            {
                aggregate.DisplayName = item.DisplayName;
            }

            aggregate.Amount = AddAmountsSaturating(aggregate.Amount, item.Amount);
        }

        return result.Values
            .OrderBy(item => item.DisplayName, StringComparer.Ordinal)
            .Take(maxTypes)
            .ToList();
    }

    public static List<ZoneBlueprintRequirement> ToRequirements(IEnumerable<ZoneBlueprintStorePriceItem> items, int maxTypes = int.MaxValue)
    {
        return NormalizeRequirements(items.Select(item => new ZoneBlueprintRequirement
        {
            ItemName = item.ItemName,
            PrefabName = item.PrefabName,
            DisplayName = item.DisplayName,
            Amount = item.Amount
        }), maxTypes);
    }

    public static List<ZoneBlueprintStorePriceItem> ToPriceItems(IEnumerable<ZoneBlueprintRequirement> items, int maxTypes = int.MaxValue)
    {
        return NormalizeRequirements(items, maxTypes)
            .Select(item => new ZoneBlueprintStorePriceItem
            {
                ItemName = item.ItemName,
                PrefabName = item.PrefabName,
                DisplayName = item.DisplayName,
                Amount = item.Amount
            })
            .ToList();
    }

    public static List<ZoneBlueprintStorePriceItem> ReadPriceItems(Inventory? inventory, int maxTypes = int.MaxValue)
    {
        if (inventory == null)
        {
            return [];
        }

        List<ZoneBlueprintRequirement> items = [];
        foreach (ItemDrop.ItemData item in inventory.GetAllItems())
        {
            if (item.m_stack <= 0 || item.m_shared.m_questItem || item.m_dropPrefab == null)
            {
                continue;
            }

            items.Add(new ZoneBlueprintRequirement
            {
                ItemName = item.m_shared.m_name,
                PrefabName = Utils.GetPrefabName(item.m_dropPrefab),
                DisplayName = item.m_shared.m_name,
                Amount = item.m_stack
            });
        }

        return ToPriceItems(items, maxTypes);
    }

    public static int GetInventorySignatureHash(Inventory? inventory)
    {
        if (inventory == null)
        {
            return 0;
        }

        unchecked
        {
            int hash = 17;
            int count = 0;
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
            {
                string name = item.m_shared?.m_name ?? "";
                int itemHash = StringComparer.Ordinal.GetHashCode(name);
                itemHash = (itemHash * 397) ^ item.m_stack;
                hash += itemHash;
                hash ^= (itemHash << 7) | (int)((uint)itemHash >> 25);
                count++;
            }

            return (hash * 397) ^ count;
        }
    }

    public static void DrawRequirementOverlay(InventoryGrid grid, IReadOnlyList<ZoneBlueprintRequirement> missing, string tooltipToken)
    {
        if (grid == null || missing.Count == 0)
        {
            return;
        }

        int index = 0;
        foreach (InventoryGrid.Element element in grid.m_elements)
        {
            if (index >= missing.Count)
            {
                break;
            }

            if (element.m_used)
            {
                continue;
            }

            ZoneBlueprintRequirement requirement = missing[index++];
            Sprite? icon = GetRequirementIcon(requirement);
            element.m_used = true;
            element.m_icon.enabled = icon != null;
            element.m_icon.sprite = icon;
            element.m_icon.color = new Color(1f, 1f, 1f, 0.45f);
            element.m_amount.enabled = true;
            element.m_amount.text = requirement.Amount.ToString();
            element.m_quality.enabled = false;
            element.m_equiped.enabled = false;
            element.m_queued.enabled = false;
            element.m_noteleport.enabled = false;
            element.m_food.enabled = false;
            element.m_durability.gameObject.SetActive(false);
            element.m_tooltip.m_topic = Localization.instance.Localize(requirement.DisplayName);
            element.m_tooltip.m_text = HomesteadLocalization.Format(tooltipToken, requirement.Amount);
        }
    }

    private static Sprite? GetRequirementIcon(ZoneBlueprintRequirement requirement)
    {
        GameObject? prefab = FindPrefab(requirement.PrefabName);
        ItemDrop? drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
        return drop != null ? drop.m_itemData.GetIcon() : null;
    }

    private static GameObject? FindPrefab(string prefabName)
    {
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return null;
        }

        return ObjectDB.instance?.GetItemPrefab(prefabName) ??
               ZNetScene.instance?.GetPrefab(prefabName) ??
               PrefabManager.Instance.GetPrefab(prefabName);
    }

    public static int TakeAllowedAmount(Inventory sourceInventory, ItemDrop.ItemData item, int requestedAmount, int remaining)
    {
        if (sourceInventory == null || item == null || requestedAmount <= 0 || remaining <= 0)
        {
            return 0;
        }

        int take = Mathf.Min(requestedAmount, remaining, item.m_stack);
        if (take <= 0 || !sourceInventory.RemoveItem(item, take))
        {
            return 0;
        }

        return take;
    }

    public static bool TrySplitIntoStacks(IEnumerable<ZoneBlueprintStorePriceItem> payoutItems, out List<ZoneBlueprintStorePriceItem> stacks, out string reason)
    {
        stacks = [];
        reason = "";
        foreach (ZoneBlueprintStorePriceItem item in ToPriceItems(ToRequirements(payoutItems)))
        {
            GameObject? prefab = ZoneBlueprintStoreVisuals.FindItemPrefab(item.PrefabName);
            ItemDrop? itemDrop = prefab ? prefab.GetComponent<ItemDrop>() : null;
            if (itemDrop == null)
            {
                reason = $"Missing payout item prefab '{item.PrefabName}'.";
                return false;
            }

            int maxStack = Mathf.Max(1, itemDrop.m_itemData.m_shared.m_maxStackSize);
            int remaining = item.Amount;
            while (remaining > 0)
            {
                int stack = Mathf.Min(remaining, maxStack);
                stacks.Add(new ZoneBlueprintStorePriceItem
                {
                    ItemName = item.ItemName,
                    PrefabName = item.PrefabName,
                    DisplayName = item.DisplayName,
                    Amount = stack
                });
                remaining -= stack;
            }
        }

        if (stacks.Count == 0)
        {
            reason = "No blueprint store payout materials found.";
            return false;
        }

        return true;
    }

    public static bool TryFillInventory(Inventory? inventory, IEnumerable<ZoneBlueprintStorePriceItem> stacks)
    {
        if (inventory == null)
        {
            return false;
        }

        foreach (ZoneBlueprintStorePriceItem stack in stacks)
        {
            GameObject? prefab = ZoneBlueprintStoreVisuals.FindItemPrefab(stack.PrefabName);
            ItemDrop? itemDrop = prefab ? prefab.GetComponent<ItemDrop>() : null;
            if (itemDrop == null || stack.Amount <= 0)
            {
                return false;
            }

            ItemDrop.ItemData item = itemDrop.m_itemData.Clone();
            item.m_stack = stack.Amount;
            item.m_dropPrefab = prefab;
            if (!inventory.AddItem(item))
            {
                return false;
            }
        }

        return true;
    }

    public static void DropAllContents(Inventory? inventory, Vector3 dropPosition)
    {
        if (inventory == null)
        {
            return;
        }

        foreach (ItemDrop.ItemData item in inventory.GetAllItems().ToList())
        {
            int stack = item.m_stack;
            inventory.RemoveItem(item, stack);
            DropItem(item, stack, dropPosition);
        }
    }

    public static void DropPriceItems(IEnumerable<ZoneBlueprintStorePriceItem> priceItems, Vector3 dropPosition)
    {
        foreach (ZoneBlueprintStorePriceItem item in ToPriceItems(ToRequirements(priceItems)))
        {
            GameObject? prefab = ZoneBlueprintStoreVisuals.FindItemPrefab(item.PrefabName);
            ItemDrop? itemDrop = prefab ? prefab.GetComponent<ItemDrop>() : null;
            if (itemDrop == null || item.Amount <= 0)
            {
                continue;
            }

            ItemDrop.ItemData drop = itemDrop.m_itemData.Clone();
            drop.m_dropPrefab = prefab;
            DropItem(drop, item.Amount, dropPosition);
        }
    }

    public static void GiveOrDropItem(ItemDrop.ItemData prototype, int amount, Vector3 dropPosition, bool preferInventory, GameObject? dropPrefab = null)
    {
        int maxStack = Mathf.Max(1, prototype.m_shared.m_maxStackSize);
        int remaining = amount;
        while (remaining > 0)
        {
            int stack = Mathf.Min(remaining, maxStack);
            ItemDrop.ItemData item = prototype.Clone();
            item.m_stack = stack;
            if (item.m_dropPrefab == null && dropPrefab != null)
            {
                item.m_dropPrefab = dropPrefab;
            }

            Inventory? inventory = preferInventory && Player.m_localPlayer != null ? Player.m_localPlayer.GetInventory() : null;
            if (inventory != null && inventory.CanAddItem(item, stack))
            {
                inventory.AddItem(item);
            }
            else if (item.m_dropPrefab != null)
            {
                ItemDrop.DropItem(item, 0, dropPosition + Vector3.up * 0.75f + UnityEngine.Random.insideUnitSphere * 0.25f, UnityEngine.Random.rotation);
            }

            remaining -= stack;
        }
    }

    private static void DropItem(ItemDrop.ItemData prototype, int amount, Vector3 dropPosition)
    {
        if (amount <= 0)
        {
            return;
        }

        ItemDrop.ItemData item = prototype.Clone();
        item.m_stack = amount;
        if (item.m_dropPrefab != null)
        {
            ItemDrop.DropItem(item, 0, dropPosition + Vector3.up * 0.75f, UnityEngine.Random.rotation);
        }
    }
}
