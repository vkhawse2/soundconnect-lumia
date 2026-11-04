using System;
using System.Text;
using System.Threading.Tasks;

namespace SoundConnect.Sony
{
    public enum NoiseMode { Off, Ambient, NoiseCancelling }

    public sealed class NoiseControlState
    {
        public NoiseMode Mode;
        public int AmbientLevel;   // 1..20 when Mode == Ambient
        public bool FocusOnVoice;
    }

    public sealed class EqualizerState
    {
        public int Preset;         // raw preset byte (see EqPresets)
        public int ClearBass;      // -10..+10
        public int[] Bands = new int[5]; // -10..+10: 400Hz, 1kHz, 2.5kHz, 6.3kHz, 16kHz
    }

    public static class EqPresets
    {
        // Authoritative table from our xm4mini code (EqualizerPresets.h).
        // The byte values are NOT sequential: Bright=0x10 ... Speech=0x17, Manual=0xA0.
        public static readonly int[] Values = { 0x00, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17 };
        public static readonly string[] Names =
            { "Off", "Bright", "Excited", "Mellow", "Relaxed", "Vocal", "Treble Boost", "Bass Boost", "Speech" };

        public static string NameOf(int preset)
        {
            for (int i = 0; i < Values.Length; i++) if (Values[i] == preset) return Names[i];
            if (preset == 0xA0) return "Manual (custom)";
            return "Preset " + preset;
        }

        public static int IndexOf(int preset)
        {
            for (int i = 0; i < Values.Length; i++) if (Values[i] == preset) return i;
            return -1;
        }
    }

    /// <summary>
    /// Sony Protocol V1 device commands for the WH-1000XM4, ported byte-for-byte
    /// from our xm4mini code (ProtocolV1.cpp). Those layouts match the legacy
    /// SonyHeadphonesClient (years on XM3 hardware) and Gadgetbridge, and the
    /// battery / noise / EQ / metadata GETs plus Speak-to-Chat were replayed
    /// against a WH-1000XM4 on firmware 3.0.1. DSEE follows Gadgetbridge.
    /// CRITICAL: opcode 0x22 is POWER OFF on this generation - only PowerOff() emits it.
    /// </summary>
    public sealed class SonyHeadphones
    {
        const byte NcAsmInquired = 0x02;
        const byte EqInquired = 0x01;
        const byte SpeakToChatInquired = 0x05;
        const byte DseeInquired = 0x02;
        const byte AutoPowerOffInquired = 0x04;

        readonly SonySession _session;
        public SonyHeadphones(SonySession session) { _session = session; }

        static byte ClampEq(int v)
        {
            if (v < -10) v = -10;
            if (v > 10) v = 10;
            return (byte)(v + 10);
        }

        /// <summary>Best-effort first poll after connect (first reply is slow on the XM4).</summary>
        public async Task InitDeviceAsync()
        {
            try
            {
                await _session.SendAndAwaitResponseAsync(
                    new byte[] { 0x66, NcAsmInquired }, 0x67, NcAsmInquired, 2500);
            }
            catch { /* non-fatal */ }
        }

        // GET 10 00 -> RET 11 00 <level> <charging>
        public async Task<Tuple<int, bool>> GetBatteryAsync()
        {
            var resp = await _session.SendAndAwaitResponseAsync(new byte[] { 0x10, 0x00 }, 0x11, 0x00);
            if (resp.Length < 4) throw new InvalidOperationException("Short battery reply");
            return Tuple.Create((int)resp[2], resp[3] == 1);
        }

        // GET 66 02 -> RET 67 02 <effect> <ncType> <dualSingle> <asmType> <asmId> <asmLevel>
        public async Task<NoiseControlState> GetNoiseControlAsync()
        {
            var resp = await _session.SendAndAwaitResponseAsync(new byte[] { 0x66, NcAsmInquired }, 0x67, NcAsmInquired);
            if (resp.Length < 8) throw new InvalidOperationException("Short noise-control reply");
            var st = new NoiseControlState();
            bool on = resp[2] != 0x00;
            bool ambient = resp[4] == 0x00; // dualSingle: 0 = passthrough (ambient), 2 = NC
            st.FocusOnVoice = resp[6] == 1;
            if (!on) st.Mode = NoiseMode.Off;
            else if (ambient) { st.Mode = NoiseMode.Ambient; st.AmbientLevel = resp[7]; }
            else st.Mode = NoiseMode.NoiseCancelling;
            return st;
        }

        // SET 68 02 <effect> 01 <dualSingle> 01 <voice> <level>
        public async Task SetNoiseControlAsync(NoiseControlState st)
        {
            bool off = st.Mode == NoiseMode.Off;
            bool ambient = st.Mode == NoiseMode.Ambient;
            int level = ambient ? Math.Max(1, Math.Min(20, st.AmbientLevel)) : 0;
            var payload = new byte[]
            {
                0x68, NcAsmInquired,
                (byte)(off ? 0x00 : 0x11),       // effect: off / adjustment completion
                0x01,                            // level adjustment
                (byte)((ambient || off) ? 0x00 : 0x02),
                0x01,
                (byte)(st.FocusOnVoice ? 1 : 0),
                (byte)level
            };
            await _session.SendAsync(payload);
        }

        // GET 56 01 -> RET 57 01 <preset> 06 <bass+10> <b1..b5 +10>
        public async Task<EqualizerState> GetEqualizerAsync()
        {
            var resp = await _session.SendAndAwaitResponseAsync(new byte[] { 0x56, EqInquired }, 0x57, EqInquired);
            if (resp.Length < 10) throw new InvalidOperationException("Short equalizer reply");
            var st = new EqualizerState();
            st.Preset = resp[2];
            st.ClearBass = resp[4] - 10;
            for (int i = 0; i < 5; i++) st.Bands[i] = resp[5 + i] - 10;
            return st;
        }

        // SET preset: 58 01 <preset> 00
        public async Task SetEqualizerPresetAsync(int preset)
        {
            await _session.SendAsync(new byte[] { 0x58, EqInquired, (byte)preset, 0x00 });
        }

        // SET custom: 58 01 A0 06 <clearBass+10> <b1..b5 +10>
        public async Task SetEqualizerCustomAsync(int clearBass, int[] bands)
        {
            var payload = new byte[4 + 1 + 5];
            payload[0] = 0x58; payload[1] = EqInquired; payload[2] = 0xA0; payload[3] = 0x06;
            payload[4] = ClampEq(clearBass);
            for (int i = 0; i < 5; i++) payload[5 + i] = ClampEq(bands[i]);
            await _session.SendAsync(payload);
        }

        // GET e6 02 -> RET e7 02 00 <onOff> ; SET e8 02 00 <onOff>   (per Gadgetbridge)
        public async Task<bool> GetDseeAsync()
        {
            var resp = await _session.SendAndAwaitResponseAsync(new byte[] { 0xE6, DseeInquired }, 0xE7, DseeInquired);
            if (resp.Length < 4) throw new InvalidOperationException("Short DSEE reply");
            return resp[3] != 0;
        }

        public async Task SetDseeAsync(bool enabled)
        {
            await _session.SendAsync(new byte[] { 0xE8, DseeInquired, 0x00, (byte)(enabled ? 1 : 0) });
        }

        // GET f6 05 -> RET f7 05 <kind> <onOff>; kind 0x02 = talking session active (still "on")
        public async Task<bool> GetSpeakToChatAsync()
        {
            var resp = await _session.SendAndAwaitResponseAsync(
                new byte[] { 0xF6, SpeakToChatInquired }, 0xF7, SpeakToChatInquired);
            if (resp.Length < 4) throw new InvalidOperationException("Short Speak-to-Chat reply");
            return resp[2] == 0x02 || resp[3] != 0;
        }

        public async Task SetSpeakToChatAsync(bool enabled)
        {
            if (enabled)
            {
                // Config first (auto sensitivity, no voice focus, standard ~30s timeout),
                // exactly like the official app; enabling alone would arm a session
                // that never closes.
                await _session.SendAsync(new byte[] { 0xFC, SpeakToChatInquired, 0x00, 0x00, 0x00, 0x01 });
            }
            await _session.SendAsync(new byte[] { 0xF8, SpeakToChatInquired, 0x01, (byte)(enabled ? 1 : 0) });
        }

        // GET f6 04 -> RET f7 04 01 <code0> <code1>
        // The XM4 only honors two values: 0x11 0x00 = "Do not turn off",
        // 0x10 0x00 = "When taken off" (documented capture, see xm4mini notes).
        public async Task<int> GetAutoPowerOffAsync()
        {
            var resp = await _session.SendAndAwaitResponseAsync(
                new byte[] { 0xF6, AutoPowerOffInquired }, 0xF7, AutoPowerOffInquired);
            if (resp.Length < 5) throw new InvalidOperationException("Short auto-power-off reply");
            if (resp[3] == 0x10 && resp[4] == 0x00) return 5; // when taken off
            return 0; // do not turn off (0x11 0x00 or anything else)
        }

        public async Task SetAutoPowerOffAsync(int index)
        {
            byte first = (byte)(index == 5 ? 0x10 : 0x11);
            await _session.SendAsync(new byte[] { 0xF8, AutoPowerOffInquired, 0x01, first, 0x00 });
        }

        // GET 04 02 -> RET 05 02 <len> <ascii version>
        public async Task<string> GetFirmwareVersionAsync()
        {
            var resp = await _session.SendAndAwaitResponseAsync(new byte[] { 0x04, 0x02 }, 0x05, -1);
            if (resp.Length > 3) return Encoding.ASCII.GetString(resp, 3, resp.Length - 3);
            return "";
        }

        // GET 18 00 -> RET 19 00 <codec>
        public async Task<string> GetCodecAsync()
        {
            var resp = await _session.SendAndAwaitResponseAsync(new byte[] { 0x18, 0x00 }, 0x19, -1);
            if (resp.Length < 3) return "";
            switch (resp[2])
            {
                case 0x01: return "SBC";
                case 0x02: return "AAC";
                case 0x10: return "LDAC";
                case 0x20: return "aptX";
                case 0x21: return "aptX HD";
                default: return "";
            }
        }

        // POWER OFF: 22 00 01 (only this method may emit opcode 0x22)
        public async Task PowerOffAsync()
        {
            try { await _session.SendAsync(new byte[] { 0x22, 0x00, 0x01 }, 800); }
            catch { /* the headset may drop the link before ACKing - that is success */ }
        }
    }
}
