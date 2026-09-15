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
        private readonly HashSet<int> selectedEntries = new HashSet<int>();
        private bool placing;
        private bool marqueeMode;
        private bool isMarqueeSelecting;
        private Vector2 marqueeStart;
        private Vector2 marqueeCurrent;
        private int batchMatchId = 1;
        private int batchShell = 0;
        private VoxelGridDirection batchFace = VoxelGridDirection.Back;
        private int batchRoll = 3;
        private bool multiSelectSyncPose = true;
        private int lastSelectedEntry = -1;
        private Vector3 tileSize = Vector3.one;
        private GameObject previewRoot;
        private readonly Dictionary<MahjongTile, int> previewEntryIndices = new Dictionary<MahjongTile, int>();

        private static readonly Color GapBoxColor = new Color(1.0f, 0.84f, 0.40f);       // Warm Gold / Amber for Tile Gap & Snap
        private static readonly Color MoveBoxColor = new Color(0.40f, 0.75f, 1.0f);      // Sky Blue for Move Selected
        private static readonly Color MoveButtonColor = new Color(0.55f, 0.85f, 1.0f);   // Vibrant Blue button accent
        private static readonly Color CreateBoxColor = new Color(0.40f, 0.92f, 0.55f);   // Emerald Green for Create Adjacent
        private static readonly Color CreateButtonColor = new Color(0.55f, 0.98f, 0.65f); // Vibrant Green button accent
        private static readonly Color BatchBoxColor = new Color(0.85f, 0.68f, 1.0f);     // Purple / Lavender for Batch Edit

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
            Event currentEvent = Event.current;
            if (currentEvent.type == EventType.KeyDown && layout != null)
            {
                // Only trigger shortcut when not focused on an active text input field
                if (GUIUtility.keyboardControl == 0)
                {
                    if (currentEvent.keyCode == KeyCode.Delete || currentEvent.keyCode == KeyCode.Backspace)
                    {
                        if (GetSelectedIndices().Count > 0)
                        {
                            DeleteSelected();
                            currentEvent.Use();
                            GUIUtility.ExitGUI();
                            return;
                        }
                    }
                    else if (currentEvent.keyCode == KeyCode.D && (currentEvent.control || currentEvent.command))
                    {
                        if (GetSelectedIndices().Count > 0)
                        {
                            DuplicateSelected();
                            currentEvent.Use();
                            GUIUtility.ExitGUI();
                            return;
                        }
                    }
                }
            }

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

            if (lastSelectedEntry != selectedEntry)
            {
                lastSelectedEntry = selectedEntry;
                if (selectedEntry >= 0 && selectedEntry < layout.Entries.Count && layout.Entries[selectedEntry] != null)
                {
                    batchFace = layout.Entries[selectedEntry].Pose.Face;
                    batchRoll = layout.Entries[selectedEntry].Pose.RollQuarterTurns;
                    batchMatchId = layout.Entries[selectedEntry].MatchId;
                    batchShell = layout.Entries[selectedEntry].SurfaceShellIndex;
                }
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
            EditorGUILayout.LabelField("Selected Tiles", selectedEntries.Count.ToString());

            Color originalBg = GUI.backgroundColor;

            // --- 1. TILE GAP & SNAP SETTINGS BOX (Vàng hổ phách / Gold) ---
            EditorGUILayout.Space(2f);
            GUI.backgroundColor = GapBoxColor;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUI.backgroundColor = originalBg;
                EditorGUILayout.LabelField("⚙ Snap & Tile Gap Settings", EditorStyles.boldLabel);
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
            }
            GUI.backgroundColor = originalBg;

            EditorGUILayout.Space(2f);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    Color prevColor = GUI.backgroundColor;
                    GUI.backgroundColor = placing ? new Color(1.0f, 0.60f, 0.15f) : new Color(1.0f, 0.90f, 0.65f);
                    placing = GUILayout.Toggle(placing, placing ? "● Placing..." : "Place Tile", "Button");

                    GUI.backgroundColor = marqueeMode ? new Color(0.25f, 0.75f, 1.0f) : new Color(0.65f, 0.88f, 1.0f);
                    marqueeMode = GUILayout.Toggle(marqueeMode, marqueeMode ? "⧈ Box Selecting..." : "Box Select", "Button");

                    using (new EditorGUI.DisabledScope(GetSelectedIndices().Count == 0))
                    {
                        GUI.backgroundColor = new Color(0.50f, 0.90f, 0.85f);
                        if (GUILayout.Button("Duplicate (Ctrl+D)"))
                        {
                            DuplicateSelected();
                        }

                        GUI.backgroundColor = new Color(1.0f, 0.42f, 0.42f);
                        if (GUILayout.Button("Delete (Del)"))
                        {
                            DeleteSelected();
                        }
                    }
                    GUI.backgroundColor = prevColor;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    Color prevColor = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(0.78f, 0.86f, 1.0f);
                    if (GUILayout.Button("Select All"))
                    {
                        SelectAll();
                    }
                    GUI.backgroundColor = new Color(0.95f, 0.78f, 0.78f);
                    if (GUILayout.Button("Clear"))
                    {
                        ClearSelection();
                    }
                    GUI.backgroundColor = new Color(0.86f, 0.78f, 1.0f);
                    if (GUILayout.Button("Invert"))
                    {
                        InvertSelection();
                    }
                    GUI.backgroundColor = new Color(1.0f, 0.92f, 0.70f);
                    if (GUILayout.Button("Center to Origin"))
                    {
                        CenterLayoutToOrigin();
                    }
                    GUI.backgroundColor = prevColor;
                }
            }

            int selectedCount = GetSelectedIndices().Count;
            if (selectedCount > 0)
            {
                string countSuffix = selectedCount > 1 ? $" ({selectedCount} Tiles)" : "";

                // --- 2. MOVE SELECTED BOX (Xanh dương / Sky Blue) ---
                EditorGUILayout.Space(2f);
                GUI.backgroundColor = MoveBoxColor;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    GUI.backgroundColor = originalBg;
                    EditorGUILayout.LabelField($"✥ Move Selected{countSuffix}", EditorStyles.boldLabel);
                    GUI.backgroundColor = MoveButtonColor;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("← Left")) MoveSelectedTiles(VoxelGridDirection.Left);
                        if (GUILayout.Button("Right →")) MoveSelectedTiles(VoxelGridDirection.Right);
                        if (GUILayout.Button("↓ Down")) MoveSelectedTiles(VoxelGridDirection.Down);
                        if (GUILayout.Button("↑ Up")) MoveSelectedTiles(VoxelGridDirection.Up);
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("⤺ Back")) MoveSelectedTiles(VoxelGridDirection.Back);
                        if (GUILayout.Button("⤻ Forward")) MoveSelectedTiles(VoxelGridDirection.Forward);
                    }
                    GUI.backgroundColor = originalBg;
                }

                // --- 3. CREATE ADJACENT TILE BOX (Xanh lá / Emerald Green) ---
                EditorGUILayout.Space(2f);
                GUI.backgroundColor = CreateBoxColor;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    GUI.backgroundColor = originalBg;
                    EditorGUILayout.LabelField($"✚ Create Adjacent Tile{countSuffix}", EditorStyles.boldLabel);
                    GUI.backgroundColor = CreateButtonColor;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("+ Left")) CreateAdjacentTiles(VoxelGridDirection.Left);
                        if (GUILayout.Button("+ Right")) CreateAdjacentTiles(VoxelGridDirection.Right);
                        if (GUILayout.Button("+ Down")) CreateAdjacentTiles(VoxelGridDirection.Down);
                        if (GUILayout.Button("+ Up")) CreateAdjacentTiles(VoxelGridDirection.Up);
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("+ Back")) CreateAdjacentTiles(VoxelGridDirection.Back);
                        if (GUILayout.Button("+ Forward")) CreateAdjacentTiles(VoxelGridDirection.Forward);
                    }
                    GUI.backgroundColor = originalBg;
                }
            }

            EditorGUILayout.Space(2f);
            using (new EditorGUILayout.HorizontalScope())
            {
                Color prevColor = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.55f, 0.92f, 0.60f);
                if (GUILayout.Button("Validate"))
                {
                    ShowValidation();
                }

                GUI.backgroundColor = new Color(1.0f, 0.65f, 0.20f);
                if (GUILayout.Button("Bake To Shape"))
                {
                    BakeShape();
                }

                GUI.backgroundColor = new Color(0.90f, 0.82f, 0.68f);
                if (GUILayout.Button("Bake Complete Level (Legacy)"))
                {
                    BakeLayout();
                }
                GUI.backgroundColor = prevColor;
            }

            EditorGUILayout.Space();
            int currentSelectedCount = GetSelectedIndices().Count;
            if (currentSelectedCount > 1)
            {
                // --- 4. BATCH EDIT BOX (Tím / Lavender) ---
                GUI.backgroundColor = BatchBoxColor;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    GUI.backgroundColor = originalBg;
                    EditorGUILayout.LabelField($"⚙ Batch Edit ({currentSelectedCount} Tiles)", EditorStyles.boldLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        batchMatchId = EditorGUILayout.IntField("Match ID", batchMatchId);
                        if (GUILayout.Button("Set All", GUILayout.Width(60)))
                        {
                            ApplyBatchMatchId(batchMatchId);
                        }
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        batchShell = EditorGUILayout.IntField("Shell", batchShell);
                        if (GUILayout.Button("Set All", GUILayout.Width(60)))
                        {
                            ApplyBatchShell(batchShell);
                        }
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        batchFace = (VoxelGridDirection)EditorGUILayout.EnumPopup("Face", batchFace);
                        if (GUILayout.Button("Set All", GUILayout.Width(60)))
                        {
                            ApplyBatchFace(batchFace);
                        }
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        batchRoll = EditorGUILayout.IntSlider("Roll", batchRoll, 0, 3);
                        if (GUILayout.Button("Set All", GUILayout.Width(60)))
                        {
                            ApplyBatchRoll(batchRoll);
                        }
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("Quick Roll", GUILayout.Width(85));
                        if (GUILayout.Button("0 (0°)")) ApplyBatchRoll(0);
                        if (GUILayout.Button("1 (90°)")) ApplyBatchRoll(1);
                        if (GUILayout.Button("2 (180°)")) ApplyBatchRoll(2);
                        if (GUILayout.Button("3 (270°)")) ApplyBatchRoll(3);
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("Rotate Group", GUILayout.Width(85));
                        if (GUILayout.Button("Roll +90°"))
                        {
                            RotateSelectedRoll(1);
                        }
                        if (GUILayout.Button("Roll -90°"))
                        {
                            RotateSelectedRoll(-1);
                        }
                        if (GUILayout.Button("Yaw +90°"))
                        {
                            RotateSelectedYaw(90f);
                        }
                        if (GUILayout.Button("Yaw -90°"))
                        {
                            RotateSelectedYaw(-90f);
                        }
                    }
                }
                GUI.backgroundColor = originalBg;
                EditorGUILayout.Space();
            }

            string selectedHeader = currentSelectedCount > 1
                ? $"Selected Tile (Lead: #{selectedEntry}) [Multi-Select: {currentSelectedCount} Tiles]"
                : "Selected Tile";
            EditorGUILayout.LabelField(selectedHeader, EditorStyles.boldLabel);
            if (currentSelectedCount > 1)
            {
                multiSelectSyncPose = EditorGUILayout.ToggleLeft($"Sync Face & Roll edits to all {currentSelectedCount} selected tiles", multiSelectSyncPose);
            }
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
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
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
            }
            entry.FinePositionOffset = EditorGUILayout.Vector3Field("Fine Position", entry.FinePositionOffset);
            entry.FineRotationOffset = EditorGUILayout.Vector3Field("Fine Rotation", entry.FineRotationOffset);
            entry.SurfaceShellIndex = EditorGUILayout.IntField("Shell", entry.SurfaceShellIndex);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(layout, "Edit Mahjong Tile Placement");
                bool faceChanged = previousFace != entry.Pose.Face;
                bool rollChanged = previousRoll != entry.Pose.RollQuarterTurns;
                bool fineRotChanged = previousFineRotation != entry.FineRotationOffset;

                if (currentSelectedCount > 1 && multiSelectSyncPose && (faceChanged || rollChanged))
                {
                    if (faceChanged && rollChanged)
                    {
                        ApplyBatchPose(entry.Pose.Face, entry.Pose.RollQuarterTurns);
                    }
                    else if (faceChanged)
                    {
                        ApplyBatchFace(entry.Pose.Face);
                    }
                    else if (rollChanged)
                    {
                        ApplyBatchRoll(entry.Pose.RollQuarterTurns);
                    }
                }
                else if (faceChanged || rollChanged || fineRotChanged)
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

        private void SelectAll()
        {
            if (layout == null) return;
            selectedEntries.Clear();
            for (int i = 0; i < layout.Entries.Count; i++)
            {
                if (layout.Entries[i] != null)
                {
                    selectedEntries.Add(i);
                }
            }
            selectedEntry = selectedEntries.Count > 0 ? 0 : -1;
            Repaint();
            SceneView.RepaintAll();
        }

        private void ClearSelection()
        {
            selectedEntries.Clear();
            selectedEntry = -1;
            Repaint();
            SceneView.RepaintAll();
        }

        private void InvertSelection()
        {
            if (layout == null) return;
            HashSet<int> inverted = new HashSet<int>();
            for (int i = 0; i < layout.Entries.Count; i++)
            {
                if (layout.Entries[i] != null && !selectedEntries.Contains(i))
                {
                    inverted.Add(i);
                }
            }
            selectedEntries.Clear();
            foreach (int idx in inverted)
            {
                selectedEntries.Add(idx);
            }
            List<int> list = GetSelectedIndices();
            selectedEntry = list.Count > 0 ? list[0] : -1;
            Repaint();
            SceneView.RepaintAll();
        }

        private void CenterLayoutToOrigin()
        {
            if (layout == null || layout.Entries.Count == 0) return;

            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < layout.Entries.Count; i++)
            {
                TileAuthoringEntry e = layout.Entries[i];
                if (e != null)
                {
                    min = Vector3.Min(min, e.ResolvedPosition);
                    max = Vector3.Max(max, e.ResolvedPosition);
                }
            }
            Vector3 center = (min + max) * 0.5f;
            if (center.sqrMagnitude < 0.0001f) return;

            Undo.RecordObject(layout, "Center Layout to Origin");
            for (int i = 0; i < layout.Entries.Count; i++)
            {
                TileAuthoringEntry e = layout.Entries[i];
                if (e != null)
                {
                    e.LocalPosition -= center;
                }
            }

            EditorUtility.SetDirty(layout);
            Repaint();
            SceneView.RepaintAll();
        }

        private void ApplyBatchMatchId(int matchId)
        {
            List<int> indices = GetSelectedIndices();
            if (indices.Count == 0 || layout == null) return;

            Undo.RecordObject(layout, "Batch Set Match ID");
            for (int i = 0; i < indices.Count; i++)
            {
                TileAuthoringEntry entry = layout.Entries[indices[i]];
                if (entry != null)
                {
                    entry.MatchId = matchId;
                }
            }
            EditorUtility.SetDirty(layout);
            Repaint();
            SceneView.RepaintAll();
        }

        private void ApplyBatchShell(int shellIndex)
        {
            List<int> indices = GetSelectedIndices();
            if (indices.Count == 0 || layout == null) return;

            Undo.RecordObject(layout, "Batch Set Shell Index");
            for (int i = 0; i < indices.Count; i++)
            {
                TileAuthoringEntry entry = layout.Entries[indices[i]];
                if (entry != null)
                {
                    entry.SurfaceShellIndex = shellIndex;
                }
            }
            EditorUtility.SetDirty(layout);
            Repaint();
            SceneView.RepaintAll();
        }

        private void ApplyBatchFace(VoxelGridDirection face)
        {
            List<int> indices = GetSelectedIndices();
            if (indices.Count == 0 || layout == null) return;

            Undo.RecordObject(layout, "Batch Set Tile Face");
            batchFace = face;
            for (int i = 0; i < indices.Count; i++)
            {
                TileAuthoringEntry entry = layout.Entries[indices[i]];
                if (entry != null)
                {
                    entry.Pose.Face = face;
                    entry.FineRotationOffset = Vector3.zero;
                    TileAuthoringEntry snapSource = FindSnapSource(entry);
                    if (entry.UseSnapOffset && snapSource != null)
                    {
                        ApplyAdjacentSnap(entry, snapSource, false);
                    }
                }
            }
            EditorUtility.SetDirty(layout);
            Repaint();
            SceneView.RepaintAll();
        }

        private void ApplyBatchRoll(int roll)
        {
            List<int> indices = GetSelectedIndices();
            if (indices.Count == 0 || layout == null) return;

            Undo.RecordObject(layout, "Batch Set Tile Roll");
            int normalizedRoll = TileSnapMath.NormalizeRoll(roll);
            batchRoll = normalizedRoll;
            for (int i = 0; i < indices.Count; i++)
            {
                TileAuthoringEntry entry = layout.Entries[indices[i]];
                if (entry != null)
                {
                    entry.Pose.RollQuarterTurns = normalizedRoll;
                    entry.FineRotationOffset = Vector3.zero;
                    TileAuthoringEntry snapSource = FindSnapSource(entry);
                    if (entry.UseSnapOffset && snapSource != null)
                    {
                        ApplyAdjacentSnap(entry, snapSource, false);
                    }
                }
            }
            EditorUtility.SetDirty(layout);
            Repaint();
            SceneView.RepaintAll();
        }

        private void ApplyBatchPose(VoxelGridDirection face, int roll)
        {
            List<int> indices = GetSelectedIndices();
            if (indices.Count == 0 || layout == null) return;

            Undo.RecordObject(layout, "Batch Set Tile Pose");
            int normalizedRoll = TileSnapMath.NormalizeRoll(roll);
            batchFace = face;
            batchRoll = normalizedRoll;
            for (int i = 0; i < indices.Count; i++)
            {
                TileAuthoringEntry entry = layout.Entries[indices[i]];
                if (entry != null)
                {
                    entry.Pose.Face = face;
                    entry.Pose.RollQuarterTurns = normalizedRoll;
                    entry.FineRotationOffset = Vector3.zero;
                    TileAuthoringEntry snapSource = FindSnapSource(entry);
                    if (entry.UseSnapOffset && snapSource != null)
                    {
                        ApplyAdjacentSnap(entry, snapSource, false);
                    }
                }
            }
            EditorUtility.SetDirty(layout);
            Repaint();
            SceneView.RepaintAll();
        }

        private void RotateSelectedRoll(int quarterTurns)
        {
            List<int> indices = GetSelectedIndices();
            if (indices.Count == 0 || layout == null) return;

            Undo.RecordObject(layout, "Rotate Selected Tiles Roll");
            for (int i = 0; i < indices.Count; i++)
            {
                TileAuthoringEntry entry = layout.Entries[indices[i]];
                if (entry != null)
                {
                    entry.Pose.RollQuarterTurns = TileSnapMath.NormalizeRoll(entry.Pose.RollQuarterTurns + quarterTurns);
                    entry.FineRotationOffset = Vector3.zero;
                    TileAuthoringEntry snapSource = FindSnapSource(entry);
                    if (entry.UseSnapOffset && snapSource != null)
                    {
                        ApplyAdjacentSnap(entry, snapSource, false);
                    }
                }
            }

            EditorUtility.SetDirty(layout);
            Repaint();
            SceneView.RepaintAll();
        }

        private void RotateSelectedYaw(float angleDegrees)
        {
            List<int> indices = GetSelectedIndices();
            if (indices.Count == 0 || layout == null) return;

            Undo.RecordObject(layout, "Rotate Selected Tiles Yaw");
            Vector3 center = Vector3.zero;
            int count = 0;
            for (int i = 0; i < indices.Count; i++)
            {
                TileAuthoringEntry entry = layout.Entries[indices[i]];
                if (entry != null)
                {
                    center += entry.ResolvedPosition;
                    count++;
                }
            }
            if (count > 0) center /= count;

            Quaternion deltaRot = Quaternion.AngleAxis(angleDegrees, Vector3.up);
            for (int i = 0; i < indices.Count; i++)
            {
                TileAuthoringEntry entry = layout.Entries[indices[i]];
                if (entry == null) continue;

                Vector3 offset = entry.ResolvedPosition - center;
                Vector3 rotatedOffset = deltaRot * offset;
                entry.LocalPosition = (center + rotatedOffset) - entry.FinePositionOffset;

                Quaternion newRot = deltaRot * Quaternion.Euler(entry.ResolvedEulerAngles);
                Quaternion baseSemantic = TileSnapMath.GetRotation(entry.Pose.Face, entry.Pose.RollQuarterTurns);
                entry.FineRotationOffset = (Quaternion.Inverse(baseSemantic) * newRot).eulerAngles;
            }

            EditorUtility.SetDirty(layout);
            Repaint();
            SceneView.RepaintAll();
        }

        private void MoveSelectedTile(VoxelGridDirection direction)
        {
            MoveSelectedTiles(direction);
        }

        private void MoveSelectedTiles(VoxelGridDirection direction)
        {
            List<int> indices = GetSelectedIndices();
            if (indices.Count == 0 || tilePrefab == null || layout == null)
            {
                return;
            }

            Undo.RecordObject(layout, $"Move {indices.Count} Tile(s) {direction}");
            int divisions = Mathf.Max(1, layout.SnapDivisions);

            for (int i = 0; i < indices.Count; i++)
            {
                TileAuthoringEntry entry = layout.Entries[indices[i]];
                if (entry == null) continue;

                Quaternion selectedRotation = Quaternion.Euler(entry.ResolvedEulerAngles);
                Vector3 worldDirection = TileSnapMath.GetPrefabWorldDirection(
                    tilePrefab.DirectionFrame,
                    selectedRotation,
                    direction);
                if (worldDirection.sqrMagnitude <= 0.0001f)
                {
                    continue;
                }

                float moveDistance = TileSnapMath.GetOrientedExtent(tileSize, selectedRotation, worldDirection)
                    * 2f / divisions;
                Vector3 worldDelta = worldDirection.normalized * moveDistance;
                Vector3 localDelta = previewRoot != null
                    ? previewRoot.transform.InverseTransformVector(worldDelta)
                    : worldDelta;

                entry.LocalPosition += localDelta;
                entry.FinePositionOffset = Vector3.zero;
                entry.UseSnapOffset = false;
                entry.SetSnapSource(null);
            }

            EditorUtility.SetDirty(layout);
            Repaint();
            SceneView.RepaintAll();
        }

        private void CreateAdjacentTile(VoxelGridDirection direction)
        {
            CreateAdjacentTiles(direction);
        }

        private void CreateAdjacentTiles(VoxelGridDirection direction)
        {
            List<int> indices = GetSelectedIndices();
            if (indices.Count == 0 || tilePrefab == null || layout == null)
            {
                return;
            }

            Undo.RecordObject(layout, $"Create Adjacent Mahjong Tile(s) ({direction})");

            List<TileAuthoringEntry> sources = new List<TileAuthoringEntry>();
            for (int i = 0; i < indices.Count; i++)
            {
                int idx = indices[i];
                if (idx >= 0 && idx < layout.Entries.Count && layout.Entries[idx] != null)
                {
                    sources.Add(layout.Entries[idx]);
                }
            }

            if (sources.Count == 0)
            {
                return;
            }

            List<int> newIndices = new List<int>();
            for (int i = 0; i < sources.Count; i++)
            {
                TileAuthoringEntry source = sources[i];
                TileAuthoringEntry created = TileAuthoringEntry.Create(
                    source.MatchId,
                    source.ResolvedPosition,
                    source.Pose.Face,
                    source.Pose.RollQuarterTurns);

                created.FineRotationOffset = source.FineRotationOffset;
                created.AdjacentDirectionSpace = TileAdjacentDirectionSpace.TilePrefab;
                created.SetSnapOffset(source, direction, 0, 0);
                created.SnapOffsetSizeSource = TileSnapOffsetSizeSource.SourceTile;
                created.SurfaceShellIndex = source.SurfaceShellIndex;

                layout.AddEntry(created);
                ApplyAdjacentSnap(created, source);

                // Prevent duplicate overlapping tile if one already exists at target position
                Vector3 targetPos = created.ResolvedPosition;
                bool isDuplicate = false;
                for (int e = 0; e < layout.Entries.Count - 1; e++)
                {
                    TileAuthoringEntry existing = layout.Entries[e];
                    if (existing != null && (existing.ResolvedPosition - targetPos).sqrMagnitude < 0.001f)
                    {
                        isDuplicate = true;
                        break;
                    }
                }

                if (isDuplicate)
                {
                    layout.RemoveEntryAt(layout.Entries.Count - 1);
                    continue;
                }

                newIndices.Add(layout.Entries.Count - 1);
            }

            if (newIndices.Count > 0)
            {
                selectedEntries.Clear();
                for (int i = 0; i < newIndices.Count; i++)
                {
                    selectedEntries.Add(newIndices[i]);
                }
                selectedEntry = newIndices[newIndices.Count - 1];
            }

            EditorUtility.SetDirty(layout);
            Repaint();
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
            if (current.type == EventType.KeyDown)
            {
                if (current.keyCode == KeyCode.Delete || current.keyCode == KeyCode.Backspace)
                {
                    if (GetSelectedIndices().Count > 0)
                    {
                        DeleteSelected();
                        current.Use();
                        return;
                    }
                }
                else if (current.keyCode == KeyCode.D && (current.control || current.command))
                {
                    if (GetSelectedIndices().Count > 0)
                    {
                        DuplicateSelected();
                        current.Use();
                        return;
                    }
                }
            }

            RefreshPreviewTiles();
            if (!placing && current.type == EventType.Layout)
            {
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            }

            DrawEntries();
            if (!placing)
            {
                if (isMarqueeSelecting)
                {
                    if (current.type == EventType.MouseDrag && current.button == 0)
                    {
                        marqueeCurrent = current.mousePosition;
                        current.Use();
                        sceneView.Repaint();
                    }
                    else if (current.type == EventType.MouseUp && current.button == 0)
                    {
                        isMarqueeSelecting = false;
                        Rect selectRect = GetScreenRect(marqueeStart, marqueeCurrent);
                        bool additive = current.control || current.command || current.shift;
                        ApplyMarqueeSelection(selectRect, additive);
                        current.Use();
                        Repaint();
                        SceneView.RepaintAll();
                    }
                    else if (current.type == EventType.Repaint)
                    {
                        DrawMarqueeRect(marqueeStart, marqueeCurrent);
                    }
                }
                else
                {
                    bool canMarquee = marqueeMode || current.shift;
                    if (current.type == EventType.MouseDown
                        && current.button == 0
                        && !current.alt
                        && GUIUtility.hotControl == 0)
                    {
                        if (canMarquee && marqueeMode)
                        {
                            isMarqueeSelecting = true;
                            marqueeStart = current.mousePosition;
                            marqueeCurrent = current.mousePosition;
                            current.Use();
                            return;
                        }
                        else if (TrySelectTile(current.mousePosition))
                        {
                            current.Use();
                            return;
                        }
                        else if (canMarquee)
                        {
                            isMarqueeSelecting = true;
                            marqueeStart = current.mousePosition;
                            marqueeCurrent = current.mousePosition;
                            current.Use();
                            return;
                        }
                    }
                }
            }

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
                    selectedEntries.Clear();
                    selectedEntry = layout.Entries.Count - 1;
                    selectedEntries.Add(selectedEntry);
                    EditorUtility.SetDirty(layout);
                    placing = false;
                    current.Use();
                    Repaint();
                }
            }
        }

        private bool TrySelectTile(Vector2 mousePosition)
        {
            if (layout == null || layout.Entries.Count == 0)
            {
                return false;
            }

            Ray ray = HandleUtility.GUIPointToWorldRay(mousePosition);
            float closestDistance = float.MaxValue;
            int closestIndex = -1;

            Vector3 boxExtent = tileSize.sqrMagnitude > 0.001f ? tileSize : Vector3.one;

            for (int index = 0; index < layout.Entries.Count; index++)
            {
                TileAuthoringEntry entry = layout.Entries[index];
                if (entry == null) continue;

                Vector3 pos = entry.ResolvedPosition;
                Quaternion rot = Quaternion.Euler(entry.ResolvedEulerAngles);

                if (RayIntersectsBox(ray, pos, rot, boxExtent, out float hitDist))
                {
                    if (hitDist < closestDistance)
                    {
                        closestDistance = hitDist;
                        closestIndex = index;
                    }
                }
            }

            // Fallback: check screen-space distance to tile center (picks even if clicked near edge)
            if (closestIndex < 0)
            {
                float minScreenDist = 30f;
                for (int index = 0; index < layout.Entries.Count; index++)
                {
                    TileAuthoringEntry entry = layout.Entries[index];
                    if (entry == null) continue;

                    Vector2 screenPos = HandleUtility.WorldToGUIPoint(entry.ResolvedPosition);
                    float d = Vector2.Distance(screenPos, mousePosition);
                    if (d < minScreenDist)
                    {
                        minScreenDist = d;
                        closestIndex = index;
                    }
                }
            }

            if (closestIndex < 0 || closestIndex >= layout.Entries.Count)
            {
                return false;
            }

            bool additiveSelection = Event.current.control || Event.current.command || Event.current.shift;
            if (!additiveSelection)
            {
                selectedEntries.Clear();
            }

            if (!selectedEntries.Add(closestIndex) && additiveSelection)
            {
                selectedEntries.Remove(closestIndex);
            }

            selectedEntry = selectedEntries.Count > 0 ? closestIndex : -1;
            Repaint();
            SceneView.RepaintAll();
            return true;
        }

        private static bool RayIntersectsBox(Ray ray, Vector3 boxCenter, Quaternion boxRotation, Vector3 boxSize, out float distance)
        {
            distance = 0f;
            Vector3 localOrigin = Quaternion.Inverse(boxRotation) * (ray.origin - boxCenter);
            Vector3 localDir = Quaternion.Inverse(boxRotation) * ray.direction;
            Vector3 half = boxSize * 0.5f;

            float tMin = 0f;
            float tMax = float.MaxValue;

            for (int i = 0; i < 3; i++)
            {
                float origin = localOrigin[i];
                float dir = localDir[i];
                float h = half[i];

                if (Mathf.Abs(dir) < 1e-6f)
                {
                    if (origin < -h || origin > h) return false;
                }
                else
                {
                    float t1 = (-h - origin) / dir;
                    float t2 = (h - origin) / dir;
                    if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                    tMin = Mathf.Max(tMin, t1);
                    tMax = Mathf.Min(tMax, t2);
                    if (tMin > tMax) return false;
                }
            }

            distance = tMin;
            return true;
        }

        private static void DrawMarqueeRect(Vector2 start, Vector2 current)
        {
            Handles.BeginGUI();
            Rect rect = GetScreenRect(start, current);
            EditorGUI.DrawRect(rect, new Color(0.2f, 0.6f, 1f, 0.2f));
            Handles.DrawSolidRectangleWithOutline(rect, Color.clear, new Color(0.1f, 0.6f, 1f, 0.9f));
            Handles.EndGUI();
        }

        private static Rect GetScreenRect(Vector2 p1, Vector2 p2)
        {
            return new Rect(
                Mathf.Min(p1.x, p2.x),
                Mathf.Min(p1.y, p2.y),
                Mathf.Max(1f, Mathf.Abs(p1.x - p2.x)),
                Mathf.Max(1f, Mathf.Abs(p1.y - p2.y)));
        }

        private void ApplyMarqueeSelection(Rect rect, bool additive)
        {
            if (layout == null) return;
            if (!additive)
            {
                selectedEntries.Clear();
            }

            if (rect.width < 5f && rect.height < 5f)
            {
                if (!additive)
                {
                    selectedEntry = -1;
                }
                return;
            }

            for (int index = 0; index < layout.Entries.Count; index++)
            {
                TileAuthoringEntry entry = layout.Entries[index];
                if (entry == null)
                {
                    continue;
                }

                Vector2 screenPoint = HandleUtility.WorldToGUIPoint(entry.ResolvedPosition);
                if (rect.Contains(screenPoint))
                {
                    selectedEntries.Add(index);
                }
            }

            List<int> selectedList = GetSelectedIndices();
            selectedEntry = selectedList.Count > 0 ? selectedList[selectedList.Count - 1] : -1;
            Repaint();
            SceneView.RepaintAll();
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
            previewEntryIndices.Clear();
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
                preview.name = $"PreviewTile_{index}";
                preview.gameObject.SetActive(true);
                previewEntryIndices[preview] = index;
                //DrawDirectionMarkers(preview);
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

                // Highlight selected tiles with a clean wireframe box (no clutter dots, no match labels)
                if (selectedEntries.Contains(index) || index == selectedEntry)
                {
                    Handles.color = index == selectedEntry
                        ? new Color(1f, 0.85f, 0.1f, 0.95f)
                        : new Color(0.2f, 0.75f, 1f, 0.85f);

                    Matrix4x4 prevMatrix = Handles.matrix;
                    Handles.matrix = Matrix4x4.TRS(position, Quaternion.Euler(entry.ResolvedEulerAngles), Vector3.one);
                    Handles.DrawWireCube(Vector3.zero, tileSize);
                    Handles.matrix = prevMatrix;
                }

                if (index != selectedEntry)
                {
                    continue;
                }

                if (Tools.current == Tool.Move)
                {
                    EditorGUI.BeginChangeCheck();
                    Vector3 moved = Handles.PositionHandle(position, Quaternion.Euler(entry.ResolvedEulerAngles));
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(layout, "Move Mahjong Tile");
                        Vector3 movedLocal = previewRoot != null
                            ? previewRoot.transform.InverseTransformPoint(moved)
                            : moved;
                        Vector3 newLocalPos = movedLocal - entry.FinePositionOffset;
                        Vector3 delta = newLocalPos - entry.LocalPosition;

                        List<int> selectedList = GetSelectedIndices();
                        if (selectedList.Count > 1)
                        {
                            for (int i = 0; i < selectedList.Count; i++)
                            {
                                TileAuthoringEntry e = layout.Entries[selectedList[i]];
                                if (e != null)
                                {
                                    e.LocalPosition += delta;
                                }
                            }
                        }
                        else
                        {
                            entry.LocalPosition = newLocalPos;
                        }

                        EditorUtility.SetDirty(layout);
                        Repaint();
                        SceneView.RepaintAll();
                    }
                }
                else if (Tools.current == Tool.Rotate)
                {
                    EditorGUI.BeginChangeCheck();
                    Quaternion currentRot = Quaternion.Euler(entry.ResolvedEulerAngles);
                    Quaternion rotated = Handles.RotationHandle(currentRot, position);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(layout, "Rotate Mahjong Tile");
                        Quaternion deltaRot = rotated * Quaternion.Inverse(currentRot);

                        List<int> selectedList = GetSelectedIndices();
                        if (selectedList.Count > 1)
                        {
                            Vector3 pivot = position;
                            for (int i = 0; i < selectedList.Count; i++)
                            {
                                TileAuthoringEntry e = layout.Entries[selectedList[i]];
                                if (e == null) continue;

                                if (selectedList[i] == selectedEntry)
                                {
                                    e.FineRotationOffset = (Quaternion.Inverse(TileSnapMath.GetRotation(e.Pose.Face, e.Pose.RollQuarterTurns)) * rotated).eulerAngles;
                                }
                                else
                                {
                                    Vector3 offsetFromPivot = e.ResolvedPosition - pivot;
                                    Vector3 rotatedOffset = deltaRot * offsetFromPivot;
                                    e.LocalPosition = (pivot + rotatedOffset) - e.FinePositionOffset;

                                    Quaternion newOrientation = deltaRot * Quaternion.Euler(e.ResolvedEulerAngles);
                                    Quaternion baseSemantic = TileSnapMath.GetRotation(e.Pose.Face, e.Pose.RollQuarterTurns);
                                    e.FineRotationOffset = (Quaternion.Inverse(baseSemantic) * newOrientation).eulerAngles;
                                }
                            }
                        }
                        else
                        {
                            entry.FineRotationOffset = (Quaternion.Inverse(TileSnapMath.GetRotation(entry.Pose.Face, entry.Pose.RollQuarterTurns)) * rotated).eulerAngles;
                        }

                        EditorUtility.SetDirty(layout);
                        Repaint();
                        SceneView.RepaintAll();
                    }
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
            List<int> indices = GetSelectedIndices();
            if (indices.Count == 0)
            {
                return;
            }

            Undo.RecordObject(layout, "Duplicate Mahjong Tiles");
            selectedEntries.Clear();
            for (int index = 0; index < indices.Count; index++)
            {
                TileAuthoringEntry source = layout.Entries[indices[index]];
                if (source == null)
                {
                    continue;
                }

                TileAuthoringEntry copy = TileAuthoringEntry.Create(
                    source.MatchId,
                    source.ResolvedPosition,
                    source.Pose.Face,
                    source.Pose.RollQuarterTurns);
                copy.FineRotationOffset = source.FineRotationOffset;
                copy.AdjacentDirectionSpace = source.AdjacentDirectionSpace;
                copy.AdjacentOffsetMode = source.AdjacentOffsetMode;
                copy.SnapOffsetSizeSource = source.SnapOffsetSizeSource;
                copy.SurfaceShellIndex = source.SurfaceShellIndex;
                copy.UseSnapOffset = false;
                copy.SetSnapSource(null);
                layout.AddEntry(copy);
                selectedEntries.Add(layout.Entries.Count - 1);
            }

            selectedEntry = selectedEntries.Count > 0 ? layout.Entries.Count - 1 : -1;
            EditorUtility.SetDirty(layout);
            Repaint();
            SceneView.RepaintAll();
        }

        private void DeleteSelected()
        {
            List<int> indices = GetSelectedIndices();
            if (indices.Count == 0)
            {
                return;
            }

            Undo.RecordObject(layout, "Delete Mahjong Tiles");
            indices.Sort((left, right) => right.CompareTo(left));
            for (int index = 0; index < indices.Count; index++)
            {
                layout.RemoveEntryAt(indices[index]);
            }

            selectedEntries.Clear();
            selectedEntry = -1;
            EditorUtility.SetDirty(layout);
            Repaint();
            SceneView.RepaintAll();
        }

        private List<int> GetSelectedIndices()
        {
            List<int> indices = new List<int>();
            foreach (int index in selectedEntries)
            {
                if (index >= 0 && index < layout.Entries.Count && layout.Entries[index] != null)
                {
                    indices.Add(index);
                }
            }

            if (indices.Count == 0
                && selectedEntry >= 0
                && selectedEntry < layout.Entries.Count
                && layout.Entries[selectedEntry] != null)
            {
                indices.Add(selectedEntry);
            }

            indices.Sort();
            return indices;
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
