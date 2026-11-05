#pragma once
#include <stdint.h>
#include <stddef.h>
#include "canguru_protocol.h"

// Beschreibung eines Parameters (Feature Parameter, PROTOKOLL.md 5.4).
// Der Kern beantwortet damit DESCRIBE_GET und prüft geschriebene Werte.
struct Cg2ParamDef
{
    uint8_t type;                // CG2_TYPE_U8, _U16, _CHOICE (Wert = Index), _BOOL
    uint16_t min, max, def, step;
    const char *unit;            // z. B. "mA", "" ohne Einheit
    const char *name;            // kurz, deutsch, z. B. "Abschaltgrenze"
    const char *const *choices;  // nur bei CG2_TYPE_CHOICE
    uint8_t choiceCount;
};

// Beschreibung einer Aktion oder eines Messwerts (nur Name und Einheit)
struct Cg2InfoDef
{
    const char *name;
    const char *unit;
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

    // Werkseinstellungen: eigene Einstellungen löschen und Standardwerte laden,
    // die Zubehöradresse bleibt erhalten. true = erfolgreich.
    virtual bool factoryReset() { return false; }

    // ---- Selbstbeschreibung (Feature Parameter / Aktion) ----
    // Der Kern beantwortet DESCRIBE_GET, READ, WRITE und EXECUTE aus diesen Tabellen.
    // Nummern zählen ab 0. Die Messwerte müssen zu getAliveValues() passen.

    virtual uint8_t paramCount() const { return 0; }
    virtual const Cg2ParamDef *paramDef(uint8_t n) const { return nullptr; }
    virtual uint16_t paramRead(uint8_t n) { return 0; }
    // Wert ist bereits auf den erlaubten Bereich begrenzt; Rückgabe = tatsächlich gespeicherter Wert
    virtual uint16_t paramWrite(uint8_t n, uint16_t value) { return value; }

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
};

extern DecoderBase *gDecoder;
