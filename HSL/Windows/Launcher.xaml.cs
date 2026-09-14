using HSL.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace HSL.Windows
{
    public partial class Launcher : Window, IDisposable, INotifyPropertyChanged
    {

        private HwndSource _windowSource;
        private bool _reloadHotkeyRegistered;
        private bool _windowSourceHooked;

        private const int WmHotkey = 0x0312;
        private const int ReloadHotkeyId = 0x48534C;
        private const uint ModNoRepeat = 0x4000;

        public event PropertyChangedEventHandler PropertyChanged;

        public ServerManager manager { get; private set; }
        public ServerInstance currentInstance { get; private set; } = default(ServerInstance);
        public ServerInstance.ResourceMeta currentResource { get; private set; } = default(ServerInstance.ResourceMeta);

        // public List<string> Languages { get; private set; }

        internal HSLConfig Config { get; private set; }
        private OpenFileDialog _ofd;
        private object _configLock { get; set; } = new object();
        private Timer _timer;
        private readonly EventHandler<string?> _serverLogHandler;
        private bool _configurationLoaded;
        private bool _isApplyingWindowSize;
        private System.Timers.Timer _scrollDebounceTimer;
        private bool autoScrollToEnd => cb_AutoScroll?.IsChecked == true;

        public Launcher()
        {
            SourceInitialized += Launcher_SourceInitialized;
            InitializeComponent();
            SizeChanged += Launcher_SizeChanged;

            _serverLogHandler = (s, line) => Dispatcher.Invoke(() => AppendServerLogLine(line));

            _scrollDebounceTimer = new System.Timers.Timer(150); // adjust if needed
            _scrollDebounceTimer.AutoReset = false;
            _scrollDebounceTimer.Elapsed += (s, e) => Dispatcher.Invoke(() =>
            {
                if (autoScrollToEnd)
                {
                    rtb_ServerLog.ScrollToEnd();
                    rtb_ServerLog.ScrollToVerticalOffset(double.MaxValue);
                }
            });

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Utils.AppendToCrashReport(((Exception)e.ExceptionObject).ToString());
                if (e.IsTerminating)
                {
                    Dispose();
                }
            };

            /*
            Languages = new List<string>();
            foreach(var resource in System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceNames())
            {
                if(resource.IndexOf("HSL.Lang.") >= 0) {
                    Languages.Add(resource.Substring(9));
                }
            }

            /*
            ResourceDictionary dictionary = new ResourceDictionary();
            dictionary.Source = new Uri("pack://application:,,,/HSL;component/Lang/eng.xaml", UriKind.Absolute);
            Application.Current.Resources.MergedDictionaries.Add(dictionary);

            */

            Closing += (s, e) => Dispose();

            manager = new ServerManager(this);
            manager.OnCreated += Manager_OnCreated;
            manager.OnDeleted += Manager_OnDeleted;

            DataContext = this;
            _timer = new Timer() { Enabled = true, Interval = 2500 };

            LoadConfiguration().ConfigureAwait(false).GetAwaiter(); // intentional thread lock

            if (manager.servers.Count > 0)
            {
                ShowServerContext(manager.servers.FirstOrDefault());
            }

            RegisterListeners();
            _timer.Start();
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private void Launcher_SourceInitialized(object sender, EventArgs e)
        {
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (_windowSource != null)
            {
                _windowSource.AddHook(LauncherWndProc);
                _windowSourceHooked = true;
            }

            TryRegisterReloadHotkey();
        }

        private IntPtr LauncherWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey && wParam.ToInt32() == ReloadHotkeyId)
            {
                currentInstance?.ReloadAllResources();
                handled = true;
            }

            return IntPtr.Zero;
        }

        private bool TryRegisterReloadHotkey()
        {
            UnregisterReloadHotkey();

            if (Config == null || _windowSource == null || string.IsNullOrWhiteSpace(Config.reload_all_resources_key) ||
                !Enum.TryParse(Config.reload_all_resources_key, true, out Key reloadKey) || reloadKey == Key.None)
            {
                return false;
            }

            int virtualKey = KeyInterop.VirtualKeyFromKey(reloadKey);
            if (virtualKey == 0 || !RegisterHotKey(_windowSource.Handle, ReloadHotkeyId, ModNoRepeat, (uint)virtualKey))
            {
                Trace.WriteLine($"Failed to register reload hotkey '{Config.reload_all_resources_key}'. Win32 error: {Marshal.GetLastWin32Error()}");
                return false;
            }

            _reloadHotkeyRegistered = true;
            return true;
        }

        private void UnregisterReloadHotkey()
        {
            if (_reloadHotkeyRegistered && _windowSource != null)
            {
                UnregisterHotKey(_windowSource.Handle, ReloadHotkeyId);
                _reloadHotkeyRegistered = false;
            }
        }

        private async Task BindReloadHotkey()
        {
            Key? selectedKey = CaptureReloadHotkey();
            if (!selectedKey.HasValue || Config == null)
            {
                return;
            }

            string previousKey = Config.reload_all_resources_key;
            Config.reload_all_resources_key = selectedKey.Value.ToString();

            if (!TryRegisterReloadHotkey())
            {
                Config.reload_all_resources_key = previousKey;
                TryRegisterReloadHotkey();
                MessageBox.Show("That key could not be registered globally. It may already be in use by Windows or another application.", "Hotkey Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            UpdateReloadHotkeyMenu();
            await Config.Save();
        }

        private Key? CaptureReloadHotkey()
        {
            Window captureWindow = new Window
            {
                Owner = this,
                Title = "Bind Reload Hotkey",
                Width = 320,
                Height = 120,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.ToolWindow,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = Brushes.Black,
                Foreground = Brushes.White,
                Focusable = true,
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = "Press the key to reload all resources.\nPress Escape to cancel.",
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
            };

            Key capturedKey = Key.None;
            captureWindow.PreviewKeyDown += (s, e) =>
            {
                Key pressedKey = e.Key == Key.System ? e.SystemKey : e.Key;
                if (pressedKey == Key.Escape)
                {
                    captureWindow.DialogResult = false;
                }
                else if (pressedKey != Key.None && !IsModifierKey(pressedKey))
                {
                    capturedKey = pressedKey;
                    captureWindow.DialogResult = true;
                }

                e.Handled = true;
            };
            captureWindow.Loaded += (s, e) => captureWindow.Focus();

            bool? result = captureWindow.ShowDialog();
            return result == true ? capturedKey : (Key?)null;
        }

        private static bool IsModifierKey(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl ||
                   key == Key.LeftAlt || key == Key.RightAlt ||
                   key == Key.LeftShift || key == Key.RightShift ||
                   key == Key.LWin || key == Key.RWin;
        }

        private void UpdateReloadHotkeyMenu()
        {
            if (Config != null)
            {
                mi_BindReloadHotkey.Header = $"Bind Reload Hotkey (currently {Config.reload_all_resources_key})...";
            }
        }

        private async Task LoadConfiguration()
        {
            Config = await HSLConfig.Load("hsl.json");
            UpdateReloadHotkeyMenu();
            TryRegisterReloadHotkey();

            _isApplyingWindowSize = true;
            try
            {
                if (Config.window_width > 0)
                {
                    Width = Config.window_width;
                }

                if (Config.window_height > 0)
                {
                    Height = Config.window_height;
                }
            }
            finally
            {
                _isApplyingWindowSize = false;
                _configurationLoaded = true;
            }

            List<Guid> deleteCache = new List<Guid>();
            bool markdirty = false;
            lock (_configLock)
            {
                string directory;
                foreach (var key in Config.servers.Keys)
                {
                    directory = Path.GetDirectoryName(Config.servers[key].exe_file);
                    if (!Directory.Exists(directory) || !ServerInstance.IsValidInstallation(directory))
                    {
                        if (MessageBox.Show(String.Format("Failed to load pre-existing server @ {0}.{1}{2}Would you like to change location?", Config.servers[key].exe_file, Environment.NewLine, Config.servers[key].exe_file), "Error", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                        {
                            _ofd ??= new OpenFileDialog();
                            _ofd.Multiselect = false;
                            _ofd.Filter = "HappinessMP.Server.Exe | *.exe";
                            if (!(_ofd?.ShowDialog() ?? false) || string.IsNullOrEmpty(_ofd.FileName) || Config.servers.Any(x => x.Value.exe_file == _ofd.FileName))
                            {
                                deleteCache.Add(key);
                                continue;
                            }
                            Config.servers[key].exe_file = _ofd.FileName;
                            markdirty = true;
                        }
                    }
                    manager.Create(Config.servers[key]);
                }
            }

            foreach (Guid guid in deleteCache)
            {
                Config.servers.Remove(guid);
            }

            if (markdirty)
            {
                await Config.Save();
            }

        }

        private async void Launcher_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_configurationLoaded || _isApplyingWindowSize || Config == null)
            {
                return;
            }

            Config.window_width = e.NewSize.Width;
            Config.window_height = e.NewSize.Height;
            await Config.Save();
        }

        private async void Manager_OnDeleted(object sender, ServerInstance e)
        {
            if (e == currentInstance)
            {
                ShowServerContext(null);
            }

            if (Config.servers.ContainsKey(e.Guid))
            {
                Config.servers.Remove(e.Guid);
                await Config.Save();
            }
        }

        private async void Manager_OnCreated(object sender, ServerInstance e)
        {
            if (!Config.servers.ContainsKey(e.Guid))
            {
                Config.servers.Add(e.Guid, e.ServerData);
                await Config.Save();
            }
        }

        internal void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void ShowServerContext(ServerInstance instance)
        {
            if (currentInstance != null)
            {
                currentInstance.StdOutput -= _serverLogHandler;
            }

            currentInstance = instance;
            OnPropertyChanged(nameof(currentInstance));
            RenderServerLog(currentInstance);

            if (currentInstance != null)
            {
                currentInstance.StdOutput += _serverLogHandler;
            }

            Title = currentInstance != null ? String.Format("HSL - {0}", currentInstance.Name) : "Happiness Server Launcher";
        }

        private void RenderServerLog(ServerInstance instance)
        {
            FlowDocument document = CreateServerLogDocument();

            if (instance != null)
            {
                foreach (string line in instance.ServerLog)
                {
                    AddLogParagraph(document, line);
                }
            }

            rtb_ServerLog.Document = document;

            if (autoScrollToEnd)
            {
                rtb_ServerLog.ScrollToEnd();
            }
        }

        private void AppendServerLogLine(string line)
        {
            if (currentInstance == null || string.IsNullOrEmpty(line))
            {
                return;
            }

            if (rtb_ServerLog.Document == null)
            {
                rtb_ServerLog.Document = CreateServerLogDocument();
            }

            AddLogParagraph(rtb_ServerLog.Document, line);

            if (autoScrollToEnd)
            {
                _scrollDebounceTimer.Stop();
                _scrollDebounceTimer.Start();
            }
        }

        private static FlowDocument CreateServerLogDocument()
        {
            return new FlowDocument
            {
                PagePadding = new Thickness(2),
                Background = Brushes.Transparent
            };
        }

        private static void AddLogParagraph(FlowDocument document, string line)
        {
            Paragraph paragraph = new Paragraph
            {
                Margin = new Thickness(0)
            };

            Match match = Regex.Match(line ?? string.Empty, @"^(\[[^\]]+\])\s+(\[[^\]]+\])\s*(.*)$");
            if (!match.Success)
            {
                paragraph.Inlines.Add(new Run(line ?? string.Empty)
                {
                    Foreground = GetMessageBrush(line)
                });
                document.Blocks.Add(paragraph);
                return;
            }

            string timestamp = match.Groups[1].Value;
            string category = match.Groups[2].Value;
            string message = match.Groups[3].Value;

            paragraph.Inlines.Add(new Run(timestamp + " ")
            {
                Foreground = Brushes.DimGray
            });
            paragraph.Inlines.Add(new Run(category)
            {
                Foreground = GetCategoryBrush(category)
            });

            if (!string.IsNullOrWhiteSpace(message))
            {
                paragraph.Inlines.Add(new Run(" " + message)
                {
                    Foreground = GetMessageBrush(message)
                });
            }

            document.Blocks.Add(paragraph);
        }

        private static Brush GetCategoryBrush(string category)
        {
            switch ((category ?? string.Empty).ToLowerInvariant())
            {
                case "[server]":
                    return Brushes.DeepSkyBlue;
                case "[network]":
                    return Brushes.MediumPurple;
                case "[httpserver]":
                    return Brushes.CornflowerBlue;
                case "[resourcemanager]":
                    return Brushes.MediumSeaGreen;
                case "[resource]":
                    return Brushes.LimeGreen;
                case "[addonmanager]":
                    return Brushes.Turquoise;
                case "[script]":
                    return Brushes.Goldenrod;
                default:
                    return Brushes.LightGray;
            }
        }

        private static Brush GetMessageBrush(string line)
        {
            string normalized = line?.ToLowerInvariant() ?? string.Empty;

            if (normalized.Contains("[error]"))
            {
                return Brushes.HotPink;
            }

            if (normalized.Contains("[warn]"))
            {
                return Brushes.Orange;
            }

            if (normalized.Contains("[success]"))
            {
                return Brushes.SpringGreen;
            }

            if (normalized.Contains("[critical]") || normalized.Contains("critical") || normalized.Contains("fatal") || normalized.Contains("exception"))
            {
                return Brushes.OrangeRed;
            }

            if (normalized.Contains("[error]") || normalized.Contains("error") || normalized.Contains("failed"))
            {
                return Brushes.IndianRed;
            }

            if (normalized.Contains("[warn]") || normalized.Contains("warn"))
            {
                return Brushes.Gold;
            }

            if (normalized.Contains("[debug]") || normalized.Contains("[trace]") || normalized.Contains("debug") || normalized.Contains("trace"))
            {
                return Brushes.Silver;
            }

            if (normalized.Contains("[load]") || normalized.Contains("loaded"))
            {
                return Brushes.PaleGreen;
            }

            if (normalized.Contains("started") || normalized.Contains("listening") || normalized.Contains("loaded") ||
                normalized.Contains("connected") || normalized.Contains("joined") || normalized.Contains("success"))
            {
                return Brushes.LightGreen;
            }

            if (normalized.Contains("stop") || normalized.Contains("disconnect") || normalized.Contains("left"))
            {
                return Brushes.Khaki;
            }

            if (normalized.Contains("http") || normalized.Contains("tcp") || normalized.Contains("udp") || normalized.Contains("port"))
            {
                return Brushes.LightSkyBlue;
            }

            return Brushes.White;
        }

        private void RegisterListeners()
        {

            _timer.Elapsed += async (s, e) =>
            {
                if (manager.IsDirty(true))
                {
                    await Config.Save();
                }
            };

            lv_ServerList.SelectionChanged += (s, e) =>
            {
                if (lv_ServerList.SelectedItem != null && lv_ServerList.SelectedItem is ServerInstance instance)
                {
                    currentResource = null;
                    OnPropertyChanged(nameof(currentResource));
                    ShowServerContext(instance);
                }
            };

            lv_ResourceList.SelectionChanged += (s, e) =>
            {
                if (lv_ResourceList.SelectedItem is ServerInstance.ResourceMeta meta)
                {
                    currentResource = meta;
                    OnPropertyChanged(nameof(currentResource));
                }
            };

            tb_ServerCmd.KeyUp += (s, e) =>
            {
                if (e.Key == Key.Enter && currentInstance != null)
                {
                    currentInstance.SendInput(tb_ServerCmd.Text);
                    tb_ServerCmd.Clear();
                }
            };

            mi_OpenServerPath.Click += (s, e) =>
            {
                _ofd ??= new OpenFileDialog();
                _ofd.Filter = "HappinessMP.Server.Exe | *.exe";
                if (_ofd.ShowDialog() ?? false)
                {
                    if (!ServerInstance.IsValidInstallation(Path.GetDirectoryName(_ofd.FileName)))
                    {
                        MessageBox.Show("This path does not contain a valid HappinessMP server.", "Error", MessageBoxButton.OK);
                        return;
                    }

                    if (Config.servers.Any(x => x.Value.exe_file == _ofd.FileName))
                    {
                        MessageBox.Show("This server has already been added.");
                        return;
                    }
                    ShowServerContext(manager.Create(_ofd.FileName, false));
                }
            };


            btn_ClearServerLog.Click += (s, e) =>
            {
                currentInstance?.ClearServerLog();
                RenderServerLog(currentInstance);
            };

            btn_StartResource.Click += (s, e) =>
            {
                if (currentInstance != null && lv_ResourceList.SelectedItem is ServerInstance.ResourceMeta meta && !string.IsNullOrEmpty(meta.Name))
                {
                    currentInstance?.StartResource(meta.Name);
                }
            };

            btn_StopResource.Click += (s, e) =>
            {
                if (currentInstance != null && lv_ResourceList.SelectedItem is ServerInstance.ResourceMeta meta && !string.IsNullOrEmpty(meta.Name))
                {
                    currentInstance?.StopResource(meta.Name);
                }
            };

            btn_ReloadResource.Click += (s, e) =>
            {
                if (currentInstance != null && lv_ResourceList.SelectedItem is ServerInstance.ResourceMeta meta && !string.IsNullOrEmpty(meta.Name))
                {
                    currentInstance?.ReloadResource(meta.Name);
                }
            };

            btn_StopAllResources.Click += (s, e) => currentInstance?.StopAllResources();

            btn_StartAllResources.Click += (s, e) => currentInstance?.StartAllResources();

            btn_ReloadAllResources.Click += (s, e) => currentInstance?.ReloadAllResources();

            mi_BindReloadHotkey.Click += async (s, e) => await BindReloadHotkey();

            (lv_ResourceList.ContextMenu = new System.Windows.Controls.ContextMenu()).Items.Add(new System.Windows.Controls.MenuItem() { Header = "Open Folder" });
            (lv_ServerList.ContextMenu = new System.Windows.Controls.ContextMenu()).Items.Add(new System.Windows.Controls.MenuItem() { Header = "Open Folder" });
            (lv_ResourceList.ContextMenu.Items[0] as System.Windows.Controls.MenuItem).Click += (s, e) =>
            {
                if (lv_ResourceList.SelectedIndex >= 0 && lv_ResourceList.SelectedItems is ServerInstance.ResourceMeta meta)
                {
                    Process.Start("explorer.exe", currentInstance.ResourceDirectory.CombinePath(meta.Name));
                }
            };
            (lv_ServerList.ContextMenu.Items[0] as System.Windows.Controls.MenuItem).Click += (s, e) =>
            {
                if (lv_ServerList.SelectedIndex >= 0 && lv_ServerList.SelectedItem is ServerInstance instance)
                {
                    Process.Start("explorer.exe", instance.ServerDirectory);
                }
            };

            mi_StartServer.Click += (s, e) => currentInstance?.Start();

            mi_StopServer.Click += (s, e) => currentInstance?.Stop(true);

            mi_RestartServer.Click += (s, e) => currentInstance?.Restart();

            mi_DeleteServer.Click += (s, e) =>
            {
                if (MessageBox.Show("Are you sure you want to delete" + currentInstance.Name + "? Files will NOT be deleted!", "Delete Server?", MessageBoxButton.YesNo) == MessageBoxResult.No)
                {
                    return;
                }
                manager.Delete(currentInstance);
            };

            mi_DeleteServerCache.Click += (s, e) => currentInstance?.DeleteServerCache();

            mi_OpenServerDirectory.Click += (s, e) => Process.Start("explorer.exe", currentInstance.ServerDirectory);

            mi_UpdateServer.Click += async (s, e) =>
            {

                if (currentInstance == null)
                {
                    return;
                }

                if (currentInstance.State != Enums.ServerState.Stopped)
                {
                    MessageBox.Show("Please stop the server first before updating!");
                    return;
                }


                string url = await Utils.GetLatestServerURL();

                if (string.IsNullOrEmpty(url))
                {
                    MessageBox.Show("Failed to download server files.");
                    return;
                }

                string filename = url.Split("/")[^1];

                byte[] _server_archive = await Utils.HTTP.GetBinaryAsync(url);

                if (_server_archive == null || _server_archive.Length < 1024)
                {
                    MessageBox.Show("Failed to download server files.");
                    return;
                }

                string zip = currentInstance.ServerDirectory.CombinePath(filename + ".tmp");

                if (File.Exists(zip))
                {
                    File.Delete(zip);
                }

                try
                {
                    await File.WriteAllBytesAsync(zip, _server_archive);

                    string version = string.Empty;

                    using (FileStream fs = File.Open(zip, FileMode.Open, FileAccess.Read))
                    {
                        using (ZipArchive archive = new ZipArchive(fs))
                        {
                            if (!archive.Entries.Any(x => x.Name.IndexOf(".exe") > 0))
                            {
                                throw new Exception("Failed to install server files.");
                            }

                            version = archive.Entries[0].FullName;

                            for (int i = 1; i < archive.Entries.Count; i++)
                            {
                                if (archive.Entries[i].FullName.IndexOf("resources") >= 0 || archive.Entries[i].Name == "settings.xml")
                                {
                                    continue;
                                }

                                string file = currentInstance.ServerDirectory.CombinePath(archive.Entries[i].FullName.Substring(archive.Entries[0].FullName.Length));

                                if (File.Exists(file))
                                {
                                    File.Delete(file);
                                }
                                archive.Entries[i].ExtractToFile(file);
                            }
                        }
                    }

                    File.Delete(zip);

                    MessageBox.Show("Updated Server: " + version);

                }
                catch (Exception ee)
                {
                    if (File.Exists(zip))
                    {
                        File.Delete(zip);
                    }
                    MessageBox.Show("Failed to install server files: " + ee.ToString(), "Error", MessageBoxButton.OK);
                }

            };

            mi_CreateServer.Click += async (s, e) =>
            {

                string directory = string.Empty;
                using (System.Windows.Forms.FolderBrowserDialog fbd = new System.Windows.Forms.FolderBrowserDialog())
                {
                    if (fbd.ShowDialog() != System.Windows.Forms.DialogResult.OK || string.IsNullOrEmpty(fbd.SelectedPath))
                    {
                        MessageBox.Show("No directory given to install server.", "Error", MessageBoxButton.OK);
                        return;
                    }

                    directory = fbd.SelectedPath;
                }

                if (!Utils.IsDirectoryEmpty(directory))
                {
                    MessageBox.Show("Directory selected is not empty.");
                    return;
                }

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string url = await Utils.GetLatestServerURL();

                if (string.IsNullOrEmpty(url))
                {
                    MessageBox.Show("Failed to download server files.");
                    return;
                }

                string filename = url.Split("/")[^1];

                byte[] _server_archive = await Utils.HTTP.GetBinaryAsync(url);

                if (_server_archive == null || _server_archive.Length < 1024)
                {
                    MessageBox.Show("Failed to download server files.");
                    return;
                }

                string zip = directory.CombinePath(filename + ".tmp");

                if (!File.Exists(zip))
                {
                    File.Delete(zip);
                }

                try
                {
                    await File.WriteAllBytesAsync(zip, _server_archive);
                    using (FileStream fs = File.Open(zip, FileMode.Open, FileAccess.Read))
                    {
                        using (ZipArchive archive = new ZipArchive(fs))
                        {
                            if (!archive.Entries.Any(x => x.Name.IndexOf(".exe") > 0))
                            {
                                throw new Exception("Corrupted server download. Aborted");
                            }

                            for (int i = 1; i < archive.Entries.Count; i++)
                            {
                                string destination = directory.CombinePath(archive.Entries[i].FullName.Substring(archive.Entries[0].FullName.Length));
                                if (archive.Entries[i].Length == 0)
                                {
                                    Directory.CreateDirectory(destination);
                                    continue;
                                }
                                archive.Entries[i].ExtractToFile(destination);
                            }
                        }
                    }
                    File.Delete(zip);
                }
                catch (Exception ee)
                {
                    if (!File.Exists(zip))
                    {
                        File.Delete(zip);
                    }
                    Utils.AppendToCrashReport(ee.ToString());
                    MessageBox.Show("Failed to install server files: " + ee.ToString(), "Error", MessageBoxButton.OK);
                    return;
                }
                manager.Create(Directory.GetFiles(directory, "*.exe").FirstOrDefault(), false);
            };
        }

        public void Dispose()
        {
            UnregisterReloadHotkey();
            if (_windowSource != null && _windowSourceHooked)
            {
                _windowSource.RemoveHook(LauncherWndProc);
                _windowSourceHooked = false;
                _windowSource = null;
            }

            if (manager != null)
            {
                Dispatcher.Invoke(manager.Dispose);
            }
        }
    }
}
