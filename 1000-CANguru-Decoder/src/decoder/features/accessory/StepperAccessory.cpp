#include "protocol_constants_core.h"
#if DECODER_TYPE == DEVTYPE_SWITCH || DECODER_TYPE == DEVTYPE_SIGNAL

#include <Arduino.h>
#include "StepperAccessory.h"
#include "Core_EspNow.h"
#include "core_globals.h"
#include "Core_Param.h"

// ------------------------------------------------------------
// Variante: Weiche oder Formsignal
// ------------------------------------------------------------
#if DECODER_TYPE == DEVTYPE_SWITCH
static CANguruPreferences &variantStore = prefsSwitch; // Namensraum wie bisher: Einstellungen bleiben erhalten
static const char *VARIANT_LINE1 = "Switch";
#else
static CANguruPreferences &variantStore = prefsSignal;
static const char *VARIANT_LINE1 = "Signal";
#endif

// ------------------------------------------------------------
// Selbstbeschreibung. Speicherschlüssel und Formate wie bisher, damit Adresse,
// Verzögerung, Laufweg und Laufrichtung beim Umstieg erhalten bleiben.
// ------------------------------------------------------------
enum ParamNr : uint8_t
{
    P_ADDRESS = 0,
    P_DELAY,
    P_TRAVEL,
    P_DIRECTION,
    P_COUNT
};

static const char *const DIRECTION_CHOICES[] = {"normal", "umgekehrt"};

static const Cg2ParamDef stepperParams[P_COUNT] = {
    // Typ, min, max, Standard, Schritt, Einheit, Name, Auswahl, Anzahl, NVS-Schlüssel, Merkmale
    {CG2_TYPE_U8, 1, 255, 1, 1, "", "Adresse", nullptr, 0, "decAdr", CG2_PF_KEEP_ON_RESET},
    {CG2_TYPE_U16, step_delay_min, step_delay_max, step_delay_std, step_delay_step, "x10 µs", "Verzögerung", nullptr, 0, "swDelay", 0},
    // Schrittweite 1: der Laufweg kommt aus der Kalibrierung und darf nicht gerundet werden
    {CG2_TYPE_U16, stepsToEnd_min, stepsToEnd_max, stepsToEnd_std, 1, "Schritte", "Laufweg", nullptr, 0, "stepsToEnd", 0},
    {CG2_TYPE_CHOICE, 0, 1, 0, 1, "", "Laufrichtung", DIRECTION_CHOICES, 2, "s_d", 0},
};

enum ActionNr : uint8_t
{
    A_SLOW_TO_ZERO = 0,
    A_SET_ZERO,
    A_SLOW_TO_END,
    A_SET_END,
    A_TOGGLE,
    A_STOP,
    A_COUNT
};

static const Cg2InfoDef stepperActions[A_COUNT] = {
    {"Kalibrieren: langsam Richtung Nullpunkt", ""},
    {"Kalibrieren: hier ist der Nullpunkt", ""},
    {"Kalibrieren: langsam Richtung Endpunkt", ""},
    {"Kalibrieren: hier ist der Endpunkt", ""},
    {"Stellung wechseln (Testfahrt)", ""},
    {"Stopp", ""},
};

StepperAccessory::StepperAccessory() : AccessoryDecoder(&variantStore) {}

void StepperAccessory::setup()
{
#if DECODER_TYPE == DEVTYPE_SWITCH
    DEVTYPE = DEVTYPE_SWITCH;
#else
    DEVTYPE = DEVTYPE_SIGNAL;
#endif
    VERS_HIGH = 2; // Zubehör-Decoder (Phase 3)
    VERS_LOW = 0;
    AccessoryDecoder::setup();
}

const char *StepperAccessory::line1() const { return VARIANT_LINE1; }

uint8_t StepperAccessory::paramCount() const { return P_COUNT; }
const Cg2ParamDef *StepperAccessory::paramDef(uint8_t n) const { return n < P_COUNT ? &stepperParams[n] : nullptr; }

void StepperAccessory::onParamChanged(uint8_t n, uint16_t value)
{
    switch (n)
    {
    case P_ADDRESS:
        address = (uint8_t)value;
        break;
    case P_DELAY:
        stepDelay = value;
        break;
    case P_TRAVEL:
        travel = value;
        break;
    case P_DIRECTION:
        reverse = (uint8_t)value;
        if (pinAplus != 0)
            setPins(); // nur nach driveSetup(), nicht schon beim Laden
        break;
    }
}

uint8_t StepperAccessory::actionCount() const { return A_COUNT; }
const Cg2InfoDef *StepperAccessory::actionDef(uint8_t n) const { return n < A_COUNT ? &stepperActions[n] : nullptr; }

// ------------------------------------------------------------
// Hardware: MX1508, Vollschritt, Pins wie bisher
// ------------------------------------------------------------

void StepperAccessory::setPins()
{
    if (!reverse)
    {
        pinAplus = GPIO_NUM_10;
        pinAminus = GPIO_NUM_7;
        pinBplus = GPIO_NUM_6;
        pinBminus = GPIO_NUM_5;
    }
    else
    {
        // mit diesen Werten fährt der Stepper in die umgekehrte Richtung
        pinAplus = GPIO_NUM_5;
        pinAminus = GPIO_NUM_6;
        pinBplus = GPIO_NUM_7;
        pinBminus = GPIO_NUM_10;
    }
}

void StepperAccessory::driveSetup()
{
    setPins();
    pinMode(pinAplus, OUTPUT);
    pinMode(pinAminus, OUTPUT);
    pinMode(pinBplus, OUTPUT);
    pinMode(pinBminus, OUTPUT);
    coilsOff();
    phase = Phase::Idle;
    running = false;
    // Lage aus der gespeicherten Stellung
    steps = position(0) ? travel : 0;
}

void StepperAccessory::coilsOff()
{
    digitalWrite(pinAplus, LOW);
    digitalWrite(pinAminus, LOW);
    digitalWrite(pinBplus, LOW);
    digitalWrite(pinBminus, LOW);
}

// Bipolare Ansteuerung Vollschritt:  1a 1b 2a 2b
//   0: 1 0 0 1   1: 0 1 0 1   2: 0 1 1 0   3: 1 0 1 0
void StepperAccessory::doStep()
{
    static const uint8_t pattern[4][4] = {{1, 0, 0, 1}, {0, 1, 0, 1}, {0, 1, 1, 0}, {1, 0, 1, 0}};
    digitalWrite(pinAplus, pattern[stepIndex][0]);
    digitalWrite(pinAminus, pattern[stepIndex][1]);
    digitalWrite(pinBplus, pattern[stepIndex][2]);
    digitalWrite(pinBminus, pattern[stepIndex][3]);
    stepIndex = towardEnd ? (stepIndex + 3) % 4 : (stepIndex + 1) % 4;
    steps += towardEnd ? 1 : -1;
}

void StepperAccessory::stop()
{
    running = false;
    phase = Phase::Idle;
    coilsOff();
}

// ------------------------------------------------------------
// Fahrt
// ------------------------------------------------------------

void StepperAccessory::driveStart(uint8_t ch, uint8_t pos)
{
    if (phase == Phase::SlowMove)
    {
        // Stellbefehl während der Kalibrierung: Kalibrierung abbrechen, Lage ist unbekannt
        stop();
        positionLost(0);
    }
    if (!(running && phase == Phase::Run))
    {
        // Lage zu Beginn: aus der bekannten Stellung. Ist sie unsicher, fährt der
        // Stepper den ganzen Laufweg (von der anderen Endlage aus).
        uint8_t from = positionKnown(0) ? position(0) : (pos ? 0 : 1);
        steps = from ? travel : 0;
    }
    // läuft schon: einfach das neue Ziel übernehmen (auch Umkehr mitten in der Fahrt)
    towardEnd = (pos == 1);
    target = towardEnd ? travel : 0;
    currentDelay = stepDelay;
    phase = Phase::Run;
    running = true;
}

void StepperAccessory::driveTick()
{
    if (!running)
        return;
    unsigned long now = micros();
    if (now - lastStepMicros < (unsigned long)currentDelay * 10)
        return;
    lastStepMicros = now;

    if (phase == Phase::SlowMove)
    {
        doStep();
        return;
    }

    // Run
    if ((towardEnd && steps >= target) || (!towardEnd && steps <= target))
    {
        stop(); // Spulen aus: der Antrieb hält die Lage selbst, spart Strom und Wärme
        positionReached(0, towardEnd ? 1 : 0);
        return;
    }
    doStep();
}

bool StepperAccessory::actionExecute(uint8_t n, const uint8_t *args, uint16_t len)
{
    switch (n)
    {
    case A_SLOW_TO_ZERO:
    case A_SLOW_TO_END:
        if (running && phase == Phase::Run)
            positionLost(0);
        towardEnd = (n == A_SLOW_TO_END);
        currentDelay = step_delay_max; // langsam
        phase = Phase::SlowMove;
        running = true;
        return true;

    case A_SET_ZERO:
        stop();
        steps = 0;
        positionReached(0, 0);
        return true;

    case A_SET_END:
    {
        stop();
        uint16_t measured = (uint16_t)abs(steps);
        uint16_t stored = measured;
        coreParamLimit(stepperParams[P_TRAVEL], stored);
        paramWrite(P_TRAVEL, stored); // speichert, ruft onParamChanged()
        if (stored != measured)
            Serial.printf("ZUBEHOER: Laufweg %u Schritte auf %u begrenzt\r\n", measured, stored);
        steps = travel;
        uint8_t ev[3] = {0, (uint8_t)(travel & 0xFF), (uint8_t)(travel >> 8)};
        sendEvent(CG2_EV_CALIBRATED, ev, sizeof(ev));
        positionReached(0, 1);
        return true;
    }

    case A_TOGGLE:
        requestPosition(0, position(0) ? 0 : 1);
        return true;

    case A_STOP:
        if (running)
            positionLost(0); // irgendwo zwischen den Endlagen angehalten
        stop();
        return true;
    }
    return false;
}

#endif
