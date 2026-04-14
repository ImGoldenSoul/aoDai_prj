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
/// </summary>
public class MeshCutter_UCloth
{
    private readonly GameObject     _target;
    private readonly Transform      _tf;
    private readonly MeshFilter     _mf;
    private readonly float          _splitForce;
    private readonly UCloth.UCCloth _ucCloth;

    // renderVertexIndex -> simNodeIndex
    private int[] _renderToSimLookup;

    private const int MIN_TRIS_FOR_PIECE =4;

    // Tích lũy triangle index đã bị cutter đi qua (dùng HashSet để không trùng lặp)
    private readonly HashSet<int> _accumulatedCutTris = new HashSet<int>();

    // Danh sách GameObject vừa được tạo trong lần Split gần nhất
    private readonly List<GameObject> _lastCreatedPieces = new List<GameObject>();

    public MeshCutter_UCloth(GameObject target, float splitForce)
    {
        _target     = target;
        _tf         = target.transform;
        _mf         = target.GetComponent<MeshFilter>();
        _splitForce = splitForce;
        _ucCloth    = target.GetComponent<UCloth.UCCloth>();

        if (_ucCloth != null)
        {
            BuildRenderToSimLookup();
            int simCount = (_ucCloth.simData?.positionsReadOnly.IsCreated == true)
                ? _ucCloth.simData.positionsReadOnly.Length : 0;
            Debug.Log($"[MeshCutter_UCloth] UCCloth detected. Verts:{_mf.mesh.vertexCount} SimNodes:{simCount}");
        }
        else
        {
            Debug.Log("[MeshCutter_UCloth] No UCCloth — using TransformPoint.");
        }
    }

    /// <summary>Gọi từ CuttingManager sau khi UCCloth đã init.</summary>
    public void Initialize()
    {
        // Không cần tính threshold nữa vì không dùng centroid removal.
        Debug.Log($"[MeshCutter_UCloth] Initialized (Seam-Split mode).");
    }

    /// <summary>Trả về danh sách piece GameObject tạo ra từ lần Split gần nhất.</summary>
    public List<GameObject> GetLastCreatedPieces() => new List<GameObject>(_lastCreatedPieces);

    // =========================================================================
    //  ENTRY POINT
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

        // ── Bước 1: Tìm các triangle bị cutter chạm vào FRAME NÀY ────────────
        //    Điều kiện: ít nhất 1 vertex nằm bên trong collider cutter,
        //               HOẶC bất kỳ cạnh nào của triangle cắt qua collider.

        int newThisFrame = 0;
        for (int t = 0; t < triCount; t++)
        {
            if (_accumulatedCutTris.Contains(t)) continue; // đã ghi nhận rồi

            int iA = tris[t*3], iB = tris[t*3+1], iC = tris[t*3+2];
            if (TriangleIntersectsCollider(worldVerts[iA], worldVerts[iB], worldVerts[iC], cutter))
            {
                _accumulatedCutTris.Add(t);
                newThisFrame++;
            }
        }

        if (newThisFrame == 0 && _accumulatedCutTris.Count == 0)
            return CutResult_Ucloth.None;

        // ── Bước 2: Dùng tổng triangle đã cắt làm "tường" flood-fill ─────────

        bool[] cutWall = new bool[triCount];
        foreach (int t in _accumulatedCutTris) cutWall[t] = true;

        var triAdj     = BuildTriangleAdjacency(tris, triCount);
        var components = FindConnectedComponentsExcluding(triCount, cutWall, triAdj);

        Debug.Log($"[MeshCutter_UCloth] accum={_accumulatedCutTris.Count}/{triCount} | components={components.Count}");

        if (components.Count == 0) return CutResult_Ucloth.None;

        // ── Bước 3: Quyết định split hay chờ thêm ────────────────────────────

        bool doSplit = !splitOnlyWhenDisconnected || components.Count >= 2;
        if (!doSplit)
            return newThisFrame > 0 ? CutResult_Ucloth.Trimmed : CutResult_Ucloth.None;

        // ── Bước 4: Xây dựng 2 mesh con ──────────────────────────────────────

        if (_ucCloth != null) _ucCloth.enabled = false;

        _lastCreatedPieces.Clear();
        Material[] mats       = _target.GetComponent<MeshRenderer>().sharedMaterials;
        Vector3    meshCenter = ComputeCenter(worldVerts);

        for (int c = 0; c < components.Count; c++)
        {
            var     compTris    = components[c];
            Mesh    pieceMesh   = BuildMeshFromTris(compTris, tris, verts, normals, uv, hasNrm, hasUV);
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
    //  Kiểm tra triangle giao với collider — KHÔNG dùng Physics API
    //  (hoạt động cả khi kéo tay trong Scene view, không cần sync transform)
    // =========================================================================

    private bool TriangleIntersectsCollider(Vector3 wA, Vector3 wB, Vector3 wC, Collider col)
    {
        if (col is BoxCollider bc)
        {
            // Chuyển 3 đỉnh về local space của box rồi test OBB
            Transform t   = bc.transform;
            Vector3   ext = bc.size * 0.5f;
            Vector3   ctr = bc.center;

            Vector3 lA = t.InverseTransformPoint(wA) - ctr;
            Vector3 lB = t.InverseTransformPoint(wB) - ctr;
            Vector3 lC = t.InverseTransformPoint(wC) - ctr;

            // Kiểm tra từng đỉnh nằm trong box
            if (PointInBox(lA, ext)) return true;
            if (PointInBox(lB, ext)) return true;
            if (PointInBox(lC, ext)) return true;

            // Kiểm tra các cạnh triangle cắt qua box (segment-AABB Slab test)
            if (SegmentIntersectsAABB(lA, lB, ext)) return true;
            if (SegmentIntersectsAABB(lB, lC, ext)) return true;
            if (SegmentIntersectsAABB(lC, lA, ext)) return true;

            // Kiểm tra tâm triangle
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

        // Fallback cho collider loại khác: dùng ClosestPoint với tolerance
        float tol = 0.001f;
        if (Vector3.SqrMagnitude(col.ClosestPoint(wA) - wA) < tol * tol) return true;
        if (Vector3.SqrMagnitude(col.ClosestPoint(wB) - wB) < tol * tol) return true;
        if (Vector3.SqrMagnitude(col.ClosestPoint(wC) - wC) < tol * tol) return true;
        return false;
    }

    // Điểm có nằm trong AABB [-ext, +ext] không?
    private static bool PointInBox(Vector3 p, Vector3 ext)
        => Mathf.Abs(p.x) <= ext.x && Mathf.Abs(p.y) <= ext.y && Mathf.Abs(p.z) <= ext.z;

    // Segment AB cắt AABB [-ext,+ext]? — Slab method
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

    // Khoảng cách bình phương từ điểm P đến tam giác ABC
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

        // Tag giống original
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

            newCloth.sphereColliders  = _ucCloth.sphereColliders;
            newCloth.capsuleColliders = _ucCloth.capsuleColliders;
            newCloth.cubeColliders    = _ucCloth.cubeColliders;

            // Copy pinColliders từ original
            newCloth.pinColliders = new System.Collections.Generic.List<Collider>(_ucCloth.pinColliders);

            // MeshCollider để va chạm vật lý (convex bắt buộc khi dùng với Rigidbody)
            var mc = piece.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex     = true;

            // Rigidbody: gravity bật, KHÔNG kinematic → piece rơi tự do
            var rb = piece.AddComponent<Rigidbody>();
            rb.useGravity  = true;
            rb.isKinematic = true;

            // ── Sao chép UClothLaserGrabber sang mảnh mới ──────────────────
            // Thiếu bước này khiến các mảnh sau khi cắt không thể tương tác được.
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

            Debug.Log($"[MeshCutter_UCloth] Piece '{name}': UCCloth + Rigidbody(gravity) + MeshCollider added, {mesh.vertexCount} verts, {tc} tris.");
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

    private void BuildRenderToSimLookup()
    {
        if (_ucCloth.simData == null || !_ucCloth.simData.positionsReadOnly.IsCreated) return;

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

    /// <summary>
    /// Flood-fill nhưng KHÔNG đi qua các triangle bị loại trừ (excluded).
    /// Khác FindConnectedComponents cũ: không loại excluded khỏi kết quả —
    /// thay vào đó dùng excluded làm TƯỜNG ngăn giữa các component.
    /// </summary>
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
