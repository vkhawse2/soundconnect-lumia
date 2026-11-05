using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace SoundConnect.Sony
{
    /// <summary>
    /// RFCOMM transport + Sony session rules, ported from our xm4mini code
    /// (SonyProtocolSession.cpp): 1-bit alternating sequence numbers, ACK
    /// frames, duplicate suppression, request/response matching by opcode.
    ///
    /// Uses only APIs available on Windows 10 Mobile build 14393:
    /// BluetoothDevice.GetRfcommServicesAsync + StreamSocket.
    /// </summary>
    public sealed class SonySession
    {
        // Sony RFCOMM control services, exactly as in our xm4mini code
        // (vendor/Client/Constants.h): try V1 first (WH-1000XM4 generation),
        // then fall back to V2.
        public static readonly Guid SonyV1ServiceUuid = new Guid("96CC203E-5068-46ad-B32D-E316F5E069BA");
        public static readonly Guid SonyV2ServiceUuid = new Guid("956C7B26-D49A-4BA8-B03F-B17D393CB6E2");

        StreamSocket _socket;
        DataWriter _writer;
        DataReader _reader;
        CancellationTokenSource _cts;

        readonly List<byte> _buffer = new List<byte>();
        readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        int _sequence;                 // mirrors the C++ session's counter
        byte _lastSeenSeq;
        bool _hasLastSeenSeq;

        TaskCompletionSource<bool> _ackWaiter;
        TaskCompletionSource<byte[]> _responseWaiter;
        byte _expectedOpcode;
        int _expectedSubtype;

        public bool IsConnected { get; private set; }

        /// <summary>Unsolicited DataMdr payloads from the headphones.</summary>
        public event Action<byte[]> Notification;

        public event Action Disconnected;

        public async Task ConnectAsync(BluetoothDevice device)
        {
            Disconnect();

            var servicesResult = await device.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
            if (servicesResult.Error != BluetoothError.Success || servicesResult.Services.Count == 0)
            {
                servicesResult = await device.GetRfcommServicesAsync(BluetoothCacheMode.Cached);
            }
            if (servicesResult.Error != BluetoothError.Success)
                throw new InvalidOperationException("Bluetooth error while looking for the Sony service: " + servicesResult.Error);

            var foundUuids = new List<string>();
            RfcommDeviceService sonyService = null;
            RfcommDeviceService v2Service = null;
            foreach (var svc in servicesResult.Services)
            {
                if (svc.ServiceId == null) continue;
                foundUuids.Add(svc.ServiceId.Uuid.ToString());
                if (svc.ServiceId.Uuid == SonyV1ServiceUuid) sonyService = svc;
                else if (svc.ServiceId.Uuid == SonyV2ServiceUuid) v2Service = svc;
            }
            if (sonyService == null) sonyService = v2Service;
            if (sonyService == null)
                throw new InvalidOperationException("Sony control service not found on this device. " +
                    "Device offers: " + (foundUuids.Count > 0 ? string.Join(", ", foundUuids) : "(none)") + ". " +
                    "Make sure the headphones are powered on and paired, and no other app is holding the connection.");

            _socket = new StreamSocket();
            await _socket.ConnectAsync(sonyService.ConnectionHostName, sonyService.ConnectionServiceName,
                SocketProtectionLevel.PlainSocket);

            _writer = new DataWriter(_socket.OutputStream);
            _reader = new DataReader(_socket.InputStream);
            _reader.InputStreamOptions = InputStreamOptions.Partial;

            _cts = new CancellationTokenSource();
            IsConnected = true;
            var _ = ReadLoopAsync(_cts.Token);
        }

        public void Disconnect()
        {
            try { _cts?.Cancel(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _socket?.Dispose(); } catch { }
            _reader = null; _writer = null; _socket = null;
            IsConnected = false;
            _buffer.Clear();
            _hasLastSeenSeq = false;
            _sequence = 0;
        }

        async Task ReadLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    uint read = await _reader.LoadAsync(512).AsTask(token);
                    if (read == 0) break;
                    uint avail = _reader.UnconsumedBufferLength;
                    if (avail == 0) continue;
                    var bytes = new byte[avail];
                    _reader.ReadBytes(bytes);
                    FeedBytes(bytes);
                }
            }
            catch { /* fall through to disconnect */ }

            if (IsConnected)
            {
                IsConnected = false;
                Disconnected?.Invoke();
            }
        }

        void FeedBytes(byte[] bytes)
        {
            _buffer.AddRange(bytes);
            while (true)
            {
                int start = _buffer.IndexOf(SonyFrame.StartMarker);
                if (start < 0) { _buffer.Clear(); return; }
                if (start > 0) _buffer.RemoveRange(0, start);

                int end = -1;
                for (int i = 1; i < _buffer.Count; i++)
                {
                    // Body bytes equal to Start/End markers are always escaped,
                    // so the first raw EndMarker terminates the frame.
                    if (_buffer[i] == SonyFrame.EndMarker) { end = i; break; }
                    if (_buffer[i] == SonyFrame.StartMarker) { start = i; break; }
                }
                if (end < 0)
                {
                    if (start > 0) { _buffer.RemoveRange(0, start); continue; }
                    if (_buffer.Count > 2048) _buffer.RemoveAt(0);
                    return; // need more bytes
                }

                var body = _buffer.GetRange(1, end - 1);
                _buffer.RemoveRange(0, end + 1);
                var frame = SonyFrame.DecodeBody(body);
                if (frame != null) HandleFrame(frame);
            }
        }

        void HandleFrame(SonyFrame.Frame frame)
        {
            if (frame.Type == SonyFrame.TypeAck)
            {
                if (frame.Sequence <= 1) _sequence = frame.Sequence;
                _ackWaiter?.TrySetResult(true);
                return;
            }

            if (frame.Type == SonyFrame.TypeDataMdr)
            {
                // ACK the device's frame with the toggled sequence bit.
                var ack = SonyFrame.Encode(SonyFrame.TypeAck, (byte)(1 - (frame.Sequence & 1)), new byte[0]);
                var __ = WriteRawAsync(ack);

                lock (this)
                {
                    if (_hasLastSeenSeq && _lastSeenSeq == frame.Sequence) return; // duplicate
                    _lastSeenSeq = frame.Sequence;
                    _hasLastSeenSeq = true;
                }

                var payload = frame.Payload;
                var waiter = _responseWaiter;
                if (waiter != null && payload.Length >= 1 && payload[0] == _expectedOpcode &&
                    (_expectedSubtype < 0 || (payload.Length >= 2 && payload[1] == (byte)_expectedSubtype)))
                {
                    _responseWaiter = null;
                    waiter.TrySetResult(payload);
                    return;
                }

                Notification?.Invoke(payload);
            }
        }

        byte NextSequence()
        {
            int cur = _sequence;
            if (cur > 1 && cur < 254) { cur &= 1; _sequence = cur; }
            byte result = (byte)cur;
            _sequence = cur + 1;
            return result;
        }

        async Task WriteRawAsync(byte[] bytes)
        {
            await _writeLock.WaitAsync();
            try
            {
                _writer.WriteBytes(bytes);
                await _writer.StoreAsync();
            }
            finally { _writeLock.Release(); }
        }

        /// <summary>Fire a SET command and wait for the device's ACK frame.</summary>
        public async Task SendAsync(byte[] payload, int timeoutMs = 1500)
        {
            if (!IsConnected) throw new InvalidOperationException("Not connected");
            await _sendLock.WaitAsync();
            try
            {
                _ackWaiter = new TaskCompletionSource<bool>();
                byte seq = NextSequence();
                await WriteRawAsync(SonyFrame.Encode(SonyFrame.TypeDataMdr, seq, payload));
                var done = await Task.WhenAny(_ackWaiter.Task, Task.Delay(timeoutMs));
                if (done != _ackWaiter.Task) throw new TimeoutException("Headphones did not ACK the command");
            }
            finally { _sendLock.Release(); }
        }

        /// <summary>Send a GET command and wait for its RET payload.</summary>
        public async Task<byte[]> SendAndAwaitResponseAsync(byte[] payload, byte retOpcode, int retSubtype,
            int timeoutMs = 2000)
        {
            if (!IsConnected) throw new InvalidOperationException("Not connected");
            await _sendLock.WaitAsync();
            try
            {
                _expectedOpcode = retOpcode;
                _expectedSubtype = retSubtype;
                _responseWaiter = new TaskCompletionSource<byte[]>();
                byte seq = NextSequence();
                await WriteRawAsync(SonyFrame.Encode(SonyFrame.TypeDataMdr, seq, payload));
                var done = await Task.WhenAny(_responseWaiter.Task, Task.Delay(timeoutMs));
                if (done != _responseWaiter.Task)
                {
                    _responseWaiter = null;
                    throw new TimeoutException("Headphones did not answer (opcode 0x" + retOpcode.ToString("X2") + ")");
                }
                return await _responseWaiter.Task;
            }
            finally { _sendLock.Release(); }
        }
    }
}
