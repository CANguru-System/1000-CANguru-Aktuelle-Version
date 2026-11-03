#pragma once
#include <stdint.h>
#include <stddef.h>
#include "canguru_protocol.h"

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
