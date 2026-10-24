using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DynamicIsland.Core.Models;
using DynamicIsland.Core.Services;

internal static class LauncherTests
{
    public static int Run()
    {
        int result = 1;
        // Shell 快捷方式操作和桌面 UI 一样在 STA 线程中执行。
        var thread = new Thread(() => result = RunOnStaThread());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static int RunOnStaThread()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DynamicIsland-LauncherTest-" + Guid.NewGuid().ToString("N")));
        string? previousDataDir = Environment.GetEnvironmentVariable("DYNAMICISLAND_DATA_DIR");
        string? previousProbeDir = Environment.GetEnvironmentVariable("DYNAMICISLAND_TEST_PROBE_DIR");
        int passed = 0;
        int failed = 0;

        void Check(string name, Action action)
        {
            try
            {
                action();
                passed++;
                Console.WriteLine($"[OK] {name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"[FAIL] {name}: {ex.Message}");
            }
        }

        try
        {
            Environment.SetEnvironmentVariable("DYNAMICISLAND_DATA_DIR", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("DYNAMICISLAND_TEST_PROBE_DIR", root);
            LogService.Init();
            string workDir = Directory.CreateDirectory(Path.Combine(root, "中文 工作目录")).FullName;
            string otherDir = Directory.CreateDirectory(Path.Combine(root, "另一个目录")).FullName;
            string host = Path.ChangeExtension(typeof(LauncherTests).Assembly.Location, ".exe");
            Require(File.Exists(host), "未找到自检程序 apphost，请先构建 SelfTest 项目。");
            string shortcut = Path.Combine(root, "中文 快捷方式.lnk");
            var launcher = new LauncherService();

            void LaunchAndVerify(LauncherItem item, string output, string expectedDir, params string[] expectedArgs)
            {
                Require(launcher.Launch(item, out string? error), error ?? "启动返回失败");
                var timer = Stopwatch.StartNew();
                while (!File.Exists(output) && timer.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Thread.Sleep(50);
                }

                Require(File.Exists(output), "已提交启动请求，但子进程未写入验证结果。");
                using var document = JsonDocument.Parse(File.ReadAllText(output));
                string? actualDir = document.RootElement.GetProperty("WorkingDirectory").GetString();
                Require(string.Equals(actualDir, expectedDir, StringComparison.OrdinalIgnoreCase),
                    $"工作目录不符：{actualDir}，预期 {expectedDir}");
                string?[] actualArgs = document.RootElement.GetProperty("Arguments").EnumerateArray().Select(x => x.GetString()).ToArray();
                Require(actualArgs.SequenceEqual(expectedArgs), "子进程参数丢失或被重复传入。");
            }

            Check("原快捷方式优先于过期目标；保留中文参数与起始位置", () =>
            {
                string output = Path.Combine(root, "original.json");
                SaveShortcut(shortcut, host, ProbeArguments(output, "中文 参数"), workDir);
                var item = launcher.CreateItem(shortcut) ?? throw new Exception("快捷方式未导入");
                item.TargetPath = Path.Combine(root, "过期目标.exe");
                item.Arguments = "--不应重复追加";
                item.WorkingDirectory = otherDir;
                LaunchAndVerify(item, output, workDir, "中文 参数");
            });

            Check("导入后修改快捷方式，点击采用最新参数和工作目录", () =>
            {
                var item = launcher.CreateItem(shortcut) ?? throw new Exception("快捷方式未导入");
                string output = Path.Combine(root, "updated.json");
                SaveShortcut(shortcut, host, ProbeArguments(output, "修改后的参数"), otherDir);
                LaunchAndVerify(item, output, otherDir, "修改后的参数");
            });

            Check("原快捷方式丢失，旧 JSON 配置可回退到目标程序", () =>
            {
                string output = Path.Combine(root, "fallback.json");
                SaveShortcut(shortcut, host, ProbeArguments(output, "回退 参数"), workDir);
                var item = launcher.CreateItem(shortcut) ?? throw new Exception("快捷方式未导入");
                string json = JsonSerializer.Serialize(item);
                item = JsonSerializer.Deserialize<LauncherItem>(json) ?? throw new Exception("配置未还原");
                File.Delete(shortcut);
                LaunchAndVerify(item, output, workDir, "回退 参数");
            });

            Check("直接启动 exe；无效工作目录回退为程序所在目录", () =>
            {
                string output = Path.Combine(root, "direct.json");
                var item = launcher.CreateItem(host) ?? throw new Exception("exe 未导入");
                item.Arguments = ProbeArguments(output, "直接 启动");
                item.WorkingDirectory = Path.Combine(root, "不存在的目录");
                LaunchAndVerify(item, output, Path.GetDirectoryName(host)!, "直接 启动");
            });

            Check("环境变量路径按当前机器解析", () =>
            {
                string output = Path.Combine(root, "environment.json");
                var item = new LauncherItem
                {
                    Name = "环境变量工作目录",
                    TargetPath = host,
                    Arguments = ProbeArguments(output, "环境 变量"),
                    WorkingDirectory = "%DYNAMICISLAND_TEST_PROBE_DIR%"
                };
                LaunchAndVerify(item, output, root, "环境 变量");
            });

            Check("原快捷方式存在时，旧配置的空目标不阻止启动", () =>
            {
                string output = Path.Combine(root, "empty-target.json");
                SaveShortcut(shortcut, host, ProbeArguments(output, "空目标"), workDir);
                LaunchAndVerify(new LauncherItem { Name = "空目标", SourcePath = shortcut }, output, workDir, "空目标");
            });

            Check("目标和原快捷方式均丢失时返回明确错误", () =>
            {
                var item = new LauncherItem
                {
                    Name = "已删除程序",
                    SourcePath = Path.Combine(root, "missing.lnk"),
                    TargetPath = Path.Combine(root, "missing.exe")
                };
                Require(!launcher.Launch(item, out string? error), "缺失目标不应报告启动成功");
                Require(error?.Contains("找不到启动目标") == true, "没有返回缺失目标原因");
                Require(!launcher.Launch(new LauncherItem(), out error) && !string.IsNullOrWhiteSpace(error), "空目标没有错误说明");
                Require(launcher.CreateItem(item.TargetPath) is null, "不存在的文件不应被导入");
            });

            Check("快捷方式未填起始位置时，保存程序目录供回退", () =>
            {
                SaveShortcut(shortcut, host, "", "");
                var item = launcher.CreateItem(shortcut) ?? throw new Exception("快捷方式未导入");
                Require(string.Equals(item.WorkingDirectory, Path.GetDirectoryName(host), StringComparison.OrdinalIgnoreCase),
                    "回退工作目录仍是快捷方式目录");
            });

            Console.WriteLine($"启动器专项测试：通过 {passed} 项，失败 {failed} 项。");
            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] 测试初始化：{ex}");
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("DYNAMICISLAND_DATA_DIR", previousDataDir);
            Environment.SetEnvironmentVariable("DYNAMICISLAND_TEST_PROBE_DIR", previousProbeDir);
            try
            {
                // 仅清理由本次测试创建、位于系统临时目录下的唯一目录。
                string tempPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (root.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"临时测试文件待清理：{root}（{ex.Message}）");
            }
        }
    }

    private static string ProbeArguments(string output, string argument) => $"--launcher-probe \"{output}\" \"{argument}\"";

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void SaveShortcut(string path, string target, string arguments, string workDir)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!);
            shortcut = ((dynamic)shell!).CreateShortcut(path);
            dynamic link = shortcut;
            link.TargetPath = target;
            link.Arguments = arguments;
            link.WorkingDirectory = workDir;
            link.Save();
        }
        finally
        {
            if (shortcut is not null) Marshal.ReleaseComObject(shortcut);
            if (shell is not null) Marshal.ReleaseComObject(shell);
        }
    }
}
