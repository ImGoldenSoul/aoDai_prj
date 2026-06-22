// ============================================================
//  SewingManager_UCloth.cs  — v11.1 (VR Input Fixed)
// ============================================================

using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using System.Linq;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class SewingManager_UCloth : MonoBehaviour
{
    [Header("References")]
    public GameObject sewer;
    public CuttingManager_UCloth cuttingManager;

    [Header("Ray Settings")]
    public float rayLength    = 5f;
    public bool  showDebugRay = true;

    [Header("Sewing Settings")]
    public string clothTag     = "Cloth";
    public float weldThreshold = 0.008f;
    public float sewRadius     = 0.04f;
    public int   edgesPerFrame = 5;
    public float minVisualSpacing = 0.01f;

    [Header("Strict Multi-Button Mapping")]
    public KeyCode startSewModeKey  = KeyCode.Space;
    public KeyCode drawStrokeKey    = KeyCode.Mouse0;
    public KeyCode cancelSewModeKey = KeyCode.Escape;

#if ENABLE_INPUT_SYSTEM
    [Header("New Input System Actions (Supports Both Controllers)")]
    public InputActionReference leftStartModeAction;
    public InputActionReference rightStartModeAction;
    
    public InputActionReference leftDrawStrokeAction;
    public InputActionReference rightDrawStrokeAction;
    
    public InputActionReference leftCancelModeAction;
    public InputActionReference rightCancelModeAction;
#endif

    private enum SewingState { Idle, SewingModeActive }
    private SewingState _currentState = SewingState.Idle;

    private MeshSewer_UCloth _sewerSession;
    private GameObject _targetObjA;
    private GameObject _targetObjB;

    private readonly List<Vector3> _currentStrokePoints = new List<Vector3>();
    private Vector3 _averageStrokeForward = Vector3.forward;
    private bool _isDrawingActive = false;

    private string _uiDisplayMessage = "";
    private float _uiMessageTimer = 0f;
    private XRGrabInteractable _sewerGrab;

    // ── DATA METRICS FOR USER STUDY ──
    private float _modeStartTime;
    private int _fpsFrameCount;
    private float _fpsAccumulatedTime;
    private List<float> _precisionErrors = new List<float>();

    // ── FIX: Enable/Disable tất cả InputAction để VR controller hoạt động ──
#if ENABLE_INPUT_SYSTEM
    void OnEnable()
    {
        leftStartModeAction?.action?.Enable();
        rightStartModeAction?.action?.Enable();
        leftDrawStrokeAction?.action?.Enable();
        rightDrawStrokeAction?.action?.Enable();
        leftCancelModeAction?.action?.Enable();
        rightCancelModeAction?.action?.Enable();
    }

    void OnDisable()
    {
        leftStartModeAction?.action?.Disable();
        rightStartModeAction?.action?.Disable();
        leftDrawStrokeAction?.action?.Disable();
        rightDrawStrokeAction?.action?.Disable();
        leftCancelModeAction?.action?.Disable();
        rightCancelModeAction?.action?.Disable();
    }
#endif

    void Start()
    {
        var clothObjects = GameObject.FindGameObjectsWithTag(clothTag);
        foreach (var obj in clothObjects)
            RegisterClothObject(obj);

        if (sewer != null)
        {
            _sewerGrab = sewer.GetComponent<XRGrabInteractable>();
            if (_sewerGrab == null)
                Debug.LogWarning("[SewingManager] Không tìm thấy XRGrabInteractable trên sewer — text trạng thái sew mode sẽ không hiển thị.");
        }

        // ── FIX: Cảnh báo nếu quên assign InputActionReference trong Inspector ──
#if ENABLE_INPUT_SYSTEM
        if (leftStartModeAction  == null) Debug.LogWarning("[SewingManager] ⚠ leftStartModeAction  chưa được assign trong Inspector!");
        if (rightStartModeAction == null) Debug.LogWarning("[SewingManager] ⚠ rightStartModeAction chưa được assign trong Inspector!");
        if (leftDrawStrokeAction  == null) Debug.LogWarning("[SewingManager] ⚠ leftDrawStrokeAction  chưa được assign trong Inspector!");
        if (rightDrawStrokeAction == null) Debug.LogWarning("[SewingManager] ⚠ rightDrawStrokeAction chưa được assign trong Inspector!");
        if (leftCancelModeAction  == null) Debug.LogWarning("[SewingManager] ⚠ leftCancelModeAction  chưa được assign trong Inspector!");
        if (rightCancelModeAction == null) Debug.LogWarning("[SewingManager] ⚠ rightCancelModeAction chưa được assign trong Inspector!");
#else
        Debug.LogWarning("[SewingManager] ⚠ ENABLE_INPUT_SYSTEM chưa được define — VR controller input sẽ không hoạt động! " +
                         "Vào Project Settings → Player → Scripting Define Symbols và thêm ENABLE_INPUT_SYSTEM.");
#endif

        Debug.Log("<color=cyan>[SewingManager]</color> Hệ thống sẵn sàng.");
    }

    private bool IsToolGrabbed()
    {
        return _sewerGrab != null && _sewerGrab.isSelected;
    }

    private void SetLocomotionEnabled(bool enabledState)
    {
        var providers = FindObjectsOfType<MonoBehaviour>();
        foreach (var provider in providers)
        {
            string typeName = provider.GetType().Name;
            if (typeName.Contains("MoveProvider") || typeName.Contains("TurnProvider") || 
                typeName.Contains("TeleportationProvider") || typeName.Contains("LocomotionSystem"))
            {
                provider.enabled = enabledState;
            }
        }
    }

    void LateUpdate()
    {
        if (sewer == null) return;

        bool wantsToStart  = IsStartPressedThisFrame();
        bool wantsToCancel = IsCancelPressedThisFrame();
        bool isDrawHeld    = IsDrawHeldNow();

        if (_uiMessageTimer > 0f)
        {
            _uiMessageTimer -= Time.deltaTime;
            if (_uiMessageTimer <= 0f) _uiDisplayMessage = "";
        }

        switch (_currentState)
        {
            case SewingState.Idle:
                if (wantsToStart)
                {
                    if (TryInitializeSewingSession())
                    {
                        _currentState = SewingState.SewingModeActive;
                        _currentStrokePoints.Clear();
                        _isDrawingActive = false;
                        
                        // Khởi động các bộ đếm User Study
                        _modeStartTime = Time.time;
                        _fpsFrameCount = 0;
                        _fpsAccumulatedTime = 0f;
                        _precisionErrors.Clear();

                        // Khóa góc nhìn, tránh lỗi dịch chuyển Viewport ngoài ý muốn
                        SetLocomotionEnabled(false);

                        if (IsToolGrabbed())
                        {
                            _uiDisplayMessage = "You are in sewing mode, press B/Y on the controller to end sewing mode";
                            _uiMessageTimer = float.MaxValue; 
                        }
                    }
                }
                break;

            case SewingState.SewingModeActive:
                // Theo dõi dữ liệu FPS trong suốt quá trình xử lý khâu
                _fpsFrameCount++;
                _fpsAccumulatedTime += Time.unscaledDeltaTime;

                if (wantsToCancel)
                {
                    ExportUserStudyData();
                    FinalizeSewingSession();
                    _currentState = SewingState.Idle;
                    SetLocomotionEnabled(true);
                    
                    _uiDisplayMessage = "End sew mode, your mesh is sewn.";
                    _uiMessageTimer = 4.0f;
                    break;
                }

                if (isDrawHeld)
                {
                    if (!_isDrawingActive) _isDrawingActive = true;
                    HandleStrokeDrawing();
                }
                else
                {
                    if (_isDrawingActive) _isDrawingActive = false;
                }

                if (_sewerSession != null)
                {
                    _sewerSession.Sew();
                }
                break;
        }
    }

    private bool TryInitializeSewingSession()
    {
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

        Ray initialRay = new Ray(sewer.transform.position, sewer.transform.forward);
        Vector3 initialHit = sewer.transform.position;
        if (Physics.Raycast(initialRay, out RaycastHit hit, rayLength)) initialHit = hit.point;

        bool initSuccess = _sewerSession.Initialize(initialHit, initialHit, sewRadius);
        
        if (!initSuccess)
        {
            Debug.LogWarning("[SewingManager] Không thể khởi tạo MeshSewer (có thể do điểm bắt đầu quá xa mép vải).");
            _sewerSession = null;
            _targetObjA = _targetObjB = null;
        }
        
        return initSuccess;
    }

    private void HandleStrokeDrawing()
    {
        Vector3 currentPos = sewer.transform.position;
        if (_currentStrokePoints.Count == 0 || Vector3.Distance(_currentStrokePoints[_currentStrokePoints.Count - 1], currentPos) > minVisualSpacing)
        {
            _currentStrokePoints.Add(currentPos);
            _averageStrokeForward = Vector3.Lerp(_averageStrokeForward, sewer.transform.forward, 0.1f);
            InjectPointToLiveMerge(currentPos);
        }
    }

    private void InjectPointToLiveMerge(Vector3 rawPoint)
    {
        if (_sewerSession == null) return;

        List<Vector3> singlePointList = new List<Vector3> { rawPoint };
        List<Vector3> projA = ProjectStrokeOntoMesh(_targetObjA, singlePointList);
        List<Vector3> projB = ProjectStrokeOntoMesh(_targetObjB, singlePointList);

        if (projA != null && projA.Count > 0 && projB != null && projB.Count > 0)
        {
            // Tính khoảng cách sai lệch Precision giữa kim/controller và trung điểm đường chỉ trên vải
            Vector3 midSeamPoint = (projA[0] + projB[0]) * 0.5f;
            float errorDist = Vector3.Distance(rawPoint, midSeamPoint);
            _precisionErrors.Add(errorDist);

            _sewerSession.AddSeam(projA[0], projB[0], sewRadius);
            _sewerSession.UpdateLiveVisuals();
        }
    }

    private void FinalizeSewingSession()
    {
        if (_sewerSession == null) return;

        _currentStrokePoints.Clear();
        _isDrawingActive = false;

        while (!_sewerSession.Sew()) { }

        string newMeshName = $"{_targetObjA.name}_InteractiveMergedCloth";
        _sewerSession.Finalize(newMeshName);

        _sewerSession = null;
        _targetObjA = _targetObjB = null;
    }

    private void ExportUserStudyData()
    {
        float duration = Time.time - _modeStartTime;
        float avgFps = _fpsAccumulatedTime > 0f ? (_fpsFrameCount / _fpsAccumulatedTime) : 0f;
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
            Debug.Log($"<color=green>[UserStudy]</color> Đã xuất báo cáo thực nghiệm thành công: {fileName}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[UserStudy] Lỗi xuất CSV file: {e.Message}");
        }
    }

    private HashSet<GameObject> FindClothesNearSewer()
    {
        HashSet<GameObject> clothes = new HashSet<GameObject>();
        for (int i = -3; i <= 3; i++)
        {
            Vector3 dir = Quaternion.Euler(0, i * 4f, 0) * sewer.transform.forward;
            Ray ray = new Ray(sewer.transform.position, dir);
            var hits = Physics.RaycastAll(ray, rayLength);

            foreach (var h in hits)
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
        Vector3 projectDir = _averageStrokeForward.normalized;

        for (int i = 0; i < strokePoints.Count; i++)
        {
            Vector3 origin = strokePoints[i];
            Vector3 rayOrigin = origin - projectDir * backupDistance;
            Ray projectionRay = new Ray(rayOrigin, projectDir);

            if (collider.Raycast(projectionRay, out RaycastHit hitForward, totalScanRange))
            {
                intersectPoints.Add(hitForward.point);
            }
        }
        return intersectPoints;
    }

    private bool IsStartPressedThisFrame()
    {
        bool pressed = Input.GetKeyDown(startSewModeKey);
#if ENABLE_INPUT_SYSTEM
        if (leftStartModeAction?.action != null) pressed |= leftStartModeAction.action.WasPressedThisFrame();
        if (rightStartModeAction?.action != null) pressed |= rightStartModeAction.action.WasPressedThisFrame();
#endif
        return pressed;
    }

    private bool IsDrawHeldNow()
    {
        bool held = Input.GetKey(drawStrokeKey);
#if ENABLE_INPUT_SYSTEM
        if (leftDrawStrokeAction?.action != null) held |= leftDrawStrokeAction.action.IsPressed();
        if (rightDrawStrokeAction?.action != null) held |= rightDrawStrokeAction.action.IsPressed();
#endif
        return held;
    }

    private bool IsCancelPressedThisFrame()
    {
        bool pressed = Input.GetKeyDown(cancelSewModeKey);
#if ENABLE_INPUT_SYSTEM
        if (leftCancelModeAction?.action != null) pressed |= leftCancelModeAction.action.WasPressedThisFrame();
        if (rightCancelModeAction?.action != null) pressed |= rightCancelModeAction.action.WasPressedThisFrame();
#endif
        return pressed;
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
        style.fontSize = 28;
        style.normal.textColor = Color.white;

        style.normal.textColor = Color.black;
        GUI.Label(new Rect(Screen.width / 2 - 298, Screen.height - 102, 600, 50), _uiDisplayMessage, style);
        GUI.Label(new Rect(Screen.width / 2 - 302, Screen.height - 98, 600, 50), _uiDisplayMessage, style);
        
        style.normal.textColor = Color.cyan;
        GUI.Label(new Rect(Screen.width / 2 - 300, Screen.height - 100, 600, 50), _uiDisplayMessage, style);
    }
}