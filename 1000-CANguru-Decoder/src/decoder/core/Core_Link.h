#pragma once
// Nachrichten zwischen Decoder und Bridge im CANguru-Protokoll Version 2
// (0400-Protocol/PROTOKOLL.md): Empfang, Adressprüfung, Antworten, Senden.

#include <stdint.h>
#include <stddef.h>
#include "canguru_protocol.h"

// Aus dem ESP-NOW-Empfangs-Callback (WLAN-Task): Nachricht nur in die Warteschlange legen
void coreLinkEnqueue(const uint8_t *data, int len);

// Aus loop(): wartende Nachrichten verarbeiten
void coreLinkLoop();

// Senden an die Bridge
void coreLinkSend(uint8_t feature, uint8_t command, const uint8_t *payload, size_t len);
void coreLinkReply(const Cg2Header &request, const uint8_t *payload, size_t len, uint8_t errorCode);

// Kern-Meldungen
void coreLinkSendHello();
void coreLinkSendRejoin();
void coreLinkSendAlive(uint8_t status, const uint16_t *values, uint8_t count);
