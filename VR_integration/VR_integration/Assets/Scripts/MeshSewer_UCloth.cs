// ============================================================
//  MeshSewer_UCloth.cs  — v10.1
//
//  [FIX-v10-1] Finalize Bước A.5: hàn boundary vertex trùng vị trí (co-located weld).
//          Root cause của KeyNotFoundException trong UCMeshPreprocessor:
//          TryCollapseToA bị SKIP (non-manifold/winding) → vertex đã SetVertex(vB,posA)
//          nhưng topology chưa hàn → UCMeshPreprocessor tra cứu edge→UCTriangle thứ hai
//          không tồn tại (boundary edge chỉ có 1 tri) → KeyNotFoundException.
//          Fix: sau compact ban đầu, quét toàn bộ boundary vertex, nếu 2 vertex cách
//          nhau < 1e-5 world-space → force TryCollapseToA (cả 2 chiều) lặp tối đa 8 pass.
//  [FIX-v10-1] Finalize Bước E: validate 2-manifold nghiêm ngặt trước khi trao UCCloth.
//          Mô phỏng edge-count loop của UCMeshPreprocessor, log rõ số boundary edge còn sót
//          và xóa T-junction cuối cùng nếu có.
//  [FIX-v10-2] AddSeam: guard hitA≈hitB ở MeshSewer level (sewRadius * 0.25f).
//          SewingManager đã có guard 0.3f nhưng self-sew gần kín vẫn có thể pass qua →
//          double guard để chặn hoàn toàn.
//
//  Giữ nguyên từ v10.0:
//  [FIX-BUG-A..D], [FIX-v9-1..4], [FIX-TOPOLOGY-1/2/3], [BUG-1..4]
// ============================================================
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using UnityEngine;
using g3;

public class MeshSewer_UCloth
{
    // ── Cấu hình ─────────────────────────────────────────────────────────
    public float WeldThreshold   = 0.05f;
    public int   MaxEdgesPerCall = 4;
    public System.Action<GameObject> OnSeamCompleted;

    // ── Trạng thái nội bộ ────────────────────────────────────────────────
    private GameObject     _goA, _goB;
    private bool           _isSelfSew;
    private UCloth.UCCloth _ucA, _ucB;
    private DMesh3         _combined;

    // ── [AREA-LOG] Diện tích mesh A/B TRƯỚC khi khâu (world-space, m²) ─────
    private float _areaBeforeA = 0f;
    private float _areaBeforeB = 0f;

    // Phân vùng vertex trong combined mesh (chỉ dùng cho two-mesh)
    private HashSet<int> _regionA = new HashSet<int>();
    private HashSet<int> _regionB = new HashSet<int>();

    private Queue<(int vA, int vB)> _pendingPairs = new Queue<(int, int)>();
    // [FIX-BUG-B] Dedup set song song với queue để tránh cùng cặp bị enqueue nhiều lần
    // khi người dùng giữ controller đứng yên → collapse dây chuyền → nổ mesh.
    private HashSet<(int, int)> _pendingPairsSet = new HashSet<(int, int)>();
    private bool _initialized = false;
    private bool _completed   = false;
    private GameObject _previewObjEdge;
    private MeshFilter _previewFilter;
    private MeshRenderer _previewRenderer;

    // ── [FIX BUG-4] Public property cho SewingManager kiểm tra trước khi AddSeam ──
    /// <summary>
    /// true nếu mesh vẫn còn ít nhất một cạnh biên hở.
    /// false nghĩa là mesh đã kín hoàn toàn — KHÔNG được AddSeam thêm nữa.
    /// </summary>
    public bool HasOpenBoundary
    {
        get
        {
            if (_combined == null) return false;
            foreach (int eid in _combined.EdgeIndices())
                if (_combined.IsBoundaryEdge(eid)) return true;
            return false;
        }
    }

    // ── [AREA-LOG] Tính diện tích một DMesh3 (world-space, m²) ─────────────
    /// <summary>
    /// Tổng diện tích tất cả triangle hợp lệ của một DMesh3.
    /// Dùng cho log diện tích mesh A/B TRƯỚC khâu (dmA/dmB đã ở world-space
    /// vì G3MeshBridge.ToDMesh3 được gọi với useWorldSpace:true).
    /// </summary>
    private static float ComputeDMesh3Area(DMesh3 mesh)
    {
        if (mesh == null) return 0f;

        float area = 0f;
        foreach (int tid in mesh.TriangleIndices())
        {
            if (!mesh.IsTriangle(tid)) continue;
            Vector3d a = Vector3d.Zero, b = Vector3d.Zero, c = Vector3d.Zero;
            mesh.GetTriVertices(tid, ref a, ref b, ref c);
            Vector3 va = new Vector3((float)a.x, (float)a.y, (float)a.z);
            Vector3 vb = new Vector3((float)b.x, (float)b.y, (float)b.z);
            Vector3 vc = new Vector3((float)c.x, (float)c.y, (float)c.z);
            area += Vector3.Cross(vb - va, vc - va).magnitude * 0.5f;
        }
        return area;
    }

    // ── Constructor ───────────────────────────────────────────────────────
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
    public bool Initialize(Vector3 hitPointA          = default,
                            Vector3 hitPointB          = default,
                            float   sewRadius          = 0.1f,
                            float   mergeGateThreshold = 0f)
    {
        if (_initialized) return true;

        // ── Gate (chỉ áp dụng khi khâu 2 mesh riêng biệt) ──────────────────
        if (!_isSelfSew && mergeGateThreshold > 0f)
        {
            float hitDist = Vector3.Distance(hitPointA, hitPointB);
            if (hitDist > mergeGateThreshold)
            {
                Debug.LogWarning($"[MeshSewer] Gate FAIL: dist={hitDist:F4} > {mergeGateThreshold:F4}.");
                return false;
            }
            Debug.Log($"[MeshSewer] Gate OK: dist={hitDist:F4}");
        }

        // ── Bước 1: Khởi tạo dữ liệu hình học tổng hợp DMesh3 ────────────────
        var mfA = _goA.GetComponent<MeshFilter>();
        if (mfA == null) { Debug.LogError("[MeshSewer] Thiếu MeshFilter trên goA!"); return false; }

        DMesh3 dmA = G3MeshBridge.ToDMesh3(mfA.mesh, _goA.transform, out _, useWorldSpace: true);
        if (dmA == null) { Debug.LogError("[MeshSewer] ToDMesh3(A) thất bại."); return false; }

        // [AREA-LOG] Diện tích mesh A trước khi khâu (world-space, đã bao gồm scale vì ToDMesh3 dùng useWorldSpace:true)
        _areaBeforeA = ComputeDMesh3Area(dmA);
        Debug.Log($"<color=yellow>[MeshSewer][Area] Mesh A ('{_goA.name}') TRƯỚC khâu: " +
                  $"{_areaBeforeA:F6} m²  ({_areaBeforeA * 10000f:F2} cm²)</color>");

        _combined = new DMesh3(dmA, bCompact: true);

        if (_isSelfSew)
        {
            _areaBeforeB = _areaBeforeA; // tự khâu → cùng 1 mesh
            Debug.Log($"[MeshSewer] Khởi tạo Tự khâu — {_combined.VertexCount} verts, {_combined.TriangleCount} tris");
        }
        else
        {
            foreach (int vid in _combined.VertexIndices())
                _regionA.Add(vid);

            var mfB = _goB.GetComponent<MeshFilter>();
            if (mfB == null) { Debug.LogError("[MeshSewer] Thiếu MeshFilter trên goB!"); return false; }

            DMesh3 dmB = G3MeshBridge.ToDMesh3(mfB.mesh, _goB.transform, out _, useWorldSpace: true);
            if (dmB == null) { Debug.LogError("[MeshSewer] ToDMesh3(B) thất bại."); return false; }

            // [AREA-LOG] Diện tích mesh B trước khi khâu (world-space)
            _areaBeforeB = ComputeDMesh3Area(dmB);
            Debug.Log($"<color=yellow>[MeshSewer][Area] Mesh B ('{_goB.name}') TRƯỚC khâu: " +
                      $"{_areaBeforeB:F6} m²  ({_areaBeforeB * 10000f:F2} cm²)</color>");
            Debug.Log($"<color=yellow>[MeshSewer][Area] Tổng diện tích A+B TRƯỚC khâu: " +
                      $"{(_areaBeforeA + _areaBeforeB):F6} m²  ({(_areaBeforeA + _areaBeforeB) * 10000f:F2} cm²)</color>");

            var idMapB = new IndexMap(true);
            new MeshEditor(_combined).AppendMesh(dmB, idMapB, out _);

            foreach (int vid in _combined.VertexIndices())
                if (!_regionA.Contains(vid))
                    _regionB.Add(vid);

            Debug.Log($"[MeshSewer] Khởi tạo Khâu gộp — {_combined.VertexCount} verts, {_combined.TriangleCount} tris");
        }

        // ── Bước 2: Thu thập ứng viên đỉnh (Candidates) ───────────────────
        var candA = new List<int>();
        var candB = new List<int>();

        double rSq = (double)sewRadius * sewRadius;
        Vector3d seedA = ToV3d(hitPointA);
        Vector3d seedB = ToV3d(hitPointB);

        if (_isSelfSew)
        {
            // ── NHÁNH 1: Xử lý Tự khâu (Self-Sew) ──
            var boundaryVerts = new HashSet<int>();
            foreach (int eid in _combined.EdgeIndices())
            {
                if (!_combined.IsBoundaryEdge(eid)) continue;
                Index2i ev = _combined.GetEdgeV(eid);
                boundaryVerts.Add(ev.a); boundaryVerts.Add(ev.b);
            }

            // [FIX BUG-1/3] Mesh đã kín → không thể khâu thêm
            if (boundaryVerts.Count == 0)
            {
                Debug.LogWarning("[MeshSewer] Initialize self-sew: Mesh đã kín hoàn toàn, không còn boundary edge.");
                return false;
            }

            foreach (int vid in boundaryVerts)
            {
                Vector3d p = _combined.GetVertex(vid);
                if ((p - seedA).LengthSquared <= rSq) candA.Add(vid);
                if ((p - seedB).LengthSquared <= rSq) candB.Add(vid);
            }

            // FALLBACK: chỉ khi vẫn còn boundary edge nhưng seed nằm ngoài radius
            if (candA.Count == 0)
                candA = boundaryVerts.OrderBy(v => (_combined.GetVertex(v) - seedA).LengthSquared).Take(8).ToList();
            if (candB.Count == 0)
                candB = boundaryVerts.OrderBy(v => (_combined.GetVertex(v) - seedB).LengthSquared).Take(8).ToList();
        }
        else
        {
            // ── NHÁNH 2: Xử lý Khâu gộp (Two-Mesh) ──
            foreach (int vid in _regionA)
            {
                Vector3d p = _combined.GetVertex(vid);
                if ((p - seedA).LengthSquared <= rSq) candA.Add(vid);
            }
            foreach (int vid in _regionB)
            {
                Vector3d p = _combined.GetVertex(vid);
                if ((p - seedB).LengthSquared <= rSq) candB.Add(vid);
            }

            if (candA.Count == 0 && _regionA.Count > 0)
                candA = _regionA.OrderBy(v => (_combined.GetVertex(v) - seedA).LengthSquared).Take(6).ToList();
            if (candB.Count == 0 && _regionB.Count > 0)
                candB = _regionB.OrderBy(v => (_combined.GetVertex(v) - seedB).LengthSquared).Take(6).ToList();
        }

        // CHỐNG TỰ SẬP: Loại bỏ đỉnh tự ghép cặp khâu với chính nó
        var intersect = candA.Intersect(candB).ToList();
        if (intersect.Count > 0)
        {
            foreach (int v in intersect)
            {
                double dA = (_combined.GetVertex(v) - seedA).LengthSquared;
                double dB = (_combined.GetVertex(v) - seedB).LengthSquared;
                if (dA < dB) candB.Remove(v); else candA.Remove(v);
            }
        }

        Debug.Log($"[MeshSewer] Candidates Ready: candA={candA.Count}, candB={candB.Count}");

        if (candA.Count == 0 || candB.Count == 0)
        {
            Debug.LogWarning("[MeshSewer] Không đủ ứng viên đỉnh để thực hiện kết nối hình học.");
            return false;
        }

        // ── Bước 3: Cross-set matching ────────────────────────────────────
        double actualMinDist = double.MaxValue;
        foreach (int vA in candA)
        {
            Vector3d pA = _combined.GetVertex(vA);
            foreach (int vB in candB)
            {
                if (vA == vB) continue;
                double d = pA.Distance(_combined.GetVertex(vB));
                if (d < actualMinDist) actualMinDist = d;
            }
        }

        if (actualMinDist == double.MaxValue)
        {
            Debug.LogWarning("[MeshSewer] Không có cặp cross-set hợp lệ.");
            return false;
        }

        double effectiveThreshold = System.Math.Max((double)WeldThreshold, actualMinDist * 1.1);
        var gridB = new PointHashGrid3d<int>(effectiveThreshold * 2.0, -1);
        foreach (int vB in candB)
            gridB.InsertPoint(vB, _combined.GetVertex(vB));

        var usedB = new HashSet<int>();
        foreach (int vA in candA)
        {
            if (!_combined.IsVertex(vA)) continue;
            Vector3d pA = _combined.GetVertex(vA);

            var nearest = gridB.FindNearestInRadius(pA, effectiveThreshold,
                vid => vid == vA ? double.MaxValue : pA.Distance(_combined.GetVertex(vid)));

            if (nearest.Key < 0 || nearest.Key == vA) continue;
            if (usedB.Contains(nearest.Key)) continue;

            var pkey0 = vA < nearest.Key ? (vA, nearest.Key) : (nearest.Key, vA);
            if (_pendingPairsSet.Add(pkey0))
            {
                _pendingPairs.Enqueue((vA, nearest.Key));
                usedB.Add(nearest.Key);
            }
        }

        if (_pendingPairs.Count == 0) return false;

        _initialized = true;
        return true;
    }

    // ── Sew (progressive) ────────────────────────────────────────────────
    /// <returns>true khi đã xử lý hết tất cả pending pairs.</returns>
    public bool Sew()
    {
        if (!_initialized || _completed) return _completed;

        int count = 0;
        while (_pendingPairs.Count > 0 && count < MaxEdgesPerCall)
        {
            var (vA, vB) = _pendingPairs.Dequeue();
            // [FIX-BUG-B] Dọn dedup set tương ứng
            var dqKey = vA < vB ? (vA, vB) : (vB, vA);
            _pendingPairsSet.Remove(dqKey);

            // [FIX-NaN-1] vA hoặc vB có thể đã bị remove bởi collapse trước trong batch
            if (!_combined.IsVertex(vA) || !_combined.IsVertex(vB)) continue;
            if (vA == vB) continue; // không tăng count vì không làm gì

            // [FIX-NaN-2] Kiểm tra vị trí của vA trước khi dùng làm snap target
            Vector3d posA = _combined.GetVertex(vA);
            if (double.IsNaN(posA.x) || double.IsNaN(posA.y) || double.IsNaN(posA.z) ||
                double.IsInfinity(posA.x) || double.IsInfinity(posA.y) || double.IsInfinity(posA.z))
            {
                Debug.LogWarning($"[MeshSewer] Sew: vA={vA} có position NaN/Inf, bỏ qua cặp này.");
                count++;
                continue;
            }

            // Snap vB → vA (lưu position cũ để revert nếu collapse thất bại)
            Vector3d oldPosB = _combined.GetVertex(vB);
            _combined.SetVertex(vB, posA);

            // Weld topology — nếu thất bại, revert vB để tránh stale-snap loop
            bool collapsed = TryCollapseToA(_combined, vA, vB);
            if (!collapsed && _combined.IsVertex(vB))
                _combined.SetVertex(vB, oldPosB);
            count++;
        }

        _completed = _pendingPairs.Count == 0;
        return _completed;
    }

    // ── AddSeam (v8.2 Fix - Topology-safe, closed-mesh guard) ──────────────────
    public bool AddSeam(Vector3 hitPointA, Vector3 hitPointB, float sewRadius)
    {
        if (!_initialized)
        {
            Debug.LogError("[MeshSewer] AddSeam() gọi trước Initialize()!");
            return false;
        }

        // 1. Thu thập tất cả boundary vertex hiện có
        var boundaryVerts = new HashSet<int>();
        foreach (int eid in _combined.EdgeIndices())
        {
            if (!_combined.IsBoundaryEdge(eid)) continue;
            Index2i ev = _combined.GetEdgeV(eid);
            boundaryVerts.Add(ev.a);
            boundaryVerts.Add(ev.b);
        }

        // [FIX BUG-1] Hard stop: mesh đã kín hoàn toàn → không fallback, không khâu thêm
        if (boundaryVerts.Count == 0)
        {
            Debug.LogWarning("[MeshSewer] AddSeam: Mesh đã kín hoàn toàn — không còn cạnh biên hở. " +
                             "Hãy gọi Finalize() để hoàn tất.");
            return false;
        }

        Vector3d seedA = ToV3d(hitPointA);
        Vector3d seedB = ToV3d(hitPointB);

        // [FIX-v10-2] Guard hitA≈hitB ở MeshSewer level (bổ sung cho guard đã có ở SewingManager)
        // Khi self-sew gần kín, hitA==hitB → candA∩candB overlap → (v,v) pair → collapse chính mình.
        if (Vector3.Distance(hitPointA, hitPointB) < sewRadius * 0.25f)
        {
            Debug.LogWarning("[MeshSewer] AddSeam: hitA≈hitB (quá gần) — bỏ qua để tránh self-collapse.");
            return false;
        }

        // [FIX-BUG-A] Giới hạn trên dynamicRadius = sewRadius * 2.5 để tránh overshoot
        // khi người dùng quét nhanh → gom nhầm boundary vertex của đường khâu cũ →
        // tạo collapse dây chuyền → nổ mesh.
        double dynamicRadius = System.Math.Min(
            System.Math.Max((double)sewRadius, Vector3.Distance(hitPointA, hitPointB) * 0.5),
            (double)sewRadius * 2.5
        );
        double rSq = dynamicRadius * dynamicRadius;

        var candA = new List<int>();
        var candB = new List<int>();

        // 2. Tìm ứng viên chỉ trong tập boundary (KHÔNG lấy vertex nội thất)
        // [FIX-NaN-7] Bỏ qua vertex có position không hợp lệ
        foreach (int vid in boundaryVerts)
        {
            if (!_combined.IsVertex(vid)) continue; // vertex đã bị remove bởi collapse trước
            Vector3d p = _combined.GetVertex(vid);
            if (double.IsNaN(p.x) || double.IsNaN(p.y) || double.IsNaN(p.z) ||
                double.IsInfinity(p.x) || double.IsInfinity(p.y) || double.IsInfinity(p.z))
                continue; // zombie position

            double distSqA = (p - seedA).LengthSquared;
            double distSqB = (p - seedB).LengthSquared;
            if (distSqA <= rSq) candA.Add(vid);
            if (distSqB <= rSq) candB.Add(vid);
        }

        // [FIX BUG-2] Fallback: KHÔNG dùng cached boundaryVerts — re-query fresh để đảm bảo
        // đồng bộ sau các Sew()/collapse đã chạy trước đó trong cùng frame.
        // Cached list có thể chứa vertex đã bị remove hoặc không còn là boundary nữa.
        if (candA.Count == 0 || candB.Count == 0)
        {
            var freshBoundaryVerts = new HashSet<int>();
            foreach (int eid in _combined.EdgeIndices())
            {
                if (!_combined.IsBoundaryEdge(eid)) continue;
                Index2i ev = _combined.GetEdgeV(eid);
                freshBoundaryVerts.Add(ev.a);
                freshBoundaryVerts.Add(ev.b);
            }
            var validFresh = freshBoundaryVerts
                .Where(v => _combined.IsVertex(v))
                .Where(v => { Vector3d p = _combined.GetVertex(v);
                              return !double.IsNaN(p.x) && !double.IsInfinity(p.x); })
                .ToList();

            if (candA.Count == 0)
                candA = validFresh.OrderBy(v => (_combined.GetVertex(v) - seedA).LengthSquared).Take(5).ToList();
            if (candB.Count == 0)
                candB = validFresh.OrderBy(v => (_combined.GetVertex(v) - seedB).LengthSquared).Take(5).ToList();
        }

        // Loại bỏ đỉnh trùng chéo
        if (candA.Count > 0 && candB.Count > 0)
        {
            var intersect = candA.Intersect(candB).ToList();
            if (intersect.Count > 0 && (candA.Count > intersect.Count || candB.Count > intersect.Count))
            {
                foreach (int v in intersect)
                {
                    double dA = (_combined.GetVertex(v) - seedA).LengthSquared;
                    double dB = (_combined.GetVertex(v) - seedB).LengthSquared;
                    if (dA < dB) candB.Remove(v); else candA.Remove(v);
                }
            }
        }

        if (candA.Count == 0 || candB.Count == 0)
        {
            Debug.LogWarning($"[MeshSewer] AddSeam thất bại: candA={candA.Count}, candB={candB.Count}.");
            return false;
        }

        // 3. Cross-set matching
        double actualMinDist = double.MaxValue;
        foreach (int vA in candA)
        {
            Vector3d pA = _combined.GetVertex(vA);
            foreach (int vB in candB)
            {
                if (vA == vB) continue;
                double d = pA.Distance(_combined.GetVertex(vB));
                if (d < actualMinDist) actualMinDist = d;
            }
        }

        if (actualMinDist == double.MaxValue) return false;

        double threshold = System.Math.Max((double)WeldThreshold, actualMinDist * 1.2);
        var gridB = new PointHashGrid3d<int>(threshold * 2.0, -1);
        foreach (int vB in candB)
            gridB.InsertPoint(vB, _combined.GetVertex(vB));

        var usedB = new HashSet<int>();
        int added = 0;

        foreach (int vA in candA)
        {
            if (!_combined.IsVertex(vA)) continue;
            Vector3d pA = _combined.GetVertex(vA);
            var nearest = gridB.FindNearestInRadius(pA, threshold,
                vid => vid == vA ? double.MaxValue : pA.Distance(_combined.GetVertex(vid)));
            if (nearest.Key < 0 || nearest.Key == vA) continue;
            if (usedB.Contains(nearest.Key)) continue;

            var pkey1 = vA < nearest.Key ? (vA, nearest.Key) : (nearest.Key, vA);
            if (_pendingPairsSet.Add(pkey1))
            {
                _pendingPairs.Enqueue((vA, nearest.Key));
                usedB.Add(nearest.Key);
                added++;
            }
        }

        if (added == 0)
        {
            int bestVA = candA.OrderBy(v => (_combined.GetVertex(v) - seedA).LengthSquared).First();
            int bestVB = candB.Where(v => v != bestVA)
                              .OrderBy(v => (_combined.GetVertex(v) - _combined.GetVertex(bestVA)).LengthSquared)
                              .FirstOrDefault();
            if (bestVB != 0 && bestVB != bestVA)
            {
                var pkeyFallback = bestVA < bestVB ? (bestVA, bestVB) : (bestVB, bestVA);
                if (_pendingPairsSet.Add(pkeyFallback))
                {
                    _pendingPairs.Enqueue((bestVA, bestVB));
                    added++;
                }
            }
        }

        _completed = false;
        Debug.Log($"[MeshSewer] AddSeam: +{added} cặp (candA:{candA.Count}, candB:{candB.Count}, boundary:{boundaryVerts.Count})");
        return added > 0;
    }

    // ── Finalize ─────────────────────────────────────────────────────────
    public GameObject Finalize(string newName = "SewnCloth")
    {
        if (!_initialized)
        {
            Debug.LogError("[MeshSewer] Gọi Finalize() trước Initialize()!");
            return null;
        }

        ClearPreviewVisuals();

        // ── 0. Flush mọi pending pair còn lại (nếu Finalize được gọi sớm) ──
        while (_pendingPairs.Count > 0)
        {
            var (vA, vB) = _pendingPairs.Dequeue();
            var flKey = vA < vB ? (vA, vB) : (vB, vA);
            _pendingPairsSet.Remove(flKey);
            if (!_combined.IsVertex(vA) || !_combined.IsVertex(vB)) continue;
            if (vA == vB) continue;
            Vector3d posA = _combined.GetVertex(vA);
            if (double.IsNaN(posA.x) || double.IsInfinity(posA.x)) continue;
            _combined.SetVertex(vB, posA);
            TryCollapseToA(_combined, vA, vB);
        }

        // ── 1. Compact dữ liệu ban đầu ──
        DMesh3 compacted = new DMesh3(_combined, bCompact: true);

        // ── 2. BIỆN PHÁP MẠNH [FIX-v9-6]: SANITIZE TOPO TUYỆT ĐỐI CHO UCLOTH ──

        // ── Bước A.5: Hàn boundary vertex trùng vị trí (co-located weld) ────────────
        // [FIX-v10-1] Root cause của KeyNotFoundException trong UCMeshPreprocessor:
        // TryCollapseToA bị SKIP (non-manifold / winding conflict) để lại các cặp vertex
        // đã được SetVertex(vB, posA) — cùng tọa độ — nhưng topology chưa hàn.
        // UCMeshPreprocessor xây edge→UCTriangle dictionary: mỗi edge nội thất phải có
        // đúng 2 entry. Boundary gap (edge chỉ 1 tri) → tra cứu tri thứ hai → KeyNotFound.
        // Fix: quét toàn bộ boundary edge, nếu 2 endpoint của edge khác nhau nhưng cùng
        // vị trí (hoặc cùng vị trí với boundary vertex khác) → force-collapse topology.
        {
            bool weldedAny = true;
            int weldPass = 0;
            const int maxWeldPasses = 8;
            const double weldEps = 1e-5; // world-space threshold (meters)

            while (weldedAny && weldPass < maxWeldPasses)
            {
                weldedAny = false;
                weldPass++;

                // Xây spatial index cho boundary vertex
                var bverts = new List<int>();
                foreach (int eid in compacted.EdgeIndices())
                {
                    if (!compacted.IsBoundaryEdge(eid)) continue;
                    Index2i ev = compacted.GetEdgeV(eid);
                    if (!bverts.Contains(ev.a)) bverts.Add(ev.a);
                    if (!bverts.Contains(ev.b)) bverts.Add(ev.b);
                }
                if (bverts.Count == 0) break;

                // Build simple pairwise distance check (boundary thường nhỏ, O(N²) ok)
                var processed = new HashSet<int>();
                foreach (int vA in bverts)
                {
                    if (!compacted.IsVertex(vA) || processed.Contains(vA)) continue;
                    Vector3d pA = compacted.GetVertex(vA);

                    foreach (int vB in bverts)
                    {
                        if (vB == vA || !compacted.IsVertex(vB) || processed.Contains(vB)) continue;
                        Vector3d pB = compacted.GetVertex(vB);
                        if ((pA - pB).LengthSquared > weldEps * weldEps) continue;

                        // Co-located: thử collapse vB → vA
                        bool ok = TryCollapseToA(compacted, vA, vB);
                        if (!ok)
                        {
                            // TryCollapseToA từ chối — thử chiều ngược lại
                            ok = TryCollapseToA(compacted, vB, vA);
                        }
                        if (ok)
                        {
                            processed.Add(vB);
                            weldedAny = true;
                            break; // vA có thể đã thay đổi neighbors, restart inner loop
                        }
                    }
                }

                if (weldedAny)
                    compacted = new DMesh3(compacted, bCompact: true);
            }

            if (weldPass > 1)
                Debug.Log($"[MeshSewer] Finalize A.5: Đã hàn boundary co-located vertices ({weldPass - 1} pass).");
        }

        // Bước A: Loại bỏ tam giác Suy biến (Degenerate) & chứa NaN/Inf
        var badTris = new List<int>();
        foreach (int tid in compacted.TriangleIndices())
        {
            if (!compacted.IsTriangle(tid)) continue;
            Index3i tri = compacted.GetTriangle(tid);
            
            // Check đỉnh hợp lệ
            if (!compacted.IsVertex(tri.a) || !compacted.IsVertex(tri.b) || !compacted.IsVertex(tri.c) ||
                tri.a == tri.b || tri.b == tri.c || tri.a == tri.c)
            {
                badTris.Add(tid);
                continue;
            }

            // Check vị trí NaN/Inf
            Vector3d pA = compacted.GetVertex(tri.a);
            Vector3d pB = compacted.GetVertex(tri.b);
            Vector3d pC = compacted.GetVertex(tri.c);
            if (double.IsNaN(pA.x) || double.IsNaN(pB.x) || double.IsNaN(pC.x) ||
                double.IsInfinity(pA.x) || double.IsInfinity(pB.x) || double.IsInfinity(pC.x))
            {
                badTris.Add(tid);
                continue;
            }

            // Check diện tích tam giác quá nhỏ (nguyên nhân gây crash toán giải tích trong uCloth)
            double area = MathUtil.Area(pA, pB, pC);
            if (area < 1e-7) 
            {
                badTris.Add(tid);
            }
        }
        
        if (badTris.Count > 0)
        {
            Debug.LogWarning($"[MeshSewer] Finalize: Loại bỏ {badTris.Count} tam giác suy biến/NaN/diện tích siêu nhỏ.");
            foreach (int tid in badTris)
                compacted.RemoveTriangle(tid, false, false);
            compacted = new DMesh3(compacted, bCompact: true);
        }

        // Bước B: Xử lý Non-Manifold Edges và Trùng lặp hướng cạnh (Cực kỳ quan trọng cho uCloth)
        {
            var edgeRegistry = new Dictionary<(int, int), List<(int tid, bool forward)>>();
            foreach (int tid in compacted.TriangleIndices())
            {
                if (!compacted.IsTriangle(tid)) continue;
                Index3i tri = compacted.GetTriangle(tid);

                // Định nghĩa 3 cặp cạnh có hướng chuẩn của tam giác này
                var edgesList = new[] {
                    (tri.a, tri.b),
                    (tri.b, tri.c),
                    (tri.c, tri.a)
                };

                foreach (var (v0, v1) in edgesList)
                {
                    var key = v0 < v1 ? (v0, v1) : (v1, v0);
                    bool isForward = v0 < v1;

                    if (!edgeRegistry.ContainsKey(key)) edgeRegistry[key] = new List<(int, bool)>();
                    edgeRegistry[key].Add((tid, isForward));
                }
            }

            var trisToDestroy = new HashSet<int>();
            foreach (var kv in edgeRegistry)
            {
                var sharedList = kv.Value;
                
                // Trường hợp 1: T-Junction vật lý (> 2 tam giác chung cạnh)
                if (sharedList.Count > 2)
                {
                    foreach (var item in sharedList) trisToDestroy.Add(item.tid);
                }
                // Trường hợp 2: Đúng 2 tam giác chung cạnh nhưng trùng Winding chuẩn (Cả hai cùng xuôi hoặc cùng ngược)
                else if (sharedList.Count == 2)
                {
                    if (sharedList[0].forward == sharedList[1].forward)
                    {
                        // Xung đột hướng nghiêm trọng! Hủy tam giác thứ hai để giữ an toàn cho uCloth Preprocessor
                        trisToDestroy.Add(sharedList[1].tid);
                    }
                }
            }

            if (trisToDestroy.Count > 0)
            {
                Debug.LogWarning($"[MeshSewer] Finalize: Hủy {trisToDestroy.Count} tam giác gây xung đột topo/Winding trùng.");
                foreach (int tid in trisToDestroy)
                    if (compacted.IsTriangle(tid)) compacted.RemoveTriangle(tid, false, false);
                compacted = new DMesh3(compacted, bCompact: true);
            }
        }

        // Bước C: Sửa Winding Order nhất quán bằng Flood-fill 2-pass an toàn
        // [FIX-BUG-D] DMesh3 không có SetTriangle(int, Index3i) → dùng RemoveTriangle +
        // AppendTriangle. Để tránh ID thay đổi giữa chừng làm BFS sai, dùng 2-pass:
        //   Pass 1 — BFS thu thập tất cả tid cần flip (không thay đổi mesh).
        //   Pass 2 — Apply toàn bộ flip một lượt rồi compact lại.
        {
            bool orientationChanged = false;
            var visitedTris  = new HashSet<int>();
            var flipNeeded   = new HashSet<int>();
            var componentLists = new List<List<int>>();  // dùng cho global orientation (Bước D)

            // Pass 1 – BFS
            foreach (int startTid in compacted.TriangleIndices().ToList())
            {
                if (!compacted.IsTriangle(startTid) || visitedTris.Contains(startTid)) continue;

                var currentComponent = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(startTid);
                visitedTris.Add(startTid);
                currentComponent.Add(startTid);

                while (queue.Count > 0)
                {
                    int curTid = queue.Dequeue();
                    if (!compacted.IsTriangle(curTid)) continue;
                    Index3i curTri = compacted.GetTriangle(curTid);

                    // Winding hiệu dụng của curTid (có thể đã được đánh dấu flip)
                    bool curFlipped = flipNeeded.Contains(curTid);
                    Index3i effectiveCur = curFlipped
                        ? new Index3i(curTri.a, curTri.c, curTri.b)
                        : curTri;

                    Index3i triEdges = compacted.GetTriEdges(curTid);
                    foreach (int eid in new[] { triEdges.a, triEdges.b, triEdges.c })
                    {
                        if (eid == DMesh3.InvalidID) continue;
                        Index2i edgeTris = compacted.GetEdgeT(eid);
                        int neighborTid = edgeTris.a == curTid ? edgeTris.b : edgeTris.a;
                        if (neighborTid == DMesh3.InvalidID || visitedTris.Contains(neighborTid)) continue;
                        if (!compacted.IsTriangle(neighborTid)) continue;

                        visitedTris.Add(neighborTid);
                        currentComponent.Add(neighborTid);

                        Index3i nTri = compacted.GetTriangle(neighborTid);
                        if (IsWindingInconsistent(effectiveCur, nTri))
                            flipNeeded.Add(neighborTid);

                        queue.Enqueue(neighborTid);
                    }
                }
                componentLists.Add(currentComponent);
            }

            // Pass 2 – Apply flip: Remove + AppendTriangle (API hợp lệ của DMesh3)
            var tidsToFlip = flipNeeded.Where(t => compacted.IsTriangle(t)).ToList();
            foreach (int tid in tidsToFlip)
            {
                Index3i t = compacted.GetTriangle(tid);
                compacted.RemoveTriangle(tid, false, false);
                int res = compacted.AppendTriangle(t.a, t.c, t.b);
                if (res < 0)
                    Debug.LogWarning($"[MeshSewer] Finalize Bước C: flip tid={tid} thất bại (AppendTriangle res={res}).");
                else
                    orientationChanged = true;
            }
            if (tidsToFlip.Count > 0)
                compacted = new DMesh3(compacted, bCompact: true); // re-compact sau flip

            // Bước D: Ép định hướng theo mesh gốc goA (reference-normal)
            // [FIX-FLIP] Signed volume chỉ đúng với closed mesh. Vải sau khi khâu thường
            // vẫn còn boundary edge (open mesh) → signed volume phụ thuộc origin, có thể
            // âm dù winding đúng → flip nhầm toàn mesh → horizon bị lật.
            // Fix: so sánh average normal của compacted với average normal của mesh gốc goA.
            // Nếu dot < 0 → flip toàn bộ.
            {
                // Thu thập reference normal từ mesh gốc goA (world space)
                Vector3d referenceNormal = Vector3d.Zero;
                {
                    var srcMf = _goA.GetComponent<MeshFilter>();
                    Mesh srcMesh = srcMf != null ? (srcMf.sharedMesh ?? srcMf.mesh) : null;
                    if (srcMesh != null)
                    {
                        Vector3[] srcVerts = srcMesh.vertices;
                        int[]     srcTris  = srcMesh.triangles;
                        Transform srcTf    = _goA.transform;
                        int sampleCount    = 0;
                        for (int t = 0; t < srcTris.Length && sampleCount < 16; t += 3)
                        {
                            Vector3 wA = srcTf.TransformPoint(srcVerts[srcTris[t]]);
                            Vector3 wB = srcTf.TransformPoint(srcVerts[srcTris[t + 1]]);
                            Vector3 wC = srcTf.TransformPoint(srcVerts[srcTris[t + 2]]);
                            Vector3 n  = Vector3.Cross(wB - wA, wC - wA);
                            if (n.sqrMagnitude > 1e-10f)
                            {
                                referenceNormal += new Vector3d(n.x, n.y, n.z);
                                sampleCount++;
                            }
                        }
                        if (sampleCount > 0) referenceNormal.Normalize();
                    }
                }

                if (referenceNormal.LengthSquared > 0.01)
                {
                    // Tính average normal của compacted (world space, tối đa 16 tam giác)
                    Vector3d compactedNormal = Vector3d.Zero;
                    int sampleC = 0;
                    foreach (int tid in compacted.TriangleIndices())
                    {
                        if (!compacted.IsTriangle(tid) || sampleC >= 16) continue;
                        Index3i tri = compacted.GetTriangle(tid);
                        Vector3d pA = compacted.GetVertex(tri.a);
                        Vector3d pB = compacted.GetVertex(tri.b);
                        Vector3d pC = compacted.GetVertex(tri.c);
                        Vector3d n  = (pB - pA).Cross(pC - pA);
                        if (n.LengthSquared > 1e-10) { compactedNormal += n; sampleC++; }
                    }
                    if (sampleC > 0) compactedNormal.Normalize();

                    if (compactedNormal.Dot(referenceNormal) < 0)
                    {
                        // Normal ngược chiều gốc → flip toàn bộ mesh
                        Debug.Log("[MeshSewer] Finalize D: Normal ngược chiều goA → flip toàn mesh.");
                        foreach (int tid in compacted.TriangleIndices().ToList())
                        {
                            if (!compacted.IsTriangle(tid)) continue;
                            Index3i tri = compacted.GetTriangle(tid);
                            compacted.RemoveTriangle(tid, false, false);
                            compacted.AppendTriangle(tri.a, tri.c, tri.b);
                        }
                        compacted = new DMesh3(compacted, bCompact: true);
                        orientationChanged = true;
                    }
                    else
                    {
                        Debug.Log("[MeshSewer] Finalize D: Normal khớp mesh gốc — giữ nguyên.");
                    }
                }
                else
                {
                    // Không lấy được reference → bỏ qua bước D hoàn toàn
                    // (an toàn hơn là flip sai bằng signed volume với open mesh)
                    Debug.LogWarning("[MeshSewer] Finalize D: Không có reference normal từ goA — bỏ qua global orientation.");
                }
            }

            if (orientationChanged)
                Debug.Log("[MeshSewer] Finalize: Đã tối ưu hóa và đồng bộ Winding Order.");
        }

        // Dọn dẹp isolated vertex sinh ra sau khi lọc bỏ tam giác lỗi
        var isolatedVerts = new List<int>();
        foreach (int vid in compacted.VertexIndices())
        {
            if (!compacted.IsVertex(vid)) continue;
            if (compacted.GetVtxTriangleCount(vid) == 0)
                isolatedVerts.Add(vid);
        }
        if (isolatedVerts.Count > 0)
        {
            foreach (int vid in isolatedVerts)
                compacted.RemoveVertex(vid, true, false);
            compacted = new DMesh3(compacted, bCompact: true);
        }

        if (compacted.VertexCount == 0 || compacted.TriangleCount == 0)
        {
            Debug.LogError("[MeshSewer] Finalize: Mesh trống sau bước lọc nghiêm ngặt — hủy.");
            return null;
        }

        if (compacted.VertexCount > 65535)
        {
            Debug.LogError($"[MeshSewer] {compacted.VertexCount} verts > 65535 (Vượt giới hạn uCloth UShort)!");
            return null;
        }

        // ── Bước E: Validate 2-manifold nghiêm ngặt theo cách UCMeshPreprocessor duyệt ──
        // [FIX-v10-1] UCMeshPreprocessor xây edge→UCTriangle dict: mỗi edge nội thất phải có
        // đúng 2 tam giác. Nếu còn edge chỉ có 1 tam giác (boundary gap sau SKIP collapse)
        // → KeyNotFoundException. Kiểm tra trước khi trao cho UCCloth và log chi tiết.
        {
            // Mô phỏng cách UCMeshPreprocessor duyệt: đếm số tam giác chia sẻ từng edge
            var edgeTriCount = new Dictionary<(int, int), int>();
            int nonManifoldEdgeCount = 0;

            foreach (int tid in compacted.TriangleIndices())
            {
                if (!compacted.IsTriangle(tid)) continue;
                Index3i tri = compacted.GetTriangle(tid);
                var pairs = new (int, int)[]
                {
                    tri.a < tri.b ? (tri.a, tri.b) : (tri.b, tri.a),
                    tri.b < tri.c ? (tri.b, tri.c) : (tri.c, tri.b),
                    tri.a < tri.c ? (tri.a, tri.c) : (tri.c, tri.a)
                };
                foreach (var p in pairs)
                {
                    edgeTriCount.TryGetValue(p, out int cnt);
                    edgeTriCount[p] = cnt + 1;
                }
            }

            var trisToRemoveE = new HashSet<int>();
            foreach (var kv in edgeTriCount)
            {
                if (kv.Value == 1)
                {
                    // Boundary edge còn sót — UCMeshPreprocessor sẽ crash ở đây
                    // Xác định triangle sở hữu edge này để xem xét loại bỏ
                    nonManifoldEdgeCount++;
                }
                else if (kv.Value > 2)
                {
                    // T-junction — xóa tất cả triangle dùng edge này
                    foreach (int tid in compacted.TriangleIndices())
                    {
                        if (!compacted.IsTriangle(tid)) continue;
                        Index3i tri = compacted.GetTriangle(tid);
                        var pairs = new (int, int)[]
                        {
                            tri.a < tri.b ? (tri.a, tri.b) : (tri.b, tri.a),
                            tri.b < tri.c ? (tri.b, tri.c) : (tri.c, tri.b),
                            tri.a < tri.c ? (tri.a, tri.c) : (tri.c, tri.a)
                        };
                        foreach (var p in pairs)
                            if (p == kv.Key) { trisToRemoveE.Add(tid); break; }
                    }
                }
            }

            if (trisToRemoveE.Count > 0)
            {
                foreach (int tid in trisToRemoveE)
                    if (compacted.IsTriangle(tid)) compacted.RemoveTriangle(tid, false, false);
                compacted = new DMesh3(compacted, bCompact: true);
                Debug.LogWarning($"[MeshSewer] Finalize E: Xóa {trisToRemoveE.Count} triangle T-junction còn sót.");
            }

            if (nonManifoldEdgeCount > 0)
                Debug.LogWarning($"[MeshSewer] Finalize E: Còn {nonManifoldEdgeCount} boundary edge sau tất cả các bước sanitize. " +
                                 $"UCMeshPreprocessor có thể gặp lỗi nếu preprocessorType dùng bending edges.");
            else
                Debug.Log($"[MeshSewer] Finalize E: Mesh đạt chuẩn 2-manifold — {compacted.TriangleCount} tris, " +
                          $"{compacted.VertexCount} verts.");
        }

        MeshNormals.QuickCompute(compacted);

        // ── 3. Khởi tạo Unity GameObject và Mesh ──
        var go = new GameObject(newName);
        go.transform.SetPositionAndRotation(_goA.transform.position, _goA.transform.rotation);
        go.transform.localScale = _goA.transform.localScale;
        go.tag   = _goA.tag;
        go.layer = _goA.layer;

        Mesh unityMesh = new Mesh();
        unityMesh.name = newName + "_Mesh";

        var vertices  = new List<Vector3>();
        var triangles = new List<int>();
        var g3ToRenderMap = new Dictionary<int, int>();

        foreach (int vid in compacted.VertexIndices())
        {
            Vector3d p       = compacted.GetVertex(vid);
            Vector3 worldPt  = new Vector3((float)p.x, (float)p.y, (float)p.z);
            Vector3 localPt  = go.transform.InverseTransformPoint(worldPt);
            int newIdx       = vertices.Count;
            vertices.Add(localPt);
            g3ToRenderMap[vid] = newIdx;
        }

        foreach (int tid in compacted.TriangleIndices())
        {
            if (!compacted.IsTriangle(tid)) continue;
            Index3i tri = compacted.GetTriangle(tid);
            if (g3ToRenderMap.TryGetValue(tri.a, out int ia) &&
                g3ToRenderMap.TryGetValue(tri.b, out int ib) &&
                g3ToRenderMap.TryGetValue(tri.c, out int ic))
            {
                if (ia == ib || ib == ic || ia == ic) continue;
                triangles.Add(ia); triangles.Add(ib); triangles.Add(ic);
            }
        }

        // Kiểm tra an toàn NaN/Inf cuối
        for (int i = 0; i < vertices.Count; i++)
        {
            Vector3 v = vertices[i];
            if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
                float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z))
            {
                Debug.LogError($"[MeshSewer] Finalize ABORT: vertex[{i}]={v} vẫn còn NaN/Inf. Hủy.");
                Object.Destroy(go);
                return null;
            }
        }

        unityMesh.indexFormat = vertices.Count > 65535
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;

        unityMesh.SetVertices(vertices);
        unityMesh.SetTriangles(triangles, 0);
        unityMesh.RecalculateNormals();
        unityMesh.RecalculateBounds();
        unityMesh.RecalculateTangents();

        // ── [AREA-LOG] Diện tích mesh SAU khi khâu ──
        // unityMesh đang ở local-space của 'go' (go.transform.localScale = _goA.transform.localScale)
        // → dùng ClothAreaCalculator.CalculateAreaFromMesh để nhân lại lossyScale ra world-space.
        float areaBeforeTotal = _isSelfSew ? _areaBeforeA : (_areaBeforeA + _areaBeforeB);
        float areaAfter       = ClothAreaCalculator.CalculateAreaFromMesh(unityMesh, go.transform.lossyScale);
        float areaSeamLost    = Mathf.Max(0f, areaBeforeTotal - areaAfter);
        float seamRatio       = areaBeforeTotal > 0f ? areaSeamLost / areaBeforeTotal : 0f;

        Debug.Log($"<color=cyan>[MeshSewer][Area] ===== BÁO CÁO DIỆN TÍCH KHÂU ('{newName}') =====\n" +
                  $"  A trước khâu : {_areaBeforeA:F6} m² ({_areaBeforeA * 10000f:F2} cm²)\n" +
                  (_isSelfSew ? "" :
                  $"  B trước khâu : {_areaBeforeB:F6} m² ({_areaBeforeB * 10000f:F2} cm²)\n" +
                  $"  Tổng A+B     : {areaBeforeTotal:F6} m² ({areaBeforeTotal * 10000f:F2} cm²)\n") +
                  $"  SAU khâu     : {areaAfter:F6} m² ({areaAfter * 10000f:F2} cm²)\n" +
                  $"  Seam mất     : {areaSeamLost:F6} m² ({areaSeamLost * 10000f:F2} cm²)  [{seamRatio * 100f:F2}%]</color>");

        var areaData = go.AddComponent<ClothAreaData>();
        areaData.originalArea = areaBeforeTotal;
        areaData.pieceArea    = areaAfter;
        areaData.seamArea     = areaSeamLost;
        areaData.seamRatio    = Mathf.Clamp01(seamRatio);

        go.AddComponent<MeshFilter>().mesh = unityMesh;

        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = _goA.GetComponent<MeshRenderer>()?.sharedMaterials ?? new Material[0];

        var mc = go.AddComponent<MeshCollider>();
        mc.sharedMesh = unityMesh;
        mc.convex     = false;

        var rb = go.AddComponent<Rigidbody>();
        rb.useGravity  = true;
        rb.isKinematic = true;

        // ── 4. Đồng bộ cấu hình dữ liệu UCCloth vật lý ──
        var ucBase = _ucA ?? _ucB;
        if (ucBase != null)
        {
            go.SetActive(false);

            var newCloth = go.AddComponent<UCloth.UCCloth>();
            newCloth.preprocessorType     = ucBase.preprocessorType;
            newCloth.materialProperties   = ucBase.materialProperties;
            newCloth.simulationProperties = ucBase.simulationProperties;
            newCloth.qualityProperties    = ucBase.qualityProperties;
            newCloth.collisionProperties  = ucBase.collisionProperties;
            newCloth.thickness            = ucBase.thickness;
            newCloth.offsetFront          = ucBase.offsetFront;
            newCloth.smoothing            = ucBase.smoothing;

            newCloth.sphereColliders  = MergeColliderArrays(_ucA?.sphereColliders,  _isSelfSew ? null : _ucB?.sphereColliders,  _goA, _isSelfSew ? null : _goB);
            newCloth.capsuleColliders = MergeColliderArrays(_ucA?.capsuleColliders, _isSelfSew ? null : _ucB?.capsuleColliders, _goA, _isSelfSew ? null : _goB);
            newCloth.cubeColliders    = MergeColliderArrays(_ucA?.cubeColliders,    _isSelfSew ? null : _ucB?.cubeColliders,    _goA, _isSelfSew ? null : _goB);

            if (newCloth.sphereColliders  == null) newCloth.sphereColliders  = new SphereCollider[0];
            if (newCloth.capsuleColliders == null) newCloth.capsuleColliders = new CapsuleCollider[0];
            if (newCloth.cubeColliders    == null) newCloth.cubeColliders    = new BoxCollider[0];

            var mergedPins = new List<Collider>();
            foreach (var src in new[] { _ucA?.pinColliders, _isSelfSew ? null : _ucB?.pinColliders })
            {
                if (src == null) continue;
                foreach (var c in src)
                {
                    if ((UnityEngine.Object)c == null) continue;
                    if (c.transform.IsChildOf(_goA.transform)) continue;
                    if (!_isSelfSew && _goB != null && c.transform.IsChildOf(_goB.transform)) continue;
                    if (!mergedPins.Contains(c)) mergedPins.Add(c);
                }
            }
            newCloth.pinColliders = mergedPins;

            go.SetActive(true);
            Debug.Log($"[MeshSewer] UCCloth đã đồng bộ cho '{newName}'.");
        }

        // ── 5. Khôi phục tương tác Laser Grabber trong VR ──
        // SỬA: Khôi phục tương tác theo cơ chế của UClothLaserGrabber3 sau khi khâu
        var grabSrc = _goA.GetComponent<UClothLaserGrabber3>()
                      ?? (_isSelfSew ? null : _goB?.GetComponent<UClothLaserGrabber3>());
        if (grabSrc != null)
        {
            var gr = go.AddComponent<UClothLaserGrabber3>();
            gr.vrController       = grabSrc.vrController;
            gr.triggerAction      = grabSrc.triggerAction;
            gr.rightController    = grabSrc.rightController;
            gr.rightTriggerAction = grabSrc.rightTriggerAction;
            gr.pullForce          = grabSrc.pullForce;
            gr.sphereSize         = grabSrc.sphereSize;
            gr.hoverColor         = grabSrc.hoverColor;
            gr.grabColor          = grabSrc.grabColor;
        }

        // ── 6. Khôi phục UClothPinner2 cho mesh khâu mới ──
        // SỬA: Thêm UClothPinner2 SAU khi UClothLaserGrabber3 đã được AddComponent,
        // vì UClothPinner2.Start() dùng GetComponent<UClothLaserGrabber3>() trên cùng GameObject.
        var pinSrc = _goA.GetComponent<UClothPinner2>()
                     ?? (_isSelfSew ? null : _goB?.GetComponent<UClothPinner2>());
        if (pinSrc != null)
        {
            var pinner = go.AddComponent<UClothPinner2>();

            // Gán grabber mới vừa thêm vào go (nếu có), không giữ ref của mesh gốc
            pinner.grabber         = go.GetComponent<UClothLaserGrabber3>();

            // Copy tham chiếu ngoài + cài đặt từ pinner nguồn
            pinner.mannequinAnchor = pinSrc.mannequinAnchor;
            pinner.pinLogger       = pinSrc.pinLogger;
            pinner.pinAction       = pinSrc.pinAction;
            pinner.pinForce        = pinSrc.pinForce;
            pinner.pinDamping      = pinSrc.pinDamping;
            pinner.maxPinSpeed     = pinSrc.maxPinSpeed;
            pinner.snapThreshold   = pinSrc.snapThreshold;
            pinner.debugMode       = pinSrc.debugMode;

            Debug.Log($"[MeshSewer] UClothPinner2 đã đồng bộ cho '{newName}'.");
        }

        SafeDisableUCloth(_goA);
        if (!_isSelfSew && _goB != null) SafeDisableUCloth(_goB);

        Debug.Log($"<color=green>[MeshSewer] ✓ Finalize thành công:</color> '{newName}' ({unityMesh.vertexCount} verts).");

        OnSeamCompleted?.Invoke(go);
        return go;
    }

    // ── FindSelfSewSecondaryHit (Fix BUG-3: sleeve / 1-loop case) ────────
    /// <summary>
    /// Tìm điểm B trên mesh tự khâu để khâu với điểm A.
    /// [FIX BUG-3] Khi chỉ còn 1 boundary loop (ống tay áo gần kín),
    /// tìm đỉnh đủ xa trên CÙNG loop thay vì trả về hitA.
    /// </summary>
    public static Vector3 FindSelfSewSecondaryHit(
        GameObject go, Vector3 hitA, float sewRadius,
        bool useSecondaryRay = true, float searchRadius = 0.3f)
    {
        if (!useSecondaryRay) return hitA;

        var mf = go.GetComponent<MeshFilter>();
        if (mf == null) return hitA;

        Mesh      mesh  = mf.mesh;
        Vector3[] verts = mesh.vertices;
        int[]     tris  = mesh.triangles;
        Transform tf    = go.transform;

        // Xây boundary adjacency
        var edgeCount = new Dictionary<(int, int), int>();
        for (int t = 0; t < tris.Length; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            AddEdge(edgeCount, a, b); AddEdge(edgeCount, b, c); AddEdge(edgeCount, a, c);
        }

        var boundarySet = new HashSet<int>();
        foreach (var kv in edgeCount)
            if (kv.Value == 1) { boundarySet.Add(kv.Key.Item1); boundarySet.Add(kv.Key.Item2); }

        if (boundarySet.Count == 0)
        {
            // Mesh hoàn toàn kín — báo rõ, không tự khâu
            Debug.LogWarning("[MeshSewer] FindSelfSewSecondaryHit: Mesh đã kín, không còn boundary vertex.");
            return hitA; // caller phải kiểm tra HasOpenBoundary trước khi dùng kết quả này
        }

        // Build world pos
        var bWorldPos = new Dictionary<int, Vector3>();
        foreach (int v in boundarySet)
            if (v >= 0 && v < verts.Length)
                bWorldPos[v] = tf.TransformPoint(verts[v]);

        if (bWorldPos.Count == 0) return hitA;

        // Tìm đỉnh gần hitA nhất (nearestToA)
        int   nearestToA  = -1;
        float nearestDist = float.MaxValue;
        foreach (var kv in bWorldPos)
        {
            float d = Vector3.Distance(kv.Value, hitA);
            if (d < nearestDist) { nearestDist = d; nearestToA = kv.Key; }
        }
        if (nearestToA < 0) return hitA;

        // Build boundary loop adjacency và flood-fill loop của A
        var adjBoundary = BuildBoundaryAdjacency(edgeCount, boundarySet);
        var compA       = FloodFill(nearestToA, adjBoundary);

        // Thử tìm bestB trên loop KHÁC (two-loop case: hai mép hở riêng biệt)
        int   bestB    = -1;
        float bestDist = float.MaxValue;
        foreach (var kv in bWorldPos)
        {
            if (compA.Contains(kv.Key)) continue;
            float d = Vector3.Distance(kv.Value, hitA);
            if (d < bestDist) { bestDist = d; bestB = kv.Key; }
        }

        // [FIX-v9-3] Chỉ còn 1 loop (ống tay áo): dùng centroid projection thay vì
        // lọc khoảng cách tuyệt đối. Tính centroid của toàn bộ boundary loop, sau đó
        // hướng từ centroid → nearestToA. Đỉnh bestB là đỉnh có dot product nhỏ nhất
        // với hướng đó (= phía đối diện của loop), tức là mép kia của ống tay áo.
        if (bestB < 0)
        {
            // Tính centroid boundary loop
            Vector3 centroid = Vector3.zero;
            foreach (var kv in bWorldPos) centroid += kv.Value;
            centroid /= bWorldPos.Count;

            // Hướng centroid → nearestToA
            Vector3 posNearestA = bWorldPos[nearestToA];
            Vector3 dirA = (posNearestA - centroid);
            float   dirALen = dirA.magnitude;

            // Nếu mesh gần như phẳng (centroid == nearestToA), fallback khoảng cách
            if (dirALen < 1e-4f)
            {
                float antiWeldRadius = sewRadius * 1.5f;
                bestDist = float.MaxValue;
                foreach (var kv in bWorldPos)
                {
                    float d = Vector3.Distance(kv.Value, hitA);
                    if (d > antiWeldRadius && d <= searchRadius && d < bestDist)
                    {
                        bestDist = d; bestB = kv.Key;
                    }
                }
            }
            else
            {
                dirA /= dirALen; // normalize
                float bestDot = float.MaxValue;
                foreach (var kv in bWorldPos)
                {
                    if (kv.Key == nearestToA) continue;
                    float d = Vector3.Distance(kv.Value, hitA);
                    if (d > searchRadius) continue;          // quá xa
                    if (d < sewRadius * 0.5f) continue;      // quá gần, tránh self-collapse

                    Vector3 dirV = ((kv.Value - centroid).normalized);
                    float dot = Vector3.Dot(dirA, dirV);
                    if (dot < bestDot) // càng nhỏ = càng đối diện
                    {
                        bestDot = dot; bestDist = d; bestB = kv.Key;
                    }
                }
            }

            if (bestB >= 0)
                Debug.Log($"[MeshSewer] Single-loop self-sew (ống tay áo): bestB dist={bestDist:F4}");
            else
                Debug.LogWarning("[MeshSewer] Không tìm được bestB trong loop duy nhất — mesh có thể đã kín.");
        }

        if (bestB < 0 || !bWorldPos.ContainsKey(bestB)) return hitA;
        return bWorldPos[bestB];
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static int FindNearestLoopIdx(
        List<(Vector3d center, List<int> verts)> centroids,
        Vector3d seed, int excludeIdx = -1)
    {
        int    best     = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < centroids.Count; i++)
        {
            if (i == excludeIdx) continue;
            double d = (centroids[i].center - seed).Length;
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    private static bool TryCollapseToA(DMesh3 mesh, int vA, int vB)
    {
        if (!mesh.IsVertex(vA) || !mesh.IsVertex(vB)) return false;

        var trisOfB = mesh.VtxTrianglesItr(vB).ToList();
        if (trisOfB.Count == 0)
        {
            // vB đang bị cô lập — xóa luôn để tránh zombie vertex
            if (mesh.IsVertex(vB)) mesh.RemoveVertex(vB, true, false);
            return false;
        }

        // ── DRY-RUN: build danh sách rebuild VÀ kiểm tra non-manifold TRƯỚC khi xóa bất cứ gì ──
        // Theo dõi số lần mỗi edge (chuẩn hóa a<b) sẽ xuất hiện trong toRebuild.
        // Một edge trong mesh 2-manifold chỉ được dùng bởi tối đa 2 triangle.
        // Nếu edge đã có 1 face ngoài trisOfB + 1 face trong toRebuild = 2 → ok (đóng seam).
        // Nếu edge đã có 1 face ngoài + ≥2 trong toRebuild → sẽ thành T-junction → ABORT.
        // Nếu edge chưa tồn tại + ≥2 trong toRebuild → cũng T-junction → ABORT.
        var trisOfBSet = new HashSet<int>(trisOfB);
        var toRebuild  = new List<(int a, int b, int c, int gid)>();

        // [FIX-v9-1] Track số lần edge mới sẽ được toRebuild đăng ký
        var newEdgesInRebuild = new Dictionary<(int, int), int>();

        foreach (int tid in trisOfB)
        {
            if (!mesh.IsTriangle(tid)) continue;
            Index3i tri = mesh.GetTriangle(tid);
            int ra  = tri.a == vB ? vA : tri.a;
            int rb  = tri.b == vB ? vA : tri.b;
            int rc  = tri.c == vB ? vA : tri.c;
            int gid = mesh.HasTriangleGroups ? mesh.GetTriangleGroup(tid) : -1;

            if (!mesh.IsVertex(ra) || !mesh.IsVertex(rb) || !mesh.IsVertex(rc)) continue;
            if (ra == rb || rb == rc || ra == rc) continue; // degenerate

            // Kiểm tra từng edge của triangle rebuild
            bool edgesOk = true;
            var edgePairs = new (int, int)[]
            {
                (System.Math.Min(ra,rb), System.Math.Max(ra,rb)),
                (System.Math.Min(rb,rc), System.Math.Max(rb,rc)),
                (System.Math.Min(ra,rc), System.Math.Max(ra,rc))
            };

            foreach (var ekey in edgePairs)
            {
                // Đếm face ngoài trisOfB đang dùng edge này
                int externalCount = 0;
                int eid = mesh.FindEdge(ekey.Item1, ekey.Item2);
                if (eid != DMesh3.InvalidID)
                {
                    Index2i edgeTris = mesh.GetEdgeT(eid);
                    if (edgeTris.a != DMesh3.InvalidID && !trisOfBSet.Contains(edgeTris.a)) externalCount++;
                    if (edgeTris.b != DMesh3.InvalidID && !trisOfBSet.Contains(edgeTris.b)) externalCount++;
                }

                // Đếm face trong toRebuild (đã đăng ký trước) dùng edge này
                newEdgesInRebuild.TryGetValue(ekey, out int rebuildCount);

                // Tổng sau khi thêm triangle này = externalCount + rebuildCount + 1
                // Nếu > 2 → T-junction → ABORT
                if (externalCount + rebuildCount + 1 > 2)
                {
                    edgesOk = false;
                    break;
                }
            }

            if (!edgesOk)
            {
                // ABORT toàn bộ collapse này — không xóa gì, giữ nguyên topology
                Debug.LogWarning($"[MeshSewer] TryCollapseToA: SKIP vA={vA} vB={vB} — non-manifold edge, topology được giữ nguyên.");
                return false;
            }

            // [FIX-BUG-C] Kiểm tra winding với external neighbor để tránh flip cục bộ
            // gây lực bending ngược chiều → diverge NaN ở frame sau.
            Index3i rebuildTri = new Index3i(ra, rb, rc);
            bool windingOk = true;
            foreach (var ekey in edgePairs)
            {
                int weid = mesh.FindEdge(ekey.Item1, ekey.Item2);
                if (weid == DMesh3.InvalidID) continue;
                Index2i wEdgeTris = mesh.GetEdgeT(weid);
                foreach (int extTid in new[] { wEdgeTris.a, wEdgeTris.b })
                {
                    if (extTid == DMesh3.InvalidID) continue;
                    if (trisOfBSet.Contains(extTid)) continue;
                    if (!mesh.IsTriangle(extTid)) continue;
                    Index3i extTri = mesh.GetTriangle(extTid);
                    if (IsWindingInconsistent(rebuildTri, extTri))
                    {
                        // Thử flip: hoán vị rb ↔ rc
                        Index3i flipped = new Index3i(ra, rc, rb);
                        if (!IsWindingInconsistent(flipped, extTri))
                            rebuildTri = flipped;
                        else
                        {
                            windingOk = false;
                            break;
                        }
                    }
                }
                if (!windingOk) break;
            }
            if (!windingOk)
            {
                Debug.LogWarning($"[MeshSewer] TryCollapseToA: SKIP vA={vA} vB={vB} — winding conflict với neighbor ngoài.");
                return false;
            }

            // Đăng ký edges của triangle này vào bảng theo dõi
            foreach (var ekey in edgePairs)
            {
                newEdgesInRebuild.TryGetValue(ekey, out int c);
                newEdgesInRebuild[ekey] = c + 1;
            }

            toRebuild.Add((rebuildTri.a, rebuildTri.b, rebuildTri.c, gid));
        }

        // ── COMMIT ──────────────────────────────────────────────────────────
        // Trường hợp toRebuild rỗng: toàn bộ triangle của vB đều degenerate sau khi thay vB→vA.
        // Xảy ra khi vA và vB kề nhau (share edge) → triangle chứa cả hai thành (vA,vA,X).
        // Đây là collapse HỢP LỆ (seam hoàn chỉnh): xóa degenerate tris và vB, không rebuild.
        if (toRebuild.Count == 0)
        {
            bool allDegenerate = true;
            foreach (int tid in trisOfB)
            {
                if (!mesh.IsTriangle(tid)) continue;
                Index3i tri = mesh.GetTriangle(tid);
                int ra = tri.a == vB ? vA : tri.a;
                int rb = tri.b == vB ? vA : tri.b;
                int rc = tri.c == vB ? vA : tri.c;
                if (ra != rb && rb != rc && ra != rc) { allDegenerate = false; break; }
            }
            if (!allDegenerate) return false; // có triangle hợp lệ bị lọc nhầm — không xóa gì

            foreach (int tid in trisOfB)
                if (mesh.IsTriangle(tid))
                    mesh.RemoveTriangle(tid, false, false);
            if (mesh.IsVertex(vB) && mesh.GetVtxTriangleCount(vB) == 0)
                mesh.RemoveVertex(vB, true, false);
            return true;
        }

        foreach (int tid in trisOfB)
            if (mesh.IsTriangle(tid))
                mesh.RemoveTriangle(tid, false, false);

        foreach (var (a, b, c, gid) in toRebuild)
        {
            int res = mesh.AppendTriangle(a, b, c, gid);
            if (res < 0)
                Debug.LogError($"[MeshSewer] TryCollapseToA: UNEXPECTED reject ({a},{b},{c}) res={res} — dry-run có lỗi!");
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
        a.CopyTo(result, 0); b.CopyTo(result, a.Length);
        return result;
    }

    private static void SafeDisableUCloth(GameObject go)
    {
        if (go == null) return;
        var uc = go.GetComponent<UCloth.UCCloth>();
        if (uc != null) uc.enabled = false;
        go.SetActive(false);
    }

    private static List<List<int>> ExtractBoundaryLoops(DMesh3 mesh, HashSet<int> boundaryVerts)
    {
        var adj = new Dictionary<int, List<int>>();
        foreach (int vid in boundaryVerts)
            adj[vid] = new List<int>();

        foreach (int eid in mesh.EdgeIndices())
        {
            if (!mesh.IsBoundaryEdge(eid)) continue;
            Index2i ev = mesh.GetEdgeV(eid);
            if (adj.ContainsKey(ev.a)) adj[ev.a].Add(ev.b);
            if (adj.ContainsKey(ev.b)) adj[ev.b].Add(ev.a);
        }

        var visited = new HashSet<int>();
        var loops   = new List<List<int>>();

        foreach (int start in boundaryVerts)
        {
            if (visited.Contains(start)) continue;

            var loop  = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(start); visited.Add(start);

            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                loop.Add(cur);
                if (!adj.ContainsKey(cur)) continue;
                foreach (int nb in adj[cur])
                    if (!visited.Contains(nb)) { visited.Add(nb); queue.Enqueue(nb); }
            }
            loops.Add(loop);
        }
        return loops;
    }
private static bool IsWindingInconsistent(Index3i tA, Index3i tB)
    {
        // Tìm 2 đỉnh chung tạo nên cạnh biên kề nhau giữa 2 tam giác
        int sharedCount = 0;
        int[] shared = new int[2];
        
        int[] arrA = new int[] { tA.a, tA.b, tA.c };
        int[] arrB = new int[] { tB.a, tB.b, tB.c };

        foreach (int vA in arrA)
        {
            foreach (int vB in arrB)
            {
                if (vA == vB)
                {
                    if (sharedCount < 2) shared[sharedCount] = vA;
                    sharedCount++;
                }
            }
        }

        if (sharedCount != 2) return false; // Không chung cạnh hoặc trùng lặp dị thường

        // Trong mesh định hướng chuẩn (Manifold Oriented), một cạnh đi từ X -> Y ở tam giác này 
        // thì bắt buộc phải đi từ Y -> X ở tam giác kề.
        int idxA_0 = System.Array.IndexOf(arrA, shared[0]);
        int idxA_1 = System.Array.IndexOf(arrA, shared[1]);
        bool isForwardA = ((idxA_0 + 1) % 3 == idxA_1);

        int idxB_0 = System.Array.IndexOf(arrB, shared[0]);
        int idxB_1 = System.Array.IndexOf(arrB, shared[1]);
        bool isForwardB = ((idxB_0 + 1) % 3 == idxB_1);

        // Nếu cả 2 đều chạy xuôi hoặc cả 2 đều chạy ngược hướng trên cùng một đoạn thẳng 
        // nghĩa là định hướng đang bị xung đột (Inconsistent Winding)
        return isForwardA == isForwardB;
    }
    private static void AddEdge(Dictionary<(int, int), int> dict, int a, int b)
    {
        var key = a < b ? (a, b) : (b, a);
        dict.TryGetValue(key, out int c); dict[key] = c + 1;
    }

    private static Dictionary<int, List<int>> BuildBoundaryAdjacency(
        Dictionary<(int, int), int> edgeCount, HashSet<int> boundarySet)
    {
        var adj = new Dictionary<int, List<int>>();
        foreach (int v in boundarySet) adj[v] = new List<int>();
        foreach (var kv in edgeCount)
        {
            if (kv.Value != 1) continue;
            int a = kv.Key.Item1, b = kv.Key.Item2;
            if (adj.ContainsKey(a)) adj[a].Add(b);
            if (adj.ContainsKey(b)) adj[b].Add(a);
        }
        return adj;
    }

    private static HashSet<int> FloodFill(int start, Dictionary<int, List<int>> adj)
    {
        var visited = new HashSet<int>();
        var queue   = new Queue<int>();
        queue.Enqueue(start); visited.Add(start);
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (!adj.ContainsKey(cur)) continue;
            foreach (int nb in adj[cur]) if (visited.Add(nb)) queue.Enqueue(nb);
        }
        return visited;
    }

    private static T[] MergeColliderArrays<T>(T[] arrayA, T[] arrayB,
        GameObject excludeGoA = null, GameObject excludeGoB = null) where T : Collider
    {
        var list = new List<T>();
        foreach (var arr in new[] { arrayA, arrayB })
        {
            if (arr == null) continue;
            foreach (var c in arr)
            {
                if ((UnityEngine.Object)c == null) continue;
                if (excludeGoA != null && c.gameObject == excludeGoA) continue;
                if (excludeGoB != null && c.gameObject == excludeGoB) continue;
                if (excludeGoA != null && c.transform.IsChildOf(excludeGoA.transform)) continue;
                if (excludeGoB != null && c.transform.IsChildOf(excludeGoB.transform)) continue;
                if (!list.Contains(c)) list.Add(c);
            }
        }
        return list.ToArray();
    }

    // ── UpdateLiveVisuals ─────────────────────────────────────────────────
    public void UpdateLiveVisuals()
    {
        if (_combined == null || _goA == null) return;

        var mrA = _goA.GetComponent<MeshRenderer>(); if (mrA != null) mrA.enabled = false;
        if (!_isSelfSew && _goB != null) { var mrB = _goB.GetComponent<MeshRenderer>(); if (mrB != null) mrB.enabled = false; }

        if (_previewObjEdge == null)
        {
            _previewObjEdge = new GameObject("[Sewing_Preview_Mesh]");
            _previewObjEdge.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            _previewObjEdge.transform.localScale = Vector3.one;
            _previewFilter   = _previewObjEdge.AddComponent<MeshFilter>();
            _previewRenderer = _previewObjEdge.AddComponent<MeshRenderer>();
            if (mrA != null) _previewRenderer.sharedMaterials = mrA.sharedMaterials;
        }

        Mesh previewMesh = G3MeshBridge.ToUnityMesh(_combined, null, out _, toLocalSpace: false);
        _previewFilter.mesh = previewMesh;
    }

    public void ClearPreviewVisuals()
    {
        if (_previewObjEdge != null)
        {
            Object.Destroy(_previewObjEdge);
            _previewObjEdge = null;
        }
        if (_goA != null) { var mrA = _goA.GetComponent<MeshRenderer>(); if (mrA != null) mrA.enabled = true; }
        if (!_isSelfSew && _goB != null) { var mrB = _goB.GetComponent<MeshRenderer>(); if (mrB != null) mrB.enabled = true; }
    }
}
