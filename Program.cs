using System;
using System.Windows.Forms;

namespace FapmDriver
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // 启动时加载 MainForm 界面
            Application.Run(new MainForm());
        }
    }
}