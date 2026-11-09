/* ----------------------------------------------------------------------------
 * "THE BEER-WARE LICENSE" (Revision 42):
 * <CANguru-Buch@web.de> wrote this file. As long as you retain this
 * notice you can do whatever you want with this stuff. If we meet some day,
 * and you think this stuff is worth it, you can buy me a beer in return
 * Gustav Wostrack
 * ----------------------------------------------------------------------------
 */
#ifndef CANGURU_H
#define CANGURU_H

// Bmp-Logo of an CANguru
#include <stdint.h>
#include <Arduino.h>
#include "protocol_constants_core.h"

// Version on the display: the central bridge version (0300-Constants/constants_manager.py, FW_BRIDGE_*)
const String CgVersionnmbr = String(FW_BRIDGE_MAJOR) + "." + String(FW_BRIDGE_MINOR);
const String CgVersion = "CANguru " + CgVersionnmbr;

#endif // CANGURU_H