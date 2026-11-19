using static TunPilotSetup.Lang;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TunPilotSetup
{
    /// <summary>Окно установки/удаления в стиле TunPilot (тёмная тема).</summary>
    sealed class SetupForm : Form
    {
        static readonly Color Bg = Color.FromArgb(24, 25, 28), Panel = Color.FromArgb(32, 34, 38), Header = Color.FromArgb(42, 44, 50),
            Grid = Color.FromArgb(48, 50, 56), Text_ = Color.FromArgb(222, 224, 228), Muted = Color.FromArgb(140, 145, 155),
            Accent = Color.FromArgb(87, 199, 120), Danger = Color.FromArgb(235, 90, 90), Warn = Color.FromArgb(222, 186, 70);

        readonly bool _uninstall;
        readonly string _uninstallDir;
        readonly TextBox _dir = new TextBox();
        readonly CheckBox _desktop = Chk(T("Ярлык на рабочем столе", "Desktop shortcut"), true);
        readonly CheckBox _autostart = Chk(T("Запускать вместе с Windows (в трей)", "Start with Windows (in the tray)"), true);
        readonly CheckBox _launch = Chk(T("Запустить TunPilot после установки", "Launch TunPilot after installation"), true);
        readonly CheckBox _removeData = Chk(T("Удалить также настройки, подписки и статистику", "Also delete settings, subscriptions and statistics"), false);
        readonly Line _progress = new Line();
        readonly Label _status = new Label { AutoSize = false, Height = 20, ForeColor = Muted };
        readonly Button _main = new Button(), _cancel = new Button();
        bool _done, _busy;
        readonly int? _cmp;   // сравнение с установленной версией (см. Installer.CompareWithInstalled)

        public SetupForm(bool uninstall, string uninstallDir)
        {
            _uninstall = uninstall; _uninstallDir = uninstallDir;
            Text = uninstall ? T("Удаление TunPilot", "Uninstall TunPilot") : T("Установка TunPilot", "Install TunPilot") + (Installer.PayloadArch == "arm64" ? " (ARM64)" : "");
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg; ForeColor = Text_;
            Font = new Font("Segoe UI", 9.75f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(560, uninstall ? 300 : 380);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // шапка
            var head = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Panel };
            var logo = new PictureBox { Size = new Size(56, 56), Location = new Point(24, 18), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Panel };
            try { using (var s = typeof(SetupForm).Assembly.GetManifestResourceStream("app.ico")) logo.Image = new Icon(s, 64, 64).ToBitmap(); } catch { }
            var title = new Label { Text = uninstall ? T("Удаление TunPilot", "Uninstall TunPilot") : "TunPilot", Font = new Font("Segoe UI", 16f, FontStyle.Bold), AutoSize = true, Location = new Point(94, 18), BackColor = Panel };
            var existing = Installer.ExistingVersion;
            _cmp = Installer.CompareWithInstalled();
            var sub = new Label
            {
                AutoSize = true, MaximumSize = new Size(440, 0), Location = new Point(96, 54), ForeColor = Muted, BackColor = Panel,
                Text = uninstall ? T("Программа, ярлыки и автозапуск будут удалены.", "The app, shortcuts and autostart will be removed.")
                    : _cmp > 0 ? T($"Обновление {existing} → {Installer.Version}. Настройки сохранятся.", $"Update {existing} → {Installer.Version}. Settings are kept.")
                    : _cmp == 0 ? T($"Версия {Installer.Version} уже установлена. Настройки сохранятся.", $"Version {Installer.Version} is already installed. Settings are kept.")
                    : _cmp < 0 ? T($"Установлена более новая {existing}, а это {Installer.Version}.", $"A newer version {existing} is installed; this is {Installer.Version}.")
                    : existing != null ? T($"Версия {Installer.Version} (установлена {existing}). Настройки сохранятся.", $"Version {Installer.Version} (installed: {existing}). Settings are kept.")
                    : T($"Версия {Installer.Version} · VPN-клиент с TUN-режимом", $"Version {Installer.Version} · VPN client with TUN mode"),
            };
            if (_cmp < 0) sub.ForeColor = Warn;
            head.Controls.AddRange(new Control[] { logo, title, sub });
            Controls.Add(head);

            int y = 112;
            if (!uninstall)
            {
                Controls.Add(new Label { Text = T("Папка установки", "Install folder"), AutoSize = true, Location = new Point(24, y), ForeColor = Muted });
                y += 24;
                _dir.Text = Installer.DefaultDir;
                _dir.SetBounds(24, y, 404, 26);
                _dir.BackColor = Panel; _dir.ForeColor = Text_; _dir.BorderStyle = BorderStyle.FixedSingle;
                var browse = Btn(T("Обзор…", "Browse…"), false);
                browse.SetBounds(436, y - 2, 100, 32);
                browse.Click += (s, e) =>
                {
                    using (var d = new FolderBrowserDialog { SelectedPath = _dir.Text, Description = T("Папка для TunPilot", "Folder for TunPilot") })
                        if (d.ShowDialog(this) == DialogResult.OK)
                            _dir.Text = Path.GetFileName(d.SelectedPath).Equals("TunPilot", StringComparison.OrdinalIgnoreCase) ? d.SelectedPath : Path.Combine(d.SelectedPath, "TunPilot");
                };
                Controls.AddRange(new Control[] { _dir, browse });
                y += 44;
                foreach (var c in new[] { _desktop, _autostart, _launch }) { c.Location = new Point(24, y); Controls.Add(c); y += 28; }
                if (existing != null) _autostart.Checked = Installer.AutostartEnabled;
            }
            else
            {
                _removeData.Location = new Point(24, y + 4);
                Controls.Add(_removeData);
                Controls.Add(new Label
                {
                    Text = T("Если оставить настройки, при повторной установке вернутся подписки, профили и статистика.", "If you keep the settings, subscriptions, profiles and statistics come back when you reinstall."),
                    AutoSize = true, MaximumSize = new Size(510, 0), Location = new Point(44, y + 32), ForeColor = Muted,
                });
            }

            _progress.SetBounds(24, ClientSize.Height - 98, 512, 4);
            _status.SetBounds(24, ClientSize.Height - 88, 512, 22);
            Controls.AddRange(new Control[] { _progress, _status });

            _main.Text = uninstall ? T("Удалить", "Uninstall") : _cmp > 0 ? T("Обновить", "Update") : _cmp == 0 ? T("Переустановить", "Reinstall") : _cmp < 0 ? T("Откатить", "Downgrade") : existing != null ? T("Обновить", "Update") : T("Установить", "Install");
            StyleBtn(_main, true, uninstall ? Danger : Accent);
            // ширина по тексту («Переустановить» длиннее «Установить»)
            int mainW = Math.Max(130, TextRenderer.MeasureText(_main.Text, _main.Font).Width + 36);
            _main.SetBounds(ClientSize.Width - 24 - 110 - 10 - mainW, ClientSize.Height - 54, mainW, 34);
            _cancel.Text = T("Отмена", "Cancel");
            StyleBtn(_cancel, false, Accent);
            _cancel.SetBounds(ClientSize.Width - 24 - 110, ClientSize.Height - 54, 110, 34);
            _main.Click += async (s, e) => await MainClick();
            _cancel.Click += (s, e) => Close();
            Controls.AddRange(new Control[] { _main, _cancel });
            AcceptButton = _main;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1;
            if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref on, 4);   // тёмный заголовок
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ActiveControl = _main;          // без синего выделения пути в поле папки
            _dir.SelectionLength = 0;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy) e.Cancel = true;   // не прерываем на середине
            base.OnFormClosing(e);
        }

        async Task MainClick()
        {
            if (_done)
            {
                if (!_uninstall && _launch.Checked)
                    try { Process.Start(new ProcessStartInfo(Path.Combine(_dir.Text.Trim(), "TunPilot.exe")) { UseShellExecute = true, WorkingDirectory = _dir.Text.Trim() }); } catch { }
                Close();
                return;
            }

            if (Installer.AppRunning)
            {
                var msg = _uninstall ? T("TunPilot сейчас запущен. Закрыть его и продолжить удаление?\nVPN-подключение прервётся.", "TunPilot is running. Close it and continue uninstalling?\nThe VPN connection will drop.")
                                     : T("TunPilot сейчас запущен. Закрыть его для обновления?\nVPN-подключение прервётся на время установки.", "TunPilot is running. Close it to update?\nThe VPN connection will drop during installation.");
                if (MessageBox.Show(this, msg, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            }

            // установщик не под тот процессор
            if (!_uninstall)
            {
                bool osArm = Installer.OsIsArm64, payloadArm = Installer.PayloadArch == "arm64";
                if (payloadArm && !osArm)
                {
                    MessageBox.Show(this, T("Это установщик для компьютеров на ARM (Snapdragon и т. п.), а у вас обычный процессор x64.\nСкачайте TunPilot-Setup-<версия>.exe без «arm64» в названии.",
                                            "This installer is for ARM computers (Snapdragon etc.), but this PC has a regular x64 processor.\nDownload TunPilot-Setup-<version>.exe without “arm64” in the name."),
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (!payloadArm && osArm &&
                    MessageBox.Show(this, T("У вас компьютер на ARM (Snapdragon и т. п.). Для него есть отдельная версия — TunPilot-Setup-<версия>-arm64.exe: она работает быстрее и надёжнее.\n\nВсё равно установить обычную версию (x64, через эмуляцию)?",
                                            "This is an ARM computer (Snapdragon etc.). There is a dedicated build for it — TunPilot-Setup-<version>-arm64.exe — which is faster and more reliable.\n\nInstall the regular x64 version (emulated) anyway?"),
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return;
            }

            // защита от случайного отката на старую версию поверх новой
            if (!_uninstall && _cmp < 0 &&
                MessageBox.Show(this,
                    T($"Сейчас установлена версия {Installer.ExistingVersion}, а этот установщик — {Installer.Version} (старее).\n\n" +
                      "Обычно это ошибка: скачан старый файл. Всё равно установить старую версию поверх новой?\nНастройки сохранятся.",
                      $"Version {Installer.ExistingVersion} is installed, and this installer is {Installer.Version} (older).\n\n" +
                      "This is usually a mistake: an old file was downloaded. Install the older version over the newer one anyway?\nSettings are kept."),
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            var dir = _uninstall ? _uninstallDir : _dir.Text.Trim();
            if (!_uninstall && (dir.Length < 4 || !Path.IsPathRooted(dir)))
            {
                MessageBox.Show(this, T("Укажите полный путь к папке, например C:\\Program Files\\TunPilot", "Enter the full folder path, for example C:\\Program Files\\TunPilot"), Text);
                return;
            }

            _busy = true;
            _main.Enabled = _cancel.Enabled = false;
            foreach (Control c in Controls) if (c is CheckBox || c is TextBox || (c is Button b && b != _main && b != _cancel)) c.Enabled = false;
            bool desktop = _desktop.Checked, autostart = _autostart.Checked, removeData = _removeData.Checked;
            Action<int, string> progress = (p, s) => BeginInvoke((Action)(() => { _progress.Value = p / 100f; _progress.Invalidate(); _status.Text = s; }));
            try
            {
                await Task.Run(() =>
                {
                    if (_uninstall) Installer.Uninstall(dir, removeData, progress);
                    else Installer.Install(dir, desktop, autostart, progress);
                });
                _done = true;
                _status.ForeColor = Accent;
                _status.Text = _uninstall ? T("TunPilot удалён.", "TunPilot was uninstalled.") : T("Установка завершена.", "Installation complete.");
                _main.Text = T("Готово", "Done");
                _main.Enabled = true;
                _cancel.Visible = false;
                _launch.Enabled = true;
            }
            catch (Exception ex)
            {
                _status.ForeColor = Danger;
                _status.Text = T("Ошибка: ", "Error: ") + ex.Message;
                MessageBox.Show(this, (_uninstall ? T("Не удалось удалить TunPilot:\n", "Failed to uninstall TunPilot:\n") : T("Не удалось установить TunPilot:\n", "Failed to install TunPilot:\n")) + ex.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _main.Enabled = _cancel.Enabled = true;
                foreach (Control c in Controls) c.Enabled = true;
            }
            finally { _busy = false; }
        }

        static CheckBox Chk(string text, bool on) =>
            new CheckBox { Text = text, Checked = on, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Text_ };

        Button Btn(string text, bool primary) { var b = new Button { Text = text }; StyleBtn(b, primary, Accent); return b; }

        static void StyleBtn(Button b, bool primary, Color accent)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.Cursor = Cursors.Hand;
            b.BackColor = primary ? accent : Header;
            b.ForeColor = primary ? Color.FromArgb(14, 20, 16) : Text_;
            b.Font = new Font("Segoe UI", 9.75f, primary ? FontStyle.Bold : FontStyle.Regular);
            b.FlatAppearance.BorderColor = primary ? accent : Grid;
            b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(accent, 0.2f) : Color.FromArgb(52, 56, 64);
        }

        /// <summary>Тонкая полоска прогресса.</summary>
        sealed class Line : Control
        {
            public float Value;
            public Line() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Grid);
                using (var b = new SolidBrush(Accent)) e.Graphics.FillRectangle(b, 0, 0, (int)(Width * Math.Max(0, Math.Min(1, Value))), Height);
            }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
