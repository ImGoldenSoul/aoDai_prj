// ============================================================
//  CuttingManager_UCloth.cs  — v14.3 (Fix AreaLoss: originalArea pre-cut + areaLoss metric)
// ============================================================

using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using System.Linq;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

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

    [Header("Strict Multi-Trigger Mapping")]
    public KeyCode cutTriggerKey = KeyCode.Mouse0;
    public KeyCode cutCancelKey  = KeyCode.Escape;
    public KeyCode drawStrokeKey = KeyCode.Mouse1;

#if ENABLE_INPUT_SYSTEM
    [Header("New Input System Actions (Supports Both Controllers)")]
    public InputActionReference leftCutTriggerAction;
    public InputActionReference rightCutTriggerAction;
    
    public InputActionReference leftCancelCutAction;
    public InputActionReference rightCancelCutAction;

    public InputActionReference leftDrawStrokeAction;
    public InputActionReference rightDrawStrokeAction;
#endif

    [Header("Cut Line Visual")]
    public Material cutLineMaterial;
    public float cutLineWidth = 0.004f;
    public Color cutLineColor = new Color(1f, 0.15f, 0.05f, 1f);

    private LineRenderer _cutLine;
    private readonly List<Vector3> _visualStrokePoints = new List<Vector3>();
    private Vector3 _averageStrokeForward = Vector3.forward;

    private readonly Dictionary<GameObject, MeshCutter_UCloth> _cutters = new Dictionary<GameObject, MeshCutter_UCloth>();
    private readonly HashSet<GameObject> _ucClothObjects = new HashSet<GameObject>();

    private float _rescanTimer;
    public float rescanInterval = 1f;

    private XRGrabInteractable _cutterGrab;
    private string _uiDisplayMessage = "";
    private float _uiMessageTimer = 0f;
    private bool _isInCutMode = false;

    // ── DATA METRICS FOR USER STUDY ──
    private float _modeStartTime;
    private int _fpsFrameCount;
    private float _fpsAccumulatedTime;
    private List<float> _precisionErrors = new List<float>();

    // ── AREA LOSS METRICS (v14.3) ──
    // Tổng diện tích gốc (pre-cut) của tất cả các mesh đã bị cắt trong session này
    private float _sessionOriginalAreaTotal = 0f;
    // Tổng diện tích sau cắt (tổng các piece) trong session này
    private float _sessionPostCutAreaTotal  = 0f;

    // ── FIX: Enable/Disable tất cả InputAction để VR controller hoạt động ──
#if ENABLE_INPUT_SYSTEM
    void OnEnable()
    {
        leftCutTriggerAction?.action?.Enable();
        rightCutTriggerAction?.action?.Enable();
        leftCancelCutAction?.action?.Enable();
        rightCancelCutAction?.action?.Enable();
        leftDrawStrokeAction?.action?.Enable();
        rightDrawStrokeAction?.action?.Enable();
    }

    void OnDisable()
    {
        leftCutTriggerAction?.action?.Disable();
        rightCutTriggerAction?.action?.Disable();
        leftCancelCutAction?.action?.Disable();
        rightCancelCutAction?.action?.Disable();
        leftDrawStrokeAction?.action?.Disable();
        rightDrawStrokeAction?.action?.Disable();
    }
#endif

    void Start()
    {
        if (cutter == null)
        {
            Debug.LogError("[CuttingManager_UCloth] Chưa gán Cutter transform!");
            enabled = false;
            return;
        }

        _cutterGrab = cutter.GetComponent<XRGrabInteractable>();
        RegisterAllClothObjects();
        SetupCutLineVisual();

        // ── FIX: Cảnh báo nếu quên assign InputActionReference trong Inspector ──
#if ENABLE_INPUT_SYSTEM
        if (leftCutTriggerAction  == null) Debug.LogWarning("[CuttingManager] ⚠ leftCutTriggerAction  chưa được assign trong Inspector!");
        if (rightCutTriggerAction == null) Debug.LogWarning("[CuttingManager] ⚠ rightCutTriggerAction chưa được assign trong Inspector!");
        if (leftCancelCutAction   == null) Debug.LogWarning("[CuttingManager] ⚠ leftCancelCutAction   chưa được assign trong Inspector!");
        if (rightCancelCutAction  == null) Debug.LogWarning("[CuttingManager] ⚠ rightCancelCutAction  chưa được assign trong Inspector!");
        if (leftDrawStrokeAction  == null) Debug.LogWarning("[CuttingManager] ⚠ leftDrawStrokeAction  chưa được assign trong Inspector!");
        if (rightDrawStrokeAction == null) Debug.LogWarning("[CuttingManager] ⚠ rightDrawStrokeAction chưa được assign trong Inspector!");
#else
        Debug.LogWarning("[CuttingManager] ⚠ ENABLE_INPUT_SYSTEM chưa được define — VR controller input sẽ không hoạt động! " +
                         "Vào Project Settings → Player → Scripting Define Symbols và thêm ENABLE_INPUT_SYSTEM.");
#endif
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
        _cutLine.endColor = cutLineColor;
        _cutLine.enabled = false;
    }

    private bool IsToolGrabbed()
    {
        return _cutterGrab != null && _cutterGrab.isSelected;
    }

    /// <summary>Khóa/mở khóa các component di chuyển của VR để tránh Viewport bị dịch chuyển khi bấm nút</summary>
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
        if (cutter == null) return;

        bool triggerPressedThisFrame = IsTriggerPressedThisFrame();
        bool cancelPressed = IsCancelPressedThisFrame();
        bool drawHeldNow = IsDrawHeldNow();
        bool drawReleasedThisFrame = IsDrawReleasedThisFrame();

        if (_uiMessageTimer > 0f)
        {
            _uiMessageTimer -= Time.deltaTime;
            if (_uiMessageTimer <= 0f) _uiDisplayMessage = "";
        }

        if (triggerPressedThisFrame)
        {
            if (!_isInCutMode)
            {
                if (IsToolGrabbed())
                {
                    ClearStroke();
                    _averageStrokeForward = cutter.forward;
                    _isInCutMode = true;

                    // Bắt đầu đo metric user study
                    _modeStartTime = Time.time;
                    _fpsFrameCount = 0;
                    _fpsAccumulatedTime = 0f;
                    _precisionErrors.Clear();

                    // Reset area loss counters cho session mới (v14.3)
                    _sessionOriginalAreaTotal = 0f;
                    _sessionPostCutAreaTotal  = 0f;

                    // Khóa di chuyển hệ thống để giữ nguyên Viewport hiện tại
                    SetLocomotionEnabled(false);
                }
            }
            else
            {
                // Không export ở đây — chỉ export khi ấn nút cancel
                ClearStroke();
                _isInCutMode = false;
                SetLocomotionEnabled(true);
                _uiDisplayMessage = "";
                _uiMessageTimer = 0f;
            }
        }

        if (_isInCutMode)
        {
            // Tính toán FPS liên tục trong chế độ
            _fpsFrameCount++;
            _fpsAccumulatedTime += Time.unscaledDeltaTime;

            if (IsToolGrabbed() && drawHeldNow)
            {
                Vector3 currentPos = cutter.position;
                if (_visualStrokePoints.Count == 0 || Vector3.Distance(_visualStrokePoints[_visualStrokePoints.Count - 1], currentPos) > minVisualSpacing)
                {
                    _visualStrokePoints.Add(currentPos);
                    _averageStrokeForward = Vector3.Lerp(_averageStrokeForward, cutter.forward, 0.2f);
                }

                UpdateVisualLineRenderer();
            }

            if (drawReleasedThisFrame && IsToolGrabbed())
            {
                ProcessStrokeCut();
            }

            if (IsToolGrabbed())
            {
                _uiDisplayMessage = drawHeldNow
                    ? "you are drawing the cut line"
                    : "you are in the cutting mode, hold draw button to trace the cut line";
                _uiMessageTimer = 0.1f;
            }
        }

        if (cancelPressed && _isInCutMode)
        {
            ExportUserStudyData();
            ClearStroke();
            _isInCutMode = false;
            SetLocomotionEnabled(true);
            
            _uiDisplayMessage = "End cutting mode.";
            _uiMessageTimer = 3f;
        }

        CleanupStaleObjects();
        RescanForNewClothObjects();
    }

    private void ClearStroke()
    {
        _visualStrokePoints.Clear();
        if (_cutLine != null) _cutLine.enabled = false;
    }

    private void UpdateVisualLineRenderer()
    {
        if (_cutLine == null) return;
        if (_visualStrokePoints.Count < 2)
        {
            _cutLine.enabled = false;
            return;
        }
        _cutLine.enabled = true;
        _cutLine.positionCount = _visualStrokePoints.Count;
        _cutLine.SetPositions(_visualStrokePoints.ToArray());
    }

    private void ProcessStrokeCut()
    {
        if (_visualStrokePoints.Count < 2) return;

        float totalLength = 0f;
        for (int i = 1; i < _visualStrokePoints.Count; i++)
            totalLength += Vector3.Distance(_visualStrokePoints[i - 1], _visualStrokePoints[i]);

        if (totalLength < minCutPathLength)
        {
            ClearStroke();
            return;
        }

        var targetsToCheck = new List<GameObject>(_cutters.Keys);

        foreach (var target in targetsToCheck)
        {
            if (target == null || !target.activeInHierarchy) continue;
            if (!_cutters.TryGetValue(target, out var mc) || mc == null) continue;

            List<Vector3> dynamicMeshIntersectionPath = ProjectStrokeOntoMesh(target, _visualStrokePoints);

            if (dynamicMeshIntersectionPath != null && dynamicMeshIntersectionPath.Count >= 2)
            {
                // Tính Cut Precision Error: perpendicular distance từ mỗi điểm projected
                // đến reference line nối P1→PN (theo công thức E_cut).
                Vector3 p1    = dynamicMeshIntersectionPath[0];
                Vector3 pN    = dynamicMeshIntersectionPath[dynamicMeshIntersectionPath.Count - 1];
                Vector3 lineDir = pN - p1;
                float   lineLen = lineDir.magnitude;
                if (lineLen > 0.001f)
                {
                    Vector3 lineDirNorm = lineDir / lineLen;
                    foreach (var pt in dynamicMeshIntersectionPath)
                    {
                        float   t       = Mathf.Clamp01(Vector3.Dot(pt - p1, lineDirNorm) / lineLen);
                        Vector3 closest = p1 + t * lineDir;
                        _precisionErrors.Add(Vector3.Distance(pt, closest));
                    }
                }

                mc.ClearPath();
                foreach (var point in dynamicMeshIntersectionPath) mc.ForceAddPathPoint(point);

                var result = mc.CommitCut(splitOnlyWhenDisconnected);
                if (result == CutResult_Ucloth.Split)
                {
                    var newPieces = mc.GetLastCreatedPieces();

                    // ── v14.3: Đo diện tích mesh GỐC trước khi deactivate ──
                    float originalArea = 0f;
                    var originalMf = target.GetComponent<MeshFilter>();
                    if (originalMf != null && originalMf.sharedMesh != null)
                        originalArea = ClothAreaCalculator.CalculateAreaFromMesh(
                            originalMf.sharedMesh, target.transform.lossyScale);

                    target.SetActive(false);
                    _cutters.Remove(target);
                    _ucClothObjects.Remove(target);

                    // ── v14.3: Đo tổng diện tích các piece sau cắt ──
                    float totalPieceArea = 0f;
                    var pieceMeshData = new List<(GameObject go, Mesh mesh)>();
                    foreach (var piece in newPieces)
                    {
                        var mf = piece.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null)
                        {
                            float a = ClothAreaCalculator.CalculateAreaFromMesh(
                                mf.sharedMesh, piece.transform.lossyScale);
                            totalPieceArea += a;
                            pieceMeshData.Add((piece, mf.sharedMesh));
                        }
                    }

                    // ── v14.3: Tính loss ──
                    float areaLoss      = Mathf.Max(0f, originalArea - totalPieceArea);
                    float areaLossRatio = originalArea > 0f ? areaLoss / originalArea : 0f;

                    // Tích lũy vào session totals
                    _sessionOriginalAreaTotal += originalArea;
                    _sessionPostCutAreaTotal  += totalPieceArea;

                    Debug.Log($"<color=cyan>[AreaLoss]</color> " +
                              $"Original={originalArea*1e4f:F2}cm² | " +
                              $"PostCut={totalPieceArea*1e4f:F2}cm² | " +
                              $"Loss={areaLoss*1e4f:F4}cm² ({areaLossRatio*100f:F2}%)");

                    // ── Gắn ClothAreaData vào từng piece (dùng originalArea pre-cut) ──
                    foreach (var (go, mesh) in pieceMeshData)
                    {
                        float pieceArea = ClothAreaCalculator.CalculateAreaFromMesh(
                            mesh, go.transform.lossyScale);
                        var data          = go.AddComponent<ClothAreaData>();
                        data.originalArea = originalArea;       // ← diện tích mesh gốc trước khi cắt
                        data.pieceArea    = pieceArea;
                        data.seamArea     = areaLoss;           // ← loss thực: original − tổng piece
                        data.seamRatio    = areaLossRatio;
                    }

                    foreach (var piece in newPieces) RegisterClothObject(piece);
                    break;
                }
            }
        }

        _visualStrokePoints.Clear();
        if (_cutLine != null) _cutLine.enabled = false;
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
            Vector3 origin = strokePoints[i];
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
                {
                    intersectPoints.Add(hitReverse.point);
                }
            }
        }

        var filteredPoints = new List<Vector3>();
        for (int i = 0; i < intersectPoints.Count; i++)
        {
            if (filteredPoints.Count == 0) filteredPoints.Add(intersectPoints[i]);
            else
            {
                if (Vector3.Distance(filteredPoints[filteredPoints.Count - 1], intersectPoints[i]) > minVisualSpacing * 0.4f)
                    filteredPoints.Add(intersectPoints[i]);
            }
        }

        if (filteredPoints.Count >= 2)
        {
            Bounds targetBounds = collider.bounds;
            float maxExttent = Mathf.Max(targetBounds.size.x, targetBounds.size.y, targetBounds.size.z);
            float adaptiveOffset = Mathf.Clamp(maxExttent * 0.2f, 0.01f, 0.3f);

            Vector3 startDir = (filteredPoints[1] - filteredPoints[0]).normalized;
            Vector3 endDir = (filteredPoints[filteredPoints.Count - 1] - filteredPoints[filteredPoints.Count - 2]).normalized;

            Vector3 startExt = filteredPoints[0] - startDir * adaptiveOffset;
            Vector3 endExt = filteredPoints[filteredPoints.Count - 1] + endDir * adaptiveOffset;
            
            filteredPoints.Insert(0, startExt);
            filteredPoints.Add(endExt);
        }

        return filteredPoints;
    }

    private void ExportUserStudyData()
    {
        float duration = Time.time - _modeStartTime;
        float avgFps = _fpsAccumulatedTime > 0f ? (_fpsFrameCount / _fpsAccumulatedTime) : 0f;
        float avgPrecisionError = _precisionErrors.Count > 0 ? _precisionErrors.Average() : 0f;

        // ── v14.3: Dùng session totals thay vì scan FindObjectsOfType ──
        // (FindObjectsOfType chỉ thấy các piece hiện còn trong scene, bỏ sót piece đã bị cắt tiếp)
        float sessionAreaLoss      = Mathf.Max(0f, _sessionOriginalAreaTotal - _sessionPostCutAreaTotal);
        float sessionAreaLossRatio = _sessionOriginalAreaTotal > 0f
            ? sessionAreaLoss / _sessionOriginalAreaTotal : 0f;

        // Tìm số thứ tự tự động tăng cho file để không đè dữ liệu cũ
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
                // Header (v14.3: thêm các cột area loss)
                sw.WriteLine("Experiment Name,Average FPS,Time (s),Cut Precision Error (m)," +
                             "Original Area (m2),Post-Cut Area (m2),Area Loss (m2),Area Loss Ratio");
                sw.WriteLine($"cut," +
                             $"{avgFps:F2}," +
                             $"{duration:F3}," +
                             $"{avgPrecisionError:F4}," +
                             $"{_sessionOriginalAreaTotal:F6}," +
                             $"{_sessionPostCutAreaTotal:F6}," +
                             $"{sessionAreaLoss:F6}," +
                             $"{sessionAreaLossRatio:F4}");
            }
            Debug.Log($"<color=green>[UserStudy]</color> Đã xuất báo cáo thực nghiệm: {fileName}\n" +
                      $"  Original={_sessionOriginalAreaTotal*1e4f:F2}cm² | " +
                      $"PostCut={_sessionPostCutAreaTotal*1e4f:F2}cm² | " +
                      $"Loss={sessionAreaLoss*1e4f:F4}cm² ({sessionAreaLossRatio*100f:F2}%)");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[UserStudy] Lỗi xuất CSV file: {e.Message}");
        }
    }

    private bool IsTriggerPressedThisFrame()
    {
        bool pressed = Input.GetKeyDown(cutTriggerKey);
#if ENABLE_INPUT_SYSTEM
        if (leftCutTriggerAction?.action != null) pressed |= leftCutTriggerAction.action.WasPressedThisFrame();
        if (rightCutTriggerAction?.action != null) pressed |= rightCutTriggerAction.action.WasPressedThisFrame();
#endif
        return pressed;
    }

    private bool IsTriggerReleasedThisFrame()
    {
        bool released = Input.GetKeyUp(cutTriggerKey);
#if ENABLE_INPUT_SYSTEM
        if (leftCutTriggerAction?.action != null) released |= leftCutTriggerAction.action.WasReleasedThisFrame();
        if (rightCutTriggerAction?.action != null) released |= rightCutTriggerAction.action.WasReleasedThisFrame();
#endif
        return released;
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

    private bool IsDrawReleasedThisFrame()
    {
        bool released = Input.GetKeyUp(drawStrokeKey);
#if ENABLE_INPUT_SYSTEM
        if (leftDrawStrokeAction?.action != null) released |= leftDrawStrokeAction.action.WasReleasedThisFrame();
        if (rightDrawStrokeAction?.action != null) released |= rightDrawStrokeAction.action.WasReleasedThisFrame();
#endif
        return released;
    }

    private bool IsCancelPressedThisFrame()
    {
        bool pressed = Input.GetKeyDown(cutCancelKey);
#if ENABLE_INPUT_SYSTEM
        if (leftCancelCutAction?.action != null) pressed |= leftCancelCutAction.action.WasPressedThisFrame();
        if (rightCancelCutAction?.action != null) pressed |= rightCancelCutAction.action.WasPressedThisFrame();
#endif
        return pressed;
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
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) || float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z))
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
        style.fontSize = 28;

        style.normal.textColor = Color.black;
        GUI.Label(new Rect(Screen.width / 2 - 298, Screen.height - 152, 600, 50), _uiDisplayMessage, style);
        GUI.Label(new Rect(Screen.width / 2 - 302, Screen.height - 148, 600, 50), _uiDisplayMessage, style);
        
        style.normal.textColor = Color.red; 
        GUI.Label(new Rect(Screen.width / 2 - 300, Screen.height - 150, 600, 50), _uiDisplayMessage, style);
    }
}