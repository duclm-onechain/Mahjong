using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using MahjongOut3D.LevelSystem;

namespace MahjongOut3D.Editor
{
    /// <summary>
    /// Bakes a manual tile layout into a reusable fixed shape asset.
    /// </summary>
    public static class TileShapeBaker
    {
        private const string ShapeOutputFolder = "Assets/00 Scripts/Mahjong/Shape Custom";

        public static ManualTileShape Bake(TileLayoutAuthoring layout, ManualTileShape target = null)
        {
            if (layout == null)
            {
                throw new System.ArgumentNullException(nameof(layout));
            }

            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int index = 0; index < layout.Entries.Count; index++)
            {
                TileAuthoringEntry entry = layout.Entries[index];
                if (entry != null)
                {
                    min = Vector3.Min(min, entry.ResolvedPosition);
                    max = Vector3.Max(max, entry.ResolvedPosition);
                }
            }
            Vector3 centerOffset = (min + max) * 0.5f;

            List<ManualTileShape.Cell> cells = new List<ManualTileShape.Cell>(layout.Entries.Count);
            VoxelGridSize gridSize = GetBakeGridSize(layout);
            for (int index = 0; index < layout.Entries.Count; index++)
            {
                TileAuthoringEntry entry = layout.Entries[index];
                if (entry == null)
                {
                    throw new System.InvalidOperationException($"Shape entry {index} is null.");
                }

                cells.Add(new ManualTileShape.Cell(
                    GetUniqueCoordinate(index, gridSize),
                    entry.ResolvedPosition - centerOffset,
                    entry.ResolvedEulerAngles,
                    entry.SurfaceShellIndex));
            }

            if (target == null)
            {
                target = ScriptableObject.CreateInstance<ManualTileShape>();
                EnsureOutputFolder();
                string path = AssetDatabase.GenerateUniqueAssetPath($"{ShapeOutputFolder}/{layout.LayoutName}Shape.asset");
                AssetDatabase.CreateAsset(target, path);
            }

            target.SetData(layout.LayoutName, gridSize, layout.LayoutOverride, layout.TilePrefab, cells);
            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssets();
            return target;
        }

        private static void EnsureOutputFolder()
        {
            if (AssetDatabase.IsValidFolder(ShapeOutputFolder))
            {
                return;
            }

            const string parentFolder = "Assets/00 Scripts/Mahjong";
            if (!AssetDatabase.IsValidFolder(parentFolder))
            {
                throw new System.InvalidOperationException($"Shape parent folder does not exist: {parentFolder}");
            }

            AssetDatabase.CreateFolder(parentFolder, "Shape Custom");
            AssetDatabase.Refresh();
        }

        private static VoxelGridSize GetBakeGridSize(TileLayoutAuthoring layout)
        {
            if (layout == null || layout.Entries.Count == 0)
            {
                return new VoxelGridSize(4, 4, 4);
            }

            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int index = 0; index < layout.Entries.Count; index++)
            {
                TileAuthoringEntry entry = layout.Entries[index];
                if (entry != null)
                {
                    min = Vector3.Min(min, entry.ResolvedPosition);
                    max = Vector3.Max(max, entry.ResolvedPosition);
                }
            }

            Vector3 span = max - min;
            Vector3 step = layout.LayoutOverride != null ? layout.LayoutOverride.CellStep : new Vector3(1f, 1.2f, 1f);
            Vector3 tileSize = layout.TilePrefab != null ? layout.TilePrefab.GetPlacementSize() : new Vector3(0.72f, 0.48f, 0.72f);
            if (tileSize.sqrMagnitude <= 0.001f) tileSize = new Vector3(0.72f, 0.48f, 0.72f);

            int width = Mathf.Max(2, Mathf.CeilToInt((span.x + tileSize.x) / Mathf.Max(0.1f, step.x)));
            int height = Mathf.Max(2, Mathf.CeilToInt((span.y + tileSize.y) / Mathf.Max(0.1f, step.y)));
            int depth = Mathf.Max(2, Mathf.CeilToInt((span.z + tileSize.z) / Mathf.Max(0.1f, step.z)));

            int entryCount = layout.Entries.Count;
            while (width * height * depth < entryCount)
            {
                if (width <= height && width <= depth) width++;
                else if (height <= width && height <= depth) height++;
                else depth++;
            }

            return new VoxelGridSize(width, height, depth);
        }

        private static Vector3Int GetUniqueCoordinate(int index, VoxelGridSize size)
        {
            int width = Mathf.Max(1, size.Width);
            int height = Mathf.Max(1, size.Height);
            int x = index % width;
            int y = (index / width) % height;
            int z = index / (width * height);
            return new Vector3Int(x, y, z);
        }
    }
}
