#pragma once

#include <vector>
#include "CANguruPreferences.h"

// Globale Deklaration (NICHT instanziert!)
extern CANguruPreferences prefs;
#if DECODER_TYPE == DEVTYPE_POWER
extern CANguruPreferences prefsPower;
#endif
#if DECODER_TYPE == DEVTYPE_SIGNAL
extern CANguruPreferences prefsSignal;
#endif
#if DECODER_TYPE == DEVTYPE_SWITCH
extern CANguruPreferences prefsSwitch;
#endif
#if DECODER_TYPE == DEVTYPE_RM
extern CANguruPreferences prefsFeedBack;
#endif
#if DECODER_TYPE == DEVTYPE_LEDSIGNAL
extern CANguruPreferences prefsLedSignal;
#endif
// Laden, Begrenzen und Speichern der Einstellungen übernimmt der Parameter-Speicher
// des Kerns (DecoderBase::paramsLoad, Cg2ParamDef mit NVS-Schlüssel).
