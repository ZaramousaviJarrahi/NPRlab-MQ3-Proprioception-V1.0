#if UNITY_ANDROID
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Removes file-sync duplicates from the generated Android project before Gradle builds it.
//
// WHY THIS EXISTS:
// This project lives in a folder that macOS syncs. When the sync sees a file change it
// sometimes keeps the old copy alongside the new one, named "<name> 2.<ext>". Unity
// regenerates the whole Gradle project under Library/Bee on every build, so the sync has a
// large, rapidly-changing directory to trip over, and the duplicates come back every time.
//
// They have broken the build in three different ways so far:
//   - "network_sec_config 2.xml" - Android resource names may not contain spaces
//   - "AndroidManifest 2.xml" in a library module - confuses Gradle's configuration phase
//   - "libil2cpp 2.so" in jniLibs - a native library that cannot be packaged
//
// Everything under the generated project is disposable build output, so deleting anything
// that matches the duplicate pattern is safe: Unity rewrites it all next build.
//
// THIS IS A WORKAROUND, NOT A FIX. The sync can also corrupt file CONTENTS rather than just
// creating copies - that is what produced a damaged scene inside a built APK and cost most
// of a day. The real fix is to move the project out of the synced folder.
public class StripDuplicateAndroidResources : IPreprocessBuildWithReport,
                                              IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 0;

    // Runs BEFORE the build starts, and cleans Unity's build-artifact folders.
    //
    // This is a separate pass from the one below and it has to be, because of WHEN each
    // one runs. The post-generate pass happens after IL2CPP has already read the compiled
    // assemblies - far too late to remove a stray copy of one.
    //
    // A duplicated "Assembly-CSharp 2.dll" sitting next to "Assembly-CSharp.dll" is read by
    // IL2CPP as a second assembly declaring the same classes, and the error it produces
    // names a class and says it was declared twice in the same assembly - which sends you
    // hunting for a duplicate script that does not exist.
    public void OnPreprocessBuild(BuildReport report)
    {
        try
        {
            int removed = 0;
            foreach (string dir in new[] { "Library/Bee/artifacts", "Library/Bee/Android" })
            {
                string full = Path.Combine(Directory.GetCurrentDirectory(), dir);
                if (!Directory.Exists(full)) continue;
                foreach (string file in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
                {
                    string nm = Path.GetFileName(file);
                    if (IsProtected(nm) || !SyncDuplicate.IsMatch(nm)) continue;
                    try { File.Delete(file); removed++; } catch { }
                }
            }
            if (removed > 0)
                Debug.Log($"StripDuplicateAndroidResources: removed {removed} duplicated build " +
                          "input(s) before the build started.");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"StripDuplicateAndroidResources: pre-build cleanup skipped - " +
                             $"{e.GetType().Name}: {e.Message}");
        }
    }

    // "name 2.ext", "name 10.ext" - a space, digits, then the extension.
    private static readonly Regex SyncDuplicate = new Regex(@" \d+\.[A-Za-z0-9]+$");

    // Never delete these, whatever their name looks like.
    //
    // This project's real working scene is called "Main 2.unity" - it began as a sync
    // duplicate and then became the scene the study actually runs on. It matches the
    // duplicate pattern exactly. Nothing under Library/Bee should ever be a source scene,
    // so this guard should never fire; it exists because the cost of being wrong is
    // deleting the scene, and the cost of the guard is nothing.
    private static readonly string[] NeverDelete = { ".unity", ".prefab", ".asmdef" };

    private static bool IsProtected(string fileName)
    {
        foreach (string ext in NeverDelete)
            if (fileName.EndsWith(ext, System.StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        // The WHOLE hook is wrapped, deliberately.
        //
        // An exception thrown from a post-generate callback fails the Unity build outright,
        // with an error that points at the callback rather than at the real problem. This
        // hook is a convenience - it tidies up after a misbehaving file sync - and a
        // convenience must never be able to break a build. If it cannot do its job it says
        // so and gets out of the way.
        try { Clean(path); }
        catch (System.Exception e)
        {
            Debug.LogWarning($"StripDuplicateAndroidResources: skipped cleanup - " +
                             $"{e.GetType().Name}: {e.Message}. If the build fails on a file " +
                             "with a space and a number in its name, delete it in Finder.");
        }
    }

    private void Clean(string path)
    {
        DirectoryInfo root = Directory.GetParent(path) ?? new DirectoryInfo(path);
        int removed = 0;

        string[] files;
        try { files = Directory.GetFiles(root.FullName, "*", SearchOption.AllDirectories); }
        catch (System.Exception e)
        {
            Debug.LogWarning($"StripDuplicateAndroidResources: could not scan the generated " +
                             $"project ({e.GetType().Name}) - skipping cleanup.");
            return;
        }

        foreach (string file in files)
        {
            string name = Path.GetFileName(file);

            if (IsProtected(name)) continue;
            bool isSyncDuplicate = SyncDuplicate.IsMatch(name);
            bool isInvalidResource = IsInsideRes(file) && HasInvalidResourceName(name);

            if (!isSyncDuplicate && !isInvalidResource) continue;

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"StripDuplicateAndroidResources: could not remove '{file}' - {e.Message}. " +
                               "The build will probably fail on this file; delete it in Finder and build again.");
            }
        }

        if (removed > 0)
            Debug.Log($"StripDuplicateAndroidResources: removed {removed} file-sync duplicate(s) " +
                      "from the generated Android project before Gradle ran.");
    }

    private static bool IsInsideRes(string file)
    {
        string dir = Path.GetDirectoryName(file) ?? "";
        return dir.Contains($"{Path.DirectorySeparatorChar}res{Path.DirectorySeparatorChar}") ||
               dir.EndsWith($"{Path.DirectorySeparatorChar}res");
    }

    // Android file-based resource names must be lowercase a-z, 0-9 or underscore.
    private static bool HasInvalidResourceName(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        foreach (char c in stem)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
            if (!ok) return true;
        }
        return false;
    }
}
#endif
