// ============================================================
//  G3MeshBridge.cs
//  Utility chuyển đổi giữa Unity Mesh và geometry3Sharp DMesh3.
//  Yêu cầu: thêm geometry3Sharp vào project (DLL hoặc source),
//           bật Scripting Define Symbol: G3_USING_UNITY
// ============================================================
using System.Collections.Generic;
using UnityEngine;
using g3;

public static class G3MeshBridge
{
    // ── Unity Mesh → DMesh3 ───────────────────────────────────────────────

    /// <summary>
    /// Chuyển Unity Mesh sang DMesh3.
    /// Trả về mảng vertexRemap: vertexRemap[renderVertIdx] = DMesh3 vertId
    /// để caller có thể map lại sim-node sau này.
    /// </summary>
    public static DMesh3 ToDMesh3(Mesh unityMesh, Transform tf,
                                  out int[] vertexRemap,
                                  bool useWorldSpace = true)
    {
        Vector3[] verts  = unityMesh.vertices;
        int[]     tris   = unityMesh.triangles;

        var dmesh = new DMesh3(false, false, false, false); // no normals/colors/uv/groups
        vertexRemap = new int[verts.Length];

        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 wp = useWorldSpace && tf != null
                ? tf.TransformPoint(verts[i])
                : verts[i];
            vertexRemap[i] = dmesh.AppendVertex(new Vector3d(wp.x, wp.y, wp.z));
        }

        for (int t = 0; t < tris.Length; t += 3)
        {
            int a = vertexRemap[tris[t]];
            int b = vertexRemap[tris[t + 1]];
            int c = vertexRemap[tris[t + 2]];
            if (a == b || b == c || a == c) continue;

            var res = dmesh.AppendTriangle(a, b, c);
            if (res < 0)
                Debug.LogWarning($"[G3MeshBridge] Triangle {t/3} bị reject (res={res})");
        }

        return dmesh;
    }

    // ── DMesh3 → Unity Mesh ───────────────────────────────────────────────

    /// <summary>
    /// Xuất DMesh3 ra Unity Mesh (local-space relative to tf nếu cần).
    /// Trả về g3VertIdToNewRenderIdx để caller có thể remap sim-node.
    /// </summary>
    public static Mesh ToUnityMesh(DMesh3 dmesh, Transform tf,
                                   out Dictionary<int, int> g3VertIdToNewRenderIdx,
                                   bool toLocalSpace = true)
    {
        g3VertIdToNewRenderIdx = new Dictionary<int, int>();

        // Compact copy để index space liên tục
        var compact = new DMesh3(dmesh, true);

        var vertices = new List<Vector3>();
        var triangles = new List<int>();

        // Vertex ID trong compact DMesh3 là [0..VertexCount-1]
        foreach (int vid in compact.VertexIndices())
        {
            Vector3d p  = compact.GetVertex(vid);
            Vector3 wp  = new Vector3((float)p.x, (float)p.y, (float)p.z);
            Vector3 lp  = toLocalSpace && tf != null ? tf.InverseTransformPoint(wp) : wp;
            int renderIdx = vertices.Count;
            vertices.Add(lp);
            g3VertIdToNewRenderIdx[vid] = renderIdx;
        }

        foreach (int tid in compact.TriangleIndices())
        {
            Index3i tri = compact.GetTriangle(tid);
            if (!g3VertIdToNewRenderIdx.TryGetValue(tri.a, out int ia)) continue;
            if (!g3VertIdToNewRenderIdx.TryGetValue(tri.b, out int ib)) continue;
            if (!g3VertIdToNewRenderIdx.TryGetValue(tri.c, out int ic)) continue;
            triangles.Add(ia); triangles.Add(ib); triangles.Add(ic);
        }

        var mesh = new Mesh();
        mesh.indexFormat = vertices.Count > 65535
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // ── Append một DMesh3 vào DMesh3 khác ───────────────────────────────

    /// <summary>
    /// Gộp meshB vào meshA (in-place). Trả về bảng map vertId của B → vertId mới trong A.
    /// </summary>
    public static Dictionary<int, int> AppendMesh(DMesh3 meshA, DMesh3 meshB)
    {
        var idMap = new Dictionary<int, int>();

        foreach (int vid in meshB.VertexIndices())
        {
            Vector3d p = meshB.GetVertex(vid);
            idMap[vid] = meshA.AppendVertex(p);
        }

        foreach (int tid in meshB.TriangleIndices())
        {
            Index3i t = meshB.GetTriangle(tid);
            meshA.AppendTriangle(idMap[t.a], idMap[t.b], idMap[t.c]);
        }

        return idMap;
    }
}
