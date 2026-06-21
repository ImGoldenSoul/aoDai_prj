using UnityEngine;
using UnityEngine.InputSystem;
using UCloth;
using Unity.Mathematics;
using System.Collections.Generic;
using System;
using System.Reflection;
using Unity.Collections;

[RequireComponent(typeof(UCCloth))]
public class UClothPinner2 : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Kéo script UClothLaserGrabber (trên cùng Object) vào đây")]
    public UClothLaserGrabber grabber;

    [Tooltip("Transform của manocanh (hoặc bone gần điểm ghim nhất). " +
             "Nếu gán, các điểm ghim sẽ bám theo manocanh khi nó di chuyển/xoay. " +
             "Để trống nếu chỉ cần ghim cố định trong không gian (manocanh đứng yên).")]
    public Transform mannequinAnchor;

    [Header("Input")]
    [Tooltip("Chọn nút bấm để Ghim (VD: XRI RightHand/Primary Button - Nút A)")]
    public InputActionReference pinAction;

    [Header("Pin Settings")]
    [Tooltip("Độ cứng giữ điểm ghim. Nên để cao hơn pullForce của Laser (Khuyến nghị: 40 - 60)")]
    public float pinForce = 50f;

    [Tooltip("Hệ số giảm chấn (damping), chống rung/giật khi điểm ghim gần tới đích. " +
             "Gợi ý ban đầu: lấy ~2 * sqrt(pinForce) rồi tinh chỉnh thêm. Tăng nếu vẫn rung.")]
    public float pinDamping = 14f;

    [Tooltip("Vận tốc tối đa mà 1 điểm ghim được phép đạt, tránh 'nổ' vải khi vừa bấm ghim ở khoảng cách xa")]
    public float maxPinSpeed = 8f;

    [Tooltip("Khoảng cách (m) dưới mức này coi như đã tới đích, dừng bơm vận tốc để tránh rung li ti do sai số float")]
    public float snapThreshold = 0.001f;

    private UCCloth clothComponent;

    // Lưu ID hạt vải + tọa độ mục tiêu
    // (world-space nếu không gán mannequinAnchor, local-space so với anchor nếu có gán)
    private Dictionary<ushort, Vector3> pinnedNodes = new Dictionary<ushort, Vector3>();

    private NativeArray<float3> clothPositions;
    private NativeArray<float3> clothVelocities;
    private bool arraysExtracted = false;

    private FieldInfo isGrabbingField;
    private FieldInfo grabbedNodeIndexField;
    private FieldInfo grabOffsetField;

    // Hàng đợi đơn giản, xử lý ở OnSimulationFinishedSafe để tránh sửa NativeArray giữa lúc job đang chạy
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

            if (isGrabbing && node != ushort.MaxValue && grabber.vrController != null)
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
                pinnedNodes.Add(_pendingNode, WorldToStored(_pendingTargetWorldPos));
                Debug.Log($"📌 Đã ghim Mềm (Soft Pin) hạt (Index: {_pendingNode}) trên không!");
            }

            _pendingPinToggle = false;
            _pendingNode = ushort.MaxValue;
        }

        // 2. CẬP NHẬT TỌA ĐỘ GHIM NẾU NGƯỜI DÙNG ĐANG NẮM KÉO
        if (grabber != null && isGrabbingField != null && grabber.vrController != null)
        {
            bool isGrabbing = (bool)isGrabbingField.GetValue(grabber);
            ushort node = (ushort)grabbedNodeIndexField.GetValue(grabber);

            if (isGrabbing && pinnedNodes.ContainsKey(node))
            {
                Vector3 offset = (Vector3)grabOffsetField.GetValue(grabber);
                Vector3 worldTarget = grabber.vrController.TransformPoint(offset);
                pinnedNodes[node] = WorldToStored(worldTarget);
            }
        }

        // 3. THỰC THI "GHIM MỀM" BẰNG VẬN TỐC — có giảm chấn (damping) + giới hạn vận tốc + deadzone
        foreach (var kvp in pinnedNodes)
        {
            ushort nodeIndex = kvp.Key;
            if (nodeIndex >= clothPositions.Length) continue; // an toàn nếu số hạt vải thay đổi

            Vector3 targetWorldPos = StoredToWorld(kvp.Value);

            float3 currentPos = clothPositions[nodeIndex];
            float3 currentVel = clothVelocities[nodeIndex];
            float3 toTarget = (float3)targetWorldPos - currentPos;

            if (math.lengthsq(toTarget) < snapThreshold * snapThreshold)
            {
                // Đã đủ gần đích, không bơm thêm vận tốc -> tránh rung li ti do sai số dấu phẩy động
                clothVelocities[nodeIndex] = float3.zero;
                continue;
            }

            // PD: kéo về đích theo sai số (P), trừ một phần vận tốc hiện tại để dập dao động (D)
            float3 desiredVel = toTarget * pinForce - currentVel * pinDamping;

            float speed = math.length(desiredVel);
            if (speed > maxPinSpeed)
                desiredVel = desiredVel / speed * maxPinSpeed;

            clothVelocities[nodeIndex] = desiredVel;
        }
    }

    // Quy đổi từ world-space sang dạng lưu trữ: local so với mannequinAnchor (nếu có), hoặc giữ nguyên world
    private Vector3 WorldToStored(Vector3 worldPos)
    {
        return mannequinAnchor != null ? mannequinAnchor.InverseTransformPoint(worldPos) : worldPos;
    }

    // Quy đổi ngược lại sang world-space hiện tại (nếu manocanh đã di chuyển, kết quả sẽ tự cập nhật theo)
    private Vector3 StoredToWorld(Vector3 stored)
    {
        return mannequinAnchor != null ? mannequinAnchor.TransformPoint(stored) : stored;
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