#pragma once
#include "protocol_constants_core.h"
#if DECODER_TYPE == DEVTYPE_LIGHT

#include <Adafruit_NeoPixel.h>
#include "AccessoryDecoder.h"

// Gebäudebeleuchtung als CANguru-Decoder: ein WS2812-Streifen (bis zu 96 LEDs, je LED ein
// Raum) an GPIO 2 der Hausbeleuchtungsplatine mit ESP32-C3 SuperMini.
//
// Betriebsarten:
//   Hausbeleuchtung - die Räume gehen unabhängig voneinander an und aus, mit zufälligen
//                     Leucht- und Dunkelzeiten; so wirkt ein Haus bewohnt
//   Effekte         - Lichteffekte für Schaufenster, Kirmes oder Disco, einer nach dem anderen
//   Test            - die Räume leuchten nacheinander auf, zum Prüfen der Verdrahtung
//
// Die Beleuchtung ist ein Zubehörteil mit einer Märklin-Adresse: grün = an, rot = aus.
// So schaltet Win-Digipet das Licht zum Beispiel abends ein. Die Einstellungen kommen aus
// dem Einstellungsfenster der CANguru-GUI (Selbstbeschreibung), nicht mehr aus einer
// Browserseite.
//
// Alle Abläufe sind nicht blockierend: loop() kehrt nach wenigen Millisekunden zurück,
// damit Funk, Lebenszeichen und Updates weiterlaufen.
class LightAccessory : public AccessoryDecoder
{
public:
    // 96 LEDs: current stays manageable (all lit at full white about 6 A). The count is
    // a 16-bit parameter, so the limit can be raised without changing the settings format.
    static const uint16_t MAX_LEDS = 96;

    LightAccessory();
    void setup() override;

    // Selbstbeschreibung
    uint8_t paramCount() const override;
    const Cg2ParamDef *paramDef(uint8_t n) const override;
    void onParamChanged(uint8_t n, uint16_t value) override;
    uint8_t actionCount() const override;
    const Cg2InfoDef *actionDef(uint8_t n) const override;
    bool actionExecute(uint8_t n, const uint8_t *args, uint16_t len) override;
    uint8_t valueCount() const override;
    const Cg2InfoDef *valueDef(uint8_t n) const override;
    uint8_t getAliveValues(uint16_t *values, uint8_t maxValues) override;

protected:
    uint8_t channelCount() const override { return 1; }
    uint8_t channelAddress(uint8_t ch) const override { return ch == 0 ? address : 0; }
    void driveSetup() override;
    void driveStart(uint8_t ch, uint8_t pos) override;
    void driveTick() override;
    bool driveBusy(uint8_t ch) const override { return false; }
    const char *line1() const override { return "Licht"; }
    const char *const *positionTexts() const override;

private:
    enum Mode : uint8_t
    {
        HOUSE = 0,
        EFFECTS = 1,
        TEST = 2
    };

    struct Room
    {
        bool on = false;
        uint16_t remaining = 0; // Takte bis zum nächsten Wechsel
    };

    void restart();          // Betriebsart neu beginnen, Streifen löschen
    void tickHouse(unsigned long now);
    void tickTest(unsigned long now);
    void tickEffect(unsigned long now);
    bool effectFrame();      // ein Bild des laufenden Effekts; true = Effekt fertig
    void tickIdentify(unsigned long now);
    void clearStrip();
    uint32_t wheel(uint8_t pos) const;
    uint16_t randomTicks(uint16_t max) const { return (uint16_t)random(1, max + 1); }

    Adafruit_NeoPixel strip;

    // Parameter (Kopie aus dem Speicher des Kerns)
    uint8_t address = 1;
    uint16_t ledCount = 24;
    bool orderGrb = false;   // false = RGB (wie bisher), true = GRB
    Mode mode = HOUSE;
    uint8_t red = 32, green = 32, blue = 32;
    uint8_t brightness = 100; // %
    uint32_t tickMs = 500;    // Takt der Hausbeleuchtung in ms (Parameter in halben Sekunden)
    uint8_t maxOnTicks = 12;
    uint8_t maxOffTicks = 24;
    uint8_t effectSpeed = 32; // ms je Bild

    bool ready = false;      // nach driveSetup()
    bool lightOn = false;    // Zubehörstellung: 1 = an
    Room rooms[MAX_LEDS];
    unsigned long lastTick = 0;

    // Test: nächster Raum, Pause nach dem letzten
    uint16_t testRoom = 0;
    uint8_t testPause = 0;

    // Effekte: Nummer, Bildzähler, Zeit des letzten Bildes
    uint8_t effect = 0;
    uint32_t frame = 0;
    unsigned long lastFrame = 0;

    // Identifizieren: Blinkphasen (gerade = hell, ungerade = dunkel)
    uint8_t identPhase = 0;
    unsigned long identStart = 0;
};

#endif
