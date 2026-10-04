using System.Collections.Generic;

// Plain data container — NOT a MonoBehaviour, NOT attached to any
// GameObject. Holds everything that needs to be independent PER PLANET.
// PlanetManager will hold one of these per unlocked planet (Earth always
// exists; Moon/Venus/Mars/Pluto get created the moment they're unlocked).
//
// Deliberately excludes anything that stays GLOBAL across all planets:
//   - lifetimeRabbits (drives the leaderboard — sums across every planet,
//     never resets, doesn't belong to any single one)
//   - totalCarrots (currency for Epic upgrades — shared, not per-farm)
//   - prestigeCount / BigBang count (global, per your spec)
//   - Epic upgrade levels (EpicUpgradeManager — permanent, global)
//
// Everything else that currently lives in GameManager/UpgradeManager/
// SellManager as a single flat value needs a per-planet copy here, since
// each planet's farm progresses independently.
[System.Serializable]
public class PlanetData
{
    public PlanetManager.Planet planet;

    // ── Resources (per-planet — this is what the top HUD shows when this
    //    planet is the active one) ──────────────────────────────────────
    public long totalRabbits = 0;
    public long totalCoins = 0;

    // ── Huts ─────────────────────────────────────────────────────────
    // Mirrors SaveManager's existing approach: parallel arrays, ordered
    // to match whatever this planet's own UpgradeManager.huts[]-equivalent
    // array is (index 0 = first hut, etc).
    public int hutCount = 1;
    public int[] hutLevel;      // one entry per hut slot on this planet
    public long[] hutRabbits;   // current population per hut slot

    // ── Common shop upgrades ─────────────────────────────────────────
    // Same name-keyed approach SaveManager already uses for Earth, so the
    // exact same save/load code can be reused per-planet later. Each
    // planet has its OWN independent set of levels — a freshly unlocked
    // Moon starts with every common upgrade back at level 0, per your spec.
    public List<string> commonUpgradeId = new List<string>();
    public List<int> commonUpgradeLevel = new List<int>();

    // ── Truck ────────────────────────────────────────────────────────
    // Each planet always has its OWN truck timer — this never gets
    // shared/synced with Earth's or any other planet's truck, per your spec.
    //
    // Stored as an ABSOLUTE REAL-WORLD timestamp (Unix seconds, UTC) —
    // NOT Time.time, which resets to 0 every time the app launches and
    // would make a saved cooldown meaningless across a quit/relaunch.
    // This is what lets the cooldown correctly keep counting down both
    // within a session (switching planets) AND across app restarts
    // (quit with 30s left, come back later, still ~30s left minus real
    // elapsed time). A value <= "now" means the truck is ready.
    public double truckReadyAtUnixSeconds = 0;

    // ── Quest buffs/debuffs ──────────────────────────────────────────
    // Percent deltas accumulated from completed/failed quests whose "home
    // planet" was this one — mirrors the same accumulator pattern
    // HutUpgradeManager/UpgradeManager already use globally (ApplyQuest…Delta),
    // just kept as one independent set per planet instead of one shared set.
    // A quest ALWAYS applies its buff/debuff to its home planet's numbers
    // here, even if the player isn't currently standing on that planet.
    public float questHutCapacityBonusPercent = 0f;
    public float questTruckCapacityBonusPercent = 0f;
    public float questReproductionBonusPercent = 0f;
    public float questHatchRateBonusPercent = 0f;

    // Convenience constructor for creating a brand-new planet's data the
    // moment it's unlocked — everything starts completely fresh.
    public PlanetData(PlanetManager.Planet planet, int hutSlotCount)
    {
        this.planet = planet;
        totalRabbits = 0;
        totalCoins = 0;
        hutCount = 1;
        hutLevel = new int[hutSlotCount];
        hutRabbits = new long[hutSlotCount];
        for (int i = 0; i < hutSlotCount; i++)
        {
            hutLevel[i] = 1;
            hutRabbits[i] = 0;
        }
        commonUpgradeId = new List<string>();
        commonUpgradeLevel = new List<int>();
        truckReadyAtUnixSeconds = 0; // 0 is always in the past, so a fresh planet's truck starts ready
    }
}
