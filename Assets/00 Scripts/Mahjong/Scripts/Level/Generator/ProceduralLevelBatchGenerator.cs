using System;
using System.Collections.Generic;
using MahjongOut3D.Data;
using MahjongOut3D.TileSystem;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MahjongOut3D.LevelSystem
{
    /// <summary>
    /// Generates many voxel-backed Mahjong level assets with different difficulty tiers from a single asset.
    /// </summary>
    [CreateAssetMenu(menuName = "Mahjong Out 3D/Level/Procedural Batch Generator", fileName = "ProceduralLevelBatchGenerator")]
    public sealed class ProceduralLevelBatchGenerator : ScriptableObject
    {
        private const int MaxSolvableGenerationAttemptsPerLevel = 32;

        private static readonly Vector3Int[] NeighborDirections =
        {
            new Vector3Int(1, 0, 0),
            new Vector3Int(-1, 0, 0),
            new Vector3Int(0, 1, 0),
            new Vector3Int(0, -1, 0),
            new Vector3Int(0, 0, 1),
            new Vector3Int(0, 0, -1),
        };

        [SerializeField] private LevelCatalog targetCatalog;
        [SerializeField] private VoxelGridLayoutSettings layoutOverride;
        [SerializeField] private MahjongTile tilePrefab;
        [SerializeField] private ManualTileShape authoredShape;
        [Header("Surface Tile Spacing")]
        [SerializeField, Min(0f)] private float surfaceTileGap = 0.03f;
        [SerializeField, Min(0f)] private float leftRightSurfaceGapOffset = 0f;
        [SerializeField, Min(0f)] private float upDownSurfaceGapOffset = 0f;
        [SerializeField, Min(0f)] private float frontBackSurfaceGapOffset = 0f;
        [SerializeField] private string outputFolder = "Assets/00 Scripts/Mahjong/Generated Levels";
        [SerializeField] private string levelNamePrefix = "Generated";
        [SerializeField] private int seed = 20260730;
        [SerializeField] private TextAsset jsonGenerationConfig;
        [SerializeField] private string googleSheetUrl;
        [SerializeField] private GenerationWriteMode generationWriteMode = GenerationWriteMode.GenerateNew;
        [SerializeField] private DifficultyBatchDefinition easySettings = DifficultyBatchDefinition.CreateEasyDefaults();
        [SerializeField] private DifficultyBatchDefinition normalSettings = DifficultyBatchDefinition.CreateNormalDefaults();
        [SerializeField] private DifficultyBatchDefinition hardSettings = DifficultyBatchDefinition.CreateHardDefaults();
        [SerializeField] private DifficultyBatchDefinition superHardSettings = DifficultyBatchDefinition.CreateSuperHardDefaults();

        public enum GenerationWriteMode
        {
            GenerateNew = 0,
            OverwriteMatching = 1,
        }

        public GenerationWriteMode WriteMode => generationWriteMode;
        public ManualTileShape AuthoredShape => authoredShape;

        /// <summary>
        /// Describes how many levels to generate for one difficulty tier and the shell-layer counts that tier may use.
        /// </summary>
        [Serializable]
        public sealed class DifficultyBatchDefinition
        {
            [SerializeField] private string label = "Normal";
            [SerializeField] private int levelCount = 5;
            [SerializeField] private int minLayerCount = 4;
            [SerializeField] private int maxLayerCount = 5;
            [SerializeField] private int minPairCount = 22;
            [SerializeField] private int maxPairCount = 24;
            [SerializeField] private float flippedTileChance = 0.5f;
            [SerializeField] private LevelShapeType shape = LevelShapeType.Cube;
            [SerializeField] private List<LevelShapeType> allowedShapes = new List<LevelShapeType>();
            [SerializeField] private LevelDifficulty difficulty = LevelDifficulty.Normal;

            /// <summary>
            /// Gets the display label used in generated asset names.
            /// </summary>
            public string Label => string.IsNullOrWhiteSpace(label) ? difficulty.ToString() : label;

            /// <summary>
            /// Gets how many levels should be generated for this tier.
            /// </summary>
            public int LevelCount => Mathf.Max(0, levelCount);

            /// <summary>
            /// Gets the minimum shell-layer count allowed for this tier.
            /// </summary>
            public int MinLayerCount => Mathf.Max(1, minLayerCount);

            /// <summary>
            /// Gets the maximum shell-layer count allowed for this tier.
            /// </summary>
            public int MaxLayerCount => Mathf.Max(MinLayerCount, maxLayerCount);

            /// <summary>
            /// Gets the minimum pair count allowed for this tier.
            /// </summary>
            public int MinPairCount => Mathf.Max(1, minPairCount);

            /// <summary>
            /// Gets the maximum pair count allowed for this tier.
            /// </summary>
            public int MaxPairCount => Mathf.Max(MinPairCount, maxPairCount);

            /// <summary>
            /// Gets the chance that a tile will be rotated 180 degrees around Y.
            /// </summary>
            public float FlippedTileChance => Mathf.Clamp01(flippedTileChance);

            /// <summary>
            /// Gets the level shape label stored in generated assets.
            /// </summary>
            public LevelShapeType Shape => shape;

            /// <summary>
            /// Gets the runtime difficulty stored in generated assets.
            /// </summary>
            public LevelDifficulty Difficulty => difficulty;

            /// <summary>
            /// Chooses a random shape from the configured pool for this tier.
            /// </summary>
            public LevelShapeType GetRandomShape(System.Random random)
            {
                SanitizeShapeConfiguration();

                LevelShapeType selectedShape;
                if (allowedShapes != null && allowedShapes.Count > 0)
                {
                    selectedShape = allowedShapes[random.Next(0, allowedShapes.Count)];
                }
                else
                {
                    selectedShape = shape;
                }

                LevelShapeType normalizedSelectedShape = NormalizeSupportedShapeType(selectedShape);
                if (normalizedSelectedShape == selectedShape)
                {
                    return normalizedSelectedShape;
                }

                return NormalizeSupportedShapeType(shape);
            }

            /// <summary>
            /// Normalizes stale serialized shape values after enum members are removed.
            /// </summary>
            public void SanitizeShapeConfiguration()
            {
                shape = NormalizeSupportedShapeType(shape);

                if (allowedShapes == null)
                {
                    allowedShapes = new List<LevelShapeType>();
                    return;
                }

                HashSet<LevelShapeType> seenShapes = new HashSet<LevelShapeType>();
                for (int index = allowedShapes.Count - 1; index >= 0; index--)
                {
                    LevelShapeType normalizedShape = NormalizeSupportedShapeType(allowedShapes[index]);
                    if (seenShapes.Contains(normalizedShape))
                    {
                        allowedShapes.RemoveAt(index);
                        continue;
                    }

                    allowedShapes[index] = normalizedShape;
                    seenShapes.Add(normalizedShape);
                }
            }

            /// <summary>
            /// Creates the default Easy tier settings.
            /// </summary>
            public static DifficultyBatchDefinition CreateEasyDefaults()
            {
                return new DifficultyBatchDefinition
                {
                    label = "Easy",
                    levelCount = 5,
                    minLayerCount = 1,
                    maxLayerCount = 2,
                    minPairCount = 12,
                    maxPairCount = 18,
                    flippedTileChance = 0.35f,
                    shape = LevelShapeType.Cube,
                    allowedShapes = new List<LevelShapeType>(),
                    difficulty = LevelDifficulty.Easy,
                };
            }

            /// <summary>
            /// Creates the default Normal tier settings.
            /// </summary>
            public static DifficultyBatchDefinition CreateNormalDefaults()
            {
                return new DifficultyBatchDefinition
                {
                    label = "Normal",
                    levelCount = 5,
                    minLayerCount = 4,
                    maxLayerCount = 5,
                    minPairCount = 22,
                    maxPairCount = 24,
                    flippedTileChance = 0.45f,
                    shape = LevelShapeType.Cube,
                    allowedShapes = new List<LevelShapeType>(),
                    difficulty = LevelDifficulty.Normal,
                };
            }

            /// <summary>
            /// Creates the default Hard tier settings.
            /// </summary>
            public static DifficultyBatchDefinition CreateHardDefaults()
            {
                return new DifficultyBatchDefinition
                {
                    label = "Hard",
                    levelCount = 5,
                    minLayerCount = 5,
                    maxLayerCount = 7,
                    minPairCount = 40,
                    maxPairCount = 60,
                    flippedTileChance = 0.55f,
                    shape = LevelShapeType.Cube,
                    allowedShapes = new List<LevelShapeType>(),
                    difficulty = LevelDifficulty.Hard,
                };
            }

            /// <summary>
            /// Creates the default Super Hard tier settings.
            /// </summary>
            public static DifficultyBatchDefinition CreateSuperHardDefaults()
            {
                return new DifficultyBatchDefinition
                {
                    label = "SuperHard",
                    levelCount = 5,
                    minLayerCount = 7,
                    maxLayerCount = 9,
                    minPairCount = 72,
                    maxPairCount = 120,
                    flippedTileChance = 0.65f,
                    shape = LevelShapeType.Cube,
                    allowedShapes = new List<LevelShapeType>(),
                    difficulty = LevelDifficulty.Expert,
                };
            }

            /// <summary>
            /// Creates a one-off level request from a JSON-config row.
            /// </summary>
            public static DifficultyBatchDefinition CreateCustom(string label, int layerCount, int minPairCount, int maxPairCount, LevelShapeType shape, LevelDifficulty difficulty)
            {
                int resolvedLayerCount = Mathf.Max(1, layerCount);
                int resolvedMinPairCount = Mathf.Max(1, minPairCount);
                int resolvedMaxPairCount = Mathf.Max(resolvedMinPairCount, maxPairCount);
                return new DifficultyBatchDefinition
                {
                    label = string.IsNullOrWhiteSpace(label) ? difficulty.ToString() : label.Trim(),
                    levelCount = 1,
                    minLayerCount = resolvedLayerCount,
                    maxLayerCount = resolvedLayerCount,
                    minPairCount = resolvedMinPairCount,
                    maxPairCount = resolvedMaxPairCount,
                    flippedTileChance = ResolveDefaultFlippedTileChance(difficulty),
                    shape = NormalizeSupportedShapeType(shape),
                    allowedShapes = new List<LevelShapeType>(),
                    difficulty = difficulty,
                };
            }

            private static float ResolveDefaultFlippedTileChance(LevelDifficulty difficulty)
            {
                switch (difficulty)
                {
                    case LevelDifficulty.Easy:
                        return 0.35f;
                    case LevelDifficulty.Normal:
                        return 0.45f;
                    case LevelDifficulty.Hard:
                        return 0.55f;
                    case LevelDifficulty.Expert:
                    default:
                        return 0.65f;
                }
            }
        }

        private static LevelShapeType NormalizeSupportedShapeType(LevelShapeType shape)
        {
            switch (shape)
            {
                case LevelShapeType.Cube:
                case LevelShapeType.Heart:
                case LevelShapeType.Cylinder:
                case LevelShapeType.Rectangle:
                case LevelShapeType.T:
                case LevelShapeType.H:
                case LevelShapeType.I:
                case LevelShapeType.L:
                case LevelShapeType.Pyramid:
                case LevelShapeType.Dome:
                case LevelShapeType.Ramp:
                    return shape;

                default:
                    return LevelShapeType.Cube;
            }
        }

        /// <summary>
        /// Caches one generated shape candidate before tile definitions are created.
        /// </summary>
        private sealed class ShapeCandidate
        {
            /// <summary>
            /// Gets or sets the selected voxel grid size.
            /// </summary>
            public VoxelGridSize GridSize { get; set; }

            /// <summary>
            /// Gets or sets the selected silhouette type.
            /// </summary>
            public LevelShapeType Shape { get; set; }

            /// <summary>
            /// Gets or sets how many shell layers this candidate was authored to expose.
            /// </summary>
            public int TargetLayerCount { get; set; }

            /// <summary>
            /// Gets or sets the selected complete-shell tile count.
            /// </summary>
            public int TargetTileCount { get; set; }

            /// <summary>
            /// Gets or sets the shell list from outside to inside.
            /// </summary>
            public List<List<TilePlacementData>> Shells { get; set; } = new List<List<TilePlacementData>>();

            /// <summary>
            /// Gets the total amount of voxels represented by the candidate.
            /// </summary>
            public int TileCapacity
            {
                get
                {
                    int total = 0;
                    for (int index = 0; index < Shells.Count; index++)
                    {
                        total += Shells[index].Count;
                    }

                    return total;
                }
            }
        }

        /// <summary>
        /// Stores a fully generated level payload before it is written to a ScriptableObject asset.
        /// </summary>
        public sealed class GeneratedLevelData
        {
            /// <summary>
            /// Gets or sets the generated level name.
            /// </summary>
            public string LevelName { get; set; }

            /// <summary>
            /// Gets or sets the generated grid size.
            /// </summary>
            public VoxelGridSize GridSize { get; set; }

            /// <summary>
            /// Gets or sets the generated layout override.
            /// </summary>
            public VoxelGridLayoutSettings LayoutOverride { get; set; }

            /// <summary>
            /// Gets or sets the generated shape tag.
            /// </summary>
            public LevelShapeType Shape { get; set; }

            /// <summary>
            /// Gets or sets the generated difficulty tag.
            /// </summary>
            public LevelDifficulty Difficulty { get; set; }

            /// <summary>
            /// Gets or sets whether the level should be treated as a surface-placement layout at runtime.
            /// </summary>
            public bool UseSurfaceTilePlacement { get; set; }

            /// <summary>
            /// Gets or sets the generated tile list.
            /// </summary>
            public List<LevelTileDefinition> Tiles { get; set; } = new List<LevelTileDefinition>();

            /// <summary>
            /// Gets or sets the generated shell-layer count.
            /// </summary>
            public int LayerCount { get; set; }

            /// <summary>
            /// Gets or sets the desired catalog slot for this generated level.
            /// </summary>
            public int CatalogIndex { get; set; } = -1;

            /// <summary>
            /// Gets or sets the duplicated runtime block count.
            /// </summary>
            public int BlockCount { get; set; } = 1;

            /// <summary>
            /// Gets or sets the grid spacing between duplicated runtime blocks.
            /// </summary>
            public int BlockSpacingCells { get; set; } = 1;

            /// <summary>
            /// Gets or sets the face-down tile ratio for this generated level.
            /// </summary>
            public float FaceDownTileRatio { get; set; }

            /// <summary>
            /// Gets or sets the combo tile ratio for this generated level.
            /// </summary>
            public float ComboTileRatio { get; set; }

            /// <summary>
            /// Gets or sets the fill categories constrained for this generated level.
            /// </summary>
            public List<string> FillCategoryNames { get; set; } = new List<string>();
        }

        /// <summary>
        /// Wraps a set of single-level JSON generation rows.
        /// </summary>
        [Serializable]
        public sealed class JsonGenerationConfig
        {
            public List<JsonGenerationLevelEntry> levels = new List<JsonGenerationLevelEntry>();
        }

        /// <summary>
        /// Describes one JSON-authored level generation row.
        /// </summary>
        [Serializable]
        public sealed class JsonGenerationLevelEntry
        {
            public string levelName;
            public int levelIndex;
            public string difficulty = "Easy";
            public int layerCount = 1;
            public int categoryCount = 1;
            public int minPair = 10;
            public int maxPair = 20;
            public int uniquePair = 10;
            public string shape = "Cube";
            public float faceDown;
            public int totalMatchedCount;
            public int blockCount = 1;
            public int blockSpacingCells = 1;
            public float comboTileRatio;
            public List<string> fillCategoryNames = new List<string>();
        }

        /// <summary>
        /// Stores one generated tile placement including its outward-facing shell direction.
        /// </summary>
        internal sealed class TilePlacementData
        {
            /// <summary>
            /// Gets or sets the source voxel coordinate that owns this face slot.
            /// </summary>
            public Vector3Int Coordinate { get; set; }

            /// <summary>
            /// Gets or sets the direction the tile face should point toward.
            /// </summary>
            public VoxelGridDirection FacingDirection { get; set; }

            /// <summary>
            /// Gets or sets the nesting depth of this tile shell, where zero is the outermost shell.
            /// </summary>
            public int ShellIndex { get; set; }

            /// <summary>
            /// Gets or sets the split-slot index on a single exposed face. Negative means centered on the face.
            /// </summary>
            public int SurfaceSlotIndex { get; set; }

            /// <summary>
            /// Gets or sets a custom local position override used by direct shell generators.
            /// </summary>
            public Vector3 CustomLocalPosition { get; set; }

            /// <summary>
            /// Gets or sets a custom local rotation override used by direct shell generators.
            /// </summary>
            public Vector3 CustomLocalEulerAngles { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether the custom local position override should be used.
            /// </summary>
            public bool UseCustomLocalPosition { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether the custom local rotation override should be used.
            /// </summary>
            public bool UseCustomLocalEulerAngles { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether nested shell compaction should still be applied to the custom local position.
            /// </summary>
            public bool ApplyShellCompaction { get; set; }
        }

        /// <summary>
        /// Stores the tile face and thickness dimensions used to build square cube shells.
        /// </summary>
        internal readonly struct CubeTileMetrics
        {
            public CubeTileMetrics(float faceWidth, float faceHeight, float thickness)
            {
                FaceWidth = Mathf.Max(0.01f, faceWidth);
                FaceHeight = Mathf.Max(0.01f, faceHeight);
                Thickness = Mathf.Max(0.01f, thickness);
            }

            public float FaceWidth { get; }

            public float FaceHeight { get; }

            public float Thickness { get; }
        }

        /// <summary>
        /// Stores the resolved tile grid used by one cube shell.
        /// </summary>
        private readonly struct CubeShellPlan
        {
            public CubeShellPlan(int columnCount, int rowCount, float sideLength)
            {
                ColumnCount = Mathf.Max(1, columnCount);
                RowCount = Mathf.Max(1, rowCount);
                SideLength = Mathf.Max(0.01f, sideLength);
            }

            public int ColumnCount { get; }

            public int RowCount { get; }

            public float SideLength { get; }
        }

        /// <summary>
        /// Creates a full batch of levels for all configured difficulty tiers.
        /// </summary>
        /// <returns>Generated level payloads.</returns>
        public List<GeneratedLevelData> GenerateLevelData(int startingSequence = 0)
        {
            SanitizeSerializedConfiguration();

            List<GeneratedLevelData> results = new List<GeneratedLevelData>();
            System.Random random = new System.Random(seed);
            int sequence = Mathf.Max(0, startingSequence);

            AppendGeneratedTier(results, easySettings, ref sequence, random);
            AppendGeneratedTier(results, normalSettings, ref sequence, random);
            AppendGeneratedTier(results, hardSettings, ref sequence, random);
            AppendGeneratedTier(results, superHardSettings, ref sequence, random);
            return results;
        }

        private void OnValidate()
        {
            SanitizeSerializedConfiguration();
        }

        private void SanitizeSerializedConfiguration()
        {
            if (easySettings == null)
            {
                easySettings = DifficultyBatchDefinition.CreateEasyDefaults();
            }

            if (normalSettings == null)
            {
                normalSettings = DifficultyBatchDefinition.CreateNormalDefaults();
            }

            if (hardSettings == null)
            {
                hardSettings = DifficultyBatchDefinition.CreateHardDefaults();
            }

            if (superHardSettings == null)
            {
                superHardSettings = DifficultyBatchDefinition.CreateSuperHardDefaults();
            }

            easySettings.SanitizeShapeConfiguration();
            normalSettings.SanitizeShapeConfiguration();
            hardSettings.SanitizeShapeConfiguration();
            superHardSettings.SanitizeShapeConfiguration();
        }

#if UNITY_EDITOR
        /// <summary>
        /// Generates ScriptableObject level assets and writes them into the configured output folder.
        /// </summary>
        /// <returns>Created level asset references.</returns>
        public List<LevelDefinition> GenerateAssets()
        {
            return GenerateAssets(generationWriteMode);
        }

        /// <summary>
        /// Generates ScriptableObject level assets and writes them into the configured output folder.
        /// </summary>
        /// <returns>Created or updated level asset references.</returns>
        public List<LevelDefinition> GenerateAssets(GenerationWriteMode writeMode)
        {
            if (targetCatalog == null)
            {
                throw new InvalidOperationException("ProceduralLevelBatchGenerator requires a target LevelCatalog.");
            }

            if (string.IsNullOrWhiteSpace(outputFolder) || !outputFolder.StartsWith("Assets", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Output folder must be a valid Unity project path starting with 'Assets'.");
            }

            EnsureFolderExists(outputFolder);
            int startingSequence = writeMode == GenerationWriteMode.GenerateNew ? GetHighestExistingSequence() : 0;
            List<GeneratedLevelData> generatedData = GenerateLevelData(startingSequence);
            return WriteGeneratedAssets(generatedData, writeMode);
        }

        /// <summary>
        /// Generates ScriptableObject level assets from the configured JSON row list.
        /// </summary>
        public List<LevelDefinition> GenerateAssetsFromJsonConfig()
        {
            return GenerateAssetsFromJsonConfig(generationWriteMode);
        }

        /// <summary>
        /// Generates ScriptableObject level assets from the configured JSON row list.
        /// </summary>
        public List<LevelDefinition> GenerateAssetsFromJsonConfig(GenerationWriteMode writeMode)
        {
            if (targetCatalog == null)
            {
                throw new InvalidOperationException("ProceduralLevelBatchGenerator requires a target LevelCatalog.");
            }

            if (jsonGenerationConfig == null || string.IsNullOrWhiteSpace(jsonGenerationConfig.text))
            {
                throw new InvalidOperationException("Assign a JSON generation config TextAsset before generating levels from JSON.");
            }

            if (string.IsNullOrWhiteSpace(outputFolder) || !outputFolder.StartsWith("Assets", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Output folder must be a valid Unity project path starting with 'Assets'.");
            }

            EnsureFolderExists(outputFolder);
            List<GeneratedLevelData> generatedData = GenerateLevelDataFromJsonConfig(jsonGenerationConfig.text);
            return WriteGeneratedAssets(generatedData, writeMode);
        }

        /// <summary>
        /// Generates ScriptableObject level assets from Google Sheet CSV content.
        /// </summary>
        public List<LevelDefinition> GenerateAssetsFromGoogleSheetCsv(string csvText)
        {
            return GenerateAssetsFromGoogleSheetCsv(csvText, generationWriteMode);
        }

        /// <summary>
        /// Generates ScriptableObject level assets from Google Sheet CSV content.
        /// </summary>
        public List<LevelDefinition> GenerateAssetsFromGoogleSheetCsv(string csvText, GenerationWriteMode writeMode)
        {
            if (targetCatalog == null)
            {
                throw new InvalidOperationException("ProceduralLevelBatchGenerator requires a target LevelCatalog.");
            }

            if (string.IsNullOrWhiteSpace(csvText))
            {
                throw new InvalidOperationException("Google Sheet CSV content is empty.");
            }

            if (string.IsNullOrWhiteSpace(outputFolder) || !outputFolder.StartsWith("Assets", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Output folder must be a valid Unity project path starting with 'Assets'.");
            }

            EnsureFolderExists(outputFolder);
            List<GeneratedLevelData> generatedData = GenerateLevelDataFromEntries(ParseGoogleSheetEntries(csvText));
            return WriteGeneratedAssets(generatedData, writeMode);
        }

        /// <summary>
        /// Resolves the configured Google Sheet URL into a CSV download URL when possible.
        /// </summary>
        public string GetResolvedGoogleSheetCsvUrl()
        {
            return ResolveGoogleSheetCsvUrl(googleSheetUrl);
        }

        private List<LevelDefinition> WriteGeneratedAssets(List<GeneratedLevelData> generatedData, GenerationWriteMode writeMode)
        {
            List<LevelDefinition> generatedAssets = new List<LevelDefinition>(generatedData.Count);

            for (int index = 0; index < generatedData.Count; index++)
            {
                GeneratedLevelData data = generatedData[index];
                string safeLevelName = SanitizeFileName(data.LevelName);
                string assetPath = $"{outputFolder}/{safeLevelName}.asset";

                if (writeMode == GenerationWriteMode.GenerateNew)
                {
                    assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);
                }

                LevelDefinition asset = writeMode == GenerationWriteMode.OverwriteMatching
                    ? AssetDatabase.LoadAssetAtPath<LevelDefinition>(assetPath)
                    : null;

                if (asset == null)
                {
                    asset = CreateInstance<LevelDefinition>();
                    ApplyGeneratedData(asset, data);
                    AssetDatabase.CreateAsset(asset, assetPath);
                }
                else
                {
                    ApplyGeneratedData(asset, data);
                }

                generatedAssets.Add(asset);
            }

            ApplyCatalogEntries(generatedAssets, generatedData);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.SetDirty(this);
            return generatedAssets;
        }
#endif

        /// <summary>
        /// Appends generated levels for a single difficulty tier.
        /// </summary>
        private void AppendGeneratedTier(List<GeneratedLevelData> results, DifficultyBatchDefinition settings, ref int sequence, System.Random random)
        {
            if (settings == null || settings.LevelCount <= 0)
            {
                return;
            }

            for (int index = 0; index < settings.LevelCount; index++)
            {
                sequence++;
                ShapeCandidate candidate = null;
                VoxelGridSize logicalGridSize = default;
                List<LevelTileDefinition> tileDefinitions = null;
                bool solved = false;

                for (int attempt = 0; attempt < MaxSolvableGenerationAttemptsPerLevel; attempt++)
                {
                    candidate = authoredShape != null
                        ? CreateAuthoredShapeCandidate()
                        : CreateShapeCandidate(settings, random);
                    if (candidate == null)
                    {
                        continue;
                    }

                    int tileCount = candidate.TargetTileCount;
                    List<TilePlacementData> occupiedCoordinates = authoredShape != null
                        ? FlattenAuthoredShapeShells(candidate.Shells)
                        : BuildOccupiedCoordinates(candidate.Shape, candidate.Shells, tileCount, random);
                    logicalGridSize = authoredShape != null
                        ? candidate.GridSize
                        : BuildLogicalGridSize(occupiedCoordinates.Count);

                    if (TryBuildTileDefinitions(candidate.Shape, occupiedCoordinates, candidate.GridSize, logicalGridSize, settings, random, out tileDefinitions))
                    {
                        solved = true;
                        break;
                    }
                }

                if (!solved || candidate == null || tileDefinitions == null)
                {
                    throw new InvalidOperationException($"Failed to generate a solvable level for difficulty '{settings.Label}' after {MaxSolvableGenerationAttemptsPerLevel} attempts.");
                }

                int layerCount = GetLayerCount(tileDefinitions);
                results.Add(new GeneratedLevelData
                {
                    LevelName = $"{levelNamePrefix}_{settings.Label}_{sequence:000}",
                    GridSize = ResolveSerializedGridSize(tileDefinitions, logicalGridSize),
                    LayoutOverride = authoredShape != null && authoredShape.LayoutOverride != null
                        ? authoredShape.LayoutOverride
                        : layoutOverride,
                    Shape = candidate.Shape,
                    Difficulty = settings.Difficulty,
                    UseSurfaceTilePlacement = ShouldUseSurfaceTilePlacement(candidate.Shape, layerCount),
                    Tiles = tileDefinitions,
                    LayerCount = layerCount,
                    BlockCount = 1,
                    BlockSpacingCells = 1,
                });
            }
        }

        private List<GeneratedLevelData> GenerateLevelDataFromJsonConfig(string json)
        {
            SanitizeSerializedConfiguration();

            JsonGenerationConfig config = ParseJsonGenerationConfig(json);
            if (config == null || config.levels == null || config.levels.Count == 0)
            {
                throw new InvalidOperationException("JSON generation config is empty. Add at least one row inside 'levels'.");
            }

            return GenerateLevelDataFromEntries(config.levels);
        }

        private List<GeneratedLevelData> GenerateLevelDataFromEntries(List<JsonGenerationLevelEntry> entries)
        {
            SanitizeSerializedConfiguration();

            if (entries == null || entries.Count == 0)
            {
                throw new InvalidOperationException("Level generation entries are empty.");
            }

            List<string> availableFillCategoryNames = new List<string>();
#if UNITY_EDITOR
            availableFillCategoryNames = ResolveAvailableFillCategoryNames();
#endif
            List<GeneratedLevelData> results = new List<GeneratedLevelData>(entries.Count);
            System.Random random = new System.Random(seed);

            for (int index = 0; index < entries.Count; index++)
            {
                JsonGenerationLevelEntry entry = entries[index];
                if (entry == null)
                {
                    continue;
                }

                results.Add(GenerateConfiguredLevelData(entry, availableFillCategoryNames, random));
            }

            return results;
        }

        private static List<JsonGenerationLevelEntry> ParseGoogleSheetEntries(string csvText)
        {
            List<string[]> rows = CSVReader.ReadCSV(csvText);
            if (rows == null || rows.Count == 0)
            {
                throw new InvalidOperationException("Google Sheet CSV has no rows.");
            }

            int headerRowIndex = FindFirstNonEmptyRow(rows);
            if (headerRowIndex < 0)
            {
                throw new InvalidOperationException("Google Sheet CSV has no header row.");
            }

            Dictionary<string, int> headerMap = BuildSheetHeaderMap(rows[headerRowIndex]);
            if (!headerMap.ContainsKey("levelindex"))
            {
                throw new InvalidOperationException("Google Sheet must contain a 'Level Index' column.");
            }

            List<JsonGenerationLevelEntry> entries = new List<JsonGenerationLevelEntry>();
            for (int rowIndex = headerRowIndex + 1; rowIndex < rows.Count; rowIndex++)
            {
                string[] row = rows[rowIndex];
                if (row == null || IsSheetRowEmpty(row))
                {
                    continue;
                }

                string levelIndexValue = GetSheetCell(row, headerMap, "levelindex");
                if (string.IsNullOrWhiteSpace(levelIndexValue))
                {
                    continue;
                }

                entries.Add(new JsonGenerationLevelEntry
                {
                    levelName = GetSheetCell(row, headerMap, "levelname"),
                    levelIndex = ParseIntOrDefault(levelIndexValue, 0),
                    difficulty = GetSheetCell(row, headerMap, "difficulty", "easy") ?? "Easy",
                    layerCount = ParseIntOrDefault(GetSheetCell(row, headerMap, "layercount", "layers", "layer"), 1),
                    categoryCount = ParseIntOrDefault(GetSheetCell(row, headerMap, "categorycount", "categories"), 1),
                    minPair = ParseIntOrDefault(GetSheetCell(row, headerMap, "minpair"), 10),
                    maxPair = ParseIntOrDefault(GetSheetCell(row, headerMap, "maxpair"), 20),
                    uniquePair = ParseIntOrDefault(GetSheetCell(row, headerMap, "uniquepair"), -1),
                    shape = GetSheetCell(row, headerMap, "shape", "shapetype") ?? "Cube",
                    faceDown = ParseFloatOrDefault(GetSheetCell(row, headerMap, "facedown", "facedownratio"), 0f),
                    totalMatchedCount = ParseIntOrDefault(GetSheetCell(row, headerMap, "totalmatchedcount"), 0),
                    blockCount = ParseIntOrDefault(GetSheetCell(row, headerMap, "blockcount"), 1),
                    blockSpacingCells = ParseIntOrDefault(GetSheetCell(row, headerMap, "blockspacingcells", "blockspacing"), 1),
                    comboTileRatio = ParseFloatOrDefault(GetSheetCell(row, headerMap, "combotileratio"), 0f),
                    fillCategoryNames = ParseFillCategoryNames(GetSheetCell(row, headerMap, "fillcategorynames", "fillcategories", "categorynames")),
                });
            }

            if (entries.Count == 0)
            {
                throw new InvalidOperationException("Google Sheet CSV has no valid data rows under the header.");
            }

            return entries;
        }

        private GeneratedLevelData GenerateConfiguredLevelData(JsonGenerationLevelEntry entry, List<string> availableFillCategoryNames, System.Random random)
        {
            LevelDifficulty difficulty = ParseDifficultyOrDefault(entry != null ? entry.difficulty : null);
            LevelShapeType shape = ParseShapeOrDefault(entry != null ? entry.shape : null);
            DifficultyBatchDefinition settings = DifficultyBatchDefinition.CreateCustom(
                entry != null ? entry.difficulty : null,
                entry != null ? entry.layerCount : 1,
                entry != null ? entry.minPair : 1,
                entry != null ? entry.maxPair : 1,
                shape,
                difficulty);

            ShapeCandidate candidate = null;
            VoxelGridSize logicalGridSize = default;
            List<LevelTileDefinition> tileDefinitions = null;
            bool solved = false;

            for (int attempt = 0; attempt < MaxSolvableGenerationAttemptsPerLevel; attempt++)
            {
                candidate = CreateShapeCandidate(settings, random);
                if (candidate == null)
                {
                    continue;
                }

                int tileCount = candidate.TargetTileCount;
                List<TilePlacementData> occupiedCoordinates = BuildOccupiedCoordinates(candidate.Shape, candidate.Shells, tileCount, random);
                logicalGridSize = BuildLogicalGridSize(occupiedCoordinates.Count);

                if (TryBuildTileDefinitions(shape, occupiedCoordinates, candidate.GridSize, logicalGridSize, settings, random, out tileDefinitions, entry != null ? entry.uniquePair : -1))
                {
                    solved = true;
                    break;
                }
            }

            if (!solved || candidate == null || tileDefinitions == null)
            {
                string levelLabel = entry != null && !string.IsNullOrWhiteSpace(entry.levelName)
                    ? entry.levelName
                    : $"levelIndex {Mathf.Max(0, entry != null ? entry.levelIndex : 0)}";
                throw new InvalidOperationException($"Failed to generate a solvable JSON-config level for {levelLabel}. Check pair bounds, layer count, and uniquePair settings.");
            }

            int layerCount = GetLayerCount(tileDefinitions);
            return new GeneratedLevelData
            {
                LevelName = ResolveGeneratedLevelName(entry, difficulty),
                CatalogIndex = Mathf.Max(0, entry != null ? entry.levelIndex : 0),
                GridSize = ResolveSerializedGridSize(tileDefinitions, logicalGridSize),
                LayoutOverride = layoutOverride,
                Shape = candidate.Shape,
                Difficulty = difficulty,
                UseSurfaceTilePlacement = ShouldUseSurfaceTilePlacement(candidate.Shape, layerCount),
                Tiles = tileDefinitions,
                LayerCount = layerCount,
                BlockCount = Mathf.Max(1, entry != null ? entry.blockCount : 1),
                BlockSpacingCells = Mathf.Max(0, entry != null ? entry.blockSpacingCells : 1),
                FaceDownTileRatio = NormalizeRatio(entry != null ? entry.faceDown : 0f),
                ComboTileRatio = NormalizeRatio(entry != null ? entry.comboTileRatio : 0f),
                FillCategoryNames = ResolveFillCategoryNames(entry, availableFillCategoryNames, random),
            };
        }

        private string ResolveGeneratedLevelName(JsonGenerationLevelEntry entry, LevelDifficulty difficulty)
        {
            if (entry != null && !string.IsNullOrWhiteSpace(entry.levelName))
            {
                return entry.levelName.Trim();
            }

            int levelIndex = Mathf.Max(0, entry != null ? entry.levelIndex : 0);
            return $"{levelNamePrefix}_{difficulty}_{levelIndex:000}";
        }

        private static JsonGenerationConfig ParseJsonGenerationConfig(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonUtility.FromJson<JsonGenerationConfig>(json);
        }

        private static LevelDifficulty ParseDifficultyOrDefault(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return LevelDifficulty.Easy;
            }

            string normalized = value.Trim();
            if (normalized.Equals("easy", StringComparison.OrdinalIgnoreCase))
            {
                return LevelDifficulty.Easy;
            }

            if (normalized.Equals("normal", StringComparison.OrdinalIgnoreCase))
            {
                return LevelDifficulty.Normal;
            }

            if (normalized.Equals("hard", StringComparison.OrdinalIgnoreCase))
            {
                return LevelDifficulty.Hard;
            }

            if (normalized.Equals("superhard", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("expert", StringComparison.OrdinalIgnoreCase))
            {
                return LevelDifficulty.Expert;
            }

            return LevelDifficulty.Easy;
        }

        private static LevelShapeType ParseShapeOrDefault(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return LevelShapeType.Cube;
            }

            string normalized = value.Trim();
            if (normalized.Equals("cube", StringComparison.OrdinalIgnoreCase))
            {
                return LevelShapeType.Cube;
            }

            if (normalized.Equals("heart", StringComparison.OrdinalIgnoreCase))
            {
                return LevelShapeType.Heart;
            }

            if (normalized.Equals("cylinder", StringComparison.OrdinalIgnoreCase))
            {
                return LevelShapeType.Cylinder;
            }

            if (normalized.Equals("pyramid", StringComparison.OrdinalIgnoreCase))
            {
                return LevelShapeType.Pyramid;
            }

            if (normalized.Equals("dome", StringComparison.OrdinalIgnoreCase) || normalized.Equals("sphere", StringComparison.OrdinalIgnoreCase))
            {
                return LevelShapeType.Dome;
            }

            if (normalized.Equals("ramp", StringComparison.OrdinalIgnoreCase) || normalized.Equals("slope", StringComparison.OrdinalIgnoreCase))
            {
                return LevelShapeType.Ramp;
            }

            if (normalized.Equals("rectangle", StringComparison.OrdinalIgnoreCase) || normalized.Equals("rect", StringComparison.OrdinalIgnoreCase))
            {
                return LevelShapeType.Rectangle;
            }

            if (normalized.Equals("t", StringComparison.OrdinalIgnoreCase))
            {
                return LevelShapeType.T;
            }

            if (normalized.Equals("h", StringComparison.OrdinalIgnoreCase))
            {
                return LevelShapeType.H;
            }

            if (normalized.Equals("i", StringComparison.OrdinalIgnoreCase))
            {
                return LevelShapeType.I;
            }

            if (normalized.Equals("l", StringComparison.OrdinalIgnoreCase))
            {
                return LevelShapeType.L;
            }

            return LevelShapeType.Cube;
        }

        private static float NormalizeRatio(float rawValue)
        {
            if (rawValue > 1f && rawValue <= 100f)
            {
                return Mathf.Clamp01(rawValue / 100f);
            }

            return Mathf.Clamp01(rawValue);
        }

        private static List<string> ResolveFillCategoryNames(JsonGenerationLevelEntry entry, List<string> availableFillCategoryNames, System.Random random)
        {
            List<string> resolvedNames = new List<string>();
            if (entry != null && entry.fillCategoryNames != null)
            {
                for (int index = 0; index < entry.fillCategoryNames.Count; index++)
                {
                    string categoryName = entry.fillCategoryNames[index];
                    if (string.IsNullOrWhiteSpace(categoryName))
                    {
                        continue;
                    }

                    string trimmedName = categoryName.Trim();
                    if (!ContainsIgnoreCase(resolvedNames, trimmedName))
                    {
                        resolvedNames.Add(trimmedName);
                    }
                }
            }

            int categoryCount = Mathf.Max(0, entry != null ? entry.categoryCount : 0);
            if (categoryCount <= 0)
            {
                return resolvedNames;
            }

            if (resolvedNames.Count >= categoryCount)
            {
                if (resolvedNames.Count > categoryCount)
                {
                    resolvedNames.RemoveRange(categoryCount, resolvedNames.Count - categoryCount);
                }

                return resolvedNames;
            }

            if (availableFillCategoryNames == null || availableFillCategoryNames.Count == 0)
            {
                return resolvedNames;
            }

            List<string> shuffledCandidates = new List<string>(availableFillCategoryNames);
            ShuffleList(shuffledCandidates, random);
            for (int index = 0; index < shuffledCandidates.Count && resolvedNames.Count < categoryCount; index++)
            {
                string candidate = shuffledCandidates[index];
                if (string.IsNullOrWhiteSpace(candidate) || ContainsIgnoreCase(resolvedNames, candidate))
                {
                    continue;
                }

                resolvedNames.Add(candidate.Trim());
            }

            return resolvedNames;
        }

        private static bool ContainsIgnoreCase(List<string> values, string candidate)
        {
            if (values == null || string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            for (int index = 0; index < values.Count; index++)
            {
                if (string.Equals(values[index], candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static int FindFirstNonEmptyRow(List<string[]> rows)
        {
            if (rows == null)
            {
                return -1;
            }

            for (int index = 0; index < rows.Count; index++)
            {
                if (!IsSheetRowEmpty(rows[index]))
                {
                    return index;
                }
            }

            return -1;
        }

        private static bool IsSheetRowEmpty(string[] row)
        {
            if (row == null || row.Length == 0)
            {
                return true;
            }

            for (int index = 0; index < row.Length; index++)
            {
                if (!string.IsNullOrWhiteSpace(row[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static Dictionary<string, int> BuildSheetHeaderMap(string[] headerRow)
        {
            Dictionary<string, int> headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (headerRow == null)
            {
                return headerMap;
            }

            for (int index = 0; index < headerRow.Length; index++)
            {
                string normalizedHeader = NormalizeSheetHeader(headerRow[index]);
                if (string.IsNullOrWhiteSpace(normalizedHeader) || headerMap.ContainsKey(normalizedHeader))
                {
                    continue;
                }

                headerMap.Add(normalizedHeader, index);
            }

            return headerMap;
        }

        private static string NormalizeSheetHeader(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            char[] chars = value.Trim().ToLowerInvariant().ToCharArray();
            System.Text.StringBuilder builder = new System.Text.StringBuilder(chars.Length);
            for (int index = 0; index < chars.Length; index++)
            {
                char current = chars[index];
                if (char.IsLetterOrDigit(current))
                {
                    builder.Append(current);
                }
            }

            return builder.ToString();
        }

        private static string GetSheetCell(string[] row, Dictionary<string, int> headerMap, params string[] aliases)
        {
            if (row == null || headerMap == null || aliases == null)
            {
                return string.Empty;
            }

            for (int aliasIndex = 0; aliasIndex < aliases.Length; aliasIndex++)
            {
                string normalizedAlias = NormalizeSheetHeader(aliases[aliasIndex]);
                if (!headerMap.TryGetValue(normalizedAlias, out int cellIndex))
                {
                    continue;
                }

                if (cellIndex < 0 || cellIndex >= row.Length)
                {
                    return string.Empty;
                }

                return row[cellIndex] != null ? row[cellIndex].Trim() : string.Empty;
            }

            return string.Empty;
        }

        private static int ParseIntOrDefault(string value, int defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            return int.TryParse(value.Trim(), out int parsedValue) ? parsedValue : defaultValue;
        }

        private static float ParseFloatOrDefault(string value, float defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            string normalized = value.Trim().Replace(",", ".");
            return float.TryParse(normalized, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float parsedValue)
                ? parsedValue
                : defaultValue;
        }

        private static List<string> ParseFillCategoryNames(string rawValue)
        {
            List<string> results = new List<string>();
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return results;
            }

            string[] splitValues = rawValue.Split(new[] { '|', ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < splitValues.Length; index++)
            {
                string candidate = splitValues[index].Trim();
                if (string.IsNullOrWhiteSpace(candidate) || ContainsIgnoreCase(results, candidate))
                {
                    continue;
                }

                results.Add(candidate);
            }

            return results;
        }

        public static string ResolveGoogleSheetCsvUrl(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                return string.Empty;
            }

            string trimmedUrl = rawUrl.Trim();
            if (trimmedUrl.IndexOf("output=csv", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmedUrl.IndexOf("format=csv", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return trimmedUrl;
            }

            string spreadsheetId = TryExtractBetween(trimmedUrl, "/d/e/", "/") ?? TryExtractBetween(trimmedUrl, "/d/", "/");
            string gid = TryExtractQueryValue(trimmedUrl, "gid");
            if (string.IsNullOrWhiteSpace(gid))
            {
                gid = TryExtractFragmentValue(trimmedUrl, "gid");
            }

            if (string.IsNullOrWhiteSpace(gid))
            {
                gid = "0";
            }

            if (string.IsNullOrWhiteSpace(spreadsheetId))
            {
                return trimmedUrl;
            }

            if (trimmedUrl.Contains("/d/e/"))
            {
                return $"https://docs.google.com/spreadsheets/d/e/{spreadsheetId}/pub?gid={gid}&single=true&output=csv";
            }

            return $"https://docs.google.com/spreadsheets/d/{spreadsheetId}/export?format=csv&gid={gid}";
        }

        private static string TryExtractBetween(string value, string startToken, string endToken)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(startToken))
            {
                return null;
            }

            int startIndex = value.IndexOf(startToken, StringComparison.OrdinalIgnoreCase);
            if (startIndex < 0)
            {
                return null;
            }

            startIndex += startToken.Length;
            int endIndex = string.IsNullOrWhiteSpace(endToken)
                ? -1
                : value.IndexOf(endToken, startIndex, StringComparison.OrdinalIgnoreCase);

            if (endIndex < 0)
            {
                endIndex = value.Length;
            }

            return endIndex > startIndex ? value.Substring(startIndex, endIndex - startIndex) : null;
        }

        private static string TryExtractQueryValue(string url, string key)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            string token = $"{key}=";
            int queryIndex = url.IndexOf('?');
            if (queryIndex < 0)
            {
                return null;
            }

            string query = url.Substring(queryIndex + 1);
            string[] segments = query.Split('&');
            for (int index = 0; index < segments.Length; index++)
            {
                if (!segments[index].StartsWith(token, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return segments[index].Substring(token.Length);
            }

            return null;
        }

        private static string TryExtractFragmentValue(string url, string key)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            int fragmentIndex = url.IndexOf('#');
            if (fragmentIndex < 0)
            {
                return null;
            }

            string fragment = url.Substring(fragmentIndex + 1);
            string[] segments = fragment.Split('&');
            string token = $"{key}=";
            for (int index = 0; index < segments.Length; index++)
            {
                if (!segments[index].StartsWith(token, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return segments[index].Substring(token.Length);
            }

            return null;
        }

        /// <summary>
        /// Chooses a random shell-layer count inside the bounds defined for the difficulty tier.
        /// </summary>
        private static int GetRandomLayerCount(DifficultyBatchDefinition settings, System.Random random)
        {
            return random.Next(settings.MinLayerCount, settings.MaxLayerCount + 1);
        }

        /// <summary>
        /// Chooses a pair count that fits inside the selected grid.
        /// </summary>
        private static int GetRandomPairCount(DifficultyBatchDefinition settings, int availableTileCount, System.Random random)
        {
            int maxPairsByShape = Mathf.Max(1, availableTileCount / 2);
            int minPairs = Mathf.Min(settings.MinPairCount, maxPairsByShape);
            int maxPairs = Mathf.Min(settings.MaxPairCount, maxPairsByShape);
            if (maxPairs < minPairs)
            {
                maxPairs = minPairs;
            }

            return random.Next(minPairs, maxPairs + 1);
        }

        /// <summary>
        /// Chooses a tile count represented by complete shell layers inside the pair bounds.
        /// </summary>
        private static int GetTargetTileCount(DifficultyBatchDefinition settings, ShapeCandidate candidate)
        {
            if (candidate == null || candidate.Shells == null || candidate.Shells.Count == 0)
            {
                return 0;
            }

            int maxTileCount = settings != null ? Mathf.Max(2, settings.MaxPairCount * 2) : int.MaxValue;
            int minTileCount = settings != null ? Mathf.Max(2, settings.MinPairCount * 2) : 2;
            return GetPreferredCompleteShellTileCount(candidate.Shells, candidate.TargetLayerCount, minTileCount, maxTileCount);
        }

        private static int GetPreferredCompleteShellTileCount(List<List<TilePlacementData>> shells, int minimumLayerCount, int minTileCount, int maxTileCount)
        {
            if (shells == null || shells.Count == 0)
            {
                return 0;
            }

            int cumulativeCount = 0;
            int bestCount = 0;
            int bestLayerCount = 0;
            for (int index = 0; index < shells.Count; index++)
            {
                if (shells[index] == null || shells[index].Count == 0)
                {
                    return bestCount;
                }

                cumulativeCount += shells[index].Count;
                if (cumulativeCount < minTileCount || cumulativeCount > maxTileCount || cumulativeCount % 2 != 0)
                {
                    continue;
                }

                int layerCount = index + 1;
                if (layerCount >= Mathf.Max(1, minimumLayerCount))
                {
                    return cumulativeCount;
                }

                if (layerCount > bestLayerCount)
                {
                    bestCount = cumulativeCount;
                    bestLayerCount = layerCount;
                }
            }

            return bestCount;
        }

        /// <summary>
        /// Chooses a visually complete cube by keeping whole shells from the outermost cube inward.
        /// </summary>
        private static int GetPreferredCubeTileCount(List<List<TilePlacementData>> shells, int minimumLayerCount, int minTileCount, int maxTileCount)
        {
            if (shells == null || shells.Count == 0)
            {
                return 0;
            }

            int cumulativeCount = 0;
            int bestCount = 0;
            int bestLayerCount = 0;
            for (int index = 0; index < shells.Count; index++)
            {
                cumulativeCount += Mathf.Max(0, shells[index].Count);
                if (cumulativeCount < 2 || cumulativeCount % 2 != 0)
                {
                    continue;
                }

                int layerCount = index + 1;
                if (layerCount >= Mathf.Max(1, minimumLayerCount) && cumulativeCount >= minTileCount && cumulativeCount <= maxTileCount)
                {
                    return cumulativeCount;
                }

                if (cumulativeCount <= maxTileCount && layerCount > bestLayerCount)
                {
                    bestCount = cumulativeCount;
                    bestLayerCount = layerCount;
                }
            }

            if (bestCount >= 2)
            {
                return bestCount;
            }

            int fallbackCount = 0;
            int targetLayerIndex = Mathf.Clamp(Mathf.Max(1, minimumLayerCount) - 1, 0, shells.Count - 1);
            for (int index = 0; index <= targetLayerIndex; index++)
            {
                fallbackCount += Mathf.Max(0, shells[index].Count);
            }

            return fallbackCount % 2 == 0 ? fallbackCount : fallbackCount - 1;
        }

        /// <summary>
        /// Generates a shape candidate with enough voxels to support the tier.
        /// </summary>
        private ShapeCandidate CreateShapeCandidate(DifficultyBatchDefinition settings, System.Random random)
        {
            ShapeCandidate bestCandidate = null;
            int requestedTileCount = settings.MinPairCount * 2;

            for (int attempt = 0; attempt < 12; attempt++)
            {
                LevelShapeType selectedShape = settings.GetRandomShape(random);
                int targetLayerCount = GetRandomLayerCount(settings, random);
                if (selectedShape == LevelShapeType.Cylinder)
                {
                    targetLayerCount = Mathf.Clamp(targetLayerCount, 1, 3);
                }

                VoxelGridSize gridSize = BuildGridSizeForShape(targetLayerCount, selectedShape);
                List<List<TilePlacementData>> shells = BuildShapeShells(gridSize, selectedShape, targetLayerCount, settings, random);
                if (GetShellTileCapacity(shells) < 2)
                {
                    continue;
                }

                ShapeCandidate candidate = new ShapeCandidate
                {
                    GridSize = gridSize,
                    Shape = selectedShape,
                    TargetLayerCount = targetLayerCount,
                    TargetTileCount = GetTargetTileCount(settings, new ShapeCandidate { Shells = shells, TargetLayerCount = targetLayerCount }),
                    Shells = shells,
                };

                if (candidate.TargetTileCount <= 0)
                {
                    continue;
                }

                if (bestCandidate == null || candidate.TileCapacity > bestCandidate.TileCapacity)
                {
                    bestCandidate = candidate;
                }

                if (candidate.TileCapacity >= requestedTileCount)
                {
                    return candidate;
                }
            }

            return bestCandidate;
        }

        private ShapeCandidate CreateAuthoredShapeCandidate()
        {
            if (authoredShape == null)
            {
                throw new InvalidOperationException("Authored shape is missing.");
            }

            if (!authoredShape.Validate(out string error))
            {
                throw new InvalidOperationException(error);
            }

            List<List<TilePlacementData>> shells = new List<List<TilePlacementData>>();
            for (int index = 0; index < authoredShape.LayerCount; index++)
            {
                shells.Add(new List<TilePlacementData>());
            }

            for (int index = 0; index < authoredShape.Cells.Count; index++)
            {
                ManualTileShape.Cell cell = authoredShape.Cells[index];
                while (shells.Count <= cell.SurfaceShellIndex)
                {
                    shells.Add(new List<TilePlacementData>());
                }

                shells[cell.SurfaceShellIndex].Add(new TilePlacementData
                {
                    Coordinate = cell.GridCoordinate,
                    FacingDirection = VoxelGridDirection.Up,
                    ShellIndex = cell.SurfaceShellIndex,
                    SurfaceSlotIndex = -1,
                    CustomLocalPosition = cell.LocalPosition,
                    CustomLocalEulerAngles = cell.LocalEulerAngles,
                    UseCustomLocalPosition = true,
                    UseCustomLocalEulerAngles = true,
                    ApplyShellCompaction = false,
                });
            }

            return new ShapeCandidate
            {
                GridSize = authoredShape.GridSize,
                Shape = LevelShapeType.Custom,
                TargetLayerCount = authoredShape.LayerCount,
                TargetTileCount = authoredShape.TileCount,
                Shells = shells,
            };
        }

        private static List<TilePlacementData> FlattenAuthoredShapeShells(List<List<TilePlacementData>> shells)
        {
            List<TilePlacementData> result = new List<TilePlacementData>();
            if (shells == null)
            {
                return result;
            }

            for (int shellIndex = 0; shellIndex < shells.Count; shellIndex++)
            {
                if (shells[shellIndex] != null)
                {
                    result.AddRange(shells[shellIndex]);
                }
            }

            return result;
        }

        /// <summary>
        /// Builds the occupied coordinates by taking shell layers in the order supplied by the shell builder.
        /// </summary>
        private static List<TilePlacementData> BuildOccupiedCoordinates(LevelShapeType shape, List<List<TilePlacementData>> shells, int tileCount, System.Random random)
        {
            if (ShouldKeepShapeSelectionCompact(shape))
            {
                return BuildCompactOccupiedCoordinates(shells, tileCount);
            }

            return BuildCompleteShellOccupiedCoordinates(shells, tileCount, random);
        }

        private static List<TilePlacementData> BuildCompleteShellOccupiedCoordinates(List<List<TilePlacementData>> shells, int tileCount, System.Random random)
        {
            List<TilePlacementData> orderedCoordinates = new List<TilePlacementData>(tileCount);
            for (int shellIndex = 0; shellIndex < shells.Count && orderedCoordinates.Count < tileCount; shellIndex++)
            {
                List<TilePlacementData> shellCoordinates = shells[shellIndex];
                if (shellCoordinates == null || orderedCoordinates.Count + shellCoordinates.Count > tileCount)
                {
                    break;
                }

                List<TilePlacementData> shuffledShell = new List<TilePlacementData>(shellCoordinates);
                Shuffle(shuffledShell, random);
                for (int coordinateIndex = 0; coordinateIndex < shuffledShell.Count; coordinateIndex++)
                {
                    orderedCoordinates.Add(ClonePlacement(shuffledShell[coordinateIndex], shellIndex));
                }
            }

            return orderedCoordinates;
        }

        private static bool ShouldKeepShapeSelectionCompact(LevelShapeType shape)
        {
            return ShapeSelectionStrategy.IsCompact(shape);
        }

        private static List<TilePlacementData> BuildCompactOccupiedCoordinates(List<List<TilePlacementData>> shells, int tileCount)
        {
            List<TilePlacementData> orderedCoordinates = new List<TilePlacementData>(tileCount);
            if (shells == null || shells.Count == 0 || tileCount <= 0)
            {
                return orderedCoordinates;
            }

            Vector3 center = CalculatePlacementSelectionCenter(shells);
            List<TilePlacementData> flattenedPlacements = new List<TilePlacementData>();

            for (int shellIndex = 0; shellIndex < shells.Count; shellIndex++)
            {
                List<TilePlacementData> shellCoordinates = shells[shellIndex];
                if (shellCoordinates == null)
                {
                    continue;
                }

                for (int coordinateIndex = 0; coordinateIndex < shellCoordinates.Count; coordinateIndex++)
                {
                    TilePlacementData placement = shellCoordinates[coordinateIndex];
                    if (placement == null)
                    {
                        continue;
                    }

                    flattenedPlacements.Add(ClonePlacement(placement, shellIndex));
                }
            }

            flattenedPlacements.Sort((left, right) => CompareCompactPlacementOrder(left, right, center));

            int selectionCount = Mathf.Min(tileCount, flattenedPlacements.Count);
            for (int index = 0; index < selectionCount; index++)
            {
                orderedCoordinates.Add(flattenedPlacements[index]);
            }

            if (orderedCoordinates.Count % 2 != 0)
            {
                orderedCoordinates.RemoveAt(orderedCoordinates.Count - 1);
            }

            return orderedCoordinates;
        }

        private static Vector3 CalculatePlacementSelectionCenter(List<List<TilePlacementData>> shells)
        {
            if (shells == null || shells.Count == 0)
            {
                return Vector3.zero;
            }

            Vector3 sum = Vector3.zero;
            int count = 0;
            for (int shellIndex = 0; shellIndex < shells.Count; shellIndex++)
            {
                List<TilePlacementData> shell = shells[shellIndex];
                if (shell == null)
                {
                    continue;
                }

                for (int placementIndex = 0; placementIndex < shell.Count; placementIndex++)
                {
                    TilePlacementData placement = shell[placementIndex];
                    if (placement == null)
                    {
                        continue;
                    }

                    sum += placement.Coordinate;
                    count++;
                }
            }

            return count > 0 ? sum / count : Vector3.zero;
        }

        private static int CompareCompactPlacementOrder(TilePlacementData left, TilePlacementData right, Vector3 center)
        {
            if (ReferenceEquals(left, right))
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

            float leftDistance = ((Vector3)left.Coordinate - center).sqrMagnitude;
            float rightDistance = ((Vector3)right.Coordinate - center).sqrMagnitude;
            int distanceComparison = leftDistance.CompareTo(rightDistance);
            if (distanceComparison != 0)
            {
                return distanceComparison;
            }

            int shellComparison = left.ShellIndex.CompareTo(right.ShellIndex);
            if (shellComparison != 0)
            {
                return shellComparison;
            }

            int yComparison = right.Coordinate.y.CompareTo(left.Coordinate.y);
            if (yComparison != 0)
            {
                return yComparison;
            }

            int xComparison = left.Coordinate.x.CompareTo(right.Coordinate.x);
            if (xComparison != 0)
            {
                return xComparison;
            }

            int zComparison = left.Coordinate.z.CompareTo(right.Coordinate.z);
            if (zComparison != 0)
            {
                return zComparison;
            }

            return left.FacingDirection.CompareTo(right.FacingDirection);
        }

        /// <summary>
        /// Creates a copy of the placement data annotated with the shell depth it belongs to.
        /// </summary>
        private static TilePlacementData ClonePlacement(TilePlacementData placement, int shellIndex)
        {
            if (placement == null)
            {
                return null;
            }

            return new TilePlacementData
            {
                Coordinate = placement.Coordinate,
                FacingDirection = placement.FacingDirection,
                ShellIndex = Mathf.Max(0, shellIndex),
                SurfaceSlotIndex = placement.SurfaceSlotIndex,
                CustomLocalPosition = placement.CustomLocalPosition,
                CustomLocalEulerAngles = placement.CustomLocalEulerAngles,
                UseCustomLocalPosition = placement.UseCustomLocalPosition,
                UseCustomLocalEulerAngles = placement.UseCustomLocalEulerAngles,
                ApplyShellCompaction = placement.ApplyShellCompaction,
            };
        }

        /// <summary>
        /// Builds the shell list for the requested shape.
        /// </summary>
        private List<List<TilePlacementData>> BuildShapeShells(VoxelGridSize gridSize, LevelShapeType shape, int targetLayerCount, DifficultyBatchDefinition settings, System.Random random)
        {
            switch (shape)
            {
                case LevelShapeType.Cube:
                    return BuildNestedCubeShells(targetLayerCount, settings);

                case LevelShapeType.Heart:
                {
                    CubeTileMetrics metrics = ResolveCubeTileMetrics();
                    HeartShellLayoutBuilder builder = new HeartShellLayoutBuilder(metrics, Mathf.Max(0f, surfaceTileGap));
                    int minTileCount = settings != null ? Mathf.Max(2, settings.MinPairCount * 2) : 2;
                    int maxTileCount = settings != null ? Mathf.Max(minTileCount, settings.MaxPairCount * 2) : minTileCount;
                    return builder.Build(targetLayerCount, minTileCount, maxTileCount, random);
                }

                case LevelShapeType.Cylinder:
                {
                    CubeTileMetrics metrics = ResolveCubeTileMetrics();
                    CylinderShellLayoutBuilder builder = new CylinderShellLayoutBuilder(metrics, Mathf.Max(0f, surfaceTileGap));
                    int minTileCount = settings != null ? Mathf.Max(2, settings.MinPairCount * 2) : 2;
                    int maxTileCount = settings != null ? Mathf.Max(minTileCount, settings.MaxPairCount * 2) : minTileCount;
                    return builder.Build(targetLayerCount, minTileCount, maxTileCount, random);
                }

                case LevelShapeType.Rectangle:
                {
                    int minTileCount = settings != null ? Mathf.Max(2, settings.MinPairCount * 2) : 2;
                    int maxTileCount = settings != null ? Mathf.Max(minTileCount, settings.MaxPairCount * 2) : minTileCount;
                    return new RectangleShellLayoutBuilder(ResolveCubeTileMetrics(), Mathf.Max(0f, surfaceTileGap)).Build(gridSize, targetLayerCount, minTileCount, maxTileCount, random);
                }

                case LevelShapeType.T:
                {
                    int minTileCount = settings != null ? Mathf.Max(2, settings.MinPairCount * 2) : 2;
                    int maxTileCount = settings != null ? Mathf.Max(minTileCount, settings.MaxPairCount * 2) : minTileCount;
                    return new TShellLayoutBuilder(ResolveCubeTileMetrics(), Mathf.Max(0f, surfaceTileGap)).Build(gridSize, targetLayerCount, minTileCount, maxTileCount, random);
                }

                case LevelShapeType.H:
                {
                    int minTileCount = settings != null ? Mathf.Max(2, settings.MinPairCount * 2) : 2;
                    int maxTileCount = settings != null ? Mathf.Max(minTileCount, settings.MaxPairCount * 2) : minTileCount;
                    return new HShellLayoutBuilder(ResolveCubeTileMetrics(), Mathf.Max(0f, surfaceTileGap)).Build(gridSize, targetLayerCount, minTileCount, maxTileCount, random);
                }

                case LevelShapeType.I:
                {
                    int minTileCount = settings != null ? Mathf.Max(2, settings.MinPairCount * 2) : 2;
                    int maxTileCount = settings != null ? Mathf.Max(minTileCount, settings.MaxPairCount * 2) : minTileCount;
                    return new IShellLayoutBuilder(ResolveCubeTileMetrics(), Mathf.Max(0f, surfaceTileGap)).Build(gridSize, targetLayerCount, minTileCount, maxTileCount, random);
                }

                case LevelShapeType.L:
                {
                    int minTileCount = settings != null ? Mathf.Max(2, settings.MinPairCount * 2) : 2;
                    int maxTileCount = settings != null ? Mathf.Max(minTileCount, settings.MaxPairCount * 2) : minTileCount;
                    return new LShellLayoutBuilder(ResolveCubeTileMetrics(), Mathf.Max(0f, surfaceTileGap)).Build(gridSize, targetLayerCount, minTileCount, maxTileCount, random);
                }

                case LevelShapeType.Pyramid:
                    return new PyramidShellLayoutBuilder(ResolveCubeTileMetrics(), Mathf.Max(0f, surfaceTileGap)).Build(gridSize);

                case LevelShapeType.Dome:
                    return new DomeShellLayoutBuilder(ResolveCubeTileMetrics(), Mathf.Max(0f, surfaceTileGap)).Build(gridSize);

                case LevelShapeType.Ramp:
                    return new RampShellLayoutBuilder(ResolveCubeTileMetrics(), Mathf.Max(0f, surfaceTileGap)).Build(gridSize);
            }

            return BuildShells(BuildShapeCoordinates(gridSize));
        }

        /// <summary>
        /// Calculates the total tile capacity across all shell layers.
        /// </summary>
        private static int GetShellTileCapacity(List<List<TilePlacementData>> shells)
        {
            int total = 0;
            for (int index = 0; index < shells.Count; index++)
            {
                total += shells[index].Count;
            }

            return total;
        }

        /// <summary>
        /// Builds nested cube shells directly in world space so the resulting block stays cubic on all three axes.
        /// </summary>
        private List<List<TilePlacementData>> BuildNestedCubeShells(int targetLayerCount, DifficultyBatchDefinition settings)
        {
            CubeTileMetrics metrics = ResolveCubeTileMetrics();
            List<List<TilePlacementData>> shells = new List<List<TilePlacementData>>();
            int layerCount = Mathf.Max(2, targetLayerCount);
            int desiredVisibleLayerCount = layerCount;
            int maxTileCount = settings != null ? Mathf.Max(2, settings.MaxPairCount * 2) : int.MaxValue;

            CubeShellPlan previousShell = CreateInitialCubeShellPlan(metrics, maxTileCount, desiredVisibleLayerCount);
            shells.Add(BuildCubeFacePanels(previousShell, metrics));

            for (int layerIndex = 1; layerIndex < layerCount; layerIndex++)
            {
                CubeShellPlan shellPlan = CreateCoveringCubeShellPlan(previousShell, metrics);
                List<TilePlacementData> shell = BuildCubeFacePanels(shellPlan, metrics);
                if (shell.Count >= 2)
                {
                    shells.Add(shell);
                }

                previousShell = shellPlan;
            }

            shells.Reverse();
            return shells;
        }

        /// <summary>
        /// Builds one cube shell using exact world-space tile positions derived from the tile prefab dimensions.
        /// </summary>
        private List<TilePlacementData> BuildCubeFacePanels(CubeShellPlan shellPlan, CubeTileMetrics metrics)
        {
            int widthCount = shellPlan.ColumnCount;
            int heightCount = shellPlan.RowCount;
            float cubeSideLength = shellPlan.SideLength;
            float normalOffset = GetRecessedCubeFaceNormalOffset(cubeSideLength, metrics);
            float widthAxisStep = Mathf.Max(0.01f, metrics.FaceWidth);
            float heightAxisStep = Mathf.Max(0.01f, metrics.FaceHeight);

            List<TilePlacementData> shell = new List<TilePlacementData>();

            for (int verticalIndex = 0; verticalIndex < widthCount; verticalIndex++)
            {
                float localY = GetCenteredPanelCoordinate(verticalIndex, widthCount, widthAxisStep);
                for (int depthIndex = 0; depthIndex < heightCount; depthIndex++)
                {
                    float localZ = GetCenteredPanelCoordinate(depthIndex, heightCount, heightAxisStep);
                    shell.Add(CreateCustomPlacement(new Vector3(-normalOffset, localY, localZ), VoxelGridDirection.Left));
                    shell.Add(CreateCustomPlacement(new Vector3(normalOffset, localY, localZ), VoxelGridDirection.Right));
                }
            }

            for (int depthIndex = 0; depthIndex < heightCount; depthIndex++)
            {
                float localZ = GetCenteredPanelCoordinate(depthIndex, heightCount, heightAxisStep);
                for (int widthIndex = 0; widthIndex < widthCount; widthIndex++)
                {
                    float localX = GetCenteredPanelCoordinate(widthIndex, widthCount, widthAxisStep);
                    shell.Add(CreateCustomPlacement(new Vector3(localX, -normalOffset, localZ), VoxelGridDirection.Down));
                    shell.Add(CreateCustomPlacement(new Vector3(localX, normalOffset, localZ), VoxelGridDirection.Up));
                }
            }

            for (int heightIndex = 0; heightIndex < heightCount; heightIndex++)
            {
                float localY = GetCenteredPanelCoordinate(heightIndex, heightCount, heightAxisStep);
                for (int widthIndex = 0; widthIndex < widthCount; widthIndex++)
                {
                    float localX = GetCenteredPanelCoordinate(widthIndex, widthCount, widthAxisStep);
                    shell.Add(CreateCustomPlacement(new Vector3(localX, localY, -normalOffset), VoxelGridDirection.Back));
                    shell.Add(CreateCustomPlacement(new Vector3(localX, localY, normalOffset), VoxelGridDirection.Forward));
                }
            }

            return shell;
        }

        /// <summary>
        /// Resolves the exact face-center offset so opposite cube faces stay perfectly balanced around the cube center.
        /// </summary>
        private static float GetRecessedCubeFaceNormalOffset(float cubeSideLength, CubeTileMetrics metrics)
        {
            float halfSideLength = Mathf.Max(0f, cubeSideLength) * 0.5f;
            float halfThickness = Mathf.Max(0.01f, metrics.Thickness) * 0.5f;
            return Mathf.Max(0f, halfSideLength - halfThickness);
        }

        /// <summary>
        /// Resolves the two in-plane face dimensions and thickness.
        /// The assigned Mahjong tile prefab takes priority so generated cube shells match the actual tile footprint.
        /// </summary>
        private CubeTileMetrics ResolveCubeTileMetrics()
        {
            if (TryGetTilePrefabBounds(out Bounds prefabBounds))
            {
                return new CubeTileMetrics(prefabBounds.size.x, prefabBounds.size.z, prefabBounds.size.y);
            }

            if (layoutOverride != null)
            {
                Vector3 layoutCellSize = layoutOverride.CellSize;
                return new CubeTileMetrics(layoutCellSize.x, layoutCellSize.z, layoutCellSize.y);
            }

            Vector3 cellSize = layoutOverride != null ? layoutOverride.CellSize : new Vector3(0.95f, 0.45f, 0.7f);
            return new CubeTileMetrics(cellSize.x, cellSize.z, cellSize.y);
        }

        /// <summary>
        /// Tries to resolve the physical tile bounds directly from the assigned prefab.
        /// Uses only the authored placement/body footprint so runtime outline presentation never affects generation spacing.
        /// </summary>
        private bool TryGetTilePrefabBounds(out Bounds prefabBounds)
        {
            prefabBounds = default;
            if (tilePrefab == null)
            {
                return false;
            }

            if (TryGetTilePrefabPlacementBounds(out prefabBounds))
            {
                return true;
            }

            Collider[] colliders = tilePrefab.GetComponentsInChildren<Collider>(true);
            if (TryEncapsulateBounds(colliders, out prefabBounds))
            {
                return true;
            }

            Renderer[] renderers = tilePrefab.GetComponentsInChildren<Renderer>(true);
            if (TryEncapsulateBounds(renderers, out prefabBounds))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Tries to resolve the tile footprint from the Mahjong tile's placement mesh/collider in parent-space units.
        /// </summary>
        private bool TryGetTilePrefabPlacementBounds(out Bounds placementBounds)
        {
            placementBounds = default;
            if (tilePrefab == null)
            {
                return false;
            }

            if (tilePrefab.MeshRenderer != null)
            {
                MeshFilter meshFilter = tilePrefab.MeshRenderer.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null &&
                    TryTransformBounds(tilePrefab.transform.worldToLocalMatrix * meshFilter.transform.localToWorldMatrix, meshFilter.sharedMesh.bounds, out Bounds rootLocalBounds) &&
                    TryTransformBounds(tilePrefab.transform.localToWorldMatrix, rootLocalBounds, out placementBounds))
                {
                    return true;
                }
            }

            if (tilePrefab.TileCollider is MeshCollider meshCollider && meshCollider.sharedMesh != null &&
                TryTransformBounds(tilePrefab.transform.worldToLocalMatrix * meshCollider.transform.localToWorldMatrix, meshCollider.sharedMesh.bounds, out Bounds meshColliderBounds) &&
                TryTransformBounds(tilePrefab.transform.localToWorldMatrix, meshColliderBounds, out placementBounds))
            {
                return true;
            }

            if (tilePrefab.TileCollider is BoxCollider boxCollider &&
                TryTransformBounds(tilePrefab.transform.worldToLocalMatrix * boxCollider.transform.localToWorldMatrix, new Bounds(boxCollider.center, boxCollider.size), out Bounds boxColliderBounds) &&
                TryTransformBounds(tilePrefab.transform.localToWorldMatrix, boxColliderBounds, out placementBounds))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Builds the most compact starting square face plan that still leaves room for the requested visible layer count.
        /// </summary>
        private static CubeShellPlan CreateInitialCubeShellPlan(CubeTileMetrics metrics, int maxTileCount, int desiredVisibleLayerCount)
        {
            float edgeInset = GetCubeFaceEdgeInset(metrics);
            int safeVisibleLayerCount = Mathf.Max(1, desiredVisibleLayerCount);
            int maxFaceTileCount = Mathf.Max(1, maxTileCount / (6 * safeVisibleLayerCount));
            GetSquareFaceTileGrid(metrics.FaceWidth, metrics.FaceHeight, maxFaceTileCount, out int baseColumnCount, out int baseRowCount);

            float panelWidth = baseColumnCount * metrics.FaceWidth;
            float panelHeight = baseRowCount * metrics.FaceHeight;
            float sideLength = Mathf.Max(panelWidth, panelHeight) + (edgeInset * 2f);
            return new CubeShellPlan(baseColumnCount, baseRowCount, sideLength);
        }

        /// <summary>
        /// Builds the next larger cube shell by choosing the smallest larger panel that still keeps the face as square as possible.
        /// This avoids growing only one axis on the next shell, which can leave an outer face visually under-filled.
        /// </summary>
        private static CubeShellPlan CreateCoveringCubeShellPlan(CubeShellPlan previousShell, CubeTileMetrics metrics)
        {
            float requiredSideLength = previousShell.SideLength + (metrics.Thickness * 2f);
            int minimumColumnCount = Mathf.Max(1, previousShell.ColumnCount);
            int minimumRowCount = Mathf.Max(1, previousShell.RowCount);
            float edgeInset = GetCubeFaceEdgeInset(metrics);
            const int SearchPaddingPerAxis = 12;

            float bestMismatch = float.MaxValue;
            float bestSideLength = float.MaxValue;
            int bestColumnCount = minimumColumnCount;
            int bestRowCount = minimumRowCount;
            int bestTileCount = int.MaxValue;
            bool foundCandidate = false;

            for (int columnCount = minimumColumnCount; columnCount <= minimumColumnCount + SearchPaddingPerAxis; columnCount++)
            {
                for (int rowCount = minimumRowCount; rowCount <= minimumRowCount + SearchPaddingPerAxis; rowCount++)
                {
                    float panelWidth = columnCount * metrics.FaceWidth;
                    float panelHeight = rowCount * metrics.FaceHeight;
                    float sideLength = Mathf.Max(panelWidth, panelHeight) + (edgeInset * 2f);
                    if (sideLength + 0.0001f < requiredSideLength)
                    {
                        continue;
                    }

                    float longestSide = Mathf.Max(panelWidth, panelHeight);
                    float mismatch = longestSide <= 0.01f ? 0f : Mathf.Abs(panelWidth - panelHeight) / longestSide;
                    int tileCount = columnCount * rowCount;

                    const float PreferredPanelMismatch = 0.18f;
                    bool candidateIsAcceptablySquare = mismatch <= PreferredPanelMismatch;
                    bool bestIsAcceptablySquare = bestMismatch <= PreferredPanelMismatch;
                    bool isBetterCompactSquare = candidateIsAcceptablySquare && bestIsAcceptablySquare && sideLength + 0.0001f < bestSideLength;
                    bool isBetterSquareClass = candidateIsAcceptablySquare && !bestIsAcceptablySquare;
                    bool isBetterMismatch = candidateIsAcceptablySquare == bestIsAcceptablySquare && mismatch + 0.0001f < bestMismatch;
                    bool isSameMismatchWithSmallerShell = Mathf.Abs(mismatch - bestMismatch) <= 0.0001f && sideLength + 0.0001f < bestSideLength;
                    bool isSameMismatchAndShellWithFewerTiles = Mathf.Abs(mismatch - bestMismatch) <= 0.0001f && Mathf.Abs(sideLength - bestSideLength) <= 0.0001f && tileCount < bestTileCount;
                    if (!isBetterCompactSquare && !isBetterSquareClass && !isBetterMismatch && !isSameMismatchWithSmallerShell && !isSameMismatchAndShellWithFewerTiles)
                    {
                        continue;
                    }

                    bestMismatch = mismatch;
                    bestSideLength = sideLength;
                    bestColumnCount = columnCount;
                    bestRowCount = rowCount;
                    bestTileCount = tileCount;
                    foundCandidate = true;
                }
            }

            if (foundCandidate)
            {
                return new CubeShellPlan(bestColumnCount, bestRowCount, bestSideLength);
            }

            int columnCountFallback = minimumColumnCount;
            int rowCountFallback = minimumRowCount;

            while (true)
            {
                float panelWidth = columnCountFallback * metrics.FaceWidth;
                float panelHeight = rowCountFallback * metrics.FaceHeight;
                float sideLength = Mathf.Max(panelWidth, panelHeight) + (edgeInset * 2f);
                if (sideLength + 0.0001f >= requiredSideLength)
                {
                    return new CubeShellPlan(columnCountFallback, rowCountFallback, sideLength);
                }

                if (panelWidth <= panelHeight)
                {
                    columnCountFallback++;
                }
                else
                {
                    rowCountFallback++;
                }
            }
        }

        /// <summary>
        /// Resolves the empty border each face needs so perpendicular faces do not overlap at the edges.
        /// </summary>
        private static float GetCubeFaceEdgeInset(CubeTileMetrics metrics)
        {
            return Mathf.Max(0.01f, metrics.Thickness + 0.001f);
        }

        /// <summary>
        /// Resolves the smallest tile grid whose total X/Y span is as square as possible.
        /// </summary>
        private static void GetSquareFaceTileGrid(float tileFaceWidth, float tileFaceHeight, int maxFaceTileCount, out int columnCount, out int rowCount)
        {
            float safeWidth = Mathf.Max(0.01f, tileFaceWidth);
            float safeHeight = Mathf.Max(0.01f, tileFaceHeight);
            const int MaxTilesPerAxis = 12;
            int safeMaxFaceTileCount = Mathf.Max(1, maxFaceTileCount);

            float bestMismatch = float.MaxValue;
            int bestColumnCount = 1;
            int bestRowCount = 1;
            int bestTileCount = int.MaxValue;

            for (int candidateColumnCount = 1; candidateColumnCount <= MaxTilesPerAxis; candidateColumnCount++)
            {
                for (int candidateRowCount = 1; candidateRowCount <= MaxTilesPerAxis; candidateRowCount++)
                {
                    int tileCount = candidateColumnCount * candidateRowCount;
                    if (tileCount > safeMaxFaceTileCount)
                    {
                        continue;
                    }

                    float panelWidth = candidateColumnCount * safeWidth;
                    float panelHeight = candidateRowCount * safeHeight;
                    float longestSide = Mathf.Max(panelWidth, panelHeight);
                    float mismatch = longestSide <= 0.01f ? 0f : Mathf.Abs(panelWidth - panelHeight) / longestSide;

                    bool isBetterMismatch = mismatch + 0.0001f < bestMismatch;
                    bool isSameMismatchWithFewerTiles = Mathf.Abs(mismatch - bestMismatch) <= 0.0001f && tileCount < bestTileCount;
                    if (!isBetterMismatch && !isSameMismatchWithFewerTiles)
                    {
                        continue;
                    }

                    bestMismatch = mismatch;
                    bestColumnCount = candidateColumnCount;
                    bestRowCount = candidateRowCount;
                    bestTileCount = tileCount;
                }
            }

            columnCount = bestColumnCount;
            rowCount = bestRowCount;
        }

        /// <summary>
        /// Tries to combine collider or renderer bounds into a single prefab bound.
        /// </summary>
        private static bool TryEncapsulateBounds<TComponent>(TComponent[] components, out Bounds combinedBounds)
            where TComponent : Component
        {
            combinedBounds = default;
            bool hasBounds = false;

            if (components == null)
            {
                return false;
            }

            for (int index = 0; index < components.Length; index++)
            {
                TComponent component = components[index];
                if (component == null)
                {
                    continue;
                }

                Bounds componentBounds;
                switch (component)
                {
                    case Collider collider:
                        componentBounds = collider.bounds;
                        break;

                    case Renderer renderer:
                        componentBounds = renderer.bounds;
                        break;

                    default:
                        continue;
                }

                if (componentBounds.size.sqrMagnitude <= Mathf.Epsilon)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    combinedBounds = componentBounds;
                    hasBounds = true;
                    continue;
                }

                combinedBounds.Encapsulate(componentBounds.min);
                combinedBounds.Encapsulate(componentBounds.max);
            }

            return hasBounds;
        }

        /// <summary>
        /// Transforms a bounds volume through the supplied matrix and returns the resulting axis-aligned bounds.
        /// </summary>
        private static bool TryTransformBounds(Matrix4x4 matrix, Bounds sourceBounds, out Bounds transformedBounds)
        {
            transformedBounds = default;
            if (sourceBounds.size.sqrMagnitude <= Mathf.Epsilon)
            {
                return false;
            }

            Vector3 sourceCenter = sourceBounds.center;
            Vector3 sourceExtents = sourceBounds.extents;
            Vector3[] corners = new Vector3[8];
            int cornerIndex = 0;

            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 corner = sourceCenter + Vector3.Scale(sourceExtents, new Vector3(x, y, z));
                        corners[cornerIndex++] = matrix.MultiplyPoint3x4(corner);
                    }
                }
            }

            transformedBounds = new Bounds(corners[0], Vector3.zero);
            for (int index = 1; index < corners.Length; index++)
            {
                transformedBounds.Encapsulate(corners[index]);
            }

            return transformedBounds.size.sqrMagnitude > Mathf.Epsilon;
        }

        /// <summary>
        /// Creates one outward-facing placement at a custom local position.
        /// </summary>
        private static TilePlacementData CreateCustomPlacement(Vector3 localPosition, VoxelGridDirection facingDirection)
        {
            return new TilePlacementData
            {
                Coordinate = Vector3Int.zero,
                FacingDirection = facingDirection,
                SurfaceSlotIndex = -1,
                CustomLocalPosition = localPosition,
                UseCustomLocalPosition = true,
            };
        }

        /// <summary>
        /// Builds a visually reasonable voxel grid for the supplied shell-layer count and shape.
        /// </summary>
        private static VoxelGridSize BuildGridSizeForShape(int layerCount, LevelShapeType shape)
        {
            int safeLayerCount = Mathf.Max(1, layerCount);
            int width = safeLayerCount + 1;
            int height = safeLayerCount + 1;
            int depth = safeLayerCount + 1;

            switch (shape)
            {
                case LevelShapeType.Cube:
                {
                    int cubeSide = Mathf.Clamp((safeLayerCount / 2) + 1, 3, 4);
                    width = cubeSide;
                    height = cubeSide;
                    depth = cubeSide;
                    break;
                }

                case LevelShapeType.Heart:
                    width = Mathf.Clamp(7 + ((safeLayerCount - 1) * 2), 7, 17);
                    height = Mathf.Max(5, width - 2);
                    depth = width >= 9 ? 5 : 3;
                    break;

                case LevelShapeType.Cylinder:
                    width = Mathf.Clamp(4 + (safeLayerCount * 2), 6, 18);
                    height = Mathf.Clamp(2 + safeLayerCount, 3, 8);
                    depth = width;
                    break;

                case LevelShapeType.Rectangle:
                    width = Mathf.Clamp(4 + (safeLayerCount * 2), 4, 18);
                    height = Mathf.Clamp(2 + (safeLayerCount / 2), 2, 8);
                    depth = Mathf.Clamp(4 + safeLayerCount, 4, 18);
                    break;

                case LevelShapeType.T:
                    width = Mathf.Clamp(5 + (safeLayerCount * 2), 5, 18);
                    height = Mathf.Clamp(4 + safeLayerCount, 4, 12);
                    depth = Mathf.Clamp(3 + (safeLayerCount / 2), 3, 8);
                    break;

                case LevelShapeType.H:
                    width = Mathf.Clamp(5 + (safeLayerCount * 2), 5, 18);
                    height = Mathf.Clamp(4 + safeLayerCount, 4, 12);
                    depth = Mathf.Clamp(3 + (safeLayerCount / 2), 3, 8);
                    break;

                case LevelShapeType.I:
                    width = Mathf.Clamp(3 + (safeLayerCount / 2), 3, 7);
                    height = Mathf.Clamp(5 + (safeLayerCount * 2), 5, 16);
                    depth = Mathf.Clamp(3 + (safeLayerCount / 2), 3, 7);
                    break;

                case LevelShapeType.L:
                    width = Mathf.Clamp(4 + safeLayerCount, 4, 12);
                    height = Mathf.Clamp(5 + (safeLayerCount * 2), 5, 16);
                    depth = Mathf.Clamp(3 + (safeLayerCount / 2), 3, 7);
                    break;

                case LevelShapeType.Pyramid:
                    width = Mathf.Clamp(3 + safeLayerCount, 3, 11);
                    height = Mathf.Clamp(2 + safeLayerCount, 2, 8);
                    depth = width;
                    break;

                case LevelShapeType.Dome:
                    width = Mathf.Clamp(3 + safeLayerCount, 3, 11);
                    height = Mathf.Clamp(2 + ((safeLayerCount + 1) / 2), 2, 6);
                    depth = width;
                    break;

                case LevelShapeType.Ramp:
                    width = Mathf.Clamp(3 + safeLayerCount, 3, 11);
                    height = Mathf.Clamp(2 + safeLayerCount, 2, 8);
                    depth = Mathf.Clamp(3 + (safeLayerCount / 2), 3, 7);
                    break;
            }

            return new VoxelGridSize(width, height, depth);
        }

        /// <summary>
        /// Builds a solid occupied voxel block for non-custom-world-space shapes.
        /// </summary>
        private static List<Vector3Int> BuildShapeCoordinates(VoxelGridSize gridSize)
        {
            List<Vector3Int> coordinates = new List<Vector3Int>(gridSize.Volume);
            for (int x = 0; x < gridSize.Width; x++)
            {
                for (int y = 0; y < gridSize.Height; y++)
                {
                    for (int z = 0; z < gridSize.Depth; z++)
                    {
                        coordinates.Add(new Vector3Int(x, y, z));
                    }
                }
            }

            return coordinates;
        }

        /// <summary>
        /// Builds tile definitions and assigns match ids in pairs.
        /// </summary>
        private bool TryBuildTileDefinitions(LevelShapeType shape, List<TilePlacementData> occupiedCoordinates, VoxelGridSize shapeGridSize, VoxelGridSize logicalGridSize, DifficultyBatchDefinition settings, System.Random random, out List<LevelTileDefinition> tileDefinitions, int requestedUniquePairCount = -1)
        {
            tileDefinitions = new List<LevelTileDefinition>(occupiedCoordinates != null ? occupiedCoordinates.Count : 0);
            if (occupiedCoordinates == null || occupiedCoordinates.Count == 0)
            {
                return true;
            }

            if (occupiedCoordinates.Count % 2 != 0)
            {
                return false;
            }

            if (!TryBuildSolvablePairSequence(occupiedCoordinates, shapeGridSize, settings.Difficulty, random, out List<TilePlacementPair> orderedPairs))
            {
                tileDefinitions.Clear();
                return false;
            }

            List<int> matchIdsByPair = BuildMatchIdsByPair(orderedPairs.Count, requestedUniquePairCount, random);
            if (matchIdsByPair == null || matchIdsByPair.Count != orderedPairs.Count)
            {
                tileDefinitions.Clear();
                return false;
            }

            int tileIndex = 0;
            for (int pairIndex = 0; pairIndex < orderedPairs.Count; pairIndex++)
            {
                TilePlacementPair pair = orderedPairs[pairIndex];
                int matchId = matchIdsByPair[pairIndex];
                tileDefinitions.Add(CreateTileDefinition(shape, matchId, tileIndex++, pair.First, shapeGridSize, logicalGridSize, settings.FlippedTileChance, random));
                tileDefinitions.Add(CreateTileDefinition(shape, matchId, tileIndex++, pair.Second, shapeGridSize, logicalGridSize, settings.FlippedTileChance, random));
            }

            return true;
        }

        private static List<int> BuildMatchIdsByPair(int pairCount, int requestedUniquePairCount, System.Random random)
        {
            if (pairCount <= 0)
            {
                return new List<int>();
            }

            int uniquePairCount = ResolveUniquePairCount(requestedUniquePairCount, pairCount);
            int repeatedPairCount = pairCount - uniquePairCount;
            if (repeatedPairCount == 1)
            {
                return null;
            }

            List<int> groupSizes = new List<int>(pairCount);
            for (int index = 0; index < uniquePairCount; index++)
            {
                groupSizes.Add(1);
            }

            while (repeatedPairCount > 0)
            {
                int nextGroupSize = ResolveRepeatedGroupSize(repeatedPairCount, random);
                if (nextGroupSize < 2)
                {
                    return null;
                }

                groupSizes.Add(nextGroupSize);
                repeatedPairCount -= nextGroupSize;
            }

            ShuffleList(groupSizes, random);

            List<int> matchIds = new List<int>(pairCount);
            int nextMatchId = 0;
            for (int index = 0; index < groupSizes.Count; index++)
            {
                int groupSize = groupSizes[index];
                for (int pairIndex = 0; pairIndex < groupSize; pairIndex++)
                {
                    matchIds.Add(nextMatchId);
                }

                nextMatchId++;
            }

            ShuffleList(matchIds, random);
            return matchIds;
        }

        private static int ResolveUniquePairCount(int requestedUniquePairCount, int pairCount)
        {
            if (pairCount <= 0)
            {
                return 0;
            }

            if (requestedUniquePairCount < 0)
            {
                return pairCount;
            }

            int resolvedUniquePairCount = Mathf.Clamp(requestedUniquePairCount, 0, pairCount);
            if (pairCount - resolvedUniquePairCount == 1)
            {
                resolvedUniquePairCount = Mathf.Min(pairCount, resolvedUniquePairCount + 1);
            }

            return resolvedUniquePairCount;
        }

        private static int ResolveRepeatedGroupSize(int repeatedPairCount, System.Random random)
        {
            if (repeatedPairCount < 2)
            {
                return 0;
            }

            if (repeatedPairCount == 2 || repeatedPairCount == 3)
            {
                return repeatedPairCount;
            }

            List<int> validGroupSizes = new List<int>();
            for (int groupSize = 2; groupSize <= repeatedPairCount; groupSize++)
            {
                if (repeatedPairCount - groupSize == 1)
                {
                    continue;
                }

                validGroupSizes.Add(groupSize);
            }

            if (validGroupSizes.Count == 0)
            {
                return 0;
            }

            return validGroupSizes[random.Next(0, validGroupSizes.Count)];
        }

        private static void ShuffleList<T>(List<T> values, System.Random random)
        {
            if (values == null || values.Count <= 1 || random == null)
            {
                return;
            }

            for (int index = values.Count - 1; index > 0; index--)
            {
                int swapIndex = random.Next(0, index + 1);
                T current = values[index];
                values[index] = values[swapIndex];
                values[swapIndex] = current;
            }
        }

        /// <summary>
        /// Builds a guaranteed-removable pair order by repeatedly choosing two currently exposed surface tiles.
        /// </summary>
        private bool TryBuildSolvablePairSequence(List<TilePlacementData> occupiedCoordinates, VoxelGridSize shapeGridSize, LevelDifficulty difficulty, System.Random random, out List<TilePlacementPair> orderedPairs)
        {
            orderedPairs = new List<TilePlacementPair>();
            if (occupiedCoordinates == null || occupiedCoordinates.Count == 0)
            {
                return true;
            }

            Dictionary<TilePlacementData, Vector3> localPositionsByPlacement = new Dictionary<TilePlacementData, Vector3>(occupiedCoordinates.Count);
            List<TilePlacementData> remainingPlacements = new List<TilePlacementData>(occupiedCoordinates.Count);
            for (int index = 0; index < occupiedCoordinates.Count; index++)
            {
                TilePlacementData placement = occupiedCoordinates[index];
                if (placement == null)
                {
                    continue;
                }

                remainingPlacements.Add(placement);
                localPositionsByPlacement[placement] = GetCompactedSurfaceTileLocalPosition(placement, shapeGridSize);
            }

            while (remainingPlacements.Count > 0)
            {
                List<TilePlacementData> exposedPlacements = GetExposedPlacements(remainingPlacements, localPositionsByPlacement);
                if (exposedPlacements.Count < 2)
                {
                    orderedPairs.Clear();
                    return false;
                }

                exposedPlacements.Sort((left, right) => CompareExposedPlacements(left, right, localPositionsByPlacement));
                TilePlacementData first = exposedPlacements[0];
                TilePlacementData second = FindBestPairCandidate(first, exposedPlacements, localPositionsByPlacement, difficulty, random);
                if (first == null || second == null)
                {
                    orderedPairs.Clear();
                    return false;
                }

                orderedPairs.Add(new TilePlacementPair(first, second));
                remainingPlacements.Remove(first);
                remainingPlacements.Remove(second);
            }

            return true;
        }

        /// <summary>
        /// Collects every tile placement that currently has no outer tile covering its selectable face.
        /// </summary>
        private List<TilePlacementData> GetExposedPlacements(List<TilePlacementData> remainingPlacements, Dictionary<TilePlacementData, Vector3> localPositionsByPlacement)
        {
            List<TilePlacementData> exposedPlacements = new List<TilePlacementData>();
            if (remainingPlacements == null || localPositionsByPlacement == null)
            {
                return exposedPlacements;
            }

            for (int index = 0; index < remainingPlacements.Count; index++)
            {
                TilePlacementData candidate = remainingPlacements[index];
                if (candidate == null || !localPositionsByPlacement.ContainsKey(candidate))
                {
                    continue;
                }

                if (IsPlacementExposed(candidate, remainingPlacements, localPositionsByPlacement))
                {
                    exposedPlacements.Add(candidate);
                }
            }

            return exposedPlacements;
        }

        /// <summary>
        /// Determines whether a tile placement has a clear outward-facing path on the current shell.
        /// </summary>
        private bool IsPlacementExposed(TilePlacementData placement, List<TilePlacementData> remainingPlacements, Dictionary<TilePlacementData, Vector3> localPositionsByPlacement)
        {
            if (placement == null || remainingPlacements == null || localPositionsByPlacement == null || !localPositionsByPlacement.TryGetValue(placement, out Vector3 placementPosition))
            {
                return false;
            }

            Vector3 outwardNormal = GetPlacementOutwardNormal(placement);
            float depthEpsilon = Mathf.Max(0.01f, GetSurfaceShellThickness() * 0.25f);
            Vector2 columnTolerance = GetSurfaceColumnTolerance(placement);
            bool placementUsesCustomFacing = UsesCustomFacing(placement);

            for (int index = 0; index < remainingPlacements.Count; index++)
            {
                TilePlacementData blocker = remainingPlacements[index];
                if (blocker == null || blocker == placement || !localPositionsByPlacement.TryGetValue(blocker, out Vector3 blockerPosition))
                {
                    continue;
                }

                if (!placementUsesCustomFacing && !UsesCustomFacing(blocker) && blocker.FacingDirection != placement.FacingDirection)
                {
                    continue;
                }

                if ((placementUsesCustomFacing || UsesCustomFacing(blocker))
                    && Vector3.Dot(outwardNormal, GetPlacementOutwardNormal(blocker)) < 0.95f)
                {
                    continue;
                }

                if (Vector3.Dot(blockerPosition, outwardNormal) <= Vector3.Dot(placementPosition, outwardNormal) + depthEpsilon)
                {
                    continue;
                }

                if (SharesSurfaceColumn(placement, placementPosition, blocker, blockerPosition, columnTolerance))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Orders exposed placements so pairing peels the outer shells evenly.
        /// </summary>
        private int CompareExposedPlacements(TilePlacementData left, TilePlacementData right, Dictionary<TilePlacementData, Vector3> localPositionsByPlacement)
        {
            if (ReferenceEquals(left, right))
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

            int shellComparison = left.ShellIndex.CompareTo(right.ShellIndex);
            if (shellComparison != 0)
            {
                return shellComparison;
            }

            if (!localPositionsByPlacement.TryGetValue(left, out Vector3 leftPosition) || !localPositionsByPlacement.TryGetValue(right, out Vector3 rightPosition))
            {
                return left.FacingDirection.CompareTo(right.FacingDirection);
            }

            float leftDepth = Vector3.Dot(leftPosition, GetPlacementOutwardNormal(left));
            float rightDepth = Vector3.Dot(rightPosition, GetPlacementOutwardNormal(right));
            int depthComparison = rightDepth.CompareTo(leftDepth);
            if (depthComparison != 0)
            {
                return depthComparison;
            }

            int facingComparison = left.FacingDirection.CompareTo(right.FacingDirection);
            if (facingComparison != 0)
            {
                return facingComparison;
            }

            return leftPosition.sqrMagnitude.CompareTo(rightPosition.sqrMagnitude);
        }

        /// <summary>
        /// Chooses a partner for the exposed tile using the scoring profile of the current difficulty tier.
        /// Easy prefers a different face whenever one is available, then keeps the pair close and on the same shell when possible.
        /// </summary>
        private TilePlacementData FindBestPairCandidate(TilePlacementData first, List<TilePlacementData> exposedPlacements, Dictionary<TilePlacementData, Vector3> localPositionsByPlacement, LevelDifficulty difficulty, System.Random random)
        {
            if (first == null || exposedPlacements == null || localPositionsByPlacement == null || !localPositionsByPlacement.TryGetValue(first, out Vector3 firstPosition))
            {
                return null;
            }

            bool requireDifferentFace = difficulty == LevelDifficulty.Easy && HasDifferentFaceCandidate(first, exposedPlacements);
            TilePlacementData bestCandidate = null;
            float bestScore = float.MinValue;
            for (int index = 0; index < exposedPlacements.Count; index++)
            {
                TilePlacementData candidate = exposedPlacements[index];
                if (candidate == null || candidate == first || !localPositionsByPlacement.TryGetValue(candidate, out Vector3 candidatePosition))
                {
                    continue;
                }

                if (requireDifferentFace && ArePlacementsFacingSimilar(first, candidate))
                {
                    continue;
                }

                float distance = Vector3.Distance(firstPosition, candidatePosition);
                float score = ScorePairCandidate(first, candidate, distance, difficulty);

                if (score > bestScore || (Mathf.Approximately(score, bestScore) && random != null && random.NextDouble() < 0.5d))
                {
                    bestScore = score;
                    bestCandidate = candidate;
                }
            }

            return bestCandidate;
        }

        private static bool HasDifferentFaceCandidate(TilePlacementData first, List<TilePlacementData> exposedPlacements)
        {
            if (first == null || exposedPlacements == null)
            {
                return false;
            }

            for (int index = 0; index < exposedPlacements.Count; index++)
            {
                TilePlacementData candidate = exposedPlacements[index];
                if (candidate != null && candidate != first && !ArePlacementsFacingSimilar(first, candidate))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Scores a candidate pair based on the configured difficulty tier.
        /// Easy keeps pairs close and on different faces when possible, while harder tiers increasingly split matches across faces and shell layers.
        /// </summary>
        private static float ScorePairCandidate(TilePlacementData first, TilePlacementData candidate, float distance, LevelDifficulty difficulty)
        {
            int shellDelta = Mathf.Abs(candidate.ShellIndex - first.ShellIndex);
            bool sameShell = shellDelta == 0;
            bool sameFacing = ArePlacementsFacingSimilar(first, candidate);
            bool oppositeFacing = ArePlacementsFacingOpposite(first, candidate);
            float shellSpread = Mathf.Min(3, shellDelta);

            switch (difficulty)
            {
                case LevelDifficulty.Easy:
                    return (sameShell ? 1000f : -shellDelta * 250f)
                        + (sameFacing ? 150f : 0f)
                        - (distance * 25f);

                case LevelDifficulty.Normal:
                    return (sameShell ? -60f : 160f + (shellSpread * 80f))
                        + (sameFacing ? 20f : 70f)
                        + (oppositeFacing ? 30f : 0f)
                        + (distance * 6f);

                case LevelDifficulty.Hard:
                    return (sameShell ? -420f : 340f + (shellSpread * 220f))
                        + (sameFacing ? -140f : 190f)
                        + (oppositeFacing ? 120f : 0f)
                        + (shellDelta >= 2 ? 180f : 0f)
                        + (distance * 14f);

                case LevelDifficulty.Expert:
                default:
                    return (sameShell ? -780f : 520f + (shellSpread * 320f))
                        + (sameFacing ? -220f : 280f)
                        + (oppositeFacing ? 180f : 0f)
                        + (shellDelta >= 2 ? 320f : 0f)
                        + (distance * 22f);
            }
        }

        /// <summary>
        /// Determines whether two surface directions are on opposite faces of the wrapped shell.
        /// </summary>
        private static bool IsOppositeFacingDirection(VoxelGridDirection first, VoxelGridDirection second)
        {
            return (first == VoxelGridDirection.Left && second == VoxelGridDirection.Right)
                || (first == VoxelGridDirection.Right && second == VoxelGridDirection.Left)
                || (first == VoxelGridDirection.Down && second == VoxelGridDirection.Up)
                || (first == VoxelGridDirection.Up && second == VoxelGridDirection.Down)
                || (first == VoxelGridDirection.Back && second == VoxelGridDirection.Forward)
                || (first == VoxelGridDirection.Forward && second == VoxelGridDirection.Back);
        }

        /// <summary>
        /// Determines whether two placements occupy the same face column across nested shells.
        /// </summary>
        private static bool SharesSurfaceColumn(TilePlacementData first, Vector3 firstPosition, TilePlacementData second, Vector3 secondPosition, Vector2 columnTolerance)
        {
            if (UsesCustomFacing(first) || UsesCustomFacing(second))
            {
                return SharesCustomSurfaceColumn(GetPlacementOutwardNormal(first), firstPosition, secondPosition, columnTolerance);
            }

            return SharesSurfaceColumn(first.FacingDirection, firstPosition, secondPosition, columnTolerance);
        }

        private static bool SharesSurfaceColumn(VoxelGridDirection facingDirection, Vector3 firstPosition, Vector3 secondPosition, Vector2 columnTolerance)
        {
            switch (facingDirection)
            {
                case VoxelGridDirection.Left:
                case VoxelGridDirection.Right:
                    return Mathf.Abs(firstPosition.y - secondPosition.y) <= columnTolerance.x
                        && Mathf.Abs(firstPosition.z - secondPosition.z) <= columnTolerance.y;

                case VoxelGridDirection.Down:
                case VoxelGridDirection.Up:
                    return Mathf.Abs(firstPosition.x - secondPosition.x) <= columnTolerance.x
                        && Mathf.Abs(firstPosition.z - secondPosition.z) <= columnTolerance.y;

                case VoxelGridDirection.Back:
                case VoxelGridDirection.Forward:
                default:
                    return Mathf.Abs(firstPosition.x - secondPosition.x) <= columnTolerance.x
                        && Mathf.Abs(firstPosition.y - secondPosition.y) <= columnTolerance.y;
            }
        }

        private static bool SharesCustomSurfaceColumn(Vector3 outwardNormal, Vector3 firstPosition, Vector3 secondPosition, Vector2 columnTolerance)
        {
            Vector3 horizontalAxis = Vector3.Cross(Vector3.up, outwardNormal);
            if (horizontalAxis.sqrMagnitude <= 0.0001f)
            {
                horizontalAxis = Vector3.Cross(Vector3.right, outwardNormal);
            }

            horizontalAxis.Normalize();
            Vector3 delta = secondPosition - firstPosition;
            float horizontalDelta = Mathf.Abs(Vector3.Dot(delta, horizontalAxis));
            float verticalDelta = Mathf.Abs(Vector3.Dot(delta, Vector3.up));
            return horizontalDelta <= columnTolerance.x && verticalDelta <= columnTolerance.y;
        }

        /// <summary>
        /// Resolves the tolerance used to decide whether two shells overlap on the same face column.
        /// </summary>
        private Vector2 GetSurfaceColumnTolerance(TilePlacementData placement)
        {
            if (UsesCustomFacing(placement))
            {
                CubeTileMetrics metrics = ResolveCubeTileMetrics();
                return new Vector2(
                    Mathf.Max(0.05f, metrics.FaceWidth * 0.35f),
                    Mathf.Max(0.05f, metrics.FaceHeight * 0.35f));
            }

            return GetSurfaceColumnTolerance(placement != null ? placement.FacingDirection : VoxelGridDirection.Forward);
        }

        private Vector2 GetSurfaceColumnTolerance(VoxelGridDirection facingDirection)
        {
            Vector3 step = layoutOverride != null ? layoutOverride.CellStep : Vector3.one;
            switch (facingDirection)
            {
                case VoxelGridDirection.Left:
                case VoxelGridDirection.Right:
                    return new Vector2(Mathf.Max(0.05f, step.y * 0.35f), Mathf.Max(0.05f, step.z * 0.35f));

                case VoxelGridDirection.Down:
                case VoxelGridDirection.Up:
                    return new Vector2(Mathf.Max(0.05f, step.x * 0.35f), Mathf.Max(0.05f, step.z * 0.35f));

                case VoxelGridDirection.Back:
                case VoxelGridDirection.Forward:
                default:
                    return new Vector2(Mathf.Max(0.05f, step.x * 0.35f), Mathf.Max(0.05f, step.y * 0.35f));
            }
        }

        private static bool UsesCustomFacing(TilePlacementData placement)
        {
            return placement != null && placement.UseCustomLocalEulerAngles;
        }

        private static bool ArePlacementsFacingSimilar(TilePlacementData first, TilePlacementData second)
        {
            if (first == null || second == null)
            {
                return false;
            }

            if (!UsesCustomFacing(first) && !UsesCustomFacing(second))
            {
                return first.FacingDirection == second.FacingDirection;
            }

            return Vector3.Dot(GetPlacementOutwardNormal(first), GetPlacementOutwardNormal(second)) >= 0.9f;
        }

        private static bool ArePlacementsFacingOpposite(TilePlacementData first, TilePlacementData second)
        {
            if (first == null || second == null)
            {
                return false;
            }

            if (!UsesCustomFacing(first) && !UsesCustomFacing(second))
            {
                return IsOppositeFacingDirection(first.FacingDirection, second.FacingDirection);
            }

            return Vector3.Dot(GetPlacementOutwardNormal(first), GetPlacementOutwardNormal(second)) <= -0.9f;
        }

        /// <summary>
        /// Stores one exposed pair selected for the authored solve path.
        /// </summary>
        private sealed class TilePlacementPair
        {
            public TilePlacementPair(TilePlacementData first, TilePlacementData second)
            {
                First = first;
                Second = second;
            }

            public TilePlacementData First { get; }

            public TilePlacementData Second { get; }
        }

        /// <summary>
        /// Creates a single tile definition with an optional 180-degree Y flip.
        /// </summary>
        private LevelTileDefinition CreateTileDefinition(LevelShapeType shape, int matchId, int tileIndex, TilePlacementData placement, VoxelGridSize shapeGridSize, VoxelGridSize logicalGridSize, float flippedTileChance, System.Random random)
        {
            bool flipTile = ShouldFlipGeneratedTile(placement, flippedTileChance, random);
            return new LevelTileDefinition
            {
                MatchId = matchId,
                GridCoordinate = authoredShape != null
                    ? placement.Coordinate
                    : ResolveGeneratedGridCoordinate(shape, placement, tileIndex, logicalGridSize),
                SurfaceShellIndex = placement.ShellIndex,
                UseCustomLocalPosition = true,
                LocalPosition = GetCompactedSurfaceTileLocalPosition(placement, shapeGridSize),
                LocalEulerAngles = GetPlacementRotationEuler(placement, flipTile),
            };
        }

        private static Vector3Int ResolveGeneratedGridCoordinate(LevelShapeType shape, TilePlacementData placement, int tileIndex, VoxelGridSize logicalGridSize)
        {
            if (shape == LevelShapeType.Cylinder && placement != null)
            {
                return placement.Coordinate;
            }

            return GetLogicalGridCoordinate(tileIndex, logicalGridSize);
        }

        private static bool ShouldFlipGeneratedTile(TilePlacementData placement, float flippedTileChance, System.Random random)
        {
            if (placement == null)
            {
                return false;
            }

            if (placement.UseCustomLocalPosition || placement.SurfaceSlotIndex >= -1)
            {
                return false;
            }

            return random != null && random.NextDouble() <= flippedTileChance;
        }

        /// <summary>
        /// Resolves the wrapped tile local position and pulls inner shells outward until they touch the previous shell.
        /// </summary>
        private Vector3 GetCompactedSurfaceTileLocalPosition(TilePlacementData placement, VoxelGridSize shapeGridSize)
        {
            if (placement == null)
            {
                return Vector3.zero;
            }

            Vector3 localPosition = placement.UseCustomLocalPosition
                ? placement.CustomLocalPosition
                : GetSurfaceTileLocalPosition(placement, shapeGridSize);

            if (placement.UseCustomLocalPosition && !placement.ApplyShellCompaction)
            {
                return localPosition;
            }

            int shellIndex = Mathf.Max(0, placement.ShellIndex);
            if (shellIndex == 0)
            {
                return localPosition;
            }

            Vector3 faceNormal = ((Vector3)VoxelGridDirections.GetOffset(placement.FacingDirection)).normalized;
            float faceStep = GetSurfaceFaceStep(placement.FacingDirection);
            float shellThickness = GetSurfaceShellThickness();
            float shellGap = Mathf.Max(0f, faceStep - shellThickness);
            if (shellGap <= Mathf.Epsilon)
            {
                return localPosition;
            }

            return localPosition + (faceNormal * (shellGap * shellIndex));
        }

        /// <summary>
        /// Extracts the shell layers from a filled voxel shape.
        /// </summary>
        private static List<List<TilePlacementData>> BuildShells(List<Vector3Int> occupiedCoordinates)
        {
            List<List<TilePlacementData>> shells = new List<List<TilePlacementData>>();
            HashSet<Vector3Int> remaining = new HashSet<Vector3Int>(occupiedCoordinates);

            while (remaining.Count > 0)
            {
                List<TilePlacementData> shell = ExtractSurfaceShell(remaining);
                if (shell.Count == 0)
                {
                    break;
                }

                shells.Add(shell);
                for (int index = 0; index < shell.Count; index++)
                {
                    remaining.Remove(shell[index].Coordinate);
                }
            }

            return shells;
        }

        /// <summary>
        /// Finds the current surface voxels of the supplied volume.
        /// </summary>
        private static List<TilePlacementData> ExtractSurfaceShell(HashSet<Vector3Int> occupiedCoordinates)
        {
            List<TilePlacementData> shell = new List<TilePlacementData>();
            foreach (Vector3Int coordinate in occupiedCoordinates)
            {
                for (int directionIndex = 0; directionIndex < NeighborDirections.Length; directionIndex++)
                {
                    Vector3Int neighbor = coordinate + NeighborDirections[directionIndex];
                    if (!occupiedCoordinates.Contains(neighbor))
                    {
                        VoxelGridDirection facingDirection = ToGridDirection(NeighborDirections[directionIndex]);
                        shell.Add(new TilePlacementData
                        {
                            Coordinate = coordinate,
                            FacingDirection = facingDirection,
                            SurfaceSlotIndex = -1,
                        });
                    }
                }
            }

            return shell;
        }

        /// <summary>
        /// Resolves the face direction a shell tile should point toward.
        /// </summary>
        private static VoxelGridDirection ResolveFacingDirection(Vector3Int coordinate, List<VoxelGridDirection> exposedDirections, Vector3 center)
        {
            if (exposedDirections == null || exposedDirections.Count == 0)
            {
                return VoxelGridDirection.Up;
            }

            if (exposedDirections.Count == 1)
            {
                return exposedDirections[0];
            }

            Vector3 outwardVector = ((Vector3)coordinate - center).normalized;
            float bestScore = float.NegativeInfinity;
            VoxelGridDirection bestDirection = exposedDirections[0];

            for (int index = 0; index < exposedDirections.Count; index++)
            {
                VoxelGridDirection direction = exposedDirections[index];
                float score = Vector3.Dot(outwardVector, ((Vector3)VoxelGridDirections.GetOffset(direction)).normalized);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestDirection = direction;
                }
            }

            return bestDirection;
        }

        /// <summary>
        /// Calculates the center of a coordinate set for outward-direction scoring.
        /// </summary>
        private static Vector3 CalculateCoordinateSetCenter(HashSet<Vector3Int> coordinates)
        {
            if (coordinates == null || coordinates.Count == 0)
            {
                return Vector3.zero;
            }

            Vector3 sum = Vector3.zero;
            foreach (Vector3Int coordinate in coordinates)
            {
                sum += coordinate;
            }

            return sum / coordinates.Count;
        }

        /// <summary>
        /// Converts a cardinal offset into the corresponding voxel-grid direction.
        /// </summary>
        private static VoxelGridDirection ToGridDirection(Vector3Int offset)
        {
            if (offset == Vector3Int.left)
            {
                return VoxelGridDirection.Left;
            }

            if (offset == Vector3Int.right)
            {
                return VoxelGridDirection.Right;
            }

            if (offset == Vector3Int.down)
            {
                return VoxelGridDirection.Down;
            }

            if (offset == Vector3Int.up)
            {
                return VoxelGridDirection.Up;
            }

            if (offset == new Vector3Int(0, 0, -1))
            {
                return VoxelGridDirection.Back;
            }

            return VoxelGridDirection.Forward;
        }

        /// <summary>
        /// Builds an outward-facing tile rotation, with optional spin around the face normal.
        /// </summary>
        private static Vector3 GetFacingRotationEuler(VoxelGridDirection facingDirection, bool flipTile)
        {
            Quaternion outwardRotation = Quaternion.Euler(GetFacingBaseEuler(facingDirection));
            if (flipTile)
            {
                Vector3 outwardNormal = VoxelGridDirections.GetOffset(facingDirection);
                outwardRotation = Quaternion.AngleAxis(180f, outwardNormal) * outwardRotation;
            }

            return outwardRotation.eulerAngles;
        }

        private static Vector3 GetPlacementRotationEuler(TilePlacementData placement, bool flipTile)
        {
            if (placement == null)
            {
                return Vector3.zero;
            }

            if (!placement.UseCustomLocalEulerAngles)
            {
                return GetFacingRotationEuler(placement.FacingDirection, flipTile);
            }

            Quaternion outwardRotation = Quaternion.Euler(placement.CustomLocalEulerAngles);
            if (flipTile)
            {
                outwardRotation = Quaternion.AngleAxis(180f, GetPlacementOutwardNormal(placement)) * outwardRotation;
            }

            return outwardRotation.eulerAngles;
        }

        private static Vector3 GetPlacementOutwardNormal(TilePlacementData placement)
        {
            if (placement == null)
            {
                return Vector3.forward;
            }

            if (placement.UseCustomLocalEulerAngles)
            {
                return (Quaternion.Euler(placement.CustomLocalEulerAngles) * Vector3.up).normalized;
            }

            return ((Vector3)VoxelGridDirections.GetOffset(placement.FacingDirection)).normalized;
        }

        /// <summary>
        /// Resolves the exact face rotation convention already used by loaded level assets.
        /// Opposite faces share the same reading direction convention, while side faces stay vertical.
        /// </summary>
        private static Vector3 GetFacingBaseEuler(VoxelGridDirection facingDirection)
        {
            switch (facingDirection)
            {
                case VoxelGridDirection.Left:
                    return new Vector3(0f, 0f, 90f);

                case VoxelGridDirection.Right:
                    return new Vector3(0f, 0f, 270f);

                case VoxelGridDirection.Down:
                    return new Vector3(0f, 0f, 180f);

                case VoxelGridDirection.Up:
                    return Vector3.zero;

                case VoxelGridDirection.Back:
                    return new Vector3(270f, 0f, 0f);

                case VoxelGridDirection.Forward:
                default:
                    return new Vector3(90f, 0f, 0f);
            }
        }

        /// <summary>
        /// Converts a sequential tile index into a compact logical runtime coordinate.
        /// </summary>
        private static Vector3Int GetLogicalGridCoordinate(int tileIndex, VoxelGridSize logicalGridSize)
        {
            int width = Mathf.Max(1, logicalGridSize.Width);
            int height = Mathf.Max(1, logicalGridSize.Height);
            int area = width * height;
            int x = tileIndex % width;
            int y = (tileIndex / width) % height;
            int z = tileIndex / area;
            return new Vector3Int(x, y, z);
        }

        /// <summary>
        /// Builds a compact cube-like logical grid large enough for every generated tile.
        /// </summary>
        private static VoxelGridSize BuildLogicalGridSize(int tileCount)
        {
            int safeTileCount = Mathf.Max(1, tileCount);
            int side = Mathf.Max(1, Mathf.CeilToInt(Mathf.Pow(safeTileCount, 1f / 3f)));

            while (side * side * side < safeTileCount)
            {
                side++;
            }

            return new VoxelGridSize(side, side, side);
        }

        private static VoxelGridSize ResolveSerializedGridSize(IReadOnlyList<LevelTileDefinition> tiles, VoxelGridSize fallbackGridSize)
        {
            if (tiles == null || tiles.Count == 0)
            {
                return fallbackGridSize;
            }

            int width = 0;
            int height = 0;
            int depth = 0;
            bool hasTile = false;

            for (int index = 0; index < tiles.Count; index++)
            {
                LevelTileDefinition tile = tiles[index];
                if (tile == null)
                {
                    continue;
                }

                hasTile = true;
                width = Mathf.Max(width, tile.GridCoordinate.x + 1);
                height = Mathf.Max(height, tile.GridCoordinate.y + 1);
                depth = Mathf.Max(depth, tile.GridCoordinate.z + 1);
            }

            return hasTile ? new VoxelGridSize(width, height, depth) : fallbackGridSize;
        }

        private static bool ShouldUseSurfaceTilePlacement(LevelShapeType shape, int layerCount)
        {
            if (shape == LevelShapeType.Cylinder && layerCount <= 1)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Calculates the custom local-space position of a tile slot wrapped on an exposed face.
        /// </summary>
        private Vector3 GetSurfaceTileLocalPosition(TilePlacementData placement, VoxelGridSize shapeGridSize)
        {
            Vector3 cellSize = layoutOverride != null ? layoutOverride.CellSize : Vector3.one;
            Vector3 cellSpacing = layoutOverride != null ? layoutOverride.CellSpacing : Vector3.zero;
            Vector3 originOffset = layoutOverride != null ? layoutOverride.OriginOffset : Vector3.zero;
            VoxelGridPivotMode pivotMode = layoutOverride != null ? layoutOverride.PivotMode : VoxelGridPivotMode.Center;
            Vector3 step = GetSurfaceTileStep(placement.FacingDirection, cellSize + cellSpacing);
            Vector3 voxelCenter = GetStaticLocalPosition(placement.Coordinate, shapeGridSize, step, originOffset, pivotMode);

            Vector3 faceNormal = ((Vector3)VoxelGridDirections.GetOffset(placement.FacingDirection)).normalized;
            Vector3 halfFaceOffset = Vector3.Scale(faceNormal, cellSize) * 0.5f;
            Vector3 paddingOffset = faceNormal * 0.02f;

            if (placement.SurfaceSlotIndex < 0)
            {
                return voxelCenter + halfFaceOffset + paddingOffset;
            }

            Vector3 tangent = GetSurfaceTangent(placement.FacingDirection);
            Vector3 slotOffset = tangent * GetSurfaceSlotDistance(placement.FacingDirection, step) * (placement.SurfaceSlotIndex == 0 ? -1f : 1f);
            return voxelCenter + halfFaceOffset + paddingOffset + slotOffset;
        }

        /// <summary>
        /// Adds a face-local in-plane gap so generated surface tiles separate visually without pushing the whole shell toward or away from the camera.
        /// </summary>
        private Vector3 GetSurfaceTileStep(VoxelGridDirection facingDirection, Vector3 baseStep)
        {
            float gap = GetSurfaceTileGap(facingDirection);
            if (gap <= Mathf.Epsilon)
            {
                return baseStep;
            }

            switch (facingDirection)
            {
                case VoxelGridDirection.Left:
                case VoxelGridDirection.Right:
                    return new Vector3(baseStep.x, baseStep.y + gap, baseStep.z + gap);

                case VoxelGridDirection.Down:
                case VoxelGridDirection.Up:
                    return new Vector3(baseStep.x + gap, baseStep.y, baseStep.z + gap);

                case VoxelGridDirection.Back:
                case VoxelGridDirection.Forward:
                default:
                    return new Vector3(baseStep.x + gap, baseStep.y + gap, baseStep.z);
            }
        }

        /// <summary>
        /// Resolves the face-specific in-plane gap used for tiles on the supplied face.
        /// </summary>
        private float GetSurfaceTileGap(VoxelGridDirection facingDirection)
        {
            float baseGap = Mathf.Max(0f, surfaceTileGap);
            switch (facingDirection)
            {
                case VoxelGridDirection.Left:
                case VoxelGridDirection.Right:
                    return baseGap + Mathf.Max(0f, leftRightSurfaceGapOffset);

                case VoxelGridDirection.Down:
                case VoxelGridDirection.Up:
                    return baseGap + Mathf.Max(0f, upDownSurfaceGapOffset);

                case VoxelGridDirection.Back:
                case VoxelGridDirection.Forward:
                default:
                    return baseGap + Mathf.Max(0f, frontBackSurfaceGapOffset);
            }
        }

        /// <summary>
        /// Converts a panel index into a centered local coordinate that supports both odd and even shell sizes.
        /// </summary>
        private static float GetCenteredPanelCoordinate(int index, int count)
        {
            return index - ((count - 1) * 0.5f);
        }

        /// <summary>
        /// Converts a panel index into an exact centered world-space coordinate for the supplied step distance.
        /// </summary>
        private static float GetCenteredPanelCoordinate(int index, int count, float step)
        {
            return ((index + 0.5f) - (count * 0.5f)) * step;
        }

        /// <summary>
        /// Resolves the normal-axis spacing currently used by surface-wrapped placements on the supplied face.
        /// </summary>
        private float GetSurfaceFaceStep(VoxelGridDirection facingDirection)
        {
            Vector3 step = layoutOverride != null ? layoutOverride.CellStep : new Vector3(0.95f, 0.45f, 0.7f);
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
        /// Resolves the physical tile thickness used when shell layers are compacted against each other.
        /// Prefers the assigned tile prefab bounds so wrapped shell layers stay visually tight across every shape.
        /// </summary>
        private float GetSurfaceShellThickness()
        {
            if (TryGetTilePrefabPlacementBounds(out Bounds placementBounds))
            {
                return Mathf.Max(0.01f, placementBounds.size.y);
            }

            if (TryGetTilePrefabBounds(out Bounds prefabBounds))
            {
                return Mathf.Max(0.01f, prefabBounds.size.y);
            }

            Vector3 cellSize = layoutOverride != null ? layoutOverride.CellSize : new Vector3(0.95f, 0.45f, 0.7f);
            return Mathf.Max(0.01f, cellSize.y);
        }

        /// <summary>
        /// Recreates centered local-space grid math without allocating a runtime voxel grid.
        /// </summary>
        private static Vector3 GetStaticLocalPosition(Vector3Int coordinate, VoxelGridSize gridSize, Vector3 step, Vector3 originOffset, VoxelGridPivotMode pivotMode)
        {
            Vector3 position = Vector3.Scale((Vector3)coordinate, step);
            if (pivotMode == VoxelGridPivotMode.Center)
            {
                Vector3 centerOffset = new Vector3(
                    (gridSize.Width - 1) * step.x,
                    (gridSize.Height - 1) * step.y,
                    (gridSize.Depth - 1) * step.z) * 0.5f;

                position -= centerOffset;
            }

            return position + originOffset;
        }

        /// <summary>
        /// Resolves the tangent axis used to split one face into two side-by-side slots.
        /// </summary>
        private static Vector3 GetSurfaceTangent(VoxelGridDirection facingDirection)
        {
            switch (facingDirection)
            {
                case VoxelGridDirection.Left:
                case VoxelGridDirection.Right:
                    return Vector3.forward;

                case VoxelGridDirection.Down:
                case VoxelGridDirection.Up:
                case VoxelGridDirection.Back:
                case VoxelGridDirection.Forward:
                default:
                    return Vector3.right;
            }
        }

        /// <summary>
        /// Resolves the half-lane spacing used by the two surface slots on one face.
        /// </summary>
        private static float GetSurfaceSlotDistance(VoxelGridDirection facingDirection, Vector3 step)
        {
            switch (facingDirection)
            {
                case VoxelGridDirection.Left:
                case VoxelGridDirection.Right:
                    return Mathf.Max(0.01f, step.z * 0.25f);

                case VoxelGridDirection.Down:
                case VoxelGridDirection.Up:
                case VoxelGridDirection.Back:
                case VoxelGridDirection.Forward:
                default:
                    return Mathf.Max(0.01f, step.x * 0.25f);
            }
        }

        /// <summary>
        /// Shuffles the supplied list using Fisher-Yates.
        /// </summary>
        private static void Shuffle<TValue>(IList<TValue> values, System.Random random)
        {
            for (int index = values.Count - 1; index > 0; index--)
            {
                int swapIndex = random.Next(0, index + 1);
                TValue temp = values[index];
                values[index] = values[swapIndex];
                values[swapIndex] = temp;
            }
        }

        /// <summary>
        /// Resolves the number of shell layers represented by the generated tiles.
        /// </summary>
        private static int GetLayerCount(IReadOnlyList<LevelTileDefinition> tiles)
        {
            if (tiles == null || tiles.Count == 0)
            {
                return 0;
            }

            int maxShellIndex = 0;
            bool hasTile = false;
            for (int index = 0; index < tiles.Count; index++)
            {
                LevelTileDefinition tile = tiles[index];
                if (tile == null)
                {
                    continue;
                }

                hasTile = true;
                maxShellIndex = Mathf.Max(maxShellIndex, tile.SurfaceShellIndex);
            }

            return hasTile ? maxShellIndex + 1 : 0;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Writes a generated payload into a concrete level asset instance.
        /// </summary>
        private static void ApplyGeneratedData(LevelDefinition asset, GeneratedLevelData data)
        {
            SerializedObject serializedObject = new SerializedObject(asset);
            serializedObject.FindProperty("<LevelName>k__BackingField").stringValue = data.LevelName;

            SerializedProperty gridSizeProperty = serializedObject.FindProperty("<GridSize>k__BackingField");
            gridSizeProperty.FindPropertyRelative("width").intValue = data.GridSize.Width;
            gridSizeProperty.FindPropertyRelative("height").intValue = data.GridSize.Height;
            gridSizeProperty.FindPropertyRelative("depth").intValue = data.GridSize.Depth;

            serializedObject.FindProperty("<LayoutOverride>k__BackingField").objectReferenceValue = data.LayoutOverride;
            serializedObject.FindProperty("<Shape>k__BackingField").intValue = (int)data.Shape;
            serializedObject.FindProperty("<UseSurfaceTilePlacement>k__BackingField").boolValue = data.UseSurfaceTilePlacement;
            serializedObject.FindProperty("<BlockCount>k__BackingField").intValue = Mathf.Max(1, data.BlockCount);
            serializedObject.FindProperty("<BlockSpacingCells>k__BackingField").intValue = Mathf.Max(0, data.BlockSpacingCells);
            serializedObject.FindProperty("<LayerCount>k__BackingField").intValue = Mathf.Max(0, data.LayerCount);
            serializedObject.FindProperty("<Difficulty>k__BackingField").enumValueIndex = (int)data.Difficulty;
            serializedObject.FindProperty("<FaceDownTileRatio>k__BackingField").floatValue = Mathf.Clamp01(data.FaceDownTileRatio);
            serializedObject.FindProperty("<ComboTileRatio>k__BackingField").floatValue = Mathf.Clamp01(data.ComboTileRatio);

            SerializedProperty fillCategoryProperty = serializedObject.FindProperty("<FillCategoryNames>k__BackingField");
            fillCategoryProperty.ClearArray();
            List<string> fillCategoryNames = data.FillCategoryNames ?? new List<string>();
            for (int index = 0; index < fillCategoryNames.Count; index++)
            {
                fillCategoryProperty.InsertArrayElementAtIndex(index);
                fillCategoryProperty.GetArrayElementAtIndex(index).stringValue = fillCategoryNames[index];
            }

            SerializedProperty tilesProperty = serializedObject.FindProperty("<Tiles>k__BackingField");
            tilesProperty.ClearArray();
            for (int index = 0; index < data.Tiles.Count; index++)
            {
                LevelTileDefinition tile = data.Tiles[index];
                tilesProperty.InsertArrayElementAtIndex(index);
                SerializedProperty tileProperty = tilesProperty.GetArrayElementAtIndex(index);
                tileProperty.FindPropertyRelative("matchId").intValue = tile.MatchId;

                SerializedProperty coordinateProperty = tileProperty.FindPropertyRelative("gridCoordinate");
                coordinateProperty.FindPropertyRelative("x").intValue = tile.GridCoordinate.x;
                coordinateProperty.FindPropertyRelative("y").intValue = tile.GridCoordinate.y;
                coordinateProperty.FindPropertyRelative("z").intValue = tile.GridCoordinate.z;

                tileProperty.FindPropertyRelative("surfaceShellIndex").intValue = tile.SurfaceShellIndex;
                tileProperty.FindPropertyRelative("useCustomLocalPosition").boolValue = tile.UseCustomLocalPosition;

                SerializedProperty localPositionProperty = tileProperty.FindPropertyRelative("localPosition");
                localPositionProperty.vector3Value = tile.LocalPosition;

                SerializedProperty eulerProperty = tileProperty.FindPropertyRelative("localEulerAngles");
                eulerProperty.vector3Value = tile.LocalEulerAngles;
            }

            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        /// <summary>
        /// Applies generated assets into the configured level catalog.
        /// </summary>
        private void ApplyCatalogEntries(List<LevelDefinition> generatedAssets, List<GeneratedLevelData> generatedData)
        {
            SerializedObject catalogObject = new SerializedObject(targetCatalog);
            SerializedProperty levelsProperty = catalogObject.FindProperty("<Levels>k__BackingField");

            for (int index = 0; index < generatedAssets.Count; index++)
            {
                LevelDefinition generatedAsset = generatedAssets[index];
                GeneratedLevelData generatedEntry = generatedData != null && index < generatedData.Count ? generatedData[index] : null;
                if (generatedAsset == null)
                {
                    continue;
                }

                if (generatedEntry != null && generatedEntry.CatalogIndex >= 0)
                {
                    if (levelsProperty.arraySize <= generatedEntry.CatalogIndex)
                    {
                        int previousSize = levelsProperty.arraySize;
                        levelsProperty.arraySize = generatedEntry.CatalogIndex + 1;
                        for (int fillIndex = previousSize; fillIndex < levelsProperty.arraySize; fillIndex++)
                        {
                            levelsProperty.GetArrayElementAtIndex(fillIndex).objectReferenceValue = null;
                        }
                    }

                    levelsProperty.GetArrayElementAtIndex(generatedEntry.CatalogIndex).objectReferenceValue = generatedAsset;
                    continue;
                }

                int existingIndex = FindCatalogIndex(levelsProperty, generatedAsset);
                if (existingIndex >= 0)
                {
                    levelsProperty.GetArrayElementAtIndex(existingIndex).objectReferenceValue = generatedAsset;
                    continue;
                }

                int insertIndex = levelsProperty.arraySize;
                levelsProperty.InsertArrayElementAtIndex(insertIndex);
                levelsProperty.GetArrayElementAtIndex(insertIndex).objectReferenceValue = generatedAsset;
            }

            catalogObject.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(targetCatalog);
        }

        private int GetHighestExistingSequence()
        {
            string[] assetGuids = AssetDatabase.FindAssets("t:LevelDefinition", new[] { outputFolder });
            int highestSequence = 0;

            for (int index = 0; index < assetGuids.Length; index++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(assetGuids[index]);
                string assetName = System.IO.Path.GetFileNameWithoutExtension(assetPath);
                int separatorIndex = assetName.LastIndexOf('_');
                if (separatorIndex < 0 || separatorIndex >= assetName.Length - 1)
                {
                    continue;
                }

                string suffix = assetName.Substring(separatorIndex + 1);
                if (!int.TryParse(suffix, out int sequence))
                {
                    continue;
                }

                if (sequence > highestSequence)
                {
                    highestSequence = sequence;
                }
            }

            return highestSequence;
        }

        private static int FindCatalogIndex(SerializedProperty levelsProperty, LevelDefinition targetLevel)
        {
            for (int index = 0; index < levelsProperty.arraySize; index++)
            {
                if (levelsProperty.GetArrayElementAtIndex(index).objectReferenceValue == targetLevel)
                {
                    return index;
                }
            }

            return -1;
        }

        /// <summary>
        /// Ensures every path segment in the configured output folder exists as a Unity asset folder.
        /// </summary>
        private static void EnsureFolderExists(string folderPath)
        {
            string[] segments = folderPath.Split('/');
            string currentPath = segments[0];
            for (int index = 1; index < segments.Length; index++)
            {
                string nextPath = $"{currentPath}/{segments[index]}";
                if (!AssetDatabase.IsValidFolder(nextPath))
                {
                    AssetDatabase.CreateFolder(currentPath, segments[index]);
                }

                currentPath = nextPath;
            }
        }

        private static List<string> ResolveAvailableFillCategoryNames()
        {
            HashSet<string> seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> resolvedNames = new List<string>();
            string[] materialGuids = AssetDatabase.FindAssets("t:MahjongMaterialSO");

            for (int guidIndex = 0; guidIndex < materialGuids.Length; guidIndex++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(materialGuids[guidIndex]);
                MahjongMaterialSO materialLibrary = AssetDatabase.LoadAssetAtPath<MahjongMaterialSO>(assetPath);
                if (materialLibrary == null || materialLibrary.FillCategories == null)
                {
                    continue;
                }

                for (int categoryIndex = 0; categoryIndex < materialLibrary.FillCategories.Count; categoryIndex++)
                {
                    MahjongMaterialCategory category = materialLibrary.FillCategories[categoryIndex];
                    string categoryName = category != null ? category.CategoryName : null;
                    if (string.IsNullOrWhiteSpace(categoryName))
                    {
                        continue;
                    }

                    string trimmedName = categoryName.Trim();
                    if (seenNames.Add(trimmedName))
                    {
                        resolvedNames.Add(trimmedName);
                    }
                }
            }

            resolvedNames.Sort(StringComparer.OrdinalIgnoreCase);
            return resolvedNames;
        }

        /// <summary>
        /// Converts a level name into a safe asset file name.
        /// </summary>
        private static string SanitizeFileName(string fileName)
        {
            string sanitized = fileName;
            char[] invalidCharacters = System.IO.Path.GetInvalidFileNameChars();
            for (int index = 0; index < invalidCharacters.Length; index++)
            {
                sanitized = sanitized.Replace(invalidCharacters[index], '_');
            }

            return sanitized.Replace(' ', '_');
        }
#endif
    }
}
