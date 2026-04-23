using UnityEngine;
using UnityEngine.InputSystem; // BẮT BUỘC THÊM DÒNG NÀY

public class AutoSweepTester : MonoBehaviour
{
    [Header("Testing Settings")]
    [Tooltip("Vật thể dùng để test (Khối cầu Collider hoặc Súng Laser)")]
    public Transform testObject;

    [Tooltip("Tốc độ di chuyển (m/s)")]
    public float speed = 5f;

    [Tooltip("Khoảng cách quét (meters)")]
    public float sweepDistance = 2f;

    private Vector3 startPos;
    private bool isTesting = false;

    void Update()
    {
        // Kiểm tra xem bàn phím có đang được kết nối không và phím Space có được bấm không (New Input System)
        bool spacePressed = Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;

        // Bấm nút Space để bắt đầu quét
        if (spacePressed && !isTesting)
        {
            startPos = testObject.position;
            isTesting = true;
            Debug.Log($"[AutoSweep] Bắt đầu test với tốc độ: {speed} m/s");
        }

        if (isTesting)
        {
            // Di chuyển vật thể thẳng về phía trước
            testObject.position += testObject.forward * speed * Time.deltaTime;

            // Dừng lại khi đã quét đủ khoảng cách
            if (Vector3.Distance(startPos, testObject.position) >= sweepDistance)
            {
                isTesting = false;
                Debug.Log("[AutoSweep] Hoàn thành vòng test!");
            }
        }
    }
}