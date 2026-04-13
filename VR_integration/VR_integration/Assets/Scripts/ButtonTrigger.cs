using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

public class ButtonTrigger : MonoBehaviour
{
    public GameObject infoCanvas;

    // Hàm này dùng cho XR Simple Interactable (Khi dùng Ray)
    public void OnRaySelect()
    {
        ToggleCanvas();
    }

    // Hàm này dùng cho Collider (Khi dùng Tay chạm trực tiếp)
    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            ToggleCanvas();
        }
    }

    private void ToggleCanvas()
    {
        if (infoCanvas != null)
        {
            infoCanvas.SetActive(!infoCanvas.activeSelf);
        }
    }
}