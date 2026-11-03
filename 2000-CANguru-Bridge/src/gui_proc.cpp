#include <Arduino.h>
#include "protocol_constants_core.h"
#include "alive.h"
#include "globals.h"
#include "gui_proc.h"
#include "PacketHandler.h"   // parsePacket + buildPacket

void sendPacketToGUI(const std::vector<uint8_t> &bytes)
{
    if (GUIipIsNotSet())
        return;

    udpSendToGui(bytes.data(), bytes.size());
}

void sendPacketToGUI(const ParsedPacket &p)
{
    if (GUIipIsNotSet())
        return;

    auto bytes = buildPacket(
        p.featureId,
        p.commandId,
        p.payload.data(),
        p.payload.size()
    );

    udpSendToGui(bytes.data(), bytes.size());
}

// Darf nur aus loop() aufgerufen werden: das UDP-Objekt verträgt keine zwei Tasks.
void udpSendToGui(const uint8_t *data, int len)
{
  if (GUIipIsNotSet())
    return;

  udp.beginPacket(guiIP, GUI_PORT);
  udp.write(data, len);
  udp.endPacket();
}
