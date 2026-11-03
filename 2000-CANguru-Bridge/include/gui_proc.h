#pragma once

#include <Arduino.h>
#include <PacketHandler.h>

// Senden an die GUI. Meldungen im Protokoll Version 2 (Sitzung, CAN, Datei, OTA)
// stehen in gui_link.h; hier nur noch das alte Format für Decoder-Nachrichten.
void udpSendToGui(const uint8_t* data, int len);
void sendPacketToGUI(const std::vector<uint8_t> &bytes);
void sendPacketToGUI(const ParsedPacket &p);
