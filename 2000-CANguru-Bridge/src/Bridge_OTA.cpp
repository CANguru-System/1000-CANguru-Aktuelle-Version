#include <Arduino.h>
#include <LittleFS.h>
#include <esp_now.h>
#include "protocol_constants_core.h"
#include "globals.h"
#include "otaQueue.h"
#include "gui_proc.h"
#include "Bridge_OTA.h"
#include "Bridge_Decoder.h"

void startOtaHttpServer()
{
  otaServer.on(FW_PATH, HTTP_GET, [](AsyncWebServerRequest *request)
               {
    Serial.println("BRIDGE: /firmware.bin handler reached");

    File *fp = new File(LittleFS.open(FW_PATH, "r"));
    if (!(*fp))
    {
        Serial.println("BRIDGE: Cannot open firmware.bin");
        delete fp;
        request->send(500, "text/plain", "Cannot open firmware");
        return;
    }

    size_t total = fp->size();
    Serial.printf("BRIDGE: Serving firmware.bin (%u bytes)\n", total);

    AsyncWebServerResponse *response =
        request->beginResponse("application/octet-stream", total,
            [fp, total](uint8_t *buffer, size_t maxLen, size_t index) mutable -> size_t
            {
                if (fp == nullptr)
                    return 0; // Datei schon geschlossen
                size_t n = fp->read(buffer, maxLen);

                // Fortschritt nur merken: dieser Callback läuft im Task des Webservers.
                // Gesendet wird in processOta() aus loop(), denn das UDP-Objekt darf nicht
                // gleichzeitig aus zwei Tasks benutzt werden (sonst kommen zerstückelte
                // Pakete bei der GUI an).
                size_t sent = index + n;
                otaProgressPercent = (uint8_t)((sent * 100.0f) / total);
                otaProgressPending = true;

                // Datei am Ende schließen. Bei bekannter Länge ruft der Webserver
                // nach dem letzten Byte nicht noch einmal auf (n == 0 kommt dann nie),
                // deshalb auch bei erreichter Gesamtgröße schließen.
                if (n == 0 || sent >= total)
                {
                    fp->close();
                    delete fp;
                    fp = nullptr;
                }

                return n;
            });

    request->send(response); });
}

void stopOtaHttpServer()
{
  otaServer.end();
}

// ------------------------------------------------------------
// OTA-Start aus der GUI (CG2_OTA_START)
// mode 0: ein Decoder (selector = ID), mode 1: alle eines Typs (selector = Typ)
// Rückgabe: 0 oder Fehlercode für die Antwort an die GUI
// ------------------------------------------------------------

uint8_t otaStartFromGui(uint8_t mode, uint8_t selector)
{
  if (mode == 0)
  {
    DecoderInfo d;
    if (!findDecoderByAssignedId(selector, d))
      return CG2_ERR_UNKNOWN_DECODER;

    otaQueue.clear();
    OtaEntry e;
    e.decoderId = d.assignedId;
    memcpy(e.mac, d.Decoder_Mac, 6);
    otaQueue.push_back(e);
    Serial.printf("OTA requested for decoder ID %d\r\n", selector);
  }
  else if (mode == 1)
  {
    startGroupOtaForType(selector);
    if (otaQueue.size() == 0)
      return CG2_ERR_UNKNOWN_DECODER;
    Serial.printf("OTA requested for %u decoders of type %02X\r\n", otaQueue.size(), selector);
  }
  else
    return CG2_ERR_BAD_VALUE;

  otaTotalCount = otaQueue.size();
  otaFinishedCount = 0;

  if (!firmwareReady)
  {
    Serial.println("FW: OTA requested, firmware not ready yet");
    otaState = OTA_WAIT_FOR_FIRMWARE;
  }
  else
    otaState = OTA_START;
  return 0;
}

// Den Start-Befehl an den Decoder sendet sendOtaStartWifiToDecoder() in Bridge_Decoder.cpp
// (Protokoll Version 2 oder altes Format für Decoder mit alter Firmware).
