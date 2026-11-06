#include "protocol_constants_core.h"
#if DECODER_TYPE == DEVTYPE_LEDSIGNAL

#include <Arduino.h>
#include "LedSignalAccessory.h"
#include "Core_EspNow.h"
#include "core_globals.h"

// Pins laut alter Software und Schaltplan (LED-Signaldecoder.png): Signal 1 ... 4
static const uint8_t PIN_RED[LedSignalAccessory::SIGNALS] = {GPIO_NUM_22, GPIO_NUM_19, GPIO_NUM_5, GPIO_NUM_16};
static const uint8_t PIN_GREEN[LedSignalAccessory::SIGNALS] = {GPIO_NUM_21, GPIO_NUM_18, GPIO_NUM_17, GPIO_NUM_4};

// PWM (LEDC): Kanal k = rot von Signal k, Kanal 4 + k = grün
static const uint32_t PWM_FREQ = 5000;
static const uint8_t PWM_BITS = 10;
static const uint32_t PWM_MAX = (1 << PWM_BITS) - 1;

static const unsigned long TEST_STEP_MS = 1500;

// ------------------------------------------------------------
// Selbstbeschreibung
// ------------------------------------------------------------
enum ParamNr : uint8_t
{
    // je Signal k (0..3): Adresse = 2k, vorhanden = 2k + 1
    P_BRIGHT_RED = 8,
    P_BRIGHT_GREEN,
    P_FADE,
    P_COUNT
};

static const Cg2ParamDef ledParams[P_COUNT] = {
    // Typ, min, max, Standard, Schritt, Einheit, Name, Auswahl, Anzahl, NVS-Schlüssel, Merkmale
    {CG2_TYPE_U8, 1, 255, 1, 1, "", "Signal 1: Adresse", nullptr, 0, "adr1", CG2_PF_KEEP_ON_RESET},
    {CG2_TYPE_BOOL, 0, 1, 1, 1, "", "Signal 1: vorhanden", nullptr, 0, "on1", CG2_PF_KEEP_ON_RESET},
    {CG2_TYPE_U8, 1, 255, 2, 1, "", "Signal 2: Adresse", nullptr, 0, "adr2", CG2_PF_KEEP_ON_RESET},
    {CG2_TYPE_BOOL, 0, 1, 1, 1, "", "Signal 2: vorhanden", nullptr, 0, "on2", CG2_PF_KEEP_ON_RESET},
    {CG2_TYPE_U8, 1, 255, 3, 1, "", "Signal 3: Adresse", nullptr, 0, "adr3", CG2_PF_KEEP_ON_RESET},
    {CG2_TYPE_BOOL, 0, 1, 1, 1, "", "Signal 3: vorhanden", nullptr, 0, "on3", CG2_PF_KEEP_ON_RESET},
    {CG2_TYPE_U8, 1, 255, 4, 1, "", "Signal 4: Adresse", nullptr, 0, "adr4", CG2_PF_KEEP_ON_RESET},
    {CG2_TYPE_BOOL, 0, 1, 1, 1, "", "Signal 4: vorhanden", nullptr, 0, "on4", CG2_PF_KEEP_ON_RESET},
    {CG2_TYPE_U8, 1, 100, 60, 1, "%", "Helligkeit rot", nullptr, 0, "briR", 0},
    {CG2_TYPE_U8, 1, 100, 60, 1, "%", "Helligkeit grün", nullptr, 0, "briG", 0},
    {CG2_TYPE_U16, 0, 1000, 300, 50, "ms", "Überblendzeit (0 = hart)", nullptr, 0, "fadeMs", 0},
};

enum ActionNr : uint8_t
{
    A_TEST = 0,
    // A_TOGGLE + k: Signal k umschalten
    A_TOGGLE,
    A_COUNT = A_TOGGLE + LedSignalAccessory::SIGNALS
};

static const Cg2InfoDef ledActions[A_COUNT] = {
    {"Alle Signale testen (grün, rot)", ""},
    {"Signal 1 umschalten", ""},
    {"Signal 2 umschalten", ""},
    {"Signal 3 umschalten", ""},
    {"Signal 4 umschalten", ""},
};

LedSignalAccessory::LedSignalAccessory() : AccessoryDecoder(&prefsLedSignal) {}

void LedSignalAccessory::setup()
{
    DEVTYPE = DEVTYPE_LEDSIGNAL;
    VERS_HIGH = 1;
    VERS_LOW = 0;
    AccessoryDecoder::setup();
}

uint8_t LedSignalAccessory::paramCount() const { return P_COUNT; }
const Cg2ParamDef *LedSignalAccessory::paramDef(uint8_t n) const { return n < P_COUNT ? &ledParams[n] : nullptr; }
uint8_t LedSignalAccessory::actionCount() const { return A_COUNT; }
const Cg2InfoDef *LedSignalAccessory::actionDef(uint8_t n) const { return n < A_COUNT ? &ledActions[n] : nullptr; }

uint8_t LedSignalAccessory::channelAddress(uint8_t ch) const
{
    // nicht angeschlossene Signale reagieren auf nichts und melden nichts an WDP
    return (ch < SIGNALS && present[ch]) ? address[ch] : 0;
}

void LedSignalAccessory::onParamChanged(uint8_t n, uint16_t value)
{
    if (n < 2 * SIGNALS)
    {
        uint8_t ch = n / 2;
        if (n % 2 == 0)
            address[ch] = (uint8_t)value;
        else
        {
            present[ch] = value != 0;
            if (ready)
                show(ch, wanted(ch), false);
        }
        return;
    }
    switch (n)
    {
    case P_BRIGHT_RED:
        brightRed = (uint8_t)value;
        break;
    case P_BRIGHT_GREEN:
        brightGreen = (uint8_t)value;
        break;
    case P_FADE:
        fadeMs = value;
        return;
    }
    // neue Helligkeit sofort zeigen
    if (ready)
        for (uint8_t ch = 0; ch < SIGNALS; ch++)
            if (!fade[ch].active)
                renderShown(ch);
}

// ------------------------------------------------------------
// LEDs
// ------------------------------------------------------------

void LedSignalAccessory::driveSetup()
{
    for (uint8_t ch = 0; ch < SIGNALS; ch++)
    {
        ledcSetup(ch, PWM_FREQ, PWM_BITS);
        ledcAttachPin(PIN_RED[ch], ch);
        ledcSetup(SIGNALS + ch, PWM_FREQ, PWM_BITS);
        ledcAttachPin(PIN_GREEN[ch], SIGNALS + ch);
        render(ch, 0, 0);
    }
    ready = true;
    // nach dem Einschalten: Rot, bis die Bridge GO meldet
    showAll(false);
}

// Pegel 0..1 je Farbe. Gemeinsame Anode: die LED leuchtet, solange der Pin LOW ist,
// also wird der Tastgrad umgekehrt. Quadratische Kennlinie, damit das Überblenden für
// das Auge gleichmäßig wirkt.
void LedSignalAccessory::render(uint8_t ch, float redLevel, float greenLevel)
{
    float r = redLevel * brightRed / 100.0f;
    float g = greenLevel * brightGreen / 100.0f;
    ledcWrite(ch, PWM_MAX - (uint32_t)(r * r * PWM_MAX));
    ledcWrite(SIGNALS + ch, PWM_MAX - (uint32_t)(g * g * PWM_MAX));
}

void LedSignalAccessory::renderShown(uint8_t ch)
{
    render(ch, shown[ch] == RED ? 1 : 0, shown[ch] == GREEN ? 1 : 0);
}

LedSignalAccessory::Aspect LedSignalAccessory::wanted(uint8_t ch) const
{
    if (!present[ch])
        return DARK;
    if (!trackOn)
        return RED; // nach dem Einschalten und bei STOPP
    return position(ch) ? GREEN : RED;
}

// Signalbild wechseln, mit Überblenden. report: nach dem Wechsel die Stellung melden.
void LedSignalAccessory::show(uint8_t ch, Aspect target, bool report)
{
    Fade &f = fade[ch];
    if (f.active && f.report)
    {
        // ein laufender Wechsel wird abgelöst: seine Stellung gilt als erreicht
        f.active = false;
        positionReached(ch, f.to == GREEN ? 1 : 0);
    }
    f.active = false;

    if (shown[ch] == target || fadeMs == 0 || testPhase != 0)
    {
        shown[ch] = target;
        if (testPhase == 0)
            renderShown(ch);
        if (report)
            positionReached(ch, target == GREEN ? 1 : 0);
        return;
    }
    f.from = shown[ch];
    f.to = target;
    f.startMs = millis();
    f.report = report;
    f.active = true;
}

void LedSignalAccessory::showAll(bool animate)
{
    uint16_t keep = fadeMs;
    if (!animate)
        fadeMs = 0;
    for (uint8_t ch = 0; ch < SIGNALS; ch++)
        show(ch, wanted(ch), false);
    fadeMs = keep;
}

// Stellbefehl (WDP oder Aktion); "steht schon richtig" hat die Basis schon erledigt
void LedSignalAccessory::driveStart(uint8_t ch, uint8_t pos)
{
    if (ch >= SIGNALS)
        return;
    if (!present[ch] || !trackOn)
    {
        // Stellung merken und melden; das Signal bleibt dunkel bzw. rot bis GO
        positionReached(ch, pos);
        return;
    }
    show(ch, pos ? GREEN : RED, true);
}

void LedSignalAccessory::onTrackPower(bool on)
{
    if (on == trackOn)
        return;
    trackOn = on;
    if (ready)
        showAll(true);
}

void LedSignalAccessory::driveTick()
{
    unsigned long now = millis();

    if (testPhase != 0)
    {
        if (now - testStartMs < TEST_STEP_MS)
            return;
        testStartMs = now;
        if (testPhase == 1)
        {
            testPhase = 2;
            for (uint8_t ch = 0; ch < SIGNALS; ch++)
                if (present[ch])
                    render(ch, 1, 0);
            return;
        }
        // Test vorbei: wieder das richtige Signalbild
        testPhase = 0;
        for (uint8_t ch = 0; ch < SIGNALS; ch++)
            if (!fade[ch].active)
                renderShown(ch);
        return;
    }

    for (uint8_t ch = 0; ch < SIGNALS; ch++)
    {
        Fade &f = fade[ch];
        if (!f.active)
            continue;
        unsigned long t = now - f.startMs;
        if (t >= fadeMs)
        {
            f.active = false;
            shown[ch] = f.to;
            renderShown(ch);
            if (f.report)
                positionReached(ch, f.to == GREEN ? 1 : 0);
            continue;
        }
        // erste Hälfte: altes Licht aus, zweite Hälfte: neues Licht an
        float half = fadeMs / 2.0f;
        float out = t < half ? 1.0f - t / half : 0.0f;
        float in = t < half ? 0.0f : (t - half) / half;
        float red = (f.from == RED ? out : 0) + (f.to == RED ? in : 0);
        float green = (f.from == GREEN ? out : 0) + (f.to == GREEN ? in : 0);
        render(ch, red, green);
    }
}

bool LedSignalAccessory::actionExecute(uint8_t n, const uint8_t *args, uint16_t len)
{
    if (n == A_TEST)
    {
        testPhase = 1;
        testStartMs = millis();
        for (uint8_t ch = 0; ch < SIGNALS; ch++)
            if (present[ch])
                render(ch, 0, 1);
        return true;
    }
    if (n >= A_TOGGLE && n < A_COUNT)
    {
        uint8_t ch = n - A_TOGGLE;
        if (!present[ch])
            return false; // nicht angeschlossen
        requestPosition(ch, position(ch) ? 0 : 1);
        return true;
    }
    return false;
}

#endif
