using System;
using System.Collections.Generic;
using MahjongOut3D.TileSystem;
using UnityEngine;

namespace MahjongOut3D.LevelSystem
{
    /// <summary>
    /// Stores a fixed manually-authored tile shape for procedural level generation.
    /// </summary>
    [CreateAssetMenu(menuName = "Mahjong Out 3D/Level/Manual Tile Shape", fileName = "ManualTileShape")]
    public sealed class ManualTileShape : ScriptableObject
    {
        [Serializable]
        public sealed class Cell
        {
            [SerializeField] private Vector3Int gridCoordinate;
            [SerializeField] private Vector3 localPosition;
            [SerializeField] private Vector3 localEulerAngles;
            [SerializeField, Min(0)] private int surfaceShellIndex;

            public Vector3Int GridCoordinate => gridCoordinate;
            public Vector3 LocalPosition => localPosition;
            public Vector3 LocalEulerAngles => localEulerAngles;
            public int SurfaceShellIndex => Mathf.Max(0, surfaceShellIndex);

            public Cell(Vector3Int gridCoordinate, Vector3 localPosition, Vector3 localEulerAngles, int surfaceShellIndex)
            {
                this.gridCoordinate = gridCoordinate;
                this.localPosition = localPosition;
                this.localEulerAngles = localEulerAngles;
                this.surfaceShellIndex = Mathf.Max(0, surfaceShellIndex);
            }
        }

        [SerializeField] private string shapeName = "Manual Shape";
        [SerializeField] private VoxelGridSize gridSize = new VoxelGridSize(4, 4, 4);
        [SerializeField] private VoxelGridLayoutSettings layoutOverride;
        [SerializeField] private MahjongTile tilePrefab;
        [SerializeField, Min(0)] private int layerCount;
        [SerializeField] private List<Cell> cells = new List<Cell>();

        public string ShapeName => string.IsNullOrWhiteSpace(shapeName) ? name : shapeName;
        public VoxelGridSize GridSize => gridSize;
        public VoxelGridLayoutSettings LayoutOverride => layoutOverride;
        public MahjongTile TilePrefab => tilePrefab;
        public int LayerCount => Mathf.Max(layerCount, ResolveLayerCount());
        public IReadOnlyList<Cell> Cells => cells;
        public int TileCount => cells != null ? cells.Count : 0;

        public void SetData(
            string valueName,
            VoxelGridSize valueGridSize,
            VoxelGridLayoutSettings valueLayoutOverride,
            MahjongTile valueTilePrefab,
            List<Cell> valueCells)
        {
            shapeName = string.IsNullOrWhiteSpace(valueName) ? "Manual Shape" : valueName.Trim();
            gridSize = valueGridSize;
            layoutOverride = valueLayoutOverride;
            tilePrefab = valueTilePrefab;
            cells = valueCells ?? new List<Cell>();
            layerCount = ResolveLayerCount();
        }

        public bool Validate(out string error)
        {
            if (cells == null || cells.Count == 0)
            {
                error = "Shape has no tiles.";
                return false;
            }

            if (cells.Count % 2 != 0)
            {
                error = $"Shape has {cells.Count} tiles; tile count must be even.";
                return false;
            }

            HashSet<Vector3Int> coordinates = new HashSet<Vector3Int>();
            for (int index = 0; index < cells.Count; index++)
            {
                Cell cell = cells[index];
                if (cell == null)
                {
                    error = $"Shape cell {index} is null.";
                    return false;
                }

                if (!coordinates.Add(cell.GridCoordinate))
                {
                    error = $"Shape contains duplicate coordinate {cell.GridCoordinate}.";
                    return false;
                }

                if (!TileSnapMath.IsFinite(cell.LocalPosition) || !TileSnapMath.IsFinite(cell.LocalEulerAngles))
                {
                    error = $"Shape cell {index} has a non-finite pose.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        private int ResolveLayerCount()
        {
            int maxShellIndex = -1;
            if (cells != null)
            {
                for (int index = 0; index < cells.Count; index++)
                {
                    if (cells[index] != null)
                    {
                        maxShellIndex = Mathf.Max(maxShellIndex, cells[index].SurfaceShellIndex);
                    }
                }
            }

            return maxShellIndex + 1;
        }

        private void OnValidate()
        {
            if (cells == null)
            {
                cells = new List<Cell>();
            }

            layerCount = ResolveLayerCount();
        }
    }
}
