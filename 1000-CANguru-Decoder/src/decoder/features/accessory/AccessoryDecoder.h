#pragma once
#include "protocol_constants_core.h"
#if DECODER_TYPE == DEVTYPE_SWITCH || DECODER_TYPE == DEVTYPE_SIGNAL || DECODER_TYPE == DEVTYPE_LEDSIGNAL

#include <Arduino.h>
#include "../../core/DecoderBase.h"
#include "CANguruPreferences.h"

// Gemeinsamer Teil aller Zubehör-Decoder (Weiche, Formsignal, LED-Signal), siehe
// Entwurf Phase 3. Ein Zubehör-Decoder hat einen oder mehrere Kanäle; jeder Kanal ist
// ein Zubehörteil mit eigener Märklin-Adresse und der Stellung 0 (rot / abzweigend)
// oder 1 (grün / gerade).
//
// Dieser Teil übernimmt: Märklin-Stellbefehle aus WDP, Rückmeldung an WDP erst nach
// Erreichen der Stellung, Speichern der Stellung (mit "unterwegs"-Vermerk für den Fall
// eines Stromausfalls während der Fahrt), Ereignis POSITION an die GUI, Messwerte.
// Der Antrieb (Unterklasse) bewegt nur und meldet die Ankunft mit positionReached().
class AccessoryDecoder : public DecoderBase
{
public:
    static const uint8_t POS_UNKNOWN = 2; // Messwert: unterwegs oder nach Stromausfall unsicher
    static const uint8_t MAX_CHANNELS = 8; // Bitmasken unten sind 8 Bit breit

    void setup() override;
    void loop() override;
    void onCanFrame(const uint8_t *frame) override;
    uint8_t getAliveValues(uint16_t *values, uint8_t maxValues) override;
    AliveState getStatus() const override;
    CANguruPreferences *paramPrefs() override { return store; }
    uint8_t valueCount() const override { return channelCount(); }
    const Cg2InfoDef *valueDef(uint8_t n) const override;

    // Stellung anfordern; nach Ankunft Rückmeldung an WDP.
    // fromGui = true: Auslöser ist die GUI (Aktion), nicht WDP. Dann erhält WDP nach Ankunft
    // zusätzlich einen Stellbefehl, damit sein Stellpult die neue Stellung übernimmt
    // (eine Rückmeldung allein übernimmt WDP nicht).
    void requestPosition(uint8_t ch, uint8_t pos, bool fromGui = false);

    // Gerätename für die CS2-Konfiguration (Zeile 2), z. B. "Switch"
    const char *line1Public() const { return line1(); }

protected:
    explicit AccessoryDecoder(CANguruPreferences *store) : store(store) {}

    // ---- vom Antrieb bereitzustellen ----
    virtual uint8_t channelCount() const = 0;
    virtual uint8_t channelAddress(uint8_t ch) const = 0; // 0 = Kanal nicht benutzt
    virtual void driveSetup() = 0;                        // Hardware, nach paramsLoad()
    virtual void driveStart(uint8_t ch, uint8_t pos) = 0; // Bewegung beginnen
    virtual void driveTick() {}                           // aus loop()
    virtual bool driveBusy(uint8_t ch) const = 0;
    virtual const char *line1() const = 0;                // Gerätename für die CS2-Konfiguration
    // Klartext der Stellung für die GUI: [0] Stellung 0, [1] Stellung 1, [2] unterwegs/unsicher
    virtual const char *const *positionTexts() const = 0;

    // ---- für den Antrieb ----
    // Stellung erreicht: speichern, Ereignis an die GUI, bei reportToWdp Rückmeldung an WDP
    void positionReached(uint8_t ch, uint8_t pos, bool reportToWdp = true);
    // Die nächste erreichte Stellung dieses Kanals stammt aus der GUI (z. B. Kalibrierung)
    void markFromGui(uint8_t ch) { if (ch < MAX_CHANNELS) fromGuiMask |= (1 << ch); }
    // Bewegung abgebrochen (Stopp): Stellung unbekannt, bis sie wieder erreicht wird
    void positionLost(uint8_t ch) { if (ch < MAX_CHANNELS) uncertain |= (1 << ch); }
    uint8_t position(uint8_t ch) const { return ch < MAX_CHANNELS ? positions[ch] : 0; }
    bool positionKnown(uint8_t ch) const { return ch < MAX_CHANNELS && !(uncertain & (1 << ch)); }

    CANguruPreferences *store;

private:
    void loadPositions();
    void setMoving(uint8_t ch, bool moving);
    void reportToWdp(uint8_t ch, bool asCommand);
    static const char *positionKey(uint8_t ch);

    uint8_t positions[MAX_CHANNELS] = {0};
    uint8_t moving = 0;    // Bitmaske: Kanal bewegt sich gerade
    uint8_t uncertain = 0; // Bitmaske: Stellung nach Stromausfall während der Fahrt unsicher
    uint8_t fromGuiMask = 0; // Bitmaske: laufende Bewegung wurde von der GUI ausgelöst
    char valueNames[MAX_CHANNELS][24];
    Cg2InfoDef valueDefs[MAX_CHANNELS];
};

#endif
