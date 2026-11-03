// ESP-NOW: Initialisierung, Empfang (-> Core_Link), Senden von CAN-Frames an die
// Bridge und die Märklin-Antworten des Kerns (PING, Gerätebeschreibung).

#include <Arduino.h>
#include <WiFi.h>
#include <esp_now.h>
#include "esp_wifi.h"
#include "Core_EspNow.h"
#include "Core_Link.h"
#include "Core_Config.h"
#include "Core_Alive.h"
#include "core_globals.h"
#include "DecoderBase.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/queue.h"

static QueueHandle_t canTxQueue = NULL;

BeforeRestartCallback beforeRestartCallback = nullptr;
AfterstartCallback afterstartCallback = nullptr;

uint8_t Bridge_Mac[] = {0x00, 0x00, 0x00, 0x00, 0x00, 0x00}; // wird beim ersten Kontakt mit der tatsächlichen MAC-Adresse gefüllt
uint8_t Decoder_Mac[6];
bool espNowReady = false;
bool bridge_is_known = false;

uint8_t uid_device[uid_num];
uint8_t hasharr[2];

uint8_t DEVTYPE = DEVTYPE_BASE;
uint8_t VERS_HIGH = 0;
uint8_t VERS_LOW = 0;

static ConfigProvider_t configProvider;

void registerConfigProvider(const ConfigProvider_t *provider)
{
    configProvider = *provider;
}

void logMac(const char *prefix, const uint8_t *mac)
{
    Serial.print(prefix);
    for (int i = 0; i < 6; i++)
    {
        Serial.printf("%02X", mac[i]);
        if (i < 5)
            Serial.print(":");
    }
    Serial.println();
}

// CAN-Frame (13 Byte) zur Bridge; gesendet vom Task espnowSendTask
void coreEspNowSendCAN2Bridge(const uint8_t *data)
{
    if (!espNowReady || !bridge_is_known || canTxQueue == NULL)
        return;
    // nicht blockieren: wird auch aus dem Timer des Rückmelders aufgerufen
    xQueueSend(canTxQueue, data, 0);
}

void generateHash()
{
#define UID_BASE 0x45009195ULL // CAN-UID
    uint32_t uid = UID_BASE + aliveGetDecoderID();
    uid_device[0] = (uint8_t)(uid >> 24);
    uid_device[1] = (uint8_t)(uid >> 16);
    uid_device[2] = (uint8_t)(uid >> 8);
    uid_device[3] = (uint8_t)uid;
    uint16_t highbyte = uid >> 16;
    uint16_t lowbyte = uid;
    uint16_t hash = highbyte ^ lowbyte;
    bitWrite(hash, 7, 0);
    bitWrite(hash, 8, 1);
    bitWrite(hash, 9, 1);
    hasharr[0] = hash >> 8;
    hasharr[1] = hash;
}

// sendPING ist die Antwort der Decoder auf eine PING-Anfrage
void sendPING()
{
    uint8_t opFrame[] = {0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
    // D-00(31)D716 R [8] 45 00 91 96 01 00 00 54    E......TRE: 31 Beispiel POWER-Decoder
    opFrame[CANcmd] = PING_R;
    opFrame[hash0] = hasharr[0];
    opFrame[hash1] = hasharr[1];
    opFrame[Framelng] = 0x08;

    for (uint8_t i = 0; i < uid_num; i++)
    {
        opFrame[i + 5] = uid_device[i];
    }
    opFrame[data4] = VERS_HIGH;
    opFrame[data5] = VERS_LOW;
    opFrame[data6] = DEVTYPE >> 8;
    opFrame[data7] = DEVTYPE;
    coreEspNowSendCAN2Bridge(opFrame);
}

static void copyStringToFrame(uint8_t *frame, int start, const String &txt)
{
    memcpy(&frame[start], txt.c_str(), txt.length());
}

void sendConfig()
{
    /*
      Format Gerätebeschreibung:
      Typ     Bedeutung
      Char    Anzahl der Messwerte im Gerät.
      Char    Anzahl der Konfigurationskanäle
      2 Byte  frei.
      U32     Seriennummer CS2.
      String  8 Byte Artikelnummer.
      String  Gerätebezeichnung, \0 Terminiert
  */

    const uint8_t ROWS = 5;
    uint8_t opFrame[] = {0x00, 0x3B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
    const char *line0 = configProvider.getLine0();
    const char *line1 = configProvider.getLine1();
    for (uint8_t line = 0; line < ROWS; line++) // 0 bis 4
    {
        switch (line)
        {
        case 0:
            opFrame[hash0] = 0x03;
            opFrame[hash1] = line + 1;
            opFrame[Framelng] = 0x08;
            // Unter Index 0 sind die Gerätebeschreibung abrufbar. Primär ist dies die Anzahl der zur Verfügung
            // gestellten Messkanäle. Weiterhin enthalten sind Angaben zur Identifikation des Gerätes.
            opFrame[data1] = 0x01;
            opFrame[data7] = aliveGetDecoderID();
            break;
        case 1:
            for (uint8_t n = 0; n < 4; n++)
            {
                opFrame[5 + n * 2] = uid_device[n] / 16 + 0x30;
                opFrame[6 + n * 2] = uid_device[n] % 16 + 0x30;
            }
            break;
        case 2:
            copyStringToFrame(opFrame, 5, line0);
            break;
        case 3:
            copyStringToFrame(opFrame, 5, line1);
            opFrame[0x0C] = 0x00;
            break;
        case 4:
            opFrame[hash0] = hasharr[0];
            opFrame[hash1] = hasharr[1];
            opFrame[Framelng] = 0x06;
            opFrame[data0] = uid_device[0];
            opFrame[data1] = uid_device[1];
            opFrame[data2] = uid_device[2];
            opFrame[data3] = uid_device[3];
            opFrame[data4] = 0x00;
            opFrame[data5] = ROWS - 1;
            opFrame[data6] = 0x00;
            opFrame[data7] = 0x00;
            break;
        }
        coreEspNowSendCAN2Bridge(opFrame);
    }
}

static void onDecDataSent(const uint8_t *mac_addr, esp_now_send_status_t status)
{
    (void)mac_addr;
    (void)status;
}

// Eingehende Daten von der Bridge über ESP-NOW (läuft im WLAN-Task)
static void onDecDataRecv(const uint8_t *mac, const uint8_t *incomingData, int len)
{
    if (!gDecoder)
        return;

    if (bridge_is_known == false)
    {
        logMac("Received first ESP-NOW packet from unknown MAC: ", mac);
        // Bridge als Unicast-Partner eintragen: Antworten gehen nur an sie
        esp_now_peer_info_t peer{};
        memcpy(&Bridge_Mac[0], mac, 6);
        memcpy(peer.peer_addr, Bridge_Mac, 6);
        peer.channel = BRIDGE_AP_CHANNEL;
        peer.ifidx = WIFI_IF_STA;
        peer.encrypt = false;
        esp_now_add_peer(&peer);
        bridge_is_known = true;
        // NVS-Zugriff nicht im WLAN-Task: Speichern der MAC übernimmt coreEspNowLoop()
    }

    // nur in die Warteschlange; Verarbeitung in loop() (Core_Link)
    coreLinkEnqueue(incomingData, len);
}

// Sendet die CAN-Frames der Warteschlange als CG2_CAN_FROM_DECODER an die Bridge
static void espnowSendTask(void *pv)
{
    uint8_t frame[CAN_FRAME_SIZE];

    for (;;)
    {
        // Blockiert, bis ein Frame in der Queue liegt
        if (xQueueReceive(canTxQueue, frame, portMAX_DELAY))
        {
            coreLinkSend(CG2_F_CAN, CG2_CAN_FROM_DECODER, frame, CAN_FRAME_SIZE);
            // kleine Pause, damit ESP-NOW nicht überlastet
            vTaskDelay(10 / portTICK_PERIOD_MS);
        }
    }
}

void coreEspNowInit()
{
    // 1. WiFi starten
    WiFi.disconnect(true, true);
    delay(100);

    WiFi.mode(WIFI_STA);
    delay(150);

    // 2. Kanal setzen
    esp_wifi_set_ps(WIFI_PS_NONE);
    esp_wifi_set_channel(BRIDGE_AP_CHANNEL, WIFI_SECOND_CHAN_NONE);
    delay(50);

    // 3. MAC-Adresse auslesen
    esp_read_mac(Decoder_Mac, ESP_MAC_WIFI_STA);
    logMac("DECODER-coreEspNowInit: WiFi STA MAC: ", Decoder_Mac);

    // 4. Jetzt erst ESP-NOW initialisieren
    esp_now_deinit();
    delay(20);

    if (esp_now_init() != ESP_OK)
    {
        Serial.println("DECODER-coreEspNowInit: ESP-NOW init failed");
        return;
    }

    espNowReady = true;

    // 5. Callbacks registrieren
    esp_now_register_recv_cb(onDecDataRecv);
    esp_now_register_send_cb(onDecDataSent);

    // 6. Peer für die Bridge anlegen, falls bekannt (nach OTA)
    if (bridge_is_known)
    {
        esp_now_peer_info_t peer{};
        memcpy(peer.peer_addr, Bridge_Mac, 6);
        peer.channel = BRIDGE_AP_CHANNEL;
        peer.ifidx = WIFI_IF_STA;
        peer.encrypt = false;

        esp_err_t pr = esp_now_add_peer(&peer);
        Serial.printf("DECODER-coreEspNowInit: add_peer result=%d\r\n", pr);
    }

    if (canTxQueue == NULL)
    {
        canTxQueue = xQueueCreate(30, CAN_FRAME_SIZE);
        xTaskCreate(espnowSendTask, "espnowSendTask", 4096, NULL, 1, NULL);
    }
}

// Aus loop(): einmalig die Bridge-MAC speichern (für REJOIN nach einem OTA)
void coreEspNowLoop()
{
    static bool macSaved = false;
    if (bridge_is_known && !macSaved)
    {
        macSaved = true;
        uint8_t stored[6] = {0};
        prefs.getBytes(key_bridgeMac, stored, 6);
        if (memcmp(stored, Bridge_Mac, 6) != 0)
            prefs.putBytes(key_bridgeMac, Bridge_Mac, 6);
    }
}
