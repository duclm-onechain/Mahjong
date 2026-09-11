using MahjongOut3D.Core;
using MahjongOut3D.Gameplay;
using MahjongOut3D.LevelSystem;
using MahjongOut3D.TileSystem;
using MahjongOut3D.Utilities;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MahjongOut3D.Managers
{
    /// <summary>
    /// Owns tile selection, pair validation, board progression and gameplay power-ups.
    /// </summary>
    public sealed class MatchManager : ManagerBehaviour
    {
        private const string SelectHistoryActionName = "Select";
        private const string MatchHistoryActionName = "Match";
        private const float AnimationCompletionTimeoutSeconds = 5f;
        private const int SelectionTrayCapacity = 4;
        private const float MemoryRevealTimeoutSeconds = 2f;
        private const int ComboExtraMatchPairCount = 3;
        private const int MaxPendingSelectionActions = SelectionTrayCapacity + 2;

        [SerializeField] private PowerUpSettings powerUpSettings;

        private readonly List<MahjongTile> selectedTiles = new List<MahjongTile>(SelectionTrayCapacity);
        private readonly List<MahjongTile> memorySelectedTiles = new List<MahjongTile>(2);
        private readonly Stack<MoveHistoryRecord> history = new Stack<MoveHistoryRecord>();
        private readonly Queue<PendingMatchResolution> pendingMatchQueue = new Queue<PendingMatchResolution>();
        private readonly Queue<MahjongTile> pendingSelectionActions = new Queue<MahjongTile>();
        private readonly HashSet<MahjongTile> queuedSelectionTiles = new HashSet<MahjongTile>();
        private readonly HashSet<MahjongTile> pendingMatchedTiles = new HashSet<MahjongTile>();
        private readonly HashSet<MahjongTile> movingToSelectionTrayTiles = new HashSet<MahjongTile>();
        private readonly Dictionary<MahjongTile, bool> memorySelectionOriginalFaceStates = new Dictionary<MahjongTile, bool>();
        private readonly List<MahjongTile> comboSourceTiles = new List<MahjongTile>(2);
        private Coroutine memoryRevealTimeoutRoutine;
        private Coroutine pendingSelectionActionsRoutine;
        private MahjongTile activeSelectionTile;
        private bool isMemoryRevealLocked;
        private int selectionActionGeneration;
        private int totalTiles;
        private int lastTrayScreenWidth;
        private int lastTrayScreenHeight;

        /// <summary>
        /// Gets a value indicating whether a match resolution is currently running.
        /// </summary>
        public bool IsResolvingMatch { get; private set; }

        /// <summary>
        /// Gets the currently selected tile count.
        /// </summary>
        public int SelectedTileCount => selectedTiles.Count;

        /// <summary>
        /// Gets the saved quantity available for a power-up.
        /// </summary>
        public int GetPowerUpAvailableCount(PowerUpType powerUpType)
        {
            return GetSaveManager()?.GetPowerUpCount(powerUpType) ?? 0;
        }

        /// <summary>
        /// Gets the bootstrap order for the match manager.
        /// </summary>
        public override int InitializationOrder => 30;

        /// <summary>
        /// Subscribes to tile taps and level generation events.
        /// </summary>
        protected override void OnInitialize()
        {
            Context.EventBus.Subscribe<TileTappedEvent>(HandleTileTapped);
            Context.EventBus.Subscribe<LevelGeneratedEvent>(HandleLevelGenerated);
            lastTrayScreenWidth = Screen.width;
            lastTrayScreenHeight = Screen.height;
        }

        /// <summary>
        /// Clears transient runtime state during shutdown.
        /// </summary>
        protected override void OnShutdown()
        {
            Context.EventBus.Unsubscribe<TileTappedEvent>(HandleTileTapped);
            Context.EventBus.Unsubscribe<LevelGeneratedEvent>(HandleLevelGenerated);
            CancelMemoryRevealTimeout();
            selectedTiles.Clear();
            memorySelectedTiles.Clear();
            history.Clear();
            pendingMatchQueue.Clear();
            ClearPendingSelectionActions();
            pendingMatchedTiles.Clear();
            movingToSelectionTrayTiles.Clear();
            memorySelectionOriginalFaceStates.Clear();
            comboSourceTiles.Clear();
            totalTiles = 0;
            isMemoryRevealLocked = false;
            IsResolvingMatch = false;
        }

        private void LateUpdate()
        {
            if (selectedTiles.Count == 0 || (lastTrayScreenWidth == Screen.width && lastTrayScreenHeight == Screen.height))
            {
                return;
            }

            lastTrayScreenWidth = Screen.width;
            lastTrayScreenHeight = Screen.height;

            AnimationManager animationManager = GetAnimationManager();
            if (animationManager == null || animationManager.IsAnimationLocked)
            {
                return;
            }

            ReflowSelectionTray();
        }

        /// <summary>
        /// Starts a match resolution block if none is active.
        /// </summary>
        /// <returns>True when the lock was acquired; otherwise false.</returns>
        public bool TryBeginResolution()
        {
            if (IsResolvingMatch)
            {
                return false;
            }

            IsResolvingMatch = true;
            return true;
        }

        /// <summary>
        /// Ends the current match resolution block.
        /// </summary>
        public void EndResolution()
        {
            IsResolvingMatch = false;
        }

        /// <summary>
        /// Clears transient tray, selection and coroutine state before the active level is regenerated.
        /// </summary>
        public void PrepareForLevelReload()
        {
            MahjongTile tileBeingProcessed = activeSelectionTile;
            StopAllCoroutines();
            CancelMemoryRevealTimeout();
            ClearPendingSelectionActions();
            if (tileBeingProcessed != null && !selectedTiles.Contains(tileBeingProcessed))
            {
                tileBeingProcessed.SetBufferedSelection(false);
                tileBeingProcessed.RestoreBoardParent();
                tileBeingProcessed.RestoreBoardPose();
                tileBeingProcessed.Deselect();
                GetAnimationManager()?.SetTrayBoardTileVisible(tileBeingProcessed, true);
                if (tileBeingProcessed.TileCollider != null)
                {
                    tileBeingProcessed.TileCollider.enabled = !tileBeingProcessed.IsRemoved && !tileBeingProcessed.IsMatched;
                }
            }

            GetAnimationManager()?.ClearTrayPreviews();

            for (int index = 0; index < selectedTiles.Count; index++)
            {
                MahjongTile tile = selectedTiles[index];
                if (tile == null)
                {
                    continue;
                }

                tile.SetBufferedSelection(false);
                tile.RestoreBoardParent();
                tile.RestoreBoardPose();
                tile.StopFaceFlipAnimation(false);
                tile.Deselect();
                GetAnimationManager()?.SetTrayBoardTileVisible(tile, true);
                if (tile.TileCollider != null)
                {
                    tile.TileCollider.enabled = !tile.IsRemoved && !tile.IsMatched;
                }
            }

            for (int index = 0; index < memorySelectedTiles.Count; index++)
            {
                MahjongTile tile = memorySelectedTiles[index];
                if (tile == null)
                {
                    continue;
                }

                tile.StopFaceFlipAnimation(false);
                tile.Deselect();
            }

            selectedTiles.Clear();
            memorySelectedTiles.Clear();
            history.Clear();
            pendingMatchQueue.Clear();
            ClearPendingSelectionActions();
            pendingMatchedTiles.Clear();
            movingToSelectionTrayTiles.Clear();
            memorySelectionOriginalFaceStates.Clear();
            comboSourceTiles.Clear();
            isMemoryRevealLocked = false;
            IsResolvingMatch = false;
        }

        /// <summary>
        /// Uses the hint power-up to highlight a valid exposed pair.
        /// </summary>
        /// <returns>True when a hint was used successfully; otherwise false.</returns>
        public bool UseHint()
        {
            if (!TryFindAnySelectablePair(out MahjongTile firstTile, out MahjongTile secondTile))
            {
                return false;
            }

            if (!TryUsePowerUp(PowerUpType.Hint))
            {
                return false;
            }

            GetAudioManager()?.PlayPowerUp(PowerUpType.Hint);
            GetAnimationManager()?.PlayHintSequence(firstTile, secondTile);
            Context.EventBus.Publish(new HintSuggestedEvent(firstTile, secondTile));
            Context.EventBus.Publish(new PowerUpUsedEvent(PowerUpType.Hint));
            IAchievementController.Instance?.UpdateAchievementProgress(EAchievementType.UseBooster);
            return true;
        }

        /// <summary>
        /// Uses the undo power-up to restore the previous reversible board state.
        /// </summary>
        /// <returns>True when Undo succeeds; otherwise false.</returns>
        public bool UseUndo()
        {
            if (!HasSelectionHistoryRecord() || !TryUsePowerUp(PowerUpType.Undo))
            {
                return false;
            }

            if (!TryPopLatestSelectionRecord(out MoveHistoryRecord record))
            {
                return false;
            }

            RestoreHistoryRecord(record);

            GetTileManager()?.RefreshTileExposure();
            PublishProgress();
            GetAudioManager()?.PlayPowerUp(PowerUpType.Undo);
            Context.EventBus.Publish(new PowerUpUsedEvent(PowerUpType.Undo));
            IAchievementController.Instance?.UpdateAchievementProgress(EAchievementType.UseBooster);
            return true;
        }

        /// <summary>
        /// Uses the shuffle power-up to randomly redistribute remaining tiles.
        /// </summary>
        /// <returns>True when Shuffle succeeds; otherwise false.</returns>
        public bool UseShuffle()
        {
            TileManager tileManager = GetTileManager();
            if (tileManager == null)
            {
                return false;
            }

            List<MahjongTile> snapshotTiles = new List<MahjongTile>(tileManager.GetRemainingTiles());
            if (snapshotTiles.Count <= 1)
            {
                return false;
            }

            if (!TryUsePowerUp(PowerUpType.Shuffle))
            {
                return false;
            }

            DeselectAll();

            List<MahjongTile> remainingTiles = new List<MahjongTile>();
            foreach (MahjongTile tile in tileManager.GetRemainingTiles())
            {
                if (tile != null && !tile.IsBufferedSelection)
                {
                    remainingTiles.Add(tile);
                }
            }

            GetAudioManager()?.PlayPowerUp(PowerUpType.Shuffle);
            GetAudioManager()?.PlayTileAppear();
            ShuffleTileMatches(remainingTiles);

            tileManager.RefreshTileExposure();
            Context.EventBus.Publish(new PowerUpUsedEvent(PowerUpType.Shuffle));
            IAchievementController.Instance?.UpdateAchievementProgress(EAchievementType.UseBooster);
            EvaluateBoardState();
            return true;
        }

        /// <summary>
        /// Uses the bomb power-up to remove a valid exposed pair automatically.
        /// </summary>
        /// <returns>True when Bomb succeeds; otherwise false.</returns>
        public bool UseBomb()
        {
            if (!TryFindAnySelectablePair(out MahjongTile firstTile, out MahjongTile secondTile))
            {
                return false;
            }

            if (!TryUsePowerUp(PowerUpType.Bomb))
            {
                return false;
            }

            GetAudioManager()?.PlayPowerUp(PowerUpType.Bomb);
            QueueMatchedPairForResolution(firstTile, secondTile, false);
            Context.EventBus.Publish(new PowerUpUsedEvent(PowerUpType.Bomb));
            IAchievementController.Instance?.UpdateAchievementProgress(EAchievementType.UseBooster);
            return true;
        }

        /// <summary>
        /// Debug helper that automatically resolves any currently valid pair without consuming resources.
        /// </summary>
        /// <returns>True when a valid pair was found and scheduled for removal; otherwise false.</returns>
        public bool DebugAutoMatch()
        {
            if (IsResolvingMatch || !TryFindAnySelectablePair(out MahjongTile firstTile, out MahjongTile secondTile))
            {
                return false;
            }

            QueueMatchedPairForResolution(firstTile, secondTile, false);
            return true;
        }

        /// <summary>
        /// Uses the X-Ray power-up to temporarily reveal one extra inner layer.
        /// </summary>
        /// <returns>True when X-Ray succeeds; otherwise false.</returns>
        public bool UseXRay()
        {
            TileManager tileManager = GetTileManager();
            if (tileManager == null)
            {
                return false;
            }

            if (!TryUsePowerUp(PowerUpType.XRay))
            {
                return false;
            }

            tileManager.EnableXRay(GetXRayDurationSeconds(), GetXRayRevealDepth());
            GetAudioManager()?.PlayPowerUp(PowerUpType.XRay);
            Context.EventBus.Publish(new PowerUpUsedEvent(PowerUpType.XRay));
            IAchievementController.Instance?.UpdateAchievementProgress(EAchievementType.UseBooster);
            return true;
        }

        /// <summary>
        /// Handles tile taps routed from the 3D raycast system.
        /// </summary>
        private void HandleTileTapped(TileTappedEvent eventData)
        {
            TileManager tileManager = GetTileManager();
            GameManager gameManager = GetGameManager();
            if (gameManager == null || tileManager == null || gameManager.CurrentFlowState != GameFlowState.Gameplay)
            {
                return;
            }

            MahjongTile tappedTile = eventData.Tile;
            if (tappedTile == null)
            {
                return;
            }

            if (IsTilePendingMatch(tappedTile)
                || IsTileMovingToSelectionTray(tappedTile)
                || queuedSelectionTiles.Contains(tappedTile))
            {
                return;
            }

            if (!tileManager.IsTileTapSelectable(tappedTile, eventData.HitInfo))
            {
                tappedTile.PlayBlockedTapFeedback();
                return;
            }

            MahjongTile matchingPreviewTile = UsesMemoryFlipSelectionMode()
                ? FindMatchingPreviewTile(tappedTile)
                : null;
            if (matchingPreviewTile != null)
            {
                StartCoroutine(HandleMatchWithPreviewedFaceDownTile(matchingPreviewTile, tappedTile));
                return;
            }

            bool usesFaceDownPreview = UsesMemoryFlipSelectionMode()
                && (tappedTile.IsFaceDown || memorySelectedTiles.Contains(tappedTile));
            if (usesFaceDownPreview)
            {
                StartCoroutine(HandleFaceDownPreviewTileTapped(tappedTile));
                return;
            }

            EnqueueSelectionTile(tappedTile);
        }

        private void EnqueueSelectionTile(MahjongTile tappedTile)
        {
            if (tappedTile == null || selectedTiles.Contains(tappedTile))
            {
                return;
            }

            if (pendingSelectionActions.Count >= MaxPendingSelectionActions)
            {
                tappedTile.PlayBlockedTapFeedback();
                return;
            }

            if (!tappedTile.TrySelect())
            {
                tappedTile.PlayBlockedTapFeedback();
                return;
            }

            queuedSelectionTiles.Add(tappedTile);
            pendingSelectionActions.Enqueue(tappedTile);
            if (pendingSelectionActionsRoutine == null)
            {
                pendingSelectionActionsRoutine = StartCoroutine(ProcessPendingSelectionActions());
            }
        }

        private IEnumerator ProcessPendingSelectionActions()
        {
            int generation = selectionActionGeneration;
            while (pendingSelectionActions.Count > 0 && generation == selectionActionGeneration)
            {
                MahjongTile tappedTile = pendingSelectionActions.Dequeue();
                queuedSelectionTiles.Remove(tappedTile);
                activeSelectionTile = tappedTile;
                if (!IsQueuedSelectionTileValid(tappedTile))
                {
                    tappedTile?.Deselect();
                    activeSelectionTile = null;
                    continue;
                }

                yield return CollapsePreviewedFaceDownTiles(tappedTile);
                if (generation != selectionActionGeneration || !IsQueuedSelectionTileValid(tappedTile))
                {
                    tappedTile?.Deselect();
                    activeSelectionTile = null;
                    continue;
                }

                MahjongTile matchingTrayTile = FindMatchingTrayTile(tappedTile);
                if (selectedTiles.Count >= SelectionTrayCapacity && matchingTrayTile == null)
                {
                    tappedTile.Deselect();
                    GetAnimationManager()?.PlayTrayOccupancyFeedback(selectedTiles.Count, false);

                    if (!IsSelectionResolutionBusy())
                    {
                        if (!TryStartFirstMatchingPairInSelectionTray(true))
                        {
                            float wait = 0f;
                            float timeout = 0.25f;
                            bool started = false;
                            while (wait < timeout)
                            {
                                wait += GetResolutionDeltaTime();
                                yield return null;
                                if (TryStartFirstMatchingPairInSelectionTray(true))
                                {
                                    started = true;
                                    break;
                                }
                            }

                            if (!started)
                            {
                                TriggerLoseWithDebug("TrayFull_MoveTileIntoSelectionTray");
                                ClearPendingSelectionActions();
                                activeSelectionTile = null;
                                yield break;
                            }
                        }
                    }
                    else
                    {
                        while (generation == selectionActionGeneration && IsSelectionResolutionBusy())
                        {
                            yield return null;
                        }
                    }

                    activeSelectionTile = null;
                    continue;
                }

                int matchSlotIndex = matchingTrayTile != null ? selectedTiles.IndexOf(matchingTrayTile) : -1;
                int targetSlotIndex = matchingTrayTile != null
                    ? Mathf.Clamp(matchSlotIndex + 1, 0, SelectionTrayCapacity - 1)
                    : Mathf.Clamp(selectedTiles.Count, 0, SelectionTrayCapacity - 1);

                yield return MoveTileIntoSelectionTray(tappedTile, matchingTrayTile, targetSlotIndex);
                activeSelectionTile = null;

                GameManager activeGameManager = GetGameManager();
                if (activeGameManager == null || activeGameManager.CurrentFlowState != GameFlowState.Gameplay)
                {
                    ClearPendingSelectionActions();
                    yield break;
                }

                while (generation == selectionActionGeneration
                    && (IsResolvingMatch || pendingMatchedTiles.Count > 0 || pendingMatchQueue.Count > 0))
                {
                    yield return null;
                }
            }

            if (generation == selectionActionGeneration)
            {
                pendingSelectionActionsRoutine = null;
            }
        }

        private bool IsQueuedSelectionTileValid(MahjongTile tile)
        {
            return tile != null
                && !tile.IsRemoved
                && !tile.IsMatched
                && !tile.IsBufferedSelection
                && !pendingMatchedTiles.Contains(tile)
                && !movingToSelectionTrayTiles.Contains(tile);
        }

        private IEnumerator HandleFaceDownPreviewTileTapped(MahjongTile tappedTile)
        {
            if (tappedTile == null || IsResolvingMatch || isMemoryRevealLocked)
            {
                yield break;
            }

            if (memorySelectedTiles.Contains(tappedTile))
            {
                MahjongTile matchingTrayTile = FindMatchingTrayTile(tappedTile);
                if (selectedTiles.Count >= SelectionTrayCapacity && matchingTrayTile == null)
                {
                    if (TryStartFirstMatchingPairInSelectionTray(true))
                    {
                        yield break;
                    }

                    matchingTrayTile = FindMatchingTrayTile(tappedTile);
                    if (selectedTiles.Count >= SelectionTrayCapacity && matchingTrayTile == null)
                    {
                        if (IsSelectionResolutionBusy())
                        {
                            yield break;
                        }

                        if (!TryStartFirstMatchingPairInSelectionTray(true))
                        {
                            float wait = 0f;
                            float timeout = 0.25f;
                            bool started = false;
                            while (wait < timeout)
                            {
                                wait += GetResolutionDeltaTime();
                                yield return null;
                                if (TryStartFirstMatchingPairInSelectionTray(true))
                                {
                                    started = true;
                                    break;
                                }
                            }

                            if (!started)
                            {
                                if (TryFindAnyBoardPair(out _, out _))
                                {
                                    yield break;
                                }

                                TriggerLoseWithDebug("TrayFull_FaceDownPreviewPath");
                                yield break;
                            }
                        }
                        else
                        {
                            yield break;
                        }
                    }
                }

                memorySelectedTiles.Remove(tappedTile);
                ClearMemorySelectionOriginalFaceState(tappedTile);
                tappedTile.Deselect();

                yield return CollapsePreviewedFaceDownTiles(tappedTile);

                if (!tappedTile.TrySelect())
                {
                    tappedTile.PlayBlockedTapFeedback();
                    yield break;
                }

                yield return MoveTileIntoSelectionTray(tappedTile, matchingTrayTile);
                yield break;
            }

            if (!tappedTile.TrySelect())
            {
                tappedTile.PlayBlockedTapFeedback();
                yield break;
            }

            yield return CollapsePreviewedFaceDownTiles(tappedTile);

            isMemoryRevealLocked = true;
            CancelMemoryRevealTimeout();
            memorySelectionOriginalFaceStates[tappedTile] = tappedTile.IsFaceDown;
            memorySelectedTiles.Add(tappedTile);
            GetAudioManager()?.PlaySelect();

            MahjongTile trayMatchTile = FindMatchingTrayTile(tappedTile);

            bool completed = false;
            GetAudioManager()?.PlayFlipTile();
            tappedTile.FlipFaceUp(() => completed = true);

            float elapsed = 0f;
            while (!completed && elapsed < AnimationCompletionTimeoutSeconds)
            {
                elapsed += GetResolutionDeltaTime();
                yield return null;
            }

            if (trayMatchTile != null)
            {
                memorySelectedTiles.Remove(tappedTile);
                ClearMemorySelectionOriginalFaceState(tappedTile);
                isMemoryRevealLocked = false;
                yield return MoveTileIntoSelectionTray(tappedTile, trayMatchTile);
                yield break;
            }

            tappedTile.Deselect();
            isMemoryRevealLocked = false;
        }

        private IEnumerator HandleMatchWithPreviewedFaceDownTile(MahjongTile previewTile, MahjongTile tappedTile)
        {
            if (previewTile == null || tappedTile == null || IsResolvingMatch || isMemoryRevealLocked)
            {
                yield break;
            }

            yield return CollapsePreviewedFaceDownTiles(previewTile);

            if (tappedTile.IsFaceDown)
            {
                if (!tappedTile.TrySelect())
                {
                    tappedTile.PlayBlockedTapFeedback();
                    yield break;
                }

                bool flipCompleted = false;
                GetAudioManager()?.PlayFlipTile();
                tappedTile.FlipFaceUp(() => flipCompleted = true);

                float flipElapsed = 0f;
                while (!flipCompleted && flipElapsed < AnimationCompletionTimeoutSeconds)
                {
                    flipElapsed += GetResolutionDeltaTime();
                    yield return null;
                }
            }

            memorySelectedTiles.Remove(previewTile);
            ClearMemorySelectionOriginalFaceState(previewTile);
            previewTile.Deselect();

            QueueMatchedPairForResolution(previewTile, tappedTile, true, true);
        }

        private IEnumerator CollapsePreviewedFaceDownTiles(MahjongTile referenceTile)
        {
            if (memorySelectedTiles.Count == 0)
            {
                yield break;
            }

            List<MahjongTile> tilesToHide = new List<MahjongTile>();
            for (int index = 0; index < memorySelectedTiles.Count; index++)
            {
                MahjongTile previewTile = memorySelectedTiles[index];
                if (previewTile == null)
                {
                    continue;
                }

                if (previewTile == referenceTile)
                {
                    continue;
                }

                if (referenceTile != null && previewTile.HasSameVisualIdentity(referenceTile))
                {
                    continue;
                }

                tilesToHide.Add(previewTile);
            }

            if (tilesToHide.Count == 0)
            {
                yield break;
            }

            isMemoryRevealLocked = true;

            bool[] completions = new bool[tilesToHide.Count];
            for (int index = 0; index < tilesToHide.Count; index++)
            {
                MahjongTile tile = tilesToHide[index];
                if (tile == null)
                {
                    completions[index] = true;
                    continue;
                }

                tile.Deselect();
                if (ShouldRestoreMemoryTileFaceDown(tile))
                {
                    int capturedIndex = index;
                    GetAudioManager()?.PlayFlipTile();
                    tile.FlipFaceDown(() => completions[capturedIndex] = true);
                }
                else
                {
                    completions[index] = true;
                }
            }

            float elapsed = 0f;
            while (elapsed < AnimationCompletionTimeoutSeconds)
            {
                bool allCompleted = true;
                for (int index = 0; index < completions.Length; index++)
                {
                    if (!completions[index])
                    {
                        allCompleted = false;
                        break;
                    }
                }

                if (allCompleted)
                {
                    break;
                }

                elapsed += GetResolutionDeltaTime();
                yield return null;
            }

            for (int index = 0; index < tilesToHide.Count; index++)
            {
                MahjongTile tile = tilesToHide[index];
                memorySelectedTiles.Remove(tile);
                ClearMemorySelectionOriginalFaceState(tile);
            }

            isMemoryRevealLocked = false;
        }

        /// <summary>
        /// Resets runtime state and starts gameplay after a new level has been generated.
        /// </summary>
        private void HandleLevelGenerated(LevelGeneratedEvent eventData)
        {
            PrepareForLevelReload();
            totalTiles = eventData.SpawnedTileCount;
            GameplayManager.Instance?.ResetCombo();
            ApplyDifficultyTileFaceState();
            AssignDifficultyComboTiles();
            if (GameplayManager.Instance != null)
            {
                if (Context.Services.TryGet(out LevelManager levelManager) && levelManager.CurrentLevelIndex >= 0)
                {
                    GameplayManager.Instance.CurrentLevel = levelManager.CurrentLevelIndex + 1;
                }
                GameplayManager.Instance.StartGame();
            }
            GetGameManager()?.StartGameplay();
            PublishProgress();
        }

        private IEnumerator HandleMemoryModeTileTapped(MahjongTile tappedTile)
        {
            if (tappedTile == null)
            {
                yield break;
            }

            if (memorySelectedTiles.Count >= 2)
            {
                yield break;
            }

            if (!tappedTile.TrySelect())
            {
                tappedTile.PlayBlockedTapFeedback();
                yield break;
            }

            history.Push(CreateSelectionSnapshotRecord(tappedTile, true));

            isMemoryRevealLocked = true;
            CancelMemoryRevealTimeout();
            memorySelectionOriginalFaceStates[tappedTile] = tappedTile.IsFaceDown;
            memorySelectedTiles.Add(tappedTile);
            GetAudioManager()?.PlaySelect();

            bool completed = false;
            GetAudioManager()?.PlayFlipTile();
            tappedTile.FlipFaceUp(() => completed = true);

            float elapsed = 0f;
            while (!completed && elapsed < AnimationCompletionTimeoutSeconds)
            {
                elapsed += GetResolutionDeltaTime();
                yield return null;
            }

            isMemoryRevealLocked = false;

            if (memorySelectedTiles.Count <= 1)
            {
                ScheduleMemoryRevealTimeout();
                yield break;
            }

            MahjongTile firstTile = memorySelectedTiles[0];
            MahjongTile secondTile = memorySelectedTiles[1];
            if (firstTile == null || secondTile == null)
            {
                HideMemorySelectedTilesInstant();
                yield break;
            }

            if (firstTile.HasSameVisualIdentity(secondTile))
            {
                QueueMatchedPairForResolution(firstTile, secondTile, true);
                yield break;
            }

            StartCoroutine(ResolveMemoryMismatch(firstTile, secondTile));
        }

        /// <summary>
        /// Resolves a successful pair match.
        /// </summary>
        private IEnumerator ResolveMatchedPair(
            MahjongTile firstTile,
            MahjongTile secondTile,
            bool rewardCoins,
            bool moveToTrayBeforeMatch = false)
        {
            if (!TryBeginResolution())
            {
                yield break;
            }

            try
            {
                bool triggersCombo = IsComboTriggerPair(firstTile, secondTile);
                firstTile.MarkMatched();
                secondTile.MarkMatched();
                GetAudioManager()?.PlayMatch();

                bool completed = false;
                AnimationManager animationManager = null;
                Context.Services.TryGet(out animationManager);
                if (animationManager != null)
                {
                    if (moveToTrayBeforeMatch)
                    {
                        yield return MoveAutoMatchedPairIntoTray(firstTile, secondTile, animationManager);
                    }

                    animationManager.PlayMatchSequence(firstTile, secondTile, () => completed = true, () => GetAudioManager()?.PlayTileSmash());
                }
                else
                {
                    GetAudioManager()?.PlayTileSmash();
                    completed = true;
                }

                float elapsed = 0f;
                while (!completed && elapsed < AnimationCompletionTimeoutSeconds)
                {
                    elapsed += GetResolutionDeltaTime();
                    yield return null;
                }

                if (!completed)
                {
                    MahjongRuntimeLogger.LogWarning("Match animation timed out. Completing resolution to avoid locked input.");
                }

                LevelManager levelManager = GetLevelManager();
                if (levelManager?.ActiveGrid != null)
                {
                    levelManager.ActiveGrid.RemoveTile(firstTile.TileId);
                    levelManager.ActiveGrid.RemoveTile(secondTile.TileId);
                }

                pendingMatchedTiles.Remove(firstTile);
                pendingMatchedTiles.Remove(secondTile);
                GetAnimationManager()?.ClearTrayTile(firstTile);
                GetAnimationManager()?.ClearTrayTile(secondTile);
                firstTile.SetBufferedSelection(false);
                secondTile.SetBufferedSelection(false);
                firstTile.MarkRemoved();
                secondTile.MarkRemoved();
                RemoveTrayTileReference(firstTile);
                RemoveTrayTileReference(secondTile);

                if (rewardCoins)
                {
                    GetSaveManager()?.AddCoins(GetCoinsPerMatch());
                }

                ReflowSelectionTray();
                GetTileManager()?.RefreshTileExposure();
                IAchievementController.Instance?.UpdateAchievementProgress(EAchievementType.Match3);
                GameplayManager.Instance?.RegisterSuccessfulMatch(GetMatchScoreValue(firstTile, secondTile));
                GetAudioManager()?.PlayCombo(GameplayManager.Instance != null ? GameplayManager.Instance.CurrentCombo : 0);
                Context.EventBus.Publish(new MatchSucceededEvent(firstTile, secondTile));
                PublishProgress();

                if (triggersCombo)
                {
                    TriggerComboAutoMatches(firstTile, secondTile);
                }
            }
            finally
            {
                EndResolution();
                if (!TryStartNextQueuedMatch())
                {
                    EvaluateBoardState();
                }
            }
        }

        /// <summary>
        /// Resolves a mismatch by deselecting both tiles after a short delay.
        /// </summary>
        private IEnumerator ResolveMismatch(MahjongTile firstTile, MahjongTile secondTile)
        {
            if (!TryBeginResolution())
            {
                yield break;
            }

            try
            {
                GetAudioManager()?.PlayMismatch();
                bool completed = false;
                AnimationManager animationManager = null;
                Context.Services.TryGet(out animationManager);
                if (animationManager != null)
                {
                    animationManager.PlayMismatchDelay(() => completed = true);
                }
                else
                {
                    completed = true;
                }

                float elapsed = 0f;
                while (!completed && elapsed < AnimationCompletionTimeoutSeconds)
                {
                    elapsed += GetResolutionDeltaTime();
                    yield return null;
                }

                if (!completed)
                {
                    MahjongRuntimeLogger.LogWarning("Mismatch animation timed out. Completing resolution to avoid locked input.");
                }

                firstTile.Deselect();
                secondTile.Deselect();
                GameplayManager.Instance?.ResetCombo();
                selectedTiles.Clear();
                Context.EventBus.Publish(new MatchFailedEvent(firstTile, secondTile));
            }
            finally
            {
                EndResolution();
            }
        }

        private IEnumerator ResolveMemoryMismatch(MahjongTile firstTile, MahjongTile secondTile)
        {
            if (!TryBeginResolution())
            {
                yield break;
            }

            CancelMemoryRevealTimeout();
            isMemoryRevealLocked = true;

            try
            {

                float elapsed = 0f;
                while (elapsed < MemoryRevealTimeoutSeconds)
                {
                    elapsed += GetResolutionDeltaTime();
                    yield return null;
                }

                bool firstCompleted = firstTile == null;
                bool secondCompleted = secondTile == null;

                if (firstTile != null)
                {
                    firstTile.Deselect();
                    if (ShouldRestoreMemoryTileFaceDown(firstTile))
                    {
                        GetAudioManager()?.PlayFlipTile();
                        firstTile.FlipFaceDown(() => firstCompleted = true);
                    }
                    else
                    {
                        firstCompleted = true;
                    }
                }

                if (secondTile != null)
                {
                    secondTile.Deselect();
                    if (ShouldRestoreMemoryTileFaceDown(secondTile))
                    {
                        GetAudioManager()?.PlayFlipTile();
                        secondTile.FlipFaceDown(() => secondCompleted = true);
                    }
                    else
                    {
                        secondCompleted = true;
                    }
                }

                elapsed = 0f;
                while ((!firstCompleted || !secondCompleted) && elapsed < AnimationCompletionTimeoutSeconds)
                {
                    elapsed += GetResolutionDeltaTime();
                    yield return null;
                }

                memorySelectedTiles.Clear();
                ClearMemorySelectionOriginalFaceState(firstTile);
                ClearMemorySelectionOriginalFaceState(secondTile);
                GameplayManager.Instance?.ResetCombo();
                Context.EventBus.Publish(new MatchFailedEvent(firstTile, secondTile));
            }
            finally
            {
                isMemoryRevealLocked = false;
                EndResolution();
            }
        }

        /// <summary>
        /// Gets a stable delta time for resolution watchdog timers.
        /// </summary>
        private static float GetResolutionDeltaTime()
        {
            return Time.unscaledDeltaTime > 0f ? Time.unscaledDeltaTime : Time.deltaTime;
        }

        /// <summary>
        /// Attempts to find any valid selectable pair among currently exposed tiles.
        /// </summary>
        private bool TryFindAnySelectablePair(out MahjongTile firstTile, out MahjongTile secondTile)
        {
            firstTile = null;
            secondTile = null;

            TileManager tileManager = GetTileManager();
            if (tileManager == null)
            {
                return false;
            }

            for (int trayIndex = 0; trayIndex < selectedTiles.Count; trayIndex++)
            {
                MahjongTile trayTile = selectedTiles[trayIndex];
                if (trayTile == null || trayTile.IsRemoved)
                {
                    continue;
                }

                foreach (MahjongTile tile in tileManager.GetExposedTiles())
                {
                    if (tile == null || tile == trayTile || tile.IsRemoved || tile.IsBufferedSelection || !tileManager.IsTileHintSelectable(tile))
                    {
                        continue;
                    }

                    if (tile.HasSameVisualIdentity(trayTile))
                    {
                        firstTile = trayTile;
                        secondTile = tile;
                        return true;
                    }
                }
            }

            List<MahjongTile> hintCandidates = GetOrderedHintCandidates(tileManager);
            Dictionary<string, MahjongTile> firstByVisualKey = new Dictionary<string, MahjongTile>();
            for (int index = 0; index < hintCandidates.Count; index++)
            {
                MahjongTile tile = hintCandidates[index];
                if (tile == null || tile.IsRemoved || tile.IsBufferedSelection || !tileManager.IsTileHintSelectable(tile))
                {
                    continue;
                }

                string visualKey = tile.VisualMatchKey;
                if (firstByVisualKey.TryGetValue(visualKey, out MahjongTile existingTile) && existingTile != tile)
                {
                    firstTile = existingTile;
                    secondTile = tile;
                    return true;
                }

                firstByVisualKey[visualKey] = tile;
            }

            return false;
        }

        /// <summary>
        /// Gets hint candidates ordered from the outermost visible shell inward.
        /// </summary>
        private List<MahjongTile> GetOrderedHintCandidates(TileManager tileManager)
        {
            List<MahjongTile> hintCandidates = new List<MahjongTile>();
            if (tileManager == null)
            {
                return hintCandidates;
            }

            foreach (MahjongTile tile in tileManager.GetExposedTiles())
            {
                hintCandidates.Add(tile);
            }

            bool useSurfaceRules = Context.Services.TryGet(out LevelManager levelManager) && levelManager.ActiveUsesSurfaceTilePlacement;
            hintCandidates.Sort((left, right) => CompareHintCandidates(left, right, useSurfaceRules));
            return hintCandidates;
        }

        /// <summary>
        /// Orders hint candidates so auto-resolve prefers the outer shell first.
        /// </summary>
        private static int CompareHintCandidates(MahjongTile left, MahjongTile right, bool useSurfaceRules)
        {
            if (object.ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left == null)
            {
                return 1;
            }

            if (right == null)
            {
                return -1;
            }

            if (useSurfaceRules)
            {
                int shellComparison = left.SurfaceShellIndex.CompareTo(right.SurfaceShellIndex);
                if (shellComparison != 0)
                {
                    return shellComparison;
                }
            }

            return left.TileId.CompareTo(right.TileId);
        }

        /// <summary>
        /// Deselects every currently selected tile.
        /// </summary>
        private void DeselectAll()
        {
            CancelMemoryRevealTimeout();

            LevelManager levelManager = GetLevelManager();
            for (int index = 0; index < selectedTiles.Count; index++)
            {
                MahjongTile tile = selectedTiles[index];
                if (tile != null)
                {
                    RestoreTileFromSelectionTray(tile, levelManager);
                }
            }

            selectedTiles.Clear();
            HideMemorySelectedTilesInstant();
            GetTileManager()?.RefreshTileExposure();
        }

        private void HideMemorySelectedTilesInstant()
        {
            for (int index = 0; index < memorySelectedTiles.Count; index++)
            {
                MahjongTile tile = memorySelectedTiles[index];
                if (tile == null)
                {
                    continue;
                }

                tile.StopFaceFlipAnimation(false);
                tile.Deselect();
                tile.SetFaceDown(ShouldRestoreMemoryTileFaceDown(tile), true);
                ClearMemorySelectionOriginalFaceState(tile);
            }

            memorySelectedTiles.Clear();
            isMemoryRevealLocked = false;
        }

        private void ScheduleMemoryRevealTimeout()
        {
            CancelMemoryRevealTimeout();
            if (!UsesMemoryFlipSelectionMode() || memorySelectedTiles.Count != 1)
            {
                return;
            }

            memoryRevealTimeoutRoutine = StartCoroutine(MemoryRevealTimeoutRoutine());
        }

        private IEnumerator MemoryRevealTimeoutRoutine()
        {
            float elapsed = 0f;
            while (elapsed < MemoryRevealTimeoutSeconds)
            {
                if (!UsesMemoryFlipSelectionMode() || memorySelectedTiles.Count != 1 || IsResolvingMatch || isMemoryRevealLocked)
                {
                    memoryRevealTimeoutRoutine = null;
                    yield break;
                }

                elapsed += GetResolutionDeltaTime();
                yield return null;
            }

            if (memorySelectedTiles.Count == 1)
            {
                MahjongTile tile = memorySelectedTiles[0];
                bool completed = tile == null;
                isMemoryRevealLocked = true;

                if (tile != null)
                {
                    tile.Deselect();
                    if (ShouldRestoreMemoryTileFaceDown(tile))
                    {
                        GetAudioManager()?.PlayFlipTile();
                        tile.FlipFaceDown(() => completed = true);
                    }
                    else
                    {
                        completed = true;
                    }
                }

                elapsed = 0f;
                while (!completed && elapsed < AnimationCompletionTimeoutSeconds)
                {
                    elapsed += GetResolutionDeltaTime();
                    yield return null;
                }

                memorySelectedTiles.Clear();
                ClearMemorySelectionOriginalFaceState(tile);
                isMemoryRevealLocked = false;
            }

            memoryRevealTimeoutRoutine = null;
        }

        private void CancelMemoryRevealTimeout()
        {
            if (memoryRevealTimeoutRoutine != null)
            {
                StopCoroutine(memoryRevealTimeoutRoutine);
                memoryRevealTimeoutRoutine = null;
            }
        }

        private void ClearPendingSelectionActions()
        {
            ClearPendingSelectionActions(null);
        }

        private void ClearPendingSelectionActions(MahjongTile preservedActiveTile)
        {
            selectionActionGeneration++;
            if (pendingSelectionActionsRoutine != null)
            {
                StopCoroutine(pendingSelectionActionsRoutine);
                pendingSelectionActionsRoutine = null;
            }

            if (activeSelectionTile != null
                && activeSelectionTile != preservedActiveTile
                && !activeSelectionTile.IsBufferedSelection
                && !activeSelectionTile.IsRemoved
                && !activeSelectionTile.IsMatched)
            {
                activeSelectionTile.Deselect();
            }

            activeSelectionTile = null;
            movingToSelectionTrayTiles.Clear();
            while (pendingSelectionActions.Count > 0)
            {
                MahjongTile tile = pendingSelectionActions.Dequeue();
                queuedSelectionTiles.Remove(tile);
                if (tile != null && tile != preservedActiveTile && !tile.IsBufferedSelection && !tile.IsRemoved && !tile.IsMatched)
                {
                    tile.Deselect();
                }
            }

            queuedSelectionTiles.Clear();
        }

        private void ApplyDifficultyTileFaceState()
        {
            TileManager tileManager = GetTileManager();
            if (tileManager == null)
            {
                return;
            }

            List<MahjongTile> tiles = new List<MahjongTile>();
            foreach (MahjongTile tile in tileManager.GetAllTiles())
            {
                if (tile == null || tile.IsRemoved || tile.IsMatched)
                {
                    continue;
                }

                tiles.Add(tile);
            }

            if (tiles.Count == 0)
            {
                return;
            }

            float faceDownRatio = GetMemoryFaceDownRatio();
            int faceDownCount = Mathf.Clamp(Mathf.RoundToInt(tiles.Count * faceDownRatio), 0, tiles.Count);

            for (int index = tiles.Count - 1; index > 0; index--)
            {
                int swapIndex = Random.Range(0, index + 1);
                (tiles[index], tiles[swapIndex]) = (tiles[swapIndex], tiles[index]);
            }

            for (int index = 0; index < tiles.Count; index++)
            {
                MahjongTile tile = tiles[index];
                tile.StopFaceFlipAnimation(false);
                tile.SetFaceDown(index < faceDownCount, true);
            }
        }

        private float GetMemoryFaceDownRatio()
        {
            LevelManager levelManager = GetLevelManager();
            if (levelManager?.ActiveLevelDefinition == null)
            {
                return 0f;
            }

            return levelManager.ActiveLevelDefinition.GetResolvedFaceDownTileRatio();
        }

        private bool UsesMemoryFlipSelectionMode()
        {
            return GetMemoryFaceDownRatio() > 0f;
        }

        private void AssignDifficultyComboTiles()
        {
            TileManager tileManager = GetTileManager();
            if (tileManager == null)
            {
                return;
            }

            Dictionary<string, List<MahjongTile>> candidateGroups = new Dictionary<string, List<MahjongTile>>();
            foreach (MahjongTile tile in tileManager.GetRemainingTiles())
            {
                if (tile == null || tile.IsRemoved)
                {
                    continue;
                }

                tile.SetComboTile(false);
                if (tile.IsBufferedSelection)
                {
                    continue;
                }

                string visualKey = tile.VisualMatchKey;
                if (!candidateGroups.TryGetValue(visualKey, out List<MahjongTile> group))
                {
                    group = new List<MahjongTile>();
                    candidateGroups.Add(visualKey, group);
                }

                group.Add(tile);
            }

            List<List<MahjongTile>> candidatePairs = new List<List<MahjongTile>>(candidateGroups.Values);
            int comboTileCount = GetDifficultyComboTileCount(candidatePairs.Count);
            if (comboTileCount <= 0 || candidatePairs.Count <= 0)
            {
                return;
            }

            for (int index = candidatePairs.Count - 1; index > 0; index--)
            {
                int swapIndex = Random.Range(0, index + 1);
                (candidatePairs[index], candidatePairs[swapIndex]) = (candidatePairs[swapIndex], candidatePairs[index]);
            }

            int assignedCount = Mathf.Min(comboTileCount, candidatePairs.Count);
            for (int index = 0; index < assignedCount; index++)
            {
                List<MahjongTile> pairTiles = candidatePairs[index];
                if (pairTiles == null || pairTiles.Count <= 0)
                {
                    continue;
                }

                MahjongTile comboTile = pairTiles[Random.Range(0, pairTiles.Count)];
                if (comboTile != null)
                {
                    comboTile.SetComboTile(true);
                }
            }
        }

        private int GetDifficultyComboTileCount(int availablePairCount)
        {
            if (availablePairCount <= 0)
            {
                return 0;
            }

            LevelManager levelManager = GetLevelManager();
            if (levelManager?.ActiveLevelDefinition == null)
            {
                return 0;
            }

            float comboRatio = levelManager.ActiveLevelDefinition.GetResolvedComboTileRatio();

            if (comboRatio <= 0f)
            {
                return 0;
            }

            int comboTileCount = Mathf.RoundToInt(availablePairCount * comboRatio);
            if (comboTileCount <= 0)
            {
                comboTileCount = 1;
            }

            return Mathf.Clamp(comboTileCount, 0, availablePairCount);
        }

        private bool IsComboTriggerPair(MahjongTile firstTile, MahjongTile secondTile)
        {
            bool firstIsCombo = firstTile != null && firstTile.IsComboTile;
            bool secondIsCombo = secondTile != null && secondTile.IsComboTile;
            return firstIsCombo || secondIsCombo;
        }

        private void TriggerComboAutoMatches(MahjongTile firstTile, MahjongTile secondTile)
        {
            comboSourceTiles.Clear();
            if (firstTile != null && firstTile.IsComboTile)
            {
                firstTile.SetComboTile(false);
                comboSourceTiles.Add(firstTile);
            }

            if (secondTile != null && secondTile.IsComboTile)
            {
                secondTile.SetComboTile(false);
                comboSourceTiles.Add(secondTile);
            }

            for (int index = 0; index < ComboExtraMatchPairCount; index++)
            {
                if (!TryFindRandomBoardPair(out MahjongTile comboFirstTile, out MahjongTile comboSecondTile))
                {
                    break;
                }

                QueueMatchedPairForResolution(comboFirstTile, comboSecondTile, false, true);
            }

            comboSourceTiles.Clear();
        }

        private bool TryFindRandomBoardPair(out MahjongTile firstTile, out MahjongTile secondTile)
        {
            firstTile = null;
            secondTile = null;

            TileManager tileManager = GetTileManager();
            if (tileManager == null)
            {
                return false;
            }

            Dictionary<string, List<MahjongTile>> pairsByVisualKey = new Dictionary<string, List<MahjongTile>>();
            foreach (MahjongTile tile in tileManager.GetRemainingTiles())
            {
                if (!IsEligibleComboTarget(tileManager, tile))
                {
                    continue;
                }

                string visualKey = tile.VisualMatchKey;
                if (!pairsByVisualKey.TryGetValue(visualKey, out List<MahjongTile> matchingTiles))
                {
                    matchingTiles = new List<MahjongTile>();
                    pairsByVisualKey.Add(visualKey, matchingTiles);
                }

                matchingTiles.Add(tile);
            }

            List<List<MahjongTile>> pairGroups = new List<List<MahjongTile>>();
            foreach (List<MahjongTile> pairGroup in pairsByVisualKey.Values)
            {
                if (pairGroup != null && pairGroup.Count >= 2)
                {
                    pairGroups.Add(pairGroup);
                }
            }

            if (pairGroups.Count <= 0)
            {
                return false;
            }

            List<MahjongTile> selectedGroup = pairGroups[Random.Range(0, pairGroups.Count)];
            int firstIndex = Random.Range(0, selectedGroup.Count);
            firstTile = selectedGroup[firstIndex];
            selectedGroup.RemoveAt(firstIndex);
            secondTile = selectedGroup[Random.Range(0, selectedGroup.Count)];
            return firstTile != null && secondTile != null;
        }

        private bool IsEligibleComboTarget(TileManager tileManager, MahjongTile tile)
        {
            if (tile == null || tile.IsRemoved || tile.IsBufferedSelection || pendingMatchedTiles.Contains(tile))
            {
                return false;
            }

            for (int index = 0; index < comboSourceTiles.Count; index++)
            {
                if (tile == comboSourceTiles[index])
                {
                    return false;
                }
            }

            return IsTileAvailableForBoardPair(tileManager, tile);
        }

        /// <summary>
        /// Publishes gameplay progress based on the number of remaining tiles.
        /// </summary>
        private void PublishProgress()
        {
            TileManager tileManager = GetTileManager();
            if (tileManager == null)
            {
                return;
            }

            int remainingTileCount = CountRemainingBoardTiles(tileManager);
            Context.EventBus.Publish(new GameplayProgressChangedEvent(remainingTileCount, totalTiles));
        }

        /// <summary>
        /// Evaluates the current board for win or no-moves conditions.
        /// </summary>
        private void EvaluateBoardState()
        {
            TileManager tileManager = GetTileManager();
            GameManager gameManager = GetGameManager();
            if (tileManager == null || gameManager == null)
            {
                return;
            }

            int remainingTileCount = CountRemainingBoardTiles(tileManager);
            bool allBoardTilesResolved = AreAllBoardTilesResolved(tileManager);

            if (remainingTileCount == 0 || allBoardTilesResolved)
            {
                GetSaveManager()?.AddCoins(GetCoinsPerLevelWin());
                GetAudioManager()?.PlayWin();
                gameManager.WinGameplay();
                return;
            }

            if (IsSelectionResolutionBusy())
            {
                return;
            }

            // Strict fallback: some board configurations can produce false-positives
            // in TryFindAnyBoardPair. As a last-resort, group remaining tiles by
            // visual key and ensure at least one group contains >= 2 truly
            // available tiles. If none do, trigger a loss.
            int strictPairCount = 0;
            var groups = new System.Collections.Generic.Dictionary<string, int>();
            foreach (MahjongTile t in tileManager.GetRemainingTiles())
            {
                if (t == null || !IsTileAvailableForBoardPair(tileManager, t))
                    continue;

                string key = t.VisualMatchKey ?? string.Empty;
                if (groups.TryGetValue(key, out int c))
                    groups[key] = c + 1;
                else
                    groups[key] = 1;
            }

            foreach (var kv in groups)
            {
                if (kv.Value >= 2)
                {
                    strictPairCount = 1;
                    break;
                }
            }

            if (remainingTileCount > 0 && strictPairCount == 0)
            {
                MahjongRuntimeLogger.Log($"[Mahjong] No valid pairs (strict) detected. Remaining={remainingTileCount}");
                Context.EventBus.Publish(new NoMovesRemainingEvent());
                TriggerLoseWithDebug("BoardNoValidPairs_StrictCheck");
                return;
            }

            if (!TryFindAnyBoardPair(out _, out _))
            {
                Context.EventBus.Publish(new NoMovesRemainingEvent());
                TriggerLoseWithDebug("BoardNoValidPairs_TryFindAnyBoardPair");
            }
        }

        private int CountRemainingBoardTiles(TileManager tileManager)
        {
            if (tileManager == null)
            {
                return 0;
            }

            int remainingCount = 0;
            foreach (MahjongTile tile in tileManager.GetAllTiles())
            {
                if (tile == null || tile.IsRemoved || tile.IsMatched || tile.IsBufferedSelection)
                {
                    continue;
                }

                if (tile.State == TileState.Hidden || tile.State == TileState.Visible || tile.State == TileState.Selected)
                {
                    remainingCount++;
                }
            }

            return remainingCount;
        }

        private bool AreAllBoardTilesResolved(TileManager tileManager)
        {
            if (tileManager == null)
            {
                return false;
            }

            int resolvedCount = 0;
            foreach (MahjongTile tile in tileManager.GetAllTiles())
            {
                if (tile == null)
                {
                    continue;
                }

                if (tile.IsRemoved || tile.IsMatched)
                {
                    resolvedCount++;
                    continue;
                }

                if (tile.IsBufferedSelection)
                {
                    continue;
                }

                return false;
            }

            return totalTiles > 0 && resolvedCount >= totalTiles;
        }

        /// <summary>
        /// Attempts to find any remaining board pair without depending on the current camera visibility.
        /// </summary>
        private bool TryFindAnyBoardPair(out MahjongTile firstTile, out MahjongTile secondTile)
        {
            firstTile = null;
            secondTile = null;

            TileManager tileManager = GetTileManager();
            if (tileManager == null)
            {
                return false;
            }

            for (int trayIndex = 0; trayIndex < selectedTiles.Count; trayIndex++)
            {
                MahjongTile trayTile = selectedTiles[trayIndex];
                if (trayTile == null || trayTile.IsRemoved)
                {
                    continue;
                }

                foreach (MahjongTile tile in tileManager.GetRemainingTiles())
                {
                    if (tile == null || tile == trayTile || tile.IsRemoved || tile.IsBufferedSelection || !IsTileAvailableForBoardPair(tileManager, tile))
                    {
                        continue;
                    }

                    if (tile.HasSameVisualIdentity(trayTile))
                    {
                        firstTile = trayTile;
                        secondTile = tile;
                        return true;
                    }
                }
            }

            Dictionary<string, MahjongTile> firstByVisualKey = new Dictionary<string, MahjongTile>();
            foreach (MahjongTile tile in tileManager.GetRemainingTiles())
            {
                if (tile == null || tile.IsRemoved || tile.IsBufferedSelection || !IsTileAvailableForBoardPair(tileManager, tile))
                {
                    continue;
                }

                string visualKey = tile.VisualMatchKey;
                if (firstByVisualKey.TryGetValue(visualKey, out MahjongTile existingTile) && existingTile != tile)
                {
                    firstTile = existingTile;
                    secondTile = tile;
                    return true;
                }

                firstByVisualKey[visualKey] = tile;
            }

            return false;
        }

        /// <summary>
        /// Determines whether a tile should count toward board move availability checks.
        /// </summary>
        private bool IsTileAvailableForBoardPair(TileManager tileManager, MahjongTile tile)
        {
            if (tileManager == null || tile == null || tile.IsRemoved || tile.IsMatched || !tile.IsInteractable || tile.IsBufferedSelection)
            {
                return false;
            }

            if (tile.State == TileState.Hidden && !UsesMemoryFlipSelectionMode())
            {
                return false;
            }

            return tileManager.IsTileHintSelectable(tile);
        }

        /// <summary>
        /// Captures tile state snapshots for a reversible move.
        /// </summary>
        private static MoveHistoryRecord CreateSnapshotRecord(string actionName, IEnumerable<MahjongTile> tiles)
        {
            MoveHistoryRecord record = new MoveHistoryRecord { actionName = actionName };
            foreach (MahjongTile tile in tiles)
            {
                if (tile == null)
                {
                    continue;
                }

                record.snapshots.Add(new TileStateSnapshot
                {
                    tileId = tile.TileId,
                    matchId = tile.MatchId,
                    gridCoordinate = tile.GridCoordinate,
                    localPosition = tile.BoardLocalPosition,
                    localEulerAngles = tile.BoardLocalEulerAngles,
                    state = tile.State,
                    isBufferedSelection = tile.IsBufferedSelection,
                    isFaceDown = tile.IsFaceDown,
                    fillTexture = tile.FillTexture,
                });
            }

            return record;
        }

        /// <summary>
        /// Captures the pre-tray board state for a single tile selection.
        /// </summary>
        private MoveHistoryRecord CreateSelectionSnapshotRecord(MahjongTile tile, bool isMemoryRevealSelection = false)
        {
            MoveHistoryRecord record = CreateSnapshotRecord(SelectHistoryActionName, new[] { tile });
            for (int index = 0; index < record.snapshots.Count; index++)
            {
                record.snapshots[index].state = TileState.Visible;
                record.snapshots[index].isBufferedSelection = false;
            }

            return record;
        }

        private MoveHistoryRecord CreateMatchSnapshotRecord(MahjongTile trayTile, MahjongTile boardTile)
        {
            MoveHistoryRecord record = CreateSnapshotRecord(MatchHistoryActionName, new[] { trayTile, boardTile });
            for (int index = 0; index < record.snapshots.Count; index++)
            {
                TileStateSnapshot snapshot = record.snapshots[index];
                if (boardTile != null && snapshot.tileId == boardTile.TileId)
                {
                    snapshot.state = TileState.Visible;
                    snapshot.isBufferedSelection = false;
                    record.snapshots[index] = snapshot;
                }
            }

            return record;
        }

        /// <summary>
        /// Restores a previously captured history record.
        /// </summary>
        private void RestoreHistoryRecord(MoveHistoryRecord record)
        {
            if (record == null || record.snapshots == null || record.snapshots.Count == 0)
            {
                return;
            }

            LevelManager levelManager = GetLevelManager();
            TileManager tileManager = GetTileManager();
            if (levelManager?.ActiveGrid == null || tileManager == null)
            {
                return;
            }

            selectedTiles.Clear();

            for (int index = 0; index < record.snapshots.Count; index++)
            {
                TileStateSnapshot snapshot = record.snapshots[index];
                levelManager.ActiveGrid.RemoveTile(snapshot.tileId);
            }

            for (int index = 0; index < record.snapshots.Count; index++)
            {
                TileStateSnapshot snapshot = record.snapshots[index];
                if (!tileManager.TryGetTile(snapshot.tileId, out MahjongTile tile) || tile == null)
                {
                    continue;
                }

                tile.SetMatchId(snapshot.matchId);
                tile.SetupFillTexture(snapshot.fillTexture);
                tile.SetGridCoordinate(snapshot.gridCoordinate);
                tile.StopFaceFlipAnimation(false);
                bool restoreFaceDown = snapshot.isFaceDown;
                tile.SetFaceDown(restoreFaceDown, true);

                if (snapshot.isBufferedSelection)
                {
                    tile.Restore(true);
                    tile.SetBufferedSelection(true);
                    tile.DetachFromBoardParent();
                    if (snapshot.state == TileState.Selected)
                    {
                        tile.TrySelect();
                    }

                    if (tile.TileCollider != null)
                    {
                        tile.TileCollider.enabled = false;
                    }

                    selectedTiles.Add(tile);
                    continue;
                }

                tile.SetBufferedSelection(false);
                tile.RestoreBoardParent();
                tile.SetLocalPose(snapshot.localPosition, snapshot.localEulerAngles);
                tile.Restore(snapshot.state != TileState.Hidden);
                if (snapshot.state == TileState.Selected)
                {
                    tile.TrySelect();
                }

                if (snapshot.state != TileState.Hidden && snapshot.state != TileState.Removed && snapshot.state != TileState.Matched)
                {
                    levelManager.ActiveGrid.TryPlaceTile(snapshot.tileId, snapshot.gridCoordinate);
                }

                if (tile.TileCollider != null)
                {
                    tile.TileCollider.enabled = !tile.IsBufferedSelection && !tile.IsRemoved && !tile.IsMatched;
                }
            }

            ReflowSelectionTray();
        }

        /// <summary>
        /// Restores the most recently buffered tray tile back to its original board slot.
        /// </summary>
        private void RestoreSelectionRecord(MoveHistoryRecord record)
        {
            if (record == null || record.snapshots == null || record.snapshots.Count == 0)
            {
                return;
            }

            LevelManager levelManager = GetLevelManager();
            TileManager tileManager = GetTileManager();
            if (levelManager?.ActiveGrid == null || tileManager == null)
            {
                return;
            }

            for (int index = 0; index < record.snapshots.Count; index++)
            {
                TileStateSnapshot snapshot = record.snapshots[index];
                if (!tileManager.TryGetTile(snapshot.tileId, out MahjongTile tile) || tile == null)
                {
                    continue;
                }

                GetAnimationManager()?.ClearTrayTile(tile);
                RemoveTrayTileReference(tile);
                levelManager.ActiveGrid.RemoveTile(snapshot.tileId);

                tile.SetMatchId(snapshot.matchId);
                tile.SetupFillTexture(snapshot.fillTexture);
                tile.SetGridCoordinate(snapshot.gridCoordinate);
                tile.SetBufferedSelection(false);
                tile.RestoreBoardParent();
                tile.SetLocalPose(snapshot.localPosition, snapshot.localEulerAngles);
                tile.Restore(snapshot.state != TileState.Hidden);
                tile.StopFaceFlipAnimation(false);
                bool restoreFaceDown = snapshot.isFaceDown;
                tile.SetFaceDown(restoreFaceDown, true);
                tile.Deselect();

                if (snapshot.state != TileState.Hidden && snapshot.state != TileState.Removed && snapshot.state != TileState.Matched)
                {
                    levelManager.ActiveGrid.TryPlaceTile(snapshot.tileId, snapshot.gridCoordinate);
                }

                if (tile.TileCollider != null)
                {
                    tile.TileCollider.enabled = !tile.IsRemoved && !tile.IsMatched;
                }

                GetAnimationManager()?.SetTrayBoardTileVisible(tile, true);
            }

            ReflowSelectionTray();
        }

        private void RestoreMemorySelectionRecord(MoveHistoryRecord record)
        {
            TileManager tileManager = GetTileManager();
            if (tileManager == null)
            {
                return;
            }

            CancelMemoryRevealTimeout();

            for (int index = 0; index < record.snapshots.Count; index++)
            {
                TileStateSnapshot snapshot = record.snapshots[index];
                if (!tileManager.TryGetTile(snapshot.tileId, out MahjongTile tile) || tile == null)
                {
                    continue;
                }

                GetAnimationManager()?.ClearTrayTile(tile);
                RemoveTrayTileReference(tile);
                tile.StopFaceFlipAnimation(false);
                bool restoreFaceDown = snapshot.isFaceDown;
                tile.SetFaceDown(restoreFaceDown, true);
                tile.Restore(snapshot.state != TileState.Hidden);
                tile.Deselect();
            }

            isMemoryRevealLocked = false;
            memorySelectedTiles.Clear();
            GetTileManager()?.RefreshTileExposure();
        }

        /// <summary>
        /// Moves a tapped tile into the temporary 4-slot tray and resolves a match when possible.
        /// </summary>
        private IEnumerator MoveTileIntoSelectionTray(MahjongTile tappedTile, MahjongTile matchingTrayTile, int targetTraySlotIndex = -1)
        {
            if (tappedTile == null)
            {
                yield break;
            }

            movingToSelectionTrayTiles.Add(tappedTile);
            try
            {
                history.Push(matchingTrayTile != null
                    ? CreateMatchSnapshotRecord(matchingTrayTile, tappedTile)
                    : CreateSelectionSnapshotRecord(tappedTile));

                if (tappedTile.IsFaceDown)
                {
                    bool flipCompleted = false;
                    GetAudioManager()?.PlayFlipTile();
                    tappedTile.FlipFaceUp(() => flipCompleted = true);

                    float flipElapsed = 0f;
                    while (!flipCompleted && flipElapsed < AnimationCompletionTimeoutSeconds)
                    {
                        flipElapsed += GetResolutionDeltaTime();
                        yield return null;
                    }
                }

                CommitTileToSelectionTray(tappedTile);

                int targetSlotIndex = targetTraySlotIndex >= 0
                    ? Mathf.Clamp(targetTraySlotIndex, 0, SelectionTrayCapacity - 1)
                    : Mathf.Clamp(selectedTiles.Count, 0, SelectionTrayCapacity - 1);

                if (matchingTrayTile != null && selectedTiles.Count < SelectionTrayCapacity)
                {
                    int matchSlotIndex = selectedTiles.IndexOf(matchingTrayTile);
                    if (matchSlotIndex >= 0)
                    {
                        int insertionIndex = Mathf.Clamp(matchSlotIndex + 1, 0, selectedTiles.Count);
                        selectedTiles.Insert(insertionIndex, tappedTile);
                        targetSlotIndex = insertionIndex;
                        ReflowSelectionTrayFromIndex(insertionIndex + 1);
                    }
                    else
                    {
                        matchingTrayTile = null;
                        selectedTiles.Add(tappedTile);
                        targetSlotIndex = selectedTiles.Count - 1;
                    }
                }
                else if (matchingTrayTile == null)
                {
                    selectedTiles.Add(tappedTile);
                    targetSlotIndex = selectedTiles.Count - 1;
                }

                GetAnimationManager()?.ShowMatchingTraySlot(targetSlotIndex);
                GetAudioManager()?.PlaySelect();

                bool completed = false;
                AnimationManager animationManager = GetAnimationManager();
                animationManager?.HideTrayCapacityWarning();
                if (matchingTrayTile != null)
                {
                    animationManager?.StopTrayShake();
                }

                if (animationManager != null)
                {
                    animationManager.PlayMoveToTray(tappedTile, targetSlotIndex, () => completed = true);
                }
                else
                {
                    completed = true;
                }

                float elapsed = 0f;
                while (!completed && elapsed < AnimationCompletionTimeoutSeconds)
                {
                    elapsed += GetResolutionDeltaTime();
                    yield return null;
                }

                if (!completed)
                {
                    MahjongRuntimeLogger.LogWarning($"Tray animation timed out for tile {tappedTile.TileId}. Snapping to slot.");
                    if (!animationManager?.SnapTrayPreviewToSlot(tappedTile, targetSlotIndex) ?? false)
                    {
                        animationManager?.SetTrayBoardTileVisible(tappedTile, true);
                    }
                }

                animationManager?.HideMatchingTraySlot();
                animationManager?.SetTrayBoardTileVisible(tappedTile, false);
                movingToSelectionTrayTiles.Remove(tappedTile);

                if (matchingTrayTile != null)
                {
                    // The tray can change while the tile is travelling to it. Resolve the
                    // current tray reference again before applying the capacity failure rule.
                    MahjongTile currentMatchingTrayTile = FindMatchingTrayTile(tappedTile);
                    if (currentMatchingTrayTile != null)
                    {
                        matchingTrayTile = currentMatchingTrayTile;
                    }

                    if (TryReservePairForMatchResolution(matchingTrayTile, tappedTile))
                    {
                        StartOrQueueMatchedPairResolution(matchingTrayTile, tappedTile, true);
                        yield break;
                    }

                    // A pair may already be reserved by another queued selection. Do not
                    // turn that transient state into a false full-tray loss.
                    if (IsSelectionResolutionBusy())
                    {
                        yield break;
                    }
                }

                animationManager?.PlayTrayOccupancyFeedback(selectedTiles.Count, false);

                if (selectedTiles.Count >= SelectionTrayCapacity)
                {
                    if (IsSelectionResolutionBusy())
                    {
                        yield break;
                    }

                    if (!TryStartFirstMatchingPairInSelectionTray(true))
                    {
                        float wait = 0f;
                        float timeout = 0.25f;
                        bool started = false;
                        while (wait < timeout)
                        {
                            wait += GetResolutionDeltaTime();
                            yield return null;
                            if (TryStartFirstMatchingPairInSelectionTray(true))
                            {
                                started = true;
                                break;
                            }
                        }

                        if (!started)
                        {
                            TriggerLoseWithDebug("TrayFull_ProcessPendingSelectionActions");
                            yield break;
                        }
                    }
                    else
                    {
                        yield break;
                    }
                }
            }
            finally
            {
                GetAnimationManager()?.HideMatchingTraySlot();
                movingToSelectionTrayTiles.Remove(tappedTile);
            }
        }

        private bool TryReservePairForMatchResolution(MahjongTile firstTile, MahjongTile secondTile)
        {
            if (!IsMatchReservationValid(firstTile) || !IsMatchReservationValid(secondTile) || firstTile == secondTile)
            {
                return false;
            }

            ReservePairForMatchResolution(firstTile, secondTile);
            return true;
        }

        private bool IsMatchReservationValid(MahjongTile tile)
        {
            return tile != null
                && !tile.IsRemoved
                && !tile.IsMatched
                && !pendingMatchedTiles.Contains(tile);
        }

        private bool IsPendingMatchResolutionValid(PendingMatchResolution pendingResolution)
        {
            return pendingResolution.First != null
                && pendingResolution.Second != null
                && !pendingResolution.First.IsRemoved
                && !pendingResolution.Second.IsRemoved
                && pendingMatchedTiles.Contains(pendingResolution.First)
                && pendingMatchedTiles.Contains(pendingResolution.Second);
        }


        private IEnumerator MoveAutoMatchedPairIntoTray(
            MahjongTile firstTile,
            MahjongTile secondTile,
            AnimationManager animationManager)
        {
            if (firstTile == null || secondTile == null || animationManager == null)
            {
                yield break;
            }

            int firstSlotIndex = Mathf.Clamp(selectedTiles.Count, 0, SelectionTrayCapacity - 2);
            int secondSlotIndex = firstSlotIndex + 1;
            bool firstCompleted = false;
            bool secondCompleted = false;

            animationManager.HideTrayCapacityWarning();
            animationManager.StopTrayShake();
            animationManager.PlayMoveToTray(firstTile, firstSlotIndex, () => firstCompleted = true);
            animationManager.PlayMoveToTray(secondTile, secondSlotIndex, () => secondCompleted = true);

            float elapsed = 0f;
            while ((!firstCompleted || !secondCompleted) && elapsed < AnimationCompletionTimeoutSeconds)
            {
                elapsed += GetResolutionDeltaTime();
                yield return null;
            }

            if (!firstCompleted || !secondCompleted)
            {
                MahjongRuntimeLogger.LogWarning("Combo auto-match tray animation timed out. Continuing with match animation.");
            }

            animationManager.SetTrayBoardTileVisible(firstTile, false);
            animationManager.SetTrayBoardTileVisible(secondTile, false);
        }

        private void QueueMatchedPairForResolution(
            MahjongTile firstTile,
            MahjongTile secondTile,
            bool rewardCoins,
            bool moveToTrayBeforeMatch = false)
        {
            if (!TryReservePairForMatchResolution(firstTile, secondTile))
            {
                return;
            }

            StartOrQueueMatchedPairResolution(firstTile, secondTile, rewardCoins, moveToTrayBeforeMatch);
        }

        private void StartOrQueueMatchedPairResolution(
            MahjongTile firstTile,
            MahjongTile secondTile,
            bool rewardCoins,
            bool moveToTrayBeforeMatch = false)
        {
            if (firstTile == null || secondTile == null)
            {
                return;
            }

            PendingMatchResolution pendingResolution = new PendingMatchResolution(firstTile, secondTile, rewardCoins, moveToTrayBeforeMatch);
            if (IsResolvingMatch)
            {
                pendingMatchQueue.Enqueue(pendingResolution);
                return;
            }

            StartCoroutine(ResolveMatchedPair(firstTile, secondTile, rewardCoins, moveToTrayBeforeMatch));
        }

        private void ReservePairForMatchResolution(MahjongTile firstTile, MahjongTile secondTile)
        {
            if (firstTile == null || secondTile == null)
            {
                return;
            }

            GetAnimationManager()?.StopTrayShake();
            pendingMatchedTiles.Add(firstTile);
            pendingMatchedTiles.Add(secondTile);
            ClearMemorySelectionOriginalFaceState(firstTile);
            ClearMemorySelectionOriginalFaceState(secondTile);

            RemoveTrayTileReference(firstTile);
            RemoveTrayTileReference(secondTile);
            firstTile.Deselect();
            secondTile.Deselect();

            if (!firstTile.IsBufferedSelection && firstTile.TileCollider != null)
            {
                firstTile.TileCollider.enabled = false;
            }

            if (!secondTile.IsBufferedSelection && secondTile.TileCollider != null)
            {
                secondTile.TileCollider.enabled = false;
            }

            ReflowSelectionTray();
        }

        private bool TryStartNextQueuedMatch()
        {
            while (pendingMatchQueue.Count > 0)
            {
                PendingMatchResolution pendingResolution = pendingMatchQueue.Dequeue();
                if (!IsPendingMatchResolutionValid(pendingResolution))
                {
                    pendingMatchedTiles.Remove(pendingResolution.First);
                    pendingMatchedTiles.Remove(pendingResolution.Second);
                    continue;
                }

                StartCoroutine(ResolveMatchedPair(
                    pendingResolution.First,
                    pendingResolution.Second,
                    pendingResolution.RewardCoins,
                    pendingResolution.MoveToTrayBeforeMatch));
                return true;
            }

            return false;
        }

        public bool IsTilePendingMatch(MahjongTile tile)
        {
            return tile != null && pendingMatchedTiles.Contains(tile);
        }

        private bool IsTileMovingToSelectionTray(MahjongTile tile)
        {
            return tile != null && movingToSelectionTrayTiles.Contains(tile);
        }

        private bool IsSelectionResolutionBusy()
        {
            return IsResolvingMatch
                || pendingMatchedTiles.Count > 0
                || pendingMatchQueue.Count > 0
                || movingToSelectionTrayTiles.Count > 0;
        }

        private bool TryPopLatestSelectionRecord(out MoveHistoryRecord record)
        {
            record = null;
            if (history.Count == 0)
            {
                return false;
            }

            Stack<MoveHistoryRecord> skippedRecords = new Stack<MoveHistoryRecord>();
            while (history.Count > 0)
            {
                MoveHistoryRecord candidate = history.Pop();
                if (IsUndoableHistoryRecord(candidate))
                {
                    record = candidate;
                    break;
                }

                skippedRecords.Push(candidate);
            }

            while (skippedRecords.Count > 0)
            {
                history.Push(skippedRecords.Pop());
            }

            return record != null;
        }

        private bool HasSelectionHistoryRecord()
        {
            foreach (MoveHistoryRecord record in history)
            {
                if (IsUndoableHistoryRecord(record))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsUndoableHistoryRecord(MoveHistoryRecord record)
        {
            return record != null
                && (record.actionName == SelectHistoryActionName || record.actionName == MatchHistoryActionName);
        }

        private MahjongTile FindMatchingPreviewTile(MahjongTile sourceTile)
        {
            if (sourceTile == null)
            {
                return null;
            }

            for (int index = 0; index < memorySelectedTiles.Count; index++)
            {
                MahjongTile tile = memorySelectedTiles[index];
                if (tile == null || tile == sourceTile || tile.IsRemoved || tile.IsMatched)
                {
                    continue;
                }

                if (tile.HasSameVisualIdentity(sourceTile))
                {
                    return tile;
                }
            }

            return null;
        }

        private bool ShouldRestoreMemoryTileFaceDown(MahjongTile tile)
        {
            return tile != null
                && memorySelectionOriginalFaceStates.TryGetValue(tile, out bool wasFaceDown)
                && wasFaceDown;
        }

        private void ClearMemorySelectionOriginalFaceState(MahjongTile tile)
        {
            if (tile != null)
            {
                memorySelectionOriginalFaceStates.Remove(tile);
            }
        }

        /// <summary>
        /// Removes a tile from the board grid and marks it as buffered inside the temporary selection tray.
        /// </summary>
        private void CommitTileToSelectionTray(MahjongTile tile)
        {
            if (tile == null)
            {
                return;
            }

            tile.SetBufferedSelection(true);
            tile.DetachFromBoardParent();
            LevelManager levelManager = GetLevelManager();
            if (levelManager?.ActiveGrid != null)
            {
                levelManager.ActiveGrid.RemoveTile(tile.TileId);
            }

            if (tile.TileCollider != null)
            {
                tile.TileCollider.enabled = false;
            }

            GetTileManager()?.RefreshTileExposure();
        }

        /// <summary>
        /// Restores a buffered tray tile back onto the board grid.
        /// </summary>
        private void RestoreTileFromSelectionTray(MahjongTile tile, LevelManager levelManager)
        {
            if (tile == null)
            {
                return;
            }

            tile.SetBufferedSelection(false);
            tile.RestoreBoardParent();
            tile.RestoreBoardPose();
            if (levelManager?.ActiveGrid != null)
            {
                levelManager.ActiveGrid.TryPlaceTile(tile.TileId, tile.GridCoordinate);
            }

            tile.Deselect();
        }

        /// <summary>
        /// Finds an already buffered tray tile with the same visible image.
        /// </summary>
        private MahjongTile FindMatchingTrayTile(MahjongTile sourceTile)
        {
            if (sourceTile == null)
            {
                return null;
            }

            for (int index = 0; index < selectedTiles.Count; index++)
            {
                MahjongTile tile = selectedTiles[index];
                if (IsAvailableTrayMatchCandidate(tile) && tile.HasSameVisualIdentity(sourceTile))
                {
                    return tile;
                }
            }

            return null;
        }

        private bool TryStartFirstMatchingPairInSelectionTray(bool rewardCoins)
        {
            for (int firstIndex = 0; firstIndex < selectedTiles.Count; firstIndex++)
            {
                MahjongTile firstTile = selectedTiles[firstIndex];
                if (!IsAvailableTrayMatchCandidate(firstTile))
                {
                    continue;
                }

                for (int secondIndex = firstIndex + 1; secondIndex < selectedTiles.Count; secondIndex++)
                {
                    MahjongTile secondTile = selectedTiles[secondIndex];
                    if (IsAvailableTrayMatchCandidate(secondTile) && firstTile.HasSameVisualIdentity(secondTile))
                    {
                        QueueMatchedPairForResolution(firstTile, secondTile, rewardCoins);
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsAvailableTrayMatchCandidate(MahjongTile tile)
        {
            return tile != null
                && !tile.IsRemoved
                && !tile.IsMatched
                && !IsTilePendingMatch(tile);
        }

        /// <summary>
        /// Removes a matched tile reference from the tray list when present.
        /// </summary>
        private void RemoveTrayTileReference(MahjongTile tile)
        {
            if (tile != null)
            {
                selectedTiles.Remove(tile);
            }
        }

        /// <summary>
        /// Repositions the remaining buffered tray tiles into compact slot order.
        /// </summary>
        private void ReflowSelectionTray()
        {
            ReflowSelectionTrayFromIndex(0);
        }

        private void ReflowSelectionTrayFromIndex(int startIndex)
        {
            AnimationManager animationManager = GetAnimationManager();
            if (animationManager == null)
            {
                return;
            }

            int clampedStartIndex = Mathf.Clamp(startIndex, 0, selectedTiles.Count);
            for (int index = clampedStartIndex; index < selectedTiles.Count; index++)
            {
                MahjongTile tile = selectedTiles[index];
                if (tile != null)
                {
                    animationManager.SnapToTray(tile, index);
                }
            }
        }

        /// <summary>
        /// Randomizes remaining pair identities while preserving the current block layout.
        /// </summary>
        private static void ShuffleTileMatches(List<MahjongTile> tiles)
        {
            if (tiles == null || tiles.Count <= 1)
            {
                return;
            }

            int nextMatchId = 0;
            for (int index = 0; index < tiles.Count; index++)
            {
                MahjongTile tile = tiles[index];
                if (tile != null)
                {
                    nextMatchId = Mathf.Max(nextMatchId, tile.MatchId + 1);
                }
            }

            Dictionary<string, List<MahjongTile>> tilesByVisualKey = new Dictionary<string, List<MahjongTile>>();
            for (int index = 0; index < tiles.Count; index++)
            {
                MahjongTile tile = tiles[index];
                if (tile == null)
                {
                    continue;
                }

                string visualKey = tile.VisualMatchKey;
                if (!tilesByVisualKey.TryGetValue(visualKey, out List<MahjongTile> pairTiles))
                {
                    pairTiles = new List<MahjongTile>();
                    tilesByVisualKey.Add(visualKey, pairTiles);
                }

                pairTiles.Add(tile);
            }

            List<PairShuffleGroup> groups = new List<PairShuffleGroup>(tilesByVisualKey.Count);
            foreach (KeyValuePair<string, List<MahjongTile>> pair in tilesByVisualKey)
            {
                if (pair.Value == null || pair.Value.Count == 0)
                {
                    continue;
                }

                groups.Add(new PairShuffleGroup
                {
                    MatchId = pair.Value[0].MatchId,
                    VisualKey = pair.Key,
                    FillTexture = pair.Value[0].FillTexture,
                    Tiles = pair.Value,
                });
            }

            List<PairShuffleGroup> targetGroups = new List<PairShuffleGroup>(groups);
            List<PairShuffleGroup> sourceGroups = new List<PairShuffleGroup>(groups);

            for (int index = sourceGroups.Count - 1; index > 0; index--)
            {
                int swapIndex = Random.Range(0, index + 1);
                (sourceGroups[index], sourceGroups[swapIndex]) = (sourceGroups[swapIndex], sourceGroups[index]);
            }

            for (int index = 0; index < targetGroups.Count; index++)
            {
                PairShuffleGroup targetGroup = targetGroups[index];
                PairShuffleGroup sourceGroup = sourceGroups[index];
                if (targetGroup.Tiles == null)
                {
                    continue;
                }

                for (int tileIndex = 0; tileIndex < targetGroup.Tiles.Count; tileIndex++)
                {
                    MahjongTile tile = targetGroup.Tiles[tileIndex];
                    if (tile == null)
                    {
                        continue;
                    }

                    tile.SetMatchId(nextMatchId + (tileIndex / 2));
                    tile.SetupFillTexture(sourceGroup.FillTexture);
                }

                nextMatchId += Mathf.Max(1, (targetGroup.Tiles.Count + 1) / 2);
            }
        }

        private sealed class PairShuffleGroup
        {
            public int MatchId { get; set; }

            public string VisualKey { get; set; }

            public Texture2D FillTexture { get; set; }

            public List<MahjongTile> Tiles { get; set; }
        }

        private readonly struct PendingMatchResolution
        {
            public PendingMatchResolution(
                MahjongTile first,
                MahjongTile second,
                bool rewardCoins,
                bool moveToTrayBeforeMatch)
            {
                First = first;
                Second = second;
                RewardCoins = rewardCoins;
                MoveToTrayBeforeMatch = moveToTrayBeforeMatch;
            }

            public MahjongTile First { get; }

            public MahjongTile Second { get; }

            public bool RewardCoins { get; }

            public bool MoveToTrayBeforeMatch { get; }
        }

        /// <summary>
        /// Consumes one saved power-up booster when possible.
        /// </summary>
        private bool TryUsePowerUp(PowerUpType powerUpType)
        {
            return GetSaveManager()?.TrySpendPowerUp(powerUpType) ?? false;
        }

        private int GetMatchScoreValue(MahjongTile firstTile, MahjongTile secondTile)
        {
            int baseScore = 10;
            if ((firstTile != null && firstTile.IsComboTile) || (secondTile != null && secondTile.IsComboTile))
            {
                baseScore += 15;
            }

            return baseScore;
        }

        private int GetCoinsPerMatch() => powerUpSettings != null ? powerUpSettings.CoinsPerMatch : 2;
        private int GetCoinsPerLevelWin() => powerUpSettings != null ? powerUpSettings.CoinsPerLevelWin : 30;
        private float GetXRayDurationSeconds() => 5f;
        private int GetXRayRevealDepth() => 1;

        private TileManager GetTileManager() => Context.Services.Get<TileManager>();
        private LevelManager GetLevelManager() => Context.Services.Get<LevelManager>();
        private GameManager GetGameManager() => GameManager.Instance;
        private AnimationManager GetAnimationManager() => Context.Services.Get<AnimationManager>();
        private AudioManager GetAudioManager() => Context.Services.Get<AudioManager>();
        private SaveManager GetSaveManager() => Context.Services.Get<SaveManager>();

        private void TriggerLoseWithDebug(string reason)
        {
            MahjongRuntimeLogger.LogWarning($"[MatchManager] TriggerLose (requested): {reason} Selected={selectedTiles.Count} Moving={movingToSelectionTrayTiles.Count} PendingMatched={pendingMatchedTiles.Count} PendingQueue={pendingMatchQueue.Count} IsResolving={IsResolvingMatch} TotalTiles={totalTiles}");
            if (!IsInvoking(nameof(ConfirmAndTriggerLose)))
            {
                StartCoroutine(ConfirmAndTriggerLose(reason));
            }
        }

        private IEnumerator ConfirmAndTriggerLose(string reason)
        {
            float wait = 0f;
            float timeout = 0.25f;
            while (wait < timeout)
            {
                wait += GetResolutionDeltaTime();
                yield return null;
                // If resolution is busy, abort early — let resolution finish.
                if (IsSelectionResolutionBusy())
                {
                    MahjongRuntimeLogger.Log($"[MatchManager] AbortLose: resolution busy during confirmation ({reason})");
                    yield break;
                }
            }

            // Re-evaluate: lose if tray still full without resolution, or if no board pairs remain.
            if ((selectedTiles.Count >= SelectionTrayCapacity && !IsSelectionResolutionBusy())
                || (!TryFindAnyBoardPair(out _, out _) && !IsSelectionResolutionBusy()))
            {
                MahjongRuntimeLogger.LogWarning($"[MatchManager] Confirmed Lose: {reason} Selected={selectedTiles.Count} Moving={movingToSelectionTrayTiles.Count} PendingMatched={pendingMatchedTiles.Count} PendingQueue={pendingMatchQueue.Count} IsResolving={IsResolvingMatch} TotalTiles={totalTiles}");
                GetAudioManager()?.PlayLose();
                GetGameManager()?.LoseGameplay();
            }
            else
            {
                MahjongRuntimeLogger.Log($"[MatchManager] Lose aborted after confirmation: {reason}");
            }
        }
    }
}
