using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A single drawn stroke: its LineRenderer, its 2D points on the session plane,
/// and its recognized shape. Owns the ink glow animations (ignite and snap flash).
/// </summary>
public class Stroke
{
    static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
    static readonly int EmissionStrengthId = Shader.PropertyToID("_EmissionStrength");

    public LineRenderer line;
    public List<Vector2> points2D;
    public ShapeLabel label;
    public bool isClosed = false;       // for arcs: has been closure-bridged
    public bool hasBeenIgnited = false;

    Material runtimeMaterial;

    /// <summary>Glow the ink in the given color as part of a seal firing.</summary>
    public void Ignite(MonoBehaviour host, Color hotColor, float intensity = 2.3f, float duration = 0.3f)
    {
        EnsureRuntimeMaterial();
        hasBeenIgnited = true;
        host.StartCoroutine(IgniteRoutine(hotColor, intensity, duration));
    }

    /// <summary>Brief glow-and-fade when a shape is recognized. Ignored once ignited.</summary>
    public void FlashSnap(MonoBehaviour host, Color flashColor, float duration = 0.4f)
    {
        if (hasBeenIgnited) return;
        EnsureRuntimeMaterial();
        host.StartCoroutine(FlashSnapRoutine(flashColor, duration));
    }

    void EnsureRuntimeMaterial()
    {
        // Clone the shared material so strokes don't share glow state
        if (runtimeMaterial != null) return;
        runtimeMaterial = new Material(line.material);
        line.material = runtimeMaterial;
    }

    void SetEmissionColor(Color c)
    {
        if (runtimeMaterial != null && runtimeMaterial.HasProperty(EmissionColorId))
            runtimeMaterial.SetColor(EmissionColorId, c);
    }

    void SetEmissionStrength(float v)
    {
        if (runtimeMaterial != null && runtimeMaterial.HasProperty(EmissionStrengthId))
            runtimeMaterial.SetFloat(EmissionStrengthId, v);
    }

    IEnumerator IgniteRoutine(Color hotColor, float intensity, float duration)
    {
        SetEmissionColor(hotColor);

        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            SetEmissionStrength(intensity * Mathf.Clamp01(t / duration));
            yield return null;
        }
        SetEmissionStrength(intensity);
    }

    IEnumerator FlashSnapRoutine(Color flashColor, float duration)
    {
        SetEmissionColor(flashColor);

        float rampUp = duration * 0.3f;
        float fadeDown = duration * 0.7f;
        const float peak = 0.8f;

        float t = 0f;
        while (t < rampUp)
        {
            if (hasBeenIgnited) yield break;  // ignition overrides the flash
            t += Time.deltaTime;
            SetEmissionStrength(Mathf.Clamp01(t / rampUp) * peak);
            yield return null;
        }

        t = 0f;
        while (t < fadeDown)
        {
            if (hasBeenIgnited) yield break;
            t += Time.deltaTime;
            SetEmissionStrength((1f - Mathf.Clamp01(t / fadeDown)) * peak);
            yield return null;
        }

        if (!hasBeenIgnited) SetEmissionStrength(0f);
    }
}
