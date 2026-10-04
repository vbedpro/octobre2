using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class TruckController : MonoBehaviour
{
    [Header("Path")]
    public Transform[] waypoints;     // ordered: [0] = parked at farm, last = end of road
    public float moveSpeed = 5f;
    public float turnSpeed = 10f;

    [Header("Behaviour")]
    public float pauseAtEnd = 0f;     // optional wait at the end before disappearing

    private Vector3 homePosition;
    private Quaternion homeRotation;
    private bool isMoving = false;

    // True for a clone spawned because the "main" truck for this planet
    // was already busy on a delivery. Clones fully destroy themselves
    // when their route finishes (or on a reset), instead of parking.
    private bool isTemporary = false;

    // Every live TruckController (main trucks AND temporary clones,
    // across all planets) registers here so BigBang / planet-switch can
    // reset all of them in one call, regardless of which planet they
    // belong to or whether their GameObject is currently active.
    private static readonly List<TruckController> activeInstances = new List<TruckController>();

    // All renderers on this truck (and its children) — toggled on/off so
    // it stops drawing/costing GPU time while parked, WITHOUT disabling
    // the GameObject itself (which would drop it out of activeInstances
    // via OnDisable and break ResetAllTrucks() finding it).
    private Renderer[] renderers;

    void OnEnable()
    {
        if (!activeInstances.Contains(this))
            activeInstances.Add(this);
    }

    void OnDisable()
    {
        activeInstances.Remove(this);
    }

    void Awake()
    {
        homePosition = transform.position;
        homeRotation = transform.rotation;

        renderers = GetComponentsInChildren<Renderer>(true);
        SetVisible(false); // starts parked/hidden until the first Dispatch()
    }

    private void SetVisible(bool visible)
    {
        if (renderers == null) return;
        for (int i = 0; i < renderers.Length; i++)
            if (renderers[i] != null) renderers[i].enabled = visible;
    }

    // Lets an external script (PlanetManager) force-set the "home"
    // position/rotation this truck snaps back to after every delivery —
    // needed because Awake() only runs once, whenever this object first
    // becomes active. If that happened before PlanetManager applied a
    // custom rotation (e.g. the object started active in the scene, or
    // this planet was visited once already), the cached home transform
    // would silently keep reverting to the ORIGINAL wrong rotation after
    // every trip, no matter what the object's live transform was set to
    // afterward. Call this any time after changing transform.rotation
    // directly to keep them in sync.
    public void SetHomeTransform(Vector3 position, Quaternion rotation)
    {
        homePosition = position;
        homeRotation = rotation;
        transform.position = position;
        transform.rotation = rotation;
    }

    // Call this when the player sells rabbits.
    public void Dispatch()
    {
        if (waypoints == null || waypoints.Length == 0) return;

        _ = GuildManager.Instance?.ReportContestProgressAsync("SendTrucks", 1);

        if (isMoving)
        {
            // This truck is already mid-delivery. Instead of dropping the
            // new batch (old behaviour: max 1 truck on the road at a
            // time), spawn a temporary duplicate that runs the same route
            // and destroys itself when done, so every sale always gets
            // its own truck.
            TruckController clone = Instantiate(this, transform.parent);
            clone.isTemporary = true;
            clone.SetHomeTransform(homePosition, homeRotation);
            clone.isMoving = false;
            clone.StopAllCoroutines();
            clone.StartCoroutine(clone.DeliveryRoute());
            return;
        }

        StopAllCoroutines();
        StartCoroutine(DeliveryRoute());
    }

    IEnumerator DeliveryRoute()
    {
        isMoving = true;
        SetVisible(true);

        // Drive forward to the end of the road.
        for (int i = 0; i < waypoints.Length; i++)
            yield return DriveTo(waypoints[i].position);

        if (pauseAtEnd > 0f)
            yield return new WaitForSeconds(pauseAtEnd);

        if (isTemporary)
        {
            Destroy(gameObject);
            yield break;
        }

        // Snap instantly back to the parking spot (hidden under the garage).
        transform.position = homePosition;
        transform.rotation = homeRotation;
        SetVisible(false);

        isMoving = false;
    }

    IEnumerator DriveTo(Vector3 target)
    {
        while (Vector3.Distance(transform.position, target) > 0.05f)
        {
            transform.position = Vector3.MoveTowards(
                transform.position, target, moveSpeed * Time.deltaTime);

            Vector3 dir = target - transform.position;
            if (dir != Vector3.zero)
            {
                Quaternion look = Quaternion.LookRotation(dir);
                transform.rotation = Quaternion.Slerp(
                    transform.rotation, look, turnSpeed * Time.deltaTime);
            }
            yield return null;
        }
    }

    // Called on BigBang and on every planet switch. Main trucks (one per
    // planet, permanent scene objects) are stopped mid-route and snapped
    // back to their parking spot. Temporary clones (spawned by Dispatch()
    // above for overlapping deliveries) are destroyed outright — there's
    // nothing to "park" them at since they don't belong to a garage slot.
    // This fixes trucks getting stuck reversing/frozen after a BigBang or
    // a fast planet switch mid-delivery.
    public static void ResetAllTrucks()
    {
        // Iterate backwards: destroying a temporary clone removes it from
        // activeInstances (via OnDisable) while we're still looping.
        for (int i = activeInstances.Count - 1; i >= 0; i--)
        {
            TruckController t = activeInstances[i];
            if (t == null) continue;

            if (t.isTemporary)
            {
                Destroy(t.gameObject);
                continue;
            }

            t.StopAllCoroutines();
            t.isMoving = false;
            t.transform.position = t.homePosition;
            t.transform.rotation = t.homeRotation;
            t.SetVisible(false);
        }
    }
}
