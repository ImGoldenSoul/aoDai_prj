using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// CuttingManager tự động tìm tất cả GameObject có tag 'cloth' làm target.
/// Khi cắt thành công, các mảnh được tạo ra vẫn có UCCloth + tag 'cloth'.
///
/// v2 – Ray-based detection:
///   Thay vì dùng Collider của cutter, Manager bắn một Ray từ transform của
///   cutter (origin = position, direction = forward) và tìm các triangle mesh
///   nằm trong bán kính <rayRadius> xung quanh đường ray để tích lũy vết cắt.
/// </summary>
public class CuttingManager_UCloth : MonoBehaviour
{
    [Header("References")]
    [Tooltip("GameObject của cutter – Ray bắn từ vị trí này theo hướng forward của nó.")]
    public GameObject cutter;

    [Header("Ray Settings")]
    [Tooltip("Chiều dài tối đa của ray cắt (m).")]
    public float rayLength = 5f;

    [Tooltip("Bán kính ống (cylinder) bao quanh ray để xác định triangle bị cắt (m).")]
    public float rayRadius = 0.02f;

    [Tooltip("Hiển thị ray debug trong Scene view.")]
    public bool showDebugRay = true;

    [Header("Settings")]
    [Tooltip("Số frame giữa 2 lần check cắt")]
    public int checkInterval = 3;

    [Tooltip("Nếu TRUE: chỉ tách khi vết cắt chia mesh thành >= 2 phần riêng biệt.")]
    public bool splitOnlyWhenDisconnected = true;

    [Tooltip("Lực đẩy các mảnh văng ra sau khi cắt")]
    public float splitForce = 1.5f;

    [Tooltip("Tag dùng để tìm Cloth targets")]
    public string clothTag = "Cloth";

    // ── private ──────────────────────────────────────────────────────────────
    private int _frameCount;

    // Map: target GameObject → cutter instance của nó
    private Dictionary<GameObject, MeshCutter_UCloth> _cutters
        = new Dictionary<GameObject, MeshCutter_UCloth>();

    void Start()
    {
        if (cutter == null)
        {
            Debug.LogError("[CuttingManager_UCloth] Chưa gán Cutter!");
            enabled = false;
            return;
        }

        RegisterAllClothObjects();
    }

    // ── Helpers: lấy Ray từ cutter ───────────────────────────────────────────

    /// <summary>Tạo Ray từ transform của cutter.</summary>
    private Ray GetCutterRay()
        => new Ray(cutter.transform.position, cutter.transform.forward);

    // ── Registration ─────────────────────────────────────────────────────────

    private void RegisterAllClothObjects()
    {
        var clothObjects = GameObject.FindGameObjectsWithTag(clothTag);
        foreach (var obj in clothObjects)
            RegisterClothObject(obj);

        Debug.Log($"[CuttingManager_UCloth] Đã đăng ký {_cutters.Count} cloth object(s).");
    }

    public void RegisterClothObject(GameObject obj)
    {
        if (obj == null || _cutters.ContainsKey(obj)) return;
        if (obj.GetComponent<MeshFilter>() == null) return;

        _cutters[obj] = null;
        StartCoroutine(InitCutterForObject(obj));
    }

    private IEnumerator InitCutterForObject(GameObject obj)
    {
        var ucCloth = obj.GetComponent<UCloth.UCCloth>();
        if (ucCloth != null)
        {
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

        var meshCutter = new MeshCutter_UCloth(obj, splitForce);

        yield return new WaitForEndOfFrame();

        if (obj == null) yield break;

        meshCutter.Initialize();
        _cutters[obj] = meshCutter;
        Debug.Log($"[CuttingManager_UCloth] ✓ Sẵn sàng cắt: {obj.name}");
    }

    // ── Update ───────────────────────────────────────────────────────────────

    void LateUpdate()
    {
        _frameCount++;
        if (_frameCount % checkInterval != 0) return;

        Ray cutRay = GetCutterRay();

        var toRemove = new List<GameObject>();
        var toAdd    = new List<GameObject>();

        foreach (var kv in _cutters)
        {
            GameObject obj    = kv.Key;
            var        mc     = kv.Value;

            if (obj == null || !obj.activeSelf)
            {
                toRemove.Add(obj);
                continue;
            }

            if (mc == null) continue;

            // ── Cheap bounds check: kiểm tra ray có đi gần bounds không ────
            var renderer = obj.GetComponent<Renderer>();
            if (renderer != null && !RayPassesNearBounds(cutRay, renderer.bounds, rayRadius + 0.1f))
                continue;

            var result = mc.PerformCutByRay(cutRay, rayLength, rayRadius, splitOnlyWhenDisconnected);

            if (result == CutResult_Ucloth.Split)
            {
                var newPieces = mc.GetLastCreatedPieces();
                foreach (var piece in newPieces)
                    toAdd.Add(piece);

                // ── Log diện tích ─────────────────────────────────────────
                var areaReport = mc.GetLastAreaReport();
                if (areaReport.HasValue)
                {
                    var r = areaReport.Value;
                    Debug.Log($"[CuttingManager_UCloth] AREA REPORT cho {obj.name}:\n" +
                              r.ToString());

                    for (int pi = 0; pi < newPieces.Count; pi++)
                    {
                        if (newPieces[pi] == null) continue;
                        var data = newPieces[pi].AddComponent<ClothAreaData>();
                        data.originalArea = r.OriginalArea;
                        data.pieceArea    = pi < r.PieceAreas.Length ? r.PieceAreas[pi] : 0f;
                        data.seamArea     = r.SeamArea;
                        data.seamRatio    = r.SeamRatio;
                    }
                }

                obj.SetActive(false);
                toRemove.Add(obj);
                Debug.Log($"[CuttingManager_UCloth] ✓ {obj.name} đã tách.");
            }
            else if (result == CutResult_Ucloth.Trimmed)
            {
                var meshCol = obj.GetComponent<MeshCollider>();
                if (meshCol != null)
                {
                    meshCol.sharedMesh = null;
                    meshCol.sharedMesh = obj.GetComponent<MeshFilter>().mesh;
                }
            }
        }

        foreach (var obj in toRemove) _cutters.Remove(obj);
        foreach (var obj in toAdd)    RegisterClothObject(obj);
    }

    // ── Geometry helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Kiểm tra nhanh: Ray có đi gần AABB bounds trong khoảng maxDist không?
    /// Dùng để early-out trước khi thử từng triangle.
    /// </summary>
    private static bool RayPassesNearBounds(Ray ray, Bounds bounds, float maxDist)
    {
        // Mở rộng bounds theo maxDist rồi test ray thông thường
        var expanded = bounds;
        expanded.Expand(maxDist * 2f);
        return expanded.IntersectRay(ray);
    }

    // ── Gizmos ───────────────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (cutter == null || !showDebugRay) return;

        Ray ray = new Ray(cutter.transform.position, cutter.transform.forward);

        // Đường ray chính
        Gizmos.color = new Color(1f, 0.3f, 0f, 0.9f);
        Gizmos.DrawRay(ray.origin, ray.direction * rayLength);

        // Hình trụ biểu thị bán kính ảnh hưởng (vẽ bằng sphere ở 2 đầu)
        Gizmos.color = new Color(1f, 0.6f, 0f, 0.25f);
        Gizmos.DrawSphere(ray.origin, rayRadius);
        Gizmos.DrawSphere(ray.origin + ray.direction * rayLength, rayRadius);
    }
}
