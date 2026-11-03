#pragma once
// Verbindung Bridge <-> GUI im CANguru-Protokoll Version 2 (0400-Protocol/PROTOKOLL.md)

#include <Arduino.h>
#include <IPAddress.h>
#include "canguru_protocol.h"

// Verarbeitet eine Nachricht der GUI im Format Version 2 (erstes Byte CG2_MAGIC)
void cg2HandleGuiPacket(const uint8_t *buf, int len, IPAddress remote);

// Sendet eine eigene Nachricht an die GUI (Folgenummer wird vergeben)
void cg2SendToGui(uint8_t feature, uint8_t command, uint8_t decoder,
                  const uint8_t *payload, size_t len);

// Ergebnis des OTA-Updates eines Decoders (CG2_OTA_RESULT)
enum OtaResult : uint8_t
{
    OTA_RESULT_OK = 0,       // neue Firmware läuft
    OTA_RESULT_REJECTED = 1, // Decoder hat die Datei abgelehnt, alte Firmware läuft weiter
    OTA_RESULT_TIMEOUT = 2   // keine Rückmeldung des Decoders
};

// Meldungen an die GUI
void cg2SendCanFrameToGui(uint32_t seq, const uint8_t *frame13, char source);
void cg2SendOtaProgress(uint8_t decoderId, uint8_t percent, uint8_t overall);
void cg2SendOtaResult(uint8_t decoderId, uint8_t result);
