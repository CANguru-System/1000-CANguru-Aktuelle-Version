#include <Arduino.h>
#include <vector>
#include <esp_now.h>
#include <LittleFS.h>
#include "otaQueue.h"
#include "globals.h"
#include "Bridge_OTA.h"
#include "gui_proc.h"
#include "Bridge_Decoder.h"

// Queue-Handling
File fwFile;
uint8_t otaTotalCount = 0;
uint8_t otaFinishedCount = 0;
uint8_t currentPercent = 0;
unsigned long lastAckSend = 0;
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

    sendGuiOtaProgress(currentOta.decoderId, percent);
    if (otaTotalCount > 0)
    {
        uint8_t overall = (uint8_t)(((otaFinishedCount * 100.0f) + percent) / otaTotalCount);
        sendGuiOtaOverallProgress(overall);
    }
}

uint32_t otaExpectedSize = 0;
uint32_t otaReceivedSize = 0;

unsigned long otaTimeoutStart = 0;
const unsigned long OTA_TIMEOUT = 15000; // 15 Sekunden
uint16_t expectedSeq = 0;

// Queue anhand des Decoder‑Typs erzeugen
void startGroupOtaForType(uint8_t targetType)
{
    otaQueue.clear();

    for (size_t i = 0; i < decoders.size(); i++)
    {
        DecoderInfo &d = decoders[i];

        if (d.decoderType == targetType)
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

        currentOta = otaQueue[0];

        if (!httpServerRunning)
        {
            startOtaHttpServer();
            httpServerRunning = true;
        }

        otaSuccessReceived = false;
        lastSentPercent = 255;
        sendOtaStartWifiToDecoder(currentOta.decoderId, 0, 0);
        sendGuiOtaCurrentDecoder(currentOta.decoderId);

        otaTimer = millis();
        Serial.printf("Started OTA for decoder ID %d\r\n", currentOta.decoderId);

        otaState = OTA_WAIT_FOR_FINISH;
        break;

    case OTA_WAIT_FOR_FINISH:

        if (otaSuccessReceived)
        {
            Serial.printf("OTA SUCCESS for decoder ID %d\r\n", currentOta.decoderId);
            sendGuiOtaResult(currentOta.decoderId, OTA_RESULT_OK);
            otaSuccessReceived = false;
            otaState = OTA_NEXT;
            break;
        }

        if (millis() - otaTimer > OTA_TIMEOUT_MS)
        {
            Serial.printf("OTA TIMEOUT for decoder ID %d\r\n", currentOta.decoderId);
            sendGuiOtaResult(currentOta.decoderId, OTA_RESULT_TIMEOUT);
            otaState = OTA_NEXT;
        }
        break;

    case OTA_NEXT:
        Serial.printf("Moving to next OTA after success for decoder ID %d\r\n", currentOta.decoderId);

        otaQueue.erase(otaQueue.begin());

        if (otaQueue.size() == 0)
        {
            Serial.println("All OTA updates finished");
            otaState = OTA_DONE;
        }
        else
        {
            otaFinishedCount++;
            otaState = OTA_START;
            Serial.printf("OTA finished for decoder ID %d, moving to next\r\n", currentOta.decoderId);
        }
        break;

    case OTA_DONE:
        // Den Webserver NICHT beenden: er liefert auch lokomotive.cs2 an WDP, und
        // otaServer.end() ohne erneutes begin() machte jedes weitere OTA unmöglich.
        // Die Route für firmware.bin bleibt registriert (httpServerRunning bleibt true).
        otaState = OTA_IDLE;
        break;
    }
}

void processFirmwareQueue()
{
    if (fwQueue.empty())
        return;

    FirmwarePacket p = fwQueue.front();
    fwQueue.pop();

    File f = LittleFS.open(FW_PATH, "a");
    if (f)
    {
        f.write(p.data, p.len);
        f.close();
        firmwareSizeOnFs += p.len;
    }
    //    Serial.printf("Received firmware total: %d bytes\r\n", firmwareSizeOnFs);
}

void processAckQueue()
{
    if (ackQueue.empty())
        return;

    // alle 2–5 ms ein ACK senden
    if (millis() - lastAckSend < 3)
        return;

    AckPacket a = ackQueue.front();
    ackQueue.pop();

    sendOtaAck(a.seq, a.status);

    lastAckSend = millis();
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
