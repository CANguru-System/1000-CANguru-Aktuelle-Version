#include <Arduino.h>
#include "protocol_constants_core.h"
#include "alive.h"
#include "globals.h"
#include "gui_proc.h"

// Darf nur aus loop() aufgerufen werden: das UDP-Objekt verträgt keine zwei Tasks.
void udpSendToGui(const uint8_t *data, int len)
{
  if (GUIipIsNotSet())
    return;

  udp.beginPacket(guiIP, GUI_PORT);
  udp.write(data, len);
  udp.endPacket();
}
