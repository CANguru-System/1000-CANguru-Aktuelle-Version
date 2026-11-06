#include <Arduino.h>
#include "DecoderBase.h"
#include "Core_Logging.h"
#include "Core_Config.h"
#include "Core_EspNow.h"
#include "Core_Link.h"
#include "Core_Alive.h"
#include "Core_Ota.h"
#include "Core_Param.h"
#include "CANguruPreferences.h"
#include "protocol_constants_core.h"

DecoderBase *gDecoder = nullptr;

void DecoderBase::setup()
{
    coreLoggingInit();
    coreConfigInit();
    coreOtaInit();

    // GANZ AM ENDE: ESP-NOW initialisieren, danach darf gesendet werden
    coreEspNowInit();
    coreRadioInit(); // Sendeleistung (Parameter des Kerns)

    // Nach einem OTA-Neustart kennt der Decoder seine ID noch und meldet sich zurück
    if (aliveGetDecoderID() != INVALID_ASSIGNED_ID)
        coreLinkSendRejoin();
}

void DecoderBase::loop()
{
    coreLinkLoop(); // Nachrichten der Bridge verarbeiten
    coreEspNowLoop();
    coreOtaLoop();
}

void DecoderBase::reply(const Cg2Header &request, const uint8_t *payload, size_t len, uint8_t errorCode)
{
    coreLinkReply(request, payload, len, errorCode);
}

void DecoderBase::sendMessage(uint8_t feature, uint8_t command, const uint8_t *payload, size_t len)
{
    coreLinkSend(feature, command, payload, len);
}

void DecoderBase::sendCanFrame(const uint8_t *frame)
{
    coreEspNowSendCAN2Bridge(frame);
}

void DecoderBase::sendEvent(uint8_t event, const uint8_t *data, size_t len)
{
    uint8_t p[CG2_ESPNOW_MAX - CG2_HEADER_SIZE];
    if (len + 1 > sizeof(p))
        return;
    p[0] = event;
    if (len > 0)
        memcpy(&p[1], data, len);
    coreLinkSend(CG2_F_EVENT, CG2_EVENT_EVENT, p, len + 1);
}

// ------------------------------------------------------------
// Parameter-Speicher des Kerns
// ------------------------------------------------------------

// Byte-Typen als Byte, u16 als 16-Bit-Wert: dieselben Formate wie die bisherigen Schlüssel
static bool storedAsByte(const Cg2ParamDef &d)
{
    return d.type != CG2_TYPE_U16;
}

static uint16_t loadValue(CANguruPreferences *prefs, const Cg2ParamDef &d)
{
    if (prefs == nullptr || d.key == nullptr)
        return d.def;
    return storedAsByte(d) ? prefs->getByte(d.key, (uint8_t)d.def) : (uint16_t)prefs->getUInt(d.key, d.def);
}

static void storeValue(CANguruPreferences *prefs, const Cg2ParamDef &d, uint16_t v)
{
    if (prefs == nullptr || d.key == nullptr)
        return;
    if (storedAsByte(d))
        prefs->putByte(d.key, (uint8_t)v);
    else
        prefs->putUInt(d.key, v);
}

void DecoderBase::paramsLoad()
{
    uint8_t count = paramCount();
    paramValues.assign(count, 0);
    for (uint8_t n = 0; n < count; n++)
    {
        const Cg2ParamDef *d = paramDef(n);
        if (d == nullptr)
            continue;
        uint16_t v = loadValue(paramPrefs(), *d);
        if (!coreParamLimit(*d, v))
            v = d->def; // gespeicherter Wert ungültig (z. B. Auswahl nicht mehr vorhanden)
        paramValues[n] = v;
        onParamChanged(n, v);
    }
}

uint16_t DecoderBase::paramRead(uint8_t n)
{
    return paramValue(n);
}

uint16_t DecoderBase::paramWrite(uint8_t n, uint16_t value)
{
    const Cg2ParamDef *d = paramDef(n);
    if (d == nullptr)
        return 0;
    if (n >= paramValues.size())
        paramValues.resize(paramCount(), 0);
    if (n < paramValues.size() && paramValues[n] == value)
        return value; // unverändert: nicht in den Flash schreiben
    storeValue(paramPrefs(), *d, value);
    if (n < paramValues.size())
        paramValues[n] = value;
    onParamChanged(n, value);
    return value;
}

bool DecoderBase::paramsFactoryReset()
{
    uint8_t count = paramCount();
    if (count == 0)
        return false;
    for (uint8_t n = 0; n < count; n++)
    {
        const Cg2ParamDef *d = paramDef(n);
        if (d == nullptr || (d->flags & CG2_PF_KEEP_ON_RESET))
            continue;
        paramWrite(n, d->def);
    }
    return true;
}
