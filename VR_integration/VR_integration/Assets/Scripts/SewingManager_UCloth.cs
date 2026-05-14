using System.Collections.Generic;
using UnityEngine;
using System.Linq;

/// <summary>
/// SewingManager_UCloth – phát hiện đối tượng vải bằng Collider thay vì Ray.
///
/// v3 – Collider-based detection:
///   Dùng Collider gắn trên sewer để phát hiện cloth object trong vùng tiếp xúc.
///   Tìm tất cả cloth object có Renderer bounds giao với bounds của sewer collider,
///   sau đó lấy tối đa 2 object gần sewer nhất để thực hiện khâu.
///   Toàn bộ logic AddWeldPair / UpdateWeldPhysics giữ nguyên.
/// </summary>
public class SewingManager_UCloth : MonoBehaviour
{
    [Header("References")]
    [Tooltip("GameObject của sewer – phải có Collider để phát hiện vùng khâu.")]
    public GameObject sewer;

    [Header("Sewing Settings")]
    public string clothTag = "Cloth";

    [Tooltip("Bán kính quanh điểm chạm (hit point) để tìm sim-node của vải A.")]
    public float sewRadius = 0.03f;

    [Tooltip("Khoảng cách tối đa từ node A đến node B trên vải B để được khâu.")]
    public float maxCrossClothDistance = 0.15f;

    [Tooltip("Khoảng thời gian chờ (giây) trước khi một node có thể được khâu tiếp")]
    public float sewCooldown = 0.5f;

    // ── private ──────────────────────────────────────────────────────────────

    private Collider _sewerCollider;
    private MeshSewer_UCloth _weldLogic;
    private GameObject _objA, _objB;

    private readonly Dictionary<int, float> _nodeCooldownsA = new Dictionary<int, float>();
    private readonly Dictionary<int, float> _nodeCooldownsB = new Dictionary<int, float>();

    void Start()
    {
        if (sewer == null)
        {
            Debug.LogError("[SewingManager_UCloth] Chưa gán Sewer!");
            enabled = false;
            return;
        }

        _sewerCollider = sewer.GetComponent<Collider>();
        if (_sewerCollider == null)
        {
            Debug.LogError("[SewingManager_UCloth] Sewer không có Collider!");
            enabled = false;
            return;
        }
    }

    // ── Update ───────────────────────────────────────────────────────────────

    void LateUpdate()
    {
        if (_sewerCollider == null) return;

        var touching = FindClothObjectsOverlappingCollider();
        if (touching.Count < 2) return;

        // Sắp xếp ổn định theo InstanceID để tránh hoán đổi mỗi frame
        var sorted        = touching.OrderBy(g => g.GetInstanceID()).ToList();
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

        // Dùng tâm collider của sewer làm điểm tham chiếu tìm sim-node
        Vector3 sewPoint = _sewerCollider.bounds.center;
        TryWeldAtPosition(sewPoint);
    }

    void FixedUpdate()
    {
        _weldLogic?.UpdateWeldPhysics();
    }

    // ── Collider helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Tìm tất cả GameObject có tag clothTag mà bounds của chúng giao với bounds của sewer collider.
    /// Kết quả sắp xếp từ gần đến xa theo khoảng cách bounds center với sewer.
    /// </summary>
    private List<GameObject> FindClothObjectsOverlappingCollider()
    {
        var result       = new List<(GameObject obj, float dist)>();
        var clothObjects = GameObject.FindGameObjectsWithTag(clothTag);
        var sewerBounds  = _sewerCollider.bounds;
        var sewerCenter  = sewerBounds.center;

        foreach (var obj in clothObjects)
        {
            if (obj == null || !obj.activeSelf) continue;
            var uc = obj.GetComponent<UCloth.UCCloth>();
            if (uc == null) continue;

            var renderer = obj.GetComponent<Renderer>();
            if (renderer == null) continue;

            if (sewerBounds.Intersects(renderer.bounds))
            {
                float dist = Vector3.Distance(sewerCenter, renderer.bounds.center);
                result.Add((obj, dist));
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
        if (sewer == null) return;

        var col = sewer.GetComponent<Collider>();
        if (col == null) return;

        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.4f);
        Gizmos.DrawWireCube(col.bounds.center, col.bounds.size);
    }
}
