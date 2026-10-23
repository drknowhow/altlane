using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace Altlane;

/// <summary>Start at sign-in through the per-user Run key. No admin rights needed.</summary>
static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "Altlane";

    static string Command => $"\"{Environment.ProcessPath}\" --autostart";

    public static bool IsOn
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(Name) is string;
        }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        if (on) k.SetValue(Name, Command);
        else k.DeleteValue(Name, throwOnMissingValue: false);
        Log.Write("autostart", new { on });
    }
}

/// <summary>
/// Admin mode: a scheduled task that starts Altlane elevated at sign-in. Windows only lets a hook see
/// keys typed into an admin window when the hook's own process is elevated too. Creating or deleting
/// the task needs elevation (one UAC prompt); starting it later does not.
/// </summary>
static class AdminTask
{
    public const string TaskName = "Altlane admin";

    public static bool SelfElevated => Win.IsElevated((uint)Environment.ProcessId) == true;

    static (int code, string output) Schtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (p.ExitCode, o.Trim());
    }

    public static bool Exists() => Schtasks($"/Query /TN \"{TaskName}\"").code == 0;

    /// <summary>Start the task now (no prompt). False when it doesn't exist or wouldn't start.</summary>
    public static bool Run() => Schtasks($"/Run /TN \"{TaskName}\"").code == 0;

    /// <summary>Task XML. <paramref name="highest"/> false is only for the self-check, which can't elevate.</summary>
    static string Xml(string user, string exe, string args, bool highest) => $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Starts Altlane as administrator when you sign in, so its Alt+Tab also works while an admin window is in front.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{SecurityElement.Escape(user)}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{SecurityElement.Escape(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>{(highest ? "HighestAvailable" : "LeastPrivilege")}</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{SecurityElement.Escape(exe)}</Command>
      <Arguments>{SecurityElement.Escape(args)}</Arguments>
    </Exec>
  </Actions>
</Task>
""";

    static int Register(string name, string xml)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"altlane-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
        try
        {
            var (code, output) = Schtasks($"/Create /TN \"{name}\" /XML \"{tmp}\" /F");
            Log.Write("admin_task_create", new { name, code, output });
            return code;
        }
        finally { File.Delete(tmp); }
    }

    /// <summary>
    /// The --admin-task entry point. install and remove must run elevated (the caller uses the runas
    /// verb); status and selfcheck don't. Exit code 0 means done (status: the task exists).
    /// </summary>
    public static int Command(string action, string? user)
    {
        switch (action)
        {
            case "install":
                if (!SelfElevated) return 5;
                return Register(TaskName, Xml(user ?? WindowsIdentity.GetCurrent().Name, Environment.ProcessPath!, "--from-task", highest: true));
            case "remove":
                if (!Exists()) return 0;
                var (code, output) = Schtasks($"/Delete /TN \"{TaskName}\" /F");
                Log.Write("admin_task_delete", new { code, output });
                return code;
            case "status":
                return Exists() ? 0 : 1;
            case "selfcheck":
                return SelfCheck();
            default:
                return 2;
        }
    }

    /// <summary>
    /// Proves the task XML and the create/query/run/delete calls work, without a UAC prompt: registers the
    /// same task at normal rights under another name, with a harmless action, then deletes it.
    /// </summary>
    static int SelfCheck()
    {
        const string name = "Altlane admin (self-check)";
        string user = WindowsIdentity.GetCurrent().Name;
        int created = Register(name, Xml(user, Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit 0", highest: false));
        bool exists = Schtasks($"/Query /TN \"{name}\"").code == 0;
        bool ran = Schtasks($"/Run /TN \"{name}\"").code == 0;
        int deleted = Schtasks($"/Delete /TN \"{name}\" /F").code;
        bool gone = Schtasks($"/Query /TN \"{name}\"").code != 0;
        Log.Write("admin_task_selfcheck", new { created, exists, ran, deleted, gone });
        return created == 0 && exists && ran && deleted == 0 && gone ? 0 : 1;
    }

    /// <summary>Run this exe elevated with arguments; the UAC prompt is the user's to answer. Returns the exit code, or null if they declined.</summary>
    public static int? Elevated(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" })!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return null; } // ERROR_CANCELLED: the user said no
    }
}
