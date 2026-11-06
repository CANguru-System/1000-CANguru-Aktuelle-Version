#include "protocol_constants_core.h"
#if DECODER_TYPE == DEVTYPE_LEDSIGNAL

#include "Pca9685.h"

// Register laut Datenblatt NXP PCA9685
static const uint8_t REG_MODE1 = 0x00;
static const uint8_t REG_MODE2 = 0x01;
static const uint8_t REG_LED0_ON_L = 0x06;
static const uint8_t REG_PRESCALE = 0xFE;

static const uint8_t MODE1_SLEEP = 0x10;
static const uint8_t MODE1_AI = 0x20; // Registeradresse beim Schreiben automatisch weiterzählen
static const uint8_t MODE2_OUTDRV = 0x04; // 1 = Totem-Pole, 0 = Open-Drain

static const uint16_t FULL = 0x1000; // Bit 4 im ON_H/OFF_H: dauerhaft an bzw. aus

bool Pca9685::begin(TwoWire &wire, uint8_t address, uint16_t frequencyHz, bool openDrainMode)
{
    bus = &wire;
    addr = address;
    bus->beginTransmission(addr);
    found = (bus->endTransmission() == 0);
    if (!found)
        return false;

    // Vorteiler nur im Schlafmodus schreibbar: 25 MHz / (4096 * f) - 1
    uint8_t prescale = (uint8_t)(25000000.0f / (4096.0f * frequencyHz) + 0.5f) - 1;
    writeReg(REG_MODE1, MODE1_SLEEP);
    writeReg(REG_PRESCALE, prescale);
    writeReg(REG_MODE1, MODE1_AI); // aufwachen, ohne ALLCALL
    delayMicroseconds(600);         // Oszillator braucht bis zu 500 µs

    for (uint8_t ch = 0; ch < CHANNELS; ch++)
        last[ch] = 0xFFFF; // unbekannt: beim ersten setLevel() sicher schreiben
    setOpenDrain(openDrainMode);
    return true;
}

void Pca9685::setOpenDrain(bool od)
{
    openDrain = od;
    if (!found)
        return;
    writeReg(REG_MODE2, od ? 0x00 : MODE2_OUTDRV);
    // Bedeutung der Werte hat sich geändert: alle Kanäle beim nächsten setLevel() neu schreiben
    for (uint8_t ch = 0; ch < CHANNELS; ch++)
        last[ch] = 0xFFFF;
}

void Pca9685::setLevel(uint8_t channel, uint16_t level)
{
    if (!found || channel >= CHANNELS)
        return;
    if (level > MAX)
        level = MAX;
    if (last[channel] == level)
        return;
    last[channel] = level;

    // Logisch High = Ausgang losgelassen (Open-Drain) bzw. High (Totem-Pole).
    // "Aktiv" ist bei Open-Drain Low, bei Totem-Pole High.
    uint16_t high = openDrain ? MAX - level : level;
    if (high == 0)
        writeChannel(channel, 0, FULL); // dauerhaft logisch Low
    else if (high >= MAX)
        writeChannel(channel, FULL, 0); // dauerhaft logisch High
    else
        writeChannel(channel, 0, high);
}

void Pca9685::writeChannel(uint8_t channel, uint16_t on, uint16_t off)
{
    bus->beginTransmission(addr);
    bus->write(REG_LED0_ON_L + 4 * channel);
    bus->write(on & 0xFF);
    bus->write(on >> 8);
    bus->write(off & 0xFF);
    bus->write(off >> 8);
    bus->endTransmission();
}

void Pca9685::writeReg(uint8_t reg, uint8_t value)
{
    bus->beginTransmission(addr);
    bus->write(reg);
    bus->write(value);
    bus->endTransmission();
}

uint8_t Pca9685::readReg(uint8_t reg)
{
    bus->beginTransmission(addr);
    bus->write(reg);
    bus->endTransmission();
    bus->requestFrom(addr, (uint8_t)1);
    return bus->available() ? bus->read() : 0;
}

#endif
