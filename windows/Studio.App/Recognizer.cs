using Windows.Foundation;
using Windows.UI.Input.Inking;

namespace Lernheft.Studio.App;

/// <summary>
/// Handschrifterkennung von Windows – springt ein, wenn kein Gemini-Schlüssel da ist.
/// Deutlich ungenauer als die KI, aber ohne Internet.
/// </summary>
public static class Recognizer
{
    public static async Task<string> RecognizeAsync(IReadOnlyList<InkStroke> strokes)
    {
        if (strokes.Count == 0) return "";
        try
        {
            var container = new InkStrokeContainer();
            var builder = new InkStrokeBuilder();
            foreach (var stroke in strokes)
            {
                var points = new List<Point>();
                for (var index = 0; index < stroke.Count; index++)
                {
                    var (x, y, _) = stroke.PointAt(index);
                    points.Add(new Point(x, y));
                }
                if (points.Count < 2) continue;
                container.AddStroke(builder.CreateStroke(points));
            }
            var recognizer = new InkRecognizerContainer();
            var german = recognizer.GetRecognizers().FirstOrDefault(r => r.Name.Contains("Deutsch") || r.Name.Contains("German"));
            if (german is not null) recognizer.SetDefaultRecognizer(german);
            var results = await recognizer.RecognizeAsync(container, InkRecognitionTarget.All);
            return string.Join(" ", results.Select(result => result.GetTextCandidates().FirstOrDefault() ?? "")).Trim();
        }
        catch (Exception error)
        {
            Services.Log("Windows-Handschrifterkennung: " + error.Message);
            return "";
        }
    }
}
