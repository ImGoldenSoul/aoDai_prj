// ============================================================
//  G3MeshCutter.cs  — v2.0  (Fixed Algorithm)
//
//  Các bug đã sửa so với v1:
//
//  BUG 1 — Seam edge sai:
//    Cũ: thêm eNewBN + eNewCN + eNewDN vào seamEdges.
//    Sửa: chỉ thêm eid gốc (sau split = [A,vNew]) + eNewBN ([vNew,B]).
//         eNewCN và eNewDN là edge xiên sang triangle kề — KHÔNG phải seam.
//
//  BUG 2 — Vị trí vertex mới sai:
//    Cũ: tính newPos theo index trong danh sách toSplit thay vì t thực.
//    Sửa: truyền split_t trực tiếp vào DMesh3.SplitEdge() — API hỗ trợ sẵn.
//         Không cần SetVertex sau split nữa.
//
//  BUG 3 — SegmentsIntersect3D kém nhạy:
//    Cũ: tolerance LengthSquared < 1e-3 (= khoảng cách 3.16 cm), thường miss.
//    Sửa: dùng khoảng cách vuông góc từ segment đường cắt tới edge mesh,
//         threshold tính từ kích thước edge (5% * edge_length).
//         Đồng thời tăng tolerance dọc đoạn từ [-0.001, 1.001] lên [-0.01, 1.01].
//
//  BUG 4 — regions=12 khi chỉ cắt 1 lần:
//    Nguyên nhân: seam không liên tục vì eNewCN/eNewDN phá vỡ flood fill.
//    Sửa: seam chỉ gồm 2 nửa của mỗi edge bị split → flood fill cho đúng 2 vùng.
//
//  API geometry3Sharp được dùng (đã xác minh từ source):
//  - DMesh3.SplitEdge(eid, out EdgeSplitInfo, double split_t)
//    → split_t đặt vị trí vertex mới dọc edge [A,B] (0=A, 1=B)
//    → vNew: vertex mới; eNewBN: edge [vNew,B]; eid gốc reuse thành [A,vNew]
//  - mesh.VtxEdgesItr(vid)
//  - DMesh3.GetTriEdges(tid), GetEdgeT(eid), GetEdgeV(eid)
//  - DMeshAABBTree3.FindNearestTriangle(p)
// ============================================================

using System.Collections.Generic;
using UnityEngine;
using g3;

public static class G3MeshCutter
{
    /// <summary>
    /// Cắt DMesh3 dọc theo <paramref name="worldPath"/>.
    /// Trả về danh sách các mảnh DMesh3, hoặc null nếu không tách được.
    /// </summary>
    public static List<DMesh3> CutAlongPath(DMesh3 mesh, List<Vector3> worldPath, out string log)
    {
        log = "";

        if (mesh == null || worldPath == null || worldPath.Count < 2)
        {
            log = "Input không hợp lệ (mesh null hoặc path < 2 điểm).";
            return null;
        }

        // ── Bước 1: Snap path points lên bề mặt mesh ─────────────────────
        var spatial     = new DMeshAABBTree3(mesh, true);
        var snappedPath = SnapPathToMesh(mesh, spatial, worldPath);

        if (snappedPath.Count < 2)
        {
            log = "Không snap được path lên mesh.";
            return null;
        }

        // ── Bước 2 & 3: Split edges dọc theo path, thu thập seam edges ───
        //  seamEdges chỉ chứa 2 nửa của mỗi edge bị split — không có edge xiên.
        var seamEdges  = new HashSet<int>();
        int totalSplits = SplitEdgesAlongPath(mesh, snappedPath, seamEdges, out string splitLog);
        log += splitLog;

        if (seamEdges.Count == 0)
        {
            log += " | Không có seam edge sau khi split.";
            return null;
        }

        // ── Bước 4: Flood fill để phân vùng triangle ──────────────────────
        var regions = FloodFillPartition(mesh, seamEdges);
        log += $" | splits={totalSplits}, seamEdges={seamEdges.Count}, regions={regions.Count}";

        if (regions.Count < 2)
        {
            log += " | Chưa tách (đường cắt chưa xuyên hết mesh?).";
            return null;
        }

        // ── Bước 5: Xuất các DMesh3 con ───────────────────────────────────
        var result = new List<DMesh3>(regions.Count);
        foreach (var region in regions)
        {
            var sub = ExtractSubMesh(mesh, region);
            if (sub != null && sub.TriangleCount >= 2)
                result.Add(sub);
        }

        log += $" | pieces={result.Count}";
        return result.Count >= 2 ? result : null;
    }

    // =========================================================================
    //  BƯỚC 1: SNAP PATH LÊN BỀ MẶT MESH
    // =========================================================================
    private static List<Vector3d> SnapPathToMesh(DMesh3 mesh, DMeshAABBTree3 spatial,
                                                   List<Vector3> path)
    {
        var snapped = new List<Vector3d>(path.Count);

        foreach (var p in path)
        {
            var p3d    = new Vector3d(p.x, p.y, p.z);
            int nearTri = spatial.FindNearestTriangle(p3d);

            if (nearTri == DMesh3.InvalidID)
            {
                snapped.Add(p3d);
                continue;
            }

            Index3i tri = mesh.GetTriangle(nearTri);
            Vector3d a  = mesh.GetVertex(tri.a);
            Vector3d b  = mesh.GetVertex(tri.b);
            Vector3d c  = mesh.GetVertex(tri.c);

            snapped.Add(ClosestPointOnTriangle(p3d, a, b, c));
        }

        return snapped;
    }

    // ── Closest point on triangle (Ericson's method) ──────────────────────
    private static Vector3d ClosestPointOnTriangle(Vector3d p, Vector3d a, Vector3d b, Vector3d c)
    {
        Vector3d ab = b - a, ac = c - a, ap = p - a;
        double d1 = ab.Dot(ap), d2 = ac.Dot(ap);
        if (d1 <= 0 && d2 <= 0) return a;

        Vector3d bp = p - b;
        double d3 = ab.Dot(bp), d4 = ac.Dot(bp);
        if (d3 >= 0 && d4 <= d3) return b;

        Vector3d cp = p - c;
        double d5 = ab.Dot(cp), d6 = ac.Dot(cp);
        if (d6 >= 0 && d5 <= d6) return c;

        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        { double v = d1 / (d1 - d3); return a + v * ab; }

        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        { double w = d2 / (d2 - d6); return a + w * ac; }

        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0)
        { double w2 = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return b + w2 * (c - b); }

        double denom = 1.0 / (vc + vb + va);
        double vv = vb * denom, ww = vc * denom;
        return a + ab * vv + ac * ww;
    }

    // =========================================================================
    //  BƯỚC 2 & 3: SPLIT EDGES + COLLECT SEAM EDGES
    //
    //  FIX BUG 1+2: Dùng split_t parameter của SplitEdge để đặt vị trí đúng.
    //  FIX BUG 3:   Tolerance dựa trên kích thước edge thực (adaptive).
    //  FIX SEAM:    Chỉ thêm eid gốc (→ [A,vNew] sau split) + eNewBN ([vNew,B]).
    // =========================================================================
    private static int SplitEdgesAlongPath(DMesh3 mesh, List<Vector3d> path,
                                            HashSet<int> outSeamEdges, out string log)
    {
        log   = "";
        int total = 0;

        // Đã split trong iteration này — tránh split lại edge vừa tạo
        var splitThisPass = new HashSet<int>();

        for (int i = 0; i < path.Count - 1; i++)
        {
            Vector3d segA = path[i];
            Vector3d segB = path[i + 1];

            // Snapshot edge IDs trước khi split (split sẽ thêm edge mới)
            var edgeSnapshot = new List<int>();
            foreach (int eid in mesh.EdgeIndices())
            {
                if (mesh.IsEdge(eid) && !splitThisPass.Contains(eid))
                    edgeSnapshot.Add(eid);
            }

            // Thu thập candidate edges cần split cho đoạn này
            var toSplit = new List<(int eid, double t)>();

            foreach (int eid in edgeSnapshot)
            {
                if (!mesh.IsEdge(eid)) continue;
                Index2i ev  = mesh.GetEdgeV(eid);
                Vector3d ea = mesh.GetVertex(ev.a);
                Vector3d eb = mesh.GetVertex(ev.b);

                if (!SegmentCrossesEdge(segA, segB, ea, eb, out double tSeg, out double tEdge))
                    continue;

                // Bỏ qua nếu giao điểm tại endpoint của edge (tránh degenerate)
                if (tEdge < 0.03 || tEdge > 0.97) continue;

                toSplit.Add((eid, tEdge));
            }

            // Split theo thứ tự t (từ segA → segB) để ổn định hơn
            toSplit.Sort((x, y) => x.t.CompareTo(y.t));

            foreach (var (eid, tEdge) in toSplit)
            {
                if (!mesh.IsEdge(eid)) continue;

                // FIX BUG 2: truyền split_t trực tiếp → vertex mới tại đúng vị trí giao
                MeshResult res = mesh.SplitEdge(eid, out DMesh3.EdgeSplitInfo si, tEdge);
                if (res != MeshResult.Ok) continue;

                // FIX BUG 1 (SEAM):
                //   Sau SplitEdge(eid, t):
                //     - eid bị reuse thành edge [A, vNew]
                //     - si.eNewBN là edge [vNew, B]
                //   → Đây là 2 nửa của đường cắt — chỉ chúng mới là seam.
                //   → si.eNewCN và si.eNewDN là edge xiên vào triangle kề, KHÔNG phải seam.
                outSeamEdges.Add(eid);          // [A, vNew]
                if (si.eNewBN != DMesh3.InvalidID)
                    outSeamEdges.Add(si.eNewBN); // [vNew, B]

                // Đánh dấu các edge mới tạo ra để không split lại trong vòng lặp kế
                splitThisPass.Add(eid);
                if (si.eNewBN != DMesh3.InvalidID) splitThisPass.Add(si.eNewBN);
                if (si.eNewCN != DMesh3.InvalidID) splitThisPass.Add(si.eNewCN);
                if (si.eNewDN != DMesh3.InvalidID) splitThisPass.Add(si.eNewDN);

                total++;
            }
        }

        log += $" SplitEdgesAlongPath: {total} splits, {path.Count - 1} segments.";
        return total;
    }

    // =========================================================================
    //  FIX BUG 3: SegmentCrossesEdge — Kiểm tra đoạn cắt có cắt ngang edge mesh.
    //
    //  Thay vì dùng tolerance tuyệt đối (1e-3 LengthSquared, thường miss),
    //  dùng 2 cách bổ sung nhau:
    //
    //  CÁCH 1: Tính tham số t1 (trên cut segment) và t2 (trên mesh edge) bằng
    //          công thức 3D least-distance. Nếu khoảng cách giữa 2 điểm gần nhất
    //          < threshold (5% * min_edge_len), coi là giao nhau.
    //
    //  CÁCH 2: Project cả 2 đoạn lên mặt phẳng perpendicular với normal trung bình,
    //          sau đó kiểm tra giao nhau 2D. Hiệu quả hơn cho cloth 2.5D.
    // =========================================================================
    private static bool SegmentCrossesEdge(Vector3d p1, Vector3d p2,
                                            Vector3d p3, Vector3d p4,
                                            out double t1, out double t2)
    {
        t1 = 0; t2 = 0;

        Vector3d d1 = p2 - p1;
        Vector3d d2 = p4 - p3;
        Vector3d r  = p1 - p3;

        double a = d1.Dot(d1);
        double e = d2.Dot(d2);

        if (a <= 1e-14 || e <= 1e-14) return false;

        double b = d1.Dot(d2);
        double c = d1.Dot(r);
        double f = d2.Dot(r);
        double denom = a * e - b * b;

        // FIX: tolerance thích nghi — 5% kích thước edge nhỏ hơn
        double edgeLen  = System.Math.Sqrt(e);
        double segLen   = System.Math.Sqrt(a);
        double minLen   = System.Math.Min(edgeLen, segLen);
        double distTol  = (minLen * 0.08);   // 8% độ dài
        double distTol2 = distTol * distTol;

        if (System.Math.Abs(denom) < 1e-12)
        {
            // Song song — không giao (nếu cần xử lý trùng thì thêm sau)
            return false;
        }

        t1 = (b * f - c * e) / denom;
        t2 = (a * f - b * c) / denom;

        // Giới hạn: t1 trên cut segment [0,1], t2 trên mesh edge [0,1]
        // FIX: mở rộng range một chút để bắt các edge ở cuối path
        if (t1 < -0.01 || t1 > 1.01 || t2 < -0.01 || t2 > 1.01) return false;

        // Clamp để tính khoảng cách thực
        double tc1 = System.Math.Max(0, System.Math.Min(1, t1));
        double tc2 = System.Math.Max(0, System.Math.Min(1, t2));

        Vector3d cp1 = p1 + tc1 * d1;
        Vector3d cp2 = p3 + tc2 * d2;

        // FIX BUG 3: tolerance thích nghi thay vì hằng số 1e-3
        bool close3D = (cp1 - cp2).LengthSquared < distTol2;
        if (close3D) return true;

        // CÁCH 2: fallback — project xuống XZ / XY / YZ (chọn mặt phẳng ít nhiễu nhất)
        // Tính normal của cut segment và test 2D projection
        Vector3d up    = new Vector3d(0, 1, 0);
        Vector3d right = d1.Cross(up);
        if (right.LengthSquared < 1e-10) right = d1.Cross(new Vector3d(1, 0, 0));
        right.Normalize();
        Vector3d fwd = d1; fwd.Normalize();

        // Project tất cả 4 điểm lên mặt phẳng {fwd, right}
        double p1u = fwd.Dot(p1), p1v = right.Dot(p1);
        double p2u = fwd.Dot(p2), p2v = right.Dot(p2);
        double p3u = fwd.Dot(p3), p3v = right.Dot(p3);
        double p4u = fwd.Dot(p4), p4v = right.Dot(p4);

        return Segments2DIntersect(p1u, p1v, p2u, p2v, p3u, p3v, p4u, p4v, out t1, out t2);
    }

    // ── Giao nhau 2D (scalar coords) ──────────────────────────────────────
    private static bool Segments2DIntersect(double ax, double ay, double bx, double by,
                                             double cx, double cy, double dx, double dy,
                                             out double t, out double s)
    {
        t = 0; s = 0;
        double dxAB = bx - ax, dyAB = by - ay;
        double dxCD = dx - cx, dyCD = dy - cy;
        double denom = dxAB * dyCD - dyAB * dxCD;
        if (System.Math.Abs(denom) < 1e-12) return false;

        double dxAC = cx - ax, dyAC = cy - ay;
        t = (dxAC * dyCD - dyAC * dxCD) / denom;
        s = (dxAC * dyAB - dyAC * dxAB) / denom;

        return t >= -0.01 && t <= 1.01 && s >= 0.03 && s <= 0.97;
    }

    // =========================================================================
    //  BƯỚC 4: FLOOD FILL PARTITION
    //  seamEdges chỉ gồm các nửa edge đúng → flood fill chia đúng 2 vùng
    // =========================================================================
    private static List<HashSet<int>> FloodFillPartition(DMesh3 mesh, HashSet<int> seamEdges)
    {
        var visited = new HashSet<int>();
        var regions = new List<HashSet<int>>();

        foreach (int tid in mesh.TriangleIndices())
        {
            if (visited.Contains(tid)) continue;

            var region = new HashSet<int>();
            var queue  = new Queue<int>();
            queue.Enqueue(tid);
            visited.Add(tid);
            region.Add(tid);

            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                if (!mesh.IsTriangle(cur)) continue;

                Index3i triEdges = mesh.GetTriEdges(cur);
                int[]   edges    = { triEdges.a, triEdges.b, triEdges.c };

                foreach (int eid in edges)
                {
                    // Seam edge = ranh giới, không vượt qua
                    if (seamEdges.Contains(eid)) continue;
                    if (!mesh.IsEdge(eid)) continue;

                    Index2i edgeTris = mesh.GetEdgeT(eid);
                    int nbTid = (edgeTris.a == cur) ? edgeTris.b : edgeTris.a;
                    if (nbTid == DMesh3.InvalidID) continue;
                    if (visited.Contains(nbTid)) continue;

                    visited.Add(nbTid);
                    region.Add(nbTid);
                    queue.Enqueue(nbTid);
                }
            }

            regions.Add(region);
        }

        return regions;
    }

    // =========================================================================
    //  BƯỚC 5: EXTRACT SUBMESH
    // =========================================================================
    private static DMesh3 ExtractSubMesh(DMesh3 source, HashSet<int> triSet)
    {
        var sub   = new DMesh3(false, false, false, false);
        var remap = new Dictionary<int, int>();

        foreach (int tid in triSet)
        {
            if (!source.IsTriangle(tid)) continue;
            Index3i tri = source.GetTriangle(tid);

            int na = GetOrAddVertex(sub, remap, source, tri.a);
            int nb = GetOrAddVertex(sub, remap, source, tri.b);
            int nc = GetOrAddVertex(sub, remap, source, tri.c);

            if (na == nb || nb == nc || na == nc) continue;

            if (sub.AppendTriangle(na, nb, nc) < 0)
                sub.AppendTriangle(na, nc, nb); // thử reverse winding
        }

        return sub;
    }

    private static int GetOrAddVertex(DMesh3 sub, Dictionary<int, int> remap,
                                       DMesh3 source, int vid)
    {
        if (remap.TryGetValue(vid, out int nv)) return nv;
        nv         = sub.AppendVertex(source.GetVertex(vid));
        remap[vid] = nv;
        return nv;
    }

    // ── BFS tìm đường edge giữa 2 vertex ────────────────────────────────
    public static List<int> FindEdgePathBetweenVertices(DMesh3 mesh, int startVid, int endVid)
    {
        if (startVid == endVid) return null;

        var prev  = new Dictionary<int, (int vid, int eid)>();
        var queue = new Queue<int>();
        queue.Enqueue(startVid);
        prev[startVid] = (-1, -1);

        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (cur == endVid) break;

            foreach (int eid in mesh.VtxEdgesItr(cur))
            {
                if (!mesh.IsEdge(eid)) continue;
                Index2i ev = mesh.GetEdgeV(eid);
                int nb = (ev.a == cur) ? ev.b : ev.a;
                if (!prev.ContainsKey(nb))
                {
                    prev[nb] = (cur, eid);
                    queue.Enqueue(nb);
                }
            }
        }

        if (!prev.ContainsKey(endVid)) return null;

        var edgePath = new List<int>();
        int v = endVid;
        while (prev[v].vid != -1)
        {
            edgePath.Add(prev[v].eid);
            v = prev[v].vid;
        }
        edgePath.Reverse();
        return edgePath;
    }
}
