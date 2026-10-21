namespace Viol3.Pixelizer.Editor
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Reflection;
    using UnityEditor;
    using UnityEngine;
    using Object = UnityEngine.Object;

    /// <summary>Outcome of one pixelize / revert operation.</summary>
    public class PixelizeResult
    {
        public bool Success;
        /// <summary>
        /// Not an error: there was nothing to do (e.g. a flat-color material without a texture, or reverting
        /// something that is not pixelated). <see cref="Success"/> is false.
        /// </summary>
        public bool Skipped;
        /// <summary>Human-readable result or error. Always set.</summary>
        public string Message;
        /// <summary>Material that was changed. Null for texture-only operations.</summary>
        public Material Material;
        /// <summary>Shader texture property that was changed, e.g. "_BaseMap".</summary>
        public string Property;
        /// <summary>The original texture (never modified).</summary>
        public Texture2D Source;
        /// <summary>The generated [name]_pixelated.png asset.</summary>
        public Texture2D Pixelated;
        /// <summary>Set by <see cref="PixelizerApi.AddPixelizerComponent"/>.</summary>
        public TexturePixelizer Component;

        public string OutputPath => Pixelated != null ? AssetDatabase.GetAssetPath(Pixelated) : null;

        public override string ToString() => (Success ? "OK: " : Skipped ? "SKIPPED: " : "FAILED: ") + Message;
    }

    /// <summary>
    /// Scripting entry point. Everything the inspector does, without the inspector.
    /// All methods are editor-only, save their changes to disk and never modify the original texture.
    /// <code>
    /// PixelizerApi.PixelizeGameObject(go, PixelizePresets.Create(PixelizePreset.Strong));
    /// PixelizerApi.PixelizeAsset("Assets/Models/Tree.prefab");
    /// PixelizerApi.RevertGameObject(go);
    /// </code>
    /// </summary>
    public static class PixelizerApi
    {
        private const string LogPrefix = "[Texture Pixelizer] ";

        #region Pixelize

        /// <summary>
        /// Saves a pixelated copy of the texture as [name]_pixelated.png next to it. No material is changed.
        /// </summary>
        public static PixelizeResult PixelizeTexture(Texture2D source, PixelizeSettings settings = null)
        {
            settings = settings ?? new PixelizeSettings();
            bool ok = PixelizerAssetUtility.PixelizeTextureAsset(source, settings, out Texture2D pixelated, out string message);
            AssetDatabase.SaveAssets();
            return new PixelizeResult { Success = ok, Message = message, Source = source, Pixelated = pixelated };
        }

        /// <summary>
        /// Pixelates the material's texture and assigns the result to the material.
        /// If the material already shows a _pixelated texture, it starts again from the original, so calling this
        /// repeatedly with different settings is safe.
        /// </summary>
        /// <param name="textureProperty">Shader property, e.g. "_BaseMap". Null/empty = the shader's main texture.</param>
        public static PixelizeResult PixelizeMaterial(Material material, PixelizeSettings settings = null,
                                                      string textureProperty = null)
        {
            PixelizeResult result = PixelizeMaterialInternal(material, settings ?? new PixelizeSettings(), textureProperty,
                new Dictionary<Texture2D, Texture2D>());
            AssetDatabase.SaveAssets();
            return result;
        }

        /// <summary>
        /// Pixelates every material on every Renderer of <paramref name="root"/> (and its children).
        /// Works for scene objects and for prefab / model assets. Materials without an image texture are reported
        /// as failed results and skipped; the rest are still processed.
        /// </summary>
        public static List<PixelizeResult> PixelizeGameObject(GameObject root, PixelizeSettings settings = null,
                                                              bool includeChildren = true)
        {
            var results = new List<PixelizeResult>();
            if (root == null)
            {
                results.Add(Fail("GameObject is null."));
                return results;
            }

            settings = settings ?? new PixelizeSettings();
            var cache = new Dictionary<Texture2D, Texture2D>();
            foreach (Material material in CollectMaterials(root, includeChildren))
                results.Add(PixelizeMaterialInternal(material, settings, null, cache));

            if (results.Count == 0) results.Add(Fail($"'{root.name}' has no Renderer with a material."));
            AssetDatabase.SaveAssets();
            return results;
        }

        /// <summary>
        /// Pixelates an asset by path. Accepts a texture (.png, .jpg, ...), a material (.mat),
        /// a prefab or model (.prefab, .fbx, ...) or a folder (every material inside it).
        /// </summary>
        public static List<PixelizeResult> PixelizeAsset(string assetPath, PixelizeSettings settings = null)
        {
            settings = settings ?? new PixelizeSettings();

            if (AssetDatabase.IsValidFolder(assetPath))
            {
                var results = new List<PixelizeResult>();
                var cache = new Dictionary<Texture2D, Texture2D>();
                foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { assetPath }))
                {
                    var material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                    if (material != null) results.Add(PixelizeMaterialInternal(material, settings, null, cache));
                }
                if (results.Count == 0) results.Add(Fail($"No materials found in folder '{assetPath}'."));
                AssetDatabase.SaveAssets();
                return results;
            }

            Object asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            switch (asset)
            {
                case Texture2D texture: return new List<PixelizeResult> { PixelizeTexture(texture, settings) };
                case Material material: return new List<PixelizeResult> { PixelizeMaterial(material, settings) };
                case GameObject go: return PixelizeGameObject(go, settings);
                case null: return new List<PixelizeResult> { Fail($"No asset at '{assetPath}'.") };
                default:
                    return new List<PixelizeResult>
                        { Fail($"Unsupported asset type {asset.GetType().Name} at '{assetPath}'.") };
            }
        }

        /// <summary>
        /// Adds (or reuses) a <see cref="TexturePixelizer"/> component on a scene object and optionally applies it.
        /// Use this when the user should be able to tweak the result later in the inspector.
        /// The texture property is set to auto-detect, so it works in any render pipeline.
        /// </summary>
        public static PixelizeResult AddPixelizerComponent(GameObject go, PixelizeSettings settings = null,
                                                           int materialIndex = 0, bool apply = true)
        {
            if (go == null) return Fail("GameObject is null.");
            if (go.GetComponent<Renderer>() == null) return Fail($"'{go.name}' has no Renderer.");

            var pixelizer = go.GetComponent<TexturePixelizer>();
            if (pixelizer == null)
            {
                pixelizer = Undo.AddComponent<TexturePixelizer>(go);
                pixelizer.textureProperty = "";
            }
            else
            {
                Undo.RecordObject(pixelizer, "Configure Texture Pixelizer");
            }

            pixelizer.materialIndex = materialIndex;
            if (settings != null) pixelizer.settings = settings;
            EditorUtility.SetDirty(pixelizer);

            var result = new PixelizeResult { Success = true, Message = "Component added.", Component = pixelizer };
            if (!apply) return result;

            result.Success = PixelizerAssetUtility.Apply(pixelizer, out result.Message);
            result.Material = PixelizerAssetUtility.GetMaterial(pixelizer);
            result.Source = pixelizer.sourceTexture;
            result.Pixelated = pixelizer.pixelatedTexture;
            AssetDatabase.SaveAssets();
            return result;
        }

        #endregion

        #region Revert

        /// <summary>Puts the original texture back on the material if it currently shows a _pixelated texture.</summary>
        public static PixelizeResult RevertMaterial(Material material, string textureProperty = null)
        {
            PixelizeResult result = RevertMaterialInternal(material, textureProperty);
            AssetDatabase.SaveAssets();
            return result;
        }

        /// <summary>Reverts every material on every Renderer of <paramref name="root"/> (and its children).</summary>
        public static List<PixelizeResult> RevertGameObject(GameObject root, bool includeChildren = true)
        {
            var results = new List<PixelizeResult>();
            if (root == null)
            {
                results.Add(Fail("GameObject is null."));
                return results;
            }

            foreach (Material material in CollectMaterials(root, includeChildren))
                results.Add(RevertMaterialInternal(material, null));
            AssetDatabase.SaveAssets();
            return results;
        }

        /// <summary>Reverts an asset by path. Accepts the same kinds of paths as <see cref="PixelizeAsset"/>, except textures.</summary>
        public static List<PixelizeResult> RevertAsset(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath))
            {
                var results = AssetDatabase.FindAssets("t:Material", new[] { assetPath })
                    .Select(guid => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid)))
                    .Where(m => m != null)
                    .Select(m => RevertMaterialInternal(m, null))
                    .ToList();
                AssetDatabase.SaveAssets();
                return results;
            }

            switch (AssetDatabase.LoadMainAssetAtPath(assetPath))
            {
                case Material material: return new List<PixelizeResult> { RevertMaterial(material) };
                case GameObject go: return RevertGameObject(go);
                case null: return new List<PixelizeResult> { Fail($"No asset at '{assetPath}'.") };
                default: return new List<PixelizeResult> { Fail($"Cannot revert '{assetPath}': not a material, prefab, model or folder.") };
            }
        }

        #endregion

        #region Settings

        /// <summary>
        /// Overrides settings fields from a "name=value" list separated by ',' or ';', e.g.
        /// "pixelSize=6, ditherMode=BlueNoise, toneBands=3". Field names are the public fields of
        /// <see cref="PixelizeSettings"/> (case-insensitive); enums are given by name. Throws on unknown names.
        /// </summary>
        public static PixelizeSettings ApplyOverrides(PixelizeSettings settings, string overrides)
        {
            if (string.IsNullOrWhiteSpace(overrides)) return settings;

            FieldInfo[] fields = typeof(PixelizeSettings).GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (string pair in overrides.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = pair.Split('=');
                if (parts.Length != 2) throw new ArgumentException($"Expected name=value, got '{pair.Trim()}'.");

                string name = parts[0].Trim(), value = parts[1].Trim();
                FieldInfo field = fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                if (field == null)
                    throw new ArgumentException($"Unknown setting '{name}'. Valid: {string.Join(", ", fields.Select(f => f.Name))}.");

                Type type = field.FieldType;
                object parsed = type.IsEnum ? Enum.Parse(type, value, true)
                    : type == typeof(bool) ? bool.Parse(value)
                    : Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
                field.SetValue(settings, parsed);
            }
            return settings;
        }

        #endregion

        #region Command line

        /// <summary>
        /// Batch-mode entry point. Example:
        /// <code>
        /// Unity -batchmode -quit -projectPath &lt;project&gt; -logFile -
        ///   -executeMethod Viol3.Pixelizer.Editor.PixelizerApi.PixelizeFromCommandLine
        ///   -pixelizeTargets "Assets/Models/Tree.prefab;Assets/Materials/Rock.mat"
        ///   -pixelizePreset Strong
        ///   -pixelizeSettings "pixelSize=6,ditherMode=BlueNoise"
        /// </code>
        /// Add -pixelizeRevert to revert instead. Exits with code 1 if any target failed (skipped ones do not count).
        /// </summary>
        public static void PixelizeFromCommandLine()
        {
            int failures = 0;
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                string targets = GetArg(args, "-pixelizeTargets");
                if (string.IsNullOrEmpty(targets))
                    throw new ArgumentException("Missing -pixelizeTargets \"path1;path2\".");

                bool revert = args.Contains("-pixelizeRevert");
                PixelizeSettings settings = null;
                if (!revert)
                {
                    string presetName = GetArg(args, "-pixelizePreset");
                    var preset = string.IsNullOrEmpty(presetName)
                        ? PixelizePreset.Default
                        : (PixelizePreset)Enum.Parse(typeof(PixelizePreset), presetName, true);
                    settings = ApplyOverrides(PixelizePresets.Create(preset), GetArg(args, "-pixelizeSettings"));
                }

                foreach (string target in targets.Split(';').Select(t => t.Trim()).Where(t => t.Length > 0))
                {
                    foreach (PixelizeResult result in revert ? RevertAsset(target) : PixelizeAsset(target, settings))
                    {
                        if (result.Success || result.Skipped) Debug.Log(LogPrefix + target + ": " + result);
                        else
                        {
                            failures++;
                            Debug.LogError(LogPrefix + target + ": " + result);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                failures++;
                Debug.LogError(LogPrefix + e.Message);
            }

            if (Application.isBatchMode) EditorApplication.Exit(failures > 0 ? 1 : 0);
        }

        private static string GetArg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        #endregion

        #region Internals

        private static PixelizeResult PixelizeMaterialInternal(Material material, PixelizeSettings settings,
                                                               string textureProperty,
                                                               Dictionary<Texture2D, Texture2D> cache)
        {
            var result = new PixelizeResult { Material = material };
            if (!ResolveMaterial(material, textureProperty, result, out string property)) return result;

            Texture2D source = PixelizerAssetUtility.FindSourceTexture(material, property);
            result.Source = source;
            if (source == null)
            {
                result.Skipped = true;
                result.Message = $"'{material.name}.{property}' has no image texture (flat color materials are not supported).";
                return result;
            }

            // Several materials can share one texture; process it only once.
            if (cache.TryGetValue(source, out Texture2D pixelated))
            {
                Undo.RecordObject(material, "Apply Texture Pixelizer");
                material.SetTexture(property, pixelated);
                EditorUtility.SetDirty(material);
                result.Success = true;
                result.Message = $"Reused {AssetDatabase.GetAssetPath(pixelated)}.";
            }
            else
            {
                result.Success = PixelizerAssetUtility.ApplyToMaterial(material, property, source, settings,
                    out pixelated, out result.Message);
                if (result.Success) cache[source] = pixelated;
            }

            result.Pixelated = pixelated;
            if (result.Success) result.Message = $"{material.name}: {result.Message}";
            return result;
        }

        private static PixelizeResult RevertMaterialInternal(Material material, string textureProperty)
        {
            var result = new PixelizeResult { Material = material };
            if (!ResolveMaterial(material, textureProperty, result, out string property)) return result;

            var current = material.GetTexture(property) as Texture2D;
            if (current == null || !current.name.EndsWith(PixelizerAssetUtility.Suffix))
            {
                result.Skipped = true;
                result.Message = $"'{material.name}' is not pixelated.";
                return result;
            }

            Texture2D source = PixelizerAssetUtility.FindSourceTexture(material, property);
            if (source == null)
            {
                result.Message = $"Original texture for '{current.name}' not found next to it.";
                return result;
            }

            Undo.RecordObject(material, "Revert Texture Pixelizer");
            material.SetTexture(property, source);
            EditorUtility.SetDirty(material);
            result.Success = true;
            result.Source = source;
            result.Message = $"{material.name}: restored {source.name}.";
            return result;
        }

        private static bool ResolveMaterial(Material material, string textureProperty, PixelizeResult result,
                                            out string property)
        {
            property = null;
            result.Message = PixelizerAssetUtility.ValidateMaterial(material);
            if (result.Message != null) return false;

            property = PixelizerAssetUtility.ResolveTextureProperty(material, textureProperty);
            result.Property = property;
            if (property != null) return true;

            result.Skipped = string.IsNullOrEmpty(textureProperty);
            result.Message = $"Material '{material.name}' has no texture property " +
                             (string.IsNullOrEmpty(textureProperty) ? "(main texture)." : $"'{textureProperty}'.");
            return false;
        }

        private static IEnumerable<Material> CollectMaterials(GameObject root, bool includeChildren)
        {
            Renderer[] renderers = includeChildren
                ? root.GetComponentsInChildren<Renderer>(true)
                : root.GetComponents<Renderer>();
            return renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct();
        }

        private static PixelizeResult Fail(string message) => new PixelizeResult { Success = false, Message = message };

        #endregion
    }
}
