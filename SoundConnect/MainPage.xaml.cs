using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Media;
using SoundConnect.Sony;

namespace SoundConnect
{
    public sealed partial class MainPage : Page
    {
        readonly SonySession _session = new SonySession();
        SonyHeadphones _hp;
        NoiseControlState _nc = new NoiseControlState { Mode = NoiseMode.Off, AmbientLevel = 10 };
        readonly List<DeviceInformation> _devices = new List<DeviceInformation>();
        bool _loading;              // true while we are filling controls from the headphones
        DispatcherTimer _ambientDebounce;
        DeviceWatcher _watcher;     // watches the XM4 come and go while the app is open
        bool _connecting;           // a connect attempt (manual or auto) is in flight
        bool _autoSuppressed;       // user tapped Disconnect: no auto-reconnect until the XM4 leaves and returns
        bool _autoWaitLogged;       // "will auto-connect when it connects" already logged
        int _tileBattery = -1;      // latest values read from the headphones, for the live tile
        bool _tileCharging;
        string _tileNc = "";
        string _tileEq = "";

        public MainPage()
        {
            InitializeComponent();
            SetControlsVisible(false);
            foreach (var name in EqPresets.Names) EqPresetCombo.Items.Add(name);
            _session.Notification += OnNotification;
            _session.Disconnected += OnDisconnected;
        }

        // The whole controls section stays hidden until the headphones are
        // connected (owner's requirement) - it appears on connect and
        // disappears again on disconnect.
        void SetControlsVisible(bool visible)
        {
            ControlsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _autoSuppressed = false;
            await RefreshDevicesAsync();
            StartWatcher();
            var _ = TileRefresh.RegisterAsync();
            await TryAutoConnectAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            StopWatcher();
        }

        // ---------- helpers ----------

        async Task RunOnUi(Action action)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
        }

        void Log(string msg)
        {
            var _ = RunOnUi(() =>
            {
                var line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
                LogText.Text = LogText.Text.Length == 0 ? line : LogText.Text + "\n" + line;
            });
        }

        async Task Guard(Func<Task> work, string what)
        {
            try { await work(); }
            catch (Exception ex) { Log(what + " failed: " + ex.Message); }
        }

        // ---------- device list / connect ----------

        async Task RefreshDevicesAsync()
        {
            try
            {
                _devices.Clear();
                DeviceCombo.Items.Clear();
                var found = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelector());
                foreach (var d in found)
                {
                    // This app serves the WH-1000XM4 only - other paired
                    // Bluetooth devices never appear in the list.
                    var n = (d.Name ?? "").ToUpperInvariant();
                    if (!n.Contains("WH-1000XM4")) continue;
                    _devices.Add(d);
                    DeviceCombo.Items.Add(d.Name);
                }
                if (DeviceCombo.Items.Count > 0) DeviceCombo.SelectedIndex = 0;
                Log(_devices.Count > 0 ? "Found WH-1000XM4" : "WH-1000XM4 not found - pair it in Settings first");
            }
            catch (Exception ex) { Log("Device list failed: " + ex.Message); }
        }

        async void RefreshDevicesBtn_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDevicesAsync();
        }

        bool _logExpanded;
        void LogToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            _logExpanded = !_logExpanded;
            LogScroller.MaxHeight = _logExpanded ? 4000 : 110;
            LogToggleBtn.Content = _logExpanded ? "Short" : "Expand";
        }

        async void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_session.IsConnected)
            {
                _autoSuppressed = true; // manual disconnect: don't auto-reconnect behind the user's back
                _session.Disconnect();
                _hp = null;
                StatusText.Text = "Not connected";
                ConnectBtn.Content = "Connect";
                SetControlsVisible(false);
                LiveTile.Clear();
                Log("Disconnected");
                return;
            }

            int idx = DeviceCombo.SelectedIndex;
            if (idx < 0 || idx >= _devices.Count) { Log("Pick a device first"); return; }

            await ConnectToAsync(_devices[idx]);
        }

        // The one connect path, used by the Connect button and by auto-connect.
        async Task ConnectToAsync(DeviceInformation info)
        {
            if (_connecting || _session.IsConnected) return;
            _connecting = true;
            ConnectBtn.IsEnabled = false;
            StatusText.Text = "Connecting...";
            try
            {
                var device = await BluetoothDevice.FromIdAsync(info.Id);
                if (device == null) throw new InvalidOperationException("Could not open the Bluetooth device");
                await _session.ConnectAsync(device);
                _hp = new SonyHeadphones(_session);
                StatusText.Text = "Connected to " + device.Name;
                ConnectBtn.Content = "Disconnect";
                SetControlsVisible(true);
                Log("Connected to " + device.Name);
                await _hp.InitDeviceAsync();
                await RefreshAllAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Connection failed";
                Log("Connect failed: " + ex.Message);
                _session.Disconnect();
                _hp = null;
            }
            finally { _connecting = false; ConnectBtn.IsEnabled = true; }
        }

        // ---------- auto-detect / auto-connect ----------

        static bool IsXm4(DeviceInformation d)
        {
            return (d.Name ?? "").ToUpperInvariant().Contains("WH-1000XM4");
        }

        void EnsureListed(DeviceInformation info)
        {
            foreach (var d in _devices) if (d.Id == info.Id) return;
            _devices.Add(info);
            DeviceCombo.Items.Add(info.Name);
            if (DeviceCombo.SelectedIndex < 0) DeviceCombo.SelectedIndex = 0;
        }

        // Auto-connect only when the phone itself reports the XM4 as connected
        // (headphones on and linked). A paired-but-off XM4 is left alone - the
        // watcher retries the moment it actually connects.
        async Task TryAutoConnectAsync()
        {
            if (_session.IsConnected || _connecting || _autoSuppressed) return;
            if (_devices.Count == 0) return;
            var info = _devices[0];
            BluetoothDevice device;
            try { device = await BluetoothDevice.FromIdAsync(info.Id); }
            catch { return; }
            if (device == null) return;
            if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            {
                if (!_autoWaitLogged)
                {
                    _autoWaitLogged = true;
                    Log("WH-1000XM4 is paired but not connected - it will connect automatically as soon as it connects to the phone");
                }
                return;
            }
            _autoWaitLogged = false;
            Log("WH-1000XM4 connected to the phone - connecting automatically");
            await ConnectToAsync(info);
        }

        void StartWatcher()
        {
            if (_watcher != null) return;
            _watcher = DeviceInformation.CreateWatcher(BluetoothDevice.GetDeviceSelector());
            _watcher.Added += Watcher_Added;
            _watcher.Updated += Watcher_Updated;
            _watcher.Removed += Watcher_Removed;
            try { _watcher.Start(); } catch { /* watcher is best-effort */ }
        }

        void StopWatcher()
        {
            if (_watcher == null) return;
            try { _watcher.Stop(); } catch { }
            _watcher = null;
        }

        void Watcher_Added(DeviceWatcher sender, DeviceInformation info)
        {
            if (!IsXm4(info)) return;
            var _ = RunOnUi(() =>
            {
                EnsureListed(info);
                var __ = TryAutoConnectAsync();
            });
        }

        void Watcher_Updated(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            if (_devices.Count == 0 || update.Id != _devices[0].Id) return;
            var _ = RunOnUi(() => { var __ = TryAutoConnectAsync(); });
        }

        void Watcher_Removed(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            if (_devices.Count == 0 || update.Id != _devices[0].Id) return;
            // The XM4 left: the next appearance is a fresh start for auto-connect.
            var _ = RunOnUi(() => { _autoSuppressed = false; _autoWaitLogged = false; });
        }

        // Keep the Start-screen tile showing the last values read.
        void PushTile()
        {
            if (_tileBattery < 0) return;
            LiveTile.Update(_tileBattery, _tileCharging, _tileNc, _tileEq);
        }

        void OnDisconnected()
        {
            var _ = RunOnUi(() =>
            {
                StatusText.Text = "Disconnected";
                ConnectBtn.Content = "Connect";
                SetControlsVisible(false);
                _hp = null;
            });
            LiveTile.Clear();
            Log("Headphones disconnected");
        }

        void OnNotification(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return;
            Log("Headphones update: " + BitConverter.ToString(payload).Replace("-", " "));
        }

        // ---------- refresh ----------

        async void RefreshBtn_Click(object sender, RoutedEventArgs e) { await RefreshAllAsync(); }

        async Task RefreshAllAsync()
        {
            if (_hp == null) return;
            _loading = true;
            try
            {
                await Guard(async () =>
                {
                    var b = await _hp.GetBatteryAsync();
                    BatteryText.Text = "Battery: " + b.Item1 + "%";
                    ChargingText.Text = b.Item2 ? "charging" : "";
                    _tileBattery = b.Item1; _tileCharging = b.Item2;
                }, "Battery read");

                await Guard(async () =>
                {
                    _nc = await _hp.GetNoiseControlAsync();
                    _tileNc = LiveTile.DescribeNc(_nc);
                    AmbientSlider.Value = _nc.AmbientLevel >= 1 ? _nc.AmbientLevel : 10;
                    AmbientValueText.Text = _nc.Mode == NoiseMode.Ambient ? _nc.AmbientLevel.ToString() : "--";
                    FocusToggle.IsOn = _nc.FocusOnVoice;
                }, "Noise control read");

                await Guard(async () =>
                {
                    var eqState = await _hp.GetEqualizerAsync();
                    _tileEq = "EQ: " + EqPresets.NameOf(eqState.Preset);
                    ApplyEqToUi(eqState);
                }, "Equalizer read");

                await Guard(async () => { DseeToggle.IsOn = await _hp.GetDseeAsync(); }, "DSEE read");
                await Guard(async () => { SpeakToggle.IsOn = await _hp.GetSpeakToChatAsync(); }, "Speak-to-Chat read");

                await Guard(async () =>
                {
                    int apo = await _hp.GetAutoPowerOffAsync();
                    AutoPowerCombo.SelectedIndex = apo == 5 ? 1 : 0;
                }, "Auto power off read");

                await Guard(async () =>
                {
                    var codec = await _hp.GetCodecAsync();
                    if (!string.IsNullOrEmpty(codec)) CodecText.Text = "Codec: " + codec;
                }, "Codec read");

                await Guard(async () =>
                {
                    var fw = await _hp.GetFirmwareVersionAsync();
                    if (!string.IsNullOrEmpty(fw)) FirmwareText.Text = "Firmware: " + fw;
                }, "Firmware read");

                PushTile();
                Log("Settings refreshed from headphones");
            }
            finally { _loading = false; }
        }

        // Push an EQ state read from the headphones into the controls.
        // Callers hold _loading = true so the updates don't re-trigger sends.
        void ApplyEqToUi(EqualizerState eq)
        {
            EqPresetCombo.SelectedIndex = EqPresets.IndexOf(eq.Preset); // -1 for Manual/unknown
            ClearBassSlider.Value = eq.ClearBass;
            var sliders = BandSliders();
            for (int i = 0; i < 5; i++) sliders[i].Value = eq.Bands[i];
        }

        Slider[] BandSliders() { return new[] { Band0Slider, Band1Slider, Band2Slider, Band3Slider, Band4Slider }; }
        TextBlock[] BandValues() { return new[] { Band0Value, Band1Value, Band2Value, Band3Value, Band4Value }; }

        // ---------- noise control ----------

        async Task ApplyNoiseAsync()
        {
            if (_hp == null || _loading) return;
            var snapshot = new NoiseControlState
            {
                Mode = _nc.Mode,
                AmbientLevel = _nc.AmbientLevel,
                FocusOnVoice = FocusToggle.IsOn
            };
            await Guard(async () =>
            {
                await _hp.SetNoiseControlAsync(snapshot);
                _nc = snapshot;
                _tileNc = LiveTile.DescribeNc(snapshot);
                PushTile();
                Log("Noise control set: " + snapshot.Mode +
                    (snapshot.Mode == NoiseMode.Ambient ? " level " + snapshot.AmbientLevel : "") +
                    (snapshot.FocusOnVoice ? " + voice focus" : ""));
            }, "Noise control set");
        }

        async void NcBtn_Click(object sender, RoutedEventArgs e)
        {
            _nc.Mode = NoiseMode.NoiseCancelling;
            AmbientValueText.Text = "--";
            await ApplyNoiseAsync();
        }

        async void AmbientBtn_Click(object sender, RoutedEventArgs e)
        {
            _nc.Mode = NoiseMode.Ambient;
            _nc.AmbientLevel = (int)AmbientSlider.Value;
            AmbientValueText.Text = _nc.AmbientLevel.ToString();
            await ApplyNoiseAsync();
        }

        async void OffBtn_Click(object sender, RoutedEventArgs e)
        {
            _nc.Mode = NoiseMode.Off;
            AmbientValueText.Text = "--";
            await ApplyNoiseAsync();
        }

        void AmbientSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_loading) return;
            AmbientValueText.Text = ((int)e.NewValue).ToString();
            if (_ambientDebounce == null)
            {
                _ambientDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _ambientDebounce.Tick += async (s, a) =>
                {
                    _ambientDebounce.Stop();
                    if (_hp != null && _session.IsConnected)
                    {
                        _nc.Mode = NoiseMode.Ambient;
                        _nc.AmbientLevel = (int)AmbientSlider.Value;
                        await ApplyNoiseAsync();
                    }
                };
            }
            _ambientDebounce.Stop();
            _ambientDebounce.Start();
        }

        async void FocusToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading || _hp == null) return;
            if (_nc.Mode == NoiseMode.Off) _nc.Mode = NoiseMode.Ambient;
            await ApplyNoiseAsync();
        }

        // ---------- equalizer ----------

        async void EqPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _hp == null) return;
            int idx = EqPresetCombo.SelectedIndex;
            if (idx < 0 || idx >= EqPresets.Values.Length) return;
            int preset = EqPresets.Values[idx];
            await Guard(async () =>
            {
                await _hp.SetEqualizerPresetAsync(preset);
                Log("EQ preset set: " + EqPresets.Names[idx]);
            }, "EQ preset set");
            // Read the EQ back and move the band sliders to the preset's actual
            // values - without this the sliders stayed wherever they were.
            await Guard(async () =>
            {
                var eq = await _hp.GetEqualizerAsync();
                _loading = true;
                try { ApplyEqToUi(eq); }
                finally { _loading = false; }
                _tileEq = "EQ: " + EqPresets.NameOf(eq.Preset);
                PushTile();
            }, "EQ readback after preset");
        }

        DispatcherTimer _eqDebounce;

        void EqSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            // keep the little value labels beside each slider current
            ClearBassValue.Text = ((int)ClearBassSlider.Value).ToString();
            var sliders = BandSliders(); var labels = BandValues();
            for (int i = 0; i < 5; i++) labels[i].Text = ((int)sliders[i].Value).ToString();

            // A genuine user move (not a programmatic fill under _loading)
            // auto-saves as the custom (Manual) preset after a short pause,
            // so the bands never need the Apply button to take effect.
            if (_loading || _hp == null || !_session.IsConnected) return;
            if (_eqDebounce == null)
            {
                _eqDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _eqDebounce.Tick += async (s, a) =>
                {
                    _eqDebounce.Stop();
                    await ApplyCustomEqAsync();
                };
            }
            _eqDebounce.Stop();
            _eqDebounce.Start();
        }

        // Sending a custom EQ is what saves it: the headphones store it as
        // their Manual preset (0xA0). The combo drops to "no preset" (Manual).
        async Task ApplyCustomEqAsync()
        {
            if (_hp == null || !_session.IsConnected) return;
            var sliders = BandSliders();
            var bands = new int[5];
            for (int i = 0; i < 5; i++) bands[i] = (int)sliders[i].Value;
            int bass = (int)ClearBassSlider.Value;
            await Guard(async () =>
            {
                await _hp.SetEqualizerCustomAsync(bass, bands);
                EqPresetCombo.SelectedIndex = -1; // now Manual
                _tileEq = "EQ: " + EqPresets.NameOf(0xA0);
                PushTile();
                Log("Custom EQ saved as Manual preset (Clear Bass " + bass + ")");
            }, "Custom EQ set");
        }

        async void ApplyEqBtn_Click(object sender, RoutedEventArgs e)
        {
            await ApplyCustomEqAsync();
        }

        // ---------- sound toggles ----------

        async void DseeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading || _hp == null) return;
            bool on = DseeToggle.IsOn;
            await Guard(async () => { await _hp.SetDseeAsync(on); Log("DSEE " + (on ? "on" : "off")); }, "DSEE set");
        }

        async void SpeakToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading || _hp == null) return;
            bool on = SpeakToggle.IsOn;
            await Guard(async () => { await _hp.SetSpeakToChatAsync(on); Log("Speak-to-Chat " + (on ? "on" : "off")); }, "Speak-to-Chat set");
        }

        // ---------- power ----------

        async void AutoPowerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _hp == null) return;
            int index = AutoPowerCombo.SelectedIndex == 1 ? 5 : 0;
            await Guard(async () =>
            {
                await _hp.SetAutoPowerOffAsync(index);
                Log("Auto power off: " + (index == 5 ? "when taken off" : "do not turn off"));
            }, "Auto power off set");
        }

        async void PowerOffBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_hp == null) return;
            var dlg = new ContentDialog
            {
                Title = "Power off headphones?",
                Content = "The headphones will switch off and disconnect.",
                PrimaryButtonText = "Power off",
                SecondaryButtonText = "Cancel"
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            await Guard(async () =>
            {
                await _hp.PowerOffAsync();
                Log("Power off sent");
            }, "Power off");
        }
    }
}
