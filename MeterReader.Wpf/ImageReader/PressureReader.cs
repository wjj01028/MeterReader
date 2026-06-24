using System.IO;
using OpenCvSharp;

namespace MeterReader.Wpf.ImageReader;

/// <summary>压力表识别器V3：切割黑色圆环 → 整形矫正 → 标记圆心和红绿指针</summary>
public static class PressureRecognizer
{
    /// <summary>是否保存识别过程中生成的中间调试图片</summary>
    public static bool SaveDebugImages { get; set; }

    // 角度→读数：起点210°(7点)=0, 终点150°(5点)=10, 总扫300°
    private static double AngleToValue(double angleDeg)
    {
        double v = ((angleDeg - 210 + 360) % 360) / 300.0 * 10.0;
        if (v < 0) v = 0; if (v > 10) v = 10;
        return Math.Round(v * 100) / 100.0;
    }

    private static double ComputeAngle(int cx, int cy, OpenCvSharp.Point tip)
    {
        double rad = Math.Atan2(tip.X - cx, -(tip.Y - cy));
        double deg = rad * 180 / Math.PI;
        if (deg < 0) deg += 360;
        return deg;
    }

    // ==================== 主入口 ====================
    public static GaugeResult Recognize(Mat src, string dir, string baseName)
    {
        var res = new GaugeResult
        {
            method = "AllNew", reading = "--", details = "",
            tickCount = 0, pointers = new List<PointerInfo>()
        };
        Mat? cropped = null, processed = null;
        try
        {
            // ==== Step 1: 查找并切割最大黑色圆环 ====
            var cropResult = FindAndCropBlackRing(src, dir, baseName);
            if (cropResult == null) { res.details = "未检测到黑色圆环"; return res; }
            cropped = cropResult.Value.image;
            res.center = cropResult.Value.center;
            res.radius = cropResult.Value.radius;

            // ==== Step 2: 整形矫正为圆形，标记圆心和红绿指针 ====
            processed = RectifyAndMark(cropped, dir, baseName, ref res);
            res.method = "AllNew";
        }
        catch (Exception ex) { res.details = ex.Message; }
        finally
        {
            cropped?.Dispose();
            processed?.Dispose();
        }
        return res;
    }

    // ==================== Step 1: 查找并切割最大黑色圆环 ====================
    private static (Mat image, OpenCvSharp.Point center, int radius)? FindAndCropBlackRing(
        Mat src, string dir, string baseName)
    {
        int W = src.Width, H = src.Height;

        // 1. 转灰度图，去除色彩干扰
        using var gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);

        // 2. 高斯模糊，消除管道波纹细小噪点，平滑纹理
        using var blur = new Mat();
        Cv2.GaussianBlur(gray, blur, new OpenCvSharp.Size(7, 7), 0);

        // 3. 二值化提取黑色区域（黑色→白色前景，白色→黑色背景）
        using var binary = new Mat();
        Cv2.Threshold(blur, binary, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        // 4. 形态学操作 —— 过滤管道细碎波纹孔洞
        using var kClose = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(9, 9));
        Cv2.MorphologyEx(binary, binary, MorphTypes.Close, kClose);
        using var kOpen = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(5, 5));
        Cv2.MorphologyEx(binary, binary, MorphTypes.Open, kOpen);

        // 保存二值+形态学中间结果供调试
        if (SaveDebugImages) Cv2.ImWrite(Path.Combine(dir, baseName + "_Binary.png"), binary);

        // 5. 裁剪 ROI，只保留白色表盘区域
        Cv2.FindContours(binary, out var contours, out var hierarchy,
            RetrievalModes.CComp, ContourApproximationModes.ApproxSimple);

        double bestScore = 0;
        OpenCvSharp.Point bestCenter = new(W / 2, H / 2);
        int bestRadius = (int)(Math.Min(W, H) * 0.3);

        for (int i = 0; i < contours.Length; i++)
        {
            double area = Cv2.ContourArea(contours[i]);
            if (area < W * H * 0.06 || area > W * H * 0.85) continue;

            double perimeter = Cv2.ArcLength(contours[i], true);
            if (perimeter < 20) continue;
            double circularity = 4 * Math.PI * area / (perimeter * perimeter);
            if (circularity < 0.25) continue;

            Cv2.MinEnclosingCircle(contours[i], out var ec, out float er);
            int ecx = (int)ec.X, ecy = (int)ec.Y, eR = (int)er;
            if (ecx < W * 0.1 || ecx > W * 0.9) continue;
            if (ecy < H * 0.1 || ecy > H * 0.9) continue;

            bool hasInnerHole = false;
            if (hierarchy != null)
            {
                int childIdx = hierarchy[i].Child;
                if (childIdx >= 0 && childIdx < contours.Length)
                {
                    double childArea = Cv2.ContourArea(contours[childIdx]);
                    hasInnerHole = childArea > area * 0.08;
                }
            }

            int innerBright = 0, innerTotal = 0;
            for (int j = 0; j < 12; j++)
            {
                double ang = j * 2 * Math.PI / 12;
                for (double rFrac = 0.50; rFrac <= 0.70; rFrac += 0.10)
                {
                    int sx = (int)(ecx + eR * rFrac * Math.Cos(ang));
                    int sy = (int)(ecy + eR * rFrac * Math.Sin(ang));
                    if (sx >= 0 && sx < W && sy >= 0 && sy < H)
                    {
                        innerTotal++;
                        if (gray.Get<byte>(sy, sx) > 130) innerBright++;
                    }
                }
            }
            double innerBrightRatio = innerTotal > 0 ? (double)innerBright / innerTotal : 0;

            double holeBonus = hasInnerHole ? 1.5 : 0.5;
            double score = circularity * eR * innerBrightRatio * holeBonus;
            if (score > bestScore && innerBrightRatio > 0.5)
            {
                bestScore = score;
                bestCenter = new OpenCvSharp.Point(ecx, ecy);
                bestRadius = eR;
            }
        }

        if (bestScore == 0) return null;

        int margin = (int)(bestRadius * 0.12);
        int cropSize = (bestRadius + margin) * 2;
        int cropX = Math.Max(0, bestCenter.X - bestRadius - margin);
        int cropY = Math.Max(0, bestCenter.Y - bestRadius - margin);
        cropSize = Math.Min(cropSize, W - cropX);
        cropSize = Math.Min(cropSize, H - cropY);
        if (cropSize <= 0) return null;

        var cropRect = new OpenCvSharp.Rect(cropX, cropY, cropSize, cropSize);
        var cropped = new Mat(src, cropRect).Clone();

        var newCenter = new OpenCvSharp.Point(bestCenter.X - cropX, bestCenter.Y - cropY);
        int newRadius = (int)(cropSize / 2.0);

        return (cropped, newCenter, newRadius);
    }

    // ==================== Step 2: 整形矫正 + 标记圆心和指针 ====================
    private static Mat RectifyAndMark(Mat cropped, string dir, string baseName, ref GaugeResult res)
    {
        int W = cropped.Width, H = cropped.Height;
        int cx = (int)res.center.X, cy = (int)res.center.Y, R = res.radius;

        Mat working = cropped;
        bool wasRectified = false;

        using var gray = new Mat();
        Cv2.CvtColor(cropped, gray, ColorConversionCodes.BGR2GRAY);
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 40, 120);
        using var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(3, 3));
        Cv2.Dilate(edges, edges, k, iterations: 2);
        Cv2.FindContours(edges, out var contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        double bestScore = 0;
        RotatedRect bestRRect = default;
        foreach (var c in contours)
        {
            double area = Cv2.ContourArea(c);
            if (area < W * H * 0.15 || area > W * H * 0.9) continue;
            if (c.Length < 20) continue;
            var rrect = Cv2.MinAreaRect(c);
            double centerDist = Math.Sqrt(
                Math.Pow(rrect.Center.X - W / 2.0, 2) + Math.Pow(rrect.Center.Y - H / 2.0, 2));
            double score = area / (centerDist + 1);
            if (score > bestScore) { bestScore = score; bestRRect = rrect; }
        }

        if (bestScore > 0)
        {
            float longAxis = Math.Max(bestRRect.Size.Width, bestRRect.Size.Height);
            float shortAxis = Math.Min(bestRRect.Size.Width, bestRRect.Size.Height);
            float axisRatio = longAxis / Math.Max(1f, shortAxis);

            if (axisRatio >= 1.15 && axisRatio <= 2.0)
            {
                int outSize = (int)(longAxis * 1.05);
                outSize = Math.Min(Math.Max(outSize, Math.Min(W, H)), Math.Max(W, H));
                outSize = (outSize + 1) / 2 * 2;

                var rawCorners = bestRRect.Points();
                Point2f tl = rawCorners.OrderBy(p => p.X + p.Y).First();
                Point2f br = rawCorners.OrderBy(p => p.X + p.Y).Last();
                Point2f tr = rawCorners.OrderBy(p => p.X - p.Y).Last();
                Point2f bl = rawCorners.OrderBy(p => p.X - p.Y).First();
                Point2f[] srcCorners = [tl, tr, br, bl];

                float pad = outSize * 0.025f;
                Point2f[] dstCorners = [
                    new Point2f(pad, pad),
                    new Point2f(outSize - pad, pad),
                    new Point2f(outSize - pad, outSize - pad),
                    new Point2f(pad, outSize - pad)
                ];

                using var perspMat = Cv2.GetPerspectiveTransform(srcCorners, dstCorners);
                var rectified = new Mat();
                Cv2.WarpPerspective(cropped, rectified, perspMat,
                    new OpenCvSharp.Size(outSize, outSize));

                working = rectified;
                wasRectified = true;
                cx = outSize / 2;
                cy = outSize / 2;
                R = (int)(outSize * 0.42);
                res.center = new OpenCvSharp.Point(cx, cy);
                res.radius = R;
            }
        }

        // --- 检测三根指针 ---
        DetectColorPointer(working, "red", cx, cy, R, res);
        DetectColorPointer(working, "green", cx, cy, R, res);
        var blackTip = DetectBlackPointer(working, cx, cy, R);
        if (blackTip.HasValue)
        {
            double ang = ComputeAngle(cx, cy, blackTip.Value);
            res.pointers.Add(new PointerInfo
            {
                colorName = "黑色", drawColor = Scalar.Black,
                angleDeg = ang, value = AngleToValue(ang), tipPoint = blackTip.Value
            });
        }

        // --- 读数计算 ---
        var bk = res.pointers.FirstOrDefault(p => p.colorName == "黑色");
        var rd = res.pointers.FirstOrDefault(p => p.colorName == "红色");
        var gn = res.pointers.FirstOrDefault(p => p.colorName == "绿色");
        res.blackValue = bk?.value ?? 0;
        res.redValue = rd?.value ?? 0;
        res.greenValue = gn?.value ?? 0;
        if (res.blackValue == 0 && res.pointers.Count > 0)
            res.blackValue = res.pointers[0].value;
        res.reading = res.blackValue > 0 ? res.blackValue.ToString("F2") : "--";

        // --- 绘制标注 ---
        using var marked = working.Clone();
        DrawAnnotations(marked, cx, cy, R, res);

        // --- 保存调试图片（含圆心、指针、结果标注） ---
        if (SaveDebugImages)
        {
            // 裁剪图标注版
            using var croppedAnnotated = cropped.Clone();
            DrawAnnotations(croppedAnnotated, (int)res.center.X, (int)res.center.Y, res.radius, res);
            Cv2.ImWrite(Path.Combine(dir, baseName + "_Cropped.png"), croppedAnnotated);

            // 最终结果图：先保存含标注的完整图，再做表盘外填白
            Cv2.ImWrite(Path.Combine(dir, baseName + "_AllNew.png"), marked);
            using var dialOnlyMask = new Mat(marked.Size(), MatType.CV_8UC1, Scalar.White);
            int dialRadius = (int)(R * 0.88);
            Cv2.Circle(dialOnlyMask, new OpenCvSharp.Point(cx, cy), dialRadius, Scalar.Black, -1);
            marked.SetTo(Scalar.White, dialOnlyMask);
        }

        if (wasRectified) return working;
        return working.Clone();
    }

    // ==================== 调试图片标注（圆心 + 三指针 + 右下角读数） ====================
    private static void DrawAnnotations(Mat img, int cx, int cy, int R, GaugeResult res)
    {
        // 圆心十字
        Cv2.Line(img, new OpenCvSharp.Point(cx - 15, cy), new OpenCvSharp.Point(cx + 15, cy),
            new Scalar(0, 255, 255), 2);
        Cv2.Line(img, new OpenCvSharp.Point(cx, cy - 15), new OpenCvSharp.Point(cx, cy + 15),
            new Scalar(0, 255, 255), 2);
        // 表盘圆
        Cv2.Circle(img, new OpenCvSharp.Point(cx, cy), R, new Scalar(255, 200, 50), 2);

        // 三指针线 + 尖端圆点
        if (res.redValue > 0 || res.greenValue > 0 || res.blackValue > 0)
        {
            var rd = res.pointers.FirstOrDefault(p => p.colorName == "红色");
            var gn = res.pointers.FirstOrDefault(p => p.colorName == "绿色");
            var bk = res.pointers.FirstOrDefault(p => p.colorName == "黑色");

            if (rd != null)
            {
                Cv2.Line(img, new OpenCvSharp.Point(cx, cy), rd.tipPoint, Scalar.Red, 3);
                Cv2.Circle(img, rd.tipPoint, 5, Scalar.Red, -1);
            }
            if (gn != null)
            {
                Cv2.Line(img, new OpenCvSharp.Point(cx, cy), gn.tipPoint, Scalar.LimeGreen, 3);
                Cv2.Circle(img, gn.tipPoint, 5, Scalar.LimeGreen, -1);
            }
            if (bk != null)
            {
                Cv2.Line(img, new OpenCvSharp.Point(cx, cy), bk.tipPoint, Scalar.Black, 3);
                Cv2.Circle(img, bk.tipPoint, 5, Scalar.Black, -1);
            }
        }

        // 右下角读数（从上到下：Red / Green / Black）
        int baseX = img.Width - 180;
        int baseY = img.Height - 70;
        int lineH = 22;
        Cv2.PutText(img, $"Red  : {res.redValue:F2}", new OpenCvSharp.Point(baseX, baseY),
            HersheyFonts.HersheySimplex, 0.65, Scalar.Red, 2);
        Cv2.PutText(img, $"Green: {res.greenValue:F2}", new OpenCvSharp.Point(baseX, baseY + lineH),
            HersheyFonts.HersheySimplex, 0.65, Scalar.LimeGreen, 2);
        Cv2.PutText(img, $"Black: {res.blackValue:F2}", new OpenCvSharp.Point(baseX, baseY + lineH * 2),
            HersheyFonts.HersheySimplex, 0.65, Scalar.Black, 2);
    }

    // ==================== 黑色指针检测（针对二值图像做径向扫描，与_Binary.png阈值一致） ====================
    private static OpenCvSharp.Point? DetectBlackPointer(Mat bgr, int cx, int cy, int R)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);

        // 使用 OTSU 自动阈值（与_Binary.png 一致），确保指针能被正确分割
        using var otsuTemp = new Mat();
        double otsuThr = Cv2.Threshold(gray, otsuTemp, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        byte thr = (byte)otsuThr;

        // 如果 OTSU 阈值过低（整体偏暗），用更宽松的均值阈值兜底
        if (thr < 30) thr = (byte)(gray.Mean().Val0 * 0.60);

        double[] rayLen = new double[360];
        for (int deg = 0; deg < 360; deg++)
        {
            double rad = deg * Math.PI / 180.0;
            double dx = Math.Sin(rad), dy = -Math.Cos(rad);
            int run = 0, maxRun = 0;
            for (int r = (int)(R * 0.08); r < (int)(R * 0.85); r++)
            {
                int x = cx + (int)(r * dx), y = cy + (int)(r * dy);
                if (x < 0 || x >= gray.Width || y < 0 || y >= gray.Height) break;
                if (gray.Get<byte>(y, x) < thr) { run++; if (run > maxRun) maxRun = run; }
                else run = 0;
            }
            rayLen[deg] = maxRun;
        }

        double[] smooth = new double[360];
        for (int i = 0; i < 360; i++)
            smooth[i] = rayLen[(i - 1 + 360) % 360] * 0.3 + rayLen[i] * 0.4 + rayLen[(i + 1) % 360] * 0.3;

        double bestVal = 0; int bestDeg = 0;
        for (int i = 0; i < 360; i++)
            if (smooth[i] > bestVal) { bestVal = smooth[i]; bestDeg = i; }

        if (bestVal < R * 0.10) return null;

        double rad0 = bestDeg * Math.PI / 180.0;
        double dx0 = Math.Sin(rad0), dy0 = -Math.Cos(rad0);
        int oppDeg = (bestDeg + 180) % 360;
        double rad1 = oppDeg * Math.PI / 180.0;
        double dx1 = Math.Sin(rad1), dy1 = -Math.Cos(rad1);

        int far0 = 0, near0 = 0, far1 = 0, near1 = 0;
        for (int r = (int)(R * 0.05); r < (int)(R * 0.85); r++)
        {
            int x0 = cx + (int)(r * dx0), y0 = cy + (int)(r * dy0);
            int x1 = cx + (int)(r * dx1), y1 = cy + (int)(r * dy1);
            if (x0 >= 0 && x0 < gray.Width && y0 >= 0 && y0 < gray.Height && gray.Get<byte>(y0, x0) < thr)
            { if (r < R * 0.30) near0++; else far0++; }
            if (x1 >= 0 && x1 < gray.Width && y1 >= 0 && y1 < gray.Height && gray.Get<byte>(y1, x1) < thr)
            { if (r < R * 0.30) near1++; else far1++; }
        }

        double quality0 = far0 - near0 * 1.5;
        double quality1 = far1 - near1 * 1.5;
        bool tipSide0 = quality0 >= quality1;

        double tipRad = tipSide0 ? rad0 : rad1;
        double tdx = Math.Sin(tipRad), tdy = -Math.Cos(tipRad);

        int tipX = cx, tipY = cy;
        for (int r = (int)(R * 0.30); r < (int)(R * 0.85); r++)
        {
            int lx = cx + (int)(r * tdx), ly = cy + (int)(r * tdy);
            if (lx < 1 || lx >= gray.Width - 1 || ly < 1 || ly >= gray.Height - 1) continue;
            if (gray.Get<byte>(ly, lx) < thr) { tipX = lx; tipY = ly; }
        }
        return new OpenCvSharp.Point(tipX, tipY);
    }

    // ==================== 红/绿指针检测（HSV颜色分割+轮廓分析） ====================
    private static void DetectColorPointer(Mat bgr, string color,
        int cx, int cy, int R, GaugeResult res)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        using var mask = new Mat();

        if (color == "red")
        {
            using var m1 = new Mat(); using var m2 = new Mat();
            Cv2.InRange(hsv, new Scalar(0, 80, 60), new Scalar(10, 255, 255), m1);
            Cv2.InRange(hsv, new Scalar(165, 80, 60), new Scalar(180, 255, 255), m2);
            Cv2.BitwiseOr(m1, m2, mask);
        }
        else
        {
            Cv2.InRange(hsv, new Scalar(35, 80, 50), new Scalar(90, 255, 255), mask);
        }

        using var k3 = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(3, 3));
        using var k5 = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(5, 5));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Open, k3);
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, k5);

        using var dialMask = new Mat(mask.Size(), MatType.CV_8UC1, Scalar.Black);
        Cv2.Circle(dialMask, new OpenCvSharp.Point(cx, cy), (int)(R * 0.95), Scalar.White, -1);
        Cv2.BitwiseAnd(mask, dialMask, mask);

        Cv2.FindContours(mask, out var contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        double bestScore = 0;
        OpenCvSharp.Point bestTip = new(cx, cy);

        foreach (var cnt in contours)
        {
            if (Cv2.ContourArea(cnt) < 20) continue;
            var m = Cv2.Moments(cnt);
            if (m.M00 < 1) continue;
            OpenCvSharp.Point centroid = new((int)(m.M10 / m.M00), (int)(m.M01 / m.M00));
            double cDist = Math.Sqrt((centroid.X - cx) * (centroid.X - cx)
                                   + (centroid.Y - cy) * (centroid.Y - cy));
            if (cDist < R * 0.2 || cDist > R * 0.90) continue;

            double maxD = 0;
            OpenCvSharp.Point farPt = centroid;
            foreach (var pt in cnt)
            {
                double d = Math.Sqrt((pt.X - cx) * (pt.X - cx) + (pt.Y - cy) * (pt.Y - cy));
                if (d > maxD && d < R * 1.05) { maxD = d; farPt = new OpenCvSharp.Point(pt.X, pt.Y); }
            }
            double score = Cv2.ContourArea(cnt) * maxD;
            if (score > bestScore) { bestScore = score; bestTip = farPt; }
        }

        if (bestScore > R * R * 0.1)
        {
            double ang = ComputeAngle(cx, cy, bestTip);
            string name = color == "red" ? "红色" : "绿色";
            Scalar drawColor = color == "red" ? Scalar.Red : Scalar.LimeGreen;
            res.pointers.Add(new PointerInfo
            {
                colorName = name, drawColor = drawColor,
                angleDeg = ang, value = AngleToValue(ang), tipPoint = bestTip
            });
        }
    }
}
