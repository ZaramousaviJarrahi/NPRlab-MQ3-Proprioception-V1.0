using System.Globalization;
using System.IO;
using UnityEngine;

// Reads the session setup from a plain text file on the headset, so a participant can be
// configured from the experimenter's computer instead of with a thumbstick inside VR.
//
// WHY THIS EXISTS:
// Participant ID, visit, practice schedule and task order previously lived in the Inspector
// or were dialled in with the right thumbstick. Both are error-prone in a running study:
// the Inspector route means rebuilding the app to counterbalance each participant, and the
// thumbstick route means a mistyped ID is discovered only when the data file is opened. A
// config file is edited on a keyboard, checked before the session, uploaded with SideQuest,
// and leaves a record of exactly how that session was set up.
//
// HOW TO USE IT:
//   1. Edit session_config.txt on your computer.
//   2. SideQuest > Files > Android/data/<package>/files/ > Upload.
//   3. Launch the app. The values are applied before anything else runs.
//
// If the file is absent the app keeps whatever is set in the Inspector and says so in the
// log, so a missing file degrades to the old behaviour rather than failing.
//
// Attach to the same GameObject as ControlManager.
public class SessionConfig : MonoBehaviour
{
    [Header("Config file")]
    public bool useConfigFile = true;
    public string fileName = "session_config.txt";

    [Header("Allocation table")]
    [Tooltip("Looks the participant up in allocation.csv and sets their condition, task " +
             "order and seed automatically, so the only thing you edit per participant is " +
             "the participant number. Upload allocation.csv once; it never changes.")]
    public bool useAllocationTable = true;
    public string allocationFileName = "allocation.csv";

    [Tooltip("Writes session_config_TEMPLATE.txt next to the data files on first run, so "
           + "there is always a correctly-formatted example to copy.")]
    public bool writeTemplateIfMissing = true;

    [Header("Result (read-only)")]
    public bool loaded = false;
    public string loadedFrom = "";
    public string summary = "";

    // Awake, not Start: every other component reads these values in its own Start, and
    // Unity runs all Awakes before any Start.
    void Awake()
    {
        if (!useConfigFile) return;

        string folder = DataFolder();
        string path = Path.Combine(folder, fileName);

        if (!File.Exists(path))
        {
            Debug.LogWarning($"SessionConfig: no {fileName} found at {folder} - using the " +
                             "values set in the Inspector instead.");
            if (writeTemplateIfMissing) WriteTemplate(folder);
            return;
        }

        try
        {
            Apply(File.ReadAllLines(path));
            if (useAllocationTable) ApplyAllocation(folder);
            loaded = true;
            loadedFrom = path;
            Debug.Log($"SessionConfig loaded from {path}  ->  {summary}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"SessionConfig: could not read {path} - {e.GetType().Name}: {e.Message}. " +
                           "Using Inspector values.");
        }
    }

    public static string DataFolder()
    {
#if UNITY_EDITOR
        string f = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "RecordedData"));
        Directory.CreateDirectory(f);
        return f;
#else
        return Application.persistentDataPath;
#endif
    }

    private void Apply(string[] lines)
    {
        var controls  = GetComponent<ExperimenterControls>();
        var counter   = GetComponent<TrialCounter>();
        var runner    = GetComponent<SessionRunner>();
        var sequencer = GetComponent<TaskSequencer>();

        var applied = new System.Collections.Generic.List<string>();

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            string key = line.Substring(0, eq).Trim().ToLowerInvariant();
            string val = line.Substring(eq + 1).Trim();
            if (val.Length == 0) continue;

            switch (key)
            {
                case "participant":
                    // Accepts "P07" or "7". The prefix and number are stored separately
                    // because the data file name is built from them.
                    if (controls != null)
                    {
                        string digits = "";
                        string prefix = "";
                        foreach (char c in val) { if (char.IsDigit(c)) digits += c; else prefix += c; }
                        if (prefix.Length > 0) controls.participantPrefix = prefix;
                        if (int.TryParse(digits, out int n)) controls.participantNumber = Mathf.Clamp(n, 1, 999);
                        applied.Add($"participant={controls.ParticipantId()}");
                    }
                    break;

                case "visit":
                    if (controls != null && int.TryParse(val, out int v))
                    {
                        controls.visitNumber = Mathf.Clamp(v, 1, 3);
                        applied.Add($"visit={controls.visitNumber}");
                    }
                    break;

                case "condition":
                case "schedule":
                    if (counter != null)
                    {
                        bool random = val.ToLowerInvariant().StartsWith("r");
                        counter.practiceSchedule = random ? TrialCounter.PracticeSchedule.Random
                                                          : TrialCounter.PracticeSchedule.Blocked;
                        applied.Add($"condition={counter.practiceSchedule}");
                    }
                    break;

                case "taskorder":
                    if (runner != null) { runner.blockedTaskOrder = val; applied.Add($"taskOrder={val}"); }
                    break;

                case "seed":
                    if (runner != null && int.TryParse(val, out int s))
                    {
                        runner.randomSeed = s;
                        applied.Add($"seed={s}");
                    }
                    break;

                case "trialspertask":
                    if (runner != null && int.TryParse(val, out int tpt))
                    {
                        runner.trialsPerTask = Mathf.Max(1, tpt);
                        applied.Add($"trialsPerTask={runner.trialsPerTask}");
                    }
                    break;

                case "familiarizationtrials":
                    if (runner != null && int.TryParse(val, out int fam))
                    {
                        runner.familiarizationTrials = Mathf.Max(0, fam);
                        applied.Add($"familiarizationTrials={runner.familiarizationTrials}");
                    }
                    break;

                case "transfer3": if (sequencer != null) { sequencer.transfer3 = val; applied.Add($"transfer3={val}"); } break;
                case "transfer5": if (sequencer != null) { sequencer.transfer5 = val; applied.Add($"transfer5={val}"); } break;

                case "retentiontrialspertask":
                    if (runner != null && int.TryParse(val, out int rt))
                    { runner.retentionTrialsPerTask = Mathf.Max(0, rt); applied.Add($"retentionTrialsPerTask={rt}"); }
                    break;
                case "transfertrials":
                    if (runner != null && int.TryParse(val, out int tt))
                    { runner.transferTrials = Mathf.Max(0, tt); applied.Add($"transferTrials={tt}"); }
                    break;
                case "retentionorder":
                    if (runner != null)
                    {
                        runner.retentionOrder = val.ToLowerInvariant().StartsWith("b")
                            ? SessionRunner.RetentionOrder.Blocked
                            : SessionRunner.RetentionOrder.Random;
                        applied.Add($"retentionOrder={runner.retentionOrder}");
                    }
                    break;

                case "taska": if (sequencer != null) { sequencer.taskA = val; applied.Add($"taskA={val}"); } break;
                case "taskb": if (sequencer != null) { sequencer.taskB = val; applied.Add($"taskB={val}"); } break;
                case "taskc": if (sequencer != null) { sequencer.taskC = val; applied.Add($"taskC={val}"); } break;

                case "gridwidth":
                    if (sequencer != null && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float gw))
                    { sequencer.gridWidth = gw; applied.Add($"gridWidth={gw}"); }
                    break;
                case "gridheight":
                    if (sequencer != null && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float gh))
                    { sequencer.gridHeight = gh; applied.Add($"gridHeight={gh}"); }
                    break;
                case "griddistance":
                    if (sequencer != null && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float gd))
                    { sequencer.gridDistance = gd; applied.Add($"gridDistance={gd}"); }
                    break;

                default:
                    Debug.LogWarning($"SessionConfig: ignoring unknown setting '{key}'.");
                    break;
            }
        }

        summary = string.Join("  ", applied);
    }

    // Looks the participant up in allocation.csv and applies their assigned condition,
    // task order and seed.
    //
    // WHY: typing the condition by hand for every participant is four chances to make a
    // mistake that is invisible until analysis - and assigning someone to the wrong group
    // does not produce an error, it produces a quietly wrong dataset. The allocation was
    // decided in advance; the software should enforce it rather than trust a tired person
    // at 9am to transcribe it correctly.
    //
    // The table wins over anything typed in session_config.txt for these three settings,
    // and says so in the log if they disagree. Untick useAllocationTable to go back to
    // setting them by hand.
    private void ApplyAllocation(string folder)
    {
        var controls = GetComponent<ExperimenterControls>();
        if (controls == null) return;

        string path = Path.Combine(folder, allocationFileName);
        if (!File.Exists(path))
        {
            Debug.LogWarning($"SessionConfig: no {allocationFileName} found - using the " +
                             "condition and task order from session_config.txt instead.");
            return;
        }

        string me = controls.ParticipantId().Trim().ToLowerInvariant();

        try
        {
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 2) { Debug.LogWarning("SessionConfig: allocation.csv is empty."); return; }

            // Find the columns by NAME, so extra columns (visit dates, notes) and any
            // column reordering in Excel do not break the lookup.
            string[] head = SplitCsv(lines[0]);
            int cPid = IndexOf(head, "participant");
            int cCond = IndexOf(head, "condition");
            int cOrder = IndexOf(head, "taskorder");
            int cSeed = IndexOf(head, "seed");

            if (cPid < 0)
            {
                Debug.LogError("SessionConfig: allocation.csv has no 'participant' column.");
                return;
            }

            for (int i = 1; i < lines.Length; i++)
            {
                string[] cell = SplitCsv(lines[i]);
                if (cell.Length <= cPid) continue;
                if (cell[cPid].Trim().ToLowerInvariant() != me) continue;

                var counter = GetComponent<TrialCounter>();
                var runner = GetComponent<SessionRunner>();
                var applied = new System.Collections.Generic.List<string>();

                if (cCond >= 0 && cCond < cell.Length && counter != null)
                {
                    bool random = cell[cCond].Trim().ToLowerInvariant().StartsWith("r");
                    counter.practiceSchedule = random ? TrialCounter.PracticeSchedule.Random
                                                      : TrialCounter.PracticeSchedule.Blocked;
                    applied.Add($"condition={counter.practiceSchedule}");
                }
                if (cOrder >= 0 && cOrder < cell.Length && runner != null)
                {
                    string o = cell[cOrder].Trim();
                    if (o.Length > 0 && o != "-") { runner.blockedTaskOrder = o; applied.Add($"taskOrder={o}"); }
                }
                if (cSeed >= 0 && cSeed < cell.Length && runner != null &&
                    int.TryParse(cell[cSeed].Trim(), out int sd))
                {
                    runner.randomSeed = sd; applied.Add($"seed={sd}");
                }

                summary = $"ALLOCATION {controls.ParticipantId()}: " + string.Join("  ", applied);
                Debug.Log($"SessionConfig: {controls.ParticipantId()} found in {allocationFileName}  ->  " +
                          string.Join("  ", applied));
                return;
            }

            // Not finding the participant is nearly always a typo in the ID, and running a
            // session under an ID that is not in the allocation plan is worse than stopping
            // to check - so this is an error, not a quiet note.
            Debug.LogError($"SessionConfig: participant '{controls.ParticipantId()}' is NOT in " +
                           $"{allocationFileName}. Check the participant number before running " +
                           "this session - the condition and task order have not been set from " +
                           "the allocation plan.");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"SessionConfig: could not read {allocationFileName} - {e.Message}");
        }
    }

    // A real CSV splitter, not line.Split(',').
    //
    // The taskOrder cell contains commas ("A,B,C"), so Excel writes it quoted. Splitting
    // naively on every comma would turn one row into three extra cells and silently shift
    // every column after it - which would read the seed column as a task order and assign
    // the wrong condition without any error at all. Quoted fields must be honoured.
    private static string[] SplitCsv(string line)
    {
        var cells = new System.Collections.Generic.List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                // A doubled quote inside a quoted field is a literal quote character.
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                cells.Add(cur.ToString());
                cur.Clear();
            }
            else cur.Append(c);
        }
        cells.Add(cur.ToString());
        return cells.ToArray();
    }

    private static int IndexOf(string[] header, string name)
    {
        for (int i = 0; i < header.Length; i++)
            if (header[i].Trim().ToLowerInvariant().Replace(" ", "") == name) return i;
        return -1;
    }

    private void WriteTemplate(string folder)
    {
        try
        {
            string path = Path.Combine(folder, "session_config_TEMPLATE.txt");
            if (File.Exists(path)) return;
            File.WriteAllText(path,
"# NPRlab session configuration.\r\n" +
"#\r\n" +
"# Rename this file to session_config.txt and upload it to the app's folder on the\r\n" +
"# headset (SideQuest > Files > Android/data/com.NPRLab.ARProprioception/files).\r\n" +
"# Lines starting with # are ignored. Anything left out keeps its Inspector value.\r\n" +
"\r\n" +
"# WITH allocation.csv UPLOADED, THESE TWO LINES ARE ALL YOU CHANGE PER SESSION.\r\n" +
"participant = P01\r\n" +
"visit       = 1\r\n" +
"\r\n" +
"# Blocked or Random.\r\n" +
"condition   = Blocked\r\n" +
"\r\n" +
"# Blocked only: the order the three tasks are practised in. COUNTERBALANCE THIS\r\n" +
"# ACROSS PARTICIPANTS - e.g. P01 A,B,C   P02 B,C,A   P03 C,A,B\r\n" +
"taskOrder   = A,B,C\r\n" +
"\r\n" +
"# Random only: any non-zero number makes that participant's order reproducible.\r\n" +
"# Use a different number per participant and write it in your log.\r\n" +
"seed        = 0\r\n" +
"\r\n" +
"familiarizationTrials = 6\r\n" +
"trialsPerTask         = 18\r\n" +
"\r\n" +
"# Target sequences, as grid positions 1-9 (1 = top-left, 9 = bottom-right).\r\n" +
"taskA = 2,7,6\r\n" +
"taskB = 2,9,4\r\n" +
"taskC = 4,3,8\r\n" +
"\r\n" +
"# Visit 2 only. Novel transfer sequences and trial counts.\r\n" +
"transfer3 = 6,1,8\r\n" +
"transfer5 = 1,3,7,9,5\r\n" +
"retentionTrialsPerTask = 6\r\n" +
"transferTrials        = 9\r\n" +
"retentionOrder        = Random\r\n" +
"\r\n" +
"# Grid geometry in metres. Changing these mid-study breaks comparability.\r\n" +
"gridWidth    = 0.50\r\n" +
"gridHeight   = 0.40\r\n" +
"gridDistance = 0.45\r\n");
            Debug.Log($"SessionConfig: wrote a template at {path}. Copy it, rename it to " +
                      $"{fileName}, and upload it to set up the next participant.");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"SessionConfig: could not write the template - {e.Message}");
        }
    }
}
