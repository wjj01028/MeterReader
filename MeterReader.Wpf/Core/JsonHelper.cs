using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MeterReader.App;

public class JsonReadEntry
{
    public string Time { get; set; } = "";
    public double Red { get; set; }
    public double Green { get; set; }
    public double Black { get; set; }
}

public class ReaderDailyFile
{
    public int InstrumentNo { get; set; }
    public List<JsonReadEntry> ReaderResult { get; set; } = new();
}

public static class JsonHelper
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void AppendResult(string jsonPath, int instrumentNo, GaugeResult result)
    {
        ReaderDailyFile file;
        if (File.Exists(jsonPath))
        {
            var json = File.ReadAllText(jsonPath);
            file = JsonSerializer.Deserialize<ReaderDailyFile>(json) ?? new ReaderDailyFile();
        }
        else
        {
            file = new ReaderDailyFile { InstrumentNo = instrumentNo };
        }

        file.ReaderResult.Add(new JsonReadEntry
        {
            Time = DateTime.Now.ToString("yyyyMMdd-HH:mm:ss"),
            Red = result.redValue,
            Green = result.greenValue,
            Black = result.blackValue
        });

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(file, Options));
    }
}
