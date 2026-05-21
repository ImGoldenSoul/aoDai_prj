// ============================================================
//  MeshSewer_UCloth.cs  — v6.0  (distance-based, no boundary loop)
//
//  Logic khâu hoàn toàn dựa vào khoảng cách, KHÔNG dùng boundary loop:
//
//  Gate check (Initialize):
//    · Tính distance(hitPointA, hitPointB).
//    · Nếu distance > mergeGateThreshold → từ chối ngay, không merge.
//    · mergeGateThreshold = 0 → bỏ qua gate (luôn thử merge).
//
//  Collect candidates:
//    · Duyệt TẤT CẢ vertex (VertexIndices()) của combined mesh.
//    · Vertex nào nằm trong sewRadius quanh hitPointA → setA.
//    · Vertex nào nằm trong sewRadius quanh hitPointB → setB.
//    · Không phân biệt boundary hay nội tâm.
//    · Không cần boundary loop, không cần loop pairing.
//
//  Match:
//    · Với mỗi vertex trong setA, tìm vertex gần nhất trong setB
//      (không phân biệt mesh gốc — hoạt động sau nhiều lần merge).
//    · Enqueue cặp (vA, vB) vào _pendingPairs.
//
//  Sew / Finalize: giữ nguyên từ v5.0.
// ============================================================
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using g3;

public class MeshSewer_UCloth
{
    // ── Cấu hình ─────────────────────────────────────────────────────────
    /// <summary>
    /// Khoảng cách tối đa giữa hai boundary vertex để chúng được weld.
    /// Snap luôn được thực hiện nên giá trị này có thể lớn hơn epsilon.
    /// </summary>
    public float WeldThreshold   = 0.05f;

    /// <summary>
    /// Số cặp vertex xử lý mỗi lần gọi Sew() (progressive mode).
    /// </summary>
    public int MaxEdgesPerCall = 4;

    /// <summary>
    /// Callback sau Finalize() — SewingManager dùng để đăng ký mesh mới.
    /// </summary>
    public System.Action<GameObject> OnSeamCompleted;

    // ── Trạng thái nội bộ ────────────────────────────────────────────────
    private GameObject     _goA, _goB;
    private bool           _isSelfSew;
    private UCloth.UCCloth _ucA, _ucB;
    private DMesh3         _combined;

    private Queue<(int vA, int vB)> _pendingPairs = new();
    private bool _initialized = false;
    private bool _completed   = false;

    // ── Constructor ───────────────────────────────────────────────────────
    /// <param name="goA">Mesh thứ nhất.</param>
    /// <param name="goB">Mesh thứ hai. Truyền goA để tự khâu.</param>
    public MeshSewer_UCloth(GameObject goA, GameObject goB,
                             float weldThreshold   = 0.05f,
                             int   maxEdgesPerCall = 4)
    {
        _goA            = goA;
        _goB            = goB;
        _isSelfSew      = (goA == goB);
        WeldThreshold   = weldThreshold;
        MaxEdgesPerCall = maxEdgesPerCall;
        _ucA            = goA.GetComponent<UCloth.UCCloth>();
        _ucB            = _isSelfSew ? _ucA : goB.GetComponent<UCloth.UCCloth>();
    }

    // ── Initialize ────────────────────────────────────────────────────────
    /// <summary>
    /// Khởi tạo pipeline khâu dựa thuần túy vào khoảng cách.
    /// </summary>
    /// <param name="hitPointA">Điểm ray chạm mesh A (world space).</param>
    /// <param name="hitPointB">Điểm ray chạm mesh B (world space).</param>
    /// <param name="sewRadius">
    ///   Bán kính vùng khâu quanh mỗi hit point (world units).
    ///   Chỉ boundary vertex trong vùng này mới được xét.
    /// </param>
    /// <param name="mergeGateThreshold">
    ///   Khoảng cách tối đa giữa hitPointA và hitPointB để cho phép merge.
    ///   0 hoặc âm = bỏ qua gate.
    /// </param>
    public bool Initialize(Vector3 hitPointA          = default,
                            Vector3 hitPointB          = default,
                            float   sewRadius          = 0.1f,
                            float   mergeGateThreshold = 0f)
    {
        if (_initialized) return true;

        // ── Gate: kiểm tra khoảng cách giữa 2 hit point ─────────────────
        if (mergeGateThreshold > 0f)
        {
            float hitDist = Vector3.Distance(hitPointA, hitPointB);
            if (hitDist > mergeGateThreshold)
            {
                Debug.LogWarning($"[MeshSewer] Gate FAIL: distance(hitA,hitB)={hitDist:F4} " +
                                 $"> mergeGateThreshold={mergeGateThreshold:F4}. Không merge.");
                return false;
            }
            Debug.Log($"[MeshSewer] Gate OK: distance(hitA,hitB)={hitDist:F4}");
        }

        // ── Bước 1: Build combined DMesh3 (world space) ──────────────────
        var mfA = _goA.GetComponent<MeshFilter>();
        if (mfA == null) { Debug.LogError("[MeshSewer] MeshFilter không tìm thấy trên goA!"); return false; }

        DMesh3 dmA = G3MeshBridge.ToDMesh3(mfA.mesh, _goA.transform, out _, useWorldSpace: true);
        if (dmA == null) { Debug.LogError("[MeshSewer] ToDMesh3(A) thất bại."); return false; }

        if (_isSelfSew)
        {
            _combined = new DMesh3(dmA, bCompact: true);
            Debug.Log($"[MeshSewer] Self-sew — {_combined.VertexCount} verts");
        }
        else
        {
            var mfB = _goB.GetComponent<MeshFilter>();
            if (mfB == null) { Debug.LogError("[MeshSewer] MeshFilter không tìm thấy trên goB!"); return false; }

            DMesh3 dmB = G3MeshBridge.ToDMesh3(mfB.mesh, _goB.transform, out _, useWorldSpace: true);
            if (dmB == null) { Debug.LogError("[MeshSewer] ToDMesh3(B) thất bại."); return false; }

            _combined = new DMesh3(dmA, bCompact: true);
            new MeshEditor(_combined).AppendMesh(dmB, new IndexMap(true), out _);
            Debug.Log($"[MeshSewer] Two-mesh — {_combined.VertexCount} verts, {_combined.TriangleCount} tris");
        }

        // ── Bước 2: Thu thập TẤT CẢ vertex trong sewRadius quanh hit point ─
        // Không phân biệt boundary hay nội tâm — mọi vertex nằm trong vùng
        // sewRadius đều được xét để snap & weld.
        // setA = vertex gần hitPointA (thuộc mesh A hoặc vùng A của combined).
        // setB = vertex gần hitPointB (thuộc mesh B hoặc vùng B của combined).

        Vector3d seedA = ToV3d(hitPointA);
        Vector3d seedB = ToV3d(hitPointB);
        double   rSq   = (double)sewRadius * sewRadius;

        var setA = new HashSet<int>();
        var setB = new HashSet<int>();

        foreach (int vid in _combined.VertexIndices())
        {
            Vector3d p = _combined.GetVertex(vid);
            if ((p - seedA).LengthSquared <= rSq) setA.Add(vid);
            if ((p - seedB).LengthSquared <= rSq) setB.Add(vid);
        }

        Debug.Log($"[MeshSewer] Verts trong sewRadius={sewRadius:F3}: " +
                  $"setA={setA.Count} | setB={setB.Count}");

        if (setA.Count == 0 || setB.Count == 0)
        {
            // Tìm vertex gần nhất để gợi ý sewRadius phù hợp
            double minDistA = double.MaxValue, minDistB = double.MaxValue;
            foreach (int vid in _combined.VertexIndices())
            {
                Vector3d p = _combined.GetVertex(vid);
                double dA = (p - seedA).Length;
                double dB = (p - seedB).Length;
                if (dA < minDistA) minDistA = dA;
                if (dB < minDistB) minDistB = dB;
            }
            Debug.LogWarning($"[MeshSewer] Không có vertex nào trong sewRadius={sewRadius:F3}. " +
                             $"Vertex gần nhất: A={minDistA:F4}, B={minDistB:F4}. " +
                             $"Thử tăng sewRadius >= {System.Math.Max(minDistA, minDistB) * 1.2:F3}.");
            return false;
        }

        // ── Bước 3: Match cặp (vA → vB gần nhất) ────────────────────────
        // Dùng PointHashGrid3d để tìm nhanh.
        // Search radius = WeldThreshold (sau snap khoảng cách = 0,
        // nhưng trước snap ta cần tìm được cặp → dùng khoảng cách thực).

        // Tính khoảng cách thực tế nhỏ nhất giữa setA và setB
        // để tự động mở rộng WeldThreshold nếu cần.
        double actualMinDist = double.MaxValue;
        var    listB         = setB.ToList();
        foreach (int vA in setA)
        {
            Vector3d pA = _combined.GetVertex(vA);
            foreach (int vB in listB)
            {
                if (vA == vB) continue;
                double d = pA.Distance(_combined.GetVertex(vB));
                if (d < actualMinDist) actualMinDist = d;
            }
        }

        // Threshold thực = max(WeldThreshold, actualMin * 1.1)
        // → luôn tìm được ít nhất 1 cặp nếu có vertex
        double effectiveThreshold = System.Math.Max(WeldThreshold, actualMinDist * 1.1);

        Debug.Log($"[MeshSewer] Khoảng cách thực tế nhỏ nhất A↔B={actualMinDist:F4}, " +
                  $"effectiveThreshold={effectiveThreshold:F4}");

        // Build grid từ setB
        var gridB  = new PointHashGrid3d<int>(effectiveThreshold * 2.0, -1);
        foreach (int vB in listB)
            gridB.InsertPoint(vB, _combined.GetVertex(vB));

        var usedB = new HashSet<int>();
        foreach (int vA in setA)
        {
            if (!_combined.IsVertex(vA)) continue;
            Vector3d pA = _combined.GetVertex(vA);

            var nearest = gridB.FindNearestInRadius(
                pA, effectiveThreshold,
                vid => pA.Distance(_combined.GetVertex(vid)));

            if (nearest.Key < 0) continue;
            if (nearest.Key == vA) continue;          // không weld với chính mình
            if (usedB.Contains(nearest.Key)) continue;

            _pendingPairs.Enqueue((vA, nearest.Key));
            usedB.Add(nearest.Key);
        }

        if (_pendingPairs.Count == 0)
        {
            Debug.LogWarning($"[MeshSewer] Không tìm được cặp vertex nào. " +
                             $"actualMinDist={actualMinDist:F4}, threshold={effectiveThreshold:F4}.");
            return false;
        }

        Debug.Log($"[MeshSewer] {_pendingPairs.Count} cặp vertex sẽ được khâu.");
        _initialized = true;
        return true;
    }

    // ── Sew ───────────────────────────────────────────────────────────────
    /// <returns>true khi đã xử lý hết tất cả pending pairs.</returns>
    public bool Sew()
    {
        if (!_initialized || _completed) return _completed;

        int count = 0;
        while (_pendingPairs.Count > 0 && count < MaxEdgesPerCall)
        {
            var (vA, vB) = _pendingPairs.Dequeue();
            if (!_combined.IsVertex(vA) || !_combined.IsVertex(vB)) continue;
            if (vA == vB) { count++; continue; }

            // Snap vB → vị trí vA
            _combined.SetVertex(vB, _combined.GetVertex(vA));

            // Weld: xóa & rebuild tam giác, thay vB → vA
            TryCollapseToA(_combined, vA, vB);

            count++;
        }

        _completed = _pendingPairs.Count == 0;
        return _completed;
    }

    // ── Finalize ──────────────────────────────────────────────────────────
    /// <summary>
    /// Tạo GameObject mới từ mesh đã khâu, kế thừa đầy đủ UCCloth.
    /// Gọi OnSeamCompleted(go) để SewingManager đăng ký vào scene.
    /// </summary>
    public GameObject Finalize(string newName = "SewnCloth")
    {
        if (!_initialized)
        {
            Debug.LogError("[MeshSewer] Gọi Finalize() trước Initialize()!");
            return null;
        }

        DMesh3 compacted = new DMesh3(_combined, bCompact: true);

        bool valid = compacted.CheckValidity(false, FailMode.ReturnOnly);
        if (!valid)
            Debug.LogWarning("[MeshSewer] Mesh vẫn có lỗi topology sau compact.");
        else
            Debug.Log("[MeshSewer] ✓ Topology hợp lệ.");

        if (compacted.VertexCount > 65535)
        {
            Debug.LogError($"[MeshSewer] {compacted.VertexCount} verts > 65535 (UCJob ushort limit)!");
            return null;
        }

        MeshNormals.QuickCompute(compacted);

        Mesh unityMesh = G3MeshBridge.ToUnityMesh(compacted, null, out _, toLocalSpace: false);
        unityMesh.RecalculateNormals();
        unityMesh.RecalculateBounds();
        unityMesh.RecalculateTangents();

        // ── Tạo GameObject ───────────────────────────────────────────────
        var go = new GameObject(newName);
        go.tag = _goA.tag;
        go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        go.transform.localScale = Vector3.one;

        go.AddComponent<MeshFilter>().mesh = unityMesh;

        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = _goA.GetComponent<MeshRenderer>()?.sharedMaterials ?? new Material[0];

        var mc = go.AddComponent<MeshCollider>();
        mc.sharedMesh = unityMesh;
        mc.convex     = false;

        var rb = go.AddComponent<Rigidbody>();
        rb.useGravity  = true;
        rb.isKinematic = true;

        // ── Copy UCCloth ─────────────────────────────────────────────────
        var ucBase = _ucA ?? _ucB;
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

            newCloth.sphereColliders  = MergeArrays(_ucA?.sphereColliders,
                                                     _isSelfSew ? null : _ucB?.sphereColliders);
            newCloth.capsuleColliders = MergeArrays(_ucA?.capsuleColliders,
                                                     _isSelfSew ? null : _ucB?.capsuleColliders);
            newCloth.cubeColliders    = MergeArrays(_ucA?.cubeColliders,
                                                     _isSelfSew ? null : _ucB?.cubeColliders);

            var mergedPins = new List<Collider>();
            if (_ucA?.pinColliders != null) mergedPins.AddRange(_ucA.pinColliders);
            if (!_isSelfSew && _ucB?.pinColliders != null)
                foreach (var c in _ucB.pinColliders)
                    if (!mergedPins.Contains(c)) mergedPins.Add(c);
            newCloth.pinColliders = mergedPins;

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

        // ── Tắt mesh gốc an toàn ─────────────────────────────────────────
        SafeDisableUCloth(_goA);
        if (!_isSelfSew) SafeDisableUCloth(_goB);

        Debug.Log($"[MeshSewer] ✓ '{newName}': {unityMesh.vertexCount} verts, " +
                  $"{unityMesh.triangles.Length / 3} tris");

        OnSeamCompleted?.Invoke(go);
        return go;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Weld vB → vA: xóa tất cả tam giác chứa vB rồi rebuild với vB thay = vA.
    /// DMesh3 không có SetTriangle → dùng RemoveTriangle + AppendTriangle.
    /// </summary>
    private static bool TryCollapseToA(DMesh3 mesh, int vA, int vB)
    {
        if (!mesh.IsVertex(vA) || !mesh.IsVertex(vB)) return false;

        var trisOfB = mesh.VtxTrianglesItr(vB).ToList();
        if (trisOfB.Count == 0) return false;

        // Snapshot trước khi modify
        var toRebuild = new List<(int a, int b, int c, int gid)>();
        foreach (int tid in trisOfB)
        {
            Index3i tri = mesh.GetTriangle(tid);
            int a   = tri.a == vB ? vA : tri.a;
            int b   = tri.b == vB ? vA : tri.b;
            int c   = tri.c == vB ? vA : tri.c;
            int gid = mesh.HasTriangleGroups ? mesh.GetTriangleGroup(tid) : -1;
            toRebuild.Add((a, b, c, gid));
        }

        foreach (int tid in trisOfB)
            mesh.RemoveTriangle(tid, false, false);

        foreach (var (a, b, c, gid) in toRebuild)
        {
            if (a == b || b == c || a == c) continue; // suy biến → bỏ
            mesh.AppendTriangle(a, b, c, gid);
        }

        if (mesh.IsVertex(vB) && mesh.GetVtxTriangleCount(vB) == 0)
            mesh.RemoveVertex(vB, true, false);

        return true;
    }

    private static Vector3d ToV3d(Vector3 v) => new Vector3d(v.x, v.y, v.z);

    private static T[] MergeArrays<T>(T[] a, T[] b)
    {
        if (a == null || a.Length == 0) return b ?? new T[0];
        if (b == null || b.Length == 0) return a;
        var result = new T[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }

    private static void SafeDisableUCloth(GameObject go)
    {
        if (go == null) return;
        var uc = go.GetComponent<UCloth.UCCloth>();
        if (uc != null) uc.enabled = false;
        go.SetActive(false);
    }
}
