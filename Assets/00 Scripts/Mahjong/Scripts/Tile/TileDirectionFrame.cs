using MahjongOut3D.LevelSystem;
using UnityEngine;

namespace MahjongOut3D.TileSystem
{
    /// <summary>
    /// Defines the stable local direction frame of a tile prefab.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TileDirectionFrame : MonoBehaviour
    {
        [Header("Reference Surface")]
        [SerializeField] private Transform objectFill;

        [Header("Direction Anchors")]
        [SerializeField] private Transform forward;
        [SerializeField] private Transform back;
        [SerializeField] private Transform left;
        [SerializeField] private Transform right;
        [SerializeField] private Transform up;
        [SerializeField] private Transform down;

        public Transform ObjectFill => objectFill;

        /// <summary>
        /// Gets the local direction represented by an anchor.
        /// </summary>
        public bool TryGetLocalDirection(VoxelGridDirection direction, out Vector3 localDirection)
        {
            Transform anchor = GetAnchor(direction);
            if (anchor == null || !anchor.IsChildOf(transform))
            {
                localDirection = GetFallbackLocalDirection(direction);
                return false;
            }

            Vector3 anchorPosition = transform.InverseTransformPoint(anchor.position);
            localDirection = anchorPosition.normalized;
            return localDirection.sqrMagnitude > 0.0001f;
        }

        /// <summary>
        /// Converts a prefab direction into the world direction of a posed tile.
        /// </summary>
        public bool TryGetWorldDirection(
            Quaternion tileRotation,
            VoxelGridDirection direction,
            out Vector3 worldDirection)
        {
            bool hasAnchor = TryGetLocalDirection(direction, out Vector3 localDirection);
            worldDirection = (tileRotation * localDirection).normalized;
            return hasAnchor && worldDirection.sqrMagnitude > 0.0001f;
        }

        /// <summary>
        /// Validates the configured anchors against the ObjectFill orientation and dimensions.
        /// </summary>
        public bool Validate(out string error)
        {
            if (objectFill == null)
            {
                error = "ObjectFill is not assigned.";
                return false;
            }

            if (objectFill.IsChildOf(transform) == false)
            {
                error = "ObjectFill must be a child of the tile root.";
                return false;
            }

            for (int index = 0; index < VoxelGridDirections.Cardinals.Length; index++)
            {
                if (!TryGetLocalDirection(VoxelGridDirections.Cardinals[index], out _))
                {
                    error = $"The {VoxelGridDirections.Cardinals[index]} direction anchor is missing or invalid.";
                    return false;
                }
            }

            TryGetLocalDirection(VoxelGridDirection.Forward, out Vector3 forwardDirection);
            TryGetLocalDirection(VoxelGridDirection.Back, out Vector3 backDirection);
            TryGetLocalDirection(VoxelGridDirection.Left, out Vector3 leftDirection);
            TryGetLocalDirection(VoxelGridDirection.Right, out Vector3 rightDirection);
            TryGetLocalDirection(VoxelGridDirection.Up, out Vector3 upDirection);
            TryGetLocalDirection(VoxelGridDirection.Down, out Vector3 downDirection);

            if (Vector3.Dot(forwardDirection, backDirection) > -0.95f
                || Vector3.Dot(leftDirection, rightDirection) > -0.95f
                || Vector3.Dot(upDirection, downDirection) > -0.95f)
            {
                error = "Opposite direction anchors must point in opposite directions.";
                return false;
            }

            // ObjectFill's visible face points toward the opposite side of its mesh normal.
            Vector3 fillNormal = -transform.InverseTransformDirection(objectFill.forward).normalized;
            if (Vector3.Dot(fillNormal, forwardDirection) < 0.95f)
            {
                error = "Forward must point out from the ObjectFill surface.";
                return false;
            }

            if (Mathf.Abs(Vector3.Dot(forwardDirection, rightDirection)) > 0.05f
                || Mathf.Abs(Vector3.Dot(forwardDirection, upDirection)) > 0.05f
                || Mathf.Abs(Vector3.Dot(rightDirection, upDirection)) > 0.05f)
            {
                error = "Forward, Right, and Up direction anchors must be perpendicular.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private Transform GetAnchor(VoxelGridDirection direction)
        {
            switch (direction)
            {
                case VoxelGridDirection.Left:
                    return left;
                case VoxelGridDirection.Right:
                    return right;
                case VoxelGridDirection.Down:
                    return down;
                case VoxelGridDirection.Up:
                    return up;
                case VoxelGridDirection.Back:
                    return back;
                case VoxelGridDirection.Forward:
                    return forward;
                default:
                    return null;
            }
        }

        private Vector3 GetFallbackLocalDirection(VoxelGridDirection direction)
        {
            return (Vector3)VoxelGridDirections.GetOffset(direction);
        }
    }
}
