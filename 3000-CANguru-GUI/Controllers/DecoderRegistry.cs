using CANguru.Models;
using System.Text.Json;

namespace CANguru.Controllers
{
    // Alle angemeldeten Decoder an einer Stelle (ersetzt DecoderStorage und die Listbox als
    // Datenquelle). Schlüssel ist die MAC-Adresse; die ID ist eine Eigenschaft, über die
    // Nachrichten der Bridge zugeordnet werden.
    //
    // Eigene Namen der Decoder werden je MAC in "decoders.json" gespeichert und bei jeder
    // Anmeldung wieder übernommen.
    //
    // Threadsicher: Anmeldung und Alive kommen aus dem UDP-Empfang, die Anzeige aus dem UI-Thread.
    public sealed class DecoderRegistry
    {
        private readonly object sync = new();
        private readonly Dictionary<string, CANguruDecoder> byMac = new();
        private readonly Dictionary<string, string> names;
        private readonly string namesFile;

        public DecoderRegistry(string namesFile = "decoders.json")
        {
            this.namesFile = namesFile;
            names = LoadNames();
        }

        // Momentaufnahme aller Decoder, nach Anzeigename sortiert
        public List<CANguruDecoder> All()
        {
            lock (sync)
                return byMac.Values.OrderBy(d => d.DisplayName).ThenBy(d => d.AssignedId).ToList();
        }

        public bool TryGetById(byte id, out CANguruDecoder decoder)
        {
            lock (sync)
            {
                decoder = byMac.Values.FirstOrDefault(d => d.AssignedId == id)!;
                return decoder != null;
            }
        }

        // Anmeldung: ersetzt einen vorhandenen Eintrag derselben MAC vollständig (Typ oder
        // Firmware können sich geändert haben). Ein anderer Decoder, der bisher dieselbe ID
        // hatte, ist veraltet und wird entfernt.
        public void AddOrUpdate(CANguruDecoder decoder)
        {
            lock (sync)
            {
                string key = decoder.MacText;
                foreach (var stale in byMac.Where(kv => kv.Key != key && kv.Value.AssignedId == decoder.AssignedId)
                                           .Select(kv => kv.Key).ToList())
                    byMac.Remove(stale);
                if (names.TryGetValue(key, out var name))
                    decoder.Name = name;
                byMac[key] = decoder;
            }
        }

        public void Remove(byte id)
        {
            lock (sync)
            {
                foreach (var key in byMac.Where(kv => kv.Value.AssignedId == id).Select(kv => kv.Key).ToList())
                    byMac.Remove(key);
            }
        }

        // Eigenen Namen setzen (leer = Typ anzeigen) und dauerhaft speichern
        public void SetName(CANguruDecoder decoder, string name)
        {
            lock (sync)
            {
                name = name.Trim();
                decoder.Name = name;
                if (name.Length == 0)
                    names.Remove(decoder.MacText);
                else
                    names[decoder.MacText] = name;
                SaveNames();
            }
        }

        private Dictionary<string, string> LoadNames()
        {
            try
            {
                if (File.Exists(namesFile))
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(namesFile))
                           ?? new Dictionary<string, string>();
            }
            catch (Exception)
            {
                // beschädigte Datei: ohne Namen weiterarbeiten
            }
            return new Dictionary<string, string>();
        }

        private void SaveNames()
        {
            File.WriteAllText(namesFile, JsonSerializer.Serialize(names, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
