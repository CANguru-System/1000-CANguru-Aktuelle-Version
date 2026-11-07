#pragma once

#include <Arduino.h>
#include "DecoderBase.h"

// Der FeedbackDecoder ist ein CANguru-Decoder für S88-Rückmeldungen.
// Er nutzt Multiplexing über die Pins s0..s3 und zPin, um 16 Kontakte auszulesen.
//
// Ab Firmware 2.0 (Phase 3) sind die Meldeadressen der 16 Kontakte Parameter
// (Parameter-Fenster der GUI); das Übergangs-Feature Rückmelder entfällt.
class FeedbackDecoder : public DecoderBase
{
public:
    static const uint8_t CONTACTS = 16;

    FeedbackDecoder() = default;

    void setup() override;
    void loop() override;

    // Märklin-CAN-Frames aus WDP (S88-Abfrage)
    void onCanFrame(const uint8_t *frame) override;
    DecoderBase::AliveState getStatus() const override;
    uint8_t getAliveValues(uint16_t *values, uint8_t maxValues) override;

    // Selbstbeschreibung: Meldeadresse je Kontakt, Messwert Belegung
    uint8_t paramCount() const override;
    const Cg2ParamDef *paramDef(uint8_t n) const override;
    CANguruPreferences *paramPrefs() override;
    uint8_t valueCount() const override;
    const Cg2InfoDef *valueDef(uint8_t n) const override;

private:
    DecoderBase::AliveState currentStatus = DecoderBase::AliveState::Online;
};
