// ============================================================
//  CuttingManager_UCloth.cs  — v16.0 (Raycast-Only, No Visual)
// ============================================================

using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using System.Linq;

public class CuttingManager_UCloth : MonoBehaviour
{
    [Header("References")]
    public Transform cutter;

    [Header("Ray Settings")]
    public float rayLength = 3f;
    public bool showDebugRay = true;

    [Header("Cut Settings")]
    public string clothTag = "Cloth";
    public float splitForce = 1.5f;
    public bool splitOnlyWhenDisconnected = true;
    public float minVisualSpacing = 0.01f;
    public float minCutPathLength = 0.04f;

    [Header("User Study")]
    public KeyCode userStudyStartKey = KeyCode.B;
    public KeyCode userStudyEndKey   = KeyCode.C;

    private readonly Dictionary<GameObject, MeshCutter_UCloth> _cutters = new Dictionary<GameObject, MeshCutter_UCloth>();
    private readonly HashSet<GameObject> _ucClothObjects = new HashSet<GameObject>();

    private float _rescanTimer;
    public float rescanInterval = 1f;

    // ── Ray-based stroke ──
    private readonly List<Vector3> _strokePoints = new List<Vector3>();
    private Vector3 _averageStrokeForward = Vector3.forward;
    private GameObject _currentRayTarget = null;
    private bool _isRayOnCloth = false;

    // ── User Study ──
    private bool _isCollectingStudyData = false;
    private float _modeStartTime;
    private int _fpsFrameCount;
    private float _fpsAccumulatedTime;
    private List<float> _precisionErrors = new List<float>();

    void Start()
    {
        if (cutter == null)
        {
            Debug.LogError("[CuttingManager] Chưa gán Cutter transform!");
            enabled = false;
            return;
        }
        RegisterAllClothObjects();
    }

    void LateUpdate()
    {
        if (cutter == null) return;

        // User Study toggle
        if (Input.GetKeyDown(userStudyStartKey) && !_isCollectingStudyData)
        {
            _isCollectingStudyData = true;
            _modeStartTime         = Time.time;
            _fpsFrameCount         = 0;
            _fpsAccumulatedTime    = 0f;
            _precisionErrors.Clear();
            Debug.Log("<color=yellow>[UserStudy-Cut]</color> Bắt đầu thu thập dữ liệu.");
        }
        if (Input.GetKeyDown(userStudyEndKey) && _isCollectingStudyData)
        {
            ExportUserStudyData();
            _isCollectingStudyData = false;
            Debug.Log("<color=green>[UserStudy-Cut]</color> Kết thúc thu thập dữ liệu.");
        }
        if (_isCollectingStudyData)
        {
            _fpsFrameCount++;
            _fpsAccumulatedTime += Time.unscaledDeltaTime;
        }

        // Raycast từ cutter
        Ray ray = new Ray(cutter.position, cutter.forward);
        if (showDebugRay) Debug.DrawRay(cutter.position, cutter.forward * rayLength, Color.red);

        if (Physics.Raycast(ray, out RaycastHit hit, rayLength))
        {
            GameObject hitObj = hit.collider.gameObject;

            if (hitObj.CompareTag(clothTag) && _cutters.ContainsKey(hitObj))
            {
                // Chuyển sang cloth object mới → commit cut cũ trước
                if (_isRayOnCloth && _currentRayTarget != hitObj)
                    TryCommitCut(_currentRayTarget);

                _isRayOnCloth     = true;
                _currentRayTarget = hitObj;

                // Tích lũy điểm stroke
                Vector3 p = hit.point;
                if (_strokePoints.Count == 0 ||
                    Vector3.Distance(_strokePoints[_strokePoints.Count - 1], p) > minVisualSpacing)
                {
                    _strokePoints.Add(p);
                    _averageStrokeForward = Vector3.Lerp(_averageStrokeForward, cutter.forward, 0.2f);
                }
            }
            else
            {
                if (_isRayOnCloth)
                {
                    TryCommitCut(_currentRayTarget);
                    _isRayOnCloth     = false;
                    _currentRayTarget = null;
                }
            }
        }
        else
        {
            if (_isRayOnCloth)
            {
                TryCommitCut(_currentRayTarget);
                _isRayOnCloth     = false;
                _currentRayTarget = null;
            }
        }

        CleanupStaleObjects();
        RescanForNewClothObjects();
    }

    private void TryCommitCut(GameObject target)
    {
        if (target == null || _strokePoints.Count < 2) { _strokePoints.Clear(); return; }

        float totalLength = 0f;
        for (int i = 1; i < _strokePoints.Count; i++)
            totalLength += Vector3.Distance(_strokePoints[i - 1], _strokePoints[i]);

        if (totalLength < minCutPathLength) { _strokePoints.Clear(); return; }

        if (!_cutters.TryGetValue(target, out var mc) || mc == null) { _strokePoints.Clear(); return; }

        List<Vector3> projected = ProjectStrokeOntoMesh(target, _strokePoints);

        if (projected != null && projected.Count >= 2)
        {
            if (_isCollectingStudyData)
            {
                int n = Mathf.Min(_strokePoints.Count, projected.Count);
                for (int i = 0; i < n; i++)
                    _precisionErrors.Add(Vector3.Distance(_strokePoints[i], projected[i]));
            }

            mc.ClearPath();
            foreach (var p in projected) mc.ForceAddPathPoint(p);

            var result = mc.CommitCut(splitOnlyWhenDisconnected);
            if (result == CutResult_Ucloth.Split)
            {
                var newPieces = mc.GetLastCreatedPieces();
                target.SetActive(false);
                _cutters.Remove(target);
                _ucClothObjects.Remove(target);
                foreach (var piece in newPieces) RegisterClothObject(piece);
            }
        }

        _strokePoints.Clear();
    }

    private List<Vector3> ProjectStrokeOntoMesh(GameObject target, List<Vector3> strokePoints)
    {
        var result  = new List<Vector3>();
        var collider = target.GetComponent<Collider>();
        if (collider == null) return null;

        float backupDistance = 20f;
        float totalScanRange = 40f;
        Vector3 projectDir   = _averageStrokeForward.normalized;
        if (Camera.main != null) projectDir = Camera.main.transform.forward;

        foreach (var origin in strokePoints)
        {
            Ray fwd = new Ray(origin - projectDir * backupDistance, projectDir);
            if (collider.Raycast(fwd, out RaycastHit h, totalScanRange))
            { result.Add(h.point); continue; }

            Ray rev = new Ray(origin + projectDir * backupDistance, -projectDir);
            if (collider.Raycast(rev, out RaycastHit h2, totalScanRange))
                result.Add(h2.point);
        }

        // Lọc điểm quá gần nhau
        var filtered = new List<Vector3>();
        foreach (var p in result)
        {
            if (filtered.Count == 0 || Vector3.Distance(filtered[filtered.Count - 1], p) > minVisualSpacing * 0.4f)
                filtered.Add(p);
        }

        // Extend hai đầu để đảm bảo cut xuyên qua biên
        if (filtered.Count >= 2)
        {
            Bounds b      = collider.bounds;
            float offset  = Mathf.Clamp(Mathf.Max(b.size.x, b.size.y, b.size.z) * 0.2f, 0.01f, 0.3f);
            Vector3 sDir  = (filtered[1] - filtered[0]).normalized;
            Vector3 eDir  = (filtered[filtered.Count - 1] - filtered[filtered.Count - 2]).normalized;
            filtered.Insert(0, filtered[0] - sDir * offset);
            filtered.Add(filtered[filtered.Count - 1] + eDir * offset);
        }

        return filtered;
    }

    private void ExportUserStudyData()
    {
        float duration          = Time.time - _modeStartTime;
        float avgFps            = _fpsAccumulatedTime > 0f ? _fpsFrameCount / _fpsAccumulatedTime : 0f;
        float avgPrecisionError = _precisionErrors.Count > 0 ? _precisionErrors.Average() : 0f;

        int idx = 1; string fileName;
        do { fileName = Path.Combine(Application.persistentDataPath, $"userStudy_cut_{idx++:D3}.csv"); }
        while (File.Exists(fileName));

        try
        {
            using (var sw = new StreamWriter(fileName))
            {
                sw.WriteLine("Experiment Name,Average FPS,Time (s),Cut Precision Error (m)");
                sw.WriteLine($"cut,{avgFps:F2},{duration:F3},{avgPrecisionError:F4}");
            }
            Debug.Log($"<color=green>[UserStudy]</color> Đã xuất: {fileName}");
        }
        catch (System.Exception e) { Debug.LogError($"[UserStudy] Lỗi xuất CSV: {e.Message}"); }
    }

    public void RegisterAllClothObjects()
    {
        foreach (var obj in GameObject.FindGameObjectsWithTag(clothTag)) RegisterClothObject(obj);
    }

    public void RegisterClothObject(GameObject obj)
    {
        if (obj == null || _cutters.ContainsKey(obj)) return;
        if (obj.GetComponent<MeshFilter>() == null) return;

        var mc = new MeshCutter_UCloth(obj, splitForce);
        mc._minPathPointSpacingOverride = minVisualSpacing;
        mc.Initialize();
        _cutters[obj] = mc;

        if (obj.GetComponent<UCloth.UCCloth>() != null)
        {
            _ucClothObjects.Add(obj);
            StartCoroutine(UpgradeCutterToSimSpace(obj));
        }
    }

    private static bool IsSimDataFinite(UCloth.UCCloth ucCloth)
    {
        if (ucCloth?.simData == null || !ucCloth.simData.positionsReadOnly.IsCreated) return false;
        var sim = ucCloth.simData.positionsReadOnly;
        if (sim.Length == 0) return false;
        for (int i = 0; i < sim.Length; i++)
        {
            var p = sim[i];
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) ||
                float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z))
                return false;
        }
        return true;
    }

    private IEnumerator UpgradeCutterToSimSpace(GameObject obj)
    {
        var ucCloth = obj.GetComponent<UCloth.UCCloth>();
        if (ucCloth == null) yield break;
        float timeout = 1.5f;
        while (timeout > 0f)
        {
            if (obj == null) yield break;
            if (ucCloth.simData != null && ucCloth.simData.positionsReadOnly.IsCreated)
            {
                yield return new WaitForSeconds(0.05f);
                if (obj == null) yield break;
                if (IsSimDataFinite(ucCloth)) break;
            }
            timeout -= Time.deltaTime;
            yield return null;
        }
        if (obj == null || !_cutters.ContainsKey(obj)) yield break;
        _cutters[obj].Initialize();
    }

    private void CleanupStaleObjects()
    {
        var toRemove = new List<GameObject>();
        foreach (var kv in _cutters) if (kv.Key == null || !kv.Key.activeSelf) toRemove.Add(kv.Key);
        foreach (var obj in toRemove) { _cutters.Remove(obj); _ucClothObjects.Remove(obj); }
    }

    private void RescanForNewClothObjects()
    {
        _rescanTimer -= Time.deltaTime;
        if (_rescanTimer > 0f) return;
        _rescanTimer = rescanInterval;
        foreach (var obj in GameObject.FindGameObjectsWithTag(clothTag))
        {
            if (obj == null || _cutters.ContainsKey(obj)) continue;
            RegisterClothObject(obj);
        }
    }
}
