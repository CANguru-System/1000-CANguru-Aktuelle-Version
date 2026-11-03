// Nachrichten zwischen Decoder und Bridge im CANguru-Protokoll Version 2
// (0400-Protocol/PROTOKOLL.md).
//
// Der ESP-NOW-Empfang läuft im WLAN-Task. Dort werden Nachrichten nur in eine
// Warteschlange gelegt; verarbeitet werden sie in loop(). So laufen NVS-Zugriffe,
// Stepper-Befehle und Neustarts nicht mehr im WLAN-Task.

#include <Arduino.h>
#include <esp_now.h>
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "Core_Link.h"
#include "Core_EspNow.h"
#include "Core_Alive.h"
#include "Core_Config.h"
#include "Core_Ota.h"
#include "core_globals.h"
#include "DecoderBase.h"

struct RxItem
{
    uint8_t len;
    uint8_t data[CG2_ESPNOW_MAX];
};

static QueueHandle_t rxQueue = NULL;
static uint8_t ownSeq = 0;
static bool warnedOldFormat = false;

// Wiederholte Anfrage der Bridge (unsere Antwort ging verloren): nicht noch einmal
// ausführen, sondern die gespeicherte Antwort erneut senden
static Cg2DuplicateFilter duplicates;
static uint8_t lastResponse[64];
static size_t lastResponseLen = 0;

// ------------------------------------------------------------
// Senden
// ------------------------------------------------------------

static void sendRaw(const uint8_t *buf, size_t len)
{
    if (!espNowReady || !bridge_is_known)
        return;
    esp_err_t s = esp_now_send(Bridge_Mac, buf, len);
    if (s != ESP_OK)
        Serial.printf("DECODER: esp_now_send failed: %d\r\n", s);
}

void coreLinkSend(uint8_t feature, uint8_t command, const uint8_t *payload, size_t len)
{
    Cg2Header h;
    h.feature = feature;
    h.command = command;
    h.seq = ownSeq++;
    h.decoder = aliveGetDecoderID();
    uint8_t buf[CG2_ESPNOW_MAX];
    size_t n = cg2Encode(buf, sizeof(buf), h, payload, len);
    if (n > 0)
        sendRaw(buf, n);
}

void coreLinkReply(const Cg2Header &request, const uint8_t *payload, size_t len, uint8_t errorCode)
{
    Cg2Header r = cg2ResponseTo(request, aliveGetDecoderID());
    uint8_t err[1] = {errorCode};
    if (errorCode != 0)
    {
        r.flags |= CG2_FLAG_ERROR;
        payload = err;
        len = 1;
    }
    uint8_t buf[CG2_ESPNOW_MAX];
    size_t n = cg2Encode(buf, sizeof(buf), r, payload, len);
    if (n == 0)
        return;
    if (n <= sizeof(lastResponse))
    {
        memcpy(lastResponse, buf, n);
        lastResponseLen = n;
    }
    sendRaw(buf, n);
}

// HELLO: [typ][mac 6][fwMajor][fwMinor][protoMinor]
void coreLinkSendHello()
{
    if (otaInProgress)
        return;
    uint8_t p[10];
    Cg2Writer w(p, sizeof(p));
    w.u8(DEVTYPE).bytes(Decoder_Mac, 6).u8(VERS_HIGH).u8(VERS_LOW).u8(CG2_PROTO_MINOR);
    Serial.printf("Sending HELLO (ID=%d)\r\n", aliveGetDecoderID());
    coreLinkSend(CG2_F_DECODER, CG2_DECODER_HELLO, p, w.size());
}

// REJOIN nach einem OTA-Neustart: [otaFehlgeschlagen 0/1], ID in Byte 5
void coreLinkSendRejoin()
{
    bool otaFailed = prefs.getBool(key_otaFailed, false);
    if (otaFailed)
        prefs.putBool(key_otaFailed, false);

    uint8_t p[1] = {(uint8_t)(otaFailed ? 1 : 0)};
    Serial.printf("Sending REJOIN with ID=%d, OTA %s\r\n", aliveGetDecoderID(), otaFailed ? "fehlgeschlagen" : "ok");
    coreLinkSend(CG2_F_DECODER, CG2_DECODER_REJOIN, p, sizeof(p));
}

// ALIVE: [status][anzahl][wert u16] x anzahl
void coreLinkSendAlive(uint8_t status, const uint16_t *values, uint8_t count)
{
    uint8_t p[2 + 2 * 16];
    Cg2Writer w(p, sizeof(p));
    w.u8(status).u8(count);
    for (uint8_t i = 0; i < count; i++)
        w.u16(values[i]);
    if (w.ok())
        coreLinkSend(CG2_F_ALIVE, CG2_ALIVE_ALIVE, p, w.size());
}

// ------------------------------------------------------------
// Empfang
// ------------------------------------------------------------

void coreLinkEnqueue(const uint8_t *data, int len)
{
    if (rxQueue == NULL)
        rxQueue = xQueueCreate(16, sizeof(RxItem));
    if (rxQueue == NULL || len <= 0 || len > CG2_ESPNOW_MAX)
        return;
    RxItem item;
    item.len = (uint8_t)len;
    memcpy(item.data, data, len);
    xQueueSend(rxQueue, &item, 0); // nicht blockieren: bei voller Warteschlange verwerfen
}

// Ist diese Nachricht für mich?
static bool isForMe(const Cg2Header &h)
{
    switch (h.addrMode())
    {
    case CG2_ADDR_ALL:
        return true;
    case CG2_ADDR_TYPE:
        return h.decoder == DEVTYPE;
    case CG2_ADDR_ID:
        // Die ID-Vergabe kommt an die MAC; die Prüfung erfolgt anhand der Nutzdaten
        if (h.feature == CG2_F_DECODER && h.command == CG2_DECODER_ASSIGN_ID)
            return true;
        return h.decoder == aliveGetDecoderID() && h.decoder != INVALID_ASSIGNED_ID;
    }
    return false;
}

// ID-Vergabe: [mac 6][id]
static void handleAssignId(const Cg2Header &h, const uint8_t *payload)
{
    Cg2Reader r(payload, h.length);
    uint8_t mac[6];
    if (!r.bytes(mac, 6))
        return;
    uint8_t id = r.u8();
    if (!r.ok() || memcmp(mac, Decoder_Mac, 6) != 0)
        return; // nicht meine MAC

    bool neu = (id != aliveGetDecoderID());
    aliveSetDecoderID(id);
    // ungültige ID speichern: nach einem Neustart beginnt die Anmeldung von vorn
    prefs.putByte(key_id, INVALID_ASSIGNED_ID);
    prefs.putBool(key_otaPending, false);
    coreLinkReply(h, nullptr, 0, 0);

    if (neu)
    {
        Serial.printf("Assigned ID %d\r\n", id);
        generateHash();
        if (afterstartCallback != nullptr)
            afterstartCallback(); // decoderspezifische Aktionen
    }
}

static void handleCanFrame(const Cg2Header &h, const uint8_t *payload)
{
    if (h.command != CG2_CAN_TO_DECODERS || h.length < CAN_FRAME_SIZE)
        return;
    switch (payload[CANcmd])
    {
    case PING:
        sendPING();
        return;
    case CONFIG_Status:
        sendConfig();
        return;
    }
    gDecoder->onCanFrame(payload);
}

static void restartDecoder()
{
    if (beforeRestartCallback != nullptr)
        beforeRestartCallback(); // decoderspezifische Aktionen
    prefs.putByte(key_id, INVALID_ASSIGNED_ID);
    coreConfigClearOtaPending();
    delay(500);
    ESP.restart();
}

static void dispatch(const uint8_t *buf, size_t len)
{
    Cg2Header h;
    const uint8_t *payload = nullptr;
    Cg2DecodeResult res = cg2Decode(buf, len, h, payload);
    if (res != Cg2DecodeResult::Ok)
    {
        if (res == Cg2DecodeResult::WrongMagic && !warnedOldFormat)
        {
            warnedOldFormat = true;
            Serial.println("DECODER: Nachricht im alten Format ignoriert - Bridge-Firmware zu alt?");
        }
        return;
    }
    if (h.isResponse() || !isForMe(h))
        return;

    if (h.ackRequested() && duplicates.isDuplicate(CG2_ID_BRIDGE, h.seq))
    {
        if (lastResponseLen > 0 && lastResponse[4] == h.seq)
            sendRaw(lastResponse, lastResponseLen);
        return;
    }

    switch (h.feature)
    {
    case CG2_F_SESSION:
        if (h.command == CG2_SESSION_START)
            coreLinkSendHello();
        return;

    case CG2_F_DECODER:
        if (h.command == CG2_DECODER_ASSIGN_ID)
        {
            handleAssignId(h, payload);
            return;
        }
        if (h.command == CG2_DECODER_RESET)
        {
            if (h.ackRequested())
                coreLinkReply(h, nullptr, 0, 0);
            restartDecoder();
            return;
        }
        break;

    case CG2_F_CAN:
        handleCanFrame(h, payload);
        return;

    case CG2_F_OTA:
        if (h.command == CG2_OTA_DECODER_START)
        {
            coreLinkReply(h, nullptr, 0, 0);
            coreOtaRequest();
            return;
        }
        break;

    case CG2_F_PARAM:
        if (h.command == CG2_PARAM_FACTORY_RESET)
        {
            bool ok = gDecoder->factoryReset();
            coreLinkReply(h, nullptr, 0, ok ? 0 : CG2_ERR_UNKNOWN_COMMAND);
            return;
        }
        break;
    }

    // Typeigene Nachrichten (Stepper, Rückmelder, Strom)
    if (gDecoder->onMessage(h, payload, h.length))
        return;

    if (h.ackRequested())
        coreLinkReply(h, nullptr, 0, CG2_ERR_UNKNOWN_COMMAND);
}

void coreLinkLoop()
{
    if (rxQueue == NULL || gDecoder == nullptr)
        return;
    RxItem item;
    while (xQueueReceive(rxQueue, &item, 0) == pdTRUE)
        dispatch(item.data, item.len);
}
