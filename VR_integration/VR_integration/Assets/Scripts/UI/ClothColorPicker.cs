using UnityEngine;
using UnityEngine.UI;

public class ClothColorPicker : MonoBehaviour
{
    public string clothTag = "Cloth";

    // Phải có chữ "public" thì Inspector mới nhìn thấy hàm này
    public void ChangeClothColor()
    {
        Color selectedColor = GetComponent<Image>().color;
        GameObject[] clothes = GameObject.FindGameObjectsWithTag(clothTag);
        int count = 0;
        
        foreach (var cloth in clothes)
        {
            Renderer rend = cloth.GetComponent<Renderer>();
            if (rend != null)
            {
                if (rend.material.HasProperty("_Color")) rend.material.color = selectedColor;
                if (rend.material.HasProperty("_BaseColor")) rend.material.SetColor("_BaseColor", selectedColor);
                count++;
            }
        }
        
        Debug.Log($"[ClothColorPicker] Đã nhận lệnh! Chuyển {count} miếng vải thành màu {selectedColor}");
    }
}