using System.Collections;
using MahjongOut3D.Core;
using MahjongOut3D.LevelSystem;
using UnityEngine;

namespace MahjongOut3D.Managers
{
    /// <summary>
    /// Tracks level selection state and owns the active runtime voxel grid.
    /// </summary>
    public sealed class LevelManager : ManagerBehaviour
    {
        [SerializeField] private LevelCatalog levelCatalog;
        [SerializeField] private VoxelGridLayoutSettings defaultGridLayout;

        [Header("Debug")]
        [SerializeField, Min(0)] private int inspectorLevelIndex;

        private VoxelGridData activeGrid;

        /// <summary>
        /// Gets the currently active level definition asset.
        /// </summary>
        public LevelDefinition ActiveLevelDefinition { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the active level uses surface-wrapped tile placement.
        /// </summary>
        public bool ActiveUsesSurfaceTilePlacement { get; private set; }

        /// <summary>
        /// Gets the currently selected level index.
        /// </summary>
        public int CurrentLevelIndex { get; private set; } = -1;

        /// <summary>
        /// Gets the currently active voxel grid.
        /// </summary>
        public VoxelGridData ActiveGrid => activeGrid;

        /// <summary>
        /// Gets the default layout used when creating new grids.
        /// </summary>
        public VoxelGridLayoutSettings DefaultGridLayout => defaultGridLayout;

        /// <summary>
        /// Gets the runtime level catalog used for level selection and progression.
        /// </summary>
        public LevelCatalog LevelCatalog => levelCatalog;

        /// <summary>
        /// Gets or sets the level index shown in the inspector debug tools.
        /// </summary>
        public int InspectorLevelIndex
        {
            get => Mathf.Max(0, inspectorLevelIndex);
            set => inspectorLevelIndex = Mathf.Max(0, value);
        }

        /// <summary>
        /// Gets the bootstrap order for the level manager.
        /// </summary>
        public override int InitializationOrder => 10;

        /// <summary>
        /// Applies the default level index from project settings.
        /// </summary>
        protected override void OnInitialize()
        {
            CurrentLevelIndex = Mathf.Max(0, IPlayerInfoController.Instance.CurrentLevel() - 1);
        }

        /// <summary>
        /// Clears the active grid when the manager shuts down.
        /// </summary>
        protected override void OnShutdown()
        {
            SetActiveGrid(null);
            SetActiveLevelDefinition(null, false);
        }

        /// <summary>
        /// Updates the currently selected level index.
        /// </summary>
        /// <param name="levelIndex">New selected level index.</param>
        public void SetCurrentLevel(int levelIndex)
        {
            CurrentLevelIndex = levelIndex;
        }

        /// <summary>
        /// Loads the current level index from the configured level catalog.
        /// </summary>
        /// <returns>True when the level was loaded; otherwise false.</returns>
        public bool LoadCurrentLevel()
        {
            Debug.Log($"[Mahjong] LevelManager.LoadCurrentLevel -> index {CurrentLevelIndex}");
            return LoadLevel(CurrentLevelIndex);
        }

        /// <summary>
        /// Loads the specified level index from the configured level catalog.
        /// </summary>
        /// <param name="levelIndex">Zero-based level index to load.</param>
        /// <returns>True when the level was loaded; otherwise false.</returns>
        public bool LoadLevel(int levelIndex)
        {
            if (levelCatalog == null || !levelCatalog.TryGetLevel(levelIndex, out LevelDefinition definition))
            {
                Debug.LogWarning($"[Mahjong] LoadLevel failed. Invalid catalog or missing level at index {levelIndex}.");
                return false;
            }

            if (!Context.Services.TryGet(out VoxelLevelGenerator generator))
            {
                Debug.LogWarning($"[Mahjong] LoadLevel failed. Missing VoxelLevelGenerator for level index {levelIndex}.");
                return false;
            }

            if (Context.Services.TryGet(out MatchManager matchManager))
            {
                matchManager.PrepareForLevelReload();
            }

            if (Context.Services.TryGet(out AnimationManager animationManager))
            {
                animationManager.CancelTransientAnimations();
            }

            SetCurrentLevel(levelIndex);
            if (GameplayManager.Instance != null)
            {
                GameplayManager.Instance.CurrentLevel = levelIndex + 1;
            }
            SetActiveLevelDefinition(definition, definition != null && definition.UseSurfaceTilePlacement);
            Debug.Log($"[Mahjong] LoadLevel success path. Generating level '{definition.name}' at index {levelIndex}.");
            if (Context.Services.TryGet(out AudioManager audioManager))
            {
                audioManager.PlayTileAppear();
            }

            generator.GenerateFromDefinition(definition);
            return true;
        }

        /// <summary>
        /// Updates the active level metadata used by gameplay systems.
        /// </summary>
        /// <param name="definition">Active level definition, or null for ad-hoc generated content.</param>
        /// <param name="useSurfaceTilePlacement">Whether the current level uses surface-wrapped tile placement.</param>
        public void SetActiveLevelDefinition(LevelDefinition definition, bool useSurfaceTilePlacement)
        {
            ActiveLevelDefinition = definition;
            ActiveUsesSurfaceTilePlacement = useSurfaceTilePlacement;
        }

        /// <summary>
        /// Loads the inspector-selected level index for quick gameplay testing.
        /// </summary>
        /// <returns>True when the level was loaded; otherwise false.</returns>
        public bool LoadInspectorLevel()
        {
            return LoadLevel(InspectorLevelIndex);
        }

        /// <summary>
        /// Reloads the currently selected level.
        /// </summary>
        /// <returns>True when the level was reloaded; otherwise false.</returns>
        public bool ReloadCurrentLevel()
        {
            return LoadCurrentLevel();
        }

        /// <summary>
        /// Attempts to load the next level in the catalog.
        /// </summary>
        /// <returns>True when the next level was loaded; otherwise false.</returns>
        public bool LoadNextLevel()
        {
            Debug.Log($"[Mahjong] LevelManager.LoadNextLevel from {CurrentLevelIndex} to {CurrentLevelIndex + 1}");
            return LoadLevel(CurrentLevelIndex + 1);
        }

        /// <summary>
        /// Creates a new voxel grid using the specified size and layout.
        /// </summary>
        /// <param name="gridSize">Dimensions of the grid to create.</param>
        /// <param name="layoutOverride">Optional layout override.</param>
        /// <returns>Newly created voxel grid instance.</returns>
        public VoxelGridData CreateGrid(VoxelGridSize gridSize, VoxelGridLayoutSettings layoutOverride = null)
        {
            return new VoxelGridData(gridSize, layoutOverride != null ? layoutOverride : defaultGridLayout);
        }

        /// <summary>
        /// Sets the active voxel grid and republishes grid events through the shared event bus.
        /// </summary>
        /// <param name="newGrid">Grid to mark as active.</param>
        public void SetActiveGrid(VoxelGridData newGrid)
        {
            if (ReferenceEquals(activeGrid, newGrid))
            {
                return;
            }

            VoxelGridData previousGrid = activeGrid;
            if (activeGrid != null)
            {
                activeGrid.CellChanged -= HandleGridCellChanged;
            }

            activeGrid = newGrid;
            if (activeGrid != null)
            {
                activeGrid.CellChanged += HandleGridCellChanged;
            }

            Context?.EventBus.Publish(new ActiveVoxelGridChangedEvent(previousGrid, activeGrid));
        }

        /// <summary>
        /// Clears the current active voxel grid reference.
        /// </summary>
        public void ClearActiveGrid()
        {
            SetActiveGrid(null);
        }

        /// <summary>
        /// Attempts to get the tile id at the specified active-grid coordinate.
        /// </summary>
        /// <param name="coordinate">Coordinate to inspect.</param>
        /// <param name="tileId">Resolved tile id.</param>
        /// <returns>True when the active grid contains a tile there; otherwise false.</returns>
        public bool TryGetTileIdAt(Vector3Int coordinate, out int tileId)
        {
            tileId = -1;
            return activeGrid != null && activeGrid.TryGetTileId(coordinate, out tileId);
        }

        /// <summary>
        /// Converts an active-grid coordinate into a local position.
        /// </summary>
        /// <param name="coordinate">Coordinate to convert.</param>
        /// <returns>Local-space position for the coordinate.</returns>
        public Vector3 GetLocalPosition(Vector3Int coordinate)
        {
            return activeGrid != null ? activeGrid.GetLocalPosition(coordinate) : coordinate;
        }

        /// <summary>
        /// Gets the local-space bounds of the active grid.
        /// </summary>
        /// <returns>Bounds of the active grid, or an empty bounds when none exists.</returns>
        public Bounds GetActiveGridLocalBounds()
        {
            return activeGrid != null ? activeGrid.GetLocalBounds() : new Bounds(Vector3.zero, Vector3.zero);
        }

        /// <summary>
        /// Republishes cell occupancy changes from the active grid.
        /// </summary>
        /// <param name="eventData">Grid cell change payload.</param>
        private void HandleGridCellChanged(VoxelGridCellChangedEvent eventData)
        {
            Context.EventBus.Publish(eventData);
        }
        public  LevelDefinition GetLevelDefinition(int levelIndex)
        {
            if (levelCatalog == null || !levelCatalog.TryGetLevel(levelIndex, out LevelDefinition definition))
            {
                Debug.LogWarning($"[Mahjong] GetLevelDefinition failed. Invalid catalog or missing level at index {levelIndex}.");
                return null;
            }
            return definition;
        }
       
    }
}
