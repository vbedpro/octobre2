using UnityEngine;

public class RabbitHut : MonoBehaviour
{
    [Header("Capacity")]
    public long maxCapacity = 100;
    private long currentRabbits = 0;
    private long reservedSpots = 0;

    [Header("Reproduction")]
    private bool reproductionUnlocked = false;

    // rabbitsPerSecond set by UpgradeManager.
    // The interval between ticks = 1 / rabbitsPerSecond.
    // Always produces exactly 1 rabbit per tick so the counter never skips.
    [HideInInspector] public float rabbitsPerTick = 0f;
    private float reproductionTimer = 0f;

    [Header("Waypoints")]
    public Transform[] waypoints;

    [Header("Spawning")]
    public GameObject rabbitPrefab;
    public Transform exitPoint;

    // TEMP DIAGNOSTIC — helps pin down the "reproduction randomly stops"
    // bug. Logs (once every 5s per hut) exactly which condition is
    // blocking reproduction whenever it's blocked despite having 2+
    // rabbits. Remove once the root cause is found.
    private float debugLogTimer = 0f;

    void Update()
    {
        if (currentRabbits >= 2)
        {
            debugLogTimer += Time.deltaTime;
            if (debugLogTimer >= 5f)
            {
                debugLogTimer = 0f;
                if (!reproductionUnlocked)
                    Debug.Log($"[ReproDebug] {name}: BLOCKED — reproductionUnlocked is false. currentRabbits={currentRabbits}");
                else if (rabbitsPerTick <= 0f)
                    Debug.Log($"[ReproDebug] {name}: BLOCKED — rabbitsPerTick={rabbitsPerTick}. currentRabbits={currentRabbits}");
                else if (IsFull())
                    Debug.Log($"[ReproDebug] {name}: BLOCKED — IsFull() true. currentRabbits={currentRabbits}, reservedSpots={reservedSpots}, maxCapacity={maxCapacity}");
            }
        }

        if (!reproductionUnlocked) return;
        if (rabbitsPerTick <= 0f) return;
        if (currentRabbits < 2) return;
        if (IsFull()) return;

        float interval = 1f / rabbitsPerTick;
        reproductionTimer += Time.deltaTime;

        // Compute how many ticks fired using direct math instead of a
        // once-per-tick while loop. The old loop was capped at 100,000
        // iterations per frame as a safety net, but once rabbitsPerTick
        // got high enough (very achievable with stacked buffs), that cap
        // was being hit EVERY SINGLE FRAME, for EVERY hut — hundreds of
        // thousands of loop iterations 60 times a second, tanking FPS.
        // This computes the exact same result in O(1), regardless of
        // whether rabbitsPerTick is 10 or 10 million.
        long ticksAvailable = (long)(reproductionTimer / interval);
        long roomLeft = System.Math.Max(0, maxCapacity - currentRabbits);
        long fired = System.Math.Min(ticksAvailable, roomLeft);

        if (fired > 0)
        {
            reproductionTimer -= fired * interval;
            currentRabbits += fired;
        }
        else if (ticksAvailable > 0)
        {
            // Hut is full — consume the timer anyway so it doesn't build
            // up an unbounded backlog while waiting for room to free up.
            reproductionTimer -= ticksAvailable * interval;
        }

        if (fired > 0)
        {
            GameManager.Instance.AddRabbitsSilent(fired);

            // NOTE: intentionally NOT reporting to QuestManager here.
            // The "Hatch Rabbits" quest tracks rabbits manually hatched via
            // the hold-to-hatch button (see BarnSpawner.SpawnRabbit), not
            // passive hut reproduction. Reproduction can fire hundreds or
            // thousands of times in a single frame at high rates, which
            // would instantly blow past the quest's target and complete it
            // before the player ever saw the ActiveQuestButton appear.
        }
    }

    // Fox damage reservation — tracks how many rabbits incoming foxes will remove.
    private long reservedFoxDamage = 0;

    public void ReserveFoxDamage(long amount)
    {
        reservedFoxDamage += amount;
    }

    public void ReleaseFoxDamage(long amount)
    {
        reservedFoxDamage = System.Math.Max(0, reservedFoxDamage - amount);
    }

    public void ClearFoxDamage()
    {
        reservedFoxDamage = 0;
    }

    // Rabbits available after accounting for incoming fox damage.
    public long GetEffectiveRabbits()
    {
        return System.Math.Max(0, currentRabbits - reservedFoxDamage);
    }

    public void ReserveSpot()
    {
        reservedSpots++;
    }

    public void ReleaseReservedSpot()
    {
        reservedSpots = System.Math.Max(0, reservedSpots - 1);
    }

    public void RabbitEntered()
    {
        reservedSpots = System.Math.Max(0, reservedSpots - 1);
        if (currentRabbits < maxCapacity)
        {
            currentRabbits++;
            GameManager.Instance.AddRabbits(1);
        }
    }

    public void RemoveRabbits(long amount)
    {
        currentRabbits = System.Math.Max(0, currentRabbits - amount);
        reservedSpots = 0;
    }

    public void UnlockReproduction()
    {
        if (!reproductionUnlocked)
        {
            reproductionUnlocked = true;
            reproductionTimer = 0f;
        }
    }

    // Called by SaveManager to restore this hut's population from a saved
    // game. Bypasses the normal entry/reproduction flow — sets the raw
    // count directly, clamped to current capacity.
    public void SetCurrentRabbits(long amount)
    {
        currentRabbits = System.Math.Clamp(amount, 0, maxCapacity);
    }

    // Called once by SaveManager on load to compute how many rabbits this
    // hut would have produced while the game was closed — same rate and
    // capacity rules as real-time reproduction (Update() above), just
    // computed in one shot for the elapsed offline duration instead of
    // ticking frame by frame. Does NOT modify state — call
    // ApplyOfflineEarnings() separately with the returned amount.
    public long CalculateOfflineEarnings(float elapsedSeconds)
    {
        return CalculateOfflineEarnings(elapsedSeconds, rabbitsPerTick);
    }

    // Overload with an explicit rate — used for a dual-segment offline
    // calc (Explosive Reproduction boosted rate for part of the window,
    // normal rate for the rest). Capacity is respected per-call, so call
    // this in chronological order and apply each result before calling
    // again, same as CalculateOfflineEarnings/ApplyOfflineEarnings normally.
    public long CalculateOfflineEarnings(float elapsedSeconds, float rateOverride)
    {
        if (!reproductionUnlocked || rateOverride <= 0f) return 0;
        if (currentRabbits < 2) return 0; // matches Update()'s own "need 2+ to breed" gate — a hut can't produce offline rabbits it couldn't have produced live
        long potential = (long)(rateOverride * elapsedSeconds);
        long remainingCapacity = System.Math.Max(0, maxCapacity - currentRabbits);
        return System.Math.Min(potential, remainingCapacity);
    }

    // Directly adds rabbits to this hut's count (clamped to capacity),
    // bypassing the normal per-tick production flow — used for applying
    // the result of CalculateOfflineEarnings() in one shot.
    public void ApplyOfflineEarnings(long amount)
    {
        currentRabbits = System.Math.Clamp(currentRabbits + amount, 0, maxCapacity);
    }

    public bool IsFull() { return (currentRabbits + reservedSpots) >= maxCapacity; }
    public long GetCurrentRabbits() { return currentRabbits; }
    public long GetMaxCapacity() { return maxCapacity; }
}
