using System.IO;
using System.Reflection;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace MeterReader.Wpf.Core;

/// <summary>YOLOv8 ONNX 模型推理器 — 检测压力表指针和读数</summary>
public class YoloDetector : IDisposable
{
    private Net? _net;
    private readonly string? _modelPath;
    private readonly int _imgSize = 640;
    private readonly float _confThreshold = 0.3f;
    private readonly float _nmsThreshold = 0.45f;
    private readonly string[] _classNames;

    public bool IsLoaded => _net != null && !_net.Empty();

    public YoloDetector()
    {
        _modelPath = FindModel();
        _classNames = ["black_pointer", "red_pointer", "green_pointer", "dial_center"];
        if (_modelPath != null)
        {
            try
            {
                _net = CvDnn.ReadNetFromOnnx(_modelPath);
                // 尝试用 CUDA，不可用则回退 CPU
                if (_net != null)
                {
                    _net.SetPreferableBackend(Backend.DEFAULT);
                    _net.SetPreferableTarget(Target.CPU);
                }
            }
            catch { _net?.Dispose(); _net = null; }
        }
        else
        {
            _net = null;
        }
    }

    private static string? FindModel()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        for (int i = 0; i < 5; i++)
        {
            string candidate = Path.Combine(dir, "assist", "gauge_yolov8.onnx");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
            if (dir == null) break;
        }
        return null;
    }

    /// <summary>检测结果</summary>
    public struct YoloDetection
    {
        public string ClassName;
        public float Confidence;
        public Rect Box;
        public OpenCvSharp.Point Center;  // bbox中心
    }

    /// <summary>执行推理，返回检测到的目标列表</summary>
    public List<YoloDetection> Detect(Mat src)
    {
        var results = new List<YoloDetection>();
        if (!IsLoaded) return results;

        try
        {
            // 1. 预处理：resize + normalize + blob
            int w = src.Width, h = src.Height;
            float scale = Math.Min((float)_imgSize / w, (float)_imgSize / h);
            int newW = (int)(w * scale), newH = (int)(h * scale);

            using var resized = new Mat();
            Cv2.Resize(src, resized, new OpenCvSharp.Size(newW, newH));
            // 填充到 640x640
            using var padded = new Mat(_imgSize, _imgSize, MatType.CV_8UC3, Scalar.Black);
            var roi = new Mat(padded, new Rect((_imgSize - newW) / 2, (_imgSize - newH) / 2, newW, newH));
            resized.CopyTo(roi);

            using var blob = CvDnn.BlobFromImage(padded, 1.0 / 255.0,
                new OpenCvSharp.Size(_imgSize, _imgSize), new Scalar(0, 0, 0), true, false);
            _net!.SetInput(blob);

            // 2. 前向推理
            using var output = _net.Forward();
            // output shape: [1, 84, 8400] 或 [1, 8400, 84]

            // 3. 解析输出
            ParseOutput(output, (float)w / _imgSize, (float)h / _imgSize, results);
        }
        catch { /* YOLO推定失败则回退传统方法 */ }

        return results;
    }

    private void ParseOutput(Mat output, float scaleX, float scaleY, List<YoloDetection> results)
    {
        int numClasses = _classNames.Length;
        int dims = output.Dims;
        int rows, cols;

        if (dims == 3)
        {
            // [1, 84, 8400] format
            int[] shape = new int[3];
            for (int i = 0; i < 3; i++) shape[i] = output.Size(i);
            rows = shape[2];  // 8400
            cols = shape[1];  // 84
        }
        else if (dims == 2)
        {
            rows = output.Rows;
            cols = output.Cols;
        }
        else return;

        int numAnchors = rows;
        var candidateBoxes = new List<Rect2d>();
        var candidateConfs = new List<float>();
        var candidateClasses = new List<int>();

        for (int i = 0; i < numAnchors; i++)
        {
            // 前4列 = cx, cy, w, h（归一化坐标）
            float bx = output.At<float>(i, 0);
            float by = output.At<float>(i, 1);
            float bw = output.At<float>(i, 2);
            float bh = output.At<float>(i, 3);

            // 后面每列 = class conf
            float maxConf = 0; int bestClass = 0;
            for (int c = 0; c < Math.Min(numClasses, cols - 4); c++)
            {
                float conf = output.At<float>(i, 4 + c);
                if (conf > maxConf) { maxConf = conf; bestClass = c; }
            }

            if (maxConf < _confThreshold) continue;

            // 坐标转换：归一化 → 像素
            float x = (bx - bw / 2) * _imgSize * scaleX;
            float y = (by - bh / 2) * _imgSize * scaleY;
            float width = bw * _imgSize * scaleX;
            float height = bh * _imgSize * scaleY;

            candidateBoxes.Add(new Rect2d(x, y, width, height));
            candidateConfs.Add(maxConf);
            candidateClasses.Add(bestClass);
        }

        // NMS 去重
        int[] nmsIndices;
        if (candidateBoxes.Count > 0)
        {
            CvDnn.NMSBoxes(candidateBoxes, candidateConfs, _confThreshold, _nmsThreshold, out nmsIndices);
        }
        else
        {
            nmsIndices = [];
        }

        foreach (int idx in nmsIndices)
        {
            var box = candidateBoxes[idx];
            results.Add(new YoloDetection
            {
                ClassName = _classNames[candidateClasses[idx]],
                Confidence = candidateConfs[idx],
                Box = new Rect((int)box.X, (int)box.Y, (int)box.Width, (int)box.Height),
                Center = new OpenCvSharp.Point((int)(box.X + box.Width / 2), (int)(box.Y + box.Height / 2))
            });
        }
    }

    public void Dispose() { _net?.Dispose(); _net = null; }
}
