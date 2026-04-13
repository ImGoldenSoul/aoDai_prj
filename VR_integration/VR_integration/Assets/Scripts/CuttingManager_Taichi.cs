using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// CuttingManager cho miếng vải mô phỏng vật lý bằng Taichi.
/// Mirror hoàn toàn CuttingManager_UCloth — tự động tìm tất cả GameObject có tag 'Cloth'.
///
/// Khi cắt thành công:
///   - Piece mới được spawn có TaichiClothVR + tag 'Cloth'.
///   - CuttingManager tự detect và đăng ký chúng → có thể cắt tiếp vô hạn.
///   - Mỗi piece được gán port UDP riêng (TaichiPortAllocator) để server Python
///     chạy độc lập cho từng mảnh.
/// </summary>
public class CuttingManager_Taichi : MonoBehaviour
{
    [Header("References")]
    [Tooltip("GameObject chứa Collider dùng làm lưỡi cắt")]
    public GameObject cutter;

    [Header("Settings")]
    [Tooltip("Số frame giữa 2 lần check cắt (giảm → nhạy hơn nhưng nặng hơn)")]
    public int checkInterval = 3;

    [Tooltip("Nếu TRUE: chỉ tách khi vết cắt chia mesh thành >= 2 phần liên thông riêng biệt.")]
    public bool splitOnlyWhenDisconnected = true;

    [Tooltip("Lực đẩy các mảnh văng ra sau khi cắt (chỉ khi không có TaichiClothVR)")]
    public float splitForce = 1.5f;

    [Tooltip("Tag dùng để tìm cloth targets")]
    public string clothTag = "Cloth";

    // ── private ───────────────────────────────────────────────────────────────
    private Collider _cutterCol;
    private int      _frameCount;

    // Map: target GameObject → MeshCutter instance
    private Dictionary<GameObject, MeshCutter_Taichi> _cutters
        = new Dictionary<GameObject, MeshCutter_Taichi>();

    // =========================================================================
    void Start()
    {
        if (cutter == null)
        {
            Debug.LogError("[CuttingManager_Taichi] Chưa gán Cutter!");
            enabled = false;
            return;
        }

        _cutterCol = cutter.GetComponent<Collider>();
        if (_cutterCol == null)
        {
            Debug.LogError("[CuttingManager_Taichi] Cutter thiếu Collider!");
            enabled = false;
            return;
        }

        RegisterAllClothObjects();
    }

    // ── Đăng ký ──────────────────────────────────────────────────────────────

    /// <summary>Tìm tất cả cloth object hiện có và đăng ký.</summary>
    private void RegisterAllClothObjects()
    {
        var clothObjects = GameObject.FindGameObjectsWithTag(clothTag);
        foreach (var obj in clothObjects)
            RegisterClothObject(obj);
        Debug.Log($"[CuttingManager_Taichi] Đã đăng ký {_cutters.Count} cloth object(s).");
    }

    /// <summary>Đăng ký 1 cloth object — chờ TaichiClothVR init xong rồi tạo MeshCutter.</summary>
    public void RegisterClothObject(GameObject obj)
    {
        if (obj == null || _cutters.ContainsKey(obj)) return;
        if (obj.GetComponent<MeshFilter>() == null) return;

        _cutters[obj] = null; // đánh dấu slot ngay để tránh đăng ký 2 lần
        StartCoroutine(InitCutterForObject(obj));
    }

    private IEnumerator InitCutterForObject(GameObject obj)
    {
        // Chờ TaichiClothVR có mesh hợp lệ (vertices != null, length > 0)
        var taichiCloth = obj.GetComponent<TaichiClothVR>();
        if (taichiCloth != null)
        {
            float timeout = 5f;
            while (timeout > 0f)
            {
                var mf = obj.GetComponent<MeshFilter>();
                if (mf != null && mf.mesh != null &&
                    mf.mesh.vertexCount > 0 && mf.mesh.triangles.Length > 0)
                    break;
                timeout -= Time.deltaTime;
                yield return null;
            }
            if (timeout <= 0f)
                Debug.LogWarning($"[CuttingManager_Taichi] Timeout chờ TaichiClothVR mesh cho '{obj.name}'!");
        }
        else
        {
            yield return null; // 1 frame
        }

        if (obj == null) yield break; // object bị destroy trong lúc chờ

        var meshCutter = new MeshCutter_Taichi(obj, splitForce);

        // Chờ thêm 1 frame cuối để mesh được render ít nhất 1 lần
        yield return new WaitForEndOfFrame();
        if (obj == null) yield break;

        meshCutter.Initialize();
        _cutters[obj] = meshCutter;
        Debug.Log($"[CuttingManager_Taichi] ✓ Sẵn sàng cắt: {obj.name}");
    }

    // =========================================================================
    void LateUpdate()
    {
        _frameCount++;
        if (_frameCount % checkInterval != 0) return;

        var toRemove = new List<GameObject>();
        var toAdd    = new List<GameObject>();

        foreach (var kv in _cutters)
        {
            GameObject obj    = kv.Key;
            var        cutter = kv.Value;

            // Bị destroy hoặc inactive
            if (obj == null || !obj.activeSelf)
            {
                toRemove.Add(obj);
                continue;
            }

            // Cutter chưa init xong
            if (cutter == null) continue;

            // Cheap bounds check — bỏ qua nếu bounding box không chạm nhau
            var targetCol = obj.GetComponent<Collider>();
            if (targetCol != null && !_cutterCol.bounds.Intersects(targetCol.bounds))
                continue;

            var result = cutter.PerformCut(_cutterCol, splitOnlyWhenDisconnected);

            if (result == CutResult_Taichi.Split)
            {
                // Đăng ký các piece mới ngay lập tức để chúng cũng có thể bị cắt
                var newPieces = cutter.GetLastCreatedPieces();
                foreach (var piece in newPieces)
                    toAdd.Add(piece);

                obj.SetActive(false);
                toRemove.Add(obj);
                Debug.Log($"[CuttingManager_Taichi] ✓ '{obj.name}' đã tách thành {newPieces.Count} mảnh.");
            }
            else if (result == CutResult_Taichi.Trimmed)
            {
                // Cập nhật MeshCollider nếu có
                var mc = obj.GetComponent<MeshCollider>();
                if (mc != null)
                {
                    mc.sharedMesh = null;
                    mc.sharedMesh = obj.GetComponent<MeshFilter>().mesh;
                }
            }
        }

        foreach (var obj in toRemove) _cutters.Remove(obj);
        foreach (var obj in toAdd)    RegisterClothObject(obj);
    }

    // ── Gizmos ────────────────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (cutter == null) return;
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.3f);
        Gizmos.matrix = cutter.transform.localToWorldMatrix;
        var col = cutter.GetComponent<Collider>();
        if      (col is BoxCollider bc)    Gizmos.DrawCube(bc.center, bc.size);
        else if (col is SphereCollider sc) Gizmos.DrawSphere(sc.center, sc.radius);
        else if (col is CapsuleCollider cc)
        {
            // Approximation: vẽ sphere ở 2 đầu mút
            Vector3 axis = cc.direction == 0 ? Vector3.right
                         : cc.direction == 2 ? Vector3.forward
                         : Vector3.up;
            float half = Mathf.Max(0f, cc.height * 0.5f - cc.radius);
            Gizmos.DrawSphere(cc.center + axis * half,  cc.radius);
            Gizmos.DrawSphere(cc.center - axis * half,  cc.radius);
        }
    }
}
