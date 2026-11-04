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

static const unsigned long START_REPEAT_MS = 5000; // Abstand der wiederholten START-Meldung

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

// ADDED: [id][typ][mac 6][fwMajor][fwMinor][protoMinor][reserviert 0]
void sendDecoderAddedToGUI(const DecoderInfo &d)
{
    uint8_t p[12];
    Cg2Writer w(p, sizeof(p));
    w.u8(d.assignedId).u8(d.decoderType).bytes(d.Decoder_Mac, 6)
        .u8(d.fwMajor).u8(d.fwMinor).u8(d.protoMinor).u8(0); // letztes Byte reserviert (früher: alte Firmware)
    cg2SendToGui(CG2_F_DECODER, CG2_DECODER_ADDED, d.assignedId, p, w.size());
}

static void decoderReady(DecoderInfo &d)
{
    d.isReady = true;
    d.announced = true;
    d.lastSeen = millis();
    sendDecoderAddedToGUI(d);
    Serial.printf("Decoder ID %d (type %02X, firmware %d.%d) ready\r\n", d.assignedId, d.decoderType, d.fwMajor, d.fwMinor);
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

// REJOIN nach einem OTA-Neustart
static void handleRejoin(DecoderInfo &d, uint8_t failReason)
{
    Serial.printf("Decoder with ID %d has REJOINED%s\r\n", d.assignedId, failReason ? " after FAILED OTA" : "");
    otaRunning = false;
    d.isReady = true;
    // Die Firmware kann sich geändert haben: beim nächsten HELLO der GUI neu melden
    d.announced = false;
    d.lastSeen = millis();
    otaDecoderFinished(d.assignedId, failReason);
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

        // Die Bridge wiederholt START regelmäßig, damit sich auch später eingeschaltete
        // oder neu geflashte Decoder anmelden. Ein bereits angemeldeter, der GUI gemeldeter
        // und unveränderter Decoder bekommt seine ID erneut, wird der GUI aber nicht noch
        // einmal gemeldet.
        int prev = findDecoderByMac(mac);
        bool unchanged = prev >= 0 && decoders[prev].isReady && decoders[prev].announced &&
                         decoders[prev].decoderType == type && decoders[prev].fwMajor == fwMajor &&
                         decoders[prev].fwMinor == fwMinor && decoders[prev].protoMinor == protoMinor;

        int idx = registerDecoder(mac, type);
        DecoderInfo &d = decoders[idx];
        d.fwMajor = fwMajor;
        d.fwMinor = fwMinor;
        d.protoMinor = protoMinor;
        d.isReady = unchanged;

        uint8_t p[7];
        memcpy(p, mac, 6);
        p[6] = d.assignedId;
        startRequest(mac, d.assignedId, CG2_F_DECODER, CG2_DECODER_ASSIGN_ID, p, sizeof(p));
        if (!unchanged)
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
                if (h.feature == CG2_F_DECODER && h.command == CG2_DECODER_ASSIGN_ID && !h.isError() && !d.isReady)
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
            handleRejoin(d, h.length >= 1 ? payload[0] : CG2_OTA_FAIL_NONE);
            return;
        }
        break;

    case CG2_F_ALIVE:
        if (h.command == CG2_ALIVE_ALIVE && h.length >= 1)
        {
            // Bereit = Status ungleich Fehler (z. B. nach einer Überstrom-Abschaltung nicht bereit)
            // Der GUI gemeldet wird ein Decoder nur über HELLO (mit Firmware-Stand);
            // das wiederholte START sorgt dafür, dass jeder Decoder HELLO sendet
            d.isReady = (payload[0] != CG2_STATUS_ERROR);
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
        {
            // Decoder mit alter Firmware (vor Protokoll Version 2) werden nicht mehr
            // unterstützt: sie müssen per USB aktualisiert werden
            static unsigned long lastWarnMs = 0;
            if (lastWarnMs == 0 || millis() - lastWarnMs > 60000)
            {
                lastWarnMs = millis();
                Serial.printf("BRIDGE: Nachricht im alten Format von %02X:%02X:%02X:%02X:%02X:%02X ignoriert"
                              " - Decoder bitte per USB aktualisieren\r\n",
                              item.mac[0], item.mac[1], item.mac[2], item.mac[3], item.mac[4], item.mac[5]);
            }
        }
    }
}

void decoderLinkLoop()
{
    unsigned long now = millis();

    // START regelmäßig wiederholen: so melden sich auch Decoder an, die nach dem
    // Start der GUI eingeschaltet oder neu geflasht wurden (nur Protokoll Version 2)
    static unsigned long nextStartMs = 0;
    if (systemReady && !otaRunning && (long)(now - nextStartMs) >= 0)
    {
        nextStartMs = now + START_REPEAT_MS;
        broadcastV2(CG2_F_SESSION, CG2_SESSION_START, CG2_ADDR_ALL, 0, nullptr, 0);
    }

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
    Serial.println("Sent START to all decoders");
}

void sendResetToDecoders()
{
    broadcastV2(CG2_F_DECODER, CG2_DECODER_RESET, CG2_ADDR_ALL, 0, nullptr, 0);
    delay(50);
}

// Märklin-Frame an alle Decoder
void sendFrame2allDecoders(uint8_t *data, int len)
{
    broadcastV2(CG2_F_CAN, CG2_CAN_TO_DECODERS, CG2_ADDR_ALL, 0, data, CAN_FRAME_SIZE);
    delay(50);
}

// Märklin-Frame an alle Decoder eines Typs
void sendFrame2TypeDecoders(uint8_t *data, int len, uint8_t decoderType)
{
    broadcastV2(CG2_F_CAN, CG2_CAN_TO_DECODERS, CG2_ADDR_TYPE, decoderType, data, CAN_FRAME_SIZE);
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
    sendV2(d.Decoder_Mac, CG2_F_CAN, CG2_CAN_TO_DECODERS, CG2_ADDR_ID, d.assignedId, 0, bridgeSeq++, data, CAN_FRAME_SIZE);
}

// Gleisspannung an/aus an die Stromdecoder (abgeleitet aus STOPP/GO am CAN-Bus)
void sendTrackPowerToDecoders(uint8_t on)
{
    uint8_t p[1] = {on};
    broadcastV2(CG2_F_POWER, CG2_POWER_TRACK, CG2_ADDR_TYPE, DEVTYPE_POWER, p, sizeof(p));
}

// OTA: Decoder lädt die Firmware per WLAN von der Bridge
void sendOtaStartWifiToDecoder(uint8_t decoderId)
{
    int idx = findDecoderIndexById(decoderId);
    if (idx < 0)
        return;
    DecoderInfo &d = decoders[idx];
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
