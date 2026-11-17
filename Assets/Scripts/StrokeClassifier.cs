using System.Collections.Generic;
using UnityEngine;

public enum ShapeLabel
{
    Unknown,
    ClosedCircle,
    OpenArc,
    TriangleUp,
    TriangleDown,
    Square,
    Diamond,
    Wave,
    KeystoneW
}
public static class StrokeClassifier
{
    public static List<Vector2> Resample(List<Vector2> points, int targetCount)
    {
        if (points.Count < 2 || targetCount < 2)
            return new List<Vector2>(points);

        // Total length of the original stroke
        float totalLength = 0f;
        for (int i = 1; i < points.Count; i++)
            totalLength += Vector2.Distance(points[i - 1], points[i]);

        float interval = totalLength / (targetCount - 1);

        List<Vector2> result = new List<Vector2> { points[0] };
        float accumulated = 0f;

        for (int i = 1; i < points.Count; i++)
        {
            Vector2 a = points[i - 1];
            Vector2 b = points[i];
            float segLength = Vector2.Distance(a, b);

            // Within this segment, we may need to place multiple sample points
            while (accumulated + segLength >= interval && result.Count < targetCount - 1)
            {
                float t = (interval - accumulated) / segLength;
                Vector2 sample = Vector2.Lerp(a, b, t);
                result.Add(sample);

                // The sample becomes the new "start" of the remaining segment
                a = sample;
                segLength = Vector2.Distance(a, b);
                accumulated = 0f;
            }

            accumulated += segLength;
        }

        // Floating-point drift can leave us one short — pad with the last point
        while (result.Count < targetCount)
            result.Add(points[points.Count - 1]);

        return result;
    }

    public static bool IsClosed(List<Vector2> points, float toleranceRatio = 0.15f, float minTolerance = 0.03f)
    {
        Vector2 center = Vector2.zero;
        foreach (var p in points) center += p;
        center /= points.Count;

        float radius = 0f;
        foreach (var p in points) radius += Vector2.Distance(p, center);
        radius /= points.Count;

        float tolerance = Mathf.Max(radius * toleranceRatio, minTolerance);

        Vector2 start = points[0];

        // Signal 1: any point in the last two-thirds of the stroke passes near the start
        int checkFrom = points.Count / 3;
        for (int i = checkFrom; i < points.Count; i++)
        {
            if (Vector2.Distance(points[i], start) < tolerance)
                return true;
        }

        // Signal 2: winding exceeds a full turn (physically must have crossed back)
        float winding = Mathf.Abs(TotalWinding(points));
        if (winding > 350f)
            return true;

        return false;
    }

    public static float TotalWinding(List<Vector2> points)
    {
        float total = 0f;
        for (int i = 1; i < points.Count - 1; i++)
        {
            Vector2 incoming = (points[i] - points[i - 1]).normalized;
            Vector2 outgoing = (points[i + 1] - points[i]).normalized;

            // Signed angle via 2D cross product
            float cross = incoming.x * outgoing.y - incoming.y * outgoing.x;
            float dot = Vector2.Dot(incoming, outgoing);
            total += Mathf.Atan2(cross, dot) * Mathf.Rad2Deg;
        }
        return total;
    }

    public static int CountCorners(List<Vector2> points, float angleThreshold = 45f, int window = 3)
     => FindCorners(points, angleThreshold, window).Count;

    public static List<int> FindCorners(List<Vector2> points, float angleThreshold = 45f, int window = 3)
    {
        List<int> corners = new();
        int n = points.Count;
        if (n < 2 * window + 1) return corners;

        float[] angles = new float[n];
        for (int i = window; i < n - window; i++)
        {
            Vector2 incoming = (points[i] - points[i - window]).normalized;
            Vector2 outgoing = (points[i + window] - points[i]).normalized;
            angles[i] = Vector2.Angle(incoming, outgoing);
        }

        for (int i = window + 1; i < n - window - 1; i++)
        {
            if (angles[i] > angleThreshold && angles[i] >= angles[i - 1] && angles[i] >= angles[i + 1])
                corners.Add(i);
        }

        // If the stroke is closed and the seam itself is a sharp corner, add point 0 as a vertex
        if (IsClosed(points))
        {
            Vector2 incoming = (points[n - 1] - points[n - 1 - window]).normalized;
            Vector2 outgoing = (points[window] - points[0]).normalized;
            if (Vector2.Angle(incoming, outgoing) > angleThreshold)
                corners.Insert(0, 0); // seam vertex is the start point
        }

        return corners;
    }
    public static float AbsoluteWinding(List<Vector2> points)
    {
        float total = 0f;
        for (int i = 1; i < points.Count - 1; i++)
        {
            Vector2 incoming = (points[i] - points[i - 1]).normalized;
            Vector2 outgoing = (points[i + 1] - points[i]).normalized;
            float cross = incoming.x * outgoing.y - incoming.y * outgoing.x;
            float dot = Vector2.Dot(incoming, outgoing);
            total += Mathf.Abs(Mathf.Atan2(cross, dot)) * Mathf.Rad2Deg;
        }
        return total;
    }

    public static float MeanNonCornerAngle(List<Vector2> points, float cornerThreshold = 45f, int window = 3)
    {
        int n = points.Count;
        float sum = 0f;
        int count = 0;

        for (int i = window; i < n - window; i++)
        {
            Vector2 incoming = (points[i] - points[i - window]).normalized;
            Vector2 outgoing = (points[i + window] - points[i]).normalized;
            float angle = Vector2.Angle(incoming, outgoing);

            if (angle < cornerThreshold)
            {
                sum += angle;
                count++;
            }
        }

        return count > 0 ? sum / count : 0f;
    }

    public static ShapeLabel Classify(List<Vector2> points)
    {
        var resampled = Resample(points, 32);
        int corners = CountCorners(resampled);
        bool closed = IsClosed(resampled);
        float meanAngle = MeanNonCornerAngle(resampled);
        float winding = Mathf.Abs(TotalWinding(resampled));
        float absWinding = AbsoluteWinding(resampled);

        // Scale via bounding box
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var p in resampled)
        {
            if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
            if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y;
        }
        float scale = Vector2.Distance(new Vector2(minX, minY), new Vector2(maxX, maxY));
        bool small = scale < 0.08f;

        Debug.Log($"corners={corners}, closed={closed}, meanAngle={meanAngle:F1}, winding={winding:F0}, absWinding={absWinding:F0}, scale={scale:F2}");

        // Small-scale relaxed rules
        if (small)
        {
            // Prefer polygons — sharp corners are a stronger signal than curving at small scale
            if (closed && corners == 3) return DetermineTriangleOrientation(resampled);
            if (closed && corners == 4) return DetermineSquareOrDiamond(resampled);
            if (winding > 120f && meanAngle > 15f)
                return closed ? ShapeLabel.ClosedCircle : ShapeLabel.OpenArc;
        }

        // Circle-family
        if (meanAngle > 20f)
        {
            if (closed && winding > 340f) return ShapeLabel.ClosedCircle;
            if (!closed && winding > 180f) return ShapeLabel.OpenArc;
        }

        // Wave — smooth zigzag (S-shape), few corners (may pick up minor inflection corners)
        if (corners <= 2 && absWinding > 240f && Mathf.Abs(TotalWinding(resampled)) < 180f)
            return ShapeLabel.Wave;

        // KeystoneW — 3 sharp corners with alternating turn direction (open zigzag)
        if (corners == 3 && !closed && Mathf.Abs(TotalWinding(resampled)) < 200f && absWinding > 200f)
            return ShapeLabel.KeystoneW;

        // Polygons
        if (corners == 3) return DetermineTriangleOrientation(resampled);
        if (corners == 4) return DetermineSquareOrDiamond(resampled);

        return ShapeLabel.Unknown;
    }

    static ShapeLabel DetermineSquareOrDiamond(List<Vector2> points)
    {
        // Compute the centroid
        Vector2 c = Vector2.zero;
        foreach (var p in points) c += p;
        c /= points.Count;

        // Get the four corner vertices
        var cornerIndices = FindCorners(points);
        if (cornerIndices.Count < 4) return ShapeLabel.Square; // fallback

        // For each vertex, compute its angle relative to center in [0, 90) after folding into a quadrant.
        // A square's vertices sit near 45°/135°/225°/315° — folded, they're near 45°.
        // A diamond's vertices sit near 0°/90°/180°/270° — folded, they're near 0° or 90°.
        float diamondScore = 0f;
        float squareScore = 0f;
        foreach (int idx in cornerIndices)
        {
            Vector2 v = points[idx] - c;
            float angleDeg = Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;
            // Fold into [0, 90)
            float folded = Mathf.Abs(((angleDeg % 90f) + 90f) % 90f);
            if (folded > 45f) folded = 90f - folded;
            // folded near 0 → diamond; folded near 45 → square
            diamondScore += 45f - folded; // higher if close to 0
            squareScore += folded;         // higher if close to 45
        }

        return diamondScore > squareScore ? ShapeLabel.Diamond : ShapeLabel.Square;
    }

    static ShapeLabel DetermineTriangleOrientation(List<Vector2> points)
    {
        Vector2 c = Vector2.zero;
        foreach (var p in points) c += p;
        c /= points.Count;

        Vector2 mostExtreme = points[0];
        foreach (var p in points)
            if (Mathf.Abs(p.y - c.y) > Mathf.Abs(mostExtreme.y - c.y))
                mostExtreme = p;

        return (mostExtreme.y > c.y) ? ShapeLabel.TriangleUp : ShapeLabel.TriangleDown;
    }
}