using UnityEngine;

/// <summary>Easing curves shared by grow, fade and resize animations.</summary>
public static class Ease
{
    /// <summary>Fast start, gentle settle.</summary>
    public static float OutCubic(float t) => 1f - Mathf.Pow(1f - t, 3f);
}
