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

            List<ManualTileShape.Cell> cells = new List<ManualTileShape.Cell>(layout.Entries.Count);
            VoxelGridSize gridSize = GetBakeGridSize(layout.GridSize, layout.Entries.Count);
            for (int index = 0; index < layout.Entries.Count; index++)
            {
                TileAuthoringEntry entry = layout.Entries[index];
                if (entry == null)
                {
                    throw new System.InvalidOperationException($"Shape entry {index} is null.");
                }

                cells.Add(new ManualTileShape.Cell(
                    GetUniqueCoordinate(index, gridSize),
                    entry.ResolvedPosition,
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

        private static VoxelGridSize GetBakeGridSize(VoxelGridSize requested, int entryCount)
        {
            int width = Mathf.Max(1, requested.Width);
            int height = Mathf.Max(1, requested.Height);
            int depth = Mathf.Max(1, requested.Depth);
            while (width * height * depth < Mathf.Max(1, entryCount))
            {
                depth++;
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
