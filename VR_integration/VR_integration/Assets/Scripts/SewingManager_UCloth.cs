// ============================================================
//  SewingManager_UCloth.cs  — v5.0  (self-sewing + scene registration)
//
//  Thay đổi so với v4:
//    - Hỗ trợ TỰ KHÂU: khi ray chỉ chạm 1 cloth object có ≥2 boundary
//      loop gần nhau, MeshSewer được gọi với goA == goB.
//    - Sau Finalize(), mesh mới được đăng ký tự động với:
//        · VRContext (grabber, environmentColliders)
//        · FabricSpawnerUI.spawnedFabrics (để DeleteLast/Restart hoạt động)
//        · CuttingManager (nếu có)
//    - Loại bỏ logic "if _objA == _objB return" — cùng object vẫn
//      được xử lý khi là tự khâu.
//    - Mesh đã merge (sau khi khâu) vẫn khâu được tiếp vì nó có tag
//      "Cloth", UCCloth component, và MeshCollider — raycast hoạt động
//      bình thường.
// ============================================================
using System.Collections.Generic;
using UnityEngine;
using System.Linq;

public class SewingManager_UCloth : MonoBehaviour
{
    [Header("References")]
    [Tooltip("GameObject của sewer — Ray bắn từ vị trí này theo hướng forward.")]
    public GameObject sewer;

    [Header("Ray Settings")]
    public float rayLength   = 3f;
    public float rayRadius   = 0.05f;
    public bool  showDebugRay = true;

    [Header("Sewing Settings")]
    public string clothTag = "Cloth";

    [Tooltip("Khoảng cách tối đa để hàn hai boundary vertex (world units).")]
    public float weldThreshold = 0.008f;

    [Tooltip("Bán kính vùng khâu tính từ điểm ray chạm vào vải (world units).")]
    public float sewRadius = 0.1f;

    [Tooltip("Số edge hàn mỗi frame (progressive mode).")]
    public int edgesPerFrame = 3;

    [Tooltip("TRUE = hàn toàn bộ ngay lập tức.")]
    public bool immediateWeld = false;

    [Header("Scene Registration")]
    [Tooltip("FabricSpawnerUI để đăng ký mesh mới vào danh sách spawnedFabrics.")]
    public FabricSpawnerUI fabricSpawnerUI;

    [Tooltip("CuttingManager để đăng ký mesh mới (có thể null).")]
    public CuttingManager_UCloth cuttingManager;

    // ── Private ───────────────────────────────────────────────────────────
    private MeshSewer_UCloth _sewer;
    private GameObject _objA, _objB;
    private bool _isSelfSew;
    private bool _sewingInProgress;

    // Gizmo
    private Vector3 _gizmoHitA, _gizmoHitB;
    private bool    _gizmoHasHit;

    // ── LateUpdate ────────────────────────────────────────────────────────
    void LateUpdate()
    {
        if (sewer == null) return;

        // Progressive sewing
        if (_sewingInProgress && _sewer != null)
        {
            bool done = _sewer.Sew();
            if (done) CommitSewn();
            return;
        }

        Ray sewRay = new Ray(sewer.transform.position, sewer.transform.forward);

        // ── Raycast vào MeshCollider ─────────────────────────────────────
        var hits = Physics.RaycastAll(sewRay, rayLength)
                          .OrderBy(h => h.distance)
                          .ToList();

        // Thu thập các cloth hit (tối đa 2 object khác nhau)
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

        // ── Xác định chế độ khâu ────────────────────────────────────────
        GameObject goA, goB;
        Vector3    hitA, hitB;
        bool       isSelf;

        if (clothHits.Count == 0)
        {
            _gizmoHasHit = false;
            return;
        }
        else if (clothHits.Count == 1)
        {
            // Chỉ 1 cloth → tự khâu (mesh phải có ≥2 boundary loop)
            (goA, hitA) = clothHits[0];
            goB  = goA;
            hitB = hitA;
            isSelf = true;
        }
        else
        {
            // 2 cloth khác nhau → khâu bình thường
            (goA, hitA) = clothHits[0];
            (goB, hitB) = clothHits[1];
            isSelf = false;
        }

        // ── Tránh khởi động lại khi đang khâu đúng cặp ──────────────────
        // (Không cần sort InstanceID vì MeshSewer v5 xử lý bất kỳ thứ tự nào)
        if (!_sewingInProgress && _objA == goA && _objB == goB) return;

        // ── Bắt đầu pipeline khâu mới ────────────────────────────────────
        _objA      = goA;
        _objB      = goB;
        _isSelfSew = isSelf;

        _gizmoHitA   = hitA;
        _gizmoHitB   = hitB;
        _gizmoHasHit = true;

        if (isSelf)
            Debug.Log($"<color=cyan>[SewingManager]</color> Tự khâu: {goA.name}@{hitA:F3}");
        else
            Debug.Log($"<color=yellow>[SewingManager]</color> Khâu: {goA.name}@{hitA:F3} <-> {goB.name}@{hitB:F3}");

        _sewer = new MeshSewer_UCloth(goA, goB, weldThreshold,
                                       immediateWeld ? int.MaxValue : edgesPerFrame);

        // Đăng ký callback TRƯỚC khi Initialize — để OnSeamCompleted
        // được gọi khi Finalize() hoàn thành.
        _sewer.OnSeamCompleted = RegisterSewnMesh;

        if (!_sewer.Initialize(hitA, hitB, sewRadius))
        {
            Debug.LogWarning("[SewingManager] Initialize() thất bại.");
            _sewer = null;
            _objA  = _objB = null;
            return;
        }

        _sewingInProgress = true;

        if (immediateWeld)
        {
            while (!_sewer.Sew()) { }
            CommitSewn();
        }
    }

    // ── CommitSewn ────────────────────────────────────────────────────────
    private void CommitSewn()
    {
        string name = _isSelfSew
            ? $"{_objA.name}_SelfSewn"
            : $"{_objA.name}_{_objB.name}_Sewn";

        // Finalize() sẽ gọi RegisterSewnMesh() qua OnSeamCompleted
        _sewer.Finalize(name);
        _sewer            = null;
        _sewingInProgress = false;
        _objA = _objB     = null;
        _gizmoHasHit      = false;
    }

    // ── RegisterSewnMesh ──────────────────────────────────────────────────
    /// <summary>
    /// Callback từ MeshSewer.OnSeamCompleted — đăng ký GameObject mới vào scene.
    /// Mesh mới nhận đầy đủ: VRContext grabber, environmentColliders,
    /// FabricSpawnerUI list, CuttingManager.
    /// </summary>
    private void RegisterSewnMesh(GameObject sewn)
    {
        if (sewn == null) return;

        // 1. VRContext — grabber + floor colliders
        if (VRContext.Instance != null)
        {
            var grabber = sewn.GetComponent<UClothLaserGrabber>();
            if (grabber != null)
            {
                grabber.vrController = VRContext.Instance.leftHandController;
                grabber.grabSphere   = VRContext.Instance.grabSphereTarget;
            }

            var uc = sewn.GetComponent<UCloth.UCCloth>();
            if (uc != null && VRContext.Instance.environmentColliders != null
                           && VRContext.Instance.environmentColliders.Length > 0)
            {
                uc.cubeColliders = MergeArrays(uc.cubeColliders,
                                               VRContext.Instance.environmentColliders);
            }
        }
        else
        {
            Debug.LogWarning("[SewingManager] VRContext.Instance == null — grabber không được gán!");
        }

        // 2. FabricSpawnerUI — thêm vào danh sách spawnedFabrics
        //    để DeleteLastFabric() và Restart() vẫn hoạt động với mesh mới
        if (fabricSpawnerUI != null)
        {
            fabricSpawnerUI.spawnedFabrics.Add(sewn);
            Debug.Log($"[SewingManager] Đã thêm '{sewn.name}' vào FabricSpawnerUI.spawnedFabrics " +
                      $"(tổng: {fabricSpawnerUI.spawnedFabrics.Count})");
        }

        // 3. CuttingManager
        if (cuttingManager != null)
        {
            cuttingManager.RegisterClothObject(sewn);
        }

        Debug.Log($"[SewingManager] ✓ Mesh '{sewn.name}' đã đăng ký vào scene.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    private static T[] MergeArrays<T>(T[] a, T[] b)
    {
        if (a == null || a.Length == 0) return b ?? new T[0];
        if (b == null || b.Length == 0) return a;
        var result = new T[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }

    // ── Gizmos ────────────────────────────────────────────────────────────
    void OnDrawGizmos()
    {
        if (sewer == null || !showDebugRay) return;

        var ray = new Ray(sewer.transform.position, sewer.transform.forward);
        Gizmos.color = _sewingInProgress
            ? new Color(0.2f, 1f, 0.3f, 0.9f)
            : new Color(0.2f, 0.8f, 1f, 0.9f);
        Gizmos.DrawRay(ray.origin, ray.direction * rayLength);

        if (_gizmoHasHit)
        {
            Gizmos.color = new Color(1f, 0.6f, 0f, 0.9f);
            Gizmos.DrawSphere(_gizmoHitA, 0.01f);
            if (!_isSelfSew) Gizmos.DrawSphere(_gizmoHitB, 0.01f);

            Gizmos.color = new Color(1f, 0.6f, 0f, 0.15f);
            Gizmos.DrawSphere(_gizmoHitA, sewRadius);
            if (!_isSelfSew) Gizmos.DrawSphere(_gizmoHitB, sewRadius);
        }
    }
}
