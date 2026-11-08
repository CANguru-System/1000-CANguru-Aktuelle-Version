using CANguru.Models;
using CANguru.Protocol;
using System.Collections.Concurrent;

namespace CANguru.Controllers
{
    // Self-description of a decoder: parameters, actions and measured values
    public sealed class DecoderDescription
    {
        public List<Cg2Description> Params { get; init; } = new();
        public List<Cg2Description> Actions { get; init; } = new();
        public List<Cg2Description> Values { get; init; } = new();
    }

    // Keeps the self-description of each decoder while the GUI runs, so the parameter
    // window opens quickly the second time (only the values are read then).
    //
    // Key: MAC and firmware. Not just type and firmware, because a description can depend
    // on the settings of the decoder itself (power decoder: the maximum threshold follows
    // the shunt). After saving or a factory reset the entry is dropped and read again.
    public static class DescriptionCache
    {
        private static readonly ConcurrentDictionary<string, DecoderDescription> cache = new();

        private static string Key(CANguruDecoder d) => $"{d.MacText}/{d.Firmware}";

        public static DecoderDescription? Get(CANguruDecoder d) =>
            cache.TryGetValue(Key(d), out var desc) ? desc : null;

        public static void Put(CANguruDecoder d, DecoderDescription desc) => cache[Key(d)] = desc;

        public static void Invalidate(CANguruDecoder d) => cache.TryRemove(Key(d), out _);
    }
}
