using System.Collections.Generic;
using UnityEngine;

// Generic item inventory — tracks WHAT the player owns and HOW MANY,
// without knowing or caring what an item actually does. Boosts today,
// cosmetics later. Purchasing a boost (BoostRow/AdBoostRow) just calls
// AddItem() here — it does NOT apply any effect. Actually using/equipping
// an item, and what that means, is entirely up to whatever UI/system reads
// from this inventory later (the Boosts inventory panel, the Cosmetics
// panel, etc.).
//
// Cosmetics note: this uses a stackable int-count model, which fits boosts
// naturally (own 3 of the same boost). Cosmetics are normally one-off
// unlocks + an equipped/unequipped state rather than a stackable count —
// when the Cosmetics system is built, it can either still use AddItem()
// here (with count meaning "owned", always 0 or 1) plus a separate small
// "which cosmetics are currently equipped" set living alongside it, or in
// its own script. Revisit this once cosmetics are actually being built.
public class InventoryManager : MonoBehaviour
{
    public static InventoryManager Instance { get; private set; }

    // itemId -> quantity owned. itemId is just a free-form string key you
    // choose per boost/cosmetic (e.g. "Boost_DoubleCoins10Min",
    // "Cosmetic_GoldenHat") — nothing here enforces what IDs exist.
    private Dictionary<string, int> itemCounts = new Dictionary<string, int>();

    // Fired whenever any item's count changes, so UI panels can refresh
    // without polling every frame. Passes the itemId that changed.
    public event System.Action<string> OnInventoryChanged;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    public int GetCount(string itemId)
    {
        return itemCounts.TryGetValue(itemId, out int count) ? count : 0;
    }

    public bool HasItem(string itemId)
    {
        return GetCount(itemId) > 0;
    }

    // Hard cap per item — applies to every source (carrot purchase, ad
    // reward, achievement rewards later, etc.), not just one code path.
    public const int MaxStackPerItem = 99;

    public void AddItem(string itemId, int amount = 1)
    {
        if (string.IsNullOrEmpty(itemId) || amount <= 0) return;

        int newCount = Mathf.Min(GetCount(itemId) + amount, MaxStackPerItem);
        itemCounts[itemId] = newCount;
        OnInventoryChanged?.Invoke(itemId);
    }

    // Returns true if the item was actually consumed (i.e. the player had
    // enough). Returns false (and changes nothing) otherwise.
    public bool ConsumeItem(string itemId, int amount = 1)
    {
        if (string.IsNullOrEmpty(itemId) || amount <= 0) return false;

        int current = GetCount(itemId);
        if (current < amount) return false;

        int remaining = current - amount;
        if (remaining <= 0)
            itemCounts.Remove(itemId);
        else
            itemCounts[itemId] = remaining;

        OnInventoryChanged?.Invoke(itemId);
        return true;
    }

    // Every distinct item currently owned (count > 0). Useful for the
    // inventory panel to know what rows to display.
    public IReadOnlyDictionary<string, int> GetAllItems()
    {
        return itemCounts;
    }

    // ---- Save/Load (mirrors AchievementsManager's GetSaveData/RestoreSaveData pattern) ----

    [System.Serializable]
    public class InventorySaveEntry
    {
        public string itemId;
        public int count;
    }

    public List<InventorySaveEntry> GetSaveData()
    {
        List<InventorySaveEntry> result = new List<InventorySaveEntry>();
        foreach (KeyValuePair<string, int> kvp in itemCounts)
        {
            result.Add(new InventorySaveEntry { itemId = kvp.Key, count = kvp.Value });
        }
        return result;
    }

    public void RestoreSaveData(List<InventorySaveEntry> saved)
    {
        itemCounts.Clear();
        if (saved == null) return;

        foreach (InventorySaveEntry entry in saved)
        {
            if (!string.IsNullOrEmpty(entry.itemId) && entry.count > 0)
                itemCounts[entry.itemId] = entry.count;
        }
    }
}
