#pragma once
#include "DecoderBase.h"

class SwitchDecoder : public DecoderBase {
public:
    void setup() override;
    void loop() override;

    bool onMessage(const Cg2Header &h, const uint8_t *payload, uint16_t payloadLen) override;
    void onCanFrame(const uint8_t *frame) override;
    bool factoryReset() override;
    AliveState getStatus() const override;
};
extern void loadDefaultsWithAddress();              // setzt seine Standardwerte und behält die Adresse bei
constexpr const char* DECODER_NVS_NAMESPACE = "switch";
