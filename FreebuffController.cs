//  Freebuff Controller  - native single-file build (csc.exe, .NET Framework).
// Dark-themed WinForms UI. Each slot (1-9) is an independent Freebuff
// instance: its own Chromium profile (--user-data-dir) and its own
// orchestrator state file (FREEBUFF_DESKTOP_STATE_PATH), so every window can
// stay logged in to a different account.
//
// Rebuild: run build.bat in this folder.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO.Compression;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

[assembly: System.Reflection.AssemblyVersion("1.9.7.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.9.7.0")]

namespace FreebuffController
{
    internal static class Program
    {
        private const string SingleInstanceName = "FreebuffMultiOpenController";

        private const string ShowEventName = "FreebuffMultiOpenController.Show";

        private const string AckEventName = "FreebuffMultiOpenController.Ack";

        internal const int AsfwAny = -1;

        internal const int SW_RESTORE = 9;

        private const long FailLogMaxBytes = 1048576L;

        internal static Mutex SingleMutex;

        internal static EventWaitHandle ShowSignal;

        internal static EventWaitHandle AckSignal;

        internal static readonly string FailLogPath = Path.Combine(Path.GetTempPath(), "freebuff-controller-log.txt");

        internal static bool FailLogMuted;

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

        [DllImport("user32.dll")]
        internal static extern void SwitchToThisWindow(IntPtr hwnd, bool fUnknown);

        [DllImport("user32.dll")]
        internal static extern bool AllowSetForegroundWindow(int dwProcessId);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        internal static extern bool BringWindowToTop(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int max);

        internal static string DescribeForegroundWindow()
        {
            try
            {
                IntPtr foregroundWindow = GetForegroundWindow();
                if (foregroundWindow == IntPtr.Zero)
                {
                    return "( нет  переднее окно )";
                }
                StringBuilder stringBuilder = new StringBuilder(256);
                GetWindowTextW(foregroundWindow, stringBuilder, stringBuilder.Capacity);
                StringBuilder stringBuilder2 = new StringBuilder(256);
                GetClassNameW(foregroundWindow, stringBuilder2, stringBuilder2.Capacity);
                uint windowThreadProcessId = GetWindowThreadProcessId(foregroundWindow, IntPtr.Zero);
                return string.Concat("hwnd=", foregroundWindow, " class=", stringBuilder2, " title=", stringBuilder, " tid=", windowThreadProcessId);
            }
            catch (Exception ex)
            {
                return "( не могу прочитать :" + ex.Message + ")";
            }
        }

        internal static bool ForceForeground(IntPtr hwnd)
        {
            IntPtr foregroundWindow = GetForegroundWindow();
            uint num = ((!(foregroundWindow == IntPtr.Zero)) ? GetWindowThreadProcessId(foregroundWindow, IntPtr.Zero) : 0u);
            uint currentThreadId = GetCurrentThreadId();
            bool flag = false;
            try
            {
                if (num != 0 && num != currentThreadId)
                {
                    flag = AttachThreadInput(currentThreadId, num, true);
                }
                ShowWindow(hwnd, 9);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                return GetForegroundWindow() == hwnd;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (flag)
                {
                    try
                    {
                        AttachThreadInput(currentThreadId, num, false);
                    }
                    catch
                    {
                    }
                }
            }
        }

        internal static void LogFail(string what)
        {
            LogFail(what, null);
        }

        internal static void LogFail(string what, Exception ex)
        {
            if (FailLogMuted)
            {
                return;
            }
            try
            {
                FileInfo fileInfo = new FileInfo(FailLogPath);
                if (fileInfo.Exists && fileInfo.Length > 1048576)
                {
                    fileInfo.Delete();
                }
                File.AppendAllText(FailLogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + what + ((ex == null) ? "" : ("  ← " + ex.GetType().Name + ": " + ex.Message)) + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            if (args != null && args.Length >= 1 && args[0] == "--self-test")
            {
                Environment.Exit(MainForm.RunSelfTest((args.Length >= 2) ? args[1] : null));
                return;
            }
            SetProcessDPIAware();
            bool createdNew;
            SingleMutex = new Mutex(true, "FreebuffMultiOpenController", out createdNew);
            if (!createdNew)
            {
                if (!TryRequestShow())
                {
                    ShowAlreadyRunningDialog();
                }
                return;
            }
            ShowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "FreebuffMultiOpenController.Show");
            AckSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "FreebuffMultiOpenController.Ack");
            ShowSignal.Reset();
            AckSignal.Reset();
            try
            {
                MainForm.EnsureChineseReply();
            }
            catch
            {
            }
            try
            {
                MainForm.EnsureAgentsMdEnabled();
            }
            catch
            {
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "freebuff-controller-error.log"), string.Concat(DateTime.Now, "  ", e.Exception, Environment.NewLine));
                }
                catch
                {
                }
                MessageBox.Show(" Ошибка контроллера:  " + e.Exception.Message, " Ошибка ", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            };
            try
            {
                MainForm mainForm = new MainForm();
                StartShowListener(mainForm);
                Application.Run(mainForm);
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "freebuff-controller-error.log"), string.Concat(DateTime.Now, "  ", ex, Environment.NewLine));
                }
                catch
                {
                }
                MessageBox.Show(" Ошибка контроллера:  " + ex.Message, " Ошибка ", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
            try
            {
                SingleMutex.ReleaseMutex();
            }
            catch
            {
            }
        }

        private static bool TryRequestShow()
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    using (EventWaitHandle eventWaitHandle = EventWaitHandle.OpenExisting("FreebuffMultiOpenController.Ack"))
                    {
                        using (EventWaitHandle eventWaitHandle2 = EventWaitHandle.OpenExisting("FreebuffMultiOpenController.Show"))
                        {
                            eventWaitHandle.WaitOne(0);
                            try
                            {
                                AllowSetForegroundWindow(-1);
                            }
                            catch
                            {
                            }
                            eventWaitHandle2.Set();
                            if (eventWaitHandle.WaitOne(600))
                            {
                                RaiseOtherControllerWindow();
                                return true;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                }
                Thread.Sleep(200);
            }
            return false;
        }

        private static IntPtr FindOtherControllerWindow()
        {
            int num = 0;
            try
            {
                num = Process.GetCurrentProcess().Id;
            }
            catch
            {
                return IntPtr.Zero;
            }
            try
            {
                Process[] processesByName = Process.GetProcessesByName("FreebuffController");
                foreach (Process process in processesByName)
                {
                    try
                    {
                        if (process.Id != num)
                        {
                            IntPtr mainWindowHandle = process.MainWindowHandle;
                            if (mainWindowHandle != IntPtr.Zero)
                            {
                                return mainWindowHandle;
                            }
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        try
                        {
                            process.Dispose();
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
            return IntPtr.Zero;
        }

        private static void RaiseOtherControllerWindow()
        {
            try
            {
                IntPtr intPtr = FindOtherControllerWindow();
                if (intPtr == IntPtr.Zero)
                {
                    return;
                }
                for (int i = 0; i < 12; i++)
                {
                    if (GetForegroundWindow() == intPtr)
                    {
                        return;
                    }
                    try
                    {
                        ShowWindow(intPtr, 9);
                    }
                    catch
                    {
                    }
                    try
                    {
                        SetForegroundWindow(intPtr);
                    }
                    catch
                    {
                    }
                    if (GetForegroundWindow() == intPtr || ForceForeground(intPtr))
                    {
                        return;
                    }
                    Thread.Sleep(70);
                }
                LogFail(" Запуск : окно  восстановлено  но  не смог  передний план ( передний план )   в  передний план  это  " + DescribeForegroundWindow());
            }
            catch (Exception ex)
            {
                LogFail(" Второй запуск: не могу показать окно ", ex);
            }
        }

        private static void StartShowListener(MainForm form)
        {
            Thread thread = new Thread((ThreadStart)delegate
            {
                while (true)
                {
                    try
                    {
                        if (!ShowSignal.WaitOne())
                        {
                            break;
                        }
                    }
                    catch (Exception)
                    {
                        break;
                    }
                    try
                    {
                        form.ShowFromSecondLaunch();
                    }
                    catch (Exception)
                    {
                    }
                }
            });
            thread.IsBackground = true;
            thread.Name = "show-listener";
            thread.Start();
        }

        private static void ShowAlreadyRunningDialog()
        {
            MessageBox.Show(" Freebuff Controller уже запущен.\n\n " + DescribeRunningController() + "\n\n окно  может  свернуто , или  скрыто  в  окно  после -- двойной клик  панель задач  /  трей  в  контроллер  может . \n если  все  поиск  не  шт.  окно ,  в  диспетчер задач  в  выше  шт.  процесс ,  двойной клик  Открыть . ", " Инфо ", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
        }

        private static string DescribeRunningController()
        {
            string text = null;
            try
            {
                text = Process.GetCurrentProcess().Id.ToString();
            }
            catch
            {
            }
            string text2 = "";
            try
            {
                Process[] processesByName = Process.GetProcessesByName("FreebuffController");
                foreach (Process process in processesByName)
                {
                    string text3;
                    try
                    {
                        text3 = process.Id.ToString();
                    }
                    catch
                    {
                        continue;
                    }
                    if (!(text3 == text))
                    {
                        string text4 = "";
                        try
                        {
                            text4 = process.MainModule.FileName;
                        }
                        catch
                        {
                        }
                        string text5 = "";
                        try
                        {
                            text5 = process.StartTime.ToString("HH:mm:ss");
                        }
                        catch
                        {
                        }
                        if (text2.Length > 0)
                        {
                            text2 += "\n";
                        }
                        string text6 = text2;
                        text2 = text6 + "· PID " + text3 + (string.IsNullOrEmpty(text4) ? "" : ("(" + text4 + ")")) + (string.IsNullOrEmpty(text5) ? "" : (",  Запуск  " + text5));
                    }
                }
            }
            catch
            {
            }
            if (text2.Length <= 0)
            {
                return "( не могу прочитать  занят  процесс )";
            }
            return " Занят процессом:\n " + text2;
        }
    }

    public class MainForm : Form
    {
        private class RoundButton : Button
        {
            public Color HoverBack = Color.Empty;

            public int Radius = 10;

            private bool hovered;

            private bool pressed;

            private Color shownColor = Color.Empty;

            private System.Windows.Forms.Timer animTimer;

            public RoundButton()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                base.FlatStyle = FlatStyle.Flat;
                base.FlatAppearance.BorderSize = 0;
            }

            private static GraphicsPath Rounded(RectangleF r, int radius)
            {
                float num = Math.Max(2f, (float)radius * 2f);
                if (num > r.Width)
                {
                    num = r.Width;
                }
                if (num > r.Height)
                {
                    num = r.Height;
                }
                GraphicsPath graphicsPath = new GraphicsPath();
                graphicsPath.AddArc(r.X, r.Y, num, num, 180f, 90f);
                graphicsPath.AddArc(r.Right - num, r.Y, num, num, 270f, 90f);
                graphicsPath.AddArc(r.Right - num, r.Bottom - num, num, num, 0f, 90f);
                graphicsPath.AddArc(r.X, r.Bottom - num, num, num, 90f, 90f);
                graphicsPath.CloseFigure();
                return graphicsPath;
            }

            private static Color Blend(Color a, Color b, float t)
            {
                return Color.FromArgb((int)Math.Round((float)(int)a.R + (float)(b.R - a.R) * t), (int)Math.Round((float)(int)a.G + (float)(b.G - a.G) * t), (int)Math.Round((float)(int)a.B + (float)(b.B - a.B) * t));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics graphics = e.Graphics;
                Color color = ((base.Parent == null) ? BackColor : base.Parent.BackColor);
                graphics.Clear(color);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                RectangleF r = new RectangleF(0f, 0f, base.Width, base.Height);
                Color color2 = ((shownColor == Color.Empty) ? TargetColor() : shownColor);
                using (GraphicsPath path = Rounded(r, Radius))
                {
                    using (SolidBrush brush = new SolidBrush(color2))
                    {
                        graphics.FillPath(brush, path);
                    }
                }
                if (Focused && ShowFocusCues)
                {
                    RectangleF r2 = new RectangleF(3.5f, 3.5f, (float)base.Width - 7f, (float)base.Height - 7f);
                    using (GraphicsPath path2 = Rounded(r2, Math.Max(2, Radius - 3)))
                    {
                        using (Pen pen = new Pen(Color.FromArgb(150, 24, 27, 33)))
                        {
                            graphics.DrawPath(pen, path2);
                        }
                    }
                }
                TextRenderer.DrawText(graphics, Text, Font, new Rectangle(0, 0, base.Width, base.Height), base.Enabled ? ForeColor : ColSub, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            // A3  подсказка  / , . 
            private Color TargetColor()
            {
                if (!base.Enabled)
                {
                    return Blend(BackColor, (base.Parent == null) ? BackColor : base.Parent.BackColor, 0.6f);
                }
                return ((hovered || pressed) && HoverBack != Color.Empty) ? HoverBack : BackColor;
            }

            // A3 : каждый  35%,  80ms ,  не . 
            private void StartAnim()
            {
                if (shownColor == Color.Empty)
                {
                    shownColor = TargetColor();
                }
                if (animTimer != null)
                {
                    return;
                }
                animTimer = new System.Windows.Forms.Timer();
                animTimer.Interval = 15;
                animTimer.Tick += delegate
                {
                    if (IsDisposed || animTimer == null)
                    {
                        return;
                    }
                    Color color = TargetColor();
                    if (shownColor.ToArgb() == color.ToArgb())
                    {
                        animTimer.Stop();
                        animTimer.Dispose();
                        animTimer = null;
                        return;
                    }
                    shownColor = Blend(shownColor, color, 0.35f);
                    if (Math.Abs((int)shownColor.R - (int)color.R) + Math.Abs((int)shownColor.G - (int)color.G) + Math.Abs((int)shownColor.B - (int)color.B) < 6)
                    {
                        shownColor = color;
                    }
                    Invalidate();
                };
                animTimer.Start();
            }

            protected override void OnEnabledChanged(EventArgs e)
            {
                base.OnEnabledChanged(e);
                StartAnim();
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                hovered = true;
                StartAnim();
                base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                hovered = false;
                pressed = false;
                StartAnim();
                base.OnMouseLeave(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                pressed = true;
                StartAnim();
                base.OnMouseDown(e);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                pressed = false;
                StartAnim();
                base.OnMouseUp(e);
            }
        }

        private class QuotaInfo
        {
            public string Text;

            public string Tip;

            public bool Exhausted;

            public bool Offline;
        }

        private class InitModeDialog : Form
        {
            private readonly RadioButton rbFresh = new RadioButton();

            private readonly RadioButton rbCopy = new RadioButton();

            private readonly ComboBox source = new ComboBox();

            private readonly List<int> sourceIndex = new List<int>();

            public int CopyFrom
            {
                get
                {
                    if (!rbCopy.Checked || source.SelectedIndex < 0)
                    {
                        return -1;
                    }
                    return sourceIndex[source.SelectedIndex];
                }
            }

            public InitModeDialog(int slot)
            {
                Text = " Запуск   Инстанс  " + slot;
                base.ClientSize = new Size(426, 246);
                BackColor = ColPanel;
                ForeColor = ColText;
                Font = new Font("Segoe UI", 9f);
                base.FormBorderStyle = FormBorderStyle.FixedDialog;
                base.MinimizeBox = false;
                base.MaximizeBox = false;
                base.ShowInTaskbar = false;
                base.StartPosition = FormStartPosition.CenterParent;
                Label value = new Label
                {
                    AutoSize = false,
                    Text = " Инстанс  " + slot + "  ещё не входил, как запустить? ",
                    Bounds = new Rectangle(16, 14, 394, 20)
                };
                base.Controls.Add(value);
                rbFresh.Text = " Новый логин ";
                rbFresh.Bounds = new Rectangle(16, 48, 180, 20);
                rbFresh.ForeColor = ColText;
                rbFresh.BackColor = ColPanel;
                rbFresh.Checked = true;
                base.Controls.Add(rbFresh);
                Label value2 = new Label
                {
                    AutoSize = false,
                    Text = " Открыть  после  в  окно  в  вход  этот  Инстанс  Аккаунт ,  каждый  шт.  окно  доступен  не  Аккаунт ",
                    Bounds = new Rectangle(38, 70, 372, 18),
                    ForeColor = ColSub,
                    Font = new Font("Segoe UI", 8.5f)
                };
                base.Controls.Add(value2);
                rbCopy.Text = " Скопировать аккаунт ";
                rbCopy.Bounds = new Rectangle(16, 100, 200, 20);
                rbCopy.ForeColor = ColText;
                rbCopy.BackColor = ColPanel;
                base.Controls.Add(rbCopy);
                source.DropDownStyle = ComboBoxStyle.DropDownList;
                source.Bounds = new Rectangle(38, 124, 300, 24);
                source.BackColor = ColNeutral;
                source.ForeColor = ColText;
                source.Font = new Font("Segoe UI", 9f);
                for (int i = 0; i <= 9; i++)
                {
                    if (i != slot && ReadTokenFor(i) != null)
                    {
                        string text = ((i == 0) ? " Главная " : (" Инстанс  " + i));
                        string text2 = AccountForState((i == 0) ? DefaultState : SlotStatePath(i));
                        if (!text2.StartsWith("("))
                        {
                            text = text + "(" + text2 + ")";
                        }
                        source.Items.Add(text);
                        sourceIndex.Add(i);
                    }
                }
                if (source.Items.Count > 0)
                {
                    source.SelectedIndex = 0;
                }
                source.Enabled = false;
                base.Controls.Add(source);
                Label label = new Label
                {
                    AutoSize = false,
                    Text = " Клонировать логин в инстанс  " + slot + ",  Открыть  после  вход . \r\n: Аккаунт  будет  общий  ежедневно  Лимит . ",
                    Bounds = new Rectangle(38, 154, 372, 34),
                    ForeColor = ColSub,
                    Font = new Font("Segoe UI", 8.5f)
                };
                base.Controls.Add(label);
                rbCopy.CheckedChanged += delegate
                {
                    source.Enabled = rbCopy.Checked;
                };
                int num = 202;
                int num2 = 246;
                if (source.Items.Count == 0)
                {
                    rbCopy.Visible = false;
                    source.Visible = false;
                    label.Visible = false;
                    num = 96;
                    num2 = 140;
                }
                base.ClientSize = new Size(426, num2);
                Button button = MakeDialogButton(" Отмена ", 198, ColNeutral, ColNeutralHover, num);
                button.DialogResult = DialogResult.Cancel;
                Button button2 = MakeDialogButton(" Запуск ", 310, ColAccent, ColAccentHover, num);
                button2.DialogResult = DialogResult.OK;
                base.AcceptButton = button2;
                base.CancelButton = button;
                ScaleUi(this, DpiScale());
            }

            private Button MakeDialogButton(string text, int x, Color back, Color hover, int y)
            {
                RoundButton roundButton = new RoundButton();
                roundButton.Text = text;
                roundButton.Bounds = new Rectangle(x, y, 100, 32);
                roundButton.BackColor = back;
                roundButton.HoverBack = hover;
                roundButton.ForeColor = BestTextOn(back);
                roundButton.Cursor = Cursors.Hand;
                base.Controls.Add(roundButton);
                return roundButton;
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                ApplyLightTitleBar(base.Handle);
            }
        }

        private class LibTarget
        {
            public string Name;

            public string StatePath;

            public string ProjectsDir;
        }

        private class HelperOrchestrator : IDisposable
        {
            public int Port;

            public string LaunchId;

            private Process proc;

            private readonly string tmpDir;

            private readonly string statePath;

            private volatile string readyLine;

            private string stderrTail = "";

            private HelperOrchestrator(string tmpDir, string statePath)
            {
                this.tmpDir = tmpDir;
                this.statePath = statePath;
            }

            public static HelperOrchestrator Start(string projectsDir, IEnumerable<string> recents)
            {
                string text = FindBunExe();
                if (text == null)
                {
                    throw new ApplicationException(" не найден  Bun (Freebuff  каталог  resources\\bun\\bun.exe)");
                }
                string text2 = Path.Combine(FreebuffResources, "orchestrator\\orchestrator.js");
                if (!File.Exists(text2))
                {
                    throw new ApplicationException(" не найден  orchestrator.js:" + text2);
                }
                string text3 = Path.Combine(Path.GetTempPath(), "freebuff-controller\\del-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(text3);
                string text4 = Path.Combine(text3, "state.json");
                List<string> list = new List<string>();
                foreach (string recent in recents)
                {
                    list.Add(recent);
                }
                File.WriteAllText(text4, new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "recentProjects", list } }), new UTF8Encoding(false));
                if (!CreateJunction(Path.Combine(text3, "projects"), projectsDir))
                {
                    try
                    {
                        Directory.Delete(text3, true);
                    }
                    catch
                    {
                    }
                    throw new ApplicationException(" Не могу создать временную общую папку (см. лог) ");
                }
                HelperOrchestrator h = new HelperOrchestrator(text3, text4);
                h.LaunchId = "freebuff-controller-" + Guid.NewGuid().ToString("N");
                ProcessStartInfo processStartInfo = new ProcessStartInfo(text, Q(text2));
                processStartInfo.UseShellExecute = false;
                processStartInfo.CreateNoWindow = true;
                processStartInfo.RedirectStandardOutput = true;
                processStartInfo.RedirectStandardError = true;
                processStartInfo.StandardOutputEncoding = Encoding.UTF8;
                processStartInfo.StandardErrorEncoding = Encoding.UTF8;
                processStartInfo.WorkingDirectory = Path.GetDirectoryName(text2);
                ProcessStartInfo processStartInfo2 = processStartInfo;
                processStartInfo2.EnvironmentVariables["PORT"] = "0";
                processStartInfo2.EnvironmentVariables["FREEBUFF_LAUNCH_ID"] = h.LaunchId;
                processStartInfo2.EnvironmentVariables["FREEBUFF_DESKTOP_STATE_PATH"] = text4;
                h.proc = Process.Start(processStartInfo2);
                h.proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null && e.Data.StartsWith("[orchestrator-ready] "))
                    {
                        h.readyLine = e.Data;
                    }
                };
                h.proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null && h.stderrTail.Length < 4000)
                    {
                        HelperOrchestrator helperOrchestrator = h;
                        helperOrchestrator.stderrTail = helperOrchestrator.stderrTail + e.Data + "\r\n";
                    }
                };
                h.proc.BeginOutputReadLine();
                h.proc.BeginErrorReadLine();
                DateTime dateTime = DateTime.Now.AddSeconds(20.0);
                while (h.readyLine == null && DateTime.Now < dateTime)
                {
                    if (h.proc.HasExited)
                    {
                        throw new ApplicationException(" временный  orchestrator  Запуск  Выход (rc=" + h.proc.ExitCode + "):\r\n" + h.stderrTail.Trim());
                    }
                    Thread.Sleep(100);
                }
                if (h.readyLine == null)
                {
                    h.Dispose();
                    throw new ApplicationException(" временный  orchestrator  Запуск :\r\n" + h.stderrTail.Trim());
                }
                Match match = Regex.Match(h.readyLine, "\"port\"\\s*:\\s*(\\d+)");
                if (!match.Success)
                {
                    h.Dispose();
                    throw new ApplicationException(" Нет порта временного orchestrator ");
                }
                h.Port = int.Parse(match.Groups[1].Value);
                return h;
            }

            public string Http(string method, string path, string bodyJson, out int status)
            {
                HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + Port + path);
                httpWebRequest.Method = method;
                httpWebRequest.Proxy = null;
                httpWebRequest.Timeout = 15000;
                httpWebRequest.ReadWriteTimeout = 15000;
                httpWebRequest.Headers["x-freebuff-launch-id"] = LaunchId;
                if (bodyJson != null)
                {
                    httpWebRequest.ContentType = "application/json";
                    byte[] bytes = Encoding.UTF8.GetBytes(bodyJson);
                    httpWebRequest.ContentLength = bytes.Length;
                    using (Stream stream = httpWebRequest.GetRequestStream())
                    {
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
                HttpWebResponse httpWebResponse;
                try
                {
                    httpWebResponse = (HttpWebResponse)httpWebRequest.GetResponse();
                }
                catch (WebException ex)
                {
                    httpWebResponse = ex.Response as HttpWebResponse;
                    if (httpWebResponse == null)
                    {
                        throw;
                    }
                }
                using (httpWebResponse)
                {
                    using (StreamReader streamReader = new StreamReader(httpWebResponse.GetResponseStream(), Encoding.UTF8))
                    {
                        status = (int)httpWebResponse.StatusCode;
                        return streamReader.ReadToEnd();
                    }
                }
            }

            public List<Dictionary<string, object>> ListThreads(out List<string> openFailed)
            {
                openFailed = new List<string>();
                Dictionary<string, Dictionary<string, object>> dictionary = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                int num = MergeProjects(dictionary);
                foreach (string item in CollectProjectPaths(statePath, JunctionTargetScan()))
                {
                    int status = 0;
                    string text = null;
                    try
                    {
                        Http("POST", "/api/project/open", new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "path", item } }), out status);
                    }
                    catch (Exception ex)
                    {
                        text = ex.Message;
                    }
                    if (status == 200)
                    {
                        num = MergeProjects(dictionary);
                        continue;
                    }
                    openFailed.Add((text != null) ? (item + "(" + text + ")") : (item + "(HTTP " + status + ")"));
                }
                if (dictionary.Count == 0 && num != 200)
                {
                    throw new ApplicationException(" Не могу прочитать сессии (HTTP " + num + ")");
                }
                List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
                foreach (Dictionary<string, object> value in dictionary.Values)
                {
                    list.Add(value);
                }
                return list;
            }

            private int MergeProjects(Dictionary<string, Dictionary<string, object>> merged)
            {
                int status;
                string input = Http("GET", "/api/projects", null, out status);
                if (status != 200)
                {
                    return status;
                }
                Dictionary<string, object> dictionary = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(input);
                object value;
                if (dictionary == null || !dictionary.TryGetValue("projects", out value))
                {
                    return status;
                }
                IEnumerable enumerable = value as IEnumerable;
                if (enumerable == null)
                {
                    return status;
                }
                foreach (object item in enumerable)
                {
                    Dictionary<string, object> dictionary2 = item as Dictionary<string, object>;
                    object value2;
                    if (dictionary2 == null || !dictionary2.TryGetValue("threads", out value2))
                    {
                        continue;
                    }
                    IEnumerable enumerable2 = value2 as IEnumerable;
                    if (enumerable2 == null)
                    {
                        continue;
                    }
                    foreach (object item2 in enumerable2)
                    {
                        Dictionary<string, object> dictionary3 = item2 as Dictionary<string, object>;
                        if (dictionary3 != null && dictionary3.ContainsKey("id"))
                        {
                            string text = Convert.ToString(dictionary3["id"]);
                            if (!string.IsNullOrEmpty(text) && !merged.ContainsKey(text))
                            {
                                merged[text] = dictionary3;
                            }
                        }
                    }
                }
                return status;
            }

            private string JunctionTargetScan()
            {
                return Path.Combine(tmpDir, "projects");
            }

            public bool DeleteThread(string id, out bool missing, out string error)
            {
                missing = false;
                error = null;
                try
                {
                    int status;
                    string text = Http("POST", "/api/thread/" + Uri.EscapeDataString(id) + "/delete", "{}", out status);
                    switch (status)
                    {
                    case 200:
                        return true;
                    case 404:
                        missing = true;
                        return false;
                    default:
                        error = "HTTP " + status + " " + text;
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            public void Dispose()
            {
                try
                {
                    if (proc != null && !proc.HasExited)
                    {
                        proc.Kill();
                    }
                    if (proc != null)
                    {
                        proc.WaitForExit(3000);
                    }
                }
                catch
                {
                }
                try
                {
                    if (proc != null)
                    {
                        proc.Dispose();
                    }
                }
                catch
                {
                }
                try
                {
                    File.Delete(statePath);
                }
                catch
                {
                }
                string[] array = new string[3] { "-wal", "-shm", "-journal" };
                foreach (string text in array)
                {
                    try
                    {
                        File.Delete(statePath + text);
                    }
                    catch
                    {
                    }
                }
                try
                {
                    File.Delete(statePath + ".orchestrator-lock.sqlite");
                }
                catch
                {
                }
                string[] array2 = new string[3] { "-wal", "-shm", "-journal" };
                foreach (string text2 in array2)
                {
                    try
                    {
                        File.Delete(statePath + ".orchestrator-lock.sqlite" + text2);
                    }
                    catch
                    {
                    }
                }
                string path = Path.Combine(tmpDir, "projects");
                try
                {
                    Directory.Delete(path, false);
                }
                catch
                {
                }
                try
                {
                    string[] files = Directory.GetFiles(tmpDir);
                    foreach (string path2 in files)
                    {
                        File.Delete(path2);
                    }
                }
                catch
                {
                }
                if (IsJunction(path))
                {
                    LogFail(" Удалить сессии : временный  каталог  junction  убрать  ошибка ,  оставить  " + tmpDir);
                    return;
                }
                try
                {
                    Directory.Delete(tmpDir, false);
                }
                catch
                {
                }
                try
                {
                    Directory.Delete(Path.GetDirectoryName(tmpDir), false);
                }
                catch
                {
                }
            }
        }

        private class DeleteThreadsDialog : Form
        {
            private class ThreadRow
            {
                public string Id;

                public string Title;

                public string Project;

                public string ProjectPath;

                public long WhenMs;

                public bool Archived;

                public bool Draft;

                public bool Running;

                public int Rank;

                public string StateText;

                public string RawState;
            }

            private readonly ComboBox libCombo = new ComboBox();

            private readonly DataGridView grid = new DataGridView();

            private readonly Label stLabel = new Label();

            private readonly TextBox searchBox = new TextBox();

            private readonly List<LibTarget> libs = new List<LibTarget>();

            private HelperOrchestrator helper;

            private int busy;

            private readonly List<ThreadRow> all = new List<ThreadRow>();

            private readonly Dictionary<string, bool> checkedIds = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            private bool quiet;

            private bool reloadPending;

            private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            private string loadNote = "";

            private string resultNote = "";

            public DeleteThreadsDialog()
            {
                Text = " Удалить сессии ";
                base.ClientSize = new Size(580, 468);
                BackColor = ColPanel;
                ForeColor = ColText;
                Font = new Font("Segoe UI", 9f);
                base.FormBorderStyle = FormBorderStyle.FixedDialog;
                base.MinimizeBox = false;
                base.MaximizeBox = false;
                base.ShowInTaskbar = false;
                base.StartPosition = FormStartPosition.CenterParent;
                Label value = new Label
                {
                    AutoSize = false,
                    Text = " выбор  Удалить  сессия ,  Удалить  после  история (,) навсегда  Удалить ,  не  восстановить . \r\n Поиск  совпадение  Название  /  Проект  /  Проект  путь ;' Выбрать все ' только  текущий  Поиск . ",
                    Bounds = new Rectangle(16, 10, 548, 34)
                };
                base.Controls.Add(value);
                Label value2 = new Label
                {
                    AutoSize = false,
                    Text = " База сессий ",
                    Bounds = new Rectangle(16, 48, 56, 20),
                    ForeColor = ColSub
                };
                base.Controls.Add(value2);
                libCombo.DropDownStyle = ComboBoxStyle.DropDownList;
                libCombo.Bounds = new Rectangle(74, 44, 490, 24);
                libCombo.BackColor = ColNeutral;
                libCombo.ForeColor = ColText;
                libCombo.Font = new Font("Segoe UI", 9f);
                libs.AddRange(DetectLibs());
                foreach (LibTarget lib in libs)
                {
                    libCombo.Items.Add(lib.Name);
                }
                if (libCombo.Items.Count > 0)
                {
                    libCombo.SelectedIndex = 0;
                }
                libCombo.SelectedIndexChanged += delegate
                {
                    Reload();
                };
                base.Controls.Add(libCombo);
                Label value3 = new Label
                {
                    AutoSize = false,
                    Text = " Поиск ",
                    Bounds = new Rectangle(16, 78, 36, 20),
                    ForeColor = ColSub
                };
                base.Controls.Add(value3);
                searchBox.Bounds = new Rectangle(56, 76, 508, 23);
                searchBox.BorderStyle = BorderStyle.FixedSingle;
                searchBox.BackColor = ColNeutral;
                searchBox.ForeColor = ColText;
                searchBox.Font = new Font("Segoe UI", 9f);
                searchBox.TextChanged += delegate
                {
                    ApplyFilter();
                };
                searchBox.KeyDown += delegate(object s, KeyEventArgs e)
                {
                    if (e.KeyCode == Keys.Escape && searchBox.Text.Length != 0)
                    {
                        searchBox.Text = "";
                        e.Handled = true;
                    }
                };
                base.Controls.Add(searchBox);
                BuildGrid();
                stLabel.AutoSize = false;
                stLabel.Bounds = new Rectangle(16, 392, 548, 18);
                stLabel.ForeColor = ColSub;
                stLabel.Text = "";
                base.Controls.Add(stLabel);
                Button button = MakeBtn(" Выбрать все ", 16, 80, ColNeutral, ColNeutralHover, 420);
                button.Click += delegate
                {
                    SetAllChecked(true);
                };
                Button button2 = MakeBtn(" Снять выбор ", 106, 100, ColNeutral, ColNeutralHover, 420);
                button2.Click += delegate
                {
                    SetAllChecked(false);
                };
                Button button4 = MakeBtn(" Удалить выбранные ", 376, 100, ColNewVersion, ColNewVersionHover, 420);
                button4.Click += delegate
                {
                    OnDelete();
                };
                Button button5 = MakeBtn(" Закрыть ", 484, 80, ColNeutral, ColNeutralHover, 420);
                button5.DialogResult = DialogResult.Cancel;
                base.CancelButton = button5;
                ScaleUi(this, DpiScale());
                Reload();
            }

            private void BuildGrid()
            {
                grid.Location = new Point(16, 106);
                grid.Size = new Size(548, 280);
                grid.ScrollBars = ScrollBars.Vertical;
                grid.AllowUserToAddRows = false;
                grid.AllowUserToDeleteRows = false;
                grid.AllowUserToResizeRows = false;
                grid.AllowUserToOrderColumns = false;
                grid.RowHeadersVisible = false;
                grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
                grid.MultiSelect = true;
                grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
                grid.BorderStyle = BorderStyle.None;
                grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
                grid.GridColor = ColLine;
                grid.BackgroundColor = ColRow;
                grid.EnableHeadersVisualStyles = false;
                grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
                grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
                grid.ColumnHeadersHeight = 30;
                DataGridViewCellStyle columnHeadersDefaultCellStyle = grid.ColumnHeadersDefaultCellStyle;
                columnHeadersDefaultCellStyle.BackColor = ColHeader;
                columnHeadersDefaultCellStyle.ForeColor = ColSub;
                columnHeadersDefaultCellStyle.SelectionBackColor = ColHeader;
                columnHeadersDefaultCellStyle.SelectionForeColor = ColSub;
                columnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9f);
                columnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 0, 0);
                DataGridViewCellStyle defaultCellStyle = grid.DefaultCellStyle;
                defaultCellStyle.BackColor = ColRow;
                defaultCellStyle.ForeColor = ColText;
                defaultCellStyle.SelectionBackColor = ColSelect;
                defaultCellStyle.SelectionForeColor = ColText;
                defaultCellStyle.Font = new Font("Segoe UI", 9f);
                defaultCellStyle.Padding = new Padding(0, 2, 0, 2);
                defaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
                defaultCellStyle.WrapMode = DataGridViewTriState.False;
                grid.RowTemplate.Height = 30;
                DataGridViewCheckBoxColumn dataGridViewCheckBoxColumn = new DataGridViewCheckBoxColumn();
                dataGridViewCheckBoxColumn.Name = "chk";
                dataGridViewCheckBoxColumn.HeaderText = "☐";
                dataGridViewCheckBoxColumn.FillWeight = 8f;
                dataGridViewCheckBoxColumn.ReadOnly = false;
                dataGridViewCheckBoxColumn.ThreeState = false;
                grid.Columns.Add(dataGridViewCheckBoxColumn);
                grid.Columns.IndexOf(dataGridViewCheckBoxColumn);
                int index = grid.Columns.Add("title", " Название ");
                grid.Columns[index].FillWeight = 42f;
                int index2 = grid.Columns.Add("proj", " Проект ");
                grid.Columns[index2].FillWeight = 24f;
                int index3 = grid.Columns.Add("when", " Активность ");
                grid.Columns[index3].FillWeight = 18f;
                int index4 = grid.Columns.Add("state", " Статус ");
                grid.Columns[index4].FillWeight = 12f;
                for (int i = 0; i < grid.Columns.Count; i++)
                {
                    grid.Columns[i].ReadOnly = i != 0;
                    grid.Columns[i].SortMode = DataGridViewColumnSortMode.NotSortable;
                }
                grid.CurrentCellDirtyStateChanged += delegate
                {
                    if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell)
                    {
                        grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                    }
                };
                grid.CellContentClick += delegate(object s, DataGridViewCellEventArgs e)
                {
                    if (e.RowIndex >= 0 && e.ColumnIndex == 0)
                    {
                        grid.EndEdit();
                    }
                };
                grid.CellValueChanged += delegate(object s, DataGridViewCellEventArgs e)
                {
                    if (!quiet && e.RowIndex >= 0 && e.ColumnIndex == 0)
                    {
                        DataGridViewRow dataGridViewRow = grid.Rows[e.RowIndex];
                        SetRowChecked(dataGridViewRow.Index, dataGridViewRow.Cells[0].Value is bool && (bool)dataGridViewRow.Cells[0].Value);
                    }
                };
                grid.CellMouseEnter += delegate(object s, DataGridViewCellEventArgs e)
                {
                    if (e.RowIndex >= 0 && e.RowIndex < grid.Rows.Count)
                    {
                        grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = ColHover;
                    }
                };
                grid.CellMouseLeave += delegate(object s, DataGridViewCellEventArgs e)
                {
                    if (e.RowIndex >= 0 && e.RowIndex < grid.Rows.Count)
                    {
                        grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.Empty;
                    }
                };
                base.Controls.Add(grid);
            }

            private Button MakeBtn(string text, int x, int width, Color back, Color hover, int y)
            {
                RoundButton roundButton = new RoundButton();
                roundButton.Text = text;
                roundButton.Bounds = new Rectangle(x, y, width, 32);
                roundButton.BackColor = back;
                roundButton.HoverBack = hover;
                roundButton.ForeColor = BestTextOn(back);
                roundButton.Cursor = Cursors.Hand;
                base.Controls.Add(roundButton);
                return roundButton;
            }

            private LibTarget CurrentLib()
            {
                int selectedIndex = libCombo.SelectedIndex;
                if (selectedIndex < 0 || selectedIndex >= libs.Count)
                {
                    return null;
                }
                return libs[selectedIndex];
            }

            private void SetStatus(string text, Color? tint = null)
            {
                stLabel.ForeColor = tint ?? ColText;
                stLabel.Text = text;
            }

            private void Reload()
            {
                if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                {
                    reloadPending = true;
                    return;
                }
                LibTarget lib = CurrentLib();
                if (lib == null)
                {
                    Interlocked.Exchange(ref busy, 0);
                    return;
                }
                grid.Rows.Clear();
                SetStatus(" Запускаю локальный сервис... ");
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string text = null;
                    List<Dictionary<string, object>> list = null;
                    List<string> openFailed = null;
                    try
                    {
                        if (helper != null)
                        {
                            helper.Dispose();
                            helper = null;
                        }
                        List<string> recents = CollectProjectPaths(lib.StatePath, lib.ProjectsDir);
                        helper = HelperOrchestrator.Start(lib.ProjectsDir, recents);
                        list = helper.ListThreads(out openFailed);
                    }
                    catch (Exception ex)
                    {
                        text = ex.Message;
                        LogFail(" Удалить сессии : Не могу прочитать сессии ", ex);
                    }
                    List<Dictionary<string, object>> captured = list;
                    string capturedErr = text;
                    List<string> capturedFailed = openFailed;
                    UiSafe(delegate
                    {
                        Interlocked.Exchange(ref busy, 0);
                        if (capturedErr != null)
                        {
                            SetStatus(" Не могу прочитать сессии ", ColNewVersion);
                            MessageBox.Show(this, " Не могу прочитать сессии :\n" + capturedErr, " Удалить сессии ", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                        }
                        else
                        {
                            FillGrid(captured, capturedFailed);
                        }
                        if (reloadPending)
                        {
                            reloadPending = false;
                            Reload();
                        }
                    });
                });
            }

            private void FillGrid(List<Dictionary<string, object>> rows, List<string> openFailed)
            {
                all.Clear();
                foreach (Dictionary<string, object> row in rows)
                {
                    string text = Str(row, "id");
                    if (text.Length != 0)
                    {
                        ThreadRow threadRow = new ThreadRow();
                        threadRow.Id = text;
                        threadRow.Title = Str(row, "title");
                        if (threadRow.Title.Length == 0)
                        {
                            threadRow.Title = " (без названия) ";
                        }
                        threadRow.ProjectPath = Str(row, "projectPath");
                        threadRow.Project = ProjectName(threadRow.ProjectPath);
                        threadRow.WhenMs = LastAt(row);
                        threadRow.Archived = row.ContainsKey("archivedAt") && row["archivedAt"] != null;
                        threadRow.Draft = Bool(row, "draft");
                        string text2 = Str(row, "turnState");
                        threadRow.Running = string.Equals(text2, "running", StringComparison.OrdinalIgnoreCase) || Bool(row, "willContinue") || Bool(row, "stopping");
                        threadRow.RawState = "status=" + Str(row, "status") + ((text2.Length > 0) ? ("  turnState=" + text2) : "");
                        if (threadRow.Running)
                        {
                            threadRow.Rank = 0;
                            threadRow.StateText = "Вкл";
                        }
                        else if (threadRow.Draft)
                        {
                            threadRow.Rank = 3;
                            threadRow.StateText = " Черновик ";
                        }
                        else if (threadRow.Archived)
                        {
                            threadRow.Rank = 4;
                            threadRow.StateText = " В архиве ";
                        }
                        else if (string.Equals(Str(row, "status"), "closed", StringComparison.OrdinalIgnoreCase))
                        {
                            threadRow.Rank = 2;
                            threadRow.StateText = " Закрыт ";
                        }
                        else
                        {
                            threadRow.Rank = 1;
                            threadRow.StateText = " Простой ";
                        }
                        all.Add(threadRow);
                    }
                }
                Dictionary<string, bool> dictionary = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (ThreadRow item in all)
                {
                    dictionary[item.Id] = true;
                }
                List<string> list = new List<string>();
                foreach (string key in checkedIds.Keys)
                {
                    if (!dictionary.ContainsKey(key))
                    {
                        list.Add(key);
                    }
                }
                foreach (string item2 in list)
                {
                    checkedIds.Remove(item2);
                }
                loadNote = ((openFailed != null && openFailed.Count > 0) ? ("   · есть  " + openFailed.Count + "  шт. проектов не открылось, сессии могут быть скрыты ") : "");
                ApplyFilter();
            }

            private void ApplyFilter()
            {
                if (grid.Columns.Count == 0)
                {
                    return;
                }
                string text = searchBox.Text.Trim();
                List<ThreadRow> list = new List<ThreadRow>();
                foreach (ThreadRow item in all)
                {
                    if (text.Length <= 0 || MatchQuery(item, text))
                    {
                        list.Add(item);
                    }
                }
                list.Sort(CompareRows);
                quiet = true;
                grid.Rows.Clear();
                foreach (ThreadRow item2 in list)
                {
                    object obj = checkedIds.ContainsKey(item2.Id);
                    int index = grid.Rows.Add(obj, item2.Title, item2.Project, WhenText(item2.WhenMs), item2.StateText);
                    grid.Rows[index].Tag = item2;
                    grid.Rows[index].Cells[4].ToolTipText = item2.RawState;
                    if (item2.Running)
                    {
                        grid.Rows[index].DefaultCellStyle.ForeColor = ColNewVersion;
                    }
                }
                quiet = false;
                UpdateSummary(list.Count);
            }

            private static bool MatchQuery(ThreadRow r, string q)
            {
                if (r.Title.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0 && r.Project.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0)
                {
                    return r.ProjectPath.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
                }
                return true;
            }

            // : в ,  Статус  в ( не ). 
            private int CompareRows(ThreadRow a, ThreadRow b)
            {
                int num = b.WhenMs.CompareTo(a.WhenMs);
                if (num == 0)
                {
                    num = a.Rank.CompareTo(b.Rank);
                }
                return num;
            }

            private static string WhenText(long ms)
            {
                if (ms > 0)
                {
                    return Epoch.AddMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                }
                return "";
            }

            private void UpdateSummary(int visible)
            {
                int count = checkedIds.Count;
                string text = loadNote + resultNote;
                resultNote = "";
                stLabel.ForeColor = ((count > 0) ? ColNewVersion : ColSub);
                if (all.Count == 0)
                {
                    stLabel.ForeColor = ColSub;
                    stLabel.Text = " В базе нет сессий. " + text;
                }
                else if (visible == 0)
                {
                    stLabel.Text = " Поиск  нет  совпадение  сессия  ·  выбор  " + count + "  шт. " + text;
                }
                else
                {
                    stLabel.Text = " Всего  " + all.Count + "  сессий · показано  " + visible + "  шт. · выбрано  " + count + "  шт. " + text;
                }
            }

            private void SetRowChecked(int rowIndex, bool value)
            {
                if (rowIndex < 0 || rowIndex >= grid.Rows.Count)
                {
                    return;
                }
                ThreadRow threadRow = grid.Rows[rowIndex].Tag as ThreadRow;
                if (threadRow != null)
                {
                    if (value)
                    {
                        checkedIds[threadRow.Id] = true;
                    }
                    else
                    {
                        checkedIds.Remove(threadRow.Id);
                    }
                    UpdateSummary(grid.Rows.Count);
                }
            }

            private static long LastAt(Dictionary<string, object> t)
            {
                string[] array = new string[4] { "lastPromptAt", "lastTurnFinishedAt", "createdAt", "updatedAt" };
                foreach (string key in array)
                {
                    if (t.ContainsKey(key) && t[key] != null)
                    {
                        try
                        {
                            return Convert.ToInt64(t[key]);
                        }
                        catch
                        {
                        }
                    }
                }
                return 0L;
            }

            private static string Str(Dictionary<string, object> t, string key)
            {
                object value;
                if (!t.TryGetValue(key, out value) || value == null)
                {
                    return "";
                }
                return Convert.ToString(value);
            }

            private static bool Bool(Dictionary<string, object> t, string key)
            {
                object value;
                if (!t.TryGetValue(key, out value) || value == null)
                {
                    return false;
                }
                try
                {
                    return Convert.ToBoolean(value);
                }
                catch
                {
                    return false;
                }
            }

            private static string ProjectName(string path)
            {
                string text = ((path == null) ? "" : path).Trim();
                while (text.Length > 0 && (text[text.Length - 1] == '\\' || text[text.Length - 1] == '/'))
                {
                    text = text.Substring(0, text.Length - 1);
                }
                if (text.Length == 0)
                {
                    return "";
                }
                string fileName = Path.GetFileName(text);
                if (fileName.Length <= 0)
                {
                    return text;
                }
                return fileName;
            }

            private void SetAllChecked(bool value)
            {
                grid.EndEdit();
                quiet = true;
                foreach (DataGridViewRow item in (IEnumerable)grid.Rows)
                {
                    item.Cells[0].Value = value;
                    ThreadRow threadRow = item.Tag as ThreadRow;
                    if (threadRow != null)
                    {
                        if (value)
                        {
                            checkedIds[threadRow.Id] = true;
                        }
                        else
                        {
                            checkedIds.Remove(threadRow.Id);
                        }
                    }
                }
                quiet = false;
                UpdateSummary(grid.Rows.Count);
            }

            private static string ConfirmText(List<ThreadRow> picked, int running)
            {
                string text = " сейчас  навсегда  Удалить  " + picked.Count + "  сессий с историей (сообщения и очередь), без восстановления.\r\n\r\n ";
                if (running > 0)
                {
                    object obj = text;
                    text = string.Concat(obj, " из них  ", running, "  шт.  запущен : после  шаги  может  не могу  норма ,  сначала  Freebuff  в  Стоп . \r\n\r\n");
                }
                text += " К удалению:\r\n ";
                int num = ((picked.Count > 12) ? 12 : picked.Count);
                for (int i = 0; i < num; i++)
                {
                    string text2 = text;
                    text = text2 + "  · " + Clip(picked[i].Title, 26) + "(" + picked[i].Project + ")" + (picked[i].Running ? "\u3000[Вкл]" : "") + "\r\n";
                }
                if (picked.Count > num)
                {
                    object obj2 = text;
                    text = string.Concat(obj2, "   · ...ещё  ", picked.Count - num, "  шт.\r\n ");
                }
                return text + "\r\n если  сессия  вкладки  ещё  в  Freebuff  окно  в ,  Удалить  после  вкладки  закрой . \r\n\r\n Точно удалить ";
            }

            private static string Clip(string s, int max)
            {
                if (s == null)
                {
                    return "";
                }
                if (s.Length > max)
                {
                    return s.Substring(0, max) + "...";
                }
                return s;
            }

            private void OnDelete()
            {
                if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                {
                    return;
                }
                grid.EndEdit();
                List<ThreadRow> list = new List<ThreadRow>();
                foreach (ThreadRow item in all)
                {
                    if (checkedIds.ContainsKey(item.Id))
                    {
                        list.Add(item);
                    }
                }
                if (list.Count == 0)
                {
                    Interlocked.Exchange(ref busy, 0);
                    SetStatus(" Ничего не выбрано. ", ColNewVersion);
                    return;
                }
                list.Sort(CompareRows);
                List<string> ids = new List<string>();
                int num = 0;
                foreach (ThreadRow item2 in list)
                {
                    ids.Add(item2.Id);
                    if (item2.Running)
                    {
                        num++;
                    }
                }
                if (MessageBox.Show(this, ConfirmText(list, num), " Подтвердить удаление ", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    Interlocked.Exchange(ref busy, 0);
                    return;
                }
                SetStatus(" Удаление... ");
                ThreadPool.QueueUserWorkItem(delegate
                {
                    int num2 = 0;
                    int num3 = 0;
                    List<string> list2 = new List<string>();
                    for (int i = 0; i < ids.Count; i++)
                    {
                        int step = i + 1;
                        UiSafe(delegate
                        {
                            SetStatus(" Удаляю  " + step + "/" + ids.Count + "...");
                        });
                        bool missing;
                        string error;
                        if (helper == null)
                        {
                            list2.Add(ids[i] + ": локальный сервис  недоступен ");
                        }
                        else if (helper.DeleteThread(ids[i], out missing, out error))
                        {
                            num2++;
                        }
                        else if (missing)
                        {
                            num3++;
                        }
                        else
                        {
                            list2.Add(ids[i] + ":" + error);
                        }
                    }
                    int fDone = num2;
                    int fMissing = num3;
                    int fTotal = ids.Count;
                    List<string> fFails = list2;
                    UiSafe(delegate
                    {
                        Interlocked.Exchange(ref busy, 0);
                        string text = " Удалено  " + fDone + "  сессий (с историей). ";
                        if (fMissing > 0 && fDone == 0 && fMissing == fTotal && fFails.Count == 0)
                        {
                            text = " шт.  все  не : " + fTotal + "  сессий отдаёт 'не существует'. Если они в списке - изменился локальный API/авторизация. ";
                        }
                        else if (fMissing > 0)
                        {
                            object obj = text;
                            text = string.Concat(obj, "  есть  ", fMissing, "  шт. - черновики или отсутствуют, пропущено. ");
                        }
                        if (fFails.Count > 0)
                        {
                            object obj2 = text;
                            text = string.Concat(obj2, "  ошибка  ", fFails.Count, "  шт. ");
                        }
                        resultNote = "  ·  " + text;
                        if (fFails.Count > 0)
                        {
                            MessageBox.Show(this, " сессия  Удалить  ошибка :\r\n" + string.Join("\r\n", fFails.ToArray()), " Удалить сессии ", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                        }
                        Reload();
                    });
                });
            }

            private void UiSafe(Action a)
            {
                try
                {
                    if (!base.IsDisposed && base.IsHandleCreated)
                    {
                        if (base.InvokeRequired)
                        {
                            BeginInvoke(a);
                        }
                        else
                        {
                            a();
                        }
                    }
                }
                catch
                {
                }
            }

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                if (helper != null)
                {
                    helper.Dispose();
                    helper = null;
                }
                base.OnFormClosed(e);
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                ApplyLightTitleBar(base.Handle);
            }
        }

        private class ProxySettingsDialog : Form
        {
            private readonly TextBox urlBox = new TextBox();

            private readonly Label stateLabel = new Label();

            private readonly Label portProbeLabel = new Label();

            public bool Changed { get; private set; }

            public ProxySettingsDialog()
            {
                Text = " Прокси ";
                base.ClientSize = new Size(460, 232);
                BackColor = ColPanel;
                ForeColor = ColText;
                Font = new Font("Segoe UI", 9f);
                base.FormBorderStyle = FormBorderStyle.FixedDialog;
                base.MinimizeBox = false;
                base.MaximizeBox = false;
                base.ShowInTaskbar = false;
                base.StartPosition = FormStartPosition.CenterParent;
                Label value = new Label
                {
                    AutoSize = false,
                    Text = " сеть  путь : Локальный прокси  ->  Системный прокси  ->  Напрямую .  из этого инструмента  Запуск  Freebuff  Инстанс  в  прокси  тоже  будет . ",
                    Bounds = new Rectangle(16, 10, 428, 36),
                    ForeColor = ColSub
                };
                base.Controls.Add(value);
                Label value2 = new Label
                {
                    AutoSize = false,
                    Text = " Адрес прокси (пусто=авто, off=выкл) ",
                    Bounds = new Rectangle(16, 52, 428, 18)
                };
                base.Controls.Add(value2);
                urlBox.Bounds = new Rectangle(16, 72, 428, 23);
                urlBox.Text = CurrentSettingText();
                base.Controls.Add(urlBox);
                stateLabel.AutoSize = false;
                stateLabel.Bounds = new Rectangle(16, 94, 428, 36);
                base.Controls.Add(stateLabel);
                portProbeLabel.AutoSize = false;
                portProbeLabel.Bounds = new Rectangle(16, 142, 428, 18);
                portProbeLabel.ForeColor = ColSub;
                base.Controls.Add(portProbeLabel);
                Label value3 = new Label
                {
                    AutoSize = false,
                    Text = " Применится сразу для контроллера и новых инстансов. ",
                    Bounds = new Rectangle(16, 164, 428, 18),
                    ForeColor = ColSub
                };
                base.Controls.Add(value3);
                Button button3 = MakeButton(" Сохранить ", 236, ColAccent, ColAccentHover);
                button3.Click += delegate
                {
                    ApplySetting(urlBox.Text.Trim());
                };
                Button button4 = MakeButton(" Отмена ", 346, ColNeutral, ColNeutralHover);
                button4.DialogResult = DialogResult.Cancel;
                base.CancelButton = button4;
                UpdateState();
                ScaleUi(this, DpiScale());
            }

            private static string CurrentSettingText()
            {
                if (localProxyMode == "off")
                {
                    return "off";
                }
                if (localProxyMode == "manual")
                {
                    return manualProxyUrl;
                }
                return "";
            }

            private void ApplySetting(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    try
                    {
                        File.Delete(LocalProxyConfigFile);
                    }
                    catch
                    {
                    }
                }
                else if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    WriteProxyConfig("off");
                }
                else
                {
                    Uri result;
                    if (!Uri.TryCreate(value, UriKind.Absolute, out result))
                    {
                        MessageBox.Show(this, " не  это  валидный  адрес ,  напр.  http://127.0.0.1:10808", " Прокси ", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                        return;
                    }
                    WriteProxyConfig(value);
                }
                ReloadProxyConfig();
                Changed = true;
                urlBox.Text = CurrentSettingText();
                if (localProxyMode == "auto")
                {
                    DetectProxyAsync();
                }
                UpdateState();
            }

            private void UpdateState()
            {
                if (localProxyMode == "off")
                {
                    stateLabel.Text = "✗ (off): сеть   Системный прокси  ->  Напрямую ,  Запуск  Инстанс  не  прокси . ";
                    stateLabel.ForeColor = ColSub;
                    RefreshPortProbe(false);
                    return;
                }
                string text = ((localProxyMode == "manual") ? manualProxyUrl : detectedProxyUrl);
                if (text == null)
                {
                    stateLabel.Text = "...  авто  проверяю : частые порты (7890 / 7897 / 10808 / 10809 / 1080) нет доступного  HTTP  прокси . ";
                    stateLabel.ForeColor = ColSub;
                    RefreshPortProbe(true);
                    return;
                }
                bool flag = ProxyAlive(text);
                string text2 = (IsSocksUrl(text) ? "(SOCKS: только  Запуск  Инстанс  использовать ,  контроллер  свои запросы пропускаю )" : "");
                stateLabel.Text = (flag ? ("✓  Локальный прокси  Работает (" + text + ")" + text2 + ": контроллер  сеть  и  Запуск  Инстанс  все  будет  использовать . ") : ("✗  не  в (" + text + "): авто   Системный прокси  ->  Напрямую ,  Запуск  Инстанс  не  прокси . "));
                stateLabel.ForeColor = (flag ? ColGreen : ColSub);
                RefreshPortProbe(true);
            }

            private void RefreshPortProbe(bool functional)
            {
                portProbeLabel.Text = " Пробую порты... ";
                ThreadPool.QueueUserWorkItem(delegate
                {
                    StringBuilder stringBuilder = new StringBuilder(" Порты: ");
                    for (int i = 0; i < AutoDetectPorts.Length; i++)
                    {
                        string url = "http://127.0.0.1:" + AutoDetectPorts[i];
                        bool flag = ProxyAlive(url) && (!functional || ProxyFunctional(url));
                        stringBuilder.Append(AutoDetectPorts[i]).Append(flag ? " ✓" : " ✗");
                        if (i < AutoDetectPorts.Length - 1)
                        {
                            stringBuilder.Append(" · ");
                        }
                    }
                    string text = stringBuilder.ToString();
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            portProbeLabel.Text = text;
                        });
                    }
                    catch
                    {
                    }
                });
            }

            private Button MakeButton(string text, int x, Color back, Color hover)
            {
                RoundButton roundButton = new RoundButton();
                roundButton.Text = text;
                roundButton.Bounds = new Rectangle(x, 184, 100, 32);
                roundButton.BackColor = back;
                roundButton.HoverBack = hover;
                roundButton.ForeColor = BestTextOn(back);
                roundButton.Cursor = Cursors.Hand;
                base.Controls.Add(roundButton);
                return roundButton;
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                ApplyLightTitleBar(base.Handle);
            }
        }

        private class ProcRow
        {
            public int Pid;

            public int Parent;

            public string Name;

            public string Exe;
        }

        private enum RestoreOutcome
        {
            Started,
            NothingToDo,
            NoBuild,
            VersionMismatch,
            Busy,
            InstancesRunning
        }

        private const int MaxSlot = 9;

        private const string trayDefaultTip = " Freebuff Controller ";

        private const string QuotaApiUrl = "https://www.codebuff.com/api/v1/freebuff/session";

        private const string FallbackUpdateFeed = "https://freebuff.com/api/desktop/updates/win-x64/latest.yml";

        private const string ReleasesPageUrl = "https://github.com/CodebuffAI/codebuff-community/releases/latest";

        private const string HanhuaMarker = "<html lang=\"zh-CN\">";

        private const int HanhuaBackupKeep = 2;

        private const string ChineseReplyMarker = " # Language Rule ";

        private const double HanhuaBuildSettleSeconds = 8.0;

        private const double HanhuaRetrySeconds = 10.0;

        private const string DefaultLocalProxyUrl = "http://127.0.0.1:10808";

        private const string Probe204Url = "http://connect.rom.miui.com/generate_204";

        private const string ProbeForeignUrl = "https://www.gstatic.com/generate_204";

        private const int MinQuotaColumnWidth = 170;

        private const uint GENERIC_WRITE_FLAG = 1073741824u;

        private const uint FILE_SHARE_RWD = 7u;

        private const uint OPEN_EXISTING_FLAG = 3u;

        private const uint FILE_FLAG_BACKUP_SEMANTICS_FLAG = 33554432u;

        private const uint FILE_FLAG_OPEN_REPARSE_POINT_FLAG = 2097152u;

        private const uint FSCTL_SET_REPARSE_POINT_CODE = 589988u;

        private const uint IO_REPARSE_TAG_MOUNT_POINT_CODE = 2684354563u;

        private const string HandoverMergeJsB64 = "Ly8gRnJlZWJ1ZmYg0LrQvtC90YLRgNC+0LvQu9C10YAg0LzRg9C70YzRgtC40LjQvdGB0YLQsNC90YHQsCDigJQg0YHQutGA0LjQv9GCINGB0LvQuNGP0L3QuNGPINGN0YHRgtCw0YTQtdGC0Ysg0YHQtdGB0YHQuNC5LgovLwovLyDQl9Cw0L/Rg9GB0LrQsNC10YLRgdGPINGI0YLQsNGC0L3Ri9C8IEZyZWVidWZmIHJlc291cmNlcy9idW4vYnVuLmV4ZTogYnVuOnNxbGl0ZSDRh9C40YLQsNC10YIg0LHQsNC30Ysg0LTQstGD0YUKLy8g0Y3QutC30LXQvNC/0LvRj9GA0L7QsiDQvdCw0L/RgNGP0LzRg9GOLCDQsCDQstGL0LHRgNCw0L3QvdGL0LUg0YHQtdGB0YHQuNC4ICh0aHJlYWRzICsgbWVzc2FnZXMgKyBxdWV1ZV9pdGVtcyArCi8vIGF1dG9fcnVuX2RlY2lzaW9uX3JlY2VpcHRzICsgdGhyZWFkX2RlbGl2ZXJpZXMpINC60L7Qv9C40YDRg9GO0YLRgdGPINC40Lcg0LHQsNC30Yst0LjRgdGC0L7Rh9C90LjQutCwCi8vINCyINCx0LDQt9GDLdC/0YDQuNGR0LzQvdC40LouINCh0LDQvCDQutC+0L3RgtGA0L7Qu9C70LXRgCDQvtGB0YLQsNGR0YLRgdGPINC+0LTQvdC+0YTQsNC50LvQvtCy0YvQvCBleGUg0LHQtdC3INC30LDQstC40YHQuNC80L7RgdGC0Lgg0L7RgiBTUUxpdGUuCi8vCi8vINCY0YHQv9C+0LvRjNC30L7QstCw0L3QuNC1OgovLyAgIGJ1biBoYW5kb3Zlci1tZXJnZS5qcyBsaXN0ICA8c3JjRGI+Ci8vICAgYnVuIGhhbmRvdmVyLW1lcmdlLmpzIG1lcmdlIDxzcmNEYj4gPGRzdERiPiA8aWRzSnNvbnxAaWRzLmpzb24+IFtyZW5hbWVzSnNvbnxAcmVuYW1lcy5qc29uXQovLyBpZHMvcmVuYW1lcyDQv9C10YDQtdC00LDRjtGC0YHRjyDQutCw0LogSlNPTiDQuNC70Lgg0LrQsNC6ICJA0L/Rg9GC0YwiICjQutC+0L3RgtGA0L7Qu9C70LXRgCDQuNC00ZHRgiDRh9C10YDQtdC3INGE0LDQudC7LAovLyDQvtCx0YXQvtC00Y8g0Y3QutGA0LDQvdC40YDQvtCy0LDQvdC40LUg0LIg0LrQvtC80LDQvdC00L3QvtC5INGB0YLRgNC+0LrQtSkuCi8vINCS0YvQstC+0LTQuNGCINC+0LTQvdGDINGB0YLRgNC+0LrRgyBKU09OIChVVEYtOCwgc3Rkb3V0KToKLy8gICB7Im9rIjp0cnVlLCJhY3Rpb24iOiJsaXN0IiwidGhyZWFkcyI6Wy4uLl19Ci8vICAgeyJvayI6dHJ1ZSwiYWN0aW9uIjoibWVyZ2UiLCJjb3BpZWQiOlsuLi5dLCJza2lwcGVkIjpbLi4uXX0KLy8gICB7Im9rIjpmYWxzZSwiZXJyb3IiOiIuLi4ifQovLyDQm9GO0LHQsNGPINC/0YDQvtCx0LvQtdC80LAg0YEg0L/Rg9GC0Y/QvNC4INC+0YLQtNCw0ZHRgiBvazpmYWxzZTsgbWVyZ2Ug0LLRi9C/0L7Qu9C90Y/QtdGC0YHRjyDQvtC00L3QvtC5INGC0YDQsNC90LfQsNC60YbQuNC10LksCi8vINC/0YDQuCDRgdCx0L7QtSDQvtGC0LrQsNGC0YvQstCw0LXRgtGB0Y8g0YbQtdC70LjQutC+0LwuCi8vCi8vINCf0YDQsNCy0LjQu9CwINC60L7Qv9C40YDQvtCy0LDQvdC40Y86Ci8vIC0g0JjQtNC10LzQv9C+0YLQtdC90YLQvdC+0YHRgtGMOiB0aHJlYWQgaWQsINGD0LbQtSDRgdGD0YnQtdGB0YLQstGD0Y7RidC40LUg0LIg0LHQsNC30LUt0L/RgNC40ZHQvNC90LjQutC1LCDQstGB0LXQs9C00LAKLy8gICDQv9GA0L7Qv9GD0YHQutCw0Y7RgtGB0Y8sINC/0LXRgNC10LfQsNC/0LjRgdC4INC90LXRgi4KLy8gLSDQoNCw0LfQstGP0LfQutCwINGA0LDQsdC+0YfQuNGFINC+0LHQu9Cw0YHRgtC10Lk6IHRocmVhZCDRgdGB0YvQu9Cw0LXRgtGB0Y8g0L3QsCBwcm9qZWN0cyDRgSDRgtC10Lwg0LbQtSByb290X3BhdGgg0LIKLy8gICDQsdCw0LfQtS3Qv9GA0LjRkdC80L3QuNC60LUgKNC10YHQu9C4INGB0YLRgNC+0LrQuCDQvdC10YIsINC+0L3QsCDRgdC+0LfQtNCw0ZHRgtGB0Y8g0LDQstGC0L7QvNCw0YLQuNGH0LXRgdC60Lgg4oCUINGN0YLQviDQuCDQtdGB0YLRjAovLyAgIHByb2plY3RfaWQg0LTQu9GPINCy0L3QtdGI0L3QtdCz0L4g0LrQu9GO0YfQsCDRgdC10YHRgdC40LgpOyDQutCw0LrRg9GOINGA0LDQsdC+0YfRg9GOINC+0LHQu9Cw0YHRgtGMINC+0YLQutGA0YvQuwovLyAgINC40YHRgtC+0YfQvdC40LosINCwINC60LDQutGD0Y4g0L/RgNC40ZHQvNC90LjQuiDigJQg0L3QtSDQstCw0LbQvdC+LgovLyAtINCf0YDQuNCy0LDRgtC90L7QtSDRgdC+0YHRgtC+0Y/QvdC40LUg0LTQstC40LbQutCwINC+0LHQvdGD0LvRj9C10YLRgdGPICh0dXJuX3N0YXRlIC8gaGFybmVzc19zdGF0ZSAvCi8vICAg0YDQtdC10YHRgtGAIGF1dG9fcnVuIC8g0YLQvtC60LXQvdGLIHNwb25zb3JlZCAvIGZyZWVidWZmX2luc3RhbmNlX2lkIC8g0L3QtdC/0YDQvtGH0LjRgtCw0L3QvdGL0LUKLy8gICBhdHRlbnRpb24gLyB3b3JsZF9zbmFwc2hvdCksINC/0L7RjdGC0L7QvNGDINC/0YDQuNC90Y/QstGI0LjQuSDRgdC10YHRgdC40Y4g0LDQutC60LDRg9C90YIg0L/RgNC+0LTQvtC70LbQsNC10YIKLy8gICDRgSDRh9C40YHRgtC+0LPQviDRgdC+0YHRgtC+0Y/QvdC40Y8gwqvRgdCy0L7QsdC+0LTQtdC9wrsg0Lgg0L3QtSDQvdCw0YHQu9C10LTRg9C10YIgcnVudGltZS3QtNC+0LvQs9C4INC/0YDQtdC00YvQtNGD0YnQtdCz0L4uCi8vIC0g0JHQtdC70YvQuSDRgdC/0LjRgdC+0Log0LrQvtC70L7QvdC+0Lo6INCy0YHQtSBJTlNFUlQg0L/QuNGI0YPRgiDRgtC+0LvRjNC60L4g0YDQtdCw0LvRjNC90L4g0YHRg9GJ0LXRgdGC0LLRg9GO0YnQuNC1INCyCi8vICAg0LHQsNC30LUt0L/RgNC40ZHQvNC90LjQutC1INC60L7Qu9C+0L3QutC4ICjQv9C10YDQtdGB0LXRh9C10L3QuNC1INC/0L4gUFJBR01BKSwg0L/QvtGN0YLQvtC80YMg0L/RgNC4INGB0LzQtdC90LUg0LLQtdGA0YHQuNC4Ci8vICAgRnJlZWJ1ZmYg0LHQuNGC0YvQuSBTUUwg0L3QtSDRgdC+0LHQuNGA0LDQtdGC0YHRjzsg0LzQuNCz0YDQsNGG0LjRjiDQutC+0LvQvtC90L7QuiDRgdCw0LzQvtC5INCx0LDQt9GLLdC/0YDQuNGR0LzQvdC40LrQsAovLyAgINC00LXQu9Cw0LXRgiB1cGdyYWRlLdC/0YDQvtGG0LXRgdGBIG9yY2hlc3RyYXRvciDQv9GA0Lgg0YHRgtCw0YDRgtC1LgoKdmFyIERhdGFiYXNlID0gZ2xvYmFsVGhpcy5EYXRhYmFzZSB8fCByZXF1aXJlKCJidW46c3FsaXRlIikuRGF0YWJhc2U7CgpmdW5jdGlvbiBvdXQob2JqKSB7CiAgcHJvY2Vzcy5zdGRvdXQud3JpdGUoSlNPTi5zdHJpbmdpZnkob2JqKSArICJcbiIpOwp9CgovLyBhcmd2W2ldOiBKU09OINGB0YLRgNC+0LrQvtC5INC40LvQuCAiQGZpbGUiIChKU09OINGH0LjRgtCw0LXRgtGB0Y8g0LjQtyDRhNCw0LnQu9CwKS4KZnVuY3Rpb24gYXJnSnNvbihpLCBmYWxsYmFjaykgewogIHZhciB2ID0gcHJvY2Vzcy5hcmd2W2ldOwogIGlmICghdikgcmV0dXJuIGZhbGxiYWNrOwogIGlmICh2LmNoYXJDb2RlQXQoMCkgPT09IDY0KSB7CiAgICB2YXIgZnMgPSByZXF1aXJlKCJmcyIpOwogICAgcmV0dXJuIEpTT04ucGFyc2UoZnMucmVhZEZpbGVTeW5jKHYuc2xpY2UoMSksICJ1dGY4IikpOwogIH0KICByZXR1cm4gSlNPTi5wYXJzZSh2KTsKfQoKZnVuY3Rpb24gZGllKG1zZykgewogIG91dCh7IG9rOiBmYWxzZSwgZXJyb3I6IFN0cmluZyhtc2cpIH0pOwogIHByb2Nlc3MuZXhpdCgwKTsgLy8g0LrQvtC90YLRgNC+0LvQu9C10YAg0YDQsNC30LHQuNGA0LDQtdGCINGC0L7Qu9GM0LrQviBKU09OINC40Lcgc3Rkb3V0LCDQutC+0LQg0LLRi9GF0L7QtNCwINC90LUg0LLQsNC20LXQvQp9CgovLyDQntGC0LrRgNGL0YLQuNC1INGC0L7Qu9GM0LrQviDQvdCwINGH0YLQtdC90LjQtTsg0LXRgdC70Lgg0LjQvNGPINC+0L/RhtC40Lgg0YMgYnVuINC90LUg0YHQvtCy0L/QsNC70L4g4oCUINC+0YLQutCw0YLRi9Cy0LDQtdC80YHRjyDQvdCwCi8vINC+0LHRi9GH0L3QvtC1ICjRhNCw0LnQuyDQvtGB0YLQsNGR0YLRgdGPINGH0LjRgtCw0LXQvNGL0LwpLgpmdW5jdGlvbiBvcGVuUk8ocGF0aCkgewogIHRyeSB7CiAgICByZXR1cm4gbmV3IERhdGFiYXNlKHBhdGgsIHsgcmVhZG9ubHk6IHRydWUgfSk7CiAgfSBjYXRjaCAoZSkgewogICAgcmV0dXJuIG5ldyBEYXRhYmFzZShwYXRoKTsKICB9Cn0KCmZ1bmN0aW9uIHRhYmxlQ29scyhkYiwgdGFibGUpIHsKICByZXR1cm4gZGIucXVlcnkoIlBSQUdNQSB0YWJsZV9pbmZvKCIgKyB0YWJsZSArICIpIikuYWxsKCkubWFwKGZ1bmN0aW9uIChjKSB7CiAgICByZXR1cm4gYy5uYW1lOwogIH0pOwp9CgovLyDQodGD0LbQsNC10Lwgcm93T2JqINC00L4g0LrQvtC70L7QvdC+0LosINC/0YDQuNGB0YPRgtGB0YLQstGD0Y7RidC40YUg0LIgZHN0Q29scywg0Lgg0LTQtdC70LDQtdC8IElOU0VSVCBPUiBJR05PUkUuCmZ1bmN0aW9uIGluc2VydFJvdyhkYiwgdGFibGUsIHJvd09iaiwgZHN0Q29scykgewogIHZhciBjb2xzID0gW107CiAgdmFyIHBhcmFtcyA9IHt9OwogIGZvciAodmFyIGsgaW4gcm93T2JqKSB7CiAgICBpZiAoZHN0Q29scy5pbmRleE9mKGspIDwgMCkgY29udGludWU7CiAgICBjb2xzLnB1c2goayk7CiAgICBwYXJhbXNbIiQiICsga10gPSByb3dPYmpba107CiAgfQogIGlmIChjb2xzLmxlbmd0aCA9PT0gMCkgcmV0dXJuOwogIHZhciBxID0gIklOU0VSVCBPUiBJR05PUkUgSU5UTyAiICsgdGFibGUgKyAiICgiICsgY29scy5qb2luKCIsICIpICsKICAgICIpIFZBTFVFUyAoIiArIGNvbHMubWFwKGZ1bmN0aW9uIChjKSB7IHJldHVybiAiJCIgKyBjOyB9KS5qb2luKCIsICIpICsgIikiOwogIGRiLnF1ZXJ5KHEpLnJ1bihwYXJhbXMpOwp9CgpmdW5jdGlvbiBsaXN0VGhyZWFkcyhzcmNQYXRoKSB7CiAgdmFyIHNyYyA9IG9wZW5STyhzcmNQYXRoKTsKICB0cnkgewogICAgdmFyIGNvdW50cyA9IHt9OwogICAgdmFyIG1jID0gc3JjLnF1ZXJ5KAogICAgICAiU0VMRUNUIHRocmVhZF9pZCwgQ09VTlQoKikgQVMgbiBGUk9NIG1lc3NhZ2VzIEdST1VQIEJZIHRocmVhZF9pZCIKICAgICk7CiAgICBmb3IgKHZhciByIG9mIG1jLmFsbCgpKSBjb3VudHNbci50aHJlYWRfaWRdID0gci5uOwogICAgdmFyIHRocmVhZHMgPSBbXTsKICAgIHZhciByb3dzID0gc3JjLnF1ZXJ5KAogICAgICAiU0VMRUNUIGlkLCB0aXRsZSwgc3RhdHVzLCB0dXJuX3N0YXRlLCBtb2RlbCwgcHJvamVjdF9wYXRoLCB1cGRhdGVkX2F0IiArCiAgICAgICIgRlJPTSB0aHJlYWRzIE9SREVSIEJZIHVwZGF0ZWRfYXQgREVTQyIKICAgICkuYWxsKCk7CiAgICBmb3IgKHZhciB0IG9mIHJvd3MpIHsKICAgICAgdGhyZWFkcy5wdXNoKHsKICAgICAgICBpZDogdC5pZCwKICAgICAgICB0aXRsZTogdC50aXRsZSwKICAgICAgICBzdGF0dXM6IHQuc3RhdHVzLAogICAgICAgIHR1cm5TdGF0ZTogdC50dXJuX3N0YXRlLAogICAgICAgIG1vZGVsOiB0Lm1vZGVsLAogICAgICAgIHByb2plY3RQYXRoOiB0LnByb2plY3RfcGF0aCwKICAgICAgICBtZXNzYWdlczogY291bnRzW3QuaWRdIHx8IDAsCiAgICAgICAgdXBkYXRlZDogdC51cGRhdGVkX2F0LAogICAgICB9KTsKICAgIH0KICAgIG91dCh7IG9rOiB0cnVlLCBhY3Rpb246ICJsaXN0IiwgdGhyZWFkczogdGhyZWFkcyB9KTsKICB9IGZpbmFsbHkgewogICAgc3JjLmNsb3NlKCk7CiAgfQp9CgovLyDQn9GA0L7QstC10YDRj9C10LwsINGH0YLQviDQsiDQsdCw0LfQtS3Qv9GA0LjRkdC80L3QuNC60LUg0LXRgdGC0Ywg0YHRgtGA0L7QutCwIHByb2plY3RzINGBINGN0YLQuNC8IHJvb3RfcGF0aCwg0LgKLy8g0LLQvtC30LLRgNCw0YnQsNC10Lwg0LXRkSBpZC4g0J7QsdGL0YfQvdC+INGN0LrQt9C10LzQv9C70Y/RgC3Qv9GA0LjRkdC80L3QuNC6INGD0LbQtSDQvtGC0LrRgNGL0LLQsNC7INGC0YMg0LbQtSDRgNCw0LHQvtGH0YPRjgovLyDQvtCx0LvQsNGB0YLRjCDQuCDRgdGC0YDQvtC60LAg0L3QsCDQvNC10YHRgtC1OyDQtdGB0LvQuCDQvdC10YIg4oCUINC00L7QsdCw0LLQu9GP0LXQvCAo0L/RgNC10LTQv9C+0YfQuNGC0LDRjyBwcm9qZWN0X2lkCi8vICAg0LjRgdGC0L7Rh9C90LjQutCwOiDQvtC9INCy0YvQstC+0LTQuNGC0YHRjyDQuNC3INC/0YPRgtC4INC4INC90LAg0L7QtNC90L7QuSDQvNCw0YjQuNC90LUg0L3QtSDQvNC10L3Rj9C10YLRgdGPOyDQv9GA0LgKLy8gICDQt9Cw0L3Rj9GC0L7QvCBpZCDQsdC10YDRkdC8INGB0LvRg9GH0LDQudC90YvQuSkuCmZ1bmN0aW9uIGVuc3VyZVByb2plY3QoZHN0LCByb290UGF0aCwgcHJlZmVycmVkSWQpIHsKICB2YXIgZm91bmQgPSBkc3QKICAgIC5xdWVyeSgiU0VMRUNUIGlkIEZST00gcHJvamVjdHMgV0hFUkUgcm9vdF9wYXRoID0gJHAiKQogICAgLmdldCh7ICRwOiByb290UGF0aCB9KTsKICBpZiAoZm91bmQpIHJldHVybiBmb3VuZC5pZDsKICBpZiAocHJlZmVycmVkSWQpIHsKICAgIHRyeSB7CiAgICAgIGRzdC5xdWVyeSgKICAgICAgICAiSU5TRVJUIE9SIElHTk9SRSBJTlRPIHByb2plY3RzIChpZCwgcm9vdF9wYXRoLCBkZWZhdWx0X2JyYW5jaCwgY3JlYXRlZF9hdCkiICsKICAgICAgICAiIFZBTFVFUyAoJGlkLCAkcnAsICRkYiwgJGNhKSIKICAgICAgKS5ydW4oeyAkaWQ6IHByZWZlcnJlZElkLCAkcnA6IHJvb3RQYXRoLCAkZGI6ICJtYWluIiwgJGNhOiBEYXRlLm5vdygpIH0pOwogICAgfSBjYXRjaCAoZSkgeyB9CiAgICBmb3VuZCA9IGRzdAogICAgICAucXVlcnkoIlNFTEVDVCBpZCBGUk9NIHByb2plY3RzIFdIRVJFIHJvb3RfcGF0aCA9ICRwIikKICAgICAgLmdldCh7ICRwOiByb290UGF0aCB9KTsKICAgIGlmIChmb3VuZCkgcmV0dXJuIGZvdW5kLmlkOwogIH0KICB2YXIgbmlkID0gY3J5cHRvLnJhbmRvbVVVSUQoKTsKICBkc3QucXVlcnkoCiAgICAiSU5TRVJUIElOVE8gcHJvamVjdHMgKGlkLCByb290X3BhdGgsIGRlZmF1bHRfYnJhbmNoLCBjcmVhdGVkX2F0KSIgKwogICAgIiBWQUxVRVMgKCRpZCwgJHJwLCAkZGIsICRjYSkiCiAgKS5ydW4oeyAkaWQ6IG5pZCwgJHJwOiByb290UGF0aCwgJGRiOiAibWFpbiIsICRjYTogRGF0ZS5ub3coKSB9KTsKICByZXR1cm4gbmlkOwp9CgpmdW5jdGlvbiBtZXJnZVRocmVhZHMoc3JjUGF0aCwgZHN0UGF0aCwgaWRzLCByZW5hbWVzKSB7CiAgaWYgKCFBcnJheS5pc0FycmF5KGlkcykgfHwgaWRzLmxlbmd0aCA9PT0gMCkgZGllKCLQvdC10YIg0YHQtdGB0YHQuNC5INC00LvRjyDQv9C10YDQtdC90L7RgdCwIik7CiAgaWYgKCFkc3RQYXRoIHx8IGRzdFBhdGggPT09IHNyY1BhdGgpIGRpZSgi0LHQsNC30LAt0L/RgNC40ZHQvNC90LjQuiDQvtGC0YHRg9GC0YHRgtCy0YPQtdGCINC40LvQuCDRgdC+0LLQv9Cw0LTQsNC10YIg0YEg0LjRgdGC0L7Rh9C90LjQutC+0LwiKTsKICBpZiAoIXJlbmFtZXMgfHwgdHlwZW9mIHJlbmFtZXMgIT09ICJvYmplY3QiKSByZW5hbWVzID0ge307CgogIHZhciBzcmMgPSBvcGVuUk8oc3JjUGF0aCk7CiAgdmFyIGRzdCA9IG5ldyBEYXRhYmFzZShkc3RQYXRoKTsKICB2YXIgY29waWVkID0gW107CiAgdmFyIHNraXBwZWQgPSBbXTsKICB0cnkgewogICAgdmFyIHNyY1RocmVhZENvbHMgPSB0YWJsZUNvbHMoc3JjLCAidGhyZWFkcyIpOwogICAgdmFyIGRzdFRocmVhZENvbHMgPSB0YWJsZUNvbHMoZHN0LCAidGhyZWFkcyIpOwogICAgdmFyIGRzdE1zZ0NvbHMgPSB0YWJsZUNvbHMoZHN0LCAibWVzc2FnZXMiKTsKICAgIHZhciBkc3RRdWV1ZUNvbHMgPSB0YWJsZUNvbHMoZHN0LCAicXVldWVfaXRlbXMiKTsKICAgIHZhciBkc3RSZWNlaXB0Q29scyA9IHRhYmxlQ29scyhkc3QsICJhdXRvX3J1bl9kZWNpc2lvbl9yZWNlaXB0cyIpOwogICAgdmFyIGRzdERlbGl2Q29scyA9IHRhYmxlQ29scyhkc3QsICJ0aHJlYWRfZGVsaXZlcmllcyIpOwoKICAgIHZhciBwcm9qQ2FjaGUgPSB7fTsKICAgIHZhciBkc3RUaHJlYWRTdG10ID0gbnVsbDsgLy8g0L3QsNCx0L7RgCDQutC+0LvQvtC90L7QuiDRgyDRgdGC0YDQvtC6INGA0LDQt9C90YvQuSwg0YHQvtCx0LjRgNCw0LXQvCDQv9C+0YHRgtGA0L7Rh9C90L4KCiAgICBkc3QudHJhbnNhY3Rpb24oZnVuY3Rpb24gKCkgewogICAgICBmb3IgKHZhciBpZCBvZiBpZHMpIHsKICAgICAgICB2YXIgdGggPSBzcmMKICAgICAgICAgIC5xdWVyeSgiU0VMRUNUICogRlJPTSB0aHJlYWRzIFdIRVJFIGlkID0gJGlkIikKICAgICAgICAgIC5nZXQoeyAkaWQ6IGlkIH0pOwogICAgICAgIGlmICghdGgpIHsKICAgICAgICAgIHNraXBwZWQucHVzaChpZCk7CiAgICAgICAgICBjb250aW51ZTsKICAgICAgICB9CiAgICAgICAgdmFyIGV4aXN0cyA9IGRzdAogICAgICAgICAgLnF1ZXJ5KCJTRUxFQ1QgMSBGUk9NIHRocmVhZHMgV0hFUkUgaWQgPSAkaWQiKQogICAgICAgICAgLmdldCh7ICRpZDogaWQgfSk7CiAgICAgICAgaWYgKGV4aXN0cykgewogICAgICAgICAgc2tpcHBlZC5wdXNoKGlkKTsgLy8g0LjQtNC10LzQv9C+0YLQtdC90YLQvdC+0YHRgtGMOiDRgdC10YHRgdC40Y8g0YEg0YLQtdC8INC20LUgaWQg0L3QtSDQv9C10YDQtdC30LDQv9C40YHRi9Cy0LDQtdGC0YHRjwogICAgICAgICAgY29udGludWU7CiAgICAgICAgfQoKICAgICAgICB2YXIgcm93ID0ge307CiAgICAgICAgZm9yICh2YXIgY29sIG9mIHNyY1RocmVhZENvbHMpIHJvd1tjb2xdID0gdGhbY29sXTsKCiAgICAgICAgLy8g0J7QsdC90YPQu9GP0LXQvCDQv9GA0LjQstCw0YLQvdC+0LUg0YHQvtGB0YLQvtGP0L3QuNC1INC00LLQuNC20LrQsDsg0LXRgdC70Lgg0LIg0LHQsNC30LUt0L/RgNC40ZHQvNC90LjQutC1INC90LXRgiDRgtCw0LrQuNGFCiAgICAgICAgLy8g0LrQvtC70L7QvdC+0LosIGluc2VydFJvdyDQvtGC0LHRgNC+0YHQuNGCINC40YUg0YHQsNC8LgogICAgICAgIHJvdy5wcm9qZWN0X2lkID0gZW5zdXJlUHJvamVjdChkc3QsIHRoLnByb2plY3RfcGF0aCwgdGgucHJvamVjdF9pZCk7CiAgICAgICAgcm93LnR1cm5fc3RhdGUgPSAiaWRsZSI7CiAgICAgICAgcm93LnF1ZXVlX3BhdXNlZCA9IDA7CiAgICAgICAgcm93LmF1dG9fcnVuID0gMDsKICAgICAgICByb3cuYXV0b19ydW5fc3RhcnRlZF9hdCA9IG51bGw7CiAgICAgICAgcm93LmF1dG9fcnVuX3Bhc3NfY291bnQgPSAwOwogICAgICAgIHJvdy5hdXRvX3J1bl9yZWZpbmVtZW50X2NvdW50ID0gMDsKICAgICAgICByb3cuYXV0b19ydW5fZGVjaXNpb25fY291bnQgPSAwOwogICAgICAgIHJvdy5hdXRvX3J1bl9zdG9wcGVkX25vdGUgPSBudWxsOwogICAgICAgIHJvdy5hdXRvX3J1bl9zdG9wcGVkX2F0ID0gbnVsbDsKICAgICAgICByb3cuaGFybmVzc19zdGF0ZSA9IG51bGw7CiAgICAgICAgcm93Lmhhcm5lc3Nfc3RhdGVfaWQgPSBudWxsOwogICAgICAgIHJvdy53b3JsZF9zbmFwc2hvdCA9IG51bGw7CiAgICAgICAgcm93LmZyZWVidWZmX2luc3RhbmNlX2lkID0gbnVsbDsKICAgICAgICByb3cuc3BvbnNvcmVkID0gbnVsbDsKICAgICAgICByb3cuc3BvbnNvcmVkX3J1bl90b2tlbiA9IG51bGw7CiAgICAgICAgcm93LnNwb25zb3JlZF9zZXR0bGVkX2F0ID0gbnVsbDsKICAgICAgICByb3cuc3BvbnNvcmVkX3Rlcm1pbmFsX3JlcG9ydHMgPSBudWxsOwogICAgICAgIHJvdy5zcG9uc29yZWRfdGVybWluYWxfYWNrX2F0ID0gbnVsbDsKICAgICAgICByb3cucGVuZGluZ19icmllZnMgPSBudWxsOwogICAgICAgIHJvdy5wZW5kaW5nX2JyaWVmc19kaWFnbm9zdGljX2tleSA9IG51bGw7CiAgICAgICAgcm93LmF0dGVudGlvbl9hY2tub3dsZWRnZWRfcmV2aXNpb24gPSByb3cuYXR0ZW50aW9uX3JldmlzaW9uIHx8IDA7CiAgICAgICAgcm93LmF0dGVudGlvbl9yZWFzb24gPSBudWxsOwogICAgICAgIHJvdy5hdHRlbnRpb25fYXQgPSBudWxsOwogICAgICAgIHJvdy5sYXN0X3R1cm5fb3V0Y29tZSA9IG51bGw7CiAgICAgICAgaWYgKHJlbmFtZXNbaWRdKSByb3cudGl0bGUgPSBTdHJpbmcocmVuYW1lc1tpZF0pLnNsaWNlKDAsIDIwMCk7CiAgICAgICAgcm93LnVwZGF0ZWRfYXQgPSBEYXRlLm5vdygpOwoKICAgICAgICBpbnNlcnRSb3coZHN0LCAidGhyZWFkcyIsIHJvdywgZHN0VGhyZWFkQ29scyk7CiAgICAgICAgY29waWVkLnB1c2goaWQpOwoKICAgICAgICBmb3IgKHZhciBtIG9mIHNyYwogICAgICAgICAgLnF1ZXJ5KCJTRUxFQ1QgKiBGUk9NIG1lc3NhZ2VzIFdIRVJFIHRocmVhZF9pZCA9ICRpZCBPUkRFUiBCWSBzZXEiKQogICAgICAgICAgLmFsbCh7ICRpZDogaWQgfSkpIHsKICAgICAgICAgIGluc2VydFJvdyhkc3QsICJtZXNzYWdlcyIsIG0sIGRzdE1zZ0NvbHMpOwogICAgICAgIH0KCiAgICAgICAgZm9yICh2YXIgcWkgb2Ygc3JjCiAgICAgICAgICAucXVlcnkoIlNFTEVDVCAqIEZST00gcXVldWVfaXRlbXMgV0hFUkUgdGhyZWFkX2lkID0gJGlkIikKICAgICAgICAgIC5hbGwoeyAkaWQ6IGlkIH0pKSB7CiAgICAgICAgICB2YXIgc3QgPSBTdHJpbmcocWkuc3RhdGUgfHwgIiIpLnRvTG93ZXJDYXNlKCk7CiAgICAgICAgICBpZiAoc3QgPT09ICJydW5uaW5nIiB8fCBzdCA9PT0gImNsYWltZWQiKSBjb250aW51ZTsgLy8g0L7RgdGC0LDRgtC60LggcnVudGltZSDQvtGCINC/0YDQvtGI0LvQvtCz0L4g0LDQutC60LDRg9C90YLQsAogICAgICAgICAgaW5zZXJ0Um93KGRzdCwgInF1ZXVlX2l0ZW1zIiwgcWksIGRzdFF1ZXVlQ29scyk7CiAgICAgICAgfQoKICAgICAgICBmb3IgKHZhciByYyBvZiBzcmMKICAgICAgICAgIC5xdWVyeSgKICAgICAgICAgICAgIlNFTEVDVCAqIEZST00gYXV0b19ydW5fZGVjaXNpb25fcmVjZWlwdHMgV0hFUkUgdGhyZWFkX2lkID0gJGlkIgogICAgICAgICAgKQogICAgICAgICAgLmFsbCh7ICRpZDogaWQgfSkpIHsKICAgICAgICAgIGluc2VydFJvdyhkc3QsICJhdXRvX3J1bl9kZWNpc2lvbl9yZWNlaXB0cyIsIHJjLCBkc3RSZWNlaXB0Q29scyk7CiAgICAgICAgfQoKICAgICAgICBmb3IgKHZhciBkdiBvZiBzcmMKICAgICAgICAgIC5xdWVyeSgiU0VMRUNUICogRlJPTSB0aHJlYWRfZGVsaXZlcmllcyBXSEVSRSB0aHJlYWRfaWQgPSAkaWQiKQogICAgICAgICAgLmFsbCh7ICRpZDogaWQgfSkpIHsKICAgICAgICAgIGluc2VydFJvdyhkc3QsICJ0aHJlYWRfZGVsaXZlcmllcyIsIGR2LCBkc3REZWxpdkNvbHMpOwogICAgICAgIH0KICAgICAgfQogICAgfSkoKTsKICB9IGZpbmFsbHkgewogICAgdHJ5IHsgc3JjLmNsb3NlKCk7IH0gY2F0Y2ggKGUpIHsgfQogICAgdHJ5IHsgZHN0LmNsb3NlKCk7IH0gY2F0Y2ggKGUpIHsgfQogIH0KICBvdXQoeyBvazogdHJ1ZSwgYWN0aW9uOiAibWVyZ2UiLCBjb3BpZWQ6IGNvcGllZCwgc2tpcHBlZDogc2tpcHBlZCB9KTsKfQoKdHJ5IHsKICB2YXIgbW9kZSA9IHByb2Nlc3MuYXJndlsyXTsKICBpZiAobW9kZSA9PT0gImxpc3QiKSB7CiAgICBpZiAoIXByb2Nlc3MuYXJndlszXSkgZGllKCLQvdC1INGD0LrQsNC30LDQvSDQv9GD0YLRjCDQuiDQsdCw0LfQtS3QuNGB0YLQvtGH0L3QuNC60YMiKTsKICAgIGxpc3RUaHJlYWRzKHByb2Nlc3MuYXJndlszXSk7CiAgfSBlbHNlIGlmIChtb2RlID09PSAibWVyZ2UiKSB7CiAgICBpZiAoIXByb2Nlc3MuYXJndlszXSB8fCAhcHJvY2Vzcy5hcmd2WzRdKSBkaWUoItC90LUg0YPQutCw0LfQsNC90Ysg0L/Rg9GC0Lgg0Log0LHQsNC30LUt0LjRgdGC0L7Rh9C90LjQutGDINC4INCx0LDQt9C1LdC/0YDQuNGR0LzQvdC40LrRgyIpOwogICAgbWVyZ2VUaHJlYWRzKAogICAgICBwcm9jZXNzLmFyZ3ZbM10sCiAgICAgIHByb2Nlc3MuYXJndls0XSwKICAgICAgYXJnSnNvbig1LCBbXSksCiAgICAgIGFyZ0pzb24oNiwge30pCiAgICApOwogIH0gZWxzZSB7CiAgICBkaWUoInVua25vd24gbW9kZTogIiArIG1vZGUpOwogIH0KfSBjYXRjaCAoZSkgewogIGRpZShlICYmIGUubWVzc2FnZSA/IGUubWVzc2FnZSA6IFN0cmluZyhlKSk7Cn0K";

        private const int StopGraceMs = 2000;

        private const int PackStagingWaitMs = 15000;

        private const string PackReleasesApiUrl = "https://api.github.com/repos/Ximmmmmmm/freebuff-zh/releases/latest";

        private const string SelfReleasesApiUrl = "https://api.github.com/repos/Dima-Sor/freebuff-controller/releases/latest";

        private const string SelfReleasesPageUrl = "https://github.com/Dima-Sor/freebuff-controller/releases/latest";

        private static readonly string FreebuffExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs\\@codebufffreebuff-desktop\\Freebuff.exe");

        private static readonly string FreebuffInstallDir = Path.GetDirectoryName(FreebuffExe);

        private static readonly string DefaultState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config\\freebuff-desktop\\state.json");

        private static readonly Regex SlotRegex = new Regex("Freebuff-slot-(\\d)(?!\\d)");

        private static readonly Regex EmailRegex = new Regex("\"email\"\\s*:\\s*\"([^\"]+)\"");

        private static readonly Regex FeedUrlRegex = new Regex("(?m)^\\s*url:\\s*(\\S+)");

        private static readonly Regex YamlVersionRegex = new Regex("(?m)^\\s*version:\\s*'?([^'\"\\r\\n]+)");

        private static readonly Regex YamlPathRegex = new Regex("(?m)^\\s*path:\\s*(\\S+)");

        private static readonly Regex YamlShaRegex = new Regex("(?m)^\\s*sha512:\\s*(\\S+)");

        private static readonly Regex LooseVersionRegex = new Regex("(\\d+)\\.(\\d+)(?:\\.(\\d+))?(?:\\.(\\d+))?");

        private static readonly Color ColBg = Color.FromArgb(255, 255, 255);

        private static readonly Color ColPanel = Color.FromArgb(246, 247, 250);

        private static readonly Color ColRow = Color.FromArgb(255, 255, 255);

        private static readonly Color ColLine = Color.FromArgb(230, 233, 238);

        private static readonly Color ColText = Color.FromArgb(23, 27, 33);

        private static readonly Color ColSub = Color.FromArgb(124, 134, 150);

        private static readonly Color ColAccent = Color.FromArgb(47, 111, 237);

        private static readonly Color ColAccentHover = Color.FromArgb(71, 130, 245);

        private static readonly Color ColNeutral = Color.FromArgb(238, 240, 244);

        private static readonly Color ColNeutralHover = Color.FromArgb(228, 231, 237);

        private static readonly Color ColGreen = Color.FromArgb(21, 158, 73);

        private static readonly Color ColHeader = Color.FromArgb(246, 247, 249);

        private static readonly Color ColSelect = Color.FromArgb(227, 236, 251);

        private static readonly Color ColHover = Color.FromArgb(240, 245, 252);

        private DataGridView grid;

        private NotifyIcon tray;

        private Label statusLabel;

        private System.Windows.Forms.Timer statusRevertTimer;

        private System.Windows.Forms.Timer fadeTimer;

        private System.Windows.Forms.Timer statusFadeTimer;

        private System.Windows.Forms.Timer statusBreatheTimer;

        private bool breatheUp;

        private float breathePhase;

        private System.Windows.Forms.Timer refreshTimer;

        private System.Windows.Forms.Timer quotaTimer;

        private System.Windows.Forms.Timer versionTimer;

        private System.Windows.Forms.Timer proxyTimer;

        private int refreshBusy;

        private int quotaBusy;

        private DateTime lastQuotaFetch = DateTime.MinValue;

        private readonly QuotaInfo[] quotaInfos = new QuotaInfo[10];

        private static readonly Color ColNewVersion = Color.FromArgb(178, 124, 8);

        private static readonly Color ColNewVersionHover = Color.FromArgb(198, 143, 28);

        private bool statusClickable;

        private Label selfLink;

        private Label hintLabel;

        private Label proxyLink;

        private ToolTip proxyTip;

        private Color proxyColor = ColSub;

        private int proxyStatusBusy;


        private Label deleteLink;

        private ToolTip deleteTip;


        private string installedVersion;

        private string latestVersion;

        private int versionCheckBusy;

        private int updateBusy;

        private bool updateStarted;

        private bool updateFailed;

        private static readonly string FreebuffResources = Path.Combine(Path.GetDirectoryName(FreebuffExe), "resources");

        private static readonly string InstalledUiDir = Path.Combine(FreebuffResources, "orchestrator\\ui");

        private static readonly string InstalledUiIndex = Path.Combine(InstalledUiDir, "index.html");

        private static readonly Regex ManifestVersionRegex = new Regex("\"targetVersion\"\\s*:\\s*\"([^\"]+)\"");

        private static readonly string HanhuaConfigFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreebuffController\\hanhua-path.txt");

        private static readonly string WindowPosFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreebuffController\\window-pos.txt");

        private Label hanhuaLabel;

        private string hanhuaDir;

        private string hanhuaRecheckVersion;

        private string hanhuaBuildStamp;

        private DateTime hanhuaBuildStableAt;

        private string hanhuaBuildHandled;

        private string brokenLoggedStamp;

        private static int hanhuaBusy;

        private string hanhuaPendingWhy;

        private bool hanhuaForcePending;


        private DateTime hanhuaRetryAt;

        private static readonly string[] LegacyHanhuaPrefFiles = new string[2]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreebuffController\\hanhua-english.txt"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreebuffController\\hanhua-auto.txt")
        };

        private DateTime showFailNotifiedAt = DateTime.MinValue;

        private static readonly int[] AutoDetectPorts = new int[5] { 7890, 7897, 10808, 10809, 1080 };

        private static readonly string LocalProxyConfigFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreebuffController\\proxy.txt");

        private static string localProxyMode = "auto";

        private static string manualProxyUrl;

        private static string detectedProxyUrl;

        private static string stickyRoute;

        private static string lastRouteText = "";

        private static int detectBusy;

        private static readonly Dictionary<int, string> launchProxyBySlot = new Dictionary<int, string>();

        private static readonly object launchProxyLock = new object();

        private static readonly HashSet<int> launchProxyNotified = new HashSet<int>();

        private static bool manualProxyNotified;

        private static int proxyWatchBusy;

        private static readonly string PendingInstallerFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreebuffController\\pending-installer.txt");

        private DateTime agentsMdRetryAt = DateTime.MinValue;

        internal static int LastJunctionError;

        internal static string LastJunctionDetail;

        private int pendingLaunchAfterShare = -1;

        private static int packStaging;

        private static readonly Regex PackMarkerRegex = new Regex("<meta name=\"hanhua-pack\" content=\"([^\"]+)\"");

        private int packBusy;

        private int selfUpdateBusy;

        private string selfLatestVersion;

        private bool selfDownloaded;

        private bool selfFailed;

        private static readonly Regex UiAssetRefRegex = new Regex("(?:src|href)=\"\\./(assets/[^\"]+)\"");

        internal static bool AgentsMdPending;

        private readonly List<Action> hanhuaWaiters = new List<Action>();

        private static readonly string UpdaterCacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "@codebufffreebuff-desktop-updater");

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        //  окно ,  не  система : 20(DWMWA_USE_IMMERSIVE_DARK_MODE)
        //  0  только  это ' не ',  система  это  Название (Win11 26200 );
        //  поэтому  35(DWMWA_CAPTION_COLOR) + 36(DWMWA_TEXT_COLOR). 
        // Win10  не  35/36  будет  ошибка ,  только ,  это  Название . 
        private static void ApplyLightTitleBar(IntPtr hwnd)
        {
            try
            {
                int value = 0;
                DwmSetWindowAttribute(hwnd, 20, ref value, 4);
                int value2 = 16777215;
                DwmSetWindowAttribute(hwnd, 35, ref value2, 4);
                int value3 = 2169623;
                DwmSetWindowAttribute(hwnd, 36, ref value3, 4);
            }
            catch
            {
            }
        }

        private static void LogFail(string what)
        {
            Program.LogFail(what, null);
        }

        private static void LogFail(string what, Exception ex)
        {
            Program.LogFail(what, ex);
        }

        public MainForm()
        {
            if (!File.Exists(FreebuffExe))
            {
                throw new ApplicationException(" Freebuff Desktop не найден:\n " + FreebuffExe + "\n\n сначала  Freebuff. ");
            }
            installedVersion = ReadInstalledVersion();
            hanhuaRecheckVersion = installedVersion;
            BuildUi();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyLightTitleBar(base.Handle);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SaveWindowPos();
            if (statusRevertTimer != null)
            {
                statusRevertTimer.Dispose();
            }
            if (fadeTimer != null)
            {
                fadeTimer.Dispose();
            }
            if (statusFadeTimer != null)
            {
                statusFadeTimer.Dispose();
            }
            if (statusBreatheTimer != null)
            {
                statusBreatheTimer.Dispose();
            }
            if (refreshTimer != null)
            {
                refreshTimer.Dispose();
            }
            if (quotaTimer != null)
            {
                quotaTimer.Dispose();
            }
            if (versionTimer != null)
            {
                versionTimer.Dispose();
            }
            if (proxyTimer != null)
            {
                proxyTimer.Dispose();
            }
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosed(e);
        }

        private void BuildUi()
        {
            Text = " Freebuff Controller v " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            base.ClientSize = new Size(660, 568);
            BackColor = ColBg;
            ForeColor = ColText;
            Font = new Font("Segoe UI", 9f);
            base.FormBorderStyle = FormBorderStyle.FixedSingle;
            base.MaximizeBox = false;
            Point startLoc;
            if (LoadWindowPos(out startLoc))
            {
                base.StartPosition = FormStartPosition.Manual;
                base.Location = startLoc;
            }
            else
            {
                base.StartPosition = FormStartPosition.CenterScreen;
            }
            base.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            hintLabel = new Label();
            hintLabel.AutoSize = false;
            hintLabel.Text = " Двойной клик - запуск ";
            hintLabel.Bounds = new Rectangle(20, 14, 76, 20);
            hintLabel.ForeColor = ColSub;
            base.Controls.Add(hintLabel);
            //  прокси  Статус  не  окно : Статус  и  порт  все  в ' Прокси ' в , 
            // / восстановить . proxyLink  только  фон  Статус ( не  интерфейс ). 
            proxyLink = new Label();
            proxyTip = new ToolTip();
            deleteLink = MakeLink(" Удалить сессии ", 486, 72, delegate
            {
                OpenDeleteThreads();
            });
            deleteTip = new ToolTip();
            deleteTip.SetToolTip(deleteLink, " Удалить сессии и историю навсегда ");
            MakeLink(" Прокси ", 568, 72, delegate
            {
                OpenProxySettings();
            });
            selfLink = new Label();
            selfLink.AutoSize = false;
            selfLink.Text = " Есть новый контроллер · обновить ";
            selfLink.Bounds = new Rectangle(20, 14, 256, 20);
            selfLink.TextAlign = ContentAlignment.MiddleRight;
            selfLink.ForeColor = ColNewVersion;
            selfLink.Cursor = Cursors.Hand;
            selfLink.MouseEnter += delegate
            {
                selfLink.ForeColor = ColNewVersionHover;
            };
            selfLink.MouseDown += delegate
            {
                selfLink.ForeColor = ColNewVersionHover;
            };
            selfLink.MouseLeave += delegate
            {
                selfLink.ForeColor = ColNewVersion;
            };
            selfLink.Visible = false;
            selfLink.Click += delegate
            {
                OnSelfUpdateClick();
            };
            base.Controls.Add(selfLink);
            BuildGrid();
            Button button = MakeButton(" Запуск ", 20, 496, 120, ColAccent, ColAccentHover);
            button.Click += delegate
            {
                DisableBriefly(button, 3000);
                OnLaunch();
            };
            Button button2 = MakeButton(" Стоп ", 180, 496, 120, ColNeutral, ColNeutralHover);
            button2.Click += delegate
            {
                DisableBriefly(button2, 2500);
                OnStop();
            };
            Button button3 = MakeButton(" Сбросить аккаунт ", 340, 496, 120, ColNeutral, ColNeutralHover);
            button3.Click += delegate
            {
                DisableBriefly(button3, 3000);
                OnReset();
            };
            Button button4 = MakeButton(" Стоп все ", 500, 496, 120, ColNeutral, ColNeutralHover);
            button4.Click += delegate
            {
                DisableBriefly(button4, 3000);
                OnStopAll();
            };
            hanhuaLabel = new Label();
            hanhuaLabel.AutoSize = false;
            hanhuaLabel.Bounds = new Rectangle(20, 542, 200, 18);
            hanhuaLabel.ForeColor = ColSub;
            hanhuaLabel.Font = new Font("Segoe UI", 8.5f);
            BuildTray();
            statusLabel = new Label();
            statusLabel.AutoSize = false;
            statusLabel.Text = ReadyStatus();
            statusLabel.Bounds = new Rectangle(20, 542, 620, 18);
            statusLabel.ForeColor = ColText;
            statusLabel.Font = new Font("Segoe UI", 9f);
            statusLabel.AutoEllipsis = true;
            statusLabel.Cursor = Cursors.Default;
            statusLabel.Click += delegate
            {
                if (statusClickable)
                {
                    OnVersionLinkClick();
                }
            };
            base.Controls.Add(statusLabel);
            float num = DpiScale();
            ScaleUi(this, num);
            try
            {
                Font font = tray.ContextMenuStrip.Font;
                tray.ContextMenuStrip.Font = new Font(font.FontFamily, font.Size * num, font.Style);
            }
            catch
            {
            }
            hanhuaDir = FindHanhuaDir();
            RefreshHanhuaUi();
            refreshTimer = new System.Windows.Forms.Timer();
            refreshTimer.Interval = 3000;
            refreshTimer.Tick += delegate
            {
                RefreshGrid();
            };
            refreshTimer.Start();
            quotaTimer = new System.Windows.Forms.Timer();
            quotaTimer.Interval = 300000;
            quotaTimer.Tick += delegate
            {
                FetchQuotasAsync(false);
            };
            quotaTimer.Start();
            versionTimer = new System.Windows.Forms.Timer();
            versionTimer.Interval = 1800000;
            versionTimer.Tick += delegate
            {
                RefreshInstalledVersion();
                CheckVersionAsync();
                CheckPackUpdateAsync();
                CheckSelfUpdateAsync();
                DetectProxyAsync();
                RefreshHanhuaUi();
                AutoCleanUnusedFiles(" Проверка периода ");
            };
            versionTimer.Start();
            proxyTimer = new System.Windows.Forms.Timer();
            proxyTimer.Interval = 60000;
            proxyTimer.Tick += delegate
            {
                DetectProxyAsync();
                WatchProxyHealth();
                RefreshProxyStatusAsync();
            };
            proxyTimer.Start();
            ComputeAndApply();
            ShowSelfUpdateNotice();
            FetchQuotasAsync(true, false);
            DetectProxyAsync();
            CheckVersionAsync();
            StartAutoRestoreHanhua(" Старт контроллера ", null);
            CheckPackUpdateAsync();
            CheckSelfUpdateAsync();
        }

        private static string ReadyStatus()
        {
            return "";
        }

        private void SetTrayTip(string text)
        {
            if (tray == null)
            {
                return;
            }
            string text2 = (string.IsNullOrEmpty(text) ? " Freebuff Controller " : (" Freebuff Controller ·  " + text));
            if (text2.Length > 63)
            {
                text2 = text2.Substring(0, 60) + "...";
            }
            try
            {
                tray.Text = text2;
            }
            catch
            {
            }
        }

        private void TrayNotify(string text)
        {
            if (tray == null || string.IsNullOrEmpty(text))
            {
                return;
            }
            try
            {
                tray.ShowBalloonTip(6000, " Freebuff Controller ", text, ToolTipIcon.Info);
            }
            catch
            {
            }
        }

        private void SetHanhuaText(string text)
        {
            if (hanhuaLabel != null)
            {
                hanhuaLabel.Text = text;
            }
        }

        // (t=0  a, t=1  b)-- /  /  Всего . 
        private static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)Math.Round((double)(int)a.R + (double)((int)b.R - (int)a.R) * t), (int)Math.Round((double)(int)a.G + (double)((int)b.G - (int)a.G) * t), (int)Math.Round((double)(int)a.B + (double)((int)b.B - (int)a.B) * t));
        }

        // A1  окно : показать  /  все ( 150ms),  не . 
        private void FadeInWindow()
        {
            if (fadeTimer != null)
            {
                fadeTimer.Stop();
                fadeTimer.Dispose();
            }
            base.Opacity = 0.0;
            fadeTimer = new System.Windows.Forms.Timer();
            fadeTimer.Interval = 12;
            fadeTimer.Tick += delegate
            {
                if (base.IsDisposed || fadeTimer == null)
                {
                    return;
                }
                double num = base.Opacity + 0.12;
                if (num >= 1.0)
                {
                    base.Opacity = 1.0;
                    fadeTimer.Stop();
                    fadeTimer.Dispose();
                    fadeTimer = null;
                }
                else
                {
                    base.Opacity = num;
                }
            };
            fadeTimer.Start();
        }

        // A2  Статус :, ,  не . 
        private void FadeStatusColor(Color target, int ms, Action done)
        {
            if (statusFadeTimer != null)
            {
                statusFadeTimer.Stop();
                statusFadeTimer.Dispose();
                statusFadeTimer = null;
            }
            Color start = statusLabel.ForeColor;
            int steps = Math.Max(2, ms / 15);
            int i = 0;
            statusFadeTimer = new System.Windows.Forms.Timer();
            statusFadeTimer.Interval = 15;
            statusFadeTimer.Tick += delegate
            {
                if (base.IsDisposed || statusLabel == null || statusFadeTimer == null)
                {
                    return;
                }
                i++;
                float num = (float)i / (float)steps;
                if (num >= 1f)
                {
                    statusLabel.ForeColor = target;
                    statusFadeTimer.Stop();
                    statusFadeTimer.Dispose();
                    statusFadeTimer = null;
                    if (done != null)
                    {
                        done();
                    }
                }
                else
                {
                    statusLabel.ForeColor = Mix(start, target, num);
                }
            };
            statusFadeTimer.Start();
        }

        // A5 ' новая версия ':, . 
        private void StartStatusBreathing()
        {
            StopStatusBreathing();
            if (base.IsDisposed || statusLabel == null)
            {
                return;
            }
            breatheUp = true;
            breathePhase = 0f;
            statusBreatheTimer = new System.Windows.Forms.Timer();
            statusBreatheTimer.Interval = 90;
            statusBreatheTimer.Tick += delegate
            {
                if (base.IsDisposed || statusLabel == null || !statusClickable || statusBreatheTimer == null)
                {
                    StopStatusBreathing();
                    return;
                }
                breathePhase += (breatheUp ? 0.08f : -0.08f);
                if (breathePhase >= 1f)
                {
                    breathePhase = 1f;
                    breatheUp = false;
                }
                else if (breathePhase <= 0f)
                {
                    breathePhase = 0f;
                    breatheUp = true;
                }
                statusLabel.ForeColor = Mix(ColNewVersion, ColNewVersionHover, breathePhase);
            };
            statusBreatheTimer.Start();
        }

        private void StopStatusBreathing()
        {
            if (statusBreatheTimer != null)
            {
                statusBreatheTimer.Stop();
                statusBreatheTimer.Dispose();
                statusBreatheTimer = null;
            }
        }

        // A6  Лимит :, . 
        private void FlashCell(DataGridViewCell cell, Color backTo)
        {
            cell.Style.ForeColor = Mix(backTo, ColText, 0.55f);
            Delay(700, delegate
            {
                try
                {
                    cell.Style.ForeColor = backTo;
                }
                catch
                {
                }
            });
        }

        private void SetStatus(string text, Color? tint = null)
        {
            SetTrayTip(text);
            if (statusLabel == null)
            {
                return;
            }
            StopStatusBreathing();
            statusClickable = false;
            statusLabel.Cursor = Cursors.Default;
            bool wasIdle = statusLabel.Text == ReadyStatus();
            Color color = tint ?? ColText;
            statusLabel.Text = text;
            if (text == ReadyStatus())
            {
                if (statusRevertTimer != null)
                {
                    statusRevertTimer.Stop();
                }
                return;
            }
            if (wasIdle)
            {
                statusLabel.ForeColor = Mix(color, ColBg, 0.75f);
                FadeStatusColor(color, 180, null);
            }
            else
            {
                statusLabel.ForeColor = color;
            }
            if (statusRevertTimer == null)
            {
                statusRevertTimer = new System.Windows.Forms.Timer();
                statusRevertTimer.Interval = 8000;
                statusRevertTimer.Tick += delegate
                {
                    statusRevertTimer.Stop();
                    StopStatusBreathing();
                    SetTrayTip(ReadyStatus());
                    if (!base.IsDisposed && statusLabel != null)
                    {
                        FadeStatusColor(ColBg, 260, delegate
                        {
                            if (statusLabel != null)
                            {
                                statusLabel.Text = ReadyStatus();
                                statusLabel.ForeColor = ColText;
                            }
                        });
                    }
                };
            }
            statusRevertTimer.Stop();
            statusRevertTimer.Start();
        }

        //  Статус ( новая версия  /  Скачать  ошибка ): OnVersionLinkClick. 
        private void SetStatusAction(string text, Color? tint = null)
        {
            SetStatus(text, tint);
            if (statusLabel == null)
            {
                return;
            }
            statusClickable = true;
            statusLabel.Cursor = Cursors.Hand;
            if (tint != null && tint.Value.ToArgb() == ColNewVersion.ToArgb())
            {
                Delay(220, StartStatusBreathing);
            }
        }

        private void ShowStatusAfterIdle(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            Delay(9000, delegate
            {
                if (!base.IsDisposed && statusLabel != null && !(statusLabel.Text != ReadyStatus()))
                {
                    SetStatus(text);
                }
            });
        }

        private void BuildGrid()
        {
            grid = new DataGridView();
            grid.Location = new Point(20, 44);
            grid.Size = new Size(620, 444);
            grid.ScrollBars = ScrollBars.None;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AllowUserToOrderColumns = false;
            grid.AllowUserToResizeColumns = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.GridColor = ColLine;
            grid.BackgroundColor = ColRow;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 38;
            DataGridViewCellStyle columnHeadersDefaultCellStyle = grid.ColumnHeadersDefaultCellStyle;
            columnHeadersDefaultCellStyle.BackColor = ColHeader;
            columnHeadersDefaultCellStyle.ForeColor = ColSub;
            columnHeadersDefaultCellStyle.SelectionBackColor = ColHeader;
            columnHeadersDefaultCellStyle.SelectionForeColor = ColSub;
            columnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9f);
            columnHeadersDefaultCellStyle.Padding = new Padding(10, 0, 0, 0);
            DataGridViewCellStyle defaultCellStyle = grid.DefaultCellStyle;
            defaultCellStyle.BackColor = ColRow;
            defaultCellStyle.ForeColor = ColText;
            defaultCellStyle.SelectionBackColor = ColSelect;
            defaultCellStyle.SelectionForeColor = ColText;
            defaultCellStyle.Font = new Font("Segoe UI", 9f);
            defaultCellStyle.Padding = new Padding(0, 1, 0, 2);
            defaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            defaultCellStyle.WrapMode = DataGridViewTriState.False;
            grid.RowTemplate.Height = 40;
            string[] array = new string[4] { " Слот ", " Статус ", " Аккаунт ", " Лимит " };
            int[] array2 = new int[4] { 20, 20, 30, 30 };
            for (int i = 0; i < array.Length; i++)
            {
                int index = grid.Columns.Add("c" + i, array[i]);
                grid.Columns[index].FillWeight = array2[i];
                grid.Columns[index].SortMode = DataGridViewColumnSortMode.NotSortable;
                grid.Columns[index].DefaultCellStyle.Padding = new Padding(12, 1, 0, 2);
            }
            try
            {
                grid.Columns[3].MinimumWidth = 170;
            }
            catch
            {
            }
            for (int j = 0; j <= 9; j++)
            {
                string text = ((j == 0) ? " Главная " : (" Слот  " + j));
                grid.Rows.Add(text, "...", "...", "...");
            }
            for (int k = 0; k < grid.Columns.Count; k++)
            {
                grid.Rows[9].Cells[k].Style.Padding = new Padding(12, 1, 0, 10);
            }
            grid.ClearSelection();
            grid.CurrentCell = null;
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0)
                {
                    LaunchIndex(e.RowIndex);
                }
            };
            grid.CellMouseEnter += delegate(object s, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0 && e.RowIndex < grid.Rows.Count)
                {
                    grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = ColHover;
                }
            };
            grid.CellMouseLeave += delegate(object s, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0 && e.RowIndex < grid.Rows.Count)
                {
                    grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.Empty;
                }
            };
            base.Controls.Add(grid);
        }

        // :, /. 
        private static Color BestTextOn(Color back)
        {
            double num = (0.299 * (double)(int)back.R + 0.587 * (double)(int)back.G + 0.114 * (double)(int)back.B) / 255.0;
            return (num > 0.6) ? ColText : Color.White;
        }

        // B8 : после  сначала ( авто ), 
        //  не ' Запуск ' не  будет  шт.  Инстанс . 
        private void DisableBriefly(Button b, int ms)
        {
            b.Enabled = false;
            Delay(ms, delegate
            {
                try
                {
                    b.Enabled = true;
                }
                catch
                {
                }
            });
        }

        // B9  окно : в , ; не  в . 
        private static bool LoadWindowPos(out Point p)
        {
            p = Point.Empty;
            try
            {
                if (!File.Exists(WindowPosFile))
                {
                    return false;
                }
                string[] array = File.ReadAllText(WindowPosFile).Trim().Split(',');
                int x;
                int y;
                if (array.Length < 2 || !int.TryParse(array[0], out x) || !int.TryParse(array[1], out y))
                {
                    return false;
                }
                Point point = new Point(x, y);
                if (!SystemInformation.VirtualScreen.Contains(point))
                {
                    return false;
                }
                p = point;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void SaveWindowPos()
        {
            try
            {
                if (base.WindowState == FormWindowState.Normal)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(WindowPosFile));
                    File.WriteAllText(WindowPosFile, base.Location.X + "," + base.Location.Y);
                }
            }
            catch
            {
            }
        }

        private Button MakeButton(string text, int x, int y, int width, Color back, Color hover)
        {
            RoundButton roundButton = new RoundButton();
            roundButton.Text = text;
            roundButton.Bounds = new Rectangle(x, y, width, 36);
            roundButton.BackColor = back;
            roundButton.HoverBack = hover;
            roundButton.ForeColor = BestTextOn(back);
            roundButton.Font = new Font("Segoe UI", 9f);
            roundButton.Cursor = Cursors.Hand;
            base.Controls.Add(roundButton);
            return roundButton;
        }

        private Label MakeLink(string text, int x, int width, EventHandler onClick)
        {
            Label l = new Label();
            l.AutoSize = false;
            l.Text = text;
            l.Bounds = new Rectangle(x, 14, width, 20);
            l.TextAlign = ContentAlignment.MiddleRight;
            l.ForeColor = ColAccent;
            l.Cursor = Cursors.Hand;
            l.MouseEnter += delegate
            {
                l.ForeColor = ColAccentHover;
            };
            l.MouseDown += delegate
            {
                l.ForeColor = ColAccentHover;
            };
            l.MouseLeave += delegate
            {
                l.ForeColor = ColAccent;
            };
            l.Click += onClick;
            base.Controls.Add(l);
            return l;
        }

        private void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            tray.Text = " Freebuff Controller ";
            tray.Visible = true;
            ContextMenuStrip contextMenuStrip = new ContextMenuStrip();
            contextMenuStrip.Items.Add(" Выход ", null, delegate
            {
                Close();
            });
            tray.ContextMenuStrip = contextMenuStrip;
            tray.DoubleClick += delegate
            {
                ShowUp();
            };
        }

        private void ShowUp()
        {
            Show();
            FadeInWindow();
            base.WindowState = FormWindowState.Normal;
            Activate();
            bool flag = false;
            try
            {
                flag = Program.SetForegroundWindow(base.Handle);
            }
            catch
            {
            }
            if (!flag)
            {
                try
                {
                    Program.SwitchToThisWindow(base.Handle, true);
                }
                catch
                {
                }
                flag = Program.GetForegroundWindow() == base.Handle;
            }
            if (!flag)
            {
                flag = Program.ForceForeground(base.Handle);
            }
            if (!flag)
            {
                NotifyShowUpFailed();
            }
        }

        private void NotifyShowUpFailed()
        {
            if (!((DateTime.Now - showFailNotifiedAt).TotalSeconds < 60.0))
            {
                showFailNotifiedAt = DateTime.Now;
                LogFail("ShowUp: окно  восстановлено  но  не  передний план ( передний план )   в  передний план  это  " + Program.DescribeForegroundWindow());
                TrayNotify(" контроллер  окно  восстановлено ,  но  не смог -- в  панель задач ( Alt+Tab  или ). ");
            }
        }

        internal void ShowFromSecondLaunch()
        {
            if (base.IsDisposed || !base.IsHandleCreated)
            {
                return;
            }
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    ShowUp();
                });
                try
                {
                    Program.AckSignal.Set();
                }
                catch
                {
                }
            }
            catch
            {
            }
        }

        private void OpenProxySettings()
        {
            DetectProxyAsync();
            bool changed;
            using (ProxySettingsDialog proxySettingsDialog = new ProxySettingsDialog())
            {
                proxySettingsDialog.ShowDialog(this);
                changed = proxySettingsDialog.Changed;
            }
            if (changed)
            {
                SetStatus(" Прокси сохранён и применён ", ColGreen);
                DetectProxyAsync();
                RefreshProxyStatusAsync();
            }
        }

        private static float DpiScale()
        {
            try
            {
                using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
                {
                    return graphics.DpiX / 96f;
                }
            }
            catch
            {
                return 1f;
            }
        }

        private static void ScaleUi(Form f, float s)
        {
            if (s < 1.01f)
            {
                return;
            }
            f.ClientSize = new Size((int)Math.Round((float)f.ClientSize.Width * s), (int)Math.Round((float)f.ClientSize.Height * s));
            foreach (Control control in f.Controls)
            {
                ScaleControlTree(control, s);
            }
        }

        private static void ScaleControlTree(Control c, float s)
        {
            c.Bounds = new Rectangle((int)Math.Round((float)c.Left * s), (int)Math.Round((float)c.Top * s), (int)Math.Round((float)c.Width * s), (int)Math.Round((float)c.Height * s));
            if (c.Font != null)
            {
                c.Font = new Font(c.Font.FontFamily, c.Font.Size * s, c.Font.Style);
            }
            RoundButton roundButton = c as RoundButton;
            if (roundButton != null)
            {
                roundButton.Radius = Math.Max(2, (int)Math.Round(10f * s));
            }
            DataGridView dataGridView = c as DataGridView;
            if (dataGridView != null)
            {
                int num = (int)Math.Round(38f * s);
                int num2 = (int)Math.Round(40f * s);
                dataGridView.ColumnHeadersHeight = num;
                dataGridView.RowTemplate.Height = num2;
                foreach (DataGridViewRow item in (IEnumerable)dataGridView.Rows)
                {
                    item.Height = num2;
                }
                DataGridViewCellStyle columnHeadersDefaultCellStyle = dataGridView.ColumnHeadersDefaultCellStyle;
                if (columnHeadersDefaultCellStyle.Font != null)
                {
                    columnHeadersDefaultCellStyle.Font = new Font(columnHeadersDefaultCellStyle.Font.FontFamily, columnHeadersDefaultCellStyle.Font.Size * s, columnHeadersDefaultCellStyle.Font.Style);
                }
                columnHeadersDefaultCellStyle.Padding = new Padding((int)Math.Round(10f * s), 0, 0, 0);
                DataGridViewCellStyle defaultCellStyle = dataGridView.DefaultCellStyle;
                if (defaultCellStyle.Font != null)
                {
                    defaultCellStyle.Font = new Font(defaultCellStyle.Font.FontFamily, defaultCellStyle.Font.Size * s, defaultCellStyle.Font.Style);
                }
                foreach (DataGridViewColumn column in dataGridView.Columns)
                {
                    column.DefaultCellStyle.Padding = new Padding((int)Math.Round(12f * s), (int)Math.Round(1f * s), 0, (int)Math.Round(2f * s));
                }
                dataGridView.Height = num + num2 * dataGridView.Rows.Count + (int)Math.Round(6f * s);
            }
            foreach (Control control in c.Controls)
            {
                ScaleControlTree(control, s);
            }
        }

        private static string SlotStatePath(int n)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config\\freebuff-desktop\\slots\\slot-" + n + "\\state.json");
        }

        private static string SlotStateDir(int n)
        {
            return Path.GetDirectoryName(SlotStatePath(n));
        }

        private static string InitModeMarkerPath(int n)
        {
            return Path.Combine(SlotStateDir(n), ".controller-init");
        }

        private static bool SlotInitialized(int n)
        {
            if (!File.Exists(SlotStatePath(n)))
            {
                return File.Exists(InitModeMarkerPath(n));
            }
            return true;
        }

        private static void RememberInitMode(int n, int copyFrom)
        {
            try
            {
                Directory.CreateDirectory(SlotStateDir(n));
                File.WriteAllText(InitModeMarkerPath(n), ((copyFrom < 0) ? "fresh" : ("copy:" + copyFrom)) + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                LogFail(" Не могу записать флаг запуска (инстанс  " + n + ")", ex);
            }
        }

        private static string SlotUserData(int n)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Freebuff-slot-" + n);
        }

        private static bool IsOwnFreebuffProcess(ManagementObject process)
        {
            try
            {
                string text = process["ExecutablePath"] as string;
                if (string.IsNullOrEmpty(text))
                {
                    return false;
                }
                string a = Path.GetFullPath(text).TrimEnd('\\', '/');
                string b = Path.GetFullPath(FreebuffExe).TrimEnd('\\', '/');
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsInstanceProcess(ManagementObject process)
        {
            if (!IsOwnFreebuffProcess(process))
            {
                return false;
            }
            string text;
            try
            {
                text = process["CommandLine"] as string;
            }
            catch
            {
                return true;
            }
            if (string.IsNullOrEmpty(text))
            {
                return true;
            }
            return text.IndexOf("--type=", StringComparison.Ordinal) < 0;
        }

        private static HashSet<int> QueryRunning(out bool mainRunning)
        {
            HashSet<int> hashSet = new HashSet<int>();
            mainRunning = false;
            try
            {
                using (ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE Name='Freebuff.exe'"))
                {
                    foreach (ManagementObject item in managementObjectSearcher.Get())
                    {
                        if (!IsInstanceProcess(item))
                        {
                            continue;
                        }
                        string text = item["CommandLine"] as string;
                        Match match = (string.IsNullOrEmpty(text) ? Match.Empty : SlotRegex.Match(text));
                        if (match.Success)
                        {
                            int result;
                            if (int.TryParse(match.Groups[1].Value, out result))
                            {
                                hashSet.Add(result);
                            }
                        }
                        else
                        {
                            mainRunning = true;
                        }
                    }
                }
            }
            catch
            {
            }
            return hashSet;
        }

        private static string AccountForState(string statePath)
        {
            if (!File.Exists(statePath))
            {
                return " (не инициализирован) ";
            }
            try
            {
                string input = File.ReadAllText(statePath);
                Match match = EmailRegex.Match(input);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
                return " (не вошел) ";
            }
            catch
            {
                return " (чтение) ";
            }
        }

        private void ComputeAndApply()
        {
            bool mainRunning;
            HashSet<int> slots = QueryRunning(out mainRunning);
            string[] array = new string[10];
            for (int i = 0; i <= 9; i++)
            {
                array[i] = ((i == 0) ? AccountForState(DefaultState) : AccountForState(SlotStatePath(i)));
            }
            ApplyToGrid(mainRunning, slots, array);
        }

        private static void ReloadProxyConfig()
        {
            string text = "auto";
            string text2 = null;
            try
            {
                if (File.Exists(LocalProxyConfigFile))
                {
                    string text3 = File.ReadAllText(LocalProxyConfigFile).Trim();
                    if (text3.Length > 0)
                    {
                        Uri result;
                        if (text3.Equals("off", StringComparison.OrdinalIgnoreCase))
                        {
                            text = "off";
                        }
                        else if (Uri.TryCreate(text3, UriKind.Absolute, out result))
                        {
                            text = "manual";
                            text2 = text3;
                        }
                    }
                }
            }
            catch
            {
            }
            localProxyMode = text;
            manualProxyUrl = text2;
            stickyRoute = null;
        }

        private static void WriteProxyConfig(string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LocalProxyConfigFile));
            File.WriteAllText(LocalProxyConfigFile, content);
        }

        private static bool IsSocksUrl(string url)
        {
            if (url != null)
            {
                return url.StartsWith("socks", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static string[] OrderedCandidates()
        {
            List<string> list = new List<string>(3);
            if (localProxyMode == "manual" && manualProxyUrl != null && !IsSocksUrl(manualProxyUrl))
            {
                list.Add(manualProxyUrl);
            }
            else if (localProxyMode == "auto" && detectedProxyUrl != null && !IsSocksUrl(detectedProxyUrl))
            {
                list.Add(detectedProxyUrl);
            }
            list.Add(null);
            list.Add("");
            if (stickyRoute != null)
            {
                List<string> list2 = new List<string>(list.Count);
                foreach (string item in list)
                {
                    if (item == stickyRoute)
                    {
                        list2.Add(item);
                        break;
                    }
                }
                foreach (string item2 in list)
                {
                    if (item2 != stickyRoute)
                    {
                        list2.Add(item2);
                    }
                }
                return list2.ToArray();
            }
            return list.ToArray();
        }

        private static void NoteRouteSuccess(string candidate)
        {
            stickyRoute = candidate;
            lastRouteText = ((candidate == null) ? " Системный прокси " : ((candidate.Length == 0) ? " Напрямую " : candidate));
        }

        private static string RouteText()
        {
            if (lastRouteText.Length <= 0)
            {
                return "";
            }
            return "  · через  " + lastRouteText;
        }

        private static void ApplyProxy(HttpWebRequest req, string candidate)
        {
            if (candidate != null)
            {
                req.Proxy = ((candidate.Length == 0) ? null : new WebProxy(candidate));
            }
        }

        private static Uri SystemProxyUri()
        {
            try
            {
                Uri uri = new Uri("https://www.codebuff.com/");
                IWebProxy systemWebProxy = WebRequest.GetSystemWebProxy();
                if (systemWebProxy == null)
                {
                    return null;
                }
                Uri proxy = systemWebProxy.GetProxy(uri);
                return (proxy == null || proxy == uri) ? null : proxy;
            }
            catch
            {
                return null;
            }
        }

        private static bool ControllerProxyAvailable()
        {
            string kind;
            string address;
            return ControllerProxyRoute(out kind, out address);
        }

        private static bool ControllerProxyRoute(out string kind, out string address)
        {
            kind = null;
            address = null;
            string text = ((localProxyMode == "manual") ? manualProxyUrl : ((localProxyMode == "auto") ? detectedProxyUrl : null));
            if (localProxyMode == "auto" && string.IsNullOrEmpty(text))
            {
                text = ProbeLocalProxyNow();
            }
            if (!string.IsNullOrEmpty(text) && !IsSocksUrl(text) && ProxyAlive(text) && (localProxyMode != "manual" || ProxyFunctional(text)))
            {
                kind = " Локальный прокси ";
                address = ShortProxyUrl(text);
                return true;
            }
            Uri uri = SystemProxyUri();
            if (uri != null && ProxyProbeOk(uri.ToString(), "http://connect.rom.miui.com/generate_204"))
            {
                kind = " Системный прокси ";
                address = ShortProxyUrl(uri.ToString());
                return true;
            }
            return false;
        }

        private static string ShortProxyUrl(string url)
        {
            try
            {
                Uri uri = new Uri(url);
                return (uri.Port > 0) ? (uri.Host + ":" + uri.Port) : uri.Host;
            }
            catch
            {
                return url;
            }
        }

        private void RefreshProxyStatusAsync()
        {
            if (Interlocked.CompareExchange(ref proxyStatusBusy, 1, 0) != 0)
            {
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                string kind = null;
                string addr = null;
                try
                {
                    ok = ControllerProxyRoute(out kind, out addr);
                }
                catch
                {
                }
                Interlocked.Exchange(ref proxyStatusBusy, 0);
                UiSafe(delegate
                {
                    ApplyProxyStatus(ok, kind, addr);
                });
            });
        }

        private void ApplyProxyStatus(bool ok, string kind, string address)
        {
            if (!base.IsDisposed && proxyLink != null)
            {
                proxyLink.Text = (ok ? (" прокси  ✓ " + address) : " прокси  ✗  не  подключение ");
                proxyColor = (ok ? ColGreen : ColNewVersion);
                proxyLink.ForeColor = proxyColor;
                if (proxyTip != null)
                {
                    proxyTip.SetToolTip(proxyLink, ok ? (" Сейчас через " + kind + "(" + address + "),  Лимит  будет  норма  обновить . \n ЛКМ : Прокси ") : " не найден  доступен  прокси : Лимит  обновить  пропуск цикла ( не  Напрямую  внешние запросы ), \n Лимит  в колонке ' Нет прокси '.  прокси  после  авто  восстановить . \n ЛКМ : Прокси ");
                }
            }
        }

        private static bool ProxyFunctional(string url)
        {
            if (ProxyProbeOk(url, "http://connect.rom.miui.com/generate_204"))
            {
                return ProxyProbeOk(url, "https://www.gstatic.com/generate_204");
            }
            return false;
        }

        private static bool ProxyProbeOk(string proxyUrl, string targetUrl)
        {
            try
            {
                Uri uri = new Uri(proxyUrl);
                HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create(targetUrl);
                httpWebRequest.Proxy = new WebProxy(uri.Host, uri.Port);
                httpWebRequest.Method = "GET";
                httpWebRequest.Timeout = 2500;
                httpWebRequest.ReadWriteTimeout = 2500;
                httpWebRequest.AllowAutoRedirect = false;
                using (HttpWebResponse httpWebResponse = (HttpWebResponse)httpWebRequest.GetResponse())
                {
                    int statusCode = (int)httpWebResponse.StatusCode;
                    return statusCode == 204 || statusCode == 200;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void DetectProxyAsync()
        {
            if (localProxyMode != "auto" || Interlocked.CompareExchange(ref detectBusy, 1, 0) != 0)
            {
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                string text = null;
                try
                {
                    text = ProbeLocalProxy();
                }
                catch
                {
                }
                detectedProxyUrl = text;
                Interlocked.Exchange(ref detectBusy, 0);
            });
        }

        private static string ProbeLocalProxy()
        {
            int[] autoDetectPorts = AutoDetectPorts;
            foreach (int num in autoDetectPorts)
            {
                string text = "http://127.0.0.1:" + num;
                if (ProxyAlive(text) && ProxyFunctional(text))
                {
                    return text;
                }
            }
            return null;
        }

        private static string ProbeLocalProxyNow()
        {
            if (Interlocked.CompareExchange(ref detectBusy, 1, 0) == 0)
            {
                string result = null;
                try
                {
                    result = ProbeLocalProxy();
                }
                catch
                {
                }
                detectedProxyUrl = result;
                Interlocked.Exchange(ref detectBusy, 0);
                return result;
            }
            int num = 0;
            while (detectBusy != 0 && num < 2000)
            {
                Thread.Sleep(100);
                num += 100;
            }
            return detectedProxyUrl;
        }

        private static bool ProxyAlive(string url)
        {
            try
            {
                Uri uri = new Uri(url);
                using (TcpClient tcpClient = new TcpClient())
                {
                    IAsyncResult asyncResult = tcpClient.BeginConnect(uri.Host, uri.Port, null, null);
                    if (!asyncResult.AsyncWaitHandle.WaitOne(500))
                    {
                        return false;
                    }
                    tcpClient.EndConnect(asyncResult);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string LaunchProxyUrl()
        {
            string text = ((localProxyMode == "manual") ? manualProxyUrl : ((localProxyMode == "auto") ? detectedProxyUrl : null));
            if (text != null && !ProxyAlive(text))
            {
                text = null;
            }
            if (text != null)
            {
                if (localProxyMode == "manual" && !IsSocksUrl(text) && !ProxyFunctional(text))
                {
                    return null;
                }
                return text;
            }
            if (localProxyMode == "auto")
            {
                string text2 = ProbeLocalProxyNow();
                if (text2 != null && ProxyAlive(text2))
                {
                    return text2;
                }
            }
            return null;
        }

        private static void ApplyLaunchProxy(ProcessStartInfo psi, string url)
        {
            if (!string.IsNullOrEmpty(url))
            {
                psi.Arguments = ((psi.Arguments.Length > 0) ? (psi.Arguments + " ") : "") + "--proxy-server=" + url;
                psi.EnvironmentVariables["HTTP_PROXY"] = url;
                psi.EnvironmentVariables["HTTPS_PROXY"] = url;
                psi.EnvironmentVariables["NO_PROXY"] = "localhost,127.0.0.1";
            }
        }

        private static void RememberLaunchProxy(int slot, string url)
        {
            lock (launchProxyLock)
            {
                if (string.IsNullOrEmpty(url))
                {
                    launchProxyBySlot.Remove(slot);
                    launchProxyNotified.Remove(slot);
                }
                else
                {
                    launchProxyBySlot[slot] = url;
                }
            }
        }

        private static bool ProxyUsable(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return true;
            }
            if (!ProxyAlive(url))
            {
                return false;
            }
            if (IsSocksUrl(url))
            {
                return true;
            }
            return ProxyFunctional(url);
        }

        private void WatchProxyHealth()
        {
            if (Interlocked.CompareExchange(ref proxyWatchBusy, 1, 0) != 0)
            {
                return;
            }
            string manual = ((localProxyMode == "manual") ? manualProxyUrl : null);
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<KeyValuePair<int, string>> targets = new List<KeyValuePair<int, string>>();
                List<int> broken = new List<int>();
                bool manualBroken = false;
                try
                {
                    bool mainRunning;
                    HashSet<int> hashSet = QueryRunning(out mainRunning);
                    lock (launchProxyLock)
                    {
                        List<int> list = new List<int>();
                        foreach (KeyValuePair<int, string> item in launchProxyBySlot)
                        {
                            if ((item.Key == 0) ? mainRunning : hashSet.Contains(item.Key))
                            {
                                targets.Add(item);
                            }
                            else
                            {
                                list.Add(item.Key);
                            }
                        }
                        foreach (int item2 in list)
                        {
                            launchProxyBySlot.Remove(item2);
                            launchProxyNotified.Remove(item2);
                        }
                    }
                    foreach (KeyValuePair<int, string> item3 in targets)
                    {
                        if (!ProxyUsable(item3.Value))
                        {
                            broken.Add(item3.Key);
                        }
                    }
                    if (manual != null)
                    {
                        manualBroken = !ProxyUsable(manual);
                    }
                }
                catch
                {
                }
                Interlocked.Exchange(ref proxyWatchBusy, 0);
                UiSafe(delegate
                {
                    if (!base.IsDisposed)
                    {
                        foreach (KeyValuePair<int, string> item4 in targets)
                        {
                            bool flag = broken.Contains(item4.Key);
                            bool flag2;
                            lock (launchProxyLock)
                            {
                                flag2 = launchProxyNotified.Contains(item4.Key);
                            }
                            if (flag && !flag2)
                            {
                                lock (launchProxyLock)
                                {
                                    launchProxyNotified.Add(item4.Key);
                                }
                                string text = ((item4.Key == 0) ? " Главная " : (" Инстанс  " + item4.Key));
                                string text2 = text + " прокси  недоступен (" + item4.Value + ")-- этот  Инстанс  сеть  может  уже ,  будет  прокси . ";
                                SetStatus(text2, ColNewVersion);
                                TrayNotify(text2);
                            }
                            else if (!flag && flag2)
                            {
                                lock (launchProxyLock)
                                {
                                    launchProxyNotified.Remove(item4.Key);
                                }
                            }
                        }
                        if (manual != null)
                        {
                            if (manualBroken && !manualProxyNotified)
                            {
                                manualProxyNotified = true;
                                string text3 = " Настроенный прокси  " + manual + "  недоступен -- контроллер  будет  авто  Системный прокси  /  Напрямую ,  прокси  восстановить  после . ";
                                SetStatus(text3, ColNewVersion);
                                TrayNotify(text3);
                            }
                            else if (!manualBroken && manualProxyNotified)
                            {
                                manualProxyNotified = false;
                            }
                        }
                    }
                });
            });
        }

        private void FetchQuotasAsync(bool force, bool announce = true)
        {
            if ((!force && (DateTime.Now - lastQuotaFetch).TotalMinutes < 5.0) || Interlocked.CompareExchange(ref quotaBusy, 1, 0) != 0)
            {
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool noProxy = false;
                string routeKind = null;
                string routeAddr = null;
                try
                {
                    noProxy = !ControllerProxyRoute(out routeKind, out routeAddr);
                    //  Лимит ( 12~21s/),  N  шт.  Аккаунт  это  N 
                    //  жди ;v1.9.6  Аккаунт ,  шт.  шт. (), 
                    //  шт.  Аккаунт .  / 30s  /  офлайн  пропустить  не . 
                    List<int> list = new List<int>();
                    for (int i = 0; i <= 9; i++)
                    {
                        if (base.IsDisposed)
                        {
                            Interlocked.Exchange(ref quotaBusy, 0);
                            return;
                        }
                        string text = ReadTokenFor(i);
                        if (text == null)
                        {
                            quotaInfos[i] = new QuotaInfo
                            {
                                Text = "-"
                            };
                        }
                        else if (noProxy)
                        {
                            quotaInfos[i] = OfflineQuota(quotaInfos[i]);
                        }
                        else
                        {
                            list.Add(i);
                        }
                    }
                    if (list.Count == 0)
                    {
                        FinishQuotaRound(noProxy, routeKind, routeAddr, force, announce);
                        return;
                    }
                    int[] pending = new int[1] { list.Count };
                    foreach (int idx in list)
                    {
                        int slot = idx;
                        string token = ReadTokenFor(slot);
                        ThreadPool.QueueUserWorkItem(delegate
                        {
                            try
                            {
                                quotaInfos[slot] = FetchQuota(token);
                            }
                            catch
                            {
                                quotaInfos[slot] = new QuotaInfo
                                {
                                    Text = " Не получено "
                                };
                            }
                            UiSafe(delegate
                            {
                                if (!base.IsDisposed)
                                {
                                    ApplyQuotaColumn();
                                }
                            });
                            if (Interlocked.Decrement(ref pending[0]) == 0)
                            {
                                FinishQuotaRound(noProxy, routeKind, routeAddr, force, announce);
                            }
                        });
                    }
                }
                catch
                {
                    Interlocked.Exchange(ref quotaBusy, 0);
                }
            });
        }

        //  Лимит  обновить ( после  шт.  Аккаунт  после ):,
        // quotaBusy , Статус . 
        private void FinishQuotaRound(bool noProxy, string routeKind, string routeAddr, bool force, bool announce)
        {
            if (!noProxy)
            {
                lastQuotaFetch = DateTime.Now;
            }
            Interlocked.Exchange(ref quotaBusy, 0);
            if (base.IsDisposed || !base.IsHandleCreated)
            {
                return;
            }
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!base.IsDisposed)
                    {
                        ApplyProxyStatus(!noProxy, routeKind, routeAddr);
                        ApplyQuotaColumn();
                        if (noProxy)
                        {
                            if (force && announce)
                            {
                                SetStatus(" не  подключение  прокси  ·  пропустить  Лимит  обновить ( прокси  после  авто  восстановить )", ColNewVersion);
                            }
                        }
                        else if (force && announce)
                        {
                            SetStatus(" Лимит обновлён " + RouteText());
                        }
                    }
                });
            }
            catch
            {
            }
        }

        private void ApplyQuotaColumn()
        {
            for (int i = 0; i <= 9; i++)
            {
                QuotaInfo quotaInfo = quotaInfos[i];
                string text = ((quotaInfo != null) ? quotaInfo.Text : null) ?? "...";
                bool flag = quotaInfo != null && quotaInfo.Exhausted;
                Color color = ((quotaInfo != null && quotaInfo.Offline) ? ColNewVersion : (flag ? Color.FromArgb(230, 90, 90) : ((quotaInfo != null && quotaInfo.Text != null) ? ColGreen : ColSub)));
                DataGridViewRow dataGridViewRow = grid.Rows[i];
                string text3 = dataGridViewRow.Cells[3].Value as string;
                SetCell(dataGridViewRow, 3, text, color);
                if (text3 != null && text3.Length > 0 && text3 != "..." && text != text3 && text != "...")
                {
                    FlashCell(dataGridViewRow.Cells[3], color);
                }
                string text2 = ((quotaInfo != null) ? quotaInfo.Tip : null) ?? "";
                DataGridViewCell dataGridViewCell = dataGridViewRow.Cells[3];
                if (dataGridViewCell.ToolTipText != text2)
                {
                    dataGridViewCell.ToolTipText = text2;
                }
            }
        }

        private static void SetCell(DataGridViewRow row, int col, string text, Color color)
        {
            DataGridViewCell dataGridViewCell = row.Cells[col];
            if (!string.Equals(dataGridViewCell.Value as string, text, StringComparison.Ordinal) || dataGridViewCell.Style.ForeColor.ToArgb() != color.ToArgb())
            {
                dataGridViewCell.Value = text;
                dataGridViewCell.Style.ForeColor = color;
            }
        }

        private static string ReadTokenFor(int i)
        {
            string path = ((i == 0) ? DefaultState : SlotStatePath(i));
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                Dictionary<string, object> dictionary = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                if (dictionary == null || !dictionary.ContainsKey("authSessions"))
                {
                    return null;
                }
                Dictionary<string, object> dictionary2 = dictionary["authSessions"] as Dictionary<string, object>;
                if (dictionary2 == null || dictionary2.Count == 0)
                {
                    return null;
                }
                Dictionary<string, object>.ValueCollection.Enumerator enumerator = dictionary2.Values.GetEnumerator();
                if (!enumerator.MoveNext())
                {
                    return null;
                }
                Dictionary<string, object> dictionary3 = enumerator.Current as Dictionary<string, object>;
                if (dictionary3 == null || !dictionary3.ContainsKey("token"))
                {
                    return null;
                }
                return dictionary3["token"] as string;
            }
            catch
            {
                return null;
            }
        }

        private static QuotaInfo FetchQuota(string token)
        {
            string[] array = OrderedCandidates();
            foreach (string proxyCandidate in array)
            {
                QuotaInfo quotaInfo = TryFetchQuota(token, proxyCandidate);
                if (quotaInfo != null)
                {
                    return quotaInfo;
                }
            }
            QuotaInfo quotaInfo2 = new QuotaInfo();
            quotaInfo2.Text = " Не получено ";
            return quotaInfo2;
        }

        private static QuotaInfo OfflineQuota(QuotaInfo previous)
        {
            string text = ((previous != null && !previous.Offline) ? previous.Text : null);
            string text2 = " не  подключение  прокси  ·  пропустить  Лимит  обновить . \n прокси  после  авто  восстановить . ";
            if (!string.IsNullOrEmpty(text) && text != "-")
            {
                text2 = text2 + "\n прошлое значение :" + text;
            }
            QuotaInfo quotaInfo = new QuotaInfo();
            quotaInfo.Text = " Нет прокси ";
            quotaInfo.Offline = true;
            quotaInfo.Tip = text2;
            return quotaInfo;
        }

        private static double DictNum(Dictionary<string, object> d, string key)
        {
            object value;
            if (d == null || !d.TryGetValue(key, out value))
            {
                return 0.0;
            }
            return Convert.ToDouble(value);
        }

        private static Dictionary<string, object> DictObj(Dictionary<string, object> d, string key)
        {
            object value;
            if (d == null || !d.TryGetValue(key, out value))
            {
                return null;
            }
            return value as Dictionary<string, object>;
        }

        private static string DictText(Dictionary<string, object> d, string key)
        {
            object value;
            if (d == null || !d.TryGetValue(key, out value))
            {
                return null;
            }
            return value as string;
        }

        private static string FmtNum(double v)
        {
            return v.ToString("0.##");
        }

        private static string FmtReset(string iso)
        {
            if (string.IsNullOrEmpty(iso))
            {
                return null;
            }
            try
            {
                return DateTime.Parse(iso, null, DateTimeStyles.RoundtripKind).ToLocalTime().ToString(" M/d HH:mm ");
            }
            catch
            {
                return null;
            }
        }

        private static QuotaInfo TryFetchQuota(string token, string proxyCandidate)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create("https://www.codebuff.com/api/v1/freebuff/session");
                ApplyProxy(httpWebRequest, proxyCandidate);
                httpWebRequest.Method = "GET";
                httpWebRequest.Timeout = 30000;
                httpWebRequest.ReadWriteTimeout = 30000;
                httpWebRequest.Headers["Authorization"] = "Bearer " + token;
                httpWebRequest.Headers["x-freebuff-multi-session"] = "1";
                httpWebRequest.Headers["x-freebuff-include-unused-rate-limits"] = "1";
                httpWebRequest.UserAgent = "FreebuffMultiOpenController/1.0";
                using (HttpWebResponse httpWebResponse = (HttpWebResponse)httpWebRequest.GetResponse())
                {
                    using (StreamReader streamReader = new StreamReader(httpWebResponse.GetResponseStream()))
                    {
                        NoteRouteSuccess(proxyCandidate);
                        string input = streamReader.ReadToEnd();
                        Dictionary<string, object> dictionary = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(input);
                        if (dictionary == null)
                        {
                            QuotaInfo quotaInfo = new QuotaInfo();
                            quotaInfo.Text = "-";
                            return quotaInfo;
                        }
                        Dictionary<string, object> dictionary2 = DictObj(dictionary, "freebucks");
                        if (dictionary2 != null && dictionary2.ContainsKey("daily"))
                        {
                            Dictionary<string, object> d = DictObj(dictionary2, "daily");
                            Dictionary<string, object> d2 = DictObj(dictionary2, "wallet");
                            double num = DictNum(d, "remaining");
                            double v = DictNum(d, "limit");
                            double num2 = DictNum(d2, "balance");
                            double num3 = DictNum(d2, "monthlyBonus");
                            double num4 = double.MaxValue;
                            Dictionary<string, object> dictionary3 = DictObj(dictionary2, "prices");
                            if (dictionary3 != null)
                            {
                                foreach (object value in dictionary3.Values)
                                {
                                    try
                                    {
                                        num4 = Math.Min(num4, Convert.ToDouble(value));
                                    }
                                    catch
                                    {
                                    }
                                }
                            }
                            QuotaInfo quotaInfo2 = new QuotaInfo();
                            List<string> list = new List<string>();
                            list.Add(FmtNum(num) + "/" + FmtNum(v));
                            if (num2 > 0.0)
                            {
                                list.Add(" Кошелёк  " + FmtNum(num2));
                            }
                            quotaInfo2.Text = string.Join("  ", list.ToArray());
                            bool flag = (quotaInfo2.Exhausted = num <= 0.0 && (num4 == double.MaxValue || num2 < num4));
                            string text = FmtReset(DictText(d, "resetAt"));
                            StringBuilder stringBuilder = new StringBuilder();
                            stringBuilder.Append(" Сегодня Freebucks осталось  " + FmtNum(num) + "/" + FmtNum(v));
                            if (num2 > 0.0)
                            {
                                stringBuilder.Append(",  Кошелёк  " + FmtNum(num2));
                            }
                            if (num3 > 0.0)
                            {
                                stringBuilder.Append("( мес. бонус  " + FmtNum(num3) + ")");
                            }
                            if (num4 != double.MaxValue)
                            {
                                stringBuilder.Append("\n дешевая модель в час  " + FmtNum(num4) + " Freebucks");
                            }
                            stringBuilder.Append("\n PT  ежедневно  0 " + ((text != null) ? (",  " + text) : ""));
                            if (flag)
                            {
                                stringBuilder.Append("\n( сегодня  Лимит  и  Кошелёк  все  не  хватает на сессию )");
                            }
                            quotaInfo2.Tip = stringBuilder.ToString();
                            return quotaInfo2;
                        }
                        if (!dictionary.ContainsKey("rateLimitsByModel"))
                        {
                            QuotaInfo quotaInfo3 = new QuotaInfo();
                            quotaInfo3.Text = "-";
                            return quotaInfo3;
                        }
                        Dictionary<string, object> dictionary4 = dictionary["rateLimitsByModel"] as Dictionary<string, object>;
                        if (dictionary4 == null || dictionary4.Count == 0)
                        {
                            QuotaInfo quotaInfo4 = new QuotaInfo();
                            quotaInfo4.Text = " Без лимита ";
                            return quotaInfo4;
                        }
                        double num5 = double.MaxValue;
                        double num6 = 0.0;
                        bool flag2 = false;
                        foreach (object value2 in dictionary4.Values)
                        {
                            Dictionary<string, object> dictionary5 = value2 as Dictionary<string, object>;
                            if (dictionary5 != null && dictionary5.ContainsKey("limit") && dictionary5.ContainsKey("recentCount"))
                            {
                                double num7 = Convert.ToDouble(dictionary5["limit"]);
                                double num8 = Convert.ToDouble(dictionary5["recentCount"]);
                                double num9 = num7 - num8;
                                if (num9 < num5)
                                {
                                    num5 = num9;
                                    num6 = num7;
                                    flag2 = true;
                                }
                            }
                        }
                        if (!flag2)
                        {
                            QuotaInfo quotaInfo5 = new QuotaInfo();
                            quotaInfo5.Text = "-";
                            return quotaInfo5;
                        }
                        if (num5 <= 0.0)
                        {
                            QuotaInfo quotaInfo6 = new QuotaInfo();
                            quotaInfo6.Text = " ост. 0/ " + num6 + "  израсходован ";
                            return quotaInfo6;
                        }
                        QuotaInfo quotaInfo7 = new QuotaInfo();
                        quotaInfo7.Text = " ост.  " + num5 + "/" + num6;
                        return quotaInfo7;
                    }
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse httpWebResponse2 = ex.Response as HttpWebResponse;
                if (httpWebResponse2 != null && httpWebResponse2.StatusCode == HttpStatusCode.Unauthorized)
                {
                    QuotaInfo quotaInfo8 = new QuotaInfo();
                    quotaInfo8.Text = " Логин просрочен ";
                    return quotaInfo8;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string ReadInstalledVersion()
        {
            try
            {
                string fileVersion = FileVersionInfo.GetVersionInfo(FreebuffExe).FileVersion;
                if (!string.IsNullOrEmpty(fileVersion))
                {
                    return fileVersion.Trim();
                }
            }
            catch
            {
            }
            return null;
        }

        private void RefreshInstalledVersion()
        {
            string value = ReadInstalledVersion();
            if (!string.IsNullOrEmpty(value))
            {
                installedVersion = value;
            }
            RefreshHanhuaLive();
        }

        private void RefreshHanhuaLive()
        {
            if (base.IsDisposed)
            {
                return;
            }
            string text = installedVersion;
            if (string.IsNullOrEmpty(text) || text == hanhuaRecheckVersion)
            {
                return;
            }
            hanhuaRecheckVersion = text;
            UiSafe(delegate
            {
                if (!base.IsDisposed)
                {
                    RefreshHanhuaUi();
                    CleanupAfterUpdate();
                    CheckPackUpdateAsync();
                    StartAutoRestoreHanhua(" Найдено обновление Freebuff ", null);
                }
            });
        }

        private static string ReadUpdateFeedUrl()
        {
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(FreebuffExe), "resources\\app-update.yml");
                if (File.Exists(path))
                {
                    Match match = FeedUrlRegex.Match(File.ReadAllText(path));
                    if (match.Success)
                    {
                        return match.Groups[1].Value.Trim().TrimEnd('/') + "/latest.yml";
                    }
                }
            }
            catch
            {
            }
            return "https://freebuff.com/api/desktop/updates/win-x64/latest.yml";
        }

        private static string FetchLatestVersion(string feedUrl)
        {
            string[] array = OrderedCandidates();
            foreach (string candidate in array)
            {
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create(feedUrl);
                    httpWebRequest.Method = "GET";
                    httpWebRequest.AllowAutoRedirect = false;
                    httpWebRequest.Timeout = 10000;
                    httpWebRequest.ReadWriteTimeout = 10000;
                    httpWebRequest.UserAgent = "FreebuffMultiOpenController/1.0";
                    ApplyProxy(httpWebRequest, candidate);
                    using (HttpWebResponse httpWebResponse = (HttpWebResponse)httpWebRequest.GetResponse())
                    {
                        NoteRouteSuccess(candidate);
                        int statusCode = (int)httpWebResponse.StatusCode;
                        if (statusCode >= 300 && statusCode < 400)
                        {
                            Match match = LooseVersionRegex.Match(httpWebResponse.Headers["Location"] ?? "");
                            if (match.Success)
                            {
                                return match.Value;
                            }
                            continue;
                        }
                        using (StreamReader streamReader = new StreamReader(httpWebResponse.GetResponseStream()))
                        {
                            Match match2 = YamlVersionRegex.Match(streamReader.ReadToEnd());
                            if (match2.Success)
                            {
                                return match2.Groups[1].Value.Trim();
                            }
                        }
                    }
                }
                catch
                {
                }
            }
            return null;
        }

        private static Version ParseLooseVersion(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return null;
            }
            Match match = LooseVersionRegex.Match(s);
            if (!match.Success)
            {
                return null;
            }
            int result;
            int.TryParse(match.Groups[3].Value, out result);
            int result2;
            int.TryParse(match.Groups[4].Value, out result2);
            return new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), result, result2);
        }

        private bool UpdateAvailable()
        {
            Version version = ParseLooseVersion(installedVersion);
            Version version2 = ParseLooseVersion(latestVersion);
            if (version != null && version2 != null)
            {
                return version2.CompareTo(version) > 0;
            }
            return false;
        }

        private void CheckVersionAsync(bool manual = false)
        {
            if (Interlocked.CompareExchange(ref versionCheckBusy, 1, 0) != 0)
            {
                return;
            }
            if (manual)
            {
                updateFailed = false;
                SetStatus(" Проверяю обновления Freebuff... ");
            }
            ApplyVersionUi(true);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string latest = null;
                try
                {
                    latest = FetchLatestVersion(ReadUpdateFeedUrl());
                }
                catch
                {
                }
                Interlocked.Exchange(ref versionCheckBusy, 0);
                if (base.IsDisposed || !base.IsHandleCreated)
                {
                    return;
                }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (!base.IsDisposed)
                        {
                            RefreshInstalledVersion();
                            latestVersion = latest;
                            Version version = ParseLooseVersion(installedVersion);
                            Version version2 = ParseLooseVersion(latest);
                            if (version != null && version2 != null && version.CompareTo(version2) >= 0)
                            {
                                bool flag = updateStarted;
                                updateStarted = false;
                                updateFailed = false;
                                if (flag)
                                {
                                    CleanupAfterUpdate();
                                }
                            }
                            ApplyVersionUi(false);
                            if (UpdateAvailable() && !updateStarted)
                            {
                                string text = ((HanhuaApplied() && HanhuaBuildDir(hanhuaDir) != null) ? " " : "");
                                if (manual)
                                {
                                    StartUpdateDownload();
                                }
                                else
                                {
                                    string text2 = " Есть версия v " + latestVersion + "  · нажми чтобы скачать установщик ";
                                    SetStatusAction(text2, ColNewVersion);
                                    TrayNotify(" Доступен Freebuff v " + latestVersion + ",  контроллер  окно  Скачать . " + text);
                                }
                            }
                            else if (manual && !string.IsNullOrEmpty(latest))
                            {
                                SetStatus("Freebuff v" + installedVersion + "  · уже актуально ");
                            }
                        }
                    });
                }
                catch
                {
                }
            });
        }

        //  версия  Статус  запись : есть ,  не . 
        // ' новая версия ' CheckVersionAsync  запись ,  в  только  Скачать  /  ошибка . 
        private void ApplyVersionUi(bool checking)
        {
            if (Interlocked.CompareExchange(ref updateBusy, 0, 0) == 1 || checking)
            {
                return;
            }
            if (updateStarted)
            {
                SetStatus((HanhuaBuildDir(hanhuaDir) != null) ? " Установщик запущен  ·  авто  вернуть " : " Установщик запущен ", ColGreen);
            }
            else if (updateFailed)
            {
                SetStatusAction(" Скачать  ошибка  ·  Открыть  Скачать ", ColNewVersion);
            }
        }

        private void UiSafe(MethodInvoker action)
        {
            if (base.IsDisposed || !base.IsHandleCreated)
            {
                return;
            }
            try
            {
                BeginInvoke(action);
            }
            catch
            {
            }
        }

        private void OnVersionLinkClick()
        {
            if (updateStarted)
            {
                Info(" Установщик запущен ,  Инфо  готово  обновление . \r\n Инфо  Freebuff  запущен ,  сначала  в  в ' Стоп все '. ");
            }
            else if (updateFailed)
            {
                updateFailed = false;
                try
                {
                    Process.Start("https://github.com/CodebuffAI/codebuff-community/releases/latest");
                }
                catch
                {
                }
                ApplyVersionUi(false);
            }
            else if (UpdateAvailable())
            {
                StartUpdateDownload();
            }
            else
            {
                CheckVersionAsync(true);
            }
        }

        private void StartUpdateDownload()
        {
            if (Interlocked.CompareExchange(ref updateBusy, 1, 0) != 0)
            {
                return;
            }
            SetStatus(" Качаю Freebuff v " + latestVersion + "  установщик... ");
            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception error = null;
                string installerPath = null;
                try
                {
                    string text = FetchUrlBody(ReadUpdateFeedUrl());
                    if (text == null)
                    {
                        throw new ApplicationException(" Нет latest.yml, скачивание без проверки остановлено ");
                    }
                    Match match = YamlPathRegex.Match(text);
                    Match match2 = YamlShaRegex.Match(text);
                    if (!match.Success || !match2.Success)
                    {
                        throw new ApplicationException("latest.yml  нет  установщик  путь  или  SHA512,  остановлен  Скачать ");
                    }
                    string text2 = match.Groups[1].Value.Trim();
                    string text3 = match2.Groups[1].Value.Trim();
                    if (!IsSha512Base64(text3))
                    {
                        throw new ApplicationException("latest.yml  SHA512  невалидный ,  остановлен  Скачать ");
                    }
                    string text4 = FeedBase() + "/" + text2;
                    string text5 = (installerPath = Path.Combine(Path.GetTempPath(), text2));
                    List<string> list = new List<string>();
                    if (text4 != null)
                    {
                        list.Add(text4);
                    }
                    DownloadFirstAvailable(list, text5, text3, delegate(long done, long total)
                    {
                        UiSafe(delegate
                        {
                            if (!base.IsDisposed)
                            {
                                SetStatus(" Качаю Freebuff v " + latestVersion + " · " + ((total > 0) ? (" качаю  " + done * 100 / total + "%") : (" Скачано  " + (done >> 20) + " MB")));
                            }
                        });
                    });
                    try
                    {
                        Process.Start(text5);
                    }
                    catch (Exception ex)
                    {
                        throw new ApplicationException(" Установщик скачан, но не запускается: " + ex.Message);
                    }
                }
                catch (Exception ex2)
                {
                    error = ex2;
                }
                Interlocked.Exchange(ref updateBusy, 0);
                if (error == null)
                {
                    UiSafe(delegate
                    {
                        if (!base.IsDisposed)
                        {
                            updateStarted = true;
                            ApplyVersionUi(false);
                            SavePendingInstaller(latestVersion, installerPath);
                            SetStatus("Freebuff  установщик  Скачано  Запуск ,  Инфо  готово .  Инфо  Freebuff  запущен ,  сначала ' Стоп все '. ", ColGreen);
                            TrayNotify(" Установщик Freebuff запущен, заверши обновление. ");
                        }
                    });
                }
                else
                {
                    UiSafe(delegate
                    {
                        if (!base.IsDisposed)
                        {
                            updateFailed = true;
                            ApplyVersionUi(false);
                            SetStatus(" Скачать  обновление  ошибка :" + error.Message, ColNewVersion);
                            TrayNotify(" Скачать  обновление  ошибка :" + error.Message);
                        }
                    });
                }
            });
        }

        private static string PendingInstallerPath()
        {
            try
            {
                if (!File.Exists(PendingInstallerFile))
                {
                    return null;
                }
                string[] array = File.ReadAllText(PendingInstallerFile).Split('\t');
                return (array.Length == 2) ? array[1].Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private static void SavePendingInstaller(string version, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PendingInstallerFile));
                File.WriteAllText(PendingInstallerFile, (version ?? "") + "\t" + path);
            }
            catch
            {
            }
        }

        private void PruneDownloadedInstaller()
        {
            try
            {
                if (!File.Exists(PendingInstallerFile))
                {
                    return;
                }
                string[] array = File.ReadAllText(PendingInstallerFile).Split('\t');
                if (array.Length != 2)
                {
                    File.Delete(PendingInstallerFile);
                    return;
                }
                string s = array[0].Trim();
                string text = array[1].Trim();
                Version version = ParseLooseVersion(installedVersion);
                Version version2 = ParseLooseVersion(s);
                if (version == null || version2 == null || version.CompareTo(version2) < 0)
                {
                    return;
                }
                long bytes = 0L;
                bool flag = false;
                try
                {
                    if (!string.IsNullOrEmpty(text) && File.Exists(text))
                    {
                        bytes = new FileInfo(text).Length;
                        flag = true;
                    }
                }
                catch
                {
                }
                if (flag)
                {
                    File.Delete(text);
                    SetStatus(" Freebuff обновлён до v " + installedVersion + ",  Удалено  Скачать  установщик ( освободить  " + HumanSize(bytes) + ")", ColGreen);
                }
                File.Delete(PendingInstallerFile);
            }
            catch
            {
            }
        }

        private static string FetchUrlBody(string url)
        {
            string[] array = OrderedCandidates();
            foreach (string candidate in array)
            {
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create(url);
                    httpWebRequest.Method = "GET";
                    httpWebRequest.AllowAutoRedirect = true;
                    httpWebRequest.Timeout = 15000;
                    httpWebRequest.ReadWriteTimeout = 15000;
                    httpWebRequest.UserAgent = "FreebuffMultiOpenController/1.0";
                    ApplyProxy(httpWebRequest, candidate);
                    using (HttpWebResponse httpWebResponse = (HttpWebResponse)httpWebRequest.GetResponse())
                    {
                        using (StreamReader streamReader = new StreamReader(httpWebResponse.GetResponseStream()))
                        {
                            string result = streamReader.ReadToEnd();
                            NoteRouteSuccess(candidate);
                            return result;
                        }
                    }
                }
                catch
                {
                }
            }
            return null;
        }

        private static string FeedBase()
        {
            string text = ReadUpdateFeedUrl();
            if (!text.EndsWith("/latest.yml"))
            {
                return text;
            }
            return text.Substring(0, text.Length - "/latest.yml".Length);
        }

        private static bool IsSha512Base64(string value)
        {
            try
            {
                return !string.IsNullOrEmpty(value) && Convert.FromBase64String(value).Length == 64;
            }
            catch
            {
                return false;
            }
        }

        private static void DownloadFirstAvailable(IList<string> urls, string dest, string shaB64, Action<long, long> progress)
        {
            Exception ex = null;
            foreach (string url in urls)
            {
                string[] array = OrderedCandidates();
                foreach (string proxyCandidate in array)
                {
                    try
                    {
                        DownloadOnce(url, dest, shaB64, progress, proxyCandidate);
                        return;
                    }
                    catch (Exception ex2)
                    {
                        ex = ex2;
                    }
                }
            }
            throw ex;
        }

        private static void DownloadOnce(string url, string dest, string shaB64, Action<long, long> progress, string proxyCandidate)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create(url);
            httpWebRequest.Method = "GET";
            httpWebRequest.AllowAutoRedirect = true;
            httpWebRequest.Timeout = 30000;
            httpWebRequest.ReadWriteTimeout = 30000;
            httpWebRequest.UserAgent = "FreebuffMultiOpenController/1.0";
            ApplyProxy(httpWebRequest, proxyCandidate);
            HttpWebResponse httpWebResponse = (HttpWebResponse)httpWebRequest.GetResponse();
            NoteRouteSuccess(proxyCandidate);
            using (httpWebResponse)
            {
                using (Stream stream = httpWebResponse.GetResponseStream())
                {
                    using (FileStream fileStream = new FileStream(dest, FileMode.Create, FileAccess.Write))
                    {
                        long contentLength = httpWebResponse.ContentLength;
                        long num = 0L;
                        byte[] array = new byte[65536];
                        SHA512 sHA = SHA512.Create();
                        byte[] hash;
                        try
                        {
                            DateTime dateTime = DateTime.MinValue;
                            int num2;
                            while ((num2 = stream.Read(array, 0, array.Length)) > 0)
                            {
                                fileStream.Write(array, 0, num2);
                                sHA.TransformBlock(array, 0, num2, null, 0);
                                num += num2;
                                if (progress != null && (DateTime.Now - dateTime).TotalMilliseconds >= 300.0)
                                {
                                    dateTime = DateTime.Now;
                                    progress(num, contentLength);
                                }
                            }
                            sHA.TransformFinalBlock(array, 0, 0);
                            hash = sHA.Hash;
                        }
                        finally
                        {
                            ((IDisposable)sHA).Dispose();
                        }
                        if (string.IsNullOrEmpty(shaB64))
                        {
                            return;
                        }
                        byte[] array2 = Convert.FromBase64String(shaB64);
                        bool flag = array2.Length == hash.Length;
                        if (flag)
                        {
                            for (int i = 0; i < array2.Length; i++)
                            {
                                if (array2[i] != hash[i])
                                {
                                    flag = false;
                                    break;
                                }
                            }
                        }
                        if (!flag)
                        {
                            try
                            {
                                File.Delete(dest);
                            }
                            catch
                            {
                            }
                            throw new ApplicationException(" SHA512 установщика не сошёлся ");
                        }
                    }
                }
            }
        }

        private void RefreshGrid()
        {
            if (Interlocked.CompareExchange(ref refreshBusy, 1, 0) != 0)
            {
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool mainRunning = false;
                HashSet<int> slots = new HashSet<int>();
                string[] accounts = new string[10];
                try
                {
                    slots = QueryRunning(out mainRunning);
                    for (int i = 0; i <= 9; i++)
                    {
                        accounts[i] = ((i == 0) ? AccountForState(DefaultState) : AccountForState(SlotStatePath(i)));
                    }
                }
                catch
                {
                }
                finally
                {
                    Interlocked.Exchange(ref refreshBusy, 0);
                }
                if (base.IsDisposed || !base.IsHandleCreated)
                {
                    return;
                }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (!base.IsDisposed)
                        {
                            ApplyToGrid(mainRunning, slots, accounts);
                            RefreshInstalledVersion();
                            DetectFreshLocalBuild();
                            TryPendingHanhuaRestore();
                            TryPendingAgentsMdFix();
                        }
                    });
                }
                catch
                {
                }
            });
        }

        private void TryPendingAgentsMdFix()
        {
            if (AgentsMdPending && !((DateTime.Now - agentsMdRetryAt).TotalSeconds < 30.0))
            {
                agentsMdRetryAt = DateTime.Now;
                EnsureAgentsMdEnabled();
            }
        }

        private void ApplyToGrid(bool mainRunning, HashSet<int> slots, string[] accounts)
        {
            for (int i = 0; i <= 9; i++)
            {
                bool flag = ((i == 0) ? mainRunning : slots.Contains(i));
                string text = accounts[i] ?? "...";
                DataGridViewRow row = grid.Rows[i];
                SetCell(row, 1, flag ? "● Вкл" : "○ Выкл", flag ? ColGreen : ColSub);
                SetCell(row, 2, text, text.StartsWith("(") ? ColSub : ColText);
            }
        }

        private int SelectedIndex()
        {
            if (grid.CurrentCell == null)
            {
                return -999;
            }
            return grid.CurrentCell.RowIndex;
        }

        private void Info(string text)
        {
            MessageBox.Show(this, text, " Freebuff Controller ", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
        }

        private bool Confirm(string text)
        {
            return MessageBox.Show(this, text, " Подтверди ", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == DialogResult.Yes;
        }

        private void Delay(int ms, Action action)
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = ms;
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                if (!base.IsDisposed)
                {
                    action();
                }
            };
            t.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            FadeInWindow();
            RefreshProxyStatusAsync();
            Delay(600, CheckShareOnStartup);
            Delay(1500, PruneHanhuaBackupsOnStartup);
            Delay(2200, delegate
            {
                RemoveLegacyHanhuaPrefs();
                AutoCleanUnusedFiles(" При старте ");
            });
        }

        private void LaunchIndex(int rowIndex)
        {
            if (Interlocked.CompareExchange(ref hanhuaBusy, 0, 0) == 1)
            {
                SetStatus(" локализация  замена файлов  ·  запись  готово  после  авто  продолжить  Запуск ...");
                RunWhenHanhuaIdle(delegate
                {
                    LaunchIndexNow(rowIndex);
                });
            }
            else if (StartAutoRestoreHanhua(" Перед запуском ", delegate
            {
                LaunchIndexNow(rowIndex);
            }) != RestoreOutcome.Started)
            {
                LaunchIndexNow(rowIndex);
            }
        }

        private void LaunchIndexNow(int rowIndex)
        {
            string what = ((rowIndex == 0) ? " Главная " : (" Инстанс  " + rowIndex));
            int copyFrom = -1;
            if (rowIndex != 0 && !SlotInitialized(rowIndex))
            {
                using (InitModeDialog initModeDialog = new InitModeDialog(rowIndex))
                {
                    if (initModeDialog.ShowDialog(this) != DialogResult.OK)
                    {
                        return;
                    }
                    copyFrom = initModeDialog.CopyFrom;
                }
            }
            if (rowIndex != 0)
            {
                string text = EnsureSharedProjects(rowIndex);
                if (text != null)
                {
                    AskShareAll(text, rowIndex);
                    return;
                }
            }
            try
            {
                if (rowIndex == 0)
                {
                    StartMain();
                }
                else
                {
                    StartSlot(rowIndex, copyFrom);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, what + "  Ошибка запуска:\n " + ex.Message, " Ошибка запуска ", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return;
            }
            if (rowIndex != 0)
            {
                RememberInitMode(rowIndex, copyFrom);
            }
            SetStatus(what + "  запуск... (подтвердится через пару сек) ");
            Delay(6000, delegate
            {
                VerifyLaunched(rowIndex, what);
            });
        }

        private static List<LibTarget> DetectLibs()
        {
            List<LibTarget> list = new List<LibTarget>();
            list.Add(new LibTarget
            {
                Name = " Общая база (все инстансы) ",
                StatePath = DefaultState,
                ProjectsDir = MainProjectsDir()
            });
            for (int i = 1; i <= 9; i++)
            {
                string text = SlotProjectsDir(i);
                if (Directory.Exists(text) && !IsJunction(text))
                {
                    list.Add(new LibTarget
                    {
                        Name = " Инстанс  " + i + "  отдельная база (не общая) ",
                        StatePath = SlotStatePath(i),
                        ProjectsDir = text
                    });
                }
            }
            return list;
        }

        private static List<string> CollectProjectPaths(string statePath, string projectsDir)
        {
            List<string> list = new List<string>();
            HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(statePath))
                {
                    Dictionary<string, object> dictionary = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(statePath));
                    object value;
                    if (dictionary != null && dictionary.TryGetValue("recentProjects", out value))
                    {
                        IEnumerable enumerable = value as IEnumerable;
                        if (enumerable != null)
                        {
                            foreach (object item in enumerable)
                            {
                                string text = Convert.ToString(item);
                                if (!string.IsNullOrEmpty(text) && hashSet.Add(text))
                                {
                                    list.Add(text);
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
            }
            try
            {
                if (Directory.Exists(projectsDir))
                {
                    string[] directories = Directory.GetDirectories(projectsDir);
                    foreach (string path in directories)
                    {
                        string path2 = Path.Combine(path, "project.json");
                        if (!File.Exists(path2))
                        {
                            continue;
                        }
                        Dictionary<string, object> dictionary2 = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path2));
                        object value2;
                        if (dictionary2 != null && dictionary2.TryGetValue("projectPath", out value2))
                        {
                            string text2 = Convert.ToString(value2);
                            if (!string.IsNullOrEmpty(text2) && hashSet.Add(text2))
                            {
                                list.Add(text2);
                            }
                        }
                    }
                }
            }
            catch
            {
            }
            return list;
        }

        private void OpenDeleteThreads()
        {
            try
            {
                using (DeleteThreadsDialog deleteThreadsDialog = new DeleteThreadsDialog())
                {
                    deleteThreadsDialog.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                LogFail(" Не могу открыть удаление сессий ", ex);
                Info(" Не могу открыть удаление сессий :\n" + ex.Message);
            }
        }

        private void VerifyLaunched(int rowIndex, string what)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok;
                try
                {
                    bool mainRunning;
                    HashSet<int> hashSet = QueryRunning(out mainRunning);
                    ok = ((rowIndex == 0) ? mainRunning : hashSet.Contains(rowIndex));
                }
                catch
                {
                    ok = true;
                }
                if (base.IsDisposed || !base.IsHandleCreated)
                {
                    return;
                }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (!base.IsDisposed)
                        {
                            RefreshGrid();
                            if (ok)
                            {
                                SetStatus(what + "  запущен ", ColGreen);
                            }
                            else
                            {
                                SetStatus(what + "  Сбой запуска ", ColNewVersion);
                                MessageBox.Show(this, what + "  процесс  отправлено  Запуск  команда  после  нет  держать . \n\n Причины :\n· Freebuff  завершается ( подожди пару сек )\n·  этот  Инстанс  каталог данных  занят \n·  антивирус  блокирует  Freebuff  Запуск ", " Результат запуска ", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                            }
                        }
                    });
                }
                catch
                {
                }
            });
        }

        private void OnLaunch()
        {
            int num = SelectedIndex();
            if (num == -999)
            {
                Info(" Сначала выбери строку. ");
            }
            else
            {
                LaunchIndex(num);
            }
        }

        private void OnStop()
        {
            int num = SelectedIndex();
            if (num == -999)
            {
                Info(" Сначала выбери строку. ");
                return;
            }
            try
            {
                KillInstances((num == 0) ? "main" : num.ToString());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, " Не остановлен:\n " + ex.Message, " Не остановлен ", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return;
            }
            SetStatus(" Команда останова отправлена... ");
            Delay(3200, RefreshGrid);
        }

        private void OnStopAll()
        {
            int num = 0;
            try
            {
                string[] array = new string[10] { "main", null, null, null, null, null, null, null, null, null };
                for (int i = 1; i <= 9; i++)
                {
                    array[i] = i.ToString();
                }
                num = KillInstances(true, array);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, " Не остановлен:\n " + ex.Message, " Не остановлен ", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return;
            }
            SetStatus((num > 0) ? (" Все команды останова отправлены... ( попутно убиваю  " + num + "  шт. остаточных процессов) ") : " Все команды останова отправлены... ");
            Delay(3200, RefreshGrid);
        }

        private void OnReset()
        {
            int idx = SelectedIndex();
            if (idx == -999)
            {
                Info(" Сначала выбери строку. ");
            }
            else if (idx == 0)
            {
                Info(" Аккаунт главной не сбрасывается здесь. ");
            }
            else if (Confirm(string.Format(" Точно сбросить инстанс {0}?\r\nЛогин и данные удалятся, нужен повторный вход. ", idx)))
            {
                KillInstances(idx.ToString());
                SetStatus(" Сбрасываю инстанс  " + idx + "...");
                Delay(2800, delegate
                {
                    TryDeleteWithRetry(idx, 3);
                });
            }
        }

        private static void TryDeleteDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
            }
        }

        private void TryDeleteWithRetry(int idx, int attemptsLeft)
        {
            TryDeleteDir(SlotStateDir(idx));
            TryDeleteDir(SlotUserData(idx));
            bool flag = !Directory.Exists(SlotStateDir(idx)) && !Directory.Exists(SlotUserData(idx));
            if (flag || attemptsLeft <= 1)
            {
                if (!flag)
                {
                    LogFail(" Сброс инстанса  " + idx + "  каталог не удаляется (занят?) ");
                }
                SetStatus(flag ? (" Инстанс  " + idx + "  сброшен ") : (" Инстанс  " + idx + "  файлы заняты, повтори сброс позже "));
                RefreshGrid();
            }
            else
            {
                Delay(1500, delegate
                {
                    TryDeleteWithRetry(idx, attemptsLeft - 1);
                });
            }
        }

        private static string MainProjectsDir()
        {
            return Path.Combine(Path.GetDirectoryName(DefaultState), "projects");
        }

        private static string SlotProjectsDir(int n)
        {
            return Path.Combine(SlotConfigRoot(n), "projects");
        }

        private static bool IsJunction(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool CreateJunction(string link, string target)
        {
            try
            {
                string directoryName = Path.GetDirectoryName(link);
                if (!string.IsNullOrEmpty(directoryName))
                {
                    Directory.CreateDirectory(directoryName);
                }
                bool flag = Directory.Exists(link);
                int num = CreateJunctionNative(link, target);
                if (IsJunction(link))
                {
                    LastJunctionError = 0;
                    LastJunctionDetail = " DeviceIoControl  Успех ";
                    return true;
                }
                if (!flag)
                {
                    TryDeleteEmptyDir(link);
                }
                string detail;
                bool flag2 = TryMklinkJunction(link, target, out detail);
                LastJunctionError = ((!flag2) ? num : 0);
                LastJunctionDetail = " Ошибка  " + num + "(" + Win32ErrorText(num) + ");" + detail;
                if (!flag2)
                {
                    LogFail(" Не могу создать junction ( " + link + " -> " + target + "):" + LastJunctionDetail);
                }
                return flag2;
            }
            catch (Exception ex)
            {
                LogFail(" Ошибка junction ( " + link + ")", ex);
                return false;
            }
        }

        private static void TryDeleteEmptyDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir) && !IsJunction(dir) && Directory.GetFileSystemEntries(dir).Length <= 0)
                {
                    Directory.Delete(dir, false);
                }
            }
            catch
            {
            }
        }

        private static bool TryMklinkJunction(string link, string target, out string detail)
        {
            detail = null;
            try
            {
                ProcessStartInfo processStartInfo = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"");
                processStartInfo.UseShellExecute = false;
                processStartInfo.CreateNoWindow = true;
                processStartInfo.RedirectStandardOutput = true;
                processStartInfo.RedirectStandardError = true;
                ProcessStartInfo startInfo = processStartInfo;
                using (Process process = Process.Start(startInfo))
                {
                    string text = process.StandardOutput.ReadToEnd().Trim();
                    string text2 = process.StandardError.ReadToEnd().Trim();
                    process.WaitForExit(15000);
                    detail = "mklink rc=" + process.ExitCode + ((text2.Length > 0) ? (" " + text2) : "") + ((text.Length > 0) ? (" " + text) : "");
                }
                return IsJunction(link);
            }
            catch (Exception ex)
            {
                detail = "mklink  Ошибка :" + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static string Win32ErrorText(int code)
        {
            if (code == 0)
            {
                return " Успех ";
            }
            if (code < 0)
            {
                return " Ошибка ";
            }
            try
            {
                return new Win32Exception(code).Message;
            }
            catch
            {
                return "Win32  Ошибка  " + code;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr handle, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private static int CreateJunctionNative(string link, string target)
        {
            IntPtr intPtr = new IntPtr(-1);
            IntPtr intPtr2 = IntPtr.Zero;
            byte[] bytes = Encoding.Unicode.GetBytes("\\??\\" + target);
            byte[] bytes2 = Encoding.Unicode.GetBytes(target);
            // REPARSE_DATA_BUFFER():0 ReparseTag / 4 ReparseDataLength /
            // 6 Reserved / 8 SubstituteNameOffset / 10 SubstituteNameLength /
            // 12 PrintNameOffset / 14 PrintNameLength / 16 PathBuffer(). 
            // ReparseDataLength = 8( шт.  ushort)+ PathBuffer .  старая  запись 
            // 12+num, путь  20( 4 ),  16  чтение  путь  не , 
            //  ERROR_INVALID_REPARSE_DATA(4392). 
            int num = bytes.Length + 2 + bytes2.Length + 2;
            int num2 = 8 + num;
            try
            {
                if (!Directory.Exists(link))
                {
                    Directory.CreateDirectory(link);
                }
                intPtr = CreateFileW(link, 1073741824u, 7u, IntPtr.Zero, 3u, 35651584u, IntPtr.Zero);
                if (intPtr == new IntPtr(-1))
                {
                    return Marshal.GetLastWin32Error();
                }
                byte[] array = new byte[8 + num2];
                BitConverter.GetBytes(2684354563u).CopyTo(array, 0);
                BitConverter.GetBytes((ushort)num2).CopyTo(array, 4);
                BitConverter.GetBytes((ushort)0).CopyTo(array, 8);
                BitConverter.GetBytes((ushort)bytes.Length).CopyTo(array, 10);
                BitConverter.GetBytes((ushort)(bytes.Length + 2)).CopyTo(array, 12);
                BitConverter.GetBytes((ushort)bytes2.Length).CopyTo(array, 14);
                bytes.CopyTo(array, 16);
                bytes2.CopyTo(array, 16 + bytes.Length + 2);
                intPtr2 = Marshal.AllocHGlobal(array.Length);
                Marshal.Copy(array, 0, intPtr2, array.Length);
                uint returned;
                return (!DeviceIoControl(intPtr, 589988u, intPtr2, (uint)array.Length, IntPtr.Zero, 0u, out returned, IntPtr.Zero)) ? Marshal.GetLastWin32Error() : 0;
            }
            catch (Exception ex)
            {
                LogFail(" создать  junction  Ошибка (" + link + ")", ex);
                return -1;
            }
            finally
            {
                if (intPtr2 != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(intPtr2);
                }
                if (intPtr != new IntPtr(-1) && intPtr != IntPtr.Zero)
                {
                    CloseHandle(intPtr);
                }
            }
        }

        private static string MigrateSlotToShared(int n)
        {
            string text = MainProjectsDir();
            string text2 = SlotProjectsDir(n);
            if (IsJunction(text2))
            {
                return null;
            }
            if (!Directory.Exists(text2))
            {
                Directory.CreateDirectory(text);
                if (!CreateJunction(text2, text))
                {
                    return " Инстанс  " + n + ": создать  общий  каталог  ошибка ";
                }
                return null;
            }
            string text3 = SlotDbPath(n);
            string text4 = SlotDbPath(0);
            bool flag = false;
            if (text3 != null)
            {
                if (text4 == null)
                {
                    Directory.CreateDirectory(text);
                    string[] directories = Directory.GetDirectories(text2);
                    foreach (string text5 in directories)
                    {
                        string text6 = Path.Combine(text, Path.GetFileName(text5));
                        if (!Directory.Exists(text6))
                        {
                            try
                            {
                                Directory.Move(text5, text6);
                            }
                            catch
                            {
                            }
                        }
                    }
                    text4 = SlotDbPath(0);
                    flag = true;
                }
                if (!flag && text4 != null && text4 != text3)
                {
                    string text7 = MergeAllInto(text4, n);
                    if (text7 != null)
                    {
                        return text7;
                    }
                }
            }
            string text8 = text2 + ".pre-share-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            bool flag2 = false;
            try
            {
                Directory.Move(text2, text8);
                flag2 = true;
            }
            catch
            {
            }
            if (!CreateJunction(text2, text))
            {
                return " Инстанс  " + n + ": создать  общий  каталог  ошибка " + (flag2 ? ("( каталог в бэкапе  " + Path.GetFileName(text8) + ")") : "( каталог всё ещё занят ,  может  этот  Инстанс  окно  не закрыто чисто ,  позже  нажми ещё раз )");
            }
            return null;
        }

        private static string MergeAllInto(string mainDb, int n)
        {
            string text = null;
            string text2 = null;
            string text3 = null;
            try
            {
                text = SnapshotDb(n);
                if (text == null)
                {
                    return " Инстанс  " + n + ": нет  читаемая  База сессий ";
                }
                string input = RunBunJson(FindBunExe(), ExtractHandoverScript(), "list " + Q(text));
                Dictionary<string, object> dictionary = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(input);
                List<string> list = new List<string>();
                IEnumerable enumerable = ((dictionary != null && dictionary.ContainsKey("threads")) ? (dictionary["threads"] as IEnumerable) : null);
                if (enumerable != null)
                {
                    foreach (object item in enumerable)
                    {
                        Dictionary<string, object> dictionary2 = item as Dictionary<string, object>;
                        if (dictionary2 != null && dictionary2.ContainsKey("id"))
                        {
                            list.Add(Convert.ToString(dictionary2["id"]));
                        }
                    }
                }
                if (list.Count == 0)
                {
                    return null;
                }
                text2 = WriteJsonTempFile(list);
                text3 = WriteJsonTempFile(new Dictionary<string, string>());
                string input2 = RunBunJson(FindBunExe(), ExtractHandoverScript(), "merge " + Q(text) + " " + Q(mainDb) + " @" + Q(text2) + " @" + Q(text3));
                Dictionary<string, object> dictionary3 = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(input2);
                if (dictionary3 == null || !dictionary3.ContainsKey("ok") || !Convert.ToBoolean(dictionary3["ok"]))
                {
                    return " Инстанс  " + n + ": объединить  ошибка " + ((dictionary3 != null && dictionary3.ContainsKey("error")) ? ("(" + Convert.ToString(dictionary3["error"]) + ")") : "");
                }
                return null;
            }
            catch (Exception ex)
            {
                return " Инстанс  " + n + ":" + ex.Message;
            }
            finally
            {
                if (text != null)
                {
                    try
                    {
                        Directory.Delete(Path.GetDirectoryName(text), true);
                    }
                    catch
                    {
                    }
                }
                if (text2 != null)
                {
                    try
                    {
                        File.Delete(text2);
                    }
                    catch
                    {
                    }
                }
                if (text3 != null)
                {
                    try
                    {
                        File.Delete(text3);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static string EnsureSharedProjects(int n)
        {
            if (n == 0)
            {
                Directory.CreateDirectory(MainProjectsDir());
                return null;
            }
            string text = SlotProjectsDir(n);
            if (IsJunction(text))
            {
                return null;
            }
            if (!Directory.Exists(text))
            {
                Directory.CreateDirectory(MainProjectsDir());
                if (!CreateJunction(text, MainProjectsDir()))
                {
                    return " Инстанс  " + n + ": создать  общий  каталог  ошибка ";
                }
                return null;
            }
            return " Инстанс  " + n + "  использует отдельную базу (не объединён). ";
        }

        private void CheckShareOnStartup()
        {
            if (HasUnsharedSlot())
            {
                AskShareAll("", -1);
            }
        }

        private bool HasUnsharedSlot()
        {
            for (int i = 1; i <= 9; i++)
            {
                string path = SlotProjectsDir(i);
                if (Directory.Exists(path) && !IsJunction(path))
                {
                    return true;
                }
            }
            return false;
        }

        private void AskShareAll(string why, int launchIndex)
        {
            List<int> list = new List<int>();
            for (int i = 1; i <= 9; i++)
            {
                string path = SlotProjectsDir(i);
                if (Directory.Exists(path) && !IsJunction(path))
                {
                    list.Add(i);
                }
            }
            if (list.Count == 0)
            {
                if (launchIndex < 0)
                {
                    SetStatus(" Все уже используют главную базу. ");
                    return;
                }
                string text = ((launchIndex == 0) ? " Главная " : (" Инстанс  " + launchIndex));
                SetStatus(text + "  Ошибка запуска: база сессий не подключена ", ColNewVersion);
                MessageBox.Show(this, text + "  Ошибка запуска:\n " + why + "\n\n сессия  общий  каталог  не смог (Windows  junction  / mklink  ошибка ),  Запуск  прерван ,  не  будет  повторные окна инициализации . \n\n сначала  закрой  все  Freebuff  окно  после  повторить ; не  Лог: \n" + Program.FailLogPath, " Ошибка запуска ", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
            else if (FindBunExe() == null)
            {
                Info(" не найден  Bun (Freebuff  каталог  resources\\bun\\bun.exe), \n не могу  объединить базы . ");
            }
            else if (Confirm(why + " есть  " + list.Count + "  шт.  Инстанс  ещё  в  использовать  отдельный  База сессий . \r\n\r\n Главная ,  навсегда  общий \r\n есть  история  будет  объединить  шт. ,  исходный каталог  оставить  бэкап (projects.pre-share-*). \r\n после  все  Инстанс  Всего  шт.  история ,  вход  Аккаунт  отдельный . \r\n нужно  сначала  Стоп все  Инстанс ( запущен  Freebuff  окно  будет  Закрыть ),  продолжить "))
            {
                string[] array = new string[10] { "main", null, null, null, null, null, null, null, null, null };
                for (int j = 1; j <= 9; j++)
                {
                    array[j] = j.ToString();
                }
                pendingLaunchAfterShare = launchIndex;
                KillInstances(array);
                SetStatus(" Обмен: останавливаю все... ");
                Delay(1200, delegate
                {
                    RunShareAllAsync();
                });
            }
        }

        private void RunShareAllAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                int[] array = new int[10];
                for (int i = 0; i <= 9; i++)
                {
                    array[i] = i;
                }
                if (!WaitSlotsStopped(array, 20000))
                {
                    UiSafe(delegate
                    {
                        if (!base.IsDisposed)
                        {
                            SetStatus(" Обмен не выполнен ");
                            pendingLaunchAfterShare = -1;
                            Info(" общий  сессия : есть  Инстанс  нет  в  20  Выход ,  прерван  миграция . \r\n Закрыть  все  Freebuff  окно  после  Запуск  контроллер . ");
                        }
                    });
                }
                else
                {
                    List<string> results = new List<string>();
                    for (int num = 1; num <= 9; num++)
                    {
                        string text = MigrateSlotToShared(num);
                        if (text != null)
                        {
                            results.Add(text);
                        }
                    }
                    UiSafe(delegate
                    {
                        if (!base.IsDisposed)
                        {
                            RefreshGrid();
                            int num2 = pendingLaunchAfterShare;
                            pendingLaunchAfterShare = -1;
                            if (results.Count == 0)
                            {
                                SetStatus(" Обмен готов: все используют главную базу ", ColGreen);
                                if (num2 >= 0)
                                {
                                    LaunchIndex(num2);
                                }
                                else
                                {
                                    Info(" сессия  общий  готово  ✓\r\n\r\n все  Инстанс  в  Всего  Главная  База сессий ( шт.  история ), \r\n вход  Аккаунт  отдельный ,  есть  Лимит . \r\n\r\n: только  в  шт.  окно -- шт.  Инстанс  запись \r\n шт.  может (WAL  не  будет ). ");
                                }
                            }
                            else
                            {
                                SetStatus(" Обмен частичный ");
                                Info(" Обмен:\r\n " + string.Join("\r\n", results) + ((num2 >= 0) ? "\r\n\r\n миграция  не  все  Успех ,  позже  нажми ещё раз  Запуск . " : ""));
                            }
                        }
                    });
                }
            });
        }

        private static string InstanceTitle(int i)
        {
            if (i != 0)
            {
                return " Инстанс  " + i;
            }
            return " Главная ";
        }

        private static string SlotConfigRoot(int i)
        {
            if (i != 0)
            {
                return Path.GetDirectoryName(SlotStatePath(i));
            }
            return Path.GetDirectoryName(DefaultState);
        }

        private static string SlotDbPath(int i)
        {
            try
            {
                string path = Path.Combine(SlotConfigRoot(i), "projects");
                if (!Directory.Exists(path))
                {
                    return null;
                }
                string[] directories = Directory.GetDirectories(path);
                foreach (string path2 in directories)
                {
                    string text = Path.Combine(path2, "desktop-v2.db");
                    if (File.Exists(text))
                    {
                        return text;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static string FindBunExe()
        {
            string text = Path.Combine(Path.GetDirectoryName(FreebuffExe), "resources\\bun\\bun.exe");
            if (!File.Exists(text))
            {
                return null;
            }
            return text;
        }

        private static string ExtractHandoverScript()
        {
            string text = Path.Combine(Path.GetTempPath(), "freebuff-controller");
            Directory.CreateDirectory(text);
            string text2 = Path.Combine(text, "handover-merge.js");
            string text3 = Encoding.UTF8.GetString(Convert.FromBase64String("Ly8gRnJlZWJ1ZmYg5aSa5byA5o6n5Yi25ZmoIOKAlCDkvJror53mjqXlipvlkIjlubbohJrmnKzjgIIKLy8KLy8g55SoIEZyZWVidWZmIOiHquW4pueahCByZXNvdXJjZXMvYnVuL2J1bi5leGUg6L+Q6KGM77yaYnVuOnNxbGl0ZSDnm7Tor7vkuKTkuKrlrp7kvovnmoQKLy8gZGVza3RvcC12Mi5kYu+8jOaKiumAieWumuS8muivne+8iHRocmVhZHMgKyBtZXNzYWdlcyArIHF1ZXVlX2l0ZW1zICsKLy8gYXV0b19ydW5fZGVjaXNpb25fcmVjZWlwdHMgKyB0aHJlYWRfZGVsaXZlcmllc++8ieS7juadpea6kOW6k+WkjeWItui/m+ebruagh+W6k+OAggovLyDmjqfliLblmajoh6rouqvkv53mjIHml6AgU1FMaXRlIOS+nei1lueahOWNleaWh+S7tiBleGXjgIIKLy8KLy8g55So5rOV77yaCi8vICAgYnVuIGhhbmRvdmVyLW1lcmdlLmpzIGxpc3QgIDxzcmNEYj4KLy8gICBidW4gaGFuZG92ZXItbWVyZ2UuanMgbWVyZ2UgPHNyY0RiPiA8ZHN0RGI+IDxpZHNKc29ufEBpZHMuanNvbj4gW3JlbmFtZXNKc29ufEByZW5hbWVzLmpzb25dCi8vIGlkcy9yZW5hbWVzIOebtOaOpeS8oCBKU09OIOaIluS8oCAiQOi3r+W+hCLvvIjmjqfliLblmajotbDmlofku7bvvIzpgb/lvIDlkb3ku6TooYzovazkuYnvvInjgIIKLy8g6L6T5Ye65LiA6KGMIEpTT07vvIhVVEYtOO+8jHN0ZG91dO+8ie+8mgovLyAgIHsib2siOnRydWUsImFjdGlvbiI6Imxpc3QiLCJ0aHJlYWRzIjpbLi4uXX0KLy8gICB7Im9rIjp0cnVlLCJhY3Rpb24iOiJtZXJnZSIsImNvcGllZCI6Wy4uLl0sInNraXBwZWQiOlsuLi5dfQovLyAgIHsib2siOmZhbHNlLCJlcnJvciI6Ii4uLiJ9Ci8vIOS7u+S9lei3r+W+hOW8guW4uOmDvei1sCBvazpmYWxzZe+8m21lcmdlIOWcqOWNleS6i+WKoemHjOWujOaIkO+8jOWksei0peWNs+aVtOS9k+Wbnua7muOAggovLwovLyDlpI3liLbop4TliJnvvJoKLy8gLSDluYLnrYnvvJrnm67moIflupPlt7LmnInnmoQgdGhyZWFkIGlkIOS4gOW+i+i3s+i/h++8jOe7neS4jeimhuebluOAggovLyAtIOW3peS9nOWMuuino+iApu+8mnRocmVhZCDmjIflkJHnm67moIflupPkuK3lkIzkuIAgcm9vdF9wYXRoIOeahCBwcm9qZWN0cyDooYzvvIjnvLrlpLHml7YKLy8gICDoh6rliqjliJvlu7rvvIzov5nmmK/kvJror53lpJbplK4gcHJvamVjdF9pZCDnmoTlvZLlsZ7vvInvvIzmnaXmupAv55uu5qCH5omT5byA5ZOq5Liq5bel5L2c5Yy6Ci8vICAg5LqS5LiN5b2x5ZON44CCCi8vIC0g5byV5pOO56eB5pyJ54q25oCB5riF6Zu277yIdHVybl9zdGF0ZSAvIGhhcm5lc3Nfc3RhdGUgLyBhdXRvX3J1biDotKbmnKwgLwovLyAgIHNwb25zb3JlZCDku6TniYwgLyBmcmVlYnVmZl9pbnN0YW5jZV9pZCAvIGF0dGVudGlvbiDmnKror7sgLyB3b3JsZF9zbmFwc2hvdO+8ie+8jAovLyAgIOaOpei/h+WOu+eahOi0puWPt+S7juW5suWHgOeahOOAjOepuumXsuOAjeS8muivnee7p+e7re+8jOS4jeiDjOS4iuS4gOi0puWPt+eahOi/kOihjOaXtuasoOi0puOAggovLyAtIOWIl+eZveWQjeWNle+8muaJgOaciSBJTlNFUlQg5Y+q5YaZ55uu5qCH5bqT55yf5a6e5a2Y5Zyo55qE5YiX77yIUFJBR01BIOS6pOmbhu+8ie+8jAovLyAgIEZyZWVidWZmIOeJiOacrOabtOabv+WinuWIoOWIl+aXtuS4jeS8muaLvOWHuuWdjyBTUUzvvJvnm67moIflupPoh6rouqvnmoTliJfov4Hnp7vkuqTnu5kKLy8gICBvcmNoZXN0cmF0b3Ig5ZCv5Yqo5pe255qEIHVwZ3JhZGUg5rWB56iL44CCCgp2YXIgRGF0YWJhc2UgPSBnbG9iYWxUaGlzLkRhdGFiYXNlIHx8IHJlcXVpcmUoImJ1bjpzcWxpdGUiKS5EYXRhYmFzZTsKCmZ1bmN0aW9uIG91dChvYmopIHsKICBwcm9jZXNzLnN0ZG91dC53cml0ZShKU09OLnN0cmluZ2lmeShvYmopICsgIlxuIik7Cn0KCi8vIGFyZ3ZbaV3vvJrlhoXogZQgSlNPTu+8jOaIliAiQGZpbGUi77yI6K+75paH5Lu26YeM55qEIEpTT07vvInjgIIKZnVuY3Rpb24gYXJnSnNvbihpLCBmYWxsYmFjaykgewogIHZhciB2ID0gcHJvY2Vzcy5hcmd2W2ldOwogIGlmICghdikgcmV0dXJuIGZhbGxiYWNrOwogIGlmICh2LmNoYXJDb2RlQXQoMCkgPT09IDY0KSB7CiAgICB2YXIgZnMgPSByZXF1aXJlKCJmcyIpOwogICAgcmV0dXJuIEpTT04ucGFyc2UoZnMucmVhZEZpbGVTeW5jKHYuc2xpY2UoMSksICJ1dGY4IikpOwogIH0KICByZXR1cm4gSlNPTi5wYXJzZSh2KTsKfQoKZnVuY3Rpb24gZGllKG1zZykgewogIG91dCh7IG9rOiBmYWxzZSwgZXJyb3I6IFN0cmluZyhtc2cpIH0pOwogIHByb2Nlc3MuZXhpdCgwKTsgLy8g5o6n5Yi25Zmo5Y+q6Kej5p6QIHN0ZG91dCBKU09O77yM6YCA5Ye656CB5peg5oSP5LmJCn0KCi8vIOWPquivu+aJk+W8gO+8m+S4h+S4gCBidW4g55qE6YCJ6aG55ZCN5a+55LiN5LiK77yM6YCA5Zue5pmu6YCa5omT5byA77yI5paH5Lu25LuN5Y+v6K+777yJ44CCCmZ1bmN0aW9uIG9wZW5STyhwYXRoKSB7CiAgdHJ5IHsKICAgIHJldHVybiBuZXcgRGF0YWJhc2UocGF0aCwgeyByZWFkb25seTogdHJ1ZSB9KTsKICB9IGNhdGNoIChlKSB7CiAgICByZXR1cm4gbmV3IERhdGFiYXNlKHBhdGgpOwogIH0KfQoKZnVuY3Rpb24gdGFibGVDb2xzKGRiLCB0YWJsZSkgewogIHJldHVybiBkYi5xdWVyeSgiUFJBR01BIHRhYmxlX2luZm8oIiArIHRhYmxlICsgIikiKS5hbGwoKS5tYXAoZnVuY3Rpb24gKGMpIHsKICAgIHJldHVybiBjLm5hbWU7CiAgfSk7Cn0KCi8vIOaKiiByb3dPYmog5pS256qE5YiwIGRzdENvbHMg6YeM5a2Y5Zyo55qE5YiX5ZCOIElOU0VSVCBPUiBJR05PUkXjgIIKZnVuY3Rpb24gaW5zZXJ0Um93KGRiLCB0YWJsZSwgcm93T2JqLCBkc3RDb2xzKSB7CiAgdmFyIGNvbHMgPSBbXTsKICB2YXIgcGFyYW1zID0ge307CiAgZm9yICh2YXIgayBpbiByb3dPYmopIHsKICAgIGlmIChkc3RDb2xzLmluZGV4T2YoaykgPCAwKSBjb250aW51ZTsKICAgIGNvbHMucHVzaChrKTsKICAgIHBhcmFtc1siJCIgKyBrXSA9IHJvd09ialtrXTsKICB9CiAgaWYgKGNvbHMubGVuZ3RoID09PSAwKSByZXR1cm47CiAgdmFyIHEgPSAiSU5TRVJUIE9SIElHTk9SRSBJTlRPICIgKyB0YWJsZSArICIgKCIgKyBjb2xzLmpvaW4oIiwgIikgKwogICAgIikgVkFMVUVTICgiICsgY29scy5tYXAoZnVuY3Rpb24gKGMpIHsgcmV0dXJuICIkIiArIGM7IH0pLmpvaW4oIiwgIikgKyAiKSI7CiAgZGIucXVlcnkocSkucnVuKHBhcmFtcyk7Cn0KCmZ1bmN0aW9uIGxpc3RUaHJlYWRzKHNyY1BhdGgpIHsKICB2YXIgc3JjID0gb3BlblJPKHNyY1BhdGgpOwogIHRyeSB7CiAgICB2YXIgY291bnRzID0ge307CiAgICB2YXIgbWMgPSBzcmMucXVlcnkoCiAgICAgICJTRUxFQ1QgdGhyZWFkX2lkLCBDT1VOVCgqKSBBUyBuIEZST00gbWVzc2FnZXMgR1JPVVAgQlkgdGhyZWFkX2lkIgogICAgKTsKICAgIGZvciAodmFyIHIgb2YgbWMuYWxsKCkpIGNvdW50c1tyLnRocmVhZF9pZF0gPSByLm47CiAgICB2YXIgdGhyZWFkcyA9IFtdOwogICAgdmFyIHJvd3MgPSBzcmMucXVlcnkoCiAgICAgICJTRUxFQ1QgaWQsIHRpdGxlLCBzdGF0dXMsIHR1cm5fc3RhdGUsIG1vZGVsLCBwcm9qZWN0X3BhdGgsIHVwZGF0ZWRfYXQiICsKICAgICAgIiBGUk9NIHRocmVhZHMgT1JERVIgQlkgdXBkYXRlZF9hdCBERVNDIgogICAgKS5hbGwoKTsKICAgIGZvciAodmFyIHQgb2Ygcm93cykgewogICAgICB0aHJlYWRzLnB1c2goewogICAgICAgIGlkOiB0LmlkLAogICAgICAgIHRpdGxlOiB0LnRpdGxlLAogICAgICAgIHN0YXR1czogdC5zdGF0dXMsCiAgICAgICAgdHVyblN0YXRlOiB0LnR1cm5fc3RhdGUsCiAgICAgICAgbW9kZWw6IHQubW9kZWwsCiAgICAgICAgcHJvamVjdFBhdGg6IHQucHJvamVjdF9wYXRoLAogICAgICAgIG1lc3NhZ2VzOiBjb3VudHNbdC5pZF0gfHwgMCwKICAgICAgICB1cGRhdGVkOiB0LnVwZGF0ZWRfYXQsCiAgICAgIH0pOwogICAgfQogICAgb3V0KHsgb2s6IHRydWUsIGFjdGlvbjogImxpc3QiLCB0aHJlYWRzOiB0aHJlYWRzIH0pOwogIH0gZmluYWxseSB7CiAgICBzcmMuY2xvc2UoKTsKICB9Cn0KCi8vIOehruS/neebruagh+W6k+WtmOWcqCByb290X3BhdGgg5a+55bqU55qEIHByb2plY3RzIOihjOW5tui/lOWbnuWFtiBpZOOAguato+W4uOaDheWGteS4i+ebruaghwovLyDlrp7kvovoh6rlt7HmiZPlvIDov4flkIzkuIDkuKrlt6XkvZzljLrjgIHooYzlt7LlrZjlnKjvvJvnvLrlpLHml7booaXkuIDooYzvvIjkvJjlhYjmsr/nlKjmnaXmupDnmoQKLy8gcHJvamVjdF9pZOKAlOKAlOWug+eUsei3r+W+hOa0vueUn++8jOWQjOS4gOWPsOacuuWZqOS4iuS4jeS8muWPmO+8m2lkIOaSnui9puaXtuaNoumaj+acuiBpZO+8ieOAggpmdW5jdGlvbiBlbnN1cmVQcm9qZWN0KGRzdCwgcm9vdFBhdGgsIHByZWZlcnJlZElkKSB7CiAgdmFyIGZvdW5kID0gZHN0CiAgICAucXVlcnkoIlNFTEVDVCBpZCBGUk9NIHByb2plY3RzIFdIRVJFIHJvb3RfcGF0aCA9ICRwIikKICAgIC5nZXQoeyAkcDogcm9vdFBhdGggfSk7CiAgaWYgKGZvdW5kKSByZXR1cm4gZm91bmQuaWQ7CiAgaWYgKHByZWZlcnJlZElkKSB7CiAgICB0cnkgewogICAgICBkc3QucXVlcnkoCiAgICAgICAgIklOU0VSVCBPUiBJR05PUkUgSU5UTyBwcm9qZWN0cyAoaWQsIHJvb3RfcGF0aCwgZGVmYXVsdF9icmFuY2gsIGNyZWF0ZWRfYXQpIiArCiAgICAgICAgIiBWQUxVRVMgKCRpZCwgJHJwLCAkZGIsICRjYSkiCiAgICAgICkucnVuKHsgJGlkOiBwcmVmZXJyZWRJZCwgJHJwOiByb290UGF0aCwgJGRiOiAibWFpbiIsICRjYTogRGF0ZS5ub3coKSB9KTsKICAgIH0gY2F0Y2ggKGUpIHsgfQogICAgZm91bmQgPSBkc3QKICAgICAgLnF1ZXJ5KCJTRUxFQ1QgaWQgRlJPTSBwcm9qZWN0cyBXSEVSRSByb290X3BhdGggPSAkcCIpCiAgICAgIC5nZXQoeyAkcDogcm9vdFBhdGggfSk7CiAgICBpZiAoZm91bmQpIHJldHVybiBmb3VuZC5pZDsKICB9CiAgdmFyIG5pZCA9IGNyeXB0by5yYW5kb21VVUlEKCk7CiAgZHN0LnF1ZXJ5KAogICAgIklOU0VSVCBJTlRPIHByb2plY3RzIChpZCwgcm9vdF9wYXRoLCBkZWZhdWx0X2JyYW5jaCwgY3JlYXRlZF9hdCkiICsKICAgICIgVkFMVUVTICgkaWQsICRycCwgJGRiLCAkY2EpIgogICkucnVuKHsgJGlkOiBuaWQsICRycDogcm9vdFBhdGgsICRkYjogIm1haW4iLCAkY2E6IERhdGUubm93KCkgfSk7CiAgcmV0dXJuIG5pZDsKfQoKZnVuY3Rpb24gbWVyZ2VUaHJlYWRzKHNyY1BhdGgsIGRzdFBhdGgsIGlkcywgcmVuYW1lcykgewogIGlmICghQXJyYXkuaXNBcnJheShpZHMpIHx8IGlkcy5sZW5ndGggPT09IDApIGRpZSgi5rKh5pyJ6KaB5o6l5Yqb55qE5Lya6K+dIik7CiAgaWYgKCFkc3RQYXRoIHx8IGRzdFBhdGggPT09IHNyY1BhdGgpIGRpZSgi55uu5qCH5bqT57y65aSx5oiW5LiO5p2l5rqQ55u45ZCMIik7CiAgaWYgKCFyZW5hbWVzIHx8IHR5cGVvZiByZW5hbWVzICE9PSAib2JqZWN0IikgcmVuYW1lcyA9IHt9OwoKICB2YXIgc3JjID0gb3BlblJPKHNyY1BhdGgpOwogIHZhciBkc3QgPSBuZXcgRGF0YWJhc2UoZHN0UGF0aCk7CiAgdmFyIGNvcGllZCA9IFtdOwogIHZhciBza2lwcGVkID0gW107CiAgdHJ5IHsKICAgIHZhciBzcmNUaHJlYWRDb2xzID0gdGFibGVDb2xzKHNyYywgInRocmVhZHMiKTsKICAgIHZhciBkc3RUaHJlYWRDb2xzID0gdGFibGVDb2xzKGRzdCwgInRocmVhZHMiKTsKICAgIHZhciBkc3RNc2dDb2xzID0gdGFibGVDb2xzKGRzdCwgIm1lc3NhZ2VzIik7CiAgICB2YXIgZHN0UXVldWVDb2xzID0gdGFibGVDb2xzKGRzdCwgInF1ZXVlX2l0ZW1zIik7CiAgICB2YXIgZHN0UmVjZWlwdENvbHMgPSB0YWJsZUNvbHMoZHN0LCAiYXV0b19ydW5fZGVjaXNpb25fcmVjZWlwdHMiKTsKICAgIHZhciBkc3REZWxpdkNvbHMgPSB0YWJsZUNvbHMoZHN0LCAidGhyZWFkX2RlbGl2ZXJpZXMiKTsKCiAgICB2YXIgcHJvakNhY2hlID0ge307CiAgICB2YXIgZHN0VGhyZWFkU3RtdCA9IG51bGw7IC8vIOavj+ihjOWIl+mbhuWPr+iDveS4jeWQjO+8jOmAkOihjOaehOW7ugoKICAgIGRzdC50cmFuc2FjdGlvbihmdW5jdGlvbiAoKSB7CiAgICAgIGZvciAodmFyIGlkIG9mIGlkcykgewogICAgICAgIHZhciB0aCA9IHNyYwogICAgICAgICAgLnF1ZXJ5KCJTRUxFQ1QgKiBGUk9NIHRocmVhZHMgV0hFUkUgaWQgPSAkaWQiKQogICAgICAgICAgLmdldCh7ICRpZDogaWQgfSk7CiAgICAgICAgaWYgKCF0aCkgewogICAgICAgICAgc2tpcHBlZC5wdXNoKGlkKTsKICAgICAgICAgIGNvbnRpbnVlOwogICAgICAgIH0KICAgICAgICB2YXIgZXhpc3RzID0gZHN0CiAgICAgICAgICAucXVlcnkoIlNFTEVDVCAxIEZST00gdGhyZWFkcyBXSEVSRSBpZCA9ICRpZCIpCiAgICAgICAgICAuZ2V0KHsgJGlkOiBpZCB9KTsKICAgICAgICBpZiAoZXhpc3RzKSB7CiAgICAgICAgICBza2lwcGVkLnB1c2goaWQpOyAvLyDluYLnrYnvvJrlkIwgaWQg5Lya6K+d57ud5LiN6KaG55uWCiAgICAgICAgICBjb250aW51ZTsKICAgICAgICB9CgogICAgICAgIHZhciByb3cgPSB7fTsKICAgICAgICBmb3IgKHZhciBjb2wgb2Ygc3JjVGhyZWFkQ29scykgcm93W2NvbF0gPSB0aFtjb2xdOwoKICAgICAgICAvLyDlvJXmk47np4HmnInnirbmgIHmuIXpm7bvvJvnm67moIflupPmsqHmnInlr7nlupTliJfml7YgaW5zZXJ0Um93IOS8muiHquWKqOS4ouW8g+OAggogICAgICAgIHJvdy5wcm9qZWN0X2lkID0gZW5zdXJlUHJvamVjdChkc3QsIHRoLnByb2plY3RfcGF0aCwgdGgucHJvamVjdF9pZCk7CiAgICAgICAgcm93LnR1cm5fc3RhdGUgPSAiaWRsZSI7CiAgICAgICAgcm93LnF1ZXVlX3BhdXNlZCA9IDA7CiAgICAgICAgcm93LmF1dG9fcnVuID0gMDsKICAgICAgICByb3cuYXV0b19ydW5fc3RhcnRlZF9hdCA9IG51bGw7CiAgICAgICAgcm93LmF1dG9fcnVuX3Bhc3NfY291bnQgPSAwOwogICAgICAgIHJvdy5hdXRvX3J1bl9yZWZpbmVtZW50X2NvdW50ID0gMDsKICAgICAgICByb3cuYXV0b19ydW5fZGVjaXNpb25fY291bnQgPSAwOwogICAgICAgIHJvdy5hdXRvX3J1bl9zdG9wcGVkX25vdGUgPSBudWxsOwogICAgICAgIHJvdy5hdXRvX3J1bl9zdG9wcGVkX2F0ID0gbnVsbDsKICAgICAgICByb3cuaGFybmVzc19zdGF0ZSA9IG51bGw7CiAgICAgICAgcm93Lmhhcm5lc3Nfc3RhdGVfaWQgPSBudWxsOwogICAgICAgIHJvdy53b3JsZF9zbmFwc2hvdCA9IG51bGw7CiAgICAgICAgcm93LmZyZWVidWZmX2luc3RhbmNlX2lkID0gbnVsbDsKICAgICAgICByb3cuc3BvbnNvcmVkID0gbnVsbDsKICAgICAgICByb3cuc3BvbnNvcmVkX3J1bl90b2tlbiA9IG51bGw7CiAgICAgICAgcm93LnNwb25zb3JlZF9zZXR0bGVkX2F0ID0gbnVsbDsKICAgICAgICByb3cuc3BvbnNvcmVkX3Rlcm1pbmFsX3JlcG9ydHMgPSBudWxsOwogICAgICAgIHJvdy5zcG9uc29yZWRfdGVybWluYWxfYWNrX2F0ID0gbnVsbDsKICAgICAgICByb3cucGVuZGluZ19icmllZnMgPSBudWxsOwogICAgICAgIHJvdy5wZW5kaW5nX2JyaWVmc19kaWFnbm9zdGljX2tleSA9IG51bGw7CiAgICAgICAgcm93LmF0dGVudGlvbl9hY2tub3dsZWRnZWRfcmV2aXNpb24gPSByb3cuYXR0ZW50aW9uX3JldmlzaW9uIHx8IDA7CiAgICAgICAgcm93LmF0dGVudGlvbl9yZWFzb24gPSBudWxsOwogICAgICAgIHJvdy5hdHRlbnRpb25fYXQgPSBudWxsOwogICAgICAgIHJvdy5sYXN0X3R1cm5fb3V0Y29tZSA9IG51bGw7CiAgICAgICAgaWYgKHJlbmFtZXNbaWRdKSByb3cudGl0bGUgPSBTdHJpbmcocmVuYW1lc1tpZF0pLnNsaWNlKDAsIDIwMCk7CiAgICAgICAgcm93LnVwZGF0ZWRfYXQgPSBEYXRlLm5vdygpOwoKICAgICAgICBpbnNlcnRSb3coZHN0LCAidGhyZWFkcyIsIHJvdywgZHN0VGhyZWFkQ29scyk7CiAgICAgICAgY29waWVkLnB1c2goaWQpOwoKICAgICAgICBmb3IgKHZhciBtIG9mIHNyYwogICAgICAgICAgLnF1ZXJ5KCJTRUxFQ1QgKiBGUk9NIG1lc3NhZ2VzIFdIRVJFIHRocmVhZF9pZCA9ICRpZCBPUkRFUiBCWSBzZXEiKQogICAgICAgICAgLmFsbCh7ICRpZDogaWQgfSkpIHsKICAgICAgICAgIGluc2VydFJvdyhkc3QsICJtZXNzYWdlcyIsIG0sIGRzdE1zZ0NvbHMpOwogICAgICAgIH0KCiAgICAgICAgZm9yICh2YXIgcWkgb2Ygc3JjCiAgICAgICAgICAucXVlcnkoIlNFTEVDVCAqIEZST00gcXVldWVfaXRlbXMgV0hFUkUgdGhyZWFkX2lkID0gJGlkIikKICAgICAgICAgIC5hbGwoeyAkaWQ6IGlkIH0pKSB7CiAgICAgICAgICB2YXIgc3QgPSBTdHJpbmcocWkuc3RhdGUgfHwgIiIpLnRvTG93ZXJDYXNlKCk7CiAgICAgICAgICBpZiAoc3QgPT09ICJydW5uaW5nIiB8fCBzdCA9PT0gImNsYWltZWQiKSBjb250aW51ZTsgLy8g5LiK5LiA6LSm5Y+355qE6L+Q6KGM5pe25q6L55WZCiAgICAgICAgICBpbnNlcnRSb3coZHN0LCAicXVldWVfaXRlbXMiLCBxaSwgZHN0UXVldWVDb2xzKTsKICAgICAgICB9CgogICAgICAgIGZvciAodmFyIHJjIG9mIHNyYwogICAgICAgICAgLnF1ZXJ5KAogICAgICAgICAgICAiU0VMRUNUICogRlJPTSBhdXRvX3J1bl9kZWNpc2lvbl9yZWNlaXB0cyBXSEVSRSB0aHJlYWRfaWQgPSAkaWQiCiAgICAgICAgICApCiAgICAgICAgICAuYWxsKHsgJGlkOiBpZCB9KSkgewogICAgICAgICAgaW5zZXJ0Um93KGRzdCwgImF1dG9fcnVuX2RlY2lzaW9uX3JlY2VpcHRzIiwgcmMsIGRzdFJlY2VpcHRDb2xzKTsKICAgICAgICB9CgogICAgICAgIGZvciAodmFyIGR2IG9mIHNyYwogICAgICAgICAgLnF1ZXJ5KCJTRUxFQ1QgKiBGUk9NIHRocmVhZF9kZWxpdmVyaWVzIFdIRVJFIHRocmVhZF9pZCA9ICRpZCIpCiAgICAgICAgICAuYWxsKHsgJGlkOiBpZCB9KSkgewogICAgICAgICAgaW5zZXJ0Um93KGRzdCwgInRocmVhZF9kZWxpdmVyaWVzIiwgZHYsIGRzdERlbGl2Q29scyk7CiAgICAgICAgfQogICAgICB9CiAgICB9KSgpOwogIH0gZmluYWxseSB7CiAgICB0cnkgeyBzcmMuY2xvc2UoKTsgfSBjYXRjaCAoZSkgeyB9CiAgICB0cnkgeyBkc3QuY2xvc2UoKTsgfSBjYXRjaCAoZSkgeyB9CiAgfQogIG91dCh7IG9rOiB0cnVlLCBhY3Rpb246ICJtZXJnZSIsIGNvcGllZDogY29waWVkLCBza2lwcGVkOiBza2lwcGVkIH0pOwp9Cgp0cnkgewogIHZhciBtb2RlID0gcHJvY2Vzcy5hcmd2WzJdOwogIGlmIChtb2RlID09PSAibGlzdCIpIHsKICAgIGlmICghcHJvY2Vzcy5hcmd2WzNdKSBkaWUoIue8uuWwkeadpea6kOW6k+i3r+W+hCIpOwogICAgbGlzdFRocmVhZHMocHJvY2Vzcy5hcmd2WzNdKTsKICB9IGVsc2UgaWYgKG1vZGUgPT09ICJtZXJnZSIpIHsKICAgIGlmICghcHJvY2Vzcy5hcmd2WzNdIHx8ICFwcm9jZXNzLmFyZ3ZbNF0pIGRpZSgi57y65bCR5p2l5rqQL+ebruagh+W6k+i3r+W+hCIpOwogICAgbWVyZ2VUaHJlYWRzKAogICAgICBwcm9jZXNzLmFyZ3ZbM10sCiAgICAgIHByb2Nlc3MuYXJndls0XSwKICAgICAgYXJnSnNvbig1LCBbXSksCiAgICAgIGFyZ0pzb24oNiwge30pCiAgICApOwogIH0gZWxzZSB7CiAgICBkaWUoInVua25vd24gbW9kZTogIiArIG1vZGUpOwogIH0KfSBjYXRjaCAoZSkgewogIGRpZShlICYmIGUubWVzc2FnZSA/IGUubWVzc2FnZSA6IFN0cmluZyhlKSk7Cn0K"));
            try
            {
                if (File.Exists(text2) && File.ReadAllText(text2) == text3)
                {
                    return text2;
                }
            }
            catch
            {
            }
            File.WriteAllText(text2, text3, new UTF8Encoding(false));
            return text2;
        }

        private static string SnapshotDb(int i)
        {
            string text = SlotDbPath(i);
            if (text == null)
            {
                return null;
            }
            string text2 = Path.Combine(Path.GetTempPath(), "freebuff-controller\\handover-" + i + "-" + DateTime.Now.Ticks);
            try
            {
                Directory.CreateDirectory(text2);
                string text3 = Path.Combine(text2, "desktop-v2.db");
                string[] array = new string[3] { "", "-wal", "-shm" };
                foreach (string text4 in array)
                {
                    try
                    {
                        if (File.Exists(text + text4))
                        {
                            File.Copy(text + text4, text3 + text4, true);
                        }
                    }
                    catch
                    {
                    }
                }
                return text3;
            }
            catch
            {
                return null;
            }
        }

        private static string Q(string s)
        {
            return "\"" + s + "\"";
        }

        private static string RunBunJson(string bunExe, string script, string args)
        {
            ProcessStartInfo processStartInfo = new ProcessStartInfo(bunExe, Q(script) + " " + args);
            processStartInfo.UseShellExecute = false;
            processStartInfo.CreateNoWindow = true;
            processStartInfo.RedirectStandardOutput = true;
            processStartInfo.RedirectStandardError = true;
            processStartInfo.StandardOutputEncoding = Encoding.UTF8;
            processStartInfo.StandardErrorEncoding = Encoding.UTF8;
            ProcessStartInfo startInfo = processStartInfo;
            using (Process process = Process.Start(startInfo))
            {
                string text = process.StandardOutput.ReadToEnd();
                string text2 = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(30000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                    }
                    throw new ApplicationException("bun  таймаут ");
                }
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new ApplicationException("bun  нет  вывод " + (string.IsNullOrEmpty(text2) ? "" : (": " + text2.Trim())));
                }
                return text.Trim();
            }
        }

        private static string WriteJsonTempFile(object obj)
        {
            string text = Path.Combine(Path.GetTempPath(), "freebuff-controller");
            Directory.CreateDirectory(text);
            string text2 = Path.Combine(text, "handover-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(text2, new JavaScriptSerializer().Serialize(obj), new UTF8Encoding(false));
            return text2;
        }

        private static bool WaitSlotsStopped(int[] slots, int timeoutMs)
        {
            for (int i = 0; i < timeoutMs; i += 300)
            {
                bool mainRunning;
                HashSet<int> hashSet = QueryRunning(out mainRunning);
                bool flag = false;
                foreach (int num in slots)
                {
                    flag |= ((num == 0) ? mainRunning : hashSet.Contains(num));
                }
                if (!flag)
                {
                    return true;
                }
                Thread.Sleep(300);
            }
            return false;
        }

        private static void StartMain()
        {
            string text = LaunchProxyUrl();
            RememberLaunchProxy(0, text);
            if (text == null)
            {
                ProcessStartInfo processStartInfo = new ProcessStartInfo(FreebuffExe);
                processStartInfo.UseShellExecute = true;
                Process.Start(processStartInfo);
            }
            else
            {
                ProcessStartInfo processStartInfo2 = new ProcessStartInfo(FreebuffExe);
                processStartInfo2.UseShellExecute = false;
                ProcessStartInfo processStartInfo3 = processStartInfo2;
                ApplyLaunchProxy(processStartInfo3, text);
                Process.Start(processStartInfo3);
            }
        }

        private static void StartSlot(int n, int copyFrom)
        {
            string text = SlotStatePath(n);
            if (!File.Exists(text))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(text));
                string text2 = ((copyFrom <= 0) ? DefaultState : SlotStatePath(copyFrom));
                if (copyFrom >= 0 && File.Exists(text2))
                {
                    try
                    {
                        File.Copy(text2, text);
                    }
                    catch
                    {
                        try
                        {
                            File.Delete(text);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            ProcessStartInfo processStartInfo = new ProcessStartInfo();
            processStartInfo.FileName = FreebuffExe;
            processStartInfo.UseShellExecute = false;
            processStartInfo.Arguments = "--user-data-dir=\"" + SlotUserData(n) + "\"";
            processStartInfo.EnvironmentVariables["FREEBUFF_DESKTOP_STATE_PATH"] = text;
            string url = LaunchProxyUrl();
            ApplyLaunchProxy(processStartInfo, url);
            RememberLaunchProxy(n, url);
            Process.Start(processStartInfo);
        }

        private static void KillInstances(params string[] targets)
        {
            KillInstances(false, targets);
        }

        private static int KillInstances(bool sweepOrphans, params string[] targets)
        {
            List<int> list = new List<int>();
            try
            {
                using (ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE Name='Freebuff.exe'"))
                {
                    foreach (ManagementObject item in managementObjectSearcher.Get())
                    {
                        if (!IsOwnFreebuffProcess(item))
                        {
                            continue;
                        }
                        string text = item["CommandLine"] as string;
                        if (string.IsNullOrEmpty(text))
                        {
                            continue;
                        }
                        Match match = SlotRegex.Match(text);
                        string text2 = (match.Success ? match.Groups[1].Value : "main");
                        foreach (string text3 in targets)
                        {
                            if (text3 == text2)
                            {
                                list.Add((int)(uint)item["ProcessId"]);
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogFail(" перечисление  Freebuff  Инстанс  ошибка ( Стоп  может  не  чисто остановлен )", ex);
            }
            list.Sort();
            List<ProcRow> list2 = SnapshotProcessTable();
            bool flag = false;
            foreach (int item2 in list)
            {
                flag |= CloseMainWindowOf(item2);
            }
            int num = 0;
            while (flag && num < 2000)
            {
                Thread.Sleep(150);
                num += 150;
                flag = false;
                foreach (int item3 in list)
                {
                    if (IsAlive(item3))
                    {
                        flag = true;
                        break;
                    }
                }
            }
            foreach (int item4 in list)
            {
                KillPid(item4);
            }
            Dictionary<int, ProcRow> dictionary = new Dictionary<int, ProcRow>();
            foreach (ProcRow item5 in list2)
            {
                dictionary[item5.Pid] = item5;
            }
            HashSet<int> hashSet = new HashSet<int>(list);
            HashSet<int> hashSet2 = new HashSet<int>();
            foreach (ProcRow item6 in list2)
            {
                if (item6.Pid != 0 && !hashSet.Contains(item6.Pid) && IsUnderFreebuffInstall(item6.Exe) && IsDescendantOf(item6.Pid, dictionary, hashSet))
                {
                    hashSet2.Add(item6.Pid);
                }
            }
            List<int> list3 = new List<int>();
            if (sweepOrphans)
            {
                foreach (ProcRow item7 in list2)
                {
                    if (IsSidecarExe(item7) && !hashSet2.Contains(item7.Pid) && (item7.Parent == 0 || !dictionary.ContainsKey(item7.Parent)))
                    {
                        list3.Add(item7.Pid);
                    }
                }
                foreach (int item8 in list3)
                {
                    hashSet2.Add(item8);
                }
            }
            foreach (int item9 in hashSet2)
            {
                KillPid(item9);
            }
            if (list3.Count > 0)
            {
                LogFail(" Стоп все: убираю  " + list3.Count + "  шт. остаточных orchestrator ");
            }
            return hashSet2.Count;
        }

        private static List<ProcRow> SnapshotProcessTable()
        {
            List<ProcRow> list = new List<ProcRow>();
            try
            {
                using (ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name, ExecutablePath FROM Win32_Process"))
                {
                    using (ManagementObjectCollection managementObjectCollection = managementObjectSearcher.Get())
                    {
                        foreach (ManagementObject item in managementObjectCollection)
                        {
                            try
                            {
                                ProcRow procRow = new ProcRow();
                                procRow.Pid = (int)(uint)item["ProcessId"];
                                procRow.Parent = (int)(uint)item["ParentProcessId"];
                                procRow.Name = item["Name"] as string;
                                procRow.Exe = item["ExecutablePath"] as string;
                                list.Add(procRow);
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogFail(" перечисление  процесс  ошибка ( Стоп  может  остаточный )", ex);
            }
            return list;
        }

        private static bool IsUnderFreebuffInstall(string exePath)
        {
            if (string.IsNullOrEmpty(exePath))
            {
                return false;
            }
            try
            {
                string value = Path.GetFullPath(FreebuffInstallDir).TrimEnd('\\', '/') + "\\";
                return Path.GetFullPath(exePath).StartsWith(value, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSidecarExe(ProcRow r)
        {
            if (!string.IsNullOrEmpty(r.Name) && r.Name.StartsWith("bun", StringComparison.OrdinalIgnoreCase))
            {
                return IsUnderFreebuffInstall(r.Exe);
            }
            return false;
        }

        private static bool IsDescendantOf(int pid, Dictionary<int, ProcRow> byPid, HashSet<int> roots)
        {
            int num = pid;
            for (int i = 0; i < 16; i++)
            {
                ProcRow value;
                if (!byPid.TryGetValue(num, out value))
                {
                    return false;
                }
                if (roots.Contains(value.Parent))
                {
                    return true;
                }
                if (value.Parent == 0 || value.Parent == num)
                {
                    return false;
                }
                num = value.Parent;
            }
            return false;
        }

        private static bool CloseMainWindowOf(int pid)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    if (process.MainWindowHandle == IntPtr.Zero)
                    {
                        return false;
                    }
                    return process.CloseMainWindow();
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsAlive(int pid)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    return !process.HasExited;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void KillPid(int pid)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    process.Kill();
                }
            }
            catch (Exception ex)
            {
                if (!(ex is InvalidOperationException) && !(ex is ArgumentException))
                {
                    LogFail(" Не могу убить pid= " + pid, ex);
                }
            }
        }

        private static string PackVersionAt(string indexHtml)
        {
            try
            {
                if (string.IsNullOrEmpty(indexHtml) || !File.Exists(indexHtml))
                {
                    return null;
                }
                Match match = PackMarkerRegex.Match(File.ReadAllText(indexHtml));
                return match.Success ? match.Groups[1].Value : null;
            }
            catch
            {
                return null;
            }
        }

        private static string InstalledPackVersion()
        {
            return PackVersionAt(InstalledUiIndex);
        }

        private static string OutputPackVersion(string hanhuaDir)
        {
            if (string.IsNullOrEmpty(hanhuaDir))
            {
                return null;
            }
            return PackVersionAt(Path.Combine(hanhuaDir, "output\\ui\\index.html"));
        }

        private bool PendingPackIsNewer()
        {
            Version version = ParseLooseVersion(OutputPackVersion(hanhuaDir));
            Version version2 = ParseLooseVersion(InstalledPackVersion());
            if (version != null)
            {
                return version.CompareTo(version2 ?? new Version(0, 0, 0, 0)) > 0;
            }
            return false;
        }

        private static object[] AsArray(object o)
        {
            if (o is object[])
            {
                return (object[])o;
            }
            ArrayList arrayList = o as ArrayList;
            if (arrayList == null)
            {
                return null;
            }
            return arrayList.ToArray();
        }

        private static bool PackTargetsInstalled(string targetVersion, string installedVersion)
        {
            Version version = ParseLooseVersion(installedVersion);
            Version version2 = ParseLooseVersion(targetVersion);
            if (version == null || version2 == null)
            {
                return false;
            }
            if (version.Major == version2.Major && version.Minor == version2.Minor)
            {
                return version.Build == version2.Build;
            }
            return false;
        }

        private void CheckPackUpdateAsync(bool manual = false)
        {
            if (Interlocked.CompareExchange(ref packBusy, 1, 0) != 0)
            {
                return;
            }
            if (string.IsNullOrEmpty(hanhuaDir))
            {
                Interlocked.Exchange(ref packBusy, 0);
                return;
            }
            RefreshInstalledVersion();
            string dir = hanhuaDir;
            string instVer = installedVersion;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception ex = null;
                string text = null;
                string mismatch = null;
                try
                {
                    text = FetchAndStageLatestPack(dir, instVer, delegate(string stage, long done, long total)
                    {
                        UiSafe(delegate
                        {
                            if (!base.IsDisposed && hanhuaLabel != null)
                            {
                                string hanhuaText = ((!(stage == " Скачать ")) ? (" пакет локализации  обновление  · " + stage + "...") : ((total > 0) ? (" пакет локализации  обновление  ·  Скачать  " + done * 100 / total + "%...") : (" пакет локализации  обновление  ·  Скачать  " + (done >> 20) + " MB...")));
                                SetHanhuaText(hanhuaText);
                                SetStatus(hanhuaText);
                            }
                        });
                    }, out mismatch);
                }
                catch (Exception ex2)
                {
                    ex = ex2;
                }
                Interlocked.Exchange(ref packBusy, 0);
                string ver = text;
                string err = ((ex == null) ? null : ex.Message);
                string mis = mismatch;
                UiSafe(delegate
                {
                    if (!base.IsDisposed)
                    {
                        if (err != null)
                        {
                            SetStatus(" пакет локализации  обновление  ошибка :" + err, ColNewVersion);
                            TrayNotify(" пакет локализации  обновление  ошибка :" + err);
                        }
                        else if (ver != null)
                        {
                            if (Interlocked.CompareExchange(ref hanhuaBusy, 0, 0) == 0)
                            {
                                SetStatus(" пакет локализации  v" + ver + "  готов · ждет применения. ", ColGreen);
                            }
                            StartAutoRestoreHanhua(" пакет локализации  готов ", null);
                        }
                        else if (mis != null)
                        {
                            SetStatus(mis, ColNewVersion);
                        }
                        RefreshHanhuaUi();
                        if (manual && err == null && ver == null && mis == null)
                        {
                            string text2 = ((hanhuaLabel == null) ? null : hanhuaLabel.Text);
                            SetStatus(" пакет локализации  уже последняя  ·  текущий  " + (string.IsNullOrEmpty(text2) ? " Статус неизвестен " : text2));
                        }
                    }
                });
            });
        }

        private void CheckSelfUpdateAsync()
        {
            if (Interlocked.CompareExchange(ref selfUpdateBusy, 1, 0) != 0)
            {
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                string text = null;
                try
                {
                    string text2 = FetchUrlBody("https://api.github.com/repos/Dima-Sor/freebuff-controller/releases/latest");
                    if (text2 != null)
                    {
                        Dictionary<string, object> dictionary = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text2);
                        string text3 = ((dictionary == null) ? null : (dictionary["tag_name"] as string));
                        if (text3 != null)
                        {
                            text = ((ParseLooseVersion(text3) == null) ? null : text3.TrimStart('v', 'V'));
                        }
                    }
                }
                catch
                {
                }
                Interlocked.Exchange(ref selfUpdateBusy, 0);
                string ver = text;
                UiSafe(delegate
                {
                    if (!base.IsDisposed)
                    {
                        Version version = Assembly.GetExecutingAssembly().GetName().Version;
                        Version version2 = ParseLooseVersion(ver);
                        if (version2 != null && version != null && version2.CompareTo(version) > 0)
                        {
                            selfLatestVersion = ver;
                            if (selfLink != null && !selfDownloaded)
                            {
                                selfLink.Text = " Контроллер v " + ver + "  доступно обновление · нажми для обновления ";
                                selfLink.Visible = true;
                                if (hintLabel != null)
                                {
                                    hintLabel.Visible = false;
                                }
                            }
                            SetStatus(" Вышел контроллер v " + ver + ", ' самообновление '. ", ColNewVersion);
                        }
                        else
                        {
                            selfLatestVersion = null;
                            if (selfLink != null && !selfDownloaded)
                            {
                                selfLink.Visible = false;
                            }
                            if (hintLabel != null)
                            {
                                hintLabel.Visible = true;
                            }
                        }
                    }
                });
            });
        }

        private void OnSelfUpdateClick()
        {
            if (selfDownloaded)
            {
                Info(" Новый контроллер скачан, применится при рестарте.\r\nДля немедленного - закрой и запусти\r\n " + SelfUpdateScriptPath());
                return;
            }
            if (selfFailed)
            {
                selfFailed = false;
                try
                {
                    Process.Start("https://github.com/Dima-Sor/freebuff-controller/releases/latest");
                    return;
                }
                catch
                {
                    return;
                }
            }
            if (selfLatestVersion == null || Interlocked.CompareExchange(ref selfUpdateBusy, 1, 0) != 0)
            {
                return;
            }
            string ver = selfLatestVersion;
            SetStatus(" Качаю контроллер v " + ver + "...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception ex = null;
                try
                {
                    string text = FetchUrlBody("https://api.github.com/repos/Dima-Sor/freebuff-controller/releases/latest");
                    Dictionary<string, object> dictionary = ((text == null) ? null : new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text));
                    object[] array = ((dictionary == null) ? null : AsArray(dictionary["assets"]));
                    string text2 = null;
                    string text3 = null;
                    if (array != null)
                    {
                        object[] array2 = array;
                        foreach (object obj2 in array2)
                        {
                            Dictionary<string, object> dictionary2 = obj2 as Dictionary<string, object>;
                            string text4 = ((dictionary2 == null) ? null : (dictionary2["name"] as string));
                            if (string.Equals(text4, "FreebuffController.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                text2 = dictionary2["browser_download_url"] as string;
                            }
                            if (text4 == "sha512.txt")
                            {
                                try
                                {
                                    string text5 = FetchUrlBody(dictionary2["browser_download_url"] as string);
                                    if (text5 != null)
                                    {
                                        string[] array3 = text5.Split('\n');
                                        foreach (string text6 in array3)
                                        {
                                            string text7 = text6.Trim();
                                            int num = text7.IndexOf(' ');
                                            if (num > 0)
                                            {
                                                string a = text7.Substring(num).Trim().TrimStart('*', '\\')
                                                    .Trim();
                                                if (string.Equals(a, "FreebuffController.exe", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    text3 = text7.Substring(0, num).Trim().ToLowerInvariant();
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                }
                                catch
                                {
                                }
                            }
                        }
                    }
                    if (text2 == null)
                    {
                        throw new ApplicationException("Release  в  не найден  FreebuffController.exe");
                    }
                    string text8 = null;
                    if (text3 != null && text3.Length == 128)
                    {
                        try
                        {
                            byte[] array4 = new byte[64];
                            for (int k = 0; k < 64; k++)
                            {
                                array4[k] = Convert.ToByte(text3.Substring(k * 2, 2), 16);
                            }
                            text8 = Convert.ToBase64String(array4);
                        }
                        catch
                        {
                        }
                    }
                    if (!IsSha512Base64(text8))
                    {
                        throw new ApplicationException("Release  нет  FreebuffController.exe  валидный  SHA512,  остановлен  обновление ");
                    }
                    string executablePath = Application.ExecutablePath;
                    string directoryName = Path.GetDirectoryName(executablePath);
                    string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(executablePath);
                    string text9 = Path.Combine(directoryName, fileNameWithoutExtension + ".new-v" + ver + ".exe");
                    DownloadFirstAvailable(new List<string> { text2 }, text9, text8, delegate(long done, long total)
                    {
                        UiSafe(delegate
                        {
                            if (!base.IsDisposed && selfLink != null)
                            {
                                selfLink.Text = ((total > 0) ? (" Качаю самообновление  " + done * 100 / total + "%...") : (" Качаю самообновление  " + (done >> 20) + " MB..."));
                            }
                        });
                    });
                    string text10 = "ping -n 2 127.0.0.1 >nul";
                    string text11 = "ping -n 3 127.0.0.1 >nul";
                    string s = "Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.MessageBox]::Show(' контроллер  самообновление  заменить  ошибка : версия  оставить  в  " + text9.Replace("'", "''") + ",  вручную  заменить  после  использовать . ')";
                    string text12 = Convert.ToBase64String(Encoding.Unicode.GetBytes(s));
                    string text13 = SelfUpdateScriptPath();
                    string text14 = SelfBackupName();
                    string text15 = SelfUpdatedMarkPath();
                    File.WriteAllText(text13, "@echo off\r\n" + text11 + "\r\n:wait\r\ntasklist /fi \"pid eq " + Process.GetCurrentProcess().Id + "\" | find \" " + Process.GetCurrentProcess().Id + " \" >nul 2>nul\r\nif not errorlevel 1 (" + text10 + " & goto wait)\r\nset /a tries=0\r\n:move\r\ncopy /y \"" + executablePath + "\" \"" + text14 + "\" >nul 2>nul\r\nmove /y \"" + text9 + "\" \"" + executablePath + "\" >nul 2>nul\r\nif not errorlevel 1 goto moved\r\n" + text10 + "\r\nset /a tries+=1\r\nif %tries% lss 60 goto move\r\nstart \"\" powershell -NoProfile -WindowStyle Hidden -EncodedCommand " + text12 + "\r\nexit /b 1\r\n:moved\r\necho " + ver + ">\"" + text15 + "\"\r\necho " + Path.GetFileName(text14) + ">>\"" + text15 + "\"\r\nstart \"\" \"" + executablePath + "\"\r\ndel \"%~f0\"\r\n", new UTF8Encoding(false));
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + text13 + "\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }
                catch (Exception ex2)
                {
                    ex = ex2;
                }
                Interlocked.Exchange(ref selfUpdateBusy, 0);
                string err = ((ex == null) ? null : ex.Message);
                UiSafe(delegate
                {
                    if (!base.IsDisposed)
                    {
                        if (err == null)
                        {
                            selfDownloaded = true;
                            if (selfLink != null)
                            {
                                selfLink.Text = " Контроллер v " + ver + "  скачано · вступит после перезапуска ";
                            }
                            SetStatus(" Контроллер v " + ver + "  скачано. Закрой инструмент для замены. ", ColGreen);
                        }
                        else
                        {
                            selfFailed = true;
                            SetStatus(" контроллер  самообновление  ошибка :" + err + "( нажми ещё раз  Открыть  Release  страница  скачай вручную )", ColNewVersion);
                        }
                    }
                });
            });
        }

        private static string SelfUpdateScriptPath()
        {
            string directoryName = Path.GetDirectoryName(Application.ExecutablePath);
            return Path.Combine(directoryName, "self-update.cmd");
        }

        private static string SelfUpdatedMarkPath()
        {
            string directoryName = Path.GetDirectoryName(Application.ExecutablePath);
            return Path.Combine(directoryName, "updated-to.txt");
        }

        private void ShowSelfUpdateNotice()
        {
            string path = SelfUpdatedMarkPath();
            if (!File.Exists(path))
            {
                return;
            }
            string text = null;
            string text2 = null;
            try
            {
                string[] array = File.ReadAllLines(path, Encoding.UTF8);
                if (array.Length > 0)
                {
                    text = array[0].Trim();
                }
                if (array.Length > 1)
                {
                    text2 = array[1].Trim();
                }
            }
            catch
            {
            }
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            Version version2 = ParseLooseVersion(text);
            if (version2 == null || version == null || version2.CompareTo(version) != 0)
            {
                return;
            }
            string text3 = null;
            if (!string.IsNullOrEmpty(text2))
            {
                string path2 = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), text2);
                if (File.Exists(path2))
                {
                    text3 = text2;
                }
            }
            ShowStatusAfterIdle(" Обновлён до v " + text + " ✓" + ((text3 != null) ? ("( старая версия в бэкапе  " + text3 + ")") : ""));
        }

        private static string SelfBackupName()
        {
            string executablePath = Application.ExecutablePath;
            string directoryName = Path.GetDirectoryName(executablePath);
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(executablePath);
            return Path.Combine(directoryName, fileNameWithoutExtension + ".bak-v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3) + ".exe");
        }

        private static string FetchAndStageLatestPack(string hanhuaDir, string installedVersion, Action<string, long, long> progress, out string mismatch)
        {
            mismatch = null;
            string text = FetchUrlBody("https://api.github.com/repos/Ximmmmmmm/freebuff-zh/releases/latest");
            if (text == null)
            {
                return null;
            }
            Dictionary<string, object> dictionary = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
            object[] array = ((dictionary == null) ? null : AsArray(dictionary["assets"]));
            if (array == null)
            {
                return null;
            }
            string text2 = null;
            object[] array2 = array;
            foreach (object obj in array2)
            {
                Dictionary<string, object> dictionary2 = obj as Dictionary<string, object>;
                if (dictionary2 != null && dictionary2["name"] as string == "pack-manifest.json")
                {
                    text2 = dictionary2["browser_download_url"] as string;
                    break;
                }
            }
            if (text2 == null)
            {
                return null;
            }
            string text3 = FetchUrlBody(text2);
            if (text3 == null)
            {
                return null;
            }
            Dictionary<string, object> dictionary3 = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text3);
            if (dictionary3 == null)
            {
                return null;
            }
            string text4 = dictionary3["packVersion"] as string;
            string text5 = dictionary3["targetVersion"] as string;
            string text6 = dictionary3["asset"] as string;
            string text7 = dictionary3["sha512"] as string;
            if (text4 == null || text5 == null || text6 == null)
            {
                return null;
            }
            if (!IsSha512Base64(text7))
            {
                throw new ApplicationException(" пакет локализации  нет валидного  SHA512,  остановлен  Скачать ");
            }
            Version value = ParseLooseVersion(OutputPackVersion(hanhuaDir)) ?? new Version(0, 0, 0, 0);
            Version version = ParseLooseVersion(text4);
            if (version == null || version.CompareTo(value) <= 0)
            {
                return null;
            }
            if (!PackTargetsInstalled(text5, installedVersion))
            {
                mismatch = " последняя  пакет локализации  v" + text4 + "  для Freebuff v " + text5 + ",  это  v" + installedVersion + "-- обновление  Freebuff  после  будет  авто  проверка . ";
                return null;
            }
            string text8 = null;
            object[] array3 = array;
            foreach (object obj2 in array3)
            {
                Dictionary<string, object> dictionary4 = obj2 as Dictionary<string, object>;
                if (dictionary4 != null && dictionary4["name"] as string == text6)
                {
                    text8 = dictionary4["browser_download_url"] as string;
                    break;
                }
            }
            if (text8 == null)
            {
                return null;
            }
            string text9 = Path.Combine(Path.GetTempPath(), text6);
            if (progress != null)
            {
                progress(" Скачать ", 0L, 0L);
            }
            List<string> list = new List<string>();
            list.Add(text8);
            DownloadFirstAvailable(list, text9, text7, delegate(long done, long total)
            {
                if (progress != null)
                {
                    progress(" Скачать ", done, total);
                }
            });
            string text10 = Path.Combine(Path.GetTempPath(), "hanhua-pack-" + text4);
            if (progress != null)
            {
                progress(" Распак. ", 0L, 0L);
            }
            if (Directory.Exists(text10))
            {
                Directory.Delete(text10, true);
            }
            ExtractZip(text9, text10);
            string text11 = Path.Combine(text10, "app.asar");
            string text12 = Path.Combine(text10, "ui");
            if (!File.Exists(text11) || !Directory.Exists(text12))
            {
                throw new ApplicationException(" пакет локализации  содержимое неполное ( нет app.asar или ui/ )");
            }
            if (progress != null)
            {
                progress(" Черновик ", 0L, 0L);
            }
            Interlocked.Exchange(ref packStaging, 1);
            try
            {
                for (int num = 0; num < 60; num++)
                {
                    if (Interlocked.CompareExchange(ref hanhuaBusy, 0, 0) == 0)
                    {
                        break;
                    }
                    Thread.Sleep(250);
                }
                if (Interlocked.CompareExchange(ref hanhuaBusy, 0, 0) != 0)
                {
                    try
                    {
                        File.Delete(text9);
                        Directory.Delete(text10, true);
                    }
                    catch
                    {
                    }
                    return null;
                }
                string text13 = Path.Combine(hanhuaDir, "output");
                Directory.CreateDirectory(text13);
                File.Copy(text11, Path.Combine(text13, "app.asar"), true);
                string text14 = Path.Combine(text13, "ui");
                if (Directory.Exists(text14))
                {
                    Directory.Delete(text14, true);
                }
                CopyDir(text12, text14);
            }
            finally
            {
                Interlocked.Exchange(ref packStaging, 0);
            }
            try
            {
                File.Delete(text9);
                Directory.Delete(text10, true);
            }
            catch
            {
            }
            return text4;
        }

        private static void ExtractZip(string zipPath, string destDir)
        {
            Directory.CreateDirectory(destDir);
            string value = Path.GetFullPath(destDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            using (ZipArchive zipArchive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in zipArchive.Entries)
                {
                    string fullPath = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                    if (!fullPath.StartsWith(value, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ApplicationException(" пакет локализации  недопустимый путь внутри :" + entry.FullName);
                    }
                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                    {
                        Directory.CreateDirectory(fullPath);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                    entry.ExtractToFile(fullPath, true);
                }
            }
        }

        private void RefreshHanhuaUi()
        {
            if (hanhuaLabel != null && Interlocked.CompareExchange(ref packBusy, 0, 0) != 1 && Interlocked.CompareExchange(ref hanhuaBusy, 0, 0) != 1)
            {
                bool flag = HanhuaApplied();
                string text = HanhuaBuildDir(hanhuaDir);
                string text2 = OutputPackVersion(hanhuaDir) ?? HanhuaTargetVersion(hanhuaDir);
                Version version = ParseLooseVersion(installedVersion);
                Version version2 = ParseLooseVersion(text2);
                bool flag2 = version != null && version2 != null && version.CompareTo(version2) > 0;
                string text3 = ((text2 == null) ? "" : (" · " + text2 + (flag2 ? "( устарело )" : "")));
                string text4 = OutputPackVersion(hanhuaDir);
                bool flag3 = text != null && PendingPackIsNewer();
                bool flag4 = flag && !InstalledUiIntact();
                if (flag4)
                {
                    SetHanhuaText(" локализация  ✗  интерфейс  битый  ·  ждет переустановки " + text3);
                }
                else if (flag)
                {
                    SetHanhuaText(flag3 ? (" Локализация ✓  ·  новый пакет  " + text4 + "  ждет ") : (" Локализация ✓ " + text3));
                }
                else if (text != null)
                {
                    SetHanhuaText(" локализация  ✗  ждет автоприменения " + text3);
                }
                else if (hanhuaDir != null)
                {
                    SetHanhuaText(" локализация  ✗  нет сборки ");
                }
                else
                {
                    SetHanhuaText(" локализация  ✗  нет репозитория ");
                }
                hanhuaLabel.ForeColor = (flag4 ? ColNewVersion : (((!flag && text != null) || flag3) ? ColGreen : ColSub));
            }
        }

        private string FindHanhuaDir()
        {
            try
            {
                string directoryName = Path.GetDirectoryName(Application.ExecutablePath);
                string[] array = new string[4]
                {
                    Path.Combine(directoryName, "hanhua"),
                    Path.GetFullPath(Path.Combine(directoryName, "..\\hanhua")),
                    Path.Combine(directoryName, "freebuff-zh"),
                    Path.GetFullPath(Path.Combine(directoryName, "..\\freebuff-zh"))
                };
                string[] array2 = array;
                foreach (string text in array2)
                {
                    if (IsValidHanhuaDir(text))
                    {
                        return text;
                    }
                }
            }
            catch
            {
            }
            string text2 = ReadHanhuaConfig();
            if (IsValidHanhuaDir(text2))
            {
                return text2;
            }
            return null;
        }

        private static string ReadHanhuaConfig()
        {
            try
            {
                if (!File.Exists(HanhuaConfigFile))
                {
                    return null;
                }
                string text = File.ReadAllText(HanhuaConfigFile).Trim();
                return (text.Length > 0) ? text : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsValidHanhuaDir(string dir)
        {
            if (string.IsNullOrEmpty(dir))
            {
                return false;
            }
            if (File.Exists(Path.Combine(dir, "dict.json")))
            {
                return Directory.Exists(Path.Combine(dir, "tools"));
            }
            return false;
        }

        private static string HanhuaBuildDir(string dir)
        {
            if (string.IsNullOrEmpty(dir))
            {
                return null;
            }
            string text = Path.Combine(dir, "output");
            if (File.Exists(Path.Combine(text, "app.asar")) && File.Exists(Path.Combine(text, "ui\\index.html")))
            {
                return text;
            }
            return null;
        }

        private static string HanhuaTargetVersion(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir))
                {
                    return null;
                }
                string path = Path.Combine(dir, "manifest.json");
                if (!File.Exists(path))
                {
                    return null;
                }
                Match match = ManifestVersionRegex.Match(File.ReadAllText(path));
                return match.Success ? match.Groups[1].Value : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool HanhuaApplied()
        {
            try
            {
                return File.Exists(InstalledUiIndex) && File.ReadAllText(InstalledUiIndex).Contains("<html lang=\"zh-CN\">");
            }
            catch
            {
                return false;
            }
        }

        private static string HanhuaErrorText(Exception ex)
        {
            if (ex is IOException || ex is UnauthorizedAccessException)
            {
                return " файл  занят  или  права ,  сначала  Закрыть  все  Freebuff  окно (" + ex.Message + ")";
            }
            return ex.Message;
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            string[] files = Directory.GetFiles(src);
            foreach (string text in files)
            {
                File.Copy(text, Path.Combine(dst, Path.GetFileName(text)), true);
            }
            string[] directories = Directory.GetDirectories(src);
            foreach (string text2 in directories)
            {
                CopyDir(text2, Path.Combine(dst, Path.GetFileName(text2)));
            }
        }

        private static bool UiDirIntact(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir))
                {
                    return false;
                }
                string path = Path.Combine(dir, "index.html");
                if (!File.Exists(path))
                {
                    return false;
                }
                MatchCollection matchCollection = UiAssetRefRegex.Matches(File.ReadAllText(path));
                if (matchCollection.Count == 0)
                {
                    LogFail(" интерфейс  в  не найден  ./assets/  ссылки ( сборка ),  пропустить  целый  проверка :" + dir);
                    return true;
                }
                foreach (Match item in matchCollection)
                {
                    string path2 = item.Groups[1].Value.Replace('/', '\\');
                    if (!File.Exists(Path.Combine(dir, path2)))
                    {
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                LogFail(" Проверка UI не удалась: " + dir, ex);
                return false;
            }
        }

        private static bool InstalledUiIntact()
        {
            return UiDirIntact(InstalledUiDir);
        }

        private static void ReplaceUiDir(string srcUi)
        {
            ReplaceUiDir(srcUi, FreebuffResources);
        }

        private static void ReplaceUiDir(string srcUi, string dstRoot)
        {
            if (!Directory.Exists(srcUi))
            {
                throw new ApplicationException(" Нет каталога ui: " + srcUi);
            }
            string text = Path.Combine(dstRoot, "orchestrator\\ui");
            string text2 = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string text3 = Path.Combine(dstRoot, "orchestrator\\ui.new-" + text2);
            string text4 = Path.Combine(dstRoot, "orchestrator\\ui.old-" + text2);
            bool flag = false;
            try
            {
                if (Directory.Exists(text3))
                {
                    Directory.Delete(text3, true);
                }
                CopyDir(srcUi, text3);
                if (!UiDirIntact(text3))
                {
                    throw new ApplicationException(" пакет локализации  в  ui/  битый (index.html  ссылки  ресурсы  нет ),  установка  держать ");
                }
                if (Directory.Exists(text))
                {
                    if (Directory.Exists(text4))
                    {
                        Directory.Delete(text4, true);
                    }
                    Directory.Move(text, text4);
                }
                try
                {
                    Directory.Move(text3, text);
                    flag = true;
                }
                catch (Exception ex)
                {
                    if (Directory.Exists(text4) && !Directory.Exists(text))
                    {
                        try
                        {
                            Directory.Move(text4, text);
                        }
                        catch (Exception ex2)
                        {
                            LogFail(" Сбой замены UI и отката: " + text, ex2);
                        }
                    }
                    LogFail(" Сбой замены UI (откат был): " + text, ex);
                    throw;
                }
            }
            finally
            {
                if (Directory.Exists(text3))
                {
                    try
                    {
                        Directory.Delete(text3, true);
                    }
                    catch
                    {
                    }
                }
                if (flag && Directory.Exists(text4))
                {
                    try
                    {
                        Directory.Delete(text4, true);
                    }
                    catch (Exception ex3)
                    {
                        LogFail(" интерфейс  каталог  не ( безвредно , ):" + text4, ex3);
                    }
                }
            }
        }

        private static string PruneUiSwapDirs()
        {
            return PruneUiSwapDirs(FreebuffResources);
        }

        private static string PruneUiSwapDirs(string dstRoot)
        {
            try
            {
                string text = Path.Combine(dstRoot, "orchestrator");
                if (!Directory.Exists(text))
                {
                    return null;
                }
                bool flag = File.Exists(Path.Combine(text, "ui\\index.html"));
                List<string> list = new List<string>();
                list.AddRange(Directory.GetDirectories(text, "ui.new-*"));
                list.AddRange(Directory.GetDirectories(text, "ui.old-*"));
                long num = 0L;
                int num2 = 0;
                foreach (string item in list)
                {
                    if (flag || item.IndexOf("ui.old-", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        long num3 = DirSize(item);
                        try
                        {
                            Directory.Delete(item, true);
                            num += num3;
                            num2++;
                        }
                        catch
                        {
                        }
                    }
                }
                if (num2 == 0)
                {
                    return null;
                }
                return " Очищено  " + num2 + "  шт. временных каталогов ( " + HumanSize(num) + ")";
            }
            catch
            {
                return null;
            }
        }

        internal static int RunSelfTest(string reportPath)
        {
            if (string.IsNullOrEmpty(reportPath))
            {
                reportPath = Path.Combine(Path.GetTempPath(), "freebuff-controller-selftest.txt");
            }
            string text = Path.Combine(Path.GetTempPath(), "ctrl-selftest-" + Guid.NewGuid().ToString("N"));
            List<string> log = new List<string>();
            int failed = 0;
            Program.FailLogMuted = true;
            Action<string, bool, string> action = delegate(string name, bool ok, string detail)
            {
                if (!ok)
                {
                    failed++;
                }
                log.Add((ok ? "PASS  " : "FAIL  ") + name + (string.IsNullOrEmpty(detail) ? "" : ("   [" + detail + "]")));
            };
            try
            {
                Directory.CreateDirectory(text);
                string text2 = Path.Combine(text, "src-good");
                string text3 = Path.Combine(text, "src-broken");
                MakeFakeUi(text2, "0.0.131.1", true);
                MakeFakeUi(text3, "0.0.131.1", false);
                action("UiDirIntact:index.html +  ссылки  ресурсы  все  в  =  целый ", UiDirIntact(text2), text2);
                action("UiDirIntact: ссылки  ресурсы  нет  =  битый ( сентинел  в  тоже  не )", !UiDirIntact(text3), text3);
                string text4 = Path.Combine(text, "src-norefs");
                Directory.CreateDirectory(text4);
                File.WriteAllText(Path.Combine(text4, "index.html"), "<html lang=\"zh-CN\"><body>no asset refs</body></html>", new UTF8Encoding(false));
                action("UiDirIntact: поиск  не  ресурсы  ссылки  не  ложное срабатывание ( не могу  считается )", UiDirIntact(text4), text4);
                action("PackVersionAt: чтение  ui/index.html  в  hanhua-pack  метка ", PackVersionAt(Path.Combine(text2, "index.html")) == "0.0.131.1", "");
                string text5 = Path.Combine(text, "dst-a");
                MakeFakeUi(Path.Combine(text5, "orchestrator\\ui"), "0.0.0", true);
                bool flag = false;
                try
                {
                    ReplaceUiDir(text2, text5);
                }
                catch
                {
                    flag = true;
                }
                string text6 = Path.Combine(text5, "orchestrator");
                action("ReplaceUiDir: не  бросить  Ошибка ", !flag, "");
                action("ReplaceUiDir: шт.  на месте ( версия  метка  = 0.0.131.1)", PackVersionAt(Path.Combine(text6, "ui\\index.html")) == "0.0.131.1", "");
                action("ReplaceUiDir: шт.  может  через  целый  проверка ", UiDirIntact(Path.Combine(text6, "ui")), "");
                action("ReplaceUiDir: не  ui.new-* / ui.old-*  временные каталоги ", Directory.GetDirectories(text6, "ui.new-*").Length == 0 && Directory.GetDirectories(text6, "ui.old-*").Length == 0, string.Join(",", Directory.GetDirectories(text6)));
                string text7 = Path.Combine(text, "dst-b");
                string text8 = Path.Combine(text7, "orchestrator\\ui");
                MakeFakeUi(text8, "OLD-MARKER", true);
                File.WriteAllText(Path.Combine(text8, "index.html"), "<html lang=\"en\"><meta name=\"hanhua-pack\" content=\"OLD-MARKER\"><script src=\"./assets/old.js\"></script>", new UTF8Encoding(false));
                flag = false;
                try
                {
                    ReplaceUiDir(text3, text7);
                }
                catch
                {
                    flag = true;
                }
                action("ReplaceUiDir: битый  нужно ( не  это  установлен )", flag, "");
                action("ReplaceUiDir: после  установка  шт.  без изменений ( OLD-MARKER  ещё  в )", PackVersionAt(Path.Combine(text8, "index.html")) == "OLD-MARKER", "");
                action("ReplaceUiDir: после  не  половинный  каталог ", Directory.GetDirectories(Path.Combine(text7, "orchestrator"), "ui.*-*").Length == 0, string.Join(",", Directory.GetDirectories(Path.Combine(text7, "orchestrator"))));
                string text9 = Path.Combine(text, "dst-c");
                MakeFakeUi(Path.Combine(text9, "orchestrator\\ui"), "0.0.131.1", true);
                Directory.CreateDirectory(Path.Combine(text9, "orchestrator\\ui.new-aaaa"));
                Directory.CreateDirectory(Path.Combine(text9, "orchestrator\\ui.old-bbbb"));
                PruneUiSwapDirs(text9);
                action("PruneUiSwapDirs: установка  ui  в ,  временные каталоги ", Directory.GetDirectories(Path.Combine(text9, "orchestrator")).Length == 1, "");
                string text10 = Path.Combine(text, "dst-d");
                Directory.CreateDirectory(Path.Combine(text10, "orchestrator\\ui.new-cccc"));
                Directory.CreateDirectory(Path.Combine(text10, "orchestrator\\ui.old-dddd"));
                PruneUiSwapDirs(text10);
                action("PruneUiSwapDirs: установка  ui  не  в  ui.old-*  оставляю ( единственный  старый ), ui.new-*  чищу ", Directory.GetDirectories(Path.Combine(text10, "orchestrator"), "ui.old-*").Length == 1 && Directory.GetDirectories(Path.Combine(text10, "orchestrator"), "ui.new-*").Length == 0, string.Join(",", Directory.GetDirectories(Path.Combine(text10, "orchestrator"))));
                string path = Path.Combine(text, "atomic.txt");
                File.WriteAllText(path, "v1", new UTF8Encoding(false));
                WriteFileAtomic(path, "v2");
                action("WriteFileAtomic: заменить ", File.ReadAllText(path) == "v2", "");
                action("WriteFileAtomic: не  временный  файл ", Directory.GetFiles(text, "atomic.txt.tmp-*").Length == 0, "");
                Dictionary<int, ProcRow> dictionary = new Dictionary<int, ProcRow>();
                dictionary[10] = new ProcRow
                {
                    Pid = 10,
                    Parent = 0
                };
                dictionary[11] = new ProcRow
                {
                    Pid = 11,
                    Parent = 10
                };
                dictionary[12] = new ProcRow
                {
                    Pid = 12,
                    Parent = 11
                };
                HashSet<int> hashSet = new HashSet<int>();
                hashSet.Add(10);
                action("IsDescendantOf: прямой  и  внук  все ", IsDescendantOf(11, dictionary, hashSet) && IsDescendantOf(12, dictionary, hashSet), "");
                dictionary[13] = new ProcRow
                {
                    Pid = 13,
                    Parent = 99
                };
                action("IsDescendantOf: не  связан  процесс  не ", !IsDescendantOf(13, dictionary, hashSet), "");
                if (Directory.Exists(FreebuffInstallDir))
                {
                    action("IsUnderFreebuffInstall: установка  exe  = true", IsUnderFreebuffInstall(FreebuffExe), FreebuffExe);
                    action("IsUnderFreebuffInstall: установка  каталог  в  orchestrator  bun = true", IsUnderFreebuffInstall(Path.Combine(FreebuffResources, "bun\\bun-baseline.exe")), "");
                    action("IsUnderFreebuffInstall: другое место  процесс (cmd.exe)= false", !IsUnderFreebuffInstall(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")), "");
                }
                string text11 = Path.Combine(text, "junction", "main-projects");
                Directory.CreateDirectory(text11);
                string text12 = Path.Combine(text, "junction", "slots", "slot-4", "projects");
                bool flag2 = CreateJunction(text12, text11) && IsJunction(text12);
                action("CreateJunction: каталог  не  в  тоже  может ( Инстанс )" + (flag2 ? "" : "[ создание reparse point  только  не  это  другие  Ошибка ]"), flag2 || LastJunctionError == 5, LastJunctionDetail);
                bool arg = true;
                string arg2;
                try
                {
                    arg2 = (ControllerProxyAvailable() ? " подключен  прокси  ->  нормально запрашиваю  Лимит " : " нет прокси  ->  пропуск цикла ");
                }
                catch (Exception ex)
                {
                    arg = false;
                    arg2 = ex.GetType().Name + ":" + ex.Message;
                }
                action("ShortProxyUrl:http://127.0.0.1:10808 -> 127.0.0.1:10808", ShortProxyUrl("http://127.0.0.1:10808") == "127.0.0.1:10808", ShortProxyUrl("http://127.0.0.1:10808"));
                bool flag3 = false;
                bool flag4 = false;
                string kind = null;
                string address = null;
                try
                {
                    flag3 = ControllerProxyRoute(out kind, out address);
                }
                catch
                {
                    flag4 = true;
                }
                action("ControllerProxyRoute: не  бросить  Ошибка , ' подключен  прокси ' маршрут  и  адрес  все  выдать ", !flag4 && (!flag3 || (!string.IsNullOrEmpty(kind) && !string.IsNullOrEmpty(address))) && (flag3 || (kind == null && address == null)), flag3 ? ("✓ " + kind + " " + address) : "✗  Нет прокси ");
                string text13 = Path.Combine(text, "junction", "slots", "slot-5", "projects");
                Directory.CreateDirectory(Path.GetDirectoryName(text13));
                int num = CreateJunctionNative(text13, text11);
                action("CreateJunctionNative: DeviceIoControl  может  junction( не  cmd)" + (IsJunction(text13) ? "" : "[ иначе  только  принимаю  Код  5: система  /  антивирус  блокирует ]"), IsJunction(text13) || num == 5, " Код  " + num + "(" + Win32ErrorText(num) + ")");
                string text14 = "http://127.0.0.1:1";
                action("ControllerProxyAvailable: может  проходит  не  бросить  Ошибка ", arg, arg2);
                action(" прокси : мертвый порт  не  считается ' подключен  прокси '(TCP  не  /  может  не )", !ProxyAlive(text14) && !ProxyProbeOk(text14, "http://connect.rom.miui.com/generate_204"), text14);
                QuotaInfo quotaInfo = OfflineQuota(null);
                QuotaInfo quotaInfo2 = new QuotaInfo();
                quotaInfo2.Text = "12/40";
                QuotaInfo quotaInfo3 = OfflineQuota(quotaInfo2);
                QuotaInfo quotaInfo4 = OfflineQuota(quotaInfo3);
                action("OfflineQuota: нет  прокси  показать ' Нет прокси ' не  это ", quotaInfo.Text == " Нет прокси " && quotaInfo.Offline && quotaInfo.Text != "12/40", quotaInfo.Text);
                action("OfflineQuota: прошлое значение  фолбэк  подсказка  Инфо ", quotaInfo3.Tip != null && quotaInfo3.Tip.Contains("12/40"), quotaInfo3.Tip);
                action("OfflineQuota: подряд  пропустить  не  будет ' Нет прокси ' как ", quotaInfo4.Tip != null && !quotaInfo4.Tip.Contains(" Нет прокси "), quotaInfo4.Tip);
            }
            catch (Exception ex2)
            {
                failed++;
                log.Add(string.Concat("FAIL   самотест  бросить  Ошибка   [", ex2, "]"));
            }
            finally
            {
                try
                {
                    Directory.Delete(text, true);
                }
                catch
                {
                }
                Program.FailLogMuted = false;
            }
            log.Insert(0, ((failed == 0) ? " Всё ок " : (failed + "  шт. с ошибкой ")) + "( Всего  " + log.Count + "  шт.) ");
            log.Insert(1, " Лог: " + Program.FailLogPath + "( во время самотеста  тихо ,  не  писать сюда )");
            try
            {
                File.WriteAllText(reportPath, string.Join(Environment.NewLine, log.ToArray()) + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
            }
            if (failed != 0)
            {
                return 1;
            }
            return 0;
        }

        private static void MakeFakeUi(string dir, string packVersion, bool withAssets)
        {
            Directory.CreateDirectory(Path.Combine(dir, "assets"));
            File.WriteAllText(Path.Combine(dir, "index.html"), "<html lang=\"zh-CN\"><head><meta name=\"hanhua-pack\" content=\"" + packVersion + "\"><script src=\"./assets/index-abc.js\"></script></head><body></body></html>", new UTF8Encoding(false));
            if (withAssets)
            {
                File.WriteAllText(Path.Combine(dir, "assets\\index-abc.js"), "// bundle\n", new UTF8Encoding(false));
            }
        }

        internal static void EnsureAgentsMdEnabled()
        {
            bool mainRunning;
            HashSet<int> hashSet = QueryRunning(out mainRunning);
            AgentsMdPending = false;
            for (int i = 0; i <= 9; i++)
            {
                if ((i == 0) ? mainRunning : hashSet.Contains(i))
                {
                    AgentsMdPending = true;
                    continue;
                }
                string text = ((i == 0) ? DefaultState : SlotStatePath(i));
                try
                {
                    string text2;
                    string text3;
                    if (File.Exists(text))
                    {
                        text2 = File.ReadAllText(text);
                        if (Regex.IsMatch(text2, "\"injectAgentsMd\"\\s*:"))
                        {
                            text3 = Regex.Replace(text2, "\"injectAgentsMd\"\\s*:\\s*(true|false)", "\"injectAgentsMd\": true");
                            goto IL_010c;
                        }
                        Match match = Regex.Match(text2, "\"uiPrefs\"\\s*:\\s*\\{");
                        if (match.Success)
                        {
                            int num = match.Index + match.Length - 1;
                            text3 = ((num + 1 < text2.Length && text2[num + 1] == '}') ? (text2.Substring(0, num + 1) + "\"injectAgentsMd\": true" + text2.Substring(num + 1)) : (text2.Substring(0, num + 1) + "\"injectAgentsMd\": true, " + text2.Substring(num + 1)));
                            goto IL_010c;
                        }
                    }
                    goto end_IL_0040;
                    IL_010c:
                    if (text3 != text2)
                    {
                        WriteFileAtomic(text, text3);
                    }
                    end_IL_0040:;
                }
                catch (Exception ex)
                {
                    LogFail(" Не могу исправить state.json: " + text, ex);
                }
            }
        }

        private static bool WriteFileAtomic(string path, string text)
        {
            string text2 = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(text2, text, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Replace(text2, path, null);
                }
                else
                {
                    File.Move(text2, path);
                }
                return true;
            }
            catch (Exception ex)
            {
                LogFail(" Атомарная запись не удалась: " + path, ex);
                try
                {
                    if (File.Exists(text2))
                    {
                        File.Delete(text2);
                    }
                }
                catch
                {
                }
                return false;
            }
        }

        internal static void EnsureChineseReply()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".AGENTS.md");
            try
            {
                string text = "# Language Rule\r\n\r\n**Always respond in Russian.**\r\n";
                if (!File.Exists(path))
                {
                    WriteFileAtomic(path, text);
                    return;
                }
                string text2 = File.ReadAllText(path);
                if (text2.IndexOf("Anti-injection Clause", StringComparison.Ordinal) < 0)
                {
                    if (text2.TrimStart().StartsWith(" # Language Rule ", StringComparison.Ordinal))
                    {
                        WriteFileAtomic(path, text);
                    }
                    else
                    {
                        File.AppendAllText(path, "\r\n\r\n" + text, new UTF8Encoding(false));
                    }
                }
            }
            catch (Exception ex)
            {
                LogFail(" Не могу записать ~/.AGENTS.md ", ex);
            }
        }

        private static string BackupPristineIfNeeded()
        {
            if (!HanhuaApplied())
            {
                string text = Path.Combine(FreebuffResources, "hanhua-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(text);
                File.Copy(Path.Combine(FreebuffResources, "app.asar"), Path.Combine(text, "app.asar"), true);
                CopyDir(Path.Combine(FreebuffResources, "orchestrator\\ui"), Path.Combine(text, "ui"));
            }
            return PruneHanhuaBackups(2);
        }

        private static string PruneHanhuaBackups(int keep)
        {
            try
            {
                if (!Directory.Exists(FreebuffResources))
                {
                    return null;
                }
                List<string> list = new List<string>(Directory.GetDirectories(FreebuffResources, "hanhua-backup-*"));
                list.Sort((string a, string b) => string.CompareOrdinal(b, a));
                long num = 0L;
                int num2 = 0;
                int num3 = 0;
                foreach (string item in list)
                {
                    if (num3 < keep && IsCompleteBackup(item))
                    {
                        num3++;
                        continue;
                    }
                    long num4 = DirSize(item);
                    try
                    {
                        Directory.Delete(item, true);
                        num += num4;
                        num2++;
                    }
                    catch
                    {
                    }
                }
                if (num2 == 0)
                {
                    return null;
                }
                return " Очищено  " + num2 + "  шт. старых бэкапов локализации ( " + HumanSize(num) + "),  только  оставить  " + num3 + "  шт. ";
            }
            catch
            {
                return null;
            }
        }

        private static bool IsCompleteBackup(string dir)
        {
            if (File.Exists(Path.Combine(dir, "app.asar")))
            {
                return File.Exists(Path.Combine(dir, "ui\\index.html"));
            }
            return false;
        }

        private static long DirSize(string dir)
        {
            long num = 0L;
            try
            {
                string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                foreach (string fileName in files)
                {
                    try
                    {
                        num += new FileInfo(fileName).Length;
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            return num;
        }

        private static string HumanSize(long bytes)
        {
            if (bytes >= 1073741824)
            {
                return ((double)bytes / 1073741824.0).ToString("0.0") + " GB";
            }
            if (bytes >= 1048576)
            {
                return ((double)bytes / 1048576.0).ToString("0.0") + " MB";
            }
            if (bytes >= 1024)
            {
                return ((double)bytes / 1024.0).ToString("0.0") + " KB";
            }
            return bytes + " B";
        }

        private void RunWhenHanhuaIdle(Action action)
        {
            if (action == null)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref hanhuaBusy, 0, 0) == 0)
            {
                action();
                return;
            }
            lock (hanhuaWaiters)
            {
                hanhuaWaiters.Add(action);
            }
        }

        private void DrainHanhuaWaiters()
        {
            Action[] array;
            lock (hanhuaWaiters)
            {
                if (hanhuaWaiters.Count == 0)
                {
                    return;
                }
                array = hanhuaWaiters.ToArray();
                hanhuaWaiters.Clear();
            }
            Action[] array2 = array;
            foreach (Action action in array2)
            {
                action();
            }
        }

        private void PruneHanhuaBackupsOnStartup()
        {
            if (Interlocked.CompareExchange(ref hanhuaBusy, 1, 0) != 0)
            {
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                string note = null;
                try
                {
                    note = PruneHanhuaBackups(2);
                }
                catch (Exception ex)
                {
                    LogFail(" Не могу убрать бэкапы локализации ", ex);
                }
                try
                {
                    string text = PruneUiSwapDirs();
                    if (text != null)
                    {
                        note = ((note == null) ? text : (note + " · " + text));
                    }
                }
                catch (Exception ex2)
                {
                    LogFail(" Не могу убрать временные каталоги ", ex2);
                }
                Interlocked.Exchange(ref hanhuaBusy, 0);
                UiSafe(delegate
                {
                    if (!base.IsDisposed)
                    {
                        if (note != null)
                        {
                            SetStatus(note);
                        }
                        RefreshHanhuaUi();
                        DrainHanhuaWaiters();
                    }
                });
            });
        }

        private void DetectFreshLocalBuild()
        {
            string text = (IsValidHanhuaDir(hanhuaDir) ? HanhuaBuildDir(hanhuaDir) : null);
            if (text == null)
            {
                hanhuaBuildStamp = null;
                return;
            }
            string text2;
            try
            {
                text2 = (OutputPackVersion(hanhuaDir) ?? "0.0.0") + "|" + File.GetLastWriteTimeUtc(Path.Combine(text, "app.asar")).Ticks + "|" + File.GetLastWriteTimeUtc(Path.Combine(text, "ui\\index.html")).Ticks + "|" + Directory.GetLastWriteTimeUtc(Path.Combine(text, "ui\\assets")).Ticks;
            }
            catch
            {
                return;
            }
            if (text2 != hanhuaBuildStamp)
            {
                hanhuaBuildStamp = text2;
                hanhuaBuildStableAt = DateTime.UtcNow;
            }
            else if (!(text2 == hanhuaBuildHandled) && !((DateTime.UtcNow - hanhuaBuildStableAt).TotalSeconds < 8.0))
            {
                hanhuaBuildHandled = text2;
                StartAutoRestoreHanhua(" Найдена новая сборка ", null);
            }
        }

        private static bool RestoreIsTransient(RestoreOutcome o)
        {
            if (o != RestoreOutcome.Busy)
            {
                return o == RestoreOutcome.InstancesRunning;
            }
            return true;
        }

        private RestoreOutcome StartAutoRestoreHanhua(string why, Action onDone)
        {
            return StartAutoRestoreHanhua(why, onDone, false);
        }

        private RestoreOutcome StartAutoRestoreHanhua(string why, Action onDone, bool force)
        {
            bool wasApplied = HanhuaApplied();
            bool broken = wasApplied && !InstalledUiIntact();
            if (broken && brokenLoggedStamp != hanhuaBuildStamp)
            {
                brokenLoggedStamp = hanhuaBuildStamp;
                LogFail(" Найдено:  установка  интерфейс  битый (index.html  в , ссылки  ресурсы  нет )->  авто  переустановка ");
            }
            if (force)
            {
                hanhuaForcePending = true;
            }
            if (wasApplied && !broken && !force && !PendingPackIsNewer())
            {
                return RestoreOutcome.NothingToDo;
            }
            if (Interlocked.CompareExchange(ref hanhuaBusy, 1, 0) != 0)
            {
                return ScheduleHanhuaRetry(why, RestoreOutcome.Busy);
            }
            if (Interlocked.CompareExchange(ref packStaging, 0, 0) == 1)
            {
                Interlocked.Exchange(ref hanhuaBusy, 0);
                return ScheduleHanhuaRetry(why, RestoreOutcome.Busy);
            }
            string build = (IsValidHanhuaDir(hanhuaDir) ? HanhuaBuildDir(hanhuaDir) : null);
            string targetVersion = ((build == null) ? null : (OutputPackVersion(hanhuaDir) ?? HanhuaTargetVersion(hanhuaDir)));
            if (build == null || !PackTargetsInstalled(targetVersion, installedVersion))
            {
                Interlocked.Exchange(ref hanhuaBusy, 0);
                ClearHanhuaRetry();
                hanhuaForcePending = false;
                if (build != null)
                {
                    return RestoreOutcome.VersionMismatch;
                }
                return RestoreOutcome.NoBuild;
            }
            bool mainRunning;
            HashSet<int> hashSet = QueryRunning(out mainRunning);
            if (mainRunning || hashSet.Count > 0)
            {
                Interlocked.Exchange(ref hanhuaBusy, 0);
                return ScheduleHanhuaRetry(why, RestoreOutcome.InstancesRunning);
            }
            ClearHanhuaRetry();
            hanhuaForcePending = false;
            string text = (broken ? " Найдено:  интерфейс  битый  ·  применить  локализация ...(" : (wasApplied ? " Найдено:  пакет локализации  ·  авто  применить ...(" : " Найдено:  локализация  не  применить  ·  авто  восстановить ...("));
            SetStatus(text + why + ")");
            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception error = null;
                string pruneNote = null;
                try
                {
                    pruneNote = BackupPristineIfNeeded();
                    File.Copy(Path.Combine(build, "app.asar"), Path.Combine(FreebuffResources, "app.asar"), true);
                    ReplaceUiDir(Path.Combine(build, "ui"));
                }
                catch (Exception ex)
                {
                    LogFail(" Не могу применить локализацию ", error = ex);
                }
                Interlocked.Exchange(ref hanhuaBusy, 0);
                UiSafe(delegate
                {
                    if (!base.IsDisposed)
                    {
                        string text2 = ((error != null) ? ((wasApplied ? " авто  применить  пакет локализации  ошибка :" : " авто  восстановить  локализация  ошибка :") + HanhuaErrorText(error) + "( до следующего раза  авто  применить  или  контроллер )") : (broken ? " применить  Локализация ✓   Открыть  Freebuff  это . " : (wasApplied ? " авто  применить  пакет локализации  ✓  Открыть  Freebuff  это  новая версия . " : " авто  восстановить  Локализация ✓   Открыть  Freebuff  это . ")));
                        SetStatus(text2, (error == null) ? ColGreen : ColNewVersion);
                        if (error != null)
                        {
                            TrayNotify(text2);
                        }
                        ShowStatusAfterIdle((error == null) ? pruneNote : null);
                        RefreshHanhuaUi();
                        if (onDone != null)
                        {
                            onDone();
                        }
                        DrainHanhuaWaiters();
                    }
                });
            });
            return RestoreOutcome.Started;
        }

        private RestoreOutcome ScheduleHanhuaRetry(string why, RestoreOutcome outcome)
        {
            bool flag = hanhuaPendingWhy != null;
            hanhuaPendingWhy = why;
            hanhuaRetryAt = DateTime.UtcNow.AddSeconds(10.0);
            if (!flag && outcome == RestoreOutcome.InstancesRunning)
            {
                SetStatus(" локализация  ждет автоприменения  ·  закрой все  Freebuff  Инстанс  после  авто  установить . ", ColGreen);
            }
            return outcome;
        }

        private void ClearHanhuaRetry()
        {
            hanhuaPendingWhy = null;
        }

        private void TryPendingHanhuaRestore()
        {
            string text = hanhuaPendingWhy;
            if (text != null && !(DateTime.UtcNow < hanhuaRetryAt))
            {
                bool force = hanhuaForcePending;
                if (!RestoreIsTransient(StartAutoRestoreHanhua(text, null, force)))
                {
                    hanhuaPendingWhy = null;
                    hanhuaForcePending = false;
                }
            }
        }

        private void ScanUpdaterCache(out List<string> doomed, out long reclaimable, out long total)
        {
            doomed = new List<string>();
            reclaimable = 0L;
            total = 0L;
            try
            {
                if (!Directory.Exists(UpdaterCacheDir))
                {
                    return;
                }
                Dictionary<string, bool> dictionary = new Dictionary<string, bool>();
                string[] files = Directory.GetFiles(UpdaterCacheDir, "*", SearchOption.AllDirectories);
                foreach (string text in files)
                {
                    long num = FileLength(text);
                    total += num;
                    if (!string.Equals(Path.GetExtension(text), ".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    Version version = ParseLooseVersion(ExeFileVersion(text));
                    Version version2 = ParseLooseVersion(installedVersion);
                    if (version != null && version2 != null && version.CompareTo(version2) <= 0)
                    {
                        doomed.Add(text);
                        reclaimable += num;
                        continue;
                    }
                    string directoryName = Path.GetDirectoryName(text);
                    if (directoryName != null)
                    {
                        dictionary[directoryName] = true;
                    }
                }
                string[] files2 = Directory.GetFiles(UpdaterCacheDir, "*", SearchOption.AllDirectories);
                foreach (string text2 in files2)
                {
                    if (!string.Equals(Path.GetExtension(text2), ".exe", StringComparison.OrdinalIgnoreCase) && IsUpdaterBookkeeping(text2))
                    {
                        string directoryName2 = Path.GetDirectoryName(text2);
                        if (directoryName2 != null && !dictionary.ContainsKey(directoryName2) && !string.Equals(directoryName2, UpdaterCacheDir, StringComparison.OrdinalIgnoreCase))
                        {
                            doomed.Add(text2);
                            reclaimable += FileLength(text2);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static bool IsUpdaterBookkeeping(string file)
        {
            string fileName = Path.GetFileName(file);
            if (!string.Equals(fileName, "update-info.json", StringComparison.OrdinalIgnoreCase))
            {
                return fileName.EndsWith(".blockmap", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        private void ScanTempInstallers(out List<string> doomed, out long reclaimable, out long total)
        {
            doomed = new List<string>();
            reclaimable = 0L;
            total = 0L;
            try
            {
                Version version = ParseLooseVersion(installedVersion);
                if (version == null)
                {
                    return;
                }
                string text = PendingInstallerPath();
                string[] files = Directory.GetFiles(Path.GetTempPath(), "Freebuff-*-win-x64.exe");
                foreach (string text2 in files)
                {
                    long num = FileLength(text2);
                    total += num;
                    if (string.IsNullOrEmpty(text) || !string.Equals(text2, text, StringComparison.OrdinalIgnoreCase))
                    {
                        string s = ExeFileVersion(text2);
                        Version version2 = ParseLooseVersion(s);
                        if ((!(version2 != null)) ? IsStaleFile(text2, TimeSpan.FromHours(2.0)) : (version2.CompareTo(version) <= 0))
                        {
                            doomed.Add(text2);
                            reclaimable += num;
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static bool IsStaleFile(string path, TimeSpan age)
        {
            try
            {
                return DateTime.Now - File.GetLastWriteTime(path) > age;
            }
            catch
            {
                return false;
            }
        }

        private static string ExeFileVersion(string path)
        {
            try
            {
                FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrEmpty(versionInfo.FileVersion))
                {
                    return versionInfo.FileVersion;
                }
                if (!string.IsNullOrEmpty(versionInfo.ProductVersion))
                {
                    return versionInfo.ProductVersion;
                }
            }
            catch
            {
            }
            return null;
        }

        private static long FileLength(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0L;
            }
        }

        private void AutoCleanUnusedFiles(string why)
        {
            RefreshInstalledVersion();
            List<string> doomed;
            long reclaimable;
            long total;
            ScanUpdaterCache(out doomed, out reclaimable, out total);
            List<string> doomed2;
            long reclaimable2;
            long total2;
            ScanTempInstallers(out doomed2, out reclaimable2, out total2);
            if (doomed.Count == 0 && doomed2.Count == 0)
            {
                return;
            }
            List<string> all = new List<string>(doomed);
            all.AddRange(doomed2);
            ThreadPool.QueueUserWorkItem(delegate
            {
                long freed = 0L;
                int failed = 0;
                foreach (string item in all)
                {
                    long num = FileLength(item);
                    try
                    {
                        File.Delete(item);
                        freed += num;
                    }
                    catch
                    {
                        failed++;
                    }
                }
                RemoveEmptyUpdaterDirs();
                UiSafe(delegate
                {
                    if (!base.IsDisposed)
                    {
                        SetStatus((failed == 0) ? (" авто  очистка установщиков  ✓  освободить  " + HumanSize(freed) + "(" + why + ")") : (" авто  очистка установщиков  " + HumanSize(freed) + "(" + failed + "  шт. занято) "), (failed == 0) ? new Color?(ColGreen) : ((Color?)null));
                    }
                });
            });
        }

        private void CleanupAfterUpdate()
        {
            PruneDownloadedInstaller();
            AutoCleanUnusedFiles(" Freebuff обновлён ");
            Delay(20000, delegate
            {
                AutoCleanUnusedFiles(" перепроверка после обновления ");
            });
        }

        private static void RemoveLegacyHanhuaPrefs()
        {
            string[] legacyHanhuaPrefFiles = LegacyHanhuaPrefFiles;
            foreach (string path in legacyHanhuaPrefFiles)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                }
            }
        }

        private static void RemoveEmptyUpdaterDirs()
        {
            try
            {
                if (!Directory.Exists(UpdaterCacheDir))
                {
                    return;
                }
                string[] directories = Directory.GetDirectories(UpdaterCacheDir);
                foreach (string path in directories)
                {
                    try
                    {
                        if (Directory.GetFileSystemEntries(path).Length == 0)
                        {
                            Directory.Delete(path);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }
    }
}
