#include <Arduino.h>
#include "Core_Alive.h"
#include "Core_EspNow.h"
#include "Core_Link.h"
#include "DecoderBase.h"
#include "protocol_constants_core.h"

extern bool otaInProgress;
extern bool bridge_is_known;

static uint8_t decoderID = INVALID_ASSIGNED_ID;
static const unsigned long aliveInterval = 1000;

void aliveSetDecoderID(uint8_t id)
{
    decoderID = id;
}

uint8_t aliveGetDecoderID()
{
    return decoderID;
}

void aliveInit()
{
}

// ALIVE: Status und die Messwerte des Decoders (z. B. Strom je Kanal)
static void sendAlive()
{
    if (aliveGetDecoderID() == INVALID_ASSIGNED_ID || otaInProgress)
        return;

    uint8_t status = (gDecoder->getStatus() == DecoderBase::AliveState::Online) ? CG2_STATUS_OK : CG2_STATUS_ERROR;
    uint16_t values[8];
    uint8_t count = gDecoder->getAliveValues(values, 8);
    coreLinkSendAlive(status, values, count);
}

void aliveLoop()
{
    static unsigned long lastAlive = 0;
    unsigned long now = millis();

    if (now - lastAlive >= aliveInterval)
    {
        lastAlive = now;
        if (bridge_is_known)
            sendAlive();
    }
}
