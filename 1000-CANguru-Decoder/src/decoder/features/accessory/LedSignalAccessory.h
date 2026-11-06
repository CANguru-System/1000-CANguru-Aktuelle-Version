#pragma once
#include "protocol_constants_core.h"
#if DECODER_TYPE == DEVTYPE_LEDSIGNAL

#include "AccessoryDecoder.h"

// Zubehör-Decoder mit LED-Antrieb: vier Lichtsignale rot/grün an einem ESP32-DevKit
// (vorhandene Platine, gemeinsame Anode an 3,3 V, jede LED mit eigenem Vorwiderstand;
// eine LED leuchtet, wenn ihr GPIO auf LOW liegt).
//
// Helligkeit je Farbe einstellbar (PWM), Wechsel mit einstellbarem Überblenden wie bei
// einer Glühlampe: erst das alte Licht aus, dann das neue an. Nach dem Einschalten und
// bei STOPP zeigen alle Signale Rot; nach GO wieder die gespeicherte Stellung.
class LedSignalAccessory : public AccessoryDecoder
{
public:
    static const uint8_t SIGNALS = 4;

    LedSignalAccessory();
    void setup() override;
    void onTrackPower(bool on) override;

    // Selbstbeschreibung
    uint8_t paramCount() const override;
    const Cg2ParamDef *paramDef(uint8_t n) const override;
    void onParamChanged(uint8_t n, uint16_t value) override;
    uint8_t actionCount() const override;
    const Cg2InfoDef *actionDef(uint8_t n) const override;
    bool actionExecute(uint8_t n, const uint8_t *args, uint16_t len) override;

protected:
    uint8_t channelCount() const override { return SIGNALS; }
    uint8_t channelAddress(uint8_t ch) const override;
    void driveSetup() override;
    void driveStart(uint8_t ch, uint8_t pos) override;
    void driveTick() override;
    bool driveBusy(uint8_t ch) const override { return ch < SIGNALS && fade[ch].active; }
    const char *line1() const override { return "LED-Signal"; }

private:
    // Was ein Signal zeigt
    enum Aspect : uint8_t
    {
        RED = 0,
        GREEN = 1,
        DARK = 2
    };

    struct Fade
    {
        bool active = false;
        Aspect from = DARK;
        Aspect to = RED;
        unsigned long startMs = 0;
        bool report = false; // nach dem Überblenden positionReached() melden
    };

    void show(uint8_t ch, Aspect target, bool report);
    void render(uint8_t ch, float redLevel, float greenLevel);
    void renderShown(uint8_t ch);
    Aspect wanted(uint8_t ch) const; // Stellung, Gleisspannung und "vorhanden" berücksichtigt
    void showAll(bool animate);

    // Parameter (Kopie aus dem Speicher des Kerns)
    uint8_t address[SIGNALS] = {1, 2, 3, 4};
    bool present[SIGNALS] = {true, true, true, true};
    uint8_t brightRed = 60;    // %
    uint8_t brightGreen = 60;  // %
    uint16_t fadeMs = 300;

    bool trackOn = false; // bis GO alle rot
    bool ready = false;   // nach driveSetup()
    Aspect shown[SIGNALS] = {DARK, DARK, DARK, DARK};
    Fade fade[SIGNALS];

    // Aktion "Alle Signale testen": grün, dann rot, dann wie vorher
    uint8_t testPhase = 0; // 0 = aus, 1 = grün, 2 = rot
    unsigned long testStartMs = 0;
};

#endif
