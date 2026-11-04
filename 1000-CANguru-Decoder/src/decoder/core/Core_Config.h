#pragma once
#include <stdint.h>

extern const char* key_id;
extern const char* key_otaPending;
extern const char* key_bridgeMac;
extern const char* key_otaFailed;   // Grund des fehlgeschlagenen OTA-Versuchs (CG2_OTA_FAIL_*), wird mit REJOIN gemeldet

void coreConfigInit();
bool coreConfigIsOtaPending();
void coreConfigClearOtaPending();
void coreConfigSetOtaPending(bool v);
