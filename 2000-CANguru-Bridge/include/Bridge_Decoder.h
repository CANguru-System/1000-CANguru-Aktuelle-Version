#pragma once
// Bridge <-> Decoder über ESP-NOW (CANguru-Protokoll Version 2, 0400-Protocol/PROTOKOLL.md)
//
// Die Bridge vermittelt: Nachrichten der GUI an einen Decoder (Byte 5 = ID) werden per
// Unicast weitergeleitet, Antworten und Meldungen der Decoder gehen unverändert an die
// GUI. Selbst bearbeitet sie nur Anmeldung, Alive, CAN-Frames und OTA.
//
// Übergang: Decoder mit alter Firmware (vor Version 2) werden weiter angemeldet, ihr
// Alive und ihre CAN-Meldungen laufen weiter und sie lassen sich per OTA aktualisieren.

#include <Arduino.h>
#include "protocol_constants_core.h"
#include "globals.h"
#include "canguru_protocol.h"

extern bool otaRunning;

void initBridgeEspNow();
void processEspNowRx();  // aus loop(): empfangene Decoder-Pakete verarbeiten
void decoderLinkLoop();  // aus loop(): Wiederholungen eigener Anfragen, Zustellfehler

bool findDecoderByAssignedId(uint8_t id, DecoderInfo &out);

// An die Decoder (Version 2 und, solange vorhanden, alte Decoder)
void sendFrame2allDecoders(uint8_t *data, int len);
void sendFrame2TypeDecoders(uint8_t *data, int len, uint8_t decoderType);
void sendConfigData(const uint8_t *data);
void sendStartToDecoders();
void sendResetToDecoders();
void sendTrackPowerToDecoders(uint8_t on);
void sendOtaStartWifiToDecoder(uint8_t decoderId);

// Nachricht der GUI an einen Decoder (Byte 5 = Decoder-ID) weiterleiten
void forwardGuiToDecoder(const Cg2Header &h, const uint8_t *frame, size_t len);

// Decoder in der GUI bekannt machen
void sendDecoderAddedToGUI(const DecoderInfo &d);
