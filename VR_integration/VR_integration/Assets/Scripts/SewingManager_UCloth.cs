using System.Collections.Generic;
using UnityEngine;
using System.Linq;

public class SewingManager_UCloth : MonoBehaviour
{
    [Header("References")]
    public GameObject sewer;

    [Header("Settings")]
    public string clothTag = "Cloth";
    public float sewRadius = 0.03f;
    public float maxCrossClothDistance = 0.15f;
    
    [Tooltip("Khoảng thời gian chờ (giây) trước khi một node có thể được khâu tiếp")]
    public float sewCooldown = 0.5f; 

    private MeshSewer_UCloth _weldLogic;
    private GameObject _objA, _objB;
    
    private readonly Dictionary<int, float> _nodeCooldownsA = new Dictionary<int, float>();
    private readonly Dictionary<int, float> _nodeCooldownsB = new Dictionary<int, float>();

    void LateUpdate()
    {
        if (sewer == null) return;

        var touching = FindTouchingClothObjects();
        if (touching.Count < 2) return;

        // FIX BUG 1: Sắp xếp để đảm bảo candidateA và B không bị hoán đổi mỗi frame
        var sorted = touching.OrderBy(g => g.GetInstanceID()).ToList();
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

        TryWeldAtSewerPosition();
    }

    void FixedUpdate()
    {
        // Cập nhật các ràng buộc vật lý liên tục dựa trên danh sách đã khâu
        _weldLogic?.UpdateWeldPhysics();
    }

    private void TryWeldAtSewerPosition()
    {
        Vector3 sewerPos = sewer.transform.position;
        float currentTime = Time.time;

        // Lấy tất cả node trong bán kính để khâu được nhiều vertex một lúc
        List<int> indicesA = GetAllSimIndicesInRadius(_objA, sewerPos, sewRadius);
        
        foreach (int idxA in indicesA)
        {
            // FIX BUG 2: Kiểm tra cooldown từng node, nếu kẹt thì bỏ qua sang node khác
            if (_nodeCooldownsA.TryGetValue(idxA, out float lastTimeA) && currentTime - lastTimeA < sewCooldown) 
                continue;

            Vector3 posA = (Vector3)_objA.GetComponent<UCloth.UCCloth>().simData.positionsReadOnly[idxA];

            // FIX BUG 3: Tìm node B gần node A nhất nhưng phải trong tầm giới hạn
            int idxB = GetClosestSimIndexInRadius(_objB, posA, maxCrossClothDistance);
            
            if (idxB == -1) continue;
            if (_nodeCooldownsB.TryGetValue(idxB, out float lastTimeB) && currentTime - lastTimeB < sewCooldown) 
                continue;

            // Thực hiện khâu và lưu vào danh sách tích lũy
            _weldLogic.AddWeldPair(idxA, idxB);
            
            _nodeCooldownsA[idxA] = currentTime;
            _nodeCooldownsB[idxB] = currentTime;

            Debug.Log($"<color=green>[Sewing]</color> Khâu: A({idxA}) ↔ B({idxB})");
        }
    }

    private List<GameObject> FindTouchingClothObjects()
    {
        var result = new List<GameObject>();
        Collider[] cols = Physics.OverlapSphere(sewer.transform.position, sewRadius * 2f);
        foreach (var c in cols)
        {
            if (c.CompareTag(clothTag))
            {
                var uc = c.GetComponent<UCloth.UCCloth>();
                if (uc != null && !result.Contains(c.gameObject)) result.Add(c.gameObject);
            }
        }
        return result;
    }

    private List<int> GetAllSimIndicesInRadius(GameObject obj, Vector3 center, float radius)
    {
        var result = new List<int>();
        var uc = obj.GetComponent<UCloth.UCCloth>();
        if (uc?.simData == null || !uc.simData.positionsReadOnly.IsCreated) return result;

        var positions = uc.simData.positionsReadOnly;
        float rSqr = radius * radius;
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
        
        var positions = uc.simData.positionsReadOnly;
        float maxSqr = maxRadius * maxRadius;
        float minSqr = float.MaxValue;
        int best = -1;

        for (int i = 0; i < positions.Length; i++)
        {
            float sqrDist = Vector3.SqrMagnitude((Vector3)positions[i] - targetWorldPos);
            if (sqrDist < minSqr && sqrDist < maxSqr) { minSqr = sqrDist; best = i; }
        }
        return best;
    }
}