using System.Drawing;
using System.Drawing.Imaging;

namespace TachoClicker;

internal sealed class DinoVisualFingerprint
{
    public bool HasVisual { get; set; }

    // 39-bin normalized HSV+Neutral color histogram of foreground Dino pixels (sum = 1.0)
    public float[] ColorHist { get; set; } = Array.Empty<float>();

    // 10x10 normalized RGB (300 floats) + alpha/coverage (100 floats) of tight-cropped foreground Dino
    public float[] GridRgb { get; set; } = Array.Empty<float>();
    public float[] GridAlpha { get; set; } = Array.Empty<float>();

    public double AspectRatio { get; set; }
    public double FillRatio { get; set; }
    public int ForegroundPixels { get; set; }
    public int SampleCount { get; set; }
}

internal static class DinoVisualMatcher
{
    public const int DefaultBoxWidth = 280;
    public const int DefaultBoxHeight = 220;
    public const int GridSize = 10;
    public const int HistBins = 39; // 12 Hue * 3 (Light/Normal/Dark) + 3 Neutral (Dark/Mid/Light)

    // Same Dino across idle animation: ~0.04..0.15; different Dino species: ~0.30..0.85
    public const double VisualMatchThreshold = 0.26;

    public static (DinoVisualFingerprint Fingerprint, Bitmap Preview) CaptureAndExtract(
        int centerX, int centerY, int boxW = DefaultBoxWidth, int boxH = DefaultBoxHeight)
    {
        boxW = Math.Clamp(boxW, 120, 480);
        boxH = Math.Clamp(boxH, 100, 400);

        int left = Math.Max(0, centerX - boxW / 2);
        int top = Math.Max(0, centerY - boxH / 2);

        var bmp = new Bitmap(boxW, boxH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(left, top, 0, 0, new Size(boxW, boxH), CopyPixelOperation.SourceCopy);
        }

        var fp = ExtractFromBitmap(bmp, out Rectangle fgBounds);

        // Create annotated preview bitmap for Live-Inspektor
        var preview = (Bitmap)bmp.Clone();
        using (var g = Graphics.FromImage(preview))
        {
            if (fp.HasVisual && fgBounds.Width > 8 && fgBounds.Height > 8)
            {
                using var pen = new Pen(Color.Cyan, 2);
                g.DrawRectangle(pen, fgBounds);
            }
            else
            {
                using var pen = new Pen(Color.OrangeRed, 2);
                g.DrawRectangle(pen, 2, 2, boxW - 5, boxH - 5);
            }
        }

        bmp.Dispose();
        return (fp, preview);
    }

    public static DinoVisualFingerprint ExtractFromBitmap(Bitmap bmp, out Rectangle fgBounds)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        fgBounds = Rectangle.Empty;

        var rect = new Rectangle(0, 0, w, h);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        byte[] rArr = new byte[w * h];
        byte[] gArr = new byte[w * h];
        byte[] bArr = new byte[w * h];
        bool[] fg = new bool[w * h];

        try
        {
            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                int stride = data.Stride;
                for (int y = 0; y < h; y++)
                {
                    byte* row = ptr + (y * stride);
                    int rowOff = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        bArr[rowOff + x] = row[x * 4 + 0];
                        gArr[rowOff + x] = row[x * 4 + 1];
                        rArr[rowOff + x] = row[x * 4 + 2];
                    }
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        // 1. Estimate arena floor background color from the 4px outer border rim using median
        var rimR = new List<int>(w * 8 + h * 8);
        var rimG = new List<int>(w * 8 + h * 8);
        var rimB = new List<int>(w * 8 + h * 8);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (x < 4 || x >= w - 4 || y < 4 || y >= h - 4)
                {
                    int i = y * w + x;
                    rimR.Add(rArr[i]);
                    rimG.Add(gArr[i]);
                    rimB.Add(bArr[i]);
                }
            }
        }

        rimR.Sort();
        rimG.Sort();
        rimB.Sort();
        int bgR = rimR[rimR.Count / 2];
        int bgG = rimG[rimG.Count / 2];
        int bgB = rimB[rimB.Count / 2];
        int bgLuma = (bgR + bgG + bgB) / 3;

        // 2. Segment foreground pixels (Dino sprite vs uniform arena floor & faint gray watermark)
        int[] rowCounts = new int[h];
        int[] colCounts = new int[w];
        int rawFgCount = 0;

        for (int y = 0; y < h; y++)
        {
            int rowOff = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = rowOff + x;
                int r = rArr[i], g = gArr[i], b = bArr[i];
                int diff = Math.Abs(r - bgR) + Math.Abs(g - bgG) + Math.Abs(b - bgB);
                int maxC = Math.Max(r, Math.Max(g, b));
                int minC = Math.Min(r, Math.Min(g, b));
                int chroma = maxC - minC;
                int luma = (r + g + b) / 3;

                // Exclude pixels close to arena background OR faint low-chroma gray arena watermark
                bool isWatermark = chroma < 24 && Math.Abs(luma - bgLuma) < 38 && diff < 85;
                if (diff > 44 && !isWatermark)
                {
                    fg[i] = true;
                    rowCounts[y]++;
                    colCounts[x]++;
                    rawFgCount++;
                }
            }
        }

        if (rawFgCount < 180)
        {
            return new DinoVisualFingerprint { HasVisual = false };
        }

        // 3. Find tight bounding box ignoring stray 1..3 pixel sparks/dust
        int minRowPx = Math.Max(4, rawFgCount / (h * 4));
        int minColPx = Math.Max(4, rawFgCount / (w * 4));

        int minY = 0;
        while (minY < h - 1 && rowCounts[minY] < minRowPx) minY++;
        int maxY = h - 1;
        while (maxY > minY && rowCounts[maxY] < minRowPx) maxY--;

        int minX = 0;
        while (minX < w - 1 && colCounts[minX] < minColPx) minX++;
        int maxX = w - 1;
        while (maxX > minX && colCounts[maxX] < minColPx) maxX--;

        int bw = maxX - minX + 1;
        int bh = maxY - minY + 1;
        if (bw < 20 || bh < 20)
        {
            return new DinoVisualFingerprint { HasVisual = false };
        }

        fgBounds = new Rectangle(minX, minY, bw, bh);

        // 4. Compute 39-bin HSV+Neutral color histogram & 10x10 normalized spatial grid
        float[] hist = new float[HistBins];
        double[] cellR = new double[GridSize * GridSize];
        double[] cellG = new double[GridSize * GridSize];
        double[] cellB = new double[GridSize * GridSize];
        int[] cellFg = new int[GridSize * GridSize];
        int[] cellTot = new int[GridSize * GridSize];
        int fgInside = 0;

        for (int y = minY; y <= maxY; y++)
        {
            int gy = Math.Min(GridSize - 1, (y - minY) * GridSize / bh);
            int rowOff = y * w;
            for (int x = minX; x <= maxX; x++)
            {
                int gx = Math.Min(GridSize - 1, (x - minX) * GridSize / bw);
                int cellIdx = gy * GridSize + gx;
                cellTot[cellIdx]++;

                int i = rowOff + x;
                if (!fg[i]) continue;

                fgInside++;
                int r = rArr[i], g = gArr[i], b = bArr[i];
                cellFg[cellIdx]++;
                cellR[cellIdx] += r / 255.0;
                cellG[cellIdx] += g / 255.0;
                cellB[cellIdx] += b / 255.0;

                int bin = ComputeColorBin(r, g, b);
                hist[bin] += 1.0f;
            }
        }

        if (fgInside < 150)
        {
            return new DinoVisualFingerprint { HasVisual = false };
        }

        for (int k = 0; k < HistBins; k++)
            hist[k] = (float)Math.Round(hist[k] / fgInside, 5);

        float[] gridRgb = new float[GridSize * GridSize * 3];
        float[] gridAlpha = new float[GridSize * GridSize];

        for (int c = 0; c < GridSize * GridSize; c++)
        {
            gridAlpha[c] = cellTot[c] > 0 ? (float)Math.Round((double)cellFg[c] / cellTot[c], 4) : 0f;
            if (cellFg[c] > 0)
            {
                gridRgb[c * 3 + 0] = (float)Math.Round(cellR[c] / cellFg[c], 4);
                gridRgb[c * 3 + 1] = (float)Math.Round(cellG[c] / cellFg[c], 4);
                gridRgb[c * 3 + 2] = (float)Math.Round(cellB[c] / cellFg[c], 4);
            }
        }

        return new DinoVisualFingerprint
        {
            HasVisual = true,
            ColorHist = hist,
            GridRgb = gridRgb,
            GridAlpha = gridAlpha,
            AspectRatio = Math.Round((double)bw / bh, 3),
            FillRatio = Math.Round((double)fgInside / (bw * bh), 3),
            ForegroundPixels = fgInside,
            SampleCount = 1
        };
    }

    public static double Distance(DinoVisualFingerprint a, DinoVisualFingerprint b)
    {
        if (!a.HasVisual || !b.HasVisual ||
            a.ColorHist.Length != HistBins || b.ColorHist.Length != HistBins ||
            a.GridRgb.Length != GridSize * GridSize * 3 || b.GridRgb.Length != GridSize * GridSize * 3 ||
            a.GridAlpha.Length != GridSize * GridSize || b.GridAlpha.Length != GridSize * GridSize)
        {
            return 1.0;
        }

        // 1. Histogram L1 distance in [0..1] (0 = identical palette, 1 = disjoint colors)
        double histL1 = 0.0;
        for (int i = 0; i < HistBins; i++)
            histL1 += Math.Abs(a.ColorHist[i] - b.ColorHist[i]);
        double histDist = Math.Min(1.0, histL1 * 0.5);

        // 2. 10x10 spatial color + silhouette distance in [0..1]
        double gridErrSum = 0.0;
        double gridWeightSum = 0.0;
        for (int c = 0; c < GridSize * GridSize; c++)
        {
            double aa = a.GridAlpha[c];
            double ba = b.GridAlpha[c];
            double wCell = Math.Max(aa, ba);
            if (wCell < 0.12) continue;

            double alphaDiff = Math.Abs(aa - ba);
            double colorDiff = 0.0;
            if (aa >= 0.12 && ba >= 0.12)
            {
                colorDiff = (Math.Abs(a.GridRgb[c * 3 + 0] - b.GridRgb[c * 3 + 0]) +
                             Math.Abs(a.GridRgb[c * 3 + 1] - b.GridRgb[c * 3 + 1]) +
                             Math.Abs(a.GridRgb[c * 3 + 2] - b.GridRgb[c * 3 + 2])) / 3.0;
            }
            else
            {
                colorDiff = 0.65; // One has Dino body here, the other is empty
            }

            gridErrSum += wCell * (0.70 * colorDiff + 0.30 * alphaDiff);
            gridWeightSum += wCell;
        }

        double gridDist = gridWeightSum > 0.01 ? Math.Min(1.0, gridErrSum / gridWeightSum) : 1.0;

        // 3. Silhouette proportions
        double shapeDist = Math.Min(1.0,
            Math.Abs(a.AspectRatio - b.AspectRatio) / 0.85 +
            Math.Abs(a.FillRatio - b.FillRatio) * 0.55);

        return 0.55 * histDist + 0.35 * gridDist + 0.10 * shapeDist;
    }

    public static void BlendInto(DinoVisualFingerprint target, DinoVisualFingerprint sample)
    {
        if (!sample.HasVisual) return;
        if (!target.HasVisual)
        {
            target.HasVisual = true;
            target.ColorHist = (float[])sample.ColorHist.Clone();
            target.GridRgb = (float[])sample.GridRgb.Clone();
            target.GridAlpha = (float[])sample.GridAlpha.Clone();
            target.AspectRatio = sample.AspectRatio;
            target.FillRatio = sample.FillRatio;
            target.ForegroundPixels = sample.ForegroundPixels;
            target.SampleCount = 1;
            return;
        }

        // Only refine during the first 6 captures so the reference stays crisp and never drifts
        if (target.SampleCount >= 6) return;

        float alpha = target.SampleCount < 3 ? 0.25f : 0.12f;
        float inv = 1.0f - alpha;

        for (int i = 0; i < HistBins && i < target.ColorHist.Length && i < sample.ColorHist.Length; i++)
            target.ColorHist[i] = (float)Math.Round(target.ColorHist[i] * inv + sample.ColorHist[i] * alpha, 5);

        for (int i = 0; i < target.GridRgb.Length && i < sample.GridRgb.Length; i++)
            target.GridRgb[i] = (float)Math.Round(target.GridRgb[i] * inv + sample.GridRgb[i] * alpha, 4);

        for (int i = 0; i < target.GridAlpha.Length && i < sample.GridAlpha.Length; i++)
            target.GridAlpha[i] = (float)Math.Round(target.GridAlpha[i] * inv + sample.GridAlpha[i] * alpha, 4);

        target.AspectRatio = Math.Round(target.AspectRatio * inv + sample.AspectRatio * alpha, 3);
        target.FillRatio = Math.Round(target.FillRatio * inv + sample.FillRatio * alpha, 3);
        target.ForegroundPixels = (int)Math.Round(target.ForegroundPixels * inv + sample.ForegroundPixels * alpha);
        target.SampleCount++;
    }

    public static void SaveReferenceThumbnail(int setupId, string setupName, Bitmap preview)
    {
        try
        {
            string safeName = string.Concat(setupName.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'));
            if (string.IsNullOrEmpty(safeName)) safeName = $"Setup{setupId}";
            string fileName = $"dino_{setupId}_{safeName}.png";

            string basePath = Path.Combine(AppContext.BaseDirectory, fileName);
            preview.Save(basePath, ImageFormat.Png);

            string projectRootDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
            if (Directory.Exists(projectRootDir) && File.Exists(Path.Combine(projectRootDir, "TachoClicker.csproj")))
            {
                preview.Save(Path.Combine(projectRootDir, fileName), ImageFormat.Png);
            }
        }
        catch
        {
            // Ignore non-critical thumbnail save errors
        }
    }

    private static int ComputeColorBin(int r, int g, int b)
    {
        double rf = r / 255.0;
        double gf = g / 255.0;
        double bf = b / 255.0;

        double max = Math.Max(rf, Math.Max(gf, bf));
        double min = Math.Min(rf, Math.Min(gf, bf));
        double delta = max - min;
        double sat = max > 1e-4 ? delta / max : 0.0;

        // Neutral colors (black/gray/white/tusks/outlines) -> bins 36, 37, 38
        if (sat < 0.18 || max < 0.14)
        {
            if (max < 0.32) return 36; // Dark neutral
            if (max < 0.72) return 37; // Mid neutral
            return 38;                 // Bright white/light gray
        }

        double hue;
        if (delta < 1e-5) hue = 0;
        else if (max == rf) hue = 60.0 * (((gf - bf) / delta) % 6.0);
        else if (max == gf) hue = 60.0 * (((bf - rf) / delta) + 2.0);
        else hue = 60.0 * (((rf - gf) / delta) + 4.0);
        if (hue < 0) hue += 360.0;

        int hueBucket = ((int)(hue / 30.0)) % 12; // 12 hue slices (30° each)
        int shadeBucket = max < 0.45 ? 0 : (sat < 0.50 ? 1 : 2); // 0=dark, 1=muted/pastel, 2=vivid
        return hueBucket * 3 + shadeBucket; // 0..35
    }
}
