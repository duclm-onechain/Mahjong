using UnityEngine;

namespace MahjongOut3D.LevelSystem
{
    /// <summary>
    /// Provides deterministic six-face pose and magnetic snap calculations for manual layouts.
    /// </summary>
    public static class TileSnapMath
    {
        public static Vector3 GetNormal(VoxelGridDirection direction)
        {
            return ((Vector3)VoxelGridDirections.GetOffset(direction)).normalized;
        }

        public static VoxelGridDirection GetCardinalDirection(Vector3 normal)
        {
            Vector3 absolute = new Vector3(Mathf.Abs(normal.x), Mathf.Abs(normal.y), Mathf.Abs(normal.z));
            if (absolute.x >= absolute.y && absolute.x >= absolute.z)
            {
                return normal.x >= 0f ? VoxelGridDirection.Right : VoxelGridDirection.Left;
            }

            if (absolute.y >= absolute.x && absolute.y >= absolute.z)
            {
                return normal.y >= 0f ? VoxelGridDirection.Up : VoxelGridDirection.Down;
            }

            return normal.z >= 0f ? VoxelGridDirection.Forward : VoxelGridDirection.Back;
        }

        public static Quaternion GetRotation(VoxelGridDirection face, int rollQuarterTurns)
        {
            Vector3 normal = GetNormal(face);
            Quaternion faceRotation = Quaternion.FromToRotation(Vector3.up, normal);
            Quaternion rollRotation = Quaternion.AngleAxis(NormalizeRoll(rollQuarterTurns) * 90f, normal);
            return rollRotation * faceRotation;
        }

        public static int NormalizeRoll(int rollQuarterTurns)
        {
            return ((rollQuarterTurns % 4) + 4) % 4;
        }

        public static Vector3 Quantize(Vector3 value, Vector3 step, TileLayoutSnapMode mode)
        {
            if (mode == TileLayoutSnapMode.Off || mode == TileLayoutSnapMode.Adjacent)
            {
                return value;
            }

            int divisions = mode == TileLayoutSnapMode.Half ? 2 : 4;
            return new Vector3(
                QuantizeAxis(value.x, step.x, divisions),
                QuantizeAxis(value.y, step.y, divisions),
                QuantizeAxis(value.z, step.z, divisions));
        }

        public static Vector3 GetFaceBasisU(VoxelGridDirection face, int rollQuarterTurns)
        {
            GetSurfaceBasis(face, rollQuarterTurns, out _, out Vector3 tangentU, out _);
            return tangentU;
        }

        public static Vector3 GetFaceBasisV(VoxelGridDirection face, int rollQuarterTurns)
        {
            GetSurfaceBasis(face, rollQuarterTurns, out _, out _, out Vector3 tangentV);
            return tangentV;
        }

        public static void GetSurfaceBasis(
            VoxelGridDirection face,
            int rollQuarterTurns,
            out Vector3 normal,
            out Vector3 tangentU,
            out Vector3 tangentV)
        {
            normal = GetNormal(face);
            tangentU = face == VoxelGridDirection.Left || face == VoxelGridDirection.Right
                ? Vector3.forward
                : Vector3.right;
            tangentV = face == VoxelGridDirection.Up || face == VoxelGridDirection.Down
                ? Vector3.forward
                : Vector3.up;

            Quaternion roll = Quaternion.AngleAxis(NormalizeRoll(rollQuarterTurns) * 90f, normal);
            tangentU = (roll * tangentU).normalized;
            tangentV = (roll * tangentV).normalized;
        }

        public static Vector3 GetAdjacentWorldDirection(
            VoxelGridDirection surfaceFace,
            int rollQuarterTurns,
            VoxelGridDirection adjacentSide)
        {
            GetSurfaceBasis(surfaceFace, rollQuarterTurns, out Vector3 normal, out Vector3 tangentU, out Vector3 tangentV);
            switch (adjacentSide)
            {
                case VoxelGridDirection.Left:
                    return -tangentU;
                case VoxelGridDirection.Right:
                    return tangentU;
                case VoxelGridDirection.Down:
                    return -tangentV;
                case VoxelGridDirection.Up:
                    return tangentV;
                case VoxelGridDirection.Back:
                    return -normal;
                case VoxelGridDirection.Forward:
                default:
                    return normal;
            }
        }

        public static Vector3 GetTangentialOffset(
            Vector3 sizeSource,
            Quaternion sizeSourceRotation,
            Vector3 tangentU,
            Vector3 tangentV,
            int offsetU,
            int offsetV,
            int divisions = 4)
        {
            // Offset values are always stored in quarter units. A half-tile is 2/4,
            // while a quarter-tile is 1/4. Do not change this denominator to 2
            // when the UI is in Half mode, or 2/4 would incorrectly become 1 tile.
            int safeDivisions = Mathf.Max(1, divisions);
            Vector3 safeTangentU = tangentU.sqrMagnitude > 0.0001f ? tangentU.normalized : Vector3.right;
            Vector3 safeTangentV = tangentV.sqrMagnitude > 0.0001f ? tangentV.normalized : Vector3.up;
            float widthU = GetOrientedExtent(sizeSource, sizeSourceRotation, safeTangentU) * 2f;
            float widthV = GetOrientedExtent(sizeSource, sizeSourceRotation, safeTangentV) * 2f;
            return safeTangentU * (widthU * offsetU / safeDivisions)
                + safeTangentV * (widthV * offsetV / safeDivisions);
        }

        public static void GetLocalTangentialBasis(Vector3 localDirection, out Vector3 localU, out Vector3 localV)
        {
            Vector3 direction = localDirection.sqrMagnitude > 0.0001f ? localDirection.normalized : Vector3.up;
            if (Mathf.Abs(direction.x) > 0.9f)
            {
                localU = Vector3.up;
                localV = Vector3.forward;
                return;
            }

            if (Mathf.Abs(direction.y) > 0.9f)
            {
                localU = Vector3.right;
                localV = Vector3.forward;
                return;
            }

            localU = Vector3.right;
            localV = Vector3.up;
        }

        public static void GetWorldTangentialBasis(Vector3 worldNormal, out Vector3 tangentU, out Vector3 tangentV)
        {
            Vector3 normal = worldNormal.sqrMagnitude > 0.0001f ? worldNormal.normalized : Vector3.up;
            Vector3 reference = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up;
            tangentU = Vector3.Cross(reference, normal).normalized;
            tangentV = Vector3.Cross(normal, tangentU).normalized;
        }

        public static Vector3 GetAdjacentPosition(
            Vector3 sourcePosition,
            Quaternion sourceRotation,
            Vector3 sourceSize,
            Vector3 targetSize,
            Quaternion targetRotation,
            Vector3 worldDirection,
            float gap,
            Vector2 tangentialOffset)
        {
            Vector3 normal = worldDirection.sqrMagnitude > 0.0001f ? worldDirection.normalized : Vector3.right;
            GetWorldTangentialBasis(normal, out Vector3 tangentU, out Vector3 tangentV);
            float sourceExtent = GetOrientedExtent(sourceSize, sourceRotation, normal);
            float targetExtent = GetOrientedExtent(targetSize, targetRotation, normal);
            return sourcePosition
                + (normal * (sourceExtent + targetExtent + Mathf.Max(0f, gap)))
                + (tangentU * tangentialOffset.x)
                + (tangentV * tangentialOffset.y);
        }

        public static bool TryFindMagneticPosition(
            Vector3 draggedPosition,
            Vector3 draggedSize,
            Quaternion draggedRotation,
            Vector3 targetPosition,
            Vector3 targetSize,
            Quaternion targetRotation,
            float snapDistance,
            float gap,
            out Vector3 snappedPosition)
        {
            snappedPosition = draggedPosition;
            Vector3[] localDirections =
            {
                Vector3.right,
                Vector3.left,
                Vector3.up,
                Vector3.down,
                Vector3.forward,
                Vector3.back,
            };

            float bestDistanceSquared = Mathf.Max(0f, snapDistance) * Mathf.Max(0f, snapDistance);
            bool found = false;
            for (int index = 0; index < localDirections.Length; index++)
            {
                Vector3 candidate = GetAdjacentPosition(
                    targetPosition,
                    targetRotation,
                    targetSize,
                    draggedSize,
                    draggedRotation,
                    localDirections[index],
                    gap,
                    Vector2.zero);
                float distanceSquared = (candidate - draggedPosition).sqrMagnitude;
                if (distanceSquared > bestDistanceSquared)
                {
                    continue;
                }

                bestDistanceSquared = distanceSquared;
                snappedPosition = candidate;
                found = true;
            }

            return found;
        }

        public static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsNaN(value.z)
                && !float.IsInfinity(value.x) && !float.IsInfinity(value.y) && !float.IsInfinity(value.z);
        }

        private static float QuantizeAxis(float value, float step, int divisions)
        {
            float quantum = Mathf.Abs(step) / divisions;
            return quantum <= Mathf.Epsilon ? value : Mathf.Round(value / quantum) * quantum;
        }

        public static float GetOrientedExtent(Vector3 size, Quaternion rotation, Vector3 worldAxis)
        {
            Vector3 localAxis = Quaternion.Inverse(rotation) * worldAxis.normalized;
            Vector3 half = size * 0.5f;
            Vector3 absolute = new Vector3(Mathf.Abs(localAxis.x), Mathf.Abs(localAxis.y), Mathf.Abs(localAxis.z));
            return Vector3.Dot(half, absolute);
        }

    }
}
