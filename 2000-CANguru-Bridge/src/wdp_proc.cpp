#include <Arduino.h>
#include <ETH.h>
#include "alive.h"
#include "can_proc.h"
#include "globals.h"
#include <ESPAsyncWebServer.h>
#include <LittleFS.h>
#include "Bridge_Decoder.h"
#include "MOD-LCD.h"
#include "protocol_constants_core.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/queue.h"

// WDP erwartet das Märklin-UDP-Format: genau ein CAN-Frame mit 13 Byte je Paket.
// Alle Aufrufer von sendToWDP() übergeben Puffer dieser Größe.
#define WDP_FRAME_SIZE CAN_FRAME_SIZE

QueueHandle_t wdpQueue = NULL;

void initWdpQueue()
{
  wdpQueue = xQueueCreate(20, WDP_FRAME_SIZE);
  if (wdpQueue == NULL)
  {
    Serial.println("ERROR: Could not create WDP queue!");
  }
}

// -------------------- Senden an WDP -------------------

void wdpSendTask(void *pv)
{
  uint8_t frame[WDP_FRAME_SIZE];

  for (;;)
  {
    if (xQueueReceive(wdpQueue, frame, portMAX_DELAY))
    {
      // direkt über UDP senden
      udpWDP.beginPacket(wdpIP, WDP_PORT_OUT);
      udpWDP.write(frame, WDP_FRAME_SIZE);
      udpWDP.endPacket();

      // kleine Pause, um Bursts zu glätten
      vTaskDelay(5 / portTICK_PERIOD_MS);
    }
  }
}

// Aus loop(): höchstens 20 ms warten, sonst den Frame verwerfen. Mit portMAX_DELAY konnte
// eine volle Warteschlange (WDP nicht erreichbar, viele Frames) die Hauptschleife anhalten.
void sendToWDP(const uint8_t *buffer)
{
  if (wdpQueue == NULL)
    return;

  if (xQueueSend(wdpQueue, buffer, pdMS_TO_TICKS(20)) != pdTRUE)
  {
    static unsigned long lastWarnMs = 0;
    if (millis() - lastWarnMs > 5000)
    {
      lastWarnMs = millis();
      Serial.println("WDP: Warteschlange voll, Frame verworfen");
    }
  }
}

// -------------------- Empfangen von WDP -------------------

void readWDP()
{
  uint8_t UDPbuffer[CAN_FRAME_SIZE]; // buffer to hold incoming packet
  uint8_t CANbuffer[CAN_FRAME_SIZE]; // buffer to hold incoming packet
  uint8_t CAN_PING_CS2[] = {0x00, 0x31, 0x47, 0x11, 0x08, 0x00, 0x00, 0x00, 0x00, 0x03, 0x08, 0xFF, 0xFF};
  int packetSize = udpWDP.parsePacket();
  // if there's data available, read a packet
  if (packetSize)
  {
    // IP von WDP übernehmen
    wdpIP = udpWDP.remoteIP();
    // read the packet into packetBufffer
    udpWDP.read(UDPbuffer, CAN_FRAME_SIZE);
    // send received data to CAN
    memcpy(&CANbuffer, &UDPbuffer, CAN_FRAME_SIZE);
    // Für Testzwecke hier die empfangenen Daten von WDP anzeigen
    //    Serial.print("From WDP:");
    //    log_buf_d(CANbuffer, CAN_FRAME_SIZE);
    // alles an die Gleisbox
    enqueueCANFrame2CAN(CANbuffer, hashDisabled);
    // alles an die GUI
    enqueueCanToFifo(CANbuffer, 'W');

    switch (CANbuffer[CANcmd])
    {
    case PING:
      sendToWDP(CAN_PING_CS2);
      sendFrame2allDecoders(CANbuffer, CAN_FRAME_SIZE);
      break;

    case PING_R:
      // WDP meldet sich (Kennung EE EE): mit einem PING antworten, damit WDP die Geräte
      // abfragt. Früher standen hier 250 ms Wartezeit und zwei Antworten, die nie
      // gesendet wurden.
      if ((CANbuffer[data6] == 0xEE) && (CANbuffer[data7] == 0xEE))
      {
        memset(UDPbuffer, 0, sizeof(UDPbuffer));
        UDPbuffer[CANcmd] = PING;
        sendToWDP(UDPbuffer);
      }
      break;

    case CONFIG_Status:
      sendConfigData(CANbuffer);
      break;

    default:
      // Stellbefehle und S88-Abfragen an die Decoder; alle übrigen Befehle (Loks)
      // filtert sendFrame2allDecoders() heraus
      sendFrame2allDecoders(CANbuffer, CAN_FRAME_SIZE);
      break;
    }
  }
}
// ----------------------------------------------------
// CS2-Download-Routen
// ----------------------------------------------------

void setupDownloadRoutesCS2()
{
  otaServer.on("/betatest/cs2/lokomotive.cs2", HTTP_GET,
               [](AsyncWebServerRequest *request)
               {
                 request->send(LittleFS, "/lokomotive.cs2", "text/plain");
               });

  otaServer.on("/config/lokomotive.cs2", HTTP_GET,
               [](AsyncWebServerRequest *request)
               {
                 request->send(LittleFS, "/lokomotive.cs2", "text/plain");
               });
}
// ----------------------------------------------------
// Upload-Route (GUI -> Bridge)
// ----------------------------------------------------

void setupUploadRoute()
{
  otaServer.on("/upload/lokomotive.cs2", HTTP_POST, [](AsyncWebServerRequest *request) {}, [](AsyncWebServerRequest *request, String filename, size_t index, uint8_t *data, size_t len, bool final)
               {
              static File uploadFile;

              if (index == 0)
              {
                if (LittleFS.exists("/lokomotive.cs2"))
                  LittleFS.remove("/lokomotive.cs2");

                uploadFile = LittleFS.open("/lokomotive.cs2", "w");
              }

              uploadFile.write(data, len);

              if (final)
              {
                uploadFile.close();
                request->send(200, "text/plain", "OK");
              } });
}

// events
//*********************************************************************************************************
//  behandelt die diversen Stadien des ETHERNET-Aufbaus
//  Network event callback
void iNetEvtCB(arduino_event_id_t event, arduino_event_info_t info)
{
  switch (event)
  {
  case ARDUINO_EVENT_WIFI_SCAN_DONE:
    displayLCD("Scanning finished");
    break;
  case ARDUINO_EVENT_ETH_START: // SYSTEM_EVENT_ETH_START:
    displayLCD("ETHERNET Started");
    break;
  case ARDUINO_EVENT_ETH_CONNECTED: // SYSTEM_EVENT_ETH_CONNECTED:
    displayLCD("ETHERNET Connected");
    // set eth hostname here
    ETH.setHostname("CANguru-Bridge");
    break;
  case ARDUINO_EVENT_ETH_GOT_IP: // SYSTEM_EVENT_ETH_GOT_IP:
    displayIP(ETH.localIP());
    displayLCD("");
    displayLCD("Connect!");
    break;
  case ARDUINO_EVENT_ETH_DISCONNECTED: // SYSTEM_EVENT_ETH_DISCONNECTED:
    displayLCD("ETHERNET Disconnected");
    break;
  case ARDUINO_EVENT_ETH_STOP: // SYSTEM_EVENT_ETH_STOP:
    displayLCD("ETHERNET Stopped");
    break;
  default:
    log_i("ETHERNET ?? %X", event);
    break;
  }
}
