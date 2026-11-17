#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor-only: mirrors every Console message to Logs/console.log in the project folder,
/// so external tools can read it. Cleared each time Play mode starts.
/// </summary>
[InitializeOnLoad]
static class ConsoleMirror
{
    static readonly string LogPath =
        Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Logs", "console.log");

    static ConsoleMirror()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
        Application.logMessageReceived -= OnLog;
        Application.logMessageReceived += OnLog;
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode)
            File.WriteAllText(LogPath, $"--- Play started {DateTime.Now:HH:mm:ss} ---\n");
    }

    static void OnLog(string message, string stackTrace, LogType type)
    {
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] [{type}] {message}\n";
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                line += stackTrace + "\n";
            File.AppendAllText(LogPath, line);
        }
        catch { /* never let logging break the editor */ }
    }
}
#endif
