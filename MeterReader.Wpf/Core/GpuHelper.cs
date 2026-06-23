using System.Diagnostics;
using OpenCvSharp;

namespace MeterReader.Wpf.Core;

public static class GpuHelper
{
    private static bool? _openClAvailable;
    private static string _gpuInfo = "";

    public static bool IsGpuAvailable
    {
        get
        {
            if (_openClAvailable.HasValue) return _openClAvailable.Value;
            try
            {
                using var testMat = new Mat(100, 100, MatType.CV_8UC3, new Scalar(0, 0, 0));
                using var testUmat = new UMat();
                testMat.CopyTo(testUmat);
                using var result = new UMat();
                Cv2.CvtColor(testUmat, result, ColorConversionCodes.BGR2GRAY);
                _openClAvailable = true;
                _gpuInfo = "OpenCL GPU enabled";
            }
            catch
            {
                _openClAvailable = false;
                _gpuInfo = "CPU only (no OpenCL)";
            }
            return _openClAvailable.Value;
        }
    }

    public static string GpuInfo => _gpuInfo;

    public static Mat PreprocessGray(Mat src)
        => IsGpuAvailable ? PreprocessGrayGpu(src) : PreprocessGrayCpu(src);

    public static Mat PreprocessColor(Mat src)
        => IsGpuAvailable ? PreprocessColorGpu(src) : PreprocessColorCpu(src);

    public static Mat CorrectIllumination(Mat gray)
        => IsGpuAvailable ? CorrectIlluminationGpu(gray) : CorrectIlluminationCpu(gray);

    // ==================== GPU (UMat/OpenCL) ====================

    private static Mat PreprocessGrayGpu(Mat src)
    {
        using var srcU = new UMat(); src.CopyTo(srcU);
        using var grayU = new UMat();
        Cv2.CvtColor(srcU, grayU, ColorConversionCodes.BGR2GRAY);
        using var blurredU = new UMat();
        Cv2.GaussianBlur(grayU, blurredU, new OpenCvSharp.Size(5, 5), 0);
        using var clahe = Cv2.CreateCLAHE(2.0, new OpenCvSharp.Size(8, 8));
        using var enhancedU = new UMat();
        clahe.Apply(blurredU, enhancedU);
        var result = new Mat(); enhancedU.CopyTo(result); return result;
    }

    private static Mat PreprocessColorGpu(Mat src)
    {
        using var srcU = new UMat(); src.CopyTo(srcU);
        using var labU = new UMat();
        Cv2.CvtColor(srcU, labU, ColorConversionCodes.BGR2Lab);
        // Channel ops require Mat; copy back for split/merge
        using var labM = new Mat();
        labU.CopyTo(labM);
        var ch = Cv2.Split(labM);
        using var clahe = Cv2.CreateCLAHE(2.0, new OpenCvSharp.Size(8, 8));
        clahe.Apply(ch[0], ch[0]);
        Cv2.Merge(ch, labM);
        foreach (var c in ch) c.Dispose();
        // Back to GPU for final CvtColor
        using var mergedU = new UMat(); labM.CopyTo(mergedU);
        using var bgrU = new UMat();
        Cv2.CvtColor(mergedU, bgrU, ColorConversionCodes.Lab2BGR);
        var result = new Mat(); bgrU.CopyTo(result); return result;
    }

    private static Mat CorrectIlluminationGpu(Mat gray)
    {
        using var srcU = new UMat(); gray.CopyTo(srcU);
        using var blurU = new UMat();
        Cv2.GaussianBlur(srcU, blurU, new OpenCvSharp.Size(51, 51), 0);
        using var flatF = new UMat(); srcU.ConvertTo(flatF, MatType.CV_32F);
        using var illumF = new UMat(); blurU.ConvertTo(illumF, MatType.CV_32F, 1.0, 1.0);
        using var divU = new UMat();
        Cv2.Divide(flatF, illumF, divU, 128.0);
        using var byteU = new UMat(); divU.ConvertTo(byteU, MatType.CV_8UC1);
        using var clahe = Cv2.CreateCLAHE(3.0, new OpenCvSharp.Size(8, 8));
        using var enhancedU = new UMat();
        clahe.Apply(byteU, enhancedU);
        var result = new Mat(); enhancedU.CopyTo(result); return result;
    }

    // ==================== CPU fallback ====================

    private static Mat PreprocessGrayCpu(Mat src)
    {
        using var gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        using var denoised = new Mat();
        Cv2.GaussianBlur(gray, denoised, new OpenCvSharp.Size(5, 5), 0);
        using var clahe = Cv2.CreateCLAHE(2.0, new OpenCvSharp.Size(8, 8));
        using var preprocessed = new Mat();
        clahe.Apply(denoised, preprocessed);
        return preprocessed.Clone();
    }

    private static Mat PreprocessColorCpu(Mat src)
    {
        using var lab = new Mat();
        Cv2.CvtColor(src, lab, ColorConversionCodes.BGR2Lab);
        var ch = Cv2.Split(lab);
        using var c = Cv2.CreateCLAHE(2.0, new OpenCvSharp.Size(8, 8));
        c.Apply(ch[0], ch[0]);
        Cv2.Merge(ch, lab);
        foreach (var x in ch) x.Dispose();
        using var result = new Mat();
        Cv2.CvtColor(lab, result, ColorConversionCodes.Lab2BGR);
        return result.Clone();
    }

    private static Mat CorrectIlluminationCpu(Mat gray)
    {
        using var illumination = new Mat();
        Cv2.GaussianBlur(gray, illumination, new OpenCvSharp.Size(51, 51), 0);
        using var flatFloat = new Mat(); gray.ConvertTo(flatFloat, MatType.CV_32F);
        using var illumFloat = new Mat(); illumination.ConvertTo(illumFloat, MatType.CV_32F, 1.0, 1.0);
        Cv2.Divide(flatFloat, illumFloat, flatFloat, 128.0);
        using var flat = new Mat(); flatFloat.ConvertTo(flat, MatType.CV_8UC1);
        using var clahe = Cv2.CreateCLAHE(3.0, new OpenCvSharp.Size(8, 8));
        using var enhanced = new Mat(); clahe.Apply(flat, enhanced);
        return enhanced.Clone();
    }
}