using System.Collections.Generic;
using UnityEngine;
using System.Linq;

/// <summary>
/// SewingManager_UCloth – phát hiện đối tượng vải bằng Ray thay vì OverlapSphere.
///
/// v2 – Ray-based detection:
///   Bắn Ray từ transform của sewer theo hướng forward.
///   Tìm tất cả cloth object có Renderer bounds bị ray đi qua trong khoảng rayLength,
///   sau đó lấy tối đa 2 object gần ray.origin nhất để thực hiện khâu.
///   Toàn bộ logic AddWeldPair / UpdateWeldPhysics giữ nguyên.
/// </summary>
public class SewingManager_UCloth : MonoBehaviour
{
    [Header("References")]
    [Tooltip("GameObject của sewer – Ray bắn từ vị trí này theo hướng forward của nó.")]
    public GameObject sewer;

    [Header("Ray Settings")]
    [Tooltip("Chiều dài tối đa của ray khâu (m).")]
    public float rayLength = 3f;

    [Tooltip("Bán kính ống bao quanh ray để phát hiện cloth object (m).")]
    public float rayRadius = 0.05f;

    [Tooltip("Hiển thị ray debug trong Scene view.")]
    public bool showDebugRay = true;

    [Header("Sewing Settings")]
    public string clothTag = "Cloth";

    [Tooltip("Bán kính quanh điểm chạm (hit point) để tìm sim-node của vải A.")]
    public float sewRadius = 0.03f;

    [Tooltip("Khoảng cách tối đa từ node A đến node B trên vải B để được khâu.")]
    public float maxCrossClothDistance = 0.15f;

    [Tooltip("Khoảng thời gian chờ (giây) trước khi một node có thể được khâu tiếp")]
    public float sewCooldown = 0.5f;

    // ── private ──────────────────────────────────────────────────────────────

    private MeshSewer_UCloth _weldLogic;
    private GameObject _objA, _objB;

    private readonly Dictionary<int, float> _nodeCooldownsA = new Dictionary<int, float>();
    private readonly Dictionary<int, float> _nodeCooldownsB = new Dictionary<int, float>();

    // ── Update ───────────────────────────────────────────────────────────────

    void LateUpdate()
    {
        if (sewer == null) return;

        Ray sewRay = GetSewerRay();

        var touching = FindClothObjectsAlongRay(sewRay);
        if (touching.Count < 2) return;

        // Sắp xếp ổn định theo InstanceID để tránh hoán đổi mỗi frame
        var sorted     = touching.OrderBy(g => g.GetInstanceID()).ToList();
        GameObject candidateA = sorted[0];
        GameObject candidateB = sorted[1];

        if (_objA != candidateA || _objB != candidateB)
        {
            _objA = candidateA;
            _objB = candidateB;
            _weldLogic = new MeshSewer_UCloth(
                _objA.GetComponent<UCloth.UCCloth>(),
                _objB.GetComponent<UCloth.UCCloth>()
            );
            _nodeCooldownsA.Clear();
            _nodeCooldownsB.Clear();
            Debug.Log($"<color=yellow>[SewingManager]</color> Cặp vải mới: {_objA.name} & {_objB.name}");
        }

        // Tìm hit point trên ray gần vải nhất để làm tâm tìm sim-node
        Vector3 sewPoint = GetRayHitPoint(sewRay, _objA);
        TryWeldAtPosition(sewPoint);
    }

    void FixedUpdate()
    {
        _weldLogic?.UpdateWeldPhysics();
    }

    // ── Ray helpers ───────────────────────────────────────────────────────────

    private Ray GetSewerRay()
        => new Ray(sewer.transform.position, sewer.transform.forward);

    /// <summary>
    /// Trả về điểm gần nhất trên ray với bounds của obj,
    /// hoặc ray.origin nếu không tính được.
    /// </summary>
    private Vector3 GetRayHitPoint(Ray ray, GameObject obj)
    {
        var renderer = obj.GetComponent<Renderer>();
        if (renderer != null && renderer.bounds.IntersectRay(ray, out float dist))
            return ray.GetPoint(Mathf.Max(0f, dist));
        return ray.origin;
    }

    /// <summary>
    /// Tìm tất cả GameObject có tag clothTag mà ray đi qua (bounds + rayRadius tolerance).
    /// Kết quả sắp xếp từ gần đến xa theo bounds center distance với ray.origin.
    /// </summary>
    private List<GameObject> FindClothObjectsAlongRay(Ray ray)
    {
        var result      = new List<(GameObject obj, float dist)>();
        var clothObjects = GameObject.FindGameObjectsWithTag(clothTag);

        foreach (var obj in clothObjects)
        {
            if (obj == null || !obj.activeSelf) continue;
            var uc = obj.GetComponent<UCloth.UCCloth>();
            if (uc == null) continue;

            var renderer = obj.GetComponent<Renderer>();
            if (renderer == null) continue;

            // Mở rộng bounds theo rayRadius rồi test
            Bounds expanded = renderer.bounds;
            expanded.Expand(rayRadius * 2f);

            if (expanded.IntersectRay(ray, out float t) && t <= rayLength + rayRadius)
            {
                float distToOrigin = Vector3.Distance(ray.origin, renderer.bounds.center);
                result.Add((obj, distToOrigin));
            }
        }

        return result
            .OrderBy(x => x.dist)
            .Select(x => x.obj)
            .ToList();
    }

    // ── Sewing logic (giữ nguyên hoàn toàn) ─────────────────────────────────

    private void TryWeldAtPosition(Vector3 sewPoint)
    {
        float currentTime = Time.time;

        List<int> indicesA = GetAllSimIndicesInRadius(_objA, sewPoint, sewRadius);

        foreach (int idxA in indicesA)
        {
            if (_nodeCooldownsA.TryGetValue(idxA, out float lastTimeA) &&
                currentTime - lastTimeA < sewCooldown)
                continue;

            Vector3 posA = (Vector3)_objA.GetComponent<UCloth.UCCloth>()
                                         .simData.positionsReadOnly[idxA];

            int idxB = GetClosestSimIndexInRadius(_objB, posA, maxCrossClothDistance);

            if (idxB == -1) continue;
            if (_nodeCooldownsB.TryGetValue(idxB, out float lastTimeB) &&
                currentTime - lastTimeB < sewCooldown)
                continue;

            _weldLogic.AddWeldPair(idxA, idxB);

            _nodeCooldownsA[idxA] = currentTime;
            _nodeCooldownsB[idxB] = currentTime;

            Debug.Log($"<color=green>[Sewing]</color> Khâu: A({idxA}) ↔ B({idxB})");
        }
    }

    private List<int> GetAllSimIndicesInRadius(GameObject obj, Vector3 center, float radius)
    {
        var result = new List<int>();
        var uc = obj.GetComponent<UCloth.UCCloth>();
        if (uc?.simData == null || !uc.simData.positionsReadOnly.IsCreated) return result;

        var   positions = uc.simData.positionsReadOnly;
        float rSqr      = radius * radius;
        for (int i = 0; i < positions.Length; i++)
        {
            if (Vector3.SqrMagnitude((Vector3)positions[i] - center) < rSqr)
                result.Add(i);
        }
        return result;
    }

    private int GetClosestSimIndexInRadius(GameObject obj, Vector3 targetWorldPos, float maxRadius)
    {
        var uc = obj.GetComponent<UCloth.UCCloth>();
        if (uc?.simData == null || !uc.simData.positionsReadOnly.IsCreated) return -1;

        var   positions = uc.simData.positionsReadOnly;
        float maxSqr    = maxRadius * maxRadius;
        float minSqr    = float.MaxValue;
        int   best      = -1;

        for (int i = 0; i < positions.Length; i++)
        {
            float sqrDist = Vector3.SqrMagnitude((Vector3)positions[i] - targetWorldPos);
            if (sqrDist < minSqr && sqrDist < maxSqr) { minSqr = sqrDist; best = i; }
        }
        return best;
    }

    // ── Gizmos ───────────────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (sewer == null || !showDebugRay) return;

        Ray ray = new Ray(sewer.transform.position, sewer.transform.forward);

        // Đường ray chính
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);
        Gizmos.DrawRay(ray.origin, ray.direction * rayLength);

        // Bán kính ảnh hưởng ở đầu và cuối ray
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.25f);
        Gizmos.DrawSphere(ray.origin, rayRadius);
        Gizmos.DrawSphere(ray.origin + ray.direction * rayLength, rayRadius);
    }
}
