// ============================================================
//  CuttingManager_UCloth.cs  — v6.0  (Auto-Cut, No Trigger)
//
//  Cắt hoàn toàn tự động — không cần bất kỳ input nào.
//
//  Logic:
//  ─────────────────────────────────────────────────────────
//  Mỗi frame bắn ray từ cutter.forward.
//
//  Nếu ray HIT cloth mesh:
//    → Tích lũy hitPoint vào _cutPath của object đó.
//    → Đánh dấu object đó đang bị "active cut" frame này.
//
//  Nếu ray KHÔNG HIT object (mà trước đó đang hit):
//    → "Stroke kết thúc" → CommitCut() nếu đủ minPathPoints.
//
//  Nếu ray HIT object khác với object đang cắt dang dở:
//    → Commit object cũ trước, bắt đầu tích lũy object mới.
//
//  Kết quả: cử động cutter qua mesh = tự động tạo vết cắt.
//  Không cần trigger, không cần button.
// ============================================================

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CuttingManager_UCloth : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Transform đầu công cụ cắt — ray được bắn từ đây.")]
    public Transform cutter;

    [Header("Ray Settings")]
    public float rayLength    = 3f;
    public bool  showDebugRay = true;

    [Header("Cut Settings")]
    public string clothTag      = "Cloth";
    public float  splitForce    = 1.5f;
    [Tooltip("Nếu TRUE: chỉ tách khi đường cắt thực sự chia mesh thành ≥ 2 vùng.")]
    public bool   splitOnlyWhenDisconnected = true;
    [Tooltip("Số điểm path tối thiểu trước khi CommitCut(). Tăng lên để tránh cut khi chạm nhẹ.")]
    public int    minPathPoints  = 6;
    [Tooltip("Khoảng cách tối thiểu giữa 2 điểm path liên tiếp (m).")]
    public float  minPathSpacing = 0.005f;
    [Tooltip("Số frame ray KHÔNG hit cloth trước khi coi là kết thúc stroke và commit.")]
    public int    missFramesToCommit = 3;

    // ── Private ───────────────────────────────────────────────────────────

    // Map: target GameObject → MeshCutter_UCloth instance
    private readonly Dictionary<GameObject, MeshCutter_UCloth> _cutters
        = new Dictionary<GameObject, MeshCutter_UCloth>();

    private readonly HashSet<GameObject> _ucClothObjects = new HashSet<GameObject>();

    // Theo dõi object đang được tích lũy path (chỉ 1 tại 1 thời điểm)
    private GameObject _activeTarget;
    private int        _missFrameCount;

    // Gizmo
    private Vector3 _gizmoHitPoint;
    private bool    _gizmoHasHit;

    // ── Lifecycle ─────────────────────────────────────────────────────────

    void Start()
    {
        if (cutter == null)
        {
            Debug.LogError("[CuttingManager_UCloth] Chưa gán Cutter transform!");
            enabled = false;
            return;
        }
        RegisterAllClothObjects();
    }

    // ── Registration ──────────────────────────────────────────────────────

    private void RegisterAllClothObjects()
    {
        var clothObjects = GameObject.FindGameObjectsWithTag(clothTag);
        foreach (var obj in clothObjects)
            RegisterClothObject(obj);
        Debug.Log($"[CuttingManager_UCloth] Đăng ký {_cutters.Count} cloth object(s).");
    }

    public void RegisterClothObject(GameObject obj)
    {
        if (obj == null || _cutters.ContainsKey(obj)) return;
        if (obj.GetComponent<MeshFilter>() == null) return;

        _cutters[obj] = null;

        if (obj.GetComponent<UCloth.UCCloth>() != null)
            _ucClothObjects.Add(obj);

        StartCoroutine(InitCutterForObject(obj));
    }

    private IEnumerator InitCutterForObject(GameObject obj)
    {
        var ucCloth = obj.GetComponent<UCloth.UCCloth>();
        if (ucCloth != null)
        {
            if (ucCloth.sphereColliders  == null) ucCloth.sphereColliders  = new SphereCollider[0];
            if (ucCloth.capsuleColliders == null) ucCloth.capsuleColliders = new CapsuleCollider[0];
            if (ucCloth.cubeColliders    == null) ucCloth.cubeColliders    = new BoxCollider[0];
            if (ucCloth.pinColliders     == null) ucCloth.pinColliders     = new System.Collections.Generic.List<Collider>();

            yield return null;
            yield return null;

            if (obj == null) yield break;

            float timeout = 5f;
            while (timeout > 0f)
            {
                if (ucCloth.simData != null && ucCloth.simData.positionsReadOnly.IsCreated)
                    break;
                timeout -= Time.deltaTime;
                yield return null;
            }
            if (timeout <= 0f)
                Debug.LogWarning($"[CuttingManager_UCloth] Timeout chờ UCCloth.simData cho {obj.name}!");
        }
        else
        {
            yield return null;
        }

        if (obj == null) yield break;

        var mc = new MeshCutter_UCloth(obj, splitForce);
        mc._minPathPointSpacingOverride = minPathSpacing;

        yield return new WaitForEndOfFrame();
        if (obj == null) yield break;

        mc.Initialize();
        _cutters[obj] = mc;
        Debug.Log($"[CuttingManager_UCloth] ✓ Sẵn sàng cắt: {obj.name}");
    }

    // ── LateUpdate — Auto-Cut Logic ───────────────────────────────────────

    void LateUpdate()
    {
        if (cutter == null) return;

        Ray cutRay = new Ray(cutter.position, cutter.forward);
        _gizmoHasHit = false;

        // Raycast vào tất cả cloth object, lấy hit gần nhất
        GameObject hitTarget = null;
        Vector3    hitPoint  = Vector3.zero;

        var hits = Physics.RaycastAll(cutRay, rayLength);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (var hit in hits)
        {
            // Tìm cloth object tương ứng với collider bị hit
            GameObject go = FindClothParent(hit.collider.gameObject);
            if (go == null) continue;
            if (!_cutters.ContainsKey(go)) continue;
            if (_cutters[go] == null) continue; // chưa Initialize xong

            hitTarget = go;
            hitPoint  = hit.point;
            _gizmoHitPoint = hitPoint;
            _gizmoHasHit   = true;
            break;
        }

        // ── Case 1: Đang hit 1 object ─────────────────────────────────────
        if (hitTarget != null)
        {
            // Nếu đang cắt object khác → commit object cũ trước
            if (_activeTarget != null && _activeTarget != hitTarget)
            {
                TryCommitActive();
            }

            // Tích lũy điểm vào object đang hit
            _activeTarget   = hitTarget;
            _missFrameCount = 0;
            _cutters[hitTarget].AccumulateHit(hitPoint);
        }
        // ── Case 2: Miss (ray không hit cloth nào) ────────────────────────
        else if (_activeTarget != null)
        {
            _missFrameCount++;

            // Chờ đủ N frame miss liên tiếp → commit (tránh commit khi ray rung)
            if (_missFrameCount >= missFramesToCommit)
            {
                TryCommitActive();
            }
        }

        // ── Cleanup: xóa object đã null/inactive ──────────────────────────
        CleanupStaleObjects();
    }

    // ── Commit active target ──────────────────────────────────────────────

    private void TryCommitActive()
    {
        if (_activeTarget == null) { ResetActive(); return; }
        if (!_cutters.TryGetValue(_activeTarget, out var mc) || mc == null) { ResetActive(); return; }

        if (mc.PathPointCount < minPathPoints)
        {
            Debug.Log($"[CuttingManager_UCloth] '{_activeTarget.name}': path {mc.PathPointCount} < {minPathPoints} điểm, bỏ qua.");
            mc.ClearPath();
            ResetActive();
            return;
        }

        var result = mc.CommitCut(splitOnlyWhenDisconnected);

        if (result == CutResult_Ucloth.Split)
        {
            var newPieces = mc.GetLastCreatedPieces();

            var areaReport = mc.GetLastAreaReport();
            if (areaReport.HasValue)
            {
                var r = areaReport.Value;
                Debug.Log($"[CuttingManager_UCloth] AREA REPORT cho {_activeTarget.name}:\n{r}");
                for (int pi = 0; pi < newPieces.Count; pi++)
                {
                    if (newPieces[pi] == null) continue;
                    var data        = newPieces[pi].AddComponent<ClothAreaData>();
                    data.originalArea = r.OriginalArea;
                    data.pieceArea    = pi < r.PieceAreas.Length ? r.PieceAreas[pi] : 0f;
                    data.seamArea     = r.SeamArea;
                    data.seamRatio    = r.SeamRatio;
                }
            }

            _activeTarget.SetActive(false);
            _cutters.Remove(_activeTarget);
            _ucClothObjects.Remove(_activeTarget);

            foreach (var piece in newPieces)
                RegisterClothObject(piece);

            Debug.Log($"[CuttingManager_UCloth] ✓ Tách thành {newPieces.Count} mảnh.");
        }
        else if (result == CutResult_Ucloth.Trimmed)
        {
            Debug.Log($"[CuttingManager_UCloth] '{_activeTarget.name}': Trimmed (chưa xuyên hết).");
        }

        ResetActive();
    }

    private void ResetActive()
    {
        _activeTarget   = null;
        _missFrameCount = 0;
    }

    // ── Tìm cloth parent của một collider (đề phòng MeshCollider trên child) ──
    private GameObject FindClothParent(GameObject go)
    {
        if (go == null) return null;
        if (_cutters.ContainsKey(go)) return go;

        // Leo lên parent
        Transform t = go.transform.parent;
        while (t != null)
        {
            if (_cutters.ContainsKey(t.gameObject)) return t.gameObject;
            t = t.parent;
        }
        return null;
    }

    // ── Dọn dẹp object null/inactive trong dict ───────────────────────────
    private readonly List<GameObject> _toRemove = new List<GameObject>();

    private void CleanupStaleObjects()
    {
        _toRemove.Clear();
        foreach (var kv in _cutters)
            if (kv.Key == null || !kv.Key.activeSelf)
                _toRemove.Add(kv.Key);

        foreach (var obj in _toRemove)
        {
            _cutters.Remove(obj);
            _ucClothObjects.Remove(obj);
            if (_activeTarget == obj) ResetActive();
        }
    }

    // ── Gizmos ────────────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (cutter == null || !showDebugRay) return;

        // Ray — đỏ khi đang tích lũy path, cam khi idle
        Gizmos.color = (_activeTarget != null)
            ? new Color(1f, 0.1f, 0.1f, 0.9f)
            : new Color(1f, 0.5f, 0f, 0.6f);
        Gizmos.DrawRay(cutter.position, cutter.forward * rayLength);

        // Hit point
        if (_gizmoHasHit)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(_gizmoHitPoint, 0.01f);
        }

        // Cut paths của từng cutter
        if (Application.isPlaying)
        {
            foreach (var kv in _cutters)
                kv.Value?.DrawDebugGizmos();
        }
    }
}
