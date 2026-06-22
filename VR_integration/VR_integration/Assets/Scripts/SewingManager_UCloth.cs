// ============================================================
//  SewingManager_UCloth.cs  — v14.0 (Raycast-Only, Auto-Finalize on Idle)
// ============================================================

using System.Collections.Generic;
using System.IO;
using UnityEngine;
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

    [Header("Auto-Finalize")]
    public float idleTimeout = 7f;   // giây không có điểm mới → tự hoàn thành khâu

    [Header("User Study")]
    public KeyCode userStudyStartKey = KeyCode.B;
    public KeyCode userStudyEndKey   = KeyCode.C;

    private MeshSewer_UCloth _sewerSession;
    private GameObject _targetObjA;
    private GameObject _targetObjB;

    private readonly List<Vector3> _strokePoints = new List<Vector3>();
    private Vector3 _averageStrokeForward = Vector3.forward;
    private bool _isRayOnCloth = false;
    private float _idleTimer = 0f;   // đếm thời gian không có tương tác mới

    // ── User Study ──
    private bool _isCollectingStudyData = false;
    private float _modeStartTime;
    private int _fpsFrameCount;
    private float _fpsAccumulatedTime;
    private List<float> _precisionErrors = new List<float>();

    void Start()
    {
        foreach (var obj in GameObject.FindGameObjectsWithTag(clothTag))
            RegisterClothObject(obj);

        if (sewer == null)
            Debug.LogWarning("[SewingManager] Chưa gán sewer GameObject.");

        Debug.Log("<color=cyan>[SewingManager]</color> Hệ thống sẵn sàng.");
    }

    void LateUpdate()
    {
        if (sewer == null) return;

        // User Study toggle
        if (Input.GetKeyDown(userStudyStartKey) && !_isCollectingStudyData)
        {
            _isCollectingStudyData = true;
            _modeStartTime         = Time.time;
            _fpsFrameCount         = 0;
            _fpsAccumulatedTime    = 0f;
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

        // Raycast từ sewer
        Ray ray = new Ray(sewer.transform.position, sewer.transform.forward);
        if (showDebugRay) Debug.DrawRay(sewer.transform.position, sewer.transform.forward * rayLength, Color.cyan);

        bool hitCloth = false;
        if (Physics.Raycast(ray, out RaycastHit hit, rayLength))
        {
            GameObject hitObj = hit.collider.gameObject;
            if (hitObj.CompareTag(clothTag) && hitObj.GetComponent<UCloth.UCCloth>() != null)
            {
                hitCloth = true;

                if (!_isRayOnCloth || _sewerSession == null)
                {
                    _isRayOnCloth = true;
                    if (_sewerSession == null)
                        TryInitializeSewingSession(hit.point);
                }

                Vector3 p = hit.point;
                if (_strokePoints.Count == 0 ||
                    Vector3.Distance(_strokePoints[_strokePoints.Count - 1], p) > minVisualSpacing)
                {
                    _strokePoints.Add(p);
                    _averageStrokeForward = Vector3.Lerp(_averageStrokeForward, sewer.transform.forward, 0.1f);
                    InjectPointToLiveMerge(p);
                }
            }
        }

        if (!hitCloth && _isRayOnCloth)
        {
            _isRayOnCloth = false;
            FinalizeSewingSession();
        }

        if (_sewerSession != null)
        {
            _sewerSession.Sew();

            // Tự hoàn thiện khâu nếu không có tương tác mới trong idleTimeout giây
            _idleTimer += Time.deltaTime;
            if (_idleTimer >= idleTimeout)
            {
                Debug.Log("<color=cyan>[SewingManager]</color> Idle timeout — tự hoàn thành khâu.");
                _isRayOnCloth = false;
                FinalizeSewingSession();
            }
        }
    }

    private bool TryInitializeSewingSession(Vector3 startPoint)
    {
        _strokePoints.Clear();
        _averageStrokeForward = sewer.transform.forward;

        HashSet<GameObject> clothes = FindClothesNearSewer();
        if (clothes.Count == 0) return false;

        if (clothes.Count >= 2)
        {
            var list = clothes.ToList();
            _targetObjA = list[0]; _targetObjB = list[1];
        }
        else
        {
            _targetObjA = clothes.First(); _targetObjB = _targetObjA;
        }

        _sewerSession = new MeshSewer_UCloth(_targetObjA, _targetObjB, weldThreshold, edgesPerFrame);
        _sewerSession.OnSeamCompleted = RegisterSewnMesh;
        _idleTimer = 0f;   // bắt đầu session → reset timer

        bool ok = _sewerSession.Initialize(startPoint, startPoint, sewRadius);
        if (!ok)
        {
            Debug.LogWarning("[SewingManager] Không thể khởi tạo MeshSewer.");
            _sewerSession = null;
            _targetObjA = _targetObjB = null;
        }
        return ok;
    }

    private void InjectPointToLiveMerge(Vector3 rawPoint)
    {
        if (_sewerSession == null) return;

        List<Vector3> single = new List<Vector3> { rawPoint };
        List<Vector3> projA  = ProjectStrokeOntoMesh(_targetObjA, single);
        List<Vector3> projB  = ProjectStrokeOntoMesh(_targetObjB, single);

        if (projA != null && projA.Count > 0 && projB != null && projB.Count > 0)
        {
            if (_isCollectingStudyData)
                _precisionErrors.Add(Vector3.Distance(rawPoint, (projA[0] + projB[0]) * 0.5f));

            _sewerSession.AddSeam(projA[0], projB[0], sewRadius);
            _sewerSession.UpdateLiveVisuals();
            _idleTimer = 0f;   // reset timer mỗi khi có điểm khâu mới
        }
    }

    private void FinalizeSewingSession()
    {
        if (_sewerSession == null) return;

        _strokePoints.Clear();

        while (!_sewerSession.Sew()) { }

        string name = $"{_targetObjA.name}_InteractiveMergedCloth";
        _sewerSession.Finalize(name);

        _sewerSession = null;
        _targetObjA = _targetObjB = null;
    }

    private HashSet<GameObject> FindClothesNearSewer()
    {
        var clothes = new HashSet<GameObject>();
        for (int i = -3; i <= 3; i++)
        {
            Vector3 dir = Quaternion.Euler(0, i * 4f, 0) * sewer.transform.forward;
            foreach (var h in Physics.RaycastAll(new Ray(sewer.transform.position, dir), rayLength))
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
        var result   = new List<Vector3>();
        var collider = target.GetComponent<Collider>();
        if (collider == null) return null;

        float backup = 10f, range = 20f;
        Vector3 dir  = _averageStrokeForward.normalized;

        foreach (var origin in strokePoints)
        {
            Ray fwd = new Ray(origin - dir * backup, dir);
            if (collider.Raycast(fwd, out RaycastHit h, range))
                result.Add(h.point);
        }
        return result;
    }

    private void ExportUserStudyData()
    {
        float duration = Time.time - _modeStartTime;
        float avgFps   = _fpsAccumulatedTime > 0f ? _fpsFrameCount / _fpsAccumulatedTime : 0f;
        float avgErr   = _precisionErrors.Count > 0 ? _precisionErrors.Average() : 0f;

        int idx = 1; string fileName;
        do { fileName = Path.Combine(Application.persistentDataPath, $"userStudy_sew_{idx++:D3}.csv"); }
        while (File.Exists(fileName));

        try
        {
            using (var sw = new StreamWriter(fileName))
            {
                sw.WriteLine("Experiment Name,Average FPS,Time (s),Sew Precision Error (m)");
                sw.WriteLine($"sew,{avgFps:F2},{duration:F3},{avgErr:F4}");
            }
            Debug.Log($"<color=green>[UserStudy]</color> Đã xuất: {fileName}");
        }
        catch (System.Exception e) { Debug.LogError($"[UserStudy] Lỗi xuất CSV: {e.Message}"); }
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
}
