using System;
using System.Collections.Generic;

namespace SoundConnect.Sony
{
    /// <summary>
    /// Sony MDR frame codec, ported 1:1 from our xm4mini Sony V1 protocol code
    /// (vendor/libs/sony-protocol FrameCodec.cpp, itself from SonyHeadphonesClient).
    /// Wire format: 0x3E [escaped body] 0x3C
    /// Body: type(1) seq(1) payloadLength(4, big-endian) payload checksum(1)
    /// Checksum: simple byte sum of everything before it.
    /// Escaping: 0x3C -> 0x3D 0x2C, 0x3D -> 0x3D 0x2D, 0x3E -> 0x3D 0x2E.
    /// </summary>
    public static class SonyFrame
    {
        public const byte StartMarker = 0x3E;
        public const byte EndMarker = 0x3C;
        public const byte EscapeMarker = 0x3D;

        public const byte TypeData = 0;
        public const byte TypeAck = 1;
        public const byte TypeDataMdr = 12;

        public sealed class Frame
        {
            public byte Type;
            public byte Sequence;
            public byte[] Payload = new byte[0];
        }

        public static byte[] Encode(byte type, byte sequence, byte[] payload)
        {
            var body = new List<byte>();
            body.Add(type);
            body.Add(sequence);
            body.Add((byte)((payload.Length >> 24) & 0xFF));
            body.Add((byte)((payload.Length >> 16) & 0xFF));
            body.Add((byte)((payload.Length >> 8) & 0xFF));
            body.Add((byte)(payload.Length & 0xFF));
            body.AddRange(payload);

            byte checksum = 0;
            foreach (var b in body) checksum += b;
            body.Add(checksum);

            var wire = new List<byte>();
            wire.Add(StartMarker);
            foreach (var b in body)
            {
                switch (b)
                {
                    case EndMarker: wire.Add(EscapeMarker); wire.Add(0x2C); break;
                    case EscapeMarker: wire.Add(EscapeMarker); wire.Add(0x2D); break;
                    case StartMarker: wire.Add(EscapeMarker); wire.Add(0x2E); break;
                    default: wire.Add(b); break;
                }
            }
            wire.Add(EndMarker);
            return wire.ToArray();
        }

        /// <summary>Decode the unescaped frame content (without 0x3E/0x3C markers).</summary>
        public static Frame DecodeBody(IList<byte> wireBody)
        {
            var data = new List<byte>();
            for (int i = 0; i < wireBody.Count; i++)
            {
                byte cur = wireBody[i];
                if (cur == EscapeMarker)
                {
                    if (i + 1 >= wireBody.Count) return null;
                    i++;
                    switch (wireBody[i])
                    {
                        case 0x2C: data.Add(EndMarker); break;
                        case 0x2D: data.Add(EscapeMarker); break;
                        case 0x2E: data.Add(StartMarker); break;
                        default: return null;
                    }
                }
                else
                {
                    data.Add(cur);
                }
            }

            if (data.Count < 7) return null;

            int payloadLen = (data[2] << 24) | (data[3] << 16) | (data[4] << 8) | data[5];
            if (data.Count < 6 + payloadLen + 1) return null;

            byte sum = 0;
            for (int i = 0; i < 6 + payloadLen; i++) sum += data[i];
            if (sum != data[6 + payloadLen]) return null; // bad checksum

            var payload = new byte[payloadLen];
            for (int i = 0; i < payloadLen; i++) payload[i] = data[6 + i];

            return new Frame { Type = data[0], Sequence = data[1], Payload = payload };
        }
    }
}
