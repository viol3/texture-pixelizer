namespace Viol3.Pixelizer
{
    using System;
    using UnityEngine;
    using UnityEngine.Serialization;

    public enum DownsampleMode
    {
        [Tooltip("Average of all texels in the block. Smooth, slightly muddy.")]
        Average,
        [Tooltip("Real texel from the block closest to its average. Keeps the original texture's character and contrast.")]
        Representative,
        [Tooltip("Center texel of the block. Sharpest, but can alias.")]
        Nearest
    }

    public enum ColorMode
    {
        [Tooltip("No color reduction (only pixelation).")]
        None,
        [Tooltip("Fixed number of levels per RGB channel.")]
        Levels,
        [Tooltip("Palette built from the texture's own colors (k-means).")]
        AdaptivePalette,
        [Tooltip("Keeps hue, splits brightness into bands across the texture's own brightness range. " +
                 "Shows dithering even on near-white or near-flat textures.")]
        ToneBands
    }

    public enum DitherMode
    {
        None,
        Bayer2x2,
        Bayer4x4,
        Bayer8x8,
        [Tooltip("Organic, non-repeating noise pattern.")]
        BlueNoise,
        [Tooltip("Error diffusion. Natural, no visible grid.")]
        FloydSteinberg,
        [Tooltip("Error diffusion with partial error spread. Softer, retro look.")]
        Atkinson
    }

    [Serializable]
    public class PixelizeSettings
    {
        [Header("Pixelation")]
        [Tooltip("Size of one pixel block in source texels. 1 = no pixelation.")]
        [Range(1, 32)] public int pixelSize = 4;
        public DownsampleMode downsampleMode = DownsampleMode.Representative;
        [Tooltip("Keep the low-res result (smaller, faster) or upscale back to the original size.")]
        public bool keepLowResolution = true;

        [Header("Color Adjustments")]
        [Range(-1f, 1f)] public float brightness = 0f;
        [Range(0f, 2f)] public float contrast = 1f;
        [Range(0f, 2f)] public float saturation = 1f;

        [Header("Color Reduction")]
        public ColorMode colorMode = ColorMode.ToneBands;
        [Tooltip("Levels per channel (Levels mode).")]
        [Range(2, 32)] public int levels = 8;
        [Tooltip("Number of palette colors (Adaptive Palette mode).")]
        [Range(2, 256)] public int paletteSize = 16;
        [Tooltip("Number of brightness bands (Tone Bands mode). Fewer = stronger, more visible dithering.")]
        [Range(2, 16)] public int toneBands = 4;
        [Tooltip("Tone Bands: spread the bands over the texture's own brightness range instead of 0-1. " +
                 "Needed for near-white / near-flat textures.")]
        public bool autoRange = true;
        [Tooltip("Tone Bands + Auto Range: minimum brightness range, so almost flat textures don't turn into pure noise.")]
        [Range(0.02f, 1f)] public float minRange = 0.15f;

        [Header("Dithering")]
        public DitherMode ditherMode = DitherMode.Bayer4x4;
        [Tooltip("How much neighbouring tones are mixed. 1 = full dithering between tones, 0 = hard posterized bands.")]
        [Range(0f, 1f)] public float ditherStrength = 1f;
        [Tooltip("Mixes random noise into the pattern to break up the regular grid.")]
        [Range(0f, 1f)] public float patternRandomness = 0.15f;
        [Tooltip("Reduces dithering in busy/detailed areas where the texture's own noise already hides banding. " +
                 "Smooth gradients still get dithered. 0 = uniform dithering everywhere.")]
        [Range(0f, 1f)] public float adaptiveDither = 0f;
        [Tooltip("Local contrast above which an area counts as fully detailed.")]
        [Range(0.01f, 0.5f)] public float detailThreshold = 0.12f;
        public int seed = 0;

        [Header("Alpha")]
        [Tooltip("Snap alpha to fully opaque/transparent.")]
        public bool hardAlpha = false;
        [Range(0f, 1f)] public float alphaCutoff = 0.5f;

        [Header("Import")]
        public bool generateMipmaps = true;
        [Tooltip("Compression can smear the dither pattern. Off is recommended.")]
        public bool compress = false;
    }

    [DisallowMultipleComponent]
    [RequireComponent(typeof(Renderer))]
    [AddComponentMenu("viol3/Texture Pixelizer")]
    public class TexturePixelizer : MonoBehaviour
    {
        public const string DisplayName = "viol3's Texture Pixelizer";
        public const string Version = "0.0.4";

        [Tooltip("Index in the renderer's material list.")]
        public int materialIndex = 0;
        [Tooltip("Texture property to process. Empty = the material's main texture (_BaseMap / _MainTex).")]
        public string textureProperty = "_BaseMap";
        [Tooltip("Texture to pixelate. Every Apply starts from this one, whatever the material currently shows. " +
                 "Empty = taken from the material on the first Apply.")]
        [FormerlySerializedAs("originalTexture")]
        public Texture2D sourceTexture;

        public PixelizeSettings settings = new PixelizeSettings();

        public Texture2D pixelatedTexture;
    }
}
