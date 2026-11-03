#pragma once

#include <Arduino.h>
#include "DecoderBase.h"

// Der FeedbackDecoder ist ein CANguru-Decoder für S88-Rückmeldungen.
// Er nutzt Multiplexing über die Pins s0..s3 und zPin, um 16 Kanäle auszulesen.

class FeedbackDecoder : public DecoderBase
{
public:
    // -----------------------------------------------------------------------
    // Konstruktor
    // -----------------------------------------------------------------------
    FeedbackDecoder() = default;

    // -----------------------------------------------------------------------
    // Setup – wird von DecoderBase::setup() aufgerufen
    // -----------------------------------------------------------------------
    void setup() override;

    // -----------------------------------------------------------------------
    // Hauptschleife – ruft DecoderBase::loop() auf
    // -----------------------------------------------------------------------
    void loop() override;

    // -----------------------------------------------------------------------
    // Befehle aus der GUI, CAN-Frames aus WDP, Werkseinstellungen
    // -----------------------------------------------------------------------
    bool onMessage(const Cg2Header &h, const uint8_t *payload, uint16_t payloadLen) override;
    void onCanFrame(const uint8_t *frame) override;
    bool factoryReset() override;

    // -----------------------------------------------------------------------
    // Status des Decoders (Alive / Offline / Error)
    // -----------------------------------------------------------------------
    DecoderBase::AliveState getStatus() const override;

private:
    // Alive-Status
    DecoderBase::AliveState currentStatus = DecoderBase::AliveState::Online;
};

extern void loadDefaultsWithAddress();              // setzt seine Standardwerte und behält die Adresse bei
// z.B. im FeedbackDecoder
constexpr const char* DECODER_NVS_NAMESPACE = "feedback";

// Keys für NVS
static const char *key_Adr = "decAdr";
static const char *key_channel = "K%d";
const uint8_t minadr = 1;
const uint8_t maxadr = 0xFF;

// im Servodecoder
//constexpr const char* DECODER_NVS_NAMESPACE = "servo";
