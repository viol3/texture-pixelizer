namespace Viol3.Pixelizer
{
    using System;

    /// <summary>Ready-made looks. Use with <see cref="PixelizePresets.Create"/> as a starting point for scripts.</summary>
    public enum PixelizePreset
    {
        /// <summary>The component defaults: Tone Bands (4), Bayer 4x4, pixel size 4.</summary>
        Default,
        /// <summary>Light touch: small pixels, more bands, softer pattern.</summary>
        Subtle,
        /// <summary>Clearly visible dithering: 3 bands, Bayer 4x4, full strength.</summary>
        Strong,
        /// <summary>Big blocks, few bands. Very low-res retro look.</summary>
        Chunky,
        /// <summary>Blue noise with extra randomness. Organic, hand-made look without a visible grid.</summary>
        Organic,
        /// <summary>16-color adaptive palette with Bayer dithering.</summary>
        LimitedPalette
    }

    public static class PixelizePresets
    {
        /// <summary>Returns a new <see cref="PixelizeSettings"/> instance for the preset. Safe to modify.</summary>
        public static PixelizeSettings Create(PixelizePreset preset)
        {
            var s = new PixelizeSettings();
            switch (preset)
            {
                case PixelizePreset.Default:
                    break;
                case PixelizePreset.Subtle:
                    s.pixelSize = 2;
                    s.toneBands = 6;
                    s.ditherMode = DitherMode.Bayer8x8;
                    s.ditherStrength = 0.7f;
                    s.patternRandomness = 0.1f;
                    break;
                case PixelizePreset.Strong:
                    s.toneBands = 3;
                    s.ditherMode = DitherMode.Bayer4x4;
                    s.ditherStrength = 1f;
                    s.patternRandomness = 0f;
                    break;
                case PixelizePreset.Chunky:
                    s.pixelSize = 8;
                    s.toneBands = 3;
                    s.ditherMode = DitherMode.Bayer2x2;
                    break;
                case PixelizePreset.Organic:
                    s.ditherMode = DitherMode.BlueNoise;
                    s.patternRandomness = 0.35f;
                    break;
                case PixelizePreset.LimitedPalette:
                    s.colorMode = ColorMode.AdaptivePalette;
                    s.paletteSize = 16;
                    s.ditherMode = DitherMode.Bayer4x4;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(preset), preset, null);
            }
            return s;
        }
    }
}
