using UnityEngine;

public class HutSlotGizmo : MonoBehaviour
{
    public Color gizmoColor = Color.yellow;
    public float radius = 1f;

    void OnDrawGizmos()
    {
        Gizmos.color = gizmoColor;
        Gizmos.DrawWireSphere(transform.position, radius);
        Gizmos.DrawIcon(transform.position, "d_BuildSettings.iPhone.Small", true);
    }
}