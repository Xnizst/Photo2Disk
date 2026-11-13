namespace Photo2Disk;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show("發生未預期的錯誤：" + Environment.NewLine + e.Exception.Message,
                "照片備份過濾工具", MessageBoxButtons.OK, MessageBoxIcon.Error);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
