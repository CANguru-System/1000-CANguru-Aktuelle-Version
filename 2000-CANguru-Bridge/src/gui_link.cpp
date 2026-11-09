// Verbindung Bridge <-> GUI im CANguru-Protokoll Version 2 (0400-Protocol/PROTOKOLL.md)
//
// Hier liegt alles, was die Bridge selbst mit der GUI bespricht: Sitzung, CAN-Tunnel,
// Übertragung von lokomotive.cs2 und das Hochladen der Firmware für OTA.
// Nachrichten für Decoder leitet Bridge_Decoder.cpp weiter.

#include <Arduino.h>
#include <LittleFS.h>
#include "gui_link.h"
#include "globals.h"
#include "gui_proc.h"
#include "CAN_proc.h"
#include "wdp_proc.h"
#include "Bridge_Decoder.h"
#include "Bridge_OTA.h"
#include "otaQueue.h"

static const char *CS2_PATH = "/lokomotive.cs2";

static uint8_t ownSeq = 0;

// Letzte Antwort auf eine Anfrage der GUI. Wiederholt die GUI eine Anfrage, weil
// unsere Antwort verloren ging, wird sie nicht noch einmal ausgeführt, sondern
// nur die gespeicherte Antwort erneut gesendet.
static Cg2DuplicateFilter guiDuplicates;
static uint8_t lastResponse[48];
static size_t lastResponseLen = 0;

// ------------------------------------------------------------
// Senden
// ------------------------------------------------------------

static void sendFrame(Cg2Header h, const uint8_t *payload, size_t len)
{
    uint8_t buf[CG2_HEADER_SIZE + 64];
    size_t n = cg2Encode(buf, sizeof(buf), h, payload, len);
    if (n > 0)
        udpSendToGui(buf, n);
}

void cg2SendToGui(uint8_t feature, uint8_t command, uint8_t decoder,
                  const uint8_t *payload, size_t len)
{
    Cg2Header h;
    h.feature = feature;
    h.command = command;
    h.seq = ownSeq++;
    h.decoder = decoder;
    sendFrame(h, payload, len);
}

// Antwort auf eine Anfrage der GUI; errorCode != 0 setzt CG2_FLAG_ERROR
static void respond(const Cg2Header &req, const uint8_t *payload, size_t len, uint8_t errorCode = 0)
{
    Cg2Header r = cg2ResponseTo(req, CG2_ID_BRIDGE);
    uint8_t err[1] = {errorCode};
    if (errorCode != 0)
    {
        r.flags |= CG2_FLAG_ERROR;
        payload = err;
        len = 1;
    }
    size_t n = cg2Encode(lastResponse, sizeof(lastResponse), r, payload, len);
    lastResponseLen = n;
    if (n > 0)
        udpSendToGui(lastResponse, n);
}

// Fehlerantwort auf eine Anfrage der GUI im Namen eines Decoders (z. B. nicht zustellbar)
void cg2SendErrorToGui(const Cg2Header &request, uint8_t decoderId, uint8_t errorCode)
{
    Cg2Header r = cg2ResponseTo(request, decoderId);
    r.flags |= CG2_FLAG_ERROR;
    uint8_t err[1] = {errorCode};
    sendFrame(r, err, 1);
}

void cg2SendCanFrameToGui(uint32_t seq, const uint8_t *frame13, char source)
{
    uint8_t p[4 + CAN_FRAME_SIZE + 1];
    Cg2Writer w(p, sizeof(p));
    w.u32(seq).bytes(frame13, CAN_FRAME_SIZE).u8((uint8_t)source);
    cg2SendToGui(CG2_F_CAN, CG2_CAN_TO_GUI, CG2_ID_BRIDGE, p, w.size());
}

void cg2SendOtaProgress(uint8_t decoderId, uint8_t percent, uint8_t overall)
{
    uint8_t p[3] = {decoderId, percent, overall};
    cg2SendToGui(CG2_F_OTA, CG2_OTA_PROGRESS, CG2_ID_BRIDGE, p, sizeof(p));
}

void cg2SendOtaResult(uint8_t decoderId, uint8_t result, uint8_t failReason)
{
    uint8_t p[3] = {decoderId, result, failReason};
    cg2SendToGui(CG2_F_OTA, CG2_OTA_RESULT, CG2_ID_BRIDGE, p, sizeof(p));
}

// ------------------------------------------------------------
// Sitzung
// ------------------------------------------------------------

static void handleSession(const Cg2Header &h, const uint8_t *payload, IPAddress remote)
{
    switch (h.command)
    {
    case CG2_SESSION_DISCOVER:
    {
        // Die GUI sucht die Bridge per Broadcast; ab jetzt senden wir an diese Adresse.
        guiIP = remote;
        uint8_t p[4] = {FW_MAJOR, FW_MINOR, CG2_PROTO_MINOR, (uint8_t)(GB_found ? 1 : 0)};
        cg2SendToGui(CG2_F_SESSION, CG2_SESSION_DISCOVER_REPLY, CG2_ID_BRIDGE, p, sizeof(p));
        Serial.printf("GUI found at %s\r\n", remote.toString().c_str());
        return;
    }
    case CG2_SESSION_START:
        systemReady = true;
        sendStartToDecoders();
        respond(h, nullptr, 0);
        return;

    case CG2_SESSION_END:
        // GUI wird beendet: Gleisbox anhalten, Decoder zurücksetzen, Bridge neu starten.
        respond(h, nullptr, 0);
        enqueueCANFrame2CAN(SysSTOPP, hashEnabled);
        processCANFrames2CAN(); // sofort senden - sonst ginge der STOPP beim Neustart verloren
        delay(1000);
        sendResetToDecoders();
        ESP.restart();
        return;
    }
    if (h.ackRequested())
        respond(h, nullptr, 0, CG2_ERR_UNKNOWN_COMMAND);
}

// ------------------------------------------------------------
// CAN-Tunnel
// ------------------------------------------------------------

static void handleCan(const Cg2Header &h, const uint8_t *payload)
{
    if (h.command != CG2_CAN_TO_TRACKBOX || h.length < CAN_FRAME_SIZE)
        return;

    uint8_t frame[CAN_FRAME_SIZE];
    memcpy(frame, payload, CAN_FRAME_SIZE);
    enqueueCANFrame2CAN(frame, hashEnabled);
    if (frame[CANcmd] == PING)
    {
        sendToWDP(frame);
        sendFrame2allDecoders(frame, CAN_FRAME_SIZE);
    }
    else if (frame[CANcmd] == Sys_CMD)
    {
        // STOPP/GO aus der GUI auch an WDP: WDP zeigt sein STOPP-Fenster nur beim Befehl
        // selbst, nicht bei der Quittung der Gleisbox (die Bridge empfängt ihre eigenen
        // Frames auf dem CAN-Bus nicht, WDP sah den Befehl also bisher nie).
        // Gleisspannung an GUI und Decoder meldet processCANFrames2CAN().
        // Der Hash im Frame stammt von der GUI (gleiche Kennung wie die Bridge).
        sendToWDP(frame);
    }
}

// ------------------------------------------------------------
// Blockweise Übertragung (lokomotive.cs2 und Firmware)
// ------------------------------------------------------------

struct BlockTransfer
{
    File file;
    uint32_t received = 0;
    uint32_t crc = 0;
    uint32_t lastOffset = 0xFFFFFFFF;
    uint32_t lastLen = 0;
};

// Schreibt einen Block [offset u32][daten]. Rückgabe: 0 oder Fehlercode.
static uint8_t writeBlock(BlockTransfer &t, const uint8_t *payload, uint16_t len)
{
    Cg2Reader r(payload, len);
    uint32_t offset = r.u32();
    if (!r.ok())
        return CG2_ERR_BAD_LENGTH;
    const uint8_t *data = payload + 4;
    uint32_t n = len - 4;

    // Wiederholung des letzten Blocks (unsere Antwort ging verloren): nur bestätigen
    if (offset == t.lastOffset && n == t.lastLen)
        return 0;
    if (!t.file || offset != t.received)
        return CG2_ERR_BAD_VALUE;
    if (t.file.write(data, n) != n)
        return CG2_ERR_BUSY;

    t.crc = cg2Crc32(t.crc, data, n);
    t.received += n;
    t.lastOffset = offset;
    t.lastLen = n;
    return 0;
}

static BlockTransfer cs2Transfer;

static void handleFile(const Cg2Header &h, const uint8_t *payload)
{
    switch (h.command)
    {
    case CG2_FILE_BLOCK:
    {
        // Der erste Block (offset 0) legt die Datei neu an
        if (h.length >= 4 && payload[0] == 0 && payload[1] == 0 && payload[2] == 0 && payload[3] == 0 &&
            cs2Transfer.lastOffset != 0)
        {
            if (cs2Transfer.file)
                cs2Transfer.file.close();
            cs2Transfer = BlockTransfer();
            cs2Transfer.file = LittleFS.open(CS2_PATH, "w");
        }
        uint8_t err = writeBlock(cs2Transfer, payload, h.length);
        respond(h, payload, 4, err); // Antwort: [offset u32]
        return;
    }
    case CG2_FILE_END:
    {
        Cg2Reader r(payload, h.length);
        uint32_t size = r.u32();
        uint32_t crc = r.u32();
        if (cs2Transfer.file)
            cs2Transfer.file.close();
        uint8_t ok = (r.ok() && size == cs2Transfer.received && crc == cs2Transfer.crc) ? 1 : 0;
        Serial.printf("lokomotive.cs2 received: %u bytes, %s\r\n", cs2Transfer.received, ok ? "OK" : "CRC/size mismatch");
        cs2Transfer.lastOffset = 0xFFFFFFFF; // nächster Block 0 beginnt eine neue Übertragung
        respond(h, &ok, 1);
        return;
    }
    }
    if (h.ackRequested())
        respond(h, nullptr, 0, CG2_ERR_UNKNOWN_COMMAND);
}

static BlockTransfer fwTransfer;
static uint32_t fwExpectedSize = 0;
static uint32_t fwExpectedCrc = 0;

static void handleOta(const Cg2Header &h, const uint8_t *payload)
{
    switch (h.command)
    {
    case CG2_OTA_UPLOAD_BEGIN:
    {
        Cg2Reader r(payload, h.length);
        fwExpectedSize = r.u32();
        fwExpectedCrc = r.u32();
        if (!r.ok())
        {
            respond(h, nullptr, 0, CG2_ERR_BAD_LENGTH);
            return;
        }
        if (fwTransfer.file)
            fwTransfer.file.close();
        fwTransfer = BlockTransfer();
        firmwareReady = false;
        firmwareSizeOnFs = 0;
        LittleFS.remove(FW_PATH);
        fwTransfer.file = LittleFS.open(FW_PATH, FILE_WRITE);
        respond(h, nullptr, 0, fwTransfer.file ? 0 : CG2_ERR_BUSY);
        Serial.printf("FW: upload begins, %u bytes\r\n", fwExpectedSize);
        return;
    }
    case CG2_OTA_UPLOAD_BLOCK:
    {
        uint8_t err = writeBlock(fwTransfer, payload, h.length);
        if (err == 0 && fwTransfer.file && fwTransfer.received >= fwExpectedSize)
        {
            fwTransfer.file.close();
            bool ok = (fwTransfer.received == fwExpectedSize && fwTransfer.crc == fwExpectedCrc);
            Serial.printf("FW: upload finished, %u bytes, CRC %s\r\n", fwTransfer.received, ok ? "OK" : "WRONG");
            if (ok)
                onUploadFinished(fwTransfer.received);
            else
                err = CG2_ERR_BAD_VALUE;
        }
        respond(h, payload, 4, err); // Antwort: [offset u32]
        return;
    }
    case CG2_OTA_START:
    {
        Cg2Reader r(payload, h.length);
        uint8_t mode = r.u8();
        uint8_t selector = r.u8();
        uint8_t err = r.ok() ? otaStartFromGui(mode, selector) : CG2_ERR_BAD_LENGTH;
        respond(h, nullptr, 0, err);
        return;
    }
    }
    if (h.ackRequested())
        respond(h, nullptr, 0, CG2_ERR_UNKNOWN_COMMAND);
}

// ------------------------------------------------------------
// Eingang
// ------------------------------------------------------------

void cg2HandleGuiPacket(const uint8_t *buf, int len, IPAddress remote)
{
    Cg2Header h;
    const uint8_t *payload = nullptr;
    if (cg2Decode(buf, len, h, payload) != Cg2DecodeResult::Ok)
    {
        Serial.println("GUI: invalid v2 packet");
        return;
    }

    // Nachricht für einen Decoder: unverändert weiterleiten. Wiederholungen erkennt
    // der Decoder selbst; seine Antwort geht unverändert an die GUI zurück.
    if (h.decoder != CG2_ID_BRIDGE)
    {
        forwardGuiToDecoder(h, buf, len);
        return;
    }

    // Wiederholte Anfrage an die Bridge: gespeicherte Antwort erneut senden
    if (h.ackRequested())
    {
        if (guiDuplicates.isDuplicate(CG2_ID_BRIDGE, h.seq) && lastResponseLen > 0 &&
            lastResponse[4] == h.seq)
        {
            udpSendToGui(lastResponse, lastResponseLen);
            return;
        }
    }

    switch (h.feature)
    {
    case CG2_F_DECODER:
        // REMOVE [id]: ausgebauten Decoder aus der dauerhaften Liste löschen
        if (h.command == CG2_DECODER_REMOVE && h.length >= 1)
        {
            respond(h, nullptr, 0, removeDecoder(payload[0]) ? 0 : CG2_ERR_UNKNOWN_DECODER);
            return;
        }
        break;
    case CG2_F_SESSION:
        handleSession(h, payload, remote);
        return;
    case CG2_F_CAN:
        handleCan(h, payload);
        return;
    case CG2_F_FILE:
        handleFile(h, payload);
        return;
    case CG2_F_OTA:
        handleOta(h, payload);
        return;
    }
    if (h.ackRequested())
        respond(h, nullptr, 0, CG2_ERR_UNKNOWN_FEATURE);
}
