using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// CuttingManager tự động tìm tất cả GameObject có tag 'cloth' làm target.
/// Khi cắt thành công, các mảnh được tạo ra vẫn có UCCloth + tag 'cloth'.
///
/// v3 – Collider-based detection:
///   Dùng Collider gắn trên cutter để xác định triangle bị cắt thay vì Ray.
///   Yêu cầu cutter phải có ít nhất một Collider (IsTrigger hay không đều được).
/// </summary>
public class CuttingManager_UCloth : MonoBehaviour
{
    [Header("References")]
    [Tooltip("GameObject của cutter – phải có Collider để phát hiện vùng cắt.")]
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
    private int _frameCount;
    private Collider _cutterCollider;

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

        _cutterCollider = cutter.GetComponent<Collider>();
        if (_cutterCollider == null)
        {
            Debug.LogError("[CuttingManager_UCloth] Cutter không có Collider!");
            enabled = false;
            return;
        }

        RegisterAllClothObjects();
    }

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

        if (_cutterCollider == null) return;

        var toRemove = new List<GameObject>();
        var toAdd    = new List<GameObject>();

        foreach (var kv in _cutters)
        {
            GameObject obj = kv.Key;
            var        mc  = kv.Value;

            if (obj == null || !obj.activeSelf)
            {
                toRemove.Add(obj);
                continue;
            }

            if (mc == null) continue;

            // ── Cheap bounds check: kiểm tra collider có giao với bounds không ──
            var renderer = obj.GetComponent<Renderer>();
            if (renderer != null && !CollidersOverlapBounds(_cutterCollider, renderer.bounds))
                continue;

            var result = mc.PerformCut(_cutterCollider, splitOnlyWhenDisconnected);

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
    /// Kiểm tra nhanh: AABB của collider cutter có overlap với bounds của cloth không.
    /// Dùng để early-out trước khi kiểm tra từng triangle.
    /// </summary>
    private static bool CollidersOverlapBounds(Collider cutterCol, Bounds clothBounds)
    {
        return cutterCol.bounds.Intersects(clothBounds);
    }

    // ── Gizmos ───────────────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (cutter == null) return;

        var col = cutter.GetComponent<Collider>();
        if (col == null) return;

        Gizmos.color = new Color(1f, 0.3f, 0f, 0.4f);
        Gizmos.DrawWireCube(col.bounds.center, col.bounds.size);
    }
}
