namespace Viol3.Pixelizer.Editor
{
    using System.IO;
    using UnityEditor;
    using UnityEngine;
    using UnityEngine.Experimental.Rendering;
    using UnityEngine.Rendering;

    public static class PixelizerAssetUtility
    {
        public const string Suffix = "_pixelated";
        private const string FallbackFolder = "Assets/PixelatedTextures";

        public static Material GetMaterial(TexturePixelizer pixelizer)
        {
            var renderer = pixelizer.GetComponent<Renderer>();
            if (renderer == null) return null;

            Material[] materials = renderer.sharedMaterials;
            if (pixelizer.materialIndex < 0 || pixelizer.materialIndex >= materials.Length) return null;
            return materials[pixelizer.materialIndex];
        }

        /// <summary>Explicit property if set, otherwise the shader's [MainTexture], _BaseMap or _MainTex.</summary>
        public static string ResolveTextureProperty(Material material, string overrideName)
        {
            if (material == null) return null;

            if (!string.IsNullOrEmpty(overrideName))
                return material.HasTexture(overrideName) ? overrideName : null;

            Shader shader = material.shader;
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) == ShaderPropertyType.Texture &&
                    (shader.GetPropertyFlags(i) & ShaderPropertyFlags.MainTexture) != 0)
                    return shader.GetPropertyName(i);
            }

            if (material.HasTexture("_BaseMap")) return "_BaseMap";
            if (material.HasTexture("_MainTex")) return "_MainTex";
            return null;
        }

        /// <summary>
        /// The texture to process: the component's Source Texture, or the material's current texture if that is empty.
        /// If the material already shows a _pixelated texture and no source is set, its original is looked up by name.
        /// </summary>
        public static Texture2D FindSourceTexture(TexturePixelizer pixelizer, Material material, string property)
        {
            if (pixelizer.sourceTexture != null) return pixelizer.sourceTexture;

            var current = material.GetTexture(property) as Texture2D;
            if (current == null || !current.name.EndsWith(Suffix)) return current;

            string path = AssetDatabase.GetAssetPath(current);
            string folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string originalName = current.name.Substring(0, current.name.Length - Suffix.Length);
            if (string.IsNullOrEmpty(folder)) return null;

            foreach (string guid in AssetDatabase.FindAssets($"{originalName} t:Texture2D", new[] { folder }))
            {
                var candidate = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(guid));
                if (candidate != null && candidate.name == originalName) return candidate;
            }
            return null;
        }

        /// <summary>Returns an error message if the material cannot be modified, otherwise null.</summary>
        public static string ValidateMaterial(Material material)
        {
            if (material == null) return "No material at this index.";

            string path = AssetDatabase.GetAssetPath(material);
            if (string.IsNullOrEmpty(path)) return null;
            if (!path.StartsWith("Assets/"))
                return $"Material '{material.name}' is read-only (it lives in {path}). Assign an editable material.";
            if (!path.EndsWith(".mat"))
                return $"Material '{material.name}' is embedded in '{path}' and is read-only. " +
                       "Extract it first (model Import Settings > Materials > Extract Materials).";
            return null;
        }

        public static Color32[] ReadPixels(Texture2D texture, out bool isSRGB)
        {
            isSRGB = IsSRGB(texture);

            // Blitting works for any texture, regardless of compression or the Read/Write flag.
            var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32,
                isSRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            RenderTexture previous = RenderTexture.active;
            var readable = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, !isSRGB);
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                readable.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
                return readable.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                Object.DestroyImmediate(readable);
            }
        }

        private static bool IsSRGB(Texture2D texture)
        {
            if (AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) is TextureImporter importer)
                return importer.sRGBTexture;
            return GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
        }

        public static Texture2D CreatePreview(Texture2D source, PixelizeSettings settings, out int paletteCount)
        {
            Color32[] pixels = ReadPixels(source, out bool isSRGB);
            Color32[] result = PixelizeProcessor.Process(pixels, source.width, source.height, settings,
                out int w, out int h, out paletteCount);

            var preview = new Texture2D(w, h, TextureFormat.RGBA32, false, !isSRGB)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            preview.SetPixels32(result);
            preview.Apply();
            return preview;
        }

        /// <summary>Processes the original texture, saves it as [name]_pixelated.png and assigns it to the material.</summary>
        public static bool Apply(TexturePixelizer pixelizer, out string message)
        {
            Material material = GetMaterial(pixelizer);
            message = ValidateMaterial(material);
            if (message != null) return false;

            string property = ResolveTextureProperty(material, pixelizer.textureProperty);
            if (property == null)
            {
                message = $"Material '{material.name}' has no texture property " +
                          (string.IsNullOrEmpty(pixelizer.textureProperty) ? "(main texture)." : $"'{pixelizer.textureProperty}'.");
                return false;
            }

            Texture2D source = FindSourceTexture(pixelizer, material, property);
            if (source == null)
            {
                message = $"No Texture2D found in '{material.name}.{property}'.";
                return false;
            }

            try
            {
                EditorUtility.DisplayProgressBar("Texture Pixelizer", $"Processing {source.name}...", 0.3f);

                Color32[] pixels = ReadPixels(source, out bool isSRGB);
                Color32[] result = PixelizeProcessor.Process(pixels, source.width, source.height, pixelizer.settings,
                    out int width, out int height, out _);

                EditorUtility.DisplayProgressBar("Texture Pixelizer", $"Saving {source.name}{Suffix}...", 0.8f);

                string outputPath = GetOutputPath(source);
                WritePng(outputPath, result, width, height, isSRGB);
                ConfigureImporter(outputPath, source, pixelizer.settings, isSRGB, width, height);

                var pixelated = AssetDatabase.LoadAssetAtPath<Texture2D>(outputPath);

                Undo.RecordObjects(new Object[] { material, pixelizer }, "Apply Texture Pixelizer");
                pixelizer.sourceTexture = source;
                pixelizer.pixelatedTexture = pixelated;
                material.SetTexture(property, pixelated);
                EditorUtility.SetDirty(material);
                EditorUtility.SetDirty(pixelizer);

                message = $"Saved {outputPath} ({width}x{height}).";
                return true;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        public static bool Revert(TexturePixelizer pixelizer, out string message)
        {
            Material material = GetMaterial(pixelizer);
            message = ValidateMaterial(material);
            if (message != null) return false;

            string property = ResolveTextureProperty(material, pixelizer.textureProperty);
            if (property == null || pixelizer.sourceTexture == null)
            {
                message = "Nothing to revert: no source texture set.";
                return false;
            }

            Undo.RecordObject(material, "Revert Texture Pixelizer");
            material.SetTexture(property, pixelizer.sourceTexture);
            EditorUtility.SetDirty(material);
            message = $"Restored {pixelizer.sourceTexture.name}.";
            return true;
        }

        private static string GetOutputPath(Texture2D source)
        {
            string sourcePath = AssetDatabase.GetAssetPath(source);
            string folder = sourcePath.StartsWith("Assets/")
                ? Path.GetDirectoryName(sourcePath).Replace('\\', '/')
                : FallbackFolder;

            if (!AssetDatabase.IsValidFolder(folder))
            {
                Directory.CreateDirectory(ToFullPath(folder));
                AssetDatabase.Refresh();
            }

            string fileName = source.name;
            foreach (char c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');
            return $"{folder}/{fileName}{Suffix}.png";
        }

        private static void WritePng(string assetPath, Color32[] pixels, int width, int height, bool isSRGB)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, !isSRGB);
            try
            {
                texture.SetPixels32(pixels);
                texture.Apply();
                File.WriteAllBytes(ToFullPath(assetPath), texture.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        }

        private static void ConfigureImporter(string assetPath, Texture2D source, PixelizeSettings settings,
                                              bool isSRGB, int width, int height)
        {
            if (!(AssetImporter.GetAtPath(assetPath) is TextureImporter importer)) return;
            var sourceImporter = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(source)) as TextureImporter;

            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = isSRGB;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = sourceImporter != null && sourceImporter.alphaIsTransparency;
            importer.wrapModeU = source.wrapModeU;
            importer.wrapModeV = source.wrapModeV;
            importer.filterMode = FilterMode.Point;
            importer.mipmapEnabled = settings.generateMipmaps;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(width, height)), 32, 16384);
            importer.textureCompression = settings.compress
                ? TextureImporterCompression.Compressed
                : TextureImporterCompression.Uncompressed;
            importer.isReadable = false;
            importer.SaveAndReimport();
        }

        private static string ToFullPath(string assetPath) =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, assetPath);
    }
}
