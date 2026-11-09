using System.Text;
using System.Threading.Channels;

namespace CANguru.Controllers
{
    // Result of a CV operation
    public sealed record CvResult(bool Ok, int Value, string Message);

    // Reads and writes CVs of locomotive decoders through the track box (Märklin CAN
    // commands Read Config 0x0E / Write Config 0x10), as the CANguru-Server 3.02 did:
    //  - CVs: locomotive ID 0xC000 = the locomotive on the programming track (only there
    //    the decoder can answer to the track box)
    //  - name of an mfx locomotive: locomotive ID 0x4000 + SID, mfx block CV 3, index 1..16
    // Track power is switched on for the operation if needed and restored afterwards.
    //
    // Frame layout (13 bytes): [prio][cmd][hash hi][hash lo][dlc][D0..D7]
    public sealed class CvProgrammer
    {
        private const byte ReadConfig = 0x0E, ReadConfigR = 0x0F, WriteConfig = 0x10, WriteConfigR = 0x11;
        private const int MaxNameLength = 16;
        private const int CvTimeoutMs = 15000;   // reading on the programming track can take a while
        private const int NameFrameTimeoutMs = 3000;

        private readonly byte hashHigh, hashLow;
        private readonly Action<byte[]> sendFrame;     // to the track box (14 bytes incl. source 'G')
        private readonly Func<bool> trackOn;
        private readonly Action<bool> switchTrack;

        private Func<byte[], bool>? filter;          // answers the running operation waits for
        private Channel<byte[]>? inbox;

        public CvProgrammer(byte hashHigh, byte hashLow, Action<byte[]> sendFrame, Func<bool> trackOn, Action<bool> switchTrack)
        {
            this.hashHigh = hashHigh;
            this.hashLow = hashLow;
            this.sendFrame = sendFrame;
            this.trackOn = trackOn;
            this.switchTrack = switchTrack;
        }

        // an operation is running (the mfx registration must then ignore config answers)
        public bool Busy => filter != null;

        // Every CAN frame from the bridge is passed here (in order)
        public void OnCanFrame(byte[] frame)
        {
            var f = filter;
            if (f != null && frame.Length >= 13 && f(frame))
                inbox?.Writer.TryWrite((byte[])frame.Clone());
        }

        // ------------------------------------------------------------
        // CVs (locomotive on the programming track)
        // ------------------------------------------------------------

        public Task<CvResult> ReadCvAsync(int cv) => RunAsync(async () =>
        {
            // 0x0E [7] 00 00 C0 00 | CV hi CV lo | count 1
            var frame = Frame(ReadConfig, 7, 0x00, 0x00, 0xC0, 0x00, (byte)(cv >> 8), (byte)cv, 0x01);
            Expect(f => f[1] == ReadConfigR && f[7] == 0xC0 && f[9] == (byte)(cv >> 8) && f[10] == (byte)cv);
            sendFrame(frame);
            var answer = await NextAsync(CvTimeoutMs);
            if (answer == null)
                return new CvResult(false, 0, "Keine Antwort der Gleisbox.");
            // 7 bytes: value in D6; shorter answer = not readable
            return answer[4] == 7
                ? new CvResult(true, answer[11], "OK")
                : new CvResult(false, 0, "Fehler: CV nicht lesbar (Lok auf dem Programmiergleis?).");
        });

        public Task<CvResult> WriteCvAsync(int cv, byte value) => RunAsync(async () =>
        {
            // 0x10 [8] 00 00 C0 00 | CV hi CV lo | value | control 00
            var frame = Frame(WriteConfig, 8, 0x00, 0x00, 0xC0, 0x00, (byte)(cv >> 8), (byte)cv, value, 0x00);
            Expect(f => f[1] == WriteConfigR && f[7] == 0xC0 && f[9] == (byte)(cv >> 8) && f[10] == (byte)cv);
            sendFrame(frame);
            var answer = await NextAsync(CvTimeoutMs);
            if (answer == null)
                return new CvResult(false, 0, "Keine Antwort der Gleisbox.");
            // control byte 0xC0 = written and verified
            return answer[12] == 0xC0
                ? new CvResult(true, answer[11], "OK")
                : new CvResult(false, 0, "Fehler: CV nicht geschrieben (Lok auf dem Programmiergleis?).");
        });

        // ------------------------------------------------------------
        // Name of an mfx locomotive (mfx block CV 3, index 1..16, one character each)
        // ------------------------------------------------------------

        public Task<(bool Ok, string Name, string Message)> ReadMfxNameAsync(byte sid) => RunAsync(async () =>
        {
            // 0x0E [7] 00 00 40 SID | 04 03 | count 16 -> 16 answers with one character each
            var frame = Frame(ReadConfig, 7, 0x00, 0x00, 0x40, sid, 0x04, 0x03, MaxNameLength);
            Expect(f => f[1] == ReadConfigR && f[7] == 0x40 && f[8] == sid);
            sendFrame(frame);
            var name = new StringBuilder();
            for (int i = 0; i < MaxNameLength; i++)
            {
                var answer = await NextAsync(NameFrameTimeoutMs);
                if (answer == null)
                    return (false, name.ToString(), i == 0 ? "Keine Antwort der Lok." : "Name unvollständig gelesen.");
                if (answer[11] != 0)
                    name.Append((char)answer[11]);
            }
            return (true, name.ToString().TrimEnd(), "OK");
        });

        public Task<(bool Ok, string Message)> WriteMfxNameAsync(byte sid, string name) => RunAsync(async () =>
        {
            // 0x10 [8] 00 00 40 SID | index CV | character | 80, index 1..16 (D4 = index * 4)
            Expect(f => f[1] == WriteConfigR && f[7] == 0x40 && f[8] == sid);
            for (int i = 0; i < MaxNameLength; i++)
            {
                byte c = i < name.Length ? (byte)name[i] : (byte)0;
                sendFrame(Frame(WriteConfig, 8, 0x00, 0x00, 0x40, sid, (byte)(4 * (i + 1)), 0x03, c, 0x80));
                // the next character only after the answer (as in version 3.02)
                if (await NextAsync(NameFrameTimeoutMs) == null)
                    return (false, $"Keine Antwort der Lok nach Zeichen {i + 1}.");
            }
            return (true, "OK");
        });

        // ------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------

        private byte[] Frame(byte cmd, byte dlc, params byte[] data)
        {
            var f = new byte[14];
            f[1] = cmd;
            f[2] = hashHigh;
            f[3] = hashLow;
            f[4] = dlc;
            Array.Copy(data, 0, f, 5, Math.Min(data.Length, 8));
            f[13] = (byte)'G'; // source shown in the CAN list
            return f;
        }

        private void Expect(Func<byte[], bool> match)
        {
            inbox = Channel.CreateUnbounded<byte[]>();
            filter = match;
        }

        private async Task<byte[]?> NextAsync(int timeoutMs)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                return await inbox!.Reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        // One operation at a time; track power on for the operation, then restored
        private async Task<T> RunAsync<T>(Func<Task<T>> operation)
        {
            if (Busy)
                throw new InvalidOperationException("Es läuft bereits eine Programmierung.");
            bool wasOn = trackOn();
            try
            {
                if (!wasOn)
                {
                    switchTrack(true);
                    await Task.Delay(500); // track box needs a moment after GO
                }
                return await operation();
            }
            finally
            {
                filter = null;
                inbox = null;
                if (!wasOn)
                    switchTrack(false);
            }
        }
    }
}
