using System.Text;
using Anthropic;
using Anthropic.Models.Messages;

namespace Inkwell.Services;

public class ClaudeOcrEngine : IOcrEngine
{
    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly string _prompt;
    private readonly Effort _effort;

    public ClaudeOcrEngine(AnthropicClient client, string model, string prompt, string effort)
    {
        _client = client;
        _model = model;
        _prompt = prompt;
        _effort = effort switch
        {
            "low" => Effort.Low,
            "medium" => Effort.Medium,
            "max" => Effort.Max,
            _ => Effort.High,
        };
    }

    public async Task<string> TranscribeAsync(string imagePath, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(imagePath, ct);
        return await SendAsync(bytes, GetMediaType(imagePath));
    }

    public Task<string> TranscribePngAsync(byte[] pngBytes, CancellationToken ct = default)
        => SendAsync(pngBytes, MediaType.ImagePng);

    private async Task<string> SendAsync(byte[] imageBytes, MediaType mediaType)
    {
        var base64 = Convert.ToBase64String(imageBytes);

        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = 16000,
            OutputConfig = new OutputConfig { Effort = _effort },
            Messages = new List<MessageParam>
            {
                new()
                {
                    Role = Role.User,
                    Content = new List<ContentBlockParam>
                    {
                        new ImageBlockParam
                        {
                            Source = new Base64ImageSource
                            {
                                MediaType = mediaType,
                                Data = base64,
                            },
                        },
                        new TextBlockParam { Text = _prompt },
                    },
                },
            },
        };

        var response = await _client.Messages.Create(parameters);

        var sb = new StringBuilder();
        foreach (var text in response.Content.Select(b => b.Value).OfType<TextBlock>())
        {
            sb.Append(text.Text);
        }
        return sb.ToString();
    }

    private static MediaType GetMediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => MediaType.ImageJpeg,
        ".png" => MediaType.ImagePng,
        ".gif" => MediaType.ImageGif,
        ".webp" => MediaType.ImageWebP,
        _ => throw new NotSupportedException($"Unsupported image extension: {Path.GetExtension(path)}"),
    };
}
