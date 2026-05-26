// ============================================================
//  MeshSewer_UCloth.cs  — v8.0
//
//  Giữ nguyên toàn bộ logic two-mesh từ v7 (đã hoạt động tốt).
//  Sửa self-sew:
//    v7 bug: chọn loop theo "centroid gần hitA nhất" rồi "loop xa nhất"
//            → sai khi mesh có >2 loop hoặc layout phức tạp.
//            Thêm nữa: _regionB = rỗng với self-sew nên cross-set
//            matching bị skip hoàn toàn.
//    v8 fix: Self-sew nhận hitPointA VÀ hitPointB riêng biệt (từ
//            SewingManager bắn 2 SphereCast). Mỗi hitPoint tìm loop
//            gần nhất độc lập → candA từ loopA, candB từ loopB.
//            Cross-set matching chạy bình thường (vA != vB đủ điều kiện).
//            Không cần _regionB với self-sew vì candA/candB đã tách biệt.
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

    // Phân vùng vertex trong combined mesh (chỉ dùng cho two-mesh)
    private HashSet<int> _regionA = new HashSet<int>();
    private HashSet<int> _regionB = new HashSet<int>();

    private Queue<(int vA, int vB)> _pendingPairs = new Queue<(int, int)>();
    private bool _initialized = false;
    private bool _completed   = false;
    private GameObject _previewObjEdge;
    private MeshFilter _previewFilter;
    private MeshRenderer _previewRenderer;

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
    // hitPointA : điểm ray chạm vào cloth A (hoặc cạnh/loop thứ nhất khi self-sew)
    // hitPointB : điểm ray chạm vào cloth B (hoặc cạnh/loop thứ hai khi self-sew)
    //             Với two-mesh hitPointB có thể == hitPointA nếu chỉ có 1 điểm hit.
    // =========================================================================
    //  ── INITIALIZE (Bản nâng cấp v8.9 - Khắc phục hoàn toàn lỗi candA = 0) ──
    // =========================================================================
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

        _combined = new DMesh3(dmA, bCompact: true);

        if (_isSelfSew)
        {
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

            // Thử tìm đỉnh quanh cạnh biên hở trước
            if (boundaryVerts.Count > 0)
            {
                foreach (int vid in boundaryVerts)
                {
                    Vector3d p = _combined.GetVertex(vid);
                    if ((p - seedA).LengthSquared <= rSq) candA.Add(vid);
                    if ((p - seedB).LengthSquared <= rSq) candB.Add(vid);
                }
            }

            // TOPO FALLBACK: Nếu người dùng khâu nếp gấp bề mặt trên mesh đã gộp vòng biên
            if (candA.Count == 0)
            {
                candA = _combined.VertexIndices()
                    .OrderBy(v => (_combined.GetVertex(v) - seedA).LengthSquared)
                    .Take(8).ToList();
            }
            if (candB.Count == 0)
            {
                candB = _combined.VertexIndices()
                    .OrderBy(v => (_combined.GetVertex(v) - seedB).LengthSquared)
                    .Take(8).ToList();
            }
        }
        else
        {
            // ── NHÁNH 2: Xử lý Khâu gộp (Two-Mesh) ──
            // Tìm kiếm đỉnh thuộc phân vùng Mesh A bám quanh điểm chạm laser A
            foreach (int vid in _regionA)
            {
                Vector3d p = _combined.GetVertex(vid);
                if ((p - seedA).LengthSquared <= rSq) candA.Add(vid);
            }
            // Tìm kiếm đỉnh thuộc phân vùng Mesh B bám quanh điểm chạm laser B
            foreach (int vid in _regionB)
            {
                Vector3d p = _combined.GetVertex(vid);
                if ((p - seedB).LengthSquared <= rSq) candB.Add(vid);
            }

            // FALLBACK CHO TWO-MESH: Đảm bảo không bị lỗi trắng ứng viên do sai lệch tọa độ tia cast VR
            if (candA.Count == 0 && _regionA.Count > 0)
            {
                candA = _regionA.OrderBy(v => (_combined.GetVertex(v) - seedA).LengthSquared).Take(6).ToList();
            }
            if (candB.Count == 0 && _regionB.Count > 0)
            {
                candB = _regionB.OrderBy(v => (_combined.GetVertex(v) - seedB).LengthSquared).Take(6).ToList();
            }
        }

        // CHỐNG TỰ SẬP: Loại bỏ triệt để việc đỉnh tự ghép cặp khâu với chính nó
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

            _pendingPairs.Enqueue((vA, nearest.Key));
            usedB.Add(nearest.Key);
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
            if (!_combined.IsVertex(vA) || !_combined.IsVertex(vB)) continue;
            if (vA == vB) { count++; continue; }

            // Snap vB → vA trong DMesh3
            _combined.SetVertex(vB, _combined.GetVertex(vA));

            // Weld topology: thay vB → vA trong tất cả triangle
            TryCollapseToA(_combined, vA, vB);
            count++;
        }

        _completed = _pendingPairs.Count == 0;
        return _completed;
    }

    // ── Finalize ─────────────────────────────────────────────────────────
   // =========================================================================
//  SỬA LỖI: HÀM FINALIZE NÂNG CẤP — FIX TRONG MESHSEWER_UCLOTH.CS
// =========================================================================
// =========================================================================
    //  ── FINALIZE (Bản sửa lỗi CS0117 & CS1061 - Chuẩn hóa API gốc) ──
    // =========================================================================
    // =========================================================================
    //  ── FINALIZE (Bản chuẩn hóa Local-Space đồng bộ hệ thống) ──
    // =========================================================================
    // =========================================================================
    //  ── FINALIZE (Bản v9.0 - Chuẩn hóa Topology SubMesh & Fix KeyNotFound) ──
    // =========================================================================
    public GameObject Finalize(string newName = "SewnCloth")
    {
        if (!_initialized)
        {
            Debug.LogError("[MeshSewer] Gọi Finalize() trước Initialize()!");
            return null;
        }

        // ── 0. Dọn sạch đối tượng xem trước đồ họa tạm thời ──
        ClearPreviewVisuals();

        // Nén chặt cấu trúc đỉnh và tam giác để dọn sạch các đỉnh cô lập thừa
        DMesh3 compacted = new DMesh3(_combined, bCompact: true);

        if (compacted.VertexCount > 65535)
        {
            Debug.LogError($"[MeshSewer] {compacted.VertexCount} verts > 65535 (Vượt giới hạn uCloth UShort)!");
            return null;
        }

        // Tính toán hệ thống pháp tuyến sạch dựa trên cấu trúc hình học mới
        MeshNormals.QuickCompute(compacted);

        // ── 1. Khởi tạo GameObject mới & Kế thừa định danh cơ bản ──
        var go = new GameObject(newName);
        
        // Đồng bộ vị trí thực thể vải mới trùng khớp hoàn toàn với vị trí thực tế của mảnh vải cũ lúc khâu
        go.transform.SetPositionAndRotation(_goA.transform.position, _goA.transform.rotation);
        go.transform.localScale = _goA.transform.localScale;

        go.tag = _goA.tag; // Kế thừa tag "Cloth" để Sewing/Cutting nhận diện tiếp
        go.layer = _goA.layer;

        // ── 2. Tạo Mesh Unity sạch 100% không chứa dữ liệu rác SubMesh cũ ──
        Mesh unityMesh = new Mesh();
        unityMesh.name = newName + "_Mesh";

        // Sử dụng cấu trúc danh sách tuần tự để trích xuất trực tiếp dữ liệu từ DMesh3 sang Local Space
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        var g3ToRenderMap = new Dictionary<int, int>();

        // Duyệt danh sách đỉnh và chuyển đổi chính xác về Local Space của GameObject mới tạo
        foreach (int vid in compacted.VertexIndices())
        {
            Vector3d p = compacted.GetVertex(vid);
            Vector3 worldPt = new Vector3((float)p.x, (float)p.y, (float)p.z);
            Vector3 localPt = go.transform.InverseTransformPoint(worldPt);
            
            int newIdx = vertices.Count;
            vertices.Add(localPt);
            g3ToRenderMap[vid] = newIdx;
        }

        // Duyệt danh sách tam giác và map lại chỉ số liên tục sạch hoàn toàn
        foreach (int tid in compacted.TriangleIndices())
        {
            if (!compacted.IsTriangle(tid)) continue;
            Index3i tri = compacted.GetTriangle(tid);
            
            if (g3ToRenderMap.TryGetValue(tri.a, out int ia) &&
                g3ToRenderMap.TryGetValue(tri.b, out int ib) &&
                g3ToRenderMap.TryGetValue(tri.c, out int ic))
            {
                // Loại bỏ các tam giác suy biến (Degenerate Triangles) ngay tại tầng nạp mảng
                if (ia == ib || ib == ic || ia == ic) continue;
                
                triangles.Add(ia);
                triangles.Add(ib);
                triangles.Add(ic);
            }
        }

        // Cấu hình định dạng chỉ số đỉnh an toàn
        unityMesh.indexFormat = vertices.Count > 65535 
            ? UnityEngine.Rendering.IndexFormat.UInt32 
            : UnityEngine.Rendering.IndexFormat.UInt16;

        // Đẩy mảng dữ liệu nguyên bản, thuần khiết vào Mesh mới
        unityMesh.SetVertices(vertices);
        unityMesh.SetTriangles(triangles, 0);
        
        unityMesh.RecalculateNormals();
        unityMesh.RecalculateBounds();
        unityMesh.RecalculateTangents();

        go.AddComponent<MeshFilter>().mesh = unityMesh;

        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = _goA.GetComponent<MeshRenderer>()?.sharedMaterials ?? new Material[0];

        // Gán MeshCollider phẳng (convex = false) tương thích vải
        var mc = go.AddComponent<MeshCollider>();
        mc.sharedMesh = unityMesh;
        mc.convex     = false;

        var rb = go.AddComponent<Rigidbody>();
        rb.useGravity  = true;
        rb.isKinematic = true;

        // ── 3. Thiết lập thông số và Kích hoạt vòng đời tự nhiên cho UCCloth ──
        //
        // FIX BUG KHÂU-XONG-KHÔNG-CẮT-ĐƯỢC (root cause):
        // AddComponent<UCCloth>() kích hoạt UCCloth.Awake() đồng bộ ngay trong frame này.
        // Nếu gán sphereColliders / cubeColliders / pinColliders SAU AddComponent,
        // UCCloth.Awake() đọc các array đó khi chúng còn null → FilterColliders() crash
        // hoặc simData không được khởi tạo → timeout → _renderToSimLookup = null
        // → GetWorldSpaceVertices fallback TransformPoint sai vị trí → không cắt được.
        //
        // Giải pháp: tắt GameObject TRƯỚC khi AddComponent → Awake() bị hoãn lại,
        // gán đầy đủ tất cả properties, rồi bật lại → UCCloth.Awake()/Start() chạy
        // với dữ liệu hoàn chỉnh.
        var ucBase = _ucA ?? _ucB;
        if (ucBase != null)
        {
            go.SetActive(false); // Tắt tạm để UCCloth.Awake() không chạy sớm

            var newCloth = go.AddComponent<UCloth.UCCloth>();

            // Copy thông số vật liệu vải
            newCloth.preprocessorType     = ucBase.preprocessorType;
            newCloth.materialProperties   = ucBase.materialProperties;
            newCloth.simulationProperties = ucBase.simulationProperties;
            newCloth.qualityProperties    = ucBase.qualityProperties;
            newCloth.collisionProperties  = ucBase.collisionProperties;
            newCloth.thickness            = ucBase.thickness;
            newCloth.offsetFront          = ucBase.offsetFront;
            newCloth.smoothing            = ucBase.smoothing;

            // Gộp mảng các Collider va chạm môi trường (Null-safe).
            // Phải set TRƯỚC khi go.SetActive(true) để UCCloth.Awake() đọc đúng giá trị.
            // Truyền excludeGoA/GoB để loại bỏ collider thuộc mesh gốc (sẽ bị deactivate)
            newCloth.sphereColliders  = MergeColliderArrays(_ucA?.sphereColliders,  _isSelfSew ? null : _ucB?.sphereColliders,  _goA, _isSelfSew ? null : _goB);
            newCloth.capsuleColliders = MergeColliderArrays(_ucA?.capsuleColliders, _isSelfSew ? null : _ucB?.capsuleColliders, _goA, _isSelfSew ? null : _goB);
            newCloth.cubeColliders    = MergeColliderArrays(_ucA?.cubeColliders,    _isSelfSew ? null : _ucB?.cubeColliders,    _goA, _isSelfSew ? null : _goB);

            // Đảm bảo không null để UCCloth.FilterColliders() không crash
            if (newCloth.sphereColliders  == null) newCloth.sphereColliders  = new SphereCollider[0];
            if (newCloth.capsuleColliders == null) newCloth.capsuleColliders = new CapsuleCollider[0];
            if (newCloth.cubeColliders    == null) newCloth.cubeColliders    = new BoxCollider[0];

            // Gộp mảng điểm ghim vải cố định (Pin).
            // Chỉ giữ pin collider KHÔNG thuộc goA/goB (vì chúng sẽ bị deactivate).
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

            go.SetActive(true); // Bật lại → UCCloth.Awake()+Start() chạy với data đầy đủ
            Debug.Log($"[MeshSewer] Thuộc tính vật lý cho '{newName}' đã đồng bộ. UCCloth sẽ init với data đầy đủ.");
        }

        // ── 4. Đồng bộ hóa bộ tương tác Grab VR (Meta Quest) ──
        var grabSrc = _goA.GetComponent<UClothLaserGrabber>() 
                      ?? (_isSelfSew ? null : _goB?.GetComponent<UClothLaserGrabber>());
        if (grabSrc != null)
        {
            var gr = go.AddComponent<UClothLaserGrabber>();
            gr.vrController  = grabSrc.vrController;
            gr.grabSphere    = grabSrc.grabSphere;
            gr.triggerAction = grabSrc.triggerAction;
            gr.pullForce     = grabSrc.pullForce;
            Debug.Log($"[MeshSewer] Kế thừa bộ tương tác VR Laser Grabber cho {newName} thành công.");
        }

        // ── 5. Giải phóng và tắt bỏ các Mesh cũ để tránh rò rỉ bộ nhớ Native ──
        SafeDisableUCloth(_goA);
        if (!_isSelfSew && _goB != null) SafeDisableUCloth(_goB);

        Debug.Log($"<color=green>[MeshSewer] ✓ Xuất bản vải Local-Space thành công:</color> '{newName}' ({unityMesh.vertexCount} verts).");

        OnSeamCompleted?.Invoke(go);
        return go;
    }

    // Hàm hỗ trợ gộp mảng Collider null-safe, tránh trùng lặp phần tử.
    // QUAN TRỌNG: Chỉ giữ lại collider KHÔNG thuộc goA/goB (tức environment colliders
    // như sàn, bàn...) vì goA/goB sẽ bị SetActive(false) ngay sau Finalize().
    // Collider thuộc goA/goB sẽ bị deactivate → UCCloth.UpdateColliderDTOs() crash.
    private static T[] MergeColliderArrays<T>(T[] arrayA, T[] arrayB,
        GameObject excludeGoA = null, GameObject excludeGoB = null) where T : Collider
    {
        var list = new List<T>();
        foreach (var arr in new[] { arrayA, arrayB })
        {
            if (arr == null) continue;
            foreach (var c in arr)
            {
                // Unity fake-null check: destroyed/deactivated object == null trong Unity
                if ((UnityEngine.Object)c == null) continue;
                // Loại bỏ collider thuộc goA/goB vì chúng sẽ bị SetActive(false)
                if (excludeGoA != null && c.gameObject == excludeGoA) continue;
                if (excludeGoB != null && c.gameObject == excludeGoB) continue;
                // Loại bỏ collider thuộc GO con của goA/goB
                if (excludeGoA != null && c.transform.IsChildOf(excludeGoA.transform)) continue;
                if (excludeGoB != null && c.transform.IsChildOf(excludeGoB.transform)) continue;
                if (!list.Contains(c)) list.Add(c);
            }
        }
        return list.ToArray();
    }


    // ── AddSeam (v8.2 - Robust Spatial Search for Continuous Sewing) ───────────────────
    public bool AddSeam(Vector3 hitPointA, Vector3 hitPointB, float sewRadius)
    {
        if (!_initialized)
        {
            Debug.LogError("[MeshSewer] AddSeam() gọi trước Initialize()!");
            return false;
        }

        // 1. Thu thập tất cả boundary vertex hiện có trong mesh tổng hợp
        var boundaryVerts = new HashSet<int>();
        foreach (int eid in _combined.EdgeIndices())
        {
            if (!_combined.IsBoundaryEdge(eid)) continue;
            Index2i ev = _combined.GetEdgeV(eid);
            boundaryVerts.Add(ev.a);
            boundaryVerts.Add(ev.b);
        }

        if (boundaryVerts.Count == 0)
        {
            Debug.LogWarning("[MeshSewer] AddSeam: Không còn cạnh biên trống nào để khâu (Mesh đã kín).");
            return false;
        }

        Vector3d seedA = ToV3d(hitPointA);
        Vector3d seedB = ToV3d(hitPointB);
        
        // Mở rộng bán kính tìm kiếm đỉnh một chút để bù đắp sai số co giãn vật lý của uCloth
        double dynamicRadius = System.Math.Max((double)sewRadius, Vector3.Distance(hitPointA, hitPointB) * 0.5);
        double rSq = dynamicRadius * dynamicRadius;

        var candA = new List<int>();
        var candB = new List<int>();

        // 2. Thu thập ứng viên thuần túy theo khoảng cách không gian (Spatial Search)
        // Cách này loại bỏ hoàn toàn sự phụ thuộc vào thứ tự/số lượng Loop biên bị biến dạng
        foreach (int vid in boundaryVerts)
        {
            Vector3d p = _combined.GetVertex(vid);
            
            double distSqA = (p - seedA).LengthSquared;
            double distSqB = (p - seedB).LengthSquared;

            if (distSqA <= rSq) candA.Add(vid);
            if (distSqB <= rSq) candB.Add(vid);
        }

        // Fallback bảo toàn: Nếu bán kính quét không đủ, lấy N đỉnh biên gần các điểm hit nhất
        if (candA.Count == 0 && boundaryVerts.Count > 0)
        {
            candA = boundaryVerts
                .OrderBy(v => (_combined.GetVertex(v) - seedA).LengthSquared)
                .Take(5).ToList();
        }
        if (candB.Count == 0 && boundaryVerts.Count > 0)
        {
            candB = boundaryVerts
                .OrderBy(v => (_combined.GetVertex(v) - seedB).LengthSquared)
                .Take(5).ToList();
        }

        // Loại bỏ các đỉnh trùng lặp chéo giữa 2 tập để tránh đỉnh tự khâu với chính nó
        if (candA.Count > 0 && candB.Count > 0)
        {
            var intersect = candA.Intersect(candB).ToList();
            if (intersect.Count > 0 && (candA.Count > intersect.Count || candB.Count > intersect.Count))
            {
                foreach (int v in intersect)
                {
                    // Đỉnh nào gần bên nào hơn thì giữ lại bên đó
                    double dA = (_combined.GetVertex(v) - seedA).LengthSquared;
                    double dB = (_combined.GetVertex(v) - seedB).LengthSquared;
                    if (dA < dB) candB.Remove(v); else candA.Remove(v);
                }
            }
        }

        if (candA.Count == 0 || candB.Count == 0)
        {
            Debug.LogWarning($"[MeshSewer] AddSeam thất bại hình học: candA={candA.Count}, candB={candB.Count}. Thử điều chỉnh góc bắn raycast.");
            return false;
        }

        // 3. Cross-set matching (Giữ nguyên logic gốc ổn định của bạn)
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

            _pendingPairs.Enqueue((vA, nearest.Key));
            usedB.Add(nearest.Key);
            added++;
        }

        if (added == 0)
        {
            // Cứu cánh cuối cùng: Ép cặp thủ công đỉnh gần nhất của tập A và B nếu phân mảnh hash grid thất bại
            int bestVA = candA.OrderBy(v => (_combined.GetVertex(v) - seedA).LengthSquared).First();
            int bestVB = candB.Where(v => v != bestVA).OrderBy(v => (_combined.GetVertex(v) - _combined.GetVertex(bestVA)).LengthSquared).FirstOrDefault();
            if (bestVB != 0)
            {
                _pendingPairs.Enqueue((bestVA, bestVB));
                added++;
            }
        }

        _completed = false; // Kích hoạt lại tiến trình cho hàm Sew() chạy ở frame tiếp theo
        Debug.Log($"[MeshSewer] AddSeam khâu nối thành công: +{added} cặp đỉnh biên mới (candA:{candA.Count}, candB:{candB.Count})");
        return added > 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Tìm index của loop có centroid gần seed nhất.
    /// excludeIdx: bỏ qua index này (dùng khi tìm loopB ≠ loopA).
    /// </summary>
    private static int FindNearestLoopIdx(
        List<(Vector3d center, List<int> verts)> centroids,
        Vector3d seed,
        int excludeIdx = -1)
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
        if (trisOfB.Count == 0) return false;

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
            if (a == b || b == c || a == c) continue;
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

    // ── ExtractBoundaryLoops ──────────────────────────────────────────────
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
            queue.Enqueue(start);
            visited.Add(start);

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
    // ── UpdateLiveVisuals (v8.3 - Real-time Preview) ─────────────────────
    public void UpdateLiveVisuals()
    {
        if (_combined == null || _goA == null) return;

        // Tắt hiển thị Mesh Renderer gốc của các vật thể đang khâu để tránh chồng chéo đồ họa
        var mrA = _goA.GetComponent<MeshRenderer>(); if (mrA != null) mrA.enabled = false;
        if (!_isSelfSew && _goB != null) { var mrB = _goB.GetComponent<MeshRenderer>(); if (mrB != null) mrB.enabled = false; }

        // Khởi tạo GameObject xem trước nếu chưa có
        if (_previewObjEdge == null)
        {
            _previewObjEdge = new GameObject("[Sewing_Preview_Mesh]");
            _previewObjEdge.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            _previewObjEdge.transform.localScale = Vector3.one;

            _previewFilter = _previewObjEdge.AddComponent<MeshFilter>();
            _previewRenderer = _previewObjEdge.AddComponent<MeshRenderer>();
            
            // Lấy tạm vật liệu từ mesh A để hiển thị cho đồng bộ
            if (mrA != null) _previewRenderer.sharedMaterials = mrA.sharedMaterials;
        }

        // Xuất mesh từ cấu trúc hình học DMesh3 hiện tại (giữ không gian World để vẽ chính xác)
        Mesh previewMesh = G3MeshBridge.ToUnityMesh(_combined, null, out _, toLocalSpace: false);
        _previewFilter.mesh = previewMesh;
    }

    // Thêm hàm dọn dẹp cấu trúc xem trước khi hủy hoặc kết thúc Session
    public void ClearPreviewVisuals()
    {
        if (_previewObjEdge != null)
        {
            Object.Destroy(_previewObjEdge);
            _previewObjEdge = null;
        }

        // Khôi phục lại hiển thị cho các Mesh gốc nếu session bị hủy giữa chừng
        if (_goA != null) { var mrA = _goA.GetComponent<MeshRenderer>(); if (mrA != null) mrA.enabled = true; }
        if (!_isSelfSew && _goB != null) { var mrB = _goB.GetComponent<MeshRenderer>(); if (mrB != null) mrB.enabled = true; }
    }
}