// ============================================================
//  SewingManager_UCloth.cs  — v5  (single-mesh self-sew support)
//
//  Thay đổi so với v4:
//    - Hỗ trợ khâu trên CÙNG 1 mesh (self-sew):
//        Ray hit đúng 1 cloth object → dùng 1 hit point làm seed
//        để tìm 2 boundary loop gần nhất trên chính nó.
//    - Hỗ trợ khâu 2 mesh khác nhau (giữ nguyên v4).
//    - SewMode enum rõ ràng để debug.
//    - _objA == _objB khi self-sew → MeshSewer nhận diện và
//      bỏ qua bước Append.
// ============================================================
using System.Collections.Generic;
using UnityEngine;
using System.Linq;

public class SewingManager_UCloth : MonoBehaviour
{
    [Header("References")]
    [Tooltip("GameObject cua sewer - Ray ban tu vi tri nay theo huong forward.")]
    public GameObject sewer;

    [Header("Ray Settings")]
    public float rayLength = 3f;
    public bool  showDebugRay = true;

    [Header("Sewing Settings")]
    public string clothTag = "Cloth";

    [Tooltip("Khoang cach toi da de han hai boundary vertex (world units).")]
    public float weldThreshold = 0.008f;

    [Tooltip("Ban kinh vung khau tinh tu diem ray cham (world units). " +
             "Chi boundary vertex trong ban kinh nay moi duoc khau.")]
    public float sewRadius = 0.1f;

    [Tooltip("So edge han moi frame (progressive mode).")]
    public int edgesPerFrame = 3;

    [Tooltip("TRUE = han toan bo ngay lap tuc.")]
    public bool immediateWeld = false;

    // ── Private ───────────────────────────────────────────────────────────
    private MeshSewer_UCloth _sewer;
    private GameObject       _objA, _objB;
    private bool             _sewingInProgress = false;

    // Gizmo
    private Vector3 _gizmoHitA, _gizmoHitB;
    private bool    _gizmoHasHit;
    private bool    _gizmoIsSelf;

    // ─────────────────────────────────────────────────────────────────────

    void LateUpdate()
    {
        if (sewer == null) return;

        // Progressive sewing đang chạy
        if (_sewingInProgress && _sewer != null)
        {
            if (_sewer.Sew()) CommitSewn();
            return;
        }

        Ray sewRay = new Ray(sewer.transform.position, sewer.transform.forward);

        // ── Raycast lấy tất cả hit cloth theo thứ tự khoảng cách ─────────
        var clothHits = Physics.RaycastAll(sewRay, rayLength)
            .OrderBy(h => h.distance)
            .Where(h => h.collider.gameObject.CompareTag(clothTag)
                     && h.collider.gameObject.GetComponent<UCloth.UCCloth>() != null)
            .GroupBy(h => h.collider.gameObject)       // gom theo object
            .Select(g => (go: g.Key, point: g.First().point))
            .Take(2)
            .ToList();

        if (clothHits.Count == 0)
        {
            _gizmoHasHit = false;
            return;
        }

        GameObject goA, goB;
        Vector3    hitA, hitB;

        if (clothHits.Count == 1)
        {
            // ── SELF-SEW: 1 mesh, khâu 2 boundary loop trên chính nó ─────
            goA  = goB  = clothHits[0].go;
            hitA = hitB = clothHits[0].point;
            _gizmoIsSelf = true;
        }
        else
        {
            // ── CROSS-SEW: 2 mesh khác nhau ───────────────────────────────
            goA  = clothHits[0].go;  hitA = clothHits[0].point;
            goB  = clothHits[1].go;  hitB = clothHits[1].point;
            _gizmoIsSelf = false;
        }

        // Sắp xếp ổn định để tránh re-trigger
        if (!_gizmoIsSelf && goA.GetInstanceID() > goB.GetInstanceID())
        {
            (goA, goB)   = (goB, goA);
            (hitA, hitB) = (hitB, hitA);
        }

        // Không re-trigger nếu đang chờ cùng cặp
        if (_objA == goA && _objB == goB) return;

        _objA = goA; _objB = goB;
        _gizmoHitA = hitA; _gizmoHitB = hitB; _gizmoHasHit = true;

        string modeStr = _gizmoIsSelf ? "self-sew" : "cross-sew";
        Debug.Log($"<color=yellow>[SewingManager]</color> [{modeStr}] " +
                  $"{goA.name}@{hitA:F3}" +
                  (_gizmoIsSelf ? "" : $" <-> {goB.name}@{hitB:F3}"));

        _sewer = new MeshSewer_UCloth(goA, goB, weldThreshold,
                                       immediateWeld ? int.MaxValue : edgesPerFrame);

        if (!_sewer.Initialize(hitA, hitB, sewRadius))
        {
            Debug.LogWarning("[SewingManager] Initialize() thất bại.");
            _sewer = null; _objA = _objB = null; _gizmoHasHit = false;
            return;
        }

        _sewingInProgress = true;

        if (immediateWeld)
        {
            while (!_sewer.Sew()) { }
            CommitSewn();
        }
    }

    private void CommitSewn()
    {
        string name = _objA == _objB
            ? $"{_objA.name}_SelfSewn"
            : $"{_objA.name}_{_objB.name}_Sewn";

        _sewer.Finalize(name);
        _sewer = null; _sewingInProgress = false;
        _objA  = _objB = null; _gizmoHasHit = false;
    }

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
            // Màu cam = cross-sew, tím = self-sew
            Gizmos.color = _gizmoIsSelf
                ? new Color(0.8f, 0.2f, 1f, 0.9f)
                : new Color(1f,   0.6f, 0f,  0.9f);

            Gizmos.DrawSphere(_gizmoHitA, 0.012f);
            if (!_gizmoIsSelf) Gizmos.DrawSphere(_gizmoHitB, 0.012f);

            Gizmos.color = _gizmoIsSelf
                ? new Color(0.8f, 0.2f, 1f, 0.12f)
                : new Color(1f,   0.6f, 0f,  0.12f);
            Gizmos.DrawSphere(_gizmoHitA, sewRadius);
            if (!_gizmoIsSelf) Gizmos.DrawSphere(_gizmoHitB, sewRadius);
        }
    }
}
