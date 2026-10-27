using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Writes the current git version into the build, so that every data file can name the
/// exact code that produced it.
///
/// WHY
///   A session CSV said who was tested, when, and under what condition, but nothing about
///   which build wrote it. Six months on, a strange-looking session could not be placed
///   before or after any given fix. Git already names every version of the code; this puts
///   that name inside the app, and DataRecorder writes it into each row as app_commit.
///
/// WHY IT HAS TO HAPPEN AT BUILD TIME
///   The headset has no git and no repository. The only moment the version is knowable is
///   while the build is being made on the development machine, so it is captured here and
///   carried along as a text asset.
///
/// WHAT GETS WRITTEN
///   `git describe --always --dirty --abbrev=7`, which gives:
///     collection-v1          the tag, when the build sits on one
///     9ba7009                the short commit hash otherwise
///     9ba7009-dirty          the hash, plus a warning that the working tree had
///                            uncommitted changes - so the hash alone does not describe
///                            what was actually built
///   "unstamped" if git could not be run at all. That is deliberately a value rather than
///   a silent blank: DataRecorder warns about it at session start.
///
/// Runs automatically before every player build. There is also Tools > NPRlab > Stamp build
/// version in the menu, for checking what it produces without doing a build.
///
/// Assets/Editor/ so it is never compiled into the player.
/// </summary>
public class StampBuildVersion : IPreprocessBuildWithReport
{
    private const string StampAsset = "Assets/Resources/build_stamp.txt";

    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        Stamp();
    }

    [MenuItem("Tools/NPRlab/Stamp build version")]
    public static void StampFromMenu()
    {
        string s = Stamp();
        EditorUtility.DisplayDialog(
            "Build stamp",
            "build_stamp.txt now reads:\n\n    " + s + "\n\n" +
            (s == "unstamped"
                ? "git could not be read. Data from this build will not identify its code."
                : s.Contains("dirty")
                    ? "The working tree has uncommitted changes, so this names code that is "
                      + "not exactly what would be built. Commit first for a real session."
                    : "This is what app_commit will say in every row of the next session."),
            "OK");
    }

    private static string Stamp()
    {
        string stamp = Describe();

        Directory.CreateDirectory(Path.GetDirectoryName(StampAsset));
        File.WriteAllText(StampAsset, stamp);
        AssetDatabase.ImportAsset(StampAsset);

        if (stamp == "unstamped")
        {
            Debug.LogWarning("StampBuildVersion: git could not be read, so this build is "
                           + "unstamped and its data files will not identify the code that "
                           + "collected them. The build itself is unaffected.");
        }
        else if (stamp.Contains("dirty"))
        {
            Debug.LogWarning($"StampBuildVersion: stamped {stamp}. The -dirty marker means the "
                           + "working tree had uncommitted changes, so the commit named in the "
                           + "data is not a complete description of what was built. Commit "
                           + "before building for a session that will be analysed.");
        }
        else
        {
            Debug.Log($"StampBuildVersion: build stamped {stamp}.");
        }

        return stamp;
    }

    /// <summary>
    /// Asks git for a short name for the current state of the repository. Never throws:
    /// a build must not fail because git is missing, and an unstamped build is a worse
    /// outcome than a broken build only if nobody is told, which is why the caller warns.
    /// </summary>
    private static string Describe()
    {
        try
        {
            // Application.dataPath is <project>/Assets in the editor, so the parent is the
            // project root, which is also the repository root for this project.
            string repo = Directory.GetParent(Application.dataPath).FullName;

            var psi = new ProcessStartInfo("git", "describe --always --dirty --abbrev=7")
            {
                WorkingDirectory = repo,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(5000);

                if (p.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                    return stdout;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"StampBuildVersion: could not run git - {e.Message}");
        }

        return "unstamped";
    }
}
