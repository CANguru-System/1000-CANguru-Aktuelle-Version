#include "protocol_constants_core.h"
#if DECODER_TYPE == DEVTYPE_SWITCH || DECODER_TYPE == DEVTYPE_SIGNAL || DECODER_TYPE == DEVTYPE_LEDSIGNAL

#include "AccessoryDecoder.h"
#include "Core_EspNow.h"
#include "Core_Alive.h"

// Speicherschlüssel: Kanal 0 wie bisher "acc_state", damit die Stellung beim Umstieg
// erhalten bleibt; weitere Kanäle "acc_state1" ...
static const char *const POSITION_KEYS[AccessoryDecoder::MAX_CHANNELS] = {
    "acc_state", "acc_state1", "acc_state2", "acc_state3", "acc_state4", "acc_state5", "acc_state6", "acc_state7"};
static const char *KEY_MOVING = "acc_moving"; // Bitmaske der Kanäle, die gerade fahren

static AccessoryDecoder *instance = nullptr;

static const char *accessoryLine0() { return "CANguru-"; }
static const char *accessoryLine1();

const char *AccessoryDecoder::positionKey(uint8_t ch)
{
    return POSITION_KEYS[ch < MAX_CHANNELS ? ch : 0];
}

void AccessoryDecoder::setup()
{
    instance = this;
    DecoderBase::setup();
    store->init();

    static ConfigProvider_t provider;
    provider.getLine0 = accessoryLine0;
    provider.getLine1 = accessoryLine1;
    registerConfigProvider(&provider);

    // Namen der Messwerte: "Stellung" bzw. "Stellung Signal 2"
    for (uint8_t ch = 0; ch < channelCount() && ch < MAX_CHANNELS; ch++)
    {
        if (channelCount() == 1)
            snprintf(valueNames[ch], sizeof(valueNames[ch]), "Stellung");
        else
            snprintf(valueNames[ch], sizeof(valueNames[ch]), "Stellung %u", ch + 1);
        valueDefs[ch].name = valueNames[ch];
        valueDefs[ch].unit = "0/1";
    }

    paramsLoad();
    loadPositions();
    driveSetup();
}

void AccessoryDecoder::loop()
{
    DecoderBase::loop();
    driveTick();
}

void AccessoryDecoder::loadPositions()
{
    moving = store->getByte(KEY_MOVING, 0);
    // Wer beim letzten Mal unterwegs war, hat seine Stellung nicht erreicht (Stromausfall)
    uncertain = moving;
    moving = 0;
    if (uncertain)
    {
        Serial.printf("ZUBEHOER: Stellung unsicher (Bitmaske %02X) - Stromausfall während der Fahrt?\r\n", uncertain);
        store->putByte(KEY_MOVING, 0);
    }
    for (uint8_t ch = 0; ch < channelCount() && ch < MAX_CHANNELS; ch++)
    {
        uint8_t p = store->getByte(positionKey(ch), 0);
        positions[ch] = p > 1 ? 0 : p;
    }
}

void AccessoryDecoder::setMoving(uint8_t ch, bool on)
{
    uint8_t before = moving;
    if (on)
        moving |= (1 << ch);
    else
        moving &= ~(1 << ch);
    if (moving != before)
        store->putByte(KEY_MOVING, moving);
}

// Märklin-Stellbefehl (aus WDP über die Bridge):
// [adresse u32 in data0..3][stellung data4][strom data5]
void AccessoryDecoder::onCanFrame(const uint8_t *frame)
{
    if (frame[CANcmd] != SWITCH_ACC)
        return;
    // Märklin sendet je Befehl "Strom an" und danach "Strom aus"; nur der erste zählt (Z2)
    if (frame[Framelng] >= 6 && frame[data5] == 0)
        return;

    int address = ((frame[data2] << 8) | frame[data3]) - MM_ACC + 1;
    if (address < 1 || address > 255)
        return;
    uint8_t pos = frame[data4] ? 1 : 0;
    for (uint8_t ch = 0; ch < channelCount(); ch++)
        if (channelAddress(ch) == address)
            requestPosition(ch, pos);
}

void AccessoryDecoder::requestPosition(uint8_t ch, uint8_t pos)
{
    if (ch >= channelCount())
        return;
    // Steht schon richtig (und sicher bekannt): nichts bewegen, nur bestätigen (Z1)
    if (!driveBusy(ch) && positionKnown(ch) && positions[ch] == pos)
    {
        reportToWdp(ch);
        return;
    }
    setMoving(ch, true);
    driveStart(ch, pos);
}

void AccessoryDecoder::positionReached(uint8_t ch, uint8_t pos, bool report)
{
    if (ch >= channelCount())
        return;
    positions[ch] = pos;
    uncertain &= ~(1 << ch);
    if (store->getByte(positionKey(ch), 0xFF) != pos)
        store->putByte(positionKey(ch), pos);
    setMoving(ch, false);

    uint8_t ev[2] = {ch, pos};
    sendEvent(CG2_EV_POSITION, ev, sizeof(ev));
    // Rückmeldung an WDP erst jetzt, mit der erreichten Stellung (Z3)
    if (report)
        reportToWdp(ch);
}

// Rückmeldung an WDP: Märklin-Frame ACC_R mit Adresse und Stellung
void AccessoryDecoder::reportToWdp(uint8_t ch)
{
    uint8_t address = channelAddress(ch);
    if (address == 0)
        return;
    uint8_t opFrame[CAN_FRAME_SIZE] = {0};
    opFrame[CANcmd] = SWITCH_ACC_R;
    opFrame[hash0] = hasharr[0];
    opFrame[hash1] = hasharr[1];
    opFrame[Framelng] = 0x05;
    uint16_t canAddress = address + MM_ACC - 1;
    opFrame[data2] = (uint8_t)(canAddress >> 8);
    opFrame[data3] = (uint8_t)canAddress;
    opFrame[data4] = positions[ch];
    sendCanFrame(opFrame);
}

// Messwerte: Stellung je Kanal, 2 = unterwegs oder unsicher
uint8_t AccessoryDecoder::getAliveValues(uint16_t *values, uint8_t maxValues)
{
    uint8_t n = 0;
    for (uint8_t ch = 0; ch < channelCount() && n < maxValues; ch++)
        values[n++] = (driveBusy(ch) || !positionKnown(ch)) ? POS_UNKNOWN : positions[ch];
    return n;
}

// Warnung, solange eine Stellung nach einem Stromausfall während der Fahrt unsicher ist
DecoderBase::AliveState AccessoryDecoder::getStatus() const
{
    return uncertain ? AliveState::Weak : AliveState::Online;
}

const Cg2InfoDef *AccessoryDecoder::valueDef(uint8_t n) const
{
    return n < channelCount() && n < MAX_CHANNELS ? &valueDefs[n] : nullptr;
}

static const char *accessoryLine1()
{
    return instance ? instance->line1Public() : "";
}

#endif
