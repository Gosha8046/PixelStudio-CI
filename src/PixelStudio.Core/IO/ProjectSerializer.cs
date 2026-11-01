using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using PixelStudio.Core.Animation;
using PixelStudio.Core.Imaging;

namespace PixelStudio.Core.IO;

/// <summary>A project file could not be read; <see cref="Exception.Message"/> is user-facing (Russian).</summary>
public sealed class ProjectFormatException : Exception
{
    public ProjectFormatException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Native project format <c>*.pxstudio</c>: UTF-8 JSON with a format id and version,
/// document settings, layers (bottom → top), palette and frames. Each cel stores its RGBA
/// pixels zlib-compressed and Base64-encoded. See docs/project-format.md.
/// </summary>
public static class ProjectSerializer
{
    public const string FormatId = "PixelStudioProject";
    public const int CurrentVersion = 1;
    public const string FileExtension = ".pxstudio";
    public const string CelEncoding = "rgba8-zlib-base64";
    public const int MaxFileBytes = 64 * 1024 * 1024;

    private static readonly ProjectJsonContext JsonContext = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    });

    public static byte[] Save(SpriteDocument document)
    {
        var dto = new ProjectDto
        {
            Format = FormatId,
            FormatVersion = CurrentVersion,
            Generator = $"PixelStudio CI {typeof(ProjectSerializer).Assembly.GetName().Version?.ToString(3)}",
            Width = document.Width,
            Height = document.Height,
            Fps = document.Timeline.Fps,
            Loop = document.Timeline.Loop,
            Palette = document.Palette.Colors.Select(c => c.ToHexRgba()).ToList(),
            Layers = document.Layers.Select(l => new LayerDto
            {
                Id = l.Id,
                Name = l.Name,
                Visible = l.Visible,
                Opacity = l.Opacity,
                BlendMode = l.BlendMode.ToString(),
            }).ToList(),
            Frames = document.Frames.Select(f => new FrameDto
            {
                Id = f.Id,
                DurationMs = f.DurationMs,
                Cels = document.Layers.Select(l => new CelDto
                {
                    LayerId = l.Id,
                    Encoding = CelEncoding,
                    Data = EncodePixels(f.GetCel(l.Id).Pixels),
                }).ToList(),
            }).ToList(),
        };

        return JsonSerializer.SerializeToUtf8Bytes(dto, JsonContext.ProjectDto);
    }

    /// <exception cref="ProjectFormatException">The data is not a valid, supported project.</exception>
    public static SpriteDocument Load(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            throw new ProjectFormatException("Файл пуст.");
        }

        if (data.Length > MaxFileBytes)
        {
            throw new ProjectFormatException(
                $"Файл слишком большой ({data.Length / (1024 * 1024)} МБ). Максимальный размер проекта — {MaxFileBytes / (1024 * 1024)} МБ.");
        }

        ProjectDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(data, JsonContext.ProjectDto);
        }
        catch (JsonException ex)
        {
            string where = ex.LineNumber is { } line ? $" (строка {line + 1}, позиция {ex.BytePositionInLine + 1})" : string.Empty;
            throw new ProjectFormatException($"Файл повреждён или не является проектом PixelStudio: некорректный JSON{where}.", ex);
        }

        if (dto is null || dto.Format != FormatId)
        {
            throw new ProjectFormatException($"Это не проект PixelStudio: ожидалось поле \"format\": \"{FormatId}\".");
        }

        if (dto.FormatVersion > CurrentVersion)
        {
            throw new ProjectFormatException(
                $"Проект сохранён в формате версии {dto.FormatVersion}, а это приложение поддерживает формат до версии {CurrentVersion}. Обновите PixelStudio CI.");
        }

        if (dto.FormatVersion < 1)
        {
            throw new ProjectFormatException($"Неподдерживаемая версия формата проекта: {dto.FormatVersion}.");
        }

        // Version 1 is the only version so far; future versions add migrations here.
        return BuildDocumentV1(dto);
    }

    private static SpriteDocument BuildDocumentV1(ProjectDto dto)
    {
        if (!DocumentLimits.IsValidSize(dto.Width, dto.Height))
        {
            throw new ProjectFormatException(
                $"Недопустимый размер холста {dto.Width}×{dto.Height}: допустимо от {DocumentLimits.MinSize} до {DocumentLimits.MaxSize} пикселей по каждой стороне.");
        }

        if (dto.Fps is < DocumentLimits.MinFps or > DocumentLimits.MaxFps)
        {
            throw new ProjectFormatException($"Недопустимое значение FPS: {dto.Fps} (допустимо {DocumentLimits.MinFps}–{DocumentLimits.MaxFps}).");
        }

        var layerDtos = dto.Layers ?? [];
        if (layerDtos.Count is 0 or > DocumentLimits.MaxLayers)
        {
            throw new ProjectFormatException($"Количество слоёв должно быть от 1 до {DocumentLimits.MaxLayers}, в файле: {layerDtos.Count}.");
        }

        var frameDtos = dto.Frames ?? [];
        if (frameDtos.Count is 0 or > DocumentLimits.MaxFrames)
        {
            throw new ProjectFormatException($"Количество кадров должно быть от 1 до {DocumentLimits.MaxFrames}, в файле: {frameDtos.Count}.");
        }

        var ids = new HashSet<int>();
        var layers = new List<Layer>(layerDtos.Count);
        for (int i = 0; i < layerDtos.Count; i++)
        {
            var l = layerDtos[i] ?? throw new ProjectFormatException($"Слой №{i + 1}: пустая запись.");
            if (l.Id <= 0 || !ids.Add(l.Id))
            {
                throw new ProjectFormatException($"Слой №{i + 1}: некорректный или повторяющийся id {l.Id}.");
            }

            if (l.Opacity is < 0 or > 100)
            {
                throw new ProjectFormatException($"Слой «{l.Name}»: непрозрачность {l.Opacity} вне диапазона 0–100.");
            }

            if (!Enum.TryParse<BlendMode>(l.BlendMode, ignoreCase: false, out var mode) || !Enum.IsDefined(mode) ||
                int.TryParse(l.BlendMode, out _))
            {
                throw new ProjectFormatException($"Слой «{l.Name}»: неизвестный режим смешивания \"{l.BlendMode}\".");
            }

            layers.Add(Layer.FromState(new LayerState(l.Id, Layer.NormalizeName(l.Name), l.Visible, l.Opacity, mode)));
        }

        int pixelCount = dto.Width * dto.Height;
        var frames = new List<Frame>(frameDtos.Count);
        for (int i = 0; i < frameDtos.Count; i++)
        {
            var f = frameDtos[i] ?? throw new ProjectFormatException($"Кадр №{i + 1}: пустая запись.");
            if (f.Id <= 0 || !ids.Add(f.Id))
            {
                throw new ProjectFormatException($"Кадр №{i + 1}: некорректный или повторяющийся id {f.Id}.");
            }

            if (f.DurationMs is { } ms && ms is < DocumentLimits.MinFrameDurationMs or > DocumentLimits.MaxFrameDurationMs)
            {
                throw new ProjectFormatException(
                    $"Кадр №{i + 1}: длительность {ms} мс вне диапазона {DocumentLimits.MinFrameDurationMs}–{DocumentLimits.MaxFrameDurationMs} мс.");
            }

            var cels = new Dictionary<int, Canvas>(layers.Count);
            foreach (var cel in f.Cels ?? [])
            {
                if (cel is null || layers.All(l => l.Id != cel.LayerId))
                {
                    throw new ProjectFormatException($"Кадр №{i + 1}: данные ссылаются на несуществующий слой {cel?.LayerId}.");
                }

                if (cels.ContainsKey(cel.LayerId))
                {
                    throw new ProjectFormatException($"Кадр №{i + 1}: повторяющиеся данные для слоя {cel.LayerId}.");
                }

                if (cel.Encoding != CelEncoding)
                {
                    throw new ProjectFormatException($"Кадр №{i + 1}: неподдерживаемая кодировка пикселей \"{cel.Encoding}\".");
                }

                var pixels = DecodePixels(cel.Data, pixelCount)
                    ?? throw new ProjectFormatException($"Кадр №{i + 1}, слой {cel.LayerId}: пиксельные данные повреждены.");
                cels[cel.LayerId] = new Canvas(dto.Width, dto.Height, pixels);
            }

            // Missing cels are allowed and mean "fully transparent".
            foreach (var layer in layers)
            {
                if (!cels.ContainsKey(layer.Id))
                {
                    cels[layer.Id] = new Canvas(dto.Width, dto.Height);
                }
            }

            frames.Add(new Frame(f.Id, f.DurationMs, cels));
        }

        var palette = new List<Color32>();
        foreach (var hex in dto.Palette ?? [])
        {
            if (!Color32.TryParseHex(hex, out var color))
            {
                throw new ProjectFormatException($"Палитра: некорректный цвет \"{hex}\".");
            }

            palette.Add(color);
        }

        if (palette.Count > DocumentLimits.MaxPaletteColors)
        {
            throw new ProjectFormatException($"Палитра содержит больше {DocumentLimits.MaxPaletteColors} цветов.");
        }

        return SpriteDocument.CreateFromParts(dto.Width, dto.Height, layers, frames, dto.Fps, dto.Loop ?? true, palette);
    }

    private static string EncodePixels(Color32[] pixels)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(MemoryMarshal.AsBytes(pixels.AsSpan()));
        }

        return Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Decodes and inflates exactly <paramref name="pixelCount"/> pixels; null on any corruption.</summary>
    private static Color32[]? DecodePixels(string? base64, int pixelCount)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }

        var compressed = new byte[base64.Length * 3 / 4 + 3];
        if (!Convert.TryFromBase64String(base64, compressed, out int compressedLength))
        {
            return null;
        }

        var pixels = new Color32[pixelCount];
        var target = MemoryMarshal.AsBytes(pixels.AsSpan());
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(compressed, 0, compressedLength), CompressionMode.Decompress);
            int total = 0;
            while (total < target.Length)
            {
                int read = zlib.Read(target[total..]);
                if (read == 0)
                {
                    return null; // Too short.
                }

                total += read;
            }

            // Bounded read: more data than expected means the cel does not belong to this canvas size.
            return zlib.ReadByte() == -1 ? pixels : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}

internal sealed class ProjectDto
{
    public string? Format { get; set; }
    public int FormatVersion { get; set; }
    public string? Generator { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }
    public bool? Loop { get; set; }
    public List<string>? Palette { get; set; }
    public List<LayerDto>? Layers { get; set; }
    public List<FrameDto>? Frames { get; set; }
}

internal sealed class LayerDto
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public bool Visible { get; set; } = true;
    public int Opacity { get; set; } = 100;
    public string? BlendMode { get; set; }
}

internal sealed class FrameDto
{
    public int Id { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? DurationMs { get; set; }

    public List<CelDto>? Cels { get; set; }
}

internal sealed class CelDto
{
    public int LayerId { get; set; }
    public string? Encoding { get; set; }
    public string? Data { get; set; }
}

[JsonSerializable(typeof(ProjectDto))]
internal sealed partial class ProjectJsonContext : JsonSerializerContext;
