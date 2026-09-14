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
    [Tooltip("Kéo script UClothLaserGrabber3 (trên cùng Object) vào đây")]
    public UClothLaserGrabber3 grabber;

    [Tooltip("Transform của manocanh (hoặc bone gần điểm ghim nhất). " +
             "Nếu gán, các điểm ghim sẽ bám theo manocanh khi nó di chuyển/xoay. " +
             "Để trống nếu chỉ cần ghim cố định trong không gian (manocanh đứng yên).")]
    public Transform mannequinAnchor;

    [Header("Data Collection Link")]
    [Tooltip("Kéo object chứa script ClothPinLogger vào đây để ghi nhận dữ liệu bài test Pinning")]
    public ClothPinLogger pinLogger;

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

    [Header("Debug")]
    [Tooltip("Bật để in log chi tiết từng bước, giúp tìm lỗi tại sao không ghim được")]
    public bool debugMode = true;

    private UCCloth clothComponent;

    // Lưu ID hạt vải + tọa độ mục tiêu
    private Dictionary<ushort, Vector3> pinnedNodes = new Dictionary<ushort, Vector3>();

    private NativeArray<float3> clothPositions;
    private NativeArray<float3> clothVelocities;
    private bool arraysExtracted = false;

    // --- Reflection Fields Tay Chính ---
    private FieldInfo isGrabbingMainField;
    private FieldInfo grabbedNodeIndexMainField;
    private FieldInfo grabOffsetMainField;

    // --- Reflection Fields Tay Phụ ---
    private FieldInfo isGrabbingSecField;
    private FieldInfo grabbedNodeIndexSecField;
    private FieldInfo grabOffsetSecField;

    private bool _pendingPinToggle = false;
    private ushort _pendingNode = ushort.MaxValue;
    private Vector3 _pendingTargetWorldPos;

    void Start()
    {
        clothComponent = GetComponent<UCCloth>();
        if (clothComponent == null)
        {
            Debug.LogError("❌ [Pinner] Không tìm thấy UCCloth trên cùng GameObject. Script sẽ không hoạt động.");
            return;
        }

        if (grabber == null) grabber = GetComponent<UClothLaserGrabber3>();
        if (grabber == null)
        {
            Debug.LogError("❌ [Pinner] Không tìm thấy UClothLaserGrabber3. Kéo script vào Inspector.");
        }
        else if (debugMode)
        {
            Debug.Log($"ℹ️ [Pinner] Đã gán grabber: {grabber.name}");
        }

        if (pinAction == null || pinAction.action == null)
        {
            Debug.LogError("❌ [Pinner] Field 'pinAction' đang để TRỐNG hoặc hỏng.");
        }
        else
        {
            pinAction.action.Enable();
        }

        if (grabber != null)
        {
            Type grabberType = grabber.GetType();
            
            // Lấy dữ liệu nội bộ tay chính
            isGrabbingMainField = grabberType.GetField("isGrabbingMain", BindingFlags.NonPublic | BindingFlags.Instance);
            grabbedNodeIndexMainField = grabberType.GetField("grabbedNodeIndexMain", BindingFlags.NonPublic | BindingFlags.Instance);
            grabOffsetMainField = grabberType.GetField("grabOffsetMain", BindingFlags.NonPublic | BindingFlags.Instance);

            // Lấy dữ liệu nội bộ tay phụ
            isGrabbingSecField = grabberType.GetField("isGrabbingSec", BindingFlags.NonPublic | BindingFlags.Instance);
            grabbedNodeIndexSecField = grabberType.GetField("grabbedNodeIndexSec", BindingFlags.NonPublic | BindingFlags.Instance);
            grabOffsetSecField = grabberType.GetField("grabOffsetSec", BindingFlags.NonPublic | BindingFlags.Instance);

            if (isGrabbingMainField == null || isGrabbingSecField == null)
            {
                Debug.LogError("❌ [Pinner] Reflection KHÔNG tìm thấy field trạng thái nắm từ UClothLaserGrabber3.");
            }
            else if (debugMode)
            {
                Debug.Log("✅ [Pinner] Reflection lấy được đủ dữ liệu nội bộ cho cả 2 tay từ UClothLaserGrabber3.");
            }
        }

        clothComponent.OnSimulationFinished += OnSimulationFinishedSafe;
    }

    void Update()
    {
        if (!arraysExtracted && clothComponent.simData != null)
            ExtractUClothInternalData();

        if (pinAction == null || pinAction.action == null || !pinAction.action.WasPressedThisFrame()) 
            return;

        if (!arraysExtracted || grabber == null || isGrabbingMainField == null) return;

        bool isGrabbingMain = (bool)isGrabbingMainField.GetValue(grabber);
        bool isGrabbingSec = (bool)isGrabbingSecField.GetValue(grabber);

        ushort nodeToPin = ushort.MaxValue;
        Vector3 targetPos = Vector3.zero;

        // Ưu tiên kiểm tra tay chính trước, sau đó đến tay phụ
        if (isGrabbingMain)
        {
            ushort node = (ushort)grabbedNodeIndexMainField.GetValue(grabber);
            if (node != ushort.MaxValue && grabber.vrController != null)
            {
                nodeToPin = node;
                Vector3 offset = (Vector3)grabOffsetMainField.GetValue(grabber);
                targetPos = grabber.vrController.TransformPoint(offset);
            }
        }
        else if (isGrabbingSec)
        {
            ushort node = (ushort)grabbedNodeIndexSecField.GetValue(grabber);
            if (node != ushort.MaxValue && grabber.rightController != null)
            {
                nodeToPin = node;
                Vector3 offset = (Vector3)grabOffsetSecField.GetValue(grabber);
                targetPos = grabber.rightController.TransformPoint(offset);
            }
        }

        if (nodeToPin == ushort.MaxValue)
        {
            if (debugMode) Debug.LogWarning("⚠️ [Pinner] Bấm nút Pin nhưng không tay nào đang nắm hạt vải hợp lệ.");
            return;
        }

        _pendingPinToggle = true;
        _pendingNode = nodeToPin;
        _pendingTargetWorldPos = targetPos;

        if (debugMode)
            Debug.Log($"✅ [Pinner] Yêu cầu ghim đưa vào hàng đợi cho node {_pendingNode}.");
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
                if(debugMode) Debug.Log($"🔓 Đã tháo ghim Mềm, hạt (Index: {_pendingNode}) rơi tự do.");
            }
            else
            {
                pinnedNodes.Add(_pendingNode, WorldToStored(_pendingTargetWorldPos));
                if(debugMode) Debug.Log($"📌 Đã ghim Mềm hạt (Index: {_pendingNode}) trên không!");

                // === BÁO CÁO CHO DATA LOGGER ===
                // Gửi thông báo tọa độ mà người dùng vừa ghim thành công sang cho hệ thống Log
                if (pinLogger != null)
                {
                    pinLogger.NotifyPinPlaced(_pendingTargetWorldPos);
                }
            }

            _pendingPinToggle = false;
            _pendingNode = ushort.MaxValue;
        }

        // 2. CẬP NHẬT TỌA ĐỘ GHIM NẾU NGƯỜI DÙNG ĐANG NẮM KÉO
        if (grabber != null)
        {
            // Kiểm tra tay chính
            if (isGrabbingMainField != null)
            {
                bool isGrabbingMain = (bool)isGrabbingMainField.GetValue(grabber);
                ushort nodeMain = (ushort)grabbedNodeIndexMainField.GetValue(grabber);
                if (isGrabbingMain && pinnedNodes.ContainsKey(nodeMain) && grabber.vrController != null)
                {
                    Vector3 offset = (Vector3)grabOffsetMainField.GetValue(grabber);
                    Vector3 worldTarget = grabber.vrController.TransformPoint(offset);
                    pinnedNodes[nodeMain] = WorldToStored(worldTarget);
                }
            }

            // Kiểm tra tay phụ
            if (isGrabbingSecField != null)
            {
                bool isGrabbingSec = (bool)isGrabbingSecField.GetValue(grabber);
                ushort nodeSec = (ushort)grabbedNodeIndexSecField.GetValue(grabber);
                if (isGrabbingSec && pinnedNodes.ContainsKey(nodeSec) && grabber.rightController != null)
                {
                    Vector3 offset = (Vector3)grabOffsetSecField.GetValue(grabber);
                    Vector3 worldTarget = grabber.rightController.TransformPoint(offset);
                    pinnedNodes[nodeSec] = WorldToStored(worldTarget);
                }
            }
        }

        // 3. THỰC THI "GHIM MỀM" BẰNG VẬN TỐC
        foreach (var kvp in pinnedNodes)
        {
            ushort nodeIndex = kvp.Key;
            if (nodeIndex >= clothPositions.Length) continue;

            Vector3 targetWorldPos = StoredToWorld(kvp.Value);
            float3 currentPos = clothPositions[nodeIndex];
            float3 currentVel = clothVelocities[nodeIndex];
            float3 toTarget = (float3)targetWorldPos - currentPos;

            if (math.lengthsq(toTarget) < snapThreshold * snapThreshold)
            {
                clothVelocities[nodeIndex] = float3.zero;
                continue;
            }

            float3 desiredVel = toTarget * pinForce - currentVel * pinDamping;
            float speed = math.length(desiredVel);
            if (speed > maxPinSpeed)
                desiredVel = desiredVel / speed * maxPinSpeed;

            clothVelocities[nodeIndex] = desiredVel;
        }
    }

    private Vector3 WorldToStored(Vector3 worldPos)
    {
        return mannequinAnchor != null ? mannequinAnchor.InverseTransformPoint(worldPos) : worldPos;
    }

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
            if(debugMode) Debug.Log($"✅ Soft Pinner đã bẻ khóa thành công! ({clothPositions.Length} hạt vải)");
        }
    }

    void OnDestroy()
    {
        if (clothComponent != null) clothComponent.OnSimulationFinished -= OnSimulationFinishedSafe;

        // FIX: pinAction là InputActionReference dùng CHUNG (shared asset) giữa tất cả các
        // UClothPinner2 — kể cả các piece sinh ra sau khi cắt/khâu. Nếu Disable() ở đây,
        // khi mesh gốc bị SetActive(false)/Destroy sau khi cắt, action sẽ bị tắt và tất
        // cả pinner mới trên các piece không nhận được input nữa (pin button không phản hồi).
        // Sửa: chỉ Disable action nếu KHÔNG còn UClothPinner2 nào khác trong scene đang
        // dùng cùng action này. Nếu còn pinner khác đang sống → giữ action enabled.
        if (pinAction != null && pinAction.action != null)
        {
            bool anotherPinnerExists = false;
            foreach (var other in FindObjectsOfType<UClothPinner2>())
            {
                if (other == this) continue;
                if (other.pinAction != null && other.pinAction.action == pinAction.action)
                {
                    anotherPinnerExists = true;
                    break;
                }
            }
            if (!anotherPinnerExists)
                pinAction.action.Disable();
        }
    }
}