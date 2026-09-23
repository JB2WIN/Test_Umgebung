using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lernheft.Studio;

/// <summary>
/// Liest einen Kalender-Link (.ics, auch webcal://) – z. B. den öffentlichen Stundenplan-Link
/// aus WebUntis – und macht daraus einen Wochenplan.
/// </summary>
public static class IcsImporter
{
    public record Event(DateTime Start, DateTime End, string Summary, string Location);

    public class IcsException(string message) : Exception(message);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static string Normalize(string link)
    {
        var text = link.Trim();
        if (text.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) text = "https://" + text["webcal://".Length..];
        return text;
    }

    public static async Task<List<Lesson>> LessonsFromLinkAsync(string link, CancellationToken cancel = default)
    {
        var text = Normalize(link);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.Scheme.StartsWith("http"))
            throw new IcsException("Das sieht nicht nach einem Kalender-Link aus. Er endet meist auf .ics.");
        string content;
        try
        {
            using var response = await Http.GetAsync(uri, cancel);
            if (!response.IsSuccessStatusCode)
                throw new IcsException($"Der Kalender ließ sich nicht laden: HTTP {(int)response.StatusCode}");
            content = await response.Content.ReadAsStringAsync(cancel);
        }
        catch (HttpRequestException error)
        {
            throw new IcsException("Der Kalender ließ sich nicht laden: " + error.Message);
        }
        if (!content.Contains("BEGIN:VEVENT"))
        {
            var website = content.Contains("<html", StringComparison.OrdinalIgnoreCase)
                          || content.Contains("login", StringComparison.OrdinalIgnoreCase);
            throw new IcsException(website
                ? "Der Link führt auf eine Anmeldeseite. Nimm „Mit WebUntis anmelden“."
                : "Die Datei enthält keine Termine.");
        }
        var lessons = Lessons(Parse(content), DateTime.Now);
        if (lessons.Count == 0) throw new IcsException("In dem Kalender stehen keine Unterrichtsstunden für die nächsten Wochen.");
        return lessons;
    }

    public static List<Event> Parse(string content)
    {
        var unfolded = new List<string>();
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            if ((raw.StartsWith(' ') || raw.StartsWith('\t')) && unfolded.Count > 0) unfolded[^1] += raw[1..];
            else unfolded.Add(raw);
        }

        var events = new List<Event>();
        DateTime? start = null, end = null;
        string summary = "", location = "";
        var inside = false;
        foreach (var line in unfolded)
        {
            if (line.StartsWith("BEGIN:VEVENT"))
            {
                inside = true;
                start = end = null;
                summary = location = "";
                continue;
            }
            if (line.StartsWith("END:VEVENT"))
            {
                if (inside && start is DateTime s && end is DateTime e && e > s) events.Add(new Event(s, e, summary, location));
                inside = false;
                continue;
            }
            var colon = line.IndexOf(':');
            if (!inside || colon < 0) continue;
            var key = line[..colon];
            var value = line[(colon + 1)..];
            var name = key.Split(';')[0].ToUpperInvariant();
            switch (name)
            {
                case "DTSTART": start = ParseDate(value, key); break;
                case "DTEND": end = ParseDate(value, key); break;
                case "SUMMARY": summary = Unescape(value); break;
                case "LOCATION": location = Unescape(value); break;
            }
        }
        return events;
    }

    private static string Unescape(string text) => text.Replace("\\n", " ").Replace("\\,", ",")
        .Replace("\\;", ";").Replace("\\\\", "\\").Trim();

    private static DateTime? ParseDate(string value, string parameters)
    {
        var utc = value.EndsWith('Z');
        var clean = value.TrimEnd('Z');
        var format = clean.Contains('T') ? "yyyyMMdd'T'HHmmss" : "yyyyMMdd";
        if (!DateTime.TryParseExact(clean, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return null;
        if (utc) return DateTime.SpecifyKind(date, DateTimeKind.Utc).ToLocalTime();
        var zoneStart = parameters.IndexOf("TZID=", StringComparison.OrdinalIgnoreCase);
        if (zoneStart >= 0)
        {
            var zoneName = parameters[(zoneStart + 5)..].Split(';')[0];
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneName);
                return TimeZoneInfo.ConvertTime(date, zone, TimeZoneInfo.Local);
            }
            catch (Exception) { }
        }
        return date;
    }

    /// <summary>Aus Terminen einen Wochenplan machen: jede Stunde je Wochentag nur einmal.</summary>
    public static List<Lesson> Lessons(List<Event> events, DateTime reference)
    {
        var window = events.Where(e => e.Start >= reference.Date.AddDays(-7) && e.Start <= reference.AddDays(21)).ToList();
        var source = window.Count > 0 ? window : events;
        var seen = new HashSet<string>();
        var lessons = new List<Lesson>();
        foreach (var item in source.OrderBy(e => e.Start))
        {
            var weekday = Lesson.TodayIndex(item.Start);
            var start = item.Start.Hour * 60 + item.Start.Minute;
            var end = item.End.Hour * 60 + item.End.Minute;
            var subject = SubjectName(item.Summary);
            if (!seen.Add($"{weekday}|{start}|{subject.ToLowerInvariant()}")) continue;
            lessons.Add(new Lesson
            {
                Weekday = weekday,
                Start = start,
                End = Math.Max(end, start + 5),
                Subject = subject,
                Room = Room(item)
            });
        }
        return lessons.OrderBy(l => l.Weekday).ThenBy(l => l.Start).ToList();
    }

    private static string SubjectName(string summary)
    {
        var text = Regex.Replace(summary, @"\s*\([^)]*\)\s*$", "");
        var first = text.Split('-', '–', '|')[0].Trim();
        return first.Length == 0 ? text.Trim() : first;
    }

    private static string Room(Event item)
    {
        if (item.Location.Length > 0) return item.Location;
        var match = Regex.Match(item.Summary, @"\(([^)]*)\)");
        if (match.Success) return match.Groups[1].Value.Trim();
        var parts = item.Summary.Split('-', '–', '|');
        return parts.Length > 1 ? parts[^1].Trim() : "";
    }
}

/// <summary>
/// Liest den Wochenplan aus der WebUntis-Oberfläche. Das ist keine offizielle Schnittstelle –
/// deshalb wird tolerant gesucht und klar gemeldet, wenn nichts Brauchbares kommt.
/// </summary>
public static class WebUntisReader
{
    public class ReadException(string message) : Exception(message);

    public record Target(string Host, int Type, string Id, string Date);

    public static Target? FindTarget(string pageUrl, string savedLink, DateTime? date = null)
    {
        var day = (date ?? DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var host = HostOf(pageUrl) ?? HostOf(IcsImporter.Normalize(savedLink));
        if (host is null) return null;
        foreach (var candidate in new[] { pageUrl, savedLink })
        {
            var id = Value("entityId", candidate) ?? Value("elementId", candidate);
            if (string.IsNullOrEmpty(id)) continue;
            var type = candidate.Contains("my-teacher") ? 2 : candidate.Contains("my-class") ? 1 : 5;
            return new Target(host, type, id, Value("date", candidate) ?? day);
        }
        return new Target(host, 5, "", day);
    }

    private static string? HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : null;

    private static string? Value(string key, string text)
    {
        var index = text.IndexOf(key + "=", StringComparison.Ordinal);
        if (index < 0) return null;
        var rest = text[(index + key.Length + 1)..];
        var end = rest.IndexOfAny(new[] { '&', '#' });
        return end < 0 ? rest : rest[..end];
    }

    public static string WeeklyDataUrl(Target target) =>
        $"https://{target.Host}/WebUntis/api/public/timetable/weekly/data" +
        $"?elementType={target.Type}&elementId={target.Id}&date={target.Date}&formatId=1";

    public static string PageConfigUrl(Target target) =>
        $"https://{target.Host}/WebUntis/api/public/timetable/weekly/pageconfig?type={target.Type}";

    /// <summary>Findet im „pageconfig" die eigene Kennung, wenn sie nicht in der Adresse stand.</summary>
    public static string? ElementIdFromPageConfig(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var payload = Find(document.RootElement, "elements");
            if (payload is not JsonElement found || found.GetProperty("elements").ValueKind != JsonValueKind.Array) return null;
            JsonElement? chosen = null;
            foreach (var element in found.GetProperty("elements").EnumerateArray())
            {
                chosen ??= element;
                if ((element.TryGetProperty("isOwn", out var own) && own.ValueKind == JsonValueKind.True)
                    || (element.TryGetProperty("current", out var current) && current.ValueKind == JsonValueKind.True))
                {
                    chosen = element;
                    break;
                }
            }
            if (chosen is not JsonElement pick || !pick.TryGetProperty("id", out var id)) return null;
            return id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString(CultureInfo.InvariantCulture) : id.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Find(JsonElement element, string key)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(key, out _)) return element;
            foreach (var property in element.EnumerateObject())
            {
                if (Find(property.Value, key) is JsonElement found) return found;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (Find(item, key) is JsonElement found) return found;
            }
        }
        return null;
    }

    private record Period(int Date, int Start, int End, string Subject, string Room, string Teacher, string State);

    private static List<Period> Periods(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new ReadException("WebUntis hat die Anmeldung nicht akzeptiert. Melde dich im Fenster an und versuch es nochmal.");
        }
        using (document)
        {
            var payload = Find(document.RootElement, "elementPeriods")
                          ?? throw new ReadException("In der Antwort von WebUntis standen keine Stunden. Öffne zuerst deinen Stundenplan und versuch es dann nochmal.");

            var names = new Dictionary<string, (string Short, string Long)>();
            if (payload.TryGetProperty("elements", out var elements) && elements.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in elements.EnumerateArray())
                {
                    if (!element.TryGetProperty("type", out var type) || !element.TryGetProperty("id", out var id)) continue;
                    var shortName = element.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var longName = element.TryGetProperty("longName", out var l) ? l.GetString() ?? shortName : shortName;
                    names[$"{type.GetInt32()}-{id.GetInt64()}"] = (shortName, longName);
                }
            }

            var result = new List<Period>();
            foreach (var table in payload.GetProperty("elementPeriods").EnumerateObject())
            {
                if (table.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var period in table.Value.EnumerateArray())
                {
                    if (!period.TryGetProperty("date", out var date) || !period.TryGetProperty("startTime", out var start)
                        || !period.TryGetProperty("endTime", out var end)) continue;
                    string subject = "", room = "", teacher = "";
                    if (period.TryGetProperty("elements", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (!part.TryGetProperty("type", out var type) || !part.TryGetProperty("id", out var id)) continue;
                            if (!names.TryGetValue($"{type.GetInt32()}-{id.GetInt64()}", out var name)) continue;
                            switch (type.GetInt32())
                            {
                                case 3: subject = name.Long.Length > 0 ? name.Long : name.Short; break;
                                case 4: room = name.Short; break;
                                case 2: teacher = name.Short; break;
                            }
                        }
                    }
                    if (subject.Length == 0)
                        subject = period.TryGetProperty("lessonText", out var text) && text.GetString() is { Length: > 0 } t ? t : "Unterricht";
                    var state = period.TryGetProperty("cellState", out var cell) ? cell.GetString() ?? "" : "";
                    result.Add(new Period(date.GetInt32(), Minutes(start.GetInt32()), Minutes(end.GetInt32()),
                        subject, room, teacher, state));
                }
            }
            return result;
        }
    }

    private static int Minutes(int hhmm) => hhmm / 100 * 60 + hhmm % 100;

    /// <summary>Der tatsächliche Stand der Woche – mit Entfall, Vertretung und Prüfungen.</summary>
    public static List<DayLesson> DayLessons(string json)
    {
        var result = Periods(json).Select(p => new DayLesson
        {
            Date = p.Date,
            Start = p.Start,
            End = p.End,
            Subject = p.Subject,
            Room = p.Room,
            Teacher = p.Teacher,
            State = p.State
        }).OrderBy(e => e.Date).ThenBy(e => e.Start).ToList();
        if (result.Count == 0) throw new ReadException("In der Antwort von WebUntis standen keine Stunden. Öffne zuerst deinen Stundenplan und versuch es dann nochmal.");
        return result;
    }

    /// <summary>Der Wochenplan ohne ausfallende Stunden.</summary>
    public static List<Lesson> Lessons(string json)
    {
        var seen = new HashSet<string>();
        var lessons = new List<Lesson>();
        foreach (var period in Periods(json))
        {
            if (period.State.Contains("CANCEL", StringComparison.OrdinalIgnoreCase)) continue;
            var date = new DateTime(period.Date / 10_000, period.Date / 100 % 100, period.Date % 100);
            var weekday = Lesson.TodayIndex(date);
            if (!seen.Add($"{weekday}|{period.Start}|{period.Subject.ToLowerInvariant()}")) continue;
            lessons.Add(new Lesson
            {
                Weekday = weekday,
                Start = period.Start,
                End = Math.Max(period.End, period.Start + 5),
                Subject = period.Subject,
                Room = period.Room
            });
        }
        if (lessons.Count == 0) throw new ReadException("In der Antwort von WebUntis standen keine Stunden. Öffne zuerst deinen Stundenplan und versuch es dann nochmal.");
        return lessons.OrderBy(l => l.Weekday).ThenBy(l => l.Start).ToList();
    }
}
