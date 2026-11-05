using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using SoundConnect.Sony;

namespace SoundConnect
{
    /// <summary>
    /// Keeps the live tile fresh while the app is closed: an in-process
    /// background task on a 15-minute TimeTrigger (the smallest interval
    /// Windows 10 Mobile allows) that briefly connects to the XM4, reads
    /// battery / noise control / EQ and rewrites the tile. If the phone
    /// says the XM4 is not connected, the tile reverts to the plain one.
    /// If the RFCOMM connect fails (the open app is holding the control
    /// channel), the existing tile is left untouched.
    /// </summary>
    public static class TileRefresh
    {
        public const string TaskName = "SoundConnectTileRefresh";

        public static async Task RegisterAsync()
        {
            try
            {
                foreach (var entry in BackgroundTaskRegistration.AllTasks)
                    if (entry.Value.Name == TaskName) return;
                var access = await BackgroundExecutionManager.RequestAccessAsync();
                if (access == BackgroundAccessStatus.DeniedBySystemPolicy ||
                    access == BackgroundAccessStatus.DeniedByUser) return;
                var builder = new BackgroundTaskBuilder();
                builder.Name = TaskName;
                builder.SetTrigger(new TimeTrigger(15, false));
                builder.Register();
            }
            catch { /* tile refresh is best-effort */ }
        }

        public static async Task RunAsync()
        {
            var work = RunCoreAsync();
            var done = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(20)));
            if (done == work) { try { await work; } catch { } }
        }

        static async Task RunCoreAsync()
        {
            DeviceInformation xm4 = null;
            var found = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelector());
            foreach (var d in found)
            {
                if ((d.Name ?? "").ToUpperInvariant().Contains("WH-1000XM4")) { xm4 = d; break; }
            }
            if (xm4 == null) { LiveTile.Clear(); return; }

            var device = await BluetoothDevice.FromIdAsync(xm4.Id);
            if (device == null) return;
            if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            {
                LiveTile.Clear();
                return;
            }

            var session = new SonySession();
            try
            {
                await session.ConnectAsync(device);
            }
            catch { return; } // foreground app likely holds the channel - keep its tile

            try
            {
                var hp = new SonyHeadphones(session);
                await hp.InitDeviceAsync();
                var batt = await hp.GetBatteryAsync();
                string ncText = "", eqText = "";
                try { ncText = LiveTile.DescribeNc(await hp.GetNoiseControlAsync()); } catch { }
                try { eqText = "EQ: " + EqPresets.NameOf((await hp.GetEqualizerAsync()).Preset); } catch { }
                LiveTile.Update(batt.Item1, batt.Item2, ncText, eqText);
            }
            catch { /* partial reads: leave the tile as it was */ }
            finally { session.Disconnect(); }
        }
    }
}
