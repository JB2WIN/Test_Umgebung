using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lernheft.Studio;

public class GeminiException(string message, int status = 0, string technical = "") : Exception(message)
{
    public int Status { get; } = status;
    public string Technical { get; } = technical;
}

public record GeminiImage(byte[] Data, string MimeType = "image/png");

public record GeminiTurn(string Role, string Text, IReadOnlyList<GeminiImage>? Images = null);

/// <summary>
/// Spricht mit der Gemini-API – mit Ersatzschlüssel, Wiederholungen bei Überlastung und
/// Verbrauchszählung. Dieselben Regeln wie in der alten iPad-App.
/// </summary>
public class GeminiClient
{
    public const string DefaultModel = "gemini-flash-latest";
    private const string Base = "https://generativelanguage.googleapis.com/v1beta";

    public const string SystemInstruction = """
        Du hilfst einer Schülerin oder einem Schüler der 10. Klasse (Gymnasium in Deutschland) beim Lernen mit den eigenen Notizen.
        Antworte auf Deutsch – außer die Aufgabe verlangt ausdrücklich eine andere Sprache.
        Schreibe Mathematik mit Unicode-Zeichen (x², √, ·, ÷, π, ≤, ≥, ½, →) und niemals mit LaTeX.
        Nutze nur einfaches Markdown: **fett**, *kursiv* und Listen mit „- " oder „1.". Keine Überschriften mit #, keine Tabellen.
        """;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public string ApiKey { get; }
    public string Model { get; }
    public string? FallbackKey { get; }
    public bool FallbackOnOverload { get; }
    private readonly UsageTracker? _usage;

    /// <summary>Für Proben: statt ins Netz zu gehen, antwortet diese Funktion.</summary>
    public static Func<HttpRequestMessage, Task<HttpResponseMessage>>? TestTransport { get; set; }

    public GeminiClient(string apiKey, string? model = null, string? fallbackKey = null,
        bool fallbackOnOverload = false, UsageTracker? usage = null)
    {
        ApiKey = apiKey.Trim();
        Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        FallbackKey = string.IsNullOrWhiteSpace(fallbackKey) || fallbackKey.Trim() == ApiKey ? null : fallbackKey.Trim();
        FallbackOnOverload = fallbackOnOverload;
        _usage = usage;
    }

    public static GeminiClient? FromSettings(Settings settings, UsageTracker? usage = null)
    {
        var key = settings.GetSecret(Keys.GeminiKey);
        if (key.Length == 0) return null;
        return new GeminiClient(key, settings.Get(Keys.GeminiModel, DefaultModel),
            settings.GetSecret(Keys.GeminiBackupKey), settings.GetBool(Keys.FallbackOnOverload, false), usage);
    }

    public Task<string> GenerateAsync(string prompt, IReadOnlyList<GeminiImage>? images = null, bool json = false,
        object? schema = null, CancellationToken cancel = default) =>
        GenerateAsync(new[] { new GeminiTurn("user", prompt, images) }, json, schema, cancel);

    public async Task<string> GenerateAsync(IReadOnlyList<GeminiTurn> turns, bool json = false, object? schema = null,
        CancellationToken cancel = default)
    {
        if (ApiKey.Length == 0)
            throw new GeminiException("Es ist kein Gemini-API-Schlüssel hinterlegt. Trag ihn unter Einstellungen ein.", 0, "kein Schlüssel");

        var contents = new JsonArray();
        foreach (var turn in turns)
        {
            var parts = new JsonArray { new JsonObject { ["text"] = turn.Text } };
            foreach (var image in turn.Images ?? Array.Empty<GeminiImage>())
            {
                parts.Add(new JsonObject
                {
                    ["inline_data"] = new JsonObject
                    {
                        ["mime_type"] = image.MimeType,
                        ["data"] = Convert.ToBase64String(image.Data)
                    }
                });
            }
            contents.Add(new JsonObject { ["role"] = turn.Role, ["parts"] = parts });
        }
        var generation = new JsonObject { ["temperature"] = 0.4 };
        if (json)
        {
            generation["responseMimeType"] = "application/json";
            if (schema is not null) generation["responseSchema"] = JsonSerializer.SerializeToNode(schema);
        }
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = SystemInstruction } } },
            ["contents"] = contents,
            ["generationConfig"] = generation
        };
        var payload = body.ToJsonString();

        // Mit Ersatzschlüssel fragt der erste nur einmal – dann sofort wechseln statt lange warten.
        var attempt = await SendAsync(ApiKey, payload, FallbackKey is null ? 3 : 1, cancel);
        if (attempt.Status != 200 && FallbackKey is not null
            && (attempt.Status == 429 || (FallbackOnOverload && attempt.Status >= 500)))
        {
            _usage?.NoteFallback();
            var second = await SendAsync(FallbackKey, payload, 3, cancel);
            if (second.Status == 200 || second.Status != 429) attempt = second;
        }
        else if (attempt.Status >= 500 && FallbackKey is not null)
        {
            var retry = await SendAsync(ApiKey, payload, 3, cancel);
            if (retry.Status == 200) attempt = retry;
        }

        if (attempt.Status != 200) throw Explain(attempt.Status, attempt.Message);

        using var document = JsonDocument.Parse(attempt.Body);
        var root = document.RootElement;
        if (root.TryGetProperty("usageMetadata", out var usage))
        {
            var input = usage.TryGetProperty("promptTokenCount", out var i) ? i.GetInt32() : 0;
            var output = usage.TryGetProperty("candidatesTokenCount", out var o) ? o.GetInt32() : 0;
            if (usage.TryGetProperty("thoughtsTokenCount", out var t)) output += t.GetInt32();
            _usage?.Add(Model, input, output);
        }
        if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out var reason))
            throw new GeminiException($"Gemini hat die Anfrage blockiert ({reason.GetString()}). Formuliere sie anders.", 200, "blockiert");

        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            throw new GeminiException("Gemini hat keine Antwort geliefert. Versuch es mit weniger Inhalt.", 200, "leere Antwort");

        var first = candidates[0];
        var builder = new StringBuilder();
        if (first.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var partsElement))
        {
            foreach (var part in partsElement.EnumerateArray())
            {
                if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
                if (part.TryGetProperty("text", out var value)) builder.Append(value.GetString());
            }
        }
        var answer = builder.ToString().Trim();
        if (answer.Length == 0)
        {
            var finish = first.TryGetProperty("finishReason", out var f) ? f.GetString() : "";
            if (finish is "SAFETY" or "PROHIBITED_CONTENT")
                throw new GeminiException($"Gemini hat die Anfrage blockiert ({finish}). Formuliere sie anders.", 200, "blockiert");
            throw new GeminiException("Gemini hat keine Antwort geliefert. Versuch es mit weniger Inhalt.", 200, "leere Antwort");
        }
        return answer;
    }

    private record Attempt(int Status, string Body, string Message);

    private async Task<Attempt> SendAsync(string key, string payload, int attempts, CancellationToken cancel)
    {
        var status = 0;
        var body = "";
        var message = "";
        for (var index = 0; index < Math.Max(1, attempts); index++)
        {
            if (index > 0) await Task.Delay(index == 1 ? 1500 : 5000, cancel);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/models/{Uri.EscapeDataString(Model)}:generateContent");
            request.Headers.Add("x-goog-api-key", key);
            request.Content = new StringContent(payload, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            try
            {
                using var response = TestTransport is not null
                    ? await TestTransport(request)
                    : await Http.SendAsync(request, cancel);
                status = (int)response.StatusCode;
                body = await response.Content.ReadAsStringAsync(cancel);
            }
            catch (HttpRequestException error)
            {
                throw new GeminiException("Keine Verbindung zu Gemini. Bist du online?", 0, error.Message);
            }
            catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new GeminiException("Die Anfrage hat zu lange gedauert. Versuch es mit weniger Inhalt.", 0, "Zeitüberschreitung");
            }
            if (status == 200) break;
            message = ErrorMessage(body) ?? $"HTTP {status}";
            if (status < 500) break;
        }
        return new Attempt(status, body, message);
    }

    private static string? ErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error)
                   && error.TryGetProperty("message", out var message)
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return body.Length > 200 ? body[..200] : body;
        }
    }

    private static GeminiException Explain(int status, string message)
    {
        var technical = $"HTTP {status}";
        if (status == 400 && message.Contains("api key", StringComparison.OrdinalIgnoreCase))
            return new GeminiException("Der API-Schlüssel ist ungültig. Prüfe ihn in den Einstellungen.", status, technical);
        return status switch
        {
            401 or 403 => new GeminiException("Zugriff verweigert. Prüfe den API-Schlüssel in den Einstellungen.", status, technical),
            404 => new GeminiException("Dieses Modell gibt es nicht (mehr). Wähle in den Einstellungen ein anderes.", status, technical),
            429 => new GeminiException("Das Limit ist gerade erreicht. Warte eine Minute und versuch es nochmal.", status, technical),
            503 => new GeminiException("Googles Gemini-Server sind gerade überlastet. Warte ein paar Minuten oder wähle in den Einstellungen „gemini-flash-lite-latest“.", status, technical),
            >= 500 => new GeminiException("Bei Gemini ist etwas schiefgegangen. Versuch es gleich nochmal.", status, technical),
            _ => new GeminiException($"Gemini meldet einen Fehler ({status}): {message}", status, technical)
        };
    }

    /// <summary>Modelle, die dieser Schlüssel nutzen darf und die Text erzeugen können.</summary>
    public async Task<List<string>> ListModelsAsync(CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/models?pageSize=200");
        request.Headers.Add("x-goog-api-key", ApiKey);
        HttpResponseMessage response;
        try
        {
            response = TestTransport is not null ? await TestTransport(request) : await Http.SendAsync(request, cancel);
        }
        catch (HttpRequestException error)
        {
            throw new GeminiException("Keine Verbindung zu Gemini. Bist du online?", 0, error.Message);
        }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancel);
            if (!response.IsSuccessStatusCode) throw Explain((int)response.StatusCode, ErrorMessage(body) ?? "");
            using var document = JsonDocument.Parse(body);
            var result = new List<string>();
            if (!document.RootElement.TryGetProperty("models", out var models)) return result;
            foreach (var model in models.EnumerateArray())
            {
                var name = model.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var methods = model.TryGetProperty("supportedGenerationMethods", out var m)
                    ? m.EnumerateArray().Select(value => value.GetString()).ToList()
                    : new List<string?>();
                if (!methods.Contains("generateContent")) continue;
                var id = name.StartsWith("models/") ? name["models/".Length..] : name;
                if (id.Contains("gemini") && !id.Contains("tts") && !id.Contains("image")) result.Add(id);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }
    }

    /// <summary>Kurze Anzeige für Fehlermeldungen.</summary>
    public static string Describe(Exception error) => error switch
    {
        GeminiException gemini when gemini.Technical.Length > 0 => $"{gemini.Message} ({gemini.Technical})",
        GeminiException gemini => gemini.Message,
        OperationCanceledException => "Abgebrochen.",
        JsonException => "Die Antwort hatte ein unerwartetes Format. Versuch es nochmal.",
        _ => error.Message
    };
}

/// <summary>
/// Zählt mit, wie viele Token bei Gemini verbraucht werden, und schätzt daraus die Kosten.
/// </summary>
public class UsageTracker(Settings settings)
{
    private const string InputKey = "usageInputTokens";
    private const string OutputKey = "usageOutputTokens";
    private const string RequestsKey = "usageRequests";
    private const string SinceKey = "usageSince";
    private const string ByModelKey = "usageByModel";
    private const string FallbackKey = "usageFallbackRequests";

    public record Price(double Input, double Output, bool IsGuess = false);

    public Price PriceFor(string model)
    {
        var ownInput = settings.GetDouble(Keys.PriceInput, 0);
        var ownOutput = settings.GetDouble(Keys.PriceOutput, 0);
        if (ownInput > 0 && ownOutput > 0) return new Price(ownInput, ownOutput);
        var name = model.ToLowerInvariant();
        if (name.Contains("flash-lite")) return name.Contains("3.5") ? new Price(0.30, 2.50) : new Price(0.25, 1.50);
        if (name.Contains("flash")) return new Price(0.75, 3.75);
        if (name.Contains("pro")) return new Price(2.50, 15.00, true);
        return new Price(0.75, 3.75, true);
    }

    /// <summary>Verbrauch je Modell: Modellname → [hinein, hinaus, Anfragen].</summary>
    public Dictionary<string, long[]> ByModel
    {
        get
        {
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, long[]>>(settings.Get(ByModelKey, "{}")) ?? new();
            }
            catch (JsonException)
            {
                return new Dictionary<string, long[]>();
            }
        }
    }

    public void Add(string model, int input, int output)
    {
        if (!settings.Has(SinceKey)) settings.SetDouble(SinceKey, AppleTime.Now);
        settings.Set(InputKey, (InputTokens + input).ToString(CultureInfo.InvariantCulture));
        settings.Set(OutputKey, (OutputTokens + output).ToString(CultureInfo.InvariantCulture));
        settings.Set(RequestsKey, (Requests + 1).ToString(CultureInfo.InvariantCulture));
        var table = ByModel;
        var entry = table.TryGetValue(model, out var existing) && existing.Length >= 3 ? existing : new long[3];
        entry[0] += input;
        entry[1] += output;
        entry[2] += 1;
        table[model] = entry;
        settings.Set(ByModelKey, JsonSerializer.Serialize(table));
    }

    public void NoteFallback() => settings.Set(FallbackKey, (FallbackRequests + 1).ToString(CultureInfo.InvariantCulture));

    public long InputTokens => long.TryParse(settings.Get(InputKey, "0"), out var value) ? value : 0;
    public long OutputTokens => long.TryParse(settings.Get(OutputKey, "0"), out var value) ? value : 0;
    public long Requests => long.TryParse(settings.Get(RequestsKey, "0"), out var value) ? value : 0;
    public long FallbackRequests => long.TryParse(settings.Get(FallbackKey, "0"), out var value) ? value : 0;
    public DateTime? Since => settings.Has(SinceKey) ? AppleTime.ToDateTime(settings.GetDouble(SinceKey, AppleTime.Now)) : null;

    public double EstimatedCostUsd => ByModel.Sum(entry =>
    {
        var price = PriceFor(entry.Key);
        var input = entry.Value.Length > 0 ? entry.Value[0] : 0;
        var output = entry.Value.Length > 1 ? entry.Value[1] : 0;
        return input / 1_000_000.0 * price.Input + output / 1_000_000.0 * price.Output;
    });

    public bool HasGuessedPrice => ByModel.Keys.Any(model => PriceFor(model).IsGuess);

    public double EstimatedCostEur
    {
        get
        {
            var rate = settings.GetDouble(Keys.UsdToEur, 0.92);
            return EstimatedCostUsd * (rate > 0 ? rate : 0.92);
        }
    }

    /// <summary>Wie lange das Guthaben beim bisherigen Tempo noch reicht (in Tagen).</summary>
    public int? RemainingDays(double budgetEur)
    {
        if (Since is not DateTime since || EstimatedCostEur <= 0) return null;
        var days = Math.Max(0.2, (DateTime.Now - since).TotalDays);
        var perDay = EstimatedCostEur / days;
        if (perDay <= 0) return null;
        return (int)((budgetEur - EstimatedCostEur) / perDay);
    }

    /// <summary>Hochrechnung auf 190 Schultage.</summary>
    public long? ProjectedYearTokens
    {
        get
        {
            if (Since is not DateTime since || Requests == 0) return null;
            var days = Math.Max(1, (DateTime.Now - since).TotalDays);
            return (long)((InputTokens + OutputTokens) / days * 190);
        }
    }

    public void Reset()
    {
        foreach (var key in new[] { InputKey, OutputKey, RequestsKey, ByModelKey, FallbackKey }) settings.Remove(key);
        settings.SetDouble(SinceKey, AppleTime.Now);
    }
}
