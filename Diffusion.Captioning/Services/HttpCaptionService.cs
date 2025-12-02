using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Captioning.Models;

namespace Diffusion.Captioning.Services;

/// <summary>
/// OpenAI-compatible HTTP caption service (e.g., LM Studio / cloud endpoints).
/// Expects /v1/chat/completions with vision support using image_url data URLs.
/// </summary>
public class HttpCaptionService : ICaptionService, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;

    public HttpCaptionService(string baseUrl, string model, string? apiKey = null, TimeSpan? timeout = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _http = new HttpClient
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(120)
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    public async Task<CaptionResult> CaptionImageAsync(string imagePath, string? prompt = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(imagePath)) throw new FileNotFoundException(imagePath);
        prompt ??= JoyCaptionService.PROMPT_DETAILED;

        var ext = Path.GetExtension(imagePath).ToLowerInvariant().Trim('.');
        var mime = ext switch { "jpg" or "jpeg" => "image/jpeg", "png" => "image/png", "webp" => "image/webp", "gif" => "image/gif", _ => "application/octet-stream" };
        var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken).ConfigureAwait(false);
        var b64 = Convert.ToBase64String(bytes);
        var dataUrl = $"data:{mime};base64,{b64}";

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");

        var body = new
        {
            model = _model,
            messages = new object[]
            {
                new {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = prompt },
                        new { type = "input_image", image_url = dataUrl }
                    }
                }
            },
            temperature = 0.7,
            max_tokens = 512
        };

        var json = JsonSerializer.Serialize(body);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var respText = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(respText);
        var root = doc.RootElement;
        // Try to parse OpenAI-compatible choices[0].message.content
        string caption = root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
            ? choices[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty
            : respText; // fallback raw

        sw.Stop();
        return new CaptionResult(caption, prompt, tokenCount: 0, generationTimeMs: sw.Elapsed.TotalMilliseconds);
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
