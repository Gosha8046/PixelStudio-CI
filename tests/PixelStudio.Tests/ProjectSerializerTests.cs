using System.Text;
using System.Text.Json.Nodes;
using PixelStudio.Core.Imaging;
using PixelStudio.Core.IO;
using PixelStudio.Tests.Support;

namespace PixelStudio.Tests;

public class ProjectSerializerTests
{
    private static SpriteDocument BuildRichDocument()
    {
        var doc = SpriteDocument.Create(24, 17);
        var l1 = doc.Layers[0];
        var l2 = doc.Layers.AddLayer("Контур");
        var l3 = doc.Layers.AddLayer("Блики");
        doc.Layers.SetOpacity(l2.Id, 35);
        doc.Layers.SetBlendMode(l2.Id, BlendMode.Multiply);
        doc.Layers.SetBlendMode(l3.Id, BlendMode.Screen);
        doc.Layers.SetVisible(l3.Id, false);
        doc.Timeline.AddFrame();
        doc.Timeline.AddFrame();
        doc.Timeline.SetFps(15);
        doc.Timeline.SetLoop(false);
        doc.Timeline.SetFrameDuration(doc.Frames[1].Id, 333);
        doc.AddPaletteColor(new Color32(1, 2, 3, 4));

        var random = new Random(7);
        foreach (var frame in doc.Frames)
        {
            foreach (var layer in new[] { l1, l2 })
            {
                var px = frame.GetCel(layer.Id).Pixels;
                for (int i = 0; i < px.Length; i += 3)
                {
                    px[i] = new Color32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                }
            }
        }

        // Invisible RGB under alpha 0 must survive too (lossless means lossless).
        doc.Frames[2].GetCel(l3.Id).Pixels[5] = new Color32(9, 8, 7, 0);
        return doc;
    }

    [Fact]
    public void SaveThenLoad_RestoresEverythingWithoutLoss()
    {
        var doc = BuildRichDocument();
        var bytes = ProjectSerializer.Save(doc);

        var loaded = ProjectSerializer.Load(bytes);

        Assert.Equal(TestDocs.Describe(doc), TestDocs.Describe(loaded));
        Assert.Equal(new Color32(9, 8, 7, 0), loaded.Frames[2].GetCel(loaded.Layers[2].Id).Pixels[5]);
        Assert.False(loaded.History.IsDirty);
        Assert.False(loaded.History.CanUndo);

        // Deterministic output: saving the loaded document yields identical bytes.
        Assert.Equal(bytes, ProjectSerializer.Save(loaded));

        // New ids never collide with loaded ones.
        var newLayer = loaded.Layers.AddLayer();
        Assert.DoesNotContain(doc.Layers.Select(l => l.Id).Concat(doc.Frames.Select(f => f.Id)), id => id == newLayer.Id);
    }

    [Fact]
    public void Save_WritesVersionedHumanReadableHeader()
    {
        var json = JsonNode.Parse(ProjectSerializer.Save(BuildRichDocument()))!;
        Assert.Equal("PixelStudioProject", (string?)json["format"]);
        Assert.Equal(1, (int?)json["formatVersion"]);
        Assert.Equal("Контур", (string?)json["layers"]![1]!["name"]);
        Assert.Equal("Multiply", (string?)json["layers"]![1]!["blendMode"]);
    }

    [Fact]
    public void Load_TreatsMissingCelsAsTransparent()
    {
        var json = JsonNode.Parse(ProjectSerializer.Save(SpriteDocument.Create(2, 2)))!;
        json["frames"]![0]!["cels"] = new JsonArray();

        var loaded = ProjectSerializer.Load(Encoding.UTF8.GetBytes(json.ToJsonString()));

        Assert.True(loaded.Frames[0].GetCel(loaded.Layers[0].Id).IsFullyTransparent());
    }

    public static TheoryData<string, string> CorruptedInputs => new()
    {
        { "", "Файл пуст" },
        { "\u0089PNG binary garbage", "некорректный JSON" },
        { "{\"format\":\"Other\",\"formatVersion\":1}", "Это не проект PixelStudio" },
        { "{\"format\":\"PixelStudioProject\",\"formatVersion\":99}", "формате версии 99" },
        { "{\"format\":\"PixelStudioProject\",\"formatVersion\":0}", "Неподдерживаемая версия" },
        { "{\"format\":\"PixelStudioProject\",\"formatVersion\":1,\"width\":5000,\"height\":4,\"fps\":8}", "Недопустимый размер холста 5000×4" },
        { "{\"format\":\"PixelStudioProject\",\"formatVersion\":1,\"width\":4,\"height\":4,\"fps\":8,\"layers\":[]}", "Количество слоёв" },
        { "{\"format\":\"PixelStudioProject\",\"formatVersion\":1,\"width\":4,\"height\":4,\"fps\":8", "некорректный JSON" },
    };

    [Theory]
    [MemberData(nameof(CorruptedInputs))]
    public void Load_RejectsInvalidFilesWithClearMessages(string content, string expectedFragment)
    {
        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Load(Encoding.UTF8.GetBytes(content)));
        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void Load_RejectsCorruptedPixelData()
    {
        var json = JsonNode.Parse(ProjectSerializer.Save(SpriteDocument.Create(4, 4)))!;
        var cel = json["frames"]![0]!["cels"]![0]!;

        cel["data"] = "not base64!!";
        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Load(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Contains("пиксельные данные повреждены", ex.Message);

        // Valid zlib stream of the wrong size (pixels for an 8x8 canvas in a 4x4 project).
        var bigger = JsonNode.Parse(ProjectSerializer.Save(SpriteDocument.Create(8, 8)))!;
        cel["data"] = (string?)bigger["frames"]![0]!["cels"]![0]!["data"];
        ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Load(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Contains("пиксельные данные повреждены", ex.Message);
    }

    [Fact]
    public void Load_RejectsInconsistentReferences()
    {
        var json = JsonNode.Parse(ProjectSerializer.Save(SpriteDocument.Create(2, 2)))!;
        json["frames"]![0]!["cels"]![0]!["layerId"] = 999;
        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Load(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Contains("несуществующий слой 999", ex.Message);

        json = JsonNode.Parse(ProjectSerializer.Save(SpriteDocument.Create(2, 2)))!;
        json["layers"]![0]!["blendMode"] = "Overlay";
        ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Load(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Contains("неизвестный режим смешивания", ex.Message);

        json = JsonNode.Parse(ProjectSerializer.Save(SpriteDocument.Create(2, 2)))!;
        json["frames"]![0]!["id"] = (int)json["layers"]![0]!["id"]!;
        ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Load(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Contains("повторяющийся id", ex.Message);
    }
}
