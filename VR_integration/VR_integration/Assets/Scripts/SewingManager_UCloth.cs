// ============================================================
//  SewingManager_UCloth.cs  — v12.0 (Raycast-Only, UserStudy B/C)
// ============================================================

using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using System.Linq;

public class SewingManager_UCloth : MonoBehaviour
{
    [Header("References")]
    public GameObject sewer;
    public CuttingManager_UCloth cuttingManager;

    [Header("Ray Settings")]
    public float rayLength    = 5f;
    public bool  showDebugRay = true;

    [Header("Sewing Settings")]
    public string clothTag        = "Cloth";
    public float weldThreshold   = 0.008f;
    public float sewRadius        = 0.04f;
    public int   edgesPerFrame    = 5;
    public float minVisualSpacing = 0.01f;

    // ── User Study Keys ──
    [Header("User Study")]
    public KeyCode userStudyStartKey = KeyCode.B;
    public KeyCode userStudyEndKey   = KeyCode.C;

    private MeshSewer_UCloth _sewerSession;
    private GameObject _targetObjA;
    private GameObject _targetObjB;

    private readonly List<Vector3> _currentStrokePoints = new List<Vector3>();
    private Vector3 _averageStrokeForward = Vector3.forward;

    // ── Ray-based continuous sew tracking ──
    private bool _isRayOnCloth = false;

    private string _uiDisplayMessage = "";
    private float _uiMessageTimer = 0f;

    // ── DATA METRICS FOR USER STUDY ──
    private bool _isCollectingStudyData = false;
    private float _modeStartTime;
    private int _fpsFrameCount;
    private float _fpsAccumulatedTime;
    private List<float> _precisionErrors = new List<float>();

    void Start()
    {
        var clothObjects = GameObject.FindGameObjectsWithTag(clothTag);
        foreach (var obj in clothObjects)
            RegisterClothObject(obj);

        if (sewer == null)
            Debug.LogWarning("[SewingManager] Chưa gán sewer GameObject.");

        Debug.Log("<color=cyan>[SewingManager]</color> Hệ thống sẵn sàng.");
    }

    void LateUpdate()
    {
        if (sewer == null) return;

        // ── User Study toggle ──
        if (Input.GetKeyDown(userStudyStartKey) && !_isCollectingStudyData)
        {
            _isCollectingStudyData  = true;
            _modeStartTime          = Time.time;
            _fpsFrameCount          = 0;
            _fpsAccumulatedTime     = 0f;
            _precisionErrors.Clear();
            Debug.Log("<color=yellow>[UserStudy-Sew]</color> Bắt đầu thu thập dữ liệu.");
        }

        if (Input.GetKeyDown(userStudyEndKey) && _isCollectingStudyData)
        {
            ExportUserStudyData();
            _isCollectingStudyData = false;
            Debug.Log("<color=green>[UserStudy-Sew]</color> Kết thúc thu thập dữ liệu.");
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

        // ── Raycast từ sewer liên tục ──
        Ray ray = new Ray(sewer.transform.position, sewer.transform.forward);
        if (showDebugRay) Debug.DrawRay(sewer.transform.position, sewer.transform.forward * rayLength, Color.cyan);

        bool hitCloth = false;
        if (Physics.Raycast(ray, out RaycastHit hit, rayLength))
        {
            GameObject hitObj = hit.collider.gameObject;
            if (hitObj.CompareTag(clothTag) && hitObj.GetComponent<UCloth.UCCloth>() != null)
            {
                hitCloth = true;

                // Khởi tạo session nếu chưa có
                if (!_isRayOnCloth || _sewerSession == null)
                {
                    _isRayOnCloth = true;
                    if (_sewerSession == null)
                        TryInitializeSewingSession(hit.point);
                }

                // Thêm điểm stroke
                Vector3 hitPoint = hit.point;
                if (_currentStrokePoints.Count == 0 ||
                    Vector3.Distance(_currentStrokePoints[_currentStrokePoints.Count - 1], hitPoint) > minVisualSpacing)
                {
                    _currentStrokePoints.Add(hitPoint);
                    _averageStrokeForward = Vector3.Lerp(_averageStrokeForward, sewer.transform.forward, 0.1f);
                    InjectPointToLiveMerge(hitPoint);
                }
            }
        }

        if (!hitCloth && _isRayOnCloth)
        {
            // Ray rời khỏi cloth → commit sew
            _isRayOnCloth = false;
            FinalizeSewingSession();
        }

        if (_sewerSession != null)
            _sewerSession.Sew();
    }

    private bool TryInitializeSewingSession(Vector3 startPoint)
    {
        _currentStrokePoints.Clear();
        _averageStrokeForward = sewer.transform.forward;

        HashSet<GameObject> targetClothes = FindClothesNearSewer();
        if (targetClothes.Count == 0) return false;

        if (targetClothes.Count >= 2)
        {
            var list = targetClothes.ToList();
            _targetObjA = list[0]; _targetObjB = list[1];
        }
        else
        {
            _targetObjA = targetClothes.First(); _targetObjB = _targetObjA;
        }

        _sewerSession = new MeshSewer_UCloth(_targetObjA, _targetObjB, weldThreshold, edgesPerFrame);
        _sewerSession.OnSeamCompleted = RegisterSewnMesh;

        bool initSuccess = _sewerSession.Initialize(startPoint, startPoint, sewRadius);

        if (!initSuccess)
        {
            Debug.LogWarning("[SewingManager] Không thể khởi tạo MeshSewer.");
            _sewerSession = null;
            _targetObjA = _targetObjB = null;
        }

        return initSuccess;
    }

    private void InjectPointToLiveMerge(Vector3 rawPoint)
    {
        if (_sewerSession == null) return;

        List<Vector3> singlePoint = new List<Vector3> { rawPoint };
        List<Vector3> projA = ProjectStrokeOntoMesh(_targetObjA, singlePoint);
        List<Vector3> projB = ProjectStrokeOntoMesh(_targetObjB, singlePoint);

        if (projA != null && projA.Count > 0 && projB != null && projB.Count > 0)
        {
            if (_isCollectingStudyData)
            {
                Vector3 midSeamPoint = (projA[0] + projB[0]) * 0.5f;
                _precisionErrors.Add(Vector3.Distance(rawPoint, midSeamPoint));
            }

            _sewerSession.AddSeam(projA[0], projB[0], sewRadius);
            _sewerSession.UpdateLiveVisuals();
        }
    }

    private void FinalizeSewingSession()
    {
        if (_sewerSession == null) return;

        _currentStrokePoints.Clear();

        while (!_sewerSession.Sew()) { }

        string newMeshName = $"{_targetObjA.name}_InteractiveMergedCloth";
        _sewerSession.Finalize(newMeshName);

        _sewerSession   = null;
        _targetObjA = _targetObjB = null;
    }

    private HashSet<GameObject> FindClothesNearSewer()
    {
        HashSet<GameObject> clothes = new HashSet<GameObject>();
        for (int i = -3; i <= 3; i++)
        {
            Vector3 dir = Quaternion.Euler(0, i * 4f, 0) * sewer.transform.forward;
            Ray r = new Ray(sewer.transform.position, dir);
            foreach (var h in Physics.RaycastAll(r, rayLength))
            {
                var go = h.collider.gameObject;
                if (go.CompareTag(clothTag) && go.GetComponent<UCloth.UCCloth>() != null)
                {
                    clothes.Add(go);
                    if (clothes.Count >= 2) return clothes;
                }
            }
        }
        return clothes;
    }

    private List<Vector3> ProjectStrokeOntoMesh(GameObject target, List<Vector3> strokePoints)
    {
        var intersectPoints = new List<Vector3>();
        var collider = target.GetComponent<Collider>();
        if (collider == null) return null;

        float backupDistance = 10f;
        float totalScanRange = 20f;
        Vector3 projectDir   = _averageStrokeForward.normalized;

        for (int i = 0; i < strokePoints.Count; i++)
        {
            Vector3 origin    = strokePoints[i];
            Vector3 rayOrigin = origin - projectDir * backupDistance;
            Ray projectionRay = new Ray(rayOrigin, projectDir);

            if (collider.Raycast(projectionRay, out RaycastHit hitForward, totalScanRange))
                intersectPoints.Add(hitForward.point);
        }
        return intersectPoints;
    }

    private void ExportUserStudyData()
    {
        float duration          = Time.time - _modeStartTime;
        float avgFps            = _fpsAccumulatedTime > 0f ? (_fpsFrameCount / _fpsAccumulatedTime) : 0f;
        float avgPrecisionError = _precisionErrors.Count > 0 ? _precisionErrors.Average() : 0f;

        int fileIndex = 1;
        string fileName = "";
        do
        {
            fileName = Path.Combine(Application.persistentDataPath, $"userStudy_sew_{fileIndex:D3}.csv");
            fileIndex++;
        } while (File.Exists(fileName));

        try
        {
            using (StreamWriter sw = new StreamWriter(fileName))
            {
                sw.WriteLine("Experiment Name,Average FPS,Time (s),Sew Precision Error (m)");
                sw.WriteLine($"sew,{avgFps:F2},{duration:F3},{avgPrecisionError:F4}");
            }
            Debug.Log($"<color=green>[UserStudy]</color> Đã xuất: {fileName}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[UserStudy] Lỗi xuất CSV: {e.Message}");
        }
    }

    public void RegisterClothObject(GameObject obj)
    {
        if (obj == null) return;
        if (!obj.CompareTag(clothTag)) obj.tag = clothTag;
    }

    private void RegisterSewnMesh(GameObject sewn)
    {
        if (sewn == null) return;
        RegisterClothObject(sewn);
        if (cuttingManager == null) cuttingManager = FindObjectOfType<CuttingManager_UCloth>();
        if (cuttingManager != null) cuttingManager.RegisterClothObject(sewn);
    }

    private void OnGUI()
    {
        if (string.IsNullOrEmpty(_uiDisplayMessage)) return;

        GUIStyle style = new GUIStyle();
        style.alignment = TextAnchor.MiddleCenter;
        style.fontSize  = 28;

        style.normal.textColor = Color.black;
        GUI.Label(new Rect(Screen.width / 2 - 298, Screen.height - 102, 600, 50), _uiDisplayMessage, style);
        GUI.Label(new Rect(Screen.width / 2 - 302, Screen.height - 98, 600, 50), _uiDisplayMessage, style);

        style.normal.textColor = Color.cyan;
        GUI.Label(new Rect(Screen.width / 2 - 300, Screen.height - 100, 600, 50), _uiDisplayMessage, style);
    }
}
