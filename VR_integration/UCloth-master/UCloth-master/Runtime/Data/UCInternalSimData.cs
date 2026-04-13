using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

namespace UCloth
{

    /// <summary>
    /// A dynamic sewing constraint: pulls <see cref="nodeIndex"/> toward
    /// <see cref="targetPosition"/> every simulation step.
    /// Rest distance is 0 — the two sewn nodes are driven to coincide.
    /// </summary>
    public struct UCSewingConstraint
    {
        /// <summary>Node index on the cloth that owns this simData.</summary>
        public ushort nodeIndex;
        /// <summary>World-space target supplied each frame by the sewing manager.</summary>
        public float3 targetPosition;
        /// <summary>Constraint stiffness (0–1). 1 = fully rigid seam.</summary>
        public float stiffness;
    }

    /// <summary>
    /// Stores simulation data used by <see cref="UCCloth"/>.
    /// </summary>
    public class UCInternalSimData : IDisposable
    {
        // --- Integration Data

        /// <summary>
        /// Read-only access to node positions.
        /// </summary>
        public NativeArray<float3> positionsReadOnly;
        internal NativeArray<float3> cPositions;

        /// <summary>
        /// Read-only access to node velocities.
        /// </summary>
        public NativeArray<float3> velocityReadOnly;
        internal NativeArray<float3> cVelocity;

        internal NativeArray<float3> cAcceleration;
        internal NativeArray<float3> cTempAcceleration;

        internal NativeArray<float> cFriction;


        // --- Sewing Constraints (cross-cloth, zero-rest-distance)
        /// <summary>
        /// Writeable list of active sewing constraints for this cloth.
        /// Populated each frame by <c>MeshSewer_UCloth</c> before the job runs.
        /// The job reads this as a copy (<see cref="cSewingConstraints"/>).
        /// </summary>
        public NativeList<UCSewingConstraint> sewingConstraints;
        internal NativeArray<UCSewingConstraint> cSewingConstraints;


        // --- Mesh Data

        // For those fields, it's not common to write them from outside, so they can be kept as read-only
        // These map directly to memory used in the sim

        /// <summary>
        /// Read-only access to edges (node connections).
        /// </summary>
        public NativeArray<UCEdge> edgesReadOnly;

        /// <summary>
        /// Read-only access to node connections at which bending forces are applied.
        /// </summary>
        public NativeArray<UCBendingEdge> bendingEdgesReadOnly;

        /// <summary>
        /// Read-only access to resting edge lengths.
        /// </summary>
        public NativeArray<float> restDistancesReadOnly;

        /// <summary>
        /// Read-only access to node neighbours.
        /// </summary>
        public NativeParallelMultiHashMap<ushort, ushort> neighboursReadOnly;


        // --- Helper Data

        /// <summary>
        /// Read-only access to node normals.
        /// </summary>
        public NativeArray<float3> normalsReadOnly;
        internal NativeArray<float3> cNormals;


        /// <summary>
        /// Read-only access to triangle normals.
        /// </summary>
        public NativeArray<float3> triangleNormalsReadOnly;
        internal NativeArray<float3> cTriangleNormals;



        /// <summary>
        /// The reciprocal of the weight. 0 for pinned nodes. Writeable.
        /// </summary>
        public NativeArray<float> reciprocalWeight;
        internal NativeArray<float> cReciprocalWeight;

        // --- Pins
        /// <summary>
        /// World-space position specified only for pinned nodes. Writeable.
        /// </summary>
        public NativeParallelHashMap<ushort, float3> pinnedPositions;
        internal NativeParallelHashMap<ushort, float3> cPinnedPositions;

        // --- Spatial Partitioning
        internal Native3DHashmapArray<ushort> cSelfCollisionRegions;
        internal NativeParallelHashSet<int3> cUtilizedSelfColRegions;


        private bool _applyPending = true;

        internal void PrepareCopies()
        {

            if (!sewingConstraints.IsCreated)
                sewingConstraints = new NativeList<UCSewingConstraint>(16, Allocator.Persistent);

            if (!positionsReadOnly.IsCreated)
                positionsReadOnly = new NativeArray<float3>(cPositions, Allocator.Persistent);

            if (!velocityReadOnly.IsCreated)
                velocityReadOnly = new NativeArray<float3>(cVelocity, Allocator.Persistent);


            if (!normalsReadOnly.IsCreated)
                normalsReadOnly = new NativeArray<float3>(cNormals, Allocator.Persistent);

            if (!triangleNormalsReadOnly.IsCreated)
                triangleNormalsReadOnly = new NativeArray<float3>(cTriangleNormals, Allocator.Persistent);


            if (!cReciprocalWeight.IsCreated)
                cReciprocalWeight = new NativeArray<float>(reciprocalWeight, Allocator.Persistent);

            if (!cPinnedPositions.IsCreated)
                cPinnedPositions = new NativeParallelHashMap<ushort, float3>(pinnedPositions.Capacity, Allocator.Persistent);
        }

        internal void CopyWriteableData()
        {
            // Read-only
            positionsReadOnly.CopyFrom(cPositions);
            velocityReadOnly.CopyFrom(cVelocity);

            normalsReadOnly.CopyFrom(cNormals);
            triangleNormalsReadOnly.CopyFrom(cTriangleNormals);

            // No need to do this every frame, only do this if modified
            if (_applyPending)
            {
                // Pinned positions
                cPinnedPositions.Clear();

                // Only schedule the copy job when there are actual pinned entries.
                // ScheduleBatch with capacity=0 does not execute the job, which means
                // [DeallocateOnJobCompletion] never fires and the TempJob KeyValues leak.
                if (pinnedPositions.Count() > 0)
                {
                    NativeParallelHashMapCopyJob<ushort, float3> copyJob = new(pinnedPositions, cPinnedPositions);
                    var copyHandle = copyJob.ScheduleBatch(pinnedPositions.Capacity, 256);
                    copyHandle.Complete();
                }

                // Weight
                cReciprocalWeight.CopyFrom(reciprocalWeight);
                _applyPending = false;
            }

            // Sync sewing constraints into a Persistent NativeArray.
            // Resize (dispose + recreate) only when count changes — avoids TempJob leaks.
            int sewCount = sewingConstraints.Length;
            if (!cSewingConstraints.IsCreated || cSewingConstraints.Length != sewCount)
            {
                if (cSewingConstraints.IsCreated) cSewingConstraints.Dispose();
                cSewingConstraints = new NativeArray<UCSewingConstraint>(sewCount, Allocator.Persistent);
            }
            if (sewCount > 0)
                cSewingConstraints.CopyFrom(sewingConstraints.AsArray());
        }


        /// <summary>
        /// Applies any external modifications to writeable data.
        /// </summary>
        public void ApplyModifiedData()
        {
            _applyPending = true;
        }

        public void Dispose()
        {
            positionsReadOnly.Dispose();
            cPositions.Dispose();

            velocityReadOnly.Dispose();
            cVelocity.Dispose();

            cAcceleration.Dispose();
            cTempAcceleration.Dispose();

            cFriction.Dispose();

            edgesReadOnly.Dispose();
            bendingEdgesReadOnly.Dispose();
            neighboursReadOnly.Dispose();
            normalsReadOnly.Dispose();
            cNormals.Dispose();
            triangleNormalsReadOnly.Dispose();
            cTriangleNormals.Dispose();
            restDistancesReadOnly.Dispose();
            reciprocalWeight.Dispose();
            cReciprocalWeight.Dispose();
            pinnedPositions.Dispose();
            cPinnedPositions.Dispose();

            if (sewingConstraints.IsCreated) sewingConstraints.Dispose();
            if (cSewingConstraints.IsCreated) cSewingConstraints.Dispose();

            if (cSelfCollisionRegions.IsCreated())
                cSelfCollisionRegions.Dispose();

            if (cUtilizedSelfColRegions.IsCreated)
                cUtilizedSelfColRegions.Dispose();
        }
    }
}