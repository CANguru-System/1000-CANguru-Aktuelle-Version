#include "protocol_constants_core.h"
#if DECODER_TYPE == DEVTYPE_LIGHT

#include <Arduino.h>
#include "LightAccessory.h"
#include "Core_EspNow.h"
#include "core_globals.h"

// Hausbeleuchtungsplatine: Datenleitung des WS2812-Streifens an GPIO 2
static const uint8_t PIN_STRIP = 2;

// Identifizieren: dreimal hell, dazwischen dunkel
static const uint8_t IDENT_PHASES = 6;
static const unsigned long IDENT_PHASE_MS = 300;

// Test: so viele Takte bleiben alle Räume an, bevor der Durchlauf neu beginnt
static const uint8_t TEST_PAUSE_TICKS = 4;

// ------------------------------------------------------------
// Selbstbeschreibung
// ------------------------------------------------------------
enum ParamNr : uint8_t
{
    P_ADDRESS = 0,
    P_LEDS,
    P_ORDER,
    P_MODE,
    P_BRIGHTNESS,
    P_RED,
    P_GREEN,
    P_BLUE,
    P_TICK,
    P_MAX_ON,
    P_MAX_OFF,
    P_EFFECT_SPEED,
    P_COUNT
};

static const char *const ORDER_CHOICES[] = {"RGB", "GRB"};
static const char *const MODE_CHOICES[] = {"Hausbeleuchtung", "Effekte", "Test"};

static const Cg2ParamDef lightParams[P_COUNT] = {
    // Typ, min, max, Standard, Schritt, Einheit, Name, Auswahl, Anzahl, NVS-Schlüssel, Merkmale
    {CG2_TYPE_U8, 1, 255, 1, 1, "", "Adresse", nullptr, 0, "adr", CG2_PF_KEEP_ON_RESET},
    // 16 bits (room for a higher limit later); own key, because the old byte value under
    // "leds" can not be read as 16 bits
    {CG2_TYPE_U16, 1, LightAccessory::MAX_LEDS, 24, 1, "", "Anzahl LEDs (Räume)", nullptr, 0, "leds16", CG2_PF_KEEP_ON_RESET},
    {CG2_TYPE_CHOICE, 0, 1, 0, 1, "", "Farbfolge des LED-Streifens", ORDER_CHOICES, 2, "order", CG2_PF_KEEP_ON_RESET},
    {CG2_TYPE_CHOICE, 0, 2, 0, 1, "", "Betriebsart", MODE_CHOICES, 3, "mode", 0},
    {CG2_TYPE_U8, 1, 100, 100, 1, "%", "Helligkeit", nullptr, 0, "bright", 0},
    // Gruppenregel "Gruppe: Name": die GUI fasst die Einträge zusammen
    {CG2_TYPE_U8, 0, 255, 32, 1, "", "Raumlicht: Rot", nullptr, 0, "red", 0},
    {CG2_TYPE_U8, 0, 255, 32, 1, "", "Raumlicht: Grün", nullptr, 0, "green", 0},
    {CG2_TYPE_U8, 0, 255, 32, 1, "", "Raumlicht: Blau", nullptr, 0, "blue", 0},
    // Takt in halben Sekunden (0,5 ... 100 s): ein 16-Bit-Wert in ms reichte nur bis 65 s.
    // Eigener Schlüssel, damit ein alter Wert in ms nicht als halbe Sekunden gelesen wird.
    {CG2_TYPE_U8, 1, 200, 1, 1, "× ½ s", "Hausbeleuchtung: Takt", nullptr, 0, "tickHalf", 0},
    {CG2_TYPE_U8, 1, 255, 12, 1, "Takte", "Hausbeleuchtung: Leuchtdauer bis", nullptr, 0, "maxOn", 0},
    {CG2_TYPE_U8, 1, 255, 24, 1, "Takte", "Hausbeleuchtung: Dunkelzeit bis", nullptr, 0, "maxOff", 0},
    {CG2_TYPE_U8, 5, 200, 32, 1, "ms", "Effekte: Bildzeit", nullptr, 0, "fxSpeed", 0},
};

enum ActionNr : uint8_t
{
    A_TOGGLE = 0,
    A_IDENTIFY,
    A_SHUFFLE,
    A_COUNT
};

static const Cg2InfoDef lightActions[A_COUNT] = {
    {"Licht ein/aus", ""},
    {"Identifizieren (dreimal blinken)", ""},
    {"Raumzeiten neu würfeln", ""},
};

// Messwerte: 0 = Stellung (aus der Basisklasse), 1 = leuchtende Räume
static const Cg2InfoDef roomsValue = {"leuchtende Räume", ""};

static const char *const POSITION_TEXTS[] = {"aus", "an", "wechselt"};

// Effekte in der Reihenfolge, in der sie laufen
enum Effect : uint8_t
{
    FX_WIPE_RED = 0,
    FX_WIPE_GREEN,
    FX_WIPE_BLUE,
    FX_WIPE_WHITE,
    FX_CHASE_WHITE,
    FX_CHASE_RED,
    FX_CHASE_BLUE,
    FX_RAINBOW,
    FX_RAINBOW_CYCLE,
    FX_CHASE_RAINBOW,
    FX_PULSE_WHITE,
    FX_RAINBOW_FADE,
    FX_COUNT
};

LightAccessory::LightAccessory()
    : AccessoryDecoder(&prefsLight), strip(MAX_LEDS, PIN_STRIP, NEO_RGB + NEO_KHZ800) {}

void LightAccessory::setup()
{
    DEVTYPE = DEVTYPE_LIGHT;
    VERS_HIGH = FW_LIGHT_MAJOR; // Version: 0300-Constants/constants_manager.py (FW_*)
    VERS_LOW = FW_LIGHT_MINOR;
    AccessoryDecoder::setup();
}

uint8_t LightAccessory::paramCount() const { return P_COUNT; }
const Cg2ParamDef *LightAccessory::paramDef(uint8_t n) const { return n < P_COUNT ? &lightParams[n] : nullptr; }
uint8_t LightAccessory::actionCount() const { return A_COUNT; }
const Cg2InfoDef *LightAccessory::actionDef(uint8_t n) const { return n < A_COUNT ? &lightActions[n] : nullptr; }
const char *const *LightAccessory::positionTexts() const { return POSITION_TEXTS; }

uint8_t LightAccessory::valueCount() const { return channelCount() + 1; }

const Cg2InfoDef *LightAccessory::valueDef(uint8_t n) const
{
    if (n < channelCount())
        return AccessoryDecoder::valueDef(n);
    return n == channelCount() ? &roomsValue : nullptr;
}

uint8_t LightAccessory::getAliveValues(uint16_t *values, uint8_t maxValues)
{
    uint8_t n = AccessoryDecoder::getAliveValues(values, maxValues);
    if (n < maxValues)
    {
        uint16_t lit = 0;
        if (lightOn && mode == HOUSE)
            for (uint16_t r = 0; r < ledCount; r++)
                lit += rooms[r].on ? 1 : 0;
        values[n++] = lit;
    }
    return n;
}

void LightAccessory::onParamChanged(uint8_t n, uint16_t value)
{
    switch (n)
    {
    case P_ADDRESS:
        address = (uint8_t)value;
        return; // ändert nichts an der Anzeige
    case P_LEDS:
        ledCount = value;
        break;
    case P_ORDER:
        orderGrb = value != 0;
        if (ready)
            strip.updateType(orderGrb ? NEO_GRB + NEO_KHZ800 : NEO_RGB + NEO_KHZ800);
        break;
    case P_MODE:
        mode = (Mode)value;
        break;
    case P_BRIGHTNESS:
        brightness = (uint8_t)value;
        if (ready)
            strip.setBrightness((uint16_t)brightness * 255 / 100);
        break;
    case P_RED:
        red = (uint8_t)value;
        break;
    case P_GREEN:
        green = (uint8_t)value;
        break;
    case P_BLUE:
        blue = (uint8_t)value;
        break;
    case P_TICK:
        tickMs = (uint32_t)value * 500;
        return; // gilt ab dem nächsten Takt
    case P_MAX_ON:
        maxOnTicks = (uint8_t)value;
        return;
    case P_MAX_OFF:
        maxOffTicks = (uint8_t)value;
        return;
    case P_EFFECT_SPEED:
        effectSpeed = (uint8_t)value;
        return;
    }
    // Anzahl, Farbfolge, Betriebsart, Helligkeit oder Farbe: sofort neu zeigen
    if (ready)
        restart();
}

// ------------------------------------------------------------
// Ein, aus und Neubeginn
// ------------------------------------------------------------

void LightAccessory::driveSetup()
{
    randomSeed(esp_random());
    strip.updateType(orderGrb ? NEO_GRB + NEO_KHZ800 : NEO_RGB + NEO_KHZ800);
    strip.begin();
    strip.setBrightness((uint16_t)brightness * 255 / 100);
    ready = true;
    lightOn = position(0) == 1;
    Serial.printf("LICHT: %u LEDs an GPIO %u, Betriebsart %u, Licht %s\r\n",
                  ledCount, PIN_STRIP, mode, lightOn ? "an" : "aus");
    restart();

    // Beim allerersten Start ist das Licht an, damit man sofort sieht, dass alles geht
    if (!store->isKey("acc_state"))
        requestPosition(0, 1);
}

// Stellbefehl aus WDP oder aus der GUI: grün = an, rot = aus. Es gibt nichts zu bewegen,
// die Stellung ist sofort erreicht.
void LightAccessory::driveStart(uint8_t ch, uint8_t pos)
{
    lightOn = pos == 1;
    Serial.printf("LICHT: %s\r\n", lightOn ? "an" : "aus");
    restart();
    positionReached(ch, pos);
}

void LightAccessory::restart()
{
    clearStrip();
    unsigned long now = millis();
    lastTick = now;
    lastFrame = now;
    for (uint16_t r = 0; r < MAX_LEDS; r++)
    {
        rooms[r].on = false;
        // versetzt beginnen, damit nicht alle Räume zugleich angehen
        rooms[r].remaining = randomTicks(maxOffTicks);
    }
    testRoom = 0;
    testPause = 0;
    effect = 0;
    frame = 0;
}

void LightAccessory::clearStrip()
{
    strip.clear();
    strip.show();
}

// ------------------------------------------------------------
// Ablauf (aus loop(), nie blockierend)
// ------------------------------------------------------------

void LightAccessory::driveTick()
{
    if (!ready)
        return;
    unsigned long now = millis();
    if (identPhase > 0)
    {
        tickIdentify(now);
        return;
    }
    if (!lightOn)
        return;
    switch (mode)
    {
    case HOUSE:
        tickHouse(now);
        break;
    case EFFECTS:
        tickEffect(now);
        break;
    case TEST:
        tickTest(now);
        break;
    }
}

// Jeder Raum wechselt nach einer zufälligen Zahl von Takten zwischen hell und dunkel
void LightAccessory::tickHouse(unsigned long now)
{
    if (now - lastTick < tickMs)
        return;
    lastTick = now;
    uint32_t on = strip.Color(red, green, blue);
    for (uint16_t r = 0; r < ledCount; r++)
    {
        Room &room = rooms[r];
        if (room.remaining == 0)
        {
            room.on = !room.on;
            room.remaining = randomTicks(room.on ? maxOnTicks : maxOffTicks);
        }
        room.remaining--;
        strip.setPixelColor(r, room.on ? on : 0);
    }
    strip.show();
}

// Die Räume gehen nacheinander an; sind alle an, folgt eine kurze Pause, dann von vorn
void LightAccessory::tickTest(unsigned long now)
{
    if (now - lastTick < tickMs / 2)
        return;
    lastTick = now;
    if (testRoom < ledCount)
    {
        strip.setPixelColor(testRoom++, strip.Color(red, green, blue));
        strip.show();
        return;
    }
    if (++testPause >= TEST_PAUSE_TICKS)
    {
        testPause = 0;
        testRoom = 0;
        clearStrip();
    }
}

void LightAccessory::tickEffect(unsigned long now)
{
    // Bildzeit je Effekt: Farbverläufe laufen mit festem Takt, die übrigen einstellbar
    unsigned long interval;
    switch (effect)
    {
    case FX_RAINBOW:
    case FX_RAINBOW_CYCLE:
        interval = 20;
        break;
    case FX_PULSE_WHITE:
        interval = 5;
        break;
    case FX_RAINBOW_FADE:
        interval = 3;
        break;
    default:
        interval = effectSpeed;
    }
    if (now - lastFrame < interval)
        return;
    lastFrame = now;
    if (effectFrame())
    {
        effect = (effect + 1) % FX_COUNT;
        frame = 0;
        strip.clear();
    }
    else
        frame++;
}

// Ein Bild des laufenden Effekts zeichnen; true, wenn der Effekt fertig ist.
// Die Effekte folgen den Beispielen der Adafruit-NeoPixel-Bibliothek, sind aber in
// einzelne Bilder zerlegt, statt mit delay() zu warten.
bool LightAccessory::effectFrame()
{
    const uint16_t n = ledCount;
    switch (effect)
    {
    case FX_WIPE_RED:
    case FX_WIPE_GREEN:
    case FX_WIPE_BLUE:
    case FX_WIPE_WHITE:
    {
        // die LEDs füllen sich nacheinander mit einer Farbe
        static const uint32_t colors[] = {0xFF0000, 0x00FF00, 0x0000FF, 0xFFFFFF};
        if (frame >= n)
            return true;
        strip.setPixelColor(frame, colors[effect - FX_WIPE_RED]);
        break;
    }
    case FX_CHASE_WHITE:
    case FX_CHASE_RED:
    case FX_CHASE_BLUE:
    {
        // Lauflicht wie an einer Kinofassade: jede dritte LED, 10 Durchläufe
        static const uint32_t colors[] = {0x7F7F7F, 0x7F0000, 0x00007F};
        if (frame >= 30)
            return true;
        uint8_t q = frame % 3;
        for (uint16_t i = 0; i < n; i++)
            strip.setPixelColor(i, (i % 3 == q) ? colors[effect - FX_CHASE_WHITE] : 0);
        break;
    }
    case FX_RAINBOW:
        // Regenbogen wandert über den Streifen
        if (frame >= 256)
            return true;
        for (uint16_t i = 0; i < n; i++)
            strip.setPixelColor(i, wheel((i + frame) & 255));
        break;
    case FX_RAINBOW_CYCLE:
        // ein ganzer Regenbogen auf dem Streifen, fünf Umläufe
        if (frame >= 256 * 5)
            return true;
        for (uint16_t i = 0; i < n; i++)
            strip.setPixelColor(i, wheel(((i * 256 / n) + frame) & 255));
        break;
    case FX_CHASE_RAINBOW:
    {
        // Lauflicht in Regenbogenfarben
        if (frame >= 256 * 3)
            return true;
        uint8_t q = frame % 3;
        uint16_t j = frame / 3;
        for (uint16_t i = 0; i < n; i++)
            strip.setPixelColor(i, (i % 3 == q) ? wheel((i + j) % 255) : 0);
        break;
    }
    case FX_PULSE_WHITE:
    {
        // weiß langsam auf- und abblenden
        if (frame >= 512)
            return true;
        uint8_t level = frame < 256 ? frame : 511 - frame;
        uint8_t v = Adafruit_NeoPixel::gamma8(level);
        strip.fill(strip.Color(v, v, v), 0, n);
        break;
    }
    case FX_RAINBOW_FADE:
    {
        // Regenbogen einblenden, drei Umläufe, ausblenden
        const uint32_t loops = 3, steps = loops * 256;
        if (frame >= steps)
            return true;
        uint32_t hue = frame * 256;
        uint8_t fade = 100;
        if (frame < 100)
            fade = frame;
        else if (frame >= steps - 100)
            fade = steps - frame;
        for (uint16_t i = 0; i < n; i++)
        {
            uint32_t pixelHue = hue + (i * 65536UL / n);
            strip.setPixelColor(i, strip.gamma32(strip.ColorHSV(pixelHue, 255, 255 * fade / 100)));
        }
        break;
    }
    default:
        return true;
    }
    strip.show();
    return false;
}

// Farbrad: 0 ... 255 ergibt rot - grün - blau - rot
uint32_t LightAccessory::wheel(uint8_t pos) const
{
    pos = 255 - pos;
    if (pos < 85)
        return Adafruit_NeoPixel::Color(255 - pos * 3, 0, pos * 3);
    if (pos < 170)
    {
        pos -= 85;
        return Adafruit_NeoPixel::Color(0, pos * 3, 255 - pos * 3);
    }
    pos -= 170;
    return Adafruit_NeoPixel::Color(pos * 3, 255 - pos * 3, 0);
}

// Identifizieren: der ganze Streifen blinkt dreimal weiß, danach weiter wie vorher
void LightAccessory::tickIdentify(unsigned long now)
{
    if (now - identStart < IDENT_PHASE_MS)
        return;
    identStart = now;
    if (identPhase >= IDENT_PHASES)
    {
        identPhase = 0;
        restart();
        return;
    }
    if (identPhase % 2 == 0)
        strip.fill(strip.Color(64, 64, 64), 0, ledCount);
    else
        strip.clear();
    strip.show();
    identPhase++;
}

bool LightAccessory::actionExecute(uint8_t n, const uint8_t *args, uint16_t len)
{
    switch (n)
    {
    case A_TOGGLE:
        // aus der GUI: WDP erhält einen Stellbefehl und übernimmt die Stellung
        requestPosition(0, lightOn ? 0 : 1, true);
        return true;
    case A_IDENTIFY:
        identPhase = 0;
        identStart = millis() - IDENT_PHASE_MS; // erste Phase sofort
        tickIdentify(millis());
        return true;
    case A_SHUFFLE:
        restart();
        return true;
    }
    return false;
}

#endif
