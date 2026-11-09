#include "protocol_constants_core.h"
#if DECODER_TYPE == DEVTYPE_LEDSIGNAL

#include <Arduino.h>
#include <Wire.h>
#include "LedSignalAccessory.h"
#include "Core_EspNow.h"
#include "core_globals.h"

// CANguru-Platine 1.0: PCA9685 an den Standard-I²C-Pins des ESP32, Adressbrücken offen
static const uint8_t PIN_SDA = 21;
static const uint8_t PIN_SCL = 22;
static const uint8_t PCA_ADDRESS = 0x40;
static const uint16_t PWM_FREQ = 1000; // Hz, flimmerfrei

static const unsigned long TEST_STEP_MS = 1500;

// Ausgänge am PCA9685: Signal k (0 ... 7): rot = 2k, grün = 2k + 1
static inline uint8_t outRed(uint8_t ch) { return 2 * ch; }
static inline uint8_t outGreen(uint8_t ch) { return 2 * ch + 1; }

// ------------------------------------------------------------
// Selbstbeschreibung
// ------------------------------------------------------------
enum ParamNr : uint8_t
{
    // je Signal k (0 ... 7): Adresse = 2k, vorhanden = 2k + 1
    P_BRIGHT_RED = 2 * LedSignalAccessory::SIGNALS,
    P_BRIGHT_GREEN,
    P_FADE,
    P_WIRING,
    P_COUNT
};

static const char *const WIRING_CHOICES[] = {"gemeinsame Anode", "gemeinsame Kathode"};

#define LED_SIGNAL_PARAMS(k)                                                                                    \
    {CG2_TYPE_U8, 1, 255, k, 1, "", "Signal " #k ": Adresse", nullptr, 0, "adr" #k, CG2_PF_KEEP_ON_RESET},     \
    {CG2_TYPE_BOOL, 0, 1, 1, 1, "", "Signal " #k ": vorhanden", nullptr, 0, "on" #k, CG2_PF_KEEP_ON_RESET}

static const Cg2ParamDef ledParams[P_COUNT] = {
    // Typ, min, max, Standard, Schritt, Einheit, Name, Auswahl, Anzahl, NVS-Schlüssel, Merkmale
    LED_SIGNAL_PARAMS(1),
    LED_SIGNAL_PARAMS(2),
    LED_SIGNAL_PARAMS(3),
    LED_SIGNAL_PARAMS(4),
    LED_SIGNAL_PARAMS(5),
    LED_SIGNAL_PARAMS(6),
    LED_SIGNAL_PARAMS(7),
    LED_SIGNAL_PARAMS(8),
    {CG2_TYPE_U8, 1, 100, 60, 1, "%", "Helligkeit rot", nullptr, 0, "briR", 0},
    {CG2_TYPE_U8, 1, 100, 60, 1, "%", "Helligkeit grün", nullptr, 0, "briG", 0},
    {CG2_TYPE_U16, 0, 3000, 1000, 50, "ms", "Überblendzeit (0 = hart)", nullptr, 0, "fadeMs", 0},
    {CG2_TYPE_CHOICE, 0, 1, 0, 1, "", "LED-Anschluss", WIRING_CHOICES, 2, "wiring", CG2_PF_KEEP_ON_RESET},
};

enum ActionNr : uint8_t
{
    A_TEST = 0,
    A_TOGGLE, // A_TOGGLE + k: Signal k umschalten
    A_COUNT = A_TOGGLE + LedSignalAccessory::SIGNALS
};

static const Cg2InfoDef ledActions[A_COUNT] = {
    {"Alle Signale testen (grün, rot)", ""},
    // Gruppenregel "Signal k: ...": die GUI ordnet die Aktion der Gruppe des Signals zu
    {"Signal 1: umschalten", ""},
    {"Signal 2: umschalten", ""},
    {"Signal 3: umschalten", ""},
    {"Signal 4: umschalten", ""},
    {"Signal 5: umschalten", ""},
    {"Signal 6: umschalten", ""},
    {"Signal 7: umschalten", ""},
    {"Signal 8: umschalten", ""},
};

// Stellung im Alive (Protokoll 2.1); 2 = Überblenden läuft oder Stellung unsicher
static const char *const POSITION_TEXTS[] = {"rot", "grün", "wechselt"};

LedSignalAccessory::LedSignalAccessory() : AccessoryDecoder(&prefsLedSignal) {}

void LedSignalAccessory::setup()
{
    DEVTYPE = DEVTYPE_LEDSIGNAL;
    VERS_HIGH = FW_LEDSIGNAL_MAJOR; // Version: 0300-Constants/constants_manager.py (FW_*)
    VERS_LOW = FW_LEDSIGNAL_MINOR;
    AccessoryDecoder::setup();
}

uint8_t LedSignalAccessory::paramCount() const { return P_COUNT; }
const Cg2ParamDef *LedSignalAccessory::paramDef(uint8_t n) const { return n < P_COUNT ? &ledParams[n] : nullptr; }
uint8_t LedSignalAccessory::actionCount() const { return A_COUNT; }
const Cg2InfoDef *LedSignalAccessory::actionDef(uint8_t n) const { return n < A_COUNT ? &ledActions[n] : nullptr; }
const char *const *LedSignalAccessory::positionTexts() const { return POSITION_TEXTS; }

uint8_t LedSignalAccessory::channelAddress(uint8_t ch) const
{
    // nicht angeschlossene Signale reagieren auf nichts und melden nichts an WDP
    return (ch < SIGNALS && present[ch]) ? address[ch] : 0;
}

// Ohne PCA9685 kann der Decoder nichts anzeigen: Fehler melden
DecoderBase::AliveState LedSignalAccessory::getStatus() const
{
    if (ready && !pwm.ok())
        return AliveState::Offline;
    return AccessoryDecoder::getStatus();
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
    case P_WIRING:
        commonAnode = (value == 0);
        if (ready)
            pwm.setOpenDrain(commonAnode);
        break;
    }
    // neue Helligkeit bzw. Anschlussart sofort zeigen
    if (ready)
        renderAll();
}

// ------------------------------------------------------------
// LEDs
// ------------------------------------------------------------

void LedSignalAccessory::driveSetup()
{
    Wire.begin(PIN_SDA, PIN_SCL, 400000);
    // Gemeinsame Anode: Open-Drain, damit ausgeschaltete LEDs wirklich stromlos sind
    if (pwm.begin(Wire, PCA_ADDRESS, PWM_FREQ, commonAnode))
        Serial.printf("LED-SIGNAL: PCA9685 an Adresse 0x%02X gefunden\r\n", PCA_ADDRESS);
    else
        Serial.printf("LED-SIGNAL: PCA9685 an Adresse 0x%02X NICHT gefunden (SDA %u, SCL %u)\r\n",
                      PCA_ADDRESS, PIN_SDA, PIN_SCL);
    ready = true;
    // nach dem Einschalten: Rot, bis die Bridge GO meldet
    showAll(false);
}

// Pegel 0..1 je Farbe; quadratische Kennlinie, damit das Überblenden für das Auge
// gleichmäßig wirkt. Die Anschlussart (Anode/Kathode) berücksichtigt der Treiber.
void LedSignalAccessory::render(uint8_t ch, float redLevel, float greenLevel)
{
    float r = redLevel * brightRed / 100.0f;
    float g = greenLevel * brightGreen / 100.0f;
    pwm.setLevel(outRed(ch), (uint16_t)(r * r * Pca9685::MAX));
    pwm.setLevel(outGreen(ch), (uint16_t)(g * g * Pca9685::MAX));
}

void LedSignalAccessory::renderShown(uint8_t ch)
{
    render(ch, shown[ch] == RED ? 1 : 0, shown[ch] == GREEN ? 1 : 0);
}

void LedSignalAccessory::renderAll()
{
    for (uint8_t ch = 0; ch < SIGNALS; ch++)
        if (!fade[ch].active && testPhase == 0)
            renderShown(ch);
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
        renderAll();
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
        requestPosition(ch, position(ch) ? 0 : 1, true); // aus der GUI: WDP erhält einen Stellbefehl
        return true;
    }
    return false;
}

#endif
