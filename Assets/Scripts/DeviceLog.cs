using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// Writes everything the app logs to a plain text file on the headset, next to the recorded
// data. Pull it off with SideQuest > Files, the same folder as the CSVs.
//
// WHY THIS EXISTS RATHER THAN JUST USING LOGCAT:
//
// logcat is fragile in exactly the situations where the log matters most. Its filter
// arguments get mangled by SideQuest's command box, -t truncates BEFORE filtering so a
// filtered tail can come back empty, and - the one that really matters - a RELEASE build
// does not send Unity's Debug.Log to logcat at all. Release is what participants will run
// on, so relying on logcat means the sessions that count are the ones with no diagnostics.
//
// This hooks the log inside the app, so it works identically in development and release
// builds, needs no cable, no adb and no shell, and survives the app being closed.
//
// Attach to the ControlManager object. It only writes a log - it changes nothing.
public class DeviceLog : MonoBehaviour
{
    [Tooltip("Leave ON. The file is small, it is written on the headset only, and it is the " +
             "only diagnostic available in a release build.")]
    public bool enableFileLog = true;

    [Tooltip("Written to the app's own folder, beside the recorded CSVs.")]
    public string fileName = "device_log.txt";

    [Tooltip("Above this size the middle is dropped, so a long session cannot fill the " +
             "headset's storage.")]
    public int maxKilobytes = 2048;

    [Tooltip("Lines from the start of the file that are NEVER trimmed. The startup block says " +
             "which participant, visit and condition were loaded, and losing it means the " +
             "session cannot be verified afterwards.")]
    public int headerLinesAlwaysKept = 120;

    private readonly List<string> _pending = new List<string>();
    private readonly object _lock = new object();
    private string _path;
    private float _nextFlush = 0f;

    void Awake()
    {
        if (!enableFileLog) return;

        // Same folder DataRecorder writes the CSVs to, so there is one place to look.
        _path = Path.Combine(Application.persistentDataPath, fileName);

        Application.logMessageReceivedThreaded += OnLog;

        Queue($"=== app started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        Queue($"    build: {(Debug.isDebugBuild ? "DEVELOPMENT" : "release")}   unity {Application.unityVersion}   {Application.platform}");
        Queue($"    log file: {_path}");
    }

    void OnDestroy()
    {
        Application.logMessageReceivedThreaded -= OnLog;
        Queue($"=== app closed {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        Flush();
    }

    void OnApplicationPause(bool paused)
    {
        // Taking the headset off pauses the app. Flushing here means a session is never
        // lost just because the log was still sitting in memory.
        if (paused) Flush();
    }

    void Update()
    {
        // Batched, because writing a file inside the log callback would stall whichever
        // thread happened to log - including the one drawing the frame.
        if (Time.unscaledTime < _nextFlush) return;
        _nextFlush = Time.unscaledTime + 1f;
        Flush();
    }

    [Tooltip("Lines containing any of these are not written to the file. The networking debug " +
             "console re-logs its ENTIRE accumulated history every time a target enters or " +
             "leaves range, which filled 4 MB in fifteen minutes and pushed the startup lines " +
             "out of the file - including the one saying which participant and condition were " +
             "loaded. Filtering it keeps the log about the session rather than about itself.")]
    public string[] ignoreLinesContaining =
    {
        "is in range", "is out of range", "Despawning", "instantiated at",
        "objects to despawn", "Reset"
    };

    private void OnLog(string message, string stackTrace, LogType type)
    {
        // Errors and warnings are never filtered, whatever they contain.
        if (type == LogType.Log && ignoreLinesContaining != null)
        {
            foreach (string skip in ignoreLinesContaining)
                if (!string.IsNullOrEmpty(skip) && message.Contains(skip)) return;
        }

        string tag = type == LogType.Error || type == LogType.Exception || type == LogType.Assert
                        ? "ERROR"
                        : type == LogType.Warning ? "WARN " : "LOG  ";

        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("  ").Append(tag).Append("  ").Append(message);

        if ((type == LogType.Error || type == LogType.Exception) && !string.IsNullOrEmpty(stackTrace))
            sb.Append('\n').Append("        ").Append(stackTrace.Replace("\n", "\n        ").TrimEnd());

        Queue(sb.ToString());
    }

    private void Queue(string line)
    {
        lock (_lock) _pending.Add(line);
    }

    private void Flush()
    {
        if (string.IsNullOrEmpty(_path)) return;

        string[] lines;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            lines = _pending.ToArray();
            _pending.Clear();
        }

        try
        {
            File.AppendAllText(_path, string.Join("\n", lines) + "\n");

            var info = new FileInfo(_path);
            if (info.Exists && info.Length > maxKilobytes * 1024L)
            {
                // The FIRST lines are kept, not just the last. Trimming the oldest half threw
                // away the startup block - which is where SessionConfig reports which
                // participant, visit and condition were actually loaded. That is the single
                // most important line in the file: without it there is no way to confirm the
                // session ran as the allocation table intended.
                string[] all = File.ReadAllLines(_path);
                int keepHead = Mathf.Min(headerLinesAlwaysKept, all.Length);

                var kept = new List<string>();
                for (int i = 0; i < keepHead; i++) kept.Add(all[i]);
                kept.Add($"=== trimmed {DateTime.Now:HH:mm:ss}: middle dropped, startup block above kept ===");
                for (int i = all.Length / 2; i < all.Length; i++) kept.Add(all[i]);

                File.WriteAllLines(_path, kept);
            }
        }
        catch (Exception)
        {
            // Never let logging break a session, and never log the failure - that would
            // recurse straight back into OnLog.
        }
    }
}
