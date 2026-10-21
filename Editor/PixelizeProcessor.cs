namespace Viol3.Pixelizer.Editor
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;

    /// <summary>
    /// CPU implementation of the dithered pixelation effect.
    /// All math runs on the stored (gamma encoded) values, which are close to perceptually uniform.
    /// </summary>
    public static class PixelizeProcessor
    {
        private static readonly Vector3 LumaWeights = new Vector3(0.299f, 0.587f, 0.114f);
        // Weights for palette distance: green matters most, blue least.
        private const float WR = 2f, WG = 4f, WB = 3f;

        private const int BlueNoiseSize = 64;
        private static float[] blueNoise;

        public static Color32[] Process(Color32[] src, int width, int height, PixelizeSettings s,
                                        out int outWidth, out int outHeight, out int paletteCount)
        {
            int pixelSize = Mathf.Max(1, s.pixelSize);
            int lw = Mathf.Max(1, Mathf.RoundToInt(width / (float)pixelSize));
            int lh = Mathf.Max(1, Mathf.RoundToInt(height / (float)pixelSize));

            Color[] low = Downsample(src, width, height, lw, lh, s.downsampleMode);
            AdjustColors(low, s);

            Color[] palette = null;
            float toneMin = 0f, toneRange = 1f;
            float spread = 0f;
            switch (s.colorMode)
            {
                case ColorMode.Levels:
                    spread = 1f / (Mathf.Max(2, s.levels) - 1);
                    break;
                case ColorMode.AdaptivePalette:
                    palette = BuildPalette(low, Mathf.Clamp(s.paletteSize, 2, 256), s.seed);
                    spread = EstimatePaletteSpacing(palette);
                    break;
                case ColorMode.ToneBands:
                    if (s.autoRange) FindToneRange(low, s.minRange, out toneMin, out toneRange);
                    spread = toneRange / (Mathf.Max(2, s.toneBands) - 1);
                    break;
            }
            paletteCount = palette != null ? palette.Length : 0;

            bool dither = s.colorMode != ColorMode.None && s.ditherMode != DitherMode.None && s.ditherStrength > 0f;
            float[] ditherFactor = dither ? BuildDitherFactors(low, lw, lh, s) : null;

            var quantizer = new Quantizer(s, palette, toneMin, toneRange);
            if (s.colorMode != ColorMode.None)
            {
                if (dither && (s.ditherMode == DitherMode.FloydSteinberg || s.ditherMode == DitherMode.Atkinson))
                    ErrorDiffusion(low, lw, lh, s, quantizer, ditherFactor, spread);
                else
                    OrderedDither(low, lw, lh, s, quantizer, dither ? ditherFactor : null);
            }

            if (s.hardAlpha)
            {
                for (int i = 0; i < low.Length; i++)
                    low[i].a = low[i].a >= s.alphaCutoff ? 1f : 0f;
            }

            if (s.keepLowResolution || pixelSize == 1)
            {
                outWidth = lw;
                outHeight = lh;
                return ToColor32(low);
            }

            outWidth = width;
            outHeight = height;
            return Upscale(low, lw, lh, width, height);
        }

        #region Downsampling

        private static Color[] Downsample(Color32[] src, int w, int h, int lw, int lh, DownsampleMode mode)
        {
            var result = new Color[lw * lh];

            for (int by = 0; by < lh; by++)
            {
                int y0 = by * h / lh;
                int y1 = Mathf.Max(y0 + 1, (by + 1) * h / lh);

                for (int bx = 0; bx < lw; bx++)
                {
                    int x0 = bx * w / lw;
                    int x1 = Mathf.Max(x0 + 1, (bx + 1) * w / lw);

                    if (mode == DownsampleMode.Nearest)
                    {
                        result[by * lw + bx] = src[((y0 + y1) / 2) * w + (x0 + x1) / 2];
                        continue;
                    }

                    float r = 0, g = 0, b = 0, a = 0;
                    int count = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        int row = y * w;
                        for (int x = x0; x < x1; x++)
                        {
                            Color32 c = src[row + x];
                            r += c.r; g += c.g; b += c.b; a += c.a;
                            count++;
                        }
                    }
                    float inv = 1f / (255f * count);
                    var mean = new Color(r * inv, g * inv, b * inv, a * inv);

                    if (mode == DownsampleMode.Average)
                    {
                        result[by * lw + bx] = mean;
                        continue;
                    }

                    // Representative: the real texel closest to the block average.
                    float best = float.MaxValue;
                    Color32 bestColor = src[y0 * w + x0];
                    for (int y = y0; y < y1; y++)
                    {
                        int row = y * w;
                        for (int x = x0; x < x1; x++)
                        {
                            Color32 c = src[row + x];
                            float dr = c.r / 255f - mean.r;
                            float dg = c.g / 255f - mean.g;
                            float db = c.b / 255f - mean.b;
                            float da = c.a / 255f - mean.a;
                            float d = WR * dr * dr + WG * dg * dg + WB * db * db + 4f * da * da;
                            if (d < best)
                            {
                                best = d;
                                bestColor = c;
                            }
                        }
                    }
                    result[by * lw + bx] = bestColor;
                }
            }
            return result;
        }

        private static Color32[] Upscale(Color[] low, int lw, int lh, int w, int h)
        {
            var result = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                int ly = Mathf.Min(lh - 1, y * lh / h);
                for (int x = 0; x < w; x++)
                {
                    int lx = Mathf.Min(lw - 1, x * lw / w);
                    result[y * w + x] = low[ly * lw + lx];
                }
            }
            return result;
        }

        private static Color32[] ToColor32(Color[] colors)
        {
            var result = new Color32[colors.Length];
            for (int i = 0; i < colors.Length; i++)
                result[i] = colors[i];
            return result;
        }

        #endregion

        #region Color adjustments

        private static void AdjustColors(Color[] colors, PixelizeSettings s)
        {
            if (Mathf.Approximately(s.brightness, 0f) && Mathf.Approximately(s.contrast, 1f) &&
                Mathf.Approximately(s.saturation, 1f))
                return;

            for (int i = 0; i < colors.Length; i++)
            {
                Color c = colors[i];
                c.r = (c.r - 0.5f) * s.contrast + 0.5f + s.brightness;
                c.g = (c.g - 0.5f) * s.contrast + 0.5f + s.brightness;
                c.b = (c.b - 0.5f) * s.contrast + 0.5f + s.brightness;

                float l = Luma(c);
                c.r = Mathf.Clamp01(l + (c.r - l) * s.saturation);
                c.g = Mathf.Clamp01(l + (c.g - l) * s.saturation);
                c.b = Mathf.Clamp01(l + (c.b - l) * s.saturation);
                colors[i] = c;
            }
        }

        private static float Luma(Color c) => c.r * LumaWeights.x + c.g * LumaWeights.y + c.b * LumaWeights.z;

        /// <summary>
        /// Brightness range of the texture (1st to 99th percentile), so bands follow the texture's own tones.
        /// A white bathtub with soft shading gets several bands instead of collapsing into one.
        /// </summary>
        private static void FindToneRange(Color[] colors, float minRange, out float min, out float range)
        {
            const int bins = 1024;
            var histogram = new int[bins];
            int total = 0;
            foreach (Color c in colors)
            {
                if (c.a <= 0.004f) continue;
                histogram[Mathf.Clamp((int)(Luma(c) * (bins - 1)), 0, bins - 1)]++;
                total++;
            }

            if (total == 0)
            {
                min = 0f;
                range = 1f;
                return;
            }

            float lo = Percentile(histogram, total, 0.01f, bins);
            float hi = Percentile(histogram, total, 0.99f, bins);

            minRange = Mathf.Clamp(minRange, 0.01f, 1f);
            if (hi - lo < minRange)
            {
                float center = (lo + hi) * 0.5f;
                lo = Mathf.Clamp(center - minRange * 0.5f, 0f, 1f - minRange);
                hi = lo + minRange;
            }

            min = lo;
            range = hi - lo;
        }

        private static float Percentile(int[] histogram, int total, float percentile, int bins)
        {
            int target = Mathf.CeilToInt(total * percentile);
            int accumulated = 0;
            for (int i = 0; i < bins; i++)
            {
                accumulated += histogram[i];
                if (accumulated >= target) return (i + 0.5f) / bins;
            }
            return 1f;
        }

        #endregion

        #region Adaptive dither factors

        /// <summary>
        /// Per pixel dither multiplier. Busy areas (high local contrast) get less dithering,
        /// because the texture's own noise already hides banding there and extra dither only adds a fake grid.
        /// </summary>
        private static float[] BuildDitherFactors(Color[] colors, int w, int h, PixelizeSettings s)
        {
            var factors = new float[colors.Length];
            float baseStrength = s.ditherStrength;

            if (s.adaptiveDither <= 0f)
            {
                for (int i = 0; i < factors.Length; i++) factors[i] = baseStrength;
                return factors;
            }

            var luma = new float[colors.Length];
            for (int i = 0; i < colors.Length; i++) luma[i] = Luma(colors[i]);

            float hi = Mathf.Max(0.0001f, s.detailThreshold);
            float lo = hi * 0.25f;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f, sumSq = 0f;
                    for (int oy = -1; oy <= 1; oy++)
                    {
                        int yy = Mathf.Clamp(y + oy, 0, h - 1);
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            int xx = Mathf.Clamp(x + ox, 0, w - 1);
                            float l = luma[yy * w + xx];
                            sum += l;
                            sumSq += l * l;
                        }
                    }
                    float mean = sum / 9f;
                    float stdDev = Mathf.Sqrt(Mathf.Max(0f, sumSq / 9f - mean * mean));
                    float detail = SmoothStep(lo, hi, stdDev);
                    factors[y * w + x] = baseStrength * (1f - s.adaptiveDither * detail);
                }
            }
            return factors;
        }

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        #endregion

        #region Dithering

        private static void OrderedDither(Color[] colors, int w, int h, PixelizeSettings s, Quantizer quantizer,
                                          float[] factors)
        {
            int matrixSize = 0;
            float[] matrix = null;
            switch (s.ditherMode)
            {
                case DitherMode.Bayer2x2: matrixSize = 2; break;
                case DitherMode.Bayer4x4: matrixSize = 4; break;
                case DitherMode.Bayer8x8: matrixSize = 8; break;
            }
            if (matrixSize > 0) matrix = BuildBayer(matrixSize);
            if (s.ditherMode == DitherMode.BlueNoise) EnsureBlueNoise();

            int offsetX = Hash(s.seed, 17) & 63;
            int offsetY = Hash(s.seed, 31) & 63;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    Color c = colors[i];

                    if (factors == null || c.a <= 0.004f)
                    {
                        colors[i] = quantizer.Quantize(c);
                        continue;
                    }

                    float threshold;
                    if (matrix != null)
                    {
                        threshold = matrix[(y % matrixSize) * matrixSize + (x % matrixSize)];
                        threshold = Mathf.Lerp(threshold, HashFloat(x, y, s.seed), s.patternRandomness);
                    }
                    else
                    {
                        threshold = blueNoise[((y + offsetY) & (BlueNoiseSize - 1)) * BlueNoiseSize +
                                              ((x + offsetX) & (BlueNoiseSize - 1))];
                        threshold = Mathf.Lerp(threshold, HashFloat(x, y, s.seed), s.patternRandomness * 0.5f);
                    }

                    colors[i] = quantizer.Quantize(c, threshold, factors[i]);
                }
            }
        }

        private static void ErrorDiffusion(Color[] colors, int w, int h, PixelizeSettings s, Quantizer quantizer,
                                           float[] factors, float spread)
        {
            bool atkinson = s.ditherMode == DitherMode.Atkinson;

            for (int y = 0; y < h; y++)
            {
                // Serpentine scan avoids directional "worm" artifacts.
                bool reverse = (y & 1) == 1;
                int dir = reverse ? -1 : 1;

                for (int n = 0; n < w; n++)
                {
                    int x = reverse ? w - 1 - n : n;
                    int i = y * w + x;
                    Color old = colors[i];

                    if (old.a <= 0.004f)
                    {
                        colors[i] = quantizer.Quantize(old);
                        continue;
                    }

                    // A little jitter breaks up the regular patterns error diffusion makes in flat areas.
                    if (s.patternRandomness > 0f)
                    {
                        float jitter = (HashFloat(x, y, s.seed) - 0.5f) * spread * 0.5f * s.patternRandomness;
                        old.r += jitter;
                        old.g += jitter;
                        old.b += jitter;
                    }

                    old.r = Mathf.Clamp(old.r, -0.5f, 1.5f);
                    old.g = Mathf.Clamp(old.g, -0.5f, 1.5f);
                    old.b = Mathf.Clamp(old.b, -0.5f, 1.5f);

                    Color q = quantizer.Quantize(old);
                    colors[i] = q;

                    float f = factors[i];
                    float er = (old.r - q.r) * f;
                    float eg = (old.g - q.g) * f;
                    float eb = (old.b - q.b) * f;

                    if (atkinson)
                    {
                        const float k = 1f / 8f;
                        Spread(colors, w, h, x + dir, y, er, eg, eb, k);
                        Spread(colors, w, h, x + 2 * dir, y, er, eg, eb, k);
                        Spread(colors, w, h, x - dir, y + 1, er, eg, eb, k);
                        Spread(colors, w, h, x, y + 1, er, eg, eb, k);
                        Spread(colors, w, h, x + dir, y + 1, er, eg, eb, k);
                        Spread(colors, w, h, x, y + 2, er, eg, eb, k);
                    }
                    else
                    {
                        Spread(colors, w, h, x + dir, y, er, eg, eb, 7f / 16f);
                        Spread(colors, w, h, x - dir, y + 1, er, eg, eb, 3f / 16f);
                        Spread(colors, w, h, x, y + 1, er, eg, eb, 5f / 16f);
                        Spread(colors, w, h, x + dir, y + 1, er, eg, eb, 1f / 16f);
                    }
                }
            }
        }

        private static void Spread(Color[] colors, int w, int h, int x, int y, float er, float eg, float eb, float k)
        {
            if (x < 0 || x >= w || y >= h) return;
            int i = y * w + x;
            Color c = colors[i];
            c.r += er * k;
            c.g += eg * k;
            c.b += eb * k;
            colors[i] = c;
        }

        private static float[] BuildBayer(int size)
        {
            int[] m = { 0 };
            int n = 1;
            while (n < size)
            {
                int n2 = n * 2;
                var next = new int[n2 * n2];
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        int v = m[y * n + x] * 4;
                        next[y * n2 + x] = v;
                        next[y * n2 + x + n] = v + 2;
                        next[(y + n) * n2 + x] = v + 3;
                        next[(y + n) * n2 + x + n] = v + 1;
                    }
                }
                m = next;
                n = n2;
            }

            var result = new float[m.Length];
            for (int i = 0; i < m.Length; i++)
                result[i] = (m[i] + 0.5f) / m.Length;
            return result;
        }

        /// <summary>
        /// Builds a tileable blue noise threshold map by repeatedly placing a point in the largest void
        /// (simplified void-and-cluster). Generated once per editor session.
        /// </summary>
        private static void EnsureBlueNoise()
        {
            if (blueNoise != null) return;

            const int size = BlueNoiseSize;
            const int count = size * size;
            const float sigma = 1.9f;
            const int radius = 6;

            var kernel = new float[(radius * 2 + 1) * (radius * 2 + 1)];
            for (int y = -radius; y <= radius; y++)
                for (int x = -radius; x <= radius; x++)
                    kernel[(y + radius) * (radius * 2 + 1) + x + radius] = Mathf.Exp(-(x * x + y * y) / (2f * sigma * sigma));

            var energy = new float[count];
            var filled = new bool[count];
            var rank = new float[count];
            var random = new System.Random(1234);

            // Tiny random bias so ties don't produce a regular placement order.
            for (int i = 0; i < count; i++) energy[i] = (float)random.NextDouble() * 1e-4f;

            for (int r = 0; r < count; r++)
            {
                int bestIndex = -1;
                float bestEnergy = float.MaxValue;
                for (int i = 0; i < count; i++)
                {
                    if (!filled[i] && energy[i] < bestEnergy)
                    {
                        bestEnergy = energy[i];
                        bestIndex = i;
                    }
                }

                filled[bestIndex] = true;
                rank[bestIndex] = (r + 0.5f) / count;

                int px = bestIndex % size, py = bestIndex / size;
                for (int y = -radius; y <= radius; y++)
                {
                    int yy = (py + y + size) % size;
                    for (int x = -radius; x <= radius; x++)
                    {
                        int xx = (px + x + size) % size;
                        energy[yy * size + xx] += kernel[(y + radius) * (radius * 2 + 1) + x + radius];
                    }
                }
            }

            blueNoise = rank;
        }

        private static int Hash(int a, int b)
        {
            unchecked
            {
                uint h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u;
                h ^= h >> 15;
                h *= 0x2C1B3C6Du;
                h ^= h >> 12;
                h *= 0x297A2D39u;
                h ^= h >> 15;
                return (int)(h & 0x7FFFFFFF);
            }
        }

        private static float HashFloat(int x, int y, int seed) => (Hash(Hash(x, y), seed) & 0xFFFFFF) / 16777216f;

        #endregion

        #region Palette

        /// <summary>K-means++ palette from the texture's own colors.</summary>
        private static Color[] BuildPalette(Color[] colors, int k, int seed)
        {
            var random = new System.Random(seed);

            var samples = new List<Vector3>();
            const int maxSamples = 20000;
            int step = Mathf.Max(1, colors.Length / maxSamples);
            for (int i = random.Next(step); i < colors.Length; i += step)
            {
                Color c = colors[i];
                if (c.a > 0.004f) samples.Add(new Vector3(c.r, c.g, c.b));
            }
            if (samples.Count == 0) return new[] { Color.black, Color.white };

            k = Mathf.Min(k, samples.Count);
            var centers = new Vector3[k];

            // K-means++ seeding.
            centers[0] = samples[random.Next(samples.Count)];
            var minDist = new float[samples.Count];
            for (int i = 0; i < samples.Count; i++) minDist[i] = Distance(samples[i], centers[0]);

            for (int c = 1; c < k; c++)
            {
                double total = 0;
                for (int i = 0; i < samples.Count; i++) total += minDist[i];

                int chosen = samples.Count - 1;
                if (total > 0)
                {
                    double target = random.NextDouble() * total;
                    for (int i = 0; i < samples.Count; i++)
                    {
                        target -= minDist[i];
                        if (target <= 0) { chosen = i; break; }
                    }
                }
                else
                {
                    chosen = random.Next(samples.Count);
                }

                centers[c] = samples[chosen];
                for (int i = 0; i < samples.Count; i++)
                    minDist[i] = Mathf.Min(minDist[i], Distance(samples[i], centers[c]));
            }

            // Lloyd iterations.
            var sums = new Vector3[k];
            var counts = new int[k];
            for (int iter = 0; iter < 12; iter++)
            {
                Array.Clear(sums, 0, k);
                Array.Clear(counts, 0, k);

                for (int i = 0; i < samples.Count; i++)
                {
                    int nearest = Nearest(centers, samples[i]);
                    sums[nearest] += samples[i];
                    counts[nearest]++;
                }

                for (int c = 0; c < k; c++)
                {
                    centers[c] = counts[c] > 0 ? sums[c] / counts[c] : samples[random.Next(samples.Count)];
                }
            }

            var palette = new Color[k];
            for (int c = 0; c < k; c++) palette[c] = new Color(centers[c].x, centers[c].y, centers[c].z, 1f);
            return palette;
        }

        private static float EstimatePaletteSpacing(Color[] palette)
        {
            if (palette.Length < 2) return 0.5f;

            float total = 0f;
            for (int i = 0; i < palette.Length; i++)
            {
                float best = float.MaxValue;
                for (int j = 0; j < palette.Length; j++)
                {
                    if (i == j) continue;
                    float dr = palette[i].r - palette[j].r;
                    float dg = palette[i].g - palette[j].g;
                    float db = palette[i].b - palette[j].b;
                    best = Mathf.Min(best, dr * dr + dg * dg + db * db);
                }
                total += Mathf.Sqrt(best);
            }
            return Mathf.Clamp(total / palette.Length, 0.01f, 0.5f);
        }

        private static float Distance(Vector3 a, Vector3 b)
        {
            float dr = a.x - b.x, dg = a.y - b.y, db = a.z - b.z;
            return WR * dr * dr + WG * dg * dg + WB * db * db;
        }

        private static int Nearest(Vector3[] centers, Vector3 v)
        {
            int best = 0;
            float bestDist = float.MaxValue;
            for (int c = 0; c < centers.Length; c++)
            {
                float d = Distance(centers[c], v);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = c;
                }
            }
            return best;
        }

        #endregion

        private class Quantizer
        {
            private const int CacheBits = 6;
            private const int CacheSize = 1 << CacheBits;

            private readonly ColorMode mode;
            private readonly float steps;
            private readonly float toneMin;
            private readonly float toneRange;
            private readonly Vector3[] palette;
            private readonly int[] nearestCache;
            private readonly int[] secondCache;

            public Quantizer(PixelizeSettings s, Color[] paletteColors, float toneMin, float toneRange)
            {
                mode = s.colorMode;
                steps = (mode == ColorMode.ToneBands ? Mathf.Max(2, s.toneBands) : Mathf.Max(2, s.levels)) - 1;
                this.toneMin = toneMin;
                this.toneRange = Mathf.Max(0.0001f, toneRange);

                if (paletteColors != null)
                {
                    palette = new Vector3[paletteColors.Length];
                    for (int i = 0; i < palette.Length; i++)
                        palette[i] = new Vector3(paletteColors[i].r, paletteColors[i].g, paletteColors[i].b);

                    nearestCache = new int[CacheSize * CacheSize * CacheSize];
                    secondCache = new int[nearestCache.Length];
                    for (int i = 0; i < nearestCache.Length; i++) nearestCache[i] = -1;
                }
            }

            /// <summary>Nearest tone, no dithering (also used by error diffusion).</summary>
            public Color Quantize(Color c) => Quantize(c, 0.5f, 0f);

            /// <summary>
            /// Ordered dithering: picks between the two tones surrounding the color.
            /// The chance of the upper/second tone equals how close the color is to it (scaled by strength),
            /// so the pattern is always clearly visible, no matter how close the tones are.
            /// </summary>
            public Color Quantize(Color c, float threshold, float strength)
            {
                float bias = (threshold - 0.5f) * strength;

                switch (mode)
                {
                    case ColorMode.Levels:
                        return new Color(Step(c.r, bias), Step(c.g, bias), Step(c.b, bias), c.a);

                    case ColorMode.ToneBands:
                    {
                        float y = Luma(c);
                        float v = Mathf.Clamp(Mathf.Floor((y - toneMin) / toneRange * steps + 0.5f + bias), 0f, steps) / steps;
                        // Shift all channels by the same amount: brightness changes, hue stays.
                        float delta = toneMin + v * toneRange - y;
                        return new Color(Mathf.Clamp01(c.r + delta), Mathf.Clamp01(c.g + delta), Mathf.Clamp01(c.b + delta), c.a);
                    }

                    case ColorMode.AdaptivePalette:
                    {
                        var v = new Vector3(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b));
                        GetNearestTwo(v, out int first, out int second);

                        Vector3 p = palette[first];
                        if (strength > 0f && second >= 0)
                        {
                            // How far along the line from the nearest to the second nearest color this pixel is (0-0.5).
                            Vector3 axis = palette[second] - palette[first];
                            float lengthSq = axis.sqrMagnitude;
                            float t = lengthSq > 1e-8f ? Mathf.Clamp01(Vector3.Dot(v - palette[first], axis) / lengthSq) : 0f;
                            if (threshold < t * strength) p = palette[second];
                        }
                        return new Color(p.x, p.y, p.z, c.a);
                    }

                    default:
                        return new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), c.a);
                }
            }

            private float Step(float value, float bias) =>
                Mathf.Clamp(Mathf.Floor(Mathf.Clamp01(value) * steps + 0.5f + bias), 0f, steps) / steps;

            private void GetNearestTwo(Vector3 v, out int first, out int second)
            {
                int r = Mathf.Clamp((int)(v.x * CacheSize), 0, CacheSize - 1);
                int g = Mathf.Clamp((int)(v.y * CacheSize), 0, CacheSize - 1);
                int b = Mathf.Clamp((int)(v.z * CacheSize), 0, CacheSize - 1);
                int key = (r << (CacheBits * 2)) | (g << CacheBits) | b;

                if (nearestCache[key] < 0)
                {
                    var center = new Vector3((r + 0.5f) / CacheSize, (g + 0.5f) / CacheSize, (b + 0.5f) / CacheSize);
                    int best = -1, next = -1;
                    float bestDist = float.MaxValue, nextDist = float.MaxValue;
                    for (int i = 0; i < palette.Length; i++)
                    {
                        float d = Distance(palette[i], center);
                        if (d < bestDist)
                        {
                            next = best; nextDist = bestDist;
                            best = i; bestDist = d;
                        }
                        else if (d < nextDist)
                        {
                            next = i; nextDist = d;
                        }
                    }
                    nearestCache[key] = best;
                    secondCache[key] = next;
                }

                first = nearestCache[key];
                second = secondCache[key];
            }
        }
    }
}
