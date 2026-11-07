#include <Arduino.h>
#include <vector>
#include <esp_now.h>
#include "Core_globals.h"
#include "Core_Config.h"
#include "Core_EspNow.h"
#include "CANguruPreferences.h"

// Keys für NVS

// Hier wird die Instanz EINMAL erzeugt
CANguruPreferences prefs("decoder");

#if DECODER_TYPE == DEVTYPE_POWER
CANguruPreferences prefsPower("power");
#endif
#if DECODER_TYPE == DEVTYPE_SIGNAL
CANguruPreferences prefsSignal("signal");
#endif
#if DECODER_TYPE == DEVTYPE_SWITCH
CANguruPreferences prefsSwitch("switch");
#endif
#if DECODER_TYPE == DEVTYPE_RM
CANguruPreferences prefsFeedBack("feedback");
#endif
#if DECODER_TYPE == DEVTYPE_LEDSIGNAL
CANguruPreferences prefsLedSignal("ledsignal");
#endif

