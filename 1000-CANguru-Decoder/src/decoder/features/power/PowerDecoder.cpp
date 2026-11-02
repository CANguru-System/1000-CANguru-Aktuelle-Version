#include "protocol_constants_core.h"
#if DECODER_TYPE == DEVTYPE_POWER

#include <Arduino.h>
#include <esp_now.h>
#include "PowerDecoder.h"
#include "Adafruit_INA3221.h"
#include "CANguruPreferences.h"
#include "Core_EspNow.h"
#include "Core_Alive.h"
#include "Core_globals.h"
#include "DecoderBase.h"

uint16_t threshold_mA = 1000;

unsigned long lastSendTime = 0;
DecoderBase::AliveState currentStatus = DecoderBase::AliveState::Online;

// Globale Objekte
static Adafruit_INA3221 ina3221;
static const gpio_num_t shutdownPin = GPIO_NUM_20; // Define the GPIO pin for shutdown

// Keys für NVS
static const char *key_power_threshold = "pwrThreshold";

// Widerstand der Shunts auf dem INA3221-Modul in Ohm.
// Booster V2.0: INA3221-Modul mit Aufdruck R100 = 0,1 Ohm (Bibliothek nimmt 0,05 Ohm an).
static const float SHUNT_OHM = 0.1f;

// Der INA3221 misst höchstens 163,8 mV am Shunt; darüber bleibt der Wert stehen.
// Ein höherer Grenzwert könnte nie erreicht werden, daher wird er begrenzt.
static const uint16_t MAX_THRESHOLD_mA = (uint16_t)(0.160f / SHUNT_OHM * 1000.0f);

// Glättung der Anzeige: gleitender Mittelwert, neuer Wert geht mit 5 % ein
// (bei ca. 6 ms je Messung entspricht das etwa 120 ms Zeitkonstante).
static const float AVG_FACTOR = 0.05f;

// Abschaltung erst, wenn so viele Messungen hintereinander über dem Grenzwert liegen
// (ca. 25 ms) - einzelne Spitzen, z. B. beim Aufsetzen einer Lok, lösen nicht aus.
static const uint8_t OVERCURRENT_SAMPLES = 4;

// Ein Kanal gilt als angeschlossen, wenn an ihm Versorgungsspannung anliegt.
static const float CONNECTED_MIN_VOLT = 5.0f;
static const unsigned long CONNECTED_CHECK_MS = 500;

// Wird in den Alive-Daten statt eines Stromwerts für "nicht angeschlossen" gesendet.
static const uint16_t NOT_CONNECTED = 0xFFFF;

static uint16_t limitThreshold(uint16_t value)
{
    return (value > MAX_THRESHOLD_mA) ? MAX_THRESHOLD_mA : value;
}

static const char *power_getLine0()
{
    return "CANguru-";
}

static const char *power_getLine1()
{
    return "Booster";
}

// Konfig lesen
int coreConfigGetUInt16(const char *key, int defaultValue)
{
    return prefsPower.getUInt(key, defaultValue);
}

// Konfig schreiben
void coreConfigSetUInt16(const char *key, int value)
{
    prefsPower.putUInt(key, value);
}

void setTrackPower(bool state)
{
    if (state)
    {
        Serial.println("POWER_CHG_TRACK_CURRENT EIN received");
        gpio_set_level(shutdownPin, HIGH); // Strom EIN
    }
    else
    {
        Serial.println("POWER_CHG_TRACK_CURRENT AUS received");
        gpio_set_level(shutdownPin, LOW); // Strom AUS
    }
}

void PowerDecoderAfterStart()
{
    // Abschalt-Pin konfigurieren
    gpio_set_direction(shutdownPin, GPIO_MODE_OUTPUT);

    // Gleisspannung ausschalten
    setTrackPower(false);

    // Log optional
}

void PowerDecoderBeforeRestart()
{
    // Gleisspannung abschalten
    setTrackPower(false);

    // INA3221 abschalten
    ina3221.reset();

    // Log optional
}

void initPowerDecoder()
{
    ConfigProvider_t provider = {
        .getLine0 = power_getLine0,
        .getLine1 = power_getLine1};

    registerConfigProvider(&provider);
}
void loadDefaults() // setzt seine Standardwerte
{
    // Hier können die Standardwerte für den PowerDecoder gesetzt werden
    Serial.println("Loading default settings for PowerDecoder...");
    //--------------------------------------------------- key -------------- Wert ------------min ----------max ---)
    // Adresse
    prefsPower.init();
    threshold_mA = limitThreshold(coreConfigGetUInt16(key_power_threshold, 1000));
}

void loadDefaultsWithAddress() // setzt seine Standardwerte und behält die Adresse bei
{
    loadDefaults();
}

void PowerDecoder::setup()
{
    // 0. Basisklasse initialisieren (WICHTIG!)
    DecoderBase::setup();

    // 1. INA3221 initialisieren
    Wire.begin(SDA_PIN, SCL_PIN, 400000);
    if (!ina3221.begin(0x40, &Wire))
    {
        while (true)
        {
            Serial.println("INA3221 begin failed");
            delay(1000);
        }
    }
    for (uint8_t ch = 0; ch < 2; ch++)
        ina3221.setShuntResistance(ch, SHUNT_OHM);
    // Grenzwert aus NVS laden
    beforeRestartCallback = PowerDecoderBeforeRestart;
    afterstartCallback = PowerDecoderAfterStart;
    DEVTYPE = DEVTYPE_POWER;
    VERS_HIGH = 1;
    VERS_LOW = 0;
    pinMode(shutdownPin, OUTPUT);

    initPowerDecoder();
    // NVS
    loadDefaults();
}

void PowerDecoder::readCurrent()
{
    // Angeschlossene Kanäle erkennen (nicht bei jeder Messung nötig)
    unsigned long now = millis();
    if (now - lastConnectedCheck >= CONNECTED_CHECK_MS)
    {
        lastConnectedCheck = now;
        for (uint8_t ch = 0; ch < 2; ch++)
            connected[ch] = ina3221.getBusVoltage(ch) > CONNECTED_MIN_VOLT;
    }

    for (uint8_t ch = 0; ch < 2; ch++)
    {
        if (!connected[ch])
        {
            avg_mA[ch] = 0.0f;
            overCount[ch] = 0;
            lastRaw_mA[ch] = 0;
            current_mA[ch] = NOT_CONNECTED;
            continue;
        }

        float amps = ina3221.getCurrentAmps(ch);
        // Negative Werte (Schaltspitzen des L298) auf 0 begrenzen
        float raw_mA = (amps > 0.0f) ? amps * 1000.0f : 0.0f;
        lastRaw_mA[ch] = (uint16_t)raw_mA;

        // Anzeige: gleitender Mittelwert
        avg_mA[ch] += (raw_mA - avg_mA[ch]) * AVG_FACTOR;
        current_mA[ch] = (uint16_t)(avg_mA[ch] + 0.5f);

        // Überstrom: aufeinanderfolgende Messungen über dem Grenzwert zählen
        if (raw_mA > threshold_mA)
        {
            if (overCount[ch] < 255)
                overCount[ch]++;
        }
        else
            overCount[ch] = 0;
    }
#if POWER_DIAG
    diagSample();
#endif
}

#if POWER_DIAG
// Diagnose: sammelt die Rohwerte des INA3221 und gibt sie einmal pro Sekunde
// auf der seriellen Schnittstelle aus, um sie mit einem Messgerät zu vergleichen.
void PowerDecoder::diagSample()
{
    static float shuntMin[3] = {1e9f, 1e9f, 1e9f};
    static float shuntMax[3] = {-1e9f, -1e9f, -1e9f};
    static float shuntSum[3] = {0, 0, 0};
    static float busSum[3] = {0, 0, 0};
    static uint32_t samples = 0;
    static unsigned long lastPrint = 0;

    // Alle drei INA-Kanaele, um die Verdrahtung (Modul CH1..CH3) zu erkennen
    for (uint8_t ch = 0; ch < 3; ch++)
    {
        float shunt_mV = ina3221.getShuntVoltage(ch) * 1000.0f;
        shuntMin[ch] = min(shuntMin[ch], shunt_mV);
        shuntMax[ch] = max(shuntMax[ch], shunt_mV);
        shuntSum[ch] += shunt_mV;
        busSum[ch] += ina3221.getBusVoltage(ch);
    }
    samples++;

    unsigned long now = millis();
    if (now - lastPrint < 1000)
        return;
    lastPrint = now;

    // Nur angeschlossene Kanäle (Busspannung > 5 V) ausführlich ausgeben,
    // die übrigen in einer kurzen Zeile zusammenfassen.
    const float connectedMinVolt = 5.0f;
    char offen[64] = "";
    for (uint8_t ch = 0; ch < 3; ch++)
    {
        float bus_V = busSum[ch] / samples;
        if (bus_V > connectedMinVolt)
        {
            Serial.printf("DIAG CH%u: I = %5.0f mA (min %5.0f / max %5.0f) | %5.2f V | gemeldet %u mA\r\n",
                          ch + 1,
                          shuntSum[ch] / samples / SHUNT_OHM,
                          shuntMin[ch] / SHUNT_OHM,
                          shuntMax[ch] / SHUNT_OHM,
                          bus_V,
                          (ch < 2) ? current_mA[ch] : 0);
        }
        else
        {
            char teil[24];
            snprintf(teil, sizeof(teil), " CH%u (%.2f V, %.0f mA)", ch + 1, bus_V, shuntSum[ch] / samples / SHUNT_OHM);
            strncat(offen, teil, sizeof(offen) - strlen(offen) - 1);
        }
        shuntMin[ch] = 1e9f;
        shuntMax[ch] = -1e9f;
        shuntSum[ch] = 0;
        busSum[ch] = 0;
    }
    Serial.printf("DIAG Grenzwert %u mA | Abschaltung CH1/CH2 = %d/%d | nicht angeschlossen:%s\r\n",
                  threshold_mA, shutdownActive[0], shutdownActive[1], offen[0] ? offen : " -");
    samples = 0;
}
#endif

void PowerDecoder::shutdown(uint8_t channel)
{
    shutdownActive[channel] = true;
    currentStatus = DecoderBase::AliveState::Offline;
    setTrackPower(false);

    // gemeldet wird der Strom der letzten Messung, die zur Abschaltung geführt hat
    const uint8_t len = 3;
    uint8_t payload[len];
    payload[0] = channel;
    payload[1] = lastRaw_mA[channel] >> 8;
    payload[2] = lastRaw_mA[channel] & 0xFF;

    gDecoder->sendToBridge(FEATURE_POWER, POWER_CMD_SHUTDOWN, payload, len);
}

void PowerDecoder::onFeatureFrame(uint8_t featureId,
                                  uint8_t commandId,
                                  const uint8_t *payload,
                                  uint8_t payloadLen)
{
    if (featureId != FEATURE_POWER)
        return;

    switch (commandId)
    {
    case POWER_CMD_SET_THRESHOLD:
    {
        if (payloadLen < 2)
            return;

        threshold_mA = limitThreshold((payload[0] << 8) | payload[1]);
        coreConfigSetUInt16(key_power_threshold, threshold_mA);

        Serial.printf("Threshold %u mA gespeichert\n", threshold_mA);
        break;
    }

    case POWER_CMD_GET_THRESHOLD:
    {
        uint16_t thr = threshold_mA;

        const uint8_t len = 2;
        uint8_t resp[len] = {uint8_t(thr >> 8), uint8_t(thr & 0xFF)};

        gDecoder->sendToBridge(FEATURE_POWER, POWER_CMD_GET_THRESHOLD, resp, len);

        Serial.printf("Threshold %u mA gesendet\n", thr);
        break;
    }

    case POWER_CMD_SHUTDOWN:
    {
        // Shutdown kommt normalerweise vom Decoder selbst,
        // aber falls die Bridge es sendet:
        if (payloadLen < 1 || payload[0] > 1)
            return;
        shutdown(payload[0]);
        break;
    }

    case POWER_CHG_TRACK_CURRENT:
    {
        // STOPP/GO von GUI oder WDP
        if (payloadLen < 1)
            return;
        bool on = (payload[0] == 1);
        if (on)
        {
            // Beim Wiedereinschalten die Überstromüberwachung neu scharf schalten,
            // sonst würde nach der ersten Abschaltung nie wieder abgeschaltet.
            shutdownActive[0] = false;
            shutdownActive[1] = false;
            overCount[0] = 0;
            overCount[1] = 0;
            currentStatus = DecoderBase::AliveState::Online;
        }
        setTrackPower(on);
        break;
    }

    case POWER_CMD_GET_VERSION:
    {

        const uint8_t len = 2;
        uint8_t resp[len] = {uint8_t(VERS_HIGH), uint8_t(VERS_LOW)};

        gDecoder->sendToBridge(FEATURE_POWER, POWER_CMD_GET_VERSION, resp, len);

        Serial.printf("Version %u.%u gesendet\n", VERS_HIGH, VERS_LOW);
        break;
    }
    }
}

uint8_t PowerDecoder::getAliveFeatureData(uint8_t *buf)
{
    uint16_t mA0 = current_mA[0]; // Assuming you want the first channel's current
    uint16_t mA1 = current_mA[1]; // Assuming you want the second channel's current

    buf[0] = mA0 >> 8;
    buf[1] = mA0 & 0xFF;
    buf[2] = mA1 >> 8;
    buf[3] = mA1 & 0xFF;

    return 4; // zwei Bytes Feature-Daten
}

DecoderBase::AliveState PowerDecoder::getStatus() const
{
    return currentStatus; // Implement your status logic here
}

void PowerDecoder::loop()
{
    // 0. das Erben von DecoderBase::loop() aufrufen, damit Core_Alive und Core_Ota funktionieren
    DecoderBase::loop();
    // 1. Messung immer durchführen
    if (aliveGetDecoderID() != 0)
        readCurrent();

    // 2. Abschalten, wenn der Strom anhaltend über dem Grenzwert liegt
    for (uint8_t ch = 0; ch < 2; ch++)
    {
        if (connected[ch] && overCount[ch] >= OVERCURRENT_SAMPLES && !shutdownActive[ch])
            shutdown(ch);
    }

    // 4. Sehr kurze Pause, damit der Watchdog glücklich bleibt
    delay(5);
}

#endif
