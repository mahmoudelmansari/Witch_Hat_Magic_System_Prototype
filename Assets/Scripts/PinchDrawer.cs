using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

/// <summary>
/// Hand-tracked pinch drawing: turns pinch strokes into recognized shapes on drawing sessions,
/// and fires seals (spells) once they're complete.
/// </summary>
public class PinchDrawer : MonoBehaviour
{
    [Header("Input")]
    [SerializeField] InputActionProperty pinchAction;
    [SerializeField] Transform fingerTip;
    [SerializeField] InputActionProperty offhandPinchAction;
    [SerializeField] Transform offhandFingerTip;
    [SerializeField] float pinchSettleTime = 0.05f;

    [Header("Drawing")]
    [SerializeField] LineRenderer inkPrefab;
    [SerializeField] Transform cursorPrefab;
    [SerializeField] float drawDistanceThreshold = 0.01f;
    [SerializeField] float sessionMaxDistance = 0.4f;

    [Header("Palm Anchoring")]
    [SerializeField] Transform offhandPalm;
    [SerializeField] float anchorDistance = 0.15f;
    [Tooltip("Which local axis of the palm bone points outward from the palm surface.")]
    [SerializeField] Vector3 palmSurfaceNormal = Vector3.up;

    [Header("Plane Visual")]
    [FormerlySerializedAs("planPrefab")]
    [SerializeField] Transform planePrefab;
    [Tooltip("Extra margin around the drawn strokes when sizing the plane visual (0.2 = 20% padding).")]
    [SerializeField] float planeMarginRatio = 0.2f;
    [Tooltip("Minimum size (in meters) of the plane visual, even for tiny drawings.")]
    [SerializeField] float planeMinSize = 0.2f;
    [Tooltip("0.1 for Unity's Plane primitive (10 units wide), 1 for a Quad (1 unit wide).")]
    [SerializeField] float planeScaleMultiplier = 0.1f;
    [SerializeField] float planeResizeDuration = 0.3f;
    [SerializeField] float planeFadeInDuration = 0.5f;
    [Tooltip("How long the dissolve animation takes when deleting a session.")]
    [SerializeField] float dissolveDuration = 1f;

    [Header("Spell Effects")]
    [SerializeField] Transform fireballPrefab;
    [SerializeField] Transform windPrefab;
    [SerializeField] Transform waterPrefab;
    [SerializeField] Transform rockPrefab;
    [SerializeField] Transform soilPrefab;
    [SerializeField] Transform smokePrefab;
    [SerializeField] Transform plantPrefab;
    [SerializeField] Transform deadPlantPrefab;
    [SerializeField] Transform lightPrefab;
    [SerializeField] Transform snapVfxPrefab;
    [SerializeField] float rockGrowDuration = 0.6f;
    [Tooltip("How far behind the seal (away from the camera) the fireball spawns on vertical planes.")]
    [SerializeField] float effectOffset = 0.08f;
    [Tooltip("How far above the seal rock and light effects float.")]
    [SerializeField] float hoverOffset = 0.1f;

    [Header("Sound Effects")]
    [SerializeField] AudioClip fireSound;
    [SerializeField] AudioClip windSound;
    [SerializeField] AudioClip waterSound;
    [SerializeField] AudioClip snapSound;
    [SerializeField] AudioClip sealActivationSound;
    [SerializeField] float sfxVolume = 0.8f;
    [SerializeField] float snapVolume = 0.5f;

    [Header("Debug")]
    [SerializeField] bool verboseLogs = false;

    Camera xrCamera;
    readonly List<DrawingSession> sessions = new();
    DrawingSession currentSession;
    Stroke currentStroke;
    LineRenderer currentInk;
    readonly List<float> currentPointTimes = new();

    Vector3 lastFingerPosition;
    bool pinched;
    float pinchStartTime;
    bool hasFirstPoint;

    // ---------- Lifecycle ----------

    void Start()
    {
        xrCamera = Camera.main;
        DrawingSession.VerboseLogs = verboseLogs;
        pinchAction.action.performed += OnPinchStart;
        pinchAction.action.canceled += OnPinchEnd;
        offhandPinchAction.action.performed += OnOffhandPinch;
    }

    void OnEnable()
    {
        pinchAction.action.Enable();
        offhandPinchAction.action.Enable();
    }

    void OnDisable()
    {
        pinchAction.action.Disable();
        offhandPinchAction.action.Disable();
    }

    void OnDestroy()
    {
        pinchAction.action.performed -= OnPinchStart;
        pinchAction.action.canceled -= OnPinchEnd;
        offhandPinchAction.action.performed -= OnOffhandPinch;
    }

    void Update()
    {
        if (pinched) Draw();

        foreach (DrawingSession s in sessions)
        {
            if (currentSession != null)
            {
                if (s != currentSession) s.HidePlaneVisual();
            }
            else
            {
                s.UpdatePlaneVisual(fingerTip.position);
            }
        }

        if (currentSession != null)
            UpdateCurrentCursor();
        else
            ShowClosestCursorWhileNavigating();
    }

    // ---------- Input: pinch start / draw / end ----------

    void OnPinchStart(InputAction.CallbackContext ctx)
    {
        currentPointTimes.Clear();
        Vector3 startPos = fingerTip.position;

        // Drawing near the palm anchors the session to it
        bool shouldAnchor = offhandPalm != null &&
                            Vector3.Distance(startPos, offhandPalm.position) < anchorDistance;

        DrawingSession session = FindNearestSession(startPos, sessionMaxDistance, shouldAnchor);
        if (session == null)
        {
            session = new DrawingSession();
            if (shouldAnchor)
            {
                session.anchor = offhandPalm;
                Vector3 localStart = offhandPalm.InverseTransformPoint(startPos);
                session.plane = new Plane(palmSurfaceNormal.normalized, localStart);
                session.InitializeBasis(localStart);
            }
            else
            {
                session.plane = new Plane((startPos - xrCamera.transform.position).normalized, startPos);
                session.InitializeBasis(startPos);
            }
            sessions.Add(session);
        }
        currentSession = session;

        pinched = true;
        currentInk = Instantiate(inkPrefab);
        if (currentSession.anchor != null)
        {
            currentInk.transform.SetParent(currentSession.anchor, false);
            currentInk.useWorldSpace = false;
        }
        currentStroke = new Stroke { line = currentInk };
        currentSession.strokes.Add(currentStroke);

        pinchStartTime = Time.time;
        hasFirstPoint = false;

        Vector3 projected = ProjectToPlane(currentSession.WorldToSession(startPos), currentSession.plane);
        currentSession.InstantiatePlaneVisual(planePrefab, projected, this, planeFadeInDuration);
        currentSession.InstantiateCursor(cursorPrefab, projected);
    }

    void Draw()
    {
        if (currentSession == null || currentInk == null) return;
        if (Time.time - pinchStartTime < pinchSettleTime) return;

        Vector3 projected = ProjectToPlane(currentSession.WorldToSession(fingerTip.position), currentSession.plane);

        if (hasFirstPoint && Vector3.Distance(lastFingerPosition, projected) < drawDistanceThreshold)
            return;

        AddPoint(projected);
        lastFingerPosition = projected;
        hasFirstPoint = true;
        UpdatePlaneVisualBounds(currentSession);
    }

    void OnPinchEnd(InputAction.CallbackContext ctx)
    {
        // 1. Guard against missing state
        if (currentInk == null || currentSession == null)
        {
            CleanupAfterStroke();
            return;
        }

        // 2. Trim trailing wiggle from the hand releasing the pinch
        float cutoff = Time.time - pinchSettleTime;
        int keepCount = currentInk.positionCount;
        while (keepCount > 0 && currentPointTimes[keepCount - 1] > cutoff)
            keepCount--;
        currentInk.positionCount = keepCount;

        if (currentInk.positionCount < 2)
        {
            CleanupAfterStroke();
            return;
        }

        // 3. Extract points in plane coordinates
        var points2D = new List<Vector2>();
        for (int i = 0; i < currentInk.positionCount; i++)
            points2D.Add(currentSession.To2D(currentInk.GetPosition(i)));

        // 4. Bridge gesture: closes an open arc, then disappears
        if (TryCloseArc(points2D, currentSession))
        {
            Destroy(currentInk.gameObject);
            currentSession.strokes.Remove(currentStroke);
            UpdatePlaneVisualBounds(currentSession);
            TryFireSeal();
            CleanupAfterStroke();
            return;
        }

        // 5. Classify
        ShapeLabel label = StrokeClassifier.Classify(points2D);
        if (verboseLogs) Debug.Log($"Classified as: {label}");

        currentStroke.points2D = points2D;
        currentStroke.label = label;
        currentStroke.isClosed = label == ShapeLabel.ClosedCircle ||
                                 label == ShapeLabel.TriangleUp ||
                                 label == ShapeLabel.TriangleDown ||
                                 label == ShapeLabel.Square ||
                                 label == ShapeLabel.Diamond;

        // 6. Snap to the clean template
        if (label != ShapeLabel.Unknown)
        {
            var cleanPoints = StrokeSnapper.SnapToTemplate(points2D, label);
            RewriteLineRenderer(currentInk, cleanPoints, currentSession);
            currentStroke.points2D = cleanPoints;
        }

        // 7. Route into the seal tree, resize the canvas, try firing
        currentSession.RouteStroke(currentStroke);
        UpdatePlaneVisualBounds(currentSession);
        TryFireSeal();

        // 8. Snap feedback (not for open arcs, which aren't finalized)
        if (label != ShapeLabel.Unknown && label != ShapeLabel.OpenArc)
            PlaySnapFeedback(currentStroke, currentSession);

        CleanupAfterStroke();
    }

    void OnOffhandPinch(InputAction.CallbackContext ctx)
    {
        Vector3 pos = offhandFingerTip.position;
        DrawingSession nearest = null;
        float minDist = 0.1f;

        foreach (DrawingSession s in sessions)
        {
            float d = Mathf.Abs(s.plane.GetDistanceToPoint(s.WorldToSession(pos)));
            if (d < minDist) { minDist = d; nearest = s; }
        }
        if (nearest == null) return;

        StartCoroutine(nearest.DissolveAndDestroy(dissolveDuration));
        sessions.Remove(nearest);
        if (currentSession == nearest) currentSession = null;
    }

    void CleanupAfterStroke()
    {
        pinched = false;
        currentInk = null;
        currentStroke = null;
        currentSession = null;
    }

    void AddPoint(Vector3 p)
    {
        currentInk.positionCount++;
        currentInk.SetPosition(currentInk.positionCount - 1, p);
        currentPointTimes.Add(Time.time);
    }

    void RewriteLineRenderer(LineRenderer line, List<Vector2> cleanPoints, DrawingSession session)
    {
        line.positionCount = cleanPoints.Count;
        for (int i = 0; i < cleanPoints.Count; i++)
            line.SetPosition(i, session.To3D(cleanPoints[i]));
    }

    static Vector3 ProjectToPlane(Vector3 point, Plane plane) =>
        point - plane.normal * plane.GetDistanceToPoint(point);

    // ---------- Sessions & cursor ----------

    DrawingSession FindNearestSession(Vector3 worldPos, float maxDist, bool anchored)
    {
        DrawingSession nearest = null;
        float minDist = maxDist;
        foreach (DrawingSession s in sessions)
        {
            if (anchored != (s.anchor != null)) continue;

            float d = Mathf.Abs(s.plane.GetDistanceToPoint(s.WorldToSession(worldPos)));
            if (d < minDist) { minDist = d; nearest = s; }
        }
        return nearest;
    }

    void UpdateCurrentCursor()
    {
        Vector3 projected = ProjectToPlane(currentSession.WorldToSession(fingerTip.position), currentSession.plane);
        currentSession.ShowCursor(projected);
    }

    void ShowClosestCursorWhileNavigating()
    {
        Vector3 pos = fingerTip.position;
        const float range = 0.1f;
        DrawingSession nearest = FindNearestSession(pos, range, anchored: false)
                              ?? FindNearestSession(pos, range, anchored: true);
        if (nearest == null) return;

        nearest.ShowCursor(ProjectToPlane(nearest.WorldToSession(pos), nearest.plane));
    }

    void UpdatePlaneVisualBounds(DrawingSession session)
    {
        if (session.anchor != null) return; // anchored sessions have no plane visual

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;

        void Include(Vector2 p)
        {
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.y < minY) minY = p.y;
            if (p.y > maxY) maxY = p.y;
        }

        foreach (var stroke in session.strokes)
            if (stroke.points2D != null)
                foreach (var p in stroke.points2D) Include(p);

        // Include the in-progress stroke too, so the canvas grows while drawing
        if (currentInk != null && currentSession == session)
        {
            for (int i = 0; i < currentInk.positionCount; i++)
            {
                Vector3 p3 = currentInk.GetPosition(i);
                Vector3 sessionPos = currentInk.useWorldSpace ? session.WorldToSession(p3) : p3;
                Include(session.To2D(sessionPos));
            }
        }

        if (minX == float.MaxValue) return;

        Vector2 center2D = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
        float width = (maxX - minX) * (1f + planeMarginRatio);
        float height = (maxY - minY) * (1f + planeMarginRatio);
        float size = Mathf.Max(width, height, planeMinSize);

        session.AnimatePlaneVisual(this, session.To3D(center2D), Vector3.one * size * planeScaleMultiplier, planeResizeDuration);
    }

    // ---------- Arc closure ----------

    bool TryCloseArc(List<Vector2> bridgePoints, DrawingSession session)
    {
        Vector2 bridgeStart = bridgePoints[0];
        Vector2 bridgeEnd = bridgePoints[bridgePoints.Count - 1];
        Vector2 bridgeDir = (bridgeEnd - bridgeStart).normalized;

        foreach (Stroke arc in session.AllOpenArcs())
        {
            Vector2 arcStart = arc.points2D[0];
            Vector2 arcEnd = arc.points2D[arc.points2D.Count - 1];

            // Generous start tolerance — about a hand's width
            float startTolerance = Mathf.Max(DrawingSession.FitRadius(arc.points2D) * 0.5f, 0.05f);

            Vector2 startedFrom, targetEndpoint;
            if (Vector2.Distance(bridgeStart, arcStart) < startTolerance)
            {
                startedFrom = arcStart;
                targetEndpoint = arcEnd;
            }
            else if (Vector2.Distance(bridgeStart, arcEnd) < startTolerance)
            {
                startedFrom = arcEnd;
                targetEndpoint = arcStart;
            }
            else continue;

            // The bridge must head toward the other endpoint (within 60°)
            Vector2 desiredDir = (targetEndpoint - startedFrom).normalized;
            if (Vector2.Dot(bridgeDir, desiredDir) < 0.5f) continue;

            var closed = StrokeSnapper.SnapToTemplate(arc.points2D, ShapeLabel.ClosedCircle);
            RewriteLineRenderer(arc.line, closed, session);
            arc.label = ShapeLabel.ClosedCircle;
            arc.points2D = closed;
            arc.isClosed = true;

            // The newly closed seal may now be able to adopt orphans
            session.AdoptOrphansIntoLeafSeals();
            if (verboseLogs) Debug.Log("[Close] Arc closed by bridge gesture");
            return true;
        }
        return false;
    }

    // ---------- Firing ----------

    void TryFireSeal()
    {
        if (currentSession == null) return;

        if (verboseLogs) LogSealTree(currentSession);

        Seal fireable = currentSession.GetFireableSeal();
        if (fireable == null) return;

        PlaySoundAt(sealActivationSound, SealCenterWorld(currentSession, fireable), currentSession);

        if (fireable.IsLeaf)
            FireLeafSeal(fireable, currentSession);
        else
            StartCoroutine(FireCompoundRoutine(fireable, currentSession));
    }

    void FireLeafSeal(Seal seal, DrawingSession session)
    {
        switch (seal.element?.label ?? ShapeLabel.Unknown)
        {
            case ShapeLabel.TriangleUp: FireFireSeal(seal, session); break;
            case ShapeLabel.TriangleDown: FireWaterSeal(seal, session); break;
            case ShapeLabel.Square: FireEarthSeal(seal, session); break;
            case ShapeLabel.Diamond: FireLightSeal(seal, session); break;
            case ShapeLabel.Wave: FireWindSeal(seal, session); break;
        }
    }

    IEnumerator FireCompoundRoutine(Seal seal, DrawingSession session)
    {
        seal.hasFired = true;

        // 1. Outer ignites first
        seal.outerCircle.Ignite(this, CompoundOuterColor);
        PlaySnapAt(SealCenterWorld(session, seal), session);
        yield return new WaitForSeconds(0.4f);

        // 2. Children ignite clockwise, starting from the topmost
        foreach (var child in SortClockwiseFromTop(seal))
        {
            Color c = GetIgnitionColor(child.element?.label ?? ShapeLabel.Unknown);
            child.outerCircle.Ignite(this, c);
            child.element?.Ignite(this, c);
            foreach (var mod in child.modifiers) mod.Ignite(this, c);

            PlaySnapAt(SealCenterWorld(session, child), session);
            yield return new WaitForSeconds(0.35f);
        }
        yield return new WaitForSeconds(0.2f);

        // 3. Resolve the recipe
        var elements = seal.GetChildElements();
        Vector3 spawnPos = SealCenterWorld(session, seal);

        if (ContainsExactly(elements, ShapeLabel.TriangleUp, ShapeLabel.TriangleDown, ShapeLabel.Wave))
        {
            SpawnGrowing(smokePrefab, spawnPos, Quaternion.identity, session);
            if (verboseLogs) Debug.Log("💨 Smoke");
        }
        else if (ContainsExactly(elements, ShapeLabel.Square, ShapeLabel.TriangleDown, ShapeLabel.Wave))
        {
            SpawnGrowing(deadPlantPrefab, spawnPos, Quaternion.Euler(-90, 0, 0), session);
            if (verboseLogs) Debug.Log("🌾 Wilted plant");
        }
        else if (ContainsExactly(elements, ShapeLabel.Square, ShapeLabel.TriangleDown, ShapeLabel.Wave, ShapeLabel.Diamond))
        {
            SpawnGrowing(plantPrefab, spawnPos, Quaternion.identity, session);
            if (verboseLogs) Debug.Log("🌱 Healthy plant");
        }
        else if (verboseLogs)
        {
            Debug.Log($"Compound [{string.Join(", ", elements)}] — no matching recipe");
        }
    }

    static List<Seal> SortClockwiseFromTop(Seal seal)
    {
        Vector2 outerCenter = DrawingSession.Centroid(seal.outerCircle.points2D);
        var ordered = new List<Seal>(seal.children);

        // Note: basisU points world-left, hence -x for a clockwise angle
        float AngleOf(Seal s)
        {
            Vector2 c = DrawingSession.Centroid(s.outerCircle.points2D) - outerCenter;
            return Mathf.Atan2(-c.x, c.y);
        }

        Seal topmost = ordered[0];
        foreach (var child in ordered)
            if (DrawingSession.Centroid(child.outerCircle.points2D).y >
                DrawingSession.Centroid(topmost.outerCircle.points2D).y)
                topmost = child;

        float startAngle = AngleOf(topmost);
        float Relative(Seal s) => Mathf.Repeat(AngleOf(s) - startAngle, 2f * Mathf.PI);

        ordered.Sort((a, b) => Relative(a).CompareTo(Relative(b)));
        return ordered;
    }

    static bool ContainsExactly(List<ShapeLabel> actual, params ShapeLabel[] expected)
    {
        if (actual.Count != expected.Length) return false;
        var remaining = new List<ShapeLabel>(actual);
        foreach (var e in expected)
            if (!remaining.Remove(e)) return false;
        return true;
    }

    // ---------- Leaf spells ----------

    void IgniteLeaf(Seal seal, Color c)
    {
        seal.outerCircle.Ignite(this, c);
        seal.element.Ignite(this, c);
        foreach (var mod in seal.modifiers) mod.Ignite(this, c);
        seal.hasFired = true;
    }

    void FireFireSeal(Seal seal, DrawingSession session)
    {
        IgniteLeaf(seal, GetIgnitionColor(ShapeLabel.TriangleUp));

        Vector3 spawnPos = SealCenterWorld(session, seal);
        spawnPos += GetFireballOffset(session.SessionDirToWorld(session.plane.normal), spawnPos);

        SpawnGrowing(fireballPrefab, spawnPos, Quaternion.identity, session);
        PlaySoundAt(fireSound, spawnPos, session);
    }

    void FireWaterSeal(Seal seal, DrawingSession session)
    {
        IgniteLeaf(seal, GetIgnitionColor(ShapeLabel.TriangleDown));

        Vector3 spawnPos = SealCenterWorld(session, seal);
        SpawnGrowing(waterPrefab, spawnPos, Quaternion.identity, session);
        PlaySoundAt(waterSound, spawnPos, session);
    }

    void FireEarthSeal(Seal seal, DrawingSession session)
    {
        IgniteLeaf(seal, GetIgnitionColor(ShapeLabel.Square));

        bool onSoil = seal.HasModifier(ShapeLabel.KeystoneW);
        Vector3 spawnPos = SealCenterWorld(session, seal) + Vector3.up * hoverOffset;
        SpawnGrowing(onSoil ? soilPrefab : rockPrefab, spawnPos, Quaternion.identity, session);
    }

    void FireLightSeal(Seal seal, DrawingSession session)
    {
        IgniteLeaf(seal, GetIgnitionColor(ShapeLabel.Diamond));

        Vector3 spawnPos = SealCenterWorld(session, seal) + Vector3.up * hoverOffset;
        Transform light = SpawnGrowing(lightPrefab, spawnPos, Quaternion.identity, session);

        // On the palm, the light follows the hand
        if (light != null && session.anchor != null)
            light.SetParent(session.anchor, true);
    }

    void FireWindSeal(Seal seal, DrawingSession session)
    {
        IgniteLeaf(seal, GetIgnitionColor(ShapeLabel.Wave));

        Vector3 spawnPos = SealCenterWorld(session, seal);

        // Wind blows from the wave's first point toward its last
        var pts = seal.element.points2D;
        Vector2 dir2D = pts[pts.Count - 1] - pts[0];
        Vector3 dir = session.SessionDirToWorld((session.basisU * dir2D.x + session.basisV * dir2D.y).normalized);

        Transform wind = SpawnGrowing(windPrefab, spawnPos, Quaternion.LookRotation(dir), session);
        if (wind != null && wind.TryGetComponent(out WindEffect effect))
            effect.windDirection = dir;

        PlaySoundAt(windSound, spawnPos, session);
    }

    // ---------- Spell helpers ----------

    static readonly Color CompoundOuterColor = new(1f, 0.85f, 0.4f);
    static readonly Color SnapFlashColor = new(1f, 1f, 0.7f);

    static Color GetIgnitionColor(ShapeLabel label) => label switch
    {
        ShapeLabel.TriangleUp => new Color(1f, 0.5f, 0.1f),     // fire orange
        ShapeLabel.TriangleDown => new Color(0.2f, 0.6f, 1f),   // water blue
        ShapeLabel.Square => new Color(0.55f, 0.35f, 0.15f),    // earth brown
        ShapeLabel.Diamond => new Color(1f, 0.95f, 0.7f),       // light white-yellow
        ShapeLabel.Wave => new Color(0.4f, 1f, 0.4f),           // wind green
        _ => CompoundOuterColor
    };

    static Vector3 SealCenterWorld(DrawingSession session, Seal seal) =>
        StrokeCenterWorld(session, seal.outerCircle);

    static Vector3 StrokeCenterWorld(DrawingSession session, Stroke stroke) =>
        session.SessionToWorld(session.To3D(DrawingSession.Centroid(stroke.points2D)));

    Transform SpawnGrowing(Transform prefab, Vector3 position, Quaternion rotation, DrawingSession session)
    {
        if (prefab == null) return null;
        Transform spawned = Instantiate(prefab, position, rotation);
        session.spawnedEffects.Add(spawned);
        StartCoroutine(GrowRoutine(spawned, spawned.localScale, rockGrowDuration));
        return spawned;
    }

    /// <summary>
    /// Fireball only: push it behind the seal (away from the camera) on vertical planes,
    /// so the seal sits between the viewer and the flame. No push on horizontal planes.
    /// </summary>
    Vector3 GetFireballOffset(Vector3 worldNormal, Vector3 pointOnPlane)
    {
        float verticalness = 1f - Mathf.Abs(Vector3.Dot(worldNormal, Vector3.up));
        Vector3 awayFromCamera = (pointOnPlane - xrCamera.transform.position).normalized;
        return awayFromCamera * effectOffset * verticalness;
    }

    static IEnumerator GrowRoutine(Transform target, Vector3 finalScale, float duration)
    {
        if (target == null) yield break;
        target.localScale = Vector3.zero;
        float t = 0f;
        while (t < duration)
        {
            if (target == null) yield break;
            t += Time.deltaTime;
            target.localScale = Vector3.Lerp(Vector3.zero, finalScale, Ease.OutCubic(Mathf.Clamp01(t / duration)));
            yield return null;
        }
        if (target != null) target.localScale = finalScale;
    }

    // ---------- Snap feedback & audio ----------

    void PlaySnapFeedback(Stroke stroke, DrawingSession session)
    {
        Vector3 pos = StrokeCenterWorld(session, stroke);
        PlaySnapAt(pos, session);

        if (snapVfxPrefab != null)
            session.spawnedEffects.Add(Instantiate(snapVfxPrefab, pos, Quaternion.identity));

        if (!stroke.hasBeenIgnited)
            stroke.FlashSnap(this, SnapFlashColor);
    }

    void PlaySnapAt(Vector3 position, DrawingSession session)
    {
        // Slight random pitch so repeated snaps don't sound identical
        PlaySoundAt(snapSound, position, session, Random.Range(0.85f, 1.2f), snapVolume);
    }

    void PlaySoundAt(AudioClip clip, Vector3 position, DrawingSession session, float pitch = 1f, float volume = -1f)
    {
        if (clip == null) return;

        var go = new GameObject("SFX_" + clip.name);
        go.transform.position = position;
        var src = go.AddComponent<AudioSource>();
        src.clip = clip;
        src.volume = volume < 0f ? sfxVolume : volume;
        src.spatialBlend = 1f;
        src.pitch = pitch;
        src.Play();

        Destroy(go, clip.length / Mathf.Max(pitch, 0.1f) + 0.1f);
        session.spawnedEffects.Add(go.transform); // dies early if the session is deleted
    }

    // ---------- Debug ----------

    static void LogSealTree(DrawingSession session)
    {
        Debug.Log($"[TryFire] top-level seals: {session.topLevelSeals.Count}, orphans: {session.orphanedElements.Count}");
        foreach (var top in session.topLevelSeals)
        {
            Debug.Log($"  Seal: closed={top.outerCircle.isClosed}, element={top.element?.label}, children={top.children.Count}, fired={top.hasFired}, valid={top.IsValid()}");
            foreach (var child in top.children)
                Debug.Log($"    Child: closed={child.outerCircle.isClosed}, element={child.element?.label}, valid={child.IsValid()}");
        }
    }
}
