using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using NetworkMonitor.Core.Service;

namespace NetworkMonitor.Service;

/// <summary>Thin wrapper over sc.exe, used by `--install`, `--uninstall` and `--status`.</summary>
internal static class ServiceInstaller
{
    public static int Install()
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine("安装后台服务需要管理员权限：请右键以管理员身份运行，或从程序的「设置 → 后台服务」点击安装（会弹出 UAC）。");
            return 5;
        }

        string exe = Environment.ProcessPath
                     ?? Path.Combine(AppContext.BaseDirectory, "NetworkMonitor.Service.exe");

        int create = RunSc($"create {ServiceConfig.ServiceName} binPath= \"{exe}\" start= auto DisplayName= \"{ServiceConfig.DisplayName}\"");
        if (create != 0)
        {
            Console.WriteLine("服务已存在，更新其配置…");
            RunSc($"config {ServiceConfig.ServiceName} binPath= \"{exe}\" start= auto displayname= \"{ServiceConfig.DisplayName}\"");
        }

        RunSc($"description {ServiceConfig.ServiceName} \"NetworkMonitor 网络流量采样与统计后台服务（仅监听 127.0.0.1）\"");
        RunSc($"failure {ServiceConfig.ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000");
        int start = RunSc($"start {ServiceConfig.ServiceName}");

        // 1056 = ERROR_SERVICE_ALREADY_RUNNING, which is a success for this verb.
        if (start is 0 or 1056)
        {
            Console.WriteLine("后台服务已安装并启动。");
            return 0;
        }

        Console.Error.WriteLine($"服务启动失败（sc.exe 退出码 {start}）。可用 --status 查看状态。");
        return start;
    }

    public static int Uninstall()
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine("卸载后台服务需要管理员权限。");
            return 5;
        }

        RunSc($"stop {ServiceConfig.ServiceName}");
        int delete = RunSc($"delete {ServiceConfig.ServiceName}");

        // 1060 = ERROR_SERVICE_DOES_NOT_EXIST
        if (delete is 0 or 1060)
        {
            Console.WriteLine("后台服务已卸载。");
            return 0;
        }

        Console.Error.WriteLine($"卸载失败（sc.exe 退出码 {delete}）。");
        return delete;
    }

    public static int Status()
    {
        int query = RunSc($"query {ServiceConfig.ServiceName}");
        if (query != 0)
        {
            Console.WriteLine("后台服务未安装。");
            return 1;
        }

        return 0;
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static int RunSc(string arguments)
    {
        try
        {
            var info = new ProcessStartInfo("sc.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(info);
            if (process is null) return -1;

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrWhiteSpace(output)) Console.WriteLine(output.TrimEnd());
            if (!string.IsNullOrWhiteSpace(error)) Console.Error.WriteLine(error.TrimEnd());
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("调用 sc.exe 失败: " + ex.Message);
            return -1;
        }
    }
}
