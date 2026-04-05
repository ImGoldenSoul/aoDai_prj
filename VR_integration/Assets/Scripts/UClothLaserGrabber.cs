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
    [Header("VR Interaction")]
    [Tooltip("Kéo Right Controller vào đây để làm nguồn bắn tia Laser")]
    public Transform vrController;

    [Tooltip("Quả cầu đỏ hiển thị điểm tay cầm (Target)")]
    public Transform grabSphere;

    [Tooltip("Chọn XRI RightHand/Select Value")]
    public InputActionReference triggerAction;

    [Header("Cloth Physics")]
    [Tooltip("Tốc độ kéo vải bay về phía tay cầm (Khuyến nghị: 10 - 20)")]
    public float pullForce = 15f;

    private UCCloth clothComponent;
    private MeshCollider meshCollider;

    private bool isGrabbing = false;
    private bool wasGrabbing = false;
    private ushort grabbedNodeIndex = ushort.MaxValue;
    private Vector3 grabOffset;

    // --- Các biến dùng để lưu pointer bẻ khóa từ UCloth ---
    private NativeArray<float3> clothPositions;
    private NativeArray<float3> clothVelocities;
    private NativeArray<float3> clothAccelerations;
    private bool arraysExtracted = false;

    void Start()
    {
        clothComponent = GetComponent<UCCloth>();
        meshCollider = GetComponent<MeshCollider>();

        if (triggerAction != null && triggerAction.action != null)
            triggerAction.action.Enable();

        if (grabSphere != null)
            grabSphere.gameObject.SetActive(false);

        // Đăng ký Event: Chỉ can thiệp vật lý khi UCloth vừa tính toán xong 1 frame
        clothComponent.OnSimulationFinished += OnSimulationFinished;
    }

    void Update()
    {
        // 0. BẺ KHÓA DỮ LIỆU
        if (!arraysExtracted && clothComponent.simData != null)
        {
            ExtractUClothInternalData();
        }

        bool triggerPressed = false;
        if (triggerAction != null && triggerAction.action != null)
        {
            // Nâng cấp cách đọc nút bấm để chống trượt khi dùng Simulator
            triggerPressed = triggerAction.action.ReadValue<float>() > 0.5f || triggerAction.action.IsPressed();
        }

        // 1. KHI VỪA BÓP CÒ: Bắn Laser
        if (triggerPressed && !wasGrabbing && vrController != null)
        {
            // === LOG BƯỚC 1 ===
            Debug.Log("🟡 BƯỚC 1: Đã nhận tín hiệu bóp cò (Trigger)!");

            Ray ray = new Ray(vrController.position, vrController.forward);

            // Vẽ một tia Laser MÀU ĐỎ trong tab SCENE (tồn tại 2 giây) để bạn nhìn rõ nó bắn đi đâu
            Debug.DrawRay(vrController.position, vrController.forward * 20f, Color.red, 2f);

            if (Physics.Raycast(ray, out RaycastHit hit, 20f))
            {
                // === LOG BƯỚC 2 ===
                Debug.Log($"🟡 BƯỚC 2: Tia Laser trúng vật thể tên là: {hit.collider.gameObject.name}");

                if (hit.collider.gameObject == this.gameObject)
                {
                    // === LOG BƯỚC 3 ===
                    Debug.Log("🟢 BƯỚC 3: Trúng CHUẨN miếng vải UCloth! Đang gọi hàm tìm hạt...");
                    grabOffset = vrController.InverseTransformPoint(hit.point);
                    FindNodeAsync(hit.point);
                }
                else
                {
                    Debug.LogWarning("🔴 LỖI: Laser trượt miếng vải (Trúng vật khác)!");
                }
            }
            else
            {
                Debug.LogWarning("🔴 LỖI: Tia Laser bắn vào không khí (Xuyên qua vải hoặc lệch hướng)!");
            }
        }
        else if (!triggerPressed)
        {
            isGrabbing = false;
            grabbedNodeIndex = ushort.MaxValue;
        }
        wasGrabbing = triggerPressed;

        // 2. KHI ĐANG GIỮ CÒ: Cập nhật vị trí quả cầu mục tiêu
        if (isGrabbing && grabbedNodeIndex != ushort.MaxValue)
        {
            Vector3 targetWorldPos = vrController.TransformPoint(grabOffset);
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

    // --- HÀM BẺ KHÓA BẰNG REFLECTION ---
    private void ExtractUClothInternalData()
    {
        Type simDataType = clothComponent.simData.GetType();

        FieldInfo posField = simDataType.GetField("cPositions", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo velField = simDataType.GetField("cVelocity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo accField = simDataType.GetField("cAcceleration", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (posField != null) clothPositions = (NativeArray<float3>)posField.GetValue(clothComponent.simData);
        if (velField != null) clothVelocities = (NativeArray<float3>)velField.GetValue(clothComponent.simData);
        if (accField != null) clothAccelerations = (NativeArray<float3>)accField.GetValue(clothComponent.simData);

        if (clothPositions.IsCreated && clothVelocities.IsCreated && clothAccelerations.IsCreated)
        {
            arraysExtracted = true;
            Debug.Log("✅ Đã bẻ khóa thành công dữ liệu nội bộ của UCloth!");
        }
    }

    // --- TÌM HẠT GẦN NHẤT ---
    private async void FindNodeAsync(Vector3 hitPoint)
    {
        UCPointQueryData query = new UCPointQueryData
        {
            position = hitPoint,
            radius = 0.5f // Tăng bán kính quét lên 50cm
        };

        List<ushort> closestPoints = await clothComponent.QueryClosestPoints(query);

        if (closestPoints != null && closestPoints.Count > 0)
        {
            grabbedNodeIndex = closestPoints[0];
            isGrabbing = true;
            Debug.Log($"🎯 ĐÃ TÓM ĐƯỢC HẠT VẢI (Index: {grabbedNodeIndex})");
        }
        else
        {
            Debug.LogWarning("❌ Laser trúng vải nhưng không tìm thấy hạt nào! (Hãy thử bắn vào giữa miếng vải)");
        }
    }

    // --- BƠM LỰC AN TOÀN TRONG EVENT ---
    private void OnSimulationFinished(object sender, EventArgs e)
    {
        if (isGrabbing && grabbedNodeIndex != ushort.MaxValue && arraysExtracted)
        {
            Vector3 targetWorldPos = vrController.TransformPoint(grabOffset);
            ApplyForceToUClothNode(grabbedNodeIndex, targetWorldPos);
        }
    }

    // --- XỬ LÝ VẬT LÝ CỐT LÕI ---
    private void ApplyForceToUClothNode(ushort nodeIndex, Vector3 targetWorldPos)
    {
        float3 currentPos = clothPositions[nodeIndex];
        float3 targetPos = targetWorldPos;

        // Tính hướng đi từ hạt vải hiện tại đến tay cầm
        float3 direction = targetPos - currentPos;

        // CÁCH 1: ÉP VẬN TỐC (Velocity Override)
        // Ép thẳng vận tốc của hạt vải hướng về phía quả cầu mục tiêu với tốc độ = pullForce
        clothVelocities[nodeIndex] = direction * pullForce;
    }

    void OnDestroy()
    {
        if (clothComponent != null)
            clothComponent.OnSimulationFinished -= OnSimulationFinished;

        if (triggerAction != null && triggerAction.action != null)
            triggerAction.action.Disable();
    }
}