// ============================================================
//  SewingManager_UCloth.cs  — v6.5 (Bổ sung Tự động Finalize không cần nút)
// ============================================================
using System.Collections.Generic;
using UnityEngine;
using System.Linq;

public class SewingManager_UCloth : MonoBehaviour
{
    [Header("References")]
    public GameObject sewer;

    // FIX Bug khâu-xong-không-cắt-được:
    // SewingManager phải biết CuttingManager để đăng ký mesh mới khâu vào
    // hệ thống cắt. Kéo CuttingManager_UCloth từ Scene vào đây trong Inspector.
    [Tooltip("Kéo CuttingManager_UCloth từ Scene vào đây để mesh sau khi khâu có thể cắt tiếp được.")]
    public CuttingManager_UCloth cuttingManager;

    [Header("Ray Settings")]
    public float rayLength    = 3f;
    public float rayRadius    = 0.05f;
    public bool  showDebugRay = true;

    [Header("Sewing Settings")]
    public string clothTag     = "Cloth";
    public float weldThreshold = 0.008f;
    public float sewRadius     = 0.1f;
    public int   edgesPerFrame = 3;
    public bool  immediateWeld = false;

    [Header("Self-Sew Settings")]
    public bool  useSecondaryRayForSelfSew = true;
    public float selfSewSearchRadius       = 0.3f;

    [Header("Auto-Finalize Settings")]
    [Tooltip("Tự động hoàn tất và tạo mesh vải sau N giây nếu người dùng không khâu thêm gì.")]
    public bool  useAutoFinalize   = true;
    public float autoFinalizeDelay = 1.5f;

    // ── Private ───────────────────────────────────────────────────────────
    private MeshSewer_UCloth _sewer;
    private GameObject _objA, _objB;
    private bool _isSelfSew;

    private enum SessionState { Idle, Sewing, WaitingForNextSeam, Finalizing }
    private SessionState _state = SessionState.Idle;

    private Vector3 _lastAddedHitA = Vector3.positiveInfinity;
    private Vector3 _lastAddedHitB = Vector3.positiveInfinity;

    // Bộ đếm thời gian không tương tác để tự động Finalize
    private float _noInteractionTimer = 0f;

    // Gizmo
    private Vector3 _gizmoHitA, _gizmoHitB;
    private bool    _gizmoHasHit;
    

    // ── Public API ────────────────────────────────────────────────────────
    public void FinalizeSession()
    {
        if (_sewer == null || _state == SessionState.Idle)
        {
            Debug.LogWarning("[SewingManager] Không có session nào để Finalize.");
            return;
        }

        _state = SessionState.Finalizing;
        Debug.Log("[SewingManager] FinalizeSession() được kích hoạt.");
    }

    public void CancelSession()
    {
        if (_state == SessionState.Idle) return;
        _sewer?.ClearPreviewVisuals(); // Dọn dẹp đồ họa preview sạch sẽ
        _sewer = null;
        _objA = _objB = null;
        _state = SessionState.Idle;
        _gizmoHasHit = false;
        _noInteractionTimer = 0f;
        Debug.Log("[SewingManager] Session bị hủy.");
    }
void Start()
{
    var clothObjects = GameObject.FindGameObjectsWithTag(clothTag);
    foreach (var obj in clothObjects)
    {
        RegisterClothObject(obj);
    }
}
    // ── LateUpdate ────────────────────────────────────────────────────────
    void LateUpdate()
    {
        if (sewer == null) return;

        // ── 1. Khối xử lý Finalize ────────────────────────────────────────
        if (_state == SessionState.Finalizing)
        {
            CommitSewn();
            return;
        }

        // ── 2. Khối tiến trình khâu (Progressive Sewing & Animation) ────────
        if (_state == SessionState.Sewing && _sewer != null)
        {
            bool done = _sewer.Sew();
            _sewer.UpdateLiveVisuals();

            if (done)
            {
                _state = SessionState.WaitingForNextSeam;
                _noInteractionTimer = 0f; // Reset bộ đếm khi vừa kết thúc 1 cụm đỉnh
                Debug.Log("[SewingManager] Đường khâu tạm thời hoàn tất. Chờ hành động tiếp theo...");
            }
            return; 
        }

        // ── 3. Cơ chế tự động Finalize khi người dùng đứng yên / ngừng khâu ──
        if (useAutoFinalize && _state == SessionState.WaitingForNextSeam && _sewer != null)
        {
            _noInteractionTimer += Time.deltaTime;
            if (_noInteractionTimer >= autoFinalizeDelay)
            {
                Debug.Log($"<color=green>[SewingManager]</color> Không có tương tác mới sau {autoFinalizeDelay}s. Tự động Finalize!");
                _state = SessionState.Finalizing;
                return;
            }
        }

        // ── 4. Bắn Raycast tìm dữ liệu hình học bề mặt vải ─────────────────
        Ray sewRay = new Ray(sewer.transform.position, sewer.transform.forward);
        var hits = Physics.RaycastAll(sewRay, rayLength).OrderBy(h => h.distance).ToList();

        var clothHits = new List<(GameObject go, Vector3 point)>();
        var seen      = new HashSet<GameObject>();

        foreach (var h in hits)
        {
            var go = h.collider.gameObject;
            if (!go.CompareTag(clothTag)) continue;
            if (go.GetComponent<UCloth.UCCloth>() == null) continue;
            if (seen.Contains(go)) continue;
            clothHits.Add((go, h.point));
            seen.Add(go);
            if (clothHits.Count == 2) break;
        }

        if (clothHits.Count == 0) 
        { 
            _gizmoHasHit = false; 
            return; 
        }

        // ── 5. Phân tách tọa độ điểm chạm A và B ───────────────────────────
        GameObject goA, goB;
        Vector3    hitA, hitB;
        bool       isSelf;

        if (clothHits.Count >= 2 && clothHits[0].go != clothHits[1].go)
        {
            (goA, hitA) = clothHits[0];
            (goB, hitB) = clothHits[1];
            isSelf = false;
        }
        else
        {
            (goA, hitA) = clothHits[0];
            goB  = goA;
            isSelf = true;
            hitB = FindSelfSewSecondaryHit(goA, hitA);
        }

        _gizmoHitA   = hitA;
        _gizmoHitB   = hitB;
        _gizmoHasHit = true;

        // ── 6. Xử lý trạng thái WAITING: Nối tiếp đường khâu trên cùng Mesh ──
        if (_state == SessionState.WaitingForNextSeam)
        {
            bool targetMatchesSession = (goA == _objA || goA == _objB) || (goB == _objA || goB == _objB);
            if (!targetMatchesSession) return;

            if (goA == goB || clothHits.Count < 2)
            {
                hitB = FindSelfSewSecondaryHit(goA, hitA);
                _isSelfSew = true;
            }

            // Kiểm tra Debounce chống spam trùng vị trí
            float moveA = Vector3.Distance(hitA, _lastAddedHitA);
            float moveB = Vector3.Distance(hitB, _lastAddedHitB);
            
            // Nếu di chuyển tâm khâu vượt quá khoảng cách tối thiểu, ta coi như có tương tác mới
            if (moveA < sewRadius * 0.3f && moveB < sewRadius * 0.3f) 
            {
                return; 
            }

            // Có di chuyển tâm khâu -> Reset bộ đếm thời gian tự động
            _noInteractionTimer = 0f;

            Debug.Log($"[SewingManager] Tiếp tục thêm đường khâu: hitA={hitA:F3} <-> hitB={hitB:F3}");

            bool added = _sewer.AddSeam(hitA, hitB, sewRadius);
            if (!added) return;

            _lastAddedHitA = hitA;
            _lastAddedHitB = hitB;
            _state = SessionState.Sewing;

            if (immediateWeld) 
            { 
                while (!_sewer.Sew()) { } 
                _sewer.UpdateLiveVisuals();
                _state = SessionState.WaitingForNextSeam;
                _noInteractionTimer = 0f;
            }
            return; 
        }

        // ── 7. Trạng thái IDLE: Khởi tạo Session khâu hoàn toàn mới ──────────
        if (_state == SessionState.Idle)
        {
            _objA      = goA;
            _objB      = goB;
            _isSelfSew = isSelf;

            _sewer = new MeshSewer_UCloth(goA, goB, weldThreshold, immediateWeld ? int.MaxValue : edgesPerFrame);
            _sewer.OnSeamCompleted = RegisterSewnMesh;

            if (isSelf)
                Debug.Log($"<color=cyan>[SewingManager]</color> Khởi tạo tự khâu: {goA.name}");
            else
                Debug.Log($"<color=yellow>[SewingManager]</color> Khởi tạo khâu gộp: {goA.name} <-> {goB.name}");

            if (!_sewer.Initialize(hitA, hitB, sewRadius))
            {
                _sewer = null;
                return;
            }

            _lastAddedHitA = hitA;
            _lastAddedHitB = hitB;
            _state = SessionState.Sewing;
            _noInteractionTimer = 0f;

            if (immediateWeld) 
            { 
                while (!_sewer.Sew()) { } 
                _sewer.UpdateLiveVisuals();
                _state = SessionState.WaitingForNextSeam;
                _noInteractionTimer = 0f;
            }
        }
    }

    private void CommitSewn()
    {
        if (_sewer == null) return;

        if (_state == SessionState.Sewing)
            while (!_sewer.Sew()) { }

        string name = _isSelfSew
            ? $"{_objA.name}_SelfSewn"
            : $"{_objA.name}_{_objB.name}_Sewn";

        _sewer.Finalize(name);

        _sewer       = null;
        _objA = _objB = null;
        _state       = SessionState.Idle;
        _gizmoHasHit = false;
        _noInteractionTimer = 0f;
        _lastAddedHitA = Vector3.positiveInfinity;
        _lastAddedHitB = Vector3.positiveInfinity;

        Debug.Log("[SewingManager] Session hoàn tất.");
    }

    private Vector3 FindSelfSewSecondaryHit(GameObject go, Vector3 hitA)
    {
        if (!useSecondaryRayForSelfSew) return hitA;

        var mf = go.GetComponent<MeshFilter>();
        if (mf == null) return hitA;

        Mesh      mesh  = mf.mesh;
        Vector3[] verts = mesh.vertices;
        int[]     tris  = mesh.triangles;
        Transform tf    = go.transform;

        var edgeCount = new Dictionary<(int, int), int>();
        for (int t = 0; t < tris.Length; t += 3)
        {
            int a = tris[t], b = tris[t+1], c = tris[t+2];
            AddEdge(edgeCount, a, b); AddEdge(edgeCount, b, c); AddEdge(edgeCount, a, c);
        }

        var boundarySet = new HashSet<int>();
        foreach (var kv in edgeCount)
            if (kv.Value == 1) { boundarySet.Add(kv.Key.Item1); boundarySet.Add(kv.Key.Item2); }

        if (boundarySet.Count == 0) return hitA;

        // BẢO ĐẢM ĐỒNG BỘ TOẠ ĐỘ: Ép chuyển chính xác từ Local sang World Space dựa trên transform thực thể vải
        var bWorldPos = new Dictionary<int, Vector3>();
        foreach (int v in boundarySet)
        {
            if (v >= 0 && v < verts.Length)
            {
                bWorldPos[v] = tf.TransformPoint(verts[v]);
            }
        }

        if (bWorldPos.Count == 0) return hitA;

        int   nearestToA  = -1;
        float nearestDist = float.MaxValue;
        foreach (var kv in bWorldPos)
        {
            float d = Vector3.Distance(kv.Value, hitA);
            if (d < nearestDist) { nearestDist = d; nearestToA = kv.Key; }
        }
        if (nearestToA < 0) return hitA;

        var adjBoundary = BuildBoundaryAdjacency(edgeCount, boundarySet);
        var compA       = FloodFill(nearestToA, adjBoundary);

        int   bestB    = -1;
        float bestDist = float.MaxValue;

        foreach (var kv in bWorldPos)
        {
            if (compA.Contains(kv.Key)) continue;
            float d = Vector3.Distance(kv.Value, hitA);
            if (d < bestDist) { bestDist = d; bestB = kv.Key; }
        }

        if (bestB < 0)
        {
            float antiWeldRadius = sewRadius * 1.5f;
            bestDist = float.MaxValue;

            foreach (var kv in bWorldPos)
            {
                float d = Vector3.Distance(kv.Value, hitA);
                if (d > antiWeldRadius && d < bestDist) { bestDist = d; bestB = kv.Key; }
            }
        }

        if (bestB < 0 || !bWorldPos.ContainsKey(bestB)) return hitA;

        return bWorldPos[bestB];
    }
// Thêm vào trong class SewingManager_UCloth
public void RegisterClothObject(GameObject obj)
{
    if (obj == null) return;
    
    // Đảm bảo object có tag chuẩn để Raycast tìm kiếm bề mặt vải nhận diện đúng
    if (!obj.CompareTag(clothTag))
    {
        obj.tag = clothTag;
    }

    Debug.Log($"[SewingManager] Đã tái đăng ký thành công mảnh vải mới '{obj.name}' làm mục tiêu khâu tiếp theo.");
}
    private void RegisterSewnMesh(GameObject sewn)
    {
        if (sewn == null) return;

        // Đăng ký để SewingManager có thể khâu tiếp
        RegisterClothObject(sewn);

        // Tự tìm CuttingManager trong scene nếu chưa gán trong Inspector
        if (cuttingManager == null)
        {
            cuttingManager = FindObjectOfType<CuttingManager_UCloth>();
            if (cuttingManager != null)
                Debug.LogWarning("[SewingManager] cuttingManager chưa được gán trong Inspector — tự tìm trong scene. " +
                                 "Hãy gán thủ công để tránh chi phí FindObjectOfType mỗi lần khâu.");
        }

        if (cuttingManager != null)
        {
            cuttingManager.RegisterClothObject(sewn);
            Debug.Log($"[SewingManager] '{sewn.name}' đã được đăng ký vào CuttingManager — có thể cắt tiếp.");
        }
        else
        {
            Debug.LogError("[SewingManager] Không tìm thấy CuttingManager_UCloth trong scene! " +
                           "Mesh vừa khâu sẽ KHÔNG thể cắt được.");
        }

        // Cấu hình tương tác VR
        if (VRContext.Instance != null)
        {
            var grabber = sewn.GetComponent<UClothLaserGrabber>();
            if (grabber != null)
            {
                grabber.vrController = VRContext.Instance.leftHandController;
                grabber.grabSphere   = VRContext.Instance.grabSphereTarget;
            }
            // KHÔNG ghi đè cubeColliders sau Awake() — UCCloth đã init với array từ Finalize().
            // environmentColliders đã được MeshSewer.Finalize() merge vào khi copy từ _ucA.
        }
        else
        {
            Debug.LogWarning("[SewingManager] VRContext.Instance == null!");
        }
    }

    private static void AddEdge(Dictionary<(int,int), int> dict, int a, int b)
    {
        var key = a < b ? (a, b) : (b, a);
        dict.TryGetValue(key, out int c); dict[key] = c + 1;
    }

    private static Dictionary<int, List<int>> BuildBoundaryAdjacency(Dictionary<(int,int), int> edgeCount, HashSet<int> boundarySet)
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

    private static T[] MergeArrays<T>(T[] a, T[] b)
    {
        if (a == null || a.Length == 0) return b ?? new T[0];
        if (b == null || b.Length == 0) return a;
        var result = new T[a.Length + b.Length];
        a.CopyTo(result, 0); b.CopyTo(result, a.Length);
        return result;
    }

    // ── Gizmos ────────────────────────────────────────────────────────────
    void OnDrawGizmos()
    {
        if (sewer == null || !showDebugRay) return;

        var ray = new Ray(sewer.transform.position, sewer.transform.forward);
        Gizmos.color = _state == SessionState.Sewing
            ? new Color(0.2f, 1f, 0.3f, 0.9f)
            : _state == SessionState.WaitingForNextSeam
                ? new Color(1f, 1f, 0f, 0.9f)
                : new Color(0.2f, 0.8f, 1f, 0.9f);
        Gizmos.DrawRay(ray.origin, ray.direction * rayLength);

        if (_gizmoHasHit)
        {
            Gizmos.color = new Color(1f, 0.6f, 0f, 0.9f);
            Gizmos.DrawSphere(_gizmoHitA, 0.012f);
            Gizmos.color = new Color(1f, 0.6f, 0f, 0.15f);
            Gizmos.DrawSphere(_gizmoHitA, sewRadius);

            if (_isSelfSew)
            {
                Gizmos.color = new Color(0f, 1f, 1f, 0.9f);
                Gizmos.DrawSphere(_gizmoHitB, 0.012f);
                Gizmos.color = new Color(0f, 1f, 1f, 0.15f);
                Gizmos.DrawSphere(_gizmoHitB, sewRadius);
                Gizmos.color = new Color(1f, 1f, 0f, 0.5f);
                Gizmos.DrawLine(_gizmoHitA, _gizmoHitB);
            }
            else
            {
                Gizmos.color = new Color(1f, 0.6f, 0f, 0.9f);
                Gizmos.DrawSphere(_gizmoHitB, 0.012f);
                Gizmos.color = new Color(1f, 0.6f, 0f, 0.15f);
                Gizmos.DrawSphere(_gizmoHitB, sewRadius);
            }
        }
    }
}