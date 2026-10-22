using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DS80Bridge.Core;

/// <summary>
/// 定位并加载 Xvisio XVSDK 的 Windows 运行库。
/// 官方安装包（XVSDK-3.2.0-msvc2019-x64.exe）默认安装到 C:\Program Files\xvsdk\bin。
/// 搜索顺序：环境变量 XVSDK_BIN → 程序目录\sdk → Program Files\xvsdk\bin。
/// </summary>
public static class SdkEnvironment
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string? lpPathName);

    public static string? BinDirectory { get; private set; }
    public static string? WrapperPath { get; private set; }
    public static bool IsLoaded { get; private set; }
    public static string LastError { get; private set; } = "";

    public static string? LocateBinDirectory()
    {
        var candidates = new List<string>();

        string? env = Environment.GetEnvironmentVariable("XVSDK_BIN");
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "sdk"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "sdk", "bin"));
        candidates.Add(@"C:\Program Files\xvsdk\bin");
        candidates.Add(@"C:\Program Files (x86)\xvsdk\bin");

        foreach (string dir in candidates)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string sdk = Path.Combine(dir, "xvsdk.dll");
            if (File.Exists(sdk)) return dir;
        }

        return null;
    }

    /// <summary>把 SDK bin 目录加入 DLL 搜索路径并加载封装库。</summary>
    public static bool EnsureLoaded(out string message)
    {
        if (IsLoaded)
        {
            message = $"SDK 已加载: {BinDirectory}";
            return true;
        }

        string? dir = LocateBinDirectory();
        if (dir == null)
        {
            message = "未找到 xvsdk.dll" +
                      "。请安装 Xvisio XVSDK，或设置环境变量 XVSDK_BIN 指向 SDK 的 bin 目录。";
            LastError = message;
            return false;
        }

        try
        {
            if (!SetDllDirectory(dir))
            {
                // 非致命：某些环境下会被策略禁止，继续尝试直接加载。
            }

            string wrapper = Path.Combine(AppContext.BaseDirectory, XvisioNative.DllName);
            if (!File.Exists(wrapper))
                throw new FileNotFoundException("缺少 DS80NativeBridge.dll，请运行 native/build.cmd 后重新构建。", wrapper);
            NativeLibrary.Load(wrapper);
            if (XvisioNative.ds80_abi_version() != 1 || XvisioNative.ds80_pose_size() != Marshal.SizeOf<SdkPose>())
                throw new InvalidOperationException("原生 SDK 桥接 ABI 不匹配，请重新构建发布包。");

            BinDirectory = dir;
            WrapperPath = wrapper;
            IsLoaded = true;
            message = $"SDK 已加载: {dir}";
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException
                                       or EntryPointNotFoundException or IOException or InvalidOperationException)
        {
            message = $"加载 {XvisioNative.DllName} 失败: {ex.Message}";
            LastError = message;
            return false;
        }
    }
}
