using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Quản lý việc khâu vải: phát hiện 2 miếng vải gần nhau và khâu chúng lại.
/// 
/// Cách dùng:
/// 1. Gán GameObject có Collider làm "sewer" (kim khâu).
/// 2. Di chuyển sewer đến điểm giáp ranh 2 miếng vải.
/// 3. 2 node gần nhất (1 từ mỗi miếng) sẽ được khâu lại tự động.
/// </summary>
public class SewingManager_UCloth : MonoBehaviour
{
    [Header("References")]
    [Tooltip("GameObject đóng vai trò kim khâu (cần có Collider)")]
    public GameObject sewer;

    [Header("Settings")]
    [Tooltip("Tag của các object vải")]
    public string clothTag = "Cloth";

    [Tooltip("Bán kính tìm điểm khâu (m)")]
    public float sewRadius = 0.03f;

    [Tooltip("Khoảng cách tối đa giữa node A và B để chấp nhận khâu (nếu quá xa thì bỏ qua)")]
    public float maxCrossClothDistance = 0.15f;

    // ── State ─────────────────────────────────────────────────────────────────
    private MeshSewer_UCloth _weldLogic;
    private GameObject _objA, _objB;
    private readonly HashSet<int> _weldedA = new HashSet<int>();
    private readonly HashSet<int> _weldedB = new HashSet<int>();

    // ── Unity Messages ────────────────────────────────────────────────────────

    void LateUpdate()
    {
        if (sewer == null) return;

        var touching = FindTouchingClothObjects();
        if (touching.Count < 2) return;

        // Nếu đổi cặp vải → reset phiên khâu
        if (_objA != touching[0] || _objB != touching[1])
        {
            _objA = touching[0];
            _objB = touching[1];
            _weldLogic = new MeshSewer_UCloth(
                _objA.GetComponent<UCloth.UCCloth>(),
                _objB.GetComponent<UCloth.UCCloth>()
            );
            _weldedA.Clear();
            _weldedB.Clear();
            Debug.Log($"<color=yellow>[SewingManager]</color> Bắt đầu phiên khâu: {_objA.name} ↔ {_objB.name}");
        }

        TryWeldAtSewerPosition();
    }

    void FixedUpdate()
    {
        // CẬP NHẬT PIN TARGET mỗi FixedUpdate — TRƯỚC khi UCCloth schedule job
        // UCCloth chạy trong Update/LateUpdate, FixedUpdate chạy trước → an toàn
        _weldLogic?.UpdateWeldPhysics();
    }

    // ── Core Logic ────────────────────────────────────────────────────────────

    /// <summary>
    /// Thử tạo 1 mối khâu tại vị trí sewer hiện tại.
    /// </summary>
    private void TryWeldAtSewerPosition()
    {
        Vector3 sewerPos = sewer.transform.position;

        // Tìm node gần nhất trên vải A trong phạm vi sewRadius
        int idxA = GetClosestSimIndexInRadius(_objA, sewerPos, sewRadius);
        if (idxA == -1 || _weldedA.Contains(idxA)) return;

        Vector3 posA = (Vector3)_objA.GetComponent<UCloth.UCCloth>().simData.positionsReadOnly[idxA];

        // Tìm node gần nhất trên vải B so với posA (không cần trong radius của sewer)
        int idxB = GetClosestSimIndex(_objB, posA);
        if (idxB == -1 || _weldedB.Contains(idxB)) return;

        // Kiểm tra khoảng cách chéo giữa 2 vải — nếu quá xa thì vô nghĩa
        Vector3 posB = (Vector3)_objB.GetComponent<UCloth.UCCloth>().simData.positionsReadOnly[idxB];
        if (Vector3.Distance(posA, posB) > maxCrossClothDistance)
        {
            Debug.Log($"<color=gray>[SewingManager]</color> Node A={idxA} và B={idxB} cách nhau {Vector3.Distance(posA, posB):F3}m — quá xa, bỏ qua.");
            return;
        }

        _weldLogic.AddWeldPair(idxA, idxB);
        _weldedA.Add(idxA);
        _weldedB.Add(idxB);
        Debug.Log($"<color=green>[SewingManager]</color> ✓ Khâu Node {idxA} ({_objA.name}) ↔ Node {idxB} ({_objB.name}) | dist={Vector3.Distance(posA, posB):F3}m");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Tìm tất cả GameObject có tag clothTag đang chạm với sewer.
    /// </summary>
    private List<GameObject> FindTouchingClothObjects()
    {
        var result = new List<GameObject>();
        Collider[] cols = Physics.OverlapSphere(sewer.transform.position, sewRadius * 2f);
        foreach (var c in cols)
        {
            if (c.CompareTag(clothTag) && c.GetComponent<UCloth.UCCloth>() != null)
                if (!result.Contains(c.gameObject))
                    result.Add(c.gameObject);
        }
        return result;
    }

    /// <summary>
    /// Tìm node gần nhất trong phạm vi maxRadius. Trả về -1 nếu không có.
    /// </summary>
    private int GetClosestSimIndexInRadius(GameObject obj, Vector3 targetWorldPos, float maxRadius)
    {
        var uc = obj.GetComponent<UCloth.UCCloth>();
        if (uc?.simData == null || !uc.simData.positionsReadOnly.IsCreated) return -1;

        var positions = uc.simData.positionsReadOnly;
        float maxSqr = maxRadius * maxRadius;
        float minSqr = float.MaxValue;
        int best = -1;

        for (int i = 0; i < positions.Length; i++)
        {
            float sqrDist = Vector3.SqrMagnitude((Vector3)positions[i] - targetWorldPos);
            if (sqrDist < minSqr && sqrDist < maxSqr)
            {
                minSqr = sqrDist;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// Tìm node gần nhất (không giới hạn radius).
    /// </summary>
    private int GetClosestSimIndex(GameObject obj, Vector3 targetWorldPos)
    {
        var uc = obj.GetComponent<UCloth.UCCloth>();
        if (uc?.simData == null || !uc.simData.positionsReadOnly.IsCreated) return -1;

        var positions = uc.simData.positionsReadOnly;
        float minSqr = float.MaxValue;
        int best = -1;

        for (int i = 0; i < positions.Length; i++)
        {
            float sqrDist = Vector3.SqrMagnitude((Vector3)positions[i] - targetWorldPos);
            if (sqrDist < minSqr) { minSqr = sqrDist; best = i; }
        }
        return best;
    }

    // ── Debug ─────────────────────────────────────────────────────────────────

    void OnDrawGizmosSelected()
    {
        if (sewer == null) return;
        Gizmos.color = new Color(0f, 1f, 0.5f, 0.3f);
        Gizmos.DrawSphere(sewer.transform.position, sewRadius);
        Gizmos.color = new Color(0f, 1f, 0.5f, 0.8f);
        Gizmos.DrawWireSphere(sewer.transform.position, sewRadius);
    }
}
