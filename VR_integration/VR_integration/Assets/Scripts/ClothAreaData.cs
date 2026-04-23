using UnityEngine;

/// <summary>
/// Lưu thông tin diện tích của miếng vải sau khi cắt.
/// Được gắn tự động vào mỗi piece GameObject bởi CuttingManager_UCloth.
///
/// Tất cả đơn vị là m² (Unity world-space).
/// Nhân với 10 000 để ra cm².
/// </summary>
public class ClothAreaData : MonoBehaviour
{
    [Header("Diện tích (m²)")]
    [Tooltip("Diện tích mesh vải GỐC trước khi cắt")]
    public float originalArea;

    [Tooltip("Diện tích của MIẾNG NÀY sau khi cắt")]
    public float pieceArea;

    [Tooltip("Diện tích đường seam bị loại (= gốc - tổng các piece)")]
    public float seamArea;

    [Tooltip("Tỉ lệ seam / gốc (0–1)")]
    [Range(0f, 1f)]
    public float seamRatio;

    // Convenience getters in cm²
    public float OriginalAreaCm2  => originalArea * 10000f;
    public float PieceAreaCm2     => pieceArea    * 10000f;
    public float SeamAreaCm2      => seamArea     * 10000f;

    void Start()
    {
        Debug.Log($"[ClothAreaData] {gameObject.name}: " +
                  $"gốc={OriginalAreaCm2:F2}cm²  " +
                  $"miếng={PieceAreaCm2:F2}cm²  " +
                  $"seam={SeamAreaCm2:F2}cm² ({seamRatio*100f:F1}%)");
    }
}
