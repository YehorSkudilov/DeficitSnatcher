using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Settings;

namespace DeficitSnatcher.Api.Ai;

/// <summary>
/// ChatGPT (OpenAI's Chat Completions API), answering in JSON. Off while OpenAI__ApiKey is unset or the AI is turned off
/// in Settings. Every call is kept (AiCall) with its purpose, site, size and answer, for each site's AI log.
/// </summary>
public sealed class OpenAiClient(HttpClient http, IConfiguration config, SettingsStore settingsStore, IServiceScopeFactory scopes,
    ILogger<OpenAiClient> log)
{
    private readonly string _key = config["OpenAI:ApiKey"] ?? "";
    private readonly string _baseUrl = (config["OpenAI:BaseUrl"] is { Length: > 0 } b ? b : "https://api.openai.com/v1").TrimEnd('/');

    public bool HasKey => _key.Length > 0;

    public async Task<bool> AvailableAsync(CancellationToken ct = default) => HasKey && (await settingsStore.GetAsync(ct)).AiEnabled;

    /// <summary>A text part of the question.</summary>
    public static object Text(string text) => new { type = "text", text };

    /// <summary>An image part (JPEG/PNG bytes).</summary>
    public static object Image(byte[] bytes, string contentType = "image/jpeg", string detail = "auto") =>
        new { type = "image_url", image_url = new { url = $"data:{contentType};base64,{Convert.ToBase64String(bytes)}", detail } };

    public sealed class AiException(string message) : Exception(message);

    /// <summary>Asks and returns the answer's JSON object. Throws AiException when the AI is off or the call fails.</summary>
    public async Task<JsonElement> AskJsonAsync(string purpose, int? siteId, int? purchaseId, string system, IEnumerable<object> userParts,
        CancellationToken ct)
    {
        if (!HasKey)
            throw new AiException("The AI is off: set the OPENAI_API_KEY secret.");
        var settings = await settingsStore.GetAsync(ct);
        if (!settings.AiEnabled)
            throw new AiException("The AI is turned off in Settings.");

        var body = new Dictionary<string, object>
        {
            ["model"] = settings.AiModel,
            ["messages"] = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = userParts.ToArray() },
            },
            ["response_format"] = new { type = "json_object" },
        };
        if (settings.AiReasoningEffort.Length > 0)
            body["reasoning_effort"] = settings.AiReasoningEffort;

        var watch = Stopwatch.StartNew();
        string? error = null;
        string answer = "";
        int inTokens = 0, outTokens = 0;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions") 
                {
                    // A plain body with its length (not streamed): what every proxy in between handles.
                    Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
                using var response = await http.SendAsync(request, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new AiException($"OpenAI answered {(int)response.StatusCode}: {Short(ErrorMessage(text), 500)}");
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("usage", out var usage))
                {
                    inTokens = usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0;
                    outTokens = usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : 0;
                }
                answer = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                break;
            }
            using var parsed = JsonDocument.Parse(answer);
            return parsed.RootElement.Clone();
        }
        catch (JsonException)
        {
            error = "The AI's answer wasn't JSON: " + Short(answer, 300);
            throw new AiException(error);
        }
        catch (AiException e)
        {
            error = e.Message;
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            error = e is TaskCanceledException ? "The AI took too long to answer." : "Couldn't reach OpenAI: " + e.Message;
            throw new AiException(error);
        }
        finally
        {
            await RecordAsync(new AiCall
            {
                At = DateTimeOffset.UtcNow,
                Purpose = purpose,
                SiteId = siteId,
                PurchaseId = purchaseId,
                Model = settings.AiModel,
                InputTokens = inTokens,
                OutputTokens = outTokens,
                DurationMs = (int)watch.ElapsedMilliseconds,
                Ok = error == null,
                Summary = Short(error ?? answer, 8000),
            });
        }
    }

    private async Task RecordAsync(AiCall call)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AiCalls.Add(call);
            await db.SaveChangesAsync();
        }
        catch (Exception e)
        {
            log.LogWarning("Couldn't record an AI call: {Error}", e.Message);
        }
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? body;
        }
        catch (Exception)
        {
            return body;
        }
    }

    public static string Short(string s, int n) => s.Length > n ? s[..n] + "…" : s;
}
