using Unity.XR.CoreUtils;
using UnityEngine;

public class XRHeightFix : MonoBehaviour
{
    private XROrigin xrOrigin;
    private CharacterController characterController;

    void Start() {
        xrOrigin = GetComponent<XROrigin>();
        characterController = GetComponent<CharacterController>();
        // Ép về Floor mode ngay khi bắt đầu
        xrOrigin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;
    }

    void Update() {
        if (xrOrigin == null || characterController == null) return;

        // 1. Cập nhật chiều cao Capsule theo chiều cao kính thực tế
        float headHeight = Mathf.Clamp(xrOrigin.CameraInOriginSpaceHeight, 0.5f, 2.5f);
        characterController.height = headHeight;

        // 2. Khóa Center: Đưa tâm Capsule về đúng vị trí Camera
        Vector3 newCenter = xrOrigin.CameraInOriginSpacePos;
        newCenter.y = (headHeight / 2f) + characterController.skinWidth;
        characterController.center = newCenter;

        // 3. CHỐNG RƠI: Nếu XR Origin bị lún xuống dưới sàn (Y < 0), ép về 0
        if (transform.position.y < -0.01f) {
            transform.position = new Vector3(transform.position.x, 0, transform.position.z);
        }
        if (Mathf.Abs(transform.position.y) > 0.001f) {
        // Ép vị trí gốc của bộ máy VR về đúng mặt sàn Y = 0
        Vector3 currentPos = transform.position;
        currentPos.y = 0; 
        transform.position = currentPos;
    }
    }
}