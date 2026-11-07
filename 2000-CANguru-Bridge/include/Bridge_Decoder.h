#pragma once
// Bridge <-> Decoder über ESP-NOW (CANguru-Protokoll Version 2, 0400-Protocol/PROTOKOLL.md)
//
// Die Bridge vermittelt: Nachrichten der GUI an einen Decoder (Byte 5 = ID) werden per
// Unicast weitergeleitet, Antworten und Meldungen der Decoder gehen unverändert an die
// GUI. Selbst bearbeitet sie nur Anmeldung, Alive, CAN-Frames, Gleisspannung und OTA.
// Rundsendungen an alle Decoder laufen über eine getaktete Warteschlange (kein delay()).

#include <Arduino.h>
#include "protocol_constants_core.h"
#include "globals.h"
#include "canguru_protocol.h"

extern bool otaRunning;

void initBridgeEspNow();
void processEspNowRx();  // aus loop(): empfangene Decoder-Pakete verarbeiten
void decoderLinkLoop();  // aus loop(): Rundsendungen, Wiederholungen eigener Anfragen, Zustellfehler

bool findDecoderByAssignedId(uint8_t id, DecoderInfo &out);

// An die Decoder
void sendFrame2allDecoders(uint8_t *data, int len); // nur PING, Stellbefehle und S88-Abfragen
void sendConfigData(const uint8_t *data);
void sendStartToDecoders();
void sendResetToDecoders();                          // sendet sofort (vor einem Neustart)
void sendTrackPowerToDecoders(uint8_t on);
void sendOtaStartWifiToDecoder(uint8_t decoderId);

// Nachricht der GUI an einen Decoder (Byte 5 = Decoder-ID) weiterleiten
void forwardGuiToDecoder(const Cg2Header &h, const uint8_t *frame, size_t len);

// Decoder in der GUI bekannt machen
void sendDecoderAddedToGUI(const DecoderInfo &d);
