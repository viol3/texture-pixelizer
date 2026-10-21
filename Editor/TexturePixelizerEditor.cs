namespace Viol3.Pixelizer.Editor
{
    using UnityEditor;
    using UnityEngine;

    [CustomEditor(typeof(TexturePixelizer))]
    [CanEditMultipleObjects]
    public class TexturePixelizerEditor : UnityEditor.Editor
    {
        private SerializedProperty materialIndex;
        private SerializedProperty textureProperty;
        private SerializedProperty settings;
        private SerializedProperty sourceTexture;
        private SerializedProperty pixelatedTexture;

        private Texture2D preview;
        private int previewPaletteCount;
        private bool autoPreview;
        private string lastMessage;
        private MessageType lastMessageType;
        private Material sharedCheckMaterial;
        private bool sharedCheckResult;

        private void OnEnable()
        {
            materialIndex = serializedObject.FindProperty(nameof(TexturePixelizer.materialIndex));
            textureProperty = serializedObject.FindProperty(nameof(TexturePixelizer.textureProperty));
            settings = serializedObject.FindProperty(nameof(TexturePixelizer.settings));
            sourceTexture = serializedObject.FindProperty(nameof(TexturePixelizer.sourceTexture));
            pixelatedTexture = serializedObject.FindProperty(nameof(TexturePixelizer.pixelatedTexture));
        }

        private void OnDisable()
        {
            ClearPreview();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField($"{TexturePixelizer.DisplayName} v{TexturePixelizer.Version}", EditorStyles.miniBoldLabel);

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.PropertyField(sourceTexture);
            DrawTargetInfo();

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(settings, true);
            bool settingsChanged = EditorGUI.EndChangeCheck();

            DrawSettingsHints();

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(pixelatedTexture);
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            DrawButtons();

            if (!string.IsNullOrEmpty(lastMessage))
                EditorGUILayout.HelpBox(lastMessage, lastMessageType);

            if (settingsChanged && autoPreview) RefreshPreview();
            DrawPreview();
        }

        private void DrawTargetInfo()
        {
            EditorGUILayout.PropertyField(materialIndex);
            EditorGUILayout.PropertyField(textureProperty);

            if (targets.Length > 1) return;

            var pixelizer = (TexturePixelizer)target;
            Material material = PixelizerAssetUtility.GetMaterial(pixelizer);
            string error = PixelizerAssetUtility.ValidateMaterial(material);
            if (error != null)
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
                return;
            }

            string property = PixelizerAssetUtility.ResolveTextureProperty(material, pixelizer.textureProperty);
            if (property == null)
            {
                EditorGUILayout.HelpBox($"'{material.name}' has no matching texture property.", MessageType.Error);
                return;
            }

            Texture2D source = PixelizerAssetUtility.FindSourceTexture(pixelizer, material, property);
            string sourceInfo = source != null ? $"{source.name} ({source.width}x{source.height})" : "none";
            if (pixelizer.sourceTexture == null && source != null) sourceInfo += " — from material";
            EditorGUILayout.HelpBox($"Material: {material.name}\nProperty: {property}\nSource: {sourceInfo}",
                source != null ? MessageType.None : MessageType.Warning);

            if (pixelizer.sourceTexture != null && pixelizer.sourceTexture.name.EndsWith(PixelizerAssetUtility.Suffix))
                EditorGUILayout.HelpBox("Source Texture is already a pixelated output; the effect will stack. " +
                                        "Use the original texture instead.", MessageType.Warning);

            if (IsMaterialShared(pixelizer, material))
                EditorGUILayout.HelpBox("This material is used by other objects too; they will all show the pixelated texture.",
                    MessageType.Info);
        }

        private void DrawSettingsHints()
        {
            if (targets.Length > 1) return;
            PixelizeSettings s = ((TexturePixelizer)target).settings;

            if (s.colorMode == ColorMode.None && s.ditherMode != DitherMode.None)
                EditorGUILayout.HelpBox("Dithering needs a Color Reduction mode; it is skipped with 'None'.", MessageType.Info);
        }

        private void DrawButtons()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(targets.Length > 1))
                {
                    if (GUILayout.Button("Preview")) RefreshPreview();
                    autoPreview = GUILayout.Toggle(autoPreview, "Auto", "Button", GUILayout.Width(50));
                }
                if (preview != null && GUILayout.Button("Clear", GUILayout.Width(50))) ClearPreview();

                if (GUILayout.Button("Reset Settings", GUILayout.Width(100)))
                {
                    foreach (Object t in targets)
                    {
                        Undo.RecordObject(t, "Reset Pixelizer Settings");
                        ((TexturePixelizer)t).settings = new PixelizeSettings();
                        EditorUtility.SetDirty(t);
                    }
                    if (autoPreview) RefreshPreview();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.backgroundColor = new Color(0.6f, 1f, 0.6f);
                if (GUILayout.Button("Apply", GUILayout.Height(28))) ApplyAll();
                GUI.backgroundColor = Color.white;

                if (GUILayout.Button("Revert to Original", GUILayout.Height(28))) RevertAll();
            }
        }

        private void ApplyAll()
        {
            int success = 0;
            foreach (Object t in targets)
            {
                if (PixelizerAssetUtility.Apply((TexturePixelizer)t, out string message))
                {
                    success++;
                    SetMessage(message, MessageType.Info);
                }
                else
                {
                    SetMessage($"{t.name}: {message}", MessageType.Error);
                    Debug.LogError($"[Texture Pixelizer] {t.name}: {message}", t);
                }
            }

            if (targets.Length > 1 && success == targets.Length)
                SetMessage($"Applied to {success} objects.", MessageType.Info);

            AssetDatabase.SaveAssets();
        }

        private void RevertAll()
        {
            foreach (Object t in targets)
            {
                bool ok = PixelizerAssetUtility.Revert((TexturePixelizer)t, out string message);
                SetMessage(targets.Length > 1 ? $"{t.name}: {message}" : message, ok ? MessageType.Info : MessageType.Warning);
            }
            AssetDatabase.SaveAssets();
        }

        private void RefreshPreview()
        {
            var pixelizer = (TexturePixelizer)target;
            Material material = PixelizerAssetUtility.GetMaterial(pixelizer);
            if (PixelizerAssetUtility.ValidateMaterial(material) != null) return;

            string property = PixelizerAssetUtility.ResolveTextureProperty(material, pixelizer.textureProperty);
            if (property == null) return;

            Texture2D source = PixelizerAssetUtility.FindSourceTexture(pixelizer, material, property);
            if (source == null)
            {
                SetMessage("No source texture to preview.", MessageType.Warning);
                return;
            }

            ClearPreview();
            preview = PixelizerAssetUtility.CreatePreview(source, pixelizer.settings, out previewPaletteCount);
        }

        private void DrawPreview()
        {
            if (preview == null) return;

            EditorGUILayout.Space();
            string palette = previewPaletteCount > 0 ? $", {previewPaletteCount} colors" : "";
            EditorGUILayout.LabelField($"Preview ({preview.width}x{preview.height}{palette})", EditorStyles.boldLabel);

            float width = EditorGUIUtility.currentViewWidth - 40f;
            float height = width * preview.height / preview.width;
            Rect rect = GUILayoutUtility.GetRect(width, height, GUILayout.ExpandWidth(true));
            EditorGUI.DrawPreviewTexture(rect, preview, null, ScaleMode.ScaleToFit);
        }

        private void ClearPreview()
        {
            if (preview != null) DestroyImmediate(preview);
            preview = null;
        }

        private void SetMessage(string message, MessageType type)
        {
            lastMessage = message;
            lastMessageType = type;
        }

        // Scanning the scene is expensive, so the result is cached per material.
        private bool IsMaterialShared(TexturePixelizer pixelizer, Material material)
        {
            if (material == sharedCheckMaterial) return sharedCheckResult;
            sharedCheckMaterial = material;
            sharedCheckResult = ScanForSharedMaterial(pixelizer, material);
            return sharedCheckResult;
        }

        private static bool ScanForSharedMaterial(TexturePixelizer pixelizer, Material material)
        {
            foreach (Renderer r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (r.gameObject == pixelizer.gameObject) continue;
                foreach (Material m in r.sharedMaterials)
                    if (m == material) return true;
            }
            return false;
        }
    }
}
