#pragma once

#include <Arduino.h>
#include <WiFiUdp.h>
#include <ESPAsyncWebServer.h>
#include <vector>

extern WiFiUDP udp;
extern IPAddress guiIP;
extern IPAddress wdpIP;
extern bool resetFlag; // nach Reset: GUI muss sich neu synchronisieren
extern unsigned long lastRequest;
extern unsigned long lastAliveSummary;
extern bool guiIsAlive;
extern unsigned long lastGuiSeen;
extern unsigned long lastGuiAlive;
extern unsigned long lastPingSend;
extern bool GleisboxFound;
const bool hashEnabled = true;
const bool hashDisabled = false;

extern bool systemReady;

extern WiFiUDP udpWDP;
extern WiFiUDP udpGUI; // GUI -> Bridge

extern AsyncWebServer otaServer;
extern const char *FW_PATH;

// ESP-NOW-Broadcast-Adresse (an alle Decoder)
const uint8_t Bridge_Mac[] = {0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF};

extern uint8_t SysSTOPP[];

struct DecoderInfo
{
  uint8_t assignedId = 0;
  uint8_t Decoder_Mac[6] = {};
  uint8_t decoderType = 0;
  uint8_t fwMajor = 0;
  uint8_t fwMinor = 0;
  uint8_t protoMinor = 0;
  bool isReady = false;   // angemeldet und meldet sich regelmäßig (Alive)
  bool hasError = false;  // letzter Alive-Status = Fehler (z. B. Stromdecoder nach Überstrom)
  bool announced = false; // GUI hat ADDED mit den Daten aus dem letzten HELLO erhalten
  unsigned long lastSeen = 0;
};

extern std::vector<DecoderInfo> decoders;

extern uint8_t FW_MAJOR;
extern uint8_t FW_MINOR;

bool GUIipIsNotSet();
void logMac(const char *prefix, const uint8_t *mac);

// Decoder-Liste. Die Zuordnung MAC -> ID wird in /decoders.txt gespeichert,
// damit ein Decoder seine ID (und damit seine CAN-UID) dauerhaft behält.
void loadDecoderList();
int findDecoderByMac(const uint8_t *mac);
int findDecoderIndexById(uint8_t id);
int registerDecoder(const uint8_t *mac, uint8_t type); // Index; neue Decoder bekommen die nächste freie ID
bool removeDecoder(uint8_t id);

