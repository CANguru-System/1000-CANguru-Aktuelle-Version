#include <Arduino.h>
#include "protocol_constants_core.h"

#pragma once

void startOtaHttpServer();
void stopOtaHttpServer();
bool sendOtaStartWifiToDecoder(uint8_t decoderId, uint8_t fwMajor, uint8_t fwMinor);

// OTA-Start aus der GUI: mode 0 = ein Decoder (selector = ID), 1 = alle eines Typs.
// Rückgabe: 0 oder CG2_ERR_* für die Antwort an die GUI
uint8_t otaStartFromGui(uint8_t mode, uint8_t selector);
