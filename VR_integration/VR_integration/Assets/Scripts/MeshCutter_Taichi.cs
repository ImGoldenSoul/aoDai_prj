using System.Collections.Generic;
using UnityEngine;
using URandom = UnityEngine.Random;

/// <summary>
/// MeshCutter cho miếng vải mô phỏng vật lý bằng Taichi (qua UDP).
///
/// Thuật toán Seam-Split tích lũy — mirror hoàn toàn MeshCutter_UCloth:
///   1. Mỗi frame tích lũy các triangle mà cutter đi qua vào _accumulatedCutTris.
///   2. Dùng tập hợp đó làm "tường" trong flood-fill để kiểm tra >= 2 vùng liên thông.
///   3. Khi đủ điều kiện split: xây 2 mesh con, tạo 2 GameObject với TaichiClothVR
///      (giữ tag 'Cloth', copy toàn bộ cấu hình UDP, và có thể tiếp tục cắt).
///
/// Điểm khác biệt so với bản UCCloth:
///   - Không có simData / renderToSimLookup.
///     Vertex world-space lấy trực tiếp từ clothMesh.vertices (đã được TaichiClothVR cập nhật mỗi frame).
///   - Mỗi piece kế thừa TaichiClothVR với cùng cấu hình mạng, nhưng mesh topology
///     thu nhỏ theo đúng tập triangle của piece đó, và Python tự reinit với kích thước mới.
/// </summary>
public class MeshCutter_Taichi
{
    // ── refs ─────────────────────────────────────────────────────────────────
    private readonly GameObject      _target;
    private readonly Transform       _tf;
    private readonly MeshFilter      _mf;
    private readonly float           _splitForce;
    private readonly TaichiClothVR   _taichiCloth;

    // Seam accumulation
    private readonly HashSet<int> _accumulatedCutTris = new HashSet<int>();

    // Kết quả split gần nhất
    private readonly List<GameObject> _lastCreatedPieces = new List<GameObject>();

    private const int MIN_TRIS_FOR_PIECE = 4;

    // ── constructor ───────────────────────────────────────────────────────────
    public MeshCutter_Taichi(GameObject target, float splitForce)
    {
        _target      = target;
        _tf          = target.transform;
        _mf          = target.GetComponent<MeshFilter>();
        _splitForce  = splitForce;
        _taichiCloth = target.GetComponent<TaichiClothVR>();

        Debug.Log($"[MeshCutter_Taichi] Created for '{target.name}'" +
                  (_taichiCloth != null ? " (TaichiClothVR detected)" : " (no TaichiClothVR — fallback)"));
    }

    /// <summary>Gọi một lần sau khi TaichiClothVR đã init xong mesh.</summary>
    public void Initialize()
    {
        Debug.Log($"[MeshCutter_Taichi] '{_target.name}' initialized (Seam-Split mode).");
    }

    /// <summary>Trả về danh sách piece vừa được spawn sau lần Split gần nhất.</summary>
    public List<GameObject> GetLastCreatedPieces() => new List<GameObject>(_lastCreatedPieces);

    // =========================================================================
    //  ENTRY POINT
    // =========================================================================

    public CutResult_Taichi PerformCut(Collider cutter, bool splitOnlyWhenDisconnected)
    {
        if (_mf == null) return CutResult_Taichi.None;

        Mesh      mesh    = _mf.mesh;
        Vector3[] verts   = mesh.vertices;   // local-space
        int[]     tris    = mesh.triangles;
        Vector2[] uv      = mesh.uv;
        Vector3[] normals = mesh.normals;

        if (verts == null || verts.Length == 0 || tris == null || tris.Length == 0)
            return CutResult_Taichi.None;

        bool hasUV  = uv      != null && uv.Length      == verts.Length;
        bool hasNrm = normals != null && normals.Length == verts.Length;
        int  triCount = tris.Length / 3;

        // Với Taichi: vertex đã ở world space? Không — TaichiClothVR gán trực tiếp
        // vào clothMesh.vertices theo tọa độ local của GameObject.
        // Chúng ta TransformPoint để ra world-space cho test va chạm.
        Vector3[] worldVerts = GetWorldSpaceVertices(verts);

        // ── Bước 1: Thu thập triangle bị cutter chạm vào frame này ──────────
        int newThisFrame = 0;
        for (int t = 0; t < triCount; t++)
        {
            if (_accumulatedCutTris.Contains(t)) continue;

            int iA = tris[t * 3], iB = tris[t * 3 + 1], iC = tris[t * 3 + 2];
            if (TriangleIntersectsCollider(worldVerts[iA], worldVerts[iB], worldVerts[iC], cutter))
            {
                _accumulatedCutTris.Add(t);
                newThisFrame++;
            }
        }

        if (newThisFrame == 0 && _accumulatedCutTris.Count == 0)
            return CutResult_Taichi.None;

        // ── Bước 2: Flood-fill dùng cutTris làm tường ───────────────────────
        bool[] cutWall = new bool[triCount];
        foreach (int t in _accumulatedCutTris) cutWall[t] = true;

        var triAdj     = BuildTriangleAdjacency(tris, triCount);
        var components = FindConnectedComponentsExcluding(triCount, cutWall, triAdj);

        Debug.Log($"[MeshCutter_Taichi] '{_target.name}' accum={_accumulatedCutTris.Count}/{triCount}" +
                  $" | components={components.Count}");

        if (components.Count == 0) return CutResult_Taichi.None;

        // ── Bước 3: Quyết định split hay chờ thêm ───────────────────────────
        bool doSplit = !splitOnlyWhenDisconnected || components.Count >= 2;
        if (!doSplit)
            return newThisFrame > 0 ? CutResult_Taichi.Trimmed : CutResult_Taichi.None;

        // ── Bước 4: Tắt TaichiClothVR gốc rồi spawn các piece ───────────────
        if (_taichiCloth != null) _taichiCloth.enabled = false;

        _lastCreatedPieces.Clear();
        Material[] mats       = _target.GetComponent<MeshRenderer>().sharedMaterials;
        Vector3    meshCenter = ComputeCenter(worldVerts);

        for (int c = 0; c < components.Count; c++)
        {
            var    compTris  = components[c];
            Mesh   pieceMesh = BuildMeshFromTris(compTris, tris, verts, normals, uv, hasNrm, hasUV);
            if (pieceMesh == null) continue;

            Vector3 pieceCenter = ComputeCenterFromTriangles(compTris, tris, worldVerts);
            Vector3 impulseDir  = (pieceCenter - meshCenter).normalized;
            if (impulseDir.sqrMagnitude < 0.01f) impulseDir = URandom.onUnitSphere;

            var piece = CreatePiece($"{_target.name}_Piece{c}", pieceMesh, mats, impulseDir);
            if (piece != null) _lastCreatedPieces.Add(piece);
        }

        return CutResult_Taichi.Split;
    }

    // =========================================================================
    //  XÂY DỰNG MESH CON từ danh sách triangle của một component
    // =========================================================================

    private Mesh BuildMeshFromTris(List<int> triIdx, int[] tris,
        Vector3[] verts, Vector3[] normals, Vector2[] uv, bool hasNrm, bool hasUV)
    {
        if (triIdx == null || triIdx.Count == 0) return null;

        var remap = new Dictionary<int, int>();
        var nV    = new List<Vector3>();
        var nN    = new List<Vector3>();
        var nUV   = new List<Vector2>();
        var nTris = new List<int>();

        foreach (int t in triIdx)
        {
            for (int k = 0; k < 3; k++)
            {
                int old = tris[t * 3 + k];
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
    //  KIỂM TRA TRIANGLE - COLLIDER (không dùng Physics API)
    // =========================================================================

    private static bool TriangleIntersectsCollider(Vector3 wA, Vector3 wB, Vector3 wC, Collider col)
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
            if (PointInBox((lA + lB + lC) / 3f, ext)) return true;
            return false;
        }

        if (col is SphereCollider sc)
        {
            Vector3 wCenter = sc.transform.TransformPoint(sc.center);
            float   wRadius = sc.radius * Mathf.Max(
                Mathf.Abs(sc.transform.lossyScale.x),
                Mathf.Abs(sc.transform.lossyScale.y),
                Mathf.Abs(sc.transform.lossyScale.z));
            return PointToTriangleSqDist(wCenter, wA, wB, wC) <= wRadius * wRadius;
        }

        if (col is CapsuleCollider cc)
        {
            // Lấy 2 đầu mút của capsule ở world space
            Vector3 capsuleAxis = cc.transform.up;
            if (cc.direction == 0) capsuleAxis = cc.transform.right;
            else if (cc.direction == 2) capsuleAxis = cc.transform.forward;
            float   halfH   = Mathf.Max(0f, cc.height * 0.5f - cc.radius);
            Vector3 wCtr    = cc.transform.TransformPoint(cc.center);
            float   wRadius = cc.radius * Mathf.Max(
                Mathf.Abs(cc.transform.lossyScale.x),
                Mathf.Abs(cc.transform.lossyScale.y),
                Mathf.Abs(cc.transform.lossyScale.z));
            Vector3 p0 = wCtr - capsuleAxis * halfH;
            Vector3 p1 = wCtr + capsuleAxis * halfH;
            // Kiểm tra khoảng cách từ segment capsule đến từng cạnh triangle
            return SegSegDistSq(p0, p1, wA, wB) <= wRadius * wRadius ||
                   SegSegDistSq(p0, p1, wB, wC) <= wRadius * wRadius ||
                   SegSegDistSq(p0, p1, wC, wA) <= wRadius * wRadius ||
                   PointToTriangleSqDist(wCtr, wA, wB, wC) <= wRadius * wRadius;
        }

        // Fallback
        float tol = 0.002f;
        if (Vector3.SqrMagnitude(col.ClosestPoint(wA) - wA) < tol * tol) return true;
        if (Vector3.SqrMagnitude(col.ClosestPoint(wB) - wB) < tol * tol) return true;
        if (Vector3.SqrMagnitude(col.ClosestPoint(wC) - wC) < tol * tol) return true;
        return false;
    }

    // ── Geometry helpers ──────────────────────────────────────────────────────

    private static bool PointInBox(Vector3 p, Vector3 ext)
        => Mathf.Abs(p.x) <= ext.x && Mathf.Abs(p.y) <= ext.y && Mathf.Abs(p.z) <= ext.z;

    private static bool SegmentIntersectsAABB(Vector3 a, Vector3 b, Vector3 ext)
    {
        Vector3 d    = b - a;
        float   tMin = 0f, tMax = 1f;
        for (int i = 0; i < 3; i++)
        {
            float ai = i == 0 ? a.x : (i == 1 ? a.y : a.z);
            float di = i == 0 ? d.x : (i == 1 ? d.y : d.z);
            float ei = i == 0 ? ext.x : (i == 1 ? ext.y : ext.z);
            if (Mathf.Abs(di) < 1e-8f)
            { if (ai < -ei || ai > ei) return false; }
            else
            {
                float t1 = (-ei - ai) / di, t2 = (ei - ai) / di;
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
        if (d1 <= 0 && d2 <= 0) return (p-a).sqrMagnitude;
        Vector3 bp = p-b;
        float d3 = Vector3.Dot(ab,bp), d4 = Vector3.Dot(ac,bp);
        if (d3 >= 0 && d4 <= d3) return (p-b).sqrMagnitude;
        Vector3 cp = p-c;
        float d5 = Vector3.Dot(ab,cp), d6 = Vector3.Dot(ac,cp);
        if (d6 >= 0 && d5 <= d6) return (p-c).sqrMagnitude;
        float vc = d1*d4 - d3*d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        { float v = d1/(d1-d3); return (p-(a+v*ab)).sqrMagnitude; }
        float vb = d5*d2 - d1*d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        { float w = d2/(d2-d6); return (p-(a+w*ac)).sqrMagnitude; }
        float va = d3*d6 - d5*d4;
        if (va <= 0 && (d4-d3) >= 0 && (d5-d6) >= 0)
        { float w2 = (d4-d3)/((d4-d3)+(d5-d6)); return (p-(b+w2*(c-b))).sqrMagnitude; }
        float den = 1f/(vc+vb+va);
        float vv = vb*den, ww = vc*den;
        return (p-(a + ab*vv + ac*ww)).sqrMagnitude;
    }

    /// <summary>Khoảng cách bình phương giữa 2 đoạn thẳng (dùng cho capsule test).</summary>
    private static float SegSegDistSq(Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4)
    {
        Vector3 d1 = p2 - p1, d2 = p4 - p3, r = p1 - p3;
        float a  = Vector3.Dot(d1,d1);
        float e  = Vector3.Dot(d2,d2);
        float f  = Vector3.Dot(d2,r);
        float s, t;
        if (a <= 1e-8f && e <= 1e-8f) return Vector3.Dot(r,r);
        if (a <= 1e-8f) { s = 0; t = Mathf.Clamp01(f/e); }
        else
        {
            float c = Vector3.Dot(d1,r);
            if (e <= 1e-8f) { t = 0; s = Mathf.Clamp01(-c/a); }
            else
            {
                float b   = Vector3.Dot(d1,d2);
                float den = a*e - b*b;
                s = den != 0 ? Mathf.Clamp01((b*f - c*e) / den) : 0;
                t = (b*s + f) / e;
                if (t < 0) { t = 0; s = Mathf.Clamp01(-c/a); }
                else if (t > 1) { t = 1; s = Mathf.Clamp01((b-c)/a); }
            }
        }
        Vector3 closest = p1 + d1*s - (p3 + d2*t);
        return Vector3.Dot(closest, closest);
    }

    // =========================================================================
    //  GRAPH HELPERS
    // =========================================================================

    private static List<List<int>> BuildTriangleAdjacency(int[] tris, int triCount)
    {
        var edgeMap = new Dictionary<(int,int), List<int>>();
        void AddEdge(int a, int b, int t)
        {
            var key = a < b ? (a,b) : (b,a);
            if (!edgeMap.ContainsKey(key)) edgeMap[key] = new List<int>();
            edgeMap[key].Add(t);
        }
        for (int t = 0; t < triCount; t++)
        {
            int iA = tris[t*3], iB = tris[t*3+1], iC = tris[t*3+2];
            AddEdge(iA, iB, t); AddEdge(iB, iC, t); AddEdge(iC, iA, t);
        }
        var adj = new List<List<int>>(triCount);
        for (int i = 0; i < triCount; i++) adj.Add(new List<int>());
        foreach (var kv in edgeMap)
        {
            var list = kv.Value;
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                { adj[list[i]].Add(list[j]); adj[list[j]].Add(list[i]); }
        }
        return adj;
    }

    private static List<List<int>> FindConnectedComponentsExcluding(
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
                    { visited[nb] = true; queue.Enqueue(nb); }
            }
            result.Add(comp);
        }
        return result;
    }

    // =========================================================================
    //  WORLD SPACE VERTICES
    //  Với Taichi cloth: clothMesh.vertices là local-space (TaichiClothVR đặt
    //  các vertex bằng tọa độ Taichi local rồi gán vào clothMesh).
    //  → Cần TransformPoint để ra world space.
    // =========================================================================

    private Vector3[] GetWorldSpaceVertices(Vector3[] localVerts)
    {
        int n = localVerts.Length;
        var result = new Vector3[n];
        for (int i = 0; i < n; i++)
            result[i] = _tf.TransformPoint(localVerts[i]);
        return result;
    }

    // =========================================================================
    //  TẠO PIECE — copy TaichiClothVR + tag + MeshCollider
    // =========================================================================

    private GameObject CreatePiece(string name, Mesh mesh, Material[] mats, Vector3 impulseDir)
    {
        int tc = mesh.triangles.Length / 3;
        if (tc < MIN_TRIS_FOR_PIECE)
        {
            Debug.Log($"[MeshCutter_Taichi] Skip '{name}' ({tc} tris quá nhỏ).");
            return null;
        }

        var piece = new GameObject(name);
        piece.transform.SetPositionAndRotation(_tf.position, _tf.rotation);
        piece.transform.localScale = _tf.lossyScale;

        // Giữ tag giống original để CuttingManager_Taichi tự tìm và đăng ký
        piece.tag = _target.tag;

        piece.AddComponent<MeshFilter>().mesh = mesh;
        piece.AddComponent<MeshRenderer>().sharedMaterials = mats;

        var mc = piece.AddComponent<MeshCollider>();
        mc.sharedMesh = mesh;

        if (_taichiCloth != null)
        {
            // ── Tính lại width × height cho piece dựa trên số vertex ──────────
            // Piece sẽ có topology nhỏ hơn; ta truyền NW, NH của original để
            // Python server tự điều chỉnh (xem cloth_taichi_piece_server.py).
            // Trong Unity, TaichiClothVR của piece nhận đúng width * height vertex
            // từ Python sub-server tương ứng.
            int pieceVertCount = mesh.vertexCount;

            // Tìm cạnh grid gần nhất cho piece (giữ tỷ lệ nguyên bản)
            int origW = _taichiCloth.width;
            int origH = _taichiCloth.height;
            (int pw, int ph) = EstimatePieceDimensions(pieceVertCount, origW, origH);

            var newCloth = piece.AddComponent<TaichiClothVR>();

            // Copy cấu hình mạng — nhưng dùng port mới (tự động tăng)
            newCloth.serverIP    = _taichiCloth.serverIP;
            newCloth.receivePort = TaichiPortAllocator.NextReceivePort();
            newCloth.sendPort    = TaichiPortAllocator.NextSendPort();
            newCloth.width       = pw;
            newCloth.height      = ph;

            // Copy VR interaction references
            newCloth.vrController  = _taichiCloth.vrController;
            newCloth.grabSphere    = _taichiCloth.grabSphere;
            newCloth.triggerAction = _taichiCloth.triggerAction;

            // Truyền mesh hiện tại vào để piece khởi động đúng topology
            newCloth.InitializeWithMesh(mesh);

            // Thêm TaichiPieceSpawner để thông báo cho Python tạo sub-server
            var spawner = piece.AddComponent<TaichiPieceSpawner>();
            spawner.pythonServerIP  = _taichiCloth.serverIP;
            spawner.spawnCommandPort = 5099;  // PORT_SPAWN_CMD khớp với cloth_taichi_server.py

            Debug.Log($"[MeshCutter_Taichi] Piece '{name}': TaichiClothVR " +
                      $"recv:{newCloth.receivePort} send:{newCloth.sendPort} " +
                      $"grid:{pw}x{ph} verts:{pieceVertCount}");
        }
        else
        {
            // Fallback (không có Taichi): thêm Rigidbody văng ra
            var rb = piece.AddComponent<Rigidbody>();
            rb.AddForce(impulseDir * _splitForce + Vector3.up * 0.3f, ForceMode.Impulse);
            rb.AddTorque(URandom.insideUnitSphere * _splitForce * 0.5f, ForceMode.Impulse);
        }

        return piece;
    }

    /// <summary>
    /// Ước tính chiều rộng/chiều cao grid cho piece.
    /// Tìm factorization (w, h) gần tỷ lệ origW:origH nhất với w*h <= vertCount.
    /// </summary>
    private static (int w, int h) EstimatePieceDimensions(int vertCount, int origW, int origH)
    {
        if (vertCount <= 0) return (2, 2);
        float ratio = (float)origW / origH;
        int bestW = 2, bestH = 2;
        float bestErr = float.MaxValue;
        for (int w = 2; w <= origW; w++)
        {
            int h = Mathf.RoundToInt(w / ratio);
            h = Mathf.Clamp(h, 2, origH);
            if (w * h > vertCount) continue;
            float err = Mathf.Abs((float)w / h - ratio);
            if (err < bestErr) { bestErr = err; bestW = w; bestH = h; }
        }
        return (bestW, bestH);
    }

    // ── Center helpers ────────────────────────────────────────────────────────

    private static Vector3 ComputeCenter(Vector3[] w)
    {
        var s = Vector3.zero;
        foreach (var v in w) s += v;
        return s / Mathf.Max(1, w.Length);
    }

    private static Vector3 ComputeCenterFromTriangles(List<int> triIdx, int[] tris, Vector3[] w)
    {
        var s = Vector3.zero; int n = 0;
        foreach (int t in triIdx)
        { s += w[tris[t*3]]; s += w[tris[t*3+1]]; s += w[tris[t*3+2]]; n += 3; }
        return n > 0 ? s / n : Vector3.zero;
    }

    public void DrawDebugGizmos() { }
}

public enum CutResult_Taichi { None, Trimmed, Split }
