using System;
using System.Collections.Generic;
using UnityEngine;
using MahjongOut3D.TileSystem;

namespace MahjongOut3D.LevelSystem
{
    /// <summary>
    /// Controls how manual placement positions are quantized in the authoring tool.
    /// </summary>
    public enum TileLayoutSnapMode
    {
        Off = 0,
        Adjacent = 1,
        Half = 2,
        Quarter = 4,
    }

    /// <summary>
    /// Chooses which tile footprint supplies the tangential half/quarter offset.
    /// </summary>
    public enum TileSnapOffsetSizeSource
    {
        SourceTile = 0,
        TargetTile = 1,
    }

    /// <summary>
    /// Selects the exact amount of tangential overlap for an adjacent tile.
    /// </summary>
    public enum TileAdjacentOffsetMode
    {
        Flush = 0,
        Quarter = 1,
        Half = 2,
    }

    /// <summary>
    /// Selects whether adjacent direction buttons follow the board or the tile pose.
    /// </summary>
    public enum TileAdjacentDirectionSpace
    {
        Board = 0,
        TilePose = 1,
        TilePrefab = 2,
    }

    /// <summary>
    /// Selects the default pose used when creating a new tile in the authoring window.
    /// </summary>
    public enum TileDefaultPlacementPose
    {
        Flat = 0,
        Standing = 1,
        Sideways = 2,
        Custom = 3,
    }

    /// <summary>
    /// Selects whether newly created tiles lie flat or stand vertically.
    /// </summary>
    public enum TilePlacementPosture
    {
        Horizontal = 0,
        Vertical = 1,
    }

    /// <summary>
    /// Stores one of the six cardinal tile surface directions and its in-plane roll.
    /// </summary>
    [Serializable]
    public sealed class TileSurfacePose
    {
        [SerializeField] private VoxelGridDirection face = VoxelGridDirection.Up;
        [SerializeField, Range(0, 3)] private int rollQuarterTurns;

        public VoxelGridDirection Face
        {
            get => face;
            set => face = value;
        }

        public int RollQuarterTurns
        {
            get => ((rollQuarterTurns % 4) + 4) % 4;
            set => rollQuarterTurns = ((value % 4) + 4) % 4;
        }
    }

    /// <summary>
    /// Authoring-only placement data for one manually positioned Mahjong tile.
    /// </summary>
    [Serializable]
    public sealed class TileAuthoringEntry
    {
        [SerializeField] private string stableId;
        [SerializeField] private int matchId;
        [SerializeField] private Vector3 localPosition;
        [SerializeField] private TileSurfacePose pose = new TileSurfacePose();
        [SerializeField] private bool useSnapOffset;
        [SerializeField] private string snapSourceStableId;
        [SerializeField] private VoxelGridDirection snapDirection = VoxelGridDirection.Right;
        [SerializeField] private TileAdjacentDirectionSpace adjacentDirectionSpace = TileAdjacentDirectionSpace.TilePrefab;
        [SerializeField, Range(-4, 4)] private int snapOffsetU;
        [SerializeField, Range(-4, 4)] private int snapOffsetV;
        [SerializeField] private TileSnapOffsetSizeSource snapOffsetSizeSource = TileSnapOffsetSizeSource.SourceTile;
        [SerializeField] private TileAdjacentOffsetMode adjacentOffsetMode = TileAdjacentOffsetMode.Flush;
        [SerializeField] private Vector3 finePositionOffset;
        [SerializeField] private Vector3 fineRotationOffset;
        [SerializeField, Min(0)] private int surfaceShellIndex;

        public string StableId
        {
            get
            {
                if (string.IsNullOrWhiteSpace(stableId))
                {
                    stableId = Guid.NewGuid().ToString("N");
                }

                return stableId;
            }
        }

        public int MatchId
        {
            get => matchId;
            set => matchId = value;
        }

        public Vector3 LocalPosition
        {
            get => localPosition;
            set => localPosition = value;
        }

        public TileSurfacePose Pose => pose ?? (pose = new TileSurfacePose());

        public bool UseSnapOffset
        {
            get => useSnapOffset;
            set => useSnapOffset = value;
        }

        public string SnapSourceStableId => snapSourceStableId;

        public void SetSnapSource(TileAuthoringEntry source)
        {
            snapSourceStableId = source != null ? source.StableId : string.Empty;
        }

        public VoxelGridDirection SnapDirection
        {
            get => snapDirection;
            set => snapDirection = value;
        }

        public TileAdjacentDirectionSpace AdjacentDirectionSpace
        {
            get => adjacentDirectionSpace;
            set => adjacentDirectionSpace = value;
        }

        public int SnapOffsetU
        {
            get => snapOffsetU;
            set => snapOffsetU = value;
        }

        public int SnapOffsetV
        {
            get => snapOffsetV;
            set => snapOffsetV = value;
        }

        public TileSnapOffsetSizeSource SnapOffsetSizeSource
        {
            get => snapOffsetSizeSource;
            set => snapOffsetSizeSource = value;
        }

        public TileAdjacentOffsetMode AdjacentOffsetMode
        {
            get => adjacentOffsetMode;
            set => adjacentOffsetMode = value;
        }

        public Vector3 FinePositionOffset
        {
            get => finePositionOffset;
            set => finePositionOffset = value;
        }

        public Vector3 FineRotationOffset
        {
            get => fineRotationOffset;
            set => fineRotationOffset = value;
        }

        public int SurfaceShellIndex
        {
            get => Mathf.Max(0, surfaceShellIndex);
            set => surfaceShellIndex = Mathf.Max(0, value);
        }

        public Vector3 ResolvedPosition => LocalPosition + FinePositionOffset;

        public Vector3 ResolvedEulerAngles =>
            (TileSnapMath.GetRotation(Pose.Face, Pose.RollQuarterTurns) *
             Quaternion.Euler(FineRotationOffset)).eulerAngles;

        public void SetStableId(string value)
        {
            stableId = string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value;
        }

        public void SetSnapOffset(VoxelGridDirection direction, int offsetU, int offsetV)
        {
            useSnapOffset = true;
            snapDirection = direction;
            snapOffsetU = offsetU;
            snapOffsetV = offsetV;
        }

        public void SetSnapOffset(TileAuthoringEntry source, VoxelGridDirection direction, int offsetU, int offsetV)
        {
            SetSnapSource(source);
            SetSnapOffset(direction, offsetU, offsetV);
        }

        public static TileAuthoringEntry Create(int matchId, Vector3 position, VoxelGridDirection face, int rollQuarterTurns)
        {
            TileAuthoringEntry entry = new TileAuthoringEntry
            {
                matchId = matchId,
                localPosition = position,
                pose = new TileSurfacePose(),
            };
            entry.Pose.Face = face;
            entry.Pose.RollQuarterTurns = rollQuarterTurns;
            return entry;
        }
    }

    /// <summary>
    /// Editable asset containing manually authored tile placements.
    /// </summary>
    [CreateAssetMenu(menuName = "Mahjong Out 3D/Level/Manual Tile Layout", fileName = "ManualTileLayout")]
    public sealed class TileLayoutAuthoring : ScriptableObject
    {
        [SerializeField] private string layoutName = "Manual Layout";
        [SerializeField] private VoxelGridSize gridSize = new VoxelGridSize(8, 8, 8);
        [SerializeField] private VoxelGridLayoutSettings layoutOverride;
        [SerializeField] private MahjongTile tilePrefab;
        [SerializeField] private TileLayoutSnapMode snapMode = TileLayoutSnapMode.Quarter;
        [SerializeField] private TileDefaultPlacementPose defaultPlacementPose = TileDefaultPlacementPose.Standing;
        [SerializeField] private TilePlacementPosture defaultPosture = TilePlacementPosture.Vertical;
        [SerializeField] private VoxelGridDirection defaultStandingFace = VoxelGridDirection.Back;
        [SerializeField, Range(0, 3)] private int defaultStandingRoll = 3;
        [SerializeField, Min(0f)] private float snapDistance = 0.18f;
        [SerializeField, Min(0f)] private float tileGap = 0.03f;
        [SerializeField] private List<TileAuthoringEntry> entries = new List<TileAuthoringEntry>();

        public string LayoutName
        {
            get => string.IsNullOrWhiteSpace(layoutName) ? name : layoutName;
            set => layoutName = value;
        }

        public VoxelGridSize GridSize => gridSize;
        public VoxelGridLayoutSettings LayoutOverride => layoutOverride;
        public MahjongTile TilePrefab => tilePrefab;
        public TileLayoutSnapMode SnapMode => snapMode;
        public TileDefaultPlacementPose DefaultPlacementPose => defaultPlacementPose;
        public TilePlacementPosture DefaultPosture => defaultPosture;
        public VoxelGridDirection DefaultStandingFace => defaultStandingFace;
        public int DefaultStandingRoll => ((defaultStandingRoll % 4) + 4) % 4;
        public float SnapDistance => Mathf.Max(0f, snapDistance);
        public float TileGap => Mathf.Max(0f, tileGap);
        public List<TileAuthoringEntry> Entries => entries ?? (entries = new List<TileAuthoringEntry>());

        public int SnapDivisions => snapMode == TileLayoutSnapMode.Half
            ? 2
            : snapMode == TileLayoutSnapMode.Quarter ? 4 : 1;

        /// <summary>
        /// Gets the stable authoring denominator used by stored tangential offsets.
        /// Offset values are always quarter units, even when the grid snap is Half.
        /// </summary>
        public int SnapOffsetDivisions => 4;

        public void SetSnapMode(TileLayoutSnapMode value)
        {
            snapMode = value;
        }

        public void SetDefaultPlacementPose(TileDefaultPlacementPose value)
        {
            defaultPlacementPose = value;
        }

        public void SetDefaultPosture(TilePlacementPosture value)
        {
            defaultPosture = value;
        }

        public void SetDefaultStandingFace(VoxelGridDirection value)
        {
            defaultStandingFace = value;
        }

        public void SetDefaultStandingRoll(int value)
        {
            defaultStandingRoll = ((value % 4) + 4) % 4;
        }

        public TileSurfacePose CreateDefaultPose()
        {
            TileSurfacePose result = new TileSurfacePose();
            if (defaultPosture == TilePlacementPosture.Horizontal)
            {
                result.Face = VoxelGridDirection.Up;
                result.RollQuarterTurns = 0;
                return result;
            }

            switch (defaultPlacementPose)
            {
                case TileDefaultPlacementPose.Standing:
                    result.Face = defaultStandingFace == VoxelGridDirection.Up || defaultStandingFace == VoxelGridDirection.Down
                        ? VoxelGridDirection.Forward
                        : defaultStandingFace;
                    result.RollQuarterTurns = defaultStandingRoll;
                    break;

                case TileDefaultPlacementPose.Sideways:
                    result.Face = defaultStandingFace == VoxelGridDirection.Up || defaultStandingFace == VoxelGridDirection.Down
                        ? VoxelGridDirection.Forward
                        : defaultStandingFace;
                    result.RollQuarterTurns = defaultStandingRoll + 1;
                    break;

                case TileDefaultPlacementPose.Custom:
                    result.Face = defaultStandingFace;
                    result.RollQuarterTurns = defaultStandingRoll;
                    break;

                case TileDefaultPlacementPose.Flat:
                default:
                    result.Face = VoxelGridDirection.Up;
                    result.RollQuarterTurns = 0;
                    break;
            }

            return result;
        }

        public void SetSnapDistance(float value)
        {
            snapDistance = Mathf.Max(0f, value);
        }

        public void SetTileGap(float value)
        {
            tileGap = Mathf.Max(0f, value);
        }

        public void SetTilePrefab(MahjongTile value)
        {
            tilePrefab = value;
        }

        public void AddEntry(TileAuthoringEntry entry)
        {
            if (entry != null)
            {
                Entries.Add(entry);
            }
        }

        public void RemoveEntryAt(int index)
        {
            if (index >= 0 && index < Entries.Count)
            {
                Entries.RemoveAt(index);
            }
        }

        private void OnValidate()
        {
            if (entries == null)
            {
                entries = new List<TileAuthoringEntry>();
            }

            snapDistance = Mathf.Max(0f, snapDistance);
            tileGap = Mathf.Max(0f, tileGap);
        }
    }
}
