using System.IO;
using OpenCvSharp;

namespace MeterReader.Wpf.ImageReader;

/// <summary>流量计识别结果</summary>
public class FlowResult
{
    public string method { get; set; } = "";
    public string reading { get; set; } = "";
    public string details { get; set; } = "";
    public int tickCount { get; set; }
}

/// <summary>流量计识别器</summary>
public static class FlowMeterRecognizer
{
    /// <summary>是否保存识别过程中生成的中间调试图片</summary>
    public static bool SaveDebugImages { get; set; }
    
    private static readonly float[] FlowScaleValues = [100, 200, 400, 600, 800, 1000];

    public static FlowResult Recognize(Mat src, Mat enhanced, string dir, string baseName)
    {
        var res = new FlowResult { method = "流量计", reading = "--", details = "", tickCount = 0 };
        int W = src.Width, H = src.Height;

        // Step 1: 找玻璃管
        OpenCvSharp.Rect tube = FindGlassTube(enhanced, W, H);
        using var tubeGray = new Mat(enhanced, tube);
        using var tubeSrc = new Mat(src, tube);
        int tw = tube.Width, th = tube.Height;

        // Step 2: 找浮子（高亮区域）
        using var hsv = new Mat(); Cv2.CvtColor(tubeSrc, hsv, ColorConversionCodes.BGR2HSV);
        using var brightMask = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 0, 150), new Scalar(180, 80, 255), brightMask);
        using var kb = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(7, 7));
        Cv2.MorphologyEx(brightMask, brightMask, MorphTypes.Close, kb);

        Cv2.FindContours(brightMask, out var bcs, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        int ballY = th / 2;
        if (bcs.Length > 0)
        {
            var bestBlob = bcs.OrderByDescending(c => Cv2.ContourArea(c)).First();
            var moments = Cv2.Moments(bestBlob);
            if (moments.M00 > 1)
                ballY = (int)(moments.M01 / moments.M00);
        }

        // Step 3: 检测刻度
        var ticks = DetectTicks(tubeGray, tw, th);
        res.tickCount = ticks.Count;

        // Step 4: 读数计算
        float ratio = (float)ballY / th;
        res.reading = (FlowScaleValues[0] + ratio * (FlowScaleValues[^1] - FlowScaleValues[0])).ToString("F1");
        res.details = $"流量计 | 球Y:{ballY}px 刻度:{ticks.Count}条";

        // 绘制标注
        DrawMarkers(src, tube, ticks, ballY, res.reading, dir, baseName);
        return res;
    }

    private static OpenCvSharp.Rect FindGlassTube(Mat enhanced, int W, int H)
    {
        using var edges = new Mat();
        Cv2.Canny(enhanced, edges, 25, 75);
        using var kclose = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(5, 25));
        using var closed = new Mat();
        Cv2.MorphologyEx(edges, closed, MorphTypes.Close, kclose);
        Cv2.FindContours(closed, out var cs, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        if (cs.Length > 0)
        {
            var sorted = cs.OrderByDescending(c =>
            {
                var r = Cv2.BoundingRect(c);
                return r.Height * r.Height / Math.Max(1.0, r.Width);
            }).ToList();

            foreach (var c in sorted.Take(3))
            {
                var r = Cv2.BoundingRect(c);
                if (r.Height >= H * 0.35 && r.Height / (double)Math.Max(1, r.Width) >= 2.0)
                {
                    int pad = Math.Min(r.Width / 4, 15);
                    int nx = Math.Max(0, r.X - pad);
                    int nw = Math.Min(W - nx, r.Width + pad * 2);
                    return new OpenCvSharp.Rect(nx, r.Y, nw, r.Height);
                }
            }
        }
        return new OpenCvSharp.Rect(W / 4, H / 20, W / 2, H * 9 / 10);
    }

    private static List<float> DetectTicks(Mat tubeGray, int tw, int th)
    {
        var result = new List<float>();
        using var sobY = new Mat();
        Cv2.Sobel(tubeGray, sobY, MatType.CV_8UC1, 0, 1, 3);
        float[] proj = new float[th];
        for (int y = 0; y < th; y++)
        {
            using var row = new Mat(sobY, new OpenCvSharp.Rect(0, y, tw, 1));
            proj[y] = (float)row.Mean().Val0;
        }
        if (th >= 5)
            for (int i = 2; i < th - 2; i++)
                proj[i] = (proj[i - 2] + proj[i - 1] + proj[i] + proj[i + 1] + proj[i + 2]) / 5f;
        float sum = 0;
        for (int i = 0; i < th; i++) sum += proj[i];
        float avg = sum / th;
        int minGap = Math.Max(5, th / 30);
        for (int i = 2; i < th - 2; i++)
        {
            if (proj[i] > avg * 1.2 && proj[i] >= proj[i - 1] && proj[i] >= proj[i + 1])
            {
                result.Add(i);
                i += minGap;
            }
        }
        return result;
    }

    private static void DrawMarkers(Mat src, OpenCvSharp.Rect tube, List<float> ticks, int ballY, string reading, string dir, string baseName)
    {
        using var marked = src.Clone();
        Cv2.Rectangle(marked, tube, Scalar.LimeGreen, 2);
        Cv2.PutText(marked, "Tube", new OpenCvSharp.Point(tube.X + 5, Math.Max(15, tube.Y - 8)),
            HersheyFonts.HersheySimplex, 0.6, Scalar.LimeGreen, 2);

        foreach (var ty in ticks)
        {
            int absY = tube.Y + (int)ty;
            Cv2.Line(marked, new OpenCvSharp.Point(tube.X, absY),
                new OpenCvSharp.Point(tube.X + 8, absY), Scalar.Yellow, 2);
        }

        int ballAbsY = tube.Y + ballY;
        int ballAbsX = tube.X + tube.Width / 2;
        int ballR = Math.Max(8, tube.Width / 6);
        Cv2.Circle(marked, new OpenCvSharp.Point(ballAbsX, ballAbsY), ballR, Scalar.Red, 2);
        Cv2.Line(marked, new OpenCvSharp.Point(ballAbsX - ballR - 5, ballAbsY),
            new OpenCvSharp.Point(ballAbsX + ballR + 5, ballAbsY), Scalar.Red, 1);

        Cv2.PutText(marked, $"Reading: {reading}",
            new OpenCvSharp.Point(10, 30), HersheyFonts.HersheySimplex, 0.9, Scalar.Red, 2);

        if (SaveDebugImages) Cv2.ImWrite(Path.Combine(dir, baseName + "_Marked.png"), marked);
    }
}
