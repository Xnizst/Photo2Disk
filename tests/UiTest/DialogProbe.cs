using System.Runtime.InteropServices;

namespace Photo2Disk.UiTest;

/// <summary>
/// 實際打開「加入…」視窗，從背景執行緒送出「開啟」（等同使用者直接按開啟），
/// 記錄視窗回傳的路徑與程式判斷結果。
/// </summary>
internal static class DialogProbe
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? cls, string title);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WmCommand = 0x0111;
    private const int IdOk = 1;

    public static List<string> Run(string folder, List<string> log)
    {
        using var dlg = MainForm.CreateAddDialog(folder);
        var presser = new Thread(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                Thread.Sleep(100);
                var h = FindWindow(null, MainForm.AddDialogTitle);
                if (h != IntPtr.Zero)
                {
                    Thread.Sleep(800);
                    SendMessage(h, WmCommand, (IntPtr)IdOk, IntPtr.Zero);
                    return;
                }
            }
        }) { IsBackground = true };
        presser.Start();
        var result = dlg.ShowDialog();
        log.Add($"視窗結果={result} FileNames=[{string.Join(" | ", dlg.FileNames)}]");
        var resolved = result == DialogResult.OK ? MainForm.ResolveDialogSelection(dlg.FileNames) : new List<string>();
        log.Add($"判斷結果=[{string.Join(" | ", resolved)}]");
        return resolved;
    }
}
