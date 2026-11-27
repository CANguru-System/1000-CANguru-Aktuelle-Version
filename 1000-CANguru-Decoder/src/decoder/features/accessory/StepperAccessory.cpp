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
static const char *const POSITION_TEXTS[] = {"abzweigend", "gerade", "unterwegs / unsicher"};
#else
static CANguruPreferences &variantStore = prefsSignal;
static const char *VARIANT_LINE1 = "Signal";
static const char *const POSITION_TEXTS[] = {"Halt", "Fahrt", "unterwegs / unsicher"};
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
#if DECODER_TYPE == DEVTYPE_SIGNAL
    P_BOUNCE, // nur Formsignal: Nachwippen des Flügels an der Endlage
#endif
    P_COUNT
};

static const char *const DIRECTION_CHOICES[] = {"normal", "umgekehrt"};
#if DECODER_TYPE == DEVTYPE_SIGNAL
static const char *const BOUNCE_CHOICES[] = {"aus", "leicht", "deutlich"};
#endif

static const Cg2ParamDef stepperParams[P_COUNT] = {
    // Typ, min, max, Standard, Schritt, Einheit, Name, Auswahl, Anzahl, NVS-Schlüssel, Merkmale
    {CG2_TYPE_U8, 1, 255, 1, 1, "", "Adresse", nullptr, 0, "decAdr", CG2_PF_KEEP_ON_RESET},
    {CG2_TYPE_U16, step_delay_min, step_delay_max, step_delay_std, step_delay_step, "x10 µs", "Verzögerung", nullptr, 0, "swDelay", 0},
    // Schrittweite 1: der Laufweg kommt aus der Kalibrierung und darf nicht gerundet werden
    {CG2_TYPE_U16, stepsToEnd_min, stepsToEnd_max, stepsToEnd_std, 1, "Schritte", "Laufweg", nullptr, 0, "stepsToEnd", 0},
    {CG2_TYPE_CHOICE, 0, 1, 0, 1, "", "Laufrichtung", DIRECTION_CHOICES, 2, "s_d", 0},
#if DECODER_TYPE == DEVTYPE_SIGNAL
    {CG2_TYPE_CHOICE, 0, 2, 1, 1, "", "Nachwippen", BOUNCE_CHOICES, 3, "bounce", 0},
#endif
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
    VERS_HIGH = FW_STEPPER_MAJOR; // Version: 0300-Constants/constants_manager.py (FW_*)
    VERS_LOW = FW_STEPPER_MINOR;
    AccessoryDecoder::setup();
}

const char *StepperAccessory::line1() const { return VARIANT_LINE1; }
const char *const *StepperAccessory::positionTexts() const { return POSITION_TEXTS; }

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
#if DECODER_TYPE == DEVTYPE_SIGNAL
    case P_BOUNCE:
        bounce = (uint8_t)value;
        break;
#endif
    }
}

uint8_t StepperAccessory::actionCount() const { return A_COUNT; }
const Cg2InfoDef *StepperAccessory::actionDef(uint8_t n) const { return n < A_COUNT ? &stepperActions[n] : nullptr; }

// ------------------------------------------------------------
// Hardware: DRV8833, Vollschritt, Pins wie bisher
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
// stepIndex ist das zuletzt geschriebene Muster. Das nächste wird erst hier aus der
// aktuellen Richtung bestimmt - so stimmt auch eine Umkehr (Nachwippen, neuer Befehl
// mitten in der Fahrt) auf den Schritt genau.
void StepperAccessory::doStep()
{
    static const uint8_t pattern[4][4] = {{1, 0, 0, 1}, {0, 1, 0, 1}, {0, 1, 1, 0}, {1, 0, 1, 0}};
    stepIndex = towardEnd ? (stepIndex + 3) % 4 : (stepIndex + 1) % 4;
    digitalWrite(pinAplus, pattern[stepIndex][0]);
    digitalWrite(pinAminus, pattern[stepIndex][1]);
    digitalWrite(pinBplus, pattern[stepIndex][2]);
    digitalWrite(pinBminus, pattern[stepIndex][3]);
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
    if (!(running && (phase == Phase::Run || phase == Phase::Bounce)))
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

    if (phase == Phase::Bounce)
    {
        // nächsten Wegpunkt anfahren; am letzten (= Endlage) ist das Wippen vorbei
        if (steps == waypoints[waypointIndex])
        {
            if (++waypointIndex >= waypointCount)
            {
                arrive();
                return;
            }
        }
        towardEnd = waypoints[waypointIndex] > steps;
        doStep();
        return;
    }

    // Run
    if ((towardEnd && steps >= target) || (!towardEnd && steps <= target))
    {
        arrivedPos = towardEnd ? 1 : 0;
        if (!startBounce())
            arrive();
        return;
    }
    doStep();
}

void StepperAccessory::arrive()
{
    stop(); // Spulen aus: der Antrieb hält die Lage selbst, spart Strom und Wärme
    positionReached(0, arrivedPos);
}

// Formsignal: Der Flügel schwingt an der Endlage ein paar Mal mit abnehmendem Ausschlag
// nach, wie beim Vorbild. Er schwingt dabei nur zur Mitte hin, nie über die kalibrierte
// Endlage hinaus - dort kann die Mechanik anstoßen. Ausschläge in Prozent des Laufwegs.
bool StepperAccessory::startBounce()
{
#if DECODER_TYPE == DEVTYPE_SIGNAL
    static const uint8_t LIGHT[] = {6, 3};
    static const uint8_t STRONG[] = {12, 6, 3};
    const uint8_t *amp = bounce == 2 ? STRONG : LIGHT;
    uint8_t n = bounce == 2 ? sizeof(STRONG) : sizeof(LIGHT);
    if (bounce == 0 || travel < 40)
        return false;
    int end = steps;                              // erreichte Endlage
    int inward = (arrivedPos == 1) ? -1 : 1;      // Richtung zur Mitte
    waypointCount = 0;
    for (uint8_t i = 0; i < n && waypointCount + 2 <= MAX_WAYPOINTS; i++)
    {
        int a = max(3, (int)travel * amp[i] / 100);
        waypoints[waypointCount++] = end + inward * a;
        waypoints[waypointCount++] = end;
    }
    waypointIndex = 0;
    // etwas schneller als die Fahrt: der Flügel federt
    currentDelay = max((int)step_delay_min, (int)stepDelay * 2 / 3);
    phase = Phase::Bounce;
    return true;
#else
    return false;
#endif
}

bool StepperAccessory::actionExecute(uint8_t n, const uint8_t *args, uint16_t len)
{
    switch (n)
    {
    case A_SLOW_TO_ZERO:
    case A_SLOW_TO_END:
        if (running && (phase == Phase::Run || phase == Phase::Bounce))
            positionLost(0);
        towardEnd = (n == A_SLOW_TO_END);
        currentDelay = step_delay_max; // langsam
        phase = Phase::SlowMove;
        running = true;
        return true;

    case A_SET_ZERO:
        stop();
        steps = 0;
        markFromGui(0); // WDP soll die neue Stellung übernehmen
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
        markFromGui(0);
        positionReached(0, 1);
        return true;
    }

    case A_TOGGLE:
        requestPosition(0, position(0) ? 0 : 1, true); // Testfahrt aus der GUI: WDP erhält einen Stellbefehl
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
