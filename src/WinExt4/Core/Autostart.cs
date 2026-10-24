using System.Diagnostics;
using System.IO;
using System.Security;

namespace WinExt4.Core;

/// <summary>
/// Starts WinExt4 at logon through a Task Scheduler task with highest
/// privileges: raw disk access needs an administrator token, and a task can
/// provide one without a UAC prompt every time.
/// </summary>
public static class Autostart
{
    private const string TaskName = "WinExt4";

    public static bool IsEnabled => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    public static void Enable()
    {
        string exe = ShellIntegration.Exe;
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        // XML instead of /SC ONLOGON flags: the defaults would kill the task
        // after 72 hours and refuse to start it on battery.
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>WinExt4: apre i dischi ext2/3/4 appena collegati</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowHardTerminate>true</AllowHardTerminate>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>--tray</Arguments></Exec></Actions>
            </Task>
            """;
        string tmp = Path.Combine(Path.GetTempPath(), "winext4-task.xml");
        File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
        try
        {
            if (RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F") != 0)
                throw new InvalidOperationException("Impossibile creare l'attività pianificata di avvio automatico.");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    public static void Disable() => RunSchtasks($"/Delete /TN \"{TaskName}\" /F");

    private static int RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
