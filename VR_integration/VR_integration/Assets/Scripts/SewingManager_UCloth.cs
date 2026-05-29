// ============================================================
//  SewingManager_UCloth.cs  — v6.7
//
//  [FIX-v9-4] Guard hitA≈hitB cho self-sew (ống tay áo).
//          Vấn đề cũ: khi FindSelfSewSecondaryHit không tìm được đỉnh đối diện
//          (loop quá nhỏ hoặc searchRadius quá hẹp), nó trả về hitA. SewingManager
//          vẫn gọi Initialize/AddSeam với seedA==seedB → candA và candB query cùng
//          một vùng → overlap hoàn toàn → tự collapse đỉnh kề nhau → topology vỡ.
//          Fix: nếu Distance(hitA, hitB) < sewRadius * 0.3f sau khi gọi
//          FindSelfSewSecondaryHit → bỏ qua frame đó, không khởi tạo session.
//
//  Giữ nguyên từ v6.6:
//  [FIX BUG-4] WaitingForNextSeam kiểm tra HasOpenBoundary trước AddSeam.
//  [FIX BUG-3] FindSelfSewSecondaryHit delegate sang MeshSewer_UCloth (static).
// ============================================================
using System.Collections.Generic;
using UnityEngine;
using System.Linq;

public class SewingManager_UCloth : MonoBehaviour
{
    [Header("References")]
    public GameObject sewer;

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
    [Tooltip("Tự động hoàn tất sau N giây nếu người dùng không khâu thêm gì.")]
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
        _sewer?.ClearPreviewVisuals();
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
            RegisterClothObject(obj);
    }

    // ── LateUpdate ────────────────────────────────────────────────────────
    void LateUpdate()
    {
        if (sewer == null) return;

        // ── 1. Finalize ───────────────────────────────────────────────────
        if (_state == SessionState.Finalizing)
        {
            CommitSewn();
            return;
        }

        // ── 2. Tiến trình khâu progressive ───────────────────────────────
        if (_state == SessionState.Sewing && _sewer != null)
        {
            bool done = _sewer.Sew();
            _sewer.UpdateLiveVisuals();

            if (done)
            {
                // [FIX BUG-4] Nếu mesh đã kín sau batch này → Finalize ngay
                if (!_sewer.HasOpenBoundary)
                {
                    Debug.Log("<color=green>[SewingManager]</color> Mesh đã kín hoàn toàn sau khâu. Tự động Finalize.");
                    _state = SessionState.Finalizing;
                    return;
                }

                _state = SessionState.WaitingForNextSeam;
                _noInteractionTimer = 0f;
                Debug.Log("[SewingManager] Đường khâu tạm thời hoàn tất. Chờ hành động tiếp theo...");
            }
            return;
        }

        // ── 3. Auto-Finalize khi đứng yên ────────────────────────────────
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

        // ── 4. Raycast ────────────────────────────────────────────────────
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

        if (clothHits.Count == 0) { _gizmoHasHit = false; return; }

        // ── 5. Phân tách hitA / hitB ──────────────────────────────────────
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

            // [FIX-v9-4] Guard: hitB vẫn quá gần hitA → FindSelfSewSecondaryHit
            // không tìm được đỉnh đối diện (thường xảy ra khi searchRadius nhỏ hơn
            // kích thước loop hoặc mesh đã gần kín). Bỏ qua frame này để tránh
            // Initialize(seedA==seedB) → candA∩candB overlap → tự collapse topology.
            if (Vector3.Distance(hitA, hitB) < sewRadius * 0.3f)
            {
                _gizmoHasHit = false;
                return;
            }
        }

        _gizmoHitA   = hitA;
        _gizmoHitB   = hitB;
        _gizmoHasHit = true;

        // ── 6. WaitingForNextSeam: Tiếp tục khâu ─────────────────────────
        if (_state == SessionState.WaitingForNextSeam)
        {
            bool targetMatchesSession = (goA == _objA || goA == _objB) || (goB == _objA || goB == _objB);
            if (!targetMatchesSession) return;

            // [FIX BUG-4] Kiểm tra mesh còn boundary trước khi AddSeam
            if (!_sewer.HasOpenBoundary)
            {
                Debug.Log("<color=green>[SewingManager]</color> Mesh đã kín — chuyển sang Finalize.");
                _state = SessionState.Finalizing;
                return;
            }

            if (goA == goB || clothHits.Count < 2)
            {
                hitB = FindSelfSewSecondaryHit(goA, hitA);
                _isSelfSew = true;

                // [FIX-v9-4] Guard cho WaitingForNextSeam: hitB vẫn quá gần hitA
                if (Vector3.Distance(hitA, hitB) < sewRadius * 0.3f)
                {
                    _noInteractionTimer += Time.deltaTime; // đếm thời gian chờ bình thường
                    return;
                }
            }

            float moveA = Vector3.Distance(hitA, _lastAddedHitA);
            float moveB = Vector3.Distance(hitB, _lastAddedHitB);
            if (moveA < sewRadius * 0.3f && moveB < sewRadius * 0.3f) return;

            _noInteractionTimer = 0f;
            Debug.Log($"[SewingManager] Tiếp tục thêm đường khâu: hitA={hitA:F3} <-> hitB={hitB:F3}");

            bool added = _sewer.AddSeam(hitA, hitB, sewRadius);
            if (!added)
            {
                // AddSeam trả false có thể vì mesh vừa kín → Finalize
                if (!_sewer.HasOpenBoundary)
                {
                    Debug.Log("<color=green>[SewingManager]</color> AddSeam thất bại vì mesh đã kín. Finalize.");
                    _state = SessionState.Finalizing;
                }
                return;
            }

            _lastAddedHitA = hitA;
            _lastAddedHitB = hitB;
            _state = SessionState.Sewing;

            if (immediateWeld)
            {
                while (!_sewer.Sew()) { }
                _sewer.UpdateLiveVisuals();
                _state = _sewer.HasOpenBoundary
                    ? SessionState.WaitingForNextSeam
                    : SessionState.Finalizing;
                _noInteractionTimer = 0f;
            }
            return;
        }

        // ── 7. Idle: Khởi tạo session mới ────────────────────────────────
        if (_state == SessionState.Idle)
        {
            _objA      = goA;
            _objB      = goB;
            _isSelfSew = isSelf;

            _sewer = new MeshSewer_UCloth(goA, goB, weldThreshold,
                                          immediateWeld ? int.MaxValue : edgesPerFrame);
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
                _state = _sewer.HasOpenBoundary
                    ? SessionState.WaitingForNextSeam
                    : SessionState.Finalizing;
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

    // [FIX BUG-3] Delegate sang MeshSewer_UCloth.FindSelfSewSecondaryHit
    // để dùng chung logic sleeve (1-loop) đã được fix
    private Vector3 FindSelfSewSecondaryHit(GameObject go, Vector3 hitA)
    {
        return MeshSewer_UCloth.FindSelfSewSecondaryHit(
            go, hitA, sewRadius,
            useSecondaryRayForSelfSew, selfSewSearchRadius);
    }

    public void RegisterClothObject(GameObject obj)
    {
        if (obj == null) return;
        if (!obj.CompareTag(clothTag)) obj.tag = clothTag;
        Debug.Log($"[SewingManager] Đã đăng ký '{obj.name}' làm mục tiêu khâu.");
    }

    private void RegisterSewnMesh(GameObject sewn)
    {
        if (sewn == null) return;
        RegisterClothObject(sewn);

        if (cuttingManager == null)
        {
            cuttingManager = FindObjectOfType<CuttingManager_UCloth>();
            if (cuttingManager != null)
                Debug.LogWarning("[SewingManager] cuttingManager chưa được gán trong Inspector — tự tìm trong scene.");
        }

        if (cuttingManager != null)
        {
            cuttingManager.RegisterClothObject(sewn);
            Debug.Log($"[SewingManager] '{sewn.name}' đã được đăng ký vào CuttingManager.");
        }
        else
        {
            Debug.LogError("[SewingManager] Không tìm thấy CuttingManager_UCloth trong scene! Mesh vừa khâu KHÔNG thể cắt được.");
        }

        if (VRContext.Instance != null)
        {
            var grabber = sewn.GetComponent<UClothLaserGrabber>();
            if (grabber != null)
            {
                grabber.vrController = VRContext.Instance.leftHandController;
                grabber.grabSphere   = VRContext.Instance.grabSphereTarget;
            }
        }
        else
        {
            Debug.LogWarning("[SewingManager] VRContext.Instance == null!");
        }
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
