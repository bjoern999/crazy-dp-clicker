using System.Drawing;
using System.Drawing.Imaging;

namespace TachoClicker;

internal sealed class DinoVisualFingerprint
{
    public const int CurrentVersion = 2;

    public bool HasVisual { get; set; }
    public int VisualVersion { get; set; } = 1;

    // 39-bin normalized HSV+Neutral color histograms (full, upper 45% = Kopf/Rueckenplatten/Kamm, lower 55% = Koerper/Beine)
    // Alle auf die enge Dino-Bounding-Box normiert -> 100% invariant gegen vertikales Gummimatten-Dehnen/Stauchen!
    public float[] ColorHist { get; set; } = Array.Empty<float>();
    public float[] UpperColorHist { get; set; } = Array.Empty<float>();
    public float[] LowerColorHist { get; set; } = Array.Empty<float>();

    // 10x10 normalized RGB (300 floats) + alpha/coverage (100 floats) of tight-cropped foreground Dino
    public float[] GridRgb { get; set; } = Array.Empty<float>();
    public float[] GridAlpha { get; set; } = Array.Empty<float>();

    public double AspectRatio { get; set; }
    public double FillRatio { get; set; }
    public int ForegroundPixels { get; set; }
    public int SampleCount { get; set; }

    public bool IsCurrentVersion =>
        HasVisual &&
        VisualVersion >= CurrentVersion &&
        ColorHist.Length == DinoVisualMatcher.HistBins &&
        UpperColorHist.Length == DinoVisualMatcher.HistBins &&
        LowerColorHist.Length == DinoVisualMatcher.HistBins;
}

internal static class DinoVisualMatcher
{
    // Groessere, nach oben/rechts verschobene Box: erfasst Kopf, Kamm, Stegosaurus-Platten & Schwanz komplett
    // ohne oberen Beschnitt und ohne den linken schwarzen Arena-Rahmen!
    public const int DefaultBoxWidth = 310;
    public const int DefaultBoxHeight = 270;
    public const int GridSize = 10;
    public const int HistBins = 39; // 12 Hue * 3 (Dark/Muted/Vivid) + 3 Neutral (Dark/Mid/Light)

    // Dank 3-Frame-Mittelung ueber die Dehn-Animation + unbeschnittener Box:
    // Gleicher Dino ~0.04..0.14; verschiedene Arten ~0.28..0.85
    public const double VisualMatchThreshold = 0.24;

    public static (DinoVisualFingerprint Fingerprint, Bitmap Preview) CaptureAndExtract(
        int centerX, int centerY, int boxW = DefaultBoxWidth, int boxH = DefaultBoxHeight)
    {
        boxW = Math.Clamp(boxW, 120, 480);
        boxH = Math.Clamp(boxH, 100, 420);

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
                // Zeige die 45%-Trennlinie (Kopf/Platten vs. Koerper) dezent an
                int splitY = fgBounds.Top + (int)(fgBounds.Height * 0.45);
                using var splitPen = new Pen(Color.FromArgb(140, Color.Yellow), 1);
                g.DrawLine(splitPen, fgBounds.Left, splitY, fgBounds.Right, splitY);
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

    public static DinoVisualFingerprint? ExtractFromPreviewPng(string pngPath)
    {
        try
        {
            if (!File.Exists(pngPath)) return null;
            using var raw = new Bitmap(pngPath);
            using var bmp = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.DrawImage(raw, 0, 0, raw.Width, raw.Height);

            // Entferne die 2px-Cyan-Rahmenlinie der Vorschau, damit sie nicht als Vordergrund gewertet wird
            int w = bmp.Width;
            int h = bmp.Height;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    if (c.R < 20 && c.G > 235 && c.B > 235)
                    {
                        int nx = x < w / 2 ? Math.Min(w - 1, x + 3) : Math.Max(0, x - 3);
                        int ny = y < h / 2 ? Math.Min(h - 1, y + 3) : Math.Max(0, y - 3);
                        bmp.SetPixel(x, y, bmp.GetPixel(nx, ny));
                    }
                }
            }

            var fp = ExtractFromBitmap(bmp, out _);
            return fp.IsCurrentVersion ? fp : null;
        }
        catch
        {
            return null;
        }
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
            return new DinoVisualFingerprint { HasVisual = false, VisualVersion = DinoVisualFingerprint.CurrentVersion };
        }

        // 3. Find tight bounding box ignoring stray sparks / dust puffs
        int minRowPx = Math.Max(6, rawFgCount / (h * 4));
        int minColPx = Math.Max(6, rawFgCount / (w * 4));

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
        if (bw < 24 || bh < 24)
        {
            return new DinoVisualFingerprint { HasVisual = false, VisualVersion = DinoVisualFingerprint.CurrentVersion };
        }

        fgBounds = new Rectangle(minX, minY, bw, bh);

        // 4. Compute full, upper-45% (head/plates/crest) and lower-55% (body/legs) histograms + 10x10 normalized grid
        float[] hist = new float[HistBins];
        float[] upperHist = new float[HistBins];
        float[] lowerHist = new float[HistBins];
        double[] cellR = new double[GridSize * GridSize];
        double[] cellG = new double[GridSize * GridSize];
        double[] cellB = new double[GridSize * GridSize];
        int[] cellFg = new int[GridSize * GridSize];
        int[] cellTot = new int[GridSize * GridSize];
        int fgInside = 0;
        int upperFg = 0;
        int lowerFg = 0;
        int splitY = minY + (int)(bh * 0.45);

        for (int y = minY; y <= maxY; y++)
        {
            int gy = Math.Min(GridSize - 1, (y - minY) * GridSize / bh);
            bool isUpper = y <= splitY;
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
                if (isUpper)
                {
                    upperHist[bin] += 1.0f;
                    upperFg++;
                }
                else
                {
                    lowerHist[bin] += 1.0f;
                    lowerFg++;
                }
            }
        }

        if (fgInside < 150)
        {
            return new DinoVisualFingerprint { HasVisual = false, VisualVersion = DinoVisualFingerprint.CurrentVersion };
        }

        for (int k = 0; k < HistBins; k++)
        {
            hist[k] = (float)Math.Round(hist[k] / fgInside, 5);
            upperHist[k] = upperFg > 0 ? (float)Math.Round(upperHist[k] / upperFg, 5) : hist[k];
            lowerHist[k] = lowerFg > 0 ? (float)Math.Round(lowerHist[k] / lowerFg, 5) : hist[k];
        }

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
            VisualVersion = DinoVisualFingerprint.CurrentVersion,
            ColorHist = hist,
            UpperColorHist = upperHist,
            LowerColorHist = lowerHist,
            GridRgb = gridRgb,
            GridAlpha = gridAlpha,
            AspectRatio = Math.Round((double)bw / bh, 3),
            FillRatio = Math.Round((double)fgInside / (bw * bh), 3),
            ForegroundPixels = fgInside,
            SampleCount = 1
        };
    }

    /// <summary>
    /// Mittelt mehrere Frames (z. B. 3 Aufnahmen ueber die Gummimatten-Dehn-Animation bei rem=210°, 155°, 100°),
    /// sodass Dehnungs-Extreme, Staubwoelkchen und Funken herausgemittelt werden.
    /// </summary>
    public static DinoVisualFingerprint AverageSamples(IReadOnlyList<DinoVisualFingerprint> samples)
    {
        var valid = samples.Where(s => s.IsCurrentVersion).ToList();
        if (valid.Count == 0)
            return new DinoVisualFingerprint { HasVisual = false, VisualVersion = DinoVisualFingerprint.CurrentVersion };
        if (valid.Count == 1)
            return valid[0];

        int n = valid.Count;
        float[] hist = new float[HistBins];
        float[] upper = new float[HistBins];
        float[] lower = new float[HistBins];
        float[] gridRgb = new float[GridSize * GridSize * 3];
        float[] gridAlpha = new float[GridSize * GridSize];

        foreach (var s in valid)
        {
            for (int i = 0; i < HistBins; i++)
            {
                hist[i] += s.ColorHist[i] / n;
                upper[i] += s.UpperColorHist[i] / n;
                lower[i] += s.LowerColorHist[i] / n;
            }
            for (int i = 0; i < gridRgb.Length; i++)
                gridRgb[i] += s.GridRgb[i] / n;
            for (int i = 0; i < gridAlpha.Length; i++)
                gridAlpha[i] += s.GridAlpha[i] / n;
        }

        for (int i = 0; i < HistBins; i++)
        {
            hist[i] = (float)Math.Round(hist[i], 5);
            upper[i] = (float)Math.Round(upper[i], 5);
            lower[i] = (float)Math.Round(lower[i], 5);
        }
        for (int i = 0; i < gridRgb.Length; i++) gridRgb[i] = (float)Math.Round(gridRgb[i], 4);
        for (int i = 0; i < gridAlpha.Length; i++) gridAlpha[i] = (float)Math.Round(gridAlpha[i], 4);

        return new DinoVisualFingerprint
        {
            HasVisual = true,
            VisualVersion = DinoVisualFingerprint.CurrentVersion,
            ColorHist = hist,
            UpperColorHist = upper,
            LowerColorHist = lower,
            GridRgb = gridRgb,
            GridAlpha = gridAlpha,
            AspectRatio = Math.Round(valid.Average(s => s.AspectRatio), 3),
            FillRatio = Math.Round(valid.Average(s => s.FillRatio), 3),
            ForegroundPixels = (int)Math.Round(valid.Average(s => s.ForegroundPixels)),
            SampleCount = 1
        };
    }

    public static double Distance(DinoVisualFingerprint a, DinoVisualFingerprint b)
    {
        if (!a.IsCurrentVersion || !b.IsCurrentVersion ||
            a.GridRgb.Length != GridSize * GridSize * 3 || b.GridRgb.Length != GridSize * GridSize * 3 ||
            a.GridAlpha.Length != GridSize * GridSize || b.GridAlpha.Length != GridSize * GridSize)
        {
            return 1.0;
        }

        // 1. Voll-, Oberteil- (Kopf/Platten/Kamm) & Unterteil-Farbhistogramm (alle 100% streckungs-invariant!)
        double fullDist = HistL1Distance(a.ColorHist, b.ColorHist);
        double upperDist = HistL1Distance(a.UpperColorHist, b.UpperColorHist);
        double lowerDist = HistL1Distance(a.LowerColorHist, b.LowerColorHist);

        // 2. 10x10 bbox-normiertes Farb- & Silhouetten-Raster (ebenfalls streckungs-invariant!)
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
                colorDiff = 0.65;
            }

            gridErrSum += wCell * (0.72 * colorDiff + 0.28 * alphaDiff);
            gridWeightSum += wCell;
        }

        double gridDist = gridWeightSum > 0.01 ? Math.Min(1.0, gridErrSum / gridWeightSum) : 1.0;

        // 3. Seitenverhaeltnis mit Deadzone +-0.14 (ignoriert Gummimatten-Dehnen/Stauchen!)
        double arDiff = Math.Max(0.0, Math.Abs(a.AspectRatio - b.AspectRatio) - 0.14);
        double shapeDist = Math.Min(1.0, arDiff / 0.80 + Math.Abs(a.FillRatio - b.FillRatio) * 0.45);

        return 0.30 * fullDist + 0.25 * upperDist + 0.15 * lowerDist + 0.25 * gridDist + 0.05 * shapeDist;
    }

    private static double HistL1Distance(float[] h1, float[] h2)
    {
        double sum = 0.0;
        for (int i = 0; i < HistBins; i++)
            sum += Math.Abs(h1[i] - h2[i]);
        return Math.Min(1.0, sum * 0.5);
    }

    public static void BlendInto(DinoVisualFingerprint target, DinoVisualFingerprint sample)
    {
        if (!sample.IsCurrentVersion) return;
        if (!target.IsCurrentVersion)
        {
            target.HasVisual = true;
            target.VisualVersion = DinoVisualFingerprint.CurrentVersion;
            target.ColorHist = (float[])sample.ColorHist.Clone();
            target.UpperColorHist = (float[])sample.UpperColorHist.Clone();
            target.LowerColorHist = (float[])sample.LowerColorHist.Clone();
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

        for (int i = 0; i < HistBins; i++)
        {
            target.ColorHist[i] = (float)Math.Round(target.ColorHist[i] * inv + sample.ColorHist[i] * alpha, 5);
            target.UpperColorHist[i] = (float)Math.Round(target.UpperColorHist[i] * inv + sample.UpperColorHist[i] * alpha, 5);
            target.LowerColorHist[i] = (float)Math.Round(target.LowerColorHist[i] * inv + sample.LowerColorHist[i] * alpha, 5);
        }

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
            SavePngBothLocations(fileName, preview);
        }
        catch { }
    }

    public static void SaveLastCapturedPreview(Bitmap preview)
    {
        try
        {
            SavePngBothLocations("dino_last_captured.png", preview);
        }
        catch { }
    }

    private static void SavePngBothLocations(string fileName, Bitmap preview)
    {
        string basePath = Path.Combine(AppContext.BaseDirectory, fileName);
        preview.Save(basePath, ImageFormat.Png);

        string projectRootDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        if (Directory.Exists(projectRootDir) && File.Exists(Path.Combine(projectRootDir, "TachoClicker.csproj")))
        {
            preview.Save(Path.Combine(projectRootDir, fileName), ImageFormat.Png);
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
