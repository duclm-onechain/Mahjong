using MahjongOut3D.LevelSystem;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MahjongOut3D.Editor
{
    /// <summary>
    /// Draws the inspector controls used to generate many procedural level assets with one click.
    /// </summary>
    [CustomEditor(typeof(ProceduralLevelBatchGenerator))]
    public sealed class ProceduralLevelBatchGeneratorEditor : UnityEditor.Editor
    {
        /// <summary>
        /// Draws the generator inspector and its action buttons.
        /// </summary>
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.HelpBox("Assign Authored Shape to preserve a fixed manually-designed shape. Leave it empty to use the built-in procedural shapes.", MessageType.None);
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Generator now writes surface-wrapped levels. Each exposed face is split into tile slots, and outer shells are generated before inner shells so the block peels from outside to inside.", MessageType.Info);
            EditorGUILayout.HelpBox("Easy keeps matched pairs close together on the same face when possible. Normal, Hard, and SuperHard progressively spread pair members across different faces and shell layers.", MessageType.Info);
            EditorGUILayout.HelpBox("SuperHard levels are written with runtime difficulty Expert to stay compatible with the existing enum.", MessageType.Info);
            if (((ProceduralLevelBatchGenerator)target).AuthoredShape != null)
            {
                EditorGUILayout.HelpBox("An authored shape is assigned. Procedural generation will preserve its fixed tile count and shell layout, then generate match pairs according to each difficulty tier.", MessageType.Info);
            }
            EditorGUILayout.HelpBox("Write Mode: New Gen sẽ giữ level cũ và tạo level mới với số tiếp theo. Overwrite Matching sẽ chỉ cập nhật level trùng tên generate, không xoá sạch catalog cũ.", MessageType.Warning);
            EditorGUILayout.HelpBox("Google Sheet link cần public/publish để Unity tải được CSV. Hỗ trợ cả link share sheet thường và link pub/output=csv.", MessageType.Info);

            if (GUILayout.Button("Generate Levels"))
            {
                GenerateLevels((ProceduralLevelBatchGenerator)target);
            }

            if (GUILayout.Button("Generate Levels From JSON Config"))
            {
                GenerateLevelsFromJsonConfig((ProceduralLevelBatchGenerator)target);
            }

            if (GUILayout.Button("Generate Levels From Google Sheet URL"))
            {
                GenerateLevelsFromGoogleSheetUrl((ProceduralLevelBatchGenerator)target);
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("Open Manual Tile Layout Editor"))
            {
                TileLayoutAuthoringWindow.Open();
            }
        }

        /// <summary>
        /// Runs the batch generator and pings the configured level catalog afterwards.
        /// </summary>
        private static void GenerateLevels(ProceduralLevelBatchGenerator generator)
        {
            try
            {
                var generatedAssets = generator.GenerateAssets(generator.WriteMode);
                Debug.Log($"Generated {generatedAssets.Count} procedural levels from {generator.name} using {generator.WriteMode}.", generator);
                EditorUtility.SetDirty(generator);
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception, generator);
                EditorUtility.DisplayDialog("Level Generation Failed", exception.Message, "OK");
            }
        }

        private static void GenerateLevelsFromJsonConfig(ProceduralLevelBatchGenerator generator)
        {
            try
            {
                var generatedAssets = generator.GenerateAssetsFromJsonConfig(generator.WriteMode);
                Debug.Log($"Generated {generatedAssets.Count} procedural levels from JSON config using {generator.WriteMode}.", generator);
                EditorUtility.SetDirty(generator);
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception, generator);
                EditorUtility.DisplayDialog("JSON Level Generation Failed", exception.Message, "OK");
            }
        }

        private static void GenerateLevelsFromGoogleSheetUrl(ProceduralLevelBatchGenerator generator)
        {
            try
            {
                string csvUrl = generator.GetResolvedGoogleSheetCsvUrl();
                if (string.IsNullOrWhiteSpace(csvUrl))
                {
                    EditorUtility.DisplayDialog("Missing Google Sheet URL", "Paste a Google Sheet URL into the generator first.", "OK");
                    return;
                }

                EditorCoroutine.start(DownloadAndGenerateLevels(generator, csvUrl));
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception, generator);
                EditorUtility.DisplayDialog("Google Sheet URL Failed", exception.Message, "OK");
            }
        }

        private static IEnumerator DownloadAndGenerateLevels(ProceduralLevelBatchGenerator generator, string csvUrl)
        {
            WWW www = new WWW(csvUrl);
            while (!www.isDone)
            {
                yield return null;
            }

            if (!string.IsNullOrWhiteSpace(www.error))
            {
                Debug.LogError($"Failed to download Google Sheet CSV: {www.error}", generator);
                EditorUtility.DisplayDialog("Google Sheet Download Failed", www.error, "OK");
                yield break;
            }

            List<LevelDefinition> generatedAssets = null;
            System.Exception generationException = null;

            try
            {
                generatedAssets = generator.GenerateAssetsFromGoogleSheetCsv(www.text, generator.WriteMode);
                EditorUtility.SetDirty(generator);
            }
            catch (System.Exception exception)
            {
                generationException = exception;
            }

            if (generationException != null)
            {
                Debug.LogException(generationException, generator);
                EditorUtility.DisplayDialog("Google Sheet Generation Failed", generationException.Message, "OK");
                yield break;
            }

            Debug.Log($"Generated {generatedAssets.Count} procedural levels from Google Sheet using {generator.WriteMode}.", generator);
        }
    }
}
