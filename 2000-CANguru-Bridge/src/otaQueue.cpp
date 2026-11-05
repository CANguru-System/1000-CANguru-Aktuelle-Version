#include <Arduino.h>
#include <vector>
#include <esp_now.h>
#include <esp_wifi.h>
#include <LittleFS.h>
#include "protocol_constants_core.h"
#include "otaQueue.h"
#include "globals.h"
#include "Bridge_OTA.h"
#include "gui_proc.h"
#include "gui_link.h"
#include "Bridge_Decoder.h"

// Queue-Handling
uint8_t otaTotalCount = 0;
uint8_t otaFinishedCount = 0;
bool httpServerRunning = false;
OtaState otaState = OTA_IDLE;

bool firmwareReady = false;
size_t firmwareSizeOnFs = 0;

unsigned long otaTimer = 0;
// Der Decoder braucht bis zu 15 s für die WLAN-Verbindung, dazu Download und Neustart.
// Mit 15 s lief die Bridge oft schon zum nächsten Decoder, während der erste noch lud.
const unsigned long OTA_TIMEOUT_MS = 60000;

std::vector<OtaEntry> otaQueue;
OtaEntry currentOta;

bool otaSuccessReceived = false;
static uint8_t otaFailReason = CG2_OTA_FAIL_NONE; // Fehlergrund aus dem REJOIN
static bool otaFailReceived = false;
static unsigned long otaRetryAtMs = 0;
static const unsigned long OTA_RETRY_DELAY_MS = 5000; // Decoder nach dem Neustart erst ankommen lassen
static const unsigned long OTA_NEXT_DELAY_MS = 2000;  // Pause zwischen zwei Decodern beim Serien-OTA
static const uint8_t OTA_MAX_ATTEMPTS = 2;

// Ein Decoder startet nach dem Download neu, ohne sich vom WLAN der Bridge abzumelden.
// Die Bridge führt ihn dann noch minutenlang als verbunden. Vor dem nächsten Decoder
// (und vor einer Wiederholung) werden deshalb alle Geräte aus dem WLAN abgemeldet;
// beim Serien-OTA kam der zweite Decoder sonst nicht ins WLAN.
static void clearApStations()
{
    esp_err_t r = esp_wifi_deauth_sta(0); // 0 = alle
    Serial.printf("OTA: WLAN-Geräte abgemeldet (%s)\r\n", esp_err_to_name(r));
}

// Verbindungsfehler lohnen eine Wiederholung, eine ungültige Datei nicht
static bool otaRetryWorthwhile(uint8_t reason)
{
    return reason == CG2_OTA_FAIL_WIFI || reason == CG2_OTA_FAIL_HTTP || reason == CG2_OTA_FAIL_DOWNLOAD;
}

void otaDecoderFinished(uint8_t decoderId, uint8_t failReason)
{
    // Nur der Decoder, der gerade aktualisiert wird, beendet den OTA-Schritt.
    // Sonst könnte ein verspäteter REJOIN den nächsten Decoder als fertig markieren.
    if (otaState != OTA_WAIT_FOR_FINISH || decoderId != currentOta.decoderId)
        return;
    if (failReason == CG2_OTA_FAIL_NONE)
        otaSuccessReceived = true;
    else
    {
        otaFailReason = failReason;
        otaFailReceived = true;
    }
}

volatile uint8_t otaProgressPercent = 0;
volatile bool otaProgressPending = false;
static uint8_t lastSentPercent = 255;

// Download-Fortschritt an die GUI melden - nur aus loop() und nur bei Änderung
static void sendOtaProgressIfChanged()
{
    if (!otaProgressPending)
        return;
    otaProgressPending = false;

    uint8_t percent = otaProgressPercent;
    if (percent == lastSentPercent)
        return;
    lastSentPercent = percent;

    uint8_t overall = (otaTotalCount > 0)
                          ? (uint8_t)(((otaFinishedCount * 100.0f) + percent) / otaTotalCount)
                          : percent;
    cg2SendOtaProgress(currentOta.decoderId, percent, overall);
}

// Queue anhand des Decoder‑Typs erzeugen
void startGroupOtaForType(uint8_t targetType)
{
    otaQueue.clear();

    for (size_t i = 0; i < decoders.size(); i++)
    {
        DecoderInfo &d = decoders[i];

        // nur angemeldete Decoder: die Liste enthält auch Decoder, die gerade nicht laufen
        if (d.decoderType == targetType && d.isReady)
        {
            OtaEntry e;
            e.decoderId = d.assignedId;
            memcpy(e.mac, d.Decoder_Mac, 6);
            otaQueue.push_back(e);
        }
    }
}

// OTA‑Zustandsmaschine (läuft in loop() der Bridge)
void processOta()
{
    sendOtaProgressIfChanged();

    switch (otaState)
    {
    case OTA_IDLE:
        break;

    case OTA_WAIT_FOR_FIRMWARE:
        if (firmwareReady)
        {
            Serial.println("FW: Firmware jetzt bereit, starte OTA");
            otaState = OTA_START;
        }
        break;

    case OTA_START:
        if (otaQueue.size() == 0)
        {
            otaState = OTA_DONE;
            break;
        }
        if ((long)(millis() - otaRetryAtMs) < 0)
            break; // Wiederholung: kurz warten

        otaQueue[0].attempts++;
        currentOta = otaQueue[0];

        if (!httpServerRunning)
        {
            startOtaHttpServer();
            httpServerRunning = true;
        }

        otaSuccessReceived = false;
        otaFailReceived = false;
        lastSentPercent = 255;
        sendOtaStartWifiToDecoder(currentOta.decoderId);
        // Fortschritt 0 % kündigt der GUI den nächsten Decoder an
        cg2SendOtaProgress(currentOta.decoderId, 0,
                           otaTotalCount > 0 ? (uint8_t)(otaFinishedCount * 100 / otaTotalCount) : 0);

        otaTimer = millis();
        Serial.printf("Started OTA for decoder ID %d\r\n", currentOta.decoderId);

        otaState = OTA_WAIT_FOR_FINISH;
        break;

    case OTA_WAIT_FOR_FINISH:

        if (otaSuccessReceived)
        {
            Serial.printf("OTA SUCCESS for decoder ID %d\r\n", currentOta.decoderId);
            cg2SendOtaResult(currentOta.decoderId, OTA_RESULT_OK, CG2_OTA_FAIL_NONE);
            otaSuccessReceived = false;
            otaState = OTA_NEXT;
            break;
        }

        if (otaFailReceived)
        {
            otaFailReceived = false;
            Serial.printf("OTA FAILED for decoder ID %d, reason %d, attempt %d\r\n",
                          currentOta.decoderId, otaFailReason, currentOta.attempts);
            if (otaRetryWorthwhile(otaFailReason) && currentOta.attempts < OTA_MAX_ATTEMPTS)
            {
                // Der Decoder läuft mit der alten Firmware weiter: noch einmal versuchen
                clearApStations();
                otaRetryAtMs = millis() + OTA_RETRY_DELAY_MS;
                otaState = OTA_START;
            }
            else
            {
                cg2SendOtaResult(currentOta.decoderId, OTA_RESULT_REJECTED, otaFailReason);
                otaState = OTA_NEXT;
            }
            break;
        }

        if (millis() - otaTimer > OTA_TIMEOUT_MS)
        {
            Serial.printf("OTA TIMEOUT for decoder ID %d\r\n", currentOta.decoderId);
            cg2SendOtaResult(currentOta.decoderId, OTA_RESULT_TIMEOUT, CG2_OTA_FAIL_NONE);
            otaState = OTA_NEXT;
        }
        break;

    case OTA_NEXT:
        otaQueue.erase(otaQueue.begin());
        otaFinishedCount++;

        if (otaQueue.size() == 0)
        {
            Serial.println("All OTA updates finished");
            otaState = OTA_DONE;
        }
        else
        {
            // Den fertigen Decoder aus dem WLAN der Bridge abmelden und kurz warten,
            // bevor der nächste sich verbindet (siehe clearApStations)
            clearApStations();
            otaRetryAtMs = millis() + OTA_NEXT_DELAY_MS;
            otaState = OTA_START;
            Serial.printf("OTA finished for decoder ID %d, moving to next\r\n", currentOta.decoderId);
        }
        break;

    case OTA_DONE:
        // Gesamtfortschritt 100 % = alle Decoder bearbeitet (auch wenn einer fehlschlug);
        // die GUI blendet daraufhin die Fortschrittsanzeige aus
        cg2SendOtaProgress(currentOta.decoderId, lastSentPercent == 255 ? 0 : lastSentPercent, 100);
        // Den Webserver NICHT beenden: er liefert auch lokomotive.cs2 an WDP, und
        // otaServer.end() ohne erneutes begin() machte jedes weitere OTA unmöglich.
        // Die Route für firmware.bin bleibt registriert (httpServerRunning bleibt true).
        otaState = OTA_IDLE;
        break;
    }
}

void onUploadFinished(size_t size)
{
    firmwareReady = true;
    firmwareSizeOnFs = size;

    Serial.printf("FW: Upload finished, size=%u bytes\n", size);

    if (otaState == OTA_WAIT_FOR_FIRMWARE)
    {
        Serial.println("FW: Upload fertig → OTA_START");
        otaState = OTA_START;
    }
}
