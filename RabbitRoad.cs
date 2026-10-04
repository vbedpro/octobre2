using UnityEngine;
using System.Collections.Generic;

public class RabbitRoad : MonoBehaviour
{
    public Transform[] waypoints;
    public float roadWidth = 1f;
    public Material roadMaterial;
    public float uvTileLength = 1f;

    [Header("Smoothing")]
    public int smoothingSegments = 8; // points generated between each pair of waypoints

    void Start()
    {
        if (waypoints == null || waypoints.Length < 2) return;

        List<Vector3> path = BuildSmoothPath();

        Mesh mesh = new Mesh();
        List<Vector3> verts = new List<Vector3>();
        List<Vector2> uvs = new List<Vector2>();
        List<int> tris = new List<int>();

        float distanceAlong = 0f;

        for (int i = 0; i < path.Count; i++)
        {
            Vector3 worldPoint = path[i] + Vector3.up * 0.01f;

            Vector3 dir;
            if (i == 0)
                dir = (path[1] - path[0]).normalized;
            else if (i == path.Count - 1)
                dir = (path[i] - path[i - 1]).normalized;
            else
            {
                Vector3 dirIn = (path[i] - path[i - 1]).normalized;
                Vector3 dirOut = (path[i + 1] - path[i]).normalized;
                dir = (dirIn + dirOut).normalized;
            }

            Vector3 side = Vector3.Cross(Vector3.up, dir).normalized * (roadWidth / 2f);

            Vector3 worldLeft = worldPoint - side;
            Vector3 worldRight = worldPoint + side;

            verts.Add(transform.InverseTransformPoint(worldLeft));
            verts.Add(transform.InverseTransformPoint(worldRight));

            float v = distanceAlong / uvTileLength;
            uvs.Add(new Vector2(0, v));
            uvs.Add(new Vector2(1, v));

            if (i < path.Count - 1)
                distanceAlong += Vector3.Distance(path[i], path[i + 1]);

            if (i > 0)
            {
                int baseIndex = (i - 1) * 2;

                tris.Add(baseIndex);
                tris.Add(baseIndex + 2);
                tris.Add(baseIndex + 1);

                tris.Add(baseIndex + 1);
                tris.Add(baseIndex + 2);
                tris.Add(baseIndex + 3);
            }
        }

        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        MeshFilter mf = gameObject.GetComponent<MeshFilter>();
        if (mf == null) mf = gameObject.AddComponent<MeshFilter>();
        mf.mesh = mesh;

        MeshRenderer mr = gameObject.GetComponent<MeshRenderer>();
        if (mr == null) mr = gameObject.AddComponent<MeshRenderer>();
        if (roadMaterial != null) mr.material = roadMaterial;

        LineRenderer lr = GetComponent<LineRenderer>();
        if (lr != null) lr.enabled = false;
    }

    // Catmull-Rom spline through the waypoints
    List<Vector3> BuildSmoothPath()
    {
        List<Vector3> points = new List<Vector3>();
        foreach (Transform wp in waypoints)
            points.Add(wp.position);

        if (points.Count < 3 || smoothingSegments < 1)
            return points;

        List<Vector3> smooth = new List<Vector3>();

        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector3 p0 = points[Mathf.Max(i - 1, 0)];
            Vector3 p1 = points[i];
            Vector3 p2 = points[i + 1];
            Vector3 p3 = points[Mathf.Min(i + 2, points.Count - 1)];

            int segments = (i == points.Count - 2) ? smoothingSegments + 1 : smoothingSegments;

            for (int s = 0; s < segments; s++)
            {
                float t = s / (float)smoothingSegments;
                smooth.Add(CatmullRom(p0, p1, p2, p3, t));
            }
        }

        return smooth;
    }

    Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;

        return 0.5f * (
            (2f * p1) +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
    }
}