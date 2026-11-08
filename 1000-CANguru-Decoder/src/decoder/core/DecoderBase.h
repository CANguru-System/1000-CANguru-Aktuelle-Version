#pragma once
#include <stdint.h>
#include <stddef.h>
#include <vector>
#include "canguru_protocol.h"

class CANguruPreferences;

// Merkmale eines Parameters (Cg2ParamDef::flags)
static const uint8_t CG2_PF_KEEP_ON_RESET = 0x01; // bleibt bei Werkseinstellungen erhalten (z. B. Adresse)

// Beschreibung eines Parameters (Feature Parameter, PROTOKOLL.md 5.4).
// Der Kern beantwortet damit DESCRIBE_GET und prüft geschriebene Werte.
// Mit key != nullptr speichert der Kern den Wert selbst (NVS, Namensraum aus paramPrefs()):
// u8, Auswahl, Ja/Nein als Byte, u16 als 16-Bit-Wert - dieselben Formate wie bisher.
struct Cg2ParamDef
{
    uint8_t type;                // CG2_TYPE_U8, _U16, _CHOICE (Wert = Index), _BOOL
    uint16_t min, max, def, step;
    const char *unit;            // z. B. "mA", "" ohne Einheit
    const char *name;            // kurz, deutsch, z. B. "Abschaltgrenze"
    const char *const *choices;  // nur bei CG2_TYPE_CHOICE
    uint8_t choiceCount;
    const char *key;             // NVS-Schlüssel (höchstens 15 Zeichen) oder nullptr (fehlt = nullptr)
    uint8_t flags;               // CG2_PF_* (fehlt = 0)
};

// Beschreibung einer Aktion oder eines Messwerts.
// Messwerte können ab Protokoll 2.1 als Auswahl oder Bitmaske beschrieben werden:
//   type CG2_TYPE_CHOICE: Wert = Index in choices (z. B. "abzweigend", "gerade", "unterwegs")
//   type CG2_TYPE_BITS:   Wert = Bitmaske, choices = Namen der Bits (Bit 0 zuerst)
// Ohne choices (choiceCount 0) ist der Messwert eine Zahl (u16) mit Einheit.
// Namensregel für Gruppen: "Gruppe: Name", z. B. "Signal 3: Stellung".
struct Cg2InfoDef
{
    const char *name;
    const char *unit;
    uint8_t type;                // nur bei choiceCount > 0: CG2_TYPE_CHOICE oder CG2_TYPE_BITS
    const char *const *choices;  // Auswahltexte bzw. Namen der Bits (fehlt = nullptr)
    uint8_t choiceCount;
};

// Basisklasse aller Decoder. Der Kern (Core_*) übernimmt Funk, Anmeldung, Alive,
// OTA und Konfiguration; ein Decodertyp implementiert nur die Hooks unten.
// Nachrichtenformat: CANguru-Protokoll Version 2 (0400-Protocol/PROTOKOLL.md).
class DecoderBase
{
public:
    enum AliveState
    {
        Offline,
        Weak,
        Online
    };

    virtual ~DecoderBase() {}

    // Abgeleitete Klassen rufen zuerst DecoderBase::setup() bzw. DecoderBase::loop() auf
    virtual void setup();
    virtual void loop();

    // ---- Hooks für die Decodertypen ----

    // Typeigene Nachricht (z. B. CG2_F_STEPPER). true = verarbeitet.
    // Bei h.ackRequested() muss der Decoder mit reply() antworten.
    virtual bool onMessage(const Cg2Header &h, const uint8_t *payload, uint16_t len) { return false; }

    // Märklin-CAN-Frame (13 Byte), z. B. Weichenbefehl oder S88-Abfrage aus WDP
    virtual void onCanFrame(const uint8_t *frame) {}

    // Messwerte für das Alive (z. B. Strom je Kanal); Rückgabe = Anzahl
    virtual uint8_t getAliveValues(uint16_t *values, uint8_t maxValues) { return 0; }

    virtual AliveState getStatus() const { return AliveState::Online; }

    // Werkseinstellungen: eigene Einstellungen auf Standard, die Zubehöradresse bleibt
    // erhalten. true = erfolgreich. Standard: alle vom Kern gespeicherten Parameter ohne
    // CG2_PF_KEEP_ON_RESET auf ihren Standardwert (siehe paramsFactoryReset()).
    virtual bool factoryReset() { return paramsFactoryReset(); }

    // Gleisspannung an/aus (STOPP/GO am CAN-Bus, von der Bridge an alle Decoder)
    virtual void onTrackPower(bool on) {}

    // ---- Selbstbeschreibung (Feature Parameter / Aktion) ----
    // Der Kern beantwortet DESCRIBE_GET, READ, WRITE und EXECUTE aus diesen Tabellen.
    // Nummern zählen ab 0. Die Messwerte müssen zu getAliveValues() passen.

    virtual uint8_t paramCount() const { return 0; }
    virtual const Cg2ParamDef *paramDef(uint8_t n) const { return nullptr; }
    // Standard: Wert aus dem Speicher des Kerns (siehe paramsLoad())
    virtual uint16_t paramRead(uint8_t n);
    // Wert ist bereits auf den erlaubten Bereich begrenzt; Rückgabe = tatsächlich gespeicherter Wert.
    // Standard: speichert (bei key) im NVS und ruft onParamChanged() auf.
    virtual uint16_t paramWrite(uint8_t n, uint16_t value);
    // Namensraum für die vom Kern gespeicherten Parameter
    virtual CANguruPreferences *paramPrefs() { return nullptr; }
    // ein Parameter hat einen neuen Wert (nach paramWrite oder Werkseinstellungen)
    virtual void onParamChanged(uint8_t n, uint16_t value) {}

    virtual uint8_t actionCount() const { return 0; }
    virtual const Cg2InfoDef *actionDef(uint8_t n) const { return nullptr; }
    // true = ausgeführt (bzw. gestartet)
    virtual bool actionExecute(uint8_t n, const uint8_t *args, uint16_t len) { return false; }

    virtual uint8_t valueCount() const { return 0; }
    virtual const Cg2InfoDef *valueDef(uint8_t n) const { return nullptr; }

    // ---- Senden an die Bridge ----

    // Antwort auf eine Anfrage; errorCode != 0 meldet einen Fehler (CG2_ERR_*)
    void reply(const Cg2Header &request, const uint8_t *payload, size_t len, uint8_t errorCode = 0);
    void reply(const Cg2Header &request) { reply(request, nullptr, 0); }

    // Eigene Nachricht ohne Anfrage (z. B. Stellung geändert, Überstrom)
    void sendMessage(uint8_t feature, uint8_t command, const uint8_t *payload, size_t len);

    // Märklin-CAN-Frame (13 Byte) an die Bridge, die ihn an WDP und GUI weitergibt
    void sendCanFrame(const uint8_t *frame);

    // Ereignis an die GUI: [ereignis][daten ...] (CG2_EV_*, PROTOKOLL.md 5.6)
    void sendEvent(uint8_t event, const uint8_t *data, size_t len);

    // ---- Parameter-Speicher des Kerns ----

    // Lädt alle Parameter mit key aus dem NVS (begrenzt auf den erlaubten Bereich) und ruft
    // für jeden onParamChanged() auf. Aufruf im setup() des Decodertyps, nach paramPrefs()->init().
    void paramsLoad();
    // Werkseinstellungen für die vom Kern gespeicherten Parameter
    bool paramsFactoryReset();
    // aktueller Wert aus dem Speicher des Kerns
    uint16_t paramValue(uint8_t n) const { return n < paramValues.size() ? paramValues[n] : 0; }

private:
    std::vector<uint16_t> paramValues;
};

extern DecoderBase *gDecoder;
