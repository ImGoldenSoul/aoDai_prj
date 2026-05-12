// ============================================================
//  MeshSewer_UCloth.cs  — v4.0
//
//  Hỗ trợ 2 chế độ:
//
//  CROSS-SEW (goA != goB):
//    Khâu 2 mesh riêng biệt lại với nhau.
//    Pipeline: Convert A + B → Append B vào combined → tìm
//    boundary loop pair gần nhau nhất → match vertex → MergeEdges.
//
//  SELF-SEW (goA == goB):
//    Khâu 2 boundary loop trên CÙNG 1 mesh (mesh đã merge trước đó).
//    Pipeline: Convert mesh → KHÔNG Append → tìm loop pair gần
//    hit point nhất → match vertex → MergeEdges.
//    Dùng khi mesh đã được khâu từ nhiều mảnh nhưng vẫn còn
//    boundary hở cần đóng lại.
//
//  Shared logic:
//    - Snap vB → vị trí vA trước MergeEdges (tránh SameOrientation)
//    - Compact DMesh3 trước Finalize (loại lỗ hổng ID)
//    - Merge pinColliders + collider arrays từ cả hai UCCloth gốc
//    - SafeDisableUCloth trước SetActive(false)
//    - Kiểm tra ushort limit (65535) của UCJob
// ============================================================
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using g3;

public class MeshSewer_UCloth
{
    // ── Cấu hình ─────────────────────────────────────────────────────────
    public float WeldThreshold   = 0.008f;
    public int   MaxEdgesPerCall = 4;
    public System.Action<GameObject> OnSeamCompleted;

    // ── Trạng thái nội bộ ────────────────────────────────────────────────
    private GameObject     _goA, _goB;
    private UCloth.UCCloth _ucA, _ucB;
    private bool           _isSelfSew;   // true khi goA == goB

    private DMesh3 _combined;
    private int[]  _remapA, _remapB, _appendMapV;

    private Queue<(int vA, int vB)> _pendingPairs = new();

    private bool _initialized = false;
    private bool _completed   = false;

    // ── Constructor ───────────────────────────────────────────────────────
    public MeshSewer_UCloth(GameObject goA, GameObject goB,
                             float weldThreshold   = 0.008f,
                             int   maxEdgesPerCall = 4)
    {
        _goA        = goA;
        _goB        = goB;
        _isSelfSew  = (goA == goB);
        WeldThreshold   = weldThreshold;
        MaxEdgesPerCall = maxEdgesPerCall;
        _ucA = goA.GetComponent<UCloth.UCCloth>();
        _ucB = _isSelfSew ? _ucA : goB.GetComponent<UCloth.UCCloth>();
    }

    // ── Initialize ────────────────────────────────────────────────────────
    /// <param name="hitPointA">Điểm ray chạm mesh A (world space).</param>
    /// <param name="hitPointB">Điểm ray chạm mesh B (world space). Bằng hitPointA nếu self-sew.</param>
    /// <param name="sewRadius">Bán kính vùng khâu. 0 = khâu toàn bộ loop.</param>
    public bool Initialize(Vector3 hitPointA = default,
                            Vector3 hitPointB = default,
                            float   sewRadius = 0f)
    {
        if (_initialized) return true;

        var mfA = _goA.GetComponent<MeshFilter>();
        if (mfA == null)
        {
            Debug.LogError("[MeshSewer] MeshFilter không tìm thấy!");
            return false;
        }

        // ── Bước 1: Convert → DMesh3 (world space) ──────────────────────
        DMesh3 dmA = G3MeshBridge.ToDMesh3(mfA.mesh, _goA.transform,
                                            out _remapA, useWorldSpace: true);
        if (dmA == null)
        {
            Debug.LogError("[MeshSewer] ToDMesh3 thất bại.");
            return false;
        }

        // ── Bước 2: Build combined ───────────────────────────────────────
        if (_isSelfSew)
        {
            // Self-sew: chỉ dùng mesh A, không Append gì thêm
            _combined = new DMesh3(dmA, true);
            Debug.Log($"[MeshSewer] [SELF-SEW] {_goA.name}: " +
                      $"{_combined.VertexCount} verts, {_combined.TriangleCount} tris");
        }
        else
        {
            // Cross-sew: Append mesh B vào combined
            var mfB = _goB.GetComponent<MeshFilter>();
            if (mfB == null)
            {
                Debug.LogError("[MeshSewer] MeshFilter của goB không tìm thấy!");
                return false;
            }
            DMesh3 dmB = G3MeshBridge.ToDMesh3(mfB.mesh, _goB.transform,
                                                out _remapB, useWorldSpace: true);
            if (dmB == null)
            {
                Debug.LogError("[MeshSewer] ToDMesh3(B) thất bại.");
                return false;
            }
            _combined = new DMesh3(dmA, true);
            var mergeMap = new IndexMap(true);
            new MeshEditor(_combined).AppendMesh(dmB, mergeMap, out _appendMapV);
            Debug.Log($"[MeshSewer] [CROSS-SEW] Combined: " +
                      $"{_combined.VertexCount} verts, {_combined.TriangleCount} tris");
        }

        // ── Bước 3: Tìm boundary loops ───────────────────────────────────
        var bl = new MeshBoundaryLoops(_combined);
        if (bl.Loops == null || bl.Loops.Count < 2)
        {
            Debug.LogWarning($"[MeshSewer] Chỉ có {bl.Loops?.Count ?? 0} boundary loop " +
                             "(cần ≥ 2). Mesh có thể đã closed hoàn toàn.");
            return false;
        }

        // ── Bước 4: Chọn loop pair ───────────────────────────────────────
        // Self-sew: ưu tiên chọn 2 loop gần hit point nhất (không nhất thiết
        //   phải gần nhau theo centroid — người dùng chỉ ray vào vùng muốn khâu)
        // Cross-sew: chọn 2 loop gần nhau nhất theo centroid (giữ nguyên)
        EdgeLoop loopA, loopB;
        if (_isSelfSew && sewRadius > 0f)
        {
            Vector3d seed = ToV3d(hitPointA);
            (loopA, loopB) = FindLoopPairNearSeed(bl.Loops, seed, sewRadius);
        }
        else
        {
            (loopA, loopB) = FindClosestLoopPair(bl.Loops);
        }

        Debug.Log($"[MeshSewer] LoopA={loopA.Vertices.Length} | LoopB={loopB.Vertices.Length}");

        // ── Bước 5: Spatial match trong sewRadius ────────────────────────
        bool     useHitSeed  = sewRadius > 0f;
        Vector3d seedA       = useHitSeed ? ToV3d(hitPointA) : Vector3d.Zero;
        Vector3d seedB       = useHitSeed ? ToV3d(hitPointB) : Vector3d.Zero;
        double   sewRadiusSq = (double)sewRadius * sewRadius;

        int[] candidatesA = useHitSeed
            ? loopA.Vertices.Where(v =>
                  (_combined.GetVertex(v) - seedA).LengthSquared <= sewRadiusSq).ToArray()
            : loopA.Vertices;

        int[] candidatesB = useHitSeed
            ? loopB.Vertices.Where(v =>
                  (_combined.GetVertex(v) - seedB).LengthSquared <= sewRadiusSq).ToArray()
            : loopB.Vertices;

        Debug.Log($"[MeshSewer] Candidates trong sewRadius={sewRadius:F3}: " +
                  $"A={candidatesA.Length} | B={candidatesB.Length}");

        if (candidatesA.Length == 0 || candidatesB.Length == 0)
        {
            double minA = loopA.Vertices.Min(v => (_combined.GetVertex(v) - seedA).Length);
            double minB = loopB.Vertices.Min(v => (_combined.GetVertex(v) - seedB).Length);
            Debug.LogWarning($"[MeshSewer] Không có candidate trong sewRadius={sewRadius:F3}. " +
                             $"Boundary gần nhất: A={minA:F4}, B={minB:F4}. " +
                             $"Thử tăng sewRadius >= {System.Math.Max(minA, minB) * 1.2:F3}.");
            return false;
        }

        // Build PointHashGrid từ candidatesB
        var gridB = new PointHashGrid3d<int>(WeldThreshold * 2.0, -1);
        foreach (int vid in candidatesB)
            gridB.InsertPoint(vid, _combined.GetVertex(vid));

        var usedB = new HashSet<int>();
        foreach (int vA in candidatesA)
        {
            // Self-sew: tránh match vertex với chính nó
            Vector3d pA      = _combined.GetVertex(vA);
            var      nearest = gridB.FindNearestInRadius(pA, WeldThreshold,
                                   (vid) => (vid == vA) ? double.MaxValue
                                                        : pA.Distance(_combined.GetVertex(vid)));
            if (nearest.Key < 0 || nearest.Key == vA) continue;
            if (usedB.Contains(nearest.Key))           continue;
            _pendingPairs.Enqueue((vA, nearest.Key));
            usedB.Add(nearest.Key);
        }

        if (_pendingPairs.Count == 0)
        {
            double minReal = double.MaxValue;
            foreach (int vA in candidatesA)
            {
                Vector3d pA = _combined.GetVertex(vA);
                foreach (int vB in candidatesB)
                    if (vB != vA)
                        minReal = System.Math.Min(minReal, pA.Distance(_combined.GetVertex(vB)));
            }
            Debug.LogWarning($"[MeshSewer] Không có cặp trong WeldThreshold={WeldThreshold:F4}. " +
                             $"Khoảng cách nhỏ nhất thực tế={minReal:F4}. " +
                             $"Thử tăng WeldThreshold >= {minReal * 1.1:F4}.");
            return false;
        }

        Debug.Log($"[MeshSewer] {_pendingPairs.Count} cặp vertex chờ khâu.");
        _initialized = true;
        return true;
    }

    // ── Sew ───────────────────────────────────────────────────────────────
    public bool Sew()
    {
        if (!_initialized || _completed) return _completed;

        int count = 0;
        while (_pendingPairs.Count > 0 && count < MaxEdgesPerCall)
        {
            var (vA, vB) = _pendingPairs.Dequeue();
            if (!_combined.IsVertex(vA) || !_combined.IsVertex(vB)) continue;

            _combined.SetVertex(vB, _combined.GetVertex(vA));

            int eA = FindBoundaryEdgeAtVertex(_combined, vA);
            int eB = FindBoundaryEdgeAtVertex(_combined, vB);
            if (eA < 0 || eB < 0 || eA == eB) continue;

            var res = _combined.MergeEdges(eA, eB, out _);
            if (res != MeshResult.Ok)
            {
                Debug.LogWarning($"[MeshSewer] MergeEdges({res}) — fallback CollapseEdge");
                int eAB = _combined.FindEdge(vA, vB);
                if (eAB >= 0) _combined.CollapseEdge(eAB, vA, out _);
            }
            count++;
        }

        _completed = _pendingPairs.Count == 0;
        return _completed;
    }

    // ── Finalize ──────────────────────────────────────────────────────────
    public GameObject Finalize(string newName = "SewnCloth")
    {
        if (!_initialized)
        {
            Debug.LogError("[MeshSewer] Gọi Finalize() trước Initialize()!");
            return null;
        }

        // Compact: xóa lỗ hổng ID sau merge/collapse
        DMesh3 compacted = new DMesh3(_combined, true);

        if (!compacted.CheckValidity(false, FailMode.ReturnOnly))
            Debug.LogWarning("[MeshSewer] Mesh vẫn có lỗi topology sau compact.");
        else
            Debug.Log("[MeshSewer] ✓ Topology hợp lệ.");

        if (compacted.VertexCount > 65535)
        {
            Debug.LogError($"[MeshSewer] {compacted.VertexCount} verts > 65535 (ushort limit UCJob)!");
            return null;
        }

        MeshNormals.QuickCompute(compacted);
        Mesh unityMesh = G3MeshBridge.ToUnityMesh(compacted, null, out _, toLocalSpace: false);
        unityMesh.RecalculateNormals();
        unityMesh.RecalculateBounds();
        unityMesh.RecalculateTangents();

        // ── Spawn GameObject ─────────────────────────────────────────────
        var go = new GameObject(newName);
        go.tag = _goA.tag;   // giữ tag "Cloth" để SewingManager detect được lần sau
        go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        go.transform.localScale = Vector3.one;

        go.AddComponent<MeshFilter>().mesh = unityMesh;

        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = _goA.GetComponent<MeshRenderer>()?.sharedMaterials
                             ?? new Material[0];

        // MeshCollider TRƯỚC UCCloth (UCCloth.Start() dùng GetComponent<MeshCollider>)
        // non-convex để UCPinner raycast pin đúng, và để SewingManager raycast tiếp
        var mc = go.AddComponent<MeshCollider>();
        mc.sharedMesh = unityMesh;
        mc.convex     = false;

        var rb = go.AddComponent<Rigidbody>();
        rb.useGravity  = true;
        rb.isKinematic = true;

        // ── Copy + Merge UCCloth từ cả hai mesh gốc ──────────────────────
        var ucA = _ucA;
        var ucB = _isSelfSew ? null : _ucB;   // self-sew chỉ có 1 nguồn
        var ucBase = ucA ?? ucB;

        if (ucBase != null)
        {
            var newCloth = go.AddComponent<UCloth.UCCloth>();
            newCloth.preprocessorType     = ucBase.preprocessorType;
            newCloth.materialProperties   = ucBase.materialProperties;
            newCloth.simulationProperties = ucBase.simulationProperties;
            newCloth.qualityProperties    = ucBase.qualityProperties;
            newCloth.collisionProperties  = ucBase.collisionProperties;
            newCloth.thickness            = ucBase.thickness;
            newCloth.offsetFront          = ucBase.offsetFront;
            newCloth.smoothing            = ucBase.smoothing;

            // Merge collider arrays từ A và B
            newCloth.sphereColliders  = MergeArrays(ucA?.sphereColliders,  ucB?.sphereColliders);
            newCloth.capsuleColliders = MergeArrays(ucA?.capsuleColliders, ucB?.capsuleColliders);
            newCloth.cubeColliders    = MergeArrays(ucA?.cubeColliders,    ucB?.cubeColliders);

            // Merge pinColliders, tránh duplicate
            var mergedPins = new List<Collider>();
            if (ucA?.pinColliders != null) mergedPins.AddRange(ucA.pinColliders);
            if (ucB?.pinColliders != null)
                foreach (var c in ucB.pinColliders)
                    if (!mergedPins.Contains(c)) mergedPins.Add(c);
            newCloth.pinColliders = mergedPins;

            Debug.Log($"[MeshSewer] pinColliders: {mergedPins.Count} " +
                      $"(A={ucA?.pinColliders?.Count ?? 0}, B={ucB?.pinColliders?.Count ?? 0})");

            // Copy UClothLaserGrabber
            var grabSrc = _goA.GetComponent<UClothLaserGrabber>()
                          ?? (_isSelfSew ? null : _goB.GetComponent<UClothLaserGrabber>());
            if (grabSrc != null)
            {
                var gr = go.AddComponent<UClothLaserGrabber>();
                gr.vrController  = grabSrc.vrController;
                gr.grabSphere    = grabSrc.grabSphere;
                gr.triggerAction = grabSrc.triggerAction;
                gr.pullForce     = grabSrc.pullForce;
            }
        }

        // Tắt đúng thứ tự: disable UCCloth trước, rồi SetActive(false)
        SafeDisableUCloth(_goA);
        if (!_isSelfSew) SafeDisableUCloth(_goB);

        Debug.Log($"[MeshSewer] ✓ Spawn '{newName}': {unityMesh.vertexCount} verts, " +
                  $"{unityMesh.triangles.Length / 3} tris");

        OnSeamCompleted?.Invoke(go);
        return go;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Self-sew: tìm 2 loop mà CẢ HAI đều có vertex gần seed.
    /// Fallback về FindClosestLoopPair nếu không thỏa.
    /// </summary>
    private (EdgeLoop, EdgeLoop) FindLoopPairNearSeed(List<EdgeLoop> loops,
                                                       Vector3d seed, float radius)
    {
        double rSq = (double)radius * radius;

        // Tính điểm số cho mỗi loop: số vertex trong bán kính seed
        var scored = loops.Select(l => (
            loop:  l,
            score: l.Vertices.Count(v =>
                (_combined.GetVertex(v) - seed).LengthSquared <= rSq)
        )).OrderByDescending(x => x.score).ToList();

        // Hai loop có nhiều vertex gần seed nhất
        if (scored.Count >= 2 && scored[0].score > 0 && scored[1].score > 0)
            return (scored[0].loop, scored[1].loop);

        // Fallback: chọn theo centroid gần nhau nhất
        return FindClosestLoopPair(loops);
    }

    private static (EdgeLoop, EdgeLoop) FindClosestLoopPair(List<EdgeLoop> loops)
    {
        if (loops.Count == 2) return (loops[0], loops[1]);

        EdgeLoop bestA = loops[0], bestB = loops[1];
        double   bestDist = double.MaxValue;
        for (int i = 0; i < loops.Count; i++)
        for (int j = i + 1; j < loops.Count; j++)
        {
            double d = LoopCentroidDist(loops[i], loops[j]);
            if (d < bestDist) { bestDist = d; bestA = loops[i]; bestB = loops[j]; }
        }
        return (bestA, bestB);
    }

    private static double LoopCentroidDist(EdgeLoop a, EdgeLoop b)
    {
        Vector3d cA = Vector3d.Zero, cB = Vector3d.Zero;
        foreach (int v in a.Vertices) cA += a.Mesh.GetVertex(v);
        foreach (int v in b.Vertices) cB += b.Mesh.GetVertex(v);
        cA /= a.Vertices.Length;
        cB /= b.Vertices.Length;
        return (cA - cB).Length;
    }

    private static int FindBoundaryEdgeAtVertex(DMesh3 mesh, int vid)
    {
        foreach (int eid in mesh.VtxEdgesItr(vid))
            if (mesh.IsBoundaryEdge(eid))
                return eid;
        return -1;
    }

    private static void SafeDisableUCloth(GameObject go)
    {
        if (go == null) return;
        var uc = go.GetComponent<UCloth.UCCloth>();
        if (uc != null) uc.enabled = false;
        go.SetActive(false);
    }

    private static T[] MergeArrays<T>(T[] a, T[] b)
    {
        if (a == null || a.Length == 0) return b ?? new T[0];
        if (b == null || b.Length == 0) return a;
        var r = new T[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }

    private static Vector3d ToV3d(Vector3 v) => new Vector3d(v.x, v.y, v.z);
}
