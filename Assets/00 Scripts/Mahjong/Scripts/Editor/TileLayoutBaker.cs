using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using MahjongOut3D.LevelSystem;

namespace MahjongOut3D.Editor
{
    /// <summary>
    /// Bakes a manual authoring asset into the existing runtime LevelDefinition format.
    /// </summary>
    public static class TileLayoutBaker
    {
        private const string LevelNameProperty = "<LevelName>k__BackingField";
        private const string GridSizeProperty = "<GridSize>k__BackingField";
        private const string LayoutOverrideProperty = "<LayoutOverride>k__BackingField";
        private const string ShapeProperty = "<Shape>k__BackingField";
        private const string SurfaceProperty = "<UseSurfaceTilePlacement>k__BackingField";
        private const string LayerCountProperty = "<LayerCount>k__BackingField";
        private const string TilesProperty = "<Tiles>k__BackingField";

        public static LevelDefinition Bake(TileLayoutAuthoring layout, LevelDefinition target = null)
        {
            if (layout == null)
            {
                throw new System.ArgumentNullException(nameof(layout));
            }

            Vector3 tileSize = ResolveColliderSize(layout.TilePrefab);
            VoxelGridSize bakeGridSize = GetBakeGridSize(layout.GridSize, layout.Entries.Count);
            List<TileLayoutValidationMessage> messages = TileLayoutValidator.Validate(layout, tileSize);
            for (int index = 0; index < messages.Count; index++)
            {
                if (messages[index].Severity == TileLayoutValidationSeverity.Error)
                {
                    throw new System.InvalidOperationException(messages[index].Message);
                }
            }

            if (target == null)
            {
                target = ScriptableObject.CreateInstance<LevelDefinition>();
                string path = AssetDatabase.GenerateUniqueAssetPath($"Assets/{layout.LayoutName}.asset");
                AssetDatabase.CreateAsset(target, path);
            }

            SerializedObject serializedObject = new SerializedObject(target);
            serializedObject.FindProperty(LevelNameProperty).stringValue = layout.LayoutName;
            SerializedProperty gridSize = serializedObject.FindProperty(GridSizeProperty);
            gridSize.FindPropertyRelative("width").intValue = bakeGridSize.Width;
            gridSize.FindPropertyRelative("height").intValue = bakeGridSize.Height;
            gridSize.FindPropertyRelative("depth").intValue = bakeGridSize.Depth;
            serializedObject.FindProperty(LayoutOverrideProperty).objectReferenceValue = layout.LayoutOverride;
            serializedObject.FindProperty(ShapeProperty).intValue = (int)LevelShapeType.Custom;
            serializedObject.FindProperty(SurfaceProperty).boolValue = true;
            serializedObject.FindProperty(LayerCountProperty).intValue = GetLayerCount(layout);

            SerializedProperty tiles = serializedObject.FindProperty(TilesProperty);
            tiles.arraySize = layout.Entries.Count;
            for (int index = 0; index < layout.Entries.Count; index++)
            {
                TileAuthoringEntry source = layout.Entries[index];
                SerializedProperty tile = tiles.GetArrayElementAtIndex(index);
                tile.FindPropertyRelative("matchId").intValue = source.MatchId;
                tile.FindPropertyRelative("gridCoordinate").vector3IntValue = GetUniqueCoordinate(index, bakeGridSize);
                tile.FindPropertyRelative("useCustomLocalPosition").boolValue = true;
                tile.FindPropertyRelative("localPosition").vector3Value = source.ResolvedPosition;
                tile.FindPropertyRelative("localEulerAngles").vector3Value = source.ResolvedEulerAngles;
                tile.FindPropertyRelative("surfaceShellIndex").intValue = source.SurfaceShellIndex;
            }

            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssets();
            return target;
        }

        private static Vector3 ResolveColliderSize(MahjongOut3D.TileSystem.MahjongTile prefab)
        {
            if (prefab == null)
            {
                return Vector3.one;
            }

            Collider collider = prefab.TileCollider != null ? prefab.TileCollider : prefab.GetComponentInChildren<Collider>(true);
            if (collider == null)
            {
                return prefab.GetPlacementSize();
            }

            if (collider is BoxCollider boxCollider)
            {
                Vector3 scale = boxCollider.transform.lossyScale;
                return Vector3.Scale(boxCollider.size, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            }

            return collider.bounds.size;
        }

        private static VoxelGridSize GetBakeGridSize(VoxelGridSize requested, int entryCount)
        {
            int width = requested.Width;
            int height = requested.Height;
            int depth = requested.Depth;
            while (width * height * depth < Mathf.Max(1, entryCount))
            {
                depth++;
            }

            return new VoxelGridSize(width, height, depth);
        }

        private static int GetLayerCount(TileLayoutAuthoring layout)
        {
            int maxLayer = -1;
            for (int index = 0; index < layout.Entries.Count; index++)
            {
                if (layout.Entries[index] != null)
                {
                    maxLayer = Mathf.Max(maxLayer, layout.Entries[index].SurfaceShellIndex);
                }
            }

            return maxLayer + 1;
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
