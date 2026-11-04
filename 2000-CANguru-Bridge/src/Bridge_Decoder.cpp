// Bridge <-> Decoder über ESP-NOW (CANguru-Protokoll Version 2, 0400-Protocol/PROTOKOLL.md)
// Beschreibung der Aufgaben: Bridge_Decoder.h

#include <Arduino.h>
#include <WiFi.h>
#include <esp_wifi.h>
#include <esp_now.h>
#include <vector>
#include <array>
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "protocol_constants_core.h"
#include "globals.h"
#include "alive.h"
#include "Bridge_Decoder.h"
#include "Bridge_OTA.h"
#include "otaQueue.h"
#include "gui_proc.h"
#include "gui_link.h"
#include "CAN_proc.h"
#include "wdp_proc.h"
#include "PacketHandler.h"

bool otaRunning = false;

const char *OTA_SSID = "BridgeOTA";
const char *OTA_PASS = "";

static uint8_t bridgeSeq = 0;

// ============================================================
// Altes Format (Übergang für Decoder vor Protokoll Version 2)
// [SendMode][Selector][Feature][Kommando][Nutzdaten]
// ============================================================

enum class SendMode : uint8_t
{
    GLOBAL,      // an alle Decoder
    TYPE,        // an alle Decoder eines Typs
    ASSIGNED_ID, // an genau einen Decoder
    MAC_DIRECT   // nur für ID_ASSIGN
};

static void legacySend(SendMode mode, uint8_t selector, uint8_t feature, uint8_t command,
                       const uint8_t *payload, size_t len, const uint8_t *mac = Bridge_Mac)
{
    uint8_t buffer[ESP_NOW_MAX_DATA_LEN];
    if (len + 4 > sizeof(buffer))
        return;
    buffer[0] = (uint8_t)mode;
    buffer[1] = selector;
    buffer[2] = feature;
    buffer[3] = command;
    if (len > 0)
        memcpy(&buffer[4], payload, len);
    esp_now_send(mac, buffer, len + 4);
}

// ID-Vergabe an alte Decoder: [id][typ][mac 6], bis zu 5 Versuche im 40-ms-Abstand
struct LegacyAssign
{
    uint8_t mac[6];
    uint8_t id;
    uint8_t type;
    uint8_t retriesLeft;
    unsigned long nextMs;
};
static std::vector<LegacyAssign> legacyAssigns;

// ============================================================
// Unicast-Partner (ESP-NOW erlaubt höchstens 20 gleichzeitig)
// ============================================================

static std::vector<std::array<uint8_t, 6>> peerOrder; // zuletzt benutzt am Ende

static bool ensurePeer(const uint8_t *mac)
{
    for (size_t i = 0; i < peerOrder.size(); i++)
    {
        if (memcmp(peerOrder[i].data(), mac, 6) == 0)
        {
            auto p = peerOrder[i];
            peerOrder.erase(peerOrder.begin() + i);
            peerOrder.push_back(p);
            return true;
        }
    }

    esp_now_peer_info_t peer{};
    memcpy(peer.peer_addr, mac, 6);
    peer.channel = BRIDGE_AP_CHANNEL;
    peer.ifidx = WIFI_IF_STA;
    peer.encrypt = false;

    esp_err_t r = esp_now_add_peer(&peer);
    if (r == ESP_ERR_ESPNOW_FULL && !peerOrder.empty())
    {
        // ältesten Partner entfernen und erneut versuchen
        esp_now_del_peer(peerOrder.front().data());
        peerOrder.erase(peerOrder.begin());
        r = esp_now_add_peer(&peer);
    }
    if (r != ESP_OK && r != ESP_ERR_ESPNOW_EXIST)
    {
        Serial.printf("BRIDGE: add peer failed: %s\r\n", esp_err_to_name(r));
        return false;
    }
    std::array<uint8_t, 6> p;
    memcpy(p.data(), mac, 6);
    peerOrder.push_back(p);
    return true;
}

// ============================================================
// Senden im Format Version 2
// ============================================================

static void sendUnicast(const uint8_t *mac, const uint8_t *buf, size_t len)
{
    if (ensurePeer(mac))
        esp_now_send(mac, buf, len);
}

static void sendV2(const uint8_t *mac, uint8_t feature, uint8_t command, uint8_t addrMode,
                   uint8_t decoder, uint8_t flags, uint8_t seq, const uint8_t *payload, size_t len)
{
    Cg2Header h;
    h.feature = feature;
    h.command = command;
    h.flags = flags;
    h.setAddrMode(addrMode);
    h.seq = seq;
    h.decoder = decoder;
    uint8_t buf[CG2_ESPNOW_MAX];
    size_t n = cg2Encode(buf, sizeof(buf), h, payload, len);
    if (n == 0)
        return;
    if (addrMode == CG2_ADDR_ID)
        sendUnicast(mac, buf, n);
    else
        esp_now_send(Bridge_Mac, buf, n); // Broadcast
}

static void broadcastV2(uint8_t feature, uint8_t command, uint8_t addrMode, uint8_t selector,
                        const uint8_t *payload, size_t len)
{
    sendV2(Bridge_Mac, feature, command, addrMode, selector, 0, bridgeSeq++, payload, len);
}

// Eigene Anfragen der Bridge an einen Decoder (ID-Vergabe, OTA-Start) mit Wiederholung
struct PendingRequest
{
    uint8_t mac[6];
    uint8_t decoderId;
    uint8_t feature;
    uint8_t command;
    uint8_t seq;
    uint8_t payload[16];
    uint8_t len;
    uint8_t retriesLeft;
    unsigned long nextMs;
};
static std::vector<PendingRequest> pendingRequests;

static void startRequest(const uint8_t *mac, uint8_t decoderId, uint8_t feature, uint8_t command,
                         const uint8_t *payload, uint8_t len)
{
    // eine gleiche, noch offene Anfrage an denselben Decoder ersetzen
    for (size_t i = 0; i < pendingRequests.size(); i++)
        if (memcmp(pendingRequests[i].mac, mac, 6) == 0 && pendingRequests[i].feature == feature &&
            pendingRequests[i].command == command)
        {
            pendingRequests.erase(pendingRequests.begin() + i);
            break;
        }

    PendingRequest r;
    memcpy(r.mac, mac, 6);
    r.decoderId = decoderId;
    r.feature = feature;
    r.command = command;
    r.seq = bridgeSeq++;
    r.len = len > sizeof(r.payload) ? sizeof(r.payload) : len;
    memcpy(r.payload, payload, r.len);
    r.retriesLeft = CG2_RETRY_COUNT;
    r.nextMs = millis() + CG2_RETRY_MS;
    pendingRequests.push_back(r);

    sendV2(mac, feature, command, CG2_ADDR_ID, decoderId, CG2_FLAG_ACK_REQ, r.seq, r.payload, r.len);
}

// Letzte an einen Decoder weitergeleitete GUI-Anfrage mit Quittung: scheitert die
// Zustellung auf Funkebene, meldet die Bridge der GUI CG2_ERR_NOT_DELIVERED.
static struct
{
    bool active = false;
    uint8_t mac[6];
    Cg2Header h;
} lastForward;

static QueueHandle_t sendFailQueue = NULL; // MACs fehlgeschlagener Sendungen (aus dem WLAN-Task)

// ============================================================
// Decoder in der GUI bekannt machen
// ============================================================

// ADDED: [id][typ][mac 6][fwMajor][fwMinor][protoMinor][alteFirmware 0/1]
void sendDecoderAddedToGUI(const DecoderInfo &d)
{
    uint8_t p[12];
    Cg2Writer w(p, sizeof(p));
    w.u8(d.assignedId).u8(d.decoderType).bytes(d.Decoder_Mac, 6)
        .u8(d.fwMajor).u8(d.fwMinor).u8(d.protoMinor).u8(d.legacy ? 1 : 0);
    cg2SendToGui(CG2_F_DECODER, CG2_DECODER_ADDED, d.assignedId, p, w.size());
}

static void decoderReady(DecoderInfo &d)
{
    d.isReady = true;
    d.lastSeen = millis();
    sendDecoderAddedToGUI(d);
    Serial.printf("Decoder ID %d (type %02X%s) ready\r\n", d.assignedId, d.decoderType, d.legacy ? ", alte Firmware" : "");
}

// Nachricht eines Decoders unverändert an die GUI; Byte 5 = ID aus der Decoder-Liste
static void forwardToGui(const uint8_t *buf, size_t len, uint8_t decoderId)
{
    uint8_t copy[CG2_ESPNOW_MAX];
    if (len > sizeof(copy))
        return;
    memcpy(copy, buf, len);
    copy[5] = decoderId;
    udpSendToGui(copy, len);
}

// REJOIN nach einem OTA-Neustart (Version 2 und alte Decoder)
static void handleRejoin(DecoderInfo &d, bool otaFailed)
{
    Serial.printf("Decoder with ID %d has REJOINED%s\r\n", d.assignedId, otaFailed ? " after FAILED OTA" : "");
    otaRunning = false;
    d.isReady = true;
    d.lastSeen = millis();

    // Nur der Decoder, der gerade aktualisiert wird, beendet den OTA-Schritt.
    // Sonst könnte ein verspäteter REJOIN den nächsten Decoder als fertig markieren.
    if (otaState == OTA_WAIT_FOR_FINISH && d.assignedId == currentOta.decoderId)
    {
        if (otaFailed)
        {
            Serial.printf("OTA FAILED for decoder ID %d, firmware unchanged\r\n", d.assignedId);
            cg2SendOtaResult(d.assignedId, OTA_RESULT_REJECTED);
            otaState = OTA_NEXT;
        }
        else
            otaSuccessReceived = true;
    }
}

// ============================================================
// Empfang: Decoder mit Protokoll Version 2
// ============================================================

static void handleV2(const uint8_t *mac, const uint8_t *buf, size_t len)
{
    Cg2Header h;
    const uint8_t *payload = nullptr;
    if (cg2Decode(buf, len, h, payload) != Cg2DecodeResult::Ok)
        return;

    // Anmeldung: HELLO [typ][mac 6][fwMajor][fwMinor][protoMinor]
    if (h.feature == CG2_F_DECODER && h.command == CG2_DECODER_HELLO && !h.isResponse())
    {
        Cg2Reader r(payload, h.length);
        uint8_t type = r.u8();
        uint8_t helloMac[6];
        r.bytes(helloMac, 6);
        uint8_t fwMajor = r.u8(), fwMinor = r.u8(), protoMinor = r.u8();
        if (!r.ok())
            return;

        int idx = registerDecoder(mac, type, false);
        DecoderInfo &d = decoders[idx];
        d.fwMajor = fwMajor;
        d.fwMinor = fwMinor;
        d.protoMinor = protoMinor;
        d.isReady = false;

        uint8_t p[7];
        memcpy(p, mac, 6);
        p[6] = d.assignedId;
        startRequest(mac, d.assignedId, CG2_F_DECODER, CG2_DECODER_ASSIGN_ID, p, sizeof(p));
        Serial.printf("HELLO from type %02X, assigning ID %d\r\n", type, d.assignedId);
        return;
    }

    int idx = findDecoderByMac(mac);
    if (idx < 0)
        return; // unbekannter Decoder: wartet auf START und meldet sich dann mit HELLO
    DecoderInfo &d = decoders[idx];
    d.lastSeen = millis();

    if (h.isResponse())
    {
        // Antwort auf eine eigene Anfrage der Bridge?
        for (size_t i = 0; i < pendingRequests.size(); i++)
        {
            PendingRequest &pr = pendingRequests[i];
            if (memcmp(pr.mac, mac, 6) == 0 && pr.feature == h.feature && pr.command == h.command && pr.seq == h.seq)
            {
                pendingRequests.erase(pendingRequests.begin() + i);
                if (h.feature == CG2_F_DECODER && h.command == CG2_DECODER_ASSIGN_ID && !h.isError())
                    decoderReady(d);
                return;
            }
        }
        // sonst: Antwort auf eine Anfrage der GUI
        if (lastForward.active && memcmp(lastForward.mac, mac, 6) == 0 && lastForward.h.seq == h.seq)
            lastForward.active = false;
        forwardToGui(buf, len, d.assignedId);
        return;
    }

    switch (h.feature)
    {
    case CG2_F_DECODER:
        if (h.command == CG2_DECODER_REJOIN)
        {
            handleRejoin(d, h.length >= 1 && payload[0] != 0);
            return;
        }
        break;

    case CG2_F_ALIVE:
        if (h.command == CG2_ALIVE_ALIVE && h.length >= 1)
        {
            // Bereit = Status ungleich Fehler (z. B. nach einer Überstrom-Abschaltung nicht bereit)
            bool wasReady = d.isReady;
            d.isReady = (payload[0] != CG2_STATUS_ERROR);
            // Decoder, der nach einem Neustart der Bridge einfach weiterläuft, der GUI melden
            if (!wasReady && d.isReady)
                sendDecoderAddedToGUI(d);
            forwardToGui(buf, len, d.assignedId);
        }
        return;

    case CG2_F_CAN:
        if (h.command == CG2_CAN_FROM_DECODER && h.length >= CAN_FRAME_SIZE)
        {
            sendToWDP(payload);
            enqueueCanToFifo(payload, 'D');
        }
        return;

    case CG2_F_POWER:
        if (h.command == CG2_POWER_SHUTDOWN)
        {
            // Nothalt an die Gleisbox, damit auch WDP und alle Loks den Halt mitbekommen.
            // Beim Senden meldet processCANFrames2CAN() den STOPP an GUI und Stromdecoder.
            enqueueCANFrame2CAN(SysSTOPP, hashEnabled);
        }
        break;
    }

    // alle übrigen Meldungen der Decoder gehen unverändert an die GUI
    forwardToGui(buf, len, d.assignedId);
}

// ============================================================
// Empfang: alte Decoder (Übergang) - nur Anmeldung, Alive, REJOIN, CAN, Überstrom
// Format: [Feature][Kommando][Nutzdaten]
// ============================================================

static void handleLegacy(const uint8_t *mac, const uint8_t *data, size_t len)
{
    if (len < 2)
        return;
    uint8_t feature = data[0], command = data[1];
    const uint8_t *p = data + 2;
    size_t plen = len - 2;

    if (feature == FEATURE_ALIVE && command == FRAME_DECODER_HELLO && plen >= 2)
    {
        // [id][typ][mac 6]
        int idx = registerDecoder(mac, p[1], true);
        DecoderInfo &d = decoders[idx];
        d.fwMajor = d.fwMinor = d.protoMinor = 0;
        d.isReady = false;

        LegacyAssign a;
        memcpy(a.mac, mac, 6);
        a.id = d.assignedId;
        a.type = d.decoderType;
        a.retriesLeft = 5;
        a.nextMs = millis();
        legacyAssigns.push_back(a);
        ensurePeer(mac);
        Serial.printf("HELLO (alte Firmware) from type %02X, assigning ID %d\r\n", d.decoderType, d.assignedId);
        return;
    }

    int idx = findDecoderByMac(mac);
    if (idx < 0)
        return;
    DecoderInfo &d = decoders[idx];
    d.lastSeen = millis();

    if (feature == FEATURE_ALIVE && command == FRAME_DECODER_ID_ACK)
    {
        for (size_t i = 0; i < legacyAssigns.size(); i++)
            if (memcmp(legacyAssigns[i].mac, mac, 6) == 0)
            {
                legacyAssigns.erase(legacyAssigns.begin() + i);
                decoderReady(d);
                break;
            }
        return;
    }

    if (feature == FEATURE_ALIVE && command == FRAME_DECODER_ALIVE && plen >= 5)
    {
        // [id][typ][status][fwHigh][fwLow][4 Byte Merkmale] -> ALIVE im Format Version 2
        d.isReady = (p[2] != 0);
        d.fwMajor = p[3];
        d.fwMinor = p[4];
        uint8_t a[2 + 4];
        Cg2Writer w(a, sizeof(a));
        w.u8(p[2] ? CG2_STATUS_OK : CG2_STATUS_ERROR);
        if (d.decoderType == DEVTYPE_POWER && plen >= 9)
            w.u8(2).u16((p[5] << 8) | p[6]).u16((p[7] << 8) | p[8]);
        else
            w.u8(0);
        cg2SendToGui(CG2_F_ALIVE, CG2_ALIVE_ALIVE, d.assignedId, a, w.size());
        return;
    }

    if (feature == FEATURE_DECODER && command == FRAME_DECODER_REJOIN)
    {
        handleRejoin(d, plen >= 2 && p[1] != 0);
        return;
    }

    if (feature == FEATURE_CS2 && command == FRAME_CAN_2BRIDGE && plen >= CAN_FRAME_SIZE)
    {
        sendToWDP(p);
        enqueueCanToFifo(p, 'D');
        return;
    }

    if (feature == FEATURE_POWER && command == POWER_CMD_SHUTDOWN && plen >= 3)
    {
        enqueueCANFrame2CAN(SysSTOPP, hashEnabled);
        uint8_t s[3];
        Cg2Writer(s, sizeof(s)).u8(p[0]).u16((p[1] << 8) | p[2]);
        cg2SendToGui(CG2_F_POWER, CG2_POWER_SHUTDOWN, d.assignedId, s, sizeof(s));
    }
}

// ============================================================
// ESP-NOW: Empfang über eine Warteschlange, Verarbeitung in loop()
// ============================================================

// Der Empfangs-Callback läuft im WLAN-Task. Würde er die Pakete direkt verarbeiten,
// griffen zwei Tasks gleichzeitig auf UDP, die Decoder-Liste und die CAN-Puffer zu
// (zerstückelte UDP-Pakete an die GUI, sporadische Abstürze). Deshalb legt er die
// Pakete nur in eine Warteschlange; verarbeitet werden sie in loop().
struct EspNowRxItem
{
    uint8_t mac[6];
    uint8_t len;
    uint8_t data[ESP_NOW_MAX_DATA_LEN];
};

static QueueHandle_t espNowRxQueue = NULL;

static void onBridgeDataRecv(const uint8_t *mac, const uint8_t *incomingData, int len)
{
    if (espNowRxQueue == NULL || len < 2 || len > ESP_NOW_MAX_DATA_LEN)
        return;

    EspNowRxItem item;
    memcpy(item.mac, mac, 6);
    item.len = (uint8_t)len;
    memcpy(item.data, incomingData, len);
    // nicht blockieren: ist die Warteschlange voll, geht das Paket verloren
    xQueueSend(espNowRxQueue, &item, 0);
}

static void onBridgeDataSent(const uint8_t *mac, esp_now_send_status_t status)
{
    // läuft im WLAN-Task: nur merken, ausgewertet in decoderLinkLoop()
    if (status != ESP_NOW_SEND_SUCCESS && sendFailQueue != NULL && memcmp(mac, Bridge_Mac, 6) != 0)
        xQueueSend(sendFailQueue, mac, 0);
}

void processEspNowRx()
{
    if (espNowRxQueue == NULL)
        return;

    EspNowRxItem item;
    while (xQueueReceive(espNowRxQueue, &item, 0) == pdTRUE)
    {
        if (cg2IsV2(item.data, item.len))
            handleV2(item.mac, item.data, item.len);
        else
            handleLegacy(item.mac, item.data, item.len);
    }
}

void decoderLinkLoop()
{
    unsigned long now = millis();

    // eigene Anfragen wiederholen
    for (size_t i = 0; i < pendingRequests.size();)
    {
        PendingRequest &r = pendingRequests[i];
        if ((long)(now - r.nextMs) < 0)
        {
            i++;
            continue;
        }
        if (r.retriesLeft == 0)
        {
            Serial.printf("BRIDGE: decoder ID %d does not answer (feature %02X, command %02X)\r\n",
                          r.decoderId, r.feature, r.command);
            pendingRequests.erase(pendingRequests.begin() + i);
            continue;
        }
        r.retriesLeft--;
        r.nextMs = now + CG2_RETRY_MS;
        sendV2(r.mac, r.feature, r.command, CG2_ADDR_ID, r.decoderId, CG2_FLAG_ACK_REQ, r.seq, r.payload, r.len);
        i++;
    }

    // ID-Vergabe an alte Decoder wiederholen
    for (size_t i = 0; i < legacyAssigns.size();)
    {
        LegacyAssign &a = legacyAssigns[i];
        if ((long)(now - a.nextMs) < 0)
        {
            i++;
            continue;
        }
        if (a.retriesLeft == 0)
        {
            Serial.println("BRIDGE: ID_ASSIGN (alte Firmware) failed after retries");
            legacyAssigns.erase(legacyAssigns.begin() + i);
            continue;
        }
        uint8_t p[8];
        p[0] = a.id;
        p[1] = a.type;
        memcpy(&p[2], a.mac, 6);
        legacySend(SendMode::MAC_DIRECT, 0, FEATURE_ALIVE, FRAME_DECODER_ID_ASSIGN, p, sizeof(p), a.mac);
        a.retriesLeft--;
        a.nextMs = now + 40;
        i++;
    }

    // Zustellfehler weitergeleiteter GUI-Anfragen melden
    uint8_t mac[6];
    while (sendFailQueue != NULL && xQueueReceive(sendFailQueue, mac, 0) == pdTRUE)
    {
        if (lastForward.active && memcmp(lastForward.mac, mac, 6) == 0)
        {
            lastForward.active = false;
            cg2SendErrorToGui(lastForward.h, lastForward.h.decoder, CG2_ERR_NOT_DELIVERED);
        }
    }
}

// ============================================================
// GUI -> Decoder weiterleiten
// ============================================================

void forwardGuiToDecoder(const Cg2Header &h, const uint8_t *frame, size_t len)
{
    int idx = findDecoderIndexById(h.decoder);
    if (idx < 0)
    {
        if (h.ackRequested())
            cg2SendErrorToGui(h, h.decoder, CG2_ERR_UNKNOWN_DECODER);
        return;
    }
    DecoderInfo &d = decoders[idx];
    if (d.legacy)
    {
        // alte Firmware versteht Version 2 nicht: nur OTA ist möglich
        if (h.ackRequested())
            cg2SendErrorToGui(h, h.decoder, CG2_ERR_UNKNOWN_FEATURE);
        return;
    }
    if (len > CG2_ESPNOW_MAX)
    {
        if (h.ackRequested())
            cg2SendErrorToGui(h, h.decoder, CG2_ERR_BAD_LENGTH);
        return;
    }

    if (h.ackRequested())
    {
        lastForward.active = true;
        memcpy(lastForward.mac, d.Decoder_Mac, 6);
        lastForward.h = h;
    }
    sendUnicast(d.Decoder_Mac, frame, len);
}

// ============================================================
// Bridge -> Decoder
// ============================================================

bool findDecoderByAssignedId(uint8_t id, DecoderInfo &out)
{
    int idx = findDecoderIndexById(id);
    if (idx < 0)
        return false;
    out = decoders[idx];
    return true;
}

void sendStartToDecoders()
{
    broadcastV2(CG2_F_SESSION, CG2_SESSION_START, CG2_ADDR_ALL, 0, nullptr, 0);
    // alte Decoder melden sich nur auf MSG_START_DECODERS
    uint8_t p[1] = {0x00};
    legacySend(SendMode::GLOBAL, 0, FEATURE_DECODER, MSG_START_DECODERS, p, sizeof(p));
    Serial.println("Sent START to all decoders");
}

void sendResetToDecoders()
{
    broadcastV2(CG2_F_DECODER, CG2_DECODER_RESET, CG2_ADDR_ALL, 0, nullptr, 0);
    uint8_t p[1] = {0x00};
    legacySend(SendMode::GLOBAL, 0, FEATURE_DECODER, CMD_DECODER_RESET, p, sizeof(p));
    delay(50);
}

// Märklin-Frame an alle Decoder
void sendFrame2allDecoders(uint8_t *data, int len)
{
    broadcastV2(CG2_F_CAN, CG2_CAN_TO_DECODERS, CG2_ADDR_ALL, 0, data, CAN_FRAME_SIZE);
    if (hasLegacyDecoders())
        legacySend(SendMode::GLOBAL, 0, FEATURE_BRIDGE2DECODER, data[CANcmd], data, CAN_FRAME_SIZE);
    delay(50);
}

// Märklin-Frame an alle Decoder eines Typs
void sendFrame2TypeDecoders(uint8_t *data, int len, uint8_t decoderType)
{
    broadcastV2(CG2_F_CAN, CG2_CAN_TO_DECODERS, CG2_ADDR_TYPE, decoderType, data, CAN_FRAME_SIZE);
    if (hasLegacyDecoders())
        legacySend(SendMode::TYPE, decoderType, FEATURE_BRIDGE2DECODER, data[CANcmd], data, CAN_FRAME_SIZE);
    delay(50);
}

// CONFIG_Status aus WDP an den Decoder, dessen Hash im Frame steht
void sendConfigData(const uint8_t *data)
{
    uint8_t id = data[8] - uid_device[3]; // data[8] enthält die Basis-Hash
    int idx = findDecoderIndexById(id);
    if (idx < 0)
        return;
    DecoderInfo &d = decoders[idx];
    if (d.legacy)
        legacySend(SendMode::ASSIGNED_ID, d.assignedId, FEATURE_BRIDGE2DECODER, data[CANcmd], data, CAN_FRAME_SIZE);
    else
        sendV2(d.Decoder_Mac, CG2_F_CAN, CG2_CAN_TO_DECODERS, CG2_ADDR_ID, d.assignedId, 0, bridgeSeq++, data, CAN_FRAME_SIZE);
}

// Gleisspannung an/aus an die Stromdecoder (abgeleitet aus STOPP/GO am CAN-Bus)
void sendTrackPowerToDecoders(uint8_t on)
{
    uint8_t p[1] = {on};
    broadcastV2(CG2_F_POWER, CG2_POWER_TRACK, CG2_ADDR_TYPE, DEVTYPE_POWER, p, sizeof(p));
    if (hasLegacyDecoders())
        legacySend(SendMode::TYPE, DEVTYPE_POWER, FEATURE_POWER, POWER_CHG_TRACK_CURRENT, p, sizeof(p));
}

// OTA: Decoder lädt die Firmware per WLAN von der Bridge
void sendOtaStartWifiToDecoder(uint8_t decoderId)
{
    int idx = findDecoderIndexById(decoderId);
    if (idx < 0)
        return;
    DecoderInfo &d = decoders[idx];
    if (d.legacy)
    {
        uint8_t p[2] = {0, 0}; // Versionsnummer, von alter Firmware nicht ausgewertet
        legacySend(SendMode::ASSIGNED_ID, d.assignedId, FEATURE_OTA, OTA_START_WIFI, p, sizeof(p));
    }
    else
        startRequest(d.Decoder_Mac, d.assignedId, CG2_F_OTA, CG2_OTA_DECODER_START, nullptr, 0);
}

// ============================================================
// Initialisierung
// ============================================================

static void initBridgeWifi()
{
    // 1. WiFi komplett initialisieren
    WiFi.mode(WIFI_MODE_APSTA); // falls du STA parallel nutzt, sonst WIFI_MODE_AP
    WiFi.persistent(false);

    // 2. SoftAP starten
    bool apOk = WiFi.softAP(OTA_SSID, OTA_PASS);
    if (!apOk)
    {
        Serial.println("BRIDGE: softAP start FAILED");
        return;
    }

    Serial.print("BRIDGE: AP IP: ");
    Serial.println(WiFi.softAPIP());

    // 3. Sleep AUS – zwingend für stabile OTA-Transfers
    WiFi.setSleep(false);

    // 4. Beacon-Intervall erhöhen (Standard 100 ms → 600 ms)
    wifi_config_t conf;
    esp_wifi_get_config(WIFI_IF_AP, &conf);
    conf.ap.beacon_interval = 600; // 600 ms
    esp_wifi_set_config(WIFI_IF_AP, &conf);

    // 5. Bandbreite auf HT40 (optional, aber gut)
    esp_wifi_set_bandwidth(WIFI_IF_AP, WIFI_BW_HT40);

    Serial.println("BRIDGE: WiFi AP ready for decoders");
}

void initBridgeEspNow()
{
    // WLAN in definierten Zustand bringen
    WiFi.disconnect(true, true);
    delay(50);
    initBridgeWifi();

    // alten ESPNOW-Stack aufräumen
    esp_now_deinit();
    delay(20);

    // ESPNOW starten
    esp_err_t r = esp_now_init();
    if (r != ESP_OK)
    {
        Serial.printf("BRIDGE: esp_now_init FAILED: %d\r\n", r);
        return;
    }

    // Broadcast-Partner
    esp_now_peer_info_t peer{};
    memcpy(peer.peer_addr, Bridge_Mac, 6);
    peer.channel = BRIDGE_AP_CHANNEL;
    peer.ifidx = WIFI_IF_STA;
    peer.encrypt = false;

    r = esp_now_add_peer(&peer);
    if (r != ESP_OK)
        Serial.printf("BRIDGE: ESP-NOW ADD PEER ERROR: %s\n\r", esp_err_to_name(r));

    if (espNowRxQueue == NULL)
        espNowRxQueue = xQueueCreate(32, sizeof(EspNowRxItem));
    if (sendFailQueue == NULL)
        sendFailQueue = xQueueCreate(8, 6);
    esp_now_register_recv_cb(onBridgeDataRecv);
    esp_now_register_send_cb(onBridgeDataSent);
}
