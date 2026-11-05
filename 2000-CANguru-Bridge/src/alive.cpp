#include <Arduino.h>
#include "protocol_constants_core.h"
#include "globals.h"
#include "gui_link.h"
#include "alive.h"
#include "MOD-LCD.h"
#include "CAN_proc.h"

// Ohne Alive in dieser Zeit gilt ein Decoder als ausgefallen
static const unsigned long ALIVE_TIMEOUT_MS = 5000;


// ----------------------------------------------------
// InitAlive
// ----------------------------------------------------
void InitAlive()
{
    resetFlag = true; // nach Neustart: GUI muss Reset-Ack schicken
    lastGuiSeen = millis();
    lastPingSend = 0;
    lastRequest = 0;
    lastGuiAlive = 0;
    guiIsAlive = false;
}

// ----------------------------------------------------
// Alive-Timeouts prüfen
// ----------------------------------------------------
void checkAliveTimeouts()
{
    unsigned long now = millis();

    for (DecoderInfo &d : decoders)
    {
        if (d.isReady && (now - d.lastSeen > ALIVE_TIMEOUT_MS))
            d.isReady = false; // Decoder gilt als ausgefallen
    }
}

// ----------------------------------------------------
// Zusammenfassung an die GUI: wie viele Decoder je Typ bereit sind
// SUMMARY: [anzahl][typ][zahl] x anzahl
// ----------------------------------------------------
void sendAliveSummary()
{
    if (GUIipIsNotSet())
        return;

    uint8_t types[16];
    uint8_t counts[16];
    uint8_t n = 0;
    int power = 0, sw = 0, signal = 0, feedback = 0;

    for (const DecoderInfo &d : decoders)
    {
        if (!d.isReady || d.hasError)
            continue; // nur Decoder ohne Fehler zählen als bereit

        uint8_t i = 0;
        while (i < n && types[i] != d.decoderType)
            i++;
        if (i == n)
        {
            if (n == sizeof(types))
                continue;
            types[n] = d.decoderType;
            counts[n] = 0;
            n++;
        }
        counts[i]++;

        switch (d.decoderType)
        {
        case DEVTYPE_POWER:  power++;    break;
        case DEVTYPE_SWITCH: sw++;       break;
        case DEVTYPE_SIGNAL: signal++;   break;
        case DEVTYPE_RM:     feedback++; break;
        }
    }

    // Display: bei jeder Änderung einer Anzahl (oder der Gleisbox) neu beschriften.
    // Bisher nur bei steigender Gesamtzahl - ein ausgefallener Decoder blieb stehen.
    static int lastPower = -1, lastSw = -1, lastSignal = -1, lastFeedback = -1;
    static bool lastGb = false;
    if (power != lastPower || sw != lastSw || signal != lastSignal || feedback != lastFeedback ||
        GB_found != lastGb)
    {
        lastPower = power;
        lastSw = sw;
        lastSignal = signal;
        lastFeedback = feedback;
        lastGb = GB_found;
        drawAliveCounts(power, sw, signal, feedback);
    }

    uint8_t p[1 + 2 * sizeof(types)];
    p[0] = n;
    for (uint8_t i = 0; i < n; i++)
    {
        p[1 + 2 * i] = types[i];
        p[2 + 2 * i] = counts[i];
    }
    cg2SendToGui(CG2_F_ALIVE, CG2_ALIVE_SUMMARY, CG2_ID_BRIDGE, p, 1 + 2 * n);
}
