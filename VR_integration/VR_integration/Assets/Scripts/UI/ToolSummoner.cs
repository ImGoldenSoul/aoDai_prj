using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

public class ToolSummoner : MonoBehaviour
{
    [Header("Các Khối Công Cụ (Tools)")]
    [Tooltip("Kéo khối (Cube) chứa chức năng CẮT vào đây")]
    public XRGrabInteractable cutTool;

    [Tooltip("Kéo khối (Cube) chứa chức năng KHÂU vào đây")]
    public XRGrabInteractable sewTool;

    [Header("Tay Cầm (Hand Interactor)")]
    [Tooltip("Kéo XR Direct Interactor vào đây")]
    public XRBaseInteractor targetHandInteractor;

    // ==========================================
    // CÁC HÀM ĐỂ GẮN VÀO NÚT UI
    // ==========================================
    public void SummonCutTool()
    {
        SummonAndAutoGrab(cutTool);
    }

    public void SummonSewTool()
    {
        SummonAndAutoGrab(sewTool);
    }

    // HÀM MỚI: NHẢ CÔNG CỤ
    public void DropTool()
    {
        if (targetHandInteractor == null) return;

        XRInteractionManager manager = targetHandInteractor.interactionManager;
        IXRSelectInteractor iInteractor = (IXRSelectInteractor)targetHandInteractor;

        // Kiểm tra xem tay có đang cầm cái gì không
        if (targetHandInteractor.hasSelection)
        {
            // Lấy vật thể đang được cầm
            IXRSelectInteractable currentItem = (IXRSelectInteractable)targetHandInteractor.firstInteractableSelected;

            if (currentItem != null)
            {
                // Ép buộc thoát khỏi trạng thái nắm giữ (Drop)
                manager.SelectExit(iInteractor, currentItem);
                Debug.Log("[ToolSummoner] Đã chủ động nhả công cụ!");
            }
        }
    }

    // ==========================================
    // LOGIC XỬ LÝ CHÍNH
    // ==========================================
    private void SummonAndAutoGrab(XRGrabInteractable toolToSummon)
    {
        if (toolToSummon == null || targetHandInteractor == null)
        {
            Debug.LogWarning("[ToolSummoner] Thiếu tham chiếu Công cụ hoặc Tay cầm!");
            return;
        }

        if (!toolToSummon.gameObject.activeSelf)
        {
            toolToSummon.gameObject.SetActive(true);
        }

        Rigidbody rb = toolToSummon.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        toolToSummon.transform.position = targetHandInteractor.transform.position;
        toolToSummon.transform.rotation = targetHandInteractor.transform.rotation;

        XRInteractionManager manager = targetHandInteractor.interactionManager;
        IXRSelectInteractor iInteractor = (IXRSelectInteractor)targetHandInteractor;
        IXRSelectInteractable iInteractable = (IXRSelectInteractable)toolToSummon;

        // Nếu đang cầm vật khác, nhả ra trước khi cầm cái mới
        if (targetHandInteractor.hasSelection)
        {
            IXRSelectInteractable currentItem = (IXRSelectInteractable)targetHandInteractor.firstInteractableSelected;
            if (currentItem != null)
            {
                manager.SelectExit(iInteractor, currentItem);
            }
        }

        manager.SelectEnter(iInteractor, iInteractable);
        Debug.Log($"[ToolSummoner] Auto-Grab: {toolToSummon.gameObject.name}");
    }
}