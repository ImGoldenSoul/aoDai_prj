using UnityEngine;
using UnityEngine.InputSystem;
using UCloth;
using Unity.Mathematics;
using System.Collections.Generic;
using System;
using System.Reflection;
using Unity.Collections;

[RequireComponent(typeof(UCCloth), typeof(MeshCollider))]
public class UClothLaserGrabber : MonoBehaviour
{
    [Header("VR Interaction - TAY CHÍNH (Giữ nguyên để không lỗi code khác)")]
    public Transform vrController;
    public Transform grabSphere;
    public InputActionReference triggerAction;

    [Header("VR Interaction - TAY PHỤ (Mới thêm)")]
    public Transform rightController;
    public Transform rightGrabSphere;
    public InputActionReference rightTriggerAction;

    [Header("Cloth Physics")]
    public float pullForce = 15f;

    private UCCloth clothComponent;
    private MeshCollider meshCollider;

    // --- Trạng thái Tay Chính ---
    private bool isGrabbingMain = false;
    private bool wasGrabbingMain = false;
    private ushort grabbedNodeIndexMain = ushort.MaxValue;
    private Vector3 grabOffsetMain;

    // --- Trạng thái Tay Phụ ---
    private bool isGrabbingSec = false;
    private bool wasGrabbingSec = false;
    private ushort grabbedNodeIndexSec = ushort.MaxValue;
    private Vector3 grabOffsetSec;

    // --- Các biến dùng để lưu pointer bẻ khóa từ UCloth ---
    private NativeArray<float3> clothPositions;
    private NativeArray<float3> clothVelocities;
    private NativeArray<float3> clothAccelerations;
    private bool arraysExtracted = false;

    void Start()
    {
        clothComponent = GetComponent<UCCloth>();
        meshCollider = GetComponent<MeshCollider>();

        // Kích hoạt input cho cả 2 tay
        if (triggerAction != null && triggerAction.action != null)
            triggerAction.action.Enable();

        if (rightTriggerAction != null && rightTriggerAction.action != null)
            rightTriggerAction.action.Enable();

        if (grabSphere != null) grabSphere.gameObject.SetActive(false);
        if (rightGrabSphere != null) rightGrabSphere.gameObject.SetActive(false);

        clothComponent.OnSimulationFinished += OnSimulationFinished;

        // Tự động tìm tay phải nếu bị trống (Hỗ trợ khi đẻ vải bằng code)
        if (rightController == null)
        {
            GameObject rc = GameObject.Find("Right Controller") ?? GameObject.Find("RightHand Controller");
            if (rc != null) rightController = rc.transform;
        }
    }

    void Update()
    {
        // 0. BẺ KHÓA DỮ LIỆU
        if (!arraysExtracted && clothComponent.simData != null)
        {
            ExtractUClothInternalData();
        }

        // 1. XỬ LÝ TAY CHÍNH
        ProcessMainHand();

        // 2. XỬ LÝ TAY PHỤ
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

        if (triggerPressed && !wasGrabbingMain && vrController != null)
        {
            Ray ray = new Ray(vrController.position, vrController.forward);
            if (Physics.Raycast(ray, out RaycastHit hit, 20f))
            {
                if (hit.collider.gameObject == this.gameObject)
                {
                    grabOffsetMain = vrController.InverseTransformPoint(hit.point);
                    FindNodeAsyncMain(hit.point);
                }
            }
        }
        else if (!triggerPressed)
        {
            isGrabbingMain = false;
            grabbedNodeIndexMain = ushort.MaxValue;
        }
        wasGrabbingMain = triggerPressed;

        if (isGrabbingMain && grabbedNodeIndexMain != ushort.MaxValue)
        {
            Vector3 targetWorldPos = vrController.TransformPoint(grabOffsetMain);
            if (grabSphere != null)
            {
                grabSphere.position = targetWorldPos;
                grabSphere.gameObject.SetActive(true);
            }
        }
        else
        {
            if (grabSphere != null) grabSphere.gameObject.SetActive(false);
        }
    }

    private async void FindNodeAsyncMain(Vector3 hitPoint)
    {
        UCPointQueryData query = new UCPointQueryData { position = hitPoint, radius = 0.5f };
        List<ushort> closestPoints = await clothComponent.QueryClosestPoints(query);

        if (closestPoints != null && closestPoints.Count > 0)
        {
            grabbedNodeIndexMain = closestPoints[0];
            isGrabbingMain = true;
        }
    }

    // ==========================================
    // LOGIC TAY PHỤ
    // ==========================================
    private void ProcessSecondaryHand()
    {
        bool triggerPressed = false;
        if (rightTriggerAction != null && rightTriggerAction.action != null)
            triggerPressed = rightTriggerAction.action.ReadValue<float>() > 0.5f || rightTriggerAction.action.IsPressed();

        if (triggerPressed && !wasGrabbingSec && rightController != null)
        {
            Ray ray = new Ray(rightController.position, rightController.forward);
            if (Physics.Raycast(ray, out RaycastHit hit, 20f))
            {
                if (hit.collider.gameObject == this.gameObject)
                {
                    grabOffsetSec = rightController.InverseTransformPoint(hit.point);
                    FindNodeAsyncSec(hit.point);
                }
            }
        }
        else if (!triggerPressed)
        {
            isGrabbingSec = false;
            grabbedNodeIndexSec = ushort.MaxValue;
        }
        wasGrabbingSec = triggerPressed;

        if (isGrabbingSec && grabbedNodeIndexSec != ushort.MaxValue)
        {
            Vector3 targetWorldPos = rightController.TransformPoint(grabOffsetSec);
            if (rightGrabSphere != null)
            {
                rightGrabSphere.position = targetWorldPos;
                rightGrabSphere.gameObject.SetActive(true);
            }
        }
        else
        {
            if (rightGrabSphere != null) rightGrabSphere.gameObject.SetActive(false);
        }
    }

    private async void FindNodeAsyncSec(Vector3 hitPoint)
    {
        UCPointQueryData query = new UCPointQueryData { position = hitPoint, radius = 0.5f };
        List<ushort> closestPoints = await clothComponent.QueryClosestPoints(query);

        if (closestPoints != null && closestPoints.Count > 0)
        {
            grabbedNodeIndexSec = closestPoints[0];
            isGrabbingSec = true;
        }
    }

    // ==========================================
    // VẬT LÝ CỐT LÕI (BƠM LỰC)
    // ==========================================
    private void ExtractUClothInternalData()
    {
        Type simDataType = clothComponent.simData.GetType();

        FieldInfo posField = simDataType.GetField("cPositions", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo velField = simDataType.GetField("cVelocity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo accField = simDataType.GetField("cAcceleration", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (posField != null) clothPositions = (NativeArray<float3>)posField.GetValue(clothComponent.simData);
        if (velField != null) clothVelocities = (NativeArray<float3>)velField.GetValue(clothComponent.simData);
        if (accField != null) accField.GetValue(clothComponent.simData); // Gia tốc không dùng tới lực trực tiếp nên giữ tham chiếu

        if (clothPositions.IsCreated && clothVelocities.IsCreated)
        {
            arraysExtracted = true;
        }
    }

    private void OnSimulationFinished(object sender, EventArgs e)
    {
        if (!arraysExtracted) return;

        // Bơm lực cho Tay Chính
        if (isGrabbingMain && grabbedNodeIndexMain != ushort.MaxValue && vrController != null)
        {
            Vector3 targetWorldPos = vrController.TransformPoint(grabOffsetMain);
            ApplyForceToUClothNode(grabbedNodeIndexMain, targetWorldPos);
        }

        // Bơm lực cho Tay Phụ
        if (isGrabbingSec && grabbedNodeIndexSec != ushort.MaxValue && rightController != null)
        {
            Vector3 targetWorldPos = rightController.TransformPoint(grabOffsetSec);
            ApplyForceToUClothNode(grabbedNodeIndexSec, targetWorldPos);
        }
    }

    private void ApplyForceToUClothNode(ushort nodeIndex, Vector3 targetWorldPos)
    {
        float3 currentPos = clothPositions[nodeIndex];
        float3 targetPos = targetWorldPos;
        float3 direction = targetPos - currentPos;

        clothVelocities[nodeIndex] = direction * pullForce;
    }

    void OnDestroy()
    {
        if (clothComponent != null)
            clothComponent.OnSimulationFinished -= OnSimulationFinished;

        if (triggerAction != null && triggerAction.action != null)
            triggerAction.action.Disable();

        if (rightTriggerAction != null && rightTriggerAction.action != null)
            rightTriggerAction.action.Disable();
    }
}