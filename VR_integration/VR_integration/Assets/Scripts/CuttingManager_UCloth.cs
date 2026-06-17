// ============================================================
//  CuttingManager_UCloth.cs  — v12.0 (Fix Alignment & Close Penetration)
// ============================================================

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class CuttingManager_UCloth : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Transform đầu công cụ cắt — ray được bắn từ đây.")]
    public Transform cutter;

    [Header("Ray Settings")]
    [Tooltip("Chiều dài tia ảo để dựng 'mặt phẳng cắt' xuyên qua mesh.")]
    public float rayLength = 3f;
    public bool showDebugRay = true;

    [Header("Cut Settings")]
    public string clothTag = "Cloth";
    public float splitForce = 1.5f;
    [Tooltip("Nếu TRUE: chỉ tách khi đường cắt thực sự chia mesh thành ≥ 2 vùng.")]
    public bool splitOnlyWhenDisconnected = true;
    [Tooltip("Khoảng cách tối thiểu giữa 2 điểm vẽ liên tiếp trong không gian (m).")]
    public float minVisualSpacing = 0.01f;
    [Tooltip("Chiều dài tối thiểu của đường nét visual (m) để kích hoạt cắt.")]
    public float minCutPathLength = 0.04f;

    [Header("Hold-Trigger-To-Draw / Release-To-Cut")]
    public KeyCode cutTriggerKey = KeyCode.Mouse0;
    public KeyCode cancelCutKey = KeyCode.Escape;
#if ENABLE_INPUT_SYSTEM
    public InputActionReference cutTriggerAction;
    public InputActionReference cancelCutAction;
#endif

    [Header("Cut Line Visual")]
    public Material cutLineMaterial;
    public float cutLineWidth = 0.004f;
    public Color cutLineColor = new Color(1f, 0.15f, 0.05f, 1f);

    // Chỉ lưu chuỗi các điểm vị trí chính xác của đầu dao (Không dùng hướng forward xoay góc của tay)
    private readonly List<Vector3> _visualStrokePoints = new List<Vector3>();
    // Lưu hướng forward trung bình của cả nhát chém để dựng mặt phẳng chiếu đồng bộ
    private Vector3 _averageStrokeForward = Vector3.forward;

    private readonly Dictionary<GameObject, MeshCutter_UCloth> _cutters = new Dictionary<GameObject, MeshCutter_UCloth>();
    private readonly HashSet<GameObject> _ucClothObjects = new HashSet<GameObject>();

    private LineRenderer _cutLine;
    private float _rescanTimer;
    public float rescanInterval = 1f;

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
        _cutLine.endColor = cutLineColor;
        _cutLine.enabled = false;
    }

    void LateUpdate()
    {
        if (cutter == null) return;

        bool triggerHeldNow = IsTriggerHeld();
        bool triggerPressedThisFrame = IsTriggerPressedThisFrame();
        bool triggerReleasedThisFrame = IsTriggerReleasedThisFrame();

        if (triggerPressedThisFrame)
        {
            _visualStrokePoints.Clear();
            _visualStrokePoints.Add(cutter.position);
            _averageStrokeForward = cutter.forward;
        }
        else if (triggerHeldNow)
        {
            Vector3 currentPos = cutter.position;
            if (_visualStrokePoints.Count == 0 || Vector3.Distance(_visualStrokePoints[_visualStrokePoints.Count - 1], currentPos) > minVisualSpacing)
            {
                _visualStrokePoints.Add(currentPos);
                // Tích lũy cộng dồn để lấy hướng forward trung bình, tránh hiện tượng lắc tay làm lệch seam
                _averageStrokeForward = Vector3.Lerp(_averageStrokeForward, cutter.forward, 0.2f);
            }
        }

        UpdateVisualLineRenderer();

        if (triggerReleasedThisFrame)
        {
            ProcessStrokeCut();
        }

        if (Input.GetKeyDown(cancelCutKey) || (cancelCutAction != null && cancelCutAction.action.WasPressedThisFrame()))
        {
            ClearStroke();
        }

        CleanupStaleObjects();
        RescanForNewClothObjects();
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

            // Tiến hành chiếu đường nét thực tế vào Mesh vải
            List<Vector3> dynamicMeshIntersectionPath = ProjectStrokeOntoMesh(target, _visualStrokePoints);

            if (dynamicMeshIntersectionPath != null && dynamicMeshIntersectionPath.Count >= 2)
            {
                mc.ClearPath();
                foreach (var point in dynamicMeshIntersectionPath)
                {
                    mc.ForceAddPathPoint(point);
                }

                var result = mc.CommitCut(splitOnlyWhenDisconnected);
                if (result == CutResult_Ucloth.Split)
                {
                    var newPieces = mc.GetLastCreatedPieces();
                    target.SetActive(false);
                    _cutters.Remove(target);
                    _ucClothObjects.Remove(target);

                    foreach (var piece in newPieces)
                    {
                        RegisterClothObject(piece);
                    }
                    break;
                }
            }
        }

        ClearStroke();
    }

    private List<Vector3> ProjectStrokeOntoMesh(GameObject target, List<Vector3> strokePoints)
    {
        var intersectPoints = new List<Vector3>();
        var collider = target.GetComponent<Collider>();
        if (collider == null) return null;

        float backupDistance = 20f; 
        float totalScanRange = 40f;

        // FIX LỖI LỆCH TỌA ĐỘ: Định hình hướng chiếu đồng nhất. 
        // Ưu tiên sử dụng hướng nhìn trực diện của Main Camera để nhát cắt khớp 100% với góc nhìn visual của người dùng.
        Vector3 projectDir = _averageStrokeForward.normalized;
        if (Camera.main != null)
        {
            projectDir = Camera.main.transform.forward;
        }

        for (int i = 0; i < strokePoints.Count; i++)
        {
            Vector3 origin = strokePoints[i]; // Lấy chuẩn vị trí 3D của visual line hiện tại

            // FIX LỖI CONTROLLER SÁT VẢI/XUYÊN VẢI:
            // Dù đầu dao nằm ở đâu, ta luôn lùi tâm bắn tia về phía sau góc nhìn camera 20 mét,
            // bảo đảm tia luôn đi từ ngoài vào và đâm xuyên qua đúng vị trí đường visual line thế giới.
            Vector3 rayOrigin = origin - projectDir * backupDistance;
            Ray projectionRay = new Ray(rayOrigin, projectDir);

            // Bắn tia xuyên biên mặt trước
            if (collider.Raycast(projectionRay, out RaycastHit hitForward, totalScanRange))
            {
                intersectPoints.Add(hitForward.point);
            }
            else
            {
                // Bắn ngược phòng trường hợp topo hở biên bị lật ngược mặt
                Ray reverseRay = new Ray(origin + projectDir * backupDistance, -projectDir);
                if (collider.Raycast(reverseRay, out RaycastHit hitReverse, totalScanRange))
                {
                    intersectPoints.Add(hitReverse.point);
                }
            }
        }

        // Lọc mượt điểm
        var filteredPoints = new List<Vector3>();
        for (int i = 0; i < intersectPoints.Count; i++)
        {
            if (filteredPoints.Count == 0)
            {
                filteredPoints.Add(intersectPoints[i]);
            }
            else
            {
                if (Vector3.Distance(filteredPoints[filteredPoints.Count - 1], intersectPoints[i]) > minVisualSpacing * 0.4f)
                {
                    filteredPoints.Add(intersectPoints[i]);
                }
            }
        }

        // Ép biên ra rìa Manh vải để luôn chia đôi Manh vải thành công
        // =========================================================================
        // FIX LỖI NaN / INVALID AABB: ÉP BIÊN THÍCH NGHI THEO KÍCH THƯỚC MESH
        // =========================================================================
        if (filteredPoints.Count >= 2)
        {
            // Tính toán kích thước Bounding Box hiện tại của Collider để lấy ngưỡng thích nghi
            Bounds targetBounds = collider.bounds;
            
            // Lấy kích thước lớn nhất của mảnh vải làm chuẩn (tránh trường hợp mảnh vải quá nhỏ)
            float maxExttent = Mathf.Max(targetBounds.size.x, targetBounds.size.y, targetBounds.size.z);
            
            // Hệ số ép biên an toàn bằng 20% kích thước tổng thể của mảnh vải hiện tại, tối đa không quá 0.3m và tối thiểu không dưới 0.01m (1cm)
            float adaptiveOffset = Mathf.Clamp(maxExttent * 0.2f, 0.01f, 0.3f);

            // Nội suy hướng kéo dài dựa trên vector mút đầu và mút cuối của nét chém
            Vector3 startDir = (filteredPoints[1] - filteredPoints[0]).normalized;
            Vector3 endDir = (filteredPoints[filteredPoints.Count - 1] - filteredPoints[filteredPoints.Count - 2]).normalized;

            // Kéo dài hai đầu mút một khoảng thích nghi vừa đủ để vượt qua biên mà không làm tràn số hình học g3
            Vector3 startExt = filteredPoints[0] - startDir * adaptiveOffset;
            Vector3 endExt = filteredPoints[filteredPoints.Count - 1] + endDir * adaptiveOffset;
            
            filteredPoints.Insert(0, startExt);
            filteredPoints.Add(endExt);
        }

        return filteredPoints;
    }

    private void ClearStroke()
    {
        _visualStrokePoints.Clear();
        _cutLine.enabled = false;
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

    private bool IsTriggerHeld()
    {
        bool held = Input.GetKey(cutTriggerKey);
#if ENABLE_INPUT_SYSTEM
        if (cutTriggerAction != null && cutTriggerAction.action != null) held |= cutTriggerAction.action.IsPressed();
#endif
        return held;
    }
    private bool IsTriggerPressedThisFrame()
    {
        bool pressed = Input.GetKeyDown(cutTriggerKey);
#if ENABLE_INPUT_SYSTEM
        if (cutTriggerAction != null && cutTriggerAction.action != null) pressed |= cutTriggerAction.action.WasPressedThisFrame();
#endif
        return pressed;
    }
    private bool IsTriggerReleasedThisFrame()
    {
        bool released = Input.GetKeyUp(cutTriggerKey);
#if ENABLE_INPUT_SYSTEM
        if (cutTriggerAction != null && cutTriggerAction.action != null) released |= cutTriggerAction.action.WasReleasedThisFrame();
#endif
        return released;
    }

    private void RegisterAllClothObjects()
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
        bool simReady = false;

        while (timeout > 0f)
        {
            if (obj == null) yield break;
            if (ucCloth.simData != null && ucCloth.simData.positionsReadOnly.IsCreated)
            {
                yield return new WaitForSeconds(0.05f); 
                if (obj == null) yield break;

                if (IsSimDataFinite(ucCloth))
                {
                    simReady = true;
                    break;
                }
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

