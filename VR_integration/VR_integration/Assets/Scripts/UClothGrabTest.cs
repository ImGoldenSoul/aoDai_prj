using UnityEngine;
using UCloth;
using Unity.Mathematics;
using System.Collections.Generic;
using System;
using System.Reflection;
using Unity.Collections;

[RequireComponent(typeof(UCCloth), typeof(MeshCollider))]
public class UClothGrabTest : MonoBehaviour
{
    [Header("Testing Setup")]
    [Tooltip("Kéo vật thể đóng vai trò là Tay Robot (ví dụ: khối Cube) vào đây")]
    public Transform robotController;

    [Tooltip("Bật cờ này để Robot liên tục bắn tia laser tóm vải")]
    public bool isTesting = true;

    [Tooltip("Tốc độ kéo vải theo Robot")]
    public float pullForce = 20f;

    private UCCloth clothComponent;
    private bool isGrabbing = false;
    private ushort grabbedNodeIndex = ushort.MaxValue;
    private Vector3 grabOffset;

    // --- Biến bẻ khóa nội bộ UCloth ---
    private NativeArray<float3> clothPositions;
    private NativeArray<float3> clothVelocities;
    private bool arraysExtracted = false;

    void Start()
    {
        clothComponent = GetComponent<UCCloth>();

        // Đăng ký Event vật lý an toàn
        clothComponent.OnSimulationFinished += OnSimulationFinishedSafe;
    }

    void Update()
    {
        if (!arraysExtracted && clothComponent.simData != null)
        {
            ExtractUClothInternalData();
        }

        // Nếu đang bật chế độ Test và chưa nắm được vải -> Liên tục rà quét
        if (isTesting && !isGrabbing && robotController != null)
        {
            // Vẽ tia laser ĐỎ để quay video báo cáo
            Debug.DrawRay(robotController.position, robotController.forward * 20f, Color.red, 0.05f);

            Ray ray = new Ray(robotController.position, robotController.forward);
            if (Physics.Raycast(ray, out RaycastHit hit, 20f))
            {
                // Nếu tia laser cắt ngang qua miếng vải
                if (hit.collider.gameObject == this.gameObject)
                {
                    grabOffset = robotController.InverseTransformPoint(hit.point);
                    FindNodeAsync(hit.point); // Gọi hàm tóm vải
                }
            }
        }

        // Nếu tắt chế độ Test -> Nhả vải ra
        if (!isTesting && isGrabbing)
        {
            isGrabbing = false;
            grabbedNodeIndex = ushort.MaxValue;
        }
    }

    // --- TÌM HẠT GẦN NHẤT ĐỂ TÓM ---
    private async void FindNodeAsync(Vector3 hitPoint)
    {
        UCPointQueryData query = new UCPointQueryData { position = hitPoint, radius = 0.5f };
        List<ushort> closestPoints = await clothComponent.QueryClosestPoints(query);

        if (closestPoints != null && closestPoints.Count > 0)
        {
            grabbedNodeIndex = closestPoints[0];
            isGrabbing = true;
            Debug.Log($"[Auto-Test] Đã tóm dính hạt vải: {grabbedNodeIndex}. Sẵn sàng kéo!");
        }
    }

    // --- BƠM VẬN TỐC KÉO VẢI (TRONG LUỒNG VẬT LÝ) ---
    private void OnSimulationFinishedSafe(object sender, EventArgs e)
    {
        if (isGrabbing && grabbedNodeIndex != ushort.MaxValue && arraysExtracted)
        {
            // 1. Tính toán vị trí mà Robot muốn hạt vải nằm ở đó
            Vector3 targetWorldPos = robotController.TransformPoint(grabOffset);

            // 2. Tính hướng kéo
            float3 currentPos = clothPositions[grabbedNodeIndex];
            float3 targetPos = targetWorldPos;
            float3 direction = targetPos - currentPos;

            // 3. Bơm vận tốc để ép hạt vải bay theo Robot
            clothVelocities[grabbedNodeIndex] = direction * pullForce;
        }
    }

    private void ExtractUClothInternalData()
    {
        Type simDataType = clothComponent.simData.GetType();
        FieldInfo posField = simDataType.GetField("cPositions", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo velField = simDataType.GetField("cVelocity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (posField != null) clothPositions = (NativeArray<float3>)posField.GetValue(clothComponent.simData);
        if (velField != null) clothVelocities = (NativeArray<float3>)velField.GetValue(clothComponent.simData);

        if (clothPositions.IsCreated && clothVelocities.IsCreated) arraysExtracted = true;
    }

    void OnDestroy()
    {
        if (clothComponent != null) clothComponent.OnSimulationFinished -= OnSimulationFinishedSafe;
    }
}