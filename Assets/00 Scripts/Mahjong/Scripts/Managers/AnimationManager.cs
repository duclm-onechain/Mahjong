using MahjongOut3D.Core;
using MahjongOut3D.CameraSystem;
using MahjongOut3D.Data;
using MahjongOut3D.TileSystem;
using MahjongOut3D.UI;
using MahjongOut3D.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace MahjongOut3D.Managers
{
    /// <summary>
    /// Coordinates tile motion feedback, hint flashes and camera shake.
    /// </summary>
    public sealed class AnimationManager : ManagerBehaviour
    {
        private const int TrayOverlaySortingOrder = 450;

        [SerializeField] private TileAnimationSettings animationSettings;
        [SerializeField] private TraySlotAnchorProvider traySlotAnchorProvider;
        [SerializeField, Min(0.1f)] private float inventoryItemPreviewScale = 1f;
        [SerializeField, Min(1f)] private float trayMatchArcSideDistance = 180f;
        [SerializeField, Min(0f)] private float trayMatchArcLift = 96f;
        [SerializeField, Min(0f)] private float trayMatchLandingYOffset = 72f;
        [SerializeField, Min(0.1f)] private float trayMatchLandingScale = 1.5f;
        [SerializeField, Min(0.05f)] private float trayMatchPreviewMinDurationSeconds = 0.34f;
        [SerializeField, Range(0.1f, 1f)] private float trayMatchEffectTriggerNormalized = 0.82f;
        [SerializeField, Range(0f, 90f)] private float matchTileMaxTiltAngle = 70f;
        [SerializeField] private Image trayCapacityWarningImage;
        [SerializeField, Min(0.05f)] private float trayShakeDurationSeconds = 1f;
        [SerializeField, Min(0f)] private float trayShakeAmplitude = 1.5f;
        [SerializeField, Min(0f)] private float trayShakeRotationDegrees = 2.5f;
        [SerializeField, Min(0f)] private float trayShakeVerticalAmplitude = 0.5f;
        [SerializeField, Min(0.05f)] private float trayWarningDurationSeconds = 3f;
        [SerializeField, Min(0.01f)] private float trayWarningBlinkIntervalSeconds = 0.12f;

        private Coroutine trayShakeRoutine;
        private Coroutine trayWarningRoutine;
        private int animationLockCount;
        private readonly Dictionary<RuntimeTrayPreview, TrayShakeState> activeTrayShakeStates = new Dictionary<RuntimeTrayPreview, TrayShakeState>();

        private readonly Dictionary<MahjongTile, RuntimeTrayPreview> trayPreviewsByTile = new Dictionary<MahjongTile, RuntimeTrayPreview>();
        private ComponentPool<ParticleSystem> particlePool;
        private MatchUiShardEffect matchUiShardEffect;
        private RectTransform trayOverlayRoot;
        private Canvas trayOverlayCanvas;
        private MahjongTile activeHintFirstTile;
        private MahjongTile activeHintSecondTile;

        /// <summary>
        /// Gets a value indicating whether a blocking animation is currently running.
        /// </summary>
        public bool IsAnimationLocked { get; private set; }

        /// <summary>
        /// Gets the bootstrap order for the animation manager.
        /// </summary>
        public override int InitializationOrder => 90;

        /// <summary>
        /// Initializes the particle pool used for match feedback.
        /// </summary>
        protected override void OnInitialize()
        {
            if (trayCapacityWarningImage == null && traySlotAnchorProvider != null)
            {
                trayCapacityWarningImage = traySlotAnchorProvider.CapacityWarningImage;
            }

            if (animationSettings != null && animationSettings.MatchParticlePrefab != null)
            {
                particlePool = new ComponentPool<ParticleSystem>(animationSettings.MatchParticlePrefab);
            }

            if (animationSettings != null && animationSettings.UseMatchUiShards && matchUiShardEffect == null)
            {
                matchUiShardEffect = MatchUiShardEffect.Create(transform);
                matchUiShardEffect?.Prewarm();
            }

            EnsureTrayOverlay();
            if (trayCapacityWarningImage != null)
            {
                trayCapacityWarningImage.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// Clears pooled animation resources during shutdown.
        /// </summary>
        protected override void OnShutdown()
        {
            HideMatchingTraySlot();
            ClearHintHighlight();
            if (trayShakeRoutine != null)
            {
                StopCoroutine(trayShakeRoutine);
                trayShakeRoutine = null;
            }

            RestoreTrayShakeStates();

            if (trayWarningRoutine != null)
            {
                StopCoroutine(trayWarningRoutine);
                trayWarningRoutine = null;
            }

            if (trayCapacityWarningImage != null)
            {
                trayCapacityWarningImage.enabled = true;
                trayCapacityWarningImage.gameObject.SetActive(false);
            }

            particlePool?.Clear();
            particlePool = null;
            if (matchUiShardEffect != null)
            {
                matchUiShardEffect.Clear();
                Destroy(matchUiShardEffect.gameObject);
                matchUiShardEffect = null;
            }

            ClearTrayPreviews();
            if (trayOverlayRoot != null)
            {
                TraySlotAnchorProvider provider = ResolveTraySlotAnchorProvider();
                bool ownsTrayOverlay = provider == null || trayOverlayRoot != provider.PreviewRoot;
                if (ownsTrayOverlay)
                {
                    Destroy(trayOverlayRoot.gameObject);
                }

                trayOverlayRoot = null;
                trayOverlayCanvas = null;
            }
        }

        /// <summary>
        /// Plays a full match-removal sequence for two tiles.
        /// </summary>
        /// <param name="firstTile">First tile to animate.</param>
        /// <param name="secondTile">Second tile to animate.</param>
        /// <param name="onCompleted">Optional callback invoked when the animation completes.</param>
        /// <param name="onImpact">Optional callback invoked when the tiles collide and feedback spawns.</param>
        /// <returns>Running coroutine instance.</returns>
        public Coroutine PlayMatchSequence(MahjongTile firstTile, MahjongTile secondTile, Action onCompleted = null, Action onImpact = null)
        {
            return StartCoroutine(PlayMatchSequenceRoutine(firstTile, secondTile, onCompleted, onImpact));
        }

        /// <summary>
        /// Plays a short mismatch pause.
        /// </summary>
        /// <param name="onCompleted">Optional callback invoked when the delay completes.</param>
        /// <returns>Running coroutine instance.</returns>
        public Coroutine PlayMismatchDelay(Action onCompleted = null)
        {
            return StartCoroutine(PlayMismatchDelayRoutine(onCompleted));
        }

        /// <summary>
        /// Applies a hint highlight to the supplied pair and keeps it visible until another hint replaces it.
        /// </summary>
        /// <param name="firstTile">First hinted tile.</param>
        /// <param name="secondTile">Second hinted tile.</param>
        /// <returns>Running coroutine instance.</returns>
        public Coroutine PlayHintSequence(MahjongTile firstTile, MahjongTile secondTile)
        {
            return StartCoroutine(PlayHintSequenceRoutine(firstTile, secondTile));
        }

        /// <summary>
        /// Moves a tile into one of the temporary selection tray slots used for gameplay testing.
        /// </summary>
        public Coroutine PlayMoveToTray(MahjongTile tile, int slotIndex, Action onCompleted = null)
        {
            return StartCoroutine(PlayMoveToTrayRoutine(tile, slotIndex, onCompleted));
        }

        /// <summary>
        /// Animates a tile to one of the temporary selection tray slots.
        /// </summary>
        public bool SnapToTray(MahjongTile tile, int slotIndex)
        {
            return AnimateTrayTileToSlot(tile, slotIndex, null);
        }

        public void ShowMatchingTraySlot(int slotIndex)
        {
            ResolveTraySlotAnchorProvider()?.ShowMatchingSlotOutline(slotIndex);
        }

        public void HideMatchingTraySlot()
        {
            ResolveTraySlotAnchorProvider()?.StopMatchingSlotOutline();
        }

        /// <summary>
        /// Animates every existing tray preview to its current compact slot.
        /// </summary>
        public void AnimateTrayTilesToSlots(IList<MahjongTile> tiles, int startIndex = 0)
        {
            if (tiles == null)
            {
                return;
            }

            int clampedStartIndex = Mathf.Clamp(startIndex, 0, tiles.Count);
            for (int index = clampedStartIndex; index < tiles.Count; index++)
            {
                MahjongTile tile = tiles[index];
                if (tile != null)
                {
                    AnimateTrayTileToSlot(tile, index, null);
                }
            }
        }

        private bool AnimateTrayTileToSlot(MahjongTile tile, int slotIndex, Action onCompleted)
        {
            if (tile == null || !trayPreviewsByTile.TryGetValue(tile, out RuntimeTrayPreview preview) || preview?.RectTransform == null)
            {
                onCompleted?.Invoke();
                return false;
            }

            Vector2 fallbackSize = preview.RectTransform.sizeDelta;
            preview.TargetSlotIndex = slotIndex;
            preview.TargetPosition = ResolveTrayAnchoredPosition(slotIndex);
            preview.TargetSize = ResolveTraySlotSize(slotIndex, fallbackSize);
            if (preview.IsEnteringTray)
            {
                return true;
            }

            if (preview.AnimationRoutine != null)
            {
                StopCoroutine(preview.AnimationRoutine);
            }

            preview.AnimationRoutine = StartCoroutine(AnimateTrayTileToSlotRoutine(preview, onCompleted));
            return true;
        }

        private IEnumerator AnimateTrayTileToSlotRoutine(RuntimeTrayPreview preview, Action onCompleted)
        {
            if (preview?.RectTransform == null)
            {
                onCompleted?.Invoke();
                yield break;
            }

            RectTransform rect = preview.RectTransform;
            Vector2 startPosition = rect.anchoredPosition;
            Vector2 startSize = rect.sizeDelta;
            Vector2 targetPosition = preview.TargetPosition;
            Vector2 targetSize = preview.TargetSize.sqrMagnitude > Mathf.Epsilon ? preview.TargetSize : startSize;
            float duration = GetTrayMoveDurationSeconds() * 0.75f;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += GetDeltaTime();
                float t = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, duration));
                float easedT = 1f - Mathf.Pow(1f - t, 3f);
                rect.anchoredPosition = Vector2.LerpUnclamped(startPosition, targetPosition, easedT);
                rect.sizeDelta = Vector2.LerpUnclamped(startSize, targetSize, easedT);
                yield return null;
            }

            rect.anchoredPosition = targetPosition;
            rect.sizeDelta = targetSize;
            rect.localScale = Vector3.one;
            preview.AnimationRoutine = null;
            onCompleted?.Invoke();
        }

        /// <summary>
        /// Plays tray feedback after a tile reaches its slot.
        /// </summary>
        public void PlayTrayOccupancyFeedback(int tileCount, bool shouldShake)
        {
            if (trayCapacityWarningImage == null)
            {
                TraySlotAnchorProvider provider = ResolveTraySlotAnchorProvider();
                if (provider != null)
                {
                    trayCapacityWarningImage = provider.CapacityWarningImage;
                }
            }

            if (shouldShake && tileCount > 1)
            {
                StopTrayShake();
                trayShakeRoutine = StartCoroutine(ShakeTrayPreviewsRoutine());
            }
            else
            {
                StopTrayShake();
            }

            if (tileCount == 3 && trayCapacityWarningImage != null)
            {
                if (trayWarningRoutine != null)
                {
                    StopCoroutine(trayWarningRoutine);
                }

                trayWarningRoutine = StartCoroutine(BlinkTrayWarningRoutine());
            }
        }

        public void StopTrayShake()
        {
            if (trayShakeRoutine != null)
            {
                StopCoroutine(trayShakeRoutine);
                trayShakeRoutine = null;
            }

            RestoreTrayShakeStates();
        }

        public void HideTrayCapacityWarning()
        {
            if (trayWarningRoutine != null)
            {
                StopCoroutine(trayWarningRoutine);
                trayWarningRoutine = null;
            }

            if (trayCapacityWarningImage != null)
            {
                trayCapacityWarningImage.enabled = true;
                trayCapacityWarningImage.gameObject.SetActive(false);
            }
        }

        private IEnumerator ShakeTrayPreviewsRoutine()
        {
            activeTrayShakeStates.Clear();
            foreach (RuntimeTrayPreview preview in trayPreviewsByTile.Values)
            {
                if (preview?.RectTransform != null)
                {
                    activeTrayShakeStates[preview] = new TrayShakeState
                    {
                        Position = preview.RectTransform.anchoredPosition,
                        Rotation = preview.RectTransform.localRotation,
                        Scale = preview.RectTransform.localScale,
                    };
                }
            }

            float duration = Mathf.Max(0.05f, trayShakeDurationSeconds);
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += GetDeltaTime();
                float progress = Mathf.Clamp01(elapsed / duration);
                float strength = Mathf.Sin(progress * Mathf.PI);
                int previewIndex = 0;
                foreach (KeyValuePair<RuntimeTrayPreview, TrayShakeState> entry in activeTrayShakeStates)
                {
                    if (entry.Key?.RectTransform != null && entry.Key.AnimationRoutine == null)
                    {
                        float phase = previewIndex * 0.42f;
                        float sway = Mathf.Sin((elapsed * 10f) + phase);
                        float secondarySway = Mathf.Sin((elapsed * 15f) + phase);
                        float offsetX = sway * trayShakeAmplitude * strength;
                        float offsetY = secondarySway * trayShakeVerticalAmplitude * strength;
                        float angle = sway * trayShakeRotationDegrees * strength;

                        entry.Key.RectTransform.anchoredPosition = entry.Value.Position + new Vector2(offsetX, offsetY);
                        entry.Key.RectTransform.localRotation = entry.Value.Rotation * Quaternion.Euler(0f, 0f, angle);
                        entry.Key.RectTransform.localScale = entry.Value.Scale;
                    }

                    previewIndex++;
                }

                yield return null;
            }

            RestoreTrayShakeStates();
            trayShakeRoutine = null;
        }

        private void RestoreTrayShakeStates()
        {
            foreach (KeyValuePair<RuntimeTrayPreview, TrayShakeState> entry in activeTrayShakeStates)
            {
                if (entry.Key?.RectTransform != null && entry.Key.AnimationRoutine == null)
                {
                    entry.Key.RectTransform.anchoredPosition = entry.Value.Position;
                    entry.Key.RectTransform.localRotation = entry.Value.Rotation;
                    entry.Key.RectTransform.localScale = entry.Value.Scale;
                }
            }

            activeTrayShakeStates.Clear();
        }

        private IEnumerator BlinkTrayWarningRoutine()
        {
            trayCapacityWarningImage.gameObject.SetActive(true);
            float duration = Mathf.Max(0.05f, trayWarningDurationSeconds);
            float interval = Mathf.Max(0.01f, trayWarningBlinkIntervalSeconds);
            float elapsed = 0f;

            while (elapsed < duration)
            {
                trayCapacityWarningImage.enabled = !trayCapacityWarningImage.enabled;
                float waitElapsed = 0f;
                while (waitElapsed < interval)
                {
                    float deltaTime = GetDeltaTime();
                    waitElapsed += deltaTime;
                    elapsed += deltaTime;
                    yield return null;
                }
            }

            trayCapacityWarningImage.enabled = true;
            trayCapacityWarningImage.gameObject.SetActive(false);
            trayWarningRoutine = null;
        }

        /// <summary>
        /// Removes the UI tray preview associated with a tile.
        /// </summary>
        public void ClearTrayTile(MahjongTile tile)
        {
            if (tile == null || !trayPreviewsByTile.TryGetValue(tile, out RuntimeTrayPreview preview))
            {
                return;
            }

            FinalizeTrayPreview(preview);
            DestroyTrayPreview(preview);
            trayPreviewsByTile.Remove(tile);
        }

        /// <summary>
        /// Clears every UI tray preview.
        /// </summary>
        public void ClearTrayPreviews()
        {
            foreach (RuntimeTrayPreview preview in trayPreviewsByTile.Values)
            {
                FinalizeTrayPreview(preview);
                DestroyTrayPreview(preview);
            }

            trayPreviewsByTile.Clear();
        }

        /// <summary>
        /// Shows or hides the original 3D tile renderers while an overlay preview represents the tile.
        /// </summary>
        public void SetTrayBoardTileVisible(MahjongTile tile, bool isVisible)
        {
            SetTileRenderersVisible(tile, isVisible);
        }

        /// <summary>
        /// Updates the current animation lock state.
        /// </summary>
        /// <param name="isLocked">New animation lock state.</param>
        public void SetAnimationLock(bool isLocked)
        {
            if (isLocked)
            {
                animationLockCount++;
            }
            else
            {
                animationLockCount = Mathf.Max(0, animationLockCount - 1);
            }

            IsAnimationLocked = animationLockCount > 0;
        }

        /// <summary>
        /// Cancels transient gameplay animations so pooled tiles can be reused safely during level reloads.
        /// </summary>
        public void CancelTransientAnimations()
        {
            StopAllCoroutines();
            HideMatchingTraySlot();
            ClearHintHighlight();
            ClearTrayPreviews();
            animationLockCount = 0;
            IsAnimationLocked = false;
        }

        /// <summary>
        /// Executes the full match animation and camera shake sequence.
        /// </summary>
        private IEnumerator PlayMatchSequenceRoutine(MahjongTile firstTile, MahjongTile secondTile, Action onCompleted, Action onImpact)
        {
            if (firstTile == null || secondTile == null)
            {
                yield break;
            }

            SetAnimationLock(true);
            bool completed = false;
            try
            {
                Camera activeCamera = null;
                if (Context.Services.TryGet(out CameraManager cameraManager))
                {
                    activeCamera = cameraManager.ActiveCamera;
                }

                if (!trayPreviewsByTile.TryGetValue(firstTile, out RuntimeTrayPreview firstPreview) && firstTile != null)
                {
                    TryCreateTrayPreview(firstTile, activeCamera, -1, out firstPreview);
                }

                if (!trayPreviewsByTile.TryGetValue(secondTile, out RuntimeTrayPreview secondPreview) && secondTile != null)
                {
                    TryCreateTrayPreview(secondTile, activeCamera, -1, out secondPreview);
                }

                if (firstPreview != null || secondPreview != null)
                {
                    yield return PlayTrayPreviewMatchSequenceRoutine(firstTile, secondTile, firstPreview, secondPreview, onImpact);
                    PlayCameraShake();
                    completed = true;
                    yield break;
                }

                Vector3 firstStart = firstTile.transform.position;
                Vector3 secondStart = secondTile.transform.position;
                Quaternion firstRotationStart = firstTile.transform.rotation;
                Quaternion secondRotationStart = secondTile.transform.rotation;

                Vector3 cameraRight = GetCameraRight();
                Vector3 cameraForward = GetCameraForward();
                if (cameraForward.sqrMagnitude <= Mathf.Epsilon)
                {
                    cameraForward = Vector3.forward;
                }

                cameraForward.Normalize();

                Quaternion uprightCardRotation = GetTrayFacingRotation(cameraForward, Vector3.up);

                float slideDistance = GetMatchSlideDistance();
                float contactOffset = GetMatchContactCenterOffset(firstTile, secondTile, uprightCardRotation, cameraRight);
                float visibleImpactOffset = contactOffset + (GetMatchDisappearGap() * 0.5f);
                float stageOffset = Mathf.Max(visibleImpactOffset + 0.18f, slideDistance * 0.45f);

                Vector3 impactWorld = GetMatchCenterWorldPosition(firstTile, secondTile);
                Vector3 firstStageWorld = impactWorld - (cameraRight * stageOffset);
                Vector3 secondStageWorld = impactWorld + (cameraRight * stageOffset);

                Quaternion firstStageRotation = uprightCardRotation;
                Quaternion secondStageRotation = uprightCardRotation;

                Vector3 firstImpactWorld = impactWorld - (cameraRight * visibleImpactOffset);
                Vector3 secondImpactWorld = impactWorld + (cameraRight * visibleImpactOffset);
                Quaternion firstImpactRotation = uprightCardRotation;
                Quaternion secondImpactRotation = uprightCardRotation;

                float duration = GetMatchDurationSeconds();
                float approachDuration = Mathf.Max(0.05f, duration * 0.38f);
                float holdDuration = Mathf.Max(0.08f, duration * 0.2f);
                float leanBackDuration = Mathf.Max(0.07f, duration * 0.18f);
                float impactDuration = Mathf.Max(0.05f, duration - (approachDuration + holdDuration + leanBackDuration));

                float firstTileTiltDirection = firstTile.transform.position.x <= secondTile.transform.position.x ? 1f : -1f;
                float secondTileTiltDirection = -firstTileTiltDirection;

                float elapsed = 0f;
                while (elapsed < approachDuration)
                {
                    elapsed += GetDeltaTime();
                    float t = Mathf.Clamp01(elapsed / approachDuration);
                    float easedT = 1f - Mathf.Pow(1f - t, 3f);
                    Quaternion firstTravelRotation = Quaternion.SlerpUnclamped(firstRotationStart, firstStageRotation, easedT);
                    Quaternion secondTravelRotation = Quaternion.SlerpUnclamped(secondRotationStart, secondStageRotation, easedT);

                    firstTile.transform.SetPositionAndRotation(
                        Vector3.LerpUnclamped(firstStart, firstStageWorld, easedT),
                        firstTravelRotation);
                    secondTile.transform.SetPositionAndRotation(
                        Vector3.LerpUnclamped(secondStart, secondStageWorld, easedT),
                        secondTravelRotation);
                    yield return null;
                }

                firstTile.transform.SetPositionAndRotation(firstStageWorld, firstStageRotation);
                secondTile.transform.SetPositionAndRotation(secondStageWorld, secondStageRotation);

                elapsed = 0f;
                while (elapsed < holdDuration)
                {
                    elapsed += GetDeltaTime();
                    firstTile.transform.SetPositionAndRotation(firstStageWorld, firstStageRotation);
                    secondTile.transform.SetPositionAndRotation(secondStageWorld, secondStageRotation);
                    yield return null;
                }

                elapsed = 0f;
                while (elapsed < leanBackDuration)
                {
                    elapsed += GetDeltaTime();
                    float t = Mathf.Clamp01(elapsed / leanBackDuration);
                    float easedT = 1f - Mathf.Pow(1f - t, 2f);
                    float tiltAngle = Mathf.Lerp(0f, matchTileMaxTiltAngle * 0.9f, easedT);
                    Quaternion firstLeanRotation = firstStageRotation * Quaternion.Euler(0f, 0f, firstTileTiltDirection * tiltAngle);
                    Quaternion secondLeanRotation = secondStageRotation * Quaternion.Euler(0f, 0f, secondTileTiltDirection * tiltAngle);

                    firstTile.transform.SetPositionAndRotation(firstStageWorld, firstLeanRotation);
                    secondTile.transform.SetPositionAndRotation(secondStageWorld, secondLeanRotation);
                    yield return null;
                }

                elapsed = 0f;
                while (elapsed < impactDuration)
                {
                    elapsed += GetDeltaTime();
                    float t = Mathf.Clamp01(elapsed / impactDuration);
                    float easedT = 1f - Mathf.Pow(1f - t, 4f);
                    float tiltAngle = Mathf.Lerp(matchTileMaxTiltAngle * 0.9f, 0f, easedT);
                    Quaternion firstImpactTiltRotation = Quaternion.SlerpUnclamped(firstStageRotation, firstImpactRotation, easedT) * Quaternion.Euler(0f, 0f, firstTileTiltDirection * tiltAngle);
                    Quaternion secondImpactTiltRotation = Quaternion.SlerpUnclamped(secondStageRotation, secondImpactRotation, easedT) * Quaternion.Euler(0f, 0f, secondTileTiltDirection * tiltAngle);

                    firstTile.transform.SetPositionAndRotation(
                        Vector3.LerpUnclamped(firstStageWorld, firstImpactWorld, easedT),
                        firstImpactTiltRotation);
                    secondTile.transform.SetPositionAndRotation(
                        Vector3.LerpUnclamped(secondStageWorld, secondImpactWorld, easedT),
                        secondImpactTiltRotation);
                    yield return null;
                }

                firstTile.transform.SetPositionAndRotation(firstImpactWorld, firstImpactRotation);
                secondTile.transform.SetPositionAndRotation(secondImpactWorld, secondImpactRotation);

                PlayMatchFeedback(firstTile, secondTile, impactWorld);
                onImpact?.Invoke();
                firstTile.SetVisible(false);
                secondTile.SetVisible(false);
                PlayCameraShake();
                completed = true;
            }
            finally
            {
                SetAnimationLock(false);
                if (completed)
                {
                    onCompleted?.Invoke();
                }
            }
        }

        /// <summary>
        /// Waits a short amount of time before invoking the mismatch callback.
        /// </summary>
        private IEnumerator PlayMismatchDelayRoutine(Action onCompleted)
        {
            SetAnimationLock(true);
            bool completed = false;
            try
            {
                float duration = GetMismatchDelaySeconds();
                float elapsed = 0f;

                while (elapsed < duration)
                {
                    elapsed += GetDeltaTime();
                    yield return null;
                }

                completed = true;
            }
            finally
            {
                SetAnimationLock(false);
                if (completed)
                {
                    onCompleted?.Invoke();
                }
            }
        }

        /// <summary>
        /// Applies the current hint highlight and replaces any previous hinted pair.
        /// </summary>
        private IEnumerator PlayHintSequenceRoutine(MahjongTile firstTile, MahjongTile secondTile)
        {
            if (firstTile == null || secondTile == null)
            {
                yield break;
            }

            if (activeHintFirstTile == firstTile && activeHintSecondTile == secondTile)
            {
                firstTile.SetHintHighlighted(true);
                secondTile.SetHintHighlighted(true);
                yield break;
            }

            ClearHintHighlight();

            activeHintFirstTile = firstTile;
            activeHintSecondTile = secondTile;
            firstTile.SetHintHighlighted(true);
            secondTile.SetHintHighlighted(true);
        }

        /// <summary>
        /// Clears the currently active hint highlight pair.
        /// </summary>
        private void ClearHintHighlight()
        {
            if (activeHintFirstTile != null)
            {
                activeHintFirstTile.SetHintHighlighted(false);
            }

            if (activeHintSecondTile != null && activeHintSecondTile != activeHintFirstTile)
            {
                activeHintSecondTile.SetHintHighlighted(false);
            }

            activeHintFirstTile = null;
            activeHintSecondTile = null;
        }

        /// <summary>
        /// Animates a tile from the board into a temporary tray slot near the top of the camera view.
        /// </summary>
        private IEnumerator PlayMoveToTrayRoutine(MahjongTile tile, int slotIndex, Action onCompleted)
        {
            if (tile == null)
            {
                onCompleted?.Invoke();
                yield break;
            }

            SetAnimationLock(true);

            Camera activeCamera = null;
            if (Context.Services.TryGet(out CameraManager cameraManager))
            {
                activeCamera = cameraManager.ActiveCamera;
            }

            bool startedUiMove = TryCreateTrayPreview(tile, activeCamera, slotIndex, out RuntimeTrayPreview preview);
            SetTileRenderersVisible(tile, false);
            if (!startedUiMove)
            {
                onCompleted?.Invoke();
                SetAnimationLock(false);
                yield break;
            }

            Vector2 startSize = preview.RectTransform.sizeDelta;
            preview.TargetSlotIndex = slotIndex;
            preview.TargetPosition = ResolveTrayAnchoredPosition(slotIndex);
            preview.TargetSize = ResolveTraySlotSize(slotIndex, startSize);
            preview.IsEnteringTray = true;
            preview.AnimationRoutine = StartCoroutine(TrackTrayMoveRoutine(preview, onCompleted));
        }

        private IEnumerator TrackTrayMoveRoutine(RuntimeTrayPreview preview, Action onCompleted)
        {
            if (preview?.RectTransform == null)
            {
                SetAnimationLock(false);
                onCompleted?.Invoke();
                yield break;
            }

            Vector2 startPosition = preview.RectTransform.anchoredPosition;
            Vector2 startSize = preview.RectTransform.sizeDelta;
            Vector2 targetPosition = preview.TargetPosition;
            Vector2 targetSize = preview.TargetSize;
            float duration = GetTrayMoveDurationSeconds();
            float elapsed = 0f;
            bool completed = false;

            try
            {
                while (elapsed < duration)
                {
                    elapsed += GetDeltaTime();
                    float t = Mathf.Clamp01(elapsed / duration);
                    float easedT = 1f - Mathf.Pow(1f - t, 3f);
                    targetPosition = preview.TargetPosition;
                    targetSize = preview.TargetSize.sqrMagnitude > Mathf.Epsilon ? preview.TargetSize : targetSize;
                    preview.RectTransform.anchoredPosition = Vector2.LerpUnclamped(startPosition, targetPosition, easedT);
                    preview.RectTransform.sizeDelta = Vector2.LerpUnclamped(startSize, targetSize, easedT);
                    yield return null;
                }

                preview.RectTransform.anchoredPosition = preview.TargetPosition;
                preview.RectTransform.sizeDelta = preview.TargetSize.sqrMagnitude > Mathf.Epsilon ? preview.TargetSize : targetSize;
                completed = true;
            }
            finally
            {
                preview.AnimationRoutine = null;
                preview.IsEnteringTray = false;
                SetAnimationLock(false);
                if (completed)
                {
                    onCompleted?.Invoke();
                }
            }
        }

        /// <summary>
        /// Immediately finalizes an active tray preview at its latest target.
        /// </summary>
        public bool SnapTrayPreviewToSlot(MahjongTile tile, int slotIndex)
        {
            if (tile == null || !trayPreviewsByTile.TryGetValue(tile, out RuntimeTrayPreview preview) || preview?.RectTransform == null)
            {
                return false;
            }

            preview.TargetSlotIndex = slotIndex;
            preview.TargetPosition = ResolveTrayAnchoredPosition(slotIndex);
            preview.TargetSize = ResolveTraySlotSize(slotIndex, preview.RectTransform.sizeDelta);
            if (preview.AnimationRoutine != null)
            {
                StopCoroutine(preview.AnimationRoutine);
                preview.AnimationRoutine = null;
                preview.IsEnteringTray = false;
                SetAnimationLock(false);
            }

            preview.RectTransform.anchoredPosition = preview.TargetPosition;
            preview.RectTransform.sizeDelta = preview.TargetSize;
            preview.RectTransform.localScale = Vector3.one;
            return true;
        }

        private void FinalizeTrayPreview(RuntimeTrayPreview preview)
        {
            if (preview == null)
            {
                return;
            }

            if (preview.AnimationRoutine != null)
            {
                StopCoroutine(preview.AnimationRoutine);
                preview.AnimationRoutine = null;
                if (preview.IsEnteringTray)
                {
                    SetAnimationLock(false);
                }
            }

            preview.IsEnteringTray = false;
        }

        private bool TryCreateTrayPreview(MahjongTile tile, Camera activeCamera, int slotIndex, out RuntimeTrayPreview preview)
        {
            preview = null;
            if (tile == null || activeCamera == null || EnsureTrayOverlay() == null)
            {
                return false;
            }

            ItemPreviewGenerator previewGenerator = ItemPreviewGenerator.Resolve();
            if (previewGenerator == null)
            {
                return false;
            }

            Vector3 originalPosition = tile.transform.position;
            Quaternion originalRotation = tile.transform.rotation;
            Transform originalParent = tile.transform.parent;
            Vector3 originalPieceScale = tile.PieceLocalScale;
            bool originalWorldPositionStays = true;
            Texture2D texture = null;
            try
            {
                if (TryGetTraySlotPose(tile, slotIndex, out Vector3 capturePosition, out Quaternion captureRotation))
                {
                    tile.transform.SetParent(GetTrayParentTransform(), true);
                    tile.transform.SetPositionAndRotation(capturePosition, captureRotation);
                }

                tile.RestorePieceLocalScale();
                texture = previewGenerator.CaptureExisting(tile.gameObject, activeCamera, true);
            }
            finally
            {
                tile.SetPieceLocalScale(originalPieceScale);
                tile.transform.SetParent(originalParent, originalWorldPositionStays);
                tile.transform.SetPositionAndRotation(originalPosition, originalRotation);
            }

            if (texture == null)
            {
                return false;
            }

            ClearTrayTile(tile);

            GameObject previewObject = new GameObject($"Tray Preview {tile.TileId}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(CanvasGroup), typeof(InventoryItemPreview));
            previewObject.transform.SetParent(trayOverlayRoot, false);

            RectTransform rectTransform = previewObject.transform as RectTransform;
            if (rectTransform == null)
            {
                Destroy(previewObject);
                Destroy(texture);
                return false;
            }

            rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            rectTransform.pivot = new Vector2(0.5f, 0.5f);
            rectTransform.localScale = Vector3.one;
            rectTransform.localRotation = Quaternion.identity;

            Image image = previewObject.GetComponent<Image>();
            image.raycastTarget = false;
            image.preserveAspect = true;

            CanvasGroup canvasGroup = previewObject.GetComponent<CanvasGroup>();
            canvasGroup.alpha = 1f;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;

            InventoryItemPreview itemPreview = previewObject.GetComponent<InventoryItemPreview>();
            itemPreview.SetPreview(texture, image);

            Vector2 startScreenPosition = activeCamera.WorldToScreenPoint(tile.transform.position);
            rectTransform.anchoredPosition = ScreenToTrayOverlayPosition(startScreenPosition);
            rectTransform.sizeDelta = EstimateTrayPreviewStartSize(texture, tile, activeCamera);

            preview = new RuntimeTrayPreview
            {
                GameObject = previewObject,
                RectTransform = rectTransform,
                CanvasGroup = canvasGroup,
                ItemPreview = itemPreview,
            };
            trayPreviewsByTile[tile] = preview;
            return true;
        }

        /// <summary>
        /// Resolves a temporary tray slot pose relative to the active gameplay camera.
        /// </summary>
        private bool TryGetTraySlotPose(MahjongTile tile, int slotIndex, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            if (!Context.Services.TryGet(out CameraManager cameraManager) || cameraManager.ActiveCamera == null)
            {
                return false;
            }

            Camera activeCamera = cameraManager.ActiveCamera;
            float z = GetTrayTargetDistance(activeCamera, tile);
            rotation = GetTrayFacingRotation(activeCamera.transform.forward, activeCamera.transform.up);

            if (TryGetTraySlotScreenPoint(slotIndex, out Vector2 trayScreenPoint))
            {
                position = activeCamera.ScreenToWorldPoint(new Vector3(trayScreenPoint.x, trayScreenPoint.y, z));
                return true;
            }

            float x = 0.5f + ((Mathf.Clamp(slotIndex, 0, 3) - 1.5f) * GetTrayViewportSlotSpacing());
            float y = GetTrayViewportY();
            position = activeCamera.ViewportToWorldPoint(new Vector3(x, y, z));
            return true;
        }

        private bool TryGetTraySlotScreenPoint(int slotIndex, out Vector2 screenPoint)
        {
            screenPoint = default;

            TraySlotAnchorProvider provider = ResolveTraySlotAnchorProvider();
            if (provider == null)
            {
                return false;
            }

            return provider.TryGetTraySlotScreenPoint(slotIndex, out screenPoint);
        }

        private TraySlotAnchorProvider ResolveTraySlotAnchorProvider()
        {
            if (traySlotAnchorProvider != null)
            {
                return traySlotAnchorProvider;
            }

            if (Context.Services.TryGet(out UIManager uiManager) && uiManager.TraySlotAnchorProvider != null)
            {
                traySlotAnchorProvider = uiManager.TraySlotAnchorProvider;
                return traySlotAnchorProvider;
            }

            traySlotAnchorProvider = FindAnyObjectByType<TraySlotAnchorProvider>(FindObjectsInactive.Include);
            return traySlotAnchorProvider;
        }

        private Transform GetTrayParentTransform()
        {
            if (Context.Services.TryGet(out CameraManager cameraManager) && cameraManager.ActiveCamera != null)
            {
                return cameraManager.ActiveCamera.transform;
            }

            return null;
        }

        /// <summary>
        /// Resolves the shared world-space center used by the match animation.
        /// </summary>
        private Vector3 GetMatchCenterWorldPosition(MahjongTile firstTile, MahjongTile secondTile)
        {
            if (!Context.Services.TryGet(out CameraManager cameraManager) || cameraManager.ActiveCamera == null)
            {
                return Vector3.Lerp(firstTile.transform.position, secondTile.transform.position, 0.5f);
            }

            Camera activeCamera = cameraManager.ActiveCamera;
            float viewportX = 0.5f;
            float viewportY = Mathf.Clamp01(GetTrayViewportY() - GetMatchViewportYOffset());
            float firstDepth = GetTrayTargetDistance(activeCamera, firstTile);
            float secondDepth = GetTrayTargetDistance(activeCamera, secondTile);
            float trayDepth = Mathf.Max(firstDepth, secondDepth);
            float targetDepth = Mathf.Max(0.5f, trayDepth - GetMatchDepthOffset());
            return activeCamera.ViewportToWorldPoint(new Vector3(viewportX, viewportY, targetDepth));
        }

        /// <summary>
        /// Keeps tray tiles from being pulled too close to the camera so their perceived size stays stable.
        /// </summary>
        private float GetTrayTargetDistance(Camera activeCamera, MahjongTile tile)
        {
            return Mathf.Max(0.5f, GetTrayDistanceFromCamera() + GetTrayDistancePadding());
        }

        /// <summary>
        /// Resolves the upright-facing rotation used by tray and match-flight presentation.
        /// </summary>
        private static Quaternion GetTrayFacingRotation(Vector3 cameraForward, Vector3 cameraUp)
        {
            Vector3 resolvedForward = cameraForward.sqrMagnitude > Mathf.Epsilon ? cameraForward.normalized : Vector3.forward;
            Vector3 resolvedUp = cameraUp.sqrMagnitude > Mathf.Epsilon ? cameraUp.normalized : Vector3.up;

            return Quaternion.AngleAxis(180f, -resolvedForward)
                * Quaternion.AngleAxis(90f, -resolvedForward)
                * Quaternion.LookRotation(resolvedUp, -resolvedForward);
        }

        /// <summary>
        /// Gets the local delta time chosen by the animation settings.
        /// </summary>
        private float GetDeltaTime()
        {
            if (animationSettings != null && animationSettings.UseUnscaledTime)
            {
                return Time.unscaledDeltaTime;
            }

            return Time.deltaTime;
        }

        /// <summary>
        /// Spawns a match particle effect at the specified position.
        /// </summary>
        private void PlayMatchParticle(Vector3 worldPosition)
        {
            if (animationSettings == null || animationSettings.MatchParticlePrefab == null)
            {
                return;
            }

            ParticleSystem particle = particlePool != null ? particlePool.Get() : Instantiate(animationSettings.MatchParticlePrefab);
            particle.transform.SetPositionAndRotation(worldPosition, Quaternion.identity);
            particle.Play();
            StartCoroutine(ReleaseParticleAfter(particle, particle.main.duration + particle.main.startLifetime.constantMax + 0.25f));
        }

        /// <summary>
        /// Returns a pooled particle to the pool after it finishes playing.
        /// </summary>
        private IEnumerator ReleaseParticleAfter(ParticleSystem particle, float delaySeconds)
        {
            float elapsed = 0f;
            while (elapsed < delaySeconds)
            {
                elapsed += GetDeltaTime();
                yield return null;
            }

            if (particlePool != null)
            {
                particlePool.Release(particle);
            }
            else if (particle != null)
            {
                Destroy(particle.gameObject);
            }
        }

        /// <summary>
        /// Plays the configured UI shard burst or falls back to the particle effect.
        /// </summary>
        private void PlayMatchFeedback(MahjongTile firstTile, MahjongTile secondTile, Vector3 worldPosition)
        {
            if (TryPlayMatchUiShardEffect(firstTile, secondTile))
            {
                return;
            }

            PlayMatchParticle(worldPosition);
        }

        /// <summary>
        /// Spawns the screen-space UI shard burst when enabled.
        /// </summary>
        private bool TryPlayMatchUiShardEffect(MahjongTile firstTile, MahjongTile secondTile)
        {
            if (animationSettings == null || !animationSettings.UseMatchUiShards)
            {
                return false;
            }

            Camera worldCamera = null;
            if (Context.Services.TryGet(out CameraManager cameraManager))
            {
                worldCamera = cameraManager.ActiveCamera;
            }

            if (worldCamera == null)
            {
                return false;
            }

            if (matchUiShardEffect == null)
            {
                matchUiShardEffect = MatchUiShardEffect.Create(transform);
            }

            matchUiShardEffect?.Prewarm();

            return matchUiShardEffect != null && matchUiShardEffect.Play(firstTile, secondTile, worldCamera, animationSettings);
        }

        /// <summary>
        /// Requests a short shake on the active orbit camera.
        /// </summary>
        private void PlayCameraShake()
        {
            if (IGameSettingController.Instance != null && !IGameSettingController.Instance.GetSetting(EGameSetting.Vibration))
            {
                return;
            }

            if (!Context.Services.TryGet(out CameraManager cameraManager))
            {
                return;
            }

            OrbitCameraController orbit = cameraManager.OrbitCameraController;
            if (orbit != null)
            {
                orbit.PlayShake(GetShakeDurationSeconds(), GetShakeAmplitude());
            }
        }

        /// <summary>
        /// Gets the horizontal right direction of the active gameplay camera.
        /// </summary>
        /// <returns>Normalized world-space right vector.</returns>
        private Vector3 GetCameraRight()
        {
            if (Context.Services.TryGet(out CameraManager cameraManager) && cameraManager.ActiveCamera != null)
            {
                Vector3 flattenedRight = Vector3.ProjectOnPlane(cameraManager.ActiveCamera.transform.right, Vector3.up);
                if (flattenedRight.sqrMagnitude > 0.0001f)
                {
                    return flattenedRight.normalized;
                }
            }

            return Vector3.right;
        }

        /// <summary>
        /// Gets the forward view direction of the active gameplay camera projected onto the horizontal plane.
        /// </summary>
        /// <returns>Normalized world-space forward vector.</returns>
        private Vector3 GetCameraForward()
        {
            if (Context.Services.TryGet(out CameraManager cameraManager) && cameraManager.ActiveCamera != null)
            {
                Vector3 flattenedForward = Vector3.ProjectOnPlane(cameraManager.ActiveCamera.transform.forward, Vector3.up);
                if (flattenedForward.sqrMagnitude > 0.0001f)
                {
                    return flattenedForward.normalized;
                }
            }

            return Vector3.forward;
        }

        /// <summary>
        /// Resolves the center offset where the two tile faces visually meet edge-to-edge along the match axis.
        /// </summary>
        private float GetMatchContactCenterOffset(MahjongTile firstTile, MahjongTile secondTile, Quaternion targetRotation, Vector3 matchAxis)
        {
            Vector3 axis = matchAxis.sqrMagnitude > Mathf.Epsilon ? matchAxis.normalized : Vector3.right;
            float firstHalfExtent = GetProjectedHalfExtent(firstTile, targetRotation, axis);
            float secondHalfExtent = GetProjectedHalfExtent(secondTile, targetRotation, axis);
            float fallbackOffset = Mathf.Max(0.025f, GetMatchSlideDistance() * 0.12f);

            if (firstHalfExtent <= Mathf.Epsilon && secondHalfExtent <= Mathf.Epsilon)
            {
                return fallbackOffset;
            }

            return Mathf.Max(fallbackOffset, (firstHalfExtent + secondHalfExtent) * 0.5f);
        }

        private RectTransform EnsureTrayOverlay()
        {
            if (trayOverlayRoot != null)
            {
                return trayOverlayRoot;
            }

            TraySlotAnchorProvider provider = ResolveTraySlotAnchorProvider();
            if (provider != null && provider.PreviewRoot != null)
            {
                trayOverlayRoot = provider.PreviewRoot;
                trayOverlayCanvas = trayOverlayRoot.GetComponentInParent<Canvas>();
                return trayOverlayRoot;
            }

            GameObject overlayObject = new GameObject("Tile Tray Overlay", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            if (transform.root != null)
            {
                overlayObject.transform.SetParent(transform.root, false);
            }

            trayOverlayCanvas = overlayObject.GetComponent<Canvas>();
            trayOverlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            trayOverlayCanvas.sortingOrder = TrayOverlaySortingOrder;
            trayOverlayCanvas.pixelPerfect = false;

            CanvasScaler scaler = overlayObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            trayOverlayRoot = overlayObject.transform as RectTransform;
            trayOverlayRoot.anchorMin = Vector2.zero;
            trayOverlayRoot.anchorMax = Vector2.one;
            trayOverlayRoot.pivot = new Vector2(0.5f, 0.5f);
            trayOverlayRoot.offsetMin = Vector2.zero;
            trayOverlayRoot.offsetMax = Vector2.zero;
            trayOverlayRoot.localScale = Vector3.one;
            trayOverlayRoot.localRotation = Quaternion.identity;
            trayOverlayRoot.anchoredPosition = Vector2.zero;
            RefreshTrayOverlayLayout();
            return trayOverlayRoot;
        }

        private void RefreshTrayOverlayLayout()
        {
            if (trayOverlayRoot == null)
            {
                return;
            }

            if (trayOverlayCanvas != null && trayOverlayCanvas.transform == trayOverlayRoot.transform)
            {
                trayOverlayRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, Screen.width);
                trayOverlayRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Screen.height);
            }

            Canvas.ForceUpdateCanvases();
        }

        private Vector2 ScreenToTrayOverlayPosition(Vector2 screenPoint)
        {
            if (EnsureTrayOverlay() == null)
            {
                return screenPoint;
            }

            Camera canvasCamera = trayOverlayCanvas != null && trayOverlayCanvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? trayOverlayCanvas.worldCamera
                : null;
            return RectTransformUtility.ScreenPointToLocalPointInRectangle(
                trayOverlayRoot,
                screenPoint,
                canvasCamera,
                out Vector2 anchoredPosition)
                ? anchoredPosition
                : screenPoint;
        }

        private Vector2 ResolveTrayAnchoredPosition(int slotIndex)
        {
            RefreshTrayOverlayLayout();
            if (TryGetTraySlotScreenPoint(slotIndex, out Vector2 screenPoint))
            {
                return ScreenToTrayOverlayPosition(screenPoint);
            }

            int clampedSlot = Mathf.Clamp(slotIndex, 0, 3);
            return ScreenToTrayOverlayPosition(new Vector2(
                Screen.width * (0.5f + ((clampedSlot - 1.5f) * GetTrayViewportSlotSpacing())),
                Screen.height * GetTrayViewportY()));
        }

        private Vector2 ResolveTraySlotSize(int slotIndex, Vector2 fallbackSize)
        {
            TraySlotAnchorProvider provider = ResolveTraySlotAnchorProvider();
            if (provider != null && provider.TryGetTraySlotAnchor(slotIndex, out RectTransform slotAnchor) && slotAnchor != null)
            {
                Vector3[] corners = new Vector3[4];
                slotAnchor.GetWorldCorners(corners);

                Canvas canvas = slotAnchor.GetComponentInParent<Canvas>();
                Camera uiCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
                Vector2 max = new Vector2(float.MinValue, float.MinValue);
                for (int index = 0; index < corners.Length; index++)
                {
                    Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(uiCamera, corners[index]);
                    min = Vector2.Min(min, screenPoint);
                    max = Vector2.Max(max, screenPoint);
                }

                Vector2 size = max - min;
                if (size.x > 1f && size.y > 1f)
                {
                    return size * Mathf.Max(0.1f, inventoryItemPreviewScale);
                }
            }

            return fallbackSize * 0.72f * Mathf.Max(0.1f, inventoryItemPreviewScale);
        }

        private Vector2 EstimateTrayPreviewStartSize(Texture2D texture, MahjongTile tile, Camera activeCamera)
        {
            if (texture == null)
            {
                return new Vector2(96f, 96f);
            }

            Vector2 textureSize = new Vector2(texture.width, texture.height);
            if (tile == null || activeCamera == null)
            {
                return textureSize;
            }

            Vector3 tileSize = tile.GetPlacementSize();
            if (tileSize.sqrMagnitude <= Mathf.Epsilon)
            {
                return textureSize;
            }

            Vector3 worldCenter = tile.transform.position;
            float widthWorld = Mathf.Max(0.01f, tileSize.x);
            float heightWorld = Mathf.Max(0.01f, tileSize.y);
            Vector3 leftScreen = activeCamera.WorldToScreenPoint(worldCenter - (activeCamera.transform.right * (widthWorld * 0.5f)));
            Vector3 rightScreen = activeCamera.WorldToScreenPoint(worldCenter + (activeCamera.transform.right * (widthWorld * 0.5f)));
            Vector3 downScreen = activeCamera.WorldToScreenPoint(worldCenter - (activeCamera.transform.up * (heightWorld * 0.5f)));
            Vector3 upScreen = activeCamera.WorldToScreenPoint(worldCenter + (activeCamera.transform.up * (heightWorld * 0.5f)));
            Vector2 screenSize = new Vector2(
                Mathf.Abs(rightScreen.x - leftScreen.x),
                Mathf.Abs(upScreen.y - downScreen.y));

            if (screenSize.x <= 1f || screenSize.y <= 1f)
            {
                return textureSize;
            }

            return screenSize;
        }

        private IEnumerator PlayTrayPreviewMatchSequenceRoutine(
            MahjongTile firstTile,
            MahjongTile secondTile,
            RuntimeTrayPreview firstPreview,
            RuntimeTrayPreview secondPreview,
            Action onImpact)
        {
            Camera activeCamera = null;
            if (Context.Services.TryGet(out CameraManager cameraManager))
            {
                activeCamera = cameraManager.ActiveCamera;
            }

            if (firstPreview == null && firstTile != null)
            {
                TryCreateTrayPreview(firstTile, activeCamera, -1, out firstPreview);
            }

            if (secondPreview == null && secondTile != null)
            {
                TryCreateTrayPreview(secondTile, activeCamera, -1, out secondPreview);
            }

            // Do not start the match-removal animation while either preview is still
            // being driven by its board-to-tray coroutine.
            while ((firstPreview != null && firstPreview.IsEnteringTray)
                || (secondPreview != null && secondPreview.IsEnteringTray))
            {
                yield return null;
            }

            SetTileRenderersVisible(firstTile, false);
            SetTileRenderersVisible(secondTile, false);

            if (firstPreview?.RectTransform == null && secondPreview?.RectTransform == null)
            {
                yield break;
            }

            if (firstPreview?.RectTransform == null)
            {
                yield return AnimateSingleTrayPreviewMatch(secondTile, secondPreview, firstTile, onImpact);
                yield break;
            }

            if (secondPreview?.RectTransform == null)
            {
                yield return AnimateSingleTrayPreviewMatch(firstTile, firstPreview, secondTile, onImpact);
                yield break;
            }

            if (firstPreview.AnimationRoutine != null)
            {
                StopCoroutine(firstPreview.AnimationRoutine);
                firstPreview.AnimationRoutine = null;
            }

            if (secondPreview.AnimationRoutine != null)
            {
                StopCoroutine(secondPreview.AnimationRoutine);
                secondPreview.AnimationRoutine = null;
            }

            RectTransform firstRect = firstPreview.RectTransform;
            RectTransform secondRect = secondPreview.RectTransform;

            // Keep both matching previews above every other tile while they move.
            firstRect.SetAsLastSibling();
            secondRect.SetAsLastSibling();

            Vector2 firstStartPos = firstRect.anchoredPosition;
            Vector2 secondStartPos = secondRect.anchoredPosition;
            Vector2 firstStartSize = firstRect.sizeDelta;
            Vector2 secondStartSize = secondRect.sizeDelta;
            Vector3 firstStartScale = firstRect.localScale;
            Vector3 secondStartScale = secondRect.localScale;

            Vector2 landingCenter = ResolveTrayMatchLandingPosition(firstStartPos, secondStartPos, firstStartSize, secondStartSize);
            float halfWidthFirst = firstStartSize.x * 0.5f;
            float halfWidthSecond = secondStartSize.x * 0.5f;
            float landingContactOffset = Mathf.Max(10f, (halfWidthFirst + halfWidthSecond) * 0.22f);
            float landingSeparationOffset = landingContactOffset + 40f;
            Vector2 firstImpactPos = landingCenter - (Vector2.right * landingSeparationOffset);
            Vector2 secondImpactPos = landingCenter + (Vector2.right * landingSeparationOffset);
            float firstSide = firstStartPos.x <= secondStartPos.x ? -1f : 1f;
            float secondSide = -firstSide;
            Vector2 firstControlOne = BuildTrayMatchFirstArcControlPoint(firstStartPos, firstImpactPos, firstSide);
            Vector2 firstControlTwo = BuildTrayMatchSecondArcControlPoint(firstStartPos, firstImpactPos, firstSide);
            Vector2 secondControlOne = BuildTrayMatchFirstArcControlPoint(secondStartPos, secondImpactPos, secondSide);
            Vector2 secondControlTwo = BuildTrayMatchSecondArcControlPoint(secondStartPos, secondImpactPos, secondSide);
            float duration = GetTrayMatchPreviewDurationSeconds();
            float effectTriggerT = GetTrayMatchEffectTriggerNormalized();
            float travelDuration = Mathf.Max(0.05f, duration * 0.42f);
            float holdDuration = Mathf.Max(0.08f, duration * 0.18f);
            float leanBackDuration = Mathf.Max(0.12f, duration * 0.28f);
            float throwDuration = Mathf.Max(0.05f, duration - (travelDuration + holdDuration + leanBackDuration));
            float elapsed = 0f;
            Vector3 firstLandingScale = firstStartScale * Mathf.Max(0.1f, trayMatchLandingScale);
            Vector3 secondLandingScale = secondStartScale * Mathf.Max(0.1f, trayMatchLandingScale);

            float firstTileTiltDirection = firstStartPos.x <= secondStartPos.x ? 1f : -1f;
            float secondTileTiltDirection = -firstTileTiltDirection;
            Vector2 firstThrowPos = firstImpactPos + (Vector2.right * firstSide * Mathf.Max(18f, landingContactOffset * 0.8f));
            Vector2 secondThrowPos = secondImpactPos + (Vector2.right * secondSide * Mathf.Max(18f, landingContactOffset * 0.8f));

            while (elapsed < travelDuration)
            {
                elapsed += GetDeltaTime();
                float t = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, travelDuration));
                float easedT = 1f - Mathf.Pow(1f - t, 3f);
                float scaleT = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, travelDuration));
                float easedScaleT = 1f - Mathf.Pow(1f - scaleT, 3f);

                firstRect.anchoredPosition = EvaluateCubicBezier(firstStartPos, firstControlOne, firstControlTwo, firstImpactPos, easedT);
                secondRect.anchoredPosition = EvaluateCubicBezier(secondStartPos, secondControlOne, secondControlTwo, secondImpactPos, easedT);
                firstRect.localScale = Vector3.LerpUnclamped(firstStartScale, firstLandingScale, easedScaleT);
                secondRect.localScale = Vector3.LerpUnclamped(secondStartScale, secondLandingScale, easedScaleT);
                firstRect.localRotation = Quaternion.identity;
                secondRect.localRotation = Quaternion.identity;
                yield return null;
            }

            firstRect.anchoredPosition = firstImpactPos;
            secondRect.anchoredPosition = secondImpactPos;
            firstRect.localScale = firstLandingScale;
            secondRect.localScale = secondLandingScale;
            firstRect.localRotation = Quaternion.identity;
            secondRect.localRotation = Quaternion.identity;

            elapsed = 0f;
            while (elapsed < holdDuration)
            {
                elapsed += GetDeltaTime();
                firstRect.anchoredPosition = firstImpactPos;
                secondRect.anchoredPosition = secondImpactPos;
                firstRect.localScale = firstLandingScale;
                secondRect.localScale = secondLandingScale;
                firstRect.localRotation = Quaternion.identity;
                secondRect.localRotation = Quaternion.identity;
                yield return null;
            }

            bool firstUsesLeftCornerPivot = firstStartPos.x <= secondStartPos.x;
            bool secondUsesLeftCornerPivot = !firstUsesLeftCornerPivot;
            firstRect.anchoredPosition = firstImpactPos;
            secondRect.anchoredPosition = secondImpactPos;

            elapsed = 0f;
            while (elapsed < leanBackDuration)
            {
                elapsed += GetDeltaTime();
                float t = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, leanBackDuration));
                float easedT = 1f - Mathf.Pow(1f - t, 3f);
                float leanAngle = Mathf.Lerp(0f, matchTileMaxTiltAngle * 0.9f, easedT);
                ApplyCornerLeanWithoutPositionJump(firstRect, firstImpactPos, firstUsesLeftCornerPivot, firstTileTiltDirection * leanAngle);
                ApplyCornerLeanWithoutPositionJump(secondRect, secondImpactPos, secondUsesLeftCornerPivot, secondTileTiltDirection * leanAngle);
                yield return null;
            }

            elapsed = 0f;
            while (elapsed < throwDuration)
            {
                elapsed += GetDeltaTime();
                float t = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, throwDuration));
                float easedT = 1f - Mathf.Pow(1f - t, 4f);
                float throwProgress = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, throwDuration));
                float leanAngle = Mathf.Lerp(matchTileMaxTiltAngle * 0.85f, 0f, throwProgress);

                Vector2 firstCurrentPos = Vector2.LerpUnclamped(firstImpactPos, firstThrowPos, easedT);
                Vector2 secondCurrentPos = Vector2.LerpUnclamped(secondImpactPos, secondThrowPos, easedT);
                ApplyCornerLeanWithoutPositionJump(firstRect, firstCurrentPos, firstUsesLeftCornerPivot, firstTileTiltDirection * leanAngle);
                ApplyCornerLeanWithoutPositionJump(secondRect, secondCurrentPos, secondUsesLeftCornerPivot, secondTileTiltDirection * leanAngle);
                yield return null;
            }

            firstRect.anchoredPosition = firstThrowPos;
            secondRect.anchoredPosition = secondThrowPos;
            firstRect.localRotation = Quaternion.identity;
            secondRect.localRotation = Quaternion.identity;

            Vector2 effectCenter = (firstRect.anchoredPosition + secondRect.anchoredPosition) * 0.5f;
            PlayMatchFeedbackUi(firstTile, secondTile, effectCenter, firstPreview, secondPreview);
            onImpact?.Invoke();
            firstTile?.SetVisible(false);
            secondTile?.SetVisible(false);

            if (firstPreview.GameObject != null)
            {
                firstPreview.GameObject.SetActive(false);
            }

            if (secondPreview.GameObject != null)
            {
                secondPreview.GameObject.SetActive(false);
            }

            DestroyTrayPreview(firstPreview);
            DestroyTrayPreview(secondPreview);
            if (firstTile != null)
            {
                trayPreviewsByTile.Remove(firstTile);
            }

            if (secondTile != null)
            {
                trayPreviewsByTile.Remove(secondTile);
            }
        }

        private Vector2 ResolveTrayMatchLandingPosition(Vector2 firstStartPos, Vector2 secondStartPos, Vector2 firstStartSize, Vector2 secondStartSize)
        {
            Vector2 trayCenter;
            if (TryGetTraySlotScreenPoint(0, out Vector2 firstSlotScreen) && TryGetTraySlotScreenPoint(3, out Vector2 lastSlotScreen))
            {
                trayCenter = ScreenToTrayOverlayPosition((firstSlotScreen + lastSlotScreen) * 0.5f);
            }
            else
            {
                trayCenter = (firstStartPos + secondStartPos) * 0.5f;
            }

            float verticalOffset = Mathf.Max(
                trayMatchLandingYOffset,
                Mathf.Max(firstStartSize.y, secondStartSize.y) * 0.32f);
            return trayCenter + Vector2.down * verticalOffset;
        }

        private Vector2 BuildTrayMatchFirstArcControlPoint(Vector2 startPosition, Vector2 endPosition, float side)
        {
            float resolvedSide = Mathf.Sign(side);
            if (Mathf.Approximately(resolvedSide, 0f))
            {
                resolvedSide = 1f;
            }

            return Vector2.LerpUnclamped(startPosition, endPosition, 0.28f)
                + new Vector2(resolvedSide * Mathf.Max(1f, trayMatchArcSideDistance), Mathf.Max(0f, trayMatchArcLift));
        }

        private Vector2 BuildTrayMatchSecondArcControlPoint(Vector2 startPosition, Vector2 endPosition, float side)
        {
            float resolvedSide = Mathf.Sign(side);
            if (Mathf.Approximately(resolvedSide, 0f))
            {
                resolvedSide = 1f;
            }

            return Vector2.LerpUnclamped(startPosition, endPosition, 0.72f)
                + new Vector2(resolvedSide * Mathf.Max(1f, trayMatchArcSideDistance) * 0.55f, Mathf.Max(0f, trayMatchArcLift) * 0.35f);
        }

        private static void ApplyCornerLeanWithoutPositionJump(RectTransform rect, Vector2 basePosition, bool useLeftCorner, float angleDegrees)
        {
            if (rect == null)
            {
                return;
            }

            Vector2 size = rect.rect.size;
            Vector2 centerToCorner = useLeftCorner
                ? new Vector2(size.x * 0.5f, size.y * 0.5f)
                : new Vector2(-size.x * 0.5f, size.y * 0.5f);

            float radians = angleDegrees * Mathf.Deg2Rad;
            Vector2 rotatedOffset = new Vector2(
                (centerToCorner.x * Mathf.Cos(radians)) - (centerToCorner.y * Mathf.Sin(radians)),
                (centerToCorner.x * Mathf.Sin(radians)) + (centerToCorner.y * Mathf.Cos(radians)));

            rect.anchoredPosition = basePosition + (rotatedOffset - centerToCorner);
            rect.localRotation = Quaternion.Euler(0f, 0f, angleDegrees);
        }

        private static Vector2 EvaluateCubicBezier(Vector2 start, Vector2 controlOne, Vector2 controlTwo, Vector2 end, float t)
        {
            float inverseT = 1f - t;
            return (inverseT * inverseT * inverseT * start)
                + (3f * inverseT * inverseT * t * controlOne)
                + (3f * inverseT * t * t * controlTwo)
                + (t * t * t * end);
        }

        private IEnumerator AnimateSingleTrayPreviewMatch(MahjongTile tileWithPreview, RuntimeTrayPreview preview, MahjongTile otherTile, Action onImpact)
        {
            if (preview?.RectTransform == null)
            {
                yield break;
            }

            if (preview.AnimationRoutine != null)
            {
                StopCoroutine(preview.AnimationRoutine);
                preview.AnimationRoutine = null;
            }

            RectTransform rect = preview.RectTransform;
            rect.SetAsLastSibling();
            Vector2 startPos = rect.anchoredPosition;
            Vector2 startSize = rect.sizeDelta;
            Vector3 startScale = rect.localScale;
            Vector2 landingPos = ResolveTrayMatchLandingPosition(startPos, startPos, startSize, startSize);
            float side = startPos.x <= landingPos.x ? -1f : 1f;
            Vector2 controlOne = BuildTrayMatchFirstArcControlPoint(startPos, landingPos, side);
            Vector2 controlTwo = BuildTrayMatchSecondArcControlPoint(startPos, landingPos, side);
            Vector3 landingScale = startScale * Mathf.Max(0.1f, trayMatchLandingScale);
            float duration = GetTrayMatchPreviewDurationSeconds();
            float effectTriggerT = GetTrayMatchEffectTriggerNormalized();
            float flightDuration = Mathf.Max(0.05f, duration * effectTriggerT);
            float elapsed = 0f;

            float tiltDirection = startPos.x <= landingPos.x ? 1f : -1f;

            while (elapsed < flightDuration)
            {
                elapsed += GetDeltaTime();
                float t = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, duration));
                float scaleT = Mathf.Clamp01(elapsed / flightDuration);
                float progress = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, flightDuration));
                float easedT = 1f - Mathf.Pow(1f - t, 3f);
                float easedScaleT = 1f - Mathf.Pow(1f - scaleT, 3f);
                float tiltAngle = matchTileMaxTiltAngle * Mathf.Sin(progress * Mathf.PI);
                rect.anchoredPosition = EvaluateCubicBezier(startPos, controlOne, controlTwo, landingPos, easedT);
                rect.localScale = Vector3.LerpUnclamped(startScale, landingScale, easedScaleT);
                rect.localRotation = Quaternion.Euler(0f, 0f, tiltDirection * tiltAngle);
                yield return null;
            }

            float finalEasedT = 1f - Mathf.Pow(1f - effectTriggerT, 3f);
            rect.anchoredPosition = EvaluateCubicBezier(startPos, controlOne, controlTwo, landingPos, finalEasedT);
            rect.localScale = landingScale;
            rect.localRotation = Quaternion.identity;

            PlayMatchFeedbackUi(tileWithPreview, otherTile, rect.anchoredPosition, preview, null);
            onImpact?.Invoke();
            tileWithPreview?.SetVisible(false);
            otherTile?.SetVisible(false);

            if (preview.GameObject != null)
            {
                preview.GameObject.SetActive(false);
            }

            DestroyTrayPreview(preview);
            if (tileWithPreview != null)
            {
                trayPreviewsByTile.Remove(tileWithPreview);
            }
        }

        private void PlayMatchFeedbackUi(
            MahjongTile firstTile,
            MahjongTile secondTile,
            Vector2 impactUiPosition,
            RuntimeTrayPreview firstPreview,
            RuntimeTrayPreview secondPreview)
        {
            if (TryPlayMatchUiShardEffectUi(firstTile, secondTile, impactUiPosition, firstPreview, secondPreview))
            {
                return;
            }

            Camera activeCamera = null;
            if (Context.Services.TryGet(out CameraManager cameraManager))
            {
                activeCamera = cameraManager.ActiveCamera;
            }

            if (activeCamera != null)
            {
                Vector2 screenPos = TrayOverlayToScreenPosition(impactUiPosition);
                float distance = GetTrayTargetDistance(activeCamera, firstTile != null ? firstTile : secondTile);
                Vector3 worldPos = activeCamera.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, distance));
                PlayMatchParticle(worldPos);
            }
        }

        private bool TryPlayMatchUiShardEffectUi(
            MahjongTile firstTile,
            MahjongTile secondTile,
            Vector2 impactPosition,
            RuntimeTrayPreview firstPreview,
            RuntimeTrayPreview secondPreview)
        {
            if (animationSettings == null || !animationSettings.UseMatchUiShards)
            {
                return false;
            }

            if (matchUiShardEffect == null)
            {
                matchUiShardEffect = MatchUiShardEffect.Create(transform);
            }

            matchUiShardEffect?.Prewarm();

            Vector2 firstUiPosition = impactPosition;
            Vector2 secondUiPosition = impactPosition;
            Vector2 firstScreenPosition = TrayOverlayToScreenPosition(firstUiPosition);
            Vector2 secondScreenPosition = TrayOverlayToScreenPosition(secondUiPosition);
            Vector2 firstSize = firstPreview?.RectTransform != null
                ? firstPreview.RectTransform.rect.size
                : new Vector2(96f, 96f);
            Vector2 secondSize = secondPreview?.RectTransform != null
                ? secondPreview.RectTransform.rect.size
                : new Vector2(96f, 96f);

            return matchUiShardEffect != null && matchUiShardEffect.PlayUi(
                firstTile,
                secondTile,
                firstScreenPosition,
                secondScreenPosition,
                firstSize,
                secondSize,
                animationSettings);
        }

        private Vector2 TrayOverlayToScreenPosition(Vector2 anchoredPosition)
        {
            if (trayOverlayRoot == null)
            {
                return anchoredPosition;
            }

            Vector3 worldPoint = trayOverlayRoot.TransformPoint(anchoredPosition);
            return RectTransformUtility.WorldToScreenPoint(null, worldPoint);
        }

        private void DestroyTrayPreview(RuntimeTrayPreview preview)
        {
            if (preview == null)
            {
                return;
            }

            if (preview.AnimationRoutine != null)
            {
                FinalizeTrayPreview(preview);
            }

            if (preview.GameObject != null)
            {
                Destroy(preview.GameObject);
            }
        }

        private static void SetTileRenderersVisible(MahjongTile tile, bool isVisible)
        {
            if (tile == null)
            {
                return;
            }

            Renderer[] renderers = tile.GetComponentsInChildren<Renderer>(true);
            for (int index = 0; index < renderers.Length; index++)
            {
                Renderer renderer = renderers[index];
                if (renderer != null)
                {
                    renderer.enabled = isVisible;
                }
            }
        }

        private struct TrayShakeState
        {
            public Vector2 Position;

            public Quaternion Rotation;

            public Vector3 Scale;
        }

        private sealed class RuntimeTrayPreview
        {
            public GameObject GameObject { get; set; }

            public RectTransform RectTransform { get; set; }

            public CanvasGroup CanvasGroup { get; set; }

            public InventoryItemPreview ItemPreview { get; set; }

            public Coroutine AnimationRoutine { get; set; }

            public int TargetSlotIndex { get; set; }

            public Vector2 TargetPosition { get; set; }

            public Vector2 TargetSize { get; set; }

            public bool IsEnteringTray { get; set; }
        }

        /// <summary>
        /// Projects a tile's half-size onto the supplied world axis for edge-contact alignment.
        /// </summary>
        private static float GetProjectedHalfExtent(MahjongTile tile, Quaternion rotation, Vector3 worldAxis)
        {
            if (tile == null)
            {
                return 0f;
            }

            Vector3 tileSize = tile.GetPlacementSize();
            if (tileSize.sqrMagnitude <= Mathf.Epsilon)
            {
                return 0f;
            }

            Vector3 halfSize = tileSize * 0.5f;
            Vector3 right = rotation * Vector3.right;
            Vector3 up = rotation * Vector3.up;
            Vector3 forward = rotation * Vector3.forward;

            return (Mathf.Abs(Vector3.Dot(right, worldAxis)) * halfSize.x)
                + (Mathf.Abs(Vector3.Dot(up, worldAxis)) * halfSize.y)
                + (Mathf.Abs(Vector3.Dot(forward, worldAxis)) * halfSize.z);
        }

        private float GetMatchDurationSeconds() => animationSettings != null ? animationSettings.MatchDurationSeconds : 0.35f;
        private float GetTrayMatchPreviewDurationSeconds() => Mathf.Max(GetMatchDurationSeconds(), trayMatchPreviewMinDurationSeconds);
        private float GetTrayMatchEffectTriggerNormalized() => Mathf.Clamp(trayMatchEffectTriggerNormalized, 0.5f, 1f);
        private float GetMatchSlideDistance() => animationSettings != null ? animationSettings.MatchSlideDistance : 1.25f;
        private float GetMatchRotationDegrees() => animationSettings != null ? animationSettings.MatchRotationDegrees : 55f;
        private float GetMatchDisappearGap() => animationSettings != null ? animationSettings.MatchDisappearGap : 0.08f;
        private float GetHintDurationSeconds() => animationSettings != null ? animationSettings.HintDurationSeconds : 0.7f;
        private float GetMismatchDelaySeconds() => animationSettings != null ? animationSettings.MismatchDelaySeconds : 0.2f;
        private float GetTrayMoveDurationSeconds() => animationSettings != null ? animationSettings.TrayMoveDurationSeconds : 0.22f;
        private float GetTrayViewportY() => animationSettings != null ? animationSettings.TrayViewportY : 0.84f;
        private float GetTrayViewportSlotSpacing() => animationSettings != null ? animationSettings.TrayViewportSlotSpacing : 0.1f;
        private float GetMatchViewportYOffset() => animationSettings != null ? animationSettings.MatchViewportYOffset : 0.05f;
        private float GetMatchDepthOffset() => animationSettings != null ? animationSettings.MatchDepthOffset : 1.25f;
        private float GetTrayDistanceFromCamera() => animationSettings != null ? animationSettings.TrayDistanceFromCamera : 8f;
        private float GetTrayDistancePadding() => animationSettings != null ? animationSettings.TrayDistancePadding : 0.75f;
        private float GetShakeDurationSeconds() => animationSettings != null ? animationSettings.ShakeDurationSeconds : 0.18f;
        private float GetShakeAmplitude() => animationSettings != null ? animationSettings.ShakeAmplitude : 0.12f;
    }
}
