#include <Arduino.h>
#include <WiFi.h>
#include <esp_wifi.h>
#include <esp_now.h>
#include <LittleFS.h>
#include <ETH.h>
#include <globals.h>
#include <Adafruit_GFX.h>
#include "protocol_constants_core.h"
#include "otaQueue.h"
#include "wdp_proc.h"
#include "CAN_proc.h"
#include "gui_proc.h"
#include "alive.h"
#include "Bridge_OTA.h"
#include "Bridge_Decoder.h"
#include "gui_link.h"
#include "MOD-LCD.h"

// Der Master speichert im setup seine Broadcast-Adresse und jeweils bei Empfang der Slaves deren MAC-Adresse.
// Er antwortet auf den Broadcast mit einer ID-Zuweisung. Danach können die Slaves per Unicast angesprochen werden.
// Zunächst sendet er Broadcast, die Dlaves melden sich und der Master registiert dann deren Adtesse und schickt eine
// ID-Zuweisung zurück. Danach können die Slaves per Unicast angesprochen werden.

// bridge_v3_alive_v1.cpp
// ESP32-C3 Bridge v3 + Alive v1
// - ESPNOW zu Decodern
// - UDP zur GUI
// - IP-Ermittlung
// - Alive-Tracking mit DecoderInfo
// - Alive-Summary an GUI
// ----------------------------------------------------
// Konfiguration
// ----------------------------------------------------

const uint16_t GUI_PORT_IN = BRIDGE_PORT; // GUI → Bridge
const uint16_t GUI_PORT_OUT = GUI_PORT;   // Bridge → GUI

const char *CS_PATH = "/lokomotive.cs2";

void initLittleFS()
{
  if (!LittleFS.begin(false))
  {
    Serial.println("LittleFS not mounted → formatting...");
    if (!LittleFS.begin(true))
    {
      Serial.println("LittleFS formatting FAILED!");
      return;
    }
    Serial.println("LittleFS formatted successfully.");
  }
  else
  {
    Serial.println("LittleFS mounted successfully.");
  }
}

// --------------------------------------------------
// setup()
// --------------------------------------------------
void setup()
{
#if defined ARDUINO_ESP32_EVB
  delay(500);
#endif
  Serial.begin(bdrMonitor);
  delay(500);

  log_i("\r\n\r\nC A N g u r u - B r i d g e");
  log_i("\n on %s", ARDUINO_BOARD);
  log_i("CPU Frequency = %d Mhz", F_CPU / 1000);
  initDisplayLCD28();

  displayLCD("CANguru-Release-Version");

  // die Routine für die Statusmeldungen des WiFi wird registriert
  wifi_event_id_t inet_evt_hnd = WiFi.onEvent(iNetEvtCB);

  ETH.begin();

  while (!ETH.linkUp())
    delay(100);
  while (ETH.localIP().toString() == "0.0.0.0")
    delay(100);

  Serial.print("Bridge IP: ");
  Serial.println(ETH.localIP());

  udpWDP.begin(WDP_PORT_IN);
  udp.begin(GUI_PORT_IN);
  initWdpQueue();

  xTaskCreate(wdpSendTask, "wdpSendTask", 4096, NULL, 1, NULL);

  InitAlive();
  initTWAI();

  // WiFi + ESP-NOW initialisieren
  initBridgeEspNow();

  setupDownloadRoutesCS2();
  setupUploadRoute();
  otaServer.begin();

  // LittleFS mounten
  initLittleFS();

  if (!LittleFS.exists(CS_PATH))
    Serial.println("BRIDGE: WARNING - lokomotive.cs2 not found in LittleFS");

  // Dauerhafte Decoder-Liste (MAC -> ID) laden
  loadDecoderList();

  Serial.println("Bridge ready for HELLO packets");
}

// ----------------------------------------------------
// GUI-Pakete lesen
// ----------------------------------------------------
void readGUI()
{
  int packetLength = udp.parsePacket();
  if (packetLength <= 0)
    return;

  lastGuiSeen = millis();

  uint8_t buf[BlockSizeBridge];
  if (packetLength > (int)sizeof(buf))
    packetLength = sizeof(buf);
  udp.read(buf, packetLength);

  // Die GUI spricht nur noch Protokoll Version 2 (gui_link.cpp)
  if (cg2IsV2(buf, packetLength))
    cg2HandleGuiPacket(buf, packetLength, udp.remoteIP());
  else
    Serial.println("GUI: Nachricht im alten Format ignoriert - GUI zu alt?");
}

// --------------------------------------------------
// loop()
// --------------------------------------------------
void loop()
{
  // Pakete der Decoder (ESP-NOW) verarbeiten, eigene Anfragen wiederholen
  processEspNowRx();
  decoderLinkLoop();
  // 0. Nachrichten von GUI lesen
  readGUI();

  // 1. CAN zuerst – höchste Priorität
  readCANMsg();

  // 2. UDP lesen (GUI / WDP)
  readWDP();

  unsigned long now = millis();
  if (now - lastRequest > 100)
  {
    lastRequest = now;
    processCANFrames2CAN();
  }

  // Alive-Timeouts + Summary
  if (millis() - lastAliveSummary > ALIVE_SUMMARY_INTERVAL_MS)
  {
    if (GUIipIsNotSet())
      return;

    lastAliveSummary = millis();
    checkAliveTimeouts();
    sendAliveSummary();
  }

  // 4. CAN erneut lesen – falls Antworten kamen
  readCANMsg();

  // 5. CAN → UDP Pipeline
  sendFiFoFromCANToGUI();

  // 6. OTA-Ablauf (Decoder nacheinander aktualisieren, Fortschritt melden)
  processOta();
}
