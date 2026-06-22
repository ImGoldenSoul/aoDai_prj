// ============================================================
//  CuttingManager_UCloth.cs  — v15.0 (Raycast-Only, UserStudy B/C)
// ============================================================

using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
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

    [Header("Cut Line Visual")]
    public Material cutLineMaterial;
    public float cutLineWidth = 0.004f;
    public Color cutLineColor = new Color(1f, 0.15f, 0.05f, 1f);

    // ── User Study Keys ──
    [Header("User Study")]
    public KeyCode userStudyStartKey = KeyCode.B;
    public KeyCode userStudyEndKey   = KeyCode.C;

    private LineRenderer _cutLine;
    private readonly List<Vector3> _visualStrokePoints = new List<Vector3>();
    private Vector3 _averageStrokeForward = Vector3.forward;

    private readonly Dictionary<GameObject, MeshCutter_UCloth> _cutters = new Dictionary<GameObject, MeshCutter_UCloth>();
    private readonly HashSet<GameObject> _ucClothObjects = new HashSet<GameObject>();

    private float _rescanTimer;
    public float rescanInterval = 1f;

    private string _uiDisplayMessage = "";
    private float _uiMessageTimer = 0f;

    // ── Ray-based continuous cut tracking ──
    private GameObject _currentRayTarget = null;   // cloth object đang bị ray chiếu vào
    private bool _isRayOnCloth = false;

    // ── DATA METRICS FOR USER STUDY ──
    private bool _isCollectingStudyData = false;
    private float _modeStartTime;
    private int _fpsFrameCount;
    private float _fpsAccumulatedTime;
    private List<float> _precisionErrors = new List<float>();

    void Start()
    {
        if (cutter == null)
        {
            Debug.LogError("[CuttingManager_UCloth] Chưa gán Cutter transform!");
            enabled = false;
            return;
        }

        RegisterAllClothObjects();
        SetupCutLineVisual();
    }

    private void SetupCutLineVisual()
    {
        var lineGo = new GameObject("[3DStrokeVisual]");
        lineGo.transform.SetParent(transform, false);
        _cutLine = lineGo.AddComponent<LineRenderer>();
        _cutLine.useWorldSpace = true;
        _cutLine.positionCount = 0;
        _cutLine.widthMultiplier = cutLineWidth;
        _cutLine.numCapVertices = 4;
        _cutLine.numCornerVertices = 4;
        _cutLine.alignment = LineAlignment.View;
        _cutLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _cutLine.receiveShadows = false;

        if (cutLineMaterial == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", cutLineColor);
            _cutLine.material = mat;
        }
        else
        {
            _cutLine.material = cutLineMaterial;
        }
        _cutLine.startColor = cutLineColor;
        _cutLine.endColor   = cutLineColor;
        _cutLine.enabled    = false;
    }

    void LateUpdate()
    {
        if (cutter == null) return;

        // ── User Study toggle ──
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

        if (_uiMessageTimer > 0f)
        {
            _uiMessageTimer -= Time.deltaTime;
            if (_uiMessageTimer <= 0f) _uiDisplayMessage = "";
        }

        // ── Raycast từ cutter liên tục ──
        Ray ray = new Ray(cutter.position, cutter.forward);
        if (showDebugRay) Debug.DrawRay(cutter.position, cutter.forward * rayLength, Color.red);

        if (Physics.Raycast(ray, out RaycastHit hit, rayLength))
        {
            GameObject hitObj = hit.collider.gameObject;

            if (hitObj.CompareTag(clothTag) && _cutters.ContainsKey(hitObj))
            {
                // Nếu bắt đầu chạm cloth mới → reset stroke
                if (!_isRayOnCloth || _currentRayTarget != hitObj)
                {
                    if (_isRayOnCloth && _currentRayTarget != hitObj)
                        TryCommitCut(_currentRayTarget);

                    _isRayOnCloth      = true;
                    _currentRayTarget  = hitObj;
                    _averageStrokeForward = cutter.forward;
                }

                // Thêm điểm vào stroke
                Vector3 hitPoint = hit.point;
                if (_visualStrokePoints.Count == 0 ||
                    Vector3.Distance(_visualStrokePoints[_visualStrokePoints.Count - 1], hitPoint) > minVisualSpacing)
                {
                    _visualStrokePoints.Add(hitPoint);
                    _averageStrokeForward = Vector3.Lerp(_averageStrokeForward, cutter.forward, 0.2f);
                }

                UpdateVisualLineRenderer();
            }
            else
            {
                // Ray chạm vật khác → kết thúc stroke
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
            // Không chạm gì → kết thúc stroke
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
        if (target == null || _visualStrokePoints.Count < 2)
        {
            ClearStroke();
            return;
        }

        float totalLength = 0f;
        for (int i = 1; i < _visualStrokePoints.Count; i++)
            totalLength += Vector3.Distance(_visualStrokePoints[i - 1], _visualStrokePoints[i]);

        if (totalLength < minCutPathLength)
        {
            ClearStroke();
            return;
        }

        if (!_cutters.TryGetValue(target, out var mc) || mc == null)
        {
            ClearStroke();
            return;
        }

        List<Vector3> projectedPath = ProjectStrokeOntoMesh(target, _visualStrokePoints);

        if (projectedPath != null && projectedPath.Count >= 2)
        {
            // Precision error
            if (_isCollectingStudyData)
            {
                int validCount = Mathf.Min(_visualStrokePoints.Count, projectedPath.Count);
                for (int i = 0; i < validCount; i++)
                    _precisionErrors.Add(Vector3.Distance(_visualStrokePoints[i], projectedPath[i]));
            }

            mc.ClearPath();
            foreach (var point in projectedPath) mc.ForceAddPathPoint(point);

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

        ClearStroke();
    }

    private void ClearStroke()
    {
        _visualStrokePoints.Clear();
        if (_cutLine != null) _cutLine.enabled = false;
    }

    private void UpdateVisualLineRenderer()
    {
        if (_cutLine == null) return;
        if (_visualStrokePoints.Count < 2) { _cutLine.enabled = false; return; }
        _cutLine.enabled      = true;
        _cutLine.positionCount = _visualStrokePoints.Count;
        _cutLine.SetPositions(_visualStrokePoints.ToArray());
    }

    private List<Vector3> ProjectStrokeOntoMesh(GameObject target, List<Vector3> strokePoints)
    {
        var intersectPoints = new List<Vector3>();
        var collider = target.GetComponent<Collider>();
        if (collider == null) return null;

        float backupDistance = 20f;
        float totalScanRange = 40f;

        Vector3 projectDir = _averageStrokeForward.normalized;
        if (Camera.main != null) projectDir = Camera.main.transform.forward;

        for (int i = 0; i < strokePoints.Count; i++)
        {
            Vector3 origin    = strokePoints[i];
            Vector3 rayOrigin = origin - projectDir * backupDistance;
            Ray projectionRay = new Ray(rayOrigin, projectDir);

            if (collider.Raycast(projectionRay, out RaycastHit hitForward, totalScanRange))
            {
                intersectPoints.Add(hitForward.point);
            }
            else
            {
                Ray reverseRay = new Ray(origin + projectDir * backupDistance, -projectDir);
                if (collider.Raycast(reverseRay, out RaycastHit hitReverse, totalScanRange))
                    intersectPoints.Add(hitReverse.point);
            }
        }

        var filteredPoints = new List<Vector3>();
        for (int i = 0; i < intersectPoints.Count; i++)
        {
            if (filteredPoints.Count == 0) filteredPoints.Add(intersectPoints[i]);
            else if (Vector3.Distance(filteredPoints[filteredPoints.Count - 1], intersectPoints[i]) > minVisualSpacing * 0.4f)
                filteredPoints.Add(intersectPoints[i]);
        }

        if (filteredPoints.Count >= 2)
        {
            Bounds targetBounds = collider.bounds;
            float maxExtent     = Mathf.Max(targetBounds.size.x, targetBounds.size.y, targetBounds.size.z);
            float adaptiveOffset = Mathf.Clamp(maxExtent * 0.2f, 0.01f, 0.3f);

            Vector3 startDir = (filteredPoints[1] - filteredPoints[0]).normalized;
            Vector3 endDir   = (filteredPoints[filteredPoints.Count - 1] - filteredPoints[filteredPoints.Count - 2]).normalized;

            filteredPoints.Insert(0, filteredPoints[0] - startDir * adaptiveOffset);
            filteredPoints.Add(filteredPoints[filteredPoints.Count - 1] + endDir * adaptiveOffset);
        }

        return filteredPoints;
    }

    private void ExportUserStudyData()
    {
        float duration         = Time.time - _modeStartTime;
        float avgFps           = _fpsAccumulatedTime > 0f ? (_fpsFrameCount / _fpsAccumulatedTime) : 0f;
        float avgPrecisionError = _precisionErrors.Count > 0 ? _precisionErrors.Average() : 0f;

        int fileIndex = 1;
        string fileName = "";
        do
        {
            fileName = Path.Combine(Application.persistentDataPath, $"userStudy_cut_{fileIndex:D3}.csv");
            fileIndex++;
        } while (File.Exists(fileName));

        try
        {
            using (StreamWriter sw = new StreamWriter(fileName))
            {
                sw.WriteLine("Experiment Name,Average FPS,Time (s),Cut Precision Error (m)");
                sw.WriteLine($"cut,{avgFps:F2},{duration:F3},{avgPrecisionError:F4}");
            }
            Debug.Log($"<color=green>[UserStudy]</color> Đã xuất: {fileName}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[UserStudy] Lỗi xuất CSV: {e.Message}");
        }
    }

    public void RegisterAllClothObjects()
    {
        foreach (var obj in GameObject.FindGameObjectsWithTag(clothTag)) RegisterClothObject(obj);
    }

    public void RegisterClothObject(GameObject obj)
    {
        if (obj == null || _cutters.ContainsKey(obj)) return;
        if (obj.GetComponent<MeshFilter>() == null) return;

        var instantCutter = new MeshCutter_UCloth(obj, splitForce);
        instantCutter._minPathPointSpacingOverride = minVisualSpacing;
        instantCutter.Initialize();
        _cutters[obj] = instantCutter;

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

    private void OnGUI()
    {
        if (string.IsNullOrEmpty(_uiDisplayMessage)) return;

        GUIStyle style = new GUIStyle();
        style.alignment = TextAnchor.MiddleCenter;
        style.fontSize  = 28;

        style.normal.textColor = Color.black;
        GUI.Label(new Rect(Screen.width / 2 - 298, Screen.height - 152, 600, 50), _uiDisplayMessage, style);
        GUI.Label(new Rect(Screen.width / 2 - 302, Screen.height - 148, 600, 50), _uiDisplayMessage, style);

        style.normal.textColor = Color.red;
        GUI.Label(new Rect(Screen.width / 2 - 300, Screen.height - 150, 600, 50), _uiDisplayMessage, style);
    }
}
