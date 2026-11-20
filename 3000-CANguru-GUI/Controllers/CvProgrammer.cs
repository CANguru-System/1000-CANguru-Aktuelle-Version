using System.Text;
using System.Threading.Channels;

namespace CANguru.Controllers
{
    // Result of a CV operation
    public sealed record CvResult(bool Ok, int Value, string Message);

    // A function of an mfx locomotive: number (F0, F1 ...), symbol (bit 0x80 = momentary,
    // i.e. only active while the key is pressed) and the outputs it switches per direction
    // Symbol: kind of function (bit 0x80 = momentary). Detail: exact symbol of mfx+ decoders
    // (one CV per function, index 2), 0 for the older layout.
    public sealed record MfxFunction(int Nr, int Symbol, int OutputsForward, int OutputsBackward, int Detail = 0)
    {
        public bool Momentary => (Symbol & 0x80) != 0;
        public override string ToString() =>
            $"F{Nr} ({CANguru.Models.MfxSymbols.Name(Symbol, Detail) ?? $"Symbol {Symbol & 0x7F}"}" +
            $"{(Detail != 0 ? $"/{Detail}" : "")}{(Momentary ? ", Taster" : "")})";
    }

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
        // Address of a Motorola locomotive (decoders that cannot be read). As the MS2 does
        // (sniffer recordings 20.11.2026, BR 24):
        //  - detect: discovery with protocol 0x21 to the track box; after about 6 s it answers
        //    with DLC 5: 00 00 | address hi lo | 0x21 (e.g. 00 00 00 18 21 = address 24)
        //  - write: Write Config to locomotive ID 0x0050 (Motorola address 80, the programming
        //    address of these decoders), CV 1 = new address, control 00; the track box confirms
        //    with control 0xC0 after about 5 s
        // ------------------------------------------------------------

        private const byte Discovery = 0x02, DiscoveryR = 0x03, MmDiscoveryProtocol = 0x21;
        private const int MmDetectTimeoutMs = 20000;

        public Task<CvResult> DetectMmAddressAsync() => RunAsync(async () =>
        {
            Expect(f => f[1] == DiscoveryR && f[4] == 5 && f[9] == MmDiscoveryProtocol);
            sendFrame(Frame(Discovery, 1, MmDiscoveryProtocol));
            var answer = await NextAsync(MmDetectTimeoutMs);
            if (answer == null)
                return new CvResult(false, 0, "Keine Adresse gefunden (Lok allein auf dem Programmiergleis?).");
            int address = (answer[7] << 8) | answer[8];
            return new CvResult(true, address, "OK");
        });

        public Task<CvResult> WriteMmAddressAsync(byte address) => RunAsync(async () =>
        {
            Expect(f => f[1] == WriteConfigR && f[7] == 0x00 && f[8] == 0x50 && f[10] == 0x01);
            sendFrame(Frame(WriteConfig, 8, 0x00, 0x00, 0x00, 0x50, 0x00, 0x01, address, 0x00));
            var answer = await NextAsync(CvTimeoutMs);
            if (answer == null)
                return new CvResult(false, 0, "Keine Antwort der Gleisbox.");
            return answer[12] == 0xC0
                ? new CvResult(true, answer[11], "OK")
                : new CvResult(false, 0, "Fehler: Adresse nicht geschrieben (Lok auf dem Programmiergleis?).");
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
        // Functions of an mfx locomotive.
        // The mfx configuration consists of blocks. A block starts with a header CV whose
        // index 0 is 0x01: [01][block number][..][..][number of entries][CVs per entry];
        // blocks follow each other, starting at the next multiple of 4 (so the search
        // tries the following CVs). Block 4 holds the functions F0, F1 ..., 3 CVs each:
        //   1st CV index 1 = symbol (bit 0x80 = momentary)
        //   2nd / 3rd CV index 1 = outputs forward / backward (0 = function not used)
        // Found by reading three locomotives (DB 640, BR 491, BR 260), November 2026.
        //
        // The V 200 052 (mfx+) also answers in this layout (20 entries, 3 CVs each) when read
        // by the GUI (20.11.2026). The MS2, however, first writes CV 0 = 2 (control 0x09) and
        // then reads ONE CV per function (CV 65 ... 96); purpose of that write unknown, the
        // GUI does not copy it. Layout with one CV per function:
        //   index 1 = kind of function (bit 0x80 = momentary), 0 = function not used
        //   index 2 = the exact symbol the MS2 shows
        // Both layouts are told apart by the number of CVs per entry in the block header.
        // ------------------------------------------------------------

        private const int FunctionBlock = 4, MaxFunctions = 32;

        public Task<(bool Ok, List<MfxFunction> Functions, string Message)> ReadMfxFunctionsAsync(byte sid) => RunAsync(async () =>
        {
            var none = new List<MfxFunction>();

            // As the MS2 does at every registration: write CV 0 = 2 (control 0x09) first; the
            // track box confirms with 0xC0. Without it the V 200 052 shows only F0 ... F15
            // (classic mfx), with it also F16 and up (MS2 recordings, 19.11.2026). The MS2
            // sends it to older decoders (BR 260, BR 491) too; it does not stay in the decoder.
            Expect(f => f[1] == WriteConfigR && f[7] == 0x40 && f[8] == sid);
            sendFrame(Frame(WriteConfig, 8, 0x00, 0x00, 0x40, sid, 0x00, 0x00, 0x02, 0x09));
            await NextAsync(2000);   // no answer: read anyway, in the classic layout

            Expect(f => f[1] == ReadConfigR && f[7] == 0x40 && f[8] == sid);

            // find the function block
            int cv = 0, start = -1, count = 0, size = 0, skipped = 0;
            for (int blocks = 0; blocks < 16 && cv < 1024; )
            {
                var h = await QueryMfxAsync(sid, cv, 0, 6);
                if (h == null)
                    return (false, none, cv == 0 ? "Keine Antwort der Lok (auf dem Programmiergleis?)." : $"Keine Antwort bei CV {cv}.");
                if (h.GetValueOrDefault(0) != 0x01)
                {
                    // no header here: try the next CV. Gaps can be long: the V 200 has
                    // block 3 at CV 20 (4 entries) and block 4 only at CV 64.
                    if (++skipped > 128)
                        return (false, none, "Funktionsblock nicht gefunden.");
                    cv++;
                    continue;
                }
                skipped = 0;
                blocks++;
                int block = h.GetValueOrDefault(1), n = h.GetValueOrDefault(4), s = h.GetValueOrDefault(5);
                if (block == FunctionBlock)
                {
                    start = cv + 1;
                    count = Math.Min(n, MaxFunctions);
                    size = s;
                    break;
                }
                cv += 1 + n * s;
            }
            if (start < 0)
                return (false, none, "Funktionsblock nicht gefunden.");

            var result = new List<MfxFunction>();
            if (size < 3)
            {
                // mfx+: one CV per function, index 1 = kind (0 = unused), index 2 = symbol
                int stride = Math.Max(size, 1);
                if (count == 0)
                    count = MaxFunctions;
                for (int nr = 0; nr < count; nr++)
                {
                    var v = await QueryMfxAsync(sid, start + nr * stride, 1, 2);
                    if (v == null)
                        return (false, result, $"Keine Antwort bei F{nr}.");
                    int kind = v.GetValueOrDefault(1), symbol = v.GetValueOrDefault(2);
                    if (kind != 0)
                        result.Add(new MfxFunction(nr, kind, 0, 0, symbol));
                }
                return (true, result, "OK");
            }

            // read the symbol of each function; symbol 0 = function not used. This is what the
            // MS2 does (BR 491: it reads only every third CV). Sound functions have no outputs,
            // so the outputs must not decide (V 200 052: only 3 of 18 functions had outputs).
            // mfx+ decoders also give the exact symbol as index 2; older ones do not answer it.
            int indices = 2;
            for (int nr = 0; nr < count; nr++)
            {
                int baseCv = start + nr * size;
                var sym = await QueryMfxAsync(sid, baseCv, 1, indices);
                if (sym == null)
                    return (false, result, $"Keine Antwort bei F{nr}.");
                if (nr == 0 && !sym.ContainsKey(2))
                    indices = 1;
                int symbol = sym.GetValueOrDefault(1);
                if (symbol != 0)
                    result.Add(new MfxFunction(nr, symbol, 0, 0, sym.GetValueOrDefault(2)));
            }
            return (true, result, "OK");
        });

        // Reads `count` values of one CV starting at startIndex; index -> value.
        // null = no answer at all. Answers for other CVs (late ones) are skipped.
        private async Task<Dictionary<int, int>?> QueryMfxAsync(byte sid, int cv, int startIndex, int count)
        {
            int ci = (startIndex << 10) | cv;
            sendFrame(Frame(ReadConfig, 7, 0x00, 0x00, 0x40, sid, (byte)(ci >> 8), (byte)ci, (byte)count));
            var values = new Dictionary<int, int>();
            while (values.Count < count)
            {
                var a = await NextAsync(values.Count == 0 ? 2000 : 1000);
                if (a == null)
                    break;
                int answer = (a[9] << 8) | a[10];
                if ((answer & 0x3FF) != cv)
                    continue;
                values[answer >> 10] = a[4] == 7 ? a[11] : 0;
            }
            return values.Count == 0 ? null : values;
        }

        // ------------------------------------------------------------
        // Exploration of the mfx configuration (for finding out which properties a
        // decoder describes). Reads CV cvFrom..cvTo, each from startIndex with `count`
        // values. Result: CV -> index -> value (-1 = the decoder answered without a value).
        // Addressing as for the name: D4..D5 = index << 10 | CV, one answer per value.
        // ------------------------------------------------------------

        public Task<SortedDictionary<int, SortedDictionary<int, int>>> ExploreMfxAsync(
            byte sid, int cvFrom, int cvTo, int startIndex, int count, Action<string> progress, CancellationToken cancel) =>
            RunAsync(async () =>
            {
                var map = new SortedDictionary<int, SortedDictionary<int, int>>();
                Expect(f => f[1] == ReadConfigR && f[7] == 0x40 && f[8] == sid);
                for (int cv = cvFrom; cv <= cvTo && !cancel.IsCancellationRequested; cv++)
                {
                    progress($"Lese CV {cv} (bis {cvTo}) ...");
                    int ci = (startIndex << 10) | cv;
                    sendFrame(Frame(ReadConfig, 7, 0x00, 0x00, 0x40, sid, (byte)(ci >> 8), (byte)ci, (byte)count));
                    for (int got = 0; got < count; got++)
                    {
                        // first answer may take longer; a CV without content does not answer
                        var a = await NextAsync(got == 0 ? 2000 : 1000);
                        if (a == null)
                            break;
                        int index = ((a[9] << 8) | a[10]) >> 10;
                        int answerCv = ((a[9] << 8) | a[10]) & 0x3FF;
                        if (!map.TryGetValue(answerCv, out var values))
                            map[answerCv] = values = new SortedDictionary<int, int>();
                        values[index] = a[4] == 7 ? a[11] : -1;
                    }
                }
                return map;
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
