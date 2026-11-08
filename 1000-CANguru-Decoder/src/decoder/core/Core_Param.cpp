// Feature Parameter und Aktion: allgemeine Verarbeitung für alle Decodertypen.
// Ein Decodertyp liefert nur seine Tabellen (DecoderBase::paramDef usw.);
// Antworten, Bereichsprüfung und Fehlermeldungen übernimmt dieser Teil.

#include <Arduino.h>
#include <esp_wifi.h>
#include "Core_Param.h"
#include "DecoderBase.h"
#include "core_globals.h"
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

// ------------------------------------------------------------
// Parameter des Kerns: gelten für alle Decodertypen und folgen auf die Parameter
// des Typs (Nummern ab paramCount() des Typs).
// ------------------------------------------------------------

// Sendeleistung für ESP-NOW und das OTA-WLAN. Viele ESP32-C3-SuperMini-Module verbinden
// sich bei voller Leistung schlecht (Antenne schlecht angepasst, Spannungseinbruch beim
// Senden); mit geringerer Leistung laufen sie oft zuverlässig. Auf der Anlage reicht die
// geringe Leistung in der Regel.
static const char *const TX_CHOICES[] = {"8,5 dBm (gering)", "11 dBm", "15 dBm", "19,5 dBm (maximal)"};
static const int8_t TX_QUARTER_DBM[] = {34, 44, 60, 78}; // Einheit von esp_wifi_set_max_tx_power: 0,25 dBm
#if defined(CONFIG_IDF_TARGET_ESP32C3)
static const uint16_t TX_DEFAULT = 0; // C3-Module: gering
#else
static const uint16_t TX_DEFAULT = 3; // klassischer ESP32: wie bisher maximal
#endif

static const Cg2ParamDef coreParams[] = {
    {CG2_TYPE_CHOICE, 0, 3, TX_DEFAULT, 1, "", "Sendeleistung", TX_CHOICES, 4, "txPower", CG2_PF_KEEP_ON_RESET},
};
static const uint8_t CORE_PARAM_COUNT = sizeof(coreParams) / sizeof(coreParams[0]);
static const uint8_t CORE_P_TXPOWER = 0;
static uint16_t coreValues[CORE_PARAM_COUNT] = {TX_DEFAULT};

static uint8_t totalParamCount() { return gDecoder->paramCount() + CORE_PARAM_COUNT; }

// Nummer n gehört dem Kern? Dann Index in coreParams, sonst -1
static int coreIndex(uint8_t n)
{
    int i = (int)n - gDecoder->paramCount();
    return (i >= 0 && i < CORE_PARAM_COUNT) ? i : -1;
}

static const Cg2ParamDef *paramDefAt(uint8_t n)
{
    int i = coreIndex(n);
    return i >= 0 ? &coreParams[i] : gDecoder->paramDef(n);
}

void coreRadioApplyTxPower()
{
    uint16_t idx = coreValues[CORE_P_TXPOWER];
    if (idx > 3)
        idx = TX_DEFAULT;
    esp_err_t r = esp_wifi_set_max_tx_power(TX_QUARTER_DBM[idx]);
    int8_t now = 0;
    esp_wifi_get_max_tx_power(&now);
    Serial.printf("DECODER: Sendeleistung %s (%.2f dBm, %s)\r\n", TX_CHOICES[idx], now / 4.0f, esp_err_to_name(r));
}

void coreRadioInit()
{
    uint16_t v = prefs.getByte(coreParams[CORE_P_TXPOWER].key, (uint8_t)TX_DEFAULT);
    if (!coreParamLimit(coreParams[CORE_P_TXPOWER], v))
        v = TX_DEFAULT;
    coreValues[CORE_P_TXPOWER] = v;
    coreRadioApplyTxPower();
}

static uint16_t coreRead(int i) { return coreValues[i]; }

static uint16_t coreWrite(int i, uint16_t value)
{
    if (coreValues[i] != value)
    {
        coreValues[i] = value;
        prefs.putByte(coreParams[i].key, (uint8_t)value);
    }
    if (i == CORE_P_TXPOWER)
        coreRadioApplyTxPower();
    return value;
}

// DESCRIBE: [art][nummer][anzahl gesamt][datentyp][min][max][standard][schritt]
//           [einheit][name][anzahlAuswahl][auswahl] x anzahlAuswahl
static void describe(const Cg2Header &h, uint8_t kind, uint8_t n)
{
    uint8_t buf[CG2_ESPNOW_MAX - CG2_HEADER_SIZE];
    Cg2Writer w(buf, sizeof(buf));

    uint8_t count = 0;
    if (kind == CG2_KIND_PARAM)
        count = totalParamCount();
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
        const Cg2ParamDef *d = paramDefAt(n);
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
        // Aktionen und Messwerte: min/max/standard/schritt ohne Bedeutung. Datentyp u16,
        // bei Messwerten mit Auswahltexten Auswahl oder Bitmaske (Protokoll 2.1)
        uint8_t choices = (d->choices != nullptr) ? d->choiceCount : 0;
        uint8_t type = choices > 0 ? d->type : CG2_TYPE_U16;
        w.u8(type).u16(0).u16(0).u16(0).u16(0);
        w.str(d->unit ? d->unit : "").str(d->name ? d->name : "").u8(choices);
        for (uint8_t i = 0; i < choices; i++)
            w.str(d->choices[i]);
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
        if (n >= totalParamCount())
        {
            gDecoder->reply(h, nullptr, 0, CG2_ERR_BAD_VALUE);
            return true;
        }
        int ci = coreIndex(n);
        uint8_t resp[3];
        Cg2Writer(resp, sizeof(resp)).u8(n).u16(ci >= 0 ? coreRead(ci) : gDecoder->paramRead(n));
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
        const Cg2ParamDef *d = (n < totalParamCount()) ? paramDefAt(n) : nullptr;
        if (d == nullptr || !coreParamLimit(*d, value))
        {
            gDecoder->reply(h, nullptr, 0, CG2_ERR_BAD_VALUE);
            return true;
        }
        int ci = coreIndex(n);
        uint16_t stored = ci >= 0 ? coreWrite(ci, value) : gDecoder->paramWrite(n, value);
        uint8_t resp[3];
        Cg2Writer(resp, sizeof(resp)).u8(n).u16(stored);
        gDecoder->reply(h, resp, sizeof(resp));
        return true;
    }
    }
    return false;
}
