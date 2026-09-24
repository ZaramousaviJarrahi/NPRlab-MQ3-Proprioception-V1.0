using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Mirrors the Unity Console to a plain text file inside the project folder.
//
// WHY:
// The Console is the only place that says what the app actually did - which sequence was
// loaded, whether the config file overrode the Inspector, how many grasps a trial counted.
// None of that is visible from outside the Editor, so diagnosing anything meant
// screenshotting the Console and reading it back by eye. This writes the same messages to
// RecordedData/editor_console.log, which is an ordinary file that any tool can read.
//
// It is Editor-only (it lives in Assets/Editor, so it is never compiled into a build) and
// it only ever WRITES a log. It does not change the project, the scene, or any data.
//
// The file is gitignored by the existing *.log rule, so it will not be committed.
//
// Menu: Tools > NPRlab > Console Log
[InitializeOnLoad]
public static class ConsoleMirror
{
    private const string FileName = "editor_console.log";
    private const long   MaxBytes = 4 * 1024 * 1024;   // 4 MB, then the oldest half is dropped

    private static readonly List<string> _pending = new List<string>();
    private static readonly object _lock = new object();
    private static bool _enabled = true;

    private const string EnabledPref = "NPRlab.ConsoleMirror.Enabled";

    static ConsoleMirror()
    {
        _enabled = EditorPrefs.GetBool(EnabledPref, true);

        // Threaded, because Unity logs from background threads too (asset import, builds)
        // and those messages are exactly the ones that are hardest to catch by eye.
        Application.logMessageReceivedThreaded += OnLog;
        EditorApplication.update += Flush;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;

        Write($"=== Editor session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
    }

    public static string LogPath()
    {
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "RecordedData"));
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, FileName);
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        // A clear marker per Play session, so a run can be found in the file without
        // guessing where it started.
        if (state == PlayModeStateChange.EnteredPlayMode)
            Write($"{Environment.NewLine}=== PLAY  {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        else if (state == PlayModeStateChange.ExitingPlayMode)
            Write($"=== STOP  {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
    }

    private static void OnLog(string message, string stackTrace, LogType type)
    {
        if (!_enabled) return;

        string tag = type == LogType.Error || type == LogType.Exception || type == LogType.Assert
                        ? "ERROR"
                        : type == LogType.Warning ? "WARN " : "LOG  ";

        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("  ").Append(tag).Append("  ").Append(message);

        // Stack traces only for errors. Attaching them to every message would bury the
        // useful lines in noise and blow the file size up for no benefit.
        if ((type == LogType.Error || type == LogType.Exception) && !string.IsNullOrEmpty(stackTrace))
            sb.Append(Environment.NewLine).Append("        ").Append(stackTrace.Replace("\n", "\n        ").TrimEnd());

        Write(sb.ToString());
    }

    private static void Write(string line)
    {
        lock (_lock) _pending.Add(line);
    }

    private static void Flush()
    {
        string[] lines;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            lines = _pending.ToArray();
            _pending.Clear();
        }

        try
        {
            string path = LogPath();
            File.AppendAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            TrimIfTooBig(path);
        }
        catch (Exception)
        {
            // Never let logging break the Editor, and never log the failure - that would
            // recurse straight back into OnLog.
        }
    }

    private static void TrimIfTooBig(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= MaxBytes) return;

        string[] all = File.ReadAllLines(path);
        var kept = new List<string> { $"=== trimmed {DateTime.Now:yyyy-MM-dd HH:mm:ss}, oldest half dropped ===" };
        for (int i = all.Length / 2; i < all.Length; i++) kept.Add(all[i]);
        File.WriteAllLines(path, kept);
    }

    [MenuItem("Tools/NPRlab/Console Log/Open")]
    private static void Open()
    {
        string path = LogPath();
        if (!File.Exists(path)) File.WriteAllText(path, "");
        EditorUtility.RevealInFinder(path);
    }

    [MenuItem("Tools/NPRlab/Console Log/Clear")]
    private static void Clear()
    {
        try
        {
            File.WriteAllText(LogPath(), $"=== cleared {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
            Debug.Log("ConsoleMirror: log cleared.");
        }
        catch (Exception e) { Debug.LogWarning($"ConsoleMirror: could not clear the log - {e.Message}"); }
    }

    [MenuItem("Tools/NPRlab/Console Log/Enabled")]
    private static void ToggleEnabled()
    {
        _enabled = !_enabled;
        EditorPrefs.SetBool(EnabledPref, _enabled);
        Debug.Log($"ConsoleMirror: {(_enabled ? "ON" : "OFF")}.");
    }

    [MenuItem("Tools/NPRlab/Console Log/Enabled", true)]
    private static bool ToggleEnabledValidate()
    {
        Menu.SetChecked("Tools/NPRlab/Console Log/Enabled", _enabled);
        return true;
    }
}
