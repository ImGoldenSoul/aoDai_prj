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
    [Tooltip("Kéo script UClothLaserGrabber (trên cùng Object) vào đây")]
    public UClothLaserGrabber grabber;

    [Header("Input")]
    [Tooltip("Chọn nút bấm để Ghim (VD: XRI RightHand/Primary Button - Nút A)")]
    public InputActionReference pinAction;

    [Header("Pin Settings")]
    [Tooltip("Lực giữ điểm ghim. Nên để cao hơn pullForce của Laser (Khuyến nghị: 40 - 60)")]
    public float pinForce = 50f;

    private UCCloth clothComponent;

    // Chỉ lưu ID hạt vải và Tọa độ mục tiêu
    private Dictionary<ushort, Vector3> pinnedNodes = new Dictionary<ushort, Vector3>();

    private NativeArray<float3> clothPositions;
    private NativeArray<float3> clothVelocities;
    private bool arraysExtracted = false;

    private FieldInfo isGrabbingField;
    private FieldInfo grabbedNodeIndexField;
    private FieldInfo grabOffsetField;

    // Hàng đợi an toàn luồng (Thread-safe Queue)
    private bool _pendingPinToggle = false;
    private ushort _pendingNode = ushort.MaxValue;
    private Vector3 _pendingTargetWorldPos;

    void Start()
    {
        clothComponent = GetComponent<UCCloth>();
        if (grabber == null) grabber = GetComponent<UClothLaserGrabber>();

        if (pinAction != null && pinAction.action != null)
            pinAction.action.Enable();

        if (grabber != null)
        {
            Type grabberType = grabber.GetType();
            isGrabbingField = grabberType.GetField("isGrabbing", BindingFlags.NonPublic | BindingFlags.Instance);
            grabbedNodeIndexField = grabberType.GetField("grabbedNodeIndex", BindingFlags.NonPublic | BindingFlags.Instance);
            grabOffsetField = grabberType.GetField("grabOffset", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        clothComponent.OnSimulationFinished += OnSimulationFinishedSafe;
    }

    void Update()
    {
        if (!arraysExtracted && clothComponent.simData != null)
            ExtractUClothInternalData();

        bool pinPressed = pinAction != null && pinAction.action != null && pinAction.action.WasPressedThisFrame();

        if (pinPressed && arraysExtracted && grabber != null && isGrabbingField != null)
        {
            bool isGrabbing = (bool)isGrabbingField.GetValue(grabber);
            ushort node = (ushort)grabbedNodeIndexField.GetValue(grabber);

            if (isGrabbing && node != ushort.MaxValue)
            {
                _pendingPinToggle = true;
                _pendingNode = node;

                Vector3 offset = (Vector3)grabOffsetField.GetValue(grabber);
                _pendingTargetWorldPos = grabber.vrController.TransformPoint(offset);
            }
        }
    }

    private void OnSimulationFinishedSafe(object sender, EventArgs e)
    {
        if (!arraysExtracted) return;

        // 1. CẬP NHẬT DANH SÁCH GHIM
        if (_pendingPinToggle && _pendingNode != ushort.MaxValue)
        {
            if (pinnedNodes.ContainsKey(_pendingNode))
            {
                pinnedNodes.Remove(_pendingNode);
                Debug.Log($"🔓 Đã tháo ghim Mềm (Soft Pin), hạt (Index: {_pendingNode}) rơi tự do.");
            }
            else
            {
                pinnedNodes.Add(_pendingNode, _pendingTargetWorldPos);
                Debug.Log($"📌 Đã ghim Mềm (Soft Pin) hạt (Index: {_pendingNode}) trên không!");
            }

            _pendingPinToggle = false;
            _pendingNode = ushort.MaxValue;
        }

        // 2. CẬP NHẬT TỌA ĐỘ GHIM NẾU NGƯỜI DÙNG ĐANG NẮM KÉO
        if (grabber != null && isGrabbingField != null)
        {
            bool isGrabbing = (bool)isGrabbingField.GetValue(grabber);
            ushort node = (ushort)grabbedNodeIndexField.GetValue(grabber);

            if (isGrabbing && pinnedNodes.ContainsKey(node))
            {
                Vector3 offset = (Vector3)grabOffsetField.GetValue(grabber);
                pinnedNodes[node] = grabber.vrController.TransformPoint(offset);
            }
        }

        // 3. THỰC THI "GHIM MỀM" BẰNG VẬN TỐC
        foreach (var kvp in pinnedNodes)
        {
            ushort nodeIndex = kvp.Key;
            Vector3 targetPos = kvp.Value;

            float3 currentPos = clothPositions[nodeIndex];
            float3 direction = (float3)targetPos - currentPos;

            // Liên tục bơm vận tốc kéo điểm này về vị trí ghim
            // Không can thiệp vào khối lượng, giúp UCloth không bị lỗi Constraint
            clothVelocities[nodeIndex] = direction * pinForce;
        }
    }

    private void ExtractUClothInternalData()
    {
        Type simDataType = clothComponent.simData.GetType();

        FieldInfo posField = simDataType.GetField("cPositions", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo velField = simDataType.GetField("cVelocity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (posField != null) clothPositions = (NativeArray<float3>)posField.GetValue(clothComponent.simData);
        if (velField != null) clothVelocities = (NativeArray<float3>)velField.GetValue(clothComponent.simData);

        if (clothPositions.IsCreated && clothVelocities.IsCreated)
        {
            arraysExtracted = true;
            Debug.Log("✅ Soft Pinner đã bẻ khóa thành công!");
        }
    }

    void OnDestroy()
    {
        if (clothComponent != null) clothComponent.OnSimulationFinished -= OnSimulationFinishedSafe;
        if (pinAction != null && pinAction.action != null) pinAction.action.Disable();

        // Phiên bản này an toàn tuyệt đối, không cần phục hồi dữ liệu vì ta không sửa mass của hệ thống
    }
}