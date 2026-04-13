using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Khâu 2 miếng vải UCloth lại với nhau bằng UCSewingConstraint.
///
/// Nguyên lý:
///   Mỗi cặp node (A trên cloth A, B trên cloth B) được kéo về cùng 1 điểm target
///   = trung điểm (posA + posB) / 2. Target được ghi vào simData.sewingConstraints
///   của từng cloth trước khi UCJob chạy. Bên trong job, ApplySewingConstraints()
///   áp position + velocity correction với stiffness cho từng node.
///
///   Node KHÔNG bị set reciprocalWeight = 0, nên:
///   - Vẫn chịu gravity, spring, collision bình thường.
///   - Đường seam di chuyển theo toàn bộ mô phỏng, không đứng yên.
///   - Không có crash "Key not present" trong ResetPinned.
/// </summary>
public class MeshSewer_UCloth
{
    public struct SewPair
    {
        public int SimIndexA;
        public int SimIndexB;
    }

    private readonly UCloth.UCCloth _ucA, _ucB;
    private readonly List<SewPair> _weldedPairs = new List<SewPair>();
    private readonly float _stiffness;

    public MeshSewer_UCloth(UCloth.UCCloth ucA, UCloth.UCCloth ucB, float stiffness = 0.8f)
    {
        _ucA = ucA;
        _ucB = ucB;
        _stiffness = Mathf.Clamp01(stiffness);
        Debug.Log($"<color=cyan>[MeshSewer]</color> Khởi tạo phiên khâu: {_ucA?.name} & {_ucB?.name} | stiffness={_stiffness}");
    }

    /// <summary>
    /// Đăng ký 1 cặp node cần khâu. Không thay đổi weight — node vẫn mô phỏng bình thường.
    /// </summary>
    public void AddWeldPair(int simIdxA, int simIdxB)
    {
        if (_ucA?.simData == null || _ucB?.simData == null) return;
        if (!_ucA.simData.positionsReadOnly.IsCreated || !_ucB.simData.positionsReadOnly.IsCreated) return;
        if (simIdxA < 0 || simIdxA >= _ucA.simData.positionsReadOnly.Length) return;
        if (simIdxB < 0 || simIdxB >= _ucB.simData.positionsReadOnly.Length) return;

        _weldedPairs.Add(new SewPair { SimIndexA = simIdxA, SimIndexB = simIdxB });
        Debug.Log($"<color=green>[MeshSewer]</color> Đăng ký mối khâu: Node {simIdxA} ({_ucA.name}) <-> Node {simIdxB} ({_ucB.name})");
    }

    /// <summary>
    /// Gọi mỗi FixedUpdate, TRƯỚC khi UCCloth schedule job (UCCloth chạy trong Update).
    /// Xóa constraints cũ và ghi lại danh sách mới với target = midpoint hiện tại.
    /// </summary>
    public void UpdateWeldPhysics()
    {
        if (_weldedPairs.Count == 0) return;
        if (_ucA?.simData == null || _ucB?.simData == null) return;
        if (!_ucA.simData.sewingConstraints.IsCreated || !_ucB.simData.sewingConstraints.IsCreated) return;
        if (!_ucA.simData.positionsReadOnly.IsCreated || !_ucB.simData.positionsReadOnly.IsCreated) return;

        _ucA.simData.sewingConstraints.Clear();
        _ucB.simData.sewingConstraints.Clear();

        foreach (var pair in _weldedPairs)
        {
            float3 posA = _ucA.simData.positionsReadOnly[pair.SimIndexA];
            float3 posB = _ucB.simData.positionsReadOnly[pair.SimIndexB];
            float3 target = (posA + posB) * 0.5f;

            _ucA.simData.sewingConstraints.Add(new UCloth.UCSewingConstraint
            {
                nodeIndex      = (ushort)pair.SimIndexA,
                targetPosition = target,
                stiffness      = _stiffness
            });

            _ucB.simData.sewingConstraints.Add(new UCloth.UCSewingConstraint
            {
                nodeIndex      = (ushort)pair.SimIndexB,
                targetPosition = target,
                stiffness      = _stiffness
            });
        }
    }

    /// <summary>Tháo toàn bộ mối khâu và xóa constraints.</summary>
    public void RemoveAllWelds()
    {
        _weldedPairs.Clear();
        if (_ucA?.simData?.sewingConstraints.IsCreated == true) _ucA.simData.sewingConstraints.Clear();
        if (_ucB?.simData?.sewingConstraints.IsCreated == true) _ucB.simData.sewingConstraints.Clear();
        Debug.Log("<color=orange>[MeshSewer]</color> Đã tháo toàn bộ mối khâu");
    }

    public int WeldCount => _weldedPairs.Count;
}
