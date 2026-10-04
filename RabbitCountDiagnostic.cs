using UnityEngine;

// TEMP DIAGNOSTIC — helps pin down the "total rabbits way higher than
// hut capacity should allow" bug. Every few seconds, sums every ACTIVE
// hut's own currentRabbits and compares it against GameManager.totalRabbits
// (what the player sees displayed). If they ever meaningfully diverge,
// logs exactly how much and by when — this tells us whether the huts are
// under-counting, the total is over-counting, or (most useful) exactly
// WHEN the gap first appears, which points at which system caused it.
// Remove once the root cause is found.
public class RabbitCountDiagnostic : MonoBehaviour
{
    public float checkInterval = 5f;
    private float timer = 0f;
    private long lastKnownGap = 0;

    void Start()
    {
        timer = checkInterval; // fire an immediate check on the very first frame instead of waiting
    }

    void Update()
    {
        timer += Time.deltaTime;
        if (timer < checkInterval) return;
        timer = 0f;

        if (GameManager.Instance == null) return;

        long hutSum = 0;
        RabbitHut[] huts = FindObjectsByType<RabbitHut>(FindObjectsInactive.Exclude);
        foreach (RabbitHut hut in huts)
            hutSum += hut.GetCurrentRabbits();

        long total = GameManager.Instance.totalRabbits;
        long gap = total - hutSum;

        // Always log the current state so we have a running trail.
        Debug.Log($"[RabbitDiag] hutSum={hutSum:N0}  totalRabbits={total:N0}  gap={gap:N0}  activeHutCount={huts.Length}");

        if (gap != lastKnownGap)
        {
            Debug.LogWarning($"[RabbitDiag] GAP CHANGED: {lastKnownGap:N0} -> {gap:N0} (delta {gap - lastKnownGap:N0}) — whatever just happened in the last {checkInterval}s is likely the cause.");
            lastKnownGap = gap;

            // Per-hut breakdown, only printed when the gap actually
            // changes (so it doesn't spam every 5s) — tells us whether
            // huts are stuck near-zero, or already sitting at their
            // (possibly wrong) capacity.
            foreach (RabbitHut hut in huts)
                Debug.Log($"[RabbitDiag]   hut '{hut.gameObject.name}': current={hut.GetCurrentRabbits():N0} / max={hut.GetMaxCapacity():N0}");
        }
    }
}
