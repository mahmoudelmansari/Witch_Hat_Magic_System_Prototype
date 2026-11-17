using System.Collections.Generic;
using UnityEngine;

public static class StrokeSnapper
{
    public static List<Vector2> SnapToTemplate(List<Vector2> points, ShapeLabel label)
    {
        var resampled = StrokeClassifier.Resample(points, 32);

        switch (label)
        {
            case ShapeLabel.ClosedCircle:
                var (cCenter, cRadius) = FitCircle(resampled);
                return GenerateCircle(cCenter, cRadius);

            case ShapeLabel.OpenArc:
                return SnapToArc(resampled);

            case ShapeLabel.TriangleUp:
                return SnapRegularTriangle(resampled, tipUp: true);

            case ShapeLabel.TriangleDown:
                return SnapDroplet(resampled);

            case ShapeLabel.Square:
                return SnapRegularSquare(resampled);

            case ShapeLabel.Diamond:
                return SnapDiamondWithRays(resampled);

            case ShapeLabel.Wave:
                return SnapToWave(resampled);

            case ShapeLabel.KeystoneW:
                return SnapToKeystoneW(resampled);

            default:
                return resampled;
        }
    }

    // ---------- Circle ----------

    static (Vector2 center, float radius) FitCircle(List<Vector2> points)
    {
        Vector2 center = Vector2.zero;
        foreach (var p in points) center += p;
        center /= points.Count;

        float radius = 0f;
        foreach (var p in points) radius += Vector2.Distance(p, center);
        radius /= points.Count;

        return (center, radius);
    }

    static List<Vector2> GenerateCircle(Vector2 center, float radius, int pointCount = 32)
    {
        List<Vector2> result = new();
        for (int i = 0; i < pointCount; i++)
        {
            float angle = (i / (float)(pointCount - 1)) * 2f * Mathf.PI;
            result.Add(new Vector2(
                center.x + radius * Mathf.Cos(angle),
                center.y + radius * Mathf.Sin(angle)
            ));
        }
        return result;
    }

    // ---------- Arc ----------

    static List<Vector2> SnapToArc(List<Vector2> points)
    {
        Vector2 center = Vector2.zero;
        foreach (var p in points) center += p;
        center /= points.Count;

        float radius = 0f;
        foreach (var p in points) radius += Vector2.Distance(p, center);
        radius /= points.Count;

        Vector2 startDir = points[0] - center;
        Vector2 endDir = points[points.Count - 1] - center;
        float startAngle = Mathf.Atan2(startDir.y, startDir.x);
        float endAngle = Mathf.Atan2(endDir.y, endDir.x);

        float rawDelta = Mathf.DeltaAngle(startAngle * Mathf.Rad2Deg, endAngle * Mathf.Rad2Deg) * Mathf.Deg2Rad;
        float winding = StrokeClassifier.TotalWinding(points);

        float sweep;
        if (winding >= 0)
            sweep = rawDelta >= 0 ? rawDelta : rawDelta + 2f * Mathf.PI;
        else
            sweep = rawDelta <= 0 ? rawDelta : rawDelta - 2f * Mathf.PI;

        return GenerateArc(center, radius, startAngle, sweep);
    }

    static List<Vector2> GenerateArc(Vector2 center, float radius, float startAngle, float sweep, int pointCount = 32)
    {
        List<Vector2> result = new();
        for (int i = 0; i < pointCount; i++)
        {
            float t = i / (float)(pointCount - 1);
            float angle = startAngle + sweep * t;
            result.Add(new Vector2(
                center.x + radius * Mathf.Cos(angle),
                center.y + radius * Mathf.Sin(angle)
            ));
        }
        return result;
    }

    // ---------- Regular polygon helpers ----------

    static (Vector2 center, float radius) FitPolygonCircumcircle(List<Vector2> corners)
    {
        Vector2 center = Vector2.zero;
        foreach (var v in corners) center += v;
        center /= corners.Count;

        float radius = 0f;
        foreach (var v in corners) radius += Vector2.Distance(v, center);
        radius /= corners.Count;

        return (center, radius);
    }

    static Vector2[] GenerateRegularVertices(Vector2 center, float radius, int sides, float startAngle)
    {
        Vector2[] result = new Vector2[sides];
        for (int i = 0; i < sides; i++)
        {
            float a = startAngle + i * (2f * Mathf.PI / sides);
            result[i] = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
        }
        return result;
    }

    static List<Vector2> ExtractCornerPositions(List<Vector2> resampled)
    {
        var indices = StrokeClassifier.FindCorners(resampled);
        var corners = new List<Vector2>();
        foreach (int idx in indices) corners.Add(resampled[idx]);
        return corners;
    }

    // ---------- Fire (TriangleUp) ----------

    static List<Vector2> SnapRegularTriangle(List<Vector2> resampled, bool tipUp)
    {
        var drawnCorners = ExtractCornerPositions(resampled);
        if (drawnCorners.Count < 3) return resampled;

        var (center, radius) = FitPolygonCircumcircle(drawnCorners);
        float startAngle = tipUp ? Mathf.PI / 2f : -Mathf.PI / 2f;
        Vector2[] regular = GenerateRegularVertices(center, radius, 3, startAngle);

        return RoundedPolygon(regular, cornerRoundness: 0.1f, cornerSubdivisions: 6);
    }

    // ---------- Water (TriangleDown as droplet) ----------

    static List<Vector2> SnapDroplet(List<Vector2> resampled)
    {
        var drawnCorners = ExtractCornerPositions(resampled);
        if (drawnCorners.Count < 3) return resampled;

        var (center, radius) = FitPolygonCircumcircle(drawnCorners);
        Vector2[] regular = GenerateRegularVertices(center, radius, 3, -Mathf.PI / 2f);

        return BulgedPolygon(regular, center, bulgeFactor: 0.15f, pointsPerEdge: 14);
    }

    // ---------- Square (Earth) ----------

    static List<Vector2> SnapRegularSquare(List<Vector2> resampled)
    {
        var drawnCorners = ExtractCornerPositions(resampled);
        if (drawnCorners.Count < 4) return resampled;

        var (center, radius) = FitPolygonCircumcircle(drawnCorners);
        Vector2[] regular = GenerateRegularVertices(center, radius, 4, Mathf.PI / 4f);

        return RoundedPolygon(regular, cornerRoundness: 0.1f, cornerSubdivisions: 6);
    }

    // ---------- Diamond (Light) — diamond with rays extending from each vertex ----------

    static List<Vector2> SnapDiamondWithRays(List<Vector2> resampled)
    {
        var drawnCorners = ExtractCornerPositions(resampled);
        if (drawnCorners.Count < 4) return resampled;

        var (center, radius) = FitPolygonCircumcircle(drawnCorners);
        // Diamond: vertices on axes (0°, 90°, 180°, 270°), starting at 90° (top)
        Vector2[] regular = GenerateRegularVertices(center, radius, 4, Mathf.PI / 2f);

        // Ray length: 40% of the circumradius, extending outward from each vertex
        float rayLength = radius * 0.4f;

        List<Vector2> result = new();
        int pointsPerSegment = 8;

        // Trace a continuous path: for each vertex, go to it → outward → back to it → next vertex.
        // Rays are drawn twice (out and back on the same line) but ink is solid, so it renders as one line.
        for (int i = 0; i < 4; i++)
        {
            Vector2 vertex = regular[i];
            Vector2 outward = (vertex - center).normalized;
            Vector2 rayEnd = vertex + outward * rayLength;

            // From previous position (or start) to this vertex
            Vector2 prev = (i == 0) ? regular[3] : regular[i - 1];
            // Only interpolate the edge on subsequent iterations
            if (i > 0)
            {
                for (int j = 1; j <= pointsPerSegment; j++)
                {
                    float t = j / (float)pointsPerSegment;
                    result.Add(Vector2.Lerp(prev, vertex, t));
                }
            }
            else
            {
                result.Add(vertex);
            }

            // Outward along the ray
            for (int j = 1; j <= pointsPerSegment; j++)
            {
                float t = j / (float)pointsPerSegment;
                result.Add(Vector2.Lerp(vertex, rayEnd, t));
            }
            // Back to the vertex along the same ray
            for (int j = 1; j <= pointsPerSegment; j++)
            {
                float t = j / (float)pointsPerSegment;
                result.Add(Vector2.Lerp(rayEnd, vertex, t));
            }
        }

        // Close the loop: last vertex (index 3) back to first (index 0)
        for (int j = 1; j <= pointsPerSegment; j++)
        {
            float t = j / (float)pointsPerSegment;
            result.Add(Vector2.Lerp(regular[3], regular[0], t));
        }

        return result;
    }

    // ---------- Polygon renderers ----------

    static List<Vector2> RoundedPolygon(Vector2[] vertices, float cornerRoundness = 0.1f, int cornerSubdivisions = 6)
    {
        List<Vector2> result = new();
        int n = vertices.Length;

        for (int i = 0; i < n; i++)
        {
            Vector2 prev = vertices[(i - 1 + n) % n];
            Vector2 curr = vertices[i];
            Vector2 next = vertices[(i + 1) % n];

            Vector2 fromPrev = Vector2.Lerp(curr, prev, cornerRoundness);
            Vector2 toNext = Vector2.Lerp(curr, next, cornerRoundness);

            for (int j = 0; j < 8; j++)
            {
                float t = j / 8f;
                Vector2 edgeStart = Vector2.Lerp(prev, curr, cornerRoundness);
                result.Add(Vector2.Lerp(edgeStart, fromPrev, t));
            }

            for (int j = 0; j < cornerSubdivisions; j++)
            {
                float t = j / (float)cornerSubdivisions;
                Vector2 a = Vector2.Lerp(fromPrev, curr, t);
                Vector2 b = Vector2.Lerp(curr, toNext, t);
                result.Add(Vector2.Lerp(a, b, t));
            }
        }

        result.Add(result[0]);
        return result;
    }

    static List<Vector2> BulgedPolygon(Vector2[] vertices, Vector2 center, float bulgeFactor = 0.5f, int pointsPerEdge = 14)
    {
        List<Vector2> result = new();
        int n = vertices.Length;

        for (int i = 0; i < n; i++)
        {
            Vector2 a = vertices[i];
            Vector2 b = vertices[(i + 1) % n];
            Vector2 edgeMid = (a + b) * 0.5f;

            Vector2 outward = (edgeMid - center).normalized;
            float edgeLength = Vector2.Distance(a, b);
            Vector2 control = edgeMid + outward * (edgeLength * bulgeFactor);

            for (int j = 0; j < pointsPerEdge; j++)
            {
                float t = j / (float)pointsPerEdge;
                Vector2 p = Mathf.Pow(1 - t, 2) * a
                         + 2 * (1 - t) * t * control
                         + Mathf.Pow(t, 2) * b;
                result.Add(p);
            }
        }

        result.Add(result[0]);
        return result;
    }

    // ---------- Wave ----------

    static List<Vector2> SnapToKeystoneW(List<Vector2> resampled)
    {
        var cornerIndices = StrokeClassifier.FindCorners(resampled);
        if (cornerIndices.Count < 3) return resampled;

        Vector2[] vertices = new Vector2[]
        {
            resampled[0],
            resampled[cornerIndices[0]],
            resampled[cornerIndices[1]],
            resampled[cornerIndices[2]],
            resampled[resampled.Count - 1]
        };

        List<Vector2> result = new();
        int pointsPerSegment = 8;
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j <= pointsPerSegment; j++)
            {
                float t = j / (float)pointsPerSegment;
                result.Add(Vector2.Lerp(vertices[i], vertices[i + 1], t));
            }
        }
        return result;
    }

    static List<Vector2> SnapToWave(List<Vector2> points)
    {
        Vector2 start = points[0];
        Vector2 end = points[points.Count - 1];
        Vector2 mid = points[points.Count / 2];

        List<Vector2> result = new();
        int perHalf = 16;

        Vector2 dir1 = mid - start;
        Vector2 perp1 = new Vector2(-dir1.y, dir1.x) * 0.5f;
        Vector2 ctrl1 = (start + mid) * 0.5f + perp1;

        for (int i = 0; i < perHalf; i++)
        {
            float t = i / (float)perHalf;
            Vector2 p = Mathf.Pow(1 - t, 2) * start
                      + 2 * (1 - t) * t * ctrl1
                      + Mathf.Pow(t, 2) * mid;
            result.Add(p);
        }

        Vector2 dir2 = end - mid;
        Vector2 perp2 = new Vector2(-dir2.y, dir2.x) * -0.5f;
        Vector2 ctrl2 = (mid + end) * 0.5f + perp2;

        for (int i = 0; i <= perHalf; i++)
        {
            float t = i / (float)perHalf;
            Vector2 p = Mathf.Pow(1 - t, 2) * mid
                      + 2 * (1 - t) * t * ctrl2
                      + Mathf.Pow(t, 2) * end;
            result.Add(p);
        }

        return result;
    }
}