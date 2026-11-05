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
            await RefreshDevicesAsync();
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
                _session.Disconnect();
                _hp = null;
                StatusText.Text = "Not connected";
                ConnectBtn.Content = "Connect";
                SetControlsVisible(false);
                Log("Disconnected");
                return;
            }

            int idx = DeviceCombo.SelectedIndex;
            if (idx < 0 || idx >= _devices.Count) { Log("Pick a device first"); return; }

            ConnectBtn.IsEnabled = false;
            StatusText.Text = "Connecting...";
            try
            {
                var device = await BluetoothDevice.FromIdAsync(_devices[idx].Id);
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
            finally { ConnectBtn.IsEnabled = true; }
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
                }, "Battery read");

                await Guard(async () =>
                {
                    _nc = await _hp.GetNoiseControlAsync();
                    AmbientSlider.Value = _nc.AmbientLevel >= 1 ? _nc.AmbientLevel : 10;
                    AmbientValueText.Text = _nc.Mode == NoiseMode.Ambient ? _nc.AmbientLevel.ToString() : "--";
                    FocusToggle.IsOn = _nc.FocusOnVoice;
                }, "Noise control read");

                await Guard(async () =>
                {
                    var eq = await _hp.GetEqualizerAsync();
                    int idx = EqPresets.IndexOf(eq.Preset);
                    EqPresetCombo.SelectedIndex = idx; // -1 for Manual/unknown
                    ClearBassSlider.Value = eq.ClearBass;
                    BandSliders()[0].Value = eq.Bands[0];
                    BandSliders()[1].Value = eq.Bands[1];
                    BandSliders()[2].Value = eq.Bands[2];
                    BandSliders()[3].Value = eq.Bands[3];
                    BandSliders()[4].Value = eq.Bands[4];
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

                Log("Settings refreshed from headphones");
            }
            finally { _loading = false; }
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
        }

        void EqSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            // keep the little value labels beside each slider current
            ClearBassValue.Text = ((int)ClearBassSlider.Value).ToString();
            var sliders = BandSliders(); var labels = BandValues();
            for (int i = 0; i < 5; i++) labels[i].Text = ((int)sliders[i].Value).ToString();
        }

        async void ApplyEqBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_hp == null) return;
            var sliders = BandSliders();
            var bands = new int[5];
            for (int i = 0; i < 5; i++) bands[i] = (int)sliders[i].Value;
            int bass = (int)ClearBassSlider.Value;
            await Guard(async () =>
            {
                await _hp.SetEqualizerCustomAsync(bass, bands);
                EqPresetCombo.SelectedIndex = -1; // now Manual
                Log("Custom EQ applied (Clear Bass " + bass + ")");
            }, "Custom EQ set");
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
