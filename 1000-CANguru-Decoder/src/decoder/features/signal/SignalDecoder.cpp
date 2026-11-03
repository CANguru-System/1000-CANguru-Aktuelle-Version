#include "protocol_constants_core.h"
#if DECODER_TYPE == DEVTYPE_SIGNAL

#include <Arduino.h>
#include "SignalDecoder.h"
#include "CANguruPreferences.h"
#include "Core_EspNow.h"
#include "Core_Alive.h"
#include "core_globals.h"
#include "Core_Config.h"
#include "DecoderBase.h"
#include "DecoderResetUtility.h"
#include "Stepper_signal.h"

/*
Variablen der steppers & Magnetartikel
*/

uint8_t decoderadr;

unsigned long lastSendTime = 0;
DecoderBase::AliveState currentStatus = DecoderBase::AliveState::Online;

static const char *signal_getLine0()
{
    return "CANguru-";
}

static const char *signal_getLine1()
{
    return "Signal";
}

void SignalDecoderAfterStart()
{
    // Log optional
}

void SignalDecoderBeforeRestart()
{
    // Log optional
}

void initSignalDecoder()
{
    ConfigProvider_t provider = {
        .getLine0 = signal_getLine0,
        .getLine1 = signal_getLine1};

    registerConfigProvider(&provider);
}

void loadDefaults(bool keepAdr) // setzt seine Standardwerte
{
    // Hier können die Standardwerte für den SignalDecoder gesetzt werden
    Serial.println("Loading default settings for SignalDecoder...");
    prefsSignal.init(); // Muss hier aufgerufen werden, damit die Werte aus dem Preferences-Speicher gelesen werden können
    //--------------------------------------------------- key -------------- Wert ------------min ----------max ---)
    // Adresse
    // decoderadr ist nicht die ID des Decoders sondern die Adresse der Weiche, die dieser Decoder steuert.
    // Sie wird in den Einstellungen der GUI eingegeben und muss mit der tatsächlichen Adresse der Weiche übereinstimmen,
    // damit die Steuerung funktioniert.
    if (keepAdr)
        prefsSignal.putByte(key_Adr, decoderadr);
    else
        decoderadr = 1;
    decoderadr = readValfromPreferences8(prefsSignal, key_Adr, decoderadr, 1, 0xFF);
    // Verzögerung
    step_delay = step_delay_std;
    step_delay = readValfromPreferences16(prefsSignal, key_SrvDel, step_delay_std, step_delay_min, step_delay_max);
    // Gesamtumdrehung
    stepsToEnd = stepsToEnd_std;
    stepsToEnd = readValfromPreferences16(prefsSignal, key_stepsToEnd, stepsToEnd_std, stepsToEnd_min, stepsToEnd_max);
    // Status der Magnetartikel versenden an die steppers
    current_position = nullpkt; // Default-Position ist nullpkt, damit die GUI die Weiche in der Anfangsstellung anzeigt, wenn noch kein Testlauf durchgeführt wurde
    current_position = (position)readValfromPreferences8(prefsSignal, key_curr_pos, nullpkt, nullpkt, endpkt);
    // Ausrichtung des Stepper Motors
    stepDirection = A_dir;
    stepDirection = (stepDirections)readValfromPreferences8(prefsSignal, key_s_d, A_dir, A_dir, B_dir);
}

void loadDefaultsWithAddress() // setzt seine Standardwerte und behält die Adresse bei
{
    loadDefaults(true);
}

void SignalDecoder::setup()
{
    // 0. Basisklasse initialisieren (WICHTIG!)
    DecoderBase::setup();
    beforeRestartCallback = SignalDecoderBeforeRestart;
    afterstartCallback = SignalDecoderAfterStart;
    DEVTYPE = DEVTYPE_SIGNAL;
    VERS_HIGH = 1;
    VERS_LOW = 0;
    initSignalDecoder();
    // nach dem ersten Mal Einlesen der gespeicherten Werte
    loadDefaults(false);
    Attach();
}

// Routine meldet an die CANguru-Bridge, wenn eine Statusänderung
// einer Weiche/Signal eingetreten ist
void acc_report()
{
    uint8_t opFrame[] = {0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
    opFrame[CANcmd] = SWITCH_ACC_R;
    opFrame[hash0] = hasharr[0];
    opFrame[hash1] = hasharr[1];
    opFrame[Framelng] = 0x05;
    // Weichenadresse
    decoderadr = readValfromPreferences8(prefsSignal, key_Adr, decoderadr, 1, 0xFF);
    int address = (uint16_t)(decoderadr >> 8) | decoderadr + MM_ACC - 1;
    opFrame[data2] = (uint8_t)(address >> 8);
    opFrame[data3] = (uint8_t)address;
    // Meldung der Lage
    opFrame[data4] = (uint8_t)readValfromPreferences8(prefsSignal, key_curr_pos, position::nullpkt, position::nullpkt, position::endpkt);
    coreEspNowSendCAN2Bridge(opFrame);
}

// Märklin-Weichenbefehl (aus WDP, über die Bridge)
void SignalDecoder::onCanFrame(const uint8_t *frame)
{
    if (frame[CANcmd] != SWITCH_ACC)
        return;

    uint8_t _to_address = (uint16_t)((frame[data2] << 8) | frame[data3]) - MM_ACC + 1;
    // Umsetzung nur bei gültiger Weichenadresse
    if (_to_address != decoderadr)
        return;

    // 1 - gerade       endpkt  nahe am Motor
    // 0 - abzweigend   nullpkt   weit weg vom Motor
    //  muss Artikel geändert werden?
    if (PosChg(any_pos))
    {
        Start2Run((position)frame[data4]);
        acc_report();
    }
}

// Befehle aus der GUI (Feature Stepper, Kommandos SWITCH_CMD_*, Nutzdaten wie bisher:
// Nutzdaten[0] = Decoder-ID). Jede Anfrage wird quittiert.
bool SignalDecoder::onMessage(const Cg2Header &h, const uint8_t *payload, uint16_t payloadLen)
{
    if (h.feature != CG2_F_STEPPER)
        return false;

    if (payloadLen < 1 || payload[0] != aliveGetDecoderID())
    {
        reply(h, nullptr, 0, CG2_ERR_BAD_VALUE);
        return true;
    }

    switch (h.command)
    {
    case SWITCH_CMD_SET_SETTINGS:
        // Empfangen der Einstellungen von der GUI, z.B. nach Änderung in den Eingabefeldern und Klick auf "Senden"
        if (payloadLen < EXP_LNG_SIGNAL_SETTINGS)
        {
            reply(h, nullptr, 0, CG2_ERR_BAD_LENGTH);
            return true;
        }
        // Decoderadresse
        prefsSignal.putByte(key_Adr, payload[1]);
        decoderadr = readValfromPreferences8(prefsSignal, key_Adr, decoderadr, 1, 0xFF);

        // Verzögerung
        prefsSignal.putUInt(key_SrvDel, (payload[2] << 8) | payload[3]);
        step_delay = readValfromPreferences16(prefsSignal, key_SrvDel, step_delay_std, step_delay_min, step_delay_max);
        // Gesamtumdrehung
        prefsSignal.putUInt(key_stepsToEnd, (payload[4] << 8) | payload[5]);
        stepsToEnd = readValfromPreferences16(prefsSignal, key_stepsToEnd, stepsToEnd_std, stepsToEnd_min, stepsToEnd_max);

        // Position
        prefsSignal.putByte(key_curr_pos, (uint8_t)payload[6]);
        current_position = (position)readValfromPreferences8(prefsSignal, key_curr_pos, position::nullpkt, position::nullpkt, position::endpkt);

        // Stepper‑Richtung
        prefsSignal.putByte(key_s_d, payload[7]);
        stepDirection = (stepDirections)readValfromPreferences8(prefsSignal, key_s_d, A_dir, A_dir, B_dir);

        SetDirection();
        reply(h);
        return true;

    case SWITCH_CMD_GET_SETTINGS:
    {
        // sendet die aktuellen Einstellungen zurück an die GUI, damit die GUI die Werte in den Eingabefeldern korrekt anzeigen kann
        uint8_t p[EXP_LNG_SIGNAL_SETTINGS];
        p[0] = aliveGetDecoderID();

        decoderadr = readValfromPreferences8(prefsSignal, key_Adr, decoderadr, 1, 0xFF);
        p[1] = decoderadr;

        step_delay = readValfromPreferences16(prefsSignal, key_SrvDel, step_delay_std, step_delay_min, step_delay_max);
        p[2] = step_delay >> 8;
        p[3] = step_delay & 0xFF;

        stepsToEnd = readValfromPreferences16(prefsSignal, key_stepsToEnd, stepsToEnd_std, stepsToEnd_min, stepsToEnd_max);
        p[4] = stepsToEnd >> 8;
        p[5] = stepsToEnd & 0xFF;

        p[6] = (uint8_t)readValfromPreferences8(prefsSignal, key_curr_pos, position::nullpkt, position::nullpkt, position::endpkt);

        stepDirection = (stepDirections)readValfromPreferences8(prefsSignal, key_s_d, A_dir, A_dir, B_dir);
        p[7] = stepDirection;

        reply(h, p, sizeof(p));
        return true;
    }

    case SWITCH_CMD_MOVE2START:
        if (payloadLen < 2)
        {
            reply(h, nullptr, 0, CG2_ERR_BAD_LENGTH);
            return true;
        }
        StartSlowMove2Start((position)payload[1]);
        reply(h);
        return true;

    case SWITCH_CMD_SET_ZERO:
        SetZeroPoint();
        reply(h);
        return true;

    case SWITCH_CMD_MOVE2END:
        if (payloadLen < 2)
        {
            reply(h, nullptr, 0, CG2_ERR_BAD_LENGTH);
            return true;
        }
        StartEndPointMove((position)payload[1]);
        reply(h);
        return true;

    case SWITCH_CMD_SET_END:
        SetEndPoint(); // meldet Laufweg (SET_END_ACK) und Stellung (SET_DIR) zusätzlich
        reply(h);
        return true;

    case SWITCH_CMD_TEST_RUN:
        if (payloadLen < 2)
        {
            reply(h, nullptr, 0, CG2_ERR_BAD_LENGTH);
            return true;
        }
        Start2Run((position)payload[1]);
        reply(h);
        return true;

    case SWITCH_CMD_STOP_ALL:
        Serial.println("SignalDecoder: STOP_ALL empfangen.");
        StopAll();
        reply(h);
        return true;
    }
    return false;
}

bool SignalDecoder::factoryReset()
{
    return DecoderReset::performReset(DECODER_NVS_NAMESPACE, loadDefaultsWithAddress);
}

DecoderBase::AliveState SignalDecoder::getStatus() const
{
    return currentStatus; // Implement your status logic here
}

void SignalDecoder::loop()
{
    // 0. das Erben von DecoderBase::loop() aufrufen, damit Core_Link und Core_Ota funktionieren
    DecoderBase::loop();
    StepperTick();
    // Sehr kurze Pause, damit der Watchdog glücklich bleibt
    delay(5);
}

#endif
