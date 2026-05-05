using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

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
    }

    public void AddWeldPair(int simIdxA, int simIdxB)
    {
        if (_ucA?.simData == null || _ucB?.simData == null) return;
        
        // Kiểm tra xem cặp này đã tồn tại chưa để tránh add trùng
        if (_weldedPairs.Exists(p => p.SimIndexA == simIdxA && p.SimIndexB == simIdxB)) return;

        _weldedPairs.Add(new SewPair { SimIndexA = simIdxA, SimIndexB = simIdxB });
    }

    public void UpdateWeldPhysics()
    {
        if (_weldedPairs.Count == 0) return;
        if (_ucA?.simData == null || _ucB?.simData == null) return;
        if (!_ucA.simData.sewingConstraints.IsCreated || !_ucB.simData.sewingConstraints.IsCreated) return;

        // Xóa để ghi lại toàn bộ danh sách tích lũy mỗi frame vật lý
        _ucA.simData.sewingConstraints.Clear();
        _ucB.simData.sewingConstraints.Clear();

        foreach (var pair in _weldedPairs)
        {
            float3 posA = _ucA.simData.positionsReadOnly[pair.SimIndexA];
            float3 posB = _ucB.simData.positionsReadOnly[pair.SimIndexB];
            
            // Điểm mục tiêu là trung điểm của 2 node để kéo chúng lại gần nhau
            float3 target = (posA + posB) * 0.5f;

            _ucA.simData.sewingConstraints.Add(new UCloth.UCSewingConstraint
            {
                nodeIndex = (ushort)pair.SimIndexA,
                targetPosition = target,
                stiffness = _stiffness
            });

            _ucB.simData.sewingConstraints.Add(new UCloth.UCSewingConstraint
            {
                nodeIndex = (ushort)pair.SimIndexB,
                targetPosition = target,
                stiffness = _stiffness
            });
        }
    }

    public void RemoveAllWelds()
    {
        _weldedPairs.Clear();
        if (_ucA?.simData?.sewingConstraints.IsCreated == true) _ucA.simData.sewingConstraints.Clear();
        if (_ucB?.simData?.sewingConstraints.IsCreated == true) _ucB.simData.sewingConstraints.Clear();
    }
}