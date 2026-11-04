#pragma once
// Zeitüberwachung der Decoder und Zusammenfassung an die GUI.
// Anmeldung und Alive-Empfang liegen in Bridge_Decoder.cpp.

#include <Arduino.h>
#include "globals.h"

void InitAlive();
void checkAliveTimeouts();  // Decoder ohne Alive als nicht bereit markieren
void sendAliveSummary();    // CG2_ALIVE_SUMMARY an die GUI, Zähler auf dem Display

const unsigned long ALIVE_SUMMARY_INTERVAL_MS = 1000;
