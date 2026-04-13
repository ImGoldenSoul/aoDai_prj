using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// CuttingManager tự động tìm tất cả GameObject có tag 'cloth' làm target.
/// Khi cắt thành công, các mảnh được tạo ra vẫn có UCCloth + tag 'cloth'.
/// </summary>
public class CuttingManager_UCloth : MonoBehaviour
{
    [Header("References")]
    public GameObject cutter;

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
    private Collider _cutterCol;
    private int      _frameCount;

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

        _cutterCol = cutter.GetComponent<Collider>();
        if (_cutterCol == null)
        {
            Debug.LogError("[CuttingManager_UCloth] Cutter thiếu Collider!");
            enabled = false;
            return;
        }

        // Tìm tất cả cloth objects hiện có và đăng ký
        RegisterAllClothObjects();
    }

    /// <summary>
    /// Tìm tất cả GameObject có tag clothTag và tạo MeshCutter cho từng cái.
    /// </summary>
    private void RegisterAllClothObjects()
    {
        var clothObjects = GameObject.FindGameObjectsWithTag(clothTag);
        foreach (var obj in clothObjects)
            RegisterClothObject(obj);

        Debug.Log($"[CuttingManager_UCloth] Đã đăng ký {_cutters.Count} cloth object(s).");
    }

    /// <summary>
    /// Đăng ký 1 cloth object: chờ UCCloth init xong rồi tạo MeshCutter.
    /// </summary>
    public void RegisterClothObject(GameObject obj)
    {
        if (obj == null || _cutters.ContainsKey(obj)) return;
        if (obj.GetComponent<MeshFilter>() == null) return;

        // Đánh dấu slot ngay để tránh đăng ký 2 lần
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

        if (obj == null) yield break; // object bị destroy trong lúc chờ

        var meshCutter = new MeshCutter_UCloth(obj, splitForce);

        // Đợi thêm 1 frame cuối để UCCloth render loop chạy ít nhất 1 lần
        yield return new WaitForEndOfFrame();

        if (obj == null) yield break;

        meshCutter.Initialize();
        _cutters[obj] = meshCutter;
        Debug.Log($"[CuttingManager_UCloth] ✓ Sẵn sàng cắt: {obj.name}");
    }

    void LateUpdate()
    {
        _frameCount++;
        if (_frameCount % checkInterval != 0) return;

        // Duyệt qua tất cả registered targets
        var toRemove = new List<GameObject>();
        var toAdd    = new List<GameObject>();

        foreach (var kv in _cutters)
        {
            GameObject obj    = kv.Key;
            var        cutter = kv.Value;

            // Đã bị destroy hoặc inactive
            if (obj == null || !obj.activeSelf)
            {
                toRemove.Add(obj);
                continue;
            }

            // Cutter chưa init xong
            if (cutter == null) continue;

            // Cheap bounds check
            var targetCol = obj.GetComponent<Collider>();
            if (targetCol != null && !_cutterCol.bounds.Intersects(targetCol.bounds))
                continue;

            var result = cutter.PerformCut(_cutterCol, splitOnlyWhenDisconnected);

            if (result == CutResult_Ucloth.Split)
            {
                // Lấy danh sách pieces vừa được tạo ra và đăng ký chúng
                var newPieces = cutter.GetLastCreatedPieces();
                foreach (var piece in newPieces)
                    toAdd.Add(piece);

                obj.SetActive(false);
                toRemove.Add(obj);
                Debug.Log($"[CuttingManager_UCloth] ✓ {obj.name} đã tách.");
            }
            else if (result == CutResult_Ucloth.Trimmed)
            {
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

    void OnDrawGizmos()
    {
        if (cutter == null) return;
        Gizmos.color = new Color(1f, 0.3f, 0f, 0.3f);
        Gizmos.matrix = cutter.transform.localToWorldMatrix;
        var col = cutter.GetComponent<Collider>();
        if (col is BoxCollider bc)   Gizmos.DrawCube(bc.center, bc.size);
        else if (col is SphereCollider sc) Gizmos.DrawSphere(sc.center, sc.radius);
    }
}