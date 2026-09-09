using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using MahjongOut3D.LevelSystem;
using MahjongOut3D.TileSystem;

namespace MahjongOut3D.Editor
{
    /// <summary>
    /// Provides a compact SceneView-driven editor for manually authored Mahjong tile layouts.
    /// </summary>
    public sealed class TileLayoutAuthoringWindow : EditorWindow
    {
        private TileLayoutAuthoring layout;
        private MahjongTile tilePrefab;
        private int selectedEntry = -1;
        private bool placing;
        private Vector3 tileSize = Vector3.one;
        private GameObject previewRoot;

        [MenuItem("Tools/Mahjong Out 3D/Levels/Manual Tile Layout Editor")]
        public static void Open()
        {
            TileLayoutAuthoringWindow window = GetWindow<TileLayoutAuthoringWindow>("Manual Tile Layout");
            window.minSize = new Vector2(330f, 260f);
            window.Show();
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            Selection.selectionChanged += Repaint;
            EnsurePreviewRoot();
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Selection.selectionChanged -= Repaint;
            DestroyPreviewRoot();
        }

        private void EnsurePreviewRoot()
        {
            if (previewRoot != null)
            {
                return;
            }

            previewRoot = new GameObject("ManualTileLayoutPreview");
            previewRoot.hideFlags = HideFlags.HideAndDontSave;
            previewRoot.transform.hideFlags = HideFlags.HideAndDontSave;
        }

        private void DestroyPreviewRoot()
        {
            if (previewRoot != null)
            {
                DestroyImmediate(previewRoot);
                previewRoot = null;
            }

        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Manual Mahjong Layout", EditorStyles.boldLabel);
            layout = (TileLayoutAuthoring)EditorGUILayout.ObjectField("Authoring Asset", layout, typeof(TileLayoutAuthoring), false);
            if (layout == null)
            {
                EditorGUILayout.HelpBox("Create a Manual Tile Layout asset, then assign it here.", MessageType.Info);
                if (GUILayout.Button("Create Layout Asset"))
                {
                    CreateLayoutAsset();
                }
                return;
            }

            MahjongTile assignedPrefab = layout.TilePrefab != null ? layout.TilePrefab : tilePrefab;
            tilePrefab = (MahjongTile)EditorGUILayout.ObjectField("Tile Prefab (asset)", assignedPrefab, typeof(MahjongTile), false);
            if (tilePrefab != layout.TilePrefab)
            {
                Undo.RecordObject(layout, "Assign Mahjong Tile Prefab");
                layout.SetTilePrefab(tilePrefab);
                EditorUtility.SetDirty(layout);
            }

            tileSize = ResolveColliderSize(tilePrefab);
            if (tilePrefab != null)
            {
                TileDirectionFrame directionFrame = tilePrefab.DirectionFrame;
                string directionError = directionFrame == null
                    ? "TileDirectionFrame is missing."
                    : string.Empty;
                if (directionFrame == null || !directionFrame.Validate(out directionError))
                {
                    EditorGUILayout.HelpBox(
                        $"Tile Prefab directions are not configured: {directionError}",
                        MessageType.Warning);
                }
            }
            EditorGUILayout.LabelField("Tiles", layout.Entries.Count.ToString());
            EditorGUILayout.LabelField("Snap", layout.SnapMode.ToString());
            EditorGUI.BeginChangeCheck();
            TileLayoutSnapMode snapMode = (TileLayoutSnapMode)EditorGUILayout.EnumPopup("Snap Step", layout.SnapMode);
            TileDefaultPlacementPose defaultPose = (TileDefaultPlacementPose)EditorGUILayout.EnumPopup("Default Tile Pose", layout.DefaultPlacementPose);
            TilePlacementPosture defaultPosture = (TilePlacementPosture)EditorGUILayout.EnumPopup("Default Posture", layout.DefaultPosture);
            EditorGUILayout.HelpBox("Default: Standing + Vertical + Back. Adjacent Left/Right/Up/Down follow the visible surface of the selected tile.", MessageType.None);
            VoxelGridDirection defaultStandingFace = layout.DefaultStandingFace;
            int defaultStandingRoll = layout.DefaultStandingRoll;
            if (defaultPose == TileDefaultPlacementPose.Standing || defaultPose == TileDefaultPlacementPose.Sideways || defaultPose == TileDefaultPlacementPose.Custom)
            {
                defaultStandingFace = (VoxelGridDirection)EditorGUILayout.EnumPopup("Default Standing Face", defaultStandingFace);
                defaultStandingRoll = EditorGUILayout.IntSlider("Default Standing Roll", defaultStandingRoll, 0, 3);
            }
            float snapDistance = EditorGUILayout.FloatField("Magnetic Distance", layout.SnapDistance);
            float tileGap = EditorGUILayout.FloatField("Tile Gap", layout.TileGap);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(layout, "Change Tile Snap Settings");
                layout.SetSnapMode(snapMode);
                layout.SetDefaultPlacementPose(defaultPose);
                layout.SetDefaultPosture(defaultPosture);
                layout.SetDefaultStandingFace(defaultStandingFace);
                layout.SetDefaultStandingRoll(defaultStandingRoll);
                layout.SetSnapDistance(snapDistance);
                layout.SetTileGap(tileGap);
                EditorUtility.SetDirty(layout);
                SceneView.RepaintAll();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                placing = GUILayout.Toggle(placing, placing ? "Placing..." : "Place Tile", "Button");
                if (GUILayout.Button("Duplicate Selected"))
                {
                    DuplicateSelected();
                }
                if (GUILayout.Button("Delete Selected"))
                {
                    DeleteSelected();
                }
            }

            if (selectedEntry >= 0 && selectedEntry < layout.Entries.Count)
            {
                EditorGUILayout.LabelField("Create Adjacent Tile", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Left")) CreateAdjacentTile(VoxelGridDirection.Left);
                    if (GUILayout.Button("Right")) CreateAdjacentTile(VoxelGridDirection.Right);
                    if (GUILayout.Button("Down")) CreateAdjacentTile(VoxelGridDirection.Down);
                    if (GUILayout.Button("Up")) CreateAdjacentTile(VoxelGridDirection.Up);
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Back")) CreateAdjacentTile(VoxelGridDirection.Back);
                    if (GUILayout.Button("Forward")) CreateAdjacentTile(VoxelGridDirection.Forward);
                }
            }

            if (GUILayout.Button("Validate"))
            {
                ShowValidation();
            }

            if (GUILayout.Button("Bake To Shape"))
            {
                BakeShape();
            }

            if (GUILayout.Button("Bake Complete Level (Legacy)"))
            {
                BakeLayout();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Selected Tile", EditorStyles.boldLabel);
            if (selectedEntry < 0 || selectedEntry >= layout.Entries.Count || layout.Entries[selectedEntry] == null)
            {
                EditorGUILayout.HelpBox("Select a tile in the Scene view.", MessageType.Info);
                return;
            }

            TileAuthoringEntry entry = layout.Entries[selectedEntry];
            VoxelGridDirection previousFace = entry.Pose.Face;
            int previousRoll = entry.Pose.RollQuarterTurns;
            Vector3 previousFineRotation = entry.FineRotationOffset;
            EditorGUI.BeginChangeCheck();
            entry.MatchId = EditorGUILayout.IntField("Match ID", entry.MatchId);
            entry.LocalPosition = EditorGUILayout.Vector3Field("Position", entry.LocalPosition);
            entry.Pose.Face = (VoxelGridDirection)EditorGUILayout.EnumPopup("Face", entry.Pose.Face);
            entry.Pose.RollQuarterTurns = EditorGUILayout.IntSlider("Roll", entry.Pose.RollQuarterTurns, 0, 3);
            entry.UseSnapOffset = EditorGUILayout.Toggle("Use Adjacent Snap", entry.UseSnapOffset);
            if (entry.UseSnapOffset)
            {
                entry.SnapDirection = (VoxelGridDirection)EditorGUILayout.EnumPopup("Adjacent Side", entry.SnapDirection);
                entry.AdjacentDirectionSpace = (TileAdjacentDirectionSpace)EditorGUILayout.EnumPopup("Direction Space", entry.AdjacentDirectionSpace);
                entry.AdjacentOffsetMode = (TileAdjacentOffsetMode)EditorGUILayout.EnumPopup("Overlap", entry.AdjacentOffsetMode);
                entry.SnapOffsetSizeSource = (TileSnapOffsetSizeSource)EditorGUILayout.EnumPopup("Offset Size From", entry.SnapOffsetSizeSource);
                int divisions = layout.SnapOffsetDivisions;
                EditorGUILayout.LabelField("Tangential Offset", layout.SnapMode == TileLayoutSnapMode.Half
                    ? "Each step is 1/2 of the real tile footprint"
                    : layout.SnapMode == TileLayoutSnapMode.Quarter
                        ? "Each step is 1/4 of the real tile footprint"
                        : "Adjacent only; offsets use one full tile footprint");
                entry.SnapOffsetU = EditorGUILayout.IntSlider("Snap Left / Right", entry.SnapOffsetU, -divisions, divisions);
                entry.SnapOffsetV = EditorGUILayout.IntSlider("Snap Down / Up", entry.SnapOffsetV, -divisions, divisions);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Center")) SetSnapOffset(entry, 0, 0);
                    if (GUILayout.Button("Left")) SetDirectionalFraction(entry, -1, 0);
                    if (GUILayout.Button("Right")) SetDirectionalFraction(entry, 1, 0);
                    if (GUILayout.Button("Down")) SetDirectionalFraction(entry, 0, -1);
                    if (GUILayout.Button("Up")) SetDirectionalFraction(entry, 0, 1);
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Left + Down")) SetDirectionalFraction(entry, -1, -1);
                    if (GUILayout.Button("Left + Up")) SetDirectionalFraction(entry, -1, 1);
                    if (GUILayout.Button("Right + Down")) SetDirectionalFraction(entry, 1, -1);
                    if (GUILayout.Button("Right + Up")) SetDirectionalFraction(entry, 1, 1);
                }
                if (GUILayout.Button("Apply Adjacent Snap"))
                {
                    ApplyAdjacentSnap(entry);
                }
            }
            entry.FinePositionOffset = EditorGUILayout.Vector3Field("Fine Position", entry.FinePositionOffset);
            entry.FineRotationOffset = EditorGUILayout.Vector3Field("Fine Rotation", entry.FineRotationOffset);
            entry.SurfaceShellIndex = EditorGUILayout.IntField("Shell", entry.SurfaceShellIndex);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(layout, "Edit Mahjong Tile Placement");
                bool poseChanged = previousFace != entry.Pose.Face
                    || previousRoll != entry.Pose.RollQuarterTurns
                    || previousFineRotation != entry.FineRotationOffset;
                if (poseChanged)
                {
                    RepositionAfterPoseChange(entry);
                }

                EditorUtility.SetDirty(layout);
                SceneView.RepaintAll();
            }
        }

        private void RepositionAfterPoseChange(TileAuthoringEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            TileAuthoringEntry snapSource = FindSnapSource(entry);
            if (entry.UseSnapOffset && snapSource != null)
            {
                ApplyAdjacentSnap(entry, snapSource, false);
                return;
            }

            Quaternion rotation = Quaternion.Euler(entry.ResolvedEulerAngles);
            Vector3 colliderCenter = entry.ResolvedPosition;
            float halfHeight = TileSnapMath.GetOrientedExtent(tileSize, rotation, Vector3.up);
            float supportY = colliderCenter.y - halfHeight;
            entry.LocalPosition += Vector3.up * -supportY;
        }

        private void SetDirectionalFraction(TileAuthoringEntry entry, int directionU, int directionV)
        {
            int amount = layout.SnapMode == TileLayoutSnapMode.Half ? 2 : 1;
            SetSnapOffset(entry, directionU * amount, directionV * amount);
        }

        private void SetSnapOffset(TileAuthoringEntry entry, int offsetU, int offsetV)
        {
            Undo.RecordObject(layout, "Set Tile Snap Offset");
            TileAuthoringEntry source = FindSnapSource(entry);
            entry.SetSnapOffset(source, entry.SnapDirection, offsetU, offsetV);
            ApplyAdjacentSnap(entry, source);
        }

        private void CreateAdjacentTile(VoxelGridDirection direction)
        {
            if (!TryGetSelected(out TileAuthoringEntry source))
            {
                return;
            }

            Undo.RecordObject(layout, "Create Adjacent Mahjong Tile");
            TileAuthoringEntry created = TileAuthoringEntry.Create(
                source.MatchId,
                source.ResolvedPosition,
                source.Pose.Face,
                source.Pose.RollQuarterTurns);
            // Adjacent creation inherits the complete pose of the source tile.
            created.FineRotationOffset = source.FineRotationOffset;
            created.AdjacentDirectionSpace = TileAdjacentDirectionSpace.TilePrefab;
            created.SetSnapOffset(source, direction, 0, 0);
            created.SnapOffsetSizeSource = TileSnapOffsetSizeSource.SourceTile;
            layout.AddEntry(created);
            selectedEntry = layout.Entries.Count - 1;
            ApplyAdjacentSnap(created, source);
            EditorUtility.SetDirty(layout);
            SceneView.RepaintAll();
        }

        private void ApplyAdjacentSnap(TileAuthoringEntry entry)
        {
            ApplyAdjacentSnap(entry, FindSnapSource(entry));
        }

        private void ApplyAdjacentSnap(TileAuthoringEntry entry, TileAuthoringEntry source)
        {
            ApplyAdjacentSnap(entry, source, true);
        }

        private void ApplyAdjacentSnap(TileAuthoringEntry entry, TileAuthoringEntry source, bool inheritSourcePose)
        {
            if (source == null)
            {
                return;
            }

            Undo.RecordObject(layout, "Apply Adjacent Tile Snap");
            Vector3 sourceRotation = source.ResolvedEulerAngles;
            // New adjacent tiles inherit the source pose. When an existing tile's face
            // is edited, preserve its new pose and only recompute its snapped position.
            if (inheritSourcePose)
            {
                entry.Pose.Face = source.Pose.Face;
                entry.Pose.RollQuarterTurns = source.Pose.RollQuarterTurns;
                entry.FineRotationOffset = source.FineRotationOffset;
            }
            Vector3 targetRotation = entry.ResolvedEulerAngles;
            Quaternion sourceQuaternion = Quaternion.Euler(sourceRotation);
            Quaternion targetQuaternion = Quaternion.Euler(targetRotation);
            Quaternion semanticRotation = TileSnapMath.GetRotation(source.Pose.Face, source.Pose.RollQuarterTurns);
            TileDirectionFrame prefabDirectionFrame = tilePrefab != null ? tilePrefab.DirectionFrame : null;
            Vector3 tangentU;
            Vector3 tangentV;
            Vector3 direction = entry.AdjacentDirectionSpace == TileAdjacentDirectionSpace.TilePrefab
                ? TileSnapMath.GetPrefabWorldDirection(prefabDirectionFrame, sourceQuaternion, entry.SnapDirection)
                : TileSnapMath.GetAdjacentWorldDirection(
                    source.Pose.Face,
                    source.Pose.RollQuarterTurns,
                    entry.SnapDirection,
                    entry.AdjacentDirectionSpace);
            if (entry.AdjacentDirectionSpace == TileAdjacentDirectionSpace.TilePrefab
                && prefabDirectionFrame != null)
            {
                GetPrefabTangentialBasis(
                    prefabDirectionFrame,
                    sourceQuaternion,
                    entry.SnapDirection,
                    out tangentU,
                    out tangentV);
            }
            else
            {
                TileSnapMath.GetAdjacentTangentialBasis(
                    semanticRotation,
                    entry.SnapDirection,
                    entry.AdjacentDirectionSpace,
                    out tangentU,
                    out tangentV);
            }
            Quaternion sizeSourceRotation = entry.SnapOffsetSizeSource == TileSnapOffsetSizeSource.SourceTile
                ? sourceQuaternion
                : targetQuaternion;
            Vector3 offset = GetBoardCornerOffset(
                sizeSourceRotation,
                tangentU,
                tangentV,
                entry.SnapOffsetU,
                entry.SnapOffsetV);
            Vector3 sourcePosition = source.ResolvedPosition;
            Vector3 sourceRootPosition = sourcePosition - (sourceQuaternion * tilePrefab.GetPlacementOffset());
            Vector3 adjacentRootPosition = TileSnapMath.GetAdjacentPosition(
                sourceRootPosition,
                sourceQuaternion,
                tileSize,
                tileSize,
                targetQuaternion,
                direction,
                layout.TileGap,
                Vector2.zero);
            Vector3 adjacent = adjacentRootPosition + (targetQuaternion * tilePrefab.GetPlacementOffset());
            adjacent += GetOverlapOffset(entry, source, direction, tangentU, tangentV);
            entry.SnapOffsetU = Mathf.Clamp(entry.SnapOffsetU, -4, 4);
            entry.SnapOffsetV = Mathf.Clamp(entry.SnapOffsetV, -4, 4);
            entry.FinePositionOffset = adjacent + offset - entry.LocalPosition;
            entry.LocalPosition = entry.ResolvedPosition;
            entry.FinePositionOffset = Vector3.zero;
            EditorUtility.SetDirty(layout);
            SceneView.RepaintAll();
        }

        private static void GetPrefabTangentialBasis(
            TileDirectionFrame directionFrame,
            Quaternion sourceRotation,
            VoxelGridDirection direction,
            out Vector3 tangentU,
            out Vector3 tangentV)
        {
            bool hasRight = directionFrame.TryGetLocalDirection(VoxelGridDirection.Right, out Vector3 right);
            bool hasUp = directionFrame.TryGetLocalDirection(VoxelGridDirection.Up, out Vector3 up);
            bool hasForward = directionFrame.TryGetLocalDirection(VoxelGridDirection.Forward, out Vector3 forward);
            if (!hasRight || !hasUp || !hasForward)
            {
                directionFrame.TryGetLocalDirection(direction, out Vector3 localDirection);
                TileSnapMath.GetLocalTangentialBasis(localDirection, out Vector3 localU, out Vector3 localV);
                tangentU = (sourceRotation * localU).normalized;
                tangentV = (sourceRotation * localV).normalized;
                return;
            }

            switch (direction)
            {
                case VoxelGridDirection.Left:
                case VoxelGridDirection.Right:
                    tangentU = (sourceRotation * forward).normalized;
                    tangentV = (sourceRotation * up).normalized;
                    break;
                case VoxelGridDirection.Down:
                case VoxelGridDirection.Up:
                    tangentU = (sourceRotation * right).normalized;
                    tangentV = (sourceRotation * forward).normalized;
                    break;
                case VoxelGridDirection.Back:
                case VoxelGridDirection.Forward:
                default:
                    tangentU = (sourceRotation * right).normalized;
                    tangentV = (sourceRotation * up).normalized;
                    break;
            }
        }

        private Vector3 GetBoardCornerOffset(
            Quaternion sizeSourceRotation,
            Vector3 tangentU,
            Vector3 tangentV,
            int offsetU,
            int offsetV)
        {
            float fractionU = Mathf.Clamp(offsetU, -4, 4) / 4f;
            float fractionV = Mathf.Clamp(offsetV, -4, 4) / 4f;
            float width = TileSnapMath.GetOrientedExtent(tileSize, sizeSourceRotation, tangentU) * 2f;
            float height = TileSnapMath.GetOrientedExtent(tileSize, sizeSourceRotation, tangentV) * 2f;
            return tangentU.normalized * (width * fractionU)
                + tangentV.normalized * (height * fractionV);
        }

        private Vector3 GetOverlapOffset(TileAuthoringEntry entry, TileAuthoringEntry source, Vector3 direction, Vector3 tangentU, Vector3 tangentV)
        {
            TileAdjacentOffsetMode mode = entry.AdjacentOffsetMode;
            if (mode == TileAdjacentOffsetMode.Flush)
            {
                return Vector3.zero;
            }

            float overlapFraction = mode == TileAdjacentOffsetMode.Half ? 0.5f : 0.25f;
            Quaternion sourceRotation = Quaternion.Euler(source.ResolvedEulerAngles);
            Quaternion targetRotation = Quaternion.Euler(entry.ResolvedEulerAngles);
            float sourceTangentWidth = TileSnapMath.GetOrientedExtent(tileSize, sourceRotation, direction) * 2f;
            float targetTangentWidth = TileSnapMath.GetOrientedExtent(tileSize, targetRotation, direction) * 2f;
            float overlap = Mathf.Min(sourceTangentWidth, targetTangentWidth) * overlapFraction;
            // Overlap is only along the selected adjacent axis. The U/V offset is
            // applied separately so Forward + Left + Down becomes a real corner.
            return -direction.normalized * overlap;
        }

        private void OnSceneGUI(SceneView sceneView)
        {
            if (layout == null)
            {
                return;
            }

            Event current = Event.current;
            RefreshPreviewTiles();
            DrawEntries();
            if (placing && current.type == EventType.MouseDown && current.button == 0 && !current.alt)
            {
                Ray ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
                Plane plane = new Plane(Vector3.up, Vector3.zero);
                if (plane.Raycast(ray, out float distance))
                {
                    Vector3 position = ray.GetPoint(distance);
                    Vector3 step = layout.LayoutOverride != null ? layout.LayoutOverride.CellStep : Vector3.one;
                    position = TileSnapMath.Quantize(position, step, layout.SnapMode);
                    if (layout.Entries.Count > 0)
                    {
                        TileAuthoringEntry nearest = FindNearestEntry(position, null);
                        if (nearest != null)
                        {
                            TileSnapMath.TryFindMagneticPosition(
                                position,
                                tileSize,
                                TileSnapMath.GetRotation(VoxelGridDirection.Up, 0),
                                nearest.ResolvedPosition,
                                tileSize,
                                TileSnapMath.GetRotation(nearest.Pose.Face, nearest.Pose.RollQuarterTurns),
                                layout.SnapDistance,
                                layout.TileGap,
                                out position);
                        }
                    }
                    Undo.RecordObject(layout, "Place Mahjong Tile");
                    TileSurfacePose defaultTilePose = layout.CreateDefaultPose();
                    TileAuthoringEntry entry = TileAuthoringEntry.Create(
                        GetNextMatchId(),
                        position,
                        defaultTilePose.Face,
                        defaultTilePose.RollQuarterTurns);
                    layout.AddEntry(entry);
                    selectedEntry = layout.Entries.Count - 1;
                    EditorUtility.SetDirty(layout);
                    placing = false;
                    current.Use();
                    Repaint();
                }
            }
        }

        private static Vector3 ResolveColliderSize(MahjongTile prefab)
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

        private void RefreshPreviewTiles()
        {
            EnsurePreviewRoot();
            int desiredCount = layout != null ? layout.Entries.Count : 0;
            while (previewRoot.transform.childCount > desiredCount)
            {
                DestroyImmediate(previewRoot.transform.GetChild(previewRoot.transform.childCount - 1).gameObject);
            }

            if (tilePrefab == null)
            {
                for (int index = 0; index < previewRoot.transform.childCount; index++)
                {
                    previewRoot.transform.GetChild(index).gameObject.SetActive(false);
                }
                return;
            }

            for (int index = 0; index < desiredCount; index++)
            {
                TileAuthoringEntry entry = layout.Entries[index];
                if (entry == null)
                {
                    if (index < previewRoot.transform.childCount)
                    {
                        previewRoot.transform.GetChild(index).gameObject.SetActive(false);
                    }
                    continue;
                }

                MahjongTile preview = index < previewRoot.transform.childCount
                    ? previewRoot.transform.GetChild(index).GetComponent<MahjongTile>()
                    : null;
                if (preview == null)
                {
                    preview = (MahjongTile)PrefabUtility.InstantiatePrefab(tilePrefab, previewRoot.transform);
                    preview.gameObject.hideFlags = HideFlags.HideAndDontSave;
                    preview.name = $"PreviewTile_{index}";
                }

                Quaternion previewRotation = Quaternion.Euler(entry.ResolvedEulerAngles);
                Vector3 placementOffset = tilePrefab.GetPlacementOffset();
                Vector3 previewRootPosition = entry.ResolvedPosition - (previewRotation * placementOffset);
                preview.transform.SetLocalPositionAndRotation(previewRootPosition, previewRotation);
                preview.gameObject.SetActive(true);
                DrawDirectionMarkers(preview);
            }
        }

        private void DrawDirectionMarkers(MahjongTile preview)
        {
            TileDirectionFrame directionFrame = preview != null ? preview.DirectionFrame : null;
            if (directionFrame == null)
            {
                return;
            }

            Vector3 markerCenter = preview.transform.TransformPoint(tilePrefab.GetPlacementOffset());
            VoxelGridDirection[] directions = VoxelGridDirections.Cardinals;
            Color[] colors = { Color.red, Color.green, Color.yellow, Color.cyan, Color.magenta, Color.blue };
            float markerDistance = Mathf.Max(tileSize.x, Mathf.Max(tileSize.y, tileSize.z)) * 0.8f;
            for (int directionIndex = 0; directionIndex < directions.Length; directionIndex++)
            {
                VoxelGridDirection direction = directions[directionIndex];
                if (!directionFrame.TryGetLocalDirection(direction, out Vector3 localDirection))
                {
                    continue;
                }

                Vector3 worldDirection = preview.transform.TransformDirection(localDirection).normalized;
                Vector3 markerPosition = markerCenter + worldDirection * markerDistance;
                Handles.color = colors[directionIndex];
                float handleSize = HandleUtility.GetHandleSize(markerPosition) * 0.08f;
                Handles.DrawLine(markerCenter, markerPosition);
                Handles.SphereHandleCap(0, markerPosition, Quaternion.identity, handleSize, EventType.Repaint);
                Handles.Label(markerPosition, direction.ToString());
            }
        }

        private void DrawEntries()
        {
            for (int index = 0; index < layout.Entries.Count; index++)
            {
                TileAuthoringEntry entry = layout.Entries[index];
                if (entry == null)
                {
                    continue;
                }

                Vector3 position = entry.ResolvedPosition;
                Handles.color = index == selectedEntry ? Color.green : Color.cyan;
                float size = HandleUtility.GetHandleSize(position) * 0.12f;
                if (Handles.Button(position, Quaternion.identity, size, size, Handles.DotHandleCap))
                {
                    selectedEntry = index;
                    Repaint();
                }

                Handles.Label(position + Vector3.up * size, $"{index}: match {entry.MatchId}");
                if (index != selectedEntry)
                {
                    continue;
                }

                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.PositionHandle(position, Quaternion.Euler(entry.ResolvedEulerAngles));
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(layout, "Move Mahjong Tile");
                    entry.LocalPosition = moved - entry.FinePositionOffset;
                    EditorUtility.SetDirty(layout);
                }

                EditorGUI.BeginChangeCheck();
                Quaternion rotated = Handles.RotationHandle(Quaternion.Euler(entry.ResolvedEulerAngles), position);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(layout, "Rotate Mahjong Tile");
                    entry.FineRotationOffset = (Quaternion.Inverse(TileSnapMath.GetRotation(entry.Pose.Face, entry.Pose.RollQuarterTurns)) * rotated).eulerAngles;
                    EditorUtility.SetDirty(layout);
                }
            }
        }

        private TileAuthoringEntry FindSnapSource(TileAuthoringEntry entry)
        {
            if (entry != null && !string.IsNullOrEmpty(entry.SnapSourceStableId))
            {
                for (int index = 0; index < layout.Entries.Count; index++)
                {
                    TileAuthoringEntry candidate = layout.Entries[index];
                    if (candidate != null && candidate.StableId == entry.SnapSourceStableId)
                    {
                        return candidate;
                    }
                }
            }

            return entry == null ? null : FindNearestEntry(entry.ResolvedPosition, entry);
        }

        private TileAuthoringEntry FindNearestEntry(Vector3 position, TileAuthoringEntry ignoredEntry)
        {
            TileAuthoringEntry nearest = null;
            float nearestDistance = float.MaxValue;
            for (int index = 0; index < layout.Entries.Count; index++)
            {
                TileAuthoringEntry candidate = layout.Entries[index];
                if (candidate == null || candidate == ignoredEntry)
                {
                    continue;
                }

                float distance = (candidate.ResolvedPosition - position).sqrMagnitude;
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = candidate;
                }
            }

            return nearest;
        }

        private int GetNextMatchId()
        {
            int maxMatchId = 0;
            for (int index = 0; index < layout.Entries.Count; index++)
            {
                TileAuthoringEntry entry = layout.Entries[index];
                if (entry != null)
                {
                    maxMatchId = Mathf.Max(maxMatchId, entry.MatchId);
                }
            }

            return maxMatchId + 1;
        }

        private void DuplicateSelected()
        {
            if (!TryGetSelected(out TileAuthoringEntry source))
            {
                return;
            }

            Undo.RecordObject(layout, "Duplicate Mahjong Tile");
            TileAuthoringEntry copy = TileAuthoringEntry.Create(
                source.MatchId,
                source.ResolvedPosition,
                source.Pose.Face,
                source.Pose.RollQuarterTurns);
            copy.FinePositionOffset = source.FinePositionOffset;
            copy.FineRotationOffset = source.FineRotationOffset;
            copy.SnapOffsetSizeSource = source.SnapOffsetSizeSource;
            copy.SetSnapOffset(source, VoxelGridDirection.Right, 0, 0);
            layout.AddEntry(copy);
            selectedEntry = layout.Entries.Count - 1;
            EditorUtility.SetDirty(layout);
            SceneView.RepaintAll();
        }

        private void DeleteSelected()
        {
            if (!TryGetSelected(out _))
            {
                return;
            }

            Undo.RecordObject(layout, "Delete Mahjong Tile");
            layout.RemoveEntryAt(selectedEntry);
            selectedEntry = Mathf.Min(selectedEntry, layout.Entries.Count - 1);
            EditorUtility.SetDirty(layout);
            SceneView.RepaintAll();
        }

        private bool TryGetSelected(out TileAuthoringEntry entry)
        {
            entry = selectedEntry >= 0 && selectedEntry < layout.Entries.Count ? layout.Entries[selectedEntry] : null;
            return entry != null;
        }

        private void ShowValidation()
        {
            List<TileLayoutValidationMessage> messages = TileLayoutValidator.Validate(layout, tileSize);
            int errors = 0;
            for (int index = 0; index < messages.Count; index++)
            {
                if (messages[index].Severity == TileLayoutValidationSeverity.Error)
                {
                    errors++;
                }
            }

            string text = messages.Count == 0 ? "Layout is valid." : $"{messages.Count} message(s), {errors} error(s).\n\n{messages[0].Message}";
            EditorUtility.DisplayDialog("Manual Layout Validation", text, "OK");
        }

        private void BakeShape()
        {
            try
            {
                ManualTileShape shape = TileShapeBaker.Bake(layout);
                Selection.activeObject = shape;
                EditorUtility.DisplayDialog(
                    "Manual Tile Shape",
                    $"Shape baked successfully.\n\nTiles: {shape.TileCount}\nShells: {shape.LayerCount}",
                    "OK");
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception, layout);
                EditorUtility.DisplayDialog("Shape Bake Failed", exception.Message, "OK");
            }
        }

        private void BakeLayout()
        {
            try
            {
                LevelDefinition definition = TileLayoutBaker.Bake(layout);
                Selection.activeObject = definition;
                EditorUtility.DisplayDialog("Manual Layout", "Layout baked successfully.", "OK");
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception, layout);
                EditorUtility.DisplayDialog("Bake Failed", exception.Message, "OK");
            }
        }

        private void CreateLayoutAsset()
        {
            TileLayoutAuthoring asset = CreateInstance<TileLayoutAuthoring>();
            string path = EditorUtility.SaveFilePanelInProject("Create Manual Tile Layout", "ManualTileLayout", "asset", "Choose a location.");
            if (string.IsNullOrWhiteSpace(path))
            {
                DestroyImmediate(asset);
                return;
            }

            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            layout = asset;
            Selection.activeObject = asset;
        }
    }
}
