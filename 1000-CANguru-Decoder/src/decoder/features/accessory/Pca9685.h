#pragma once
#include "protocol_constants_core.h"
#if DECODER_TYPE == DEVTYPE_LEDSIGNAL

#include <Arduino.h>
#include <Wire.h>

// Minimaler Treiber für den PWM-Baustein PCA9685 (16 Kanäle, 12 Bit) über I²C.
// Schreibt nur Kanäle, deren Wert sich geändert hat, damit der Bus beim Überblenden
// mehrerer Signale nicht unnötig belastet wird.
class Pca9685
{
public:
    static const uint8_t CHANNELS = 16;
    static const uint16_t MAX = 4095;

    // openDrain: Ausgänge schalten nur nach Masse (für LEDs mit gemeinsamer Anode;
    // ein ausgeschalteter Ausgang ist dann wirklich stromlos). Sonst Totem-Pole.
    bool begin(TwoWire &wire, uint8_t address, uint16_t frequencyHz, bool openDrain);
    void setOpenDrain(bool openDrain);

    // Anteil der Zeit, in der der Ausgang "aktiv" ist, 0 ... MAX.
    // Aktiv heißt bei Open-Drain: nach Masse geschaltet (LED mit gemeinsamer Anode leuchtet),
    // bei Totem-Pole: auf High (LED mit gemeinsamer Kathode leuchtet).
    void setLevel(uint8_t channel, uint16_t level);

    bool ok() const { return found; }

private:
    void writeReg(uint8_t reg, uint8_t value);
    uint8_t readReg(uint8_t reg);
    void writeChannel(uint8_t channel, uint16_t on, uint16_t off);

    TwoWire *bus = nullptr;
    uint8_t addr = 0x40;
    bool found = false;
    bool openDrain = true;
    uint16_t last[CHANNELS];
};

#endif
