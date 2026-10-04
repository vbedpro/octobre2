using UnityEngine;

public class RabbitMover : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 2f;
    public float turnSpeed = 200f;        // degrees per second (lower = wider, lazier turns)
    public float arrivalDistance = 0.2f;  // how close before switching to the next waypoint
    public Transform[] waypoints;
    public RabbitHut targetHut;

    [Header("Path Randomness (auto-randomized per rabbit)")]
    public float maxLaneOffset = 0.6f;    // how far sideways each rabbit can walk
    public float wiggleAmplitude = 0.15f; // size of the side-to-side wiggle
    public float wiggleFrequency = 2f;    // speed of the wiggle

    [Header("Safety")]
    [Tooltip("Rabbit is force-destroyed after this many seconds to prevent stuck rabbits.")]
    public float maxLifetime = 30f;

    private int currentWaypoint = 0;
    private float laneOffset;
    private float wigglePhase;
    private float speedMul;
    private float freqMul;
    private float lifetime = 0f;

    void Start()
    {
        laneOffset  = Random.Range(-maxLaneOffset, maxLaneOffset);
        wigglePhase = Random.Range(0f, Mathf.PI * 2f);
        freqMul     = Random.Range(0.8f, 1.2f);
        speedMul    = Random.Range(0.9f, 1.1f);
    }

    void Update()
    {
        // Auto-destroy stuck rabbits.
        lifetime += Time.deltaTime;
        if (lifetime >= maxLifetime)
        {
            // IMPORTANT: a rabbit that gets stuck (e.g. running in circles)
            // must still be counted as owned. Previously this just released
            // the reserved spot and destroyed the rabbit uncounted, which
            // silently desyncs "hatched" vs "owned" counts. Harmless most of
            // the time, but during the tutorial's mandatory 5-hatch step it
            // could permanently soft-lock the player at the Sell Rabbits step
            // (hatched 5, owns 4, can never reach the minimum-5 sell gate).
            // Fix: force the rabbit into its target hut (counts normally)
            // instead of vanishing it. It just "teleports in" instead of
            // walking in.
            if (targetHut != null)
            {
                EnterHut();
            }
            else
            {
                // No hut reference at all (shouldn't normally happen) —
                // nothing to credit it to, so just clean up.
                Destroy(gameObject);
            }
            return;
        }

        if (waypoints == null || waypoints.Length == 0) return;
        if (currentWaypoint >= waypoints.Length) return;

        Transform target = waypoints[currentWaypoint];

        // FINAL segment (heading to the last waypoint = the door):
        // ignore steering, lane and wiggle entirely — go straight to the
        // actual waypoint position so every rabbit lines up with the door.
        bool finalSegment = (currentWaypoint >= waypoints.Length - 1);
        if (finalSegment)
        {
            Vector3 doorTarget = new Vector3(target.position.x, transform.position.y, target.position.z);

            Vector3 toDoor = doorTarget - transform.position;
            if (toDoor.sqrMagnitude > 0.0001f)
            {
                Quaternion rot = Quaternion.LookRotation(toDoor);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, rot, turnSpeed * 2f * Time.deltaTime);
            }

            transform.position = Vector3.MoveTowards(transform.position, doorTarget, moveSpeed * speedMul * Time.deltaTime);

            if (Vector3.Distance(transform.position, doorTarget) < arrivalDistance)
            {
                currentWaypoint++;
                if (currentWaypoint >= waypoints.Length)
                    EnterHut();
            }
            return;
        }

        float lane   = laneOffset;
        float wiggle = Mathf.Sin(Time.time * wiggleFrequency * freqMul + wigglePhase) * wiggleAmplitude;

        // direction to the waypoint (flattened)
        Vector3 toTarget = target.position - transform.position;
        toTarget.y = 0f;
        Vector3 flatDir = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : transform.forward;

        // sideways vector for lane + wiggle
        Vector3 side = Vector3.Cross(Vector3.up, flatDir);
        Vector3 aim = target.position + side * (lane + wiggle);

        // steer gradually toward the aim point
        Vector3 dir = aim - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.0001f)
        {
            Quaternion targetRot = Quaternion.LookRotation(dir);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, turnSpeed * Time.deltaTime);
        }

        // move along current facing so turns curve instead of snapping
        transform.position += transform.forward * (moveSpeed * speedMul) * Time.deltaTime;

        // advance when close to the actual waypoint
        float reach = arrivalDistance + Mathf.Abs(laneOffset);
        if (Vector3.Distance(transform.position, target.position) < reach)
        {
            currentWaypoint++;
        }
    }

    void EnterHut()
    {
        if (targetHut != null)
            targetHut.RabbitEntered();
        Destroy(gameObject);
    }
}
