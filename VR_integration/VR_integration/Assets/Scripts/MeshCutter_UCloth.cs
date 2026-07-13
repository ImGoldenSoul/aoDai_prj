// ============================================================
//  MeshCutter_UCloth.cs  — v4.0  (Raycast-Boundary Split)
//
//  Thuật toán cắt mới — KHÔNG xóa triangle, KHÔNG dùng Collider.
//
//  Pipeline:
//  1. Mỗi frame CuttingManager gọi AccumulateRaycastHit(hitPoint, hitTriangle).
//     Các điểm raycast được lưu thành _cutPath (danh sách world-space points).
//  2. Khi CommitCut() được gọi (hoặc tự động khi path đủ dài),
//     G3MeshCutter.CutAlongPath() dùng DMesh3 để:
//       a. Snap/insert các điểm path lên cạnh gần nhất của mesh.
//       b. Tách (split) các edge bị path đi qua.
//       c. Flood-fill hai vùng tách ra từ vết cắt → 2 DMesh3 con.
//  3. Mỗi DMesh3 được chuyển lại thành Unity Mesh qua G3MeshBridge.ToUnityMesh().
//  4. Tạo 2 GameObject piece với UCCloth (giống pipeline cũ).
//
//  So sánh với v3 (Collider/triangle-deletion):
//  + Không mất vải tại vết cắt (tách sạch, không xóa tam giác).
//  + Boundary chính xác theo hành trình raycast thực tế.
//  + Dùng geometry3Sharp để xử lý topology đảm bảo 2-manifold.
// ============================================================

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using URandom = UnityEngine.Random;
using g3;

public class MeshCutter_UCloth
{
    // ── References ───────────────────────────────────────────────────────
    private readonly GameObject     _target;
    private readonly Transform      _tf;
    private readonly MeshFilter     _mf;
    private readonly float          _splitForce;
    private readonly UCloth.UCCloth _ucCloth;

    // render vertex → sim node (built in Initialize after simData ready)
    private int[] _renderToSimLookup;

    private const int MIN_TRIS_FOR_PIECE = 4;

    // ── Cut Path Accumulation ─────────────────────────────────────────────
    // Danh sách điểm world-space mà raycast đã chạm vào mesh theo thứ tự thời gian
    private readonly List<Vector3> _cutPath = new List<Vector3>();

    // Khoảng cách tối thiểu giữa 2 điểm liên tiếp trên path (tránh noise)
    private float _minPathPointSpacing = 0.005f;

    /// <summary>Cho phép CuttingManager ghi đè spacing từ Inspector settings.</summary>
    public float _minPathPointSpacingOverride
    {
        set { if (value > 0f) _minPathPointSpacing = value; }
    }

    // ── Output ────────────────────────────────────────────────────────────
    private readonly List<GameObject> _lastCreatedPieces = new List<GameObject>();
    private ClothAreaCalculator.CutAreaReport? _lastAreaReport;

    public ClothAreaCalculator.CutAreaReport? GetLastAreaReport() => _lastAreaReport;
    public List<GameObject> GetLastCreatedPieces() => new List<GameObject>(_lastCreatedPieces);

    // ── Debug ─────────────────────────────────────────────────────────────
    public bool showDebugPath = true;

    // =========================================================================
    //  CONSTRUCTOR
    // =========================================================================
    public MeshCutter_UCloth(GameObject target, float splitForce)
    {
        _target     = target;
        _tf         = target.transform;
        _mf         = target.GetComponent<MeshFilter>();
        _splitForce = splitForce;
        _ucCloth    = target.GetComponent<UCloth.UCCloth>();

        if (_ucCloth != null)
            Debug.Log($"[MeshCutter_UCloth] UCCloth detected trên '{target.name}'. Chờ Initialize().");
        else
            Debug.Log($"[MeshCutter_UCloth] No UCCloth trên '{target.name}'.");
    }

    // =========================================================================
    //  INITIALIZE — gọi SAU KHI UCCloth.simData sẵn sàng
    // =========================================================================
    public void Initialize()
    {
        if (_ucCloth != null)
            BuildRenderToSimLookup();

        Debug.Log($"[MeshCutter_UCloth] Initialized '{_target.name}' (Raycast-Boundary-Split mode).");
    }

    // =========================================================================
    //  ACCUMULATE RAYCAST HIT
    //  Gọi từ CuttingManager mỗi frame khi raycast chạm vào mesh này.
    //  Trả về true nếu điểm được thêm vào path.
    // =========================================================================
    public bool AccumulateHit(Vector3 worldHitPoint)
    {
        if (_cutPath.Count > 0)
        {
            float d = Vector3.Distance(_cutPath[_cutPath.Count - 1], worldHitPoint);
            if (d < _minPathPointSpacing) return false;
        }

        _cutPath.Add(worldHitPoint);
        return true;
    }
    // =========================================================================
    //  FORCE ADD PATH POINT (Dùng cho nhát cắt chiếu từ không gian)
    // =========================================================================
    /// <summary>
    /// Ép thêm điểm hình học đã tính toán trực tiếp vào path cắt mà không thông qua bộ lọc raycast thời gian thực.
    /// </summary>
    public void ForceAddPathPoint(Vector3 worldPoint)
    {
        _cutPath.Add(worldPoint);
    }

    /// <summary>Số điểm đã tích lũy trên đường cắt.</summary>
    public int PathPointCount => _cutPath.Count;

    /// <summary>Đọc trực tiếp danh sách điểm world-space hiện tại (để vẽ LineRenderer/visual khác).</summary>
    public IReadOnlyList<Vector3> CutPathPoints => _cutPath;

    /// <summary>Xóa path hiện tại (khi hủy cut).</summary>
    public void ClearPath() => _cutPath.Clear();

    // =========================================================================
    //  COMMIT CUT — Thực thi cắt mesh dọc theo _cutPath tích lũy
    //  Đã tích hợp bộ lọc chặn đứng lỗi trùng đỉnh NaN / sụp đổ MinMaxAABB
    // =========================================================================
    public CutResult_Ucloth CommitCut(bool splitOnlyWhenDisconnected)
    {
        if (_mf == null || _cutPath.Count < 2)
        {
            Debug.LogWarning($"[MeshCutter_UCloth] CommitCut: path quá ngắn ({_cutPath.Count} điểm). Cần ít nhất 2.");
            return CutResult_Ucloth.None;
        }

        // Lazy retry: nếu BuildRenderToSimLookup() đã BỎ QUA lúc Initialize() (vì simData
        // còn NaN ngay sau khi piece vừa AddComponent<UCCloth>()), thử lại 1 lần ở đây —
        // tới lúc người dùng thực sự thả trigger để cắt, solver thường đã có nhiều frame
        // hơn để converge. Không thử lại thì piece sẽ kẹt vĩnh viễn ở local-space cutting
        // (vẫn cắt được, chỉ không đồng bộ chính xác theo sim).
        if (_ucCloth != null && _renderToSimLookup == null)
            BuildRenderToSimLookup();

        // Build world-space vertices từ simData nếu có UCCloth
        Mesh     mesh      = _mf.mesh;
        Vector3[] verts    = mesh.vertices;
        Vector3[] worldV   = GetWorldSpaceVertices(verts);

        // Chuyển Unity mesh → DMesh3 (world-space)
        var dmesh = G3MeshBridge.ToDMesh3(mesh, _tf, out _, useWorldSpace: true);

        // FIX "tia xác định seam bị lệch khỏi đường vẽ":
        // _cutPath chứa các điểm world-space được raycast CHẠM trên MeshCollider —
        // collider này dùng mesh TĨNH (sharedMesh = render mesh tại lần cập nhật gần
        // nhất), trong khi dmesh dùng để CẮT lại được rebuild theo VỊ TRÍ SIM MỚI NHẤT
        // (RebuildDMeshFromSimPositions, bên dưới). Nếu vải đã dao động/di chuyển giữa
        // lúc người dùng vẽ và lúc CommitCut() chạy, 2 hệ tọa độ này lệch pha nhau →
        // SnapPathToMesh() trong G3MeshCutter sẽ snap nhầm sang tam giác khác trên vải,
        // khiến đường seam lệch khỏi đường vẽ thấy bằng mắt.
        // Sửa: trước khi đổi dmesh sang vị trí sim, với MỖI điểm trong _cutPath, tìm
        // toạ độ barycentric của nó trên mesh TĨNH (verts, đúng hệ mà raycast đã dùng),
        // rồi dùng CHÍNH toạ độ barycentric đó để nội suy lại vị trí tương ứng trên
        // worldV (vị trí sim mới nhất). Kết quả: điểm path luôn "bám" đúng điểm trên
        // bề mặt vải mà người dùng đã chạm, bất kể vải đã di chuyển bao nhiêu giữa 2
        // thời điểm — không còn lệch theo độ trễ cập nhật collider.
        List<Vector3> cutPathForCut = _cutPath;
        if (_ucCloth != null && _renderToSimLookup != null
            && _ucCloth.simData != null && _ucCloth.simData.positionsReadOnly.IsCreated)
        {
            Vector3[] collisionWorldV = GetColliderWorldSpaceVertices(verts);
            cutPathForCut = RemapPathToSimSpace(_cutPath, mesh.triangles, collisionWorldV, worldV);

            dmesh = RebuildDMeshFromSimPositions(mesh, worldV);
        }

        // Thực hiện cắt geometry3Sharp
        List<DMesh3> pieces = G3MeshCutter.CutAlongPath(dmesh, cutPathForCut, out string cutLog);
        Debug.Log($"[MeshCutter_UCloth] CutAlongPath: {cutLog}");

        if (pieces == null || pieces.Count < 2)
        {
            if (!splitOnlyWhenDisconnected)
            {
                // Chưa tách nhưng đã ghi nhận path — trả Trimmed để thông báo đã có hoạt động
                return _cutPath.Count > 0 ? CutResult_Ucloth.Trimmed : CutResult_Ucloth.None;
            }
            return CutResult_Ucloth.None;
        }

        // FIX: build trước danh sách piece Unity Mesh hợp lệ (≥ MIN_TRIS_FOR_PIECE)
        // TRƯỚC KHI tắt UCCloth gốc. Trước đây _ucCloth.enabled bị set false ngay khi
        // geometry3Sharp báo "đã tách (regions ≥ 2)", nhưng nếu sau lọc kích thước chỉ
        // còn < 2 mảnh đủ lớn thì mesh gốc đã bị tắt simulation mà KHÔNG có gì thay thế
        // → vải "đông cứng", không cắt tiếp được nữa (đúng vấn đề người dùng gặp phải).
        Material[] mats       = _target.GetComponent<MeshRenderer>().sharedMaterials;
        Vector3    meshCenter = worldV.Aggregate(Vector3.zero, (s, v) => s + v) / worldV.Length;

        // Định nghĩa các ngưỡng an toàn để loại bỏ các cấu trúc hình học suy biến gây chia cho 0 trong Solver vật lý
        const double MIN_TRIANGLE_AREA = 1e-6;    // 1 mm vuông
        const double MIN_EDGE_LENGTH_SQ = 1e-6;   // 1 mm chiều dài cạnh bình phương

        var pendingPieces = new List<(Mesh mesh, Vector3 impulseDir)>();
        for (int c = 0; c < pieces.Count; c++)
        {
            DMesh3 subMesh = pieces[c];

            // ── BỘ LỌC KHỬ TAM GIÁC SUY BIẾN (DEGENERATE FILTER) ──
            var degenerateTriangles = new List<int>();
            foreach (int tid in subMesh.TriangleIndices())
            {
                if (!subMesh.IsTriangle(tid)) continue;

                Index3i triVerts = subMesh.GetTriangle(tid);
                Vector3d v0 = subMesh.GetVertex(triVerts.a);
                Vector3d v1 = subMesh.GetVertex(triVerts.b);
                Vector3d v2 = subMesh.GetVertex(triVerts.c);

                // 1. Kiểm tra chiều dài các cạnh của tam giác
                double d01 = (v1 - v0).LengthSquared;
                double d12 = (v2 - v1).LengthSquared;
                double d20 = (v0 - v2).LengthSquared;

                if (d01 < MIN_EDGE_LENGTH_SQ || d12 < MIN_EDGE_LENGTH_SQ || d20 < MIN_EDGE_LENGTH_SQ)
                {
                    degenerateTriangles.Add(tid);
                    continue;
                }

                // 2. Kiểm tra diện tích thông qua tích có hướng (Cross Product)
                Vector3d edge1 = v1 - v0;
                Vector3d edge2 = v2 - v0;
                Vector3d cross = edge1.Cross(edge2);
                double area = cross.Length * 0.5;

                if (area < MIN_TRIANGLE_AREA || double.IsNaN(area) || double.IsInfinity(area))
                {
                    degenerateTriangles.Add(tid);
                }
            }

            // Tiến hành gỡ bỏ hoàn toàn tam giác lỗi và cô lập các đỉnh rác
            if (degenerateTriangles.Count > 0)
            {
                Debug.LogWarning($"[MeshCutter_UCloth] Phát hiện và loại bỏ {degenerateTriangles.Count} tam giác suy biến/siêu nhỏ trên Mảnh {c} để bảo vệ luồng tiền xử lý của uCloth.");
                foreach (int tid in degenerateTriangles)
                {
                    subMesh.RemoveTriangle(tid, true); // true giải phóng đỉnh cô lập (isolated vertices) an toàn
                }
            }

            // Chuyển đổi từ dữ liệu dmesh hình học sạch sang Unity Mesh thông thường
            Mesh pieceMesh = G3MeshBridge.ToUnityMesh(subMesh, _tf, out _, toLocalSpace: true);
            if (pieceMesh == null || pieceMesh.triangles.Length / 3 < MIN_TRIS_FOR_PIECE) continue;

            Vector3 pieceCenter = ComputeDMeshCenter(subMesh);
            Vector3 impulseDir  = (pieceCenter - meshCenter).normalized;
            if (impulseDir.sqrMagnitude < 0.01f) impulseDir = URandom.onUnitSphere;

            pendingPieces.Add((pieceMesh, impulseDir));
        }

        if (pendingPieces.Count < 2)
        {
            Debug.LogWarning($"[MeshCutter_UCloth] CommitCut: geometry3Sharp tách được {pieces.Count} vùng nhưng " +
                              $"chỉ {pendingPieces.Count} mảnh đủ lớn (≥{MIN_TRIS_FOR_PIECE} tris) → bỏ qua kết quả này, " +
                              $"GIỮ NGUYÊN mesh gốc (KHÔNG tắt UCCloth) để còn cắt tiếp được.");
            // Không ClearPath(): giữ path hiện có để gộp với nét vẽ tiếp theo của người dùng.
            return splitOnlyWhenDisconnected ? CutResult_Ucloth.None : CutResult_Ucloth.Trimmed;
        }

        // Từ đây chắc chắn sẽ tạo ra ≥ 2 mảnh hợp lệ → an toàn để tắt UCCloth gốc.
        if (_ucCloth != null) _ucCloth.enabled = false;

        _lastCreatedPieces.Clear();
        for (int c = 0; c < pendingPieces.Count; c++)
        {
            var (pieceMesh, impulseDir) = pendingPieces[c];
            var piece = CreatePiece($"{_target.name}_Piece{c}", pieceMesh, mats, impulseDir);
            if (piece != null) _lastCreatedPieces.Add(piece);
        }

        _cutPath.Clear();
        return _lastCreatedPieces.Count >= 2 ? CutResult_Ucloth.Split : CutResult_Ucloth.None;
    }

    // =========================================================================
    //  REBUILD DMESH FROM SIM POSITIONS
    //  Tạo lại DMesh3 dùng vị trí simulate thực tế (không phải local mesh vertices)
    // =========================================================================
    private DMesh3 RebuildDMeshFromSimPositions(Mesh mesh, Vector3[] worldVerts)
    {
        int[]  tris = mesh.triangles;
        var dmesh   = new DMesh3(false, false, false, false);

        // Thêm vertex theo vị trí sim thực
        for (int i = 0; i < worldVerts.Length; i++)
        {
            var p = worldVerts[i];
            dmesh.AppendVertex(new Vector3d(p.x, p.y, p.z));
        }

        // Thêm triangle
        for (int t = 0; t < tris.Length; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            if (a == b || b == c || a == c) continue;
            if (dmesh.AppendTriangle(a, b, c) < 0)
                Debug.LogWarning($"[MeshCutter_UCloth] Triangle {t/3} reject khi rebuild từ simData.");
        }

        return dmesh;
    }

    // =========================================================================
    //  CREATE PIECE — copy UCCloth settings + tag (giống v3)
    // =========================================================================
    private GameObject CreatePiece(string name, Mesh mesh, Material[] mats, Vector3 impulseDir)
    {
        int tc = mesh.triangles.Length / 3;
        if (tc < MIN_TRIS_FOR_PIECE)
        {
            Debug.Log($"[MeshCutter_UCloth] Skip '{name}' ({tc} tris quá nhỏ).");
            return null;
        }

        var piece = new GameObject(name);
        piece.transform.SetPositionAndRotation(_tf.position, _tf.rotation);
        piece.transform.localScale = _tf.lossyScale;
        piece.tag = _target.tag;

        // FIX "mesh mới không cắt tiếp được" (ray miss hoàn toàn, 0 hit log):
        // GameObject mới luôn sinh ra ở layer 0 ("Default") theo mặc định của Unity,
        // BẤT KỂ _target đang ở layer nào. Nếu project dùng 1 Physics Layer riêng cho
        // vải (rất phổ biến để cloth không tự va vào tay/controller VR ở layer khác),
        // piece mới sẽ lệch layer → Physics.RaycastAll/SphereCastAll của CuttingManager
        // không bao giờ thấy được collider của nó (collision matrix loại layer 0 ra),
        // dẫn đến ray "miss hoàn toàn" trên piece vừa cắt — không phải do logic đăng ký
        // (_cutters dictionary) sai, mà do tầng Physics/Unity. Sửa: luôn copy layer của
        // _target sang piece mới, để piece thừa hưởng đúng layer collision matrix.
        piece.layer = _target.layer;

        // Giữ piece trong cùng hierarchy với _target (parent gốc) để dễ quản lý/cleanup
        // và để FindClothParent() trong CuttingManager hoạt động đúng nếu collider con
        // được thêm bên dưới piece trong tương lai.
        if (_target.transform.parent != null)
            piece.transform.SetParent(_target.transform.parent, true);

        piece.AddComponent<MeshFilter>().mesh   = mesh;
        piece.AddComponent<MeshRenderer>().sharedMaterials = mats;

        if (_ucCloth != null)
        {
            var newCloth = piece.AddComponent<UCloth.UCCloth>();
            newCloth.preprocessorType     = _ucCloth.preprocessorType;
            newCloth.materialProperties   = _ucCloth.materialProperties;
            newCloth.simulationProperties = _ucCloth.simulationProperties;
            newCloth.qualityProperties    = _ucCloth.qualityProperties;
            newCloth.collisionProperties  = _ucCloth.collisionProperties;
            newCloth.thickness            = _ucCloth.thickness;
            newCloth.offsetFront          = _ucCloth.offsetFront;
            newCloth.smoothing            = _ucCloth.smoothing;

            newCloth.sphereColliders = (_ucCloth.sphereColliders  != null && _ucCloth.sphereColliders.Length  > 0)
                ? (SphereCollider[])_ucCloth.sphereColliders.Clone()  : new SphereCollider[0];
            newCloth.capsuleColliders = (_ucCloth.capsuleColliders != null && _ucCloth.capsuleColliders.Length > 0)
                ? (CapsuleCollider[])_ucCloth.capsuleColliders.Clone() : new CapsuleCollider[0];
            newCloth.cubeColliders = (_ucCloth.cubeColliders != null && _ucCloth.cubeColliders.Length > 0)
                ? (BoxCollider[])_ucCloth.cubeColliders.Clone()        : new BoxCollider[0];
            newCloth.pinColliders = (_ucCloth.pinColliders != null && _ucCloth.pinColliders.Count > 0)
                ? new System.Collections.Generic.List<Collider>(_ucCloth.pinColliders)
                : new System.Collections.Generic.List<Collider>();

            var mc        = piece.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex     = false;

            var rb         = piece.AddComponent<Rigidbody>();
            rb.useGravity  = true;
            rb.isKinematic = true;

            // SỬA: Chuyển đổi tính chất sang UClothLaserGrabber3 cho các mảnh con sau khi cắt
            var originalGrabber = _target.GetComponent<UClothLaserGrabber3>();
            if (originalGrabber != null)
            {
                var newGrabber = piece.AddComponent<UClothLaserGrabber3>();
                newGrabber.vrController       = originalGrabber.vrController;
                newGrabber.triggerAction      = originalGrabber.triggerAction;
                newGrabber.rightController    = originalGrabber.rightController;
                newGrabber.rightTriggerAction = originalGrabber.rightTriggerAction;
                newGrabber.pullForce          = originalGrabber.pullForce;
                newGrabber.sphereSize         = originalGrabber.sphereSize;
                newGrabber.hoverColor         = originalGrabber.hoverColor;
                newGrabber.grabColor          = originalGrabber.grabColor;
            }

            // SỬA: Chuyển đổi tính chất sang UClothPinner2 cho các mảnh con sau khi cắt
            // (phải AddComponent TRƯỚC khi gán grabber vì UClothPinner2.Start() dùng GetComponent<UClothLaserGrabber3>)
            var originalPinner = _target.GetComponent<UClothPinner2>();
            if (originalPinner != null)
            {
                var newPinner = piece.AddComponent<UClothPinner2>();

                // Gán grabber mới vừa thêm vào piece (nếu có), thay vì grabber của mesh gốc
                newPinner.grabber         = piece.GetComponent<UClothLaserGrabber3>();

                // Copy các tham chiếu ngoài + cài đặt từ pinner gốc
                newPinner.mannequinAnchor = originalPinner.mannequinAnchor;
                newPinner.pinLogger       = originalPinner.pinLogger;
                newPinner.pinAction       = originalPinner.pinAction;
                newPinner.pinForce        = originalPinner.pinForce;
                newPinner.pinDamping      = originalPinner.pinDamping;
                newPinner.maxPinSpeed     = originalPinner.maxPinSpeed;
                newPinner.snapThreshold   = originalPinner.snapThreshold;
                newPinner.debugMode       = originalPinner.debugMode;
            }

            Debug.Log($"[MeshCutter_UCloth] Piece '{name}': UCCloth added, {mesh.vertexCount} verts, {tc} tris.");
        }
        else
        {
            var mc        = piece.AddComponent<MeshCollider>();
            mc.convex     = false;
            mc.sharedMesh = mesh;

            var rb = piece.AddComponent<Rigidbody>();
            rb.AddForce(impulseDir * _splitForce + Vector3.up * 0.3f, ForceMode.Impulse);
            rb.AddTorque(URandom.insideUnitSphere * _splitForce * 0.5f, ForceMode.Impulse);
        }

        return piece;
    }

    // =========================================================================
    //  HELPERS
    // =========================================================================

    private void BuildRenderToSimLookup()
    {
        if (_ucCloth?.simData == null || !_ucCloth.simData.positionsReadOnly.IsCreated)
        {
            Debug.LogWarning($"[MeshCutter_UCloth] BuildRenderToSimLookup: simData chưa sẵn sàng.");
            return;
        }
        var sim    = _ucCloth.simData.positionsReadOnly;

        // FIX NaN lan truyền qua nhiều lần cắt liên tiếp: 'IsCreated' chỉ xác nhận
        // buffer đã alloc, KHÔNG xác nhận solver đã converge. Trên piece vừa cắt ra
        // (đặc biệt piece-của-piece, bị cắt nhiều lần), buffer có thể còn chứa giá
        // trị uninitialized/non-finite. Nếu build lookup từ dữ liệu này, mọi lần cắt
        // sau sẽ dùng GetWorldSpaceVertices() trả về NaN → dmesh NaN → mesh con NaN →
        // UCRenderer log "Failed extracting collision mesh ... non-finite value" và
        // MeshCollider không bake được nữa (ray miss tuyệt đối, im lặng).
        // Sửa: không build lookup nếu phát hiện NaN/Infinity — để lại _renderToSimLookup
        // = null, khiến GetWorldSpaceVertices() tự fallback về local-mesh-transform an
        // toàn (xem nhánh "if (_ucCloth == null || _renderToSimLookup == null ...)").
        if (!AllFinite(sim))
        {
            Debug.LogWarning($"[MeshCutter_UCloth] BuildRenderToSimLookup: simData của '{_target.name}' " +
                              $"chứa giá trị non-finite (NaN/Infinity) — solver có thể chưa converge hoặc " +
                              $"piece có topology bất thường sau khi cắt nhiều lần. BỎ QUA build lookup lần " +
                              $"này, sẽ dùng local mesh vertices (an toàn hơn) cho tới khi simData ổn định.");
            return;
        }

        var rVerts = _mf.mesh.vertices;
        int rCount = rVerts.Length;
        int sCount = sim.Length;

        _renderToSimLookup = new int[rCount];
        for (int ri = 0; ri < rCount; ri++)
        {
            Vector3 wp    = _tf.TransformPoint(rVerts[ri]);
            float   bestD = float.MaxValue;
            int     best  = ri < sCount ? ri : 0;
            for (int si = 0; si < sCount; si++)
            {
                var sp = sim[si];
                float dx = wp.x - sp.x, dy = wp.y - sp.y, dz = wp.z - sp.z;
                float d  = dx*dx + dy*dy + dz*dz;
                if (d < bestD) { bestD = d; best = si; }
            }
            _renderToSimLookup[ri] = best;
        }
        Debug.Log($"[MeshCutter_UCloth] Lookup built: {rCount} render verts → {sCount} sim nodes.");
    }

    /// <summary>
    /// Kiểm tra toàn bộ buffer vị trí sim không chứa NaN/Infinity. Viết 2 overload cho
    /// 2 kiểu thường gặp của simData.positionsReadOnly tùy bản UCCloth đang dùng
    /// (NativeArray&lt;Vector3&gt; hoặc NativeArray&lt;Unity.Mathematics.float3&gt;) —
    /// compiler tự chọn đúng overload tại compile-time theo kiểu suy luận của 'var sim'
    /// ở nơi gọi, KHÔNG cần runtime type-check/dynamic/boxing (an toàn cho IL2CPP/AOT).
    /// </summary>
    private static bool AllFinite(Unity.Collections.NativeArray<Vector3> buf)
    {
        for (int i = 0; i < buf.Length; i++)
        {
            var p = buf[i];
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)
                || float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z))
                return false;
        }
        return true;
    }

    private static bool AllFinite(Unity.Collections.NativeArray<Unity.Mathematics.float3> buf)
    {
        for (int i = 0; i < buf.Length; i++)
        {
            var p = buf[i];
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)
                || float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z))
                return false;
        }
        return true;
    }

    private Vector3[] GetWorldSpaceVertices(Vector3[] localVerts)
    {
        int n      = localVerts.Length;
        var result = new Vector3[n];

        if (_ucCloth == null || _renderToSimLookup == null
            || _ucCloth.simData == null || !_ucCloth.simData.positionsReadOnly.IsCreated)
        {
            for (int i = 0; i < n; i++) result[i] = _tf.TransformPoint(localVerts[i]);
            return result;
        }

        var sim = _ucCloth.simData.positionsReadOnly;

        // FIX NaN runtime: simData có thể đã hợp lệ lúc Initialize() nhưng trở thành
        // non-finite SAU ĐÓ (vd: solver tạm thời mất ổn định do va chạm mạnh, hoặc
        // piece vừa pin lại sau cắt). Tự kiểm tra lại mỗi lần gọi (rẻ, chỉ O(n) trên
        // 1 lần CommitCut, không phải mỗi frame) để KHÔNG BAO GIỜ trả NaN ra ngoài —
        // đây là tuyến phòng thủ cuối trước khi NaN có thể lan vào dmesh cắt.
        if (!AllFinite(sim))
        {
            Debug.LogWarning($"[MeshCutter_UCloth] GetWorldSpaceVertices: simData của '{_target.name}' " +
                              $"hiện đang chứa giá trị non-finite — fallback an toàn về local mesh vertices " +
                              $"cho lần cắt này (tránh tạo piece NaN không thể raycast được nữa).");
            for (int i = 0; i < n; i++) result[i] = _tf.TransformPoint(localVerts[i]);
            return result;
        }

        for (int ri = 0; ri < n; ri++)
        {
            int si = ri < _renderToSimLookup.Length ? _renderToSimLookup[ri] : ri;
            if (si >= 0 && si < sim.Length)
            { var p = sim[si]; result[ri] = new Vector3(p.x, p.y, p.z); }
            else
            { result[ri] = _tf.TransformPoint(localVerts[ri]); }
        }
        return result;
    }

    /// <summary>
    /// FIX lệch seam: vị trí world-space mà MeshCollider/raycast THỰC SỰ dùng để
    /// chạm vào — luôn là transform hiện tại của render mesh tĩnh, KHÔNG đi qua
    /// simData lookup. Đây là hệ tọa độ "gốc" của _cutPath (vì _cutPath được lấy
    /// từ hit.point trên collider này trong CuttingManager). Dùng để tính barycentric
    /// gốc của mỗi điểm path trước khi remap sang vị trí sim mới nhất.
    /// </summary>
    private Vector3[] GetColliderWorldSpaceVertices(Vector3[] localVerts)
    {
        int n = localVerts.Length;
        var result = new Vector3[n];
        for (int i = 0; i < n; i++) result[i] = _tf.TransformPoint(localVerts[i]);
        return result;
    }

    /// <summary>
    /// FIX "tia xác định seam bị lệch": với mỗi điểm trong <paramref name="path"/>
    /// (world-space, lấy từ raycast trên collider tĩnh — <paramref name="colliderSpaceVerts"/>),
    /// tìm tam giác gần nhất + toạ độ barycentric của điểm đó trên hệ collider-space,
    /// rồi dùng CHÍNH toạ độ barycentric ấy để nội suy lại vị trí tương ứng trên
    /// <paramref name="simSpaceVerts"/> (vị trí sim mới nhất dùng để build dmesh cắt).
    /// Kết quả: điểm path luôn bám đúng điểm trên bề mặt vải mà người dùng đã vẽ, kể
    /// cả khi vải đã dao động/di chuyển giữa lúc vẽ và lúc CommitCut() build dmesh.
    /// </summary>
    private List<Vector3> RemapPathToSimSpace(List<Vector3> path, int[] triangles,
                                               Vector3[] colliderSpaceVerts, Vector3[] simSpaceVerts)
    {
        var remapped = new List<Vector3>(path.Count);
        int triCount = triangles.Length / 3;

        // Brute-force O(path.Count * triCount) — chạy 1 lần duy nhất lúc CommitCut
        // (không phải mỗi frame) nên thường rẻ; cảnh báo nếu mesh rất lớn để dễ chẩn
        // đoán nếu CommitCut bắt đầu giật lag trên mesh có rất nhiều triangle.
        if ((long)path.Count * triCount > 2_000_000)
            Debug.LogWarning($"[MeshCutter_UCloth] RemapPathToSimSpace: path.Count={path.Count} × " +
                              $"triCount={triCount} khá lớn — có thể gây giật khi CommitCut(). " +
                              $"Cân nhắc giảm minPathSpacing hoặc tối ưu nếu xảy ra thường xuyên.");

        foreach (var p in path)
        {
            float bestDistSq = float.MaxValue;
            int   bestA = -1, bestB = -1, bestC = -1;
            float bestU = 0f, bestV = 0f, bestW = 0f;

            for (int t = 0; t < triCount; t++)
            {
                int ia = triangles[t * 3];
                int ib = triangles[t * 3 + 1];
                int ic = triangles[t * 3 + 2];

                Vector3 a = colliderSpaceVerts[ia];
                Vector3 b = colliderSpaceVerts[ib];
                Vector3 c = colliderSpaceVerts[ic];

                Vector3 closest = ClosestPointOnTriangleBarycentric(p, a, b, c, out float u, out float v, out float w);
                float dSq = (closest - p).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestA = ia; bestB = ib; bestC = ic;
                    bestU = u; bestV = v; bestW = w;
                }
            }

            if (bestA < 0)
            {
                // Không tìm được tam giác hợp lệ (mesh rỗng?) — giữ điểm gốc, không nội suy.
                remapped.Add(p);
                continue;
            }

            // Nội suy CHÍNH toạ độ barycentric đã tìm được sang hệ sim-space.
            Vector3 simPos = bestU * simSpaceVerts[bestA]
                            + bestV * simSpaceVerts[bestB]
                            + bestW * simSpaceVerts[bestC];
            remapped.Add(simPos);
        }

        return remapped;
    }

    /// <summary>Closest point trên tam giác (Ericson's method) + toạ độ barycentric (u,v,w; u+v+w=1).</summary>
    private static Vector3 ClosestPointOnTriangleBarycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c,
                                                               out float u, out float v, out float w)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) { u = 1f; v = 0f; w = 0f; return a; }

        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) { u = 0f; v = 1f; w = 0f; return b; }

        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) { u = 0f; v = 0f; w = 1f; return c; }

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            float t = d1 / (d1 - d3);
            u = 1f - t; v = t; w = 0f;
            return a + t * ab;
        }

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            float t = d2 / (d2 - d6);
            u = 1f - t; v = 0f; w = t;
            return a + t * ac;
        }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
        {
            float t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            u = 0f; v = 1f - t; w = t;
            return b + t * (c - b);
        }

        float denom = 1f / (vc + vb + va);
        v = vb * denom;
        w = vc * denom;
        u = 1f - v - w;
        return a + ab * v + ac * w;
    }

    private static Vector3 ComputeDMeshCenter(DMesh3 dmesh)
    {
        var sum  = Vector3d.Zero;
        int cnt  = 0;
        foreach (int vid in dmesh.VertexIndices())
        { sum += dmesh.GetVertex(vid); cnt++; }
        if (cnt == 0) return Vector3.zero;
        sum /= cnt;
        return new Vector3((float)sum.x, (float)sum.y, (float)sum.z);
    }

    // ── Gizmo path preview ─────────────────────────────────────────────────
    public void DrawDebugGizmos()
    {
        if (!showDebugPath || _cutPath.Count < 2) return;
        Gizmos.color = Color.red;
        for (int i = 0; i < _cutPath.Count - 1; i++)
            Gizmos.DrawLine(_cutPath[i], _cutPath[i + 1]);
        Gizmos.color = new Color(1f, 0.3f, 0f);
        foreach (var p in _cutPath)
            Gizmos.DrawSphere(p, 0.007f);
    }
}

public enum CutResult_Ucloth { None, Trimmed, Split }
