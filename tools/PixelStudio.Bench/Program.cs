using System.Runtime.InteropServices;
using PixelStudio.Core;
using PixelStudio.Core.Diagnostics;
using PixelStudio.Core.Imaging;
using PixelStudio.Core.IO;

// Usage:
//   dotnet run -c Release --project tools/PixelStudio.Bench                 -> performance table
//   dotnet run -c Release --project tools/PixelStudio.Bench -- --sample DIR -> writes a sample
//       project, frame PNG, sprite sheet PNG + JSON into DIR (used to cross-check the PNG
//       encoder with an independent decoder and to produce docs/examples).
if (args.Length == 2 && args[0] == "--sample")
{
    WriteSample(args[1]);
    return;
}

Console.WriteLine($".NET {Environment.Version}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine(PerfProbe.FormatTable(PerfProbe.RunAll(scale: 5)));

static void WriteSample(string directory)
{
    Directory.CreateDirectory(directory);
    var doc = SpriteDocument.Create(16, 16);
    var body = doc.Layers[0];
    var shade = doc.Layers.AddLayer("Тень");
    doc.Layers.SetBlendMode(shade.Id, BlendMode.Multiply);
    doc.Layers.SetOpacity(shade.Id, 60);
    doc.Timeline.AddFrame();
    doc.Timeline.AddFrame();
    doc.Timeline.AddFrame();
    doc.Timeline.SetFps(8);
    doc.Timeline.SetFrameDuration(doc.Frames[3].Id, 250);

    var orange = new Color32(0xEF, 0x7D, 0x57);
    var dark = new Color32(0x5D, 0x27, 0x5D);
    for (int i = 0; i < doc.Frames.Count; i++)
    {
        var frame = doc.Frames[i];
        var cel = frame.GetCel(body.Id);
        var dirty = new DirtyRect();
        int bob = i % 2;
        for (int y = 4; y < 12; y++)
        {
            cel.DrawLine(4, y + bob, 11, y + bob, orange, ref dirty);
        }

        cel.SetPixel(6, 6 + bob, Color32.Black);
        cel.SetPixel(9, 6 + bob, Color32.Black);
        frame.GetCel(shade.Id).DrawLine(4, 11 + bob, 11, 11 + bob, dark, ref dirty);
        frame.GetCel(shade.Id).DrawLine(3 + i, 14, 12 - i, 14, new Color32(0, 0, 0, 120), ref dirty);
    }

    File.WriteAllBytes(Path.Combine(directory, "slime.pxstudio"), ProjectSerializer.Save(doc));
    File.WriteAllBytes(Path.Combine(directory, "slime_frame0.png"), Exporter.ExportFramePng(doc, 0));
    var sheet = Exporter.ExportSpriteSheet(doc, new SpriteSheetOptions(Columns: 2, Spacing: 2, ImageFileName: "slime.png"));
    File.WriteAllBytes(Path.Combine(directory, "slime.png"), sheet.Png);
    File.WriteAllText(Path.Combine(directory, "slime.json"), sheet.Json + "\n");

    // Raw composites for an external decoder to compare against.
    for (int i = 0; i < doc.Frames.Count; i++)
    {
        var composite = Compositor.CompositeFrame(doc, doc.Frames[i]);
        File.WriteAllBytes(Path.Combine(directory, $"frame{i}.rgba"), MemoryMarshal.AsBytes(composite.Pixels.AsSpan()).ToArray());
    }

    Console.WriteLine($"Sample written to {Path.GetFullPath(directory)}");
}
