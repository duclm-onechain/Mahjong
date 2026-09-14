using System.Collections;
using System.Collections.Generic;
using MahjongOut3D.Core;
using MahjongOut3D.Data;
using MahjongOut3D.Managers;
using MahjongOut3D.TileSystem;
using MahjongOut3D.Utilities;
using MahjongOut3D;
using UnityEngine;
using UnityEngine.Rendering;

namespace MahjongOut3D.LevelSystem
{
    /// <summary>
    /// Spawns Mahjong tile instances from ScriptableObject, JSON or 3D array voxel sources.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VoxelLevelGenerator : MonoBehaviour
    {
        private sealed class MatchFillGroup
        {
            private readonly HashSet<int> shellIndices = new HashSet<int>();
            private readonly HashSet<int> runtimeMatchIds = new HashSet<int>();

            public MatchFillGroup(int matchId)
            {
                MatchId = matchId;
            }

            public int MatchId { get; }

            public int OuterShellIndex { get; private set; } = int.MaxValue;

            public int InnerShellIndex { get; private set; } = int.MinValue;

            public int ShellSpan => shellIndices.Count <= 1 ? 0 : InnerShellIndex - OuterShellIndex;

            public IEnumerable<int> ShellIndices => shellIndices;

            public IEnumerable<int> RuntimeMatchIds => runtimeMatchIds;

            public void RegisterShell(int shellIndex)
            {
                int normalizedShellIndex = Mathf.Max(0, shellIndex);
                shellIndices.Add(normalizedShellIndex);
                OuterShellIndex = Mathf.Min(OuterShellIndex, normalizedShellIndex);
                InnerShellIndex = Mathf.Max(InnerShellIndex, normalizedShellIndex);
            }

            public void RegisterRuntimeMatchId(int runtimeMatchId)
            {
                runtimeMatchIds.Add(runtimeMatchId);
            }
        }

        private const string DefaultFallbackVisualSourceName = "Bamboo_1";
        private static readonly Color[] DebugMatchPalette =
        {
            new Color(0.94f, 0.42f, 0.42f),
            new Color(0.31f, 0.76f, 0.93f),
            new Color(0.44f, 0.84f, 0.54f),
            new Color(0.97f, 0.73f, 0.32f),
            new Color(0.72f, 0.54f, 0.95f),
            new Color(0.96f, 0.47f, 0.75f),
            new Color(0.38f, 0.85f, 0.8f),
            new Color(0.84f, 0.84f, 0.36f),
        };

        [Header("References")]
        [SerializeField] private LevelDefinition levelDefinition;
        [SerializeField] private MahjongTile tilePrefab;
        [SerializeField] private Transform tileRoot;
        [SerializeField] private GameObject fallbackVisualSource;
        [SerializeField] private TileVisualSettings fallbackTileVisualSettings;
       // [SerializeField] private Material[] matchIndicatorMaterials;

        [Header("Tile Tuning")]
        [SerializeField] private bool applyTileBaseColor = true;
        [SerializeField] private Color tileBaseColor = Color.white;
        [Header("Cube Grid Spacing")]
        [Tooltip("Cube-only spacing scale applied to regular voxel-grid tile positions.")]
        [SerializeField] private Vector3 tileSpacingOffset;

        [Header("Cube Surface Spacing")]
        [Tooltip("Cube-only base spacing offset for surface-placement levels.")]
        [SerializeField] private Vector3 surfaceTileSpacingOffset;
        [Tooltip("Cube-only extra spacing for Left/Right faces on surface-placement levels.")]
        [SerializeField] private Vector3 leftRightSurfaceSpacingOffset;
        [Tooltip("Cube-only extra spacing for Up/Down faces on surface-placement levels.")]
        [SerializeField] private Vector3 upDownSurfaceSpacingOffset;
        [Tooltip("Cube-only extra spacing for Front/Back faces on surface-placement levels.")]
        [SerializeField] private Vector3 frontBackSurfaceSpacingOffset;
        [Tooltip("Cube-only local Z scale applied to Front/Back face tiles on surface-placement levels.")]
        [SerializeField, Range(0.01f, 1f)] private float frontBackSurfaceTileLocalZScale = 0.95f;
        [SerializeField, Range(0.5f, 1f)] private float surfaceShellSeparationScale = 0.8f;

        [Header("Generation")]
        [SerializeField] private bool generateOnStart;
        [SerializeField] private bool clearExistingChildrenOnGenerate = true;
        [SerializeField] private bool usePooling = true;
        [SerializeField] private bool generateIncrementally = true;
        [SerializeField, Min(1)] private int tilesPerFrame = 24;
        [SerializeField] private bool prewarmPoolForGeneration = true;
        [SerializeField, Min(1)] private int prewarmTilesPerFrame = 32;
        [SerializeField] private bool playAssembleOnLoad = true;
        [SerializeField, Min(0.05f)] private float assembleDurationSeconds = 0.45f;
        [SerializeField, Range(0f, 0.4f)] private float assembleMaxStaggerSeconds = 0.16f;
        [SerializeField, Min(0.1f)] private float assembleScatterRadius = 3.2f;
        [SerializeField, Min(0f)] private float assembleDepthJitter = 0.8f;
        [SerializeField, Min(1f)] private float cameraFramePaddingOnLoad = 1.7f;

        private readonly List<MahjongTile> spawnedTiles = new List<MahjongTile>();
        private readonly List<Transform> runtimeBlockRoots = new List<Transform>();
        private readonly Dictionary<int, Texture2D> fillTexturesByMatchId = new Dictionary<int, Texture2D>();
        private readonly List<Texture2D> activeLevelFillTextures = new List<Texture2D>();

        private readonly struct TileAssemblePose
        {
            public TileAssemblePose(MahjongTile tile, Vector3 startPosition, Quaternion startRotation, Vector3 endPosition, Quaternion endRotation, float delaySeconds)
            {
                Tile = tile;
                StartPosition = startPosition;
                StartRotation = startRotation;
                EndPosition = endPosition;
                EndRotation = endRotation;
                DelaySeconds = delaySeconds;
            }

            public MahjongTile Tile { get; }

            public Vector3 StartPosition { get; }

            public Quaternion StartRotation { get; }

            public Vector3 EndPosition { get; }

            public Quaternion EndRotation { get; }

            public float DelaySeconds { get; }
        }

        private ComponentPool<MahjongTile> tilePool;
        private GameContext context;
        private LevelManager levelManager;
        private TileManager tileManager;
        private CameraManager cameraManager;
        private ZoomSlider zoomSlider;
        private int nextTileId;
        private MahjongTile runtimeFallbackTilePrefab;
        private Quaternion defaultTileRootLocalRotation = Quaternion.identity;
        private Coroutine activeGenerationRoutine;
        public MahjongMaterialSO mahjongMaterialSO;
        private Material pieceBaseMaterial;
        private Texture2D pieceTexture;
        private bool activeCubeSurfaceTilePlacement;
        private bool isCurrentShapeCube;

        /// <summary>
        /// Initializes the generator with the shared runtime context.
        /// </summary>
        /// <param name="gameContext">Shared runtime context.</param>
        public void Initialize(GameContext gameContext)
        {
            context = gameContext;
            context.Services.Register(typeof(VoxelLevelGenerator), this);
            levelManager = context.Services.Get<LevelManager>();
            tileManager = context.Services.Get<TileManager>();
            context.Services.TryGet(out cameraManager);
            zoomSlider = FindFirstObjectByType<ZoomSlider>(FindObjectsInactive.Exclude);

            if (tileRoot == null)
            {
                tileRoot = transform;
            }

            defaultTileRootLocalRotation = tileRoot != null ? tileRoot.localRotation : Quaternion.identity;

            EnsureTileTemplate();

            if (tilePrefab != null && usePooling)
            {
                tilePool = new ComponentPool<MahjongTile>(tilePrefab);
            }
        }

        /// <summary>
        /// Generates the configured level definition once the scene starts.
        /// </summary>
        private void Start()
        {
            if (!generateOnStart || context == null || levelDefinition == null)
            {
                return;
            }

            //GenerateFromDefinition(levelDefinition);
        }

        /// <summary>
        /// Generates a level from a ScriptableObject definition.
        /// </summary>
        /// <param name="definition">Definition to generate from.</param>
        /// <returns>List of spawned tiles.</returns>
        public IReadOnlyList<MahjongTile> GenerateFromDefinition(LevelDefinition definition)
        {
            if (definition == null)
            {
                MahjongRuntimeLogger.LogWarning("VoxelLevelGenerator received a null LevelDefinition.");
                return spawnedTiles;
            }

            levelDefinition = definition;
            levelManager?.SetActiveLevelDefinition(definition, definition.UseSurfaceTilePlacement);
            isCurrentShapeCube = definition.Shape == LevelShapeType.Cube;
            activeCubeSurfaceTilePlacement = definition.UseSurfaceTilePlacement && isCurrentShapeCube;
            ConfigureFillTexturePool(definition.FillCategoryNames);

            VoxelGridSize singleBlockGridSize = definition.GetSingleBlockRuntimeGridSize();

            IList<LevelTileDefinition> singleBlockRuntimeTiles = BuildRuntimeTileDefinitions(
                definition.UseSurfaceTilePlacement,
                definition.Shape,
                definition.LayoutOverride,
                definition.Tiles);

            float blockStrideLocalX = ResolveBlockStrideLocalX(singleBlockGridSize, definition.LayoutOverride, singleBlockRuntimeTiles, definition.BlockSpacingCells);
            float blockStrideLocalY = ResolveBlockStrideLocalY(singleBlockGridSize, definition.LayoutOverride, singleBlockRuntimeTiles, definition.BlockSpacingCells);
            IList<LevelTileDefinition> runtimeTiles = ExpandRuntimeTileDefinitions(definition, singleBlockGridSize, definition.LayoutOverride, singleBlockRuntimeTiles);
            return Generate(definition.LevelName, definition.GetRuntimeGridSize(), definition.LayoutOverride, runtimeTiles, definition.BlockCount, blockStrideLocalX, blockStrideLocalY);
        }

        /// <summary>
        /// Generates a level from a JSON payload.
        /// </summary>
        /// <param name="json">JSON payload to parse.</param>
        /// <returns>List of spawned tiles.</returns>
        public IReadOnlyList<MahjongTile> GenerateFromJson(string json)
        {
            LevelJsonData jsonData = LevelJsonSerializer.FromJson(json);
            if (jsonData == null)
            {
                MahjongRuntimeLogger.LogWarning("VoxelLevelGenerator could not parse the provided level JSON.");
                return spawnedTiles;
            }

            VoxelGridSize gridSize = new VoxelGridSize(jsonData.width, jsonData.height, jsonData.depth);
            levelManager?.SetActiveLevelDefinition(null, jsonData.useSurfaceTilePlacement);
            isCurrentShapeCube = jsonData.shape == LevelShapeType.Cube;
            activeCubeSurfaceTilePlacement = jsonData.useSurfaceTilePlacement && isCurrentShapeCube;
            ConfigureFillTexturePool(jsonData.fillCategoryNames);
            IList<LevelTileDefinition> runtimeTiles = BuildRuntimeTileDefinitions(
                jsonData.useSurfaceTilePlacement,
                jsonData.shape,
                null,
                LevelJsonSerializer.ToTileDefinitions(jsonData));
            return Generate(jsonData.levelName, gridSize, null, runtimeTiles, 1, 0f, 0f);
        }
        /// <summary>
        /// Returns a random piece texture from the configured MahjongMaterialSO.
        /// The whole generated block shares one common piece texture.
        /// </summary>
        public Texture2D RandomPieceTexture()
        {
            if (mahjongMaterialSO == null)
            {
                return null;
            }

            List<Texture2D> resolvedPieceTextures = mahjongMaterialSO.GetActivePieceTextures();
            if (resolvedPieceTextures == null || resolvedPieceTextures.Count == 0)
            {
                return null;
            }

            int randomIndex = Random.Range(0, resolvedPieceTextures.Count);
            return resolvedPieceTextures[randomIndex];
        }

        /// <summary>
        /// Returns every currently active piece texture that can be previewed from the gameplay HUD.
        /// </summary>
        public IReadOnlyList<Texture2D> GetAvailablePieceTextures()
        {
            return mahjongMaterialSO != null ? mahjongMaterialSO.GetActivePieceTextures() : new List<Texture2D>();
        }

        /// <summary>
        /// Gets the piece texture currently applied to the active board.
        /// </summary>
        public Texture2D CurrentPieceTexture => pieceTexture;

        /// <summary>
        /// Applies a piece texture to every spawned tile so designers can preview it at runtime.
        /// </summary>
        public void ApplyPieceTexture(Texture2D texture)
        {
            pieceTexture = texture;

            for (int index = 0; index < spawnedTiles.Count; index++)
            {
                MahjongTile tile = spawnedTiles[index];
                if (tile != null)
                {
                    tile.SetupPieceTexture(texture);
                }
            }
        }

        private Texture2D GetFillTextureForMatch(int matchId)
        {
            if (activeLevelFillTextures.Count == 0)
            {
                return null;
            }

            if (fillTexturesByMatchId.TryGetValue(matchId, out Texture2D cachedTexture))
            {
                return cachedTexture;
            }

            int resolvedIndex = Mathf.Abs(matchId) % activeLevelFillTextures.Count;
            Texture2D resolvedTexture = activeLevelFillTextures[resolvedIndex];
            fillTexturesByMatchId[matchId] = resolvedTexture;
            return resolvedTexture;
        }
        
        /// <summary>
        /// Generates a level from a 3D array of match ids.
        /// </summary>
        /// <param name="matchIdGrid">3D array where -1 means empty and non-negative values are match ids.</param>
        /// <returns>List of spawned tiles.</returns>
        public IReadOnlyList<MahjongTile> GenerateFromMatchIdArray(int[,,] matchIdGrid)
        {
            if (matchIdGrid == null)
            {
                return spawnedTiles;
            }

            int width = matchIdGrid.GetLength(0);
            int height = matchIdGrid.GetLength(1);
            int depth = matchIdGrid.GetLength(2);

            List<LevelTileDefinition> tiles = new List<LevelTileDefinition>();
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        int matchId = matchIdGrid[x, y, z];
                        if (matchId < 0)
                        {
                            continue;
                        }

                        tiles.Add(new LevelTileDefinition
                        {
                            MatchId = matchId,
                            GridCoordinate = new Vector3Int(x, y, z),
                            LocalEulerAngles = Vector3.zero,
                        });
                    }
                }
            }

            levelManager?.SetActiveLevelDefinition(null, false);
            activeCubeSurfaceTilePlacement = false;
            isCurrentShapeCube = false;
            ConfigureFillTexturePool(null);
            return Generate("ArrayGeneratedLevel", new VoxelGridSize(width, height, depth), null, tiles, 1, 0f, 0f);
        }

        /// <summary>
        /// Generates a level from a 3D boolean voxel mask and auto-builds match pairs.
        /// </summary>
        /// <param name="occupancyMask">3D mask where true means the cell contains a tile.</param>
        /// <returns>List of spawned tiles.</returns>
        public IReadOnlyList<MahjongTile> GenerateFromOccupancyMask(bool[,,] occupancyMask)
        {
            if (occupancyMask == null)
            {
                return spawnedTiles;
            }

            int width = occupancyMask.GetLength(0);
            int height = occupancyMask.GetLength(1);
            int depth = occupancyMask.GetLength(2);

            List<Vector3Int> occupiedCoordinates = new List<Vector3Int>();
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        if (occupancyMask[x, y, z])
                        {
                            occupiedCoordinates.Add(new Vector3Int(x, y, z));
                        }
                    }
                }
            }

            if (occupiedCoordinates.Count % 2 != 0)
            {
                MahjongRuntimeLogger.LogWarning("Occupancy mask contains an odd number of tiles, so auto-pair generation is impossible.");
                return spawnedTiles;
            }

            List<LevelTileDefinition> tiles = new List<LevelTileDefinition>(occupiedCoordinates.Count);
            int matchId = 0;
            for (int index = 0; index < occupiedCoordinates.Count; index += 2)
            {
                tiles.Add(new LevelTileDefinition { MatchId = matchId, GridCoordinate = occupiedCoordinates[index], LocalEulerAngles = Vector3.zero });
                tiles.Add(new LevelTileDefinition { MatchId = matchId, GridCoordinate = occupiedCoordinates[index + 1], LocalEulerAngles = Vector3.zero });
                matchId++;
            }

            levelManager?.SetActiveLevelDefinition(null, false);
            activeCubeSurfaceTilePlacement = false;
            isCurrentShapeCube = false;
            ConfigureFillTexturePool(null);
            return Generate("MaskGeneratedLevel", new VoxelGridSize(width, height, depth), null, tiles, 1, 0f, 0f);
        }

        /// <summary>
        /// Clears every currently spawned tile instance.
        /// </summary>
        public void ClearGeneratedLevel()
        {
            for (int index = 0; index < spawnedTiles.Count; index++)
            {
                MahjongTile tile = spawnedTiles[index];
                if (tile == null)
                {
                    continue;
                }

                tileManager?.UnregisterTile(tile);
                if (usePooling && tilePool != null)
                {
                    tilePool.Release(tile, tileRoot);
                }
                else
                {
                    Destroy(tile.gameObject);
                }
            }

            spawnedTiles.Clear();
            ClearRuntimeBlockRoots();
            fillTexturesByMatchId.Clear();
            activeLevelFillTextures.Clear();
            nextTileId = 0;
            levelManager?.ClearActiveGrid();
            isCurrentShapeCube = false;

            if (clearExistingChildrenOnGenerate && tileRoot != null && (!usePooling || tilePool == null))
            {
                for (int index = tileRoot.childCount - 1; index >= 0; index--)
                {
                    Transform child = tileRoot.GetChild(index);
                    if (child != null)
                    {
                        Destroy(child.gameObject);
                    }
                }
            }
        }

        /// <summary>
        /// Builds the runtime grid, spawns tiles and focuses the orbit camera.
        /// </summary>
        private IReadOnlyList<MahjongTile> Generate(string levelName, VoxelGridSize gridSize, VoxelGridLayoutSettings layoutOverride, IList<LevelTileDefinition> tileDefinitions, int runtimeBlockCount, float blockStrideLocalX, float blockStrideLocalY)
        {
            CancelActiveGeneration();

            if (ShouldGenerateIncrementally())
            {
                activeGenerationRoutine = StartCoroutine(GenerateIncrementally(levelName, gridSize, layoutOverride, tileDefinitions, runtimeBlockCount, blockStrideLocalX, blockStrideLocalY));
                return spawnedTiles;
            }

            return GenerateImmediate(levelName, gridSize, layoutOverride, tileDefinitions, runtimeBlockCount, blockStrideLocalX, blockStrideLocalY);
        }

        private IReadOnlyList<MahjongTile> GenerateImmediate(string levelName, VoxelGridSize gridSize, VoxelGridLayoutSettings layoutOverride, IList<LevelTileDefinition> tileDefinitions, int runtimeBlockCount, float blockStrideLocalX, float blockStrideLocalY)
        {
            if (!TryPrepareGeneration(gridSize, layoutOverride, tileDefinitions, runtimeBlockCount, blockStrideLocalX, blockStrideLocalY, out VoxelGridData grid))
            {
                return spawnedTiles;
            }

            SpawnTileDefinitionsImmediate(grid, tileDefinitions);
            if (ShouldPlayAssembleOnLoad())
            {
                activeGenerationRoutine = StartCoroutine(FinalizeGenerationRoutine(levelName, grid));
            }
            else
            {
                FinalizeGeneration(levelName, grid);
            }

            return spawnedTiles;
        }

        private IEnumerator GenerateIncrementally(string levelName, VoxelGridSize gridSize, VoxelGridLayoutSettings layoutOverride, IList<LevelTileDefinition> tileDefinitions, int runtimeBlockCount, float blockStrideLocalX, float blockStrideLocalY)
        {
            if (!TryPrepareGeneration(gridSize, layoutOverride, tileDefinitions, runtimeBlockCount, blockStrideLocalX, blockStrideLocalY, out VoxelGridData grid))
            {
                activeGenerationRoutine = null;
                yield break;
            }

            if (prewarmPoolForGeneration)
            {
                yield return PrewarmPoolIfNeeded(CountSpawnableTileDefinitions(grid, tileDefinitions));
            }

            yield return SpawnTileDefinitionsIncrementally(grid, tileDefinitions, GetTilesPerFrame());
            if (ShouldPlayAssembleOnLoad())
            {
                yield return FinalizeGenerationRoutine(levelName, grid);
            }
            else
            {
                FinalizeGeneration(levelName, grid);
            }

            activeGenerationRoutine = null;
        }

        private bool TryPrepareGeneration(
            VoxelGridSize gridSize,
            VoxelGridLayoutSettings layoutOverride,
            IList<LevelTileDefinition> tileDefinitions,
            int runtimeBlockCount,
            float blockStrideLocalX,
            float blockStrideLocalY,
            out VoxelGridData grid)
        {
            grid = null;
            if (context == null)
            {
                MahjongRuntimeLogger.LogWarning("VoxelLevelGenerator must be initialized before generating levels.");
                return false;
            }

            EnsureTileTemplate();

            tileManager?.SetVisibilityRefreshSuspended(true);

            ClearGeneratedLevel();
            PrepareRuntimeBlockRoots(runtimeBlockCount, blockStrideLocalX, blockStrideLocalY);

            grid = levelManager.CreateGrid(gridSize, layoutOverride);
            levelManager.SetActiveGrid(grid);
            pieceBaseMaterial = mahjongMaterialSO != null ? mahjongMaterialSO.PieceBaseMaterial : null;
            pieceTexture = RandomPieceTexture();
            if (activeLevelFillTextures.Count == 0)
            {
                ConfigureFillTexturePool(levelDefinition != null ? levelDefinition.FillCategoryNames : null);
            }

            PrepareFillTexturesForLevel(tileDefinitions);
            return true;
        }

        private void SpawnTileDefinitionsImmediate(VoxelGridData grid, IList<LevelTileDefinition> tileDefinitions)
        {
            if (tileDefinitions == null)
            {
                return;
            }

            for (int index = 0; index < tileDefinitions.Count; index++)
            {
                LevelTileDefinition definition = tileDefinitions[index];
                if (definition == null || !grid.Contains(definition.GridCoordinate))
                {
                    continue;
                }

                SpawnTile(grid, definition);
            }
        }

        private IEnumerator SpawnTileDefinitionsIncrementally(VoxelGridData grid, IList<LevelTileDefinition> tileDefinitions, int batchSize)
        {
            if (tileDefinitions == null)
            {
                yield break;
            }

            int spawnedThisFrame = 0;
            for (int index = 0; index < tileDefinitions.Count; index++)
            {
                LevelTileDefinition definition = tileDefinitions[index];
                if (definition == null || !grid.Contains(definition.GridCoordinate))
                {
                    continue;
                }

                SpawnTile(grid, definition);
                spawnedThisFrame++;
                if (spawnedThisFrame >= batchSize)
                {
                    spawnedThisFrame = 0;
                    yield return null;
                }
            }
        }

        private void FinalizeGeneration(string levelName, VoxelGridData grid)
        {
            if (Application.isPlaying)
            {
                activeGenerationRoutine = StartCoroutine(FinalizeGenerationRoutine(levelName, grid, false));
                return;
            }

            FocusCameraOnGrid(grid);
            RefreshSpawnedTilePresentation();
            Physics.SyncTransforms();
            if (tileManager != null)
            {
                tileManager.SetVisibilityRefreshSuspended(false);
                tileManager.RefreshTileExposure();
            }

            Transform rotationRoot = runtimeBlockRoots.Count > 0 && runtimeBlockRoots[0] != null
                ? runtimeBlockRoots[0]
                : (tileRoot != null ? tileRoot : transform);
            if (cameraManager != null)
            {
                cameraManager.SetRotationTarget(rotationRoot);
            }

            context.EventBus.Publish(new LevelGeneratedEvent(levelName, spawnedTiles.Count, grid));
        }

        private IEnumerator FinalizeGenerationRoutine(string levelName, VoxelGridData grid, bool playAssemble = true)
        {
            FocusCameraOnGrid(grid);
            yield return null;
            if (playAssemble)
            {
                yield return PlayAssembleSequence(grid);
            }

            RefreshSpawnedTilePresentation();
            Physics.SyncTransforms();
            yield return new WaitForFixedUpdate();
            Physics.SyncTransforms();
            if (tileManager != null)
            {
                tileManager.SetVisibilityRefreshSuspended(false);
                tileManager.RefreshTileExposure();
            }

            Transform finalRotationRoot = runtimeBlockRoots.Count > 0 && runtimeBlockRoots[0] != null
                ? runtimeBlockRoots[0]
                : (tileRoot != null ? tileRoot : transform);
            if (cameraManager != null)
            {
                cameraManager.SetRotationTarget(finalRotationRoot);
            }

            context.EventBus.Publish(new LevelGeneratedEvent(levelName, spawnedTiles.Count, grid));
            activeGenerationRoutine = null;
        }

        private void CancelActiveGeneration()
        {
            if (activeGenerationRoutine == null)
            {
                return;
            }

            Coroutine runningRoutine = activeGenerationRoutine;
            activeGenerationRoutine = null;
            StopCoroutine(runningRoutine);
            tileManager?.SetVisibilityRefreshSuspended(false);
        }

        private bool ShouldGenerateIncrementally()
        {
            return generateIncrementally && Application.isPlaying;
        }

        private bool ShouldPlayAssembleOnLoad()
        {
            return playAssembleOnLoad && Application.isPlaying && spawnedTiles.Count > 0;
        }

        private int CountSpawnableTileDefinitions(VoxelGridData grid, IList<LevelTileDefinition> tileDefinitions)
        {
            if (grid == null || tileDefinitions == null)
            {
                return 0;
            }

            int count = 0;
            for (int index = 0; index < tileDefinitions.Count; index++)
            {
                LevelTileDefinition definition = tileDefinitions[index];
                if (definition != null && grid.Contains(definition.GridCoordinate))
                {
                    count++;
                }
            }

            return count;
        }

        private IEnumerator PrewarmPoolIfNeeded(int requiredTileCount)
        {
            if (!usePooling || tilePool == null || requiredTileCount <= tilePool.AvailableCount)
            {
                yield break;
            }

            int missingTileCount = requiredTileCount - tilePool.AvailableCount;
            int warmedThisFrame = 0;
            for (int index = 0; index < missingTileCount; index++)
            {
                MahjongTile pooledTile = tilePool.Get(tileRoot);
                tilePool.Release(pooledTile, tileRoot);
                warmedThisFrame++;
                if (warmedThisFrame >= GetPrewarmTilesPerFrame())
                {
                    warmedThisFrame = 0;
                    yield return null;
                }
            }
        }

        private int GetTilesPerFrame()
        {
            return Mathf.Max(1, tilesPerFrame);
        }

        private int GetPrewarmTilesPerFrame()
        {
            return Mathf.Max(1, prewarmTilesPerFrame);
        }

        private void RefreshSpawnedTilePresentation()
        {
            for (int index = 0; index < spawnedTiles.Count; index++)
            {
                MahjongTile tile = spawnedTiles[index];
                if (tile == null)
                {
                    continue;
                }

                tile.RefreshPresentation(true);
            }
        }

        private IEnumerator PlayAssembleSequence(VoxelGridData grid)
        {
            if (spawnedTiles.Count == 0)
            {
                yield break;
            }
            // Try to match the assemble timing to the tile appear audio clip so the
            // visual assembly feels synced to the sound effect. Fall back to the
            // configured max stagger when the clip isn't available.
            MahjongOut3D.Data.MahjongAudioSettings audioSettings = Resources.Load<MahjongOut3D.Data.MahjongAudioSettings>("MahjongAudioSettings");
            AudioClip appearClip = audioSettings != null ? audioSettings.TileAppearClip : null;

            float duration = Mathf.Max(0.05f, assembleDurationSeconds);
            float clipLength = (appearClip != null && appearClip.length > 0f) ? appearClip.length : (duration + assembleMaxStaggerSeconds);
            float maxStagger = Mathf.Max(0f, clipLength - duration);

            List<TileAssemblePose> poses = BuildTileAssemblePoses(grid, maxStagger);
            if (poses.Count == 0)
            {
                yield break;
            }

            // Ensure the entire assemble sequence spans the clip length (or
            // computed total based on delays) so the animation completes with the
            // audio.
            float totalDuration = Mathf.Max(duration, clipLength);
            for (int index = 0; index < poses.Count; index++)
            {
                TileAssemblePose pose = poses[index];
                if (pose.DelaySeconds + duration > totalDuration)
                {
                    totalDuration = pose.DelaySeconds + duration;
                }
            }

            float elapsed = 0f;
            while (elapsed < totalDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                for (int index = 0; index < poses.Count; index++)
                {
                    TileAssemblePose pose = poses[index];
                    MahjongTile tile = pose.Tile;
                    if (tile == null)
                    {
                        continue;
                    }

                    float normalizedTime = Mathf.Clamp01((elapsed - pose.DelaySeconds) / duration);
                    float easedTime = EaseOutCubic(normalizedTime);
                    tile.transform.SetPositionAndRotation(
                        Vector3.LerpUnclamped(pose.StartPosition, pose.EndPosition, easedTime),
                        Quaternion.SlerpUnclamped(pose.StartRotation, pose.EndRotation, easedTime));
                }

                yield return null;
            }

            for (int index = 0; index < poses.Count; index++)
            {
                TileAssemblePose pose = poses[index];
                if (pose.Tile == null)
                {
                    continue;
                }

                pose.Tile.transform.SetPositionAndRotation(pose.EndPosition, pose.EndRotation);
            }
        }

        private List<TileAssemblePose> BuildTileAssemblePoses(VoxelGridData grid, float maxStagger)
        {
            List<TileAssemblePose> poses = new List<TileAssemblePose>(spawnedTiles.Count);
            if (!TryBuildSpawnedTileBounds(out Bounds worldBounds))
            {
                Transform tileRootTransform = tileRoot != null ? tileRoot : transform;
                if (grid != null && tileRootTransform != null)
                {
                    worldBounds = TransformBounds(tileRootTransform, grid.GetLocalBounds());
                }
                else
                {
                    return poses;
                }
            }

            Vector3 center = worldBounds.center;
            Camera activeCamera = cameraManager != null ? cameraManager.ActiveCamera : null;
            Vector3 cameraPosition = activeCamera != null ? activeCamera.transform.position : center - (Vector3.forward * 8f);
            Vector3 cameraForward = activeCamera != null ? activeCamera.transform.forward.normalized : (center - cameraPosition).normalized;
            if (cameraForward.sqrMagnitude <= Mathf.Epsilon)
            {
                cameraForward = Vector3.forward;
            }

            Vector3 cameraRight = activeCamera != null ? activeCamera.transform.right.normalized : Vector3.right;
            Vector3 cameraUp = activeCamera != null ? activeCamera.transform.up.normalized : Vector3.up;
            float distanceToCenter = Vector3.Distance(cameraPosition, center);
            Vector3 assembleOrigin = cameraPosition + (cameraForward * Mathf.Max(1.6f, distanceToCenter * 0.32f));
            int poseCount = Mathf.Max(1, spawnedTiles.Count - 1);

            for (int index = 0; index < spawnedTiles.Count; index++)
            {
                MahjongTile tile = spawnedTiles[index];
                if (tile == null)
                {
                    continue;
                }

                Vector3 endPosition = tile.transform.position;
                Quaternion endRotation = tile.transform.rotation;
                Vector2 scatter2D = ResolveAssembleScatter(index, tile.TileId);
                float depthOffset = ResolveAssembleDepthOffset(index, tile.TileId);
                Vector3 startPosition = assembleOrigin
                    + (cameraRight * scatter2D.x * assembleScatterRadius)
                    + (cameraUp * scatter2D.y * assembleScatterRadius)
                    - (cameraForward * depthOffset);
                Quaternion startRotation = Quaternion.Euler(
                    ResolveHashedRange(tile.TileId, 0, -65f, 65f),
                    ResolveHashedRange(tile.TileId, 1, -140f, 140f),
                    ResolveHashedRange(tile.TileId, 2, -45f, 45f)) * endRotation;
                float delay = maxStagger * (index / (float)poseCount);

                tile.SetVisible(true);
                if (tile.TileCollider != null)
                {
                    tile.TileCollider.enabled = false;
                }

                tile.transform.SetPositionAndRotation(startPosition, startRotation);
                poses.Add(new TileAssemblePose(tile, startPosition, startRotation, endPosition, endRotation, delay));
            }

            return poses;
        }

        private Vector2 ResolveAssembleScatter(int index, int tileId)
        {
            float x = ResolveHashedRange(tileId, index * 3, -1f, 1f);
            float y = ResolveHashedRange(tileId, (index * 3) + 1, -1f, 1f);
            Vector2 scatter = new Vector2(x, y);
            if (scatter.sqrMagnitude <= 0.0001f)
            {
                return new Vector2(0.2f, 0.35f);
            }

            return scatter.normalized * Mathf.Lerp(0.35f, 1f, Mathf.Clamp01(Mathf.Abs(ResolveHashedRange(tileId, (index * 3) + 2, 0f, 1f))));
        }

        private float ResolveAssembleDepthOffset(int index, int tileId)
        {
            return Mathf.Lerp(0f, assembleDepthJitter, ResolveHashedRange(tileId, index + 7, 0f, 1f));
        }

        private static float ResolveHashedRange(int seed, int salt, float min, float max)
        {
            float wave = Mathf.Sin((seed * 12.9898f) + (salt * 78.233f)) * 43758.5453f;
            float normalized = wave - Mathf.Floor(wave);
            return Mathf.Lerp(min, max, normalized);
        }

        private static float EaseOutCubic(float t)
        {
            float clamped = Mathf.Clamp01(t);
            float inverse = 1f - clamped;
            return 1f - (inverse * inverse * inverse);
        }

        private void ConfigureFillTexturePool(IList<string> categoryNames)
        {
            activeLevelFillTextures.Clear();
            fillTexturesByMatchId.Clear();

            if (mahjongMaterialSO == null)
            {
                return;
            }

            List<Texture2D> resolvedTextures = mahjongMaterialSO.GetActiveFillTextures(categoryNames);
            if ((resolvedTextures == null || resolvedTextures.Count == 0) && categoryNames != null && categoryNames.Count > 0)
            {
                resolvedTextures = mahjongMaterialSO.GetActiveFillTextures();
            }

            if (resolvedTextures == null || resolvedTextures.Count == 0)
            {
                return;
            }

            activeLevelFillTextures.AddRange(resolvedTextures);
            Shuffle(activeLevelFillTextures);
        }

        /// <summary>
        /// Pre-assigns fill textures per match so outer shells avoid sharing the same fill as much as possible.
        /// This keeps the block from exposing too many interchangeable pairs on the outer layer.
        /// </summary>
        private void PrepareFillTexturesForLevel(IList<LevelTileDefinition> tileDefinitions)
        {
            fillTexturesByMatchId.Clear();
            if (tileDefinitions == null || tileDefinitions.Count == 0 || activeLevelFillTextures.Count == 0)
            {
                return;
            }

            Dictionary<int, MatchFillGroup> groupsByMatchId = new Dictionary<int, MatchFillGroup>();
            for (int index = 0; index < tileDefinitions.Count; index++)
            {
                LevelTileDefinition definition = tileDefinitions[index];
                if (definition == null)
                {
                    continue;
                }

                int sourceMatchId = ResolveSourceMatchId(definition);
                if (!groupsByMatchId.TryGetValue(sourceMatchId, out MatchFillGroup group))
                {
                    group = new MatchFillGroup(sourceMatchId);
                    groupsByMatchId.Add(sourceMatchId, group);
                }

                group.RegisterShell(definition.SurfaceShellIndex);
                group.RegisterRuntimeMatchId(definition.MatchId);
            }

            List<MatchFillGroup> groups = new List<MatchFillGroup>(groupsByMatchId.Values);
            groups.Sort((left, right) =>
            {
                int outerShellComparison = left.OuterShellIndex.CompareTo(right.OuterShellIndex);
                if (outerShellComparison != 0)
                {
                    return outerShellComparison;
                }

                int shellSpanComparison = right.ShellSpan.CompareTo(left.ShellSpan);
                if (shellSpanComparison != 0)
                {
                    return shellSpanComparison;
                }

                return left.MatchId.CompareTo(right.MatchId);
            });

            Dictionary<int, HashSet<Texture2D>> usedTexturesByShell = new Dictionary<int, HashSet<Texture2D>>();
            Dictionary<Texture2D, int> usageCounts = new Dictionary<Texture2D, int>();

            for (int index = 0; index < groups.Count; index++)
            {
                MatchFillGroup group = groups[index];
                Texture2D selectedTexture = ChooseFillTextureForGroup(group, usedTexturesByShell, usageCounts);
                if (selectedTexture == null)
                {
                    continue;
                }

                foreach (int runtimeMatchId in group.RuntimeMatchIds)
                {
                    fillTexturesByMatchId[runtimeMatchId] = selectedTexture;
                }

                if (!usageCounts.ContainsKey(selectedTexture))
                {
                    usageCounts[selectedTexture] = 0;
                }

                usageCounts[selectedTexture]++;
                foreach (int shellIndex in group.ShellIndices)
                {
                    if (!usedTexturesByShell.TryGetValue(shellIndex, out HashSet<Texture2D> usedTextures))
                    {
                        usedTextures = new HashSet<Texture2D>();
                        usedTexturesByShell.Add(shellIndex, usedTextures);
                    }

                    usedTextures.Add(selectedTexture);
                }
            }
        }

        private Texture2D ChooseFillTextureForGroup(MatchFillGroup group, Dictionary<int, HashSet<Texture2D>> usedTexturesByShell, Dictionary<Texture2D, int> usageCounts)
        {
            Texture2D bestTexture = null;
            int bestUsage = int.MaxValue;
            bool bestTouchesShellReuse = true;

            for (int index = 0; index < activeLevelFillTextures.Count; index++)
            {
                Texture2D candidate = activeLevelFillTextures[index];
                if (candidate == null)
                {
                    continue;
                }

                bool touchesShellReuse = false;
                foreach (int shellIndex in group.ShellIndices)
                {
                    if (usedTexturesByShell.TryGetValue(shellIndex, out HashSet<Texture2D> usedTextures) && usedTextures.Contains(candidate))
                    {
                        touchesShellReuse = true;
                        break;
                    }
                }

                int usage = usageCounts.TryGetValue(candidate, out int count) ? count : 0;
                bool isBetter = bestTexture == null
                    || (bestTouchesShellReuse && !touchesShellReuse)
                    || (bestTouchesShellReuse == touchesShellReuse && usage < bestUsage);

                if (!isBetter)
                {
                    continue;
                }

                bestTexture = candidate;
                bestUsage = usage;
                bestTouchesShellReuse = touchesShellReuse;
            }

            return bestTexture;
        }

        private static void Shuffle<TValue>(IList<TValue> values)
        {
            if (values == null)
            {
                return;
            }

            for (int index = values.Count - 1; index > 0; index--)
            {
                int swapIndex = Random.Range(0, index + 1);
                TValue temporary = values[index];
                values[index] = values[swapIndex];
                values[swapIndex] = temporary;
            }
        }

        /// <summary>
        /// Returns the authored tile list without applying any runtime cube migration.
        /// </summary>
        private IList<LevelTileDefinition> BuildRuntimeTileDefinitions(bool useSurfaceTilePlacement, LevelShapeType shape, VoxelGridLayoutSettings layoutOverride, IList<LevelTileDefinition> sourceTiles)
        {
            if (sourceTiles == null)
            {
                return sourceTiles;
            }

            List<LevelTileDefinition> runtimeTiles = new List<LevelTileDefinition>(sourceTiles.Count);
            List<float> shellMagnitudes = useSurfaceTilePlacement ? new List<float>(sourceTiles.Count) : null;
            bool hasAuthoredNestedShellIndices = false;

            for (int index = 0; index < sourceTiles.Count; index++)
            {
                LevelTileDefinition clone = CloneTileDefinition(sourceTiles[index]);
                if (clone == null)
                {
                    continue;
                }

                runtimeTiles.Add(clone);
                clone.RuntimeSourceMatchId = clone.RuntimeSourceMatchId >= 0 ? clone.RuntimeSourceMatchId : clone.MatchId;
                hasAuthoredNestedShellIndices |= clone.SurfaceShellIndex > 0;
                if (!useSurfaceTilePlacement)
                {
                    continue;
                }

                VoxelGridDirection facingDirection = ResolveFacingDirection(clone.LocalEulerAngles);
                float shellMagnitude = GetCubeShellNormalMagnitude(clone.LocalPosition, facingDirection);
                shellMagnitudes.Add(shellMagnitude);
            }

            if (!useSurfaceTilePlacement || shape != LevelShapeType.Cube)
            {
                return runtimeTiles;
            }

            List<float> uniqueMagnitudes = BuildUniqueDescendingMagnitudes(shellMagnitudes);
            bool inferredNestedShellIndices = false;
            for (int index = 0; index < runtimeTiles.Count; index++)
            {
                LevelTileDefinition tile = runtimeTiles[index];
                if (tile == null || tile.SurfaceShellIndex > 0 || !tile.UseCustomLocalPosition)
                {
                    continue;
                }

                VoxelGridDirection facingDirection = ResolveFacingDirection(tile.LocalEulerAngles);
                float shellMagnitude = GetCubeShellNormalMagnitude(tile.LocalPosition, facingDirection);
                int resolvedShellIndex = ResolveShellIndex(shellMagnitude, uniqueMagnitudes);
                tile.SurfaceShellIndex = resolvedShellIndex;
                inferredNestedShellIndices |= resolvedShellIndex > 0;
            }

            if (!hasAuthoredNestedShellIndices && inferredNestedShellIndices)
            {
                CompactSurfaceShellLayers(runtimeTiles, layoutOverride);
            }

            if (shape == LevelShapeType.Cube)
            {
                ApplySurfaceTileSpacing(runtimeTiles);
            }

            return runtimeTiles;
        }

        /// <summary>
        /// Applies face-aware in-plane spacing to authored surface tiles without pushing them outward from the block.
        /// </summary>
        private void ApplySurfaceTileSpacing(IList<LevelTileDefinition> runtimeTiles)
        {
            if (runtimeTiles == null || runtimeTiles.Count == 0)
            {
                return;
            }

            for (int index = 0; index < runtimeTiles.Count; index++)
            {
                LevelTileDefinition tile = runtimeTiles[index];
                if (tile == null || !tile.UseCustomLocalPosition)
                {
                    continue;
                }

                VoxelGridDirection facingDirection = ResolveFacingDirection(tile.LocalEulerAngles);
                Vector3 spacingOffset = GetSurfaceTileSpacingOffset(facingDirection);
                if (spacingOffset == Vector3.zero)
                {
                    continue;
                }

                tile.LocalPosition = ApplySurfaceTileSpacingOffset(tile.LocalPosition, facingDirection, spacingOffset);
            }
        }

        /// <summary>
        /// Pulls nested surface shells slightly closer together while preserving their order and cube-like layering.
        /// This runs at load time so existing generated level assets also benefit from tighter layer spacing.
        /// </summary>
        private void CompactSurfaceShellLayers(IList<LevelTileDefinition> runtimeTiles, VoxelGridLayoutSettings layoutOverride)
        {
            if (runtimeTiles == null || runtimeTiles.Count == 0 || surfaceShellSeparationScale >= 0.9999f)
            {
                return;
            }

            float shellThickness = GetSurfaceShellThickness(layoutOverride);
            if (shellThickness <= Mathf.Epsilon)
            {
                return;
            }

            for (int index = 0; index < runtimeTiles.Count; index++)
            {
                LevelTileDefinition tile = runtimeTiles[index];
                if (tile == null || tile.SurfaceShellIndex <= 0 || !tile.UseCustomLocalPosition)
                {
                    continue;
                }

                VoxelGridDirection facingDirection = ResolveFacingDirection(tile.LocalEulerAngles);
                float faceStep = GetSurfaceFaceStep(layoutOverride, facingDirection);
                float authoredGap = Mathf.Max(0f, faceStep - shellThickness);
                if (authoredGap <= Mathf.Epsilon)
                {
                    continue;
                }

                float targetGap = authoredGap * Mathf.Clamp(surfaceShellSeparationScale, 0f, 1f);
                float additionalOutwardOffset = (targetGap - authoredGap) * tile.SurfaceShellIndex;
                if (Mathf.Abs(additionalOutwardOffset) <= 0.0001f)
                {
                    continue;
                }

                Vector3 faceNormal = ((Vector3)VoxelGridDirections.GetOffset(facingDirection)).normalized;
                tile.LocalPosition += faceNormal * additionalOutwardOffset;
            }
        }

        /// <summary>
        /// Creates a detached tile-definition copy safe for runtime adjustments.
        /// </summary>
        private static LevelTileDefinition CloneTileDefinition(LevelTileDefinition source)
        {
            if (source == null)
            {
                return null;
            }

            return new LevelTileDefinition
            {
                MatchId = source.MatchId,
                GridCoordinate = source.GridCoordinate,
                SurfaceShellIndex = source.SurfaceShellIndex,
                UseCustomLocalPosition = source.UseCustomLocalPosition,
                LocalPosition = source.LocalPosition,
                LocalEulerAngles = source.LocalEulerAngles,
                RuntimeBlockIndex = source.RuntimeBlockIndex,
                RuntimeSourceMatchId = source.RuntimeSourceMatchId,
            };
        }

        /// <summary>
        /// Duplicates the authored block into multiple runtime blocks while keeping each block on its own rotation root.
        /// </summary>
        private IList<LevelTileDefinition> ExpandRuntimeTileDefinitions(LevelDefinition definition, VoxelGridSize singleBlockGridSize, VoxelGridLayoutSettings layoutOverride, IList<LevelTileDefinition> singleBlockRuntimeTiles)
        {
            if (singleBlockRuntimeTiles == null)
            {
                return singleBlockRuntimeTiles;
            }

            int resolvedBlockCount = definition != null ? Mathf.Max(1, definition.BlockCount) : 1;
            if (resolvedBlockCount <= 1)
            {
                for (int index = 0; index < singleBlockRuntimeTiles.Count; index++)
                {
                    LevelTileDefinition tile = singleBlockRuntimeTiles[index];
                    if (tile == null)
                    {
                        continue;
                    }

                    tile.RuntimeBlockIndex = 0;
                    tile.RuntimeSourceMatchId = ResolveSourceMatchId(tile);
                }

                return singleBlockRuntimeTiles;
            }

            List<LevelTileDefinition> expandedTiles = new List<LevelTileDefinition>(singleBlockRuntimeTiles.Count * resolvedBlockCount);
            VoxelGridData singleBlockGrid = levelManager.CreateGrid(singleBlockGridSize, layoutOverride);
            int blockStrideWidth = definition.GetBlockStrideWidth(singleBlockGridSize);
            int runtimeMatchIdStride = GetRuntimeMatchIdStride(singleBlockRuntimeTiles);

            for (int blockIndex = 0; blockIndex < resolvedBlockCount; blockIndex++)
            {
                int gridOffsetX = blockIndex * blockStrideWidth;
                for (int tileIndex = 0; tileIndex < singleBlockRuntimeTiles.Count; tileIndex++)
                {
                    LevelTileDefinition source = singleBlockRuntimeTiles[tileIndex];
                    if (source == null)
                    {
                        continue;
                    }

                    LevelTileDefinition clone = CloneTileDefinition(source);
                    int sourceMatchId = ResolveSourceMatchId(source);
                    clone.MatchId = sourceMatchId + (blockIndex * runtimeMatchIdStride);
                    clone.GridCoordinate = new Vector3Int(source.GridCoordinate.x + gridOffsetX, source.GridCoordinate.y, source.GridCoordinate.z);
                    clone.LocalPosition = source.UseCustomLocalPosition ? source.LocalPosition : singleBlockGrid.GetLocalPosition(source.GridCoordinate);
                    clone.UseCustomLocalPosition = true;
                    clone.RuntimeBlockIndex = blockIndex;
                    clone.RuntimeSourceMatchId = sourceMatchId;
                    expandedTiles.Add(clone);
                }
            }

            return expandedTiles;
        }

        /// <summary>
        /// Resolves the authored source match id used to preserve pair visuals across duplicated runtime blocks.
        /// </summary>
        private static int ResolveSourceMatchId(LevelTileDefinition definition)
        {
            if (definition == null)
            {
                return 0;
            }

            return definition.RuntimeSourceMatchId >= 0 ? definition.RuntimeSourceMatchId : definition.MatchId;
        }

        /// <summary>
        /// Computes a safe per-block match-id stride so duplicated blocks never share the same runtime match id.
        /// </summary>
        private static int GetRuntimeMatchIdStride(IList<LevelTileDefinition> tileDefinitions)
        {
            int maxMatchId = 0;
            if (tileDefinitions != null)
            {
                for (int index = 0; index < tileDefinitions.Count; index++)
                {
                    LevelTileDefinition definition = tileDefinitions[index];
                    if (definition == null)
                    {
                        continue;
                    }

                    maxMatchId = Mathf.Max(maxMatchId, ResolveSourceMatchId(definition));
                }
            }

            return maxMatchId + 1;
        }

        /// <summary>
        /// Resolves which cardinal face a custom-placed cube tile is pointing toward.
        /// </summary>
        private static VoxelGridDirection ResolveFacingDirection(Vector3 localEulerAngles)
        {
            Vector3 normal = Quaternion.Euler(localEulerAngles) * Vector3.up;
            Vector3 absoluteNormal = new Vector3(Mathf.Abs(normal.x), Mathf.Abs(normal.y), Mathf.Abs(normal.z));

            if (absoluteNormal.x >= absoluteNormal.y && absoluteNormal.x >= absoluteNormal.z)
            {
                return normal.x >= 0f ? VoxelGridDirection.Right : VoxelGridDirection.Left;
            }

            if (absoluteNormal.y >= absoluteNormal.x && absoluteNormal.y >= absoluteNormal.z)
            {
                return normal.y >= 0f ? VoxelGridDirection.Up : VoxelGridDirection.Down;
            }

            return normal.z >= 0f ? VoxelGridDirection.Forward : VoxelGridDirection.Back;
        }

        /// <summary>
        /// Reads the magnitude on the axis normal to the resolved cube face.
        /// </summary>
        private static float GetCubeShellNormalMagnitude(Vector3 localPosition, VoxelGridDirection facingDirection)
        {
            switch (facingDirection)
            {
                case VoxelGridDirection.Left:
                case VoxelGridDirection.Right:
                    return Mathf.Abs(localPosition.x);

                case VoxelGridDirection.Down:
                case VoxelGridDirection.Up:
                    return Mathf.Abs(localPosition.y);

                case VoxelGridDirection.Back:
                case VoxelGridDirection.Forward:
                default:
                    return Mathf.Abs(localPosition.z);
            }
        }

        /// <summary>
        /// Applies a corrected magnitude on the axis normal to the resolved cube face.
        /// </summary>
        private static Vector3 SetCubeShellNormalMagnitude(Vector3 localPosition, VoxelGridDirection facingDirection, float correctedMagnitude)
        {
            switch (facingDirection)
            {
                case VoxelGridDirection.Left:
                case VoxelGridDirection.Right:
                    localPosition.x = Mathf.Sign(localPosition.x == 0f ? (facingDirection == VoxelGridDirection.Right ? 1f : -1f) : localPosition.x) * correctedMagnitude;
                    return localPosition;

                case VoxelGridDirection.Down:
                case VoxelGridDirection.Up:
                    localPosition.y = Mathf.Sign(localPosition.y == 0f ? (facingDirection == VoxelGridDirection.Up ? 1f : -1f) : localPosition.y) * correctedMagnitude;
                    return localPosition;

                case VoxelGridDirection.Back:
                case VoxelGridDirection.Forward:
                default:
                    localPosition.z = Mathf.Sign(localPosition.z == 0f ? (facingDirection == VoxelGridDirection.Forward ? 1f : -1f) : localPosition.z) * correctedMagnitude;
                    return localPosition;
            }
        }

        /// <summary>
        /// Resolves the additive in-plane spacing offset for the supplied face pair.
        /// </summary>
        private Vector3 GetSurfaceTileSpacingOffset(VoxelGridDirection facingDirection)
        {
            Vector3 baseOffset = surfaceTileSpacingOffset;
            switch (facingDirection)
            {
                case VoxelGridDirection.Left:
                case VoxelGridDirection.Right:
                    return baseOffset + leftRightSurfaceSpacingOffset;

                case VoxelGridDirection.Down:
                case VoxelGridDirection.Up:
                    return baseOffset + upDownSurfaceSpacingOffset;

                case VoxelGridDirection.Back:
                case VoxelGridDirection.Forward:
                default:
                    return baseOffset + frontBackSurfaceSpacingOffset;
            }
        }

        /// <summary>
        /// Applies per-axis spacing only on the axes that lie on the current face plane.
        /// This increases the distance between tiles on the face without moving the face outward.
        /// </summary>
        private static Vector3 ApplySurfaceTileSpacingOffset(Vector3 localPosition, VoxelGridDirection facingDirection, Vector3 spacingOffset)
        {
            Vector3 spacingScale = new Vector3(
                Mathf.Max(0f, 1f + spacingOffset.x),
                Mathf.Max(0f, 1f + spacingOffset.y),
                Mathf.Max(0f, 1f + spacingOffset.z));

            switch (facingDirection)
            {
                case VoxelGridDirection.Left:
                case VoxelGridDirection.Right:
                    localPosition.y *= spacingScale.y;
                    localPosition.z *= spacingScale.z;
                    return localPosition;

                case VoxelGridDirection.Down:
                case VoxelGridDirection.Up:
                    localPosition.x *= spacingScale.x;
                    localPosition.z *= spacingScale.z;
                    return localPosition;

                case VoxelGridDirection.Back:
                case VoxelGridDirection.Forward:
                default:
                    localPosition.x *= spacingScale.x;
                    localPosition.y *= spacingScale.y;
                    return localPosition;
            }
        }

        /// <summary>
        /// Collapses repeated shell magnitudes into one descending set used to infer shell depth.
        /// </summary>
        private static List<float> BuildUniqueDescendingMagnitudes(List<float> magnitudes)
        {
            List<float> uniqueMagnitudes = new List<float>();
            if (magnitudes == null || magnitudes.Count == 0)
            {
                return uniqueMagnitudes;
            }

            magnitudes.Sort((left, right) => right.CompareTo(left));
            for (int index = 0; index < magnitudes.Count; index++)
            {
                float magnitude = magnitudes[index];
                if (uniqueMagnitudes.Count == 0 || Mathf.Abs(uniqueMagnitudes[uniqueMagnitudes.Count - 1] - magnitude) > 0.0001f)
                {
                    uniqueMagnitudes.Add(magnitude);
                }
            }

            return uniqueMagnitudes;
        }

        /// <summary>
        /// Maps a shell magnitude back to its outer-to-inner shell index.
        /// </summary>
        private static int ResolveShellIndex(float magnitude, List<float> uniqueMagnitudes)
        {
            if (uniqueMagnitudes == null || uniqueMagnitudes.Count == 0)
            {
                return 0;
            }

            int bestIndex = 0;
            float bestDelta = Mathf.Abs(uniqueMagnitudes[0] - magnitude);
            for (int index = 1; index < uniqueMagnitudes.Count; index++)
            {
                float delta = Mathf.Abs(uniqueMagnitudes[index] - magnitude);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestIndex = index;
                }
            }

            return bestIndex;
        }

        /// <summary>
        /// Resolves the corrected center offset used by the current cube-shell generator for the supplied face.
        /// </summary>
        private static float GetCorrectedCubeShellNormalMagnitude(int shellWidth, Vector3 cellSize, VoxelGridDirection facingDirection)
        {
            float shellCount = Mathf.Max(2, shellWidth);
            switch (facingDirection)
            {
                case VoxelGridDirection.Left:
                case VoxelGridDirection.Right:
                case VoxelGridDirection.Down:
                case VoxelGridDirection.Up:
                    return Mathf.Max(0.01f, (shellCount * cellSize.x) - cellSize.y) * 0.5f;

                case VoxelGridDirection.Back:
                case VoxelGridDirection.Forward:
                default:
                    return Mathf.Max(0.01f, (shellCount * cellSize.z) - cellSize.y) * 0.5f;
            }
        }

        /// <summary>
        /// Resolves the face-normal step distance used by wrapped surface tiles.
        /// </summary>
        private float GetSurfaceFaceStep(VoxelGridLayoutSettings layoutOverride, VoxelGridDirection facingDirection)
        {
            VoxelGridLayoutSettings resolvedLayout = layoutOverride != null ? layoutOverride : levelManager != null ? levelManager.DefaultGridLayout : null;
            Vector3 step = resolvedLayout != null ? resolvedLayout.CellStep : new Vector3(0.95f, 0.45f, 0.7f);

            switch (facingDirection)
            {
                case VoxelGridDirection.Left:
                case VoxelGridDirection.Right:
                    return Mathf.Max(0.01f, step.x);

                case VoxelGridDirection.Down:
                case VoxelGridDirection.Up:
                    return Mathf.Max(0.01f, step.y);

                case VoxelGridDirection.Back:
                case VoxelGridDirection.Forward:
                default:
                    return Mathf.Max(0.01f, step.z);
            }
        }

        /// <summary>
        /// Resolves the effective tile thickness used when tightening wrapped surface shells.
        /// </summary>
        private float GetSurfaceShellThickness(VoxelGridLayoutSettings layoutOverride)
        {
            MahjongTile template = tilePrefab != null ? tilePrefab : runtimeFallbackTilePrefab;
            if (template != null)
            {
                float placementThickness = template.GetPlacementSize().y;
                if (placementThickness > 0.01f)
                {
                    return placementThickness;
                }
            }

            VoxelGridLayoutSettings resolvedLayout = layoutOverride != null ? layoutOverride : levelManager != null ? levelManager.DefaultGridLayout : null;
            Vector3 cellSize = resolvedLayout != null ? resolvedLayout.CellSize : new Vector3(0.95f, 0.45f, 0.7f);
            return Mathf.Max(0.01f, cellSize.y);
        }

        /// <summary>
        /// Spawns a single tile instance and registers it with the runtime systems.
        /// </summary>
        private void SpawnTile(VoxelGridData grid, LevelTileDefinition definition)
        {
            Transform parent = GetRuntimeBlockRoot(definition != null ? definition.RuntimeBlockIndex : 0);
            MahjongTile template = tilePrefab != null ? tilePrefab : runtimeFallbackTilePrefab;
            MahjongTile tile = usePooling && tilePool != null ? tilePool.Get(parent) : Instantiate(template, parent);
            Vector3 pieceScaleMultiplier = ResolveTileLocalScale(definition);
            tile.SetPieceLocalScaleMultiplier(pieceScaleMultiplier);
            Quaternion spawnRotation = Quaternion.Euler(definition.LocalEulerAngles);
            Vector3 baseLocalPosition = ApplyTileSpacing(definition.UseCustomLocalPosition ? definition.LocalPosition : grid.GetLocalPosition(definition.GridCoordinate));
            Vector3 placementOffset = spawnRotation * tile.GetPlacementOffset();
            TileRuntimeData runtimeData = new TileRuntimeData
            {
                TileId = nextTileId++,
                MatchId = definition.MatchId,
                GridCoordinate = definition.GridCoordinate,
                LocalPosition = baseLocalPosition - placementOffset,
                LocalEulerAngles = definition.LocalEulerAngles,
                SurfaceShellIndex = definition.SurfaceShellIndex,
                RuntimeBlockIndex = definition.RuntimeBlockIndex,
            };
            
            tile.ApplyRuntimeData(runtimeData);
            tile.Setup(pieceBaseMaterial, mahjongMaterialSO != null ? mahjongMaterialSO.FillBaseMaterial : null);
            tile.SetupPieceTexture(pieceTexture);
            tile.SetupFillTexture(GetFillTextureForMatch(definition.MatchId));
            if (applyTileBaseColor)
            {
                tile.SetDebugMatchColor(tileBaseColor);
            }
            else
            {
                tile.ClearDebugMatchColor();
            }

            //tile.SetMatchIndicatorMaterial(GetMatchIndicatorMaterial(definition.MatchId));
            tile.ResetTile();
            grid.TryPlaceTile(tile.TileId, definition.GridCoordinate);
            tileManager.RegisterTile(tile);
            spawnedTiles.Add(tile);
        }

        /// <summary>
        /// Resolves the optional test material used to identify a tile match group.
        /// </summary>
        /// <param name="matchId">Match identifier.</param>
        /// <returns>Indicator material for that match group, or null when none is configured.</returns>
        // private Material GetMatchIndicatorMaterial(int matchId)
        // {
        //     if (matchIndicatorMaterials == null || matchIndicatorMaterials.Length == 0)
        //     {
        //         return null;
        //     }

        //     int materialIndex = Mathf.Abs(matchId) % matchIndicatorMaterials.Length;
        //     return matchIndicatorMaterials[materialIndex];
        // }

        /// <summary>
        /// Applies an outward spacing offset so the distance between tiles can be tuned from the inspector.
        /// This is strictly applied only to Cube levels as documented in the inspector header/tooltip.
        /// </summary>
        /// <param name="localPosition">Original tile local position.</param>
        /// <returns>Adjusted tile local position.</returns>
        private Vector3 ApplyTileSpacing(Vector3 localPosition)
        {
            if (!isCurrentShapeCube || tileSpacingOffset == Vector3.zero || localPosition.sqrMagnitude <= Mathf.Epsilon)
            {
                return localPosition;
            }

            Vector3 spacingScale = new Vector3(
                Mathf.Max(0f, 1f + tileSpacingOffset.x),
                Mathf.Max(0f, 1f + tileSpacingOffset.y),
                Mathf.Max(0f, 1f + tileSpacingOffset.z));

            return Vector3.Scale(localPosition, spacingScale);
        }

        /// <summary>
        /// Resolves the local scale multiplier applied to the Mahjong piece renderer.
        /// Front/Back faces get a slightly thinner Z multiplier so they read closer to the other face orientations.
        /// </summary>
        private Vector3 ResolveTileLocalScale(LevelTileDefinition definition)
        {
            if (!activeCubeSurfaceTilePlacement || definition == null)
            {
                return Vector3.one;
            }

            VoxelGridDirection facingDirection = ResolveFacingDirection(definition.LocalEulerAngles);
            if (facingDirection != VoxelGridDirection.Back && facingDirection != VoxelGridDirection.Forward)
            {
                return Vector3.one;
            }

            return new Vector3(1f, 1f, Mathf.Clamp(frontBackSurfaceTileLocalZScale, 0.01f, 1f));
        }

        /// <summary>
        /// Moves the orbit camera focus to the center of the generated grid.
        /// </summary>
        private void FocusCameraOnGrid(VoxelGridData grid)
        {
            if (cameraManager == null || grid == null)
            {
                return;
            }

            Transform tileRootTransform = tileRoot != null ? tileRoot : transform;
            if (tileRootTransform != null)
            {
                tileRootTransform.localRotation = defaultTileRootLocalRotation;
            }

            for (int index = 0; index < runtimeBlockRoots.Count; index++)
            {
                Transform runtimeBlockRoot = runtimeBlockRoots[index];
                if (runtimeBlockRoot != null)
                {
                    runtimeBlockRoot.localRotation = Quaternion.identity;
                }
            }

            Transform rotationRoot = runtimeBlockRoots.Count > 0 && runtimeBlockRoots[0] != null
                ? runtimeBlockRoots[0]
                : tileRootTransform;
            cameraManager.SetRotationTarget(rotationRoot);

            Bounds worldBounds = default;
            bool useRuntimeTileBounds = runtimeBlockRoots.Count > 1;
            if (useRuntimeTileBounds)
            {
                useRuntimeTileBounds = TryBuildSpawnedTileBounds(out worldBounds);
            }

            if (useRuntimeTileBounds)
            {
                worldBounds.Expand(worldBounds.size * 0.18f);
            }
            else
            {
                Bounds localBounds = grid.GetLocalBounds();
                worldBounds = TransformBounds(tileRootTransform, localBounds);
            }

            float framePadding = ResolveCameraFramePadding();
            cameraManager.FrameBounds(worldBounds, framePadding, true, true);
            zoomSlider?.SyncWithCamera();
        }

        private float ResolveCameraFramePadding()
        {
            float framePadding = runtimeBlockRoots.Count <= 1
                ? 1f
                : Mathf.Max(1f, cameraFramePaddingOnLoad);

            if (runtimeBlockRoots.Count == 2)
            {
                framePadding = Mathf.Max(framePadding, 1.42f);
            }

            return framePadding;
        }

        /// <summary>
        /// Creates one runtime transform root per duplicated block.
        /// </summary>
        private void PrepareRuntimeBlockRoots(int blockCount, float blockStrideLocalX, float blockStrideLocalY)
        {
            ClearRuntimeBlockRoots();

            Transform rootParent = tileRoot == null ? transform : tileRoot;
            int resolvedBlockCount = Mathf.Max(1, blockCount);
            float centeredStartOffset = -0.5f * (resolvedBlockCount - 1) * blockStrideLocalX;
            float centeredVerticalOffset = -0.5f * (resolvedBlockCount - 1) * blockStrideLocalY;
            for (int blockIndex = 0; blockIndex < resolvedBlockCount; blockIndex++)
            {
                GameObject blockRootObject = new GameObject(resolvedBlockCount > 1 ? $"Runtime Block {blockIndex + 1}" : "Runtime Block");
                Transform blockRoot = blockRootObject.transform;
                blockRoot.SetParent(rootParent, false);
                blockRoot.localPosition = resolvedBlockCount == 2
                    ? new Vector3(0f, centeredVerticalOffset + (blockStrideLocalY * blockIndex), 0f)
                    : new Vector3(centeredStartOffset + (blockStrideLocalX * blockIndex), 0f, 0f);
                blockRoot.localRotation = Quaternion.identity;
                blockRoot.localScale = Vector3.one;
                runtimeBlockRoots.Add(blockRoot);
            }
        }

        /// <summary>
        /// Removes any runtime block roots created during the previous generation pass.
        /// </summary>
        private void ClearRuntimeBlockRoots()
        {
            for (int index = 0; index < runtimeBlockRoots.Count; index++)
            {
                Transform runtimeBlockRoot = runtimeBlockRoots[index];
                if (runtimeBlockRoot != null)
                {
                    Destroy(runtimeBlockRoot.gameObject);
                }
            }

            runtimeBlockRoots.Clear();
        }

        /// <summary>
        /// Resolves the parent transform used when spawning a tile for the requested runtime block.
        /// </summary>
        private Transform GetRuntimeBlockRoot(int blockIndex)
        {
            if (blockIndex >= 0 && blockIndex < runtimeBlockRoots.Count && runtimeBlockRoots[blockIndex] != null)
            {
                return runtimeBlockRoots[blockIndex];
            }

            return tileRoot == null ? transform : tileRoot;
        }

        /// <summary>
        /// Computes the horizontal local-space distance between duplicated block roots.
        /// </summary>
        private float ResolveBlockStrideLocalX(VoxelGridSize singleBlockGridSize, VoxelGridLayoutSettings layoutOverride, IList<LevelTileDefinition> singleBlockRuntimeTiles, int blockSpacingCells)
        {
            VoxelGridLayoutSettings resolvedLayout = layoutOverride != null ? layoutOverride : levelManager != null ? levelManager.DefaultGridLayout : null;
            Vector3 cellSize = resolvedLayout != null ? resolvedLayout.CellSize : new Vector3(0.95f, 0.45f, 0.7f);

            Bounds blockBounds = BuildLocalTileBounds(singleBlockGridSize, layoutOverride, singleBlockRuntimeTiles);
            float baseWidth = blockBounds.size.x;
            if (baseWidth <= Mathf.Epsilon)
            {
                baseWidth = Mathf.Max(cellSize.x, singleBlockGridSize.Width * cellSize.x);
            }

            return baseWidth + (Mathf.Max(0, blockSpacingCells) * Mathf.Max(0.01f, cellSize.x));
        }

        private float ResolveBlockStrideLocalY(VoxelGridSize singleBlockGridSize, VoxelGridLayoutSettings layoutOverride, IList<LevelTileDefinition> singleBlockRuntimeTiles, int blockSpacingCells)
        {
            VoxelGridLayoutSettings resolvedLayout = layoutOverride != null ? layoutOverride : levelManager != null ? levelManager.DefaultGridLayout : null;
            Vector3 cellSize = resolvedLayout != null ? resolvedLayout.CellSize : new Vector3(0.95f, 0.45f, 0.7f);

            Bounds blockBounds = BuildLocalTileBounds(singleBlockGridSize, layoutOverride, singleBlockRuntimeTiles);
            float baseHeight = Mathf.Max(blockBounds.size.y, blockBounds.size.z);
            if (baseHeight <= Mathf.Epsilon)
            {
                baseHeight = Mathf.Max(cellSize.z, singleBlockGridSize.Depth * cellSize.z, cellSize.y, singleBlockGridSize.Height * cellSize.y);
            }

            return baseHeight + (Mathf.Max(0, blockSpacingCells) * Mathf.Max(0.01f, Mathf.Max(cellSize.y, cellSize.z)));
        }


        /// <summary>
        /// Builds local-space bounds around the tile centers of one authored block.
        /// </summary>
        private Bounds BuildLocalTileBounds(VoxelGridSize singleBlockGridSize, VoxelGridLayoutSettings layoutOverride, IList<LevelTileDefinition> tileDefinitions)
        {
            VoxelGridData singleBlockGrid = levelManager.CreateGrid(singleBlockGridSize, layoutOverride);
            bool hasBounds = false;
            Bounds bounds = default;

            if (tileDefinitions != null)
            {
                for (int index = 0; index < tileDefinitions.Count; index++)
                {
                    LevelTileDefinition definition = tileDefinitions[index];
                    if (definition == null)
                    {
                        continue;
                    }

                    Vector3 localPosition = ApplyTileSpacing(definition.UseCustomLocalPosition ? definition.LocalPosition : singleBlockGrid.GetLocalPosition(definition.GridCoordinate));
                    if (!hasBounds)
                    {
                        bounds = new Bounds(localPosition, Vector3.zero);
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(localPosition);
                    }
                }
            }

            if (hasBounds)
            {
                return bounds;
            }

            return singleBlockGrid.GetLocalBounds();
        }

        /// <summary>
        /// Builds a world-space bounds volume that encloses every spawned tile.
        /// </summary>
        /// <param name="worldBounds">Combined world-space bounds.</param>
        /// <returns>True when at least one valid bounds source was found; otherwise false.</returns>
        private bool TryBuildSpawnedTileBounds(out Bounds worldBounds)
        {
            worldBounds = default;
            bool hasBounds = false;

            for (int index = 0; index < spawnedTiles.Count; index++)
            {
                MahjongTile tile = spawnedTiles[index];
                if (tile == null)
                {
                    continue;
                }

                if (tile.TileCollider != null)
                {
                    if (!hasBounds)
                    {
                        worldBounds = tile.TileCollider.bounds;
                        hasBounds = true;
                    }
                    else
                    {
                        worldBounds.Encapsulate(tile.TileCollider.bounds);
                    }

                    continue;
                }

                Renderer[] renderers = tile.GetComponentsInChildren<Renderer>(false);
                for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
                {
                    Renderer renderer = renderers[rendererIndex];
                    if (renderer == null)
                    {
                        continue;
                    }

                    if (!hasBounds)
                    {
                        worldBounds = renderer.bounds;
                        hasBounds = true;
                    }
                    else
                    {
                        worldBounds.Encapsulate(renderer.bounds);
                    }
                }
            }

            return hasBounds;
        }

        /// <summary>
        /// Converts local-space bounds into world-space bounds using the specified transform.
        /// </summary>
        /// <param name="targetTransform">Transform providing the local-to-world conversion.</param>
        /// <param name="localBounds">Local-space bounds to convert.</param>
        /// <returns>World-space bounds.</returns>
        private static Bounds TransformBounds(Transform targetTransform, Bounds localBounds)
        {
            Vector3 center = targetTransform.TransformPoint(localBounds.center);
            Vector3 extents = localBounds.extents;

            Vector3 axisX = targetTransform.TransformVector(new Vector3(extents.x, 0f, 0f));
            Vector3 axisY = targetTransform.TransformVector(new Vector3(0f, extents.y, 0f));
            Vector3 axisZ = targetTransform.TransformVector(new Vector3(0f, 0f, extents.z));

            Vector3 worldExtents = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));

            return new Bounds(center, worldExtents * 2f);
        }

        /// <summary>
        /// Resolves a debug color for the supplied match identifier.
        /// </summary>
        /// <param name="matchId">Match identifier.</param>
        /// <returns>Consistent debug color for that pair group.</returns>
        private static Color GetDebugMatchColor(int matchId)
        {
            if (DebugMatchPalette.Length == 0)
            {
                return Color.white;
            }

            int colorIndex = Mathf.Abs(matchId) % DebugMatchPalette.Length;
            return DebugMatchPalette[colorIndex];
        }

        /// <summary>
        /// Ensures the generator has a valid tile template, creating a runtime cube fallback when needed.
        /// </summary>
        private void EnsureTileTemplate()
        {
            if (tilePrefab != null)
            {
                return;
            }

            if (runtimeFallbackTilePrefab != null)
            {
                tilePrefab = runtimeFallbackTilePrefab;
                return;
            }

            CreatePrimitiveFallbackTile();
        }

        /// <summary>
        /// Creates the old primitive-based fallback when no imported Mahjong source is available.
        /// </summary>
        private void CreatePrimitiveFallbackTile()
        {

            GameObject tileObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            tileObject.name = "RuntimeFallbackMahjongTileTemplate";
            tileObject.hideFlags = HideFlags.HideAndDontSave;
            tileObject.SetActive(false);
            int defaultLayer = LayerMask.NameToLayer("Default");
            tileObject.layer = defaultLayer >= 0 ? defaultLayer : 0;
            tileObject.transform.SetParent(transform, false);
            tileObject.transform.localScale = new Vector3(0.95f, 0.45f, 0.7f);

            MeshRenderer renderer = tileObject.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;

                MeshRenderer sourceRenderer = ResolveFallbackMaterialRenderer();
                if (sourceRenderer != null && sourceRenderer.sharedMaterials != null && sourceRenderer.sharedMaterials.Length > 0)
                {
                    renderer.sharedMaterial = sourceRenderer.sharedMaterials[0];
                }
                else
                {
                    Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                    if (shader != null)
                    {
                        Material material = new Material(shader);
                        material.name = "RuntimeFallbackMahjongTileMaterial";
                        if (material.HasProperty("_BaseColor"))
                        {
                            material.SetColor("_BaseColor", new Color(0.96f, 0.94f, 0.9f, 1f));
                        }
                        else if (material.HasProperty("_Color"))
                        {
                            material.SetColor("_Color", new Color(0.96f, 0.94f, 0.9f, 1f));
                        }

                        if (material.HasProperty("_EmissionColor"))
                        {
                            material.EnableKeyword("_EMISSION");
                            material.SetColor("_EmissionColor", Color.black);
                        }

                        renderer.sharedMaterial = material;
                    }
                }
            }

            TileOutlinePresenter outlinePresenter = tileObject.GetComponent<TileOutlinePresenter>();
            if (outlinePresenter == null)
            {
                outlinePresenter = tileObject.AddComponent<TileOutlinePresenter>();
            }

            TileVisualController visualController = tileObject.GetComponent<TileVisualController>();
            if (visualController == null)
            {
                visualController = tileObject.AddComponent<TileVisualController>();
            }

            MahjongTile tile = tileObject.GetComponent<MahjongTile>();
            if (tile == null)
            {
                tile = tileObject.AddComponent<MahjongTile>();
            }

            if (fallbackTileVisualSettings != null)
            {
                SetPrivateField(visualController, "settings", fallbackTileVisualSettings);
            }

            runtimeFallbackTilePrefab = tile;
            tilePrefab = runtimeFallbackTilePrefab;

            if (usePooling)
            {
                tilePool = new ComponentPool<MahjongTile>(tilePrefab);
            }
        }

        /// <summary>
        /// Resolves the imported Mahjong visual source used for runtime fallback tiles.
        /// </summary>
        /// <returns>Resolved source object when available; otherwise null.</returns>
        private GameObject ResolveFallbackVisualSource()
        {
            if (fallbackVisualSource != null)
            {
                return fallbackVisualSource;
            }

            GameObject[] sceneObjects = FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int index = 0; index < sceneObjects.Length; index++)
            {
                GameObject candidate = sceneObjects[index];
                if (candidate != null && candidate.name.Equals(DefaultFallbackVisualSourceName, System.StringComparison.OrdinalIgnoreCase))
                {
                    fallbackVisualSource = candidate;
                    return fallbackVisualSource;
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves the renderer used only as a material donor for fallback runtime tiles.
        /// </summary>
        /// <returns>Renderer providing Mahjong materials when available; otherwise null.</returns>
        private MeshRenderer ResolveFallbackMaterialRenderer()
        {
            GameObject source = ResolveFallbackVisualSource();
            return source != null ? source.GetComponentInChildren<MeshRenderer>(true) : null;
        }

        /// <summary>
        /// Writes a serialized private field on a runtime-created component.
        /// </summary>
        /// <typeparam name="TTarget">Component type to modify.</typeparam>
        /// <typeparam name="TValue">Value type being assigned.</typeparam>
        /// <param name="target">Component instance receiving the value.</param>
        /// <param name="fieldName">Serialized field name.</param>
        /// <param name="value">Value to assign.</param>
        private static void SetPrivateField<TTarget, TValue>(TTarget target, string fieldName, TValue value)
            where TTarget : Component
        {
            if (target == null)
            {
                return;
            }

            System.Reflection.FieldInfo field = typeof(TTarget).GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            field?.SetValue(target, value);
        }
    }
}
