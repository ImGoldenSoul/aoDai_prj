using UnityEngine;
using UnityEngine.InputSystem;
using UCloth;
using Unity.Mathematics;
using System.Collections.Generic;
using System;
using System.Reflection;
using Unity.Collections;

[RequireComponent(typeof(UCCloth))]
public class UClothPinner : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Kéo script UClothLaserGrabber (trên cùng Object này) vào đây")]
    public UClothLaserGrabber grabber;

    [Header("Input")]
    [Tooltip("Chọn nút bấm để Ghim (VD: XRI RightHand/Primary Button - Nút A)")]
    public InputActionReference pinAction;

    private UCCloth clothComponent;
    private Dictionary<ushort, float3> pinnedNodes = new Dictionary<ushort, float3>();

    private NativeArray<float3> clothPositions;
    private NativeArray<float3> clothVelocities;
    private bool arraysExtracted = false;

    // --- Biến dùng để bẻ khóa (Reflection) UClothLaserGrabber ---
    private FieldInfo isGrabbingField;
    private FieldInfo grabbedNodeIndexField;
    private FieldInfo grabOffsetField;

    void Start()
    {
        clothComponent = GetComponent<UCCloth>();
        if (grabber == null) grabber = GetComponent<UClothLaserGrabber>();

        if (pinAction != null && pinAction.action != null)
            pinAction.action.Enable();

        // 1. SETUP BẺ KHÓA SCRIPT GRABBER
        if (grabber != null)
        {
            Type grabberType = grabber.GetType();
            // Lấy quyền truy cập vào các biến private của Grabber
            isGrabbingField = grabberType.GetField("isGrabbing", BindingFlags.NonPublic | BindingFlags.Instance);
            grabbedNodeIndexField = grabberType.GetField("grabbedNodeIndex", BindingFlags.NonPublic | BindingFlags.Instance);
            grabOffsetField = grabberType.GetField("grabOffset", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        clothComponent.OnSimulationFinished += OnSimulationFinished;
    }

    void Update()
    {
        if (!arraysExtracted && clothComponent.simData != null) ExtractUClothInternalData();

        bool pinPressed = pinAction != null && pinAction.action != null && pinAction.action.WasPressedThisFrame();

        // NẾU BẤM NÚT GHIM VÀ ĐÃ BẺ KHÓA THÀNH CÔNG GRABBER
        if (pinPressed && grabber != null && isGrabbingField != null)
        {
            // Đọc trộm giá trị hiện tại của Grabber
            bool isGrabbing = (bool)isGrabbingField.GetValue(grabber);
            ushort node = (ushort)grabbedNodeIndexField.GetValue(grabber);

            if (isGrabbing && node != ushort.MaxValue)
            {
                if (pinnedNodes.ContainsKey(node))
                {
                    pinnedNodes.Remove(node); // Bấm lần 2 để tháo ghim
                    Debug.Log($"🔓 Đã tháo ghim hạt vải (Index: {node})");
                }
                else
                {
                    // Lấy vị trí tay cầm để tính tọa độ ghim
                    Vector3 offset = (Vector3)grabOffsetField.GetValue(grabber);
                    Vector3 targetWorldPos = grabber.vrController.TransformPoint(offset);
                    
                    pinnedNodes.Add(node, targetWorldPos); // Bấm lần 1 để ghim
                    Debug.Log($"📌 Đã ghim hạt vải (Index: {node}) trên không!");
                }
            }
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

    private void OnSimulationFinished(object sender, EventArgs e)
    {
        if (!arraysExtracted) return;

        // 1. Nếu cầm laser nắm lại cái "Ghim" và kéo đi, cập nhật tọa độ ghim mới
        if (grabber != null && isGrabbingField != null)
        {
            bool isGrabbing = (bool)isGrabbingField.GetValue(grabber);
            ushort node = (ushort)grabbedNodeIndexField.GetValue(grabber);

            if (isGrabbing && pinnedNodes.ContainsKey(node))
            {
                Vector3 offset = (Vector3)grabOffsetField.GetValue(grabber);
                Vector3 targetWorldPos = grabber.vrController.TransformPoint(offset);
                pinnedNodes[node] = targetWorldPos;
            }
        }

        // 2. ÉP TỌA ĐỘ VÀ KHÓA VẬN TỐC CÁC ĐIỂM GHIM
        foreach (var kvp in pinnedNodes)
        {
            clothPositions[kvp.Key] = kvp.Value;
            clothVelocities[kvp.Key] = float3.zero; // Ghi đè vận tốc = 0, vô hiệu hóa lực kéo thừa nếu có
        }
    }

    void OnDestroy()
    {
        if (clothComponent != null) clothComponent.OnSimulationFinished -= OnSimulationFinished;
        if (pinAction != null && pinAction.action != null) pinAction.action.Disable();
    }
}