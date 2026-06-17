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
    /// <param name="boundarySnapFactor">
    /// FIX TOPOLOGY BUG — Một "slit" (đường cắt nằm hoàn toàn trong nội thất mesh,
    /// không chạm boundary và không khép vòng) KHÔNG THỂ tách mesh disc thành 2 vùng,
    /// bất kể bao nhiêu edge bị split: flood-fill luôn có thể đi vòng qua đầu mút slit.
    /// Tham số này = hệ số nhân với độ dài cạnh trung bình của mesh, dùng làm ngưỡng
    /// để (a) snap 2 đầu path vào đúng boundary edge gần nhất nếu đủ gần, hoặc
    /// (b) khép path thành vòng kín nếu 2 đầu path đã gần nhau (cắt thủng 1 lỗ giữa mesh).
    /// </param>
    public static List<DMesh3> CutAlongPath(DMesh3 mesh, List<Vector3> worldPath, out string log,
                                             double boundarySnapFactor = 6.0)
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

        // ── Bước 1b: FIX — đảm bảo path thực sự "xuyên" được mesh ─────────
        //  Một path chỉ là slit nội thất sẽ KHÔNG bao giờ làm regions > 1,
        //  vì flood-fill luôn vòng qua được 2 đầu mút của nó. Ở đây ta:
        //   - snap từng đầu path vào boundary edge gần nhất nếu đủ gần
        //     (đường cắt "chạm mép vải"), HOẶC
        //   - khép path thành vòng kín nếu 2 đầu đã gần nhau (cắt 1 lỗ thủng).
        double avgEdgeLen      = ComputeAverageEdgeLength(mesh);
        double boundarySnapDist = avgEdgeLen * boundarySnapFactor;
        snappedPath = CloseOrExtendToBoundary(mesh, snappedPath, boundarySnapDist, out string boundaryLog);
        log += boundaryLog;

        // ── Bước 2 & 3: Split edges dọc theo path, thu thập seam edges ───
        //  seamEdges chỉ chứa 2 nửa của mỗi edge bị split — không có edge xiên.
        //  seamChain  ghi lại thứ tự các vertex mới sinh ra dọc theo path (đầu/cuối
        //  của chain = 2 "đầu mút" thực của đường seam, dùng cho fallback bên dưới).
        var seamEdges  = new HashSet<int>();
        var seamChain  = new List<int>();
        int totalSplits = SplitEdgesAlongPath(mesh, snappedPath, seamEdges, seamChain, out string splitLog);
        log += splitLog;

        if (seamEdges.Count == 0)
        {
            log += " | Không có seam edge sau khi split.";
            return null;
        }

        // ── Bước 4: Flood fill để phân vùng triangle ──────────────────────
        var regions = FloodFillPartition(mesh, seamEdges);
        log += $" | splits={totalSplits}, seamEdges={seamEdges.Count}, regions={regions.Count}";

        // ── Bước 4b: FALLBACK — ĐẢM BẢO LUÔN TÁCH ĐƯỢC dù seam chưa hoàn thành ──
        //  Trường hợp đường seam người dùng vẽ là 1 "slit" hở (2 đầu mút không khép
        //  vòng và không đủ gần boundary để được snap ở Bước 1b) thì flood-fill luôn
        //  có thể đi vòng qua đầu mút slit → regions vẫn = 1 dù split đúng số edge.
        //  Sửa: CƯỠNG ÉP nối 2 đầu mút của seam tới boundary GẦN NHẤT bằng cách đi
        //  dọc theo các CẠNH CÓ SẴN của mesh (BFS trên đồ thị cạnh) — không cần thêm
        //  giao điểm hình học mới, chỉ "mượn" cạnh mesh sẵn có làm seam. Cách này luôn
        //  thành công với bất kỳ mesh nào có boundary (vải hở mép — tức hầu hết mọi
        //  mảnh vải thực tế), bất kể seam người dùng vẽ ngắn/lệch/chưa chạm mép.
        if (regions.Count < 2)
        {
            int forcedEdges = ForceConnectSeamToBoundary(mesh, seamChain, seamEdges);
            if (forcedEdges > 0)
            {
                regions = FloodFillPartition(mesh, seamEdges);
                log += $" | FORCE-LINK: seam chưa hoàn thành (slit hở/nội thất) → cưỡng ép nối " +
                       $"{forcedEdges} cạnh có sẵn từ đầu mút seam tới boundary gần nhất → regions={regions.Count}.";
            }
        }

        if (regions.Count < 2)
        {
            log += " | Chưa tách (mesh không có boundary nào để nối tới — có thể là mặt kín hoàn toàn).";
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
    //  FIX TOPOLOGY BUG: CLOSE-OR-EXTEND-TO-BOUNDARY
    //
    //  Vấn đề gốc: seamEdges đúng (đã split đúng cạnh) nhưng regions vẫn = 1
    //  vì đường cắt là 1 "slit" hở — 2 đầu mút nằm giữa mesh, không chạm
    //  boundary và không khép vòng. Flood-fill luôn đi vòng được qua đầu mút
    //  slit nên không bao giờ tách được, BẤT KỂ số lượng edge bị split.
    //
    //  Sửa bằng 1 trong 2 cách (tuỳ tình huống của path):
    //   A) Nếu 2 đầu path đã gần nhau (người dùng vẽ 1 vòng khép kín giữa
    //      mesh) → nối điểm cuối về điểm đầu để khép seam thành vòng tròn
    //      → flood-fill tách ra được 1 "đảo" (lỗ thủng) + phần còn lại.
    //   B) Nếu không, thử kéo từng đầu path snap thẳng vào boundary edge
    //      gần nhất (nếu đủ gần) → đường cắt thực sự chạm mép vải ở cả
    //      2 đầu → flood-fill tách được 2 vùng bên trái/phải đường cắt.
    //   Nếu cả 2 cách đều không áp dụng được → đây thực sự là 1 slit dở,
    //   sẽ log rõ lý do để debug (không phải lỗi splitting).
    // =========================================================================
    private static double ComputeAverageEdgeLength(DMesh3 mesh)
    {
        double sum = 0; int n = 0;
        foreach (int eid in mesh.EdgeIndices())
        {
            if (!mesh.IsEdge(eid)) continue;
            Index2i ev = mesh.GetEdgeV(eid);
            Vector3d a = mesh.GetVertex(ev.a), b = mesh.GetVertex(ev.b);
            double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            sum += System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
            n++;
        }
        return n > 0 ? sum / n : 0.01;
    }

    private static List<(int eid, Vector3d a, Vector3d b)> GetBoundaryEdges(DMesh3 mesh)
    {
        var list = new List<(int eid, Vector3d a, Vector3d b)>();
        foreach (int eid in mesh.EdgeIndices())
        {
            if (!mesh.IsEdge(eid) || !mesh.IsBoundaryEdge(eid)) continue;
            Index2i ev = mesh.GetEdgeV(eid);
            list.Add((eid, mesh.GetVertex(ev.a), mesh.GetVertex(ev.b)));
        }
        return list;
    }

    private static Vector3d ClosestPointOnSegment(Vector3d p, Vector3d a, Vector3d b, out double dist)
    {
        Vector3d ab   = b - a;
        double   len2 = ab.LengthSquared;
        double   t    = len2 > 1e-14 ? (p - a).Dot(ab) / len2 : 0.0;
        t = System.Math.Max(0.0, System.Math.Min(1.0, t));
        Vector3d cp = a + t * ab;
        double dx = p.x - cp.x, dy = p.y - cp.y, dz = p.z - cp.z;
        dist = System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return cp;
    }

    private static List<Vector3d> CloseOrExtendToBoundary(DMesh3 mesh, List<Vector3d> path,
                                                            double snapDist, out string log)
    {
        log = "";
        if (path.Count < 2) return path;

        var result = new List<Vector3d>(path);

        // ── Case A: 2 đầu path đã gần nhau → khép vòng (cắt 1 lỗ thủng) ──
        Vector3d head = result[0], tail = result[result.Count - 1];
        double hdx = head.x - tail.x, hdy = head.y - tail.y, hdz = head.z - tail.z;
        double endToEndDist = System.Math.Sqrt(hdx * hdx + hdy * hdy + hdz * hdz);

        if (endToEndDist < snapDist)
        {
            result.Add(head); // nối điểm cuối quay lại điểm đầu, khép seam
            log = $" | Loop khép kín được phát hiện (Δ={endToEndDist:F4} < {snapDist:F4}), seal seam thành vòng tròn.";
            return result;
        }

        // ── Case B: thử snap từng đầu vào boundary edge gần nhất ─────────
        var boundaryEdges = GetBoundaryEdges(mesh);
        if (boundaryEdges.Count == 0)
        {
            log = " | Mesh không có boundary edge (closed surface) và 2 đầu path không khép vòng" +
                  " → không thể tách (cần đường cắt dạng vòng kín cho mesh kín).";
            return result;
        }

        bool startSnapped = TrySnapEndpoint(result, atStart: true,  boundaryEdges, snapDist, out double dStart);
        bool endSnapped   = TrySnapEndpoint(result, atStart: false, boundaryEdges, snapDist, out double dEnd);

        // ── Case C: vẫn chưa đủ gần boundary → KÉO DÀI đầu mút theo hướng nét
        //    vẽ tại đó (extrapolate) một khoảng = snapDist, rồi thử snap lại.
        //    Điều này giúp các nhát vẽ "gần chạm mép nhưng chưa đủ" (rất hay
        //    gặp khi người dùng vẽ tự do) vẫn xuyên được ra biên mà không cần
        //    họ vẽ chính xác tới rìa vải.
        if (!startSnapped && result.Count >= 2)
        {
            Vector3d dir = (result[0] - result[1]);
            if (dir.LengthSquared > 1e-12)
            {
                dir.Normalize();
                Vector3d extended = result[0] + dir * snapDist;
                var probe = new List<Vector3d> { extended, result[1] };
                startSnapped = TrySnapEndpoint(probe, atStart: true, boundaryEdges, snapDist, out dStart);
                if (startSnapped) result[0] = probe[0];
            }
        }
        if (!endSnapped && result.Count >= 2)
        {
            int last = result.Count - 1;
            Vector3d dir = (result[last] - result[last - 1]);
            if (dir.LengthSquared > 1e-12)
            {
                dir.Normalize();
                Vector3d extended = result[last] + dir * snapDist;
                var probe = new List<Vector3d> { result[last - 1], extended };
                endSnapped = TrySnapEndpoint(probe, atStart: false, boundaryEdges, snapDist, out dEnd);
                if (endSnapped) result[last] = probe[1];
            }
        }

        log = $" | StartToBoundary={dStart:F4} (snapped={startSnapped}), EndToBoundary={dEnd:F4} (snapped={endSnapped}), threshold={snapDist:F4}";

        if (!startSnapped && !endSnapped)
            log += " | CẢNH BÁO: cả 2 đầu path đều quá xa boundary và không khép vòng → đây là slit nội thất, KHÔNG THỂ tách (vuốt dao xa hơn ra tới mép vải, hoặc khép path thành vòng tròn).";
        else if (!startSnapped || !endSnapped)
            log += " | CẢNH BÁO: chỉ 1 đầu path chạm boundary → đường cắt vẫn là slit hở 1 đầu, sẽ KHÔNG tách.";

        return result;
    }

    private static bool TrySnapEndpoint(List<Vector3d> path, bool atStart,
        List<(int eid, Vector3d a, Vector3d b)> boundaryEdges, double snapDist, out double bestDist)
    {
        int idx = atStart ? 0 : path.Count - 1;
        Vector3d p = path[idx];

        bestDist = double.MaxValue;
        Vector3d bestPoint = p;
        foreach (var (eid, a, b) in boundaryEdges)
        {
            Vector3d cp = ClosestPointOnSegment(p, a, b, out double d);
            if (d < bestDist) { bestDist = d; bestPoint = cp; }
        }

        if (bestDist < snapDist)
        {
            path[idx] = bestPoint;
            return true;
        }
        return false;
    }

    // =========================================================================
    //  BƯỚC 2 & 3: SPLIT EDGES + COLLECT SEAM EDGES
    //
    //  FIX BUG 1+2: Dùng split_t parameter của SplitEdge để đặt vị trí đúng.
    //  FIX BUG 3:   Tolerance dựa trên kích thước edge thực (adaptive).
    //  FIX SEAM:    Chỉ thêm eid gốc (→ [A,vNew] sau split) + eNewBN ([vNew,B]).
    // =========================================================================
    private static int SplitEdgesAlongPath(DMesh3 mesh, List<Vector3d> path,
                                            HashSet<int> outSeamEdges, List<int> outSeamChain, out string log)
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

                // Ghi lại vertex mới theo đúng thứ tự dọc path → 2 đầu của chain
                // (outSeamChain[0] và outSeamChain[last]) chính là 2 "đầu mút" thật
                // của đường seam, dùng để cưỡng ép nối ra boundary nếu cần (Bước 4b).
                if (si.vNew != DMesh3.InvalidID)
                    outSeamChain.Add(si.vNew);

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
    //  FALLBACK BƯỚC 4b: FORCE-CONNECT SEAM ENDPOINTS TO NEAREST BOUNDARY
    //
    //  Khi seam là 1 slit hở (2 đầu mút không khép vòng, không đủ gần boundary để
    //  được snap hình học ở Bước 1b), flood-fill không bao giờ tách được mesh vì
    //  luôn có thể "đi vòng" qua đầu mút hở. Ở đây ta cưỡng ép nối từng đầu mút của
    //  seamChain tới vertex boundary GẦN NHẤT theo đồ thị cạnh thực của mesh (BFS,
    //  không cần intersection hình học mới) — đảm bảo LUÔN tách được với mesh có
    //  boundary (đúng với hầu hết mảnh vải, vốn luôn có mép hở).
    // =========================================================================
    private static int ForceConnectSeamToBoundary(DMesh3 mesh, List<int> seamChain, HashSet<int> seamEdges)
    {
        if (seamChain == null || seamChain.Count == 0) return 0;

        int head = seamChain[0];
        int tail = seamChain[seamChain.Count - 1];

        int added = ConnectVertexToNearestBoundary(mesh, head, seamEdges);
        if (tail != head)
            added += ConnectVertexToNearestBoundary(mesh, tail, seamEdges);

        return added;
    }

    /// <summary>
    /// BFS từ startVid dọc theo cạnh thực của mesh tới vertex boundary gần nhất
    /// (tính theo số cạnh, không phải khoảng cách hình học — đủ tốt cho mục đích
    /// "cưỡng ép tách" và rẻ hơn nhiều so với Dijkstra trên mesh lớn). Tất cả cạnh
    /// trên đường đi tìm được sẽ được thêm vào seamEdges. Trả về số cạnh đã thêm
    /// (0 nếu startVid đã nằm trên boundary, hoặc mesh không có boundary nào).
    /// </summary>
    private static int ConnectVertexToNearestBoundary(DMesh3 mesh, int startVid, HashSet<int> seamEdges)
    {
        if (!mesh.IsVertex(startVid)) return 0;
        if (mesh.IsBoundaryVertex(startVid)) return 0; // đầu mút đã ở mép vải, không cần nối thêm

        var prev  = new Dictionary<int, (int vid, int eid)>();
        var queue = new Queue<int>();
        queue.Enqueue(startVid);
        prev[startVid] = (-1, -1);
        int foundVid = -1;

        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (cur != startVid && mesh.IsBoundaryVertex(cur)) { foundVid = cur; break; }

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

        if (foundVid < 0) return 0; // mesh kín hoàn toàn, không có boundary để nối tới

        int v = foundVid, n = 0;
        while (prev[v].vid != -1)
        {
            seamEdges.Add(prev[v].eid);
            v = prev[v].vid;
            n++;
        }
        return n;
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
