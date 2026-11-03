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
#include "handlers.h"
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

// --- Forward Declarations ---
void handleFirmware();

// Forward für Routing-Funktionen
void sendSwitchSignalSettingsToDecoder(uint8_t featureId, uint8_t commandId, const uint8_t *payload, int payloadLen);

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

  Serial.println("Bridge ready for HELLO packets");

  // Decoder-Liste initialisieren
  //  loadDecoderList();
}

// ----------------------------------------------------
// Routing der GUI-Nachrichten für Decoder im alten Format (Feature | Frame | Payload).
// Sitzung, CAN, Datei und OTA kommen im Protokoll Version 2 (gui_link.cpp).
// ----------------------------------------------------
void handleGuiPacket(uint8_t featureId, uint8_t commandId, const uint8_t *payload, int payloadLen)
{

  switch (featureId)
  {
  case FEATURE_POWER:
    switch (commandId)
    {
    case POWER_CMD_GET_THRESHOLD:
      handleGetPowerDecoderSettings();
      break;
    case POWER_CMD_SET_THRESHOLD:
      handleSetPowerDecoderSettings(payload, payloadLen);
      break;
    default:
      Serial.printf("Unknown POWER commandId %u\r\n", commandId);
      break;
    }
    break;

  case FEATURE_FEEDBACK:
    handleFeedbackGuiCommand(commandId, payload, payloadLen);
    break;

  case FEATURE_SWITCH:
  case FEATURE_SIGNAL:
    switch (commandId)
    {
    case SWITCH_CMD_SET_SETTINGS:
    case SWITCH_CMD_GET_SETTINGS:
    case SWITCH_CMD_MOVE2START:
    case SWITCH_CMD_SET_ZERO:
    case SWITCH_CMD_MOVE2END:
    case SWITCH_CMD_SET_END:
    case SWITCH_CMD_TEST_RUN:
    case SWITCH_CMD_STOP_ALL:
    case SIGNAL_CMD_SET_SETTINGS:
    case SIGNAL_CMD_GET_SETTINGS:
    case SIGNAL_CMD_MOVE2START:
    case SIGNAL_CMD_SET_ZERO:
    case SIGNAL_CMD_MOVE2END:
    case SIGNAL_CMD_SET_END:
    case SIGNAL_CMD_TEST_RUN:
    case SIGNAL_CMD_STOP_ALL:
      sendSwitchSignalSettingsToDecoder(featureId, commandId, payload, payloadLen);
      break;

      // andere Features …

    default:
      Serial.printf("Unknown SWITCH commandId %02X\r\n", commandId);
      break;
    }
    break;
  case FEATURE_SETTING:
    switch (commandId)
    {
    case SETTING_CMD_RESET_TO_DEFAULTS:
      sendResetSettingsToDecoder(payload, payloadLen);
      break;

    default:
      Serial.printf("Unknown SETTING commandId %u\r\n", commandId);
      break;
    }
    break;

  default:
    Serial.printf("Unknown DECODER featureId %02X\r\n", featureId);
    break;
  }
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

  // Protokoll Version 2: Sitzung, CAN-Tunnel, lokomotive.cs2, OTA
  if (cg2IsV2(buf, packetLength))
  {
    cg2HandleGuiPacket(buf, packetLength, udp.remoteIP());
    return;
  }

  // ----------------------------------------------------
  // Altes Format für Decoder-Nachrichten (bis Schritt 2.3):
  //    Feature | Frame | Payload
  // ----------------------------------------------------
  if (packetLength >= 2)
  {
    uint8_t feature = buf[0];
    uint8_t frame = buf[1];

    const uint8_t *payload = nullptr;
    int payloadLen = packetLength - 2;

    if (payloadLen > 0)
      payload = &buf[2];

    handleGuiPacket(feature, frame, payload, payloadLen);
  }

  //  onUdpData(buf, packetLength);
}

// ----------------------------------------------------
// SWITCH-Frames an Decoder senden (ESP-NOW)
// ----------------------------------------------------

void sendSwitchSignalSettingsToDecoder(uint8_t featureId, uint8_t commandId, const uint8_t *payload, int payloadLen)
{
  if (payloadLen == 0)
  {
    Serial.println("ERROR: SWITCH/SIGNAL packet without payload");
    return;
  }

  // Signal-Kommandos gehen an die Signaldecoder, alle anderen an die Weichendecoder
  const uint8_t decoderType = (featureId == FEATURE_SIGNAL) ? DEVTYPE_SIGNAL : DEVTYPE_SWITCH;

  auto bytes = buildPacket(
      featureId,
      commandId,
      payload,
      payloadLen);
  sendTheData(SendMode::TYPE, decoderType, bytes.data(), bytes.size());
}

// ----------------------------------------------------
// Hilfsfunktion: lokomotive.cs2 anzeigen
// ----------------------------------------------------
void showLokomotiveFile()
{
  File f = LittleFS.open("/lokomotive.cs2", "r");
  if (!f)
  {
    Serial.println("Fehler: lokomotive.cs2 konnte nicht geöffnet werden!");
    return;
  }

  Serial.println("===== Inhalt von lokomotive.cs2 =====");

  while (f.available())
  {
    String line = f.readStringUntil('\n');
    Serial.println(line);
  }

  Serial.println("=====================================");
  f.close();
}

// --------------------------------------------------
// loop()
// --------------------------------------------------
void loop()
{
  processAssignRetries();
  cleanupFinishedAssigns();
  // Pakete der Decoder (ESP-NOW) verarbeiten
  processEspNowRx();
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

  // 7. Alive-Überwachung + Alive-Senden
  checkAliveStates();
}
