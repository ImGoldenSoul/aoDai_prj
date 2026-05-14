using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using URandom = UnityEngine.Random;

/// <summary>
/// Mesh cutter tương thích với UCloth — thuật toán Seam Split (tích lũy qua frame).
///
/// Không XÓA triangle. Thay vào đó:
///   1. Mỗi frame tích lũy các triangle mà cutter đi qua vào _accumulatedCutTris.
///   2. Dùng tập hợp đó làm "tường" trong flood-fill để kiểm tra xem mesh đã bị
///      chia thành >= 2 vùng liên thông chưa.
///   3. Khi đủ điều kiện split: nhân đôi seam vertex, xây dựng 2 mesh con sạch,
///      tạo 2 GameObject với UCCloth giữ nguyên tag.
///
/// v2 – Thêm PerformCutByRay(): phát hiện triangle bằng Ray thay vì Collider.
///       PerformCut(Collider) vẫn được giữ lại để backward-compatible.
///
/// v3 – FIX:
///   [Bug #1 & #2] Constructor KHÔNG gọi BuildRenderToSimLookup() sớm (simData chưa sẵn sàng).
///                 Initialize() mới là nơi build lookup, sau khi CuttingManager chờ simData xong.
///   [Bug #4]      MeshCollider trên piece dùng convex=false (cloth mesh là non-convex / phẳng).
/// </summary>
public class MeshCutter_UCloth
{
    private readonly GameObject     _target;
    private readonly Transform      _tf;
    private readonly MeshFilter     _mf;
    private readonly float          _splitForce;
    private readonly UCloth.UCCloth _ucCloth;

    // renderVertexIndex -> simNodeIndex
    // Được build trong Initialize() SAU KHI simData sẵn sàng, KHÔNG phải trong constructor.
    private int[] _renderToSimLookup;

    private const int MIN_TRIS_FOR_PIECE = 4;

    // Tích lũy triangle index đã bị cutter đi qua (dùng HashSet để không trùng lặp)
    private readonly HashSet<int> _accumulatedCutTris = new HashSet<int>();

    // Danh sách GameObject vừa được tạo trong lần Split gần nhất
    private readonly List<GameObject> _lastCreatedPieces = new List<GameObject>();

    // Report diện tích của lần Split gần nhất (null nếu chưa từng split)
    private ClothAreaCalculator.CutAreaReport? _lastAreaReport;

    /// <summary>Trả về report diện tích của lần cắt gần nhất (null nếu chưa split).</summary>
    public ClothAreaCalculator.CutAreaReport? GetLastAreaReport() => _lastAreaReport;

    // =========================================================================
    //  CONSTRUCTOR
    //  FIX Bug #1 & #2: KHÔNG gọi BuildRenderToSimLookup() ở đây.
    //  simData của UCCloth chưa sẵn sàng tại thời điểm constructor chạy
    //  (UCCloth.Start() chưa hoàn thành). Lookup sẽ được build trong Initialize().
    // =========================================================================
    public MeshCutter_UCloth(GameObject target, float splitForce)
    {
        _target     = target;
        _tf         = target.transform;
        _mf         = target.GetComponent<MeshFilter>();
        _splitForce = splitForce;
        _ucCloth    = target.GetComponent<UCloth.UCCloth>();

        if (_ucCloth != null)
        {
            // KHÔNG gọi BuildRenderToSimLookup() ở đây — simData chưa có.
            // Initialize() sẽ được CuttingManager gọi sau khi chờ simData xong.
            Debug.Log($"[MeshCutter_UCloth] UCCloth detected trên '{target.name}'. " +
                      $"Verts:{_mf.mesh.vertexCount} — chờ Initialize() để build lookup.");
        }
        else
        {
            Debug.Log($"[MeshCutter_UCloth] No UCCloth trên '{target.name}' — sẽ dùng TransformPoint.");
        }
    }

    // =========================================================================
    //  INITIALIZE
    //  FIX Bug #1 & #2: Build _renderToSimLookup TẠI ĐÂY, sau khi CuttingManager
    //  đã chờ UCCloth.simData sẵn sàng (coroutine InitCutterForObject).
    // =========================================================================
    /// <summary>
    /// Gọi từ CuttingManager SAU KHI UCCloth.simData đã được khởi tạo.
    /// Build render→sim vertex lookup để GetWorldSpaceVertices() hoạt động đúng.
    /// </summary>
    public void Initialize()
    {
        if (_ucCloth != null)
        {
            BuildRenderToSimLookup();

            bool lookupReady = _renderToSimLookup != null;
            int  simCount    = (_ucCloth.simData?.positionsReadOnly.IsCreated == true)
                               ? _ucCloth.simData.positionsReadOnly.Length : 0;

            Debug.Log($"[MeshCutter_UCloth] Initialized '{_target.name}' (Seam-Split mode). " +
                      $"Lookup built: {(lookupReady ? "YES" : "NO — simData chưa sẵn sàng!")} | " +
                      $"Verts:{_mf.mesh.vertexCount} SimNodes:{simCount}");

            if (!lookupReady)
                Debug.LogWarning($"[MeshCutter_UCloth] '{_target.name}': _renderToSimLookup = null sau Initialize(). " +
                                 "Sẽ fallback TransformPoint — vị trí đỉnh có thể sai khi cloth simulate.");
        }
        else
        {
            Debug.Log($"[MeshCutter_UCloth] Initialized '{_target.name}' (no UCCloth — TransformPoint mode).");
        }
    }

    /// <summary>Trả về danh sách piece GameObject tạo ra từ lần Split gần nhất.</summary>
    public List<GameObject> GetLastCreatedPieces() => new List<GameObject>(_lastCreatedPieces);

    // =========================================================================
    //  ENTRY POINT — Ray-based (v2, mặc định)
    // =========================================================================

    /// <summary>
    /// Tìm các triangle giao với ống trụ bao quanh <paramref name="ray"/>
    /// (bán kính <paramref name="radius"/>, chiều dài <paramref name="maxLength"/>)
    /// rồi tích lũy và thực hiện split giống hệt PerformCut(Collider).
    /// </summary>
    public CutResult_Ucloth PerformCutByRay(Ray ray, float maxLength, float radius,
                                             bool splitOnlyWhenDisconnected)
    {
        if (_mf == null) return CutResult_Ucloth.None;

        Mesh      mesh    = _mf.mesh;
        Vector3[] verts   = mesh.vertices;
        int[]     tris    = mesh.triangles;
        Vector2[] uv      = mesh.uv;
        Vector3[] normals = mesh.normals;

        if (verts.Length == 0 || tris.Length == 0) return CutResult_Ucloth.None;

        bool hasUV    = uv      != null && uv.Length      == verts.Length;
        bool hasNrm   = normals != null && normals.Length == verts.Length;
        int  triCount = tris.Length / 3;

        Vector3[] worldVerts = GetWorldSpaceVertices(verts);

        // ── Bước 1: Tìm các triangle bị ray "quét" qua FRAME NÀY ────────────
        int newThisFrame = 0;
        float radiusSqr  = radius * radius;

        for (int t = 0; t < triCount; t++)
        {
            if (_accumulatedCutTris.Contains(t)) continue;

            int     iA = tris[t * 3], iB = tris[t * 3 + 1], iC = tris[t * 3 + 2];
            Vector3 wA = worldVerts[iA], wB = worldVerts[iB], wC = worldVerts[iC];

            if (TriangleIntersectsRayCylinder(wA, wB, wC, ray, maxLength, radiusSqr))
            {
                _accumulatedCutTris.Add(t);
                newThisFrame++;
            }
        }

        if (newThisFrame == 0 && _accumulatedCutTris.Count == 0)
            return CutResult_Ucloth.None;

        // ── Bước 2-5: Giống hệt PerformCut(Collider) ─────────────────────────
        return ExecuteSplitLogic(worldVerts, tris, normals, uv, verts,
                                 hasNrm, hasUV, triCount,
                                 newThisFrame, splitOnlyWhenDisconnected);
    }

    // =========================================================================
    //  ENTRY POINT — Collider-based (giữ lại để backward-compatible)
    // =========================================================================

    public CutResult_Ucloth PerformCut(Collider cutter, bool splitOnlyWhenDisconnected)
    {
        if (_mf == null) return CutResult_Ucloth.None;

        Mesh      mesh    = _mf.mesh;
        Vector3[] verts   = mesh.vertices;
        int[]     tris    = mesh.triangles;
        Vector2[] uv      = mesh.uv;
        Vector3[] normals = mesh.normals;

        if (verts.Length == 0 || tris.Length == 0) return CutResult_Ucloth.None;

        bool hasUV    = uv      != null && uv.Length      == verts.Length;
        bool hasNrm   = normals != null && normals.Length == verts.Length;
        int  triCount = tris.Length / 3;

        Vector3[] worldVerts = GetWorldSpaceVertices(verts);

        int newThisFrame = 0;
        for (int t = 0; t < triCount; t++)
        {
            if (_accumulatedCutTris.Contains(t)) continue;

            int iA = tris[t*3], iB = tris[t*3+1], iC = tris[t*3+2];
            if (TriangleIntersectsCollider(worldVerts[iA], worldVerts[iB], worldVerts[iC], cutter))
            {
                _accumulatedCutTris.Add(t);
                newThisFrame++;
            }
        }

        if (newThisFrame == 0 && _accumulatedCutTris.Count == 0)
            return CutResult_Ucloth.None;

        return ExecuteSplitLogic(worldVerts, tris, normals, uv, verts,
                                 hasNrm, hasUV, triCount,
                                 newThisFrame, splitOnlyWhenDisconnected);
    }

    // =========================================================================
    //  SHARED SPLIT LOGIC (dùng chung cho cả Ray và Collider path)
    // =========================================================================

    private CutResult_Ucloth ExecuteSplitLogic(
        Vector3[] worldVerts, int[] tris, Vector3[] normals, Vector2[] uv, Vector3[] verts,
        bool hasNrm, bool hasUV, int triCount,
        int newThisFrame, bool splitOnlyWhenDisconnected)
    {
        bool[] cutWall = new bool[triCount];
        foreach (int t in _accumulatedCutTris) cutWall[t] = true;

        var triAdj     = BuildTriangleAdjacency(tris, triCount);
        var components = FindConnectedComponentsExcluding(triCount, cutWall, triAdj);

        Debug.Log($"[MeshCutter_UCloth] accum={_accumulatedCutTris.Count}/{triCount} | components={components.Count}");

        if (components.Count == 0) return CutResult_Ucloth.None;

        bool doSplit = !splitOnlyWhenDisconnected || components.Count >= 2;
        if (!doSplit)
            return newThisFrame > 0 ? CutResult_Ucloth.Trimmed : CutResult_Ucloth.None;

        _lastAreaReport = ClothAreaCalculator.BuildReport(
            worldVerts, tris, components, _accumulatedCutTris);
        Debug.Log(_lastAreaReport.Value.ToString());

        if (_ucCloth != null) _ucCloth.enabled = false;

        _lastCreatedPieces.Clear();
        Material[] mats       = _target.GetComponent<MeshRenderer>().sharedMaterials;
        Vector3    meshCenter = ComputeCenter(worldVerts);

        for (int c = 0; c < components.Count; c++)
        {
            var     compTris  = components[c];
            Mesh    pieceMesh = BuildMeshFromTris(compTris, tris, verts, normals, uv, hasNrm, hasUV);
            if (pieceMesh == null) continue;

            Vector3 pieceCenter = ComputeCenterFromTriangles(compTris, tris, worldVerts);
            Vector3 impulseDir  = (pieceCenter - meshCenter).normalized;
            if (impulseDir.sqrMagnitude < 0.01f) impulseDir = URandom.onUnitSphere;

            var piece = CreatePiece($"{_target.name}_Piece{c}", pieceMesh, mats, impulseDir);
            if (piece != null) _lastCreatedPieces.Add(piece);
        }

        return CutResult_Ucloth.Split;
    }

    // =========================================================================
    //  BUILD MESH — giữ tất cả triangle của component, không xóa gì cả.
    // =========================================================================

    private Mesh BuildMeshFromTris(List<int> triIdx, int[] tris,
        Vector3[] verts, Vector3[] normals, Vector2[] uv, bool hasNrm, bool hasUV)
    {
        if (triIdx.Count == 0) return null;

        var remap  = new Dictionary<int,int>();
        var nV     = new List<Vector3>();
        var nN     = new List<Vector3>();
        var nUV    = new List<Vector2>();
        var nTris  = new List<int>();

        foreach (int t in triIdx)
        {
            for (int k = 0; k < 3; k++)
            {
                int old = tris[t*3+k];
                if (!remap.ContainsKey(old))
                {
                    remap[old] = nV.Count;
                    nV.Add(verts[old]);
                    if (hasNrm) nN.Add(normals[old]);
                    if (hasUV)  nUV.Add(uv[old]);
                }
                nTris.Add(remap[old]);
            }
        }

        var m = new Mesh();
        m.SetVertices(nV);
        if (hasNrm && nN.Count == nV.Count) m.SetNormals(nN);
        if (hasUV  && nUV.Count == nV.Count) m.SetUVs(0, nUV);
        m.SetTriangles(nTris, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    // =========================================================================
    //  RAY-CYLINDER INTERSECTION TEST
    // =========================================================================

    /// <summary>
    /// Trả về true nếu triangle (wA, wB, wC) giao với ống trụ xung quanh ray.
    /// </summary>
    private static bool TriangleIntersectsRayCylinder(
        Vector3 wA, Vector3 wB, Vector3 wC,
        Ray ray, float maxLength, float radiusSqr)
    {
        // 1. Kiểm tra từng đỉnh + centroid
        Vector3 centroid = (wA + wB + wC) / 3f;
        if (PointNearRay(wA,       ray, maxLength, radiusSqr)) return true;
        if (PointNearRay(wB,       ray, maxLength, radiusSqr)) return true;
        if (PointNearRay(wC,       ray, maxLength, radiusSqr)) return true;
        if (PointNearRay(centroid, ray, maxLength, radiusSqr)) return true;

        // 2. Kiểm tra từng cạnh
        if (SegmentNearRay(wA, wB, ray, maxLength, radiusSqr)) return true;
        if (SegmentNearRay(wB, wC, ray, maxLength, radiusSqr)) return true;
        if (SegmentNearRay(wC, wA, ray, maxLength, radiusSqr)) return true;

        // 3. Ray-triangle intersection (Möller–Trumbore)
        return RayIntersectsTriangle(ray, maxLength, wA, wB, wC);
    }

    private static bool PointNearRay(Vector3 p, Ray ray, float maxLength, float radiusSqr)
    {
        Vector3 d  = p - ray.origin;
        float   t  = Vector3.Dot(d, ray.direction);
        if (t < 0f || t > maxLength) return false;

        Vector3 closest = ray.origin + ray.direction * t;
        return (p - closest).sqrMagnitude <= radiusSqr;
    }

    private static bool SegmentNearRay(Vector3 sA, Vector3 sB,
                                        Ray ray, float maxLength, float radiusSqr)
    {
        Vector3 rayEnd = ray.origin + ray.direction * maxLength;

        Vector3 d1 = sB      - sA;
        Vector3 d2 = rayEnd  - ray.origin;
        Vector3 r  = sA      - ray.origin;

        float a = Vector3.Dot(d1, d1);
        float e = Vector3.Dot(d2, d2);
        float f = Vector3.Dot(d2, r);

        float s, t;

        if (a <= 1e-8f && e <= 1e-8f)
        {
            return r.sqrMagnitude <= radiusSqr;
        }
        if (a <= 1e-8f)
        {
            s = 0f;
            t = Mathf.Clamp01(f / e);
        }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= 1e-8f)
            {
                t = 0f;
                s = Mathf.Clamp01(-c / a);
            }
            else
            {
                float b    = Vector3.Dot(d1, d2);
                float denom = a * e - b * b;
                if (Mathf.Abs(denom) > 1e-8f)
                    s = Mathf.Clamp01((b * f - c * e) / denom);
                else
                    s = 0f;

                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
            }
        }

        Vector3 closest1 = sA        + d1 * s;
        Vector3 closest2 = ray.origin + d2 * t;
        return (closest1 - closest2).sqrMagnitude <= radiusSqr;
    }

    private static bool RayIntersectsTriangle(Ray ray, float maxLength,
                                               Vector3 v0, Vector3 v1, Vector3 v2)
    {
        const float EPSILON = 1e-8f;

        Vector3 edge1 = v1 - v0;
        Vector3 edge2 = v2 - v0;
        Vector3 h     = Vector3.Cross(ray.direction, edge2);
        float   a     = Vector3.Dot(edge1, h);

        if (a > -EPSILON && a < EPSILON) return false;

        float   f = 1f / a;
        Vector3 s = ray.origin - v0;
        float   u = f * Vector3.Dot(s, h);
        if (u < 0f || u > 1f) return false;

        Vector3 q = Vector3.Cross(s, edge1);
        float   v = f * Vector3.Dot(ray.direction, q);
        if (v < 0f || u + v > 1f) return false;

        float t = f * Vector3.Dot(edge2, q);
        return t >= 0f && t <= maxLength;
    }

    // =========================================================================
    //  COLLIDER INTERSECTION TEST (giữ lại cho PerformCut(Collider))
    // =========================================================================

    private bool TriangleIntersectsCollider(Vector3 wA, Vector3 wB, Vector3 wC, Collider col)
    {
        if (col is BoxCollider bc)
        {
            Transform t   = bc.transform;
            Vector3   ext = bc.size * 0.5f;
            Vector3   ctr = bc.center;

            Vector3 lA = t.InverseTransformPoint(wA) - ctr;
            Vector3 lB = t.InverseTransformPoint(wB) - ctr;
            Vector3 lC = t.InverseTransformPoint(wC) - ctr;

            if (PointInBox(lA, ext)) return true;
            if (PointInBox(lB, ext)) return true;
            if (PointInBox(lC, ext)) return true;

            if (SegmentIntersectsAABB(lA, lB, ext)) return true;
            if (SegmentIntersectsAABB(lB, lC, ext)) return true;
            if (SegmentIntersectsAABB(lC, lA, ext)) return true;

            Vector3 lCen = (lA + lB + lC) / 3f;
            if (PointInBox(lCen, ext)) return true;

            return false;
        }

        if (col is SphereCollider sc)
        {
            Vector3 wCenter = sc.transform.TransformPoint(sc.center);
            float   wRadius = sc.radius * Mathf.Max(
                Mathf.Abs(sc.transform.lossyScale.x),
                Mathf.Abs(sc.transform.lossyScale.y),
                Mathf.Abs(sc.transform.lossyScale.z));

            float d2 = PointToTriangleSqDist(wCenter, wA, wB, wC);
            return d2 <= wRadius * wRadius;
        }

        float tol = 0.001f;
        if (Vector3.SqrMagnitude(col.ClosestPoint(wA) - wA) < tol * tol) return true;
        if (Vector3.SqrMagnitude(col.ClosestPoint(wB) - wB) < tol * tol) return true;
        if (Vector3.SqrMagnitude(col.ClosestPoint(wC) - wC) < tol * tol) return true;
        return false;
    }

    private static bool PointInBox(Vector3 p, Vector3 ext)
        => Mathf.Abs(p.x) <= ext.x && Mathf.Abs(p.y) <= ext.y && Mathf.Abs(p.z) <= ext.z;

    private static bool SegmentIntersectsAABB(Vector3 a, Vector3 b, Vector3 ext)
    {
        Vector3 d    = b - a;
        float   tMin = 0f, tMax = 1f;

        for (int i = 0; i < 3; i++)
        {
            float ai  = i == 0 ? a.x : (i == 1 ? a.y : a.z);
            float di  = i == 0 ? d.x : (i == 1 ? d.y : d.z);
            float ei  = i == 0 ? ext.x : (i == 1 ? ext.y : ext.z);

            if (Mathf.Abs(di) < 1e-8f)
            {
                if (ai < -ei || ai > ei) return false;
            }
            else
            {
                float t1 = (-ei - ai) / di;
                float t2 = ( ei - ai) / di;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                tMin = Mathf.Max(tMin, t1);
                tMax = Mathf.Min(tMax, t2);
                if (tMin > tMax) return false;
            }
        }
        return true;
    }

    private static float PointToTriangleSqDist(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b-a, ac = c-a, ap = p-a;
        float d1 = Vector3.Dot(ab,ap), d2 = Vector3.Dot(ac,ap);
        if (d1<=0 && d2<=0) return (p-a).sqrMagnitude;

        Vector3 bp = p-b;
        float d3 = Vector3.Dot(ab,bp), d4 = Vector3.Dot(ac,bp);
        if (d3>=0 && d4<=d3) return (p-b).sqrMagnitude;

        Vector3 cp = p-c;
        float d5 = Vector3.Dot(ab,cp), d6 = Vector3.Dot(ac,cp);
        if (d6>=0 && d5<=d6) return (p-c).sqrMagnitude;

        float vc = d1*d4 - d3*d2;
        if (vc<=0 && d1>=0 && d3<=0)
        { float v = d1/(d1-d3); return (p-(a+v*ab)).sqrMagnitude; }

        float vb = d5*d2 - d1*d6;
        if (vb<=0 && d2>=0 && d6<=0)
        { float w = d2/(d2-d6); return (p-(a+w*ac)).sqrMagnitude; }

        float va = d3*d6 - d5*d4;
        if (va<=0 && (d4-d3)>=0 && (d5-d6)>=0)
        { float w2 = (d4-d3)/((d4-d3)+(d5-d6)); return (p-(b+w2*(c-b))).sqrMagnitude; }

        float denom = 1f/(vc+vb+va);
        float vv = vb*denom, ww = vc*denom;
        return (p-(a + ab*vv + ac*ww)).sqrMagnitude;
    }

    // =========================================================================
    //  TẠO PIECE — copy UCCloth settings + tag
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

        piece.AddComponent<MeshFilter>().mesh = mesh;
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

            // Copy toàn bộ collider arrays từ original UCCloth sang piece.
            // Dùng null-safe copy: nếu original null/empty thì gán empty array để
            // UCCloth.FilterColliders() không crash, đồng thời vẫn giữ nguyên reference
            // khi original có collider thực sự.
            newCloth.sphereColliders = (_ucCloth.sphereColliders != null && _ucCloth.sphereColliders.Length > 0)
                ? (SphereCollider[])_ucCloth.sphereColliders.Clone()
                : new SphereCollider[0];

            newCloth.capsuleColliders = (_ucCloth.capsuleColliders != null && _ucCloth.capsuleColliders.Length > 0)
                ? (CapsuleCollider[])_ucCloth.capsuleColliders.Clone()
                : new CapsuleCollider[0];

            newCloth.cubeColliders = (_ucCloth.cubeColliders != null && _ucCloth.cubeColliders.Length > 0)
                ? (BoxCollider[])_ucCloth.cubeColliders.Clone()
                : new BoxCollider[0];

            newCloth.pinColliders = (_ucCloth.pinColliders != null && _ucCloth.pinColliders.Count > 0)
                ? new System.Collections.Generic.List<Collider>(_ucCloth.pinColliders)
                : new System.Collections.Generic.List<Collider>();

            // FIX Bug #4: cloth mesh là non-convex (mặt phẳng/dạng tấm).
            // convex=true trên mesh phẳng thường bị Unity reject hoặc tạo collider sai,
            // khiến các piece spawn ra không thể cắt tiếp. Dùng convex=false.
            // Nếu cần Rigidbody + physics collision, hãy dùng primitive collider riêng.
            var mc = piece.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex     = false;

            var rb = piece.AddComponent<Rigidbody>();
            rb.useGravity  = true;
            rb.isKinematic = true;

            var originalGrabber = _target.GetComponent<UClothLaserGrabber>();
            if (originalGrabber != null)
            {
                var newGrabber = piece.AddComponent<UClothLaserGrabber>();
                newGrabber.vrController  = originalGrabber.vrController;
                newGrabber.grabSphere    = originalGrabber.grabSphere;
                newGrabber.triggerAction = originalGrabber.triggerAction;
                newGrabber.pullForce     = originalGrabber.pullForce;
                Debug.Log($"[MeshCutter_UCloth] Piece '{name}': UClothLaserGrabber copied → piece có thể tương tác.");
            }
            else
            {
                Debug.LogWarning($"[MeshCutter_UCloth] Piece '{name}': Không tìm thấy UClothLaserGrabber trên original '{_target.name}'. Piece sẽ không thể grab được.");
            }

            Debug.Log($"[MeshCutter_UCloth] Piece '{name}': UCCloth + Rigidbody(gravity) + MeshCollider(non-convex) added, {mesh.vertexCount} verts, {tc} tris.");
        }
        else
        {
            var mc = piece.AddComponent<MeshCollider>();
            mc.convex = false; mc.sharedMesh = mesh; mc.enabled = false;

            var rb = piece.AddComponent<Rigidbody>();
            rb.AddForce(impulseDir * _splitForce + Vector3.up * 0.3f, ForceMode.Impulse);
            rb.AddTorque(URandom.insideUnitSphere * _splitForce * 0.5f, ForceMode.Impulse);
        }

        return piece;
    }

    // =========================================================================
    //  CÁC HELPER
    // =========================================================================

    /// <summary>
    /// Build _renderToSimLookup: ánh xạ render vertex index → sim node index gần nhất.
    /// CHỈ gọi từ Initialize(), sau khi simData đã sẵn sàng.
    /// </summary>
    private void BuildRenderToSimLookup()
    {
        if (_ucCloth == null) return;
        if (_ucCloth.simData == null || !_ucCloth.simData.positionsReadOnly.IsCreated)
        {
            Debug.LogWarning($"[MeshCutter_UCloth] BuildRenderToSimLookup: simData chưa sẵn sàng cho '{_target.name}'. Lookup không được build.");
            return;
        }

        var sim    = _ucCloth.simData.positionsReadOnly;
        var rVerts = _mf.mesh.vertices;
        int rCount = rVerts.Length;
        int sCount = sim.Length;

        _renderToSimLookup = new int[rCount];
        for (int ri = 0; ri < rCount; ri++)
        {
            Vector3 wp = _tf.TransformPoint(rVerts[ri]);
            float bestD = float.MaxValue;
            int   best  = ri < sCount ? ri : 0;
            for (int si = 0; si < sCount; si++)
            {
                var sp = sim[si];
                float dx=wp.x-sp.x, dy=wp.y-sp.y, dz=wp.z-sp.z;
                float d = dx*dx+dy*dy+dz*dz;
                if (d < bestD) { bestD=d; best=si; }
            }
            _renderToSimLookup[ri] = best;
        }

        Debug.Log($"[MeshCutter_UCloth] BuildRenderToSimLookup: {rCount} render verts → {sCount} sim nodes mapped.");
    }

    private Vector3[] GetWorldSpaceVertices(Vector3[] localVerts)
    {
        int n = localVerts.Length;
        var result = new Vector3[n];

        if (_ucCloth == null || _renderToSimLookup == null ||
            _ucCloth.simData == null || !_ucCloth.simData.positionsReadOnly.IsCreated)
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

    private List<List<int>> BuildTriangleAdjacency(int[] tris, int triCount)
    {
        var edgeMap = new Dictionary<(int,int), List<int>>();
        void AddEdge(int a, int b, int t)
        {
            var key = a<b?(a,b):(b,a);
            if (!edgeMap.ContainsKey(key)) edgeMap[key] = new List<int>();
            edgeMap[key].Add(t);
        }
        for (int t = 0; t < triCount; t++)
        {
            int iA=tris[t*3], iB=tris[t*3+1], iC=tris[t*3+2];
            AddEdge(iA,iB,t); AddEdge(iB,iC,t); AddEdge(iC,iA,t);
        }
        var adj = new List<List<int>>(triCount);
        for (int t = 0; t < triCount; t++) adj.Add(new List<int>());
        foreach (var kv in edgeMap)
        {
            var list = kv.Value;
            for (int i=0;i<list.Count;i++)
                for (int j=i+1;j<list.Count;j++)
                { adj[list[i]].Add(list[j]); adj[list[j]].Add(list[i]); }
        }
        return adj;
    }

    private List<List<int>> FindConnectedComponentsExcluding(
        int triCount, bool[] excluded, List<List<int>> adj)
    {
        var visited = new bool[triCount];
        var result  = new List<List<int>>();

        for (int start = 0; start < triCount; start++)
        {
            if (excluded[start] || visited[start]) continue;
            var comp  = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(start);
            visited[start] = true;
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                comp.Add(cur);
                foreach (int nb in adj[cur])
                    if (!excluded[nb] && !visited[nb])
                    { visited[nb]=true; queue.Enqueue(nb); }
            }
            result.Add(comp);
        }
        return result;
    }

    private Vector3 ComputeCenter(Vector3[] w)
    {
        var s = Vector3.zero;
        foreach (var v in w) s += v;
        return s / Mathf.Max(1, w.Length);
    }

    private Vector3 ComputeCenterFromTriangles(List<int> triIdx, int[] tris, Vector3[] w)
    {
        var s=Vector3.zero; int n=0;
        foreach (int t in triIdx)
        { s+=w[tris[t*3]]; s+=w[tris[t*3+1]]; s+=w[tris[t*3+2]]; n+=3; }
        return n>0?s/n:Vector3.zero;
    }

    public void DrawDebugGizmos() { }
}

public enum CutResult_Ucloth { None, Trimmed, Split }
