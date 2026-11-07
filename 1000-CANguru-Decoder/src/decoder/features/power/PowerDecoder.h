#pragma once
#include "Arduino.h"
#include "../core/DecoderBase.h"
#include <Wire.h>
#include "protocol_constants_core.h"

// 1 = Rohwerte des INA3221 einmal pro Sekunde seriell ausgeben (Diagnose)
#ifndef POWER_DIAG
#define POWER_DIAG 0
#endif

class PowerDecoder : public DecoderBase
{
public:

    void setup() override;
    void loop() override;

    uint8_t getAliveValues(uint16_t *values, uint8_t maxValues) override;
    AliveState getStatus() const override;
    void onTrackPower(bool on) override;

    // Selbstbeschreibung: Abschaltgrenze als Parameter (gespeichert vom Kern),
    // Strom je Kanal als Messwert; Werkseinstellungen übernimmt der Kern
    uint8_t paramCount() const override;
    const Cg2ParamDef *paramDef(uint8_t n) const override;
    CANguruPreferences *paramPrefs() override;
    void onParamChanged(uint8_t n, uint16_t value) override;
    uint8_t valueCount() const override;
    const Cg2InfoDef *valueDef(uint8_t n) const override;

private:
    void readCurrent();
#if POWER_DIAG
    void diagSample();
#endif
    void shutdown(uint8_t channel);

    uint16_t current_mA[2] = {0, 0};     // geglättet, für die Anzeige (0xFFFF = nicht angeschlossen)
    uint16_t lastRaw_mA[2] = {0, 0};     // letzte Einzelmessung
    float avg_mA[2] = {0.0f, 0.0f};      // gleitender Mittelwert
    uint8_t overCount[2] = {0, 0};       // Messungen in Folge über dem Grenzwert
    bool connected[2] = {true, true};    // Versorgungsspannung am Kanal vorhanden
    unsigned long lastConnectedCheck = 0;

    static const uint8_t SDA_PIN = 6;
    static const uint8_t SCL_PIN = 7;

    bool shutdownActive[2] = {false, false};

    unsigned long lastSendTime = 0;
};
