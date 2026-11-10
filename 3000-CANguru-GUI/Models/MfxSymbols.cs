namespace CANguru.Models
{
    // Function symbols of mfx locomotive decoders and their counterpart in lokomotive.cs2
    // (CS2 function type, shown by WDP). The two numberings differ.
    //
    // mfx symbols: meaning found by reading DB 640, BR 491 and comparing with the MS2
    // (bit 0x80 = momentary, the function only works while the key is pressed).
    // CS2 types: determined with a symbol test in WDP (November 2026), e.g.
    //   1 Spitzenbeleuchtung, 2 Innenbeleuchtung, 3 Schlusslicht, 4 Fernlicht, 5 Sound,
    //   6 Pantograph, 7 Dampf, 8 Rangiergang, 9 Telex, 10 Horn, 11 Rangierpfiff,
    //   12 Pfeife, 13 Glocke, 14 Gantry, 19 Horn, 20 Bremsenquietschen aus, 21/22 Sound,
    //   23/24 Betriebsgeräusch Motor, 25 Durchsage, 26 Kohle schaufeln, 27/28 Türen
    //   schließen, 29/30 Tenderlüfter, 31 Feuerbüchse, 32 Innenbeleuchtung,
    //   34/35 Tischbeleuchtung, 38/40 Zugnummernanzeige, 41/42 Führerstandslicht,
    //   43/44 Pufferschläge, 45 Durchsage, 46 Kranhaken.
    // In lokomotive.cs2 a momentary function keeps bit 0x80 (WDP reads the symbol from
    // the lower bits).
    public static class MfxSymbols
    {
        // mfx symbol (without bit 0x80) -> (meaning, CS2 type)
        private static readonly Dictionary<int, (string Name, int Cs2)> Table = new()
        {
            [3] = ("Licht", 1),
            [4] = ("Innenbeleuchtung", 2),
            [7] = ("Horn", 10),
            [8] = ("Motorgeräusch", 23),
            [9] = ("Ansage", 25),
        };

        // CS2 type for lokomotive.cs2; null = unknown symbol (WDP then shows its default)
        public static int? ToCs2(int mfxSymbol) =>
            Table.TryGetValue(mfxSymbol & 0x7F, out var e) ? e.Cs2 | (mfxSymbol & 0x80) : null;

        // meaning for messages; null = unknown
        public static string? Name(int mfxSymbol) =>
            Table.TryGetValue(mfxSymbol & 0x7F, out var e) ? e.Name : null;
    }
}
