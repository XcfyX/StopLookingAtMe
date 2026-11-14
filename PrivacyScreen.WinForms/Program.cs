using System.Windows.Forms;

namespace PrivacyScreen;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Fatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write("Unhandled domain error: " + e.ExceptionObject);
        try
        {
            using var mutex = new Mutex(true, @"Local\PrivacyScreen.OneInstance", out bool first);
            if (!first)
            {
                MessageBox.Show("程序已运行，请查看系统托盘。", "PrivacyScreen");
                return;
            }
            Application.Run(new PrivacyContext());
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Fatal(ex); }
    }

    private static void Fatal(Exception ex)
    {
        Log.Write("Fatal: " + ex);
        // 绝不在异常路径显示模态对话框：如果屏保钩子还在，实体键盘可能无法关闭弹窗。
        // 直接结束进程，Windows 会撤销进程所注册的低层钩子并关闭窗口。
        Environment.Exit(1);
    }
}
