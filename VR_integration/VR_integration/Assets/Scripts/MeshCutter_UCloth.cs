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

    /// <summary>Số điểm đã tích lũy trên đường cắt.</summary>
    public int PathPointCount => _cutPath.Count;

    /// <summary>Xóa path hiện tại (khi hủy cut).</summary>
    public void ClearPath() => _cutPath.Clear();

    // =========================================================================
    //  COMMIT CUT — thực sự thực hiện cắt mesh dọc theo _cutPath tích lũy
    // =========================================================================
    public CutResult_Ucloth CommitCut(bool splitOnlyWhenDisconnected)
    {
        if (_mf == null || _cutPath.Count < 2)
        {
            Debug.LogWarning($"[MeshCutter_UCloth] CommitCut: path quá ngắn ({_cutPath.Count} điểm). Cần ít nhất 2.");
            return CutResult_Ucloth.None;
        }

        // Build world-space vertices từ simData nếu có UCCloth
        Mesh     mesh      = _mf.mesh;
        Vector3[] verts    = mesh.vertices;
        Vector3[] worldV   = GetWorldSpaceVertices(verts);

        // Chuyển Unity mesh → DMesh3 (world-space)
        var dmesh = G3MeshBridge.ToDMesh3(mesh, _tf, out _, useWorldSpace: true);
        // Đồng bộ vị trí từ simData nếu có (override vertex positions)
        if (_ucCloth != null && _renderToSimLookup != null
            && _ucCloth.simData != null && _ucCloth.simData.positionsReadOnly.IsCreated)
        {
            dmesh = RebuildDMeshFromSimPositions(mesh, worldV);
        }

        // Thực hiện cắt geometry3Sharp
        List<DMesh3> pieces = G3MeshCutter.CutAlongPath(dmesh, _cutPath, out string cutLog);
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

        // Tắt UCCloth gốc
        if (_ucCloth != null) _ucCloth.enabled = false;

        _lastCreatedPieces.Clear();
        Material[] mats       = _target.GetComponent<MeshRenderer>().sharedMaterials;
        Vector3    meshCenter = worldV.Aggregate(Vector3.zero, (s, v) => s + v) / worldV.Length;

        for (int c = 0; c < pieces.Count; c++)
        {
            Mesh pieceMesh = G3MeshBridge.ToUnityMesh(pieces[c], _tf,
                                out _, toLocalSpace: true);
            if (pieceMesh == null || pieceMesh.triangles.Length / 3 < MIN_TRIS_FOR_PIECE) continue;

            // Tính center của piece trong world space để tính hướng impulse
            Vector3 pieceCenter = ComputeDMeshCenter(pieces[c]);
            Vector3 impulseDir  = (pieceCenter - meshCenter).normalized;
            if (impulseDir.sqrMagnitude < 0.01f) impulseDir = URandom.onUnitSphere;

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

            var originalGrabber = _target.GetComponent<UClothLaserGrabber>();
            if (originalGrabber != null)
            {
                var newGrabber          = piece.AddComponent<UClothLaserGrabber>();
                newGrabber.vrController  = originalGrabber.vrController;
                newGrabber.grabSphere    = originalGrabber.grabSphere;
                newGrabber.triggerAction = originalGrabber.triggerAction;
                newGrabber.pullForce     = originalGrabber.pullForce;
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
