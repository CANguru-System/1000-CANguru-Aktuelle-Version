#pragma once
#include <stdint.h>
#include "canguru_protocol.h"

// Feature Parameter (DESCRIBE_GET, READ, WRITE) und Aktion (EXECUTE) für alle
// Decodertypen, anhand der Tabellen aus DecoderBase (PROTOKOLL.md 5.4 und 5.5).
// FACTORY_RESET bearbeitet weiterhin Core_Link.
// Rückgabe: true = Nachricht bearbeitet (Antwort gesendet).
bool coreParamHandle(const Cg2Header &h, const uint8_t *payload, uint16_t len);

// Begrenzt einen Wert auf den erlaubten Bereich eines Parameters (auch Schrittweite).
// false = Wert ungültig (Auswahlindex außerhalb der Liste).
bool coreParamLimit(const struct Cg2ParamDef &def, uint16_t &value);
