#pragma once

#include <Arduino.h>

// Rohes UDP-Paket an die GUI. Die Nachrichten im Protokoll Version 2 baut gui_link.cpp;
// darf nur aus loop() aufgerufen werden (das UDP-Objekt verträgt keine zwei Tasks).
void udpSendToGui(const uint8_t* data, int len);
