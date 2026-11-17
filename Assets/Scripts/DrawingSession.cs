using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// One drawing canvas: a locked plane (optionally anchored to a moving transform such as the palm),
/// the strokes drawn on it, the seal tree built from them, and everything spawned by its spells.
/// All plane/point data is stored in "session space": world space, or the anchor's local space.
/// </summary>
public class DrawingSession
{
    static readonly int CenterAlphaId = Shader.PropertyToID("_CenterAlpha");
    static readonly int EdgeAlphaId = Shader.PropertyToID("_EdgeAlpha");
    static readonly int DissolveAmountId = Shader.PropertyToID("_DissolveAmount");
    static readonly int EmissionStrengthId = Shader.PropertyToID("_EmissionStrength");

    // How far outside a circle's fitted radius a point can be and still count as "inside"
    const float ContainmentTolerance = 1.15f;

    public static bool VerboseLogs;

    public Transform anchor;  // null = world space; set = local to this transform
    public Plane plane;       // in session space
    public Vector3 origin;    // in session space
    public Vector3 basisU;    // in session space
    public Vector3 basisV;    // in session space

    public List<Stroke> strokes = new();
    public List<Seal> topLevelSeals = new();
    public List<Stroke> orphanedElements = new();
    public List<Stroke> orphanedModifiers = new();
    public List<Transform> spawnedEffects = new();

    public Transform cursor;
    Transform planeVisual;
    Coroutine planeResizeCoroutine;

    // ---------- Coordinate conversion ----------

    public Vector3 WorldToSession(Vector3 worldPos) =>
        anchor != null ? anchor.InverseTransformPoint(worldPos) : worldPos;

    public Vector3 SessionToWorld(Vector3 sessionPos) =>
        anchor != null ? anchor.TransformPoint(sessionPos) : sessionPos;

    public Vector3 SessionDirToWorld(Vector3 sessionDir) =>
        anchor != null ? anchor.TransformDirection(sessionDir) : sessionDir;

    public void InitializeBasis(Vector3 originPoint)
    {
        origin = originPoint;

        // basisV = world-up projected onto the plane
        Vector3 projectedUp = Vector3.up - Vector3.Dot(Vector3.up, plane.normal) * plane.normal;

        // Edge case: plane is horizontal — fall back to forward
        if (projectedUp.sqrMagnitude < 1e-6f)
            projectedUp = Vector3.forward;

        basisV = projectedUp.normalized;
        basisU = Vector3.Cross(plane.normal, basisV).normalized; // note: points world-left
    }

    public Vector2 To2D(Vector3 point)
    {
        Vector3 relative = point - origin;
        return new Vector2(Vector3.Dot(relative, basisU), Vector3.Dot(relative, basisV));
    }

    public Vector3 To3D(Vector2 planeCoord) =>
        origin + basisU * planeCoord.x + basisV * planeCoord.y;

    // ---------- 2D geometry helpers ----------

    public static Vector2 Centroid(List<Vector2> pts)
    {
        Vector2 c = Vector2.zero;
        foreach (var p in pts) c += p;
        return c / pts.Count;
    }

    public static float FitRadius(List<Vector2> pts)
    {
        Vector2 c = Centroid(pts);
        float r = 0f;
        foreach (var p in pts) r += Vector2.Distance(p, c);
        return r / pts.Count;
    }

    // ---------- Plane visual ----------

    public void InstantiatePlaneVisual(Transform prefab, Vector3 sessionPos, MonoBehaviour host, float fadeInDuration)
    {
        // Anchored sessions don't show a plane visual — the ink sits directly on the anchor (the palm)
        if (anchor != null || planeVisual != null) return;

        planeVisual = Object.Instantiate(prefab);
        planeVisual.localPosition = sessionPos;
        planeVisual.localRotation = Quaternion.FromToRotation(Vector3.up, plane.normal);

        // Start invisible, then fade in to the material's configured alphas
        var renderer = planeVisual.GetComponentInChildren<Renderer>();
        if (renderer == null) return;

        Material mat = renderer.material; // instance, safe to modify
        float targetCenter = mat.HasProperty(CenterAlphaId) ? mat.GetFloat(CenterAlphaId) : 0.05f;
        float targetEdge = mat.HasProperty(EdgeAlphaId) ? mat.GetFloat(EdgeAlphaId) : 0.9f;
        SetPlaneAlphas(mat, 0f, 0f);
        host.StartCoroutine(FadeInRoutine(mat, targetCenter, targetEdge, fadeInDuration));
    }

    static void SetPlaneAlphas(Material mat, float center, float edge)
    {
        if (mat.HasProperty(CenterAlphaId)) mat.SetFloat(CenterAlphaId, center);
        if (mat.HasProperty(EdgeAlphaId)) mat.SetFloat(EdgeAlphaId, edge);
    }

    IEnumerator FadeInRoutine(Material mat, float targetCenter, float targetEdge, float duration)
    {
        float t = 0f;
        while (t < duration)
        {
            if (mat == null) yield break;
            t += Time.deltaTime;
            float eased = Ease.OutCubic(Mathf.Clamp01(t / duration));
            SetPlaneAlphas(mat, targetCenter * eased, targetEdge * eased);
            yield return null;
        }
        if (mat != null) SetPlaneAlphas(mat, targetCenter, targetEdge);
    }

    public void UpdatePlaneVisual(Vector3 fingerTipWorld)
    {
        if (anchor != null || planeVisual == null) return;

        const float visibleRange = 0.3f;
        float perpDist = Mathf.Abs(plane.GetDistanceToPoint(WorldToSession(fingerTipWorld)));

        if (perpDist > visibleRange)
        {
            HidePlaneVisual();
            HideCursor();
            return;
        }
        planeVisual.gameObject.SetActive(true);
    }

    public void HidePlaneVisual()
    {
        if (planeVisual != null) planeVisual.gameObject.SetActive(false);
    }

    public void AnimatePlaneVisual(MonoBehaviour host, Vector3 targetLocalPos, Vector3 targetScale, float duration)
    {
        if (planeVisual == null) return;
        if (planeResizeCoroutine != null) host.StopCoroutine(planeResizeCoroutine);
        planeResizeCoroutine = host.StartCoroutine(PlaneResizeRoutine(targetLocalPos, targetScale, duration));
    }

    IEnumerator PlaneResizeRoutine(Vector3 targetPos, Vector3 targetScale, float duration)
    {
        Vector3 startPos = planeVisual.localPosition;
        Vector3 startScale = planeVisual.localScale;
        float t = 0f;
        while (t < duration)
        {
            if (planeVisual == null) yield break;
            t += Time.deltaTime;
            float eased = Ease.OutCubic(Mathf.Clamp01(t / duration));
            planeVisual.localPosition = Vector3.Lerp(startPos, targetPos, eased);
            planeVisual.localScale = Vector3.Lerp(startScale, targetScale, eased);
            yield return null;
        }
        if (planeVisual != null)
        {
            planeVisual.localPosition = targetPos;
            planeVisual.localScale = targetScale;
        }
    }

    // ---------- Cursor ----------

    public void InstantiateCursor(Transform prefab, Vector3 sessionPos)
    {
        if (cursor != null) return;
        cursor = Object.Instantiate(prefab);
        if (anchor != null) cursor.SetParent(anchor, false);
        ShowCursor(sessionPos);
    }

    public void ShowCursor(Vector3 sessionPos)
    {
        if (cursor == null) return;
        cursor.localPosition = sessionPos;
        cursor.localRotation = Quaternion.LookRotation(plane.normal);
        cursor.gameObject.SetActive(true);
    }

    public void HideCursor()
    {
        if (cursor != null) cursor.gameObject.SetActive(false);
    }

    // ---------- Teardown ----------

    public void Destroy()
    {
        foreach (var s in strokes)
            if (s.line != null) Object.Destroy(s.line.gameObject);
        strokes.Clear();

        foreach (var effect in spawnedEffects)
            if (effect != null) Object.Destroy(effect.gameObject);
        spawnedEffects.Clear();

        if (planeVisual != null) Object.Destroy(planeVisual.gameObject);
        if (cursor != null) Object.Destroy(cursor.gameObject);

        topLevelSeals.Clear();
        orphanedElements.Clear();
        orphanedModifiers.Clear();
    }

    /// <summary>Dissolve the plane from its corner and fade the ink, then destroy everything.</summary>
    public IEnumerator DissolveAndDestroy(float duration)
    {
        Material planeMat = null;
        if (planeVisual != null)
        {
            var renderer = planeVisual.GetComponentInChildren<Renderer>();
            if (renderer != null) planeMat = renderer.material;
        }

        // Snapshot each stroke's current glow so we can fade it to zero
        var startGlow = new List<float>();
        foreach (var s in strokes)
        {
            var mat = s.line != null ? s.line.material : null;
            startGlow.Add(mat != null && mat.HasProperty(EmissionStrengthId) ? mat.GetFloat(EmissionStrengthId) : 0f);
        }

        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            float progress = Mathf.Clamp01(t / duration);

            if (planeMat != null && planeMat.HasProperty(DissolveAmountId))
                planeMat.SetFloat(DissolveAmountId, progress);

            for (int i = 0; i < strokes.Count; i++)
            {
                var mat = strokes[i].line != null ? strokes[i].line.material : null;
                if (mat != null && mat.HasProperty(EmissionStrengthId))
                    mat.SetFloat(EmissionStrengthId, startGlow[i] * (1f - progress));
            }
            yield return null;
        }

        Destroy();
    }

    // ---------- Seal tree: lookup ----------

    public Seal GetFireableSeal()
    {
        // Any orphaned element means the user is still composing — defer everything
        if (orphanedElements.Count > 0) return null;

        foreach (Seal top in topLevelSeals)
            if (!top.hasFired && top.IsFullyClosed() && top.IsValid())
                return top;
        return null;
    }

    public List<Stroke> AllOpenArcs()
    {
        var result = new List<Stroke>();
        foreach (var top in topLevelSeals)
            CollectOpenArcs(top, result);
        return result;
    }

    static void CollectOpenArcs(Seal seal, List<Stroke> result)
    {
        if (seal.outerCircle != null && seal.outerCircle.label == ShapeLabel.OpenArc)
            result.Add(seal.outerCircle);
        foreach (var child in seal.children)
            CollectOpenArcs(child, result);
    }

    /// <summary>
    /// Finds the innermost seal containing the point that satisfies the filter,
    /// preferring the smallest across all top-level trees.
    /// </summary>
    Seal FindSmallestContaining(Vector2 point, Func<Seal, bool> accept)
    {
        Seal best = null;
        float bestRadius = float.MaxValue;
        foreach (var top in topLevelSeals)
        {
            var found = FindContainingRecursive(top, point, accept);
            if (found == null) continue;
            float r = FitRadius(found.outerCircle.points2D);
            if (r < bestRadius) { best = found; bestRadius = r; }
        }
        return best;
    }

    static Seal FindContainingRecursive(Seal seal, Vector2 point, Func<Seal, bool> accept)
    {
        Vector2 center = Centroid(seal.outerCircle.points2D);
        float radius = FitRadius(seal.outerCircle.points2D);
        if (Vector2.Distance(center, point) > radius * ContainmentTolerance) return null;

        // A nested seal is more specific — check children first
        foreach (var child in seal.children)
        {
            var inner = FindContainingRecursive(child, point, accept);
            if (inner != null) return inner;
        }
        return accept(seal) ? seal : null;
    }

    static bool IsClosedLeaf(Seal s) => s.outerCircle.isClosed && s.IsLeaf;
    static bool IsEmptyClosedLeaf(Seal s) => IsClosedLeaf(s) && s.element == null;

    public Seal FindSmallestContainingSeal(Vector2 point) => FindSmallestContaining(point, _ => true);

    // ---------- Seal tree: routing ----------

    /// <summary>Place a newly recognized stroke into the seal tree, or orphan it until a home appears.</summary>
    public void RouteStroke(Stroke stroke)
    {
        if (stroke.label == ShapeLabel.Unknown) return;

        Vector2 strokeCenter = Centroid(stroke.points2D);
        if (VerboseLogs) Debug.Log($"[Route] {stroke.label} at {strokeCenter}");

        if (stroke.label == ShapeLabel.OpenArc || stroke.label == ShapeLabel.ClosedCircle)
            RouteCircle(stroke, strokeCenter);
        else if (stroke.label == ShapeLabel.KeystoneW)
            RouteModifier(stroke, strokeCenter);
        else
            RouteElement(stroke, strokeCenter);
    }

    void RouteCircle(Stroke stroke, Vector2 center)
    {
        var newSeal = new Seal { outerCircle = stroke };
        float radius = FitRadius(stroke.points2D);

        // Adopt orphaned elements and modifiers that fall inside the new circle
        foreach (var o in TakeStrokesInside(orphanedElements, center, radius))
            if (newSeal.element == null) newSeal.element = o;
        newSeal.modifiers.AddRange(TakeStrokesInside(orphanedModifiers, center, radius));

        Seal parent = FindSmallestContainingSeal(center);
        if (VerboseLogs) Debug.Log($"[Route]   circle: parent={(parent != null)}, parentClosed={parent?.outerCircle.isClosed}, element={newSeal.element?.label}");
        if (parent != null && !parent.outerCircle.isClosed)
        {
            // Drawn inside an open scope — becomes a child of it
            parent.children.Add(newSeal);
            return;
        }

        // Otherwise it's top-level, and wraps any existing top-level seals it encloses
        var enclosed = topLevelSeals.FindAll(s =>
            Vector2.Distance(center, Centroid(s.outerCircle.points2D)) < radius);
        foreach (var s in enclosed)
        {
            topLevelSeals.Remove(s);
            newSeal.children.Add(s);
        }
        topLevelSeals.Add(newSeal);
    }

    void RouteModifier(Stroke stroke, Vector2 center)
    {
        Seal owner = FindSmallestContainingSeal(center);
        if (owner != null && IsClosedLeaf(owner))
            owner.modifiers.Add(stroke);
        else
            orphanedModifiers.Add(stroke); // no target yet
    }

    void RouteElement(Stroke stroke, Vector2 center)
    {
        Seal owner = FindSmallestContainingSeal(center);
        if (VerboseLogs) Debug.Log($"[Route]   element: owner={(owner != null)}, eligible={(owner != null && IsEmptyClosedLeaf(owner))}");
        if (owner != null && IsEmptyClosedLeaf(owner))
            owner.element = stroke;
        else
            orphanedElements.Add(stroke); // owner open, compound, or slot taken
    }

    static List<Stroke> TakeStrokesInside(List<Stroke> pool, Vector2 center, float radius)
    {
        var taken = pool.FindAll(s => Vector2.Distance(center, Centroid(s.points2D)) < radius);
        foreach (var s in taken) pool.Remove(s);
        return taken;
    }

    /// <summary>After an arc closes, sweep orphans into any leaf seal that can now hold them.</summary>
    public void AdoptOrphansIntoLeafSeals()
    {
        foreach (var orphan in new List<Stroke>(orphanedElements))
        {
            Seal target = FindSmallestContaining(Centroid(orphan.points2D), IsEmptyClosedLeaf);
            if (target == null) continue;
            orphanedElements.Remove(orphan);
            target.element = orphan;
        }

        foreach (var orphan in new List<Stroke>(orphanedModifiers))
        {
            Seal target = FindSmallestContaining(Centroid(orphan.points2D), IsClosedLeaf);
            if (target == null) continue;
            orphanedModifiers.Remove(orphan);
            target.modifiers.Add(orphan);
        }
    }
}
