using MahjongOut3D.Core;
using MahjongOut3D.Gameplay;
using MahjongOut3D.GameplayInput;
using MahjongOut3D.TileSystem;
using UnityEngine;

namespace MahjongOut3D.CameraSystem
{
    /// <summary>
    /// Rotates the generated Mahjong block itself so lighting remains stable while the player inspects the puzzle.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BlockRotationController : MonoBehaviour
    {
        private const float FallbackRotationSpeed = 0.2f;
        private const float FallbackVerticalRotationMultiplier = 1.5f;
        private const float FallbackRotationInertia = 0.88f;
        private const float FallbackRotationSmoothing = 0.08f;

        [SerializeField] private OrbitCameraSettings settings;
        [SerializeField] private Transform rotationTarget;
        [SerializeField] private bool resetRotationWhenTargetChanges = true;
        [SerializeField, Min(0f)] private float idleRotationDelaySeconds = 15f;
        [SerializeField] private float idleRotationSpeedDegrees = 3f;

        private bool isInitialized;
        private float lastActivityTime;
        private EventBus eventBus;
        private Transform contentTarget;
        private Transform contentOriginalParent;
        private Transform dynamicPivot;
        private Quaternion baseLocalRotation = Quaternion.identity;
        private Quaternion targetRotationOffset = Quaternion.identity;
        private Quaternion currentRotationOffset = Quaternion.identity;
        private Vector2 inertialRotationVelocity;
        private int lastDragFrame = -1;

        /// <summary>
        /// Gets the transform currently being rotated.
        /// </summary>
        public Transform RotationTarget => contentTarget != null ? contentTarget : rotationTarget;

        /// <summary>
        /// Initializes the rotation controller.
        /// </summary>
        /// <param name="context">Shared runtime context.</param>
        public void Initialize(GameContext context)
        {
            if (isInitialized)
            {
                return;
            }

            isInitialized = true;
            eventBus = context?.EventBus;
            if (eventBus != null)
            {
                eventBus.Subscribe<ScreenActivityInputEvent>(HandleScreenActivity);
            }

            lastActivityTime = GetCurrentTime();
            ApplyTargetRotation(true);
        }

        /// <summary>
        /// Clears transient runtime motion state.
        /// </summary>
        public void Shutdown()
        {
            eventBus?.Unsubscribe<ScreenActivityInputEvent>(HandleScreenActivity);
            eventBus = null;
            RestoreContentParent();
            if (dynamicPivot != null)
            {
                Destroy(dynamicPivot.gameObject);
                dynamicPivot = null;
            }

            isInitialized = false;
            rotationTarget = null;
            contentTarget = null;
            contentOriginalParent = null;
            targetRotationOffset = Quaternion.identity;
            currentRotationOffset = Quaternion.identity;
            inertialRotationVelocity = Vector2.zero;
            lastDragFrame = -1;
            lastActivityTime = 0f;
        }

        /// <summary>
        /// Updates which transform should spin when the player drags.
        /// </summary>
        /// <param name="target">Transform to rotate.</param>
        public void SetRotationTarget(Transform target)
        {
            if (target == null)
            {
                RestoreContentParent();
                rotationTarget = null;
                contentTarget = null;
                contentOriginalParent = null;
                ApplyTargetRotation(true);
                return;
            }

            if (target == contentTarget)
            {
                UpdateDynamicPivotPosition();
                return;
            }

            RestoreContentParent();
            contentTarget = target;
            contentOriginalParent = target.parent;
            rotationTarget = EnsureDynamicPivot(target);
            baseLocalRotation = rotationTarget != null ? rotationTarget.localRotation : Quaternion.identity;

            if (resetRotationWhenTargetChanges)
            {
                targetRotationOffset = Quaternion.identity;
                currentRotationOffset = Quaternion.identity;
                inertialRotationVelocity = Vector2.zero;

                if (rotationTarget != null)
                {
                    rotationTarget.localRotation = baseLocalRotation;
                }
            }

            ApplyTargetRotation(true);
        }

        /// <summary>
        /// Applies a drag delta to the puzzle block rotation.
        /// </summary>
        /// <param name="screenDelta">Pointer delta in screen pixels.</param>
        public void Rotate(Vector2 screenDelta)
        {
            if (!isInitialized || rotationTarget == null)
            {
                return;
            }

            float deltaTime = Mathf.Max(GetDeltaTime(), 0.0001f);
            Vector2 scaledDelta = screenDelta * GetRotationSpeed();
            scaledDelta.y *= GetVerticalRotationMultiplier();

            StopIdleRotation();
            ApplyRotationDelta(scaledDelta);

            inertialRotationVelocity = scaledDelta / deltaTime;
            lastDragFrame = Time.frameCount;
        }

        /// <summary>
        /// Advances the smoothed block rotation.
        /// </summary>
        private void LateUpdate()
        {
            if (!isInitialized || rotationTarget == null)
            {
                return;
            }

            ApplyRotationInertia();
            ApplyIdleRotation();
            ApplyTargetRotation(false);
        }

        private void HandleScreenActivity(ScreenActivityInputEvent eventData)
        {
            lastActivityTime = GetCurrentTime();
            StopIdleRotation();
            inertialRotationVelocity = Vector2.zero;
        }

        private void ApplyIdleRotation()
        {
            if (idleRotationDelaySeconds <= 0f || GetCurrentTime() - lastActivityTime < idleRotationDelaySeconds)
            {
                return;
            }

            targetRotationOffset = Quaternion.AngleAxis(-idleRotationSpeedDegrees * GetDeltaTime(), Vector3.up) * targetRotationOffset;
        }

        private void StopIdleRotation()
        {
            lastActivityTime = GetCurrentTime();
        }

        /// <summary>
        /// Applies decaying inertial rotation after the drag ends.
        /// </summary>
        private void ApplyRotationInertia()
        {
            if (lastDragFrame == Time.frameCount)
            {
                return;
            }

            if (inertialRotationVelocity.sqrMagnitude <= 0.0001f)
            {
                inertialRotationVelocity = Vector2.zero;
                return;
            }

            float deltaTime = GetDeltaTime();
            ApplyRotationDelta(inertialRotationVelocity * deltaTime);

            float decay = Mathf.Pow(Mathf.Clamp01(GetRotationInertia()), deltaTime * 60f);
            inertialRotationVelocity *= decay;
        }

        /// <summary>
        /// Applies the smoothed local rotation to the current target.
        /// </summary>
        /// <param name="snap">True to snap immediately.</param>
        private void ApplyTargetRotation(bool snap)
        {
            if (rotationTarget == null)
            {
                return;
            }

            if (snap)
            {
                currentRotationOffset = targetRotationOffset;
            }
            else
            {
                float interpolation = 1f - Mathf.Exp(-GetDeltaTime() / Mathf.Max(0.0001f, GetRotationSmoothing()));
                currentRotationOffset = Quaternion.Slerp(currentRotationOffset, targetRotationOffset, interpolation);
            }

            rotationTarget.localRotation = currentRotationOffset * baseLocalRotation;
        }

        /// <summary>
        /// Wraps the block in a runtime pivot so rotations happen around the remaining tile center.
        /// </summary>
        private Transform EnsureDynamicPivot(Transform target)
        {
            if (dynamicPivot == null)
            {
                GameObject pivotObject = new GameObject("Runtime Block Rotation Pivot");
                dynamicPivot = pivotObject.transform;
            }

            dynamicPivot.SetParent(contentOriginalParent, true);
            dynamicPivot.SetPositionAndRotation(ResolveContentWorldCenter(target), target.rotation);
            target.SetParent(dynamicPivot, true);
            return dynamicPivot;
        }

        /// <summary>
        /// Re-centers the runtime pivot without changing the block's visible world pose.
        /// </summary>
        public void UpdateDynamicPivotPosition()
        {
            if (dynamicPivot == null || contentTarget == null || contentTarget.parent != dynamicPivot)
            {
                return;
            }

            Transform pivotParent = dynamicPivot.parent;
            Quaternion pivotRotation = dynamicPivot.rotation;
            contentTarget.SetParent(pivotParent, true);
            dynamicPivot.position = ResolveContentWorldCenter(contentTarget);
            dynamicPivot.rotation = pivotRotation;
            contentTarget.SetParent(dynamicPivot, true);
        }

        private void RestoreContentParent()
        {
            if (contentTarget != null && dynamicPivot != null && contentTarget.parent == dynamicPivot)
            {
                contentTarget.SetParent(contentOriginalParent, true);
            }
        }

        private static Vector3 ResolveContentWorldCenter(Transform target)
        {
            if (target == null)
            {
                return Vector3.zero;
            }

            if (TryGetRemainingTileLocalCenter(target, out Vector3 localCenter))
            {
                return target.TransformPoint(localCenter);
            }

            if (TryGetRendererBounds(target, out Bounds rendererBounds))
            {
                return rendererBounds.center;
            }

            return target.position;
        }

        private static bool TryGetRemainingTileLocalCenter(Transform target, out Vector3 localCenter)
        {
            localCenter = Vector3.zero;
            if (target == null)
            {
                return false;
            }

            MahjongTile[] tiles = target.GetComponentsInChildren<MahjongTile>(true);
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool hasBounds = false;

            for (int index = 0; index < tiles.Length; index++)
            {
                MahjongTile tile = tiles[index];
                if (tile == null || tile.IsRemoved || tile.IsMatched || tile.IsBufferedSelection)
                {
                    continue;
                }

                Vector3 tileLocalPos = target.InverseTransformPoint(tile.transform.position);
                min = Vector3.Min(min, tileLocalPos);
                max = Vector3.Max(max, tileLocalPos);
                hasBounds = true;
            }

            if (!hasBounds)
            {
                return false;
            }

            localCenter = (min + max) * 0.5f;
            return true;
        }

        private static bool TryGetRendererBounds(Transform target, out Bounds bounds)
        {
            bounds = default;
            if (target == null)
            {
                return false;
            }

            Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
            bool hasBounds = false;
            for (int index = 0; index < renderers.Length; index++)
            {
                Renderer renderer = renderers[index];
                if (renderer == null || !renderer.enabled)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return hasBounds;
        }

        /// <summary>
        /// Applies one unconstrained drag delta to the target quaternion.
        /// </summary>
        /// <param name="scaledDelta">Scaled pointer delta already converted into angular units.</param>
        private void ApplyRotationDelta(Vector2 scaledDelta)
        {
            Quaternion yawRotation = Quaternion.AngleAxis(-scaledDelta.x, Vector3.up);
            Quaternion pitchRotation = Quaternion.AngleAxis(scaledDelta.y, Vector3.right);
            targetRotationOffset = yawRotation * pitchRotation * targetRotationOffset;
        }

        /// <summary>
        /// Gets the active simulation delta time.
        /// </summary>
        /// <returns>Simulation delta time.</returns>
        private float GetDeltaTime()
        {
            if (settings != null && settings.UseUnscaledTime)
            {
                return Time.unscaledDeltaTime;
            }

            return Time.deltaTime;
        }

        private float GetCurrentTime()
        {
            if (settings != null && settings.UseUnscaledTime)
            {
                return Time.unscaledTime;
            }

            return Time.time;
        }

        /// <summary>
        /// Gets the configured horizontal rotation speed.
        /// </summary>
        /// <returns>Rotation speed.</returns>
        private float GetRotationSpeed()
        {
            return settings != null ? settings.RotationSpeed : FallbackRotationSpeed;
        }

        /// <summary>
        /// Gets the configured vertical rotation multiplier.
        /// </summary>
        /// <returns>Vertical rotation multiplier.</returns>
        private float GetVerticalRotationMultiplier()
        {
            return settings != null ? settings.VerticalRotationMultiplier : FallbackVerticalRotationMultiplier;
        }

        /// <summary>
        /// Gets the configured rotation inertia.
        /// </summary>
        /// <returns>Rotation inertia.</returns>
        private float GetRotationInertia()
        {
            return settings != null ? settings.RotationInertia : FallbackRotationInertia;
        }

        /// <summary>
        /// Gets the configured smoothing duration.
        /// </summary>
        /// <returns>Rotation smoothing duration.</returns>
        private float GetRotationSmoothing()
        {
            return settings != null ? settings.RotationSmoothing : FallbackRotationSmoothing;
        }
    }
}
