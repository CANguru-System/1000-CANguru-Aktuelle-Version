#include <Arduino.h>
#include "DecoderBase.h"
#include "Core_Logging.h"
#include "Core_Config.h"
#include "Core_EspNow.h"
#include "Core_Link.h"
#include "Core_Alive.h"
#include "Core_Ota.h"

DecoderBase *gDecoder = nullptr;

void DecoderBase::setup()
{
    coreLoggingInit();
    coreConfigInit();
    coreOtaInit();

    // GANZ AM ENDE: ESP-NOW initialisieren, danach darf gesendet werden
    coreEspNowInit();

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
