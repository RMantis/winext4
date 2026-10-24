using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WinExt4.Core;

public enum FsckMode
{
    /// <summary>-n: read only, answers "no" to everything.</summary>
    Check,
    /// <summary>-y into a draft: the source is not touched.</summary>
    RepairInDraft,
    /// <summary>-p: only the fixes e2fsck considers safe.</summary>
    Preen,
    /// <summary>-y: answers "yes" to everything, on the source.</summary>
    RepairAll,
}

public sealed record FsckResult(int ExitCode, string Output, FsckMode Mode, string? DraftPath, bool Cancelled)
{
    /// <summary>Errors were found and left as they are (-n, or -p that gave up).</summary>
    public bool Uncorrected => (ExitCode & 4) != 0;
    public bool Corrected => (ExitCode & 3) != 0;
    public bool Failed => (ExitCode & ~7) != 0 || Cancelled;
    public bool Clean => ExitCode == 0 && !Cancelled;

    public string Summary =>
        Cancelled ? "Controllo interrotto." :
        (ExitCode & 8) != 0 ? "Il controllo non è riuscito (errore operativo): vedi i messaggi." :
        (ExitCode & 16) != 0 ? "Opzioni non valide." :
        (ExitCode & 32) != 0 ? "Controllo annullato." :
        Uncorrected && Mode == FsckMode.Check ? "Sono stati trovati errori. Nessuna modifica è stata fatta." :
        Uncorrected && Mode == FsckMode.Preen ? "Ci sono errori che la riparazione automatica non corregge da sola: usa «Ripara in una bozza» o «Ripara tutto»." :
        Uncorrected ? "Alcuni errori non sono stati corretti." :
        Corrected && Mode == FsckMode.RepairInDraft ? "Errori corretti nella bozza: la sorgente non è stata modificata. Rivedi le modifiche e applicale." :
        Corrected ? "Errori corretti." :
        "Nessun errore trovato.";
}

/// <summary>
/// Runs e4fsck.exe (e2fsck on WinExt4's I/O layer) as a child process:
/// e2fsck is a program with global state that exits on fatal errors.
/// </summary>
public static class Fsck
{
    public static string Exe => Path.Combine(AppContext.BaseDirectory, "e4fsck.exe");
    public static bool Available => File.Exists(Exe);

    /// <summary>Archive members are read through this process, so the child cannot reach them.</summary>
    public static bool Supported(Ext4Source s) => !s.IsInArchive;

    /// <summary>Conditions for which a check is worth suggesting (none = empty).</summary>
    public static List<string> Reasons(Ext4VolumeInfo info, SuperInfo? sb)
    {
        var r = new List<string>();
        if (info.HasErrors) r.Add("il filesystem è segnato con errori");
        else if (!info.Clean) r.Add("non è stato smontato correttamente");
        if (sb is not null)
        {
            if (sb.ErrorCount > 0 && !info.HasErrors) r.Add($"il kernel ha registrato {sb.ErrorCount} errori");
            if (sb.MaxMountCount > 0 && sb.MountCount >= sb.MaxMountCount)
                r.Add($"è stato montato {sb.MountCount} volte (controllo previsto ogni {sb.MaxMountCount})");
            if (sb.CheckIntervalSeconds > 0 && sb.LastCheck is { } last &&
                last.AddSeconds(sb.CheckIntervalSeconds) < DateTime.Now)
                r.Add($"l'ultimo controllo risale al {last:d}");
        }
        return r;
    }

    private static readonly Regex ProgressLine = new(@"^([1-5]) (\d+) (\d+) ", RegexOptions.Compiled);

    // e2fsck's own weights for the five passes (unix.c), in percent
    private static readonly double[] PassStart = { 0, 0, 70, 90, 92, 95 };
    private static readonly double[] PassWeight = { 0, 70, 20, 2, 3, 5 };

    /// <summary>
    /// Runs the check; <paramref name="onLine"/> gets e2fsck's messages,
    /// <paramref name="onProgress"/> the overall fraction (0..1) and the pass.
    /// Cancelling kills the process: safe for Check and RepairInDraft only.
    /// </summary>
    public static async Task<FsckResult> RunAsync(Ext4Source s, FsckMode mode, bool force, string? draftPath,
        Action<string> onLine, Action<double, int> onProgress, CancellationToken cancel)
    {
        var psi = new ProcessStartInfo(Exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(mode switch { FsckMode.Check => "-n", FsckMode.Preen => "-p", _ => "-y" });
        if (force) psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add("2");
        if (s.Offset > 0 || s.Length > 0)
        {
            psi.ArgumentList.Add("--offset");
            psi.ArgumentList.Add(s.Offset.ToString());
            psi.ArgumentList.Add("--length");
            psi.ArgumentList.Add(s.Length.ToString());
        }
        if (draftPath is not null)
        {
            psi.ArgumentList.Add("--draft");
            psi.ArgumentList.Add(draftPath);
            psi.ArgumentList.Add("--desc");
            psi.ArgumentList.Add(Drafts.Describe(s));
        }
        psi.ArgumentList.Add(s.Device);

        var output = new StringBuilder();
        void Line(string? l)
        {
            if (l is null) return;
            // non-interactive e2fsck wraps some messages in \001..\002
            l = l.Replace("\u0001", "").Replace("\u0002", "");
            lock (output) output.AppendLine(l);
            onLine(l);
        }

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var m = ProgressLine.Match(e.Data);
            if (!m.Success)
            {
                Line(e.Data);
                return;
            }
            int pass = int.Parse(m.Groups[1].Value);
            double cur = double.Parse(m.Groups[2].Value), max = double.Parse(m.Groups[3].Value);
            double f = (PassStart[pass] + PassWeight[pass] * (max > 0 ? Math.Min(1, cur / max) : 0)) / 100;
            onProgress(f, pass);
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        bool cancelled = false;
        try
        {
            await p.WaitForExitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            try { p.Kill(); } catch (InvalidOperationException) { }
            await p.WaitForExitAsync();
        }
        p.WaitForExit(); // flushes the redirected streams
        string text;
        lock (output) text = output.ToString();
        return new FsckResult(cancelled ? 32 : p.ExitCode, text, mode, draftPath, cancelled);
    }
}
