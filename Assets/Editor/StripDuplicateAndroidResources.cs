#if UNITY_ANDROID
using System.IO;
using UnityEditor.Android;
using UnityEngine;

// Removes macOS/iCloud duplicate files from the generated Android project before Gradle
// compiles it.
//
// WHY THIS EXISTS:
// This project lives in a folder that macOS syncs. When the sync sees two versions of a file
// it keeps both, naming the second one "<name> 2.<ext>". Harmless almost everywhere - except
// in an Android resource folder, where filenames may only contain lowercase letters, digits
// and underscores. A single "network_sec_config 2.xml" fails the whole build with:
//
//     Error: ' ' is not a valid file-based resource name character
//
// Unity regenerates the Gradle project under Library/Bee on every build and the duplicate
// comes back with it, so deleting it by hand fixes exactly one build and no more. This hook
// runs after Unity has written the Gradle project and before Gradle compiles it, which is the
// only moment where the bad file exists and can still be removed.
//
// It only touches res/ folders inside the GENERATED project (Library/Bee/...), which is
// disposable build output. Nothing in Assets/ is read or changed.
public class StripDuplicateAndroidResources : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 0;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        // "path" is the unityLibrary module. The launcher module sits beside it, so sweep the
        // whole generated project rather than just this one folder.
        DirectoryInfo root = Directory.GetParent(path) ?? new DirectoryInfo(path);
        int removed = 0;

        foreach (string resDir in Directory.GetDirectories(root.FullName, "res", SearchOption.AllDirectories))
        {
            foreach (string file in Directory.GetFiles(resDir, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!IsInvalidResourceName(name)) continue;

                try
                {
                    File.Delete(file);
                    removed++;
                    Debug.Log($"StripDuplicateAndroidResources: removed invalid Android resource '{Path.GetFileName(file)}'.");
                }
                catch (IOException e)
                {
                    Debug.LogError($"StripDuplicateAndroidResources: could not remove '{file}' - {e.Message}. " +
                                   "The build will fail on this file; delete it in Finder and build again.");
                }
            }
        }

        if (removed > 0)
            Debug.Log($"StripDuplicateAndroidResources: cleaned {removed} duplicate resource file(s) before Gradle ran.");
    }

    // Android file-based resource names must be lowercase a-z, 0-9 or underscore.
    private static bool IsInvalidResourceName(string name)
    {
        foreach (char c in name)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
            if (!ok) return true;
        }
        return false;
    }
}
#endif
