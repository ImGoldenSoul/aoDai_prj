using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Tính diện tích bề mặt vải trước và sau khi cắt.
///
/// Công thức:
///   S_original  = tổng diện tích tất cả triangle của mesh gốc (local space, có scale)
///   S_piece[i]  = tổng diện tích triangle thuộc component[i]  (flood-fill result)
///   S_seam      = S_original - sum(S_piece[i])
///               = diện tích các triangle nằm trong _accumulatedCutTris (đường seam bị loại)
///
/// Ghi chú: Diện tích tính trên LOCAL mesh * lossyScale để ra đơn vị world-space.
/// Nếu vải đang biến dạng (UCCloth simulate), tính trên world-space vertices cho chính xác.
/// </summary>
public static class ClothAreaCalculator
{
    // =========================================================================
    //  PUBLIC API
    // =========================================================================

    /// <summary>
    /// Tính diện tích toàn bộ mesh (world-space vertices, ví dụ lấy từ UCCloth simData).
    /// Dùng cho mesh gốc TRƯỚC khi cắt.
    /// </summary>
    public static float CalculateTotalArea(Vector3[] worldVertices, int[] triangles)
    {
        float area = 0f;
        int triCount = triangles.Length / 3;
        for (int t = 0; t < triCount; t++)
            area += TriangleArea(
                worldVertices[triangles[t * 3]],
                worldVertices[triangles[t * 3 + 1]],
                worldVertices[triangles[t * 3 + 2]]);
        return area;
    }

    /// <summary>
    /// Tính diện tích của một tập hợp triangle index (world-space vertices).
    /// Dùng để tính diện tích từng piece sau khi cắt.
    /// </summary>
    public static float CalculateAreaForTriangleSet(
        IEnumerable<int> triangleIndices,
        int[] triangles,
        Vector3[] worldVertices)
    {
        float area = 0f;
        foreach (int t in triangleIndices)
            area += TriangleArea(
                worldVertices[triangles[t * 3]],
                worldVertices[triangles[t * 3 + 1]],
                worldVertices[triangles[t * 3 + 2]]);
        return area;
    }

    /// <summary>
    /// Tính diện tích trực tiếp từ một Mesh đã build sẵn (local space * scale).
    /// Dùng sau khi piece đã được tạo ra (không cần worldVerts của mesh gốc).
    /// </summary>
    public static float CalculateAreaFromMesh(Mesh mesh, Vector3 lossyScale)
    {
        Vector3[] verts = mesh.vertices;
        int[]     tris  = mesh.triangles;
        float area = 0f;
        int triCount = tris.Length / 3;
        for (int t = 0; t < triCount; t++)
        {
            // Scale local vertices → approximate world-space area
            Vector3 a = ScaleVertex(verts[tris[t * 3    ]], lossyScale);
            Vector3 b = ScaleVertex(verts[tris[t * 3 + 1]], lossyScale);
            Vector3 c = ScaleVertex(verts[tris[t * 3 + 2]], lossyScale);
            area += TriangleArea(a, b, c);
        }
        return area;
    }

    // =========================================================================
    //  RESULT STRUCT
    // =========================================================================

    public struct CutAreaReport
    {
        /// <summary>Diện tích mesh gốc trước khi cắt (m²)</summary>
        public float OriginalArea;

        /// <summary>Diện tích từng piece sau khi cắt (m²)</summary>
        public float[] PieceAreas;

        /// <summary>Tổng diện tích các piece</summary>
        public float TotalPieceArea;

        /// <summary>
        /// Diện tích phần seam bị loại bỏ = OriginalArea - TotalPieceArea
        /// (các triangle trong _accumulatedCutTris không thuộc bất kỳ piece nào)
        /// </summary>
        public float SeamArea;

        /// <summary>Tỉ lệ seam / original (0–1)</summary>
        public float SeamRatio => OriginalArea > 0f ? SeamArea / OriginalArea : 0f;

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[ClothAreaReport]");
            sb.AppendLine($"  Original : {OriginalArea * 10000f:F2} cm²  ({OriginalArea:F6} m²)");
            for (int i = 0; i < PieceAreas.Length; i++)
                sb.AppendLine($"  Piece[{i}] : {PieceAreas[i] * 10000f:F2} cm²  ({PieceAreas[i]:F6} m²)");
            sb.AppendLine($"  Seam lost: {SeamArea * 10000f:F2} cm²  ({SeamArea:F6} m²)  [{SeamRatio * 100f:F1}%]");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Tính report đầy đủ: diện tích gốc, từng piece, và seam.
    ///
    /// Tham số:
    ///   worldVertices    — vertices của mesh gốc ở world-space (từ UCCloth simData hoặc TransformPoint)
    ///   triangles        — mesh.triangles của mesh gốc
    ///   pieceComponents  — kết quả flood-fill: mỗi List<int> là tập triangle index của 1 piece
    ///   accumulatedCutTris — tập seam triangle đã bị loại (dùng để verify S_seam)
    /// </summary>
    public static CutAreaReport BuildReport(
        Vector3[]       worldVertices,
        int[]           triangles,
        List<List<int>> pieceComponents,
        HashSet<int>    accumulatedCutTris = null)
    {
        float originalArea = CalculateTotalArea(worldVertices, triangles);

        var pieceAreas = new float[pieceComponents.Count];
        float totalPiece = 0f;
        for (int i = 0; i < pieceComponents.Count; i++)
        {
            pieceAreas[i] = CalculateAreaForTriangleSet(pieceComponents[i], triangles, worldVertices);
            totalPiece += pieceAreas[i];
        }

        // S_seam = phần bị mất.
        // Có thể tính trực tiếp từ accumulatedCutTris nếu được truyền vào,
        // hoặc dùng hiệu số (cho cùng kết quả về mặt toán học).
        float seamArea;
        if (accumulatedCutTris != null && accumulatedCutTris.Count > 0)
            seamArea = CalculateAreaForTriangleSet(accumulatedCutTris, triangles, worldVertices);
        else
            seamArea = originalArea - totalPiece;   // fallback: hiệu số

        return new CutAreaReport
        {
            OriginalArea   = originalArea,
            PieceAreas     = pieceAreas,
            TotalPieceArea = totalPiece,
            SeamArea       = Mathf.Max(0f, seamArea)
        };
    }

    // =========================================================================
    //  PRIVATE HELPERS
    // =========================================================================

    private static float TriangleArea(Vector3 a, Vector3 b, Vector3 c)
        => Vector3.Cross(b - a, c - a).magnitude * 0.5f;

    private static Vector3 ScaleVertex(Vector3 v, Vector3 s)
        => new Vector3(v.x * s.x, v.y * s.y, v.z * s.z);
}
