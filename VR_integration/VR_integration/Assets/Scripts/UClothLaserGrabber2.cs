using UnityEngine;
using UnityEngine.InputSystem;
using UCloth;
using Unity.Mathematics;
using System.Collections.Generic;
using System;
using System.Reflection;
using Unity.Collections;

[RequireComponent(typeof(UCCloth), typeof(MeshCollider))]
public class UClothLaserGrabber2 : MonoBehaviour
{
    [Header("VR Interaction - TAY CHÍNH")]
    public Transform vrController;
    public InputActionReference triggerAction;

    [Header("VR Interaction - TAY PHỤ")]
    public Transform rightController;
    public InputActionReference rightTriggerAction;

    [Header("Cloth Physics")]
    public float pullForce = 15f;

    [Header("Visual Feedback - Sphere")]
    public float sphereSize = 0.03f;
    public Color hoverColor = new Color(1f, 1f, 0f, 0.6f);
    public Color grabColor  = new Color(0f, 1f, 0f, 1.0f);

    private UCCloth clothComponent;
    private MeshCollider meshCollider;

    // --- Trạng thái Tay Chính ---
    private bool isGrabbingMain = false;
    private bool wasGrabbingMain = false;
    private ushort grabbedNodeIndexMain = ushort.MaxValue;
    private Vector3 grabOffsetMain;
    private bool isFindingNodeMain = false;

    // --- Trạng thái Tay Phụ ---
    private bool isGrabbingSec = false;
    private bool wasGrabbingSec = false;
    private ushort grabbedNodeIndexSec = ushort.MaxValue;
    private Vector3 grabOffsetSec;
    private bool isFindingNodeSec = false;

    // --- Hover ---
    private Vector3 hoverPointMain;
    private Vector3 hoverPointSec;
    private bool isHoveringMain = false;
    private bool isHoveringSec = false;

    // --- Sphere objects ---
    private GameObject sphereMain;
    private GameObject sphereSec;
    private Material sphereMatMain;
    private Material sphereMatSec;

    // --- UCloth internal arrays ---
    private NativeArray<float3> clothPositions;
    private NativeArray<float3> clothVelocities;
    private bool arraysExtracted = false;

    // --- FIX: Cache vị trí node an toàn, chỉ cập nhật trong OnSimulationFinished ---
    // Không bao giờ đọc clothPositions trực tiếp trong Update()
    private Vector3 cachedNodePosMain = Vector3.zero;
    private Vector3 cachedNodePosSec  = Vector3.zero;

    void Start()
    {
        Debug.Log("[UClothGrabber2] Start() called");

        clothComponent = GetComponent<UCCloth>();
        meshCollider = GetComponent<MeshCollider>();

        if (meshCollider != null && meshCollider.convex)
        {
            Debug.LogWarning("[UClothGrabber2] MeshCollider.convex = true! Tự tắt để raycast hoạt động.");
            meshCollider.convex = false;
        }

        if (triggerAction != null && triggerAction.action != null)
            triggerAction.action.Enable();

        if (rightTriggerAction != null && rightTriggerAction.action != null)
            rightTriggerAction.action.Enable();

        clothComponent.OnSimulationFinished += OnSimulationFinished;

        if (rightController == null)
        {
            GameObject rc = GameObject.Find("Right Controller") ?? GameObject.Find("RightHand Controller");
            if (rc != null) rightController = rc.transform;
        }

        // Tạo sphere tay chính
        sphereMain = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphereMain.name = "GrabSphereMain";
        sphereMain.transform.localScale = Vector3.one * sphereSize;
        Destroy(sphereMain.GetComponent<SphereCollider>());
        sphereMatMain = new Material(Shader.Find("Standard"));
        SetMaterialTransparent(sphereMatMain);
        sphereMatMain.color = hoverColor;
        sphereMain.GetComponent<Renderer>().material = sphereMatMain;
        sphereMain.SetActive(false);

        // Tạo sphere tay phụ
        sphereSec = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphereSec.name = "GrabSphereSec";
        sphereSec.transform.localScale = Vector3.one * sphereSize;
        Destroy(sphereSec.GetComponent<SphereCollider>());
        sphereMatSec = new Material(Shader.Find("Standard"));
        SetMaterialTransparent(sphereMatSec);
        sphereMatSec.color = hoverColor;
        sphereSec.GetComponent<Renderer>().material = sphereMatSec;
        sphereSec.SetActive(false);

        Debug.Log("[UClothGrabber2] Spheres created OK");
    }

    void Update()
    {
        if (!arraysExtracted && clothComponent.simData != null)
            ExtractUClothInternalData();

        ProcessMainHand();
        ProcessSecondaryHand();
    }

    // ==========================================
    // LOGIC TAY CHÍNH
    // ==========================================
    private void ProcessMainHand()
    {
        bool triggerPressed = false;
        if (triggerAction != null && triggerAction.action != null)
            triggerPressed = triggerAction.action.ReadValue<float>() > 0.5f || triggerAction.action.IsPressed();

        // --- HOVER ---
        isHoveringMain = false;
        if (!isGrabbingMain && vrController != null)
        {
            Ray ray = new Ray(vrController.position, vrController.forward);
            if (Physics.Raycast(ray, out RaycastHit hit, 20f) && hit.collider.gameObject == this.gameObject)
            {
                isHoveringMain = true;
                hoverPointMain = hit.point;

                sphereMain.transform.position = hoverPointMain;
                sphereMain.SetActive(true);
                sphereMatMain.color = hoverColor;
            }
            else
            {
                sphereMain.SetActive(false);
            }
        }

        // --- GRAB: Bấm trigger ---
        if (triggerPressed && !wasGrabbingMain && !isFindingNodeMain && vrController != null && isHoveringMain)
        {
            Vector3 fixedHitPoint = hoverPointMain;
            grabOffsetMain = vrController.InverseTransformPoint(fixedHitPoint);
            FindNodeAsyncMain(fixedHitPoint, vrController);
        }
        else if (!triggerPressed)
        {
            isGrabbingMain = false;
            isFindingNodeMain = false;
            grabbedNodeIndexMain = ushort.MaxValue;
        }
        wasGrabbingMain = triggerPressed;

        // --- Đang grab: dùng cachedNodePosMain (an toàn, không đọc NativeArray) ---
        if (isGrabbingMain && grabbedNodeIndexMain != ushort.MaxValue)
        {
            sphereMain.transform.position = cachedNodePosMain;
            sphereMain.SetActive(true);
            sphereMatMain.color = grabColor;
        }
        else if (!isHoveringMain)
        {
            sphereMain.SetActive(false);
        }
    }

    private async void FindNodeAsyncMain(Vector3 hitPoint, Transform controllerAtTime)
    {
        isFindingNodeMain = true;
        UCPointQueryData query = new UCPointQueryData { position = hitPoint, radius = 0.01f };
        List<ushort> closestPoints = await clothComponent.QueryClosestPoints(query);
        if (closestPoints != null && closestPoints.Count > 0)
        {
            grabbedNodeIndexMain = closestPoints[0];
            grabOffsetMain = controllerAtTime.InverseTransformPoint(hitPoint);
            cachedNodePosMain = hitPoint; // Khởi tạo cache ngay lúc grab
            isGrabbingMain = true;
            Debug.Log("[UClothGrabber2] Main grabbed node: " + grabbedNodeIndexMain);
        }
        isFindingNodeMain = false;
    }

    // ==========================================
    // LOGIC TAY PHỤ
    // ==========================================
    private void ProcessSecondaryHand()
    {
        bool triggerPressed = false;
        if (rightTriggerAction != null && rightTriggerAction.action != null)
            triggerPressed = rightTriggerAction.action.ReadValue<float>() > 0.5f || rightTriggerAction.action.IsPressed();

        // --- HOVER ---
        isHoveringSec = false;
        if (!isGrabbingSec && rightController != null)
        {
            Ray ray = new Ray(rightController.position, rightController.forward);
            if (Physics.Raycast(ray, out RaycastHit hit, 20f) && hit.collider.gameObject == this.gameObject)
            {
                isHoveringSec = true;
                hoverPointSec = hit.point;

                sphereSec.transform.position = hoverPointSec;
                sphereSec.SetActive(true);
                sphereMatSec.color = hoverColor;
            }
            else
            {
                sphereSec.SetActive(false);
            }
        }

        // --- GRAB ---
        if (triggerPressed && !wasGrabbingSec && !isFindingNodeSec && rightController != null && isHoveringSec)
        {
            Vector3 fixedHitPoint = hoverPointSec;
            grabOffsetSec = rightController.InverseTransformPoint(fixedHitPoint);
            FindNodeAsyncSec(fixedHitPoint, rightController);
        }
        else if (!triggerPressed)
        {
            isGrabbingSec = false;
            isFindingNodeSec = false;
            grabbedNodeIndexSec = ushort.MaxValue;
        }
        wasGrabbingSec = triggerPressed;

        // --- Đang grab: dùng cachedNodePosSec ---
        if (isGrabbingSec && grabbedNodeIndexSec != ushort.MaxValue)
        {
            sphereSec.transform.position = cachedNodePosSec;
            sphereSec.SetActive(true);
            sphereMatSec.color = grabColor;
        }
        else if (!isHoveringSec)
        {
            sphereSec.SetActive(false);
        }
    }

    private async void FindNodeAsyncSec(Vector3 hitPoint, Transform controllerAtTime)
    {
        isFindingNodeSec = true;
        UCPointQueryData query = new UCPointQueryData { position = hitPoint, radius = 0.01f };
        List<ushort> closestPoints = await clothComponent.QueryClosestPoints(query);
        if (closestPoints != null && closestPoints.Count > 0)
        {
            grabbedNodeIndexSec = closestPoints[0];
            grabOffsetSec = controllerAtTime.InverseTransformPoint(hitPoint);
            cachedNodePosSec = hitPoint;
            isGrabbingSec = true;
            Debug.Log("[UClothGrabber2] Sec grabbed node: " + grabbedNodeIndexSec);
        }
        isFindingNodeSec = false;
    }

    // ==========================================
    // VẬT LÝ CỐT LÕI - chạy SAU KHI job hoàn thành
    // ==========================================
    private void ExtractUClothInternalData()
    {
        Type simDataType = clothComponent.simData.GetType();
        FieldInfo posField = simDataType.GetField("cPositions", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo velField = simDataType.GetField("cVelocity",  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (posField != null) clothPositions  = (NativeArray<float3>)posField.GetValue(clothComponent.simData);
        if (velField != null) clothVelocities = (NativeArray<float3>)velField.GetValue(clothComponent.simData);

        if (clothPositions.IsCreated && clothVelocities.IsCreated)
        {
            arraysExtracted = true;
            Debug.Log("[UClothGrabber2] UCloth arrays extracted OK");
        }
    }

    private void OnSimulationFinished(object sender, EventArgs e)
    {
        // Đây là nơi DUY NHẤT được phép đọc/ghi clothPositions và clothVelocities
        // vì job đã Complete() trước khi event này được gọi
        if (!arraysExtracted) return;

        // Cập nhật cache vị trí node (an toàn vì job đã xong)
        if (isGrabbingMain && grabbedNodeIndexMain != ushort.MaxValue)
        {
            cachedNodePosMain = (Vector3)(clothPositions[grabbedNodeIndexMain]);
            ApplyForceToUClothNode(grabbedNodeIndexMain, vrController.TransformPoint(grabOffsetMain));
        }

        if (isGrabbingSec && grabbedNodeIndexSec != ushort.MaxValue)
        {
            cachedNodePosSec = (Vector3)(clothPositions[grabbedNodeIndexSec]);
            ApplyForceToUClothNode(grabbedNodeIndexSec, rightController.TransformPoint(grabOffsetSec));
        }

        UpdateMeshCollider();
    }

    private void UpdateMeshCollider()
    {
        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf != null && meshCollider != null)
        {
            meshCollider.sharedMesh = null;
            meshCollider.sharedMesh = mf.sharedMesh;
        }
        else
        {
            SkinnedMeshRenderer smr = GetComponent<SkinnedMeshRenderer>();
            if (smr != null && meshCollider != null)
            {
                Mesh bakedMesh = new Mesh();
                smr.BakeMesh(bakedMesh);
                meshCollider.sharedMesh = bakedMesh;
            }
        }
    }

    private void ApplyForceToUClothNode(ushort nodeIndex, Vector3 targetWorldPos)
    {
        float3 currentPos = clothPositions[nodeIndex];
        float3 direction  = (float3)(Vector3)targetWorldPos - currentPos;
        clothVelocities[nodeIndex] = direction * pullForce;
    }

    private void SetMaterialTransparent(Material mat)
    {
        mat.SetFloat("_Mode", 3);
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.renderQueue = 3000;
    }

    void OnDestroy()
    {
        if (clothComponent != null)
            clothComponent.OnSimulationFinished -= OnSimulationFinished;

        if (triggerAction != null && triggerAction.action != null)
            triggerAction.action.Disable();

        if (rightTriggerAction != null && rightTriggerAction.action != null)
            rightTriggerAction.action.Disable();

        if (sphereMain != null) Destroy(sphereMain);
        if (sphereSec  != null) Destroy(sphereSec);
    }
}