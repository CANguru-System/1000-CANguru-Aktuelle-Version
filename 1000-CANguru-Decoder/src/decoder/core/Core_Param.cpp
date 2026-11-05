// Feature Parameter und Aktion: allgemeine Verarbeitung für alle Decodertypen.
// Ein Decodertyp liefert nur seine Tabellen (DecoderBase::paramDef usw.);
// Antworten, Bereichsprüfung und Fehlermeldungen übernimmt dieser Teil.

#include <Arduino.h>
#include "Core_Param.h"
#include "DecoderBase.h"
#include "protocol_constants_core.h"

bool coreParamLimit(const Cg2ParamDef &def, uint16_t &value)
{
    switch (def.type)
    {
    case CG2_TYPE_BOOL:
        value = value ? 1 : 0;
        return true;
    case CG2_TYPE_CHOICE:
        return value < def.choiceCount;
    default:
        break;
    }

    uint16_t lo = def.min, hi = def.max;
    if (def.type == CG2_TYPE_U8 && hi > 255)
        hi = 255;
    if (value < lo)
        value = lo;
    if (value > hi)
        value = hi;
    // auf die Schrittweite runden (gezählt ab dem Minimum), ohne den Bereich zu verlassen
    if (def.step > 1)
    {
        uint32_t steps = ((uint32_t)(value - lo) + def.step / 2) / def.step;
        uint32_t v = lo + steps * def.step;
        if (v > hi)
            v -= def.step;
        value = (uint16_t)v;
    }
    return true;
}

// DESCRIBE: [art][nummer][anzahl gesamt][datentyp][min][max][standard][schritt]
//           [einheit][name][anzahlAuswahl][auswahl] x anzahlAuswahl
static void describe(const Cg2Header &h, uint8_t kind, uint8_t n)
{
    uint8_t buf[CG2_ESPNOW_MAX - CG2_HEADER_SIZE];
    Cg2Writer w(buf, sizeof(buf));

    uint8_t count = 0;
    if (kind == CG2_KIND_PARAM)
        count = gDecoder->paramCount();
    else if (kind == CG2_KIND_ACTION)
        count = gDecoder->actionCount();
    else if (kind == CG2_KIND_VALUE)
        count = gDecoder->valueCount();
    else
    {
        gDecoder->reply(h, nullptr, 0, CG2_ERR_BAD_VALUE);
        return;
    }
    if (n >= count)
    {
        gDecoder->reply(h, nullptr, 0, CG2_ERR_BAD_VALUE);
        return;
    }

    w.u8(kind).u8(n).u8(count);
    if (kind == CG2_KIND_PARAM)
    {
        const Cg2ParamDef *d = gDecoder->paramDef(n);
        if (d == nullptr)
        {
            gDecoder->reply(h, nullptr, 0, CG2_ERR_BAD_VALUE);
            return;
        }
        w.u8(d->type).u16(d->min).u16(d->max).u16(d->def).u16(d->step);
        w.str(d->unit ? d->unit : "").str(d->name ? d->name : "");
        uint8_t choices = (d->type == CG2_TYPE_CHOICE) ? d->choiceCount : 0;
        w.u8(choices);
        for (uint8_t i = 0; i < choices; i++)
            w.str(d->choices[i]);
    }
    else
    {
        const Cg2InfoDef *d = (kind == CG2_KIND_ACTION) ? gDecoder->actionDef(n) : gDecoder->valueDef(n);
        if (d == nullptr)
        {
            gDecoder->reply(h, nullptr, 0, CG2_ERR_BAD_VALUE);
            return;
        }
        // Aktionen und Messwerte: Datentyp u16, min/max/standard/schritt ohne Bedeutung
        w.u8(CG2_TYPE_U16).u16(0).u16(0).u16(0).u16(0);
        w.str(d->unit ? d->unit : "").str(d->name ? d->name : "").u8(0);
    }

    if (!w.ok())
    {
        // Texte zu lang für ein ESP-NOW-Paket: Fehler in der Tabelle des Decoders
        Serial.printf("DECODER: Beschreibung %d/%d passt nicht in ein Paket\r\n", kind, n);
        gDecoder->reply(h, nullptr, 0, CG2_ERR_BAD_LENGTH);
        return;
    }
    gDecoder->reply(h, buf, w.size());
}

bool coreParamHandle(const Cg2Header &h, const uint8_t *payload, uint16_t len)
{
    Cg2Reader r(payload, len);

    if (h.feature == CG2_F_ACTION)
    {
        if (h.command != CG2_ACTION_EXECUTE)
            return false;
        uint8_t n = r.u8();
        if (!r.ok())
        {
            gDecoder->reply(h, nullptr, 0, CG2_ERR_BAD_LENGTH);
            return true;
        }
        if (n >= gDecoder->actionCount())
        {
            gDecoder->reply(h, nullptr, 0, CG2_ERR_BAD_VALUE);
            return true;
        }
        bool ok = gDecoder->actionExecute(n, payload + 1, len - 1);
        uint8_t resp[1] = {n};
        if (ok)
            gDecoder->reply(h, resp, sizeof(resp));
        else
            gDecoder->reply(h, nullptr, 0, CG2_ERR_BUSY);
        return true;
    }

    if (h.feature != CG2_F_PARAM)
        return false;

    switch (h.command)
    {
    case CG2_PARAM_DESCRIBE_GET:
    {
        uint8_t kind = r.u8();
        uint8_t n = r.u8();
        if (!r.ok())
            gDecoder->reply(h, nullptr, 0, CG2_ERR_BAD_LENGTH);
        else
            describe(h, kind, n);
        return true;
    }

    case CG2_PARAM_READ:
    {
        uint8_t n = r.u8();
        if (!r.ok())
        {
            gDecoder->reply(h, nullptr, 0, CG2_ERR_BAD_LENGTH);
            return true;
        }
        if (n >= gDecoder->paramCount())
        {
            gDecoder->reply(h, nullptr, 0, CG2_ERR_BAD_VALUE);
            return true;
        }
        uint8_t resp[3];
        Cg2Writer(resp, sizeof(resp)).u8(n).u16(gDecoder->paramRead(n));
        gDecoder->reply(h, resp, sizeof(resp));
        return true;
    }

    case CG2_PARAM_WRITE:
    {
        uint8_t n = r.u8();
        uint16_t value = r.u16();
        if (!r.ok())
        {
            gDecoder->reply(h, nullptr, 0, CG2_ERR_BAD_LENGTH);
            return true;
        }
        const Cg2ParamDef *d = (n < gDecoder->paramCount()) ? gDecoder->paramDef(n) : nullptr;
        if (d == nullptr || !coreParamLimit(*d, value))
        {
            gDecoder->reply(h, nullptr, 0, CG2_ERR_BAD_VALUE);
            return true;
        }
        uint16_t stored = gDecoder->paramWrite(n, value);
        uint8_t resp[3];
        Cg2Writer(resp, sizeof(resp)).u8(n).u16(stored);
        gDecoder->reply(h, resp, sizeof(resp));
        return true;
    }
    }
    return false;
}
