#pragma once
#include "protocol_constants_core.h"
#if DECODER_TYPE == DEVTYPE_SWITCH || DECODER_TYPE == DEVTYPE_SIGNAL

#include "AccessoryDecoder.h"

// Zubehör-Decoder mit Stepper-Antrieb: Weiche und Formsignal (ESP32-C3, MX1508, ein Kanal).
// Beide Varianten entstehen aus diesem Quelltext; sie unterscheiden sich nur im
// Decodertyp, im Speicher-Namensraum und im Gerätenamen.
//
// Stellung 0 = Nullpunkt (abzweigend bzw. Halt, fern vom Motor),
//          1 = Endpunkt  (gerade bzw. Fahrt, nahe am Motor).
class StepperAccessory : public AccessoryDecoder
{
public:
    StepperAccessory();
    void setup() override;

    // Selbstbeschreibung
    uint8_t paramCount() const override;
    const Cg2ParamDef *paramDef(uint8_t n) const override;
    void onParamChanged(uint8_t n, uint16_t value) override;
    uint8_t actionCount() const override;
    const Cg2InfoDef *actionDef(uint8_t n) const override;
    bool actionExecute(uint8_t n, const uint8_t *args, uint16_t len) override;

protected:
    uint8_t channelCount() const override { return 1; }
    uint8_t channelAddress(uint8_t ch) const override { return address; }
    void driveSetup() override;
    void driveStart(uint8_t ch, uint8_t pos) override;
    void driveTick() override;
    bool driveBusy(uint8_t ch) const override { return running; } // Fahrt oder Kalibrierung
    const char *line1() const override;

private:
    enum class Phase : uint8_t
    {
        Idle,     // steht
        SlowMove, // Kalibrierung: langsame Fahrt, bis "Hier Nullpunkt/Endpunkt" oder "Stopp"
        Run       // Fahrt zwischen den Endlagen
    };

    void setPins();
    void doStep();
    void coilsOff();
    void stop();

    // Parameter (Kopie aus dem Speicher des Kerns)
    uint8_t address = 1;
    uint16_t stepDelay = step_delay_std; // Verzögerung je Schritt in 10 µs
    uint16_t travel = stepsToEnd_std;    // Schritte zwischen den Endlagen
    uint8_t reverse = 0;                 // Laufrichtung umgekehrt

    // Fahrt
    Phase phase = Phase::Idle;
    bool running = false;
    int steps = 0;        // aktuelle Lage in Schritten, 0 = Nullpunkt
    int target = 0;       // Ziel in Schritten (bei Run)
    bool towardEnd = false;
    uint16_t currentDelay = step_delay_std;
    uint8_t stepIndex = 0;
    unsigned long lastStepMicros = 0;
    uint8_t pinAplus = 0, pinAminus = 0, pinBplus = 0, pinBminus = 0;
};

#endif
